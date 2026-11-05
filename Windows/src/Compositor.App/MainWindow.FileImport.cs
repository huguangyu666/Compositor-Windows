using Compositor.Core;
using Compositor.Core.Psd;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private sealed record FileImportResult(int ImportedFiles, IReadOnlyList<string> Failures, bool Retired = false);
    private sealed record DecodedFile(Surface? Image, PsdDocument? Photoshop, IReadOnlyList<PsdConversion>? Conversions = null);
    private sealed class FileImportQueue(EditorDocument owner, Task retired)
    {
        public readonly EditorDocument Owner = owner;
        public readonly Task Retired = retired;
        public Task Tail = Task.CompletedTask;
        public int Pending;
    }
    private FileImportQueue? fileImports;
    private CancellationTokenSource? fileImportCancellation;
    private bool fileImportOwnsBusy;
    private int projectDialogDepth;
    private bool IsFileImporting => fileImports is { Pending: > 0 } queue && ReferenceEquals(queue.Owner, document);

    /// <summary>
    /// The extensions an image import takes, used by both gates — the picker's filter and the drop's — so the
    /// two cannot drift. The Mac answers by content type and accepts JPEG, PNG, TIFF and both Photoshop
    /// containers (IO/ImageImporter.swift:59-60). HEIC is in that list and deliberately not here: WinRT's
    /// decoder needs a separately installed HEVC codec for it, so offering the extension would mean a file
    /// that opens the dialog and then fails, rather than a format this build can read.
    /// </summary>
    internal static readonly string[] ImportableExtensions = [".png", ".jpg", ".jpeg", ".tif", ".tiff", ".psd", ".psb"];

    internal static bool IsImportableExtension(string extension) => ImportableExtensions.Contains(extension.ToLowerInvariant());
#if DEBUG
    private Func<string, Task>? beforeImportDecode;
    private Action<long>? beforeImportBudget;
    private Action<long>? psdDecodeAllocated;
#endif
    private Task<FileImportResult> ImportFilesAsync(EditorDocument owner, IReadOnlyList<string> paths, PointD? point = null)
    {
        if (windowClosed || !ReferenceEquals(owner, document)) return Task.FromResult(new FileImportResult(0, [], true));
        if (paths.Count == 0) return Task.FromResult(new FileImportResult(0, []));
        var queue = fileImports;
        if (queue is null || !ReferenceEquals(queue.Owner, owner)) fileImports = queue = new(owner, documentRetired.Task);
        var orderedPaths = paths.ToArray();
        var preceding = queue.Tail;
        queue.Pending++;
        var work = RunFileImportAsync(queue, preceding, orderedPaths, point);
        queue.Tail = work;
        RefreshFileImportAvailability();
        return work;
    }

    private async Task<FileImportResult> RunFileImportAsync(FileImportQueue queue, Task preceding, string[] paths, PointD? point)
    {
        int imported = 0; var failures = new List<string>();
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        bool Current() => !windowClosed && !queue.Retired.IsCompleted && ReferenceEquals(document, queue.Owner);
        try
        {
            if (await Task.WhenAny(preceding, queue.Retired) == queue.Retired || !Current()) return new(0, [], true);
            await preceding;
            // Incoming requests wait for existing dialogs/project work, as EditorSession does.
            while (Current() && (projectDialogDepth > 0 || renamingLayer || textDialogOpen || colorRangeSampling || selectionAmountPending
                || StatusProgress.Visibility == Visibility.Visible)) await Task.Delay(16);
            if (!Current()) return new(0, [], true);
            EndSelectionMove(); StopSelectionAutoscroll();
            FinishPointerStroke();
            if (healingStroke is not null && !healingFinishing) await FinishHealingAsync(null);
            while (Current() && healingFinishing) await Task.Delay(16);
            if (!Current()) return new(imported, failures, true);
            ClearCropBox(); cropDragging = false;
            if (placementDragActive) FinishPlacementDrag();
            if (selectionWorkResult is { } selectionWork && await Task.WhenAny(selectionWork, queue.Retired) == queue.Retired)
                return new(0, [], true);
            if (!Current()) return new(imported, failures, true);
            await FinishTransformBeforeOutputAsync();
            if (!Current() || !await ApplyGradientDraftAsync() || !Current()) return new(0, [], true);
            fileImportCancellation = cancellation; fileImportOwnsBusy = true; SetBusy(true);
            using var batch = queue.Owner.BeginFileImport(point);
            foreach (var path in paths)
            {
                if (!Current()) return new(imported, failures, true);
                try
                {
                    long remainingPixels = batch.RemainingPixels;
#if DEBUG
                    beforeImportBudget?.Invoke(remainingPixels);
#endif
                    // File I/O, PSD parsing and WIC decode all run away from the UI thread.
                    var decode = Task.Run(async () =>
                    {
#if DEBUG
                        if (beforeImportDecode is { } hook) await hook(path);
#endif
                        token.ThrowIfCancellationRequested();
                        if (PsdReader.IsPhotoshopFile(path))
                        {
#if DEBUG
                            long before = GC.GetAllocatedBytesForCurrentThread();
#endif
                            var photoshop = PsdReader.ReadFile(path, remainingPixels: remainingPixels, cancellationToken: token);
                            // The Mac builds the document during the read and puts its conversion sheet up
                            // before anything applies; the same collection runs here, off the UI thread.
#if DEBUG
                            psdDecodeAllocated?.Invoke(GC.GetAllocatedBytesForCurrentThread() - before);
#endif
                            return new DecodedFile(null, photoshop, EditorDocument.CollectPsdConversions(photoshop));
                        }
                        var bytes = await File.ReadAllBytesAsync(path, token);
                        token.ThrowIfCancellationRequested();
                        return new DecodedFile(await WicImaging.DecodeSurfaceAsync(bytes, token), null);
                    });
                    if (await Task.WhenAny(decode, queue.Retired) == queue.Retired || !Current())
                    { _ = ObserveDiscardedImportAsync(decode); return new(imported, failures, true); }
                    var decoded = await decode;
                    // Nothing is applied until the user continues: a layered file with lines to report
                    // gets its sheet first, and Cancel skips exactly this file, as the Mac's `continue` does.
                    if (decoded.Photoshop is { } gated && decoded.Conversions is { Count: > 0 } lines)
                    {
                        if (!await ConfirmPsdConversionsAsync(Path.GetFileName(path), lines))
                        {
                            if (!Current()) return new(imported, failures, true);
                            continue;
                        }
                        if (!Current()) return new(imported, failures, true);
                    }
                    var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    QueueCanvasInput(queue.Owner, () =>
                    {
                        try
                        {
                            if (!Current()) { published.TrySetCanceled(); return; }
                            var name = Path.GetFileNameWithoutExtension(path);
                            var layer = decoded.Photoshop is { } psd ? batch.AddPhotoshop(psd, name) : batch.AddImage(decoded.Image!, name);
                            selectedLayerId = layer.Id; selectedLayerIds.Clear(); selectedLayerIds.Add(layer.Id);
                            paintOnMask = false; PaintMaskCheck.IsChecked = false; maskAloneLayerId = null;
                            if (layer.ParentId is { } parent) collapsedGroups.Remove(parent);
                            if (layer.IsGroupOrFalse) collapsedGroups.Remove(layer.Id);
                            RefreshLayerPanel();
                            string status = decoded.Photoshop is { IsCompositeFallback: false } photoshop
                                ? Loc.F("S_ImportedLayers", Path.GetFileName(path), photoshop.Layers.Count)
                                : Loc.F("S_Imported", Path.GetFileName(path), decoded.Image?.Width ?? decoded.Photoshop!.Width,
                                    decoded.Image?.Height ?? decoded.Photoshop!.Height);
                            int cropped = decoded.Photoshop?.Layers.Count(source => source.CroppedToCanvas) ?? 0;
                            if (cropped > 0) status = Loc.F("S_ImportedPsdCropped", status, cropped);
                            var fallbackModes = decoded.Photoshop?.Layers.Where(source => source.HasUnsupportedBlendMode)
                                .Select(source => source.BlendKey.Trim()).Distinct().ToArray();
                            if (fallbackModes is { Length: > 0 })
                                status = Loc.F("S_ImportedPsdBlendFallback", status, string.Join(", ", fallbackModes));
                            SetStatus(status);
                            published.TrySetResult();
                        }
                        catch (Exception ex) { published.TrySetException(ex); }
                    });
                    if (await Task.WhenAny(published.Task, queue.Retired) == queue.Retired || !Current()) return new(imported, failures, true);
                    await published.Task; imported++;
                }
                catch (Exception ex)
                {
                    if (!Current()) return new(imported, failures, true);
                    failures.Add($"{Path.GetFileName(path)}: {ex.Message}");
                    App.Log($"File import failed ({Path.GetFileName(path)}): {ex}");
                }
            }
            if (failures.Count > 0) Report(Loc.T("SC_ImportImage"), new IOException(string.Join(Environment.NewLine, failures)));
            return new(imported, failures);
        }
        catch (Exception ex)
        {
            if (!Current()) return new(imported, failures, true);
            App.Log($"File import request failed: {ex}"); failures.Add(ex.Message); SetStatus(ex.Message);
            return new(imported, failures);
        }
        finally
        {
            queue.Pending--;
            if (ReferenceEquals(fileImportCancellation, cancellation))
            { fileImportCancellation = null; fileImportOwnsBusy = false; SetBusy(false); }
            if (ReferenceEquals(fileImports, queue)) RefreshFileImportAvailability();
        }
    }

    private static async Task ObserveDiscardedImportAsync(Task<DecodedFile> decode)
    { try { await decode; } catch (OperationCanceledException) { } catch (Exception ex) { App.Log($"Retired file decode failed: {ex}"); } }

    private void RetireFileImports()
    {
        fileImports = null; fileImportCancellation?.Cancel(); fileImportCancellation = null;
        if (fileImportOwnsBusy) { fileImportOwnsBusy = false; SetBusy(false); }
        RefreshFileImportAvailability();
    }

    private void RefreshFileImportAvailability()
    {
        LayerList.IsEnabled = ToolCombo.IsEnabled = !IsFileImporting && !IsGradientFinishing && !IsDeletePending;
        RefreshTransformInspector(); RefreshTransformAvailability(true);
    }

    // ContentView uses a drop point only inside the canvas viewport; chrome drops center.
    private PointD? FileDropPoint(PointD position) => position.X >= 0 && position.Y >= 0
        && position.X < CanvasStage.ActualWidth && position.Y < CanvasStage.ActualHeight ? viewport.ToDocument(position) : null;

    private async Task WaitForFileImportsAsync()
    {
        if (fileImports is not { Pending: > 0 } queue) return;
        await Task.WhenAny(queue.Tail, queue.Retired);
    }

    private async Task<ContentDialogResult> ShowProjectDialogAsync(ContentDialog dialog)
    {
        projectDialogDepth++;
        try
        {
#if DEBUG
            if (projectDialogProbe is { } probe) return await probe(dialog);
#endif
            return await dialog.ShowAsync();
        }
        finally { projectDialogDepth--; }
    }
}
