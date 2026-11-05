#if DEBUG
using Compositor.Core;
using Microsoft.UI.Xaml;
using Windows.System;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task VerifyFloatingTransformAsync(Dictionary<string, object> report)
    {
        var scene = EditorDocument.Create(200, 140); var id = scene.Snapshot.Manifest.Layers[0].Id;
        var original = Selection.Rectangle(200, 140, 30, 40, 24, 16);
        scene.SetSelection(original); OpenDocument(scene); await WaitForFramesAsync();
        var originalManifest = ManifestJson.Serialize(scene.Snapshot.Manifest); var image = scene.Snapshot.Images[id];
        OnFreeTransform(this, new()); await WaitForFramesAsync(); RootGrid.UpdateLayout();
        Require(LayerDisplaySnapshot!.Manifest.Layers.Count == 2 && SelectedLayer?.Id != id,
            "Ctrl+T with selected pixels did not display a temporary floating sibling.");
        var floatingId = SelectedLayer!.Id;
        Require(layerRows.ContainsKey(floatingId) && transformPersistent && transformApplyButton.Visibility == Visibility.Visible,
            "Floating layer row or persistent Apply controls are missing.");
        Require(scene.LayerCount == 1 && !scene.CanUndo && ReferenceEquals(image, scene.Snapshot.Images[id])
            && originalManifest == ManifestJson.Serialize(scene.Snapshot.Manifest) && ReferenceEquals(original, scene.ActiveSelection),
            "Floating entry changed live document pixels, selection or history.");
        Near(30, CurrentTransformValue!.Origin.X, "Floating X"); Near(24, CurrentTransformValue.Size.Width, "Floating width");
        AssertTransformUpload(LayerDisplaySnapshot);
        report["floatingSelectionCtrlTTemporaryRowDocumentGridAndGpu"] = "passed";

        ChangeTransformNumber(TransformField.X, 65); await WaitForFramesAsync(); AssertTransformUpload(LayerDisplaySnapshot!);
        Require(!scene.CanUndo && ReferenceEquals(image, scene.Snapshot.Images[id]), "Floating preview changed live pixels/history.");
        await FinishTransformBeforeOutputAsync(); await WaitForFramesAsync();
        Require(scene.LayerCount == 1 && SelectedLayer?.Id == id && scene.CanUndo && scene.ActiveSelection?.Bounds == new RectI(65, 40, 24, 16),
            "Floating merge did not restore the source row and move the selected pixels/outline.");
        AssertUploadedFrame(scene); OnUndo(this, new()); await WaitForFramesAsync();
        Require(!scene.CanUndo && ReferenceEquals(image, scene.Snapshot.Images[id]) && ReferenceEquals(original, scene.ActiveSelection),
            "Floating pixel/selection Undo was not one exact step.");
        AssertUploadedFrame(scene); OnRedo(this, new()); await WaitForFramesAsync(); AssertUploadedFrame(scene);
        report["floatingSelectionMergePixelOutlineSingleUndoRedoGpu"] = "passed";

        OnUndo(this, new()); await WaitForFramesAsync();
        OnFreeTransform(this, new()); await WaitForFramesAsync();
        ChangeTransformNumber(TransformField.X, 110); ChangeTransformNumber(TransformField.Rotation, 27);
        await WaitForFramesAsync(); QueueTransformCancel(); await WaitForFramesAsync();
        Require(scene.CanRedo && !scene.CanUndo && ReferenceEquals(image, scene.Snapshot.Images[id]) && ReferenceEquals(original, scene.ActiveSelection)
            && SelectedLayer?.Id == id && LayerDisplaySnapshot!.Manifest.Layers.Count == 1,
            "Floating cancellation lost source pixels, selection, rows or Redo.");
        AssertUploadedFrame(scene);
        OnFreeTransform(this, new()); await WaitForFramesAsync(); await FinishTransformBeforeOutputAsync(); await WaitForFramesAsync();
        Require(!scene.CanUndo && scene.CanRedo && ReferenceEquals(image, scene.Snapshot.Images[id]), "Unchanged floating Apply added a seam/history.");
        Require(StatusText.Text == Loc.T("Tr_Unavailable"), "An unchanged floating Apply gave no feedback at all.");
        AssertUploadedFrame(scene); report["floatingSelectionCancelAndUnchangedApplyKeepPixelsAndRedo"] = "passed";

        scene = EditorDocument.Create(200, 140); id = scene.Snapshot.Manifest.Layers[0].Id;
        var other = scene.ImportImage(new Surface(3, 3), "Other").Id;
        scene.SetActiveLayer(id); scene = new EditorDocument(scene.Snapshot); scene.SetSelection(original);
        OpenDocument(scene); await WaitForFramesAsync(); OnFreeTransform(this, new()); await WaitForFramesAsync();
        ChangeTransformNumber(TransformField.X, 80); await WaitForFramesAsync();
        var switching = floatingTransform!;
        HandleThumbnailClick(other, false, VirtualKeyModifiers.None);
        await switching.Completion.Task;
        report["completionThumbnailSwitchBeforeTargetPublication"] = new { requested = other, selected = SelectedLayer?.Id,
            active = scene.Snapshot.Manifest.ActiveLayerId, pending = renderInputs.HasPending };
        await WaitForTransformStateAsync(() => floatingTransform is null && SelectedLayer?.Id == other);
        await WaitForFramesAsync();
        Require(scene.CanUndo && scene.ActiveSelection?.Bounds == new RectI(80, 40, 24, 16) && SelectedLayer?.Id == other,
            "Thumbnail selection raced the floating merge or lost the requested target.");
        AssertUploadedFrame(scene); OnUndo(this, new()); await WaitForFramesAsync();
        Require(!scene.CanUndo && ReferenceEquals(original, scene.ActiveSelection), "Layer switch created extra floating history.");
        report["floatingSelectionThumbnailSwitchWaitsForMerge"] = "passed";

        OpenDocument(scene); await WaitForFramesAsync(); OnFreeTransform(this, new()); await WaitForFramesAsync();
        report["floatingPointerFocusForegroundPrecondition"] = await RequireForegroundForFocusProbeAsync();
        Require(CanvasStage.Focus(FocusState.Programmatic), "Floating pointer fixture could not focus the canvas before pressing.");
        await WaitForTransformStateAsync(() => OutlineIsReady);
        var geometry = OutlineDisplayIdentity;
        BeginTransformPointer(new(42, 48), VirtualKeyModifiers.Control);
        MoveTransformPointer(new(72, 66), VirtualKeyModifiers.Control); await WaitForFramesAsync();
        Require(CurrentTransformValue!.Origin.X != 30 && ReferenceEquals(geometry, OutlineDisplayIdentity), "Floating drag did not reuse the outline geometry.");
        report["floatingPointerFocusBefore"] = new { canvas = CanvasStage.FocusState.ToString(), field = transformFields[TransformField.X].FocusState.ToString(),
            pointer = transformPointerDraftActive, finishing = placementFinishing, originalX = transformDrag?.Original.Origin.X };
        var focused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var focusEvents = new List<object>();
        var focusField = transformFields[TransformField.X];
        object FocusSnapshot() => new { fieldState = focusField.FocusState.ToString(),
            focusedType = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(RootGrid.XamlRoot)?.GetType().Name,
            fieldOwnsFocus = ReferenceEquals(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(RootGrid.XamlRoot), focusField),
            fieldStillCurrent = ReferenceEquals(focusField, transformFields[TransformField.X]),
            pointer = transformPointerDraftActive, originX = CurrentTransformValue?.Origin.X,
            activations = windowActivationCount, deactivations = windowDeactivationCount,
            lastActivationState };
        void ObserveFocus(object sender, RoutedEventArgs args)
        {
            focusEvents.Add(new { sourceType = args.OriginalSource?.GetType().Name,
                fieldIsSource = ReferenceEquals(args.OriginalSource, focusField), snapshot = FocusSnapshot() });
            if (ReferenceEquals(args.OriginalSource, transformFields[TransformField.X])) focused.TrySetResult();
        }
        RootGrid.GotFocus += ObserveFocus;
        try
        {
            Require(transformFields[TransformField.X].Focus(FocusState.Programmatic), "Floating focus fixture could not focus a real field.");
            report["floatingPointerFocusImmediately"] = FocusSnapshot();
            // FocusState changes before the routed GotFocus handlers finish.
            await focused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // The five-second budget and this rethrow are untouched; the post-mortem only runs once it is already
            // red. It asks the one question that splits the two remaining causes — the routed event never arrives
            // at all, versus the event arriving later than the budget — by waiting three more seconds and reading
            // whether anything came in, with the activation counters beside it.
            var extra = await Task.WhenAny(focused.Task, Task.Delay(3000));
            report["floatingPointerFocusPostmortem"] = new { arrivedInExtra3Seconds = ReferenceEquals(extra, focused.Task),
                eventsAfterExtra = focusEvents.Count, activations = windowActivationCount,
                deactivations = windowDeactivationCount, lastActivationState };
            report["floatingPointerFocusFailure"] = FocusSnapshot();
            report["floatingPointerFocusEvents"] = focusEvents;
            throw;
        }
        finally { RootGrid.GotFocus -= ObserveFocus; }
        // Recorded on the green path too: the reds show the field owning focus with no routed event at all, and
        // whether a green run's event is on time or merely inside the budget is the next question.
        report["floatingPointerFocusEventCount"] = focusEvents.Count;
        await WaitForFramesAsync();
        Near(30, CurrentTransformValue!.Origin.X, "Floating pointer focus loss restores grab placement");
        Require(transformPersistent && !scene.CanUndo, "Focus loss ended the persistent floating transaction.");
        QueueTransformCancel(); await WaitForFramesAsync(); AssertUploadedFrame(scene);
        report["floatingSelectionPointerFocusRestoresGrabAndKeepsDraft"] = "passed";

        OnFreeTransform(this, new()); await WaitForFramesAsync();
        ChangeTransformNumber(TransformField.X, 90); await WaitForFramesAsync();
        LayerList.SelectedItem = layerRows[other];
        await WaitForTransformStateAsync(() => floatingTransform is null && SelectedLayer?.Id == other); await WaitForFramesAsync();
        Require(scene.CanUndo && scene.ActiveSelection?.Bounds == new RectI(90, 40, 24, 16) && SelectedLayer?.Id == other,
            "Layer row selection raced the floating merge or lost the requested target.");
        AssertUploadedFrame(scene); OnUndo(this, new()); await WaitForFramesAsync();
        Require(!scene.CanUndo && ReferenceEquals(original, scene.ActiveSelection), "Layer row switch added extra floating history.");
        report["floatingSelectionLayerRowSwitchWaitsForMerge"] = "passed";

        scene = EditorDocument.Create(200, 140); scene.SetSelection(original); OpenDocument(scene); await WaitForFramesAsync();
        OnFreeTransform(this, new()); await WaitForFramesAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(false);
        beforeFrameRender = () => { entered.TrySetResult(); release.Wait(); };
        try
        {
            ChangeTransformNumber(TransformField.X, 85);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            ToolCombo.SelectedIndex = (int)CanvasTool.Brush; await Task.Delay(1);
            // #38 split these two facts: the tool the user picked is live the moment they pick it, while the
            // switch's own teardown stays suspended behind the render reader — which is what the merge is
            // still owed. So the field reads Brush here on purpose, and the proof that the switch has not
            // returned is that the merge it waits for has not committed.
            Require(tool == CanvasTool.Brush && !scene.CanUndo && floatingTransform is not null,
                "The floating merge committed while the tool switch was still waiting for it, so this no longer "
                + "holds a switch suspended behind a pending merge.");
        }
        finally { beforeFrameRender = null; release.Set(); }
        Require(await WaitForToolSwitchLandedAsync(CanvasTool.Brush), "The tool switch never finished after the merge was released.");
        await WaitForFramesAsync();
        Require(scene.CanUndo && scene.ActiveSelection?.Bounds == new RectI(85, 40, 24, 16) && floatingTransform is null,
            "Tool change lost the latest floating placement.");
        AssertUploadedFrame(scene); OnUndo(this, new()); await WaitForFramesAsync(); Require(!scene.CanUndo, "Tool change added extra floating history.");
        report["floatingSelectionToolSwitchDuringWorkerWaitsForLatestMerge"] = "passed";

        scene = EditorDocument.Create(200, 140); scene.SetSelection(original); OpenDocument(scene); await WaitForFramesAsync();
        OnFreeTransform(this, new()); await WaitForFramesAsync();
        var ownerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var ownerRelease = new ManualResetEventSlim(false);
        beforeFrameRender = () => { ownerEntered.TrySetResult(); ownerRelease.Wait(); };
        var replacement = EditorDocument.Create(200, 140);
        int failures = renderWorkerFailureCount;
        Task abandoned;
        try
        {
            ChangeTransformNumber(TransformField.X, 65); await ownerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            abandoned = FinishTransformBeforeOutputAsync(); OpenDocument(replacement);
        }
        finally { beforeFrameRender = null; ownerRelease.Set(); }
        await abandoned.WaitAsync(TimeSpan.FromSeconds(5)); await WaitForFramesAsync();
        Require(floatingTransform is null && !scene.CanUndo && !replacement.CanUndo, "Old floating owner published after document replacement.");
        AssertUploadedFrame(replacement);
        report["floatingSelectionOwnerReplacementCompletesQueuedBarrier"] = "passed";
        Require(renderWorkerFailureCount == failures, "Expected floating owner cancellation was reported as a worker failure.");
        report["floatingSelectionExpectedOwnerCancellationIsNotFailure"] = "passed";

        foreach (bool independent in new[] { false, true }) foreach (bool effects in new[] { false, true })
        {
            scene = EditorDocument.Create(128, 128); id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
            var layer = scene.Snapshot.Manifest.Layers[0]; var asset = new Surface(32, 32);
            for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++)
            { int index = y * asset.Stride + x * 4; asset.Pixels[index] = (byte)(x * 4); asset.Pixels[index + 1] = (byte)(y * 4); asset.Pixels[index + 3] = 160; }
            ((Dictionary<Guid, Surface>)scene.Snapshot.Images)[id] = asset;
            layer.Transform = new() { Origin = new(20, 20), Size = new(64, 64), Rotation = 30, FlipX = true };
            var mask = new MaskSurface(32, 32); for (int i = 0; i < mask.Pixels.Length; i++) mask.Pixels[i] = (byte)(96 + i % 160);
            ((Dictionary<Guid, MaskSurface>)scene.Snapshot.Masks)[id] = mask; layer.MaskFile = ProjectValidator.Uuid(id) + ".mask.png";
            if (independent) { layer.MaskLinked = false; layer.MaskPlacement = new() { Origin = new(16, 24), Size = new(72, 48) }; }
            if (effects) scene.SetLayerEffects(id, new() { Shadow = new() { Blur = 8, Distance = 5 } });
            scene = new(scene.Snapshot); original = Selection.Rectangle(128, 128, 24, 24, 40, 40).Feathered(2);
            scene.SetSelection(original); OpenDocument(scene); await WaitForFramesAsync();
            OnFreeTransform(this, new()); await WaitForFramesAsync();
            Require(floatingTransform is not null, "Rotated masked floating selection did not start.");
            AssertTransformUpload(LayerDisplaySnapshot!);
            ChangeTransformNumber(TransformField.X, 53); ChangeTransformNumber(TransformField.Rotation, 17);
            ChangeTransformOption(value => value.FlipX = true); await WaitForFramesAsync();
            AssertTransformUpload(LayerDisplaySnapshot!);
            await FinishTransformBeforeOutputAsync(); await WaitForFramesAsync(); AssertUploadedFrame(scene);
            Require(scene.CanUndo && scene.LayerCount == 1, "Masked floating merge lost its transaction/source row.");
            OnUndo(this, new()); await WaitForFramesAsync(); AssertUploadedFrame(scene);
            Require(!scene.CanUndo && ReferenceEquals(asset, scene.Snapshot.Images[id]) && ReferenceEquals(mask, scene.Snapshot.Masks[id])
                && ReferenceEquals(original, scene.ActiveSelection), "Masked floating Undo lost exact source state.");
            report[$"floatingSelectionRotatedFlipFeatherGpuMask{independent}Effects{effects}"] = "passed";
        }

        scene = EditorDocument.Create(200, 140); OpenDocument(scene); await WaitForFramesAsync();
        var selectionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var selectionRelease = new ManualResetEventSlim(false);
        QueueSelectionWork((_, _) => { selectionEntered.TrySetResult(); selectionRelease.Wait(); return Selection.Rectangle(200, 140, 30, 40, 24, 16); },
            false, "Selection ready");
        try
        {
            await selectionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); OnFreeTransform(this, new()); await WaitForFramesAsync();
            Require(floatingTransform is null && transformInspectorEdit is null, "Ctrl+T began a whole-layer draft before the pending selection was published.");
        }
        finally { selectionRelease.Set(); }
        await WaitForTransformStateAsync(() => selectionWorkResult is null); await WaitForFramesAsync();
        report["floatingSelectionPendingSelectionDoesNotFallBackToWholeLayer"] = "passed";

        scene = EditorDocument.Create(200, 140); original = Selection.Rectangle(200, 140, 30, 40, 24, 16);
        scene.SetSelection(original); OpenDocument(scene); await WaitForFramesAsync();
        var preparing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var prepareRelease = new ManualResetEventSlim(false);
        beforeFrameRender = () => { preparing.TrySetResult(); prepareRelease.Wait(); };
        try { OnFreeTransform(this, new()); await preparing.Task.WaitAsync(TimeSpan.FromSeconds(5)); OnUndo(this, new()); }
        finally { beforeFrameRender = null; prepareRelease.Set(); }
        await WaitForFramesAsync();
        Require(floatingTransform is null && transformInspectorEdit is null && !scene.CanUndo && ReferenceEquals(original, scene.ActiveSelection),
            "Undo during floating preparation retained a draft or changed selection/history.");
        AssertUploadedFrame(scene); report["floatingSelectionUndoDuringPreparationCancelsDraft"] = "passed";

        scene = EditorDocument.Create(4096, 4096); original = Selection.Rectangle(4096, 4096, 200, 200, 32, 32);
        scene.SetSelection(original); OpenDocument(scene); await WaitForFramesAsync();
        OnFreeTransform(this, new()); await WaitForFramesAsync(); var prepareFrame = lastFrameProbe;
        await WaitForTransformStateAsync(() => OutlineIsReady);
        var warm = compositor; geometry = OutlineDisplayIdentity;
        showTransformControls = false; BeginTransformPointer(new(216, 216), VirtualKeyModifiers.Control); await WaitForFramesAsync();
        showTransformControls = true;
        var ticks = new List<FrameProbe>();
        for (int i = 0; i < 14; i++)
        {
            int before = presentedFrameCount;
            MoveTransformPointer(new(226 + i, 219), VirtualKeyModifiers.Control); await WaitForFramesAsync();
            Require(presentedFrameCount == before + 1, "Floating performance sample did not produce a fresh frame.");
            if (i >= 2) ticks.Add(lastFrameProbe);
        }
        Require(ReferenceEquals(warm, compositor) && ReferenceEquals(geometry, OutlineDisplayIdentity) && ReferenceEquals(original, scene.ActiveSelection),
            "4K floating drag replaced its compositor/outline geometry or changed the live selection.");
        AssertTransformUpload(LayerDisplaySnapshot!);
        FinishPlacementDrag(); await WaitForFramesAsync();
        Require(floatingTransform is not null && !scene.CanUndo, "Pointer release prematurely merged the persistent floating draft.");
        report["floatingSelection4KWarmFrames"] = new { prepareFrame, samples = ticks.Count,
            tickMedianMs = ticks.Select(frame => frame.InputMs + frame.CpuMs + frame.UploadMs).Order().ElementAt(ticks.Count / 2),
            tickMaxMs = ticks.Max(frame => frame.InputMs + frame.CpuMs + frame.UploadMs),
            inputAndWorkerMedianKiB = ticks.Select(frame => (frame.InputBytes + frame.WorkerBytes) / 1024d).Order().ElementAt(ticks.Count / 2),
            band = ticks[^1].Band };
        RootGrid.UpdateLayout(); await Task.Delay(40); await SaveWorkspaceCaptureAsync("floating-selection-workspace.png");
        var savePath = Path.Combine(AppContext.BaseDirectory, "floating-save-probe.comp");
        // Use the output barrier and real store without altering the user's recent list.
        await FinishTransformBeforeOutputAsync(); await ProjectStore.SaveAsync(scene.Snapshot, savePath); await WaitForFramesAsync();
        var loaded = await ProjectStore.LoadAsync(savePath);
        Require(floatingTransform is null && scene.CanUndo && loaded.Manifest.Layers.Count == 1,
            "Saving serialized a temporary floating layer or lost its single history step.");
        id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        Require(ManifestJson.Serialize(loaded.Manifest) == ManifestJson.Serialize(scene.Snapshot.Manifest)
            && loaded.Images[id].Pixels.AsSpan().SequenceEqual(scene.Snapshot.Images[id].Pixels),
            "Saved floating pixels differ after project reload.");
        AssertUploadedFrame(scene); OnUndo(this, new()); await WaitForFramesAsync(); AssertUploadedFrame(scene);
        Require(!scene.CanUndo && ReferenceEquals(original, scene.ActiveSelection), "Save did not preserve the floating single Undo.");
        report["floatingSelectionSaveBarrierReloadPixelsAndSingleUndo"] = "passed";
    }
}
#endif
