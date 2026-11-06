#if DEBUG
using Compositor.Core;
using System.Diagnostics;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task MeasureSelectionMaskCreationAsync(Dictionary<string, object> report)
    {
        var scene = EditorDocument.Create(4096, 4096); OpenDocument(scene); await WaitForFramesAsync();
        var id = scene.Snapshot.Manifest.Layers[0].Id; var original = scene.Snapshot.Images[id];
        var selection = Selection.Rectangle(4096, 4096, 512, 512, 3072, 3072); scene.SetSelection(selection);
        if (!await WaitUntilAsync(() => OutlineIsReady, 5000)) throw new TimeoutException("Mask fixture outline was not ready.");
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
            var work = CreateLayerMaskAsync(revealing: true, useSelection: true);
            double dispatchMs = watch.Elapsed.TotalMilliseconds;
            if (!await work) throw new InvalidOperationException("The 4K selection mask was not created.");
            await WaitForFramesAsync(); double readyMs = watch.Elapsed.TotalMilliseconds;
            await Task.Delay(40);
            report["selectionMask4K"] = new { dispatchMs, readyMs, uiHeartbeatMaxGapMs = gap, uiHeartbeatTicks = ticks };
        }
        finally { timer.Stop(); }
        if (!ReferenceEquals(original, scene.Snapshot.Images[id]) || scene.ActiveSelection is not null || !paintOnMask
            || scene.Snapshot.Masks[id].Pixels[0] != 0 || scene.Snapshot.Masks[id].Pixels[2048 * 4096 + 2048] != 255)
            throw new InvalidOperationException("Selection mask creation changed its source, selection or coverage incorrectly.");
        if (presented!.GetPixelBytes(0, 0, 1, 1).Any(value => value != 0)
            || presented.GetPixelBytes(2048, 2048, 1, 1).Any(value => value != 255))
            throw new InvalidOperationException("Selection mask GPU spot checks differ.");
        OnUndo(this, new()); await WaitForFramesAsync();
        if (scene.CanUndo || scene.Snapshot.Masks.ContainsKey(id) || !ReferenceEquals(selection, scene.ActiveSelection))
            throw new InvalidOperationException("Selection mask creation did not undo once with its selection.");
        OnRedo(this, new()); await WaitForFramesAsync();
        if (!scene.Snapshot.Masks.ContainsKey(id) || scene.ActiveSelection is not null) throw new InvalidOperationException("Selection mask creation did not redo.");
        report["selectionMaskCreationSingleSample"] = "passed";
    }
}
#endif
