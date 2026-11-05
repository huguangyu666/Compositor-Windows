using Xunit;

namespace Compositor.Core.Tests;

public class SelectionFeatherDivisionTests
{
    [Fact]
    public void ReciprocalPreservesQuotientBoundariesForEverySupportedCount()
    {
        var method = typeof(Selection).GetMethod("DivideCoverage", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var divide = method.CreateDelegate<Func<uint, ulong, byte>>();
        for (uint count = 1; count <= Limits.MaxSide; count++)
        {
            ulong reciprocal = ((1UL << 48) + count - 1) / count;
            for (uint quotient = 0; quotient < 256; quotient++)
            {
                uint start = quotient * count;
                if (divide(start, reciprocal) != quotient || divide(start + count / 2, reciprocal) != quotient
                    || divide(start + count - 1, reciprocal) != quotient)
                    Assert.Fail($"Reciprocal changed quotient {quotient} at count {count}.");
            }
        }
    }

    [Theory]
    [InlineData(30000, 1, 60000)]
    [InlineData(1, 30000, 60000)]
    [InlineData(30000, 1, 250)]
    [InlineData(1, 30000, 250)]
    [InlineData(129, 257, 128)]
    [InlineData(257, 129, 128)]
    public void LargestSampleCountsAndClippedRampsMatchOriginalIntegerDivision(int width, int height, double feather)
    {
        var mask = new MaskSurface(width, height); new Random(78123).NextBytes(mask.Pixels);
        var before = mask.Pixels.ToArray();
        var expected = SelectionFeatherReference.Render(mask, feather);
        var selection = new Selection(mask, new(0, 0, width, height), feather);
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            Assert.Equal(expected.Pixels[y * width + x], selection.CoverageAt(x, y));
        Assert.Equal(before, mask.Pixels);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(254)]
    [InlineData(255)]
    public void UniformCoverageSurvivesMaximumDenominatorExactly(byte value)
    {
        var mask = new MaskSurface(30000, 1); mask.Pixels.AsSpan().Fill(value);
        var selection = new Selection(mask, new(0, 0, 30000, 1), 60000);
        for (int x = 0; x < 30000; x++) Assert.Equal(value, selection.CoverageAt(x, 0));
    }
}
