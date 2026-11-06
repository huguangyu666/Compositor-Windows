using Compositor.Core;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private void DrawTimeline(CanvasControl sender, CanvasDrawEventArgs e)
    {
        if (document is not { } owner || Timeline is not { } timeline) return;
        var draw = e.DrawingSession; double width = sender.ActualWidth, height = sender.ActualHeight;
        if (width <= TimelineNamesWidth || height <= TimelineRulerHeight) return;
        using var font = new CanvasTextFormat { FontSize = 11, WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
            VerticalAlignment = CanvasVerticalAlignment.Center };
        draw.FillRectangle(new Rect(0, 0, width, TimelineRulerHeight), Color(43, 43, 43));
        draw.DrawText(Loc.T("TL_Layers"), new Rect(12, 0, TimelineNamesWidth - 20, TimelineRulerHeight), Color(180, 180, 180), font);
        int step = 1; while (step * timelinePixelsPerFrame < 68) step *= 2;
        int start = (int)timelineHorizontal.Value / step * step;
        using (draw.CreateLayer(1, new Rect(TimelineNamesWidth, 0, width - TimelineNamesWidth, height)))
        {
            for (int frame = start; frame <= timeline.DurationFrames && TimelineX(frame) <= width; frame += step)
            {
                float x = (float)TimelineX(frame);
                draw.DrawLine(x, (float)TimelineRulerHeight, x, (float)height, Color(49, 49, 49));
                draw.DrawText($"{(double)frame / timeline.FramesPerSecond:0.##}s", new Rect(x + 5, 0, 72, TimelineRulerHeight), Color(177, 177, 177), font);
            }
        }
        var layers = owner.Snapshot.Manifest.Layers; var tracks = timeline.Tracks.ToDictionary(track => track.LayerId);
        int first = (int)timelineVertical.Value, count = (int)Math.Ceiling((height - TimelineRulerHeight) / TimelineTrackHeight);
        for (int row = first; row < Math.Min(layers.Count, first + count); row++)
        {
            var layer = layers[layers.Count - row - 1]; double y = TimelineRulerHeight + (row - first) * TimelineTrackHeight;
            bool selected = timelineLayer == layer.Id;
            if (selected) draw.FillRectangle(new Rect(0, y, width, TimelineTrackHeight), Color(43, 53, 66));
            draw.FillRectangle(new Rect(0, y, TimelineNamesWidth, TimelineTrackHeight), selected ? Color(43, 53, 66) : Color(37, 37, 37));
            draw.DrawText((layer.IsGroupOrFalse ? "▸ " : "") + layer.Name, new Rect(12, y, TimelineNamesWidth - 22, TimelineTrackHeight),
                layer.IsVisible ? Color(230, 230, 230) : Color(125, 125, 125), font);
            draw.DrawLine(0, (float)(y + TimelineTrackHeight), (float)width, (float)(y + TimelineTrackHeight), Color(52, 52, 52));
            int clipIn = tracks.GetValueOrDefault(layer.Id)?.InFrame ?? 0, clipOut = tracks.GetValueOrDefault(layer.Id)?.OutFrame ?? timeline.DurationFrames;
            if (timelineDrag is { } drag && drag.Layer == layer.Id && drag.Kind is "in" or "out" or "clip") (clipIn, clipOut) = TimelineDragClip(drag);
            using (draw.CreateLayer(1, new Rect(TimelineNamesWidth, y, width - TimelineNamesWidth, TimelineTrackHeight)))
            {
                double x0 = TimelineX(clipIn), x1 = TimelineX(clipOut);
                draw.FillRoundedRectangle(new Rect(x0, y + 5, Math.Max(2, x1 - x0), TimelineTrackHeight - 10), 3, 3,
                    selected ? Color(62, 111, 151) : Color(53, 77, 98));
                draw.DrawLine((float)x0 + 3, (float)y + 10, (float)x0 + 3, (float)y + 20, Color(135, 177, 203), 2);
                draw.DrawLine((float)x1 - 3, (float)y + 10, (float)x1 - 3, (float)y + 20, Color(135, 177, 203), 2);
                if (tracks.TryGetValue(layer.Id, out var track))
                {
                    foreach (int frame in Enum.GetValues<AnimationProperty>().SelectMany(track.Keys).Select(key => key.Frame).Distinct())
                    {
                        double x = TimelineX(timelineDrag is { Kind: "key" } moving && moving.Layer == layer.Id && moving.Key == frame ? timelineFrame : frame);
                        if (x < TimelineNamesWidth - 8 || x > width + 8) continue;
                        var ink = selected && frame == timelineFrame ? Color(255, 215, 102) : Color(223, 232, 239);
                        float cx = (float)x, cy = (float)(y + TimelineTrackHeight / 2);
                        draw.DrawLine(cx, cy - 4, cx + 4, cy, ink, 2); draw.DrawLine(cx + 4, cy, cx, cy + 4, ink, 2);
                        draw.DrawLine(cx, cy + 4, cx - 4, cy, ink, 2); draw.DrawLine(cx - 4, cy, cx, cy - 4, ink, 2);
                    }
                }
            }
        }
        draw.DrawLine((float)TimelineNamesWidth, 0, (float)TimelineNamesWidth, (float)height, Color(70, 70, 70));
        float playhead = (float)TimelineX(timelineFrame);
        if (playhead >= TimelineNamesWidth && playhead <= width)
        {
            draw.DrawLine(playhead, 0, playhead, (float)height, Color(85, 195, 255), 1.5f);
            draw.FillRectangle(playhead - 4, 0, 8, 7, Color(85, 195, 255));
        }
    }

    private void OnTimelinePressed(object sender, PointerRoutedEventArgs e)
    {
        if (Timeline is not { } timeline || document is not { } owner || timelineExporting) return;
        var point = e.GetCurrentPoint(timelineCanvas); if (!point.Properties.IsLeftButtonPressed) return;
        CommitTimelineFields(); StopTimelinePlayback(); timelineCanvas.Focus(FocusState.Programmatic);
        double x = point.Position.X, y = point.Position.Y;
        if (y < TimelineRulerHeight && x >= TimelineNamesWidth)
        {
            SeekTimeline(TimelineAt(x)); timelineDrag = (Guid.Empty, "seek", 0, 0, 0, timelineFrame, x, e.Pointer.PointerId);
        }
        else
        {
            int row = (int)timelineVertical.Value + (int)((y - TimelineRulerHeight) / TimelineTrackHeight);
            if (row < 0 || row >= owner.LayerCount) return;
            var layer = owner.Snapshot.Manifest.Layers[owner.LayerCount - row - 1]; timelineLayer = layer.Id;
            if (selectedLayerId != layer.Id) SelectEditorLayer(owner, layer.Id);
            if (x < TimelineNamesWidth) { RefreshTimeline(); e.Handled = true; return; }
            var track = timeline.Tracks.FirstOrDefault(track => track.LayerId == layer.Id);
            int start = track?.InFrame ?? 0, end = track?.OutFrame ?? timeline.DurationFrames;
            int? key = track is null ? null : Enum.GetValues<AnimationProperty>().SelectMany(track.Keys).Select(key => key.Frame).Distinct()
                .Where(frame => Math.Abs(TimelineX(frame) - x) <= 7).OrderBy(frame => Math.Abs(TimelineX(frame) - x)).Cast<int?>().FirstOrDefault();
            string kind = key is not null ? "key" : Math.Abs(x - TimelineX(start)) < 7 ? "in" : Math.Abs(x - TimelineX(end)) < 7 ? "out"
                : x >= TimelineX(start) && x < TimelineX(end) ? "clip" : "seek";
            SeekTimeline(key ?? TimelineAt(x));
            timelineDrag = (layer.Id, kind, start, end, key ?? -1, timelineFrame, x, e.Pointer.PointerId);
        }
        timelineCanvas.CapturePointer(e.Pointer); RefreshTimeline(); e.Handled = true;
    }
    private void OnTimelineMoved(object sender, PointerRoutedEventArgs e)
    {
        if (timelineDrag is not { } drag || drag.Pointer != e.Pointer.PointerId) return;
        double x = e.GetCurrentPoint(timelineCanvas).Position.X;
        if (drag.Kind is "seek" or "key") SeekTimeline(TimelineAt(x), stop: false);
        else { timelineDrag = drag with { Anchor = TimelineAt(x) }; timelineCanvas.Invalidate(); }
        e.Handled = true;
    }
    private (int In, int Out) TimelineDragClip((Guid Layer, string Kind, int Start, int End, int Key, int Anchor, double X, uint Pointer) drag)
    {
        int duration = Timeline?.DurationFrames ?? 120;
        if (drag.Kind == "in") return (Math.Clamp(drag.Start + drag.Anchor - TimelineAt(drag.X), 0, drag.End - 1), drag.End);
        if (drag.Kind == "out") return (drag.Start, Math.Clamp(drag.End + drag.Anchor - TimelineAt(drag.X), drag.Start + 1, duration));
        var track = Timeline?.Tracks.FirstOrDefault(track => track.LayerId == drag.Layer);
        var keys = track is null ? [] : Enum.GetValues<AnimationProperty>().SelectMany(track.Keys).Select(key => key.Frame).ToArray();
        int earliest = Math.Min(drag.Start, keys.Length == 0 ? drag.Start : keys.Min());
        int latest = Math.Max(drag.End, keys.Length == 0 ? drag.End : keys.Max() + 1);
        int delta = Math.Clamp(drag.Anchor - TimelineAt(drag.X), -earliest, duration - latest);
        return (drag.Start + delta, drag.End + delta);
    }
    private void OnTimelineReleased(object sender, PointerRoutedEventArgs e)
    {
        if (timelineDrag is not { } drag || document is not { } owner) return;
        timelineDrag = null; timelineCanvas.ReleasePointerCapture(e.Pointer);
        if (drag.Kind == "key" && drag.Key != timelineFrame)
            TimelineChange(() => owner.MoveAnimationKeys(drag.Layer, drag.Key, timelineFrame, Enum.GetValues<AnimationProperty>()));
        else if (drag.Kind is "in" or "out" or "clip")
        {
            var range = TimelineDragClip(drag);
            if (drag.Kind == "clip") TimelineChange(() => owner.ShiftAnimationTrack(drag.Layer, range.In - drag.Start));
            else TimelineChange(() => owner.SetAnimationClip(drag.Layer, range.In, range.Out));
        }
        RefreshTimeline(); e.Handled = true;
    }
    private void OnTimelineCancelled(object sender, PointerRoutedEventArgs e)
    { if (timelineDrag is { } drag && drag.Pointer == e.Pointer.PointerId) { timelineDrag = null; timelineCanvas.Invalidate(); } }
    private void OnTimelineKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Space) { ToggleTimelinePlayback(); e.Handled = true; }
        else if (e.Key == VirtualKey.Left) { SeekTimeline(timelineFrame - 1); e.Handled = true; }
        else if (e.Key == VirtualKey.Right) { SeekTimeline(timelineFrame + 1); e.Handled = true; }
        else if (e.Key == VirtualKey.Home) { SeekTimeline(0); e.Handled = true; }
        else if (e.Key == VirtualKey.End) { SeekTimeline((Timeline?.DurationFrames ?? 1) - 1); e.Handled = true; }
        else if (e.Key == VirtualKey.Delete && CanEditTimeline && document is { } owner && timelineLayer is { } id)
        { TimelineChange(() => owner.RemoveAnimationKeys(id, timelineFrame, Enum.GetValues<AnimationProperty>())); e.Handled = true; }
    }
}
