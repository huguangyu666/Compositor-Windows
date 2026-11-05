using Xunit;
using System.Linq;
using System.IO;

namespace Compositor.Core.Tests;

/// <summary>
/// Image strokes grow past the layer's pixel grid, as `BrushStroke.swift` does: the virtual
/// grid spans the source plus the canvas reach, only the source plus touched tiles
/// materialize, and the commit keeps the original pixels' density, rotation and world place
/// while trimming to the painted area. An attached mask re-edges over the new area (never
/// stretched); a placed mask keeps its own pixels and its own place.
/// </summary>
public class ImageStrokeGridTests
{
    private static EditorDocument Scene(int canvas = 64)
    {
        var scene = EditorDocument.Create(canvas, canvas);
        var id = scene.Snapshot.Manifest.Layers[0].Id;
        // A 32×32 blue image at (16,16): the canvas reaches past its grid on every side.
        ((Dictionary<Guid, Surface>)scene.Snapshot.Images)[id] = Solid(32, 32, 20, 60, 240);
        scene.Snapshot.Manifest.Layers[0].Transform = new LayerTransform { Origin = new PointD(16, 16), Size = new SizeD(32, 32) };
        return scene;
    }

    private static Surface Solid(int width, int height, byte r, byte g, byte b)
    {
        var surface = new Surface(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = y * surface.Stride + x * 4;
                surface.Pixels[index] = r;
                surface.Pixels[index + 1] = g;
                surface.Pixels[index + 2] = b;
                surface.Pixels[index + 3] = 255;
            }
        }
        return surface;
    }

    private static byte Pixel(Surface surface, int x, int y, int channel) => surface.Pixels[y * surface.Stride + x * 4 + channel];

    [Fact]
    public void BrushBeyondTheGridGrowsTheLayerAndKeepsTheSourceInPlace()
    {
        var scene = Scene(); var id = scene.Snapshot.Manifest.Layers[0].Id;
        var original = scene.Snapshot.Images[id]; var before = scene.Snapshot.Manifest.Layers[0].Transform;
        var key = new object();
        // Document (48.5, 31.5) is 0.5 inside the canvas's right edge of the grid + 16: outside
        // the source's [16,48) span. The dab must grow the image, not clip to the old plane.
        Assert.True(scene.BeginStroke(key, id, new(2, 1, 1, 250, 10, 10), new(48.5, 31.5)));
        scene.EndStroke(key);
        var grown = scene.Snapshot.Images[id]; var layer = scene.Snapshot.Manifest.Layers[0];
        Assert.NotSame(original, grown);
        Assert.True(grown.Width > 32 || grown.Height > 32, "the plane stayed at the source grid");
        // Density and orientation survive: one grid pixel still covers the same document area.
        var after = layer.Transform;
        Assert.Equal(before.Rotation, after.Rotation); Assert.Equal(before.FlipX, after.FlipX); Assert.Equal(before.FlipY, after.FlipY);
        Assert.Equal(before.Size.Width / 32 * grown.Width, after.Size.Width, 6);
        Assert.Equal(before.Size.Height / 32 * grown.Height, after.Size.Height, 6);
        // The old pixels hold their world place: the source's (16,16) center still lands at
        // document (32,32) under the grown placement.
        var moved = BrushEngine.DocumentToPixel(after, grown.Width, grown.Height, new(32, 32));
        Assert.Equal(16, (int)Math.Round(moved.X)); Assert.Equal(16, (int)Math.Round(moved.Y));
        // And the painted point carries the brush color.
        var dab = BrushEngine.DocumentToPixel(after, grown.Width, grown.Height, new(48.5, 31.5));
        Assert.Equal(250, Pixel(grown, (int)dab.X, (int)dab.Y, 0));
        // One undo restores bytes, placement and reference identity; redo takes them again.
        Assert.True(scene.Undo()); Assert.False(scene.CanUndo);
        Assert.Same(original, scene.Snapshot.Images[id]); Assert.Equal(before, scene.Snapshot.Manifest.Layers[0].Transform);
        Assert.True(scene.Redo()); Assert.Same(grown, scene.Snapshot.Images[id]);
    }

    [Fact]
    public void StrokeInsideTheGridKeepsTheExactOldBytes()
    {
        var scene = Scene(); var id = scene.Snapshot.Manifest.Layers[0].Id;
        var transform = scene.Snapshot.Manifest.Layers[0].Transform;
        var key = new object();
        Assert.True(scene.BeginStroke(key, id, new(2, 1, 1, 250, 10, 10), new(20.5, 20.5)));
        scene.ContinueStroke(key, new(30.5, 20.5)); scene.EndStroke(key);
        var image = scene.Snapshot.Images[id];
        // The stroke never left the grid: same dimensions, same placement, painted bytes inside.
        Assert.Equal(32, image.Width); Assert.Equal(32, image.Height);
        Assert.Equal(transform, scene.Snapshot.Manifest.Layers[0].Transform);
        Assert.Equal(250, Pixel(image, 4, 4, 0));
        Assert.Equal(20, Pixel(image, 25, 25, 0)); // untouched source bytes
    }

    [Fact]
    public void AttachedMaskReEdgesWhiteAndPlacedMaskKeepsItsPlace()
    {
        var scene = Scene(); var id = scene.Snapshot.Manifest.Layers[0].Id;
        var mask = new MaskSurface(32, 32); mask.Pixels.AsSpan().Fill(255); mask.Pixels[31 * 32 + 31] = 0;
        Assert.True(scene.AddRasterMask(id, mask));
        var key = new object();
        Assert.True(scene.BeginStroke(key, id, new(2, 1, 1, 250, 10, 10), new(48.5, 31.5)));
        scene.EndStroke(key);
        var after = scene.Snapshot.Masks[id]; var layer = scene.Snapshot.Manifest.Layers[0];
        Assert.Null(layer.MaskPlacement); // attached masks stay attached to the new grid
        var outside = BrushEngine.DocumentToPixel(layer.Transform, after.Width, after.Height, new(48.5, 31.5));
        Assert.Equal(255, after.Pixels[(int)outside.Y * after.Width + (int)outside.X]); // new area reveals, never hides
        Assert.Equal(0, after.Pixels[31 * after.Width + 31]); // the old black corner stays where it was
        Assert.Equal(255, after.Pixels[0]); // the old white body stays put
        Assert.True(scene.Undo()); Assert.Same(mask, scene.Snapshot.Masks[id]); // one undo rolls image and mask together
    }

    [Fact]
    public void PlacedMaskKeepsPixelsAndPlacementThroughImageGrowth()
    {
        var scene = Scene(); var id = scene.Snapshot.Manifest.Layers[0].Id;
        var mask = new MaskSurface(8, 8); mask.Pixels.AsSpan().Fill(255);
        Assert.True(scene.AddRasterMask(id, mask));
        var placed = new LayerTransform { Origin = new PointD(20, 20), Size = new SizeD(8, 8) };
        scene.Snapshot.Manifest.Layers[0].MaskPlacement = placed; scene = new(scene.Snapshot);
        var key = new object();
        Assert.True(scene.BeginStroke(key, id, new(2, 1, 1, 250, 10, 10), new(48.5, 31.5)));
        scene.EndStroke(key);
        var layer = scene.Snapshot.Manifest.Layers[0];
        Assert.Same(mask, scene.Snapshot.Masks[id]); // pixels untouched
        Assert.Equal(placed, layer.MaskPlacement); // and its own place on the document survives
    }

    [Fact]
    public void GrowthAllocatesTheTouchedBoxNotTheWholeCanvas()
    {
        var scene = EditorDocument.Create(4096, 4096); var id = scene.Snapshot.Manifest.Layers[0].Id;
        scene.ReplaceLayerImage(id, Solid(64, 64, 20, 60, 240));
        scene.Snapshot.Manifest.Layers[0].Transform = new() { Origin = new(2048, 2048), Size = new(64, 64) };
        scene = new(scene.Snapshot);
        long before = GC.GetAllocatedBytesForCurrentThread(); var key = new object();
        Assert.True(scene.BeginStroke(key, id, new(2, 1, 1, 250, 10, 10), new(2100.5, 2080.5)));
        scene.EndStroke(key);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        var grown = scene.Snapshot.Images[id];
        // The plane spans the source plus the touched tile, never the 4096² canvas.
        Assert.True(bytes < 20 * 1024 * 1024, $"a local growth stroke allocated {bytes} bytes");
        Assert.True(grown.PixelCount <= 64L * 64 + 320 * 320, $"the committed plane is {grown.Width}x{grown.Height}");
        var warm = new CanvasCompositor(scene.Snapshot); warm.Render();
        Assert.Equal(new CanvasCompositor(scene.Snapshot).Render().Pixels, warm.Render().Pixels);
    }

    [Fact]
    public void OversizedGrowthIsRefusedBeforeAllocatingOrOpeningHistory()
    {
        // A layer magnified 32×: its canvas spans 32_768 grid pixels, past the side limit, so
        // a stroke at the far corner is refused before a plane, a tile, or an undo step.
        var scene = EditorDocument.Create(4096, 4096); var id = scene.Snapshot.Manifest.Layers[0].Id;
        scene.ReplaceLayerImage(id, Solid(8, 8, 20, 60, 240));
        scene.Snapshot.Manifest.Layers[0].Transform = new() { Origin = new(0, 0), Size = new(1, 1) };
        scene = new(scene.Snapshot);
        var original = scene.Snapshot.Images[id]; var transform = scene.Snapshot.Manifest.Layers[0].Transform;
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.False(scene.BeginStroke(new object(), id, new(2, 1, 1, 250, 10, 10), new(4095.5, 4095.5)));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 256 * 1024);
        Assert.False(scene.CanUndo); Assert.False(scene.IsDirty);
        Assert.Same(original, scene.Snapshot.Images[id]); Assert.Equal(transform, scene.Snapshot.Manifest.Layers[0].Transform);
    }

    [Fact]
    public void LaterTilesRetainEarlierPaintAndUndoTogether()
    {
        var scene = Scene(768); var id = scene.Snapshot.Manifest.Layers[0].Id;
        var original = scene.Snapshot.Images[id];
        var key = new object();
        Assert.True(scene.BeginStroke(key, id, new(2, 1, 1, 250, 10, 10), new(20.5, 20.5)));
        var first = scene.Snapshot.Images[id]; var capture = scene.CaptureSave(); var frozen = capture.Snapshot.Images[id].Pixels.ToArray();
        Assert.True(scene.ContinueStroke(key, new(400.5, 20.5))); // a second tile beyond the first
        scene.EndStroke(key);
        Assert.NotSame(first, scene.Snapshot.Images[id]); // the plane grew into a new surface
        var layer = scene.Snapshot.Manifest.Layers[0]; var grown = scene.Snapshot.Images[id];
        var early = BrushEngine.DocumentToPixel(layer.Transform, grown.Width, grown.Height, new(20.5, 20.5));
        var late = BrushEngine.DocumentToPixel(layer.Transform, grown.Width, grown.Height, new(400.5, 20.5));
        Assert.Equal(250, Pixel(grown, (int)early.X, (int)early.Y, 0)); // earlier paint survived the growth
        Assert.Equal(250, Pixel(grown, (int)late.X, (int)late.Y, 0));
        Assert.Equal(frozen, capture.Snapshot.Images[id].Pixels); // the save capture stayed immutable
        Assert.True(scene.Undo()); Assert.False(scene.CanUndo); Assert.Same(original, scene.Snapshot.Images[id]);
        Assert.True(scene.Redo());
    }

    [Fact]
    public void AStrokeOnALiveTextLayerRetiresItsStyleAndUndoRestoresBoth()
    {
        // §5's metadata rule: painting rasterizes a text or shape layer's live style, and one
        // undo puts back both the original pixels and the editable record, as ReplaceLayerImage
        // does. The plane path opens its step before the plane publishes, so the memento still
        // holds the style.
        var scene = Scene(); var layer = scene.Snapshot.Manifest.Layers[0]; var id = layer.Id;
        layer.Text = new() { Content = "Editable", FontName = "Segoe UI", FontSize = 12 };
        layer.Shape = System.Text.Json.JsonSerializer.SerializeToElement(new { kind = "rectangle" });
        var text = layer.Text; var shape = layer.Shape;
        var original = scene.Snapshot.Images[id];
        var key = new object();
        Assert.True(scene.BeginStroke(key, id, new(6, 1, 1, 255, 0, 0), new(17.5, 17.5))); // inside the 32×32 at (16,16)
        scene.EndStroke(key);
        var painted = scene.Snapshot.Manifest.Layers[0];
        Assert.Null(painted.Text); Assert.Null(painted.Shape);
        Assert.NotSame(original, scene.Snapshot.Images[id]);
        Assert.True(scene.Undo());
        var restored = scene.Snapshot.Manifest.Layers[0];
        Assert.Equal(text, restored.Text); Assert.True(System.Text.Json.JsonElement.DeepEquals(shape!.Value, restored.Shape!.Value));
        Assert.Same(original, scene.Snapshot.Images[id]);
    }

    [Fact]
    public void WarmAndPartialFramesMatchFreshComposition()
    {
        var scene = Scene(); var id = scene.Snapshot.Manifest.Layers[0].Id;
        var warm = new CanvasCompositor(scene.Snapshot); warm.Render();
        var partial = new CanvasCompositor(scene.Snapshot); partial.Render();
        var key = new object(); var from = new PointD(30.5, 20.5); var to = new PointD(52.5, 20.5);
        Assert.True(scene.BeginStroke(key, id, new(2, 1, 1, 250, 10, 10), from));
        scene.ContinueStroke(key, to); scene.EndStroke(key);
        var expected = new CanvasCompositor(scene.Snapshot).Render();
        warm.SetSnapshot(scene.Snapshot); Assert.Equal(expected.Pixels, warm.Render().Pixels);
        var layer = scene.Snapshot.Manifest.Layers[0]; var grown = scene.Snapshot.Images[id];
        partial.SetSnapshot(scene.Snapshot);
        var band = partial.ExpandPixelDirty(BrushEngine.StrokePresentationBounds(layer.Transform, grown.Width, grown.Height, from, to, 2));
        var actual = partial.Render(band.X, band.Y, band.X + band.Width, band.Y + band.Height);
        var differences = Enumerable.Range(0, 64 * 64).Where(pixel => !expected.Pixels.AsSpan(pixel * 4, 4).SequenceEqual(actual.Pixels.AsSpan(pixel * 4, 4))).ToArray();
        Assert.True(differences.Length == 0, $"band={band}, differing pixels={differences.Length}");
    }

    [Fact]
    public async Task GrownStrokeSavesAndReloadsTheExpandedImage()
    {
        var scene = Scene(); var id = scene.Snapshot.Manifest.Layers[0].Id;
        var key = new object();
        Assert.True(scene.BeginStroke(key, id, new(2, 1, 1, 250, 10, 10), new(48.5, 31.5)));
        scene.EndStroke(key);
        string path = Path.Combine(Path.GetTempPath(), "compositor-grow-" + Guid.NewGuid().ToString("N") + ".comp");
        try
        {
            await ProjectStore.SaveAsync(scene.CaptureSave().Snapshot, path);
            var restored = await ProjectStore.LoadAsync(path);
            Assert.Equal(scene.Snapshot.Images[id].Pixels, restored.Images[id].Pixels);
            Assert.Equal(scene.Snapshot.Manifest.Layers[0].Transform, restored.Manifest.Layers[0].Transform);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Fact]
    public void RotatedGrowthKeepsRotationAndWorldPlace()
    {
        var scene = Scene(); var id = scene.Snapshot.Manifest.Layers[0].Id;
        var layer = scene.Snapshot.Manifest.Layers[0];
        layer.Transform = new LayerTransform { Origin = new PointD(16, 16), Size = new SizeD(32, 32), Rotation = 30 };
        scene = new(scene.Snapshot);
        var original = scene.Snapshot.Images[id]; var before = layer.Transform;
        var key = new object();
        Assert.True(scene.BeginStroke(key, id, new(2, 1, 1, 250, 10, 10), new(31.5, 6.5))); // above the rotated grid
        scene.EndStroke(key);
        var after = scene.Snapshot.Manifest.Layers[0].Transform; var grown = scene.Snapshot.Images[id];
        Assert.Equal(30, after.Rotation);
        Assert.NotSame(original, grown);
        // The old grid's document point (32,32) — its center — lands inside the grown plane.
        var center = BrushEngine.DocumentToPixel(after, grown.Width, grown.Height, new(32, 32));
        Assert.True(center.X >= 0 && center.X < grown.Width && center.Y >= 0 && center.Y < grown.Height);
        Assert.Equal(20, Pixel(grown, (int)center.X, (int)center.Y, 0)); // still the source blue at its old spot
    }

    [Fact]
    public void SelectionClipsGrowthOutsideItsBounds()
    {
        var scene = Scene(); var id = scene.Snapshot.Manifest.Layers[0].Id;
        scene.SetSelection(Selection.Rectangle(64, 64, 48, 16, 8, 32)); // a strip right of the grid
        var key = new object();
        Assert.True(scene.BeginStroke(key, id, new(2, 1, 1, 250, 10, 10), new(51.5, 31.5))); // inside the strip
        scene.EndStroke(key);
        var image = scene.Snapshot.Images[id]; var layer = scene.Snapshot.Manifest.Layers[0];
        var painted = BrushEngine.DocumentToPixel(layer.Transform, image.Width, image.Height, new(51.5, 31.5));
        Assert.Equal(250, Pixel(image, (int)painted.X, (int)painted.Y, 0));
        Assert.True(layer.Transform.Size.Width > 32, "the stroke did not grow past the source grid");
    }

    [Fact]
    public void BlankLayerPaintStaysLocalAndLandsAtThePointer()
    {
        var scene = EditorDocument.Create(1024, 1024);
        var blank = scene.AddBlankLayer()!;
        var key = new object();
        Assert.True(scene.BeginStroke(key, blank.Id, new(2, 1, 1, 10, 200, 10), new(512.5, 512.5)));
        scene.EndStroke(key);
        var image = scene.Snapshot.Images[blank.Id];
        Assert.NotNull(image);
        var layer = scene.Snapshot.Manifest.Layers.First(item => item.Id == blank.Id);
        var center = BrushEngine.DocumentToPixel(layer.Transform, image.Width, image.Height, new(512.5, 512.5));
        Assert.Equal(10, Pixel(image, (int)center.X, (int)center.Y, 0));
        // Painting does not materialize the whole canvas; the plane covers only touched tiles.
        Assert.True(image.PixelCount <= 2 * 256 * 256, $"{image.Width}x{image.Height} plane from a 4px dab");
        Assert.True(scene.Undo()); Assert.False(scene.Snapshot.Images.ContainsKey(blank.Id));
        Assert.True(scene.Redo());
    }
}
