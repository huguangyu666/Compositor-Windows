using Xunit;

namespace Compositor.Core.Tests;

public class ResamplerRowCopyTests
{
    [Fact]
    public void ExactNearestPlacementNeedsNoSamplingTables()
    {
        var source = EffectRegionTests.Pattern(1024, 100);
        var target = new Surface(1024, 100);
        var transform = new LayerTransform { Size = new(1024, 100), Sampling = LayerSampling.Nearest };
        Resampler.Draw(source, transform, target, 0, 0, 1024, 100);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Resampler.Draw(source, transform, target, 0, 0, 1024, 100);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 512);
    }

    [Theory]
    [InlineData(LayerSampling.Nearest)]
    [InlineData(LayerSampling.Smooth)]
    [InlineData(LayerSampling.HighQuality)]
    public void IntegerPlacementClipsRowsAndPreservesSourceAndDestinationPadding(LayerSampling sampling)
    {
        var source = EffectRegionTests.Pattern(31, 23); var input = source.Pixels.ToArray();
        foreach (var origin in new[] { new PointD(0, 0), new(7, 5), new(-11, -3), new(1000000, -1000000) })
        {
            var actual = new Surface(47, 35, Enumerable.Repeat((byte)173, (47 * 4 + 20) * 35).ToArray(), 47 * 4 + 20);
            var expected = actual.Clone();
            var transform = new LayerTransform { Origin = origin, Size = new(31, 23), Sampling = sampling };
            for (int y = 2; y < 31; y++)
            for (int x = 3; x < 43; x++)
            {
                int sx = x - (int)origin.X, sy = y - (int)origin.Y;
                if ((uint)sx < 31 && (uint)sy < 23)
                    source.Pixels.AsSpan(sy * source.Stride + sx * 4, 4)
                        .CopyTo(expected.Pixels.AsSpan(y * expected.Stride + x * 4, 4));
            }
            Resampler.Draw(source, transform, actual, 3, 2, 43, 31);
            Assert.Equal(expected.Pixels, actual.Pixels); Assert.Equal(input, source.Pixels);
        }
    }

    [Theory]
    [InlineData(LayerSampling.Nearest)]
    [InlineData(LayerSampling.Smooth)]
    [InlineData(LayerSampling.HighQuality)]
    public void CroppedMemoryKeepsFullGridOffsetsWhenCopyingRows(LayerSampling sampling)
    {
        var whole = EffectRegionTests.Pattern(80, 60); var crop = whole.Crop(12, 9, 41, 31);
        var transform = new LayerTransform { Origin = new(7, -3), Size = new(80, 60), Sampling = sampling };
        var expected = new Surface(100, 80); var actual = new Surface(100, 80);
        Resampler.Draw(whole, transform, expected, 25, 12, 45, 29);
        Resampler.Draw(new SourceWindow(crop, 80, 60, 12, 9), transform, actual, 25, 12, 45, 29);
        Assert.Equal(expected.Pixels, actual.Pixels);
    }

    [Theory]
    [InlineData(LayerSampling.Nearest)]
    [InlineData(LayerSampling.Smooth)]
    [InlineData(LayerSampling.HighQuality)]
    public void AliasedTranslationKeepsEstablishedForwardCopyOrder(LayerSampling sampling)
    {
        foreach (var origin in new[] { new PointD(1, 0), new(0, 1) })
        {
            var image = EffectRegionTests.Pattern(6, 3); var original = image.Clone();
            var transform = new LayerTransform { Origin = origin, Size = new(6, 3), Sampling = sampling };
            Resampler.Draw(image, transform, image, 0, 0, 6, 3);
            for (int y = 0; y < 3; y++)
            for (int x = 0; x < 6; x++)
            {
                int expectedX = origin.X == 1 ? 0 : x, expectedY = origin.Y == 1 ? 0 : y;
                Assert.Equal(original.Pixels.AsSpan(expectedY * original.Stride + expectedX * 4, 4).ToArray(),
                    image.Pixels.AsSpan(y * image.Stride + x * 4, 4).ToArray());
            }
        }
    }
}
