using Compositor.Core;
using Xunit;

namespace Compositor.Renderer.Tests;

public class CanvasThumbnailTests
{
    [Theory]
    [InlineData(100, 50, 36, 36, 18)]
    [InlineData(50, 100, 30, 15, 30)]
    [InlineData(10000, 1, 36, 36, 1)]
    public void FitsTheCanvasInWholePoints(int width, int height, int box, int expectedWidth, int expectedHeight)
        => Assert.Equal((expectedWidth, expectedHeight), CanvasThumbnail.FittedSize(width, height, box));

    [Fact]
    public void ImageOccupiesItsCanvasPositionAndKeepsSourcePixels()
    {
        var image = new Surface(1, 1, [200, 20, 10, 255], 4);
        var frame = CanvasThumbnail.Draw(100, 50, 50, new() { Origin = new(25, 10), Size = new(20, 10), Sampling = LayerSampling.Nearest }, image);
        Assert.Equal(100, frame.Width); Assert.Equal(50, frame.Height);
        Assert.Equal(new byte[] { 200, 20, 10, 255 }, frame.Pixels.AsSpan(15 * frame.Stride + 30 * 4, 4).ToArray());
        Assert.Equal(new byte[] { 82, 82, 82, 255 }, frame.Pixels.AsSpan(0, 4).ToArray());
        Assert.Equal(new byte[] { 200, 20, 10, 255 }, image.Pixels);
    }

    [Fact]
    public void RotationAndFlipFollowTheLayerMapping()
    {
        var image = new Surface(2, 1, [255, 0, 0, 255, 0, 0, 255, 255], 8);
        var transform = new LayerTransform { Origin = new(3, 4), Size = new(4, 2), Rotation = 90, Sampling = LayerSampling.Nearest };
        var frame = CanvasThumbnail.Draw(10, 10, 5, transform, image);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, frame.Pixels.AsSpan(3 * frame.Stride + 4 * 4, 4).ToArray());
        transform.FlipX = true;
        var flipped = CanvasThumbnail.Draw(10, 10, 5, transform, image);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, flipped.Pixels.AsSpan(3 * frame.Stride + 4 * 4, 4).ToArray());
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)255)]
    public void MaskContinuesItsEdgeBackgroundOutsideIndependentPlacement(byte background)
    {
        var pixels = Enumerable.Repeat(background, 9).ToArray(); pixels[4] = (byte)(255 - background);
        var mask = new MaskSurface(3, 3, pixels);
        var frame = CanvasThumbnail.Draw(10, 10, 5, new() { Origin = new(4, 2), Size = new(3, 3), Sampling = LayerSampling.Nearest }, mask: mask);
        Assert.Equal(background, frame.Pixels[0]); Assert.Equal(255 - background, frame.Pixels[3 * frame.Stride + 5 * 4]);
        Assert.All(Enumerable.Range(0, 100), i => Assert.Equal(255, frame.Pixels[i * 4 + 3]));
    }
}
