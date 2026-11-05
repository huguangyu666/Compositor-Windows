using Xunit;

namespace Compositor.Core.Tests;

public class MaskStrokeGridTests
{
    [Fact]
    public void UniformMaskPaintPromotesItsGridWithoutHidingTheWholeLayer()
    {
        var scene = EditorDocument.Create(64, 64); var id = scene.Snapshot.Manifest.Layers[0].Id;
        var original = MaskSurface.Solid(true); scene.AddRasterMask(id, original); scene = new(scene.Snapshot);
        var image = scene.Snapshot.Images[id]; var key = new object();
        Assert.True(scene.BeginStroke(key, id, new(2, 1, 1, 0, 0, 0), new(32.5, 32.5), onMask: true)); scene.EndStroke(key);
        var mask = scene.Snapshot.Masks[id]; Assert.Equal(64, mask.Width); Assert.Equal(64, mask.Height);
        Assert.Equal(0, mask.Pixels[32 * 64 + 32]); Assert.Equal(255, mask.Pixels[0]); Assert.Same(image, scene.Snapshot.Images[id]);
        Assert.True(scene.Undo()); Assert.False(scene.CanUndo); Assert.Same(original, scene.Snapshot.Masks[id]);
        Assert.True(scene.Redo()); Assert.Same(mask, scene.Snapshot.Masks[id]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MaskPaintingGrowsPastItsPlacementAndKeepsItsEdgeTone(bool revealing)
    {
        var scene = EditorDocument.Create(64, 64); var id = scene.Snapshot.Manifest.Layers[0].Id;
        var original = new MaskSurface(8, 8); original.Pixels.AsSpan().Fill(revealing ? (byte)255 : (byte)0);
        original.Pixels[3 * 8 + 3] = 128; scene.AddRasterMask(id, original);
        var placed = new LayerTransform { Origin = new(16, 16), Size = new(8, 8) };
        scene.Snapshot.Manifest.Layers[0].MaskPlacement = placed; scene = new(scene.Snapshot);
        var image = scene.Snapshot.Images[id]; var layerTransform = scene.Snapshot.Manifest.Layers[0].Transform;
        byte paint = revealing ? (byte)0 : (byte)255; var key = new object();
        Assert.True(scene.BeginStroke(key, id, new(2, 1, 1, paint, paint, paint), new(30.5, 20.5), onMask: true)); scene.EndStroke(key);
        var layer = scene.Snapshot.Manifest.Layers[0]; var rendered = new Surface(64, 64);
        MaskDisplay.Draw(scene.Snapshot.Masks[id], layer.MaskPlacement!, rendered, new(0, 0, 64, 64));
        Assert.Equal(paint, rendered.Pixels[20 * rendered.Stride + 30 * 4]);
        Assert.Equal(128, rendered.Pixels[19 * rendered.Stride + 19 * 4]);
        Assert.Equal(revealing ? 255 : 0, rendered.Pixels[40 * rendered.Stride + 40 * 4]);
        Assert.Equal(layerTransform, layer.Transform); Assert.Same(image, scene.Snapshot.Images[id]); Assert.Equal(revealing ? 255 : 0, original.Pixels[0]);
        Assert.True(scene.Undo()); Assert.False(scene.CanUndo); Assert.Same(original, scene.Snapshot.Masks[id]); Assert.Equal(placed, scene.Snapshot.Manifest.Layers[0].MaskPlacement);
        Assert.True(scene.Redo());
    }

    [Fact]
    public void RotatedUniformMaskUsesItsOwnGridAndSelection()
    {
        var scene = EditorDocument.Create(64, 64); var id = scene.Snapshot.Manifest.Layers[0].Id;
        var original = MaskSurface.Solid(true); scene.AddRasterMask(id, original);
        scene.Snapshot.Manifest.Layers[0].MaskPlacement = new() { Origin = new(14, 15), Size = new(16, 12), Rotation = 25, FlipX = true };
        scene = new(scene.Snapshot); scene.SetSelection(Selection.Rectangle(64, 64, 32, 0, 32, 64));
        var key = new object(); Assert.True(scene.BeginStroke(key, id, new(2, 1, .5, 0, 0, 0), new(30.5, 20.5), onMask: true));
        scene.ContinueStroke(key, new(42.5, 20.5)); scene.EndStroke(key);
        var rendered = new Surface(64, 64); var layer = scene.Snapshot.Manifest.Layers[0];
        MaskDisplay.Draw(scene.Snapshot.Masks[id], layer.MaskPlacement!, rendered, new(0, 0, 64, 64));
        Assert.Equal(255, rendered.Pixels[20 * rendered.Stride + 29 * 4]);
        Assert.InRange(rendered.Pixels[20 * rendered.Stride + 38 * 4], 120, 180);
        Assert.Equal(255, original.Pixels[0]);
    }

    [Fact]
    public void LargeVirtualGridMaterializesOnlyTouchedTiles()
    {
        var scene = EditorDocument.Create(4096, 4096); var id = scene.Snapshot.Manifest.Layers[0].Id;
        scene.ReplaceLayerImage(id, new Surface(64, 64));
        scene.Snapshot.Manifest.Layers[0].Transform = new() { Origin = new(2048, 2048), Size = new(4, 4) };
        scene.AddRasterMask(id, MaskSurface.Solid(true)); scene = new(scene.Snapshot);
        long before = GC.GetAllocatedBytesForCurrentThread(); var key = new object();
        Assert.True(scene.BeginStroke(key, id, new(.5, 1, 1, 0, 0, 0), new(2050.5, 2050.5), onMask: true)); scene.EndStroke(key);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(bytes < 2 * 1024 * 1024, $"A local mask stroke allocated {bytes} bytes.");
        Assert.InRange(scene.Snapshot.Masks[id].PixelCount, 64 * 64, 256 * 256);
        Assert.True(scene.Undo()); Assert.Equal(1, scene.Snapshot.Masks[id].PixelCount);
    }

    [Fact]
    public void OversizedPromotionIsRefusedBeforeAllocatingOrOpeningHistory()
    {
        var scene = EditorDocument.Create(16, 16); var id = scene.Snapshot.Manifest.Layers[0].Id;
        var original = MaskSurface.Solid(true); scene.AddRasterMask(id, original);
        scene.Snapshot.Manifest.Layers[0].MaskPlacement = new() { Size = new(30_000, 30_000) }; scene = new(scene.Snapshot);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.False(scene.BeginStroke(new object(), id, new(2, 1, 1, 0, 0, 0), new(8, 8), onMask: true));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 256 * 1024);
        Assert.False(scene.CanUndo); Assert.False(scene.IsDirty); Assert.Same(original, scene.Snapshot.Masks[id]);
    }

    [Fact]
    public void AdditionalTilesRetainEarlierPaintAndUndoTogether()
    {
        var scene = EditorDocument.Create(768, 768); var id = scene.Snapshot.Manifest.Layers[0].Id;
        var original = new MaskSurface(8, 8); original.Pixels.AsSpan().Fill(255); scene.AddRasterMask(id, original);
        scene.Snapshot.Manifest.Layers[0].MaskPlacement = new() { Origin = new(300, 300), Size = new(8, 8) }; scene = new(scene.Snapshot);
        var key = new object(); Assert.True(scene.BeginStroke(key, id, new(3, 1, 1, 0, 0, 0), new(100.5, 100.5), onMask: true));
        var first = scene.Snapshot.Masks[id]; var capture = scene.CaptureSave(); var frozen = capture.Snapshot.Masks[id].Pixels.ToArray();
        Assert.True(scene.ContinueStroke(key, new(600.5, 600.5))); scene.EndStroke(key); Assert.NotSame(first, scene.Snapshot.Masks[id]);
        var rendered = new Surface(768, 768); var layer = scene.Snapshot.Manifest.Layers[0];
        MaskDisplay.Draw(scene.Snapshot.Masks[id], layer.MaskPlacement!, rendered, new(0, 0, 768, 768));
        Assert.Equal(0, rendered.Pixels[100 * rendered.Stride + 100 * 4]); Assert.Equal(0, rendered.Pixels[600 * rendered.Stride + 600 * 4]);
        Assert.Equal(frozen, capture.Snapshot.Masks[id].Pixels);
        Assert.True(scene.Undo()); Assert.False(scene.CanUndo); Assert.Same(original, scene.Snapshot.Masks[id]); Assert.True(scene.Redo());
    }

    [Fact]
    public void GrowingMaskWarmAndPartialFramesMatchFreshComposition()
    {
        var scene = EditorDocument.Create(64, 64); var id = scene.Snapshot.Manifest.Layers[0].Id;
        var mask = new MaskSurface(8, 8); mask.Pixels.AsSpan().Fill(255); mask.Pixels[3 * 8 + 3] = 128;
        scene.AddRasterMask(id, mask); scene.Snapshot.Manifest.Layers[0].MaskPlacement = new() { Origin = new(16, 16), Size = new(8, 8) }; scene = new(scene.Snapshot);
        var warm = new CanvasCompositor(scene.Snapshot); warm.Render(); var partial = new CanvasCompositor(scene.Snapshot); partial.Render();
        var key = new object(); var from = new PointD(30.5, 20.5); var to = new PointD(40.5, 20.5);
        scene.BeginStroke(key, id, new(2, 1, 1, 0, 0, 0), from, onMask: true); scene.ContinueStroke(key, to); scene.EndStroke(key);
        var expected = new CanvasCompositor(scene.Snapshot).Render(); warm.SetSnapshot(scene.Snapshot); Assert.Equal(expected.Pixels, warm.Render().Pixels);
        partial.SetSnapshot(scene.Snapshot); var placed = scene.Snapshot.Manifest.Layers[0].MaskPlacement!; var current = scene.Snapshot.Masks[id];
        var band = partial.ExpandPixelDirty(BrushEngine.StrokePresentationBounds(placed, current.Width, current.Height, from, to, 2));
        var actual = partial.Render(band.X, band.Y, band.X + band.Width, band.Y + band.Height);
        var differences = Enumerable.Range(0, 64 * 64).Where(pixel => !expected.Pixels.AsSpan(pixel * 4, 4).SequenceEqual(actual.Pixels.AsSpan(pixel * 4, 4))).ToArray();
        Assert.True(differences.Length == 0, $"band={band}, differing pixels={differences.Length}; first={string.Join(',', differences.Take(8).Select(pixel => $"{pixel % 64}/{pixel / 64}"))}");
        key = new object(); to = new(50.5, 30.5);
        Assert.True(scene.BeginStroke(key, id, new(2, 1, 1, 0, 0, 0), to, onMask: true)); scene.EndStroke(key);
        partial.SetSnapshot(scene.Snapshot);
        band = partial.ExpandPixelDirty(BrushEngine.StrokePresentationBounds(placed, current.Width, current.Height, to, to, 2));
        Assert.True(band.Width < 32 && band.Height < 32, $"An unchanged mask grid forced a broad band: {band}.");
        Assert.Equal(new CanvasCompositor(scene.Snapshot).Render().Pixels,
            partial.Render(band.X, band.Y, band.X + band.Width, band.Y + band.Height).Pixels);
    }
}
