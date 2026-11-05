#if DEBUG
using System.Diagnostics;
using Compositor.Core;
using Windows.Graphics;

namespace Compositor.App;

public sealed partial class MainWindow
{
    /// <summary>
    /// Measurement-only probe: what a 4K brush drag and a big selection actually cost, split into the stages the
    /// renderer already times (input, snapshot preparation, composition, BGRA, upload) plus the wait a user feels
    /// (accepted point → the frame that shows it). Nothing here asserts a speed; it reports distributions.
    /// </summary>
    internal async Task RunPaintPerfProbeAsync()
    {
        var report = new Dictionary<string, object>();
        var started = Stopwatch.StartNew();
        const int points = 31, strokes = 5;
        try
        {
            await Task.Delay(400);
            AppWindow.Resize(new SizeInt32(1180, 820));
            if (Environment.GetEnvironmentVariable("COMPOSITOR_PAINT_FIRST_ONLY") == "1")
            {
                report["scope"] = "first-dab-only";
                report["brush4096Rich"] = await PaintPerfBrushAsync(4096, 0, 3, burst: false, rich: true);
                report["brush4096RichSampled"] = "passed";
            }
            else
            {
                foreach (int side in new[] { 1024, 4096 })
                {
                    report[$"brush{side}Paced"] = await PaintPerfBrushAsync(side, points, strokes, burst: false);
                    report["brush" + side + "PacedSampled"] = "passed";
                    report[$"brush{side}Burst"] = await PaintPerfBrushAsync(side, points, strokes, burst: true);
                    report["brush" + side + "BurstSampled"] = "passed";
                    report[$"brush{side}Rich"] = await PaintPerfBrushAsync(side, points, strokes, burst: false, rich: true);
                    report["brush" + side + "RichSampled"] = "passed";
                }
                report["brushMaskedShadow4096"] = await PaintPerfBrushAsync(4096, points, strokes, burst: false, maskedShadow: true);
                report["brushMaskedShadow4096Sampled"] = "passed";
                report["selection4096"] = await PaintPerfSelectionAsync(4096);
                report["selection4096Sampled"] = "passed";
            }
            report["totalSeconds"] = Math.Round(started.Elapsed.TotalSeconds, 2);
            report["status"] = "passed";
        }
        catch (Exception ex)
        {
            report["status"] = "failed";
            report["error"] = ex.ToString();
            App.Log($"Paint perf probe failed: {ex}", LogLevel.Error);
        }
        finally
        {
            WriteProbeReport("paint-perf.json", report);
            if (!windowClosed) { forceClose = true; Close(); }
        }
    }

    // Shadow blur tops out at 500 in the model; side / 8 asked for 512 at 4096 and the setter refused it
    // silently, which is how one round of this probe ended up measuring a plain layer.
    private static double EffectBlur(int side) => Math.Min(500, side / 8);

    private async Task<object> PaintPerfBrushAsync(int side, int points, int strokes, bool burst, bool maskedShadow = false, bool rich = false)
    {
        long region0 = LayerEffectsRenderer.RenderRegionBytes, full0 = LayerEffectsRenderer.RenderBytes, update0 = LayerEffectsRenderer.UpdateBytes;
        double window0 = LayerEffectsRenderer.DebugWindowMs, blur0 = LayerEffectsRenderer.DebugBlurMs;
        int blurCalls0 = LayerEffectsRenderer.DebugBlurCalls;
        var scene = EditorDocument.Create(side, side);
        var target = scene.Snapshot.Manifest.Layers[0].Id;
        if (maskedShadow)
        {
            scene.AddHideAllMask(target);
            scene.SetLayerEffects(target, new LayerEffects { Shadow = new() { Blur = EffectBlur(side), Distance = 20 } });
            Require(scene.Snapshot.Manifest.Layers.Single(layer => layer.Id == target).Effects is not null,
                $"the masked scenario at {side}px has no shadow to measure.");
            await WaitForFramesAsync();
        }
        if (rich)
        {
            // The stress matrix's document, stage for stage: a shape layer, a curves adjustment, a hidden mask
            // and a big shadow on the painted layer, then a feathered selection left active. That combination
            // costs 881 ms per stroke at 3840 while a plain one costs single digits; this splits it.
            scene.AddShapeLayer(new ShapeStyle { Kind = ShapeKind.Rectangle, Red = .2, Green = .4, Blue = .8 },
                side / 4, side / 4, new PointD(side / 8, side / 8));
            scene.AddAdjustment(AdjustmentKind.Curves);
            scene.AddHideAllMask(target);
            scene.SetLayerEffects(target, new LayerEffects { Shadow = new() { Blur = EffectBlur(side), Distance = 20 } });
            Require(scene.Snapshot.Manifest.Layers.Single(layer => layer.Id == target).Effects is not null,
                $"the {side}px scenario lost its shadow: SetLayerEffects refused the parameters, so this group would measure a plain layer.");
            scene.SetSelection(Selection.Rectangle(side, side, side / 4, side / 4, side / 2, side / 2).Feathered(side / 32));
            await WaitForSelectionReadyAsync();
            scene.SetActiveLayer(target);
            await WaitForFramesAsync();
        }
        OpenDocument(scene); await WaitForFramesAsync();
        ToolCombo.SelectedIndex = (int)CanvasTool.Brush;
        Require(await WaitUntilAsync(() => tool == CanvasTool.Brush, 2000), "the brush tool never landed before the measurement.");
        BrushSizeSlider.Value = 64; BrushHardnessSlider.Value = 50; BrushOpacitySlider.Value = 100;
        await WaitForFramesAsync();

        var waits = new List<double>(); var inputs = new List<double>(); var preparations = new List<double>();
        var compositions = new List<double>(); var bgras = new List<double>(); var uploads = new List<double>();
        var cpuTotal = new List<double>(); var workerBytes = new List<long>(); var bandArea = new List<double>();
        var uiCost = new List<double>(); var firstFrame = new List<double>(); var settle = new List<double>();
        var firstStages = new List<object>();
        int frames = 0;

        var fromDoc = new PointD(side * .2, side * .5);
        for (int stroke = 0; stroke < strokes; stroke++)
        {
            var from = viewport.ToView(fromDoc);
            var stamp = Stopwatch.GetTimestamp();
            int gen2Before = GC.CollectionCount(2);
            int featherBuilds = scene.ActiveSelection?.DebugFeatherBuilds ?? 0;
            BeginPointerStroke(from);
            double requestMs = Stopwatch.GetElapsedTime(stamp).TotalMilliseconds;
            int beforeStart = presentedFrameCount;
            if (burst)
            {
                for (int i = 1; i <= points; i++)
                {
                    var point = viewport.ToView(new PointD(fromDoc.X + side * .6 * i / points, fromDoc.Y));
                    AppendPointerStroke(point);
                }
                FinishPointerStroke(viewport.ToView(new PointD(fromDoc.X + side * .6, fromDoc.Y)));
                Require(await WaitUntilAsync(() => !HasPendingInteraction && !HasPendingDirty, 20000),
                    "the coalesced drag never went quiet, so its settle time cannot be reported.");
                await WaitForFramesAsync();
                settle.Add((Stopwatch.GetTimestamp() - stamp) * 1000d / Stopwatch.Frequency);
                frames += presentedFrameCount - beforeStart;
                continue;
            }
            Require(await WaitUntilAsync(() => presentedFrameCount > beforeStart, 10000), "the opening point of the stroke published no frame.");
            double firstReadyMs = Stopwatch.GetElapsedTime(stamp).TotalMilliseconds;
            firstFrame.Add(firstReadyMs);
            var first = lastFrameProbe;
            firstStages.Add(new { stroke, readyMs = firstReadyMs, requestMs, first.InputMs, first.PreparationMs,
                first.CompositionMs, first.BgraMs, first.UploadMs, first.CpuMs, first.InputBytes, first.WorkerBytes,
                first.Band, gen2Collections = GC.CollectionCount(2) - gen2Before,
                scene.DebugStrokeCloneMs, scene.DebugStrokeReplaceMs, scene.DebugStrokeDepositMs, scene.DebugStrokePaintMs, scene.DebugStrokeNotifyMs,
                featherMs = scene.ActiveSelection is { } current && current.DebugFeatherBuilds > featherBuilds ? current.DebugFeatherBuildMs : 0 });
            for (int i = 1; i <= points; i++)
            {
                var point = viewport.ToView(new PointD(fromDoc.X + side * .6 * i / points, fromDoc.Y));
                int before = presentedFrameCount;
                var pointStamp = Stopwatch.GetTimestamp();
                AppendPointerStroke(point);
                uiCost.Add((Stopwatch.GetTimestamp() - pointStamp) * 1000d / Stopwatch.Frequency);
                Require(await WaitUntilAsync(() => presentedFrameCount > before, 10000),
                    $"an accepted brush point at side {side} published no frame within 10 s.");
                frames++;
                waits.Add(frameWaitMs); inputs.Add(lastFrameProbe.InputMs); preparations.Add(lastFrameProbe.PreparationMs);
                compositions.Add(lastFrameProbe.CompositionMs);
                bgras.Add(lastFrameProbe.BgraMs); uploads.Add(lastFrameProbe.UploadMs);
                cpuTotal.Add(lastFrameProbe.CpuMs); workerBytes.Add(lastFrameProbe.WorkerBytes);
                bandArea.Add((double)lastFrameProbe.Band.Width * lastFrameProbe.Band.Height);
            }
            var finishStamp = Stopwatch.GetTimestamp();
            int beforeTail = presentedFrameCount;
            FinishPointerStroke(viewport.ToView(new PointD(fromDoc.X + side * .6, fromDoc.Y)));
            Require(await WaitUntilAsync(() => !HasPendingInteraction && !HasPendingDirty, 20000), "the stroke tail never went quiet.");
            await WaitForFramesAsync();
            settle.Add((Stopwatch.GetTimestamp() - finishStamp) * 1000d / Stopwatch.Frequency);
            frames += presentedFrameCount - beforeTail;
        }

        double waitSum = waits.Sum();
        return new
        {
            side, maskedShadow, burst, strokes, points, frames,
            pointWaitMs = PaintPerfStats(waits),
            inputMs = PaintPerfStats(inputs),
            preparationMs = PaintPerfStats(preparations),
            compositionMs = PaintPerfStats(compositions),
            bgraMs = PaintPerfStats(bgras),
            uploadMs = PaintPerfStats(uploads),
            renderTotalMs = PaintPerfStats(cpuTotal),
            uiAcceptMs = PaintPerfStats(uiCost),
            firstStrokeFrameMs = PaintPerfStats(firstFrame),
            firstStrokeStages = firstStages,
            settleMs = PaintPerfStats(settle),
            effectsMsPerFrame = frames == 0 ? null : new {
                renderWindow = Math.Round((LayerEffectsRenderer.DebugWindowMs - window0) / frames, 2),
                gaussianBlur = Math.Round((LayerEffectsRenderer.DebugBlurMs - blur0) / frames, 2),
                blurCallsPerFrame = Math.Round((LayerEffectsRenderer.DebugBlurCalls - blurCalls0) / (double)frames, 2),
            },
            effectsBytesPerFrameMiB = frames == 0 ? null : new {
                renderRegion = Math.Round((LayerEffectsRenderer.RenderRegionBytes - region0) / 1048576d / frames, 2),
                renderFull = Math.Round((LayerEffectsRenderer.RenderBytes - full0) / 1048576d / frames, 2),
                updateIncremental = Math.Round((LayerEffectsRenderer.UpdateBytes - update0) / 1048576d / frames, 2),
            },
            workerBytesPerFrameMax = workerBytes.Count == 0 ? 0 : workerBytes.Max(),
            workerBytesTotalMib = Math.Round(workerBytes.Sum() / 1048576d, 2),
            bandPixelsMedian = MedianOf(bandArea),
            shareOfWait = waitSum == 0 ? null : new
            {
                input = Math.Round(inputs.Sum() / waitSum * 100, 1),
                preparation = Math.Round(preparations.Sum() / waitSum * 100, 1),
                composition = Math.Round(compositions.Sum() / waitSum * 100, 1),
                bgra = Math.Round(bgras.Sum() / waitSum * 100, 1),
                upload = Math.Round(uploads.Sum() / waitSum * 100, 1),
            },
        };
    }

    private async Task<object> PaintPerfSelectionAsync(int side)
    {
        var scene = EditorDocument.Create(side, side);
        OpenDocument(scene); await WaitForFramesAsync();
        var build = new List<double>(); var publish = new List<double>();
        for (int round = 0; round < 5; round++)
        {
            var stamp = Stopwatch.GetTimestamp();
            var ellipse = Selection.Ellipse(side, side, side / 2d, side / 2d, side * .3, side * .25, antialiased: true).Feathered(side / 32);
            build.Add((Stopwatch.GetTimestamp() - stamp) * 1000d / Stopwatch.Frequency);
            stamp = Stopwatch.GetTimestamp();
            scene.SetSelection(ellipse);
            await WaitForSelectionReadyAsync(); await WaitForFramesAsync();
            publish.Add((Stopwatch.GetTimestamp() - stamp) * 1000d / Stopwatch.Frequency);
        }
        return new { side, rounds = build.Count, featheredEllipseBuildMs = PaintPerfStats(build),
            publishWaitMs = PaintPerfStats(publish) };
    }

    private static double MedianOf(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;
        var ordered = values.Order().ToArray();
        return Math.Round(ordered[ordered.Length / 2], 1);
    }

    private static object PaintPerfStats(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return new { count = 0 };
        var ordered = values.Order().ToArray();
        return new { count = ordered.Length, median = Math.Round(ordered[ordered.Length / 2], 2),
            p95 = Math.Round(ordered[Math.Min(ordered.Length - 1, (int)Math.Ceiling(ordered.Length * .95) - 1)], 2),
            max = Math.Round(ordered[^1], 2), mean = Math.Round(ordered.Average(), 2) };
    }
}
#endif
