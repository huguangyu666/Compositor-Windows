using Compositor.Core;
using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// Numbers for the Tonal Contrast filter, ported from <c>adjust_tonal_contrast</c>
/// (AdjustPixels.c:641-672). Every expected byte comes from Windows/tools/tonal-contrast-golden.py, a second
/// derivation of the same C — two implementations agreeing, not a run of the Mac, which this machine cannot
/// execute. The blurred base is handed in by hand, so the kernel is measured without depending on which
/// Gaussian the app happened to use for its base.
/// </summary>
public class TonalContrastFilterTests
{
    private static Surface Pixel(byte r, byte g, byte b, byte a = 255)
    {
        var surface = new Surface(1, 1);
        surface.Pixels[0] = r;
        surface.Pixels[1] = g;
        surface.Pixels[2] = b;
        surface.Pixels[3] = a;
        return surface;
    }

    private static (byte R, byte G, byte B, byte A) Run(byte level, byte baseLevel, double amount,
        double shadows, double midtones, double highlights, byte alpha = 255, byte baseAlpha = 255)
    {
        var surface = Pixel(level, level, level, alpha);
        TonalContrastFilter.Apply(surface, Pixel(baseLevel, baseLevel, baseLevel, baseAlpha),
            amount, shadows, midtones, highlights);
        return (surface.Pixels[0], surface.Pixels[1], surface.Pixels[2], surface.Pixels[3]);
    }

    [Fact]
    public void TheDefaultsAreTheSheetsOwn()
    {
        // Filters.swift:65-69. Amount 50 is what strength 1.0 means, since the kernel divides by 50.
        Assert.Equal(50, TonalContrastFilter.DefaultAmount);
        Assert.Equal(40, TonalContrastFilter.DefaultShadows);
        Assert.Equal(60, TonalContrastFilter.DefaultMidtones);
        Assert.Equal(30, TonalContrastFilter.DefaultHighlights);
        Assert.Equal(16, TonalContrastFilter.DefaultRadius);
    }

    [Fact]
    public void TheToneWeightsComeFromTheBaseLuminanceAlone()
    {
        // The pixel's own brightness plays no part in which tone band it belongs to: that is read off the
        // blurred copy, which is what keeps a bright edge from being treated as a highlight.
        void Band(double baseLum, double shadow, double midtone, double highlight)
        {
            var found = TonalContrastFilter.Weights(baseLum);
            Assert.Equal(shadow, found.Shadows, precision: 9);
            Assert.Equal(midtone, found.Midtones, precision: 9);
            Assert.Equal(highlight, found.Highlights, precision: 9);
        }
        Band(0.10, 1.0, 0.0, 0.0);
        Band(0.15, 1.0, 0.0, 0.0);
        Band(0.325, 0.5, 0.5, 0.0);
        Band(0.50, 0.0, 1.0, 0.0);
        Band(0.675, 0.0, 0.5, 0.5);
        Band(0.85, 0.0, 0.0, 1.0);
        Band(0.95, 0.0, 0.0, 1.0);
        var (other, middle, upper) = TonalContrastFilter.Weights(0.42);
        Assert.Equal(1.0, other + middle + upper, precision: 9);
    }

    [Fact]
    public void AMidtoneLiftedOverItsOwnBlurGoesUp()
    {
        // grey 128 against a base of 110: baseLum 0.431 is all midtone, so only the midtones slider answers.
        Assert.Equal((139, 139, 139, 255), Run(128, 110, 50, 40, 60, 30));
        Assert.Equal((165, 165, 165, 255), Run(128, 110, 100, 100, 100, 100));
        Assert.Equal((130, 130, 130, 255), Run(128, 110, 50, 100, 0, 0));
        Assert.Equal((111, 111, 111, 255), Run(128, 110, 50, 0, -100, 0));
        // Highlights at 100 does nothing here: the weight is 0 at this base luminance, and the kernel writes
        // back the same bytes rather than skipping the pixel.
        Assert.Equal((128, 128, 128, 255), Run(128, 110, 50, 0, 0, 100));
    }

    [Fact]
    public void TheLuminanceTermFoldsTheEffectOffBlackAndWhite()
    {
        // 4·lum·(1−lum) is the whole reason a flat sky cannot be crunched into a poster: the same detail moves
        // mid grey by 11 levels and 10/245 by 6/4.
        Assert.Equal(0, Run(0, 110, 50, 40, 60, 30).R);
        Assert.Equal(6, Run(10, 110, 50, 40, 60, 30).R);
        Assert.Equal(48, Run(64, 110, 50, 40, 60, 30).R);
        Assert.Equal(139, Run(128, 110, 50, 40, 60, 30).R);
        Assert.Equal(211, Run(192, 110, 50, 40, 60, 30).R);
        Assert.Equal(249, Run(245, 110, 50, 40, 60, 30).R);
        Assert.Equal(255, Run(255, 110, 50, 40, 60, 30).R);
    }

    [Fact]
    public void DetailPastTheKneeStopsPaying()
    {
        // 0.18·tanh(detail·6): doubling the detail from 0.071 to 0.149 buys 6 levels, another 0.157 buys one,
        // and it never reaches the 0.18 asymptote no matter how far the base is pulled away.
        Assert.Equal(128, Run(128, 128, 50, 40, 60, 30).R);
        Assert.Equal(139, Run(128, 110, 50, 40, 60, 30).R);
        Assert.Equal(145, Run(128, 90, 50, 40, 60, 30).R);
        Assert.Equal(146, Run(128, 60, 50, 40, 60, 30).R);
        Assert.Equal(146, Run(128, 20, 50, 40, 60, 30).R);
    }

    [Fact]
    public void GuardsAndSkipsLeaveTheBytesAlone()
    {
        Assert.Equal((128, 128, 128, 255), Run(128, 110, 0, 40, 60, 30));
        // All three tone sliders at zero is the identity, and the sheet treats it as one (Filters.swift:606-607).
        Assert.Equal((128, 128, 128, 255), Run(128, 110, 50, 0, 0, 0));
        // A pixel with no coverage and a base with no coverage are both skipped: the second matters because the
        // blurred copy loses coverage where the layer had none, so it can be the empty one.
        Assert.Equal((0, 0, 0, 0), Run(0, 110, 50, 40, 60, 30, alpha: 0));
        Assert.Equal((128, 128, 128, 255), Run(128, 110, 50, 40, 60, 30, baseAlpha: 0));
        // Half coverage: the straight colour moves by the same delta and is re-premultiplied, so alpha holds.
        Assert.Equal((69, 69, 69, 128), Run(64, 55, 50, 40, 60, 30, alpha: 128, baseAlpha: 128));
    }
}
