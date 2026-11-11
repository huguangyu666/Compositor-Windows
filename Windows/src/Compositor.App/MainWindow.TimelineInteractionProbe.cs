#if DEBUG
using Compositor.Core;
using System.Diagnostics;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private Action<double, long>? timelineRefreshProbe, timelineDrawProbe;
    private Action<double>? timelinePlayheadGeometryProbe;

    private async Task VerifyImmediateTimelinePointerAsync(Dictionary<string, object> report)
    {
        double originalScale = timelinePixelsPerFrame, originalScroll = timelineHorizontal.Value;
        double observed = double.NaN, maxError = 0;
        timelinePlayheadGeometryProbe = x => observed = x;
        try
        {
            timelinePixelsPerFrame = 30; RefreshTimelineScroll(); timelineHorizontal.Value = 20;
            SeekTimeline(24); timelineCanvas.Invalidate(); await WaitForFramesAsync();
            Require(await WaitUntilAsync(() => double.IsFinite(observed), 2000), "The playhead did not appear before pointer verification.");
            timelineDrag = (Guid.Empty, "seek", 0, 0, 0, 24, TimelineX(24), 1u);
            for (int i = 1; i <= 12; i++)
            {
                double x = TimelineX(24) + i * .02 * timelinePixelsPerFrame;
                MoveTimelineDrag(x); maxError = Math.Max(maxError, Math.Abs(observed - x));
            }
            report["subframePointerGeometry"] = new { requests = 12, maxErrorPixels = maxError, sampledFrame = timelineFrame };
            Require(maxError < .01 && timelineFrame == 24,
                "The time pointer waited for frame snapping/rendering instead of following subframe mouse movement.");
            report["pointerFollowsInputBeforeFrameRendering"] = "passed";
        }
        finally
        {
            timelinePlayheadGeometryProbe = null; CancelTimelineDrag(); timelinePixelsPerFrame = originalScale;
            RefreshTimelineScroll(); timelineHorizontal.Value = originalScroll;
        }
    }

    private async Task MeasureTimelineUiAsync(Dictionary<string, object> report)
    {
        var refresh = new List<double>(); var draws = new List<double>(); var bursts = new List<double>();
        long refreshBytes = 0, drawBytes = 0;
        timelineRefreshProbe = (ms, bytes) => { refresh.Add(ms); refreshBytes += bytes; };
        timelineDrawProbe = (ms, bytes) => { draws.Add(ms); drawBytes += bytes; };
        static object Stats(List<double> values, long bytes = 0) => new { count = values.Count,
            medianMs = values.Count == 0 ? 0 : values.Order().ElementAt(values.Count / 2),
            maxMs = values.Count == 0 ? 0 : values.Max(), totalMs = values.Sum(), allocatedBytes = bytes };
        try
        {
            for (int i = 0; i < 60; i++)
            {
                long start = Stopwatch.GetTimestamp();
                for (int input = 0; input < 16; input++) SeekTimeline((i * 3) % Timeline!.DurationFrames, stop: false);
                bursts.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                await Task.Delay(8);
            }
            await WaitForFramesAsync(); await Task.Delay(80);
            var seek = new { requests = 960, bursts = Stats(bursts), refresh = Stats(refresh, refreshBytes), draw = Stats(draws, drawBytes) };
            refresh.Clear(); draws.Clear(); refreshBytes = drawBytes = 0;
            for (int i = 0; i < 40; i++)
            {
                timelineVertical.Value = i % Math.Max(1, (int)timelineVertical.Maximum + 1);
                timelineCanvas.Invalidate(); await Task.Delay(8);
            }
            report["timelineUiMeasurements"] = new { seek, scrolling = new { refresh = Stats(refresh, refreshBytes), draw = Stats(draws, drawBytes) } };
        }
        finally { timelineRefreshProbe = timelineDrawProbe = null; timelineVertical.Value = 0; }
    }

    private async Task VerifyIdleTimelineInputAsync(Dictionary<string, object> report)
    {
        SeekTimeline(24); await WaitForFramesAsync(); await Task.Delay(120);
        int redraws = 0; timelineDrawProbe = (_, _) => redraws++;
        try
        {
            for (int i = 0; i < 12; i++)
            {
                SeekTimeline(24, stop: false); await Task.Delay(8);
            }
            report["sameFramePointerInput"] = new { requests = 12, redraws };
            Require(redraws == 0, "Pointer movement within the current frame repainted the unchanged timeline.");
            report["sameFrameInputDoesNotRepaint"] = "passed";
        }
        finally { timelineDrawProbe = null; }
    }

    private async Task VerifyTimelineDragInputAsync(Dictionary<string, object> report)
    {
        var owner = document!; var layer = timelineLayer!.Value;
        var history = owner.CurrentHistoryId; string source = ManifestJson.Serialize(owner.Snapshot.Manifest);
        timelineDrag = (Guid.Empty, "seek", 0, 0, 0, timelineFrame, TimelineX(timelineFrame), 1u);
        for (int frame = 25; frame <= 40; frame++) QueueTimelineDrag(frame);
        Require(await WaitUntilAsync(() => timelineFrame == 40, 2000), "Queued pointer input did not present its latest frame.");
        QueueTimelineDrag(41); CompleteTimelineDrag(); await WaitForFramesAsync();
        Require(timelineFrame == 41 && owner.CurrentHistoryId == history && source == ManifestJson.Serialize(owner.Snapshot.Manifest),
            "Releasing a scrub lost its final position or changed the source/history.");
        report["coalescedScrubAndExactRelease"] = "passed";

        SeekTimeline(48);
        double grab = TimelineX(48) + 5;
        timelineDrag = (layer, "key", 0, 120, 48, 48, grab, 1u);
        Require(TimelineDragAt(grab) == 48, "Grabbing a key off center changed its frame.");
        QueueTimelineDrag(TimelineDragAt(grab + 12 * timelinePixelsPerFrame)); CompleteTimelineDrag();
        Require(Timeline!.Tracks.Single().PositionX[1].Frame == 60, "Releasing a key before the next display tick lost its final frame.");
        OnUndo(this, new Microsoft.UI.Xaml.RoutedEventArgs()); await WaitForFramesAsync();
        Require(owner.CurrentHistoryId == history && Timeline!.Tracks.Single().PositionX[1].Frame == 48,
            "A key drag did not undo in one step.");

        TimelineChange(() => owner.SetAnimationClip(layer, 12, 96));
        var clipHistory = owner.CurrentHistoryId;
        timelineDrag = (layer, "clip", 12, 96, -1, 30, TimelineX(30), 1u);
        QueueTimelineDrag(42); CompleteTimelineDrag();
        Require(Timeline!.Tracks.Single().InFrame == 24 && Timeline.Tracks.Single().OutFrame == 108
            && Timeline.Tracks.Single().PositionX[1].Frame == 60, "Clip release did not move its range and keys together.");
        OnUndo(this, new Microsoft.UI.Xaml.RoutedEventArgs()); await WaitForFramesAsync();
        Require(owner.CurrentHistoryId == clipHistory && Timeline!.Tracks.Single().InFrame == 12,
            "A clip drag did not undo in one step.");
        OnUndo(this, new Microsoft.UI.Xaml.RoutedEventArgs()); await WaitForFramesAsync();
        Require(owner.CurrentHistoryId == history && source == ManifestJson.Serialize(owner.Snapshot.Manifest),
            "Drag verification failed to restore the source.");
        report["keyAndClipReleaseHaveSingleUndo"] = "passed";

        int cancelledFrame = timelineFrame;
        timelineDrag = (Guid.Empty, "seek", 0, 0, 0, timelineFrame, TimelineX(timelineFrame), 1u);
        QueueTimelineDrag(80); CancelTimelineDrag(); await Task.Delay(60);
        Require(timelineFrame == cancelledFrame && !timelineDragFrameQueued, "Cancelled pointer input updated the playhead later.");
        report["cancelledDragRetiresQueuedInput"] = "passed";
    }

    private async Task MeasureTimelineInteractionAsync(Dictionary<string, object> report)
    {
        string? path = Environment.GetEnvironmentVariable("COMPOSITOR_TIMELINE_PROBE_PROJECT");
        if (string.IsNullOrEmpty(path)) return;
        var scene = new EditorDocument(await ProjectStore.LoadAsync(path));
        OpenDocument(scene); await WaitForFramesAsync();
        string source = ManifestJson.Serialize(scene.Snapshot.Manifest); var history = scene.CurrentHistoryId;
        await MeasureTimelineUiAsync(report);
        var inputs = new List<double>(); var frames = new List<FrameProbe>();
        int gpuFrames = 0, start = presentedFrameCount;
        frameTimelineProbe = stage =>
        {
            if (stage != "pixels-uploaded") return;
            frames.Add(lastFrameProbe); if (lastFrameUsedGpu) gpuFrames++;
        };
        static double Median(IEnumerable<double> values)
        { var sorted = values.Order().ToArray(); return sorted.Length == 0 ? 0 : sorted[sorted.Length / 2]; }
        try
        {
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < 90; i++)
            {
                var input = Stopwatch.StartNew(); SeekTimeline((120 + i * 3) % Timeline!.DurationFrames);
                inputs.Add(input.Elapsed.TotalMilliseconds); await Task.Delay(8);
            }
            int during = presentedFrameCount - start; double scrubMs = clock.Elapsed.TotalMilliseconds;
            await WaitForFramesAsync();
            var scrub = new { requests = inputs.Count, publishedDuringInput = during, elapsedMs = scrubMs,
                inputMedianMs = Median(inputs), inputMaxMs = inputs.Max(), renderMedianMs = Median(frames.Select(f => f.CpuMs)),
                uploadMedianMs = Median(frames.Select(f => f.UploadMs)), gpuFrames, completedFrames = frames.Count };
            frames.Clear(); gpuFrames = 0; SeekTimeline(120); await WaitForFramesAsync();
            start = presentedFrameCount; clock.Restart(); ToggleTimelinePlayback();
            await Task.Delay(2200); StopTimelinePlayback();
            double playbackMs = clock.Elapsed.TotalMilliseconds; int playbackFrames = presentedFrameCount - start;
            await WaitForFramesAsync();
            report["timelineInteractionMeasurements"] = new { path, scene.LayerCount, width = scene.Snapshot.Manifest.Width,
                height = scene.Snapshot.Manifest.Height, fps = Timeline!.FramesPerSecond, scrub,
                playback = new { publishedFrames = playbackFrames, elapsedMs = playbackMs,
                    publishedFramesPerSecond = playbackFrames * 1000 / playbackMs,
                    renderMedianMs = Median(frames.Select(f => f.CpuMs)), uploadMedianMs = Median(frames.Select(f => f.UploadMs)), gpuFrames } };
            Require(source == ManifestJson.Serialize(scene.Snapshot.Manifest) && history == scene.CurrentHistoryId,
                "Timeline measurement changed the user's saved scene or its history.");
        }
        finally { frameTimelineProbe = null; StopTimelinePlayback(); }
    }

    private async Task VerifyTimelineScrubbingAsync(Dictionary<string, object> report)
    {
        await WaitForFramesAsync();
        using var firstRelease = new ManualResetEventSlim(); using var latestRelease = new ManualResetEventSlim();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var latestEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int passes = 0, before = presentedFrameCount;
        beforeFrameRender = () =>
        {
            bool first = Interlocked.Increment(ref passes) == 1;
            (first ? firstEntered : latestEntered).TrySetResult();
            if (!(first ? firstRelease : latestRelease).Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Held timeline frame did not resume.");
        };
        try
        {
            SeekTimeline(10); await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            SeekTimeline(11); firstRelease.Set();
            await latestEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            report["scrubbingPublication"] = new { publishedBefore = before, publishedWhileLatestPending = presentedFrameCount, timelineFrame };
            Require(presentedFrameCount > before,
                "Seeking discarded a complete animation frame while the next requested frame was still rendering.");
        }
        finally { beforeFrameRender = null; firstRelease.Set(); latestRelease.Set(); await WaitForFramesAsync(); }
        Require(timelineFrame == 11 && presented is not null, "Scrubbing did not settle on the latest requested frame.");
        var expected = new CanvasCompositor(AnimationEvaluator.Evaluate(document!.Snapshot, 11)).Render();
        var actual = presented!.GetPixelBytes();
        for (int p = 0; p < actual.Length; p += 4)
            Require(actual[p] == expected.Pixels[p + 2] && actual[p + 1] == expected.Pixels[p + 1]
                && actual[p + 2] == expected.Pixels[p] && actual[p + 3] == expected.Pixels[p + 3],
                "Scrubbing settled on pixels from an older animation frame.");
        report["scrubbingPublishesDuringInputAndSettlesLatest"] = "passed";

        timelinePreview.IsChecked = false; RefreshTimelineMode(); RenderCanvas(); await WaitForFramesAsync();
        before = presentedFrameCount;
        SeekTimeline(12); SeekTimeline(13); await WaitForFramesAsync();
        Require(presentedFrameCount == before, "Seeking with animation preview disabled rendered the unchanged base canvas.");
        report["disabledPreviewSeekingDoesNotRenderBaseCanvas"] = "passed";
        timelinePreview.IsChecked = true; RefreshTimelineMode(); SeekTimeline(24); RenderCanvas(); await WaitForFramesAsync();
    }
}
#endif
