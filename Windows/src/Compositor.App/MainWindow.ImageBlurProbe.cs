#if DEBUG
using Compositor.Core;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task VerifyImageBlurAsync(Dictionary<string, object> report)
    {
        static EditorDocument Scene()
        {
            var scene = EditorDocument.Create(96, 96); var id = scene.Snapshot.Manifest.Layers[0].Id;
            var image = new Surface(32, 24);
            for (int y = 0; y < 24; y++) for (int x = 0; x < 32; x++)
            {
                int at = y * image.Stride + x * 4;
                image.Pixels[at] = image.Pixels[at + 1] = image.Pixels[at + 2] = x < 16 ? (byte)0 : (byte)255;
                image.Pixels[at + 3] = 255;
            }
            scene.ReplaceLayerImage(id, image); scene.Snapshot.Manifest.Layers[0].Transform = new() { Size = new(32, 24) };
            return new(scene.Snapshot);
        }
        async Task SelectBlur(double opacity, double radius, double tip)
        {
            ToolCombo.SelectedIndex = (int)CanvasTool.Blur;
            Require(await WaitUntilAsync(() => tool == CanvasTool.Blur && toolChangeApplied == toolChangeRevision, 2000), "Image Blur tool did not settle.");
            BlurModeCombo.SelectedIndex = 0; BlurRadiusBox.Value = radius;
            BrushSizeSlider.Value = tip; BrushHardnessSlider.Value = 100; BrushOpacitySlider.Value = opacity;
            await WaitForFramesAsync();
        }

        var scene = Scene(); var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        scene.SetSelection(Selection.Rectangle(96, 96, 16, 0, 80, 96)); OpenDocument(scene); await WaitForFramesAsync();
        await SelectBlur(25, 2, 4); var original = scene.Snapshot.Images[id]; var bytes = original.Pixels.ToArray(); var warm = compositor;
        BeginPointerStroke(new(15.5, 12.5)); AppendPointerStroke(new(16.5, 12.5)); AppendPointerStroke(new(15.5, 12.5)); FinishPointerStroke();
        await WaitForFramesAsync(); var image = scene.Snapshot.Images[id]; int at = 12 * image.Stride + 16 * 4;
        Require(image.Pixels[at] == 230 && image.Pixels[at - 4] == 0 && bytes.AsSpan().SequenceEqual(original.Pixels)
            && ReferenceEquals(warm, compositor), "Image Blur ignored opacity/selection, changed its source or discarded the compositor.");
        AssertUploadedFrame(scene); OnUndo(this, new()); await WaitForFramesAsync();
        Require(!scene.CanUndo && ReferenceEquals(original, scene.Snapshot.Images[id]), "Image Blur did not undo exactly one complete stroke.");
        OnRedo(this, new()); await WaitForFramesAsync(); AssertUploadedFrame(scene);
        report["imageBlurHonorsOpacitySelectionFrozenSourceAndOneUndo"] = "passed";

        scene = Scene(); id = scene.Snapshot.Manifest.ActiveLayerId!.Value; var placed = scene.Snapshot.Manifest.Layers[0].Transform;
        placed.Origin = new(12, 20); placed.Size = new(64, 48); placed.Rotation = 23; placed.FlipX = true;
        var target = BrushEngine.PixelToDocument(placed, 32, 24, new(15.5, 12.5));
        scene = new(scene.Snapshot); OpenDocument(scene); await WaitForFramesAsync(); await SelectBlur(50, 4, 5);
        BeginPointerStroke(target); FinishPointerStroke(); await WaitForFramesAsync();
        image = scene.Snapshot.Images[id]; at = 12 * image.Stride + 15 * 4;
        Require(image.Pixels[at] == 51 && image.Pixels[at + 3] == 255, $"Image Blur missed placement or radius scaling (value={image.Pixels[at]}).");
        AssertUploadedFrame(scene); report["imageBlurUsesRotationScaleFlipAndCanvasRadius"] = "passed";

        scene = Scene(); id = scene.Snapshot.Manifest.ActiveLayerId!.Value; OpenDocument(scene); await WaitForFramesAsync(); await SelectBlur(100, 2, 1);
        BeginPointerStroke(new(15.5, 4.5)); AppendPointerStroke(new(15.5, 19.5)); FinishPointerStroke(); await WaitForFramesAsync();
        Require(scene.Snapshot.Images[id].Pixels[12 * 32 * 4 + 15 * 4] == 102, "Sparse image-blur input left a gap between pointer events.");
        AssertUploadedFrame(scene); report["imageBlurSparseInputPaintsContinuousPath"] = "passed";
    }
}
#endif
