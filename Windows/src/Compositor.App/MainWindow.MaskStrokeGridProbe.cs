#if DEBUG
using Compositor.Core;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task VerifyMaskStrokeGridAsync(Dictionary<string, object> report)
    {
        async Task Brush(bool white = false)
        {
            ToolCombo.SelectedIndex = (int)CanvasTool.Brush;
            Require(await WaitUntilAsync(() => tool == CanvasTool.Brush && toolChangeApplied == toolChangeRevision, 2000), "Mask-grid Brush tool did not settle.");
            BrushSizeSlider.Value = 2; BrushHardnessSlider.Value = 100; BrushOpacitySlider.Value = 100; BrushSmoothingSlider.Value = 0;
            foregroundColor = white ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
            await WaitForFramesAsync();
        }
        static Surface MaskView(EditorDocument scene, Guid id)
        {
            var layer = scene.Snapshot.Manifest.Layers.Single(item => item.Id == id);
            var view = new Surface(scene.Snapshot.Manifest.Width, scene.Snapshot.Manifest.Height);
            MaskDisplay.Draw(scene.Snapshot.Masks[id], layer.MaskPlacement ?? layer.Transform, view, new(0, 0, view.Width, view.Height));
            return view;
        }
        var scene = EditorDocument.Create(64, 64); var id = scene.Snapshot.Manifest.Layers[0].Id;
        OpenDocument(scene); await WaitForFramesAsync();
        Require(await AddWorkspaceMaskAsync(0), "The footer refused its reveal mask."); await WaitForFramesAsync();
        var solid = scene.Snapshot.Masks[id]; var source = scene.Snapshot.Images[id];
        Require(solid.Width == 1 && solid.Height == 1 && paintOnMask, "The footer did not create and select a uniform mask.");
        await Brush(); BeginPointerStroke(new(32.5, 32.5)); FinishPointerStroke(); await WaitForFramesAsync();
        var mask = scene.Snapshot.Masks[id];
        Require(mask.Width == 64 && mask.Height == 64 && mask.Pixels[32 * 64 + 32] == 0 && mask.Pixels[0] == 255
            && ReferenceEquals(source, scene.Snapshot.Images[id]), "Painting the footer mask affected its whole plane or the image.");
        AssertUploadedFrame(scene); OnUndo(this, new()); await WaitForFramesAsync();
        Require(ReferenceEquals(solid, scene.Snapshot.Masks[id]) && scene.CanUndo, "Mask promotion did not undo separately from mask creation.");
        OnUndo(this, new()); await WaitForFramesAsync(); Require(!scene.CanUndo && !scene.Snapshot.Masks.ContainsKey(id), "Mask promotion added extra Undo steps.");
        report["footerUniformMaskPaintsLocallyAndUndoesOnce"] = "passed";

        foreach (bool revealing in new[] { true, false })
        {
            scene = EditorDocument.Create(64, 64); id = scene.Snapshot.Manifest.Layers[0].Id;
            var original = new MaskSurface(8, 8); original.Pixels.AsSpan().Fill(revealing ? (byte)255 : (byte)0); original.Pixels[3 * 8 + 3] = 128;
            scene.AddRasterMask(id, original); var placed = new LayerTransform { Origin = new(16, 16), Size = new(8, 8) };
            scene.Snapshot.Manifest.Layers[0].MaskPlacement = placed; scene = new(scene.Snapshot);
            source = scene.Snapshot.Images[id]; var imagePlacement = scene.Snapshot.Manifest.Layers[0].Transform;
            OpenDocument(scene); await WaitForFramesAsync(); HandleThumbnailClick(id, true, 0); await WaitForFramesAsync(); await Brush(!revealing);
            BeginPointerStroke(new(30.5, 20.5)); AppendPointerStroke(new(40.5, 20.5)); FinishPointerStroke(); await WaitForFramesAsync();
            var view = MaskView(scene, id); byte paint = revealing ? (byte)0 : (byte)255;
            Require(view.Pixels[20 * view.Stride + 30 * 4] == paint && view.Pixels[19 * view.Stride + 19 * 4] == 128
                && view.Pixels[40 * view.Stride + 40 * 4] == (revealing ? 255 : 0)
                && ReferenceEquals(source, scene.Snapshot.Images[id]) && imagePlacement.Equals(scene.Snapshot.Manifest.Layers[0].Transform),
                "Mask growth moved the image/source feature or lost the edge tone.");
            AssertUploadedFrame(scene); OnUndo(this, new()); await WaitForFramesAsync();
            Require(!scene.CanUndo && ReferenceEquals(original, scene.Snapshot.Masks[id]) && placed.Equals(scene.Snapshot.Manifest.Layers[0].MaskPlacement), "Placed mask growth did not undo once.");
            OnRedo(this, new()); await WaitForFramesAsync(); AssertUploadedFrame(scene);
            report[revealing ? "whiteMaskGrowsWithoutMovingItsSource" : "blackMaskGrowsWithoutChangingItsBackdrop"] = "passed";
        }

        scene = EditorDocument.Create(64, 64); var child = scene.Snapshot.Manifest.Layers[0].Id; id = scene.GroupLayers([child])!.Id;
        solid = MaskSurface.Solid(false); scene.AddRasterMask(id, solid); scene = new(scene.Snapshot); source = scene.Snapshot.Images[child];
        OpenDocument(scene); await WaitForFramesAsync(); HandleThumbnailClick(id, true, 0); await WaitForFramesAsync(); await Brush(true);
        BeginPointerStroke(new(32.5, 32.5)); FinishPointerStroke(); await WaitForFramesAsync(); mask = scene.Snapshot.Masks[id];
        Require(mask.Pixels[32 * mask.Width + 32] == 255 && mask.Pixels[0] == 0 && ReferenceEquals(source, scene.Snapshot.Images[child]), "Uniform folder mask did not reveal just the local stroke.");
        AssertUploadedFrame(scene); OnUndo(this, new()); await WaitForFramesAsync(); Require(!scene.CanUndo && ReferenceEquals(solid, scene.Snapshot.Masks[id]), "Folder mask promotion did not undo once.");
        report["uniformFolderMaskRevealsOnlyPaintedArea"] = "passed";

        scene = EditorDocument.Create(64, 64); id = scene.Snapshot.Manifest.Layers[0].Id; scene.AddRasterMask(id, MaskSurface.Solid(true));
        scene.Snapshot.Manifest.Layers[0].MaskPlacement = new() { Origin = new(14, 15), Size = new(16, 12), Rotation = 25, FlipX = true };
        scene = new(scene.Snapshot); scene.SetSelection(Selection.Rectangle(64, 64, 32, 0, 32, 64));
        OpenDocument(scene); await WaitForFramesAsync(); HandleThumbnailClick(id, true, 0); await WaitForFramesAsync(); await Brush(); BrushOpacitySlider.Value = 50;
        BeginPointerStroke(new(30.5, 20.5)); AppendPointerStroke(new(42.5, 20.5)); FinishPointerStroke(); await WaitForFramesAsync();
        var selected = MaskView(scene, id);
        Require(selected.Pixels[20 * selected.Stride + 29 * 4] == 255 && selected.Pixels[20 * selected.Stride + 38 * 4] is >= 120 and <= 180,
            "Rotated uniform mask ignored selection or the opacity cap.");
        AssertUploadedFrame(scene);
        var root = Environment.GetEnvironmentVariable("COMPOSITOR_LOG_DIRECTORY") ?? throw new InvalidOperationException("The mask-grid probe requires an isolated log directory.");
        var path = System.IO.Path.Combine(root, "mask-grid.comp"); var capture = scene.CaptureSave();
        await ProjectStore.SaveAsync(capture.Snapshot, path); var loaded = await ProjectStore.LoadAsync(path);
        Require(loaded.Masks[id].Pixels.AsSpan().SequenceEqual(scene.Snapshot.Masks[id].Pixels)
            && loaded.Manifest.Layers[0].MaskPlacement!.Equals(scene.Snapshot.Manifest.Layers[0].MaskPlacement), "Grown mask did not preserve its pixels/placement through .comp.");
        report["rotatedMaskGrowthClipsSelectionAndSavesItsPlacement"] = "passed";

        scene = EditorDocument.Create(768, 768); id = scene.Snapshot.Manifest.Layers[0].Id;
        var small = new MaskSurface(8, 8); small.Pixels.AsSpan().Fill(255); scene.AddRasterMask(id, small);
        scene.Snapshot.Manifest.Layers[0].MaskPlacement = new() { Origin = new(300, 300), Size = new(8, 8) }; scene = new(scene.Snapshot);
        OpenDocument(scene); await WaitForFramesAsync(); HandleThumbnailClick(id, true, 0); await WaitForFramesAsync(); await Brush();
        BeginPointerStroke(new(100.5, 100.5)); await WaitForFramesAsync(); AppendPointerStroke(new(600.5, 100.5)); await WaitForFramesAsync();
        Require(pointerStroke is { Started: true }, "Growing into a new mask tile closed the active pointer stroke.");
        AssertUploadedFrame(scene); AppendPointerStroke(new(600.5, 600.5)); FinishPointerStroke(); await WaitForFramesAsync();
        var grown = MaskView(scene, id);
        Require(grown.Pixels[100 * grown.Stride + 100 * 4] == 0 && grown.Pixels[600 * grown.Stride + 600 * 4] == 0, "Later mask tiles lost earlier paint.");
        AssertUploadedFrame(scene); OnUndo(this, new()); await WaitForFramesAsync();
        Require(!scene.CanUndo && ReferenceEquals(small, scene.Snapshot.Masks[id]), "A multi-tile mask stroke added more than one Undo step.");
        report["maskTileGrowthKeepsPointerAliveAndOneUndo"] = "passed";
    }
}
#endif
