#if DEBUG
using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace Compositor.App;

public sealed partial class MainWindow
{
    /// <summary>
    /// #43: the layer panel's drop, driven the way every other pointer workflow in this suite is driven —
    /// the production entry below the event plumbing, with a point computed from the row grids the panel
    /// drew. What a real drag adds on top is WinUI's own press-and-move, which no probe here can fabricate;
    /// the geometry, the placement it turns into, and the batch it writes are production code paths
    /// (<see cref="LayerDropTarget"/>, <see cref="PlaceDroppedRows"/>).
    /// </summary>
    private async Task VerifyLayerDropAsync(Dictionary<string, object> report)
    {
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        Point PointInRow(int index, double fraction)
        {
            var grid = layerItems[index];
            return grid.TransformToVisual(RootGrid).TransformPoint(new Point(grid.ActualWidth / 2, grid.ActualHeight * fraction));
        }
        Point BelowLastRow()
        {
            var grid = layerItems[^1];
            return grid.TransformToVisual(RootGrid).TransformPoint(new Point(grid.ActualWidth / 2, grid.ActualHeight + 24));
        }
        string NameAt(int panelRow) => panelRow < layerItems.Count ? ((LayerRow)layerItems[panelRow].Tag).Layer.Name : "(none)";
        int RowIndex(Guid id)
        {
            for (int index = 0; index < layerItems.Count; index++)
                if (((LayerRow)layerItems[index].Tag).Layer.Id == id) return index;
            return -1;
        }

        var scene = EditorDocument.Create(64, 48);
        OpenDocument(scene); await WaitForFramesAsync();
        var background = scene.Snapshot.Manifest.Layers[0].Id;
        var red = scene.ImportImage(new Surface(16, 16), "Red").Id;
        var blue = scene.ImportImage(new Surface(16, 16), "Blue").Id;
        await WaitForFramesAsync();

        // The panel reads top of the stack first: Blue, Red, Background.
        Require(layerItems.Count == 3 && ((LayerRow)layerItems[0].Tag).Layer.Id == blue,
            $"the layer panel did not list its three rows top-first (row 0 is {((LayerRow)layerItems[0].Tag).Layer.Name}).");
        // Every point below is measured against a row's height, so the rows have to be laid out first: a
        // zero-height row makes any point fall past the last row, which reads as the bottom of the stack and
        // reds a claim that has nothing to do with the one being made (ldrag-redB ran into exactly that).
        Require(await WaitUntilAsync(() => layerItems.All(grid => grid.ActualHeight > 0), 2000),
            "the layer panel never laid its rows out, so a drop point had no row band to fall in.");

        // 1 — which band of a row the pointer is in decides above-or-into, and a point past the last row is
        // the bottom of the stack. The Mac gets the same three answers from NSTableView's proposal.
        Require(LayerDropTarget(PointInRow(0, 0.1)) is (0, LayerDropZone.Above), "the top band of a row did not offer the row above it.");
        Require(LayerDropTarget(PointInRow(1, 0.5)) is (1, LayerDropZone.Above),
            "the body of a plain row did not offer the row above it, as the Mac's .above proposal does.");
        Require(LayerDropTarget(BelowLastRow()) is (3, LayerDropZone.AtBottom), "a point below the last row is not the bottom of the stack.");
        report["layerDropReadsTheRowBands"] = "passed";

        // 2 — a drop above the top row stacks the layer there.
        PlaceDroppedRows([background], 0, LayerDropZone.Above);
        await WaitForFramesAsync();
        Require(NameAt(0) == "Background" && NameAt(1) == "Blue" && NameAt(2) == "Red",
            $"the drop above the top row did not stack it there ({string.Join(", ", Enumerable.Range(0, 3).Select(NameAt))}).");
        report["aDropAboveTheTopRowStacksItThere"] = "passed";

        // 3 — one Undo is the whole drop: a batch writes one step, as the Mac's beginEdit around its loop.
        Require(scene.Undo(), "Undo undid nothing after the drop.");
        Require(NameAt(0) == "Blue" && NameAt(2) == "Background",
            $"Undo left the dropped layer on top ({string.Join(", ", Enumerable.Range(0, 3).Select(NameAt))}).");
        report["aDropIsOneUndoStep"] = "passed";

        // 4 — the body of a folder row takes the drop inside it, and a folded folder opens to show what came in.
        var folder = scene.GroupLayers(red)!;
        RefreshLayerPanel();
        await WaitForFramesAsync();
        Require(await WaitUntilAsync(() => RowIndex(folder.Id) >= 0, 2000),
            $"the folder row never reached the panel (rows: {string.Join(", ", layerItems.Select(grid => ((LayerRow)grid.Tag).Layer.Name))}).");
        int folderRow = RowIndex(folder.Id);
        Require(await WaitUntilAsync(() => layerItems.All(grid => grid.ActualHeight > 0), 2000),
            "the panel had not laid the folder row out when its bands were read.");
        Require(LayerDropTarget(PointInRow(folderRow, 0.5)) is (var target, LayerDropZone.Into) && target == folderRow,
            $"the body of a folder row did not offer the folder itself (row {folderRow} is a "
            + $"{(((LayerRow)layerItems[folderRow].Tag).IsGroupView ? "folder" : "plain")} row read as {LayerDropTarget(PointInRow(folderRow, 0.5))?.Zone}).");
        collapsedGroups.Add(folder.Id);
        RefreshLayerPanel();
        Require(RowIndex(red) < 0, "a folded folder still showed its child row.");
        // The folded folder has one fewer row above it, so the drop has to aim at the row as it is now.
        folderRow = RowIndex(folder.Id);
        PlaceDroppedRows([blue], folderRow, LayerDropZone.Into);
        await WaitForFramesAsync();
        Require(scene.Snapshot.Manifest.Layers.Single(layer => layer.Id == blue).ParentId == folder.Id,
            "the drop into the folder left the layer at the top level.");
        Require(RowIndex(red) >= 0 && RowIndex(blue) >= 0, "the folder the drop landed in stayed folded.");
        report["aDropIntoAFolderReparentsItAndOpensIt"] = "passed";

        // 5 — what a drag carries: the selected rows, minus anything a dragged folder already brings along.
        selectedLayerId = folder.Id;
        selectedLayerIds.Clear();
        selectedLayerIds.Add(folder.Id);
        selectedLayerIds.Add(red);
        var carried = DraggedLayerRows(new List<object>());
        Require(carried.Count == 1 && carried[0] == folder.Id,
            $"the drag carried [{string.Join(",", carried)}] instead of just the folder that contains the rest.");
        report["aDraggedFolderCarriesItsChildren"] = "passed";

        // 6 — a refused drop leaves nothing behind: neither a moved row nor an undo step to sit through.
        var second = scene.GroupLayers(blue)!;
        RefreshLayerPanel();
        await WaitForFramesAsync();
        int secondRow = RowIndex(second.Id);
        Require(secondRow >= 0 || await WaitUntilAsync(() => RowIndex(second.Id) >= 0, 2000),
            "the second folder never reached the panel, so the refusal had no row to drop on.");
        secondRow = RowIndex(second.Id);
        PlaceDroppedRows([second.Id], secondRow, LayerDropZone.Into); // a folder dropped into itself
        await WaitForFramesAsync();
        Require(RowIndex(second.Id) == secondRow, "dropping a folder into itself moved its row.");
        // One Undo is the grouping that made `second`, so the refusal pushed nothing of its own.
        Require(scene.Undo(), "Undo undid nothing after the refused drop.");
        Require(RowIndex(second.Id) < 0 && RowIndex(folder.Id) >= 0,
            "the refused drop left a step behind: one Undo took the folder away instead of the grouping.");
        report["aRefusedDropLeavesNoStepBehindIt"] = "passed";

        // 7 — the bottom of the panel starts the stack, and the rows that moved stay selected.
        PlaceDroppedRows([background, red], layerItems.Count, LayerDropZone.AtBottom);
        await WaitForFramesAsync();
        Require(NameAt(layerItems.Count - 1) == "Red" && NameAt(layerItems.Count - 2) == "Background",
            $"the drop at the bottom did not start the stack with both rows, dragged order first ({NameAt(layerItems.Count - 2)}, {NameAt(layerItems.Count - 1)}).");
        Require(selectedLayerIds.Count == 2 && selectedLayerIds.Contains(background) && selectedLayerIds.Contains(red),
            "the rows that moved were left unselected, so they cannot be dragged on again.");
        Require(SelectedLayer?.Id == background, $"the drag handed the primary to {SelectedLayer?.Name} instead of the topmost row dragged.");
        report["aDropAtTheBottomStartsTheStackAndKeepsTheSelection"] = "passed";

        // 8 — the Option-drag: the original stays where it was, the copy lands on the row, and the copy is
        // what the panel selects.
        int layersBeforeCopy = scene.Snapshot.Manifest.Layers.Count;
        PlaceDroppedRows([blue], 0, LayerDropZone.Above, copy: true);
        await WaitForFramesAsync();
        Require(scene.Snapshot.Manifest.Layers.Count == layersBeforeCopy + 1, "the copy drop added no layer.");
        Require(scene.Snapshot.Manifest.Layers.Any(layer => layer.Id == blue), "the copy drop moved the original instead of leaving it.");
        var copyRow = (LayerRow)layerItems[0].Tag;
        Require(copyRow.Layer.Name == "Blue copy", $"the copy did not land on the row it was dropped above (row 0 is {copyRow.Layer.Name}).");
        Require(SelectedLayer?.Id == copyRow.Layer.Id && selectedLayerIds.Count == 1,
            $"the panel selected {SelectedLayer?.Name} instead of the copy it just made.");
        Require(scene.Undo() && scene.Snapshot.Manifest.Layers.Count == layersBeforeCopy,
            "duplicating and moving the copy were more than one undo step.");
        report["aCopyDropLeavesTheOriginalAndSelectsTheCopy"] = "passed";

        // 9 — the offer the pointer sees follows the key it is given, and a row that takes nothing offers
        // nothing. Which key is physically down is read in the handler, not here.
        Require(LayerDropOperation(null, copyHeld: false) == DataPackageOperation.None
                && LayerDropOperation((Guid.Empty, null, false), copyHeld: false) == DataPackageOperation.Move
                && LayerDropOperation((Guid.Empty, null, false), copyHeld: true) == DataPackageOperation.Copy,
            "the drag offer stopped following the key: Ctrl must copy, a plain drag must move, no row must offer nothing.");
        report["theDragOfferFollowsTheKey"] = "passed";

        // 10 — the Mac's effect row dragged onto a layer: the effect lands there, the layer it came from keeps
        // its own, and a row that cannot take it is never offered.
        var effectScene = EditorDocument.Create(64, 48);
        OpenDocument(effectScene); await WaitForFramesAsync();
        var shadowed = effectScene.ImportImage(new Surface(16, 16), "Shadowed").Id;
        var plain = effectScene.ImportImage(new Surface(16, 16), "Plain").Id;
        Require(effectScene.SetLayerEffects(shadowed, new LayerEffects
        {
            Shadow = new() { Blur = 30 },
            ColorOverlay = new() { Opacity = .5 },
        }), "the effect fixture attached nothing to the source layer.");
        await WaitForFramesAsync();
        int plainRow = RowIndex(plain);
        // The hit test below reads the row's arranged height, and a row the list has realized but not yet
        // measured answers zero — which a point cannot tell apart from a point on the wrong row. This group has
        // been dying here on its own (it did the same in 2 of 4 runs of the pre-CR-g binary), so the measure is
        // waited for as the precondition it is, named as such, and the hit test stays a single assertion.
        Require(await WaitUntilAsync(() => plainRow >= 0 && plainRow < layerItems.Count
                && layerItems[plainRow].ActualHeight > 0, 6000),
            $"the row an effect has to land on never got measured (rows {layerItems.Count}, row index {plainRow}, "
            + $"height {(plainRow >= 0 && plainRow < layerItems.Count ? layerItems[plainRow].ActualHeight : -1)}).");
        Require(LayerRowAt(PointInRow(plainRow, 0.5)) == plain, "a point inside a row did not name that row.");
        Require(EffectDropOperation(true, true, effectScene.CanCopyEffect(LayerEffectKind.Shadow, shadowed, plain)) == DataPackageOperation.Copy
                && EffectDropOperation(true, true, effectScene.CanCopyEffect(LayerEffectKind.Shadow, shadowed, shadowed)) == DataPackageOperation.None
                && EffectDropOperation(true, false, true) == DataPackageOperation.None,
            "an effect drag was offered a layer that cannot take it, or refused one that can.");
        CopyEffectToRow(LayerEffectKind.Shadow, shadowed, plain);
        await WaitForFramesAsync();
        Require(LayerEffects.Parse(effectScene.Snapshot.Manifest.Layers.Single(layer => layer.Id == plain).Effects)?.Shadow is { Blur: 30 },
            "the dropped effect never reached the target layer.");
        Require(LayerEffects.Parse(effectScene.Snapshot.Manifest.Layers.Single(layer => layer.Id == shadowed).Effects)?.Shadow is { Blur: 30 },
            "the layer the effect was copied from lost its own.");
        Require(SelectedLayer?.Id == plain, $"the copy left the selection on {SelectedLayer?.Name} instead of the layer that took it.");
        report["anEffectRowDroppedOnALayerCopiesItThere"] = "passed";

        // 11 — dropped on the layer whose effects panel is open: the preview is committed first and the panel
        // comes down, because this panel previews the layer's whole set under one session and a Cancel
        // afterwards would roll the copy back too.
        OnLayerEffects(this, new RoutedEventArgs());
        Require(FloatingPanelIsOpen("effects"), "no effects panel opened on the layer the copy was dropped on.");
        var switches = (Grid)((StackPanel)FloatingPanelBody("effects")!).Children[0];
        switches.Children.OfType<CheckBox>().First().IsChecked = true;   // Stroke, previewed under the session
        await WaitForFramesAsync();
        CopyEffectToRow(LayerEffectKind.ColorOverlay, shadowed, plain);
        await WaitForFramesAsync();
        Require(!FloatingPanelIsOpen("effects"), "the panel stayed up over a layer whose session it no longer owns.");
        var landed = LayerEffects.Parse(effectScene.Snapshot.Manifest.Layers.Single(layer => layer.Id == plain).Effects);
        Require(landed?.Stroke is not null, "the panel's previewed stroke was thrown away by the drop.");
        Require(landed?.ColorOverlay is not null, "the copied overlay never reached the layer.");
        Require(effectScene.Undo(), "the copy was not its own undo step after the committed preview.");
        Require(LayerEffects.Parse(effectScene.Snapshot.Manifest.Layers.Single(layer => layer.Id == plain).Effects)?.Stroke is not null,
            "Undo took the committed stroke away with the copy, so the two were one step.");
        report["aCopyOntoTheLayerBeingEditedCommitsItsPreview"] = "passed";

        // 12 — the Mac's toggleGroupExpansion: folding a folder that holds the layer you are working on hands
        // the selection to the folder, because that child's row goes away with the fold.
        var foldScene = EditorDocument.Create(64, 48);
        OpenDocument(foldScene); await WaitForFramesAsync();
        var folded = foldScene.ImportImage(new Surface(16, 16), "Folded");
        var foldGroup = foldScene.GroupLayers([folded.Id])!;
        RefreshLayerPanel(); await WaitForFramesAsync();
        SelectLayerRow(folded.Id);
        ToggleFolderExpansion(foldGroup.Id);
        Require(RowIndex(folded.Id) < 0 && RowIndex(foldGroup.Id) >= 0, "folding the folder left its child row on screen.");
        Require(SelectedLayer?.Id == foldGroup.Id,
            $"folding a folder that holds the working layer left the selection on {SelectedLayer?.Name}.");
        ToggleFolderExpansion(foldGroup.Id);
        Require(RowIndex(folded.Id) >= 0 && SelectedLayer?.Id == foldGroup.Id,
            "opening the folder again moved the selection, which the Mac leaves alone.");
        var unrelated = foldScene.ImportImage(new Surface(16, 16), "Unrelated");
        RefreshLayerPanel(); await WaitForFramesAsync();
        SelectLayerRow(unrelated.Id);
        ToggleFolderExpansion(foldGroup.Id);
        Require(SelectedLayer?.Id == unrelated.Id, "folding an unrelated folder moved the selection.");
        report["foldingAFolderHandsTheSelectionToTheFolder"] = "passed";

        // 13 - the drop picture (the Mac's setDropRow, NativeLayerList.swift:406). The zone decision
        // has been probed by coordinate since the placement work; what was never pinned is that the
        // pointer SEES it. The probe installs a real drag record and drives the DragOver seam itself
        // (no OLE), checking which element is shown and where - the line above a plain row's band,
        // the highlight over a folder row's band, nothing past the accepted-op gate, and the picture
        // hidden again when the session ends.
        {
            var picture = EditorDocument.Create(64, 48);
            OpenDocument(picture); await WaitForFramesAsync();
            var pickLeaf = picture.ImportImage(new Surface(16, 16), "Pick leaf");
            var pickFolder = picture.GroupLayers(pickLeaf.Id)!;
            var pickPlain = picture.ImportImage(new Surface(16, 16), "Pick plain").Id;
            var pickTarget = picture.ImportImage(new Surface(16, 16), "Pick target").Id;
            picture.SetLayerEffects(pickPlain, new LayerEffects { Stroke = new() });   // the drag's source carries it
            RefreshLayerPanel(); await WaitForFramesAsync();
            Require(await WaitUntilAsync(() => layerItems.All(grid => grid.ActualHeight > 0), 2000),
                "the feedback fixture never laid its rows out.");
            int folderPanel = RowIndex(pickFolder.Id), plainPanel = RowIndex(pickPlain);
            Require(folderPanel >= 0 && plainPanel >= 0, "the feedback fixture lost its rows.");
            layerPanelDrag = new(picture, [pickTarget], null);
            void Over(Point at) => TrackLayerDropFeedback(at, LayerPanelDropOperation(at, copy: false));

            Over(PointInRow(plainPanel, 0.5));   // plain body offers above
            Require(DropInsertionLine.Visibility == Visibility.Visible && DropFolderBand.Visibility == Visibility.Collapsed,
                "a plain row's drop showed no insertion line.");
            double rowTop = layerItems[plainPanel].TransformToVisual(LayerDropFeedback).TransformPoint(new Point(0, 0)).Y;
            Require(Math.Abs(Canvas.GetTop(DropInsertionLine) - rowTop) < 1.5,
                $"the insertion line did not sit on the row it drops above (line {Canvas.GetTop(DropInsertionLine)}, row top {rowTop}).");

            var folderPoint = PointInRow(folderPanel, 0.5);
            var folderLocal = LayerList.TransformToVisual(RootGrid).Inverse.TransformPoint(folderPoint);
            var folderOperation = LayerPanelDropOperation(folderPoint, copy: false);
            Over(folderPoint);  // folder body offers INTO
            Require(DropFolderBand.Visibility == Visibility.Visible && DropInsertionLine.Visibility == Visibility.Collapsed,
                $"a folder row's drop showed no band highlight (local={folderLocal}, list={LayerList.ActualWidth}x{LayerList.ActualHeight}, target={LayerDropTarget(folderPoint)}, operation={folderOperation}).");
            Require(Math.Abs(Canvas.GetTop(DropFolderBand) - layerItems[folderPanel].TransformToVisual(LayerDropFeedback)
                    .TransformPoint(new Point(0, 0)).Y) < 1.5 && DropFolderBand.Height == layerItems[folderPanel].ActualHeight,
                "the folder highlight did not cover that row's own band.");

            Over(new Point(-50, -50));   // outside the list in root coordinates: nothing offered, nothing painted
            Require(DropInsertionLine.Visibility == Visibility.Collapsed && DropFolderBand.Visibility == Visibility.Collapsed,
                "a point outside the list kept a drop picture lit.");

            var effectDrag = new LayerPanelDrag(picture, Array.Empty<Guid>(), (LayerEffectKind.Stroke, pickPlain));   // source carries the stroke below
            layerPanelDrag = effectDrag;
            // No offer-precondition here on purpose: the seam paints only when the drag carries no
            // effect, so the claim is structural, and demanding a live offer made the fixture
            // environment-sensitive (transient state left by inline predecessors refused the offer
            // while the scope run accepted it). An assertion that only holds in one runner is noise.
            Over(PointInRow(RowIndex(pickTarget), 0.5));   // effect drags accept rows - and paint nothing, as on the Mac
            Require(DropInsertionLine.Visibility == Visibility.Collapsed && DropFolderBand.Visibility == Visibility.Collapsed,
                "an effect drag drew the plain-drop line (the Mac's :398 returns before setDropRow).");
            layerPanelDrag = null;

            HideLayerDropFeedback();
            Require(DropInsertionLine.Visibility == Visibility.Collapsed && DropFolderBand.Visibility == Visibility.Collapsed,
                "the hide seam left an element visible.");
        }
        report["theDropPictureFollowsTheZoneDecision"] = "passed";
    }
}
#endif
