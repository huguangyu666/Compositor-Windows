#if DEBUG
using Compositor.Core;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task VerifySelectionMaskWorkerAsync(Dictionary<string, object> report)
    {
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        static EditorDocument Scene()
        {
            var scene = EditorDocument.Create(16, 16);
            scene.SetSelection(Selection.Rectangle(16, 16, 4, 4, 8, 8).Feathered(1));
            return scene;
        }
        foreach (bool revealing in new[] { true, false })
        {
            var scene = Scene(); OpenDocument(scene); await WaitForFramesAsync();
            var id = scene.Snapshot.Manifest.ActiveLayerId!.Value; var source = scene.Snapshot; var selection = scene.ActiveSelection;
            var reference = new EditorDocument(new ProjectSnapshot { Manifest = ManifestJson.Deserialize(ManifestJson.Serialize(source.Manifest)), Images = source.Images, Masks = source.Masks });
            reference.SetSelection(selection); Require(reference.AddSelectionMask(id, revealing), "The mask control did not change.");
            int uiThread = Environment.CurrentManagedThreadId, computeThread = uiThread;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim(false);
            beforeSelectionMaskCompute = () =>
            {
                computeThread = Environment.CurrentManagedThreadId; entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Mask worker control timed out.");
            };
            var work = CreateLayerMaskAsync(revealing, useSelection: true);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var pulse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                DispatcherQueue.TryEnqueue(() => pulse.TrySetResult()); await pulse.Task.WaitAsync(TimeSpan.FromSeconds(1));
                Require(computeThread != uiThread && !work.IsCompleted && ReferenceEquals(source, scene.Snapshot)
                    && ReferenceEquals(selection, scene.ActiveSelection) && !scene.CanUndo && !LayerList.IsEnabled && !paintOnMask,
                    "Mask preparation blocked/published on the UI thread or changed its live source/target.");
                BeginPointerStroke(new(7, 7)); OnUndo(this, new());
                Require(pointerStroke is null && !scene.CanUndo && !await DeleteSelectedLayersAsync(), "Mask preparation allowed overlapping edits.");
                release.Set(); Require(await work, "The prepared mask did not commit."); await WaitForFramesAsync();
                Require(scene.Snapshot.Masks[id].Pixels.SequenceEqual(reference.Snapshot.Masks[id].Pixels)
                    && ReferenceEquals(source.Images[id], scene.Snapshot.Images[id]) && scene.ActiveSelection is null && paintOnMask
                    && !IsDeletePending && LayerList.IsEnabled, "The worker result differs or retained busy state.");
                AssertUploadedFrame(scene); OnUndo(this, new()); await WaitForFramesAsync();
                Require(!scene.CanUndo && !scene.Snapshot.Masks.ContainsKey(id) && ReferenceEquals(selection, scene.ActiveSelection), "Mask creation did not undo exactly once.");
            }
            finally { release.Set(); beforeSelectionMaskCompute = null; }
            report[revealing ? "revealMaskWorkerKeepsUiLiveAndPublishesOnce" : "hideMaskWorkerKeepsUiLiveAndPublishesOnce"] = "passed";
        }

        var stale = Scene(); OpenDocument(stale); await WaitForFramesAsync(); var staleId = stale.Snapshot.Manifest.ActiveLayerId!.Value;
        using (var release = new ManualResetEventSlim(false))
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            beforeSelectionMaskCompute = () => { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Stale mask control timed out."); };
            var work = CreateLayerMaskAsync(true, true);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); stale.RenameLayer(staleId, "Newer name"); release.Set();
                Require(!await work && stale.Snapshot.Masks.Count == 0 && stale.ActiveSelection is not null, "Stale mask preparation overwrote a newer document.");
                OnUndo(this, new()); await WaitForFramesAsync(); Require(!stale.CanUndo, "Rejected mask preparation added history.");
            }
            finally { release.Set(); beforeSelectionMaskCompute = null; }
        }
        report["maskCreationRejectsNewerMetadata"] = "passed";

        var old = Scene(); OpenDocument(old); await WaitForFramesAsync();
        using (var releaseOld = new ManualResetEventSlim(false))
        using (var releaseNew = new ManualResetEventSlim(false))
        {
            var enteredOld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            beforeSelectionMaskCompute = () => { enteredOld.TrySetResult(); if (!releaseOld.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Retired mask control timed out."); };
            var oldWork = CreateLayerMaskAsync(true, true);
            try
            {
                await enteredOld.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var replacement = Scene(); OpenDocument(replacement); await WaitForFramesAsync();
                Require(!IsDeletePending && LayerList.IsEnabled, "Replacement retained mask creation's controls.");
                var enteredNew = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                beforeSelectionMaskCompute = () => { enteredNew.TrySetResult(); if (!releaseNew.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("New mask control timed out."); };
                var newWork = CreateLayerMaskAsync(false, true); await enteredNew.Task.WaitAsync(TimeSpan.FromSeconds(3)); releaseOld.Set();
                Require(!await oldWork && IsDeletePending && !LayerList.IsEnabled && replacement.Snapshot.Masks.Count == 0,
                    "Old mask completion published or released a newer operation's controls.");
                releaseNew.Set(); Require(await newWork, "Replacement mask did not commit."); await WaitForFramesAsync();
                Require(!IsDeletePending && !old.CanUndo && old.ActiveSelection is not null, "Retired mask changed its old document.");
            }
            finally { releaseOld.Set(); releaseNew.Set(); beforeSelectionMaskCompute = null; }
        }
        report["retiredMaskCreationCannotReleaseNewerBusyState"] = "passed";

        var oversized = Scene(); var folder = oversized.GroupLayers([oversized.Snapshot.Manifest.ActiveLayerId!.Value])!;
        folder.Transform.Size = new(300_000, 300_000); oversized = new(oversized.Snapshot); oversized.SetSelection(Selection.Rectangle(16, 16, 1, 1, 4, 4));
        OpenDocument(oversized); await WaitForFramesAsync();
        Require(!await CreateLayerMaskAsync(true, true) && !IsDeletePending && LayerList.IsEnabled && !oversized.CanUndo
            && oversized.ActiveSelection is not null && oversized.Snapshot.Masks.Count == 0, "A refused mask plane left history, selection or busy controls behind.");
        report["unavailableMaskPlaneReleasesControlsWithoutHistory"] = "passed";
    }
}
#endif
