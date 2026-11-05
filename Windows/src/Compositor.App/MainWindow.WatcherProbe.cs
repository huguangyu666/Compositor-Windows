#if DEBUG
using System.Text.Json;
using Compositor.Core;
using Microsoft.UI.Xaml.Controls;

namespace Compositor.App;

public sealed partial class MainWindow
{
    // DEBUG-only verification of the external-change path (audit item 17): a package that is briefly
    // unreadable has to report itself, and that failure must not leave the watcher deaf to the
    // change that follows it.
    internal async Task RunWatcherProbeAsync()
    {
        var report = new Dictionary<string, object>();
        var root = Path.Combine(Path.GetTempPath(), $"compositor-watcher-probe-{Guid.NewGuid():N}");
        FileStream? lockHandle = null;
        try
        {
            await Task.Delay(300);
            Directory.CreateDirectory(Path.Combine(root, "images"));
            var scene = EditorDocument.Create(8, 6);
            var asset = scene.Snapshot.Manifest.Layers[0].ImageFile ?? throw new InvalidOperationException("The fixture has no asset.");
            var currentAssets = new[] { asset }.ToHashSet(StringComparer.Ordinal);
            await ProjectStore.SaveAsync(scene.Snapshot, root);
            OpenDocument(new EditorDocument(await ProjectStore.LoadAsync(root)), root);
            await WaitForFramesAsync();
            var cleanStatus = StatusText.Text;

            // 1. An external write whose asset another process holds: the reload has to say so on the
            //    UI thread instead of letting the I/O error escape an async void handler.
            lockHandle = File.Open(Path.Combine(root, "images", asset), FileMode.Open, FileAccess.Write, FileShare.None);
            var first = scene.Snapshot;
            first.Manifest.Layers[0].Name = "externally renamed";
            await ProjectStore.SaveAsync(first, root, currentAssets);
            var reported = await WaitUntilAsync(() => StatusText.Text != cleanStatus, 5000);
            report["faultReported"] = reported;
            report["faultStatus"] = StatusText.Text;
            report["documentStillOpen"] = document is not null;
            report["nameAfterFault"] = document?.Snapshot.Manifest.Layers[0].Name ?? string.Empty;

            // 2. Once the volume answers again the next external change must still land.
            lockHandle.Dispose();
            lockHandle = null;
            var second = scene.Snapshot;
            second.Manifest.Layers[0].Name = "second external write";
            await ProjectStore.SaveAsync(second, root, currentAssets);
            var recovered = await WaitUntilAsync(() => document?.Snapshot.Manifest.Layers[0].Name == "second external write", 8000);
            report["recovered"] = recovered;
            report["recoveredStatus"] = StatusText.Text;
            report["watcherStatus"] = watcher?.Status() ?? "no watcher";
            Require(recovered, "a transient read failure left the watcher deaf to the next change.");
            Require(report["faultReported"] is true, "the failed reload was not reported to the user.");

            // 3. Unsaved work is never replaced without asking (the macOS prompt this mirrors). Answering
            //    Revert takes their version; the ask itself has to carry both answers and the project name.
            var asked = new List<string>();
            projectDialogProbe = dialog =>
            {
                asked.Add($"{dialog.Title}|{(dialog.Content as TextBlock)?.Text}|{dialog.PrimaryButtonText}|{dialog.SecondaryButtonText}");
                return Task.FromResult(ContentDialogResult.Primary);
            };
            document!.RenameLayer(document.Snapshot.Manifest.Layers[0].Id, "MineBeforeRevert");
            Require(document.IsDirty, "the revert fixture left no unsaved edit to defend.");
            var theirs = await ProjectStore.LoadAsync(root);
            theirs.Manifest.Layers[0].Name = "TheirsBeforeRevert";
            await ProjectStore.SaveAsync(theirs, root, currentAssets);
            var reverted = await WaitUntilAsync(() => document?.Snapshot.Manifest.Layers[0].Name == "TheirsBeforeRevert", 8000);
            report["askedShape"] = asked.Count == 1 ? asked[0] : $"{asked.Count} asks";
            Require(asked.Count == 1, $"a dirty document with an outside write asked {asked.Count} times instead of once.");
            Require(asked[0].Contains(Loc.T("Dlg_Revert"), StringComparison.Ordinal)
                  && asked[0].Contains(Loc.T("Dlg_KeepMine"), StringComparison.Ordinal)
                  && asked[0].Contains(Loc.T("M_ExternalConflictBody"), StringComparison.Ordinal),
                "the conflict prompt did not offer both answers with the macOS wording.");
            Require(reverted, "answering Revert left the editor holding its own version.");
            Require(document is not null && !document.IsDirty, "the revert reload did not land as a saved document.");

            // 4. "Keep Mine" keeps the unsaved edit and takes their package as the baseline, so the next
            //    deliberate save is allowed to replace it — once, on purpose, not by silence.
            asked.Clear();
            projectDialogProbe = _ => Task.FromResult(ContentDialogResult.Secondary);
            var keptDocument = document!;
            keptDocument.RenameLayer(keptDocument.Snapshot.Manifest.Layers[0].Id, "MineKept");
            var theirsAgain = await ProjectStore.LoadAsync(root);
            theirsAgain.Manifest.Layers[0].Name = "TheirsKeptAway";
            await ProjectStore.SaveAsync(theirsAgain, root, currentAssets);
            var kept = await WaitUntilAsync(() => StatusText.Text == Loc.T("S_ExternalKept"), 8000);
            Require(kept, "answering Keep Mine gave no visible confirmation.");
            Require(document!.Snapshot.Manifest.Layers[0].Name == "MineKept", "Keep Mine reloaded the document anyway.");
            Require(document.IsDirty, "Keep Mine dropped the unsaved mark the window close depends on.");
            var savedAfterKeep = await SaveNow();
            Require(savedAfterKeep, "the deliberate save after Keep Mine was still refused.");
            Require(!File.ReadAllText(Path.Combine(root, "manifest.json")).Contains("TheirsKeptAway", StringComparison.Ordinal),
                "the save after Keep Mine did not replace the outside version.");
            projectDialogProbe = null;

            // 5. A busy editor defers the prompt instead of dropping it (the macOS scheduleRecheck). With
            //    a stroke open the write must not be answered at all, and it must come back by itself once
            //    the stroke closes — "ask again later" that never asks again is the failure this pins.
            asked.Clear();
            projectDialogProbe = _ => { asked.Add("late"); return Task.FromResult(ContentDialogResult.Secondary); };
            var busyDoc = document!;
            var busyLayerId = busyDoc.Snapshot.Manifest.Layers[0].Id;
            busyDoc.RenameLayer(busyLayerId, "MineBusy");
            Require(busyDoc.IsDirty, "the busy fixture left no unsaved edit.");
            LayerList.SelectedItem = layerRows[busyLayerId];
            tool = CanvasTool.Brush;
            BeginPointerStroke(new(4, 4)); AppendPointerStroke(new(6, 5));
            Require(pointerStroke is not null, "the busy fixture did not leave a stroke open.");
            var theirsBusy = await ProjectStore.LoadAsync(root);
            theirsBusy.Manifest.Layers[0].Name = "TheirsDuringStroke";
            await ProjectStore.SaveAsync(theirsBusy, root, currentAssets);
            var deferred = await WaitUntilAsync(() => StatusText.Text == Loc.T("S_ExternalWait"), 8000);
            Require(deferred, "an outside write during an open stroke gave no deferral message.");
            Require(asked.Count == 0, $"the conflict prompt fired {asked.Count} time(s) while a stroke was open.");
            // The same seam the window-activation hook calls has to find the change still pending, and
            // re-presenting it must not turn into an ask while the editor is busy.
            Require(ResumeDeferredExternalChange(), "a pending change could not be re-presented from the activation seam.");
            await Task.Delay(300);
            Require(asked.Count == 0, "re-presenting a change while busy answered it anyway.");
            FinishPointerStroke(new(8, 6)); await WaitForFramesAsync();
            var cameBack = await WaitUntilAsync(() => asked.Count == 1, 15_000);
            report["deferredAskBacklog"] = asked.Count;
            Require(cameBack, "the deferred prompt never came back after the stroke closed.");
            Require(document!.Snapshot.Manifest.Layers[0].Name == "MineBusy", "the late prompt replaced the unsaved work.");
            Require(!ResumeDeferredExternalChange(), "an answered change was still pending on the activation seam.");
            projectDialogProbe = null;

            // 6. A background tab whose package an agent rewrote: the port's single window watcher is
            //    bound to the tab on screen, and it baselines its digest when it attaches - so while the
            //    tab is parked, nothing watches that package. The Mac's per-tab controllers close the
            //    same gap by re-checking when the tab comes forward (resumeExternalChangeCheck,
            //    IO/ProjectController+ExternalChanges.swift:28-30). The port's door for that check is
            //    rebind: the arriving tab's saved digest is still what MarkSaved left, so a digest
            //    mismatch is detectable AT ARRIVAL without a live watcher. This scenario drives the
            //    whole case over that door: a clean parked tab reloads on its own, and a dirty one
            //    asks the moment it lands in front (the ask, not the reload, is what the Mac fronts).
            var firstPath = root;
            var secondScene = EditorDocument.Create(10, 8);
            var secondRoot = Path.Combine(root, "second.comp");
            Directory.CreateDirectory(Path.Combine(secondRoot, "images"));
            await ProjectStore.SaveAsync(secondScene.Snapshot, secondRoot);
            var firstTab = workspace.Current;
            // The probe opened this document through OpenDocument, which bypasses the real open's
            // MarkSaved (LoadAndOpenProjectAsync does it); the baseline has to exist before the
            // scenario, the way a user-opened project carries one.
            firstTab.Document!.MarkSaved(firstTab.ProjectPath);
            ProjectWorkspaceTab secondTab;
            try
            {
                await OpenProjectAsync(secondRoot);
                await WaitForFramesAsync();
                secondTab = workspace.Current;
                Require(secondTab.Id != firstTab.Id && secondTab.ProjectPath == secondRoot,
                    $"opening the second project did not adopt its own tab (path {secondTab.ProjectPath}).");
                // A clean parked tab: an agent writes the background package while the window looks elsewhere.
                var parkedTab = firstTab;
                var parkedDoc = parkedTab.Document!;
                Require(!parkedDoc.IsDirty, "the parked fixture tab carried unsaved work into the clean case.");
                SwitchProjectTab(parkedTab.Id); await WaitForFramesAsync();
                var theirsWhileParked = await ProjectStore.LoadAsync(firstPath);
                theirsWhileParked.Manifest.Layers[0].Name = "RewrittenWhileParked";
                var parkedAssets = new[] { theirsWhileParked.Manifest.Layers[0].ImageFile! }.ToHashSet(StringComparer.Ordinal);
                await ProjectStore.SaveAsync(theirsWhileParked, firstPath, parkedAssets);
                SwitchProjectTab(secondTab.Id); await WaitForFramesAsync();
                SwitchProjectTab(parkedTab.Id); await WaitForFramesAsync();
                var surfaced = await WaitUntilAsync(
                    () => parkedTab.Document?.Snapshot.Manifest.Layers[0].Name == "RewrittenWhileParked", 8000);
                report["parkedCleanWriteSurfacesAtArrival"] = surfaced;
                Require(surfaced, "an agent write to a parked clean package never surfaced at arrival: "
                    + $"the window still shows {parkedTab.Document?.Snapshot.Manifest.Layers[0].Name}.");
                // A dirty parked tab: the write must ASK at arrival, not reload under the unsaved edit.
                asked.Clear();
                projectDialogProbe = _ => { asked.Add("arrival"); return Task.FromResult(ContentDialogResult.Primary); };
                var dirtyTab = secondTab; var dirtyDoc = dirtyTab.Document!;
                dirtyDoc.RenameLayer(dirtyDoc.Snapshot.Manifest.Layers[0].Id, "MineWhileParked");
                Require(dirtyDoc.IsDirty, "the dirty parked fixture left no unsaved edit.");
                SwitchProjectTab(parkedTab.Id); await WaitForFramesAsync();
                var theirsDirty = await ProjectStore.LoadAsync(secondRoot);
                theirsDirty.Manifest.Layers[0].Name = "TheirsWhileParked";
                var dirtyAssets = new[] { theirsDirty.Manifest.Layers[0].ImageFile! }.ToHashSet(StringComparer.Ordinal);
                await ProjectStore.SaveAsync(theirsDirty, secondRoot, dirtyAssets);
                SwitchProjectTab(dirtyTab.Id); await WaitForFramesAsync();
                var askedAtArrival = await WaitUntilAsync(() => asked.Count == 1, 8000);
                report["parkedDirtyWriteAsksAtArrival"] = askedAtArrival;
                Require(askedAtArrival, $"a dirty parked package asked {asked.Count} times at arrival; expected once.");
                var revertedOnArrival = await WaitUntilAsync(
                    () => dirtyTab.Document?.Snapshot.Manifest.Layers[0].Name == "TheirsWhileParked", 8000);
                Require(revertedOnArrival, "answering Revert at arrival did not land on the dirty tab's document.");
                Require(parkedTab.Document?.Snapshot.Manifest.Layers[0].Name == "RewrittenWhileParked",
                    "the second tab's conflict leaked into the first tab's document.");
                projectDialogProbe = null;
            }
            finally
            {
                var extra = workspace.Tabs.FirstOrDefault(t => t.ProjectPath == secondRoot);
                if (extra is not null) workspace.Tabs.Remove(extra);
                if (workspace.Current.Id != firstTab.Id) SwitchProjectTab(firstTab.Id);
                await WaitForFramesAsync();
            }

            report["status"] = "passed";
        }
        catch (Exception ex)
        {
            report["status"] = "failed";
            report["error"] = ex.ToString();
            App.Log($"Watcher probe failed: {ex}", LogLevel.Error);
        }
        finally
        {
            lockHandle?.Dispose();
            WriteProbeReport("watcher-probe.json", report);
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
            catch (IOException)
            {
                // A probe scratch package left behind is noise, not a failure of the behavior under test.
            }

            forceClose = true;
            Close();
        }
    }

    // The watcher posts its change to this thread, so waiting has to yield rather than spin.
    private async Task<bool> WaitUntilAsync(Func<bool> until, int timeoutMilliseconds)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (until()) return true;
            await Task.Delay(50);
        }

        return until();
    }
}
#endif
