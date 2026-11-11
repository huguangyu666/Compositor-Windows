using Compositor.Core;
using Compositor.Segmentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Text.Json.Nodes;
using Windows.Storage.Pickers;
using Windows.System;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private const int ObjectSelectionDefaultFeather = 0;

    private sealed class ObjectSelectionDraft(EditorDocument owner, Selection? original)
    {
        public EditorDocument Owner { get; } = owner;
        public Selection? Original { get; } = original;
        public List<SelectionPoint> Points { get; } = [];
        public SelectionBox? Box;
        public Selection? Result;
        public MaskSurface? Coverage;
        public bool NeedsRefinement;
        public CancellationTokenSource? Cancellation;
        public TaskCompletionSource<bool>? Ready;
        public long Request;
        public bool Busy;
    }

    private ObjectSelectionDraft? objectSelectionDraft;
    private ModelStore? objectSelectionStore;
    private SamSegmenter? objectSegmenter;
    private EditorDocument? objectSelectionImageOwner;
    private Task<Surface>? objectSelectionImage;
    private Task? objectSelectionWarmUp = null;
    private CancellationTokenSource? objectSelectionWarmUpCancellation;
    private bool objectSelectionWarming;
    private PointD? objectSelectionPress;
    private PointD objectSelectionPointer;
    private bool objectSelectionExclude, objectModelInstalling;
    private CancellationTokenSource? objectSelectionModelCancellation;
    private StackPanel objectSelectionControls = null!;
    private readonly Canvas objectSelectionOverlay = new() { IsHitTestVisible = false };
    private PointD? objectSelectionCursorPoint;
    private Button objectSelectionApply = null!, objectSelectionCancel = null!, objectSelectionReset = null!, objectSelectionModel = null!;
    private CheckBox objectSelectionSoft = null!;
    private NumberBox objectSelectionFeather = null!;
    private TextBlock objectSelectionStatus = null!;
    private bool ObjectSelectionActive => objectSelectionDraft is not null || objectSelectionPress is not null || objectModelInstalling;
    private ModelStore ObjectSelectionModels => objectSelectionStore ??= CreateObjectSelectionModels();

    private static ModelStore CreateObjectSelectionModels(bool preferSam2 = false)
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Compositor", "Models");
        var sam2 = new ModelStore(Path.Combine(root, "SAM2.1-Tiny"), ModelStore.Sam2Files);
        var legacy = new ModelStore(Path.Combine(root, "MobileSAM"), ModelStore.MobileSamFiles);
        return preferSam2 || sam2.IsInstalled || !legacy.IsInstalled ? sam2 : legacy;
    }
#if DEBUG
    private Task? objectSelectionPreparationProbe;
    private Task? objectSelectionRefinementProbe;
#endif

    private void InitializeObjectSelection()
    {
        objectSelectionControls = new() { Orientation = Orientation.Horizontal, Spacing = 6, Visibility = Visibility.Collapsed };
        objectSelectionApply = QuickButton("ObjectSelectionApply", "OS_Apply", () => ConfirmObjectSelection());
        objectSelectionCancel = QuickButton("ObjectSelectionCancel", "OS_Cancel", CancelObjectSelection);
        objectSelectionReset = QuickButton("ObjectSelectionReset", "OS_Reset", () => { CancelObjectSelection(); SetStatus(ToolHint(tool)); });
        objectSelectionModel = QuickButton("ObjectSelectionModel", "OS_Model", () => _ = ConfigureObjectSelectionModelAsync());
        objectSelectionSoft = NameQuickControl(new CheckBox { Content = Loc.T("OS_SoftEdges"), IsChecked = true }, "ObjectSelectionSoftEdges", "OS_SoftEdges");
        objectSelectionSoft.Checked += (_, _) => RefreshObjectSelectionOptions();
        objectSelectionSoft.Unchecked += (_, _) => RefreshObjectSelectionOptions();
        objectSelectionFeather = QuickNumber("ObjectSelectionFeather", "OS_Feather", 0, 250, ObjectSelectionDefaultFeather);
        objectSelectionFeather.ValueChanged += (_, _) =>
        { if (double.IsFinite(objectSelectionFeather.Value)) RefreshObjectSelectionOptions(reuseCoverage: true); };
        objectSelectionStatus = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, MaxWidth = 230,
            TextTrimming = TextTrimming.CharacterEllipsis };
        foreach (var child in new UIElement[] { objectSelectionApply, objectSelectionCancel, objectSelectionReset,
            objectSelectionSoft, new TextBlock { Text = Loc.T("OS_Feather"), VerticalAlignment = VerticalAlignment.Center },
            objectSelectionFeather, objectSelectionModel, objectSelectionStatus }) objectSelectionControls.Children.Add(child);
        ToolOptionsPanel.Children.Add(objectSelectionControls); ToolOverlay.Children.Add(objectSelectionOverlay);
        RefreshObjectSelectionControls();
    }

    private bool UseObjectSelectionCircleCursor()
    {
        if (tool != CanvasTool.ObjectSelect || document is not { } owner || objectModelInstalling
            || objectSelectionCursorPoint is not { } point || spaceHeld || panLast is not null
            || zoomDrag is not null || samplingPalette) return false;
        var pixel = viewport.ToDocument(point);
        return pixel.X >= 0 && pixel.Y >= 0 && pixel.X < owner.Snapshot.Manifest.Width && pixel.Y < owner.Snapshot.Manifest.Height;
    }

    private void RefreshObjectSelectionControls()
    {
        if (objectSelectionControls is null) return;
        objectSelectionControls.Visibility = tool == CanvasTool.ObjectSelect ? Visibility.Visible : Visibility.Collapsed;
        objectSelectionApply.IsEnabled = objectSelectionDraft is { Busy: false, Result: not null } && !objectModelInstalling;
        objectSelectionCancel.IsEnabled = objectSelectionReset.IsEnabled = ObjectSelectionActive;
        objectSelectionModel.IsEnabled = !ObjectSelectionActive && !objectSelectionWarming;
        objectSelectionSoft.IsEnabled = !objectModelInstalling;
        objectSelectionFeather.IsEnabled = !objectModelInstalling && objectSelectionSoft.IsChecked == true;
        if (!objectModelInstalling) objectSelectionStatus.Text = Loc.T(objectSelectionDraft is { Busy: true } ? "OS_Working"
            : objectSelectionDraft is { NeedsRefinement: true } ? "OS_Refining"
            : objectSelectionDraft?.Result is { IsEmpty: true } ? "OS_Empty" : objectSelectionDraft?.Result is not null ? "OS_Refine" : "OS_Hint");
        if (!objectModelInstalling && objectSelectionDraft is null && objectSelectionWarming) objectSelectionStatus.Text = Loc.T("OS_Preparing");
    }

    private void RefreshObjectSelectionOptions(bool reuseCoverage = false)
    {
        if (objectSelectionDraft is { } draft && !objectModelInstalling) _ = RefreshObjectSelectionAsync(draft, reuseCoverage);
        RefreshObjectSelectionControls();
    }

    private void BeginObjectSelectionPointer(PointD point, VirtualKeyModifiers modifiers)
    {
        if (objectModelInstalling || document is not { } owner) return;
        if (objectSelectionDraft is null)
        {
            try { RequireEditorIdle(); }
            catch (InvalidOperationException ex) { SetStatus(ex.Message); return; }
        }
        if (point.X < 0 || point.Y < 0 || point.X >= owner.Snapshot.Manifest.Width || point.Y >= owner.Snapshot.Manifest.Height) return;
        objectSelectionPress = objectSelectionPointer = point;
        objectSelectionExclude = modifiers.HasFlag(VirtualKeyModifiers.Menu);
        RefreshObjectSelectionMarkers(); RefreshObjectSelectionControls();
    }

    private void MoveObjectSelectionPointer(PointD point)
    {
        if (objectSelectionPress is null || document is null) return;
        objectSelectionPointer = new(Math.Clamp(point.X, 0, document.Snapshot.Manifest.Width), Math.Clamp(point.Y, 0, document.Snapshot.Manifest.Height));
        RefreshObjectSelectionMarkers();
    }

    private void EndObjectSelectionPointer(PointD point)
    {
        if (objectSelectionPress is not { } start) return;
        MoveObjectSelectionPointer(point); var end = objectSelectionPointer; objectSelectionPress = null;
        double dx = Math.Abs(end.X - start.X), dy = Math.Abs(end.Y - start.Y);
        if (!objectSelectionExclude && dx * viewport.PointsPerPixel >= 4 && dy * viewport.PointsPerPixel >= 4)
            _ = PromptObjectSelectionAsync(null, new(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y), dx, dy));
        else _ = PromptObjectSelectionAsync(new(start, !objectSelectionExclude), null);
    }

    private async Task PromptObjectSelectionAsync(SelectionPoint? point, SelectionBox? box)
    {
        if (document is not { } owner || objectModelInstalling) return;
        if (objectSelectionDraft is null && point is { Include: false } && box is null)
        { SetStatus(Loc.T("OS_PositiveFirst")); RefreshObjectSelectionMarkers(); RefreshObjectSelectionControls(); return; }
        var draft = objectSelectionDraft ??= new(owner, owner.ActiveSelection);
        if (point is { } prompt)
        {
            if (draft.Points.Count >= 64) { SetStatus(Loc.T("OS_PointLimit")); return; }
            draft.Points.Add(prompt);
        }
        if (box is not null) draft.Box = box;
        await RefreshObjectSelectionAsync(draft);
    }

    private async Task RefreshObjectSelectionAsync(ObjectSelectionDraft draft, bool reuseCoverage = false)
    {
        var coverage = reuseCoverage ? draft.Coverage : null;
        if (!reuseCoverage) draft.Coverage = null;
        draft.Ready?.TrySetResult(false);
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); draft.Ready = ready;
        draft.Cancellation?.Cancel();
        using var pending = new CancellationTokenSource(); var token = pending.Token;
        draft.Cancellation = pending; long request = ++draft.Request; draft.Busy = true;
        RefreshObjectSelectionMarkers(); RefreshObjectSelectionControls(); RefreshQuickToolControls(); RefreshAiActions();
        var points = draft.Points.ToArray(); var box = draft.Box;
        bool soft = objectSelectionSoft.IsChecked == true;
        int feather = soft ? (int)Math.Clamp(double.IsFinite(objectSelectionFeather.Value)
            ? objectSelectionFeather.Value : ObjectSelectionDefaultFeather, 0, 250) : 0;
        string combine = selectionMode == LassoSelectionMode.Add ? "add" : selectionMode == LassoSelectionMode.Subtract ? "subtract" : "replace";
        bool refine = coverage is not null && draft.NeedsRefinement;
        Surface? sample = null;
        bool Current() => !windowClosed && !token.IsCancellationRequested && ReferenceEquals(document, draft.Owner)
            && ReferenceEquals(objectSelectionDraft, draft) && request == draft.Request;
        try
        {
            if (coverage is null)
            {
                if (!await EnsureObjectSelectionModelAsync(true, token)) { if (Current()) CancelObjectSelection(); return; }
                if (objectSelectionWarmUp is { } warming) await warming.WaitAsync(token);
#if DEBUG
                if (objectSelectionPreparationProbe is { } preparation) await preparation.WaitAsync(token);
#endif
                sample = await GetObjectSelectionImageAsync(draft.Owner, token);
                objectSegmenter ??= new(ObjectSelectionModels);
                coverage = await objectSegmenter.SelectAsync(sample, points, box, soft, token, refineEdges: false);
                refine = true;
            }
            if (!await PublishAsync(coverage, refine) || !refine) return;
            // Let the usable contour reach the screen before spending time on final edges.
            // Each new prompt cancels this idle work and decodes only the newest request.
            await Task.Delay(120, token);
#if DEBUG
            if (objectSelectionRefinementProbe is { } refinement) await refinement.WaitAsync(token);
#endif
            sample ??= await GetObjectSelectionImageAsync(draft.Owner, token);
            var fitted = await Task.Run(() => SamEdgeRefinement.Refine(sample, coverage, soft, points, token), token);
            await PublishAsync(fitted, false);

            async Task<bool> PublishAsync(MaskSurface mask, bool needsRefinement)
            {
                var result = await Task.Run(() => PrepareObjectSelection(mask.Width, mask.Height, draft.Original,
                    new() { ["combine"] = combine, ["feather"] = feather }, mask, token), token);
                if (!Current()) return false;
                draft.Coverage = mask; draft.Result = result; draft.NeedsRefinement = needsRefinement; draft.Busy = false;
                ready.TrySetResult(true);
                RefreshSelectionOutline(); RefreshObjectSelectionMarkers(); RefreshObjectSelectionControls();
                return true;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (Current())
            {
                if (draft.Result is null) RefreshSelectionOutline();
                SetStatus(Loc.F("OS_Error", ex.Message)); App.Log($"Object selection: {ex}", LogLevel.Error);
            }
        }
        finally
        {
            ready.TrySetResult(false);
            if (ReferenceEquals(objectSelectionDraft, draft) && request == draft.Request)
            { draft.Busy = false; draft.Cancellation = null; RefreshObjectSelectionControls(); }
        }
    }

    private Task<Surface> GetObjectSelectionImageAsync(EditorDocument owner, CancellationToken cancellation)
    {
        if (!ReferenceEquals(objectSelectionImageOwner, owner) || objectSelectionImage is null || objectSelectionImage.IsFaulted || objectSelectionImage.IsCanceled)
        {
            var input = SelectionSampleInput.Capture(owner.Snapshot);
            objectSelectionImageOwner = owner; objectSelectionImage = Task.Run(() => input.Render(true, null));
        }
        return objectSelectionImage.WaitAsync(cancellation);
    }

    private void BeginObjectSelectionWarmUp()
    {
        if (document is not { } owner || objectSelectionWarmUp is not null || !ObjectSelectionModels.IsInstalled) return;
        var pending = new CancellationTokenSource(); objectSelectionWarmUpCancellation = pending; objectSelectionWarming = true;
        objectSelectionWarmUp = PrepareAsync(); RefreshObjectSelectionControls();

        async Task PrepareAsync()
        {
            try
            {
                await ObjectSelectionModels.EnsureAsync(false, null, pending.Token);
                var sample = await GetObjectSelectionImageAsync(owner, pending.Token);
                pending.Token.ThrowIfCancellationRequested();
                objectSegmenter ??= new(ObjectSelectionModels);
                await objectSegmenter.PrepareImageAsync(sample, pending.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!pending.IsCancellationRequested) App.Log($"Object selection preparation: {ex}", LogLevel.Diagnostic); }
            finally
            {
                if (ReferenceEquals(objectSelectionWarmUpCancellation, pending))
                { objectSelectionWarmUpCancellation = null; objectSelectionWarming = false; RefreshObjectSelectionControls(); }
                pending.Dispose();
            }
        }
    }

    private async Task FinishObjectSelectionForToolChangeAsync()
    {
        var draft = objectSelectionDraft;
        if (draft is null || tool == CanvasTool.ObjectSelect) return;
        while (ReferenceEquals(objectSelectionDraft, draft) && draft.Busy && tool != CanvasTool.ObjectSelect)
        {
            if (draft.Ready is not { } ready) break;
            await ready.Task;
        }
        if (tool != CanvasTool.ObjectSelect && ReferenceEquals(objectSelectionDraft, draft)
            && ReferenceEquals(document, draft.Owner) && !ConfirmObjectSelection()) CancelObjectSelection();
    }

    private static Selection PrepareObjectSelection(int width, int height, Selection? current, JsonObject args, MaskSurface coverage, CancellationToken cancellation)
    {
        var input = args.DeepClone().AsObject(); input["mode"] = "mask";
        if (input["feather"] is null && input["softEdges"]?.GetValue<bool>() != false) input["feather"] = ObjectSelectionDefaultFeather;
        // An explicitly empty result protects the entire image. Null would remove the constraint.
        var result = EditorAutomation.PrepareSelection(new() { Width = width, Height = height }, current, input, coverage, cancellation)
            ?? new Selection(new MaskSurface(width, height), new(0, 0, 0, 0));
        // Build the existing Gaussian coverage cache on this worker before UI publication.
        if (result.Feather > 0) _ = result.CoverageAt(0, 0);
        cancellation.ThrowIfCancellationRequested();
        return result;
    }

    private void InvalidateObjectSelectionImage()
    {
        objectSelectionWarmUpCancellation?.Cancel(); objectSelectionWarmUpCancellation = null;
        objectSelectionWarmUp = null; objectSelectionWarming = false;
        CancelObjectSelection(); objectSelectionImageOwner = null; objectSelectionImage = null;
    }

    private void RefreshObjectSelectionMarkers()
    {
        objectSelectionOverlay.Children.Clear();
        // Prompts guide inference; only the active gesture is drawn over the photo.
        // The settled result uses the normal selection contour through DisplayedSelection.
        if (objectSelectionPress is not { } start) return;
        double unit = 1 / Math.Max(.001, viewport.PointsPerPixel);
        double dx = Math.Abs(start.X - objectSelectionPointer.X), dy = Math.Abs(start.Y - objectSelectionPointer.Y);
        if (!objectSelectionExclude && dx > 0 && dy > 0)
        {
            var rectangle = new Rectangle { Width = dx, Height = dy, StrokeThickness = unit,
                Stroke = new SolidColorBrush(Microsoft.UI.Colors.Cyan), StrokeDashArray = new DoubleCollection { 4, 3 } };
            Canvas.SetLeft(rectangle, Math.Min(start.X, objectSelectionPointer.X)); Canvas.SetTop(rectangle, Math.Min(start.Y, objectSelectionPointer.Y));
            objectSelectionOverlay.Children.Add(rectangle);
        }
    }

    private bool ConfirmObjectSelection()
    {
        if (objectSelectionDraft is not { Busy: false, Result: { } result } draft || !ReferenceEquals(document, draft.Owner) || objectModelInstalling) return false;
        ClearObjectSelection(restoreOutline: false);
        draft.Owner.SetSelectionUndoable(result);
        SetStatus(Loc.T("OS_Applied")); return true;
    }

    private void CancelObjectSelection() => ClearObjectSelection(restoreOutline: true);

    private void ClearObjectSelection(bool restoreOutline)
    {
        bool hadPreview = objectSelectionDraft?.Result is not null;
        objectSelectionDraft?.Ready?.TrySetResult(false);
        objectSelectionDraft?.Cancellation?.Cancel(); objectSelectionDraft = null; objectSelectionPress = null;
        objectSelectionModelCancellation?.Cancel(); objectSelectionOverlay.Children.Clear();
        if (restoreOutline && hadPreview) RefreshSelectionOutline();
        RefreshObjectSelectionControls();
        if (quickToolsReady) { RefreshQuickToolControls(); RefreshAiActions(); }
    }

    private bool HandleObjectSelectionKey(KeyRoutedEventArgs args)
    {
        if (!ObjectSelectionActive || TypingInField) return false;
        if (IsCancelPress(args.Key)) { CancelObjectSelection(); args.Handled = true; return true; }
        if (args.Key == VirtualKey.Enter) { ConfirmObjectSelection(); args.Handled = true; return true; }
        return false;
    }

    private async Task ConfigureObjectSelectionModelAsync()
    {
        if (ObjectSelectionActive) return;
        try { await EnsureObjectSelectionModelAsync(true, default, configure: true); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetStatus(Loc.F("OS_Error", ex.Message)); }
    }

    private async Task<bool> EnsureObjectSelectionModelAsync(bool interactive, CancellationToken cancellation, bool configure = false)
    {
        var store = configure ? CreateObjectSelectionModels(preferSam2: true) : ObjectSelectionModels;
        if (!configure && (store.IsInstalled || !interactive)) { await store.EnsureAsync(false, null, cancellation); return true; }
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        objectSelectionModelCancellation = pending; objectModelInstalling = true; RefreshObjectSelectionControls();
        try
        {
            var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = Loc.T("OS_Model"), Content = Loc.T("OS_InstallDescription"),
                PrimaryButtonText = Loc.T("OS_Download"), SecondaryButtonText = Loc.T("OS_Import"), CloseButtonText = Loc.T("OS_Cancel") };
            var answer = await ShowProjectDialogAsync(dialog); pending.Token.ThrowIfCancellationRequested();
            if (answer == ContentDialogResult.None) return false;
            var progress = new Progress<double>(value =>
            { if (ReferenceEquals(objectSelectionModelCancellation, pending) && !pending.IsCancellationRequested) objectSelectionStatus.Text = Loc.F("OS_DownloadProgress", (int)(value * 100)); });
            if (answer == ContentDialogResult.Secondary)
            {
                var picker = new FolderPicker { SettingsIdentifier = "objectSelectionModel", SuggestedStartLocation = PickerLocationId.Downloads };
                picker.FileTypeFilter.Add("*"); InitializeWithWindow(picker);
                var folder = await picker.PickSingleFolderAsync(); pending.Token.ThrowIfCancellationRequested();
                if (folder is null) return false;
                if (!File.Exists(Path.Combine(folder.Path, ModelStore.Sam2Files[0].Name)))
                {
                    string legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Compositor", "Models", "MobileSAM");
                    store = new(legacy, ModelStore.MobileSamFiles);
                }
                await store.ImportAsync(folder.Path, progress, pending.Token);
            }
            else await store.EnsureAsync(true, progress, pending.Token);
            if (!ReferenceEquals(store, objectSelectionStore))
            {
                objectSegmenter?.Dispose(); objectSegmenter = null; objectSelectionStore = store;
            }
            SetStatus(Loc.T("OS_ModelReady")); return true;
        }
        finally
        {
            if (ReferenceEquals(objectSelectionModelCancellation, pending))
            { objectSelectionModelCancellation = null; objectModelInstalling = false; RefreshObjectSelectionControls(); }
        }
    }
}
