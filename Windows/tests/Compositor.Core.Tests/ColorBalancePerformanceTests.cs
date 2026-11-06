using Xunit;

namespace Compositor.Core.Tests;

public class ColorBalancePerformanceTests
{
    [Fact]
    public void PhotoColorBalanceDoesNotAllocatePerPixel()
    {
        var surface = new Surface(256, 256);
        Array.Fill(surface.Pixels, (byte)255);
        var adjustment = new LayerAdjustment { Kind = AdjustmentKind.ColorBalance,
            ColorBalanceSettings = new() { MidCyanRed = 5, ShadowYellowBlue = 3 } };
        AdjustmentEngine.Apply(adjustment, surface, 0, 0, surface.Width, surface.Height);
        long before = GC.GetAllocatedBytesForCurrentThread();
        AdjustmentEngine.Apply(adjustment, surface, 0, 0, surface.Width, surface.Height);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(bytes < 32_768, $"Color Balance allocated {bytes} bytes for one photo frame.");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ColorBalanceMatchesOriginalMathWithPaddingAndPartialRegions(bool preserve, bool opaque)
    {
        const int width = 521, height = 517, stride = width * 4 + 12;
        var random = new Random(739);
        var pixels = new byte[stride * height]; random.NextBytes(pixels);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int i = y * stride + x * 4;
            byte alpha = opaque ? (byte)255 : (byte)(x % 256);
            pixels[i + 3] = alpha;
            for (int c = 0; c < 3; c++) pixels[i + c] = (byte)Math.Min(alpha, pixels[i + c]);
        }
        var settings = new ColorBalanceSettings { ShadowCyanRed = -75, ShadowMagentaGreen = 12, ShadowYellowBlue = 28,
            MidCyanRed = 30, MidMagentaGreen = -24, MidYellowBlue = 7,
            HighlightCyanRed = 65, HighlightMagentaGreen = -83, HighlightYellowBlue = -18, PreserveLuminosity = preserve };
        var expected = new Surface(width, height, pixels.ToArray(), stride);
        var actual = new Surface(width, height, pixels, stride);
        Original(settings, expected, 3, 2, width - 2, height - 1);
        AdjustmentEngine.Apply(new() { Kind = AdjustmentKind.ColorBalance, ColorBalanceSettings = settings }, actual, 3, 2, width - 2, height - 1);
        Assert.Equal(expected.Pixels, actual.Pixels);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpaqueChannelPairsKeepOriginalRoundingAndBlackLuminosity(bool preserve)
    {
        byte[] blues = [0, 1, 31, 127, 128, 254, 255];
        var input = new Surface(259, blues.Length * 256);
        Array.Fill(input.Pixels, (byte)173);
        for (int y = 0; y < input.Height; y++)
        for (int x = 1; x <= 256; x++)
        {
            int i = y * input.Stride + x * 4;
            input.Pixels[i] = (byte)(x - 1);
            input.Pixels[i + 1] = (byte)(y % 256);
            input.Pixels[i + 2] = blues[y / 256];
            input.Pixels[i + 3] = 255;
        }
        foreach (var settings in new[] {
            new ColorBalanceSettings { ShadowCyanRed = -2, ShadowYellowBlue = 2, MidCyanRed = 1,
                MidYellowBlue = -2, HighlightCyanRed = 4, HighlightYellowBlue = -5, PreserveLuminosity = preserve },
            new ColorBalanceSettings { ShadowCyanRed = -100, ShadowMagentaGreen = -100, ShadowYellowBlue = -100,
                MidCyanRed = -100, MidMagentaGreen = -100, MidYellowBlue = -100,
                HighlightCyanRed = -100, HighlightMagentaGreen = -100, HighlightYellowBlue = -100, PreserveLuminosity = preserve } })
        {
            var expected = input.Clone(); var actual = input.Clone();
            Original(settings, expected, 1, 0, 257, input.Height);
            AdjustmentEngine.Apply(new() { Kind = AdjustmentKind.ColorBalance, ColorBalanceSettings = settings }, actual, 1, 0, 257, input.Height);
            Assert.Equal(expected.Pixels, actual.Pixels);
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(8)]
    [InlineData(19)]
    public void OpaqueRowsWithPartialAlphaKeepScalarFallback(int partialX)
    {
        var input = new Surface(37, 3);
        new Random(91).NextBytes(input.Pixels);
        for (int y = 0; y < input.Height; y++)
        for (int x = 0; x < input.Width; x++)
        {
            int i = y * input.Stride + x * 4;
            byte alpha = x == partialX ? (byte)(y * 127) : (byte)255;
            input.Pixels[i + 3] = alpha;
            for (int c = 0; c < 3; c++) input.Pixels[i + c] = Math.Min(input.Pixels[i + c], alpha);
        }
        var settings = new ColorBalanceSettings { MidCyanRed = 31, ShadowMagentaGreen = -19, HighlightYellowBlue = 70 };
        var expected = input.Clone(); var actual = input.Clone();
        Original(settings, expected, 1, 0, 36, input.Height);
        AdjustmentEngine.Apply(new() { Kind = AdjustmentKind.ColorBalance, ColorBalanceSettings = settings }, actual, 1, 0, 36, input.Height);
        Assert.Equal(expected.Pixels, actual.Pixels);
    }

    // Frozen scalar formulas from the original AdjustPixels.c port, including operation order.
    private static void Original(ColorBalanceSettings settings, Surface surface, int x0, int y0, int x1, int y1)
    {
        float[] shadows = [(float)(settings.ShadowCyanRed / 100), (float)(settings.ShadowMagentaGreen / 100), (float)(settings.ShadowYellowBlue / 100)];
        float[] midtones = [(float)(settings.MidCyanRed / 100), (float)(settings.MidMagentaGreen / 100), (float)(settings.MidYellowBlue / 100)];
        float[] highlights = [(float)(settings.HighlightCyanRed / 100), (float)(settings.HighlightMagentaGreen / 100), (float)(settings.HighlightYellowBlue / 100)];
        for (int y = y0; y < y1; y++)
        for (int x = x0; x < x1; x++)
        {
            int index = y * surface.Stride + x * 4;
            float alpha = surface.Pixels[index + 3];
            if (alpha == 0) continue;
            float[] c = new float[3];
            for (int i = 0; i < 3; i++) c[i] = Math.Min(255f, surface.Pixels[index + i] * 255f / alpha) / 255f;
            float before = 0.299f * c[0] + 0.587f * c[1] + 0.114f * c[2];
            for (int i = 0; i < 3; i++)
            {
                float v = c[i];
                float s = Math.Clamp((v - .333f) / -.25f + .5f, 0f, 1f) * .7f;
                float h = Math.Clamp((v + .333f - 1f) / .25f + .5f, 0f, 1f) * .7f;
                float m = Math.Clamp((v - .333f) / .25f + .5f, 0f, 1f) * Math.Clamp((v + .333f - 1f) / -.25f + .5f, 0f, 1f) * .7f;
                c[i] = Math.Clamp(v + shadows[i] * s + midtones[i] * m + highlights[i] * h, 0f, 1f);
            }
            if (settings.PreserveLuminosity)
            {
                float after = .299f * c[0] + .587f * c[1] + .114f * c[2];
                if (after > .0001f)
                {
                    float ratio = before / after;
                    for (int i = 0; i < 3; i++) c[i] = Math.Clamp(c[i] * ratio, 0f, 1f);
                }
            }
            for (int i = 0; i < 3; i++) surface.Pixels[index + i] = (byte)Math.Clamp(MathF.Round(c[i] * alpha, MidpointRounding.AwayFromZero), 0f, alpha);
        }
    }
}
