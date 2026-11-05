#if DEBUG
using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task VerifyDeleteKeyAsync(Dictionary<string, object> report)
    {
        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
        async Task<bool> Press(VirtualKey key, DependencyObject source, VirtualKeyModifiers modifiers = 0)
        {
            var work = TryDeleteKeyAsync(new(key, modifiers), source);
            Require(work is not null, $"The editing surface did not take {key}.");
            bool changed = await work!; await WaitForFramesAsync(); return changed;
        }
        var savedBindings = shortcutChords.ToDictionary(); var savedBackground = backgroundColor;
        try
        {
            shortcutChords.Clear();
            var scene = EditorDocument.Create(8, 8); scene.ImportImage(new Surface(4, 4), "Second");
            scene = new EditorDocument(scene.Snapshot); OpenDocument(scene); await WaitForFramesAsync();
            LayerList.SelectAll(); Require(await WaitUntilAsync(() => selectedLayerIds.Count == 2, 2000), "Native multiselection did not settle.");
            Require(await Press(VirtualKey.Delete, LayerList) && scene.LayerCount == 0, "Delete did not remove all selected rows.");
            OnUndo(this, new()); await WaitForFramesAsync(); Require(scene.LayerCount == 2 && !scene.CanUndo, "Delete-key batch did not undo once.");
            scene = EditorDocument.Create(8, 8); OpenDocument(scene); await WaitForFramesAsync();
            Require(await Press(VirtualKey.Back, CanvasStage) && scene.LayerCount == 0, "Backspace did not delete the layer without a selection.");
            report["deleteAndBackspaceReachLayerDeletion"] = "passed";

            scene = EditorDocument.Create(8, 8); var id = scene.Snapshot.Manifest.Layers[0].Id;
            scene.Snapshot.Manifest.Layers[0].Transform.FlipX = true;
            OpenDocument(scene); await WaitForFramesAsync();
            var selection = Selection.Rectangle(8, 8, 0, 0, 1, 1); scene.SetSelection(selection);
            Require(await Press(VirtualKey.Delete, CanvasStage) && scene.LayerCount == 1 && ReferenceEquals(selection, scene.ActiveSelection),
                "Delete with a selection removed the layer/selection instead of its pixels.");
            Require(scene.Snapshot.Images[id].Pixels[7 * 4 + 3] == 0 && scene.Snapshot.Images[id].Pixels[3] == 255,
                "Delete did not clear the flipped pixel under the selection.");
            AssertUploadedFrame(scene); OnUndo(this, new()); await WaitForFramesAsync(); Require(!scene.CanUndo, "Pixel clearing added multiple history steps.");
            scene.SetSelection(new(new(8, 8), new(0, 0, 0, 0)));
            Require(!await Press(VirtualKey.Delete, CanvasStage) && scene.LayerCount == 1 && !scene.CanUndo,
                "An empty selection fell through to layer deletion.");
            report["deleteClearsSelectedPixelsAndKeepsEmptySelectionGuard"] = "passed";

            scene = EditorDocument.Create(8, 8); id = scene.Snapshot.Manifest.Layers[0].Id;
            var shared = MaskSurface.Solid(revealing: true); scene.AddRasterMask(id, shared); scene = new EditorDocument(scene.Snapshot);
            OpenDocument(scene); await WaitForFramesAsync(); paintOnMask = true; SyncPaintTargetControls(); RefreshLayerPanel();
            backgroundColor = Microsoft.UI.Colors.Black; scene.SetSelection(Selection.Rectangle(8, 8, 1, 1, 1, 1));
            Require(await Press(VirtualKey.Back, CanvasStage) && scene.Snapshot.Masks[id].Width == 8,
                "Delete on a selected uniform mask did not expand its grid.");
            Require(scene.Snapshot.Masks[id].Pixels[9] == 0 && scene.Snapshot.Masks[id].Pixels.Where((_, i) => i != 9).All(value => value == 255)
                && shared.Pixels[0] == 255, "Selected mask deletion filled outside the selection or rewrote shared coverage.");
            AssertUploadedFrame(scene); OnUndo(this, new()); await WaitForFramesAsync(); Require(!scene.CanUndo && ReferenceEquals(shared, scene.Snapshot.Masks[id]), "Mask clear did not undo once.");
            scene.SetSelection(null);
            Require(await Press(VirtualKey.Delete, LayerList) && scene.LayerCount == 1 && !scene.Snapshot.Masks.ContainsKey(id) && !paintOnMask,
                "Delete on a mask target removed its layer instead.");
            OnUndo(this, new()); await WaitForFramesAsync(); Require(!scene.CanUndo && scene.Snapshot.Masks.ContainsKey(id), "Mask deletion did not undo once.");
            paintOnMask = true; SyncPaintTargetControls(); scene.SetSelection(Selection.Rectangle(8, 8, 1, 1, 1, 1));
            Require(await DeleteLayerOrMaskAsync() && scene.LayerCount == 1 && !scene.Snapshot.Masks.ContainsKey(id),
                "Trash did not remove the targeted mask independently of the selection.");
            report["deleteMaskTargetAndTrashRespectSelectionPriority"] = "passed";

            scene = EditorDocument.Create(8, 8); OpenDocument(scene); await WaitForFramesAsync();
            foreach (var source in new DependencyObject[] { new TextBox(), new RichEditBox(), new PasswordBox(), new NumberBox(), new ComboBox(), RootGrid })
                Require(TryDeleteKeyAsync(new(VirtualKey.Delete, 0), source) is null, "Delete escaped a field or an unrelated control.");
            Require(TryDeleteKeyAsync(new(VirtualKey.Delete, VirtualKeyModifiers.Control), CanvasStage) is null
                && TryDeleteKeyAsync(new(VirtualKey.Back, VirtualKeyModifiers.Menu), LayerList) is null, "Modified Delete reached content editing.");
            shortcutChords["canvas:DeleteTarget"] = new(VirtualKey.Q, 0);
            Require(TryDeleteKeyAsync(new(VirtualKey.Delete, 0), CanvasStage) is null
                && TryDeleteKeyAsync(new(VirtualKey.Back, 0), LayerList) is null, "Old Delete/Backspace keys remained active after rebinding.");
            Require(await Press(VirtualKey.Q, CanvasStage) && scene.LayerCount == 0, "A rebound Delete did not reach its target.");
            shortcutChords.Clear();
            report["deleteScopeModifiersAndRebindingProtectFields"] = "passed";

            scene = EditorDocument.Create(8, 8); OpenDocument(scene); await WaitForFramesAsync();
            ToolCombo.SelectedIndex = (int)CanvasTool.Lasso; await WaitForFramesAsync();
            BeginLasso(new(1, 1), 0); MoveLasso(new(6, 1)); MoveLasso(new(6, 6));
            Require(lassoPoints is { Count: > 1 }, "The lasso delete fixture has no vertices.");
            int count = lassoPoints!.Count;
            Require(await Press(VirtualKey.Delete, CanvasStage) && lassoPoints?.Count == count - 1 && scene.LayerCount == 1 && !scene.CanUndo,
                "Delete bypassed the active lasso vertices.");
            while (lassoPoints is not null) Require(await Press(VirtualKey.Back, CanvasStage), "Backspace did not remove a lasso vertex.");
            Require(scene.LayerCount == 1 && !scene.CanUndo, "Removing the last lasso vertex also deleted the layer.");
            report["lassoVerticesTakeDeleteBeforeDocumentTargets"] = "passed";

            scene.SetSelection(Selection.Rectangle(8, 8, 0, 0, 1, 1));
            using var release = new ManualResetEventSlim(false);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            beforeFrameRender = () => { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Delete-key reader timed out."); };
            try
            {
                RenderCanvas(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var pending = TryDeleteKeyAsync(new(VirtualKey.Delete, 0), CanvasStage)!;
                var replacement = EditorDocument.Create(8, 8); OpenDocument(replacement);
                release.Set();
                Require(!await pending && !replacement.CanUndo && replacement.LayerCount == 1, "A queued clear reached a replacement document.");
                await WaitForFramesAsync(); AssertUploadedFrame(replacement);
            }
            finally { beforeFrameRender = null; release.Set(); }
            report["queuedDeleteRejectsRetiredDocument"] = "passed";
        }
        finally
        {
            shortcutChords.Clear(); foreach (var pair in savedBindings) shortcutChords.Add(pair.Key, pair.Value);
            backgroundColor = savedBackground; beforeFrameRender = null;
        }
    }
}
#endif
