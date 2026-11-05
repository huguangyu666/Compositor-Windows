#if DEBUG
using System.Diagnostics;
using Compositor.Core;
using Windows.Graphics;

namespace Compositor.App;

public sealed partial class MainWindow
{
    /// <summary>
    /// A01 follow-up on the E05 finding: after a 4K document is handed off to a 64×64 one, private bytes do
    /// not return to the small-document baseline. This splits that residue into parts a claim can be made
    /// about: what the managed heap still holds after a forced full collection (measured against the managed
    /// baseline, so the runtime's own footprint is not charged to the document), what is left outside the
    /// managed heap, and whether the switch released the render target the abandoned document made. The last
    /// block runs twelve alternating rounds, which is what tells "one document's buffers are retained" from
    /// "a pool that warmed up once": a pool plateaus, a retention keeps a slope. Two claims are made on the
    /// managed heap — the per-size residue plateau and the soak's own floor — because, as the note where
    /// they are read records, a plateau measured against each round's own baseline is blind to a hold that
    /// accumulates, and only the floor sees that one.
    /// </summary>
    internal async Task RunRetentionProbeAsync()
    {
        var report = new Dictionary<string, object>();
        double Mib(double bytes) => Math.Round(bytes / 1048576d, 2);

        // One document's full working shape, opened, used, then handed back to a 64×64.
        async Task<Dictionary<string, object>> RunOneSize(int side, int round)
        {
            OpenDocument(EditorDocument.Create(64, 64));
            await WaitForFramesAsync(); await WaitForFramesAsync();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            double baseline = Measure(collect: true);
            long baselineManaged = GC.GetTotalMemory(forceFullCollection: true);

            var scene = EditorDocument.Create(side, side);
            var baseLayer = scene.Snapshot.Manifest.Layers[0];
            OpenDocument(scene);
            await WaitForFramesAsync();
            scene.AddShapeLayer(new ShapeStyle { Kind = ShapeKind.Rectangle, Red = .2, Green = .4, Blue = .8 },
                side / 4, side / 4, new PointD(side / 8, side / 8));
            scene.AddAdjustment(AdjustmentKind.Curves);
            scene.AddHideAllMask(baseLayer.Id);
            scene.SetLayerEffects(baseLayer.Id, new LayerEffects { Shadow = new() { Blur = side / 8, Distance = 20 } });
            scene.SetSelection(Selection.Rectangle(side, side, side / 4, side / 4, side / 2, side / 2).Feathered(side / 32));
            await WaitForSelectionReadyAsync();
            ToolCombo.SelectedIndex = (int)CanvasTool.Brush;
            var from = viewport.ToView(new PointD(side * .3, side * .5));
            var to = viewport.ToView(new PointD(side * .7, side * .5));
            BeginPointerStroke(from); AppendPointerStroke(to); FinishPointerStroke(to);
            var imported = new Surface(side, side);
            imported.Pixels.AsSpan().Fill(255);
            scene.ImportImage(imported, "Retention plate");
            await WaitForFramesAsync(); await WaitForFramesAsync();
            double peak = Measure(collect: false);

            // Hold the object this document is about to abandon, so the disposal claim names one object
            // rather than a count, and read it back before a frame can be drawn: the upload pass makes a
            // target of the new document's own size whenever the field is null or the size differs, so a
            // sample taken after the frames waits would read "released" for a build that only released it
            // there. Sampling at the switch is what makes this claim fire on the one missing line.
            var abandoned = fullTarget;
            OpenDocument(EditorDocument.Create(64, 64));
            bool releasedAtSwitch = abandoned is null || IsDisposed(abandoned);
            bool clearedAtSwitch = abandoned is null || !ReferenceEquals(fullTarget, abandoned);
            await WaitForFramesAsync(); await WaitForFramesAsync();
            double residueAfterCollect = Measure(collect: true);
            long managedAfter = GC.GetTotalMemory(forceFullCollection: true);
            // A plain forced collect leaves large-object segments committed even when nothing in them is
            // live. Compacting tells "the runtime is holding returned-to-nothing segments" apart from
            // "something native is still allocated", which the private-bytes number cannot do by itself.
            GC.Collect(int.MaxValue, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(int.MaxValue, GCCollectionMode.Forced, blocking: true, compacting: true);
            double residueAfterCompaction = Measure(collect: false);

            double residueGrowth = residueAfterCollect - baseline;
            double compactedGrowth = residueAfterCompaction - baseline;
            double managedGrowth = managedAfter - baselineManaged;
            return new Dictionary<string, object>
            {
                ["side"] = side,
                ["round"] = round,
                ["baselineMib"] = Mib(baseline),
                ["baselineManagedMib"] = Mib(baselineManaged),
                ["peakMib"] = Mib(peak),
                ["residueAfterCollectionMib"] = Mib(residueAfterCollect),
                ["residueAfterCompactionMib"] = Mib(residueAfterCompaction),
                ["residueGrowthOverBaselineMib"] = Mib(residueGrowth),
                ["residueGrowthAfterCompactionMib"] = Mib(compactedGrowth),
                ["managedGrowthMib"] = Mib(managedGrowth),
                // Private bytes are process commit. What the managed heap does not account for is native —
                // Direct2D/WIC/driver-side allocations, or heap segments the runtime keeps instead of
                // returning to the OS. This probe names the split; it does not identify the allocator.
                ["residueOutsideManagedHeapMib"] = Mib(residueGrowth - managedGrowth),
                ["abandonedRenderTargetReleasedAtSwitch"] = releasedAtSwitch,
                ["abandonedRenderTargetClearedAtSwitch"] = clearedAtSwitch,
            };
        }

        var rows = new List<object>();
        var rounds = new List<Dictionary<string, object>>();
        try
        {
            await Task.Delay(400);
            AppWindow.Resize(new SizeInt32(1180, 820));
            foreach (int side in new[] { 1024, 2048, 3840 }) rows.Add(await RunOneSize(side, 0));
            // Twelve rounds, alternating the size: same-size rounds cannot see a hold that only attaches when
            // a document of another size arrives (the large-object buckets are per size, which is exactly where
            // a stale render target would hide), and twelve rounds is a session rather than a demo.
            for (var round = 1; round <= 12; round++) rounds.Add(await RunOneSize(round % 2 == 0 ? 1024 : 2048, round));

            // #30's conclusion, written as something a regression can break. Private bytes are process commit
            // the runtime keeps rather than returns, and they oscillated -33.5 to +94.4 MiB across six rounds,
            // so they stay reported and ungated — a number another process can move is B08's shape. The gate
            // sits on the managed figures, and its threshold is set by what it has to catch: one abandoned
            // document's pixels are a 16 MiB byte[] (2048×2048×4), so a single leaked buffer per round
            // breaches 4 MiB by the third.
            // The residue is gated per size, because alternating the rounds is what showed it to be a per-size
            // steady state and not a constant: a round that used 2048 hands back ~61 MiB of managed pixels, a
            // round that used 1024 hands back ~15 MiB, each figure the same in every round it appears.
            foreach (int side in rounds.Select(row => Convert.ToInt32(row["side"])).Distinct())
            {
                var series = rounds.Where(row => Equals(row["side"], side))
                    .Select(row => Convert.ToDouble(row["managedGrowthMib"])).ToArray();
                double spread = series.Max() - series.Min();
                Require(spread < 4, $"the managed residue for {side}×{side} documents is not a plateau: "
                    + string.Join(", ", series.Select(value => value.ToString("0.0")))
                    + $" MiB over {series.Length} rounds, a spread of {spread:0.0} MiB.");
                Require(series.Length >= 4, $"the soak only made {series.Length} rounds at {side}×{side}, "
                    + "which is not enough to tell a plateau from a slope.");
                report[$"managedGrowth{side}SpreadMib"] = Math.Round(spread, 2);
                report[$"managedResiduePlateau{side}"] = "passed";
            }
            // Each round measures its residue against its own baseline, which turns out to make the plateau
            // above blind to the classic leak: a buffer held forever lifts every later baseline by exactly as
            // much as it lifts the residue. Holding one imported Surface per round (ret-redA) left the residue
            // at 61.02 MiB in all six 2048 rounds and the run green. So the soak gates the floor as well —
            // the managed level each round starts from — over its last half, where a one-time warmup has
            // settled and a per-document hold has had six rounds to show a slope.
            var floors = rounds.Select(row => Convert.ToDouble(row["baselineManagedMib"])).ToArray();
            double floorDrift = floors[^1] - floors[floors.Length / 2];
            Require(Math.Abs(floorDrift) < 4, $"the soak's managed floor moved {floorDrift:0.0} MiB over its last "
                + $"{floors.Length - floors.Length / 2} of {floors.Length} rounds: "
                + string.Join(", ", floors.Select(value => value.ToString("0.0"))) + " MiB.");
            report["managedFloorDriftMib"] = Math.Round(floorDrift, 2);
            report["managedFloorFlatInSoakTail"] = "passed";
            // Each gate is also a named group, so the suite's group count is the number of claims this probe
            // still makes: an assertion quietly deleted from the source drops the count and the run, while
            // status=passed on its own would not have noticed.
            Require(rounds.All(row => Equals(row["abandonedRenderTargetReleasedAtSwitch"], true)),
                "a round handed its document back and the switch never released the render target it made for it.");
            report["abandonedRenderTargetsReleasedAtSwitch"] = "passed";
            Require(rounds.All(row => Equals(row["abandonedRenderTargetClearedAtSwitch"], true)),
                "the field that names the abandoned render target still points at it right after the switch.");
            report["abandonedRenderTargetClearedAtSwitch"] = "passed";

            var process = Process.GetCurrentProcess();
            report["rows"] = rows;
            report["soakRounds"] = rounds;
            report["peakWorkingSetMib"] = Mib(process.PeakWorkingSet64);
            report["renderFailures"] = renderPublicationFailureCount + renderInputFailureCount;
            report["status"] = "passed";
        }
        catch (Exception ex)
        {
            report["status"] = "failed";
            report["error"] = ex.ToString();
            report["rows"] = rows;
            report["soakRounds"] = rounds;
            App.Log($"Retention probe failed: {ex}", LogLevel.Error);
        }
        finally
        {
            WriteProbeReport("retention-probe.json", report);
            if (!windowClosed) { forceClose = true; Close(); }
        }
    }
}
#endif
