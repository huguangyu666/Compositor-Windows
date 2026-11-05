using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private DispatcherQueueTimer? layerAutoscroll;
    private ScrollViewer? layerScrollViewer;
    private Point layerScrollPoint;

    private void TrackLayerAutoscroll(Point point)
    {
        if (maskCopyGesture is not { Moved: true } && visibilitySwipe is null) return;
        layerScrollPoint = point;
        layerScrollViewer ??= FindLayerScrollViewer(LayerList);
        if (layerScrollViewer is not { } scroll || LayerScrollDelta(scroll) == 0)
        { layerAutoscroll?.Stop(); return; }
        if (layerAutoscroll is null)
        {
            layerAutoscroll = DispatcherQueue.CreateTimer();
            layerAutoscroll.Interval = TimeSpan.FromSeconds(1.0 / 60);
            layerAutoscroll.Tick += (_, _) => StepLayerAutoscroll();
        }
        if (!layerAutoscroll.IsRunning) layerAutoscroll.Start();
    }

    private static ScrollViewer? FindLayerScrollViewer(DependencyObject node)
    {
        if (node is ScrollViewer scroll) return scroll;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (FindLayerScrollViewer(VisualTreeHelper.GetChild(node, i)) is { } found) return found;
        return null;
    }

    private double LayerScrollDelta(ScrollViewer scroll)
    {
        var point = scroll.TransformToVisual(RootGrid).Inverse.TransformPoint(layerScrollPoint);
        if (point.X < 0 || point.X >= scroll.ActualWidth || scroll.ActualHeight <= 0) return 0;
        double edge = Math.Min(24, scroll.ActualHeight / 2);
        if (point.Y < edge) return -Math.Min(12, 2 + (edge - point.Y) * .4);
        if (point.Y > scroll.ActualHeight - edge) return Math.Min(12, 2 + (point.Y - scroll.ActualHeight + edge) * .4);
        return 0;
    }

    private void StepLayerAutoscroll()
    {
        var owner = visibilitySwipe?.Owner ?? maskCopyGesture?.Owner;
        if (windowClosed || !CanSelectLayerTarget || owner is null || !ReferenceEquals(document, owner))
        { FinishVisibilitySwipe(); CancelMaskCopyGesture(); return; }
        // ChangeView settles during layout. Visit the row revealed by the preceding tick before
        // advancing, including the final row when the list has reached its scrolling limit.
        if (visibilitySwipe is not null) ApplyVisibilityAtPoint(layerScrollPoint);
        if (layerScrollViewer is not { } scroll) return;
        double next = Math.Clamp(scroll.VerticalOffset + LayerScrollDelta(scroll), 0, scroll.ScrollableHeight);
        if (next == scroll.VerticalOffset) { layerAutoscroll?.Stop(); return; }
        scroll.ChangeView(null, next, null, true);
    }

    private void StopLayerAutoscroll()
    {
        layerAutoscroll?.Stop();
        layerScrollViewer = null;
    }

    private void CancelMaskCopyGesture()
    {
        bool captured = maskCopyGesture is not null;
        maskCopyGesture = null;
        StopLayerAutoscroll();
        if (captured) LayerList.ReleasePointerCaptures();
    }
}
