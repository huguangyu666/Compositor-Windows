using Xunit;

namespace Compositor.Core.Tests;

public class GrayPerspectiveGatherTests
{
    [Fact]
    public void InvalidWrappedDimensionsRemainRejectedBeforeGathering()
    {
        var value = new LayerTransform { Size = new(8, 8) };
        foreach (var size in new[] { new SizeD(0, 1), new SizeD(1, 0), new SizeD(-2, -2) })
        {
            var mask = new MaskSurface((int)size.Width, (int)size.Height, [0, 31, 127, 255]);
            Assert.Throws<ArgumentException>(() => DistortWarp.WarpMask(mask, value, DistortWarp.CornersOf(value)));
        }
    }

    [Theory]
    [InlineData(3, 1)] [InlineData(1, 4)] [InlineData(2, 2)] [InlineData(2, 3)] [InlineData(3, 3)]
    public void GrayGatherClampsFinalReadsAndIgnoresPadding(int width, int height)
    {
        foreach (int padding in new[] { 0, 7 })
        foreach (bool flipX in new[] { false, true })
        foreach (bool flipY in new[] { false, true })
        foreach (int? cap in new int?[] { null, 3, 7 })
        {
            var pixels = new byte[width * height + padding]; new Random(7301).NextBytes(pixels);
            var original = pixels.ToArray(); var mask = new MaskSurface(width, height, pixels);
            var rgba = new Surface(width, height);
            for (int i = 0; i < width * height; i++)
            { rgba.Pixels[i * 4] = rgba.Pixels[i * 4 + 1] = rgba.Pixels[i * 4 + 2] = pixels[i]; rgba.Pixels[i * 4 + 3] = 255; }
            var value = new LayerTransform { Origin = new(-.25, -.375), Size = new(13.5, 11.25), FlipX = flipX, FlipY = flipY };
            var corners = DistortWarp.CornersOf(value); corners[0] = new(-.5, -.625); corners[2] = new(12.5, 10.125);
            var expected = DistortWarp.Warp(rgba, value, corners, cap);
            var actual = DistortWarp.WarpMask(mask, value, corners, 173, cap);
            Assert.Equal(expected.Transform, actual.Transform);
            Assert.Equal(expected.Image.Width, actual.Mask.Width); Assert.Equal(expected.Image.Height, actual.Mask.Height);
            for (int i = 0; i < actual.Mask.Pixels.Length; i++)
                Assert.Equal(expected.Image.Pixels[i * 4 + 3] == 0 ? (byte)173 : expected.Image.Pixels[i * 4], actual.Mask.Pixels[i]);
            Assert.Equal(original, pixels);
        }
    }
}
