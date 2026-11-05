#if DEBUG
using Compositor.Core;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private static Func<string, CancellationToken, Task<ProjectSnapshot>>? projectOpenLoadProbe;
    private Func<Task<StorageFolder?>>? projectFolderPickerProbe;
    private Action<Exception>? projectOpenFailureProbe;

    internal async Task RunProjectOpenProbeAsync()
    {
        var report = new Dictionary<string, object>(); var failures = new Dictionary<string, string>();
        var root = Path.Combine(AppContext.BaseDirectory, "project-open-fixtures", Guid.NewGuid().ToString("N"));
        var previousPreferences = Environment.GetEnvironmentVariable("COMPOSITOR_PREFERENCES_DIRECTORY");
        Environment.SetEnvironmentVariable("COMPOSITOR_PREFERENCES_DIRECTORY", Path.Combine(root, "preferences"));
        try
        {
            await WaitForFramesAsync();
            var pathA = Path.Combine(root, "A.comp"); var pathB = Path.Combine(root, "B.comp");
            await ProjectStore.SaveAsync(EditorDocument.Create(24, 20).Snapshot, pathA);
            await ProjectStore.SaveAsync(EditorDocument.Create(31, 23).Snapshot, pathB);
            var snapshotA = await ProjectStore.LoadAsync(pathA); var snapshotB = await ProjectStore.LoadAsync(pathB);
            var folderA = await StorageFolder.GetFolderFromPathAsync(pathA);
            async Task Check(string name, Func<EditorDocument, Task> test)
            {
                projectOpenLoadProbe = null; projectFolderPickerProbe = null; projectOpenFailureProbe = null;
                closeConfirmProbe = () => Task.FromResult(true);
                ResetTabsForProbe();
                var owner = EditorDocument.Create(16, 12); owner.SetOpacity(owner.Snapshot.Manifest.Layers[0].Id, .65);
                OpenDocument(owner); await WaitForFramesAsync();
                try { await test(owner); report[name] = "passed"; }
                catch (Exception ex) { failures[name] = ex.ToString(); }
            }

            await Check("recentMenuOpensWithoutReparentingError", async owner =>
            {
                ClearRecentProjects(); RememberRecentProject(pathA);
                RootGrid.UpdateLayout();
                var file = new MenuBarItemAutomationPeer(MenuBarRoot.Items[0]);
                file.Expand(); await Task.Delay(250);
                var recent = (IExpandCollapseProvider)FrameworkElementAutomationPeer.CreatePeerForElement(OpenRecentMenu)
                    .GetPattern(PatternInterface.ExpandCollapse);
                recent.Expand(); await Task.Delay(250);
                Exception? fault = null; projectOpenFailureProbe = error => fault = error;
                try
                {
                    Require(await WaitUntilAsync(() => Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(ClearRecentMenuItem) is not null, 2000),
                        $"The recent menu was not presented: file={file.ExpandCollapseState}, recent={recent.ExpandCollapseState}, loaded={OpenRecentMenu.IsLoaded}, clearLoaded={ClearRecentMenuItem.IsLoaded}.");
                    var entry = OpenRecentMenu.Items.OfType<MenuFlyoutItem>().First();
                    ((IInvokeProvider)new MenuFlyoutItemAutomationPeer(entry).GetPattern(PatternInterface.Invoke)).Invoke();
                    Require(await WaitUntilAsync(() => projectPath == pathA && projectOpenRequest is null, 5000),
                        "The real recent-menu command did not finish opening its project.");
                    await WaitForFramesAsync();
                    Require(lastErrorDetails is null && OpenRecentMenu.Items.Contains(ClearRecentMenuItem)
                        && ClearRecentMenuItem.IsEnabled, $"Opening from the presented menu reported: {fault?.ToString() ?? lastErrorDetails}");
                }
                finally { recent.Collapse(); file.Collapse(); }
            });

            await Check("openMenuLandsOnItsOwnTab", async owner =>
            {
                // The original's open() asks about nothing: each project arrives on its own tab and
                // the dirty one on screen simply keeps its unsaved work (ProjectWorkspace.swift).
                int confirmations = 0;
                closeConfirmProbe = () => { confirmations++; return Task.FromResult(true); };
                projectFolderPickerProbe = () => Task.FromResult<StorageFolder?>(folderA);
                await OpenProjectFromPickerAsync();
                Require(projectPath == pathA && !ReferenceEquals(document, owner) && confirmations == 0,
                    $"The real menu route prompted {confirmations} times or did not open its chosen package.");
                Require(workspace.Tabs.Count == 2 && workspace.Tabs.Any(tab => ReferenceEquals(tab.Document, owner)),
                    "the open did not land on its own tab; the previous project is gone.");
            });
            await Check("cancelledPickerKeepsDraftWithoutPrompt", async owner =>
            {
                int confirmations = 0;
                closeConfirmProbe = () => { confirmations++; return Task.FromResult(true); };
                projectFolderPickerProbe = () => Task.FromResult<StorageFolder?>(null);
                await OpenProjectFromPickerAsync();
                Require(confirmations == 0 && ReferenceEquals(document, owner) && owner.IsDirty && owner.CanUndo,
                    "Cancelling the picker asked to discard or changed the current document.");
            });
            await Check("pickerCannotOpenOverRetiredOwner", async owner =>
            {
                var picked = new TaskCompletionSource<StorageFolder?>();
                projectFolderPickerProbe = () => picked.Task;
                var open = OpenProjectFromPickerAsync();
                var replacement = EditorDocument.Create(19, 17); OpenDocument(replacement);
                picked.SetResult(folderA); await open;
                Require(ReferenceEquals(document, replacement) && projectPath is null, "A retired picker replaced the new document.");
            });
            await Check("decodedProjectCannotReplaceNewOwner", async owner =>
            {
                var decoded = new TaskCompletionSource<ProjectSnapshot>(); var entered = new TaskCompletionSource();
                CancellationToken observed = default;
                projectOpenLoadProbe = (_, token) => { observed = token; entered.TrySetResult(); return decoded.Task; };
                var open = OpenProjectAsync(pathA); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var replacement = EditorDocument.Create(19, 17); OpenDocument(replacement);
                decoded.SetResult(snapshotA); await open;
                Require(ReferenceEquals(document, replacement) && projectPath is null, "A completed old load replaced the new owner.");
                Require(observed.IsCancellationRequested, "Retiring a document did not cancel its project decoder.");
            });
            await Check("latestOpenWinsEvenWhenOldDecoderIgnoresCancellation", async owner =>
            {
                var decoded = new TaskCompletionSource<ProjectSnapshot>(); var entered = new TaskCompletionSource();
                CancellationToken observed = default;
                projectOpenLoadProbe = (path, token) => path == pathA ? Held(token) : Task.FromResult(snapshotB);
                Task<ProjectSnapshot> Held(CancellationToken token) { observed = token; entered.TrySetResult(); return decoded.Task; }
                var first = OpenProjectAsync(pathA); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await OpenProjectAsync(pathB); var replacement = document;
                decoded.SetResult(snapshotA); await first;
                Require(ReferenceEquals(document, replacement) && projectPath == pathB && document?.Snapshot.Manifest.Width == 31,
                    "An older decoder overwrote the later open request.");
                Require(observed.IsCancellationRequested, "The newer open request did not cancel the old decoder.");
            });
            await Check("openDuringEditsIsRefusedWithWorkIntact", async owner =>
            {
                // Under the tab contract nothing replaces the open document, so edits made while a
                // decode is in flight need no confirmation to survive: the open lands on its own tab
                // (the Mac gates only what is mid-flight, and a finished decode of another project
                // never overwrites this one).
                var decoded = new TaskCompletionSource<ProjectSnapshot>(); var entered = new TaskCompletionSource();
                projectOpenLoadProbe = (_, _) => { entered.TrySetResult(); return decoded.Task; };
                var open = OpenProjectAsync(pathA); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var id = owner.Snapshot.Manifest.Layers[0].Id; owner.SetOpacity(id, .45);
                int confirmations = 0;
                closeConfirmProbe = () => { confirmations++; return Task.FromResult(false); };
                decoded.SetResult(snapshotA); await open;
                Require(confirmations == 0 && projectPath == pathA && !ReferenceEquals(document, owner),
                    $"the open prompted ({confirmations}) or did not take its own tab.");
                Require(workspace.Tabs.Any(tab => ReferenceEquals(tab.Document, owner))
                    && Math.Abs((owner.Snapshot.Manifest.Layers[0].Opacity ?? 1) - .45) < .0001,
                    "the tab left behind lost the edits made during the decode.");
            });
            await Check("invalidFolderDoesNotPromptToDiscard", async owner =>
            {
                int confirmations = 0;
                closeConfirmProbe = () => { confirmations++; return Task.FromResult(true); };
                await OpenProjectAsync(Path.Combine(root, "missing.comp"));
                Require(confirmations == 0 && ReferenceEquals(document, owner) && owner.IsDirty,
                    "An invalid package asked to discard the current document.");
            });
            await Check("staleLoadFailureDoesNotOverwriteNewStatus", async owner =>
            {
                var decoded = new TaskCompletionSource<ProjectSnapshot>(); var entered = new TaskCompletionSource();
                projectOpenLoadProbe = (_, _) => { entered.TrySetResult(); return decoded.Task; };
                var open = OpenProjectAsync(pathA); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var replacement = EditorDocument.Create(19, 17); OpenDocument(replacement); await WaitForFramesAsync();
                const string marker = "project-open-new-owner"; SetStatus(marker);
                decoded.SetException(new IOException("project-open expected stale decoder fault")); await open;
                Require(ReferenceEquals(document, replacement) && StatusText.Text == marker,
                    "An obsolete load fault changed the new document or its status.");
            });
            await Check("decodedOpenLandsOnItsTabWithoutPrompting", async owner =>
            {
                // The prompt-era variant of this group asked what the old confirmation used to gate;
                // the tab contract never confirms: a decoded project simply gets its own tab.
                int confirmations = 0;
                closeConfirmProbe = () => { confirmations++; return Task.FromResult(true); };
                projectOpenLoadProbe = (_, _) => Task.FromResult(snapshotA);
                await OpenProjectAsync(pathA);
                Require(confirmations == 0 && projectPath == pathA && ReferenceEquals(document, workspace.Current.Document)
                    && workspace.Tabs.Any(tab => ReferenceEquals(tab.Document, owner)),
                    "a decoded open either prompted or lost the visible project.");
            });
            await Check("decoderObservesRetirementCancellationWithoutError", async owner =>
            {
                var entered = new TaskCompletionSource();
                projectOpenLoadProbe = async (_, token) =>
                {
                    entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return snapshotA;
                };
                var open = OpenProjectAsync(pathA); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var replacement = EditorDocument.Create(19, 17); OpenDocument(replacement); await WaitForFramesAsync();
                const string marker = "project-open-cancelled-cleanly"; SetStatus(marker);
                await open.WaitAsync(TimeSpan.FromSeconds(5));
                Require(ReferenceEquals(document, replacement) && StatusText.Text == marker,
                    "Cancelling the decoder showed an obsolete error or replaced its owner.");
            });
            await Check("currentDecoderFailureKeepsUnsavedDocumentAndReports", async owner =>
            {
                int confirmations = 0;
                closeConfirmProbe = () => { confirmations++; return Task.FromResult(true); };
                projectOpenLoadProbe = (_, _) => Task.FromException<ProjectSnapshot>(new IOException("project-open expected live decoder fault"));
                await OpenProjectAsync(pathA);
                Require(confirmations == 0 && ReferenceEquals(document, owner) && owner.IsDirty && owner.CanUndo && projectPath is null,
                    "A failed decoder discarded work or prompted to discard it.");
                Require(lastErrorDetails?.Contains("project-open expected live decoder fault", StringComparison.Ordinal) == true,
                    "The current decoder's failure was hidden.");
            });
            report["caseFailures"] = failures;
            Require(failures.Count == 0, $"Project-open regressions failed: {string.Join(", ", failures.Keys)}.");
            report["status"] = "passed";
        }
        catch (Exception ex) { report["status"] = "failed"; report["error"] = ex.ToString(); App.Log($"Project open probe failed: {ex}", LogLevel.Error); }
        finally
        {
            Environment.SetEnvironmentVariable("COMPOSITOR_PREFERENCES_DIRECTORY", previousPreferences);
            projectOpenLoadProbe = null; projectFolderPickerProbe = null; projectOpenFailureProbe = null; closeConfirmProbe = null;
            OpenDocument(EditorDocument.Create(16, 12)); await WaitForFramesAsync();
            var prefix = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "project-open-fixtures")) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(root).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Directory.Exists(root)) Directory.Delete(root, true);
            WriteProbeReport("project-open-probe.json", report);
            forceClose = true; Close();
        }
    }
}
#endif
