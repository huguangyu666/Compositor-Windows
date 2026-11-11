using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private Grid timelinePlayhead = null!;
    private readonly TranslateTransform timelinePlayheadShift = new();
    private readonly RectangleGeometry timelinePlayheadClip = new();
    private Storyboard? timelinePlayheadAnimation;
    private double? timelinePointerX;
    private int timelineHighlightedFrame = -1;

    private Grid CreateTimelineSurface()
    {
        var surface = new Grid(); surface.Children.Add(timelineCanvas);
        var overlay = new Canvas { IsHitTestVisible = false, Clip = timelinePlayheadClip };
        var ink = new SolidColorBrush(Color(85, 195, 255));
        timelinePlayhead = new Grid { Width = 8, Height = 160, RenderTransform = timelinePlayheadShift, UseLayoutRounding = false };
        timelinePlayhead.Children.Add(new Border { Width = 1.5, HorizontalAlignment = HorizontalAlignment.Center, Background = ink });
        timelinePlayhead.Children.Add(new Border { Width = 8, Height = 7, VerticalAlignment = VerticalAlignment.Top, Background = ink });
        overlay.Children.Add(timelinePlayhead); surface.Children.Add(overlay);
        return surface;
    }

    private void RefreshTimelinePlayhead(bool restartAnimation = false)
    {
        if (timelinePlayhead is null) return;
        double width = timelineCanvas.ActualWidth, height = timelineCanvas.ActualHeight;
        bool visible = timelineVisible && Timeline is not null && width > TimelineNamesWidth && height > 0;
        timelinePlayhead.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) { StopTimelinePlayheadAnimation(); return; }
        if (timelinePlayhead.Height != height) timelinePlayhead.Height = height;
        var clip = new Rect(TimelineNamesWidth, 0, width - TimelineNamesWidth, height);
        if (timelinePlayheadClip.Rect != clip) timelinePlayheadClip.Rect = clip;
        if (timelinePlaying)
        {
            if (restartAnimation || timelinePlayheadAnimation is null) StartTimelinePlayheadAnimation();
            return;
        }
        double x = timelinePointerX ?? TimelineX(timelineFrame);
        if (timelinePlayheadShift.X != x - 4) timelinePlayheadShift.X = x - 4;
#if DEBUG
        timelinePlayheadGeometryProbe?.Invoke(timelinePlayheadShift.X + 4);
#endif
    }

    private void MoveTimelinePlayhead(double x)
    {
        timelinePointerX = Math.Clamp(x, TimelineX(0), TimelineX((Timeline?.DurationFrames ?? 1) - 1));
        RefreshTimelinePlayhead();
    }

    private void StartTimelinePlayheadAnimation()
    {
        StopTimelinePlayheadAnimation();
        if (!timelinePlaying || Timeline is not { } timeline) return;
        // RenderTransform animations stay on the compositor. Image rendering and UI frame
        // sampling may finish later without turning the time pointer into frame-sized jumps.
        var motion = new DoubleAnimation
        {
            From = TimelineX(0) - 4, To = TimelineX(timeline.DurationFrames) - 4,
            Duration = new(TimeSpan.FromSeconds((double)timeline.DurationFrames / timeline.FramesPerSecond)),
            RepeatBehavior = timeline.Loop ? RepeatBehavior.Forever : new RepeatBehavior(1),
        };
        Storyboard.SetTarget(motion, timelinePlayheadShift); Storyboard.SetTargetProperty(motion, "X");
        timelinePlayheadAnimation = new Storyboard(); timelinePlayheadAnimation.Children.Add(motion);
        timelinePlayheadAnimation.Begin();
        double position = timelineStartFrame + timelineClock.Elapsed.TotalSeconds * timeline.FramesPerSecond;
        position = timeline.Loop ? position % timeline.DurationFrames : Math.Min(position, timeline.DurationFrames);
        timelinePlayheadAnimation.SeekAlignedToLastTick(TimeSpan.FromSeconds(position / timeline.FramesPerSecond));
    }

    private void StopTimelinePlayheadAnimation()
    {
        timelinePlayheadAnimation?.Stop(); timelinePlayheadAnimation = null;
    }
}
