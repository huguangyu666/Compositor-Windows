using Xunit;

namespace Compositor.Core.Tests;

public class CloneCoverageTests
{
    private static (EditorDocument Scene, Guid Id) Create()
    {
        var scene = EditorDocument.Create(48, 48); var id = scene.Snapshot.Manifest.Layers[0].Id;
        var image = new Surface(24, 24);
        for (int y = 0; y < 24; y++) for (int x = 0; x < 24; x++)
        {
            int at = y * image.Stride + x * 4;
            image.Pixels[at + (x < 6 ? 0 : 2)] = 255; image.Pixels[at + 3] = 255;
        }
        scene.ReplaceLayerImage(id, image); scene.Snapshot.Manifest.Layers[0].Transform = new() { Size = new(24, 24) };
        return (new(scene.Snapshot), id);
    }

    [Fact]
    public void StrokeSamplesTheOriginalRatherThanItsOwnPaint()
    {
        var (scene, id) = Create(); var original = scene.Snapshot.Images[id]; var key = new object();
        Assert.True(scene.BeginCloneStroke(id, new(2.5, 8.5), new(6.5, 8.5), 1, 1, false, key));
        Assert.True(scene.CloneStampAppend(id, new(6.5, 8.5), new(10.5, 8.5), 1, 1)); scene.EndCloneStroke();
        Assert.Equal(0, scene.Snapshot.Images[id].Pixels[8 * original.Stride + 10 * 4]);
        Assert.True(scene.Undo()); Assert.False(scene.CanUndo); Assert.Same(original, scene.Snapshot.Images[id]);
        Assert.True(scene.Redo());
    }

    [Fact]
    public void SelectionClipsTheCloneTip()
    {
        var (scene, id) = Create(); scene.SetSelection(Selection.Rectangle(48, 48, 16, 0, 32, 48));
        var original = scene.Snapshot.Images[id];
        Assert.True(scene.BeginCloneStroke(id, new(3.5, 8.5), new(15.5, 8.5), 3, 1, false, new object())); scene.EndCloneStroke();
        Assert.Equal(0, scene.Snapshot.Images[id].Pixels[8 * original.Stride + 14 * 4]);
        Assert.Equal(255, scene.Snapshot.Images[id].Pixels[8 * original.Stride + 16 * 4]);
    }

    [Fact]
    public void SourceAndDestinationUseDocumentCoordinatesOnAPlacedLayer()
    {
        var (scene, id) = Create(); var placed = scene.Snapshot.Manifest.Layers[0].Transform;
        placed.Origin = new(10, 10); placed.Size = new(30, 30); placed.Rotation = 31; placed.FlipX = true;
        var source = BrushEngine.PixelToDocument(placed, 24, 24, new(2.5, 8.5));
        var target = BrushEngine.PixelToDocument(placed, 24, 24, new(16.5, 8.5));
        Assert.True(scene.BeginCloneStroke(id, source, target, 2, 1, false, new object())); scene.EndCloneStroke();
        Assert.InRange(scene.Snapshot.Images[id].Pixels[8 * 24 * 4 + 16 * 4], 254, 255);
    }

    [Fact]
    public void OverlappingDabsStayWithinTheStrokeOpacityAndPreserveTransparentSource()
    {
        var (scene, id) = Create(); var original = scene.Snapshot.Images[id];
        int transparent = 10 * original.Stride + 2 * 4; original.Pixels.AsSpan(transparent, 4).Clear();
        var key = new object(); Assert.True(scene.BeginCloneStroke(id, new(2.5, 8.5), new(14.5, 8.5), 4, 1, false, key, .25));
        scene.CloneStampAppend(id, new(3.5, 8.5), new(15.5, 8.5), 4, 1);
        scene.CloneStampAppend(id, new(2.5, 8.5), new(14.5, 8.5), 4, 1); scene.EndCloneStroke();
        int at = 8 * original.Stride + 14 * 4; var pixels = scene.Snapshot.Images[id].Pixels;
        Assert.Equal(64, pixels[at]); Assert.Equal(191, pixels[at + 2]); Assert.Equal(255, pixels[at + 3]);
        Assert.Equal(original.Pixels.AsSpan(10 * original.Stride + 14 * 4, 4).ToArray(), pixels.AsSpan(10 * original.Stride + 14 * 4, 4).ToArray());
    }

    [Fact]
    public void TranslucentSamplesBlendSourceOverTheOriginal()
    {
        var (scene, id) = Create(); var image = scene.Snapshot.Images[id];
        int sample = 8 * image.Stride + 2 * 4; image.Pixels[sample] = 128; image.Pixels[sample + 3] = 128;
        Assert.True(scene.BeginCloneStroke(id, new(2.5, 8.5), new(14.5, 8.5), 2, 1, false, new object(), .5)); scene.EndCloneStroke();
        int at = 8 * image.Stride + 14 * 4; var pixels = scene.Snapshot.Images[id].Pixels;
        Assert.Equal(64, pixels[at]); Assert.Equal(191, pixels[at + 2]); Assert.Equal(255, pixels[at + 3]);
    }

    [Fact]
    public void AllLayersSampleUsesCanvasCoordinatesAndRefusesEmptySelection()
    {
        var (scene, id) = Create(); var layer = scene.Snapshot.Manifest.Layers[0]; layer.Transform.Origin = new(10, 10);
        var top = scene.ImportImage(new Surface(1, 1, [0, 255, 0, 255], 4), "Green");
        top.Transform = new() { Origin = new(2, 2), Size = new(4, 4) }; scene = new(scene.Snapshot);
        Assert.True(scene.BeginCloneStroke(id, new(3.5, 3.5), new(24.5, 18.5), 2, 1, true, new object(), .5)); scene.EndCloneStroke();
        int at = 8 * 24 * 4 + 14 * 4; Assert.Equal(128, scene.Snapshot.Images[id].Pixels[at + 1]);
        Assert.True(scene.Undo()); Assert.False(scene.CanUndo);
        scene.SetSelection(new(new MaskSurface(48, 48), new(0, 0, 0, 0)));
        Assert.False(scene.BeginCloneStroke(id, new(3, 3), new(24, 18), 2, 1, true, new object()));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(23, 1.25)]
    [InlineData(45, .5)]
    public void PlacedClonePresentationIncludesResamplingFootprint(double rotation, double scale)
    {
        var (scene, id) = Create(); var placed = scene.Snapshot.Manifest.Layers[0].Transform;
        placed.Origin = new(10, 10); placed.Size = new(24 * scale, 24 * scale); placed.Rotation = rotation; placed.FlipX = true;
        var source = BrushEngine.PixelToDocument(placed, 24, 24, new(2.5, 8.5));
        var target = BrushEngine.PixelToDocument(placed, 24, 24, new(16.5, 8.5));
        var compositor = new CanvasCompositor(scene.Snapshot); compositor.Render();
        Assert.True(scene.BeginCloneStroke(id, source, target, 5, 1, false, new object(), .5)); scene.EndCloneStroke();
        compositor.SetSnapshot(scene.Snapshot);
        var band = compositor.ExpandPixelDirty(BrushEngine.StrokePresentationBounds(placed, 24, 24, target, target, 5));
        var actual = compositor.Render(band.X, band.Y, band.X + band.Width, band.Y + band.Height).Pixels;
        var expected = new CanvasCompositor(scene.Snapshot).Render().Pixels;
        var differences = Enumerable.Range(0, 48 * 48).Where(pixel => !expected.AsSpan(pixel * 4, 4).SequenceEqual(actual.AsSpan(pixel * 4, 4))).ToArray();
        Assert.True(differences.Length == 0, $"band={band}, differing pixels={differences.Length}; first={string.Join(',', differences.Take(8).Select(pixel => $"{pixel % 48}/{pixel / 48}"))}");
    }
}
