using Xunit;

namespace Compositor.Core.Tests;

public class MaskedPointAdjustmentTests
{
    [Theory]
    [InlineData(AdjustmentKind.Exposure, false)]
    [InlineData(AdjustmentKind.Exposure, true)]
    [InlineData(AdjustmentKind.Curves, false)]
    [InlineData(AdjustmentKind.Curves, true)]
    [InlineData(AdjustmentKind.Levels, false)]
    [InlineData(AdjustmentKind.Levels, true)]
    public void FusedPointAdjustmentKeepsOriginalRoundingAtEveryCoverageAndAlpha(AdjustmentKind kind, bool opaque)
    {
        const int width = 521, height = 517, stride = width * 4 + 12;
        var random = new Random(73); var pixels = new byte[stride * height]; random.NextBytes(pixels);
        var mask = new MaskSurface(width, height); random.NextBytes(mask.Pixels);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int i = y * stride + x * 4;
            pixels[i + 3] = opaque ? (byte)255 : (byte)(x % 256);
            for (int c = 0; c < 3; c++) pixels[i + c] = (byte)Math.Min(pixels[i + c], pixels[i + 3]);
            if (x % 3 < 2) mask.Pixels[y * width + x] = x % 3 == 0 ? (byte)0 : (byte)255;
        }
        var original = new Surface(width, height, pixels.ToArray(), stride);
        var expected = original.Clone(); var actual = original.Clone();
        var adjustment = new LayerAdjustment { Kind = kind, ExposureSettings = new() { Exposure = .28 } };
        adjustment.Levels.Ranges[0].Black = 17; adjustment.Levels.Ranges[0].Gamma = 1.3;
        adjustment.Curves.Channels[0] = [new(0, 3), new(80, 61), new(160, 182), new(255, 249)];
        const int x0 = 3, y0 = 2, x1 = width - 2, y1 = height - 1;
        AdjustmentEngine.Apply(adjustment, expected, x0, y0, x1, y1);
        for (int y = y0; y < y1; y++)
        for (int x = x0; x < x1; x++)
        {
            int i = y * stride + x * 4, coverage = mask.Pixels[y * width + x];
            for (int c = 0; c < 4; c++)
                expected.Pixels[i + c] = (byte)((expected.Pixels[i + c] * coverage + pixels[i + c] * (255 - coverage) + 127) / 255);
        }
        Assert.True(AdjustmentEngine.TryApplyMaskedPoint(adjustment, mask, actual, x0, y0, x1, y1));
        Assert.Equal(expected.Pixels, actual.Pixels);
        Assert.Equal(pixels, original.Pixels);
    }
}
