using System.Security.Cryptography;
using Xunit;

namespace Compositor.Core.Tests;

public class LassoScanTests
{
    [Theory]
    [InlineData(false, "50A6090F16EA8F5B98C073CC4685988DB59FFCF36ACD5D6A88E4E52F37067A85")]
    [InlineData(true, "7AB62D70A9729B16F99913BE89D857691A35BE06B5B8C485C3233BAD73498F4C")]
    public void CoverageAndBoundsRetainOriginalScanlineResults(bool antialiased, string expected)
    {
        // Frozen from 169e5bb: random, crossing, repeated, clipped and subpixel-boundary paths.
        using var combined = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var points in LassoScanScenario.Fixtures())
        {
            var selection = LassoSelection.Rasterize(64, 65, points, antialiased);
            combined.AppendData(selection.Coverage.Pixels);
            foreach (int value in new[] { selection.Bounds.X, selection.Bounds.Y, selection.Bounds.Width, selection.Bounds.Height })
                combined.AppendData(BitConverter.GetBytes(value));
        }
        Assert.Equal(expected, Convert.ToHexString(combined.GetHashAndReset()));
    }
}
