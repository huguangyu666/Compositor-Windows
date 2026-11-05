#if DEBUG
using Compositor.Core;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task VerifyPaintTargetGuardsAsync(Dictionary<string, object> report)
    {
        static EditorDocument Scene(bool hidden = false, bool parentHidden = false)
        {
            var scene = EditorDocument.Create(32, 32);
            var pixels = new Surface(16, 16); pixels.Pixels.AsSpan().Fill(255);
            var layer = scene.ImportImage(pixels, "Paint target");
            if (parentHidden) { var folder = scene.GroupLayers([layer.Id])!; folder.IsVisible = false; }
            if (hidden) layer.IsVisible = false;
            scene.SetActiveLayer(layer.Id); return new(scene.Snapshot);
        }
        async Task Tool(CanvasTool target, int mode)
        {
            ToolCombo.SelectedIndex = (int)target;
            Require(await WaitUntilAsync(() => tool == target && toolChangeApplied == toolChangeRevision, 2000), "The paint-guard tool did not settle.");
            BlurModeCombo.SelectedIndex = mode; cloneSource = new(4, 4); CloneAllLayersCheck.IsChecked = true;
            BrushSizeSlider.Value = 4; await WaitForFramesAsync();
        }
        var tools = new[] { (CanvasTool.Brush, 0), (CanvasTool.Eraser, 0), (CanvasTool.Clone, 0),
            (CanvasTool.Blur, 0), (CanvasTool.Blur, 1), (CanvasTool.Blur, 2) };
        var scene = Scene(); OpenDocument(scene); await WaitForFramesAsync(); LayerList.SelectAll();
        Require(await WaitUntilAsync(() => selectedLayerIds.Count == 2, 2000), "The multi-target paint fixture did not select both rows.");
        await WaitForFramesAsync();
        foreach (var (target, mode) in tools)
        {
            await Tool(target, mode); var images = scene.Snapshot.Images; int frames = presentedFrameCount;
            BeginPointerStroke(new(8, 8)); AppendPointerStroke(new(10, 8)); FinishPointerStroke(); await WaitForFramesAsync();
            Require(pointerStroke is null && !scene.CanUndo && ReferenceEquals(images, scene.Snapshot.Images)
                && presentedFrameCount == frames && StatusText.Text == Loc.T("S_PaintMultipleLayers"),
                $"{target}/{mode} painted or rendered with several layers selected, or did not explain the refusal.");
        }
        report["brushEraserCloneBlurAndWarpRefuseMultipleTargets"] = "passed";

        foreach (bool parent in new[] { false, true })
        {
            scene = Scene(hidden: !parent, parentHidden: parent); OpenDocument(scene); await WaitForFramesAsync();
            foreach (var (target, mode) in tools)
            {
                await Tool(target, mode); var images = scene.Snapshot.Images; var before = images.ToDictionary(pair => pair.Key, pair => pair.Value.Pixels.ToArray());
                BeginPointerStroke(new(8, 8)); AppendPointerStroke(new(10, 8)); FinishPointerStroke(); await WaitForFramesAsync();
                Require(pointerStroke is null && !scene.CanUndo && !scene.IsDirty && ReferenceEquals(images, scene.Snapshot.Images)
                    && before.All(pair => pair.Value.AsSpan().SequenceEqual(scene.Snapshot.Images[pair.Key].Pixels))
                    && StatusText.Text == Loc.F("S_PaintHiddenLayer", SelectedLayer!.Name),
                    $"{target}/{mode} edited a target hidden by {(parent ? "its folder" : "its own eye")}.");
            }
            AssertUploadedFrame(scene);
        }
        report["rasterToolsRespectLayerAndInheritedVisibility"] = "passed";

        scene = Scene(); OpenDocument(scene); await WaitForFramesAsync(); await Tool(CanvasTool.Brush, 0);
        var id = SelectedLayer!.Id; var original = scene.Snapshot.Images[id];
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(false);
        beforeFrameRender = () =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Paint-guard worker control timed out.");
        };
        try
        {
            scene.SetOpacity(id, .8); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            BeginPointerStroke(new(8, 8)); FinishPointerStroke(); scene.SetVisible(id, false);
            beforeFrameRender = null; release.Set(); await WaitForFramesAsync();
            Require(pointerStroke is null && ReferenceEquals(original, scene.Snapshot.Images[id]), "A queued stroke painted after its target became hidden.");
            OnUndo(this, new()); await WaitForFramesAsync(); OnUndo(this, new()); await WaitForFramesAsync();
            Require(!scene.CanUndo, "A refused queued stroke added an Undo step."); AssertUploadedFrame(scene);
        }
        finally { beforeFrameRender = null; release.Set(); }
        report["queuedFirstDabRechecksVisibilityWithoutAddingHistory"] = "passed";
        await VerifyMaskPaintTargetsAsync(report);
    }
}
#endif
