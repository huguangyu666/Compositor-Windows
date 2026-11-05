#if DEBUG
using System.Diagnostics;
using System.Text.Json;
using Compositor.Core;
using Windows.Graphics;

namespace Compositor.App;

public sealed partial class MainWindow
{
    // DEBUG-only measurement for audit item 7 (GPU texture lifetime). It switches documents at a
    // fixed size and samples private bytes each round, so "held without Dispose" is either a
    // measured step or it is not; the finalizer pass is sampled separately on purpose.
    internal async Task RunLifetimeProbeAsync()
    {
        var report = new Dictionary<string, object>();
        try
        {
            await Task.Delay(400);
            AppWindow.Resize(new SizeInt32(1100, 760));
            const int side = 1600; // a 1600x1600 B8G8R8A8 target is 10 MiB
            OpenDocument(EditorDocument.Create(side, side));
            await WaitForFramesAsync(); await WaitForFramesAsync();

            var raw = new List<double>();
            var collected = new List<double>();
            var disposedAfterSwitch = new List<bool>();
            raw.Add(Measure(collect: false));
            collected.Add(Measure(collect: true));
            for (var round = 0; round < 6; round++)
            {
                // The target this document is about to abandon, and whether the switch released it.
                var abandoned = fullTarget;
                OpenDocument(EditorDocument.Create(side, side));
                await WaitForFramesAsync(); await WaitForFramesAsync();
                if (abandoned is not null) disposedAfterSwitch.Add(IsDisposed(abandoned));
                raw.Add(Measure(collect: false));
                collected.Add(Measure(collect: true));
            }

            double Mib(double bytes) => Math.Round(bytes / 1048576d, 2);
            report["side"] = side;
            report["rounds"] = raw.Count - 1;
            report["abandonedTargets"] = disposedAfterSwitch.Count;
            report["abandonedTargetsDisposed"] = disposedAfterSwitch.Count(released => released);
            report["rawMib"] = raw.Select(Mib).ToArray();
            report["afterGarbageCollectionMib"] = collected.Select(Mib).ToArray();
            report["rawGrowthPerRoundMib"] = Mib(raw[^1] - raw[0]) / (raw.Count - 1);
            report["collectedGrowthPerRoundMib"] = Math.Round((collected[^1] - collected[0]) / 1048576d / (collected.Count - 1), 3);
            report["presentedFrameCount"] = presentedFrameCount;
            report["renderFailures"] = renderPublicationFailureCount + renderInputFailureCount;
            report["status"] = "passed";
        }
        catch (Exception ex)
        {
            report["status"] = "failed";
            report["error"] = ex.ToString();
            App.Log($"Lifetime probe failed: {ex}", LogLevel.Error);
        }
        finally
        {
            WriteProbeReport("lifetime-probe.json", report);
            forceClose = true;
            Close();
        }
    }

    private static bool IsDisposed(Microsoft.Graphics.Canvas.CanvasRenderTarget target)
    {
        // A released Win2D resource throws on any access, which is the deterministic signal here.
        try
        {
            _ = target.Size;
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    private static double Measure(bool collect)
    {
        var process = System.Diagnostics.Process.GetCurrentProcess();
        if (collect)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        return (double)process.PrivateMemorySize64;
    }
}
#endif
