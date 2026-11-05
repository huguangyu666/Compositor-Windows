#if DEBUG
using Compositor.Core;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task VerifySelectionClearWorkerAsync(Dictionary<string, object> report)
    {
        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
        static EditorDocument Scene(bool mask)
        {
            var scene = EditorDocument.Create(16, 16); var id = scene.Snapshot.Manifest.Layers[0].Id;
            if (mask) scene.AddRasterMask(id, MaskSurface.Solid(revealing: true));
            scene = new(scene.Snapshot); scene.SetSelection(Selection.Rectangle(16, 16, 4, 4, 8, 8));
            return scene;
        }
        var savedBackground = backgroundColor;
        try
        {
            backgroundColor = Microsoft.UI.ColorHelper.FromArgb(255, 17, 17, 17);
            foreach (bool mask in new[] { false, true })
            {
                var scene = Scene(mask); OpenDocument(scene); await WaitForFramesAsync();
                paintOnMask = mask; SyncPaintTargetControls(); RefreshLayerPanel();
                var source = scene.Snapshot; var id = source.Manifest.Layers[0].Id;
                var reference = new EditorDocument(new ProjectSnapshot { Manifest = ManifestJson.Deserialize(ManifestJson.Serialize(source.Manifest)), Images = source.Images, Masks = source.Masks });
                reference.SetSelection(scene.ActiveSelection);
                Require(mask ? reference.FillMask(id, 17) : reference.CutSelectionMasked(id, scene.ActiveSelection!), "The clear control made no change.");
                int uiThread = Environment.CurrentManagedThreadId, workerThread = uiThread;
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var release = new ManualResetEventSlim(false);
                beforePlaneClearCompute = () =>
                {
                    workerThread = Environment.CurrentManagedThreadId; entered.TrySetResult();
                    if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Clear worker control timed out.");
                };
                var pending = DeletePlaneTargetAsync(clearSelection: true);
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    var pulse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    DispatcherQueue.TryEnqueue(() => pulse.TrySetResult());
                    await pulse.Task.WaitAsync(TimeSpan.FromSeconds(1));
                    Require(workerThread != uiThread && !pending.IsCompleted && IsDeletePending && !LayerList.IsEnabled
                        && ReferenceEquals(source, scene.Snapshot) && !scene.CanUndo, "Clear blocked/published on the UI thread or changed live state before completion.");
                    BeginPointerStroke(new(7, 7)); OnUndo(this, new());
                    Require(pointerStroke is null && !scene.CanUndo && !await DeleteSelectedLayersAsync(), "Clear allowed overlapping paint/history/deletion input.");
                    release.Set(); Require(await pending, "Prepared clear did not commit."); await WaitForFramesAsync();
                    Require(!IsDeletePending && LayerList.IsEnabled && ToolCombo.IsEnabled, "Clear retained busy controls.");
                    Require((mask ? scene.Snapshot.Masks[id].Pixels : scene.Snapshot.Images[id].Pixels)
                        .SequenceEqual(mask ? reference.Snapshot.Masks[id].Pixels : reference.Snapshot.Images[id].Pixels), "Worker pixels differ from the established kernel.");
                    AssertUploadedFrame(scene); OnUndo(this, new()); await WaitForFramesAsync();
                    Require(!scene.CanUndo && ReferenceEquals(source.Images[id], scene.Snapshot.Images[id])
                        && (!mask || ReferenceEquals(source.Masks[id], scene.Snapshot.Masks[id])), "Prepared clear did not undo exactly once.");
                }
                finally { release.Set(); beforePlaneClearCompute = null; }
                report[mask ? "maskClearWorkerKeepsUiLiveAndPublishesOnce" : "pixelClearWorkerKeepsUiLiveAndPublishesOnce"] = "passed";
            }

            var stale = Scene(false); OpenDocument(stale); await WaitForFramesAsync();
            var staleId = stale.Snapshot.Manifest.Layers[0].Id; var original = stale.Snapshot.Images[staleId];
            using (var release = new ManualResetEventSlim(false))
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                beforePlaneClearCompute = () => { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Stale clear control timed out."); };
                var pending = DeletePlaneTargetAsync(clearSelection: true);
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    stale.RenameLayer(staleId, "Newer metadata"); release.Set();
                    Require(!await pending && ReferenceEquals(original, stale.Snapshot.Images[staleId]), "Stale clear overwrote a newer document.");
                    OnUndo(this, new()); await WaitForFramesAsync(); Require(!stale.CanUndo, "A refused clear added history after the rename.");
                }
                finally { release.Set(); beforePlaneClearCompute = null; }
            }
            report["clearWorkerRejectsNewerMetadata"] = "passed";

            var old = Scene(false); OpenDocument(old); await WaitForFramesAsync();
            using (var releaseOld = new ManualResetEventSlim(false))
            using (var releaseNew = new ManualResetEventSlim(false))
            {
                var enteredOld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                beforePlaneClearCompute = () => { enteredOld.TrySetResult(); if (!releaseOld.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Retired clear control timed out."); };
                var oldWork = DeletePlaneTargetAsync(clearSelection: true);
                try
                {
                    await enteredOld.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    var replacement = Scene(false); OpenDocument(replacement); await WaitForFramesAsync();
                    Require(!IsDeletePending && LayerList.IsEnabled, "Retiring a clear did not release its controls.");
                    var enteredNew = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    beforePlaneClearCompute = () => { enteredNew.TrySetResult(); if (!releaseNew.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Replacement clear control timed out."); };
                    var newWork = DeletePlaneTargetAsync(clearSelection: true);
                    await enteredNew.Task.WaitAsync(TimeSpan.FromSeconds(3)); releaseOld.Set();
                    Require(!await oldWork && IsDeletePending && !LayerList.IsEnabled && !replacement.CanUndo,
                        "An old clear modified the replacement or released a newer operation's busy state.");
                    releaseNew.Set(); Require(await newWork, "Replacement clear did not commit."); await WaitForFramesAsync();
                    Require(!IsDeletePending && LayerList.IsEnabled && !old.CanUndo, "Clear retirement retained busy state or changed its old document.");
                    AssertUploadedFrame(replacement);
                }
                finally { releaseOld.Set(); releaseNew.Set(); beforePlaneClearCompute = null; }
            }
            report["retiredClearCannotReleaseNewerBusyState"] = "passed";

            var unchanged = Scene(true); OpenDocument(unchanged); await WaitForFramesAsync();
            paintOnMask = true; SyncPaintTargetControls(); RefreshLayerPanel(); backgroundColor = Microsoft.UI.Colors.White;
            var kept = unchanged.Snapshot;
            Require(!await DeletePlaneTargetAsync(clearSelection: true) && ReferenceEquals(kept, unchanged.Snapshot)
                && !unchanged.CanUndo && !IsDeletePending && LayerList.IsEnabled, "A no-op clear created history, pixels or retained busy controls.");
            report["unchangedWorkerClearKeepsHistoryAndReleasesControls"] = "passed";
        }
        finally { beforePlaneClearCompute = null; backgroundColor = savedBackground; }
    }
}
#endif
