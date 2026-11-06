#if DEBUG
using Compositor.Core;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task VerifyMaskWorkflowAsync(Dictionary<string, object> report)
    {
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
        var scene = EditorDocument.Create(16, 16); OpenDocument(scene); await WaitForFramesAsync();
        var id = scene.Snapshot.Manifest.Layers[0].Id;
        var selection = Selection.Rectangle(16, 16, 4, 4, 8, 8); scene.SetSelection(selection);
        Require(Equals(ToolTipService.GetToolTip(MaskWorkspaceButton), Loc.T("A_AddMaskSelection")), "The footer hint ignored the active selection.");
        Invoke(MaskWorkspaceButton);
        Require(await WaitUntilAsync(() => scene.Snapshot.Masks.ContainsKey(id) && !IsDeletePending, 2500), "Footer mask work did not complete.");
        await WaitForFramesAsync();
        if (!scene.Snapshot.Masks.ContainsKey(id) || scene.ActiveSelection is not null || !paintOnMask)
            throw new InvalidOperationException("The footer mask button did not use the selection and target its new mask.");
        Require(scene.Snapshot.Masks[id].Pixels[0] == 0 && scene.Snapshot.Masks[id].Pixels[8 * 16 + 8] == 255 && !MaskWorkspaceButton.IsEnabled,
            "The footer did not reveal only the selected area or stayed enabled over its mask.");
        OnUndo(this, new()); await WaitForFramesAsync();
        Require(!scene.CanUndo && !paintOnMask && ReferenceEquals(selection, scene.ActiveSelection), "Footer selection-mask creation did not undo once with its selection.");
        report["footerMaskButtonConsumesSelectionAndTargetsMask"] = "passed";

        foreach (bool selected in new[] { false, true })
        foreach (bool alt in new[] { false, true })
        {
            scene = EditorDocument.Create(16, 16); OpenDocument(scene); await WaitForFramesAsync(); id = scene.Snapshot.Manifest.Layers[0].Id;
            var pixels = scene.Snapshot.Images[id];
            if (selected) scene.SetSelection(selection);
            Require(await AddWorkspaceMaskAsync(alt ? VirtualKeyModifiers.Menu : 0), "The footer modifier route refused its mask."); await WaitForFramesAsync();
            var mask = scene.Snapshot.Masks[id];
            Require(paintOnMask && scene.ActiveSelection is null && ReferenceEquals(pixels, scene.Snapshot.Images[id]), "Mask creation changed image pixels or failed to select its mask.");
            if (!selected) Require(mask.Width == 1 && mask.Height == 1 && mask.Pixels[0] == (alt ? 0 : 255), "A plain footer mask allocated a full plane or ignored Alt.");
            else Require(mask.Width == 16 && mask.Pixels[0] == (alt ? 255 : 0) && mask.Pixels[8 * 16 + 8] == (alt ? 0 : 255), "Footer selection masking ignored its modifier.");
            OnUndo(this, new()); await WaitForFramesAsync(); Require(!scene.CanUndo, "Footer masking created multiple Undo steps.");
        }
        report["footerModifiersChooseUniformOrSelectedMask"] = "passed";

        foreach (bool revealing in new[] { false, true })
        {
            scene = EditorDocument.Create(16, 16); OpenDocument(scene); await WaitForFramesAsync(); id = scene.Snapshot.Manifest.Layers[0].Id;
            scene.SetSelection(selection);
            if (revealing) OnAddMask(this, new()); else OnAddHideAllMask(this, new());
            await WaitForFramesAsync();
            Require(scene.Snapshot.Masks[id].Pixels is [var value] && value == (revealing ? 255 : 0)
                && ReferenceEquals(selection, scene.ActiveSelection) && paintOnMask, "An explicit all-mask command consumed the selection or allocated the full plane.");
            OnUndo(this, new()); await WaitForFramesAsync(); Require(!scene.CanUndo, "An explicit mask command did not undo once.");
        }
        report["explicitMaskCommandsKeepSelectionAndUseUniformPlanes"] = "passed";

        scene = EditorDocument.Create(16, 16); scene.ImportImage(new Surface(8, 8), "Second"); scene = new(scene.Snapshot);
        OpenDocument(scene); await WaitForFramesAsync(); LayerList.SelectAll();
        Require(await WaitUntilAsync(() => selectedLayerIds.Count == 2, 2000), "The mask refusal fixture did not select two layers."); await WaitForFramesAsync();
        Require(!MaskWorkspaceButton.IsEnabled && !await AddWorkspaceMaskAsync(0) && scene.Snapshot.Masks.Count == 0 && !scene.CanUndo, "The footer attached a mask to an ambiguous multi-selection.");
        var target = scene.Snapshot.Manifest.Layers[0].Id; SelectLayerRow(target); RefreshLayerPanel();
        Require(BeginTransformInspector(persistent: true), "The mask refusal fixture did not begin its transform.");
        Require(!MaskWorkspaceButton.IsEnabled && !await AddWorkspaceMaskAsync(0) && !scene.CanUndo, "A transform draft allowed mask creation.");
        QueueTransformCancel(); await WaitForFramesAsync();
        report["multiSelectionAndDraftRefuseMaskCreation"] = "passed";

        scene = EditorDocument.Create(16, 16); id = scene.Snapshot.Manifest.Layers[0].Id;
        scene.Snapshot.Manifest.Layers[0].Transform.Sampling = LayerSampling.Nearest;
        scene.AddRasterMask(id, new(2, 2, [255, 255, 255, 0])); scene.SetMaskEnabled(id, false);
        scene = new(scene.Snapshot); OpenDocument(scene); await WaitForFramesAsync();
        HandleThumbnailClick(id, true, VirtualKeyModifiers.Menu); await WaitForFramesAsync(); RootGrid.UpdateLayout();
        Require(CurrentMaskAloneId == id && MaskAloneBadge.Visibility == Visibility.Visible && MaskAloneBadge.ActualHeight == 26
            && MaskAloneName.Text == SelectedLayer!.Name && !scene.CanUndo && paintOnMask, "Mask-alone view did not show its named badge without history.");
        Require(presented!.GetPixelBytes(12, 12, 1, 1).SequenceEqual(new byte[] { 0, 0, 0, 255 }), "The mask-alone fixture did not show grayscale coverage.");
        scene.RenameLayer(id, "Badge name"); await WaitForFramesAsync(); Require(MaskAloneName.Text == "Badge name", "The badge retained the old layer name.");
        OnUndo(this, new()); await WaitForFramesAsync(); Require(!scene.CanUndo, "Badge naming interfered with document Undo.");
        Invoke(MaskAloneCloseButton); await WaitForFramesAsync();
        Require(CurrentMaskAloneId is null && MaskAloneBadge.Visibility == Visibility.Collapsed && paintOnMask && !scene.CanUndo && pointerStroke is null,
            "Returning to the composite changed the paint target/history or started painting.");
        AssertUploadedFrame(scene);
        report["maskAloneBadgeNamesLayerAndReturnsWithoutHistory"] = "passed";

        HandleThumbnailClick(id, true, VirtualKeyModifiers.Menu); await WaitForFramesAsync();
        scene.RemoveMask(id); await WaitForFramesAsync();
        Require(MaskAloneBadge.Visibility == Visibility.Collapsed && CurrentMaskAloneId is null, "Deleting the viewed mask retained its badge.");
        scene = EditorDocument.Create(16, 16); id = scene.Snapshot.Manifest.Layers[0].Id;
        scene.AddRasterMask(id, MaskSurface.Solid(revealing: false)); OpenDocument(scene); await WaitForFramesAsync();
        HandleThumbnailClick(id, true, VirtualKeyModifiers.Menu); await WaitForFramesAsync();
        var replacement = EditorDocument.Create(8, 8); OpenDocument(replacement); await WaitForFramesAsync();
        Require(MaskAloneBadge.Visibility == Visibility.Collapsed && MaskAloneName.Text.Length == 0 && !replacement.CanUndo,
            "Document replacement retained the old mask badge.");
        report["maskDeletionAndReplacementRetireBadge"] = "passed";
        await VerifyMaskPaintTargetsAsync(report);
    }
}
#endif
