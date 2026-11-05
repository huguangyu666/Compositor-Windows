using Xunit;

namespace Compositor.Core.Tests;

public class ImageBlurStrokeTests
{
    private static (EditorDocument Scene, Guid Id) Create()
    {
        var scene = EditorDocument.Create(64, 64); var id = scene.Snapshot.Manifest.Layers[0].Id;
        var image = new Surface(32, 24);
        for (int y = 0; y < 24; y++) for (int x = 0; x < 32; x++)
        {
            int at = y * image.Stride + x * 4;
            image.Pixels[at] = image.Pixels[at + 1] = image.Pixels[at + 2] = x < 16 ? (byte)0 : (byte)255;
            image.Pixels[at + 3] = 255;
        }
        scene.ReplaceLayerImage(id, image); scene.Snapshot.Manifest.Layers[0].Transform = new() { Size = new(32, 24) };
        return (new(scene.Snapshot), id);
    }

    [Fact]
    public void SelectionClipsImageBlur()
    {
        var (scene, id) = Create(); var original = scene.Snapshot.Images[id];
        scene.SetSelection(Selection.Rectangle(64, 64, 16, 0, 48, 64));
        Assert.True(scene.BeginBlurStroke(id, 2, new object()));
        Assert.True(scene.BlurStampApply(id, new(15.5, 12.5), 4, 1)); scene.EndBlurStroke();
        Assert.Equal(0, scene.Snapshot.Images[id].Pixels[12 * original.Stride + 15 * 4]);
        Assert.InRange(scene.Snapshot.Images[id].Pixels[12 * original.Stride + 16 * 4], 1, 254);
    }

    [Fact]
    public void BlurUsesDocumentCoordinatesOnRotatedScaledAndFlippedLayers()
    {
        var (scene, id) = Create(); var original = scene.Snapshot.Images[id];
        var transform = scene.Snapshot.Manifest.Layers[0].Transform;
        transform.Origin = new(12, 14); transform.Size = new(40, 30); transform.Rotation = 21; transform.FlipX = true;
        var target = BrushEngine.PixelToDocument(transform, 32, 24, new(16.5, 12.5));
        Assert.True(scene.BeginBlurStroke(id, 2, new object()));
        Assert.True(scene.BlurStampApply(id, target, 4, 1)); scene.EndBlurStroke();
        Assert.InRange(scene.Snapshot.Images[id].Pixels[12 * original.Stride + 16 * 4], 1, 254);
    }

    [Theory]
    [InlineData(.5)]
    [InlineData(1)]
    [InlineData(2)]
    public void RadiusUsesPlacementScaleAndOverlapsKeepTheOpacityCap(double scale)
    {
        var (scene, id) = Create(); var original = scene.Snapshot.Images[id];
        var placed = scene.Snapshot.Manifest.Layers[0].Transform; placed.Size = new(32 * scale, 24 * scale);
        var point = BrushEngine.PixelToDocument(placed, 32, 24, new(15.5, 12.5));
        Assert.True(scene.BeginBlurStroke(id, 2, new object(), .25));
        Assert.True(scene.BlurStampApply(id, point, 3, 1));
        scene.BlurStampApply(id, new(point.X + 1, point.Y), 3, 1); scene.BlurStampApply(id, point, 3, 1); scene.EndBlurStroke();
        var padded = BlurWindowTests.PaddedBlur(original, 2 / scale, out int margin);
        int soft = (12 + margin) * padded.Stride + (15 + margin) * 4;
        Assert.Equal((byte)MathF.Round(padded.Pixels[soft] * .25f), scene.Snapshot.Images[id].Pixels[12 * original.Stride + 15 * 4]);
        Assert.True(scene.Undo()); Assert.False(scene.CanUndo); Assert.Same(original, scene.Snapshot.Images[id]);
        Assert.True(scene.Redo());
    }

    [Theory]
    [InlineData(1, 19, 3)]
    [InlineData(31, 1, 2)]
    [InlineData(79, 61, 5)]
    public void TransparentSampleMatchesPaddedFullKernelAtImageEdges(int width, int height, double sigma)
    {
        var source = new Surface(width, height);
        for (int at = 0; at < source.Pixels.Length; at += 4) { source.Pixels[at] = 100; source.Pixels[at + 3] = 128; }
        var padded = BlurWindowTests.PaddedBlur(source, sigma, out int margin);
        var sample = new GaussianBlurSample(source, sigma, transparentEdges: true);
        foreach (var region in new[] { new RectI(0, 0, 1, 1), new(width - 1, height - 1, 1, 1), new(width / 2, height / 2, 1, 1) })
        {
            var window = sample.Render(region);
            int soft = (region.Y + margin) * padded.Stride + (region.X + margin) * 4;
            int at = region.Y * window.Stride + region.X * 4 + window.Offset;
            Assert.Equal(padded.Pixels.AsSpan(soft, 4).ToArray(), window.Pixels.AsSpan(at, 4).ToArray());
            Assert.InRange(sample.RetainedBytes, 0, 2 * 1024 * 1024);
        }
    }

    [Fact]
    public void EmptySelectionAndZeroOpacityDoNotOpenHistory()
    {
        var (scene, id) = Create(); Assert.False(scene.BeginBlurStroke(id, 2, new object(), 0));
        scene.SetSelection(new(new MaskSurface(64, 64), new(0, 0, 0, 0)));
        Assert.False(scene.BeginBlurStroke(id, 2, new object())); Assert.False(scene.CanUndo); Assert.False(scene.IsDirty);
    }

    [Fact]
    public void SparsePointerSegmentsStillPaintAContinuousStroke()
    {
        var (scene, id) = Create(); Assert.True(scene.BeginBlurStroke(id, 2, new object()));
        scene.BlurStampApply(id, new(15.5, 4.5), 1, 1); scene.BlurStampApply(id, new(15.5, 19.5), 1, 1); scene.EndBlurStroke();
        Assert.InRange(scene.Snapshot.Images[id].Pixels[12 * 32 * 4 + 15 * 4], 1, 254);
    }
}
