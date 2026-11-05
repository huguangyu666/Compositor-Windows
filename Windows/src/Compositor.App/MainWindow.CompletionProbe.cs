#if DEBUG
using Compositor.Core;
using Windows.System;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private Action<string>? beforeInteractionCompletionUi;

    private async Task VerifyInteractionCompletionsAsync(Dictionary<string, object> report)
    {
        var failures = new List<string>();
        foreach (string kind in new[] { "pixel", "floating", "distort" })
        {
            var scene = kind == "distort" ? TransformScene() : EditorDocument.Create(64, 48);
            if (kind != "distort") scene.SetSelection(Selection.Rectangle(64, 48, 10, 10, 8, 8));
            OpenDocument(scene); await WaitForFramesAsync();
            if (kind == "pixel")
            {
                ToolCombo.SelectedIndex = (int)CanvasTool.RectSelect; await WaitForFramesAsync();
                Require(TryBeginPixelMove(new(12, 12), VirtualKeyModifiers.Control), "Fault probe could not begin pixel move.");
                MovePixelsTo(new(22, 12), VirtualKeyModifiers.Control); await WaitForFramesAsync();
            }
            else
            {
                if (kind == "floating") { OnFreeTransform(this, new()); await WaitForFramesAsync(); ChangeTransformNumber(TransformField.X, 20); }
                else
                {
                    ToolCombo.SelectedIndex = (int)CanvasTool.Move; await WaitForFramesAsync();
                    var start = CurrentTransformValue!.Point(TransformDrag.Handles[0]);
                    BeginTransformPointer(start, VirtualKeyModifiers.Control); await WaitForFramesAsync();
                    MoveTransformPointer(new(start.X + 5, start.Y + 3), VirtualKeyModifiers.Control); FinishPlacementDrag();
                }
                await WaitForFramesAsync();
            }
            int before = renderPublicationFailureCount;
            bool injected = false;
            beforeInteractionCompletionUi = phase =>
            {
                if (phase != kind + "-publish") return;
                injected = true; beforeInteractionCompletionUi = null;
                throw new InvalidOperationException("Expected completion probe UI failure: " + phase);
            };
            var output = FinishTransformBeforeOutputAsync();
            try
            {
                await WaitForFramesAsync();
                Require(injected && renderPublicationFailureCount == before + 1, "Publication fault did not reach the real UI callback.");
                await output.WaitAsync(TimeSpan.FromSeconds(5));
                Require(output.IsCompletedSuccessfully, "Output is still waiting after " + kind + " cleared its session.");
                Require(scene.CanUndo && pixelMove is null && floatingTransform is null && distortCompletion is null,
                    "Publication fault lost the committed history or retained an edit.");
                AssertUploadedFrame(scene);
                OnUndo(this, new()); await WaitForFramesAsync(); AssertUploadedFrame(scene);
                Require(!scene.CanUndo, "Publication fault opened an extra undo step.");
                report[kind + "PublicationFaultSignalsOutputAndKeepsGpuHistory"] = "passed";
            }
            catch (Exception ex) { failures.Add(ex.Message); report[kind + "PublicationFault"] = ex.Message; }
            finally { beforeInteractionCompletionUi = null; }
        }

        foreach (string kind in new[] { "pixel", "floating" })
        {
            var scene = EditorDocument.Create(64, 48); scene.SetSelection(Selection.Rectangle(64, 48, 10, 10, 8, 8));
            OpenDocument(scene); await WaitForFramesAsync();
            Task<bool> completion;
            if (kind == "pixel")
            {
                ToolCombo.SelectedIndex = (int)CanvasTool.RectSelect; await WaitForFramesAsync();
                Require(TryBeginPixelMove(new(12, 12), VirtualKeyModifiers.Control), "Cancel fault probe could not begin move.");
                completion = pixelMove!.Completion.Task;
            }
            else { OnFreeTransform(this, new()); await WaitForFramesAsync(); completion = floatingTransform!.Completion.Task; }
            bool injected = false;
            beforeInteractionCompletionUi = phase =>
            {
                if (phase != kind + "-cancel") return;
                injected = true; beforeInteractionCompletionUi = null; throw new InvalidOperationException("Expected cancel UI failure.");
            };
            try
            {
                try { if (kind == "pixel") CancelPixelMove(); else DiscardFloatingTransform(); }
                catch (InvalidOperationException) when (injected) { }
                Require(injected && completion.IsCompletedSuccessfully && !await completion, "Cancel fault left " + kind + " completion unresolved.");
                Require(!scene.CanUndo && pixelMove is null && floatingTransform is null, "Failed cancel changed history or retained a session.");
                report[kind + "CancelFaultStillResolvesCompletion"] = "passed";
            }
            catch (Exception ex) { failures.Add(ex.Message); report[kind + "CancelFault"] = ex.Message; }
            finally { beforeInteractionCompletionUi = null; }
        }
        var work = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = WaitForInteractionWorkAsync(work.Task, retired.Task);
        Require(!waiting.IsCompleted, "Interaction wait did not wait for active work.");
        retired.TrySetResult();
        Require(!await waiting.WaitAsync(TimeSpan.FromSeconds(5)) && !work.Task.IsCompleted,
            "Retiring an owner still waited for or completed its worker.");
        work.TrySetResult(); report["retirementWaitDoesNotRequireOrCompleteTheWorker"] = "passed";

        work = new(TaskCreationOptions.RunContinuationsAsynchronously); retired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        waiting = WaitForInteractionWorkAsync(work.Task, retired.Task); work.TrySetResult();
        Require(await waiting && !retired.Task.IsCompleted, "Active work completion retired its document.");
        report["currentOwnerWaitCompletesNormally"] = "passed";
        var fault = Task.FromException(new IOException("Expected active worker failure."));
        try { await WaitForInteractionWorkAsync(fault, retired.Task); throw new InvalidOperationException("Active worker fault was swallowed."); }
        catch (IOException) { }
        retired.TrySetResult(); Require(!await WaitForInteractionWorkAsync(fault, retired.Task), "Retired fault reached a new owner.");
        report["currentWorkerFaultPropagatesAndRetiredFaultIsIgnored"] = "passed";

        // The preceding functional RED produced this state: cleared session, outstanding
        // completion. Keep the orphan synthetic here so this separately checks the wait fence.
        var beforeNudge = pixelNudgeWork; var orphan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = EditorDocument.Create(64, 48); OpenDocument(old); await WaitForFramesAsync();
        pixelNudgeWork = orphan.Task; var outputWaiting = FinishTransformBeforeOutputAsync();
        try
        {
            Require(!outputWaiting.IsCompleted, "Output did not wait for the outstanding nudge.");
            var replacement = EditorDocument.Create(80, 60); OpenDocument(replacement);
            await outputWaiting.WaitAsync(TimeSpan.FromSeconds(5)); await WaitForFramesAsync();
            Require(!old.CanUndo && !replacement.CanUndo && !orphan.Task.IsCompleted && ReferenceEquals(document, replacement),
                "Retired output edited the new owner or fabricated old work completion.");
            AssertUploadedFrame(replacement); report["syntheticOrphanNudgeOutputRetiresWithoutTouchingNewOwner"] = "passed";
        }
        finally { pixelNudgeWork = beforeNudge; orphan.TrySetResult(); await outputWaiting; }
        report["completionFaultsObserved"] = failures.ToArray();
        if (failures.Count > 0) throw new InvalidOperationException(string.Join("; ", failures));
    }
}
#endif
