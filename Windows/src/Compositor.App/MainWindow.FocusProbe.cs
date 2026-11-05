#if DEBUG
using System.Runtime.InteropServices;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private static class FocusProbeNative
    {
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    }

    private static bool ProbeOwnsForeground()
    {
        FocusProbeNative.GetWindowThreadProcessId(FocusProbeNative.GetForegroundWindow(), out uint process);
        return process == Environment.ProcessId;
    }

    private async Task<object> RequireForegroundForFocusProbeAsync(int timeoutMs = 2000)
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        bool wasForeground = ProbeOwnsForeground();
        bool visible = FocusProbeNative.IsWindowVisible(handle);
        bool requestedForeground = false;
        if (!wasForeground)
        {
            Activate();
            // Activate alone can report CodeActivated while another process still
            // owns the foreground. The fixture needs actual keyboard focus.
            requestedForeground = FocusProbeNative.SetForegroundWindow(handle);
        }
        if (!ProbeOwnsForeground() && Environment.GetEnvironmentVariable("COMPOSITOR_PROBE_FOREGROUND") == "1")
        {
            App.Log($"focus probe awaiting foreground pid={Environment.ProcessId} timeoutMs={timeoutMs}");
            // Interactive setup happens before a simulated gesture exists. Keep
            // the original focus assertions and the probe's overall deadline.
            if (timeoutMs == 2000)
                Require(await WaitUntilAsync(ProbeOwnsForeground, 60000), "Interactive focus fixture could not acquire the foreground.");
        }
        Require(await WaitUntilAsync(() => ProbeOwnsForeground()
                && lastActivationState != "Deactivated", timeoutMs),
            $"Focus probe could not establish its foreground precondition: visible={visible}, requested={requestedForeground}, "
            + $"window={handle}, foreground={FocusProbeNative.GetForegroundWindow()}, activations={windowActivationCount}/{lastActivationState}.");
        if (Environment.GetEnvironmentVariable("COMPOSITOR_PROBE_FOREGROUND") == "1")
            App.Log($"focus probe foreground confirmed pid={Environment.ProcessId} timeoutMs={timeoutMs}");
        return new { wasForeground, visible, requestedForeground, foregroundConfirmed = true, activations = windowActivationCount, lastActivationState };
    }
}
#endif
