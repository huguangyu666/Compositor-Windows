#if DEBUG
using System.Diagnostics;
using System.Text.Json;

namespace Compositor.App;

public sealed partial class MainWindow
{
    // DEBUG-only verification of the logging path inside the real process: the entry point, the
    // bounded backlog, the error-priority rule, a cancelled close and the exit drain. The records
    // themselves are checked by the driver script after this process exits.
    internal async Task RunLoggingProbeAsync()
    {
        var report = new Dictionary<string, object>();
        var token = $"loggingprobe{Environment.ProcessId}";
        try
        {
            await Task.Delay(300);
            report["before"] = AppLogging.StatusLine();
            App.Log($"{token} kept error: canvas job failed for real", LogLevel.Error);
            App.Log($"{token} ordinary diagnostic");

            // A threefold backlog must stay bounded and return to the UI thread. What the contract promises
            // under a full queue is accounting, not arrival: a diagnostic that cannot be queued is shed and
            // counted, an error never is. Requiring every record to arrive instead encoded "the consumer
            // keeps up on this machine", and that is what failed at 07:20 with ~400 MB of physical memory
            // left: 2 018 of 3 072 diagnostics were shed, the backlog peaked at the cap, and no error was
            // lost. Saturation is therefore reported, not required either way.
            var capacity = AppLogging.CapacitySetting;
            var watch = Stopwatch.StartNew();
            for (var index = 0; index < capacity * 3; index++)
                App.Log($"{token} slow frame band=64x64 render+upload={index}.0 ms interactive=False", LogLevel.Diagnostic);
            watch.Stop();
            report["burstRecords"] = capacity * 3;
            report["burstMilliseconds"] = watch.ElapsedMilliseconds;

            await Task.Delay(1500);
            var status = AppLogging.StatusLine();
            report["afterBurst"] = status;
            report["droppedDiagnostic"] = AppLogging.DroppedDiagnosticCount;
            report["droppedError"] = AppLogging.DroppedErrorCount;
            report["queueHighWater"] = AppLogging.QueueHighWaterCount;
            report["written"] = AppLogging.WrittenCount;
            Require(AppLogging.DroppedErrorCount == 0, "slow-frame noise crowded an error out of the backlog.");
            Require(AppLogging.QueueHighWaterCount <= capacity, $"the backlog reached {AppLogging.QueueHighWaterCount} records over a {capacity} cap.");
            Require(watch.ElapsedMilliseconds < 2000, $"the burst of {capacity * 3} records took {watch.ElapsedMilliseconds} ms");
            var shed = AppLogging.DroppedDiagnosticCount;
            Require(AppLogging.EnqueuedCount + shed >= capacity * 3,
                $"the burst vanished instead of being counted: enqueued {AppLogging.EnqueuedCount}, shed {shed}, expected at least {capacity * 3}.");
            report["burstSaturated"] = shed > 0;
            report["burstAccounted"] = true;

            // One close attempt that the user answers with Cancel must not stop the logger. A
            // programmatic Window.Close() is not suppressible here, so the probe posts the same
            // WM_CLOSE the title bar button sends and cancels that request.
            var cancelledOnce = false;
            void CancelOnceHandler(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
            {
                if (cancelledOnce) return;
                cancelledOnce = true;
                args.Cancel = true;
                App.Log($"{token} closing request cancelled", LogLevel.Diagnostic);
            }

            AppWindow.Closing += CancelOnceHandler;
            App.Log($"{token} before user-style close request", LogLevel.Diagnostic);
            _ = PostMessage(new UIntPtr(AppWindow.Id.Value), WmClose, IntPtr.Zero, IntPtr.Zero);
            await Task.Delay(600);
            report["closeCancelled"] = cancelledOnce;
            report["isRunningAfterCancel"] = AppLogging.IsRunning;
            Require(cancelledOnce, "the probe never saw a close request to cancel.");
            Require(AppLogging.IsRunning, "a cancelled close stopped the logger.");
            App.Log($"{token} logged after a cancelled close", LogLevel.Error);
            await Task.Delay(300);

            // The exit path: this record must be on disk once the process has left.
            App.Log($"{token} last record before close", LogLevel.Error);
            report["logDirectory"] = AppLogging.LogDirectory;

            // Deliberately not injecting an unhandled exception here. A throw raised from a
            // DispatcherQueue callback fast-fails the process (0xC0000409) without reaching App's
            // UnhandledException handler, so it would kill this probe rather than exercise it.
            // The global handler's coverage belongs to audit item 5, where that observation is recorded.
            report["status"] = "passed";
        }
        catch (Exception ex)
        {
            report["status"] = "failed";
            report["error"] = ex.ToString();
            App.Log($"Logging probe failed: {ex}", LogLevel.Error);
        }
        finally
        {
            WriteProbeReport("logging-probe.json", report);
            forceClose = true;
            Close();
        }
    }

    // WM_CLOSE: the request the title bar button and Alt+F4 send, which AppWindow.Closing can cancel.
    private const uint WmClose = 0x0010;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessage(UIntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
#endif
