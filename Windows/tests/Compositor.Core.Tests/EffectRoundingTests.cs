using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// D10: what rounding actually does at the effect sites. Two findings are pinned here, and both were
/// measured rather than assumed: the color-to-byte conversion has no observable tie to break, while
/// the shadow's whole-pixel coverage shift does — and that shift is an approximation the macOS
/// original never makes, because it hands Core Graphics a fractional offset.
/// </summary>
public class EffectRoundingTests
{
    private static Surface Opaque(byte value, int size = 8)
    {
        var surface = new Surface(size, size);
        for (int i = 0; i < surface.Pixels.Length; i += 4)
        {
            surface.Pixels[i] = value; surface.Pixels[i + 1] = value; surface.Pixels[i + 2] = value; surface.Pixels[i + 3] = 255;
        }
        return surface;
    }

    private static LayerEffects ShadowAt(double distance) => new()
    {
        // Angle 180 puts the shadow exactly to the right: dx = distance, dy ≈ 1e-16.
        Shadow = new ShadowEffect { Angle = 180, Distance = distance, Blur = 0, Opacity = 1, Red = 0, Green = 0, Blue = 0 },
    };

    [Fact]
    public void HalfPixelShadowOffsetMatchesNeitherNeighbouringWholePixel()
    {
        // Measured, not assumed: a 2.5 px shadow is byte-different from both 2 px and 3 px, so the
        // fractional distance still reaches something in the pipeline even though the coverage offset
        // and the reach both round it (LayerEffectsRenderer.cs CoverageAlpha / ShadowReach). The
        // macOS original never rounds — it hands Core Graphics offsetBy(dx: 2.5) and the rasterizer
        // antialiases. Which site keeps the fraction here is NOT pinned down; recorded as open in the
        // D10 report rather than "fixed" by forcing one rounding rule on both.
        var half = LayerEffectsRenderer.Render(Opaque(200), null, ShadowAt(2.5));
        var whole = LayerEffectsRenderer.Render(Opaque(200), null, ShadowAt(2));
        var beyond = LayerEffectsRenderer.Render(Opaque(200), null, ShadowAt(3));
        Assert.False(whole.Result.Pixels.AsSpan().SequenceEqual(half.Result.Pixels));
        Assert.False(beyond.Result.Pixels.AsSpan().SequenceEqual(half.Result.Pixels));
        Assert.False(whole.Result.Pixels.AsSpan().SequenceEqual(beyond.Result.Pixels));
    }

    [Fact]
    public void TheOnlyExactlyRepresentableMidpointOfAByteConversionIsHalf()
    {
        // x * 255 lands exactly on k + 0.5 only when x is dyadic, and in 0–1 that leaves x = 0.5 → 127.5.
        // Away-from-zero and banker's both give 128, so this records that the byte sites have no tie
        // to disagree about — the rounding mode there is documentation, not a behavior change.
        var effects = new LayerEffects { Shadow = new ShadowEffect { Red = 0.5, Green = 0.5, Blue = 0.5 } };
        var color = effects.Color(LayerEffectKind.Shadow);
        Assert.Equal(128, color.R);
        Assert.Equal(128, color.G);
        Assert.Equal(128, color.B);
    }

    [Fact]
    public void ShadowBytesLandOnTheSameValueThroughEitherRoundingRule()
    {
        // The neighbors of the midpoint: 0.4960784 (126.5/255) cannot be represented exactly, so the
        // product is not a tie and both rules agree. Pinning this keeps a future "fix" from claiming
        // a difference these sites cannot produce.
        foreach (var value in new[] { 126.5 / 255, 124.5 / 255, 0.4, 0.99 })
        {
            var effects = new LayerEffects { Shadow = new ShadowEffect { Red = value, Green = value, Blue = value } };
            var color = effects.Color(LayerEffectKind.Shadow);
            var expected = (int)System.Math.Floor(value * 255 + 0.5);
            Assert.Equal((byte)expected, color.R);
            Assert.Equal((byte)expected, color.G);
            Assert.Equal((byte)expected, color.B);
        }
    }
}
