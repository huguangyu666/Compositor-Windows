#if DEBUG
using System.Diagnostics;
using Compositor.Core;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task MeasureSelectionClearAsync(Dictionary<string, object> report)
    {
        var scene = EditorDocument.Create(4096, 4096); OpenDocument(scene); await WaitForFramesAsync();
        var id = scene.Snapshot.Manifest.Layers[0].Id; var original = scene.Snapshot.Images[id];
        var selection = Selection.Rectangle(4096, 4096, 512, 512, 3072, 3072);
        scene.SetSelection(selection);
        if (!await WaitUntilAsync(() => OutlineIsReady, 5000)) throw new TimeoutException("Clear fixture outline did not become ready.");
        await WaitForFramesAsync();
        var timer = DispatcherQueue.CreateTimer(); timer.Interval = TimeSpan.FromMilliseconds(16);
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heartbeat = Stopwatch.StartNew(); double last = 0, gap = 0; int ticks = 0;
        timer.Tick += (_, _) =>
        {
            double now = heartbeat.Elapsed.TotalMilliseconds;
            gap = Math.Max(gap, now - last); last = now; ticks++; first.TrySetResult();
        };
        timer.Start();
        try
        {
            await first.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var watch = Stopwatch.StartNew();
            var work = DeletePlaneTargetAsync(clearSelection: true);
            double dispatchMs = watch.Elapsed.TotalMilliseconds;
            if (!await work) throw new InvalidOperationException("The 4K clear did not apply.");
            await WaitForFramesAsync();
            double readyMs = watch.Elapsed.TotalMilliseconds;
            await Task.Delay(40);
            report["clear4K"] = new { dispatchMs, readyMs, uiHeartbeatMaxGapMs = gap, uiHeartbeatTicks = ticks };
        }
        finally { timer.Stop(); }
        int center = (2048 * original.Width + 2048) * 4;
        if (scene.Snapshot.Images[id].Pixels[center + 3] != 0 || original.Pixels[center + 3] != 255
            || scene.Snapshot.Images[id].Pixels[3] != 255 || !ReferenceEquals(selection, scene.ActiveSelection))
            throw new InvalidOperationException("Clear changed its source, selection or pixels outside the rectangle.");
        if (fullTarget!.GetPixelBytes(2048, 2048, 1, 1).Any(value => value != 0)
            || fullTarget.GetPixelBytes(0, 0, 1, 1).Any(value => value != 255))
            throw new InvalidOperationException("The 4K clear GPU spot checks do not match.");
        OnUndo(this, new()); await WaitForFramesAsync();
        if (scene.CanUndo || !ReferenceEquals(original, scene.Snapshot.Images[id])) throw new InvalidOperationException("Clear did not undo once.");
        OnRedo(this, new()); await WaitForFramesAsync();
        if (scene.Snapshot.Images[id].Pixels[center + 3] != 0) throw new InvalidOperationException("Clear did not redo.");
        report["selectionClear4KSingleSample"] = "passed";
    }
}
#endif
