#if DEBUG
using System.Text.Json;
using Compositor.Core;

namespace Compositor.App;

public sealed partial class MainWindow
{
    // DEBUG-only replacement for the unsaved-changes dialog: the recovery probe has to make the act
    // of asking itself fault, which no amount of clicking on a real dialog can do.
    private Func<Task<bool>>? closeConfirmProbe;

    /// <summary>
    /// Verifies in the real process that a fault inside the close confirmation cannot strand an
    /// unsaved document: the window stays open with a visible reason and keeps rendering, and the
    /// next close request still closes. That last step is measured from outside — the report is
    /// written before the request, so a regression shows up as a driver timeout instead of a pass.
    /// </summary>
    internal async Task RunRecoveryProbeAsync()
    {
        var report = new Dictionary<string, object>();
        var token = $"recoveryprobe{Environment.ProcessId}";
        try
        {
            await Task.Delay(200);
            var scene = EditorDocument.Create(64, 48); OpenDocument(scene); await WaitForFramesAsync();
            OnFreeTransform(this, new()); await WaitForFramesAsync();
            ChangeTransformNumber(TransformField.X, 6); FinishTransformDraft(); await WaitForFramesAsync();
            Require(scene.IsDirty && scene.CanUndo, "Recovery fixture did not leave an unsaved edit.");
            var frames = presentedFrameCount;
            var priorStatus = StatusText.Text;

            App.Log($"{token} before faulting close request", LogLevel.Diagnostic);
            closeConfirmProbe = () => throw new InvalidOperationException($"{token} expected close confirm fault");
            _ = PostMessage(new UIntPtr(AppWindow.Id.Value), WmClose, IntPtr.Zero, IntPtr.Zero);
            var reported = await WaitUntilAsync(() => StatusText.Text != priorStatus, 5000);
            report["faultStatus"] = StatusText.Text;
            Require(reported, "The faulting close confirmation gave no visible reason.");
            Require(!windowClosed && !forceClose, "The faulting close request took the window down.");
            Require(scene.IsDirty && scene.CanUndo && transformInspectorEdit is null, "The faulting close lost the document or its history.");
            // An idle editor presents no frames, so "still working" is shown by editing again: the
            // swallowed fault may not leave the window alive but unable to commit or undo.
            OnFreeTransform(this, new()); await WaitForFramesAsync();
            ChangeTransformNumber(TransformField.X, 9); FinishTransformDraft(); await WaitForFramesAsync();
            Require(presentedFrameCount > frames, "The editor stopped presenting frames after the faulting close.");
            Require(Math.Abs(scene.Snapshot.Manifest.Layers[0].Transform.Origin.X - 9) < 0.001, "The edit after the faulting close did not commit.");
            OnUndo(this, new()); await WaitForFramesAsync();
            Require(Math.Abs(scene.Snapshot.Manifest.Layers[0].Transform.Origin.X - 6) < 0.001 && scene.CanUndo,
                "The edit after the faulting close did not undo back to the first one.");
            report["closeConfirmFaultKeepsWindowOpen"] = "passed";
            report["status"] = "passed";
            WriteProbeReport("recovery-probe.json", report);

            App.Log($"{token} before second close request", LogLevel.Diagnostic);
            closeConfirmProbe = () => Task.FromResult(true);
            _ = PostMessage(new UIntPtr(AppWindow.Id.Value), WmClose, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            report["status"] = "failed"; report["error"] = ex.ToString();
            App.Log($"Recovery probe failed: {ex}", LogLevel.Error);
            WriteProbeReport("recovery-probe.json", report);
            forceClose = true; Close();
        }
    }
}
#endif
