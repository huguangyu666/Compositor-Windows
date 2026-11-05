using System.Reflection;
using Xunit;

namespace Compositor.Core.Tests;

public class EffectBlitByteTests
{
    private delegate void Blend(Surface output, byte[] alpha, byte[]? mask, Surface source, RectI window, int inset, bool opaque);
    private static readonly Blend Blit = typeof(LayerEffectsRenderer).GetMethod("Blit", BindingFlags.Static | BindingFlags.NonPublic)!.CreateDelegate<Blend>();

    [Theory]
    [InlineData(1, 1, false)]
    [InlineData(1, 1, true)]
    [InlineData(3, 5, false)]
    [InlineData(3, 5, true)]
    [InlineData(17, 9, false)]
    [InlineData(17, 9, true)]
    public void MaskedAndUnmaskedChannelsKeepScalarBytesWithStrideAndClipping(int width, int height, bool masked)
    {
        var source = Pattern(width, height, 12); var output = Pattern(width + 7, height + 5, 8);
        var expected = output.Pixels.ToArray(); var sourceBefore = source.Pixels.ToArray();
        var alpha = new byte[output.Width * output.Height]; new Random(7301).NextBytes(alpha);
        var mask = masked ? new byte[alpha.Length] : null;
        if (mask is not null) new Random(7302).NextBytes(mask);
        var window = new RectI(-2, 1, output.Width, output.Height); const int inset = 2;
        for (int y = 0; y < output.Height; y++) for (int x = 0; x < output.Width; x++)
        {
            int sx = x + window.X - inset, sy = y + window.Y - inset;
            if ((uint)sx >= (uint)source.Width || (uint)sy >= (uint)source.Height) continue;
            double a = alpha[y * output.Width + x] / 255.0;
            if (a == 0) continue;
            double scale = (mask?[y * output.Width + x] ?? 255) / 255.0;
            for (int c = 0; c < 4; c++)
            {
                int input = sy * source.Stride + sx * 4 + c, target = y * output.Stride + x * 4 + c;
                expected[target] = (byte)Math.Round(source.Pixels[input] * scale + expected[target] * (1 - a), MidpointRounding.AwayFromZero);
            }
        }
        Blit(output, alpha, mask, source, window, inset, false);
        Assert.Equal(expected, output.Pixels); Assert.Equal(sourceBefore, source.Pixels);
    }

    [Fact]
    public void SeparateCoverageQuantizationKeepsOriginalUncheckedByteConversion()
    {
        var source = new Surface(1, 1); source.Pixels.AsSpan().Fill(2);
        var output = new Surface(1, 1); output.Pixels.AsSpan().Fill(255);
        Blit(output, [1], [254], source, new(0, 0, 1, 1), 0, false);
        Assert.All(output.Pixels, channel => Assert.Equal(0, channel));
    }

    [Fact]
    public void ZeroShapeCoverageLeavesDestinationAndPaddingUntouched()
    {
        var source = Pattern(7, 5, 12); var output = Pattern(7, 5, 8); var before = output.Pixels.ToArray();
        var mask = new byte[35]; new Random(7302).NextBytes(mask);
        Blit(output, new byte[35], mask, source, new(0, 0, 7, 5), 0, false);
        Assert.Equal(before, output.Pixels);
    }

    private static Surface Pattern(int width, int height, int padding)
    {
        int stride = width * 4 + padding; var bytes = new byte[stride * height]; new Random(7303).NextBytes(bytes);
        return new(width, height, bytes, stride);
    }
}
