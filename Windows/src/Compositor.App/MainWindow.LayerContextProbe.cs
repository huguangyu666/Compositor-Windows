#if DEBUG
using Compositor.Core;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task VerifyLayerContextAsync(Dictionary<string, object> report)
    {
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        static IEnumerable<MenuFlyoutItem> Items(IEnumerable<MenuFlyoutItemBase> items)
        {
            foreach (var item in items)
            {
                if (item is MenuFlyoutItem command) yield return command;
                if (item is MenuFlyoutSubItem sub)
                    foreach (var child in Items(sub.Items)) yield return child;
            }
        }
        async Task Invoke(MenuFlyout menu, string name, Func<bool> completed)
        {
            menu.ShowAt(LayerList); RootGrid.UpdateLayout();
            try
            {
                var item = Items(menu.Items).Single(item => item.Name == name);
                Require(item.IsEnabled, $"The context action {name} was disabled.");
                ((IInvokeProvider)new MenuFlyoutItemAutomationPeer(item).GetPattern(PatternInterface.Invoke)).Invoke();
                Require(await WaitUntilAsync(() => completed() && !IsDeletePending, 2500), $"The context action {name} did not complete.");
            }
            finally { menu.Hide(); }
            await WaitForFramesAsync();
        }
        var scene = EditorDocument.Create(32, 24);
        var id = scene.Snapshot.Manifest.Layers[0].Id;
        scene.AddRasterMask(id, MaskSurface.Solid(revealing: true)); scene.SetLayerEffects(id, new() { Stroke = new() });
        scene.ImportImage(new Surface(8, 8), "Second"); var top = scene.ImportImage(new Surface(8, 8), "Top").Id;
        scene = new(scene.Snapshot); OpenDocument(scene); await WaitForFramesAsync();
        if (layerRows[id].ContextFlyout is not MenuFlyout)
            throw new InvalidOperationException("Layer rows have no context-menu entry point.");
        report["layerRowsExposeContextMenu"] = "passed";

        LayerList.SelectAll(); Require(await WaitUntilAsync(() => selectedLayerIds.Count == 3, 2000), "Context fixture could not select all rows.");
        await WaitForFramesAsync(); var warm = compositor; int frames = presentedFrameCount;
        var menu = (await PrepareLayerContextAsync(id))!;
        Require(selectedLayerIds.Count == 3 && LayerList.SelectedItems.Count == 3 && selectedLayerId == id && !scene.CanUndo
            && Items(menu.Items).Single(item => item.Name == "LayerContextRename").IsEnabled == false
            && ReferenceEquals(warm, compositor) && presentedFrameCount == frames, "Row context lost multi-selection, rebuilt rendering or enabled multi-rename.");
        await Invoke(menu, "LayerContextDuplicate", () => scene.LayerCount == 6);
        OnUndo(this, new()); await WaitForFramesAsync(); Require(scene.LayerCount == 3 && !scene.CanUndo, "Context duplicate was not one Undo.");
        report["rowContextPreservesMultiSelectionAndCopiesOnce"] = "passed";

        SelectLayerRow(top); RefreshLayerPanel(); menu = (await PrepareLayerContextAsync(id, mask: true))!;
        var pixels = scene.Snapshot.Images[id];
        Require(paintOnMask && selectedLayerId == id && selectedLayerIds.SetEquals([id]) && LayerList.SelectedItems.Count == 1
            && Items(menu.Items).Single(item => item.Name == "LayerContextDelete").Text == Loc.T("M_ContextDeleteMask"), "Mask context did not target its thumbnail.");
        await Invoke(menu, "LayerContextDelete", () => !scene.Snapshot.Masks.ContainsKey(id));
        Require(scene.LayerCount == 3 && ReferenceEquals(pixels, scene.Snapshot.Images[id]), "Mask context deletion removed its layer or pixels.");
        OnUndo(this, new()); await WaitForFramesAsync(); Require(!scene.CanUndo, "Context mask deletion was not one Undo.");
        menu = (await PrepareLayerContextAsync(id, effect: LayerEffectKind.Stroke))!;
        Require(SelectedEffect == (id, LayerEffectKind.Stroke) && LayerList.SelectedItems.Count == 0, "Effect context did not select its effect.");
        await Invoke(menu, "LayerContextDelete", () => LayerEffects.Parse(scene.Snapshot.Manifest.Layers.Single(layer => layer.Id == id).Effects)?.Stroke is null);
        Require(scene.LayerCount == 3 && scene.Snapshot.Masks.ContainsKey(id) && ReferenceEquals(pixels, scene.Snapshot.Images[id]), "Effect context deletion changed its layer or mask.");
        OnUndo(this, new()); await WaitForFramesAsync(); Require(!scene.CanUndo, "Context effect deletion was not one Undo.");
        report["thumbnailAndEffectContextDeleteTheirOwnTargets"] = "passed";

        menu = (await PrepareLayerContextAsync(top, mask: false))!;
        await Invoke(menu, "LayerContextHideMask", () => scene.Snapshot.Masks.ContainsKey(top));
        Require(scene.Snapshot.Masks[top].Pixels is [0] && paintOnMask && selectedLayerId == top, "Context hide-all mask was not uniform or targeted.");
        OnUndo(this, new()); await WaitForFramesAsync();
        var selection = Selection.Rectangle(32, 24, 2, 2, 8, 8); scene.SetSelection(selection);
        menu = (await PrepareLayerContextAsync(top, mask: false))!;
        await Invoke(menu, "LayerContextRevealMask", () => scene.Snapshot.Masks.ContainsKey(top));
        Require(scene.ActiveSelection is null && paintOnMask, "Selection-mask creation did not consume the selection or target its mask.");
        OnUndo(this, new()); await WaitForFramesAsync(); Require(!scene.CanUndo && ReferenceEquals(selection, scene.ActiveSelection), "Selection-mask Undo did not restore the selection once.");
        scene.SetSelection(null);
        report["contextMaskCreationUsesSelectionAndOneUndo"] = "passed";

        menu = (await PrepareLayerContextAsync(id, mask: false))!;
        var stale = menu;
        var replacement = new EditorDocument(scene.Snapshot); OpenDocument(replacement); await WaitForFramesAsync();
        stale.ShowAt(LayerList); RootGrid.UpdateLayout();
        ((IInvokeProvider)new MenuFlyoutItemAutomationPeer(Items(stale.Items).Single(item => item.Name == "LayerContextDuplicate")).GetPattern(PatternInterface.Invoke)).Invoke();
        stale.Hide(); await WaitForFramesAsync();
        Require(replacement.LayerCount == 3 && !replacement.CanUndo && !scene.CanUndo, "A stale context menu edited a replacement with matching layer IDs.");
        report["oldContextMenuCannotEditReplacementDocument"] = "passed";

        var text = EditorDocument.Create(320, 240); var textId = text.AddTextLayer(new() { Content = "Thumbnail text", FontName = "Segoe UI", FontSize = 24 }, new(20, 50))!.Id;
        text.AddAdjustment(AdjustmentKind.Exposure); var adjustmentId = text.Snapshot.Manifest.ActiveLayerId!.Value;
        text = new(text.Snapshot); OpenDocument(text); await WaitForFramesAsync();
        Require(await OpenThumbnailContentAsync(textId) && inlineEditor is not null && text.ActiveTextDraft?.LayerId == textId,
            "Text thumbnail action did not open existing inline text.");
        var textDraft = text.ActiveTextDraft;
        Require(await OpenThumbnailContentAsync(textId) && ReferenceEquals(textDraft, text.ActiveTextDraft), "A repeated thumbnail action committed/reopened the text draft.");
        menu = (await PrepareLayerContextAsync(textId))!;
        Require(Items(menu.Items).All(item => !item.IsEnabled), "Context commands were enabled while text was being edited.");
        CloseInlineText(cancelling: true); await WaitForFramesAsync(); Require(!text.CanUndo, "Opening existing text changed document history.");
        Require(await OpenThumbnailContentAsync(adjustmentId) && selectedLayerId == adjustmentId && AdjustmentPanel.Visibility == Visibility.Visible
            && !text.CanUndo, "Adjustment thumbnail action did not expose its settings.");
        report["thumbnailContentActionOpensTextAndAdjustmentEditors"] = "passed";

        // Layer > "Edit Adjustment…" (CompositorApp.swift:295-297): the Mac's menu door onto the SAME
        // engine the row's double-tap already opens. The group pins what is specific to the door: its
        // place in the Layer menu, the offered state following the picked layer, refusing instead of
        // falling through to the double-tap's rename answer, and the editor opening without history.
        {
            var menuDoc = EditorDocument.Create(32, 24);
            menuDoc.AddAdjustment(AdjustmentKind.Exposure);
            var adjLayerId = menuDoc.Snapshot.Manifest.ActiveLayerId!.Value;
            menuDoc = new EditorDocument(menuDoc.Snapshot);
            OpenDocument(menuDoc); await WaitForFramesAsync();
            int indexOf(Guid layerId) => layerItems.ToList().FindIndex(grid => ((LayerRow)grid.Tag).Layer.Id == layerId);
            void PickRow(Guid layerId)
            {
                int index = indexOf(layerId);
                LayerList.DeselectRange(new ItemIndexRange(0, (uint)layerItems.Count()));
                LayerList.SelectRange(new ItemIndexRange(index, 1));
            }
            var canvasId = menuDoc.Snapshot.Manifest.Layers.First(layer => layer.Adjustment is null).Id;

            // Position: directly under the Add-Adjustment submenu, above Layer Effects (the Mac's
            // order). Keyed on the NAMED neighbours, never on labels: this box renders zh-CN, so a
            // Text match would silently hunt for the wrong words. Expected run: Rename, Add-Adjustment
            // submenu, Edit Adjustment, Layer Effects, separator, Add Reveal Mask.
            var layerMenu = LayerMenu.Items;
            int renameAt = layerMenu.ToList().IndexOf(RenameMenuItem);
            Require(renameAt >= 0 && renameAt + 5 < layerMenu.Count
                    && layerMenu[renameAt + 1] is MenuFlyoutSubItem
                    && ReferenceEquals(layerMenu[renameAt + 2], EditAdjustmentMenuItem)
                    && layerMenu[renameAt + 3] is MenuFlyoutItem
                    && layerMenu[renameAt + 4] is MenuFlyoutSeparator
                    && ReferenceEquals(layerMenu[renameAt + 5], AddMaskMenuItem),
                $"Edit Adjustment is not between the Add-Adjustment submenu and Layer Effects "
                + $"(rename=#{renameAt}; the run after it is "
                + string.Join(", ", layerMenu.Skip(renameAt + 1).Take(5).Select(entry => entry.GetType().Name)) + ").");

            PickRow(adjLayerId);
            Require(await WaitUntilAsync(() => selectedLayerId == adjLayerId && EditAdjustmentMenuItem.IsEnabled, 2500),
                "picking the adjustment layer never offered the menu door.");
            OnEditAdjustment(this, new RoutedEventArgs());
            Require(await WaitUntilAsync(() => AdjustmentPanel.Visibility == Visibility.Visible, 2500),
                "the menu door never opened the adjustment panel.");
            Require(!menuDoc.CanUndo, "the menu door wrote history for a pure peek at settings.");

            PickRow(canvasId);
            Require(await WaitUntilAsync(() => selectedLayerId == canvasId && !EditAdjustmentMenuItem.IsEnabled, 2500),
                "the plain canvas layer still offered the menu door.");
            OnEditAdjustment(this, new RoutedEventArgs());   // the Mac's disabled item: nothing to open
            await WaitForFramesAsync();
            Require(!renamingLayer && !menuDoc.CanUndo,
                "the refused menu door fell through to the double-tap's rename answer (the Mac's disabled item opens nothing).");

            // The Mac keys the item on the ACTIVE layer's adjustment, so the single picks above are the
            // whole rule; a native two-row SelectRange has no deterministic primary to assert against
            // and would test the list, not the door. Leave the scene clean for the drafts fixture.
            OpenDocument(EditorDocument.Create(32, 24)); await WaitForFramesAsync();
        }
        report["theLayerMenuOpensTheAdjustmentEditorThroughTheSameDoor"] = "passed";

        OpenDocument(EditorDocument.Create(32, 24)); await WaitForFramesAsync();
        id = document!.Snapshot.Manifest.Layers[0].Id;
        Require(BeginTransformInspector(persistent: true), "Context draft fixture could not begin.");
        menu = (await PrepareLayerContextAsync(id))!;
        Require(transformInspectorEdit is not null && Items(menu.Items).All(item => !item.IsEnabled), "Opening same-row context committed a draft or enabled content actions.");
        QueueTransformCancel(); await WaitForFramesAsync();
        report["sameRowContextPreservesDraftAndDisablesCommands"] = "passed";
    }
}
#endif
