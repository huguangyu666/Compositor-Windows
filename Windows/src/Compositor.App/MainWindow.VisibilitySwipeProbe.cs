#if DEBUG
using Compositor.Core;
using Windows.Foundation;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task VerifyVisibilitySwipeAsync(Dictionary<string, object> report)
    {
        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
        Point InRow(Guid id)
        {
            var grid = layerRows[id];
            return grid.TransformToVisual(RootGrid).TransformPoint(new(grid.ActualWidth / 2, grid.ActualHeight / 2));
        }
        bool Visible(EditorDocument owner, Guid id) => owner.Snapshot.Manifest.Layers.Single(layer => layer.Id == id).IsVisible;

        var scene = EditorDocument.Create(64, 48);
        var child = scene.ImportImage(new Surface(16, 16), "Child");
        var folder = scene.GroupLayers([child.Id])!;
        var hidden = scene.ImportImage(new Surface(16, 16), "Already hidden");
        hidden.IsVisible = false;
        var top = scene.ImportImage(new Surface(16, 16), "Top");
        scene = new EditorDocument(scene.Snapshot);
        OpenDocument(scene); await WaitForFramesAsync(); RootGrid.UpdateLayout();
        var primary = selectedLayerId;
        var selection = selectedLayerIds.ToHashSet();
        var warm = compositor;
        var rows = layerRows.ToDictionary(pair => pair.Key, pair => pair.Value);
        Require(BeginVisibilitySwipe(top.Id), "The top eye could not start a swipe.");
        UpdateVisibilitySwipe(InRow(hidden.Id));
        UpdateVisibilitySwipe(InRow(folder.Id));
        UpdateVisibilitySwipe(InRow(top.Id));
        FinishVisibilitySwipe(); await WaitForFramesAsync();
        Require(!Visible(scene, top.Id) && !Visible(scene, hidden.Id) && !Visible(scene, folder.Id) && Visible(scene, child.Id),
            "The swipe toggled an already-hidden row, revisited row, or unvisited folder child.");
        Require(primary == selectedLayerId && selection.SetEquals(selectedLayerIds) && ReferenceEquals(warm, compositor)
            && rows.All(pair => ReferenceEquals(pair.Value, layerRows[pair.Key])), "An eye swipe changed selection or rebuilt the layer panel/compositor.");
        Require(rows[top.Id].Tag is LayerRow { Visible: false, Eye.IsChecked: false }
            && rows[folder.Id].Tag is LayerRow { Visible: false, Eye.IsChecked: false }, "Swiped eye controls are stale.");
        AssertUploadedFrame(scene);
        OnUndo(this, new()); await WaitForFramesAsync();
        Require(!scene.CanUndo && Visible(scene, top.Id) && !Visible(scene, hidden.Id) && Visible(scene, folder.Id),
            "The complete hide swipe did not undo once.");
        OnRedo(this, new()); await WaitForFramesAsync(); AssertUploadedFrame(scene);
        Require(BeginVisibilitySwipe(top.Id), "A hidden eye could not start a show swipe.");
        UpdateVisibilitySwipe(InRow(hidden.Id)); UpdateVisibilitySwipe(InRow(folder.Id));
        FinishVisibilitySwipe(); await WaitForFramesAsync();
        Require(Visible(scene, top.Id) && Visible(scene, hidden.Id) && Visible(scene, folder.Id), "A hidden start did not show the visited rows.");
        OnUndo(this, new()); await WaitForFramesAsync();
        Require(!Visible(scene, top.Id) && !Visible(scene, hidden.Id) && !Visible(scene, folder.Id), "Showing rows did not undo independently.");
        report["visibilitySwipeUniformStateSingleUndoGpuAndStableRows"] = "passed";

        scene = new EditorDocument(scene.Snapshot); OpenDocument(scene); await WaitForFramesAsync();
        var eye = ((LayerRow)layerRows[top.Id].Tag).Eye;
        eye.IsChecked = true; await WaitForFramesAsync();
        Require(Visible(scene, top.Id) && visibilitySwipe is null && scene.CanUndo, "Keyboard/automation eye toggles stopped working.");
        OnUndo(this, new()); await WaitForFramesAsync();
        Require(!scene.CanUndo && !Visible(scene, top.Id), "A normal eye toggle did not remain one Undo.");
        Require(BeginVisibilitySwipe(top.Id), "The interleaving control did not start.");
        scene.RenameLayer(hidden.Id, "Renamed during swipe");
        Require(visibilitySwipe is null, "A foreign document change left an eye gesture open.");
        UpdateVisibilitySwipe(InRow(hidden.Id)); FinishVisibilitySwipe();
        Require(!Visible(scene, hidden.Id), "An ended gesture changed another row after an unrelated edit.");
        OnUndo(this, new()); await WaitForFramesAsync();
        Require(Visible(scene, top.Id), "Undoing the rename consumed the swipe too.");
        OnUndo(this, new()); await WaitForFramesAsync();
        Require(!scene.CanUndo && !Visible(scene, top.Id), "The interrupted swipe did not retain its own Undo.");
        report["visibilitySwipeKeepsKeyboardTogglesAndEndsOnForeignEdit"] = "passed";

        scene = EditorDocument.Create(64, 48);
        var bottom = scene.Snapshot.Manifest.Layers[0].Id;
        for (int i = 0; i < 35; i++) scene.ImportImage(new Surface(16, 16), $"Eye row {i}");
        top = scene.Snapshot.Manifest.Layers[^1];
        scene = new EditorDocument(scene.Snapshot); OpenDocument(scene); await WaitForFramesAsync(); RootGrid.UpdateLayout();
        var scroll = FindLayerScrollViewer(LayerList) ?? throw new InvalidOperationException("Layer scrolling is missing.");
        scroll.ChangeView(null, 0, null, true);
        Require(await WaitUntilAsync(() => scroll.VerticalOffset == 0, 2000), "The eye fixture did not return to the first row.");
        Point AtEdge() => scroll.TransformToVisual(RootGrid).TransformPoint(new(scroll.ActualWidth / 2, scroll.ActualHeight - 1));
        warm = compositor; primary = selectedLayerId;
        Require(BeginVisibilitySwipe(top.Id), "The scrolling eye control did not start.");
        UpdateVisibilitySwipe(AtEdge());
        Require(await WaitUntilAsync(() => scroll.VerticalOffset >= scroll.ScrollableHeight - 1 && !Visible(scene, bottom), 8000),
            "A stationary eye swipe did not reach and change the last row.");
        FinishVisibilitySwipe(); await WaitForFramesAsync();
        Require(primary == selectedLayerId && ReferenceEquals(warm, compositor)
            && scene.Snapshot.Manifest.Layers.Count(layer => !layer.IsVisible) > 5,
            "Scrolling did not visit the revealed rows or preserve the active layer/cache.");
        AssertUploadedFrame(scene);
        OnUndo(this, new()); await WaitForFramesAsync();
        Require(!scene.CanUndo && scene.Snapshot.Manifest.Layers.All(layer => layer.IsVisible), "The long swipe required multiple Undo steps.");
        report["visibilitySwipeAutoscrollVisitsRevealedRowsWithOneUndo"] = "passed";

        Require(BeginVisibilitySwipe(top.Id), "The cancellation control did not start.");
        UpdateVisibilitySwipe(AtEdge()); FinishVisibilitySwipe();
        Require(visibilitySwipe is null && layerAutoscroll?.IsRunning != true, "Ending the gesture retained its timer.");
        Require(BeginVisibilitySwipe(top.Id), "The replacement control did not start.");
        var replacement = EditorDocument.Create(64, 48);
        OpenDocument(replacement); await WaitForFramesAsync();
        UpdateVisibilitySwipe(AtEdge()); FinishVisibilitySwipe();
        Require(visibilitySwipe is null && layerAutoscroll?.IsRunning != true && !replacement.CanUndo
            && replacement.Snapshot.Manifest.Layers[0].IsVisible, "A retired eye gesture changed the replacement document.");
        AssertUploadedFrame(replacement);
        report["visibilitySwipeStopsOnEndAndDocumentReplacement"] = "passed";
    }
}
#endif
