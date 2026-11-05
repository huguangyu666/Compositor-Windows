using Xunit;

namespace Compositor.Core.Tests;

public class MaskDisplayAxisTests
{
    [Theory]
    [InlineData(LayerSampling.Nearest, false, false)] [InlineData(LayerSampling.Nearest, true, false)]
    [InlineData(LayerSampling.Nearest, false, true)] [InlineData(LayerSampling.Nearest, true, true)]
    [InlineData(LayerSampling.Smooth, false, false)] [InlineData(LayerSampling.Smooth, true, false)]
    [InlineData(LayerSampling.Smooth, false, true)] [InlineData(LayerSampling.Smooth, true, true)]
    [InlineData(LayerSampling.HighQuality, false, false)] [InlineData(LayerSampling.HighQuality, true, false)]
    [InlineData(LayerSampling.HighQuality, false, true)] [InlineData(LayerSampling.HighQuality, true, true)]
    public void ScaledDisplayMatchesScalarForClippedBandsFlipsAndRotatedFallback(LayerSampling quality, bool flipX, bool flipY)
    {
        var mask = new MaskSurface(17, 13); new Random(7301).NextBytes(mask.Pixels); var source = mask.Pixels.ToArray();
        foreach (double scale in new[] { .25, 1, 2 })
        foreach (double rotation in new[] { 0, 360, 180, 33.125 })
        foreach (var size in new[] { new SizeD(4.5, 7.25), new SizeD(13.125, 18.5), new SizeD(83.5, 77.125) })
        foreach (var band in new[] { new RectI(0, 0, 43, 29), new RectI(-4, 5, 24, 11), new RectI(7, -3, 11, 14), new RectI(60, 30, 4, 3) })
        {
            var value = new LayerTransform { Origin = new(-2.125, 1.375), Size = size, Sampling = quality, FlipX = flipX, FlipY = flipY, Rotation = rotation };
            var expected = new Surface(43, 29); expected.Pixels.AsSpan().Fill(19); var actual = expected.Clone();
            MaskDisplay.DrawReference(mask, value, expected, band, scale); MaskDisplay.Draw(mask, value, actual, band, scale);
            Assert.Equal(expected.Pixels, actual.Pixels);
        }
        Assert.Equal(source, mask.Pixels);
    }

    [Theory]
    [InlineData(0, 1, 0)] [InlineData(1, 2, 2)] [InlineData(254, 255, 254)]
    public void CubicMidpointsKeepRoundToEven(byte first, byte second, byte expected)
    {
        var mask = new MaskSurface(2, 1, [first, second]); var value = new LayerTransform { Origin = new(-.5, 0), Size = new(4, 1) };
        var output = new Surface(4, 1); MaskDisplay.Draw(mask, value, output, new(0, 0, 4, 1));
        Assert.Equal(expected, output.Pixels[4]); Assert.Equal(255, output.Pixels[7]);
        var reference = new Surface(4, 1); MaskDisplay.DrawReference(mask, value, reference, new(0, 0, 4, 1));
        Assert.Equal(reference.Pixels, output.Pixels);
    }

    [Fact]
    public void ReusedColumnScratchDoesNotRetainAnotherMasksGridOrOutsideColumns()
    {
        foreach (var grid in new[] { new SizeD(91, 7), new SizeD(1, 1), new SizeD(17, 13), new SizeD(3, 71) })
        foreach (var origin in new[] { new PointD(-11.375, -.25), new PointD(24.5, 10.125), new PointD(1000000, -1000000) })
        {
            var mask = new MaskSurface((int)grid.Width, (int)grid.Height); new Random(7301).NextBytes(mask.Pixels);
            var value = new LayerTransform { Origin = origin, Size = new(103.125, 91.5), FlipX = true, FlipY = true };
            var expected = new Surface(101, 46); expected.Pixels.AsSpan().Fill(71); var actual = expected.Clone();
            var band = new RectI(1, 2, 99, 43);
            MaskDisplay.DrawReference(mask, value, expected, band); MaskDisplay.Draw(mask, value, actual, band);
            Assert.Equal(expected.Pixels, actual.Pixels);
        }
    }

    [Fact]
    public void ScaledDisplayPreservesPaddedRowsAndPixelsOutsideTheBand()
    {
        var mask = new MaskSurface(17, 13); new Random(7301).NextBytes(mask.Pixels);
        var value = new LayerTransform { Origin = new(.75, -.125), Size = new(27.125, 22.5), FlipY = true };
        const int stride = 104;
        var expected = new Surface(23, 17, Enumerable.Repeat((byte)71, stride * 17).ToArray(), stride);
        var actual = new Surface(23, 17, expected.Pixels.ToArray(), stride); var band = new RectI(5, 5, 12, 9);
        MaskDisplay.DrawReference(mask, value, expected, band); MaskDisplay.Draw(mask, value, actual, band);
        Assert.Equal(expected.Pixels, actual.Pixels);
    }

    [Fact]
    public void WarmScaledDisplayReusesBoundedScratchWithoutCopyingTheMask()
    {
        var mask = new MaskSurface(2048, 16); new Random(7301).NextBytes(mask.Pixels);
        var value = new LayerTransform { Origin = new(-.25, -.125), Size = new(4096.5, 32.25) };
        var output = new Surface(4096, 32); var band = new RectI(0, 0, 4096, 32);
        for (int i = 0; i < 3; i++) MaskDisplay.Draw(mask, value, output, band);
        // B08's family, and the same shape as the Gaussian sibling above it: the scratch is rented from
        // ArrayPool.Shared, whose per-thread cached bucket a test class running in parallel can empty, and
        // then the next rent allocates — the exact size of the cubic terms array (16,384 × Vector256<double>).
        // One throwaway draw used to be the whole guard and a full-suite run still charged 524,312 bytes to
        // the window after it (docs/b08-warmalloc-red-2026-10-15.txt): the bucket can be taken between that
        // draw and the measurement. Each window below returns what it rents, so a robbed window seeds the next;
        // the median keeps a systematic allocation red and treats the rare steal as the noise it always was.
        // The threshold is where it was.
        var samples = new long[8];
        for (int window = 0; window < samples.Length; window++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread(); MaskDisplay.Draw(mask, value, output, band);
            samples[window] = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        long median = samples.Order().ElementAt(samples.Length / 2);
        Assert.True(median < 65536, $"Warm scaled mask display allocated {median} bytes (median of {string.Join(", ", samples)}).");
    }
}
