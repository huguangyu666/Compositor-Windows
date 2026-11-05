#if DEBUG
using Compositor.Core;
using Microsoft.UI.Xaml.Controls;
using System.Diagnostics;
using System.Text.Json;

namespace Compositor.App;

public sealed partial class MainWindow
{
    /// <summary>Opt-in, programmatic WinUI measurement on a synthetic unsaved document.
    /// It exercises the real refresh/selection handlers and layout, then closes only this
    /// process. Pointer, focus, visual and cross-platform acceptance remain separate.</summary>
    internal async Task RunPanelPerformanceProbeAsync()
    {
        var report = new Dictionary<string, object>();
        try
        {
            const int side = 1024, count = 1000;
            var scene = EditorDocument.Create(side, side);
            var image = new Surface(1, 1, [60, 90, 120, 255], 4);
            var images = (Dictionary<Guid, Surface>)scene.Snapshot.Images;
            var first = scene.Snapshot.Manifest.Layers[0]; images[first.Id] = image;
            for (int i = 1; i < count; i++)
            {
                var layer = new ProjectLayerRecord { Id = Guid.NewGuid(), Name = $"Layer {i}", IsVisible = i < 4,
                    ImageFile = "image.png", Transform = new() { Size = new(side, side) } };
                scene.Snapshot.Manifest.Layers.Add(layer); images[layer.Id] = image;
            }
            scene.Snapshot.Manifest.ActiveLayerId = scene.Snapshot.Manifest.Layers[^1].Id;
            var targets = new[] { scene.AddAdjustment(AdjustmentKind.Levels).Id, scene.AddAdjustment(AdjustmentKind.Levels).Id };
            scene.Snapshot.Manifest.ActiveLayerId = targets[0];
            var timer = Stopwatch.StartNew(); OpenDocument(scene); LayerList.UpdateLayout();
            report["initialOpenAndLayoutMs"] = timer.Elapsed.TotalMilliseconds;
            await WaitForReady();
            await levelsHistogramCache.GetAsync(LevelsHistogramInput.Capture(scene.Snapshot, targets[1]));
            LayerList.SelectedItem = layerRows[targets[1]]; await WaitForReady();
            LayerList.SelectedItem = layerRows[targets[0]]; await WaitForReady();
            var originalRow = layerRows[first.Id];

            report["warmRefreshAndLayout"] = Measure(() => { RefreshLayerPanel(); LayerList.UpdateLayout(); });
            int switchIndex = 0;
            report["cachedLevelsSelectionAndLayout"] = Measure(() =>
            {
                LayerList.SelectedItem = layerRows[targets[++switchIndex % 2]];
                LayerList.UpdateLayout();
            });
            await WaitForReady();
            var levelsPanel = (StackPanel)AdjustmentPanel.Children[1];
            var channel = (ComboBox)levelsPanel.Children[0];
            channel.SelectedIndex = 3; // Previously bins[4] threw here.
            levelsPanel.UpdateLayout();
            var histogram = (Canvas)((Viewbox)levelsPanel.Children[1]).Child;
            if (histogram.Children.Count != 1) throw new InvalidOperationException("Blue histogram did not draw one path.");
            if (!ReferenceEquals(originalRow, layerRows[first.Id])) throw new InvalidOperationException("Stable row was rebuilt.");
            scene.RenameLayer(first.Id, "Renamed"); scene.Undo();
            var row = (LayerRow)layerRows[first.Id].Tag;
            if (!ReferenceEquals(row.Layer, scene.Snapshot.Manifest.Layers[0]))
                throw new InvalidOperationException("Row handlers retained the record from before Undo.");
            report["rowCount"] = layerItems.Count;
            report["blueChannelAndUndoBinding"] = "passed";
            report["status"] = "passed";

            async Task WaitForReady()
            {
                var wait = Stopwatch.StartNew();
                while (wait.Elapsed < TimeSpan.FromSeconds(30))
                {
                    if (AdjustmentPanel.Children.Count > 1 && AdjustmentPanel.Children[1] is StackPanel panel
                        && panel.Children.LastOrDefault() is StackPanel autos
                        && autos.Children.OfType<Button>().All(button => button.IsEnabled)) return;
                    await Task.Delay(16);
                }
                throw new TimeoutException("Histogram did not become ready.");
            }

            static object Measure(Action operation)
            {
                for (int i = 0; i < 3; i++) operation();
                var times = new double[31];
                for (int i = 0; i < times.Length; i++)
                { var watch = Stopwatch.StartNew(); operation(); times[i] = watch.Elapsed.TotalMilliseconds; }
                Array.Sort(times);
                return new { medianMs = times[15], p95Ms = times[29], maxMs = times[^1], samples = times.Length };
            }
        }
        catch (Exception ex) { report["status"] = "failed"; report["error"] = ex.ToString(); App.Log($"Panel probe failed: {ex}"); }
        finally
        {
            WriteProbeReport("panel-performance.json", report);
            forceClose = true; Close();
        }
    }
}
#endif
