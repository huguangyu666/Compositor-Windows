#if DEBUG
using Compositor.Core;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Windows.System;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task VerifyLayerEffectRowsAsync(Dictionary<string, object> report)
    {
        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
        static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
        var scene = EditorDocument.Create(24, 24); var id = scene.Snapshot.Manifest.Layers[0].Id;
        scene.SetLayerEffects(id, new() { Stroke = new(), Shadow = new() });
        scene = new(scene.Snapshot); OpenDocument(scene); await WaitForFramesAsync(); RootGrid.UpdateLayout();
        var effectRows = FilterFinishDescendants(layerRows[id]).OfType<Grid>()
            .Where(grid => grid.Name.StartsWith("LayerEffect_", StringComparison.Ordinal)).ToArray();
        if (effectRows.Length != 2) throw new InvalidOperationException("The layer list does not show the layer's two effect rows.");
        report["layerListShowsPresentEffects"] = "passed";

        var row = (LayerRow)layerRows[id].Tag;
        var strokeRow = row.EffectRows[LayerEffectKind.Stroke].Root;
        var original = scene.Snapshot.Images[id]; var warm = compositor; int frames = presentedFrameCount;
        Invoke(row.EffectRows[LayerEffectKind.Shadow].Label);
        Require(await WaitUntilAsync(() => SelectedEffect == (id, LayerEffectKind.Shadow), 2000), "The row button did not select its effect.");
        await WaitForFramesAsync(); RefreshLayerPanel();
        Require(LayerList.SelectedItems.Count == 0 && selectedLayerIds.SetEquals([id]) && !paintOnMask
            && !scene.CanUndo && ReferenceEquals(warm, compositor) && frames == presentedFrameCount,
            "Effect selection changed history/rendering or retained a layer/mask highlight.");
        Require(ReferenceEquals(strokeRow, row.EffectRows[LayerEffectKind.Stroke].Root), "Effect controls were rebuilt on selection.");
        report["effectButtonSelectsWithoutRenderingOrHistory"] = "passed";

        ((IToggleProvider)new CheckBoxAutomationPeer(row.EffectRows[LayerEffectKind.Shadow].Eye).GetPattern(PatternInterface.Toggle)).Toggle();
        Require(await WaitUntilAsync(() => LayerEffects.Parse(scene.Snapshot.Manifest.Layers[0].Effects)?.Shadow?.Enabled == false, 2000),
            "The effect eye did not hide its effect."); await WaitForFramesAsync();
        Require(LayerEffects.Parse(scene.Snapshot.Manifest.Layers[0].Effects) is { Shadow: { Enabled: false } }
            && SelectedEffect == (id, LayerEffectKind.Shadow) && ReferenceEquals(original, scene.Snapshot.Images[id]),
            "The eye removed parameters, changed pixels or lost effect selection.");
        AssertUploadedFrame(scene); OnUndo(this, new()); await WaitForFramesAsync();
        Require(!scene.CanUndo && row.EffectRows[LayerEffectKind.Shadow].Eye.IsChecked == true, "Effect visibility did not undo once.");
        report["effectEyeKeepsSettingsAndUndoesOnce"] = "passed";

        var selection = Selection.Rectangle(24, 24, 2, 2, 6, 6); scene.SetSelection(selection);
        var delete = TryDeleteKeyAsync(new(VirtualKey.Delete, 0), LayerList);
        Require(delete is not null && await delete, "Delete did not take the selected effect."); await WaitForFramesAsync();
        Require(scene.LayerCount == 1 && LayerEffects.Parse(scene.Snapshot.Manifest.Layers[0].Effects) is { Stroke: not null, Shadow: null }
            && ReferenceEquals(original, scene.Snapshot.Images[id]) && ReferenceEquals(selection, scene.ActiveSelection)
            && SelectedEffect is null && !row.EffectRows.ContainsKey(LayerEffectKind.Shadow), "Delete edited the layer/selection or left a stale effect row.");
        OnUndo(this, new()); await WaitForFramesAsync();
        Require(!scene.CanUndo && row.EffectRows.Count == 2 && SelectedEffect is null, "Effect deletion did not undo once.");
        report["effectDeletePrecedesSelectedPixelsAndRestoresOnce"] = "passed";

        Require(await SelectLayerEffectAsync(id, LayerEffectKind.Stroke, editing: true), "Double-click action did not open the effect editor.");
        RootGrid.UpdateLayout();
        Require(floatingPanels.TryGetValue("effects", out var panel) && FilterFinishDescendants(panel.Root).OfType<ComboBox>().Single(combo => combo.Name == "LayerEffectKind").SelectedIndex == 0,
            "The editor did not open on the selected kind.");
        Require(await SelectLayerEffectAsync(id, LayerEffectKind.Shadow, editing: true), "Switching the effect editor failed.");
        RootGrid.UpdateLayout();
        Require(FilterFinishDescendants(panel!.Root).OfType<ComboBox>().Single(combo => combo.Name == "LayerEffectKind").SelectedIndex == 1, "An open editor did not follow the double-clicked kind.");
        Require(await DeleteLayerOrMaskAsync(), "Trash did not remove the selected effect while its panel was open."); await WaitForFramesAsync();
        Require(!floatingPanels.ContainsKey("effects") && LayerEffects.Parse(scene.Snapshot.Manifest.Layers[0].Effects)?.Shadow is null
            && ReferenceEquals(original, scene.Snapshot.Images[id]), "The open editor restored the removed effect or trash deleted pixels.");
        OnUndo(this, new()); await WaitForFramesAsync(); Require(!scene.CanUndo, "Trash added unexpected history.");
        report["effectEditorTargetsKindAndTrashRetiresIt"] = "passed";

        Require(await SelectLayerEffectAsync(id, LayerEffectKind.Stroke), "Effect reselection failed.");
        HandleThumbnailClick(id, false, 0); await WaitForFramesAsync();
        Require(SelectedEffect is null && LayerList.SelectedItems.Count == 1, "Thumbnail targeting retained the effect selection.");
        var sourceMask = scene.ImportImage(new Surface(4, 4), "Mask source").Id;
        scene.AddRasterMask(sourceMask, MaskSurface.Solid(revealing: true)); await WaitForFramesAsync();
        Require(await SelectLayerEffectAsync(id, LayerEffectKind.Stroke), "Effect selection before mask copy failed.");
        CopyMaskToRow(sourceMask, id); await WaitForFramesAsync();
        Require(SelectedEffect is null && paintOnMask && SelectedLayer?.Id == id && scene.Snapshot.Masks.ContainsKey(id),
            "Mask copy retained an effect target instead of targeting the copied mask.");
        Require(await SelectLayerEffectAsync(id, LayerEffectKind.Stroke), "Effect reselection before replacement failed.");
        OpenDocument(EditorDocument.Create(8, 8)); await WaitForFramesAsync();
        Require(SelectedEffect is null && layerRows.Values.All(item => ((LayerRow)item.Tag).EffectRows.Count == 0), "Replacement retained old effect selection or controls.");
        report["thumbnailAndReplacementReleaseEffectSelection"] = "passed";
    }
}
#endif
