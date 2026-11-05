#if DEBUG
using Compositor.Core;
using Windows.Storage;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private static Func<ProjectSnapshot, string, IReadOnlySet<string>?, byte[]?, Task>? projectSaveWriteProbe;

    internal async Task RunProjectSaveProbeAsync()
    {
        var report = new Dictionary<string, object>(); var failures = new Dictionary<string, string>();
        var root = Path.Combine(AppContext.BaseDirectory, "project-save-fixtures", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            async Task Check(string name, Func<EditorDocument, string, Task> test)
            {
                projectSaveWriteProbe = null; projectFolderPickerProbe = null;
                var path = Path.Combine(root, name + ".comp"); var owner = EditorDocument.Create(32, 24);
                await ProjectStore.SaveAsync(owner.Snapshot, path); OpenDocument(owner, path); owner.MarkSaved(path);
                owner.RenameLayer(owner.Snapshot.Manifest.Layers[0].Id, "Before save"); await WaitForFramesAsync();
                try { await test(owner, path); report[name] = "passed"; }
                catch (Exception ex) { failures[name] = ex.ToString(); }
            }
            static Task Disk(ProjectSnapshot snapshot, string path, IReadOnlySet<string>? unchanged, byte[]? digest) =>
                ProjectStore.SaveAsync(snapshot, path, unchanged, expectedDiskDigest: digest);

            await Check("saveAsPickerCannotSaveRetiredOwner", async (owner, path) =>
            {
                var picked = new TaskCompletionSource<StorageFolder?>(); var entered = new TaskCompletionSource();
                var target = Path.Combine(root, "picker-target"); Directory.CreateDirectory(target);
                var folder = await StorageFolder.GetFolderFromPathAsync(target);
                projectFolderPickerProbe = () => { entered.TrySetResult(); return picked.Task; };
                var saving = SaveAsNow(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var replacement = EditorDocument.Create(19, 17); OpenDocument(replacement);
                picked.SetResult(folder); bool saved = await saving;
                Require(!saved && ReferenceEquals(document, replacement) && projectPath is null && !Directory.Exists(Path.Combine(target, "Untitled.comp")),
                    "A retired Save As picker saved or rebound the new document.");
            });
            foreach (bool saveAs in new[] { false, true })
            await Check(saveAs ? "lateSaveAsCannotRebindNewOwner" : "lateSaveCannotMarkNewOwnerSaved", async (owner, path) =>
            {
                var entered = new TaskCompletionSource(); var release = new TaskCompletionSource();
                projectSaveWriteProbe = async (snapshot, destination, unchanged, digest) =>
                { entered.TrySetResult(); await release.Task; await Disk(snapshot, destination, unchanged, digest); };
                if (saveAs)
                {
                    var target = Path.Combine(root, "late-saveas"); Directory.CreateDirectory(target);
                    var folder = await StorageFolder.GetFolderFromPathAsync(target);
                    projectFolderPickerProbe = () => Task.FromResult<StorageFolder?>(folder);
                }
                var saving = saveAs ? SaveAsNow() : SaveNow(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var replacement = EditorDocument.Create(19, 17); replacement.RenameLayer(replacement.Snapshot.Manifest.Layers[0].Id, "New unsaved edit");
                OpenDocument(replacement); await WaitForFramesAsync(); const string marker = "new-owner-unsaved"; SetStatus(marker);
                release.TrySetResult(); bool saved = await saving;
                Require(!saved && ReferenceEquals(document, replacement) && replacement.IsDirty && projectPath is null && StatusText.Text == marker,
                    "An old save changed the new owner's dirty mark, destination or status.");
            });
            await Check("saveCapturesMetadataAndPreservesLaterEdits", async (owner, path) =>
            {
                var entered = new TaskCompletionSource(); var release = new TaskCompletionSource(); string? capturedName = null;
                projectSaveWriteProbe = async (snapshot, destination, unchanged, digest) =>
                { entered.TrySetResult(); await release.Task; capturedName = snapshot.Manifest.Layers[0].Name; await Disk(snapshot, destination, unchanged, digest); };
                var saving = SaveNow(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                owner.RenameLayer(owner.Snapshot.Manifest.Layers[0].Id, "Typed while saving");
                release.TrySetResult(); bool saved = await saving;
                var disk = await ProjectStore.LoadAsync(path);
                Require(!saved && owner.IsDirty && owner.CanUndo && capturedName == "Before save" && disk.Manifest.Layers[0].Name == "Before save",
                    "A save adopted later metadata or marked later edits as saved.");
                projectSaveWriteProbe = null;
                Require(await SaveNow() && !owner.IsDirty, "The next save could not write the later edits.");
                Require((await ProjectStore.LoadAsync(path)).Manifest.Layers[0].Name == "Typed while saving", "The second save lost the later edit.");
            });
            foreach (bool onMask in new[] { false, true })
            await Check(onMask ? "saveFreezesActiveMaskPixels" : "saveFreezesActiveBrushPixels", async (owner, path) =>
            {
                var id = owner.Snapshot.Manifest.Layers[0].Id;
                if (onMask)
                {
                    var mask = new MaskSurface(32, 24); mask.Pixels.AsSpan().Fill(255);
                    Require(owner.AddRasterMask(id, mask), "The mask fixture could not add its mask.");
                }
                var key = new object(); Require(owner.BeginStroke(key, id, new BrushSettings(3, 1, 1, 0, 0, 0), new(5, 12), onMask, onMask), "The active stroke fixture refused its first point.");
                var entered = new TaskCompletionSource(); var release = new TaskCompletionSource(); bool stable = false;
                projectSaveWriteProbe = async (snapshot, destination, unchanged, digest) =>
                {
                    var bytes = (onMask ? snapshot.Masks[id].Pixels : snapshot.Images[id].Pixels).ToArray();
                    entered.TrySetResult(); await release.Task;
                    stable = bytes.AsSpan().SequenceEqual(onMask ? snapshot.Masks[id].Pixels : snapshot.Images[id].Pixels);
                    await Disk(snapshot, destination, unchanged, digest);
                };
                var saving = SaveNow(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                owner.ContinueStroke(key, new(24, 12)); release.TrySetResult(); bool saved = await saving; owner.EndStroke(key);
                Require(stable && !saved && owner.IsDirty, "A live stroke changed the in-flight save pixels or was marked saved.");
            });
            await Check("saveRequestsWriteSerially", async (owner, path) =>
            {
                var entered = new TaskCompletionSource(); var secondEntered = new TaskCompletionSource();
                var firstRelease = new TaskCompletionSource(); var secondRelease = new TaskCompletionSource(); int calls = 0;
                projectSaveWriteProbe = async (snapshot, destination, unchanged, digest) =>
                {
                    int call = ++calls; if (call == 1) { entered.TrySetResult(); await firstRelease.Task; }
                    else { secondEntered.TrySetResult(); await secondRelease.Task; }
                    await Disk(snapshot, destination, unchanged, digest);
                };
                var first = SaveNow(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                owner.RenameLayer(owner.Snapshot.Manifest.Layers[0].Id, "Second save"); var second = SaveNow();
                await Task.Delay(80); bool serialized = calls == 1;
                firstRelease.TrySetResult(); await first; await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                secondRelease.TrySetResult(); bool saved = await second;
                Require(serialized && saved && !owner.IsDirty && (await ProjectStore.LoadAsync(path)).Manifest.Layers[0].Name == "Second save",
                    "Save requests overlapped or the latest save did not keep its contents.");
            });
            await Check("obsoleteSaveFailureDoesNotChangeNewStatus", async (owner, path) =>
            {
                var entered = new TaskCompletionSource(); var release = new TaskCompletionSource();
                projectSaveWriteProbe = async (_, _, _, _) => { entered.TrySetResult(); await release.Task; throw new IOException("expected obsolete save fault"); };
                var saving = SaveNow(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var replacement = EditorDocument.Create(19, 17); OpenDocument(replacement); await WaitForFramesAsync();
                const string marker = "new-owner-after-old-fault"; SetStatus(marker); release.TrySetResult(); bool saved = await saving;
                Require(!saved && ReferenceEquals(document, replacement) && StatusText.Text == marker, "An old save fault polluted the new document.");
            });
            await Check("cancelledSaveAsDoesNotWriteOrRebind", async (owner, path) =>
            {
                int writes = 0; projectFolderPickerProbe = () => Task.FromResult<StorageFolder?>(null);
                projectSaveWriteProbe = (_, _, _, _) => { writes++; return Task.CompletedTask; };
                Require(!await SaveAsNow() && writes == 0 && ReferenceEquals(document, owner) && owner.IsDirty && projectPath == path,
                    "Cancelling Save As wrote data or changed the current destination.");
            });
            await Check("saveAsKeepsLaterEditsAndNextSaveUsesNewDestination", async (owner, path) =>
            {
                var target = Path.Combine(root, "saveas-newer-edits"); Directory.CreateDirectory(target);
                var folder = await StorageFolder.GetFolderFromPathAsync(target);
                projectFolderPickerProbe = () => Task.FromResult<StorageFolder?>(folder);
                var entered = new TaskCompletionSource(); var release = new TaskCompletionSource();
                projectSaveWriteProbe = async (snapshot, destination, unchanged, digest) =>
                { entered.TrySetResult(); await release.Task; await Disk(snapshot, destination, unchanged, digest); };
                var saving = SaveAsNow(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                owner.RenameLayer(owner.Snapshot.Manifest.Layers[0].Id, "After Save As"); release.TrySetResult();
                bool saved = await saving; var destination = Path.Combine(target, "Untitled.comp");
                Require(!saved && owner.IsDirty && projectPath == destination && (await ProjectStore.LoadAsync(destination)).Manifest.Layers[0].Name == "Before save",
                    "Save As did not preserve later edits and bind its completed destination.");
                projectSaveWriteProbe = null;
                Require(await SaveNow() && !owner.IsDirty && (await ProjectStore.LoadAsync(destination)).Manifest.Layers[0].Name == "After Save As",
                    "The next save did not write the newer edit to the Save As destination.");
            });
            await Check("newTextDraftSurvivesSaveAndPreventsCloseAcknowledgement", async (owner, path) =>
            {
                var entered = new TaskCompletionSource(); var release = new TaskCompletionSource();
                projectSaveWriteProbe = async (snapshot, destination, unchanged, digest) =>
                { entered.TrySetResult(); await release.Task; await Disk(snapshot, destination, unchanged, digest); };
                var saving = SaveNow(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Require(owner.BeginTextDraft(null, new(2, 3), null, new LayerTextStyle { Content = "Typed during save" }), "The new text draft fixture failed.");
                var draft = owner.ActiveTextDraft; release.TrySetResult(); bool saved = await saving;
                Require(!saved && draft is not null && ReferenceEquals(owner.ActiveTextDraft, draft),
                    "Save completion discarded a newer text draft or acknowledged it as saved.");
                owner.CancelTextDraft();
            });
            report["caseFailures"] = failures;
            Require(failures.Count == 0, $"Save regressions failed: {string.Join(", ", failures.Keys)}.");
            report["status"] = "passed";
        }
        catch (Exception ex) { report["status"] = "failed"; report["error"] = ex.ToString(); App.Log($"Project save probe failed: {ex}", LogLevel.Error); }
        finally
        {
            projectSaveWriteProbe = null; projectFolderPickerProbe = null;
            OpenDocument(EditorDocument.Create(16, 12)); await WaitForFramesAsync();
            var prefix = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "project-save-fixtures")) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(root).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Directory.Exists(root)) Directory.Delete(root, true);
            WriteProbeReport("project-save-probe.json", report); forceClose = true; Close();
        }
    }
}
#endif
