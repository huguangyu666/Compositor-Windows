using Xunit;

namespace Compositor.Core.Tests;

public class SelectionFeatherVectorTests
{
    [Theory]
    [InlineData(7, 67, 1)]
    [InlineData(8, 67, 7)]
    [InlineData(9, 67, 1)]
    [InlineData(16, 37, 3)]
    [InlineData(17, 37, 7)]
    [InlineData(65, 31, 16)]
    [InlineData(30000, 8, 1)]
    [InlineData(8, 30000, 100000)]
    public void CompleteCoverageMatchesOriginalAcrossVectorTailsAndWideCounts(int width, int height, double feather)
    {
        var mask = new MaskSurface(width, height); new Random(3907).NextBytes(mask.Pixels);
        var original = mask.Pixels.ToArray();
        var expected = SelectionFeatherReference.Render(mask, feather).Pixels;
        var selection = new Selection(mask, new(0, 0, width, height), feather);
        var actual = new byte[mask.Pixels.Length];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) actual[y * width + x] = selection.CoverageAt(x, y);
        Assert.Equal(expected, actual);
        Assert.Equal(original, mask.Pixels);
    }
}
