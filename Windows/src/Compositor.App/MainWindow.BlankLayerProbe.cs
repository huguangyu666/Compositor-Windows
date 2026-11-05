#if DEBUG
using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Windows.System;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task VerifyBlankLayerWorkflowAsync(Dictionary<string, object> report)
    {
        var scene = EditorDocument.Create(32, 32); OpenDocument(scene); await WaitForFramesAsync();
        var source = scene.Snapshot.Images; var baseline = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        var selection = Selection.Rectangle(32, 32, 4, 4, 24, 24); scene.SetSelection(selection);
        await WaitForSelectionReadyAsync();
        var chord = NewBlankLayerMenuItem.KeyboardAccelerators.Single();
        Require(chord.Key == VirtualKey.N && chord.Modifiers == (VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift)
            && RemappableShortcuts().Any(item => item.Id == nameof(NewBlankLayerMenuItem)), "New Blank Layer has no rebindable default chord.");
        Require(BlankWorkspaceButton.IsEnabled && NewBlankLayerMenuItem.IsEnabled, "New Blank Layer starts disabled.");
        ((IInvokeProvider)new ButtonAutomationPeer(BlankWorkspaceButton).GetPattern(PatternInterface.Invoke)).Invoke();
        Require(await WaitUntilAsync(() => scene.LayerCount == 2, 2000), "The footer did not create a blank layer.");
        await WaitForFramesAsync(); var blank = SelectedLayer!; var blankId = blank.Id;
        Require(blank.Name == "Layer 1" && blank.ImageFile is null && !scene.Snapshot.Images.ContainsKey(blank.Id)
            && ReferenceEquals(source, scene.Snapshot.Images) && selectedLayerIds.SetEquals([blank.Id])
            && !paintOnMask && ReferenceEquals(selection, scene.ActiveSelection), "Blank creation changed source pixels, selection or its new target.");
        AssertUploadedFrame(scene);
        report["blankFooterAndRebindableMenuCreateAssetFreeTarget"] = "passed";

        ToolCombo.SelectedIndex = (int)CanvasTool.Brush;
        BrushSizeSlider.Value = 6; BrushHardnessSlider.Value = 100; BrushOpacitySlider.Value = 100;
        BeginPointerStroke(new(16, 16)); FinishPointerStroke(new(18, 16)); await WaitForFramesAsync();
        Require(scene.Snapshot.Images.ContainsKey(blankId) && SelectedLayer?.ImageFile is not null,
            "The first brush could not paint on the new layer.");
        Require(BlankWorkspaceButton.IsEnabled && NewBlankLayerMenuItem.IsEnabled, "The completed brush left New Blank Layer disabled.");
        AssertUploadedFrame(scene); OnUndo(this, new()); await WaitForFramesAsync();
        Require(scene.LayerCount == 2 && !scene.Snapshot.Images.ContainsKey(blankId) && SelectedLayer?.ImageFile is null,
            "First brush Undo did not restore the asset-free layer.");
        OnRedo(this, new()); await WaitForFramesAsync(); Require(scene.Snapshot.Images.ContainsKey(blankId), "First brush Redo lost pixels.");
        OnUndo(this, new()); await WaitForFramesAsync(); OnUndo(this, new()); await WaitForFramesAsync();
        Require(scene.LayerCount == 1 && !scene.CanUndo && SelectedLayer?.Id == baseline, "Creation and first brush did not form separate Undo steps.");
        report["blankFirstBrushUploadsAndUndoesSeparatelyFromCreation"] = "passed";

        scene = EditorDocument.Create(32, 32); scene.AddFolder(); var folder = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        var child = scene.ImportImage(new Surface(8, 8), "Inside"); child.ParentId = folder;
        scene.SetActiveLayer(folder); scene = new(scene.Snapshot); OpenDocument(scene); await WaitForFramesAsync();
        collapsedGroups.Add(folder); RefreshLayerPanel(); OnAddBlankLayer(NewBlankLayerMenuItem, new()); await WaitForFramesAsync();
        Require(SelectedLayer is { ImageFile: null } added && added.ParentId == folder && !collapsedGroups.Contains(folder)
            && layerRows.ContainsKey(added.Id) && layerItems.Any(item => ((LayerRow)item.Tag).Layer.Id == added.Id),
            "New Blank Layer did not enter and expand the selected folder.");
        OnUndo(this, new()); await WaitForFramesAsync(); Require(!scene.CanUndo && SelectedLayer?.Id == folder, "Folder creation Undo changed its previous history.");
        report["blankMenuEntersAndExpandsSelectedFolder"] = "passed";

        Require(BeginTransformInspector(persistent: true), "The blank refusal fixture did not begin a transform.");
        int count = scene.LayerCount; OnAddBlankLayer(this, new());
        Require(!BlankWorkspaceButton.IsEnabled && !NewBlankLayerMenuItem.IsEnabled && scene.LayerCount == count,
            "An active transform allowed blank-layer creation.");
        QueueTransformCancel(); await WaitForFramesAsync();
        Require(BlankWorkspaceButton.IsEnabled && NewBlankLayerMenuItem.IsEnabled, "Cancel did not restore New Blank Layer.");
        report["blankCreationRespectsDraftCommandAvailability"] = "passed";

        scene = EditorDocument.Create(32, 32); OpenDocument(scene); await WaitForFramesAsync();
        baseline = scene.Snapshot.Manifest.ActiveLayerId!.Value; var originalPixels = scene.Snapshot.Images[baseline];
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(false);
        beforeFrameRender = () =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Blank-layer frame control timed out.");
        };
        try
        {
            scene.SetOpacity(baseline, .75); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            OnAddBlankLayer(this, new()); blankId = SelectedLayer!.Id;
            Require(blankId != baseline && !scene.Snapshot.Images.ContainsKey(blankId), "A running frame delayed the new blank editing target.");
            BeginPointerStroke(new(16, 16)); FinishPointerStroke();
            beforeFrameRender = null; release.Set(); await WaitForFramesAsync();
            Require(scene.Snapshot.Images.ContainsKey(blankId) && ReferenceEquals(originalPixels, scene.Snapshot.Images[baseline]),
                "A quick brush after New Blank Layer painted the previous layer.");
            AssertUploadedFrame(scene);
        }
        finally { beforeFrameRender = null; release.Set(); }
        report["blankTargetPrecedesBrushQueuedBehindRunningFrame"] = "passed";
    }
}
#endif
