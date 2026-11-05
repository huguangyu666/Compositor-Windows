#if DEBUG
using Compositor.Core;
using Windows.System;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task VerifyFloatingContinuationsAsync(Dictionary<string, object> report)
    {
        var scene = EditorDocument.Create(200, 140); var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        var selection = Selection.Rectangle(200, 140, 30, 40, 24, 16); var originalPixels = scene.Snapshot.Images[id];
        scene.SetSelection(selection); OpenDocument(scene); await WaitForFramesAsync();
        OnFreeTransform(this, new()); await WaitForFramesAsync(); ChangeTransformNumber(TransformField.X, 65); await WaitForFramesAsync();
        var start = CurrentTransformValue!.Center;
        showTransformControls = false; BeginTransformPointer(start, VirtualKeyModifiers.Menu); await WaitForFramesAsync(); showTransformControls = true;
        FinishPlacementDrag(); await WaitForFramesAsync();
        Require(floatingTransform is not null && transformPersistent && !scene.CanUndo && scene.LayerCount == 1,
            "Alt click without movement committed the floating draft or made a copy.");
        report["floatingAltClickPreservesPersistentDraft"] = "passed";
        showTransformControls = false; BeginTransformPointer(start, VirtualKeyModifiers.Menu); await WaitForFramesAsync(); showTransformControls = true;
        MoveTransformPointer(new(start.X + 20, start.Y + 11), VirtualKeyModifiers.Menu | VirtualKeyModifiers.Control);
        await WaitForTransformStateAsync(() => floatingTransform is null && transformDuplicateSelection is not null); await WaitForFramesAsync();
        Require(floatingTransform is null && transformDuplicateSelection is not null && LayerDisplaySnapshot!.Manifest.Layers.Count == 2
            && scene.LayerCount == 1 && scene.CanUndo && !transformPersistent,
            "Alt movement did not finish the floating merge and begin a whole-layer copy draft.");
        var copied = SelectedLayer!.Id;
        Require(copied != id && layerRows.ContainsKey(copied)
            && LayerDisplaySnapshot!.Images[copied].Pixels.AsSpan().SequenceEqual(scene.Snapshot.Images[id].Pixels),
            "Floating Alt copied the temporary lifted pixels instead of the merged source.");
        // EditorCanvas keeps the grab's original transform after beginDuplicateTransform.
        Near(85, CurrentTransformValue!.Origin.X, "Floating Alt grab X"); Near(51, CurrentTransformValue.Origin.Y, "Floating Alt grab Y");
        Near(24, CurrentTransformValue.Size.Width, "Floating Alt grab width"); Near(16, CurrentTransformValue.Size.Height, "Floating Alt grab height");
        AssertTransformUpload(LayerDisplaySnapshot!); FinishPlacementDrag(); await WaitForFramesAsync(); AssertUploadedFrame(scene);
        Require(scene.LayerCount == 2 && SelectedLayer?.Id == copied, "Floating Alt release lost its copied row.");
        OnUndo(this, new()); await WaitForFramesAsync(); AssertUploadedFrame(scene);
        Require(scene.LayerCount == 1 && scene.CanUndo && scene.ActiveSelection?.Bounds == new RectI(65, 40, 24, 16),
            "Copy Undo consumed the earlier floating merge.");
        OnUndo(this, new()); await WaitForFramesAsync(); AssertUploadedFrame(scene);
        Require(!scene.CanUndo && ReferenceEquals(originalPixels, scene.Snapshot.Images[id]) && ReferenceEquals(selection, scene.ActiveSelection),
            "Floating Alt did not preserve two exact history steps.");
        report["floatingAltMergedSourceCopyRowsGrabGeometryGpuTwoUndo"] = "passed";

        foreach (var boundary in new[] { "release", "cancel", "capture", "owner", "output" })
        {
            scene = EditorDocument.Create(200, 140); id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
            originalPixels = scene.Snapshot.Images[id]; selection = Selection.Rectangle(200, 140, 30, 40, 24, 16);
            scene.SetSelection(selection); OpenDocument(scene); await WaitForFramesAsync(); OnFreeTransform(this, new()); await WaitForFramesAsync();
            ChangeTransformNumber(TransformField.X, 65); await WaitForFramesAsync(); start = CurrentTransformValue!.Center;
            showTransformControls = false; BeginTransformPointer(start, VirtualKeyModifiers.Menu); await WaitForFramesAsync(); showTransformControls = true;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var release = new ManualResetEventSlim(false);
            beforeFrameRender = () => { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Floating copy reader timed out."); };
            FloatingDuplicateGesture copying;
            Task? output = null; var replacement = EditorDocument.Create(200, 140);
            try
            {
                MoveTransformPointer(new(start.X + 20, start.Y + 11), VirtualKeyModifiers.Menu | VirtualKeyModifiers.Control);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); copying = floatingDuplicate!;
                Require(copying is not null && !scene.CanUndo, "Held reader did not block the floating copy merge.");
                MoveTransformPointer(new(start.X + 40, start.Y + 19), VirtualKeyModifiers.Menu | VirtualKeyModifiers.Control);
                if (boundary == "release") { FinishPlacementDrag(); OnHealingPointerCanceled(this, null!); }
                else if (boundary == "cancel") QueueTransformCancel();
                else if (boundary == "capture") OnHealingPointerCanceled(this, null!);
                else if (boundary == "owner") OpenDocument(replacement);
                else { output = FinishTransformBeforeOutputAsync(); Require(!output.IsCompleted, "Output did not wait for the floating copy."); }
            }
            finally { beforeFrameRender = null; release.Set(); }
            if (output is not null) await output.WaitAsync(TimeSpan.FromSeconds(5));
            await copying.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitForFramesAsync();
            if (boundary == "owner")
            {
                AssertUploadedFrame(replacement); Require(!scene.CanUndo && !replacement.CanUndo && floatingDuplicate is null,
                    "An old owner published floating merge/copy after replacement.");
            }
            else
            {
                await WaitForTransformStateAsync(() => floatingTransform is null); await WaitForFramesAsync(); AssertUploadedFrame(scene);
                Require(scene.CanUndo && scene.ActiveSelection?.Bounds == new RectI(65, 40, 24, 16), "Floating boundary lost the prior merge/selection.");
                if (boundary is "release" or "output")
                {
                    Require(scene.LayerCount == 2 && transformInspectorEdit is null, "Released/output floating copy did not finish in one copy step.");
                    var copy = scene.Snapshot.Manifest.Layers.Single(layer => layer.Id != id);
                    Near(105, copy.Transform.Origin.X, "Latest released floating copy X"); Near(59, copy.Transform.Origin.Y, "Latest released floating copy Y");
                    OnUndo(this, new()); await WaitForFramesAsync(); Require(scene.LayerCount == 1 && scene.CanUndo, "Copy Undo lost the prior merge.");
                }
                else Require(scene.LayerCount == 1 && transformInspectorEdit is null, "Cancelling the pending copy left a copied layer/draft.");
                OnUndo(this, new()); await WaitForFramesAsync(); AssertUploadedFrame(scene);
                Require(!scene.CanUndo && ReferenceEquals(originalPixels, scene.Snapshot.Images[id]) && ReferenceEquals(selection, scene.ActiveSelection),
                    "Floating copy boundary lost exact source history.");
            }
            report["floatingAltHeldReader" + boundary] = "passed";
        }

        // A press that lands while a copy gesture is still parked on its reader must not take the
        // slot: replacing `floatingDuplicate` would strand the old gesture's Completion, and the
        // output barrier awaits that task with no timeout of its own.
        scene = EditorDocument.Create(200, 140); id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        originalPixels = scene.Snapshot.Images[id]; selection = Selection.Rectangle(200, 140, 30, 40, 24, 16);
        scene.SetSelection(selection); OpenDocument(scene); await WaitForFramesAsync();
        OnFreeTransform(this, new()); await WaitForFramesAsync();
        ChangeTransformNumber(TransformField.X, 65); await WaitForFramesAsync(); start = CurrentTransformValue!.Center;
        showTransformControls = false; BeginTransformPointer(start, VirtualKeyModifiers.Menu); await WaitForFramesAsync(); showTransformControls = true;
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var parkedRelease = new ManualResetEventSlim(false);
        beforeFrameRender = () => { parked.TrySetResult(); if (!parkedRelease.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Floating copy press fixture timed out."); };
        FloatingDuplicateGesture held;
        try
        {
            MoveTransformPointer(new(start.X + 20, start.Y + 11), VirtualKeyModifiers.Menu | VirtualKeyModifiers.Control);
            await parked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            held = floatingDuplicate!;
            Require(held is not null && !held.Completion.Task.IsCompleted, "Press fixture did not park a copy gesture.");
            BeginTransformPointer(new(start.X + 60, start.Y + 40), VirtualKeyModifiers.Menu);
            MoveTransformPointer(new(start.X + 70, start.Y + 45), VirtualKeyModifiers.Menu | VirtualKeyModifiers.Control);
            Require(held is not null && ReferenceEquals(floatingDuplicate, held) && !held.Completion.Task.IsCompleted,
                "A press during a parked copy replaced the gesture or signalled its Completion.");
            FinishPlacementDrag(); OnHealingPointerCanceled(this, null!);
        }
        finally { beforeFrameRender = null; parkedRelease.Set(); }
        await held.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForTransformStateAsync(() => floatingDuplicate is null && floatingTransform is null);
        await WaitForFramesAsync(); AssertUploadedFrame(scene);
        Require(floatingDuplicate is null && scene.LayerCount == 2 && transformInspectorEdit is null && scene.CanUndo,
            "The parked copy did not finish exactly once after an extra press.");
        Require(scene.ActiveSelection?.Bounds == new RectI(65, 40, 24, 16), "The extra press disturbed the selection the copy started from.");
        OnUndo(this, new()); await WaitForFramesAsync();
        Require(scene.LayerCount == 1 && scene.CanUndo, "Extra press during parked copy added a history step.");
        OnUndo(this, new()); await WaitForFramesAsync();
        Require(!scene.CanUndo && ReferenceEquals(originalPixels, scene.Snapshot.Images[id]),
            "Extra press during parked copy left a stray draft or history.");
        report["floatingCopyPressWhileParkedCannotStrandCompletion"] = "passed";

        scene = EditorDocument.Create(200, 140); id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        selection = Selection.Rectangle(200, 140, 30, 40, 24, 16); originalPixels = scene.Snapshot.Images[id];
        scene.SetSelection(selection); OpenDocument(scene); await WaitForFramesAsync(); OnFreeTransform(this, new()); await WaitForFramesAsync();
        ChangeTransformNumber(TransformField.X, 65); await WaitForFramesAsync(); start = CurrentTransformValue!.Center;
        showTransformControls = false; BeginTransformPointer(start, VirtualKeyModifiers.Menu); await WaitForFramesAsync(); showTransformControls = true;
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var queuedRelease = new ManualResetEventSlim(false);
        beforeFrameRender = () => { queued.TrySetResult(); if (!queuedRelease.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Queued floating reader timed out."); };
        Task queuedOutput;
        try
        {
            QueueCanvasInput(scene, () => SetTransformDraft(CopyTransform(CurrentTransformValue!)));
            await queued.Task.WaitAsync(TimeSpan.FromSeconds(5));
            MoveTransformPointer(new(start.X + 40, start.Y + 19), VirtualKeyModifiers.Menu | VirtualKeyModifiers.Control);
            Require(floatingDuplicate is null, "Queued copy fixture started before the reader released.");
            queuedOutput = FinishTransformBeforeOutputAsync(); Require(!queuedOutput.IsCompleted, "Queued output returned before its copy started.");
        }
        finally { beforeFrameRender = null; queuedRelease.Set(); }
        await queuedOutput.WaitAsync(TimeSpan.FromSeconds(5)); await WaitForFramesAsync(); AssertUploadedFrame(scene);
        Require(scene.LayerCount == 2 && floatingDuplicate is null && floatingTransform is null && transformInspectorEdit is null,
            "Output did not finish a copy triggered in the queued drain.");
        Near(105, scene.Snapshot.Manifest.Layers.Single(layer => layer.Id != id).Transform.Origin.X, "Queued output copy X");
        OnUndo(this, new()); await WaitForFramesAsync(); Require(scene.CanUndo && scene.LayerCount == 1, "Queued copy Undo lost the prior merge.");
        OnUndo(this, new()); await WaitForFramesAsync(); Require(!scene.CanUndo && ReferenceEquals(originalPixels, scene.Snapshot.Images[id]), "Queued copy history was not two steps.");
        report["floatingAltQueuedTriggerOutputWaitsForBothSteps"] = "passed";

        foreach (bool unchanged in new[] { false, true })
        {
            scene = EditorDocument.Create(200, 140); id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
            selection = Selection.Rectangle(200, 140, 30, 40, 24, 16); originalPixels = scene.Snapshot.Images[id];
            scene.SetSelection(selection); OpenDocument(scene); await WaitForFramesAsync(); OnFreeTransform(this, new()); await WaitForFramesAsync();
            if (!unchanged) { ChangeTransformNumber(TransformField.X, 65); await WaitForFramesAsync(); }
            start = CurrentTransformValue!.Center;
            showTransformControls = false; BeginTransformPointer(start, VirtualKeyModifiers.Menu); await WaitForFramesAsync(); showTransformControls = true;
            MoveTransformPointer(new(start.X + 20, start.Y + 11), VirtualKeyModifiers.Menu | VirtualKeyModifiers.Control);
            await WaitForTransformStateAsync(() => transformDuplicateSelection is not null); await WaitForFramesAsync(); AssertTransformUpload(LayerDisplaySnapshot!);
            if (unchanged) FinishPlacementDrag(); else QueueTransformCancel();
            await WaitForFramesAsync(); AssertUploadedFrame(scene);
            Require(scene.LayerCount == (unchanged ? 2 : 1) && scene.CanUndo && transformInspectorEdit is null,
                "Floating copy no-op/cancel boundary left incorrect rows/history.");
            OnUndo(this, new()); await WaitForFramesAsync(); AssertUploadedFrame(scene);
            Require(!scene.CanUndo && scene.LayerCount == 1 && ReferenceEquals(originalPixels, scene.Snapshot.Images[id]) && ReferenceEquals(selection, scene.ActiveSelection),
                "Unchanged floating merge added history, or copy cancel removed the prior merge.");
            report[unchanged ? "floatingAltUnchangedMergeOnlyOneCopyUndo" : "floatingAltReadyCopyCancelKeepsPriorMerge"] = "passed";
        }
    }
}
#endif
