#if DEBUG
using System.Diagnostics;
using Compositor.Core;
using Windows.Graphics;

namespace Compositor.App;

public sealed partial class MainWindow
{
    /// <summary>
    /// E05: what a typical working document costs in memory, at 1024 / 2048 / 3840. Every stage is a real
    /// production call (layer, mask, effect, selection, brush stroke, import, save), each one timed and
    /// weighed both as the process stands and after a forced collection — a hot-kernel median says nothing
    /// about whether an afternoon of editing fits in memory. Each size ends by handing the window a 64×64
    /// document and weighing the residue, because that is the only place a "it came back down" claim can
    /// come from. One run on one machine; nothing here is extrapolated to a user's session.
    /// </summary>
    internal async Task RunStressMatrixAsync()
    {
        var report = new Dictionary<string, object>();
        var started = Stopwatch.StartNew();
        var matrix = new List<object>();
        double Mib(double bytes) => Math.Round(bytes / 1048576d, 2);
        var scratch = Path.Combine(Path.GetTempPath(), $"compositor-stress-matrix-{Environment.ProcessId}");
        try
        {
            Directory.CreateDirectory(scratch);
            await Task.Delay(400);
            AppWindow.Resize(new SizeInt32(1180, 820));
            foreach (int side in new[] { 1024, 2048, 3840 })
            {
                var stages = new List<object>();
                int framesBefore = presentedFrameCount;
                var watch = Stopwatch.StartNew();
                void Sample(string stage)
                {
                    watch.Stop();
                    stages.Add(new
                    {
                        stage,
                        ms = Math.Round(watch.Elapsed.TotalMilliseconds, 1),
                        privateMib = Mib(Measure(collect: false)),
                        afterGcMib = Mib(Measure(collect: true)),
                        presentedFrames = presentedFrameCount - framesBefore,
                    });
                    framesBefore = presentedFrameCount;
                    watch.Restart();
                }

                var scene = EditorDocument.Create(side, side);
                OpenDocument(scene);
                await WaitForFramesAsync(); await WaitForFramesAsync();
                Sample("document");

                var baseLayer = scene.Snapshot.Manifest.Layers[0];
                scene.AddShapeLayer(new ShapeStyle { Kind = ShapeKind.Rectangle, Red = .2, Green = .4, Blue = .8 },
                    side / 4, side / 4, new PointD(side / 8, side / 8));
                await WaitForFramesAsync();
                Sample("second layer");
                scene.AddAdjustment(AdjustmentKind.Curves);
                await WaitForFramesAsync();
                Sample("third layer (adjustment)");

                scene.AddHideAllMask(baseLayer.Id);
                await WaitForFramesAsync();
                Sample("mask");
                scene.SetLayerEffects(baseLayer.Id, new LayerEffects { Shadow = new() { Blur = side / 8, Distance = 20 } });
                await WaitForFramesAsync();
                Sample("shadow effect");

                scene.SetSelection(Selection.Rectangle(side, side, side / 4, side / 4, side / 2, side / 2).Feathered(side / 32));
                await WaitForSelectionReadyAsync();
                var established = scene.ActiveSelection;
                Sample("feathered selection");

                // One real brush stroke across the middle of the canvas: the undo history and the touched
                // tiles are the part a kernel benchmark never measures.
                ToolCombo.SelectedIndex = (int)CanvasTool.Brush;
                var strokeFrom = viewport.ToView(new PointD(side * .3, side * .5));
                var strokeTo = viewport.ToView(new PointD(side * .7, side * .5));
                BeginPointerStroke(strokeFrom); AppendPointerStroke(strokeTo); FinishPointerStroke(strokeTo);
                await WaitForFramesAsync();
                Sample("brush stroke");

                var imported = new Surface(side, side);
                imported.Pixels.AsSpan().Fill(255);
                scene.ImportImage(imported, "Imported plate");
                await WaitForFramesAsync();
                Sample("import full canvas");

                var savePath = Path.Combine(scratch, $"{side}.comp");
                await ProjectStore.SaveAsync(scene.Snapshot, savePath);
                Sample("save project");
                await ProjectStore.SaveAsync(scene.Snapshot, savePath);
                Sample("save again");

                // A queued selection job cancelled after it has started must publish nothing: the selection
                // the document already had has to still be the selection.
                QueueSelectionWork((_, _) => { Thread.Sleep(400); return Selection.Rectangle(side, side, 1, 1, 4, 4); }, false, "stress");
                await Task.Delay(120);
                CancelSelectionWork();
                await WaitForFramesAsync();
                Require(ReferenceEquals(established, scene.ActiveSelection),
                    "a cancelled selection worker replaced the published selection.");
                report[$"cancelledSelectionAt{side}KeepsPublishedSelection"] = "passed";
                Sample("cancelled selection worker");

                OpenDocument(EditorDocument.Create(64, 64));
                await WaitForFramesAsync(); await WaitForFramesAsync();
                Sample("handed off to 64x64");

                matrix.Add(new
                {
                    side,
                    mibPerFullLayer = Mib((double)side * side * 4),
                    stages,
                });
            }

            var process = Process.GetCurrentProcess();
            report["matrix"] = matrix;
            report["peakWorkingSetMib"] = Mib(process.PeakWorkingSet64);
            report["renderFailures"] = renderPublicationFailureCount + renderInputFailureCount;
            report["presentedFrameCount"] = presentedFrameCount;
            report["totalSeconds"] = Math.Round(started.Elapsed.TotalSeconds, 2);
            report["status"] = "passed";
        }
        catch (Exception ex)
        {
            report["status"] = "failed";
            report["error"] = ex.ToString();
            App.Log($"Stress matrix failed: {ex}", LogLevel.Error);
        }
        finally
        {
            WriteProbeReport("stress-matrix.json", report);
            try { Directory.Delete(scratch, recursive: true); }
            catch (Exception ex) { App.Log($"Stress matrix scratch cleanup failed for {scratch}: {ex.Message}"); }
            if (!windowClosed) { forceClose = true; Close(); }
        }
    }
}
#endif
