#if DEBUG
using Compositor.Core;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Windows.System;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task VerifyLayerSelectionAsync(Dictionary<string, object> report)
    {
        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
        HashSet<Guid> NativeSelection() => LayerList.SelectedItems.OfType<ListViewItem>()
            .Select(item => ((LayerRow)item.Tag).Layer.Id).ToHashSet();
        async Task Settled(HashSet<Guid> expected)
        {
            Require(await WaitUntilAsync(() => selectedLayerIds.SetEquals(expected), 2500),
                $"The editor did not adopt the native layer selection (expected {string.Join(',', expected)}, "
                + $"editor {string.Join(',', selectedLayerIds)}, native {string.Join(',', NativeSelection())}).");
            await WaitForFramesAsync();
            Require(NativeSelection().SetEquals(expected), "Refreshing the editor changed the native selection.");
        }

        var scene = EditorDocument.Create(64, 48);
        for (int i = 0; i < 5; i++) scene.ImportImage(new Surface(8, 8), $"Selection row {i}");
        scene = new EditorDocument(scene.Snapshot);
        OpenDocument(scene); await WaitForFramesAsync();
        Require(LayerList.SelectionMode == ListViewSelectionMode.Extended,
            "The layer list still uses single selection instead of native Ctrl/Shift selection.");
        var warm = compositor;
        int frames = presentedFrameCount;
        var range = layerItems.Take(4).Select(grid => ((LayerRow)grid.Tag).Layer.Id).ToHashSet();
        LayerList.SelectRange(new ItemIndexRange(0, 4));
        await Settled(range);
        Require(!scene.CanUndo && ReferenceEquals(warm, compositor) && presentedFrameCount == frames,
            "Range selection created history or recomposited the document.");
        RefreshLayerPanel(); await Settled(range);
        report["nativeLayerRangeSelectionSurvivesRefreshWithoutRenderOrHistory"] = "passed";

        var primary = selectedLayerId!.Value;
        LayerList.SelectedItems.Remove(layerRows[primary]);
        range.Remove(primary); await Settled(range);
        Require(selectedLayerId is { } next && range.Contains(next)
            && scene.Snapshot.Manifest.ActiveLayerId == next, "Removing the primary did not hand it to a selected row.");
        var remaining = range.First();
        foreach (var item in LayerList.SelectedItems.ToArray())
            if (!ReferenceEquals(item, layerRows[remaining])) LayerList.SelectedItems.Remove(item);
        await Settled([remaining]);
        LayerList.SelectedItems.Remove(layerRows[remaining]);
        await Settled([]);
        Require(selectedLayerId is null && SelectedLayer is null && scene.Snapshot.Manifest.ActiveLayerId is null,
            "Removing the last selected row left a hidden editing target.");
        LayerList.SelectedItems.Add(layerRows[primary]); await Settled([primary]);
        report["nativeLayerToggleHandlesPrimaryAndEmptySelection"] = "passed";

        LayerList.SelectRange(new ItemIndexRange(0, 3));
        var three = layerItems.Take(3).Select(grid => ((LayerRow)grid.Tag).Layer.Id).ToHashSet();
        // SelectRange adds to the native selection, just as Ctrl+Shift adds another range.
        three.Add(primary); await Settled(three);
        var target = ((LayerRow)layerItems[^1].Tag).Layer.Id;
        HandleThumbnailClick(target, false, VirtualKeyModifiers.None);
        await Settled([target]);
        Require(SelectedLayer?.Id == target && !scene.CanUndo, "A thumbnail did not collapse the selected rows to its layer.");
        report["layerThumbnailCollapsesNativeRangeSelection"] = "passed";

        using (var release = new ManualResetEventSlim(false))
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            beforeFrameRender = () =>
            {
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Layer selection reader timed out.");
            };
            try
            {
                RenderCanvas(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                LayerList.SelectedItems.Clear();
                LayerList.SelectRange(new ItemIndexRange(1, 3));
            }
            finally { beforeFrameRender = null; release.Set(); }
            var latest = layerItems.Skip(1).Take(3).Select(grid => ((LayerRow)grid.Tag).Layer.Id).ToHashSet();
            await Settled(latest);
            Require(!scene.CanUndo, "Queued selection changes created history.");
        }
        report["latestNativeLayerSelectionWinsWhileRenderIsBusy"] = "passed";

        scene = TransformScene(true);
        // Integer upright geometry isolates native membership from the existing pointer rounding.
        foreach (var layer in scene.Snapshot.Manifest.Layers)
        {
            layer.Transform.Origin = new(Math.Round(layer.Transform.Origin.X), Math.Round(layer.Transform.Origin.Y));
            layer.Transform.Rotation = 0;
        }
        OpenDocument(scene); await WaitForFramesAsync();
        ToolCombo.SelectedIndex = (int)CanvasTool.Move; await WaitForFramesAsync();
        LayerList.SelectAll();
        var both = scene.Snapshot.Manifest.Layers.Select(layer => layer.Id).ToHashSet();
        await Settled(both);
        Require(transformInspectorBasis?.IsGroup == true, "Native selection did not reach the group transform.");
        var origin = scene.Snapshot.Manifest.Layers.ToDictionary(layer => layer.Id, layer => layer.Transform.Origin);
        var box = CurrentTransformValue!;
        BeginTransformPointer(box.Center, VirtualKeyModifiers.None); await WaitForFramesAsync();
        MoveTransformPointer(new(box.Center.X + 13, box.Center.Y + 7), VirtualKeyModifiers.Control); await WaitForFramesAsync();
        FinishPlacementDrag(); await WaitForFramesAsync();
        Require(scene.Snapshot.Manifest.Layers.All(layer => Math.Abs(layer.Transform.Origin.X - origin[layer.Id].X - 13) < .001
            && Math.Abs(layer.Transform.Origin.Y - origin[layer.Id].Y - 7) < .001), "The native selection did not move both layers.");
        AssertUploadedFrame(scene);
        OnUndo(this, new()); await WaitForFramesAsync();
        Require(!scene.CanUndo && scene.Snapshot.Manifest.Layers.All(layer => layer.Transform.Origin == origin[layer.Id]),
            "The native group move did not undo once.");
        AssertUploadedFrame(scene);
        report["nativeLayerSelectionDrivesGroupMoveAndSingleUndo"] = "passed";

        // Select > "Layer's Pixels" / "Mask's Black Areas" (CompositorApp.swift:231-243): the menu runs
        // the SAME engine as a Ctrl+click on a row thumbnail (LoadThumbnailSelection), so this group
        // drives the menu handlers and checks what the menu path must own — the offered state, the
        // traced geometry, the Mac's silent guards, and the one-undo commit.
        var menuScene = EditorDocument.Create(64, 48);
        var half = new Surface(8, 8);
        for (int row = 0; row < 4; row++)
            for (int x = 0; x < 8; x++)
            { int p = row * 8 * 4 + x * 4; half.Pixels[p + 3] = 255; half.Pixels[p] = 200; }
        menuScene.ImportImage(half, "Half opaque");
        menuScene = new EditorDocument(menuScene.Snapshot);
        OpenDocument(menuScene); await WaitForFramesAsync(); await WaitForSelectionReadyAsync();
        Guid RowOf(string name) => menuScene.Snapshot.Manifest.Layers.First(layer => layer.Name == name).Id;
        void SelectRow(string name)
        {
            // WinUI SelectRange ADDS to the native selection, so a "pick just this row" helper must
            // clear the old range first or the fixture silently drifts into multi-selection.
            int index = layerItems.ToList().FindIndex(grid => ((LayerRow)grid.Tag).Layer.Name == name);
            LayerList.DeselectRange(new ItemIndexRange(0, (uint)layerItems.Count()));
            LayerList.SelectRange(new ItemIndexRange(index, 1));
        }
        SelectRow("Half opaque");
        Require(await WaitUntilAsync(() => selectedLayerId == RowOf("Half opaque") && selectedLayerIds.Count == 1, 2500),
            "the menu-load fixture never selected its own row alone.");
        await WaitForFramesAsync();
        Require(LoadLayerPixelsMenuItem.IsEnabled && !LoadMaskBlackMenuItem.IsEnabled,
            "a pixel-carrying layer without a mask did not offer exactly the pixels load.");
        var prior = menuScene.ActiveSelection;
        OnLoadLayerPixels(this, new());
        Require(await WaitUntilAsync(() => !ReferenceEquals(menuScene.ActiveSelection, prior), 3000),
            "the menu load never reached the selection.");
        var cover = menuScene.ActiveSelection!.Coverage;   // document-sized grid; the 8×8 layer sits at (0,0)
        Require(cover.Width == 64 && cover.Height == 48
                && cover.Pixels[0] > 0 && cover.Pixels[3 * 64 + 7] > 0   // inside: rows 0-3 (opaque)
                && cover.Pixels[8] == 0                                  // right of the blob, row 0
                && cover.Pixels[4 * 64] == 0,                            // alpha 0 stops the trace
            "the traced selection did not match the layer's own opaque rectangle.");
        Require(menuScene.CanUndo, "the load committed without an undo step.");
        menuScene.Undo(); await WaitForFramesAsync();
        Require(menuScene.ActiveSelection is null || ReferenceEquals(menuScene.ActiveSelection, prior),
            "one undo did not remove exactly the load.");

        // An all-clear layer: the Mac's engine has nothing to trace and keeps the current selection
        // (RasterSelection returns null before any commit) — the menu must not empty it.
        var cleared = new Surface(6, 6);
        menuScene.ImportImage(cleared, "All clear");
        menuScene = new EditorDocument(menuScene.Snapshot);
        OpenDocument(menuScene); await WaitForFramesAsync(); await WaitForSelectionReadyAsync();
        SelectRow("Half opaque");
        OnLoadLayerPixels(this, new());
        Require(await WaitUntilAsync(() => menuScene.ActiveSelection is not null, 3000),
            "the pixels load never produced a selection on the half-opaque layer.");
        var withSel = menuScene.ActiveSelection;
        SelectRow("All clear");
        Require(await WaitUntilAsync(() => selectedLayerId == RowOf("All clear") && selectedLayerIds.Count == 1, 2500),
            "the all-clear row never became the sole selection.");
        await WaitForFramesAsync();
        Require(!LoadMaskBlackMenuItem.IsEnabled && LoadLayerPixelsMenuItem.IsEnabled,
            "a maskless pixel layer offered the wrong pair of loads.");
        OnLoadLayerPixels(this, new()); await WaitForFramesAsync();
        Require(ReferenceEquals(menuScene.ActiveSelection, withSel),
            "the all-clear layer emptied the selection instead of keeping it (the Mac traces nothing).");

        // Multi-select is the port's deliberate divergence: the Mac offers the active layer, this
        // window refuses to trace two rows under one name, so both items disable.
        int first = layerItems.ToList().FindIndex(grid => ((LayerRow)grid.Tag).Layer.Name == "Half opaque");
        LayerList.SelectRange(new ItemIndexRange(first, 2));
        await WaitForFramesAsync();
        Require(!LoadLayerPixelsMenuItem.IsEnabled && !LoadMaskBlackMenuItem.IsEnabled,
            "multi-selection still offered single-layer loads.");

        // Mask side: offered only once a mask exists; the hide-all mask is all black, so the load
        // selects the full 6×6 layer rectangle (black < 128 everywhere).
        SelectRow("All clear");
        Require(await WaitUntilAsync(() => selectedLayerId == RowOf("All clear") && selectedLayerIds.Count == 1, 2500),
            "the fixture never returned to a single-row selection for the mask add.");
        await WaitForFramesAsync();
        var clearId = RowOf("All clear");
        OnAddHideAllMask(this, new());
        Require(await WaitUntilAsync(() => menuScene.Snapshot.Masks.ContainsKey(clearId)
                && LoadMaskBlackMenuItem.IsEnabled, 2500),
            "the hide-all mask never landed or never offered the black-areas load.");
        var beforeMask = menuScene.ActiveSelection;
        OnLoadMaskBlack(this, new());
        Require(await WaitUntilAsync(() => !ReferenceEquals(menuScene.ActiveSelection, beforeMask), 3000),
            "the black-areas load never reached the selection.");
        var maskCover = menuScene.ActiveSelection!.Coverage;
        Require(maskCover.Width == 64 && maskCover.Height == 48
                && maskCover.Pixels[0] > 0 && maskCover.Pixels[5 * 64 + 5] > 0   // 6×6 all black
                && maskCover.Pixels[6] == 0 && maskCover.Pixels[6 * 64] == 0,    // stops at the layer rect
            "the black-areas load did not cover exactly the 6×6 mask rectangle.");
        report["theSelectMenuLoadsShareTheThumbnailEngine"] = "passed";

        await VerifyLayerDropAsync(report);
    }
}
#endif
