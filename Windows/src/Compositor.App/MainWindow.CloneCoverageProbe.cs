#if DEBUG
using Compositor.Core;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task VerifyCloneCoverageAsync(Dictionary<string, object> report)
    {
        static EditorDocument Scene()
        {
            var scene = EditorDocument.Create(96, 96); var id = scene.Snapshot.Manifest.Layers[0].Id;
            var image = new Surface(48, 32);
            for (int y = 0; y < 32; y++) for (int x = 0; x < 48; x++)
            {
                int at = y * image.Stride + x * 4;
                image.Pixels[at + (x < 8 ? 0 : 2)] = 255; image.Pixels[at + 3] = 255;
            }
            scene.ReplaceLayerImage(id, image); scene.Snapshot.Manifest.Layers[0].Transform = new() { Size = new(48, 32) };
            return new(scene.Snapshot);
        }
        async Task SelectClone(double opacity, bool all)
        {
            ToolCombo.SelectedIndex = (int)CanvasTool.Clone;
            Require(await WaitUntilAsync(() => tool == CanvasTool.Clone && toolChangeApplied == toolChangeRevision, 2000), "Clone tool did not settle.");
            BrushSizeSlider.Value = 5; BrushHardnessSlider.Value = 100; BrushOpacitySlider.Value = opacity;
            CloneAlignedCheck.IsChecked = false; CloneAllLayersCheck.IsChecked = all;
            await WaitForFramesAsync();
        }

        var scene = Scene(); var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        scene.SetSelection(Selection.Rectangle(96, 96, 24, 0, 72, 96)); OpenDocument(scene); await WaitForFramesAsync();
        await SelectClone(25, false); cloneSource = new(4.5, 16.5);
        var original = scene.Snapshot.Images[id]; var originalBytes = original.Pixels.ToArray(); var warm = compositor;
        BeginPointerStroke(new(24.5, 16.5)); AppendPointerStroke(new(26.5, 16.5)); AppendPointerStroke(new(24.5, 16.5)); FinishPointerStroke();
        await WaitForFramesAsync(); var pixels = scene.Snapshot.Images[id].Pixels; int at = 16 * original.Stride + 24 * 4;
        Require(pixels[at] == 64 && pixels[at + 2] == 191 && pixels[at + 3] == 255
            && pixels[at - 4] == 0 && originalBytes.AsSpan().SequenceEqual(original.Pixels) && ReferenceEquals(warm, compositor),
            "Clone ignored opacity/selection, edited its source or discarded the compositor.");
        AssertUploadedFrame(scene); OnUndo(this, new()); await WaitForFramesAsync();
        Require(ReferenceEquals(original, scene.Snapshot.Images[id]) && !scene.CanUndo, "Clone did not undo exactly one complete stroke.");
        OnRedo(this, new()); await WaitForFramesAsync(); AssertUploadedFrame(scene);
        report["clonePointerRespectsOpacitySelectionSourceAndOneUndo"] = "passed";

        foreach (bool all in new[] { false, true })
        {
            scene = Scene(); id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
            var placed = scene.Snapshot.Manifest.Layers[0].Transform;
            placed.Origin = new(20, 22); placed.Size = new(60, 40); placed.Rotation = 23; placed.FlipX = true;
            var source = BrushEngine.PixelToDocument(placed, 48, 32, new(4.5, 16.5));
            var target = BrushEngine.PixelToDocument(placed, 48, 32, new(24.5, 16.5));
            var green = new Surface(8, 8);
            for (int pixel = 0; pixel < 64; pixel++) green.Pixels[pixel * 4 + 1] = green.Pixels[pixel * 4 + 3] = 255;
            var overlay = scene.ImportImage(green, "Sample overlay");
            overlay.Transform = new() { Origin = new(source.X - 3, source.Y - 3), Size = new(6, 6) };
            scene.SetActiveLayer(id); scene = new(scene.Snapshot); OpenDocument(scene); await WaitForFramesAsync();
            await SelectClone(50, all); cloneSource = source;
            BeginPointerStroke(target); FinishPointerStroke(); await WaitForFramesAsync();
            at = 16 * 48 * 4 + 24 * 4; pixels = scene.Snapshot.Images[id].Pixels;
            Require(pixels[at + (all ? 1 : 0)] is >= 127 and <= 128 && pixels[at + 2] is >= 127 and <= 128,
                $"Placed clone sampled the wrong coordinates or layer set (all={all}, rgba={string.Join(',', pixels.AsSpan(at, 4).ToArray())}).");
            AssertUploadedFrame(scene);
            report[all ? "cloneAllLayersUsesFrozenCanvasSampleOnPlacedTarget" : "cloneLocalSampleFollowsRotationScaleAndFlip"] = "passed";
        }
    }
}
#endif
