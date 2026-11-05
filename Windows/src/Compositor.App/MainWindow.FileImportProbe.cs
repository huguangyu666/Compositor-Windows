#if DEBUG
using Compositor.Core;
using Windows.System;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task VerifyFileImportsAsync(Dictionary<string, object> report)
    {
        Require(toolInitialized && tool == CanvasTool.Move && TransformControls.Visibility == Microsoft.UI.Xaml.Visibility.Visible,
            "Initial Move tool did not initialize its visible parameters.");
        report["fileToolContinuationPreservesInitialMoveParameters"] = "passed";
        string directory = Path.Combine(Path.GetTempPath(), "compositor-file-import-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string firstPath = Path.Combine(directory, "First.png"), secondPath = Path.Combine(directory, "Second.jpg");
        var image = new Surface(4, 3); for (int offset = 0; offset < image.Pixels.Length; offset += 4)
        { image.Pixels[offset + 1] = 128; image.Pixels[offset + 3] = 128; }
        await File.WriteAllBytesAsync(firstPath, await WicImaging.EncodePngAsync(image));
        await File.WriteAllBytesAsync(secondPath, await WicImaging.EncodeJpegAsync(image, 100));
        try
        {
            var scene = FloatingTargetScene(); var originalSelection = scene.ActiveSelection;
            OpenDocument(scene); await WaitForFramesAsync(); OnFreeTransform(this, new()); await WaitForFramesAsync();
            ChangeTransformNumber(TransformField.X, 65); await WaitForFramesAsync();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var decodeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim(false);
            beforeFrameRender = () => { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Import reader timed out."); };
            beforeImportDecode = _ => { decodeEntered.TrySetResult(); return Task.CompletedTask; };
            Task<FileImportResult>? job = null;
            try
            {
                MarkInteractionDirty(new(0, 0, 1, 1), grow: 0); PumpRender(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                job = ImportFilesAsync(scene, [firstPath, secondPath]); await Task.Delay(60);
                report["importHeldMergeObserved"] = new { decoded = decodeEntered.Task.IsCompleted, completed = job.IsCompleted, layers = scene.LayerCount, undo = scene.CanUndo };
                Require(!decodeEntered.Task.IsCompleted && !job.IsCompleted && scene.LayerCount == 2 && !scene.CanUndo,
                    "File import decoded or mutated the source before the floating reader/merge finished.");
            }
            finally { beforeFrameRender = null; release.Set(); if (job is not null) await job; beforeImportDecode = null; }
            await WaitForFramesAsync(); AssertUploadedFrame(scene);
            Require(scene.LayerCount == 4 && scene.ActiveSelection?.Bounds == new RectI(65, 40, 24, 16), "Import lost its preceding floating merge.");
            Require(scene.Snapshot.Manifest.Layers[^2].Name == "First" && scene.Snapshot.Manifest.Layers[^1].Name == "Second", "Provider file order changed.");
            OnUndo(this, new()); await WaitForFramesAsync(); Require(scene.LayerCount == 2 && scene.CanUndo, "Batch import was not one Undo after merge.");
            OnUndo(this, new()); await WaitForFramesAsync(); Require(!scene.CanUndo && ReferenceEquals(originalSelection, scene.ActiveSelection), "Merge Undo lost the original selection.");
            AssertUploadedFrame(scene); report["importWaitsForFloatingMergeProviderOrderGpuTwoHistorySteps"] = "passed";

            scene = EditorDocument.Create(100, 80); OpenDocument(scene); await WaitForFramesAsync();
            var inside = new PointD(CanvasStage.ActualWidth / 2, CanvasStage.ActualHeight / 2);
            Require(FileDropPoint(inside) == viewport.ToDocument(inside) && FileDropPoint(new(-1, inside.Y)) is null
                && FileDropPoint(new(CanvasStage.ActualWidth + 1, inside.Y)) is null,
                "Chrome drops used an off-canvas point or canvas drops lost the viewport mapping.");
            report["fileDropUsesViewportPointOnlyInsideCanvas"] = "passed";
            // One gate for both doors. The Mac's importer answers by content type and takes JPEG, PNG, TIFF
            // and both Photoshop containers (ImageImporter.swift:59-60); TIFF is in here because WinRT ships
            // that codec, HEIC stays out because it needs a separately installed one. The block below is what
            // proves the accepted formats actually arrive; this assertion only pins the list the picker's
            // filter and the drop's filter both read.
            Require(new[] { ".png", ".jpg", ".jpeg", ".tif", ".TIF", ".tiff", ".psd", ".psb" }.All(IsImportableExtension),
                "the import gate dropped a format this build can read.");
            Require(!IsImportableExtension(".heic") && !IsImportableExtension(".txt") && !IsImportableExtension(""),
                "the import gate took a format this build cannot decode.");
            report["importGateTakesTiffAndNotHeic"] = "passed";
            var folder = Guid.NewGuid(); scene.Snapshot.Manifest.Layers.Add(new ProjectLayerRecord { Id = folder, Name = "Destination", IsGroup = true,
                IsVisible = true, Transform = new() { Size = new(100, 80) } }); scene.SetActiveLayer(folder); await WaitForFramesAsync();
            var result = await ImportFilesAsync(scene, [firstPath, secondPath], new(25.75, -2.2)); await WaitForFramesAsync(); AssertUploadedFrame(scene);
            Require(result.ImportedFiles == 2 && result.Failures.Count == 0 && !result.Retired, "Successful file batch returned failure.");
            foreach (var layer in scene.Snapshot.Manifest.Layers.Where(l => l.ImageFile is not null && l.Name != "Background"))
                Require(layer.ParentId == folder && layer.Transform.Origin == new PointD(23, -4), "File drop lost its point or selected parent.");
            Require(!collapsedGroups.Contains(folder) && selectedLayerId == scene.Snapshot.Manifest.ActiveLayerId, "Import did not reveal/select its layer.");
            OnUndo(this, new()); await WaitForFramesAsync(); Require(scene.LayerCount == 2 && !scene.CanUndo, "Drop batch history split.");
            report["fileDropPointParentSelectionAndBatchUndoGpu"] = "passed";

            // The gate above only says the extension is accepted; this is the file actually arriving. The bytes
            // are hand-built because WIC's TIFF codec can decode but the product never encodes TIFF, so there is
            // no round trip to lean on.
            scene = EditorDocument.Create(100, 80); OpenDocument(scene); await WaitForFramesAsync();
            string tiffPath = Path.Combine(directory, "Plate.tif");
            var tiffPixels = new byte[4 * 3 * 3];
            for (int pixel = 0; pixel < 12; pixel++)
            { tiffPixels[pixel * 3] = (byte)(10 + pixel); tiffPixels[pixel * 3 + 1] = (byte)(200 - pixel); tiffPixels[pixel * 3 + 2] = 90; }
            await File.WriteAllBytesAsync(tiffPath, FileImportBaselineTiff(4, 3, tiffPixels));
            result = await ImportFilesAsync(scene, [tiffPath]); await WaitForFramesAsync(); AssertUploadedFrame(scene);
            Require(result.ImportedFiles == 1 && result.Failures.Count == 0 && scene.LayerCount == 2,
                "A .tif on disk did not become one layer through the import entry the picker and the drop both call.");
            var plate = scene.Snapshot.Manifest.Layers[^1];
            Require(plate.Name == "Plate" && plate.Transform.Size.Width == 4 && plate.Transform.Size.Height == 3,
                "The imported TIFF lost its file name or its natural size.");
            var decoded = scene.Snapshot.Images[plate.Id];
            Require(Enumerable.Range(0, 12).All(index => decoded.Pixels[index * 4] == (byte)(10 + index)
                    && decoded.Pixels[index * 4 + 1] == (byte)(200 - index) && decoded.Pixels[index * 4 + 2] == 90
                    && decoded.Pixels[index * 4 + 3] == 255),
                "The imported TIFF's stored pixels drifted from the RGB written into the file.");
            report["aTiffOnDiskImportsAsItsOwnPixels"] = "passed";

            scene = EditorDocument.Create(100, 80); OpenDocument(scene); await WaitForFramesAsync();
            result = await ImportFilesAsync(scene, [Path.Combine(directory, "Missing.png"), firstPath, Path.Combine(directory, "Broken.psd")]);
            await WaitForFramesAsync(); AssertUploadedFrame(scene);
            Require(result.ImportedFiles == 1 && result.Failures.Count == 2 && scene.LayerCount == 2,
                "A failed file aborted later successes or was not reported.");
            Require(StatusProgress.Visibility != Microsoft.UI.Xaml.Visibility.Visible, "Import failure left the busy indicator active.");
            OnUndo(this, new()); await WaitForFramesAsync(); Require(!scene.CanUndo && scene.LayerCount == 1, "Partial batch had extra history.");
            report["fileReadFailuresKeepSuccessAndRestoreBusyWithOneUndo"] = "passed";

            var decodeHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var decodeRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            scene = EditorDocument.Create(100, 80); OpenDocument(scene); await WaitForFramesAsync();
            beforeImportDecode = _ => { decodeHeld.TrySetResult(); return decodeRelease.Task; };
            var queued = ImportFilesAsync(scene, [firstPath]); var replacement = EditorDocument.Create(120, 90);
            try
            {
                await decodeHeld.Task.WaitAsync(TimeSpan.FromSeconds(5));
                OnAddFolder(this, new()); OnUndo(this, new()); OnFreeTransform(this, new()); BeginPointerStroke(new(5, 5));
                Require(scene.LayerCount == 1 && !scene.CanUndo && pointerStroke is null && !renderInputs.HasPending,
                    "Import decode allowed content input/history or a new transform.");
                OpenDocument(replacement);
                result = await queued.WaitAsync(TimeSpan.FromSeconds(5));
                Require(result.Retired && result.ImportedFiles == 0 && !scene.CanUndo && !replacement.CanUndo,
                    "Retiring a decode waited for it or published into another owner.");
                Require(StatusProgress.Visibility != Microsoft.UI.Xaml.Visibility.Visible, "Old import retained the new owner's busy state.");
            }
            finally { decodeRelease.TrySetResult(); beforeImportDecode = null; await queued; }
            await WaitForFramesAsync(); AssertUploadedFrame(replacement);
            report["heldFileDecodeBlocksEditsAndRetiresWithoutPublishingToNewOwner"] = "passed";

            var firstHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int decodes = 0; scene = EditorDocument.Create(100, 80); OpenDocument(scene); await WaitForFramesAsync();
            beforeImportDecode = path => { Interlocked.Increment(ref decodes); if (path == firstPath) { firstHeld.TrySetResult(); return firstRelease.Task; } return Task.CompletedTask; };
            var first = ImportFilesAsync(scene, [firstPath]); await firstHeld.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = ImportFilesAsync(scene, [secondPath], new(70, 60));
            try { await Task.Delay(60); Require(Volatile.Read(ref decodes) == 1 && !second.IsCompleted, "Requests decoded concurrently or overtook provider order."); }
            finally { firstRelease.TrySetResult(); await first; await second; beforeImportDecode = null; }
            await WaitForFramesAsync(); AssertUploadedFrame(scene);
            Require(scene.Snapshot.Manifest.Layers[^1].Name == "Second" && scene.Snapshot.Manifest.Layers[^1].Transform.Origin == new PointD(68, 58), "Queued request lost its placement.");
            OnUndo(this, new()); await WaitForFramesAsync(); Require(scene.LayerCount == 2 && scene.CanUndo, "Separate import requests coalesced together.");
            OnUndo(this, new()); await WaitForFramesAsync(); Require(scene.LayerCount == 1 && !scene.CanUndo, "Request history had additional steps.");
            report["fileRequestsSerializeDecodeAndKeepSeparateUndoSteps"] = "passed";

            scene = EditorDocument.Create(100, 80); OpenDocument(scene); await WaitForFramesAsync();
            var secondHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int uiThread = Environment.CurrentManagedThreadId, decodeThread = uiThread;
            beforeImportDecode = path =>
            {
                decodeThread = Environment.CurrentManagedThreadId;
                if (path == secondPath) { secondHeld.TrySetResult(); return secondRelease.Task; }
                return Task.CompletedTask;
            };
            var partial = ImportFilesAsync(scene, [firstPath, secondPath]);
            try
            {
                await secondHeld.Task.WaitAsync(TimeSpan.FromSeconds(5)); await WaitForFramesAsync(); AssertUploadedFrame(scene);
                Require(decodeThread != uiThread && scene.LayerCount == 2 && scene.CanUndo && !partial.IsCompleted,
                    "Import withheld the first success or decoded on the UI thread.");
            }
            finally { secondRelease.TrySetResult(); await partial; beforeImportDecode = null; }
            await WaitForFramesAsync(); OnUndo(this, new()); await WaitForFramesAsync(); AssertUploadedFrame(scene);
            Require(scene.LayerCount == 1 && !scene.CanUndo && scene.CanRedo, "Incremental publication split batch history.");
            var noChange = scene.Snapshot;
            result = await ImportFilesAsync(scene, [Path.Combine(directory, "Unreadable.png")]); await WaitForFramesAsync();
            Require(result.ImportedFiles == 0 && result.Failures.Count == 1 && ReferenceEquals(noChange, scene.Snapshot)
                && scene.CanRedo && !scene.CanUndo && !scene.IsDirty && !IsFileImporting, "All-failed import changed history or retained its lease.");
            OnRedo(this, new()); await WaitForFramesAsync(); AssertUploadedFrame(scene);
            Require(scene.LayerCount == 3, "All-failed import lost the preceding Redo.");
            report["fileWorkerIncrementalGpuPublicationAndAllFailedRedo"] = "passed";

            scene = EditorDocument.Create(100, 80); OpenDocument(scene); ToolCombo.SelectedIndex = (int)CanvasTool.Brush; await WaitForFramesAsync();
            BeginPointerStroke(new(8, 8)); cropRectangle = new(5, 5, 20, 20); cropHasBox = true;
            result = await ImportFilesAsync(scene, [firstPath]); await WaitForFramesAsync(); AssertUploadedFrame(scene);
            Require(result.ImportedFiles == 1 && pointerStroke is null && cropRectangle is null && !cropHasBox, "Import retained a stroke or crop draft.");
            OnUndo(this, new()); await WaitForFramesAsync(); Require(scene.LayerCount == 1 && scene.CanUndo, "Import consumed the preceding stroke history.");
            OnUndo(this, new()); await WaitForFramesAsync(); AssertUploadedFrame(scene); Require(!scene.CanUndo, "Stroke finish created extra history.");
            report["fileImportFinishesStrokeCancelsCropAndKeepsSeparateHistoryGpu"] = "passed";

            scene = EditorDocument.Create(100, 80); OpenDocument(scene); ToolCombo.SelectedIndex = (int)CanvasTool.Healing;
            Require(await WaitForToolSwitchLandedAsync(CanvasTool.Healing),
                "the Healing switch never finished, so the repair below runs in the workspace of the tool it replaced.");
            await WaitForFramesAsync();
            var healingId = scene.Snapshot.Manifest.ActiveLayerId!.Value;
            var repairEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var repairRelease = new ManualResetEventSlim(false);
            bool movedRepairToOrigin = false;
            beforeFrameRender = () =>
            {
                if (healingStroke is { } repair)
                {
                    if (repair.PreviewSnapshot.Images[healingId].Pixels[0] != 255) movedRepairToOrigin = true;
                    repairEntered.TrySetResult();
                    if (!repairRelease.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Import repair reader timed out.");
                }
            };
            Task<FileImportResult>? afterRepair = null;
            try
            {
                BeginHealing(new(70, 60)); await repairEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                afterRepair = ImportFilesAsync(scene, [firstPath]); await Task.Delay(60);
                Require(!afterRepair.IsCompleted && healingFinishing, "Import decoded before the repair reader finished.");
            }
            finally { repairRelease.Set(); if (afterRepair is not null) await afterRepair; beforeFrameRender = null; }
            await WaitForFramesAsync(); AssertUploadedFrame(scene);
            Require(!movedRepairToOrigin && healingStroke is null && !healingFinishing && scene.LayerCount == 2,
                "Finishing repair for import appended an invented endpoint or retained the repair.");
            report["fileImportFinishesRepairWithoutInventingEndpointGpu"] = "passed";

            scene = EditorDocument.Create(100, 80); OpenDocument(scene); await WaitForFramesAsync();
            var retireRepairEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var retireRepairRelease = new ManualResetEventSlim(false);
            beforeFrameRender = () => { retireRepairEntered.TrySetResult(); if (!retireRepairRelease.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Retired import repair reader timed out."); };
            Task<FileImportResult>? retiringRepair = null; var nextOwner = EditorDocument.Create(120, 90);
            try
            {
                BeginHealing(new(70, 60)); await retireRepairEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                retiringRepair = ImportFilesAsync(scene, [firstPath]); await Task.Delay(60);
                OpenDocument(nextOwner); cropRectangle = new(4, 5, 20, 25); cropHasBox = true;
                await Task.Delay(60);
                report["fileRetiredRepairPreparationObserved"] = new { crop = cropRectangle, cropHasBox, pending = renderInputs.HasPending, completed = retiringRepair.IsCompleted };
                result = await retiringRepair.WaitAsync(TimeSpan.FromSeconds(5));
                Require(result.Retired && cropRectangle == new RectI(4, 5, 20, 25) && cropHasBox && !nextOwner.CanUndo && !renderInputs.HasPending,
                    "Retired import preparation changed the new owner's crop, history or input queue.");
            }
            finally { beforeFrameRender = null; retireRepairRelease.Set(); if (retiringRepair is not null) await retiringRepair; ClearCropBox(); }
            await WaitForFramesAsync(); AssertUploadedFrame(nextOwner);
            report["fileRetirementDuringRepairPreparationPreservesNewOwnerCropAndQueue"] = "passed";

            scene = EditorDocument.Create(100, 80); OpenDocument(scene); await WaitForFramesAsync();
            var accessDecode = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            beforeImportDecode = _ => { accessDecode.TrySetResult(); return Task.CompletedTask; };
            projectDialogDepth++;
            var waiting = ImportFilesAsync(scene, [firstPath]);
            try { await Task.Delay(60); Require(!accessDecode.Task.IsCompleted && !waiting.IsCompleted && !scene.CanUndo, "Incoming import bypassed project dialog access."); }
            finally { projectDialogDepth--; await waiting; beforeImportDecode = null; }
            await WaitForFramesAsync(); AssertUploadedFrame(scene);
            report["fileIncomingRequestWaitsForProjectDialogAccess"] = "passed";

            string flattenedPath = Path.Combine(directory, "Flat.psd");
            await File.WriteAllBytesAsync(flattenedPath, FileImportCompositePsd());
            scene = EditorDocument.Create(100, 80); OpenDocument(scene); await WaitForFramesAsync();
            result = await ImportFilesAsync(scene, [flattenedPath]); await WaitForFramesAsync(); AssertUploadedFrame(scene);
            var flat = scene.Snapshot.Manifest.Layers[^1];
            Require(result.ImportedFiles == 1 && !flat.IsGroupOrFalse && flat.Name == "Flat" && flat.Transform.Origin == new PointD(49, 39)
                && scene.Snapshot.Images[flat.Id].Pixels.AsSpan().SequenceEqual(new byte[] { 232, 90, 84, 255, 16, 200, 11, 255, 128, 70, 255, 255, 64, 40, 33, 255 }),
                "PSD merged RLE fallback lost filename, centered placement or RGB planes.");
            var saved = Path.Combine(directory, "import.comp"); await ProjectStore.SaveAsync(scene.Snapshot, saved);
            var loaded = await ProjectStore.LoadAsync(saved);
            Require(ManifestJson.Serialize(loaded.Manifest) == ManifestJson.Serialize(scene.Snapshot.Manifest)
                && loaded.Images[flat.Id].Pixels.AsSpan().SequenceEqual(scene.Snapshot.Images[flat.Id].Pixels), "File import did not survive a real project save/reload.");
            OnUndo(this, new()); await WaitForFramesAsync(); AssertUploadedFrame(scene); Require(!scene.CanUndo && scene.LayerCount == 1, "PSD fallback had extra history.");
            report["filePsdRleMergedFallbackRgbGpuAndProjectStoreRoundTrip"] = "passed";

            await VerifyPsdBudgetsAsync(directory, report);

            scene = EditorDocument.Create(200, 140); var sourceId = scene.Snapshot.Manifest.ActiveLayerId!.Value;
            var sourcePixels = scene.Snapshot.Images[sourceId]; originalSelection = Selection.Rectangle(200, 140, 30, 40, 24, 16);
            scene.SetSelection(originalSelection); OpenDocument(scene); await WaitForFramesAsync(); OnFreeTransform(this, new()); await WaitForFramesAsync();
            ChangeTransformNumber(TransformField.X, 65); await WaitForFramesAsync(); var start = CurrentTransformValue!.Center;
            showTransformControls = false; BeginTransformPointer(start, VirtualKeyModifiers.Menu); await WaitForFramesAsync(); showTransformControls = true;
            var copyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var importDecoded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var copyRelease = new ManualResetEventSlim(false);
            beforeFrameRender = () => { copyEntered.TrySetResult(); if (!copyRelease.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Import copy reader timed out."); };
            beforeImportDecode = _ => { importDecoded.TrySetResult(); return Task.CompletedTask; };
            Task<FileImportResult>? afterCopy = null;
            try
            {
                MoveTransformPointer(new(start.X + 20, start.Y + 11), VirtualKeyModifiers.Menu | VirtualKeyModifiers.Control);
                await copyEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                afterCopy = ImportFilesAsync(scene, [firstPath]); await Task.Delay(60);
                Require(!importDecoded.Task.IsCompleted && !afterCopy.IsCompleted && floatingDuplicate is not null && !scene.CanUndo,
                    "Import bypassed the pending floating merge/copy.");
            }
            finally { beforeFrameRender = null; copyRelease.Set(); if (afterCopy is not null) await afterCopy; beforeImportDecode = null; }
            await WaitForFramesAsync(); AssertUploadedFrame(scene);
            Require(scene.LayerCount == 3 && scene.Snapshot.Manifest.Layers[^2].Transform.Origin == new PointD(85, 51), "Import lost the released Alt copy.");
            OnUndo(this, new()); await WaitForFramesAsync(); Require(scene.LayerCount == 2 && scene.CanUndo, "Import Undo consumed the copy.");
            OnUndo(this, new()); await WaitForFramesAsync(); Require(scene.LayerCount == 1 && scene.CanUndo, "Copy Undo consumed the merge.");
            OnUndo(this, new()); await WaitForFramesAsync(); AssertUploadedFrame(scene);
            Require(!scene.CanUndo && ReferenceEquals(sourcePixels, scene.Snapshot.Images[sourceId]) && ReferenceEquals(originalSelection, scene.ActiveSelection),
                "File continuation did not preserve merge, copy and import as three exact history steps.");
            report["fileImportWaitsForFloatingAltMergeCopyGpuThreeUndo"] = "passed";
        }
        finally { beforeImportDecode = null; beforeFrameRender = null; projectDialogProbe = null; Directory.Delete(directory, true); }
    }

    private static byte[] FileImportCompositePsd()
    {
        using var stream = new MemoryStream(); stream.Write("8BPS"u8); U16(1); stream.Write(new byte[6]); U16(3);
        U32(2); U32(2); U16(8); U16(3); U32(0); U32(0); U32(0); U16(1);
        for (int i = 0; i < 6; i++) U16(3);
        foreach (var plane in new byte[][] { [232, 16, 128, 64], [90, 200, 70, 40], [84, 11, 255, 33] })
            for (int row = 0; row < 2; row++) { stream.WriteByte(1); stream.Write(plane.AsSpan(row * 2, 2)); }
        return stream.ToArray();
        void U16(int value) => stream.Write([(byte)(value >> 8), (byte)value]);
        void U32(int value) => stream.Write([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
    }

    private static byte[] FileImportBaselineTiff(int width, int height, byte[] rgb)
    {
        using var stream = new MemoryStream();
        const int bitsOffset = 122, pixelOffset = 128;
        stream.Write("II"u8); U16(42); U32(8);
        U16(9);
        U16(256); U16(4); U32(1); U32(width);
        U16(257); U16(4); U32(1); U32(height);
        U16(258); U16(3); U32(3); U32(bitsOffset);
        U16(259); U16(3); U32(1); U16(1); U16(0);
        U16(262); U16(3); U32(1); U16(2); U16(0);
        U16(273); U16(4); U32(1); U32(pixelOffset);
        U16(277); U16(3); U32(1); U16(3); U16(0);
        U16(278); U16(4); U32(1); U32(height);
        U16(279); U16(4); U32(1); U32(rgb.Length);
        U32(0);
        U16(8); U16(8); U16(8);
        stream.Write(rgb);
        return stream.ToArray();
        void U16(int value) => stream.Write([(byte)value, (byte)(value >> 8)]);
        void U32(int value) => stream.Write([(byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24)]);
    }
}
#endif
