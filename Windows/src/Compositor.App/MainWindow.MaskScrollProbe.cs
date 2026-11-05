#if DEBUG
using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task VerifyMaskCopyAutoscrollAsync(Dictionary<string, object> report)
    {
        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
        static ScrollViewer? FindScroll(DependencyObject node)
        {
            if (node is ScrollViewer scroll) return scroll;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                if (FindScroll(VisualTreeHelper.GetChild(node, i)) is { } found) return found;
            return null;
        }

        var scene = EditorDocument.Create(64, 48);
        var target = scene.Snapshot.Manifest.Layers[0].Id;
        for (int i = 0; i < 35; i++) scene.ImportImage(new Surface(16, 16), $"Scroll row {i}");
        var source = scene.Snapshot.Manifest.Layers[^1];
        source.MaskFile = ProjectValidator.Uuid(source.Id) + ".mask.png";
        source.MaskLinked = false;
        source.MaskEnabled = false;
        source.MaskPlacement = new() { Origin = new(3, 4), Size = new(4, 4), Sampling = LayerSampling.Nearest };
        var mask = new MaskSurface(4, 4, [0, 32, 64, 96, 128, 160, 192, 224, 255, 0, 255, 0, 1, 2, 3, 4]);
        ((Dictionary<Guid, MaskSurface>)scene.Snapshot.Masks)[source.Id] = mask;
        scene = new EditorDocument(scene.Snapshot);
        OpenDocument(scene); await WaitForFramesAsync(); RootGrid.UpdateLayout();
        var scroll = FindScroll(LayerList) ?? throw new InvalidOperationException("Layer list scroll viewer is missing.");
        Require(scroll.ScrollableHeight > 200, "The mask-copy fixture does not overflow the list.");
        scroll.ChangeView(null, 0, null, true);
        await WaitUntilAsync(() => scroll.VerticalOffset == 0, 2000);
        Point InScroll(double y, double? x = null) => scroll.TransformToVisual(RootGrid)
            .TransformPoint(new Point(x ?? scroll.ActualWidth / 2, y));
        var sourceGrid = layerRows[source.Id];
        var start = sourceGrid.TransformToVisual(RootGrid).TransformPoint(new(sourceGrid.ActualWidth / 2, sourceGrid.ActualHeight / 2));

        BeginMaskCopyGesture(source.Id, start);
        UpdateMaskCopyGesture(new(start.X + 2, start.Y));
        await Task.Delay(80);
        Require(maskCopyGesture is { Moved: false } && scroll.VerticalOffset == 0,
            "A mask click below the drag threshold scrolled the list.");
        FinishMaskCopyGesture(new(start.X + 2, start.Y)); await WaitForFramesAsync();
        Require(CurrentMaskAloneId == source.Id && !scene.CanUndo, "An Alt click no longer shows the mask alone.");
        HandleThumbnailClick(source.Id, false, Windows.System.VirtualKeyModifiers.None); await WaitForFramesAsync();
        Require(CurrentMaskAloneId is null, "Returning to the pixel target did not leave mask-alone view.");
        var compositorBefore = compositor;
        int frames = presentedFrameCount;
        BeginMaskCopyGesture(source.Id, start);
        UpdateMaskCopyGesture(InScroll(scroll.ActualHeight - 1));
        Require(await WaitUntilAsync(() => scroll.VerticalOffset > 60, 2000),
            "Dragging a mask to the lower edge did not scroll the layer list.");
        double movingOffset = scroll.VerticalOffset;
        Require(await WaitUntilAsync(() => scroll.VerticalOffset > movingOffset + 35, 1500),
            "Mask autoscroll stopped when the pointer stayed still at the edge.");
        report["maskCopyScrollsWithStationaryEdgePointer"] = "passed";

        UpdateMaskCopyGesture(InScroll(scroll.ActualHeight / 2));
        await Task.Delay(80);
        double middleOffset = scroll.VerticalOffset;
        await Task.Delay(120);
        Require(scroll.VerticalOffset == middleOffset, "Returning to the middle did not stop mask autoscroll.");
        UpdateMaskCopyGesture(InScroll(1));
        Require(await WaitUntilAsync(() => scroll.VerticalOffset < middleOffset - 20, 1500),
            "Dragging to the upper edge did not reverse mask autoscroll.");
        UpdateMaskCopyGesture(InScroll(1, -10));
        await Task.Delay(80);
        double outsideOffset = scroll.VerticalOffset;
        await Task.Delay(120);
        Require(scroll.VerticalOffset == outsideOffset, "Leaving the list horizontally kept scrolling it.");
        Require(!scene.CanUndo && ReferenceEquals(compositor, compositorBefore) && presentedFrameCount == frames,
            "Scrolling the layer list changed history or recomposited the image.");
        report["maskCopyScrollStopsAndReversesWithoutCanvasWork"] = "passed";

        UpdateMaskCopyGesture(InScroll(scroll.ActualHeight - 1));
        Require(await WaitUntilAsync(() => scroll.VerticalOffset >= scroll.ScrollableHeight - 1, 8000),
            "Mask autoscroll did not reach the last layer.");
        UpdateMaskCopyGesture(InScroll(scroll.ActualHeight / 2));
        RootGrid.UpdateLayout();
        var grid = layerRows[target];
        var drop = grid.TransformToVisual(RootGrid).TransformPoint(new(grid.ActualWidth / 2, grid.ActualHeight / 2));
        Require(ThumbnailDropTarget(drop) == target, "The newly revealed layer is not a mask drop target.");
        Require(ThumbnailDropTarget(InScroll(-10)) is null, "An offscreen row accepted a mask drop above the list.");
        FinishMaskCopyGesture(drop); await WaitForFramesAsync();
        Require(SelectedLayer?.Id == target && paintOnMask && CurrentMaskAloneId is null
            && ReferenceEquals(scene.Snapshot.Masks[target], mask), "The mask did not land on the revealed layer.");
        var landed = scene.Snapshot.Manifest.Layers.Single(layer => layer.Id == target);
        Require(landed.MaskEnabled == false && landed.MaskLinked == false && landed.MaskPlacement?.Origin == new PointD(3, 4),
            "The scrolled mask copy lost its placement or flags.");
        Require(scene.Undo() && !scene.CanUndo && !scene.Snapshot.Masks.ContainsKey(target),
            "The scrolled mask copy was not one Undo step.");
        Require(scene.Redo() && ReferenceEquals(scene.Snapshot.Masks[target], mask), "The scrolled mask copy did not Redo.");
        await WaitForFramesAsync();
        report["maskCopyToOffscreenLayerPreservesSingleUndoRedo"] = "passed";

        BeginMaskCopyGesture(source.Id, start);
        UpdateMaskCopyGesture(InScroll(1));
        Require(await WaitUntilAsync(() => scroll.VerticalOffset < scroll.ScrollableHeight - 20, 1500),
            "The cancellation control did not start scrolling.");
        CancelMaskCopyGesture();
        await Task.Delay(80);
        double cancelledOffset = scroll.VerticalOffset;
        await Task.Delay(120);
        Require(maskCopyGesture is null && layerAutoscroll?.IsRunning != true && scroll.VerticalOffset == cancelledOffset,
            "Cancelling a mask gesture retained its scroll timer.");
        BeginMaskCopyGesture(source.Id, start);
        UpdateMaskCopyGesture(InScroll(1));
        var replacement = new EditorDocument(scene.Snapshot);
        OpenDocument(replacement); await WaitForFramesAsync();
        Require(maskCopyGesture is null && layerAutoscroll?.IsRunning != true && !replacement.CanUndo,
            "Replacing the document retained an old mask gesture.");
        report["maskCopyScrollRetiresOnCancelAndDocumentReplacement"] = "passed";
    }
}
#endif
