#if DEBUG
using Compositor.Core;

namespace Compositor.App;

public sealed partial class MainWindow
{
    internal async Task RunDraftExitProbeAsync()
    {
        var report = new Dictionary<string, object>(); var failures = new Dictionary<string, string>();
        var root = Path.Combine(AppContext.BaseDirectory, "draft-exit-fixtures", Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(root, "Opened.comp"); await ProjectStore.SaveAsync(EditorDocument.Create(24, 20).Snapshot, path);
            async Task Check(string name, Func<EditorDocument, Task> test)
            {
                closeConfirmProbe = null; projectSaveWriteProbe = null;
                ResetTabsForProbe();
                var owner = EditorDocument.Create(64, 48); OpenDocument(owner); await WaitForFramesAsync();
                try { await test(owner); report[name] = "passed"; }
                catch (Exception ex) { failures[name] = ex.ToString(); }
            }
            void Draft(EditorDocument owner) => Require(owner.BeginTextDraft(null, new(4, 5), null,
                new LayerTextStyle { Content = "Pending words", FontSize = 8 }), "The text draft fixture was refused.");
            void CloseRequest() => _ = PostMessage(new UIntPtr(AppWindow.Id.Value), WmClose, IntPtr.Zero, IntPtr.Zero);

            await Check("cleanTextDraftParticipatesInWindowClose", async owner =>
            {
                Draft(owner); Require(!owner.IsDirty, "The text draft fixture was already committed.");
                Require(NeedsProjectCloseConfirmation, "A clean document's text draft bypasses window-close confirmation.");
                var asked = new TaskCompletionSource(); int calls = 0;
                closeConfirmProbe = () => { calls++; asked.TrySetResult(); return Task.FromResult(false); };
                CloseRequest(); await asked.Task.WaitAsync(TimeSpan.FromSeconds(5)); await WaitForFramesAsync();
                Require(!windowClosed && !forceClose && calls == 1 && owner.ActiveTextDraft is null && owner.IsDirty
                    && owner.Snapshot.Manifest.Layers.Any(layer => layer.Text?.Content == "Pending words"),
                    "Cancelling close lost the text instead of settling it into undoable content.");
                Require(owner.Undo() && owner.LayerCount == 1 && !owner.CanUndo, "Settling text did not create exactly one undo step.");
            });
            await Check("cleanGradientDraftParticipatesInWindowClose", async owner =>
            {
                ToolCombo.SelectedIndex = (int)CanvasTool.Gradient; await WaitForFramesAsync();
                GradientStyleCombo.SelectedIndex = 1; GradientOpacitySlider.Value = 100;
                BeginGradient(new(4, 24)); MoveGradient(new(60, 24)); EndGradientDrag(); await WaitForFramesAsync();
                Require(gradientEdit is not null && !owner.IsDirty, "The gradient fixture was not an uncommitted draft.");
                Require(NeedsProjectCloseConfirmation, "A clean document's gradient bypasses window-close confirmation.");
                var asked = new TaskCompletionSource();
                closeConfirmProbe = () => { asked.TrySetResult(); return Task.FromResult(false); };
                CloseRequest(); await asked.Task.WaitAsync(TimeSpan.FromSeconds(5)); await WaitForFramesAsync();
                Require(!windowClosed && !forceClose && gradientEdit is null && owner.IsDirty && owner.CanUndo,
                    "Cancelling close dropped the gradient instead of settling its pixels.");
                Require(owner.Undo() && !owner.CanUndo, "Settling the gradient did not create exactly one undo step.");
            });
            await Check("newProjectIsRefusedOverTextDraft", async owner =>
            {
                // With tabs, New never replaces the visible project — the prompt-era flow is gone.
                // The Mac's menu disables New while a draft is open (canStartProjectOperation); this
                // is the same refusal: nothing moves, nothing commits, the draft stays open.
                Draft(owner);
                closeConfirmProbe = () => Task.FromException<bool>(new InvalidOperationException("New asked a discard question"));
                await NewProjectAsync();
                Require(ReferenceEquals(document, owner) && owner.ActiveTextDraft is not null && workspace.Tabs.Count == 1,
                    "New opened a tab or touched the document across an open text draft.");
            });
            await Check("openProjectIsRefusedOverTextDraft", async owner =>
            {
                // The Mac gates the open at the command's door while a draft is open; nothing may
                // replace or prompt around one any more, so the visible document and its draft come
                // out untouched.
                Draft(owner);
                closeConfirmProbe = () => Task.FromException<bool>(new InvalidOperationException("Open asked a discard question"));
                await OpenProjectAsync(path);
                Require(ReferenceEquals(document, owner) && owner.ActiveTextDraft is not null && projectPath is null
                    && workspace.Tabs.Count == 1, "Open replaced or prompted across an open text draft.");
            });
            await Check("repeatedWindowCloseAsksOnlyOnce", async owner =>
            {
                owner.RenameLayer(owner.Snapshot.Manifest.Layers[0].Id, "Unsaved");
                var entered = new TaskCompletionSource(); var answer = new TaskCompletionSource<bool>(); int calls = 0;
                closeConfirmProbe = () => { calls++; entered.TrySetResult(); return answer.Task; };
                CloseRequest(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); CloseRequest(); await Task.Delay(80);
                bool once = calls == 1; answer.TrySetResult(false); await WaitForFramesAsync();
                Require(once && !windowClosed && !forceClose, "Repeated close requests opened overlapping discard decisions.");
            });
            await Check("retiredDiscardDecisionCannotAuthorizeNewDocument", async owner =>
            {
                owner.RenameLayer(owner.Snapshot.Manifest.Layers[0].Id, "Unsaved");
                var entered = new TaskCompletionSource(); var answer = new TaskCompletionSource<bool>();
                closeConfirmProbe = () => { entered.TrySetResult(); return answer.Task; };
                var confirming = ConfirmDiscardChanges(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var replacement = EditorDocument.Create(19, 17); OpenDocument(replacement);
                answer.TrySetResult(true); bool discard = await confirming;
                Require(!discard && ReferenceEquals(document, replacement), "An old confirmation authorized discarding a different document.");
            });
            await Check("discardWaitsForInFlightSave", async owner =>
            {
                var savePath = Path.Combine(root, "Saving.comp"); await ProjectStore.SaveAsync(owner.Snapshot, savePath);
                OpenDocument(owner, savePath); owner.MarkSaved(savePath); owner.RenameLayer(owner.Snapshot.Manifest.Layers[0].Id, "Saving edit");
                var entered = new TaskCompletionSource(); var release = new TaskCompletionSource();
                projectSaveWriteProbe = async (snapshot, destination, unchanged, digest) =>
                { entered.TrySetResult(); await release.Task; await ProjectStore.SaveAsync(snapshot, destination, unchanged, expectedDiskDigest: digest); };
                var saving = SaveNow(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                closeConfirmProbe = () => Task.FromResult(false);
                var confirming = ConfirmDiscardChanges(); await Task.Delay(80); bool waited = !confirming.IsCompleted;
                release.TrySetResult(); await saving; await confirming;
                Require(waited && !windowClosed && ReferenceEquals(document, owner), "Discard confirmation did not wait for the captured disk write.");
            });
            await Check("uncommittableTextDraftPreventsDiscard", async owner =>
            {
                Draft(owner); owner.ActiveTextDraft!.Style.FontSize = double.NaN; int questions = 0;
                closeConfirmProbe = () => { questions++; return Task.FromResult(true); };
                bool discard = await ConfirmDiscardChanges();
                Require(!discard && owner.ActiveTextDraft is not null && questions == 0 && ReferenceEquals(document, owner),
                    "A text draft that could not commit was offered for silent discard.");
            });
            report["caseFailures"] = failures;
            Require(failures.Count == 0, $"Draft exit regressions failed: {string.Join(", ", failures.Keys)}.");
            report["status"] = "passed";
        }
        catch (Exception ex) { report["status"] = "failed"; report["error"] = ex.ToString(); App.Log($"Draft exit probe failed: {ex}", LogLevel.Error); }
        finally
        {
            closeConfirmProbe = null; projectSaveWriteProbe = null;
            OpenDocument(EditorDocument.Create(16, 12)); await WaitForFramesAsync();
            var prefix = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "draft-exit-fixtures")) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(root).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Directory.Exists(root)) Directory.Delete(root, true);
            WriteProbeReport("draft-exit-probe.json", report); forceClose = true; Close();
        }
    }
}
#endif
