using Xunit;

namespace Compositor.Core.Tests;

public class MaskDisplayBatchTests
{
    [Theory]
    [InlineData(false, false, 0)] [InlineData(true, false, 0)] [InlineData(false, true, 0)] [InlineData(true, true, 0)]
    [InlineData(false, false, 255)] [InlineData(true, false, 255)] [InlineData(false, true, 255)] [InlineData(true, true, 255)]
    public void BatchedCubicMatchesReferenceForNarrowRowsFlipsAndPaddedDirtyBands(bool flipX, bool flipY, byte edge)
    {
        foreach (var grid in new[] { new SizeD(1, 1), new SizeD(3, 5), new SizeD(4, 1), new SizeD(5, 2), new SizeD(17, 3), new SizeD(67, 19) })
        foreach (double scale in new[] { .25, 1, 2 })
        foreach (var size in new[] { new SizeD(.75, 3.25), new SizeD(19.125, 17.625), new SizeD(73.5, 41.25) })
        {
            var mask = new MaskSurface((int)grid.Width, (int)grid.Height); new Random(7301).NextBytes(mask.Pixels);
            for (int y = 0; y < mask.Height; y++) for (int x = 0; x < mask.Width; x++)
                if (x == 0 || y == 0 || x == mask.Width - 1 || y == mask.Height - 1) mask.Pixels[y * mask.Width + x] = edge;
            var source = mask.Pixels.ToArray(); var transform = new LayerTransform { Origin = new(-.125, 1.375), Size = size, FlipX = flipX, FlipY = flipY };
            foreach (var band in new[] { new RectI(0, 0, 31, 23), new RectI(3, 5, 24, 13), new RectI(-3, -2, 14, 11), new RectI(7, 6, 1, 3) })
            {
                const int stride = 141;
                var expected = new Surface(31, 23, Enumerable.Repeat((byte)71, stride * 23).ToArray(), stride);
                var actual = new Surface(31, 23, expected.Pixels.ToArray(), stride);
                MaskDisplay.DrawReference(mask, transform, expected, band, scale); MaskDisplay.Draw(mask, transform, actual, band, scale);
                Assert.Equal(expected.Pixels, actual.Pixels);
            }
            Assert.Equal(source, mask.Pixels);
        }
    }

    [Fact]
    public void RowTermsAreInvalidatedBetweenCallsAfterInPlaceSourceEditsAndGridChanges()
    {
        foreach (int width in new[] { 17, 4, 31, 17 })
        {
            var mask = new MaskSurface(width, 13); new Random(7301).NextBytes(mask.Pixels);
            var transform = new LayerTransform { Origin = new(.125, -.25), Size = new(67.25, 45.5), FlipY = true };
            var actual = new Surface(63, 47); var expected = new Surface(63, 47); var band = new RectI(0, 0, 63, 47);
            MaskDisplay.Draw(mask, transform, actual, band);
            for (int y = 1; y < mask.Height - 1; y++) for (int x = 1; x < mask.Width - 1; x++) mask.Pixels[y * width + x] = (byte)(255 - mask.Pixels[y * width + x]);
            MaskDisplay.Draw(mask, transform, actual, band); MaskDisplay.DrawReference(mask, transform, expected, band);
            Assert.Equal(expected.Pixels, actual.Pixels);
        }
    }

    [Fact]
    public void IntegerSampleCentersPreserveExactPixelsAlongFractionallyPlacedRows()
    {
        var mask = new MaskSurface(17, 13); new Random(7301).NextBytes(mask.Pixels);
        foreach (var origin in new[] { new PointD(.25, 0), new PointD(0, .25), new PointD(.25, .25) })
        {
            var transform = new LayerTransform { Origin = origin, Size = new(17, 13) };
            var actual = new Surface(31, 23); var expected = new Surface(31, 23); var band = new RectI(0, 0, 31, 23);
            MaskDisplay.Draw(mask, transform, actual, band); MaskDisplay.DrawReference(mask, transform, expected, band);
            Assert.Equal(expected.Pixels, actual.Pixels);
        }
    }
}
