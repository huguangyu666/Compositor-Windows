#if DEBUG
using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task VerifyFloatingCommandsAsync(Dictionary<string, object> report)
    {
        var scene = FloatingTargetScene(); var selection = scene.ActiveSelection;
        var sceneSourceId = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        Action[] commands = [
            () => OnAddFolder(this, new()), () => OnGroupLayers(this, new()), () => OnMergeLayers(this, new()),
            () => OnToggleClippingMask(this, new()), () => OnToggleMaskLink(this, new()), () => OnToggleMaskEnabled(this, new()),
            () => OnDuplicateLayers(this, new()), () => OnMoveOutOfFolder(this, new()), () => OnUngroupLayer(this, new()),
            () => OnRaiseLayer(this, new()), () => OnLowerLayer(this, new()), () => OnDeleteLayer(this, new()),
            () => OnInvertPixels(this, new()), () => OnInvertMask(this, new()), () => OnContentFill(this, new()),
            () => OnAddMask(this, new()), () => OnAddHideAllMask(this, new()), () => OnRevealSelectionMask(this, new()),
            () => OnHideSelectionMask(this, new()), () => OnRemoveMask(this, new()),
            () => OnAddAdjustment(new Button { Tag = "Invert" }, new()), () => OnLayerViaCopy(this, new()),
            () => OpacitySlider.Value = 45, () => OpacityBox.Value = 66, () => BlendCombo.SelectedIndex = 1,
            () => VisibleCheck.IsChecked = false,
            () => ((LayerRow)layerRows[sceneSourceId].Tag).Eye.IsChecked = false ];
        var original = ManifestJson.Serialize(scene.Snapshot.Manifest);
        OpenDocument(scene); await WaitForFramesAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(false);
        beforeFrameRender = () => { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Command preparation reader timed out."); };
        try
        {
            OnFreeTransform(this, new()); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Require(floatingTransform is { Display: null }, "Command preparation fixture already published.");
            AssertCommandsUnchanged("preparing");
        }
        finally { beforeFrameRender = null; release.Set(); }
        await WaitForTransformStateAsync(() => floatingTransform?.Display is not null); await WaitForFramesAsync();
        report["floatingPreparingLayerAndPixelCommandsRejectDraft"] = "passed";
        AssertCommandsUnchanged("ready"); report["floatingReadyLayerAndPixelCommandsRejectDraft"] = "passed";
        QueueTransformCancel(); await WaitForFramesAsync(); AssertUploadedFrame(scene);

        scene.SetSelection(null); original = ManifestJson.Serialize(scene.Snapshot.Manifest); selection = null;
        OnFreeTransform(this, new()); await WaitForFramesAsync();
        AssertCommandsUnchanged("persistent whole-layer");
        QueueTransformCancel(); await WaitForFramesAsync();
        report["persistentTransformLayerAndPixelCommandsRejectDraft"] = "passed";

        scene = FloatingTargetScene(); sceneSourceId = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        original = ManifestJson.Serialize(scene.Snapshot.Manifest); selection = scene.ActiveSelection;
        OpenDocument(scene); await WaitForFramesAsync(); OnFreeTransform(this, new()); await WaitForFramesAsync();
        var start = CurrentTransformValue!.Center;
        showTransformControls = false; BeginTransformPointer(start, VirtualKeyModifiers.Menu); await WaitForFramesAsync(); showTransformControls = true;
        var copyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var copyRelease = new ManualResetEventSlim(false);
        beforeFrameRender = () => { copyEntered.TrySetResult(); if (!copyRelease.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Command copy reader timed out."); };
        var gapChecked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            MoveTransformPointer(new(start.X + 20, start.Y + 11), VirtualKeyModifiers.Menu | VirtualKeyModifiers.Control);
            await copyEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Require(floatingDuplicate is not null, "Command fixture did not start the copy continuation.");
            AssertCommandsUnchanged("pending copy merge");
            QueueCanvasInput(scene, () =>
            {
                Require(floatingTransform is null && floatingDuplicate is not null, "Command fixture missed the merge/copy gap.");
                original = ManifestJson.Serialize(scene.Snapshot.Manifest); selection = scene.ActiveSelection;
                AssertCommandsUnchanged("merge/copy gap"); gapChecked.TrySetResult();
            });
        }
        finally { beforeFrameRender = null; copyRelease.Set(); }
        await gapChecked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForTransformStateAsync(() => transformDuplicateSelection is not null); await WaitForFramesAsync();
        QueueTransformCancel(); await WaitForFramesAsync(); AssertUploadedFrame(scene);
        Require(scene.LayerCount == 2 && !scene.CanUndo, "Draft commands or copy cancellation changed the unchanged source history.");
        report["floatingPendingCopyAndMergeGapCommandsKeepSourceAndHistory"] = "passed";

        scene = FloatingTargetScene(); OpenDocument(scene); await WaitForFramesAsync();
        OnAddFolder(this, new()); await WaitForFramesAsync();
        Require(scene.LayerCount == 3 && scene.CanUndo, "Idle command guard also blocked normal layer editing.");
        OnUndo(this, new()); await WaitForFramesAsync(); Require(scene.LayerCount == 2 && !scene.CanUndo, "Idle folder Undo was not one step.");
        AssertUploadedFrame(scene); report["idleLayerCommandsRemainUndoable"] = "passed";

        void AssertCommandsUnchanged(string phase)
        {
            for (int index = 0; index < commands.Length; index++)
            {
                commands[index]();
                Require(original == ManifestJson.Serialize(scene.Snapshot.Manifest) && ReferenceEquals(selection, scene.ActiveSelection)
                    && !scene.CanUndo && !renderInputs.HasPending, "Layer/pixel command " + index + " mutated the " + phase + " transform source.");
            }
        }
    }
}
#endif
