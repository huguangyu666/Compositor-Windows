#if DEBUG
using Compositor.Core;
using Microsoft.UI.Xaml.Controls;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task VerifyLayerDeletionAsync(Dictionary<string, object> report)
    {
        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
        static EditorDocument ClippedScene()
        {
            var scene = EditorDocument.Create(8, 8);
            var source = scene.Snapshot.Manifest.Layers[0]; source.IsVisible = false; source.Opacity = .5;
            var pixels = new Surface(8, 8); pixels.Pixels.AsSpan().Fill(255);
            for (int x = 0; x < 8; x++) pixels.Pixels[x * 4 + 3] = (byte)(x * 32);
            ((Dictionary<Guid, Surface>)scene.Snapshot.Images)[source.Id] = pixels;
            var white = new Surface(8, 8); white.Pixels.AsSpan().Fill(255);
            var target = scene.ImportImage(white, "Clipped target"); target.MaskSourceId = source.Id;
            return new EditorDocument(scene.Snapshot);
        }
        void Select(params Guid[] ids)
        {
            selectedLayerIds.Clear(); selectedLayerIds.UnionWith(ids); selectedLayerId = ids[0];
            selectingLayer = true;
            try { document!.SetActiveLayer(ids[0]); } finally { selectingLayer = false; }
            RefreshLayerPanel();
        }
        try
        {
            var scene = EditorDocument.Create(8, 8);
            var child = scene.ImportImage(new Surface(4, 4), "Child");
            var folder = scene.GroupLayers([child.Id])!;
            var other = scene.ImportImage(new Surface(4, 4), "Other");
            scene = new EditorDocument(scene.Snapshot);
            OpenDocument(scene); await WaitForFramesAsync(); Select(folder.Id, child.Id, other.Id);
            projectDialogProbe = _ => throw new InvalidOperationException("A plain batch deletion asked for a dependency choice.");
            Require(await DeleteSelectedLayersAsync(), "Deleting selected roots failed."); await WaitForFramesAsync();
            Require(scene.LayerCount == 1 && selectedLayerIds.Count == 1 && LayerList.SelectedItems.Count == 1,
                "Batch deletion kept a selected layer or stale selected row.");
            AssertUploadedFrame(scene);
            OnUndo(this, new()); await WaitForFramesAsync();
            Require(scene.LayerCount == 4 && !scene.CanUndo, "Folder + selected child + other layer did not undo once.");
            OnRedo(this, new()); await WaitForFramesAsync(); AssertUploadedFrame(scene);
            report["selectedFolderAndRowsDeleteAsOneUndo"] = "passed";

            scene = EditorDocument.Create(8, 8);
            scene.ImportImage(new Surface(4, 4), "Kept"); scene.ImportImage(new Surface(4, 4), "Old primary");
            scene = new EditorDocument(scene.Snapshot); OpenDocument(scene); await WaitForFramesAsync();
            var intended = scene.Snapshot.Manifest.Layers[0].Id;
            using (var releaseSelection = new ManualResetEventSlim(false))
            {
                var readerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                beforeFrameRender = () =>
                {
                    readerEntered.TrySetResult();
                    if (!releaseSelection.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Deletion selection reader timed out.");
                };
                Task<bool> pendingDelete;
                try
                {
                    RenderCanvas(); await readerEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    LayerList.SelectedItems.Clear(); LayerList.SelectedItems.Add(layerRows[intended]);
                    pendingDelete = DeleteSelectedLayersAsync();
                }
                finally { beforeFrameRender = null; releaseSelection.Set(); }
                Require(await pendingDelete, "Deletion did not accept the current native selection during a retiring frame.");
                await WaitForFramesAsync();
                Require(scene.LayerCount == 2 && scene.Snapshot.Manifest.Layers.All(layer => layer.Id != intended)
                    && scene.Snapshot.Manifest.Layers.Any(layer => layer.Name == "Old primary"), "Deletion used the stale editor selection.");
                OnUndo(this, new()); await WaitForFramesAsync(); Require(!scene.CanUndo && scene.LayerCount == 3, "Rapid select/delete added extra history.");
            }
            report["deletionUsesLatestNativeSelectionDuringBusyFrame"] = "passed";

            foreach (var answer in new[] { ContentDialogResult.None, ContentDialogResult.Secondary, ContentDialogResult.Primary })
            {
                scene = ClippedScene(); var source = scene.Snapshot.Manifest.Layers[0].Id; var target = scene.Snapshot.Manifest.Layers[1].Id;
                OpenDocument(scene); await WaitForFramesAsync(); Select(source);
                var before = ManifestJson.Serialize(scene.Snapshot.Manifest);
                var pixels = scene.Snapshot.Images[target]; var expected = new CanvasCompositor(scene.Snapshot).Render().Pixels.ToArray();
                int prompts = 0;
                projectDialogProbe = dialog =>
                {
                    prompts++;
                    Require(dialog.PrimaryButtonText == Loc.T("Dlg_BakeAndDelete") && dialog.SecondaryButtonText == Loc.T("Dlg_UnlinkAndDelete")
                        && dialog.CloseButtonText == Loc.T("T_Dlg_Cancel"), "Deletion did not offer the three dependency choices.");
                    return Task.FromResult(answer);
                };
                bool changed = await DeleteSelectedLayersAsync(); await WaitForFramesAsync();
                Require(prompts == 1 && !IsDeletePending && LayerList.IsEnabled && ToolCombo.IsEnabled, "Deletion left busy controls or repeated its dialog.");
                if (answer == ContentDialogResult.None)
                {
                    Require(!changed && before == ManifestJson.Serialize(scene.Snapshot.Manifest) && !scene.CanUndo
                        && ReferenceEquals(pixels, scene.Snapshot.Images[target]), "Cancelling deletion changed document/history.");
                }
                else
                {
                    Require(changed && scene.LayerCount == 1 && scene.Snapshot.Manifest.Layers[0].MaskSourceId is null,
                        "Deleting a source retained a dangling clipping link.");
                    ProjectValidator.Validate(scene.Snapshot.Manifest); AssertUploadedFrame(scene);
                    if (answer == ContentDialogResult.Primary)
                        Require(expected.SequenceEqual(new CanvasCompositor(scene.Snapshot).Render().Pixels), "Baking changed the clipped pixel fixture.");
                    else Require(ReferenceEquals(pixels, scene.Snapshot.Images[target]), "Remove Links rewrote target pixels.");
                    OnUndo(this, new()); await WaitForFramesAsync();
                    Require(!scene.CanUndo && scene.LayerCount == 2 && scene.Snapshot.Manifest.Layers[1].MaskSourceId == source,
                        "Source deletion and link/pixel changes did not undo together.");
                    OnRedo(this, new()); await WaitForFramesAsync(); AssertUploadedFrame(scene);
                }
                report[$"liveSourceDeletion{answer}IsAtomic"] = "passed";
            }

            scene = ClippedScene(); OpenDocument(scene); await WaitForFramesAsync();
            var changing = scene.Snapshot.Manifest.Layers[0].Id; Select(changing);
            projectDialogProbe = _ => { scene.RenameLayer(changing, "Changed during choice"); return Task.FromResult(ContentDialogResult.Secondary); };
            Require(!await DeleteSelectedLayersAsync() && scene.LayerCount == 2, "A stale dependency choice deleted newer state.");
            OnUndo(this, new()); await WaitForFramesAsync();
            Require(!scene.CanUndo && scene.LayerCount == 2, "Refusing stale deletion added a history step.");
            report["staleLayerDeletionChoicePreservesNewerDocument"] = "passed";

            scene = ClippedScene(); OpenDocument(scene); await WaitForFramesAsync(); Select(scene.Snapshot.Manifest.Layers[0].Id);
            projectDialogProbe = _ => Task.FromResult(ContentDialogResult.Primary);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            beforeLayerDeletionBake = async () => { entered.TrySetResult(); await release.Task; };
            var deleting = DeleteSelectedLayersAsync();
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                BeginPointerStroke(new(2, 2)); OnUndo(this, new());
                Require(pointerStroke is null && !scene.CanUndo && !LayerList.IsEnabled, "Baking allowed paint/history input into its captured source.");
                var replacement = EditorDocument.Create(8, 8); OpenDocument(replacement); await WaitForFramesAsync();
                Require(!IsDeletePending && LayerList.IsEnabled, "Document replacement retained deletion's busy controls.");
                release.TrySetResult();
                Require(!await deleting && !replacement.CanUndo && replacement.LayerCount == 1, "A retired bake modified the replacement document.");
                AssertUploadedFrame(replacement);
            }
            finally { release.TrySetResult(); beforeLayerDeletionBake = null; }
            report["retiredLayerDeletionReleasesBusyAndRejectsLateBake"] = "passed";
        }
        finally { projectDialogProbe = null; beforeLayerDeletionBake = null; }
    }
}
#endif
