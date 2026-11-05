using Compositor.Core;
using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// The two views that replace the picture while Option is held on a slider: Camera Raw's clipping read of the
/// light pass, and the sharpen mask. Numbers come from the macOS kernel — the clip map from
/// <c>AdjustPixels.c:333-344</c>, the mask from <c>AdjustPixels.c:859-891</c>, and which passes still run from
/// <c>CameraRaw.swift:229-241</c> — transcribed in <c>Windows/tools/clipping-golden.py</c>.
/// </summary>
public class CameraRawClippingViewTests
{
    private static Surface Solid(int width, int height, byte r, byte g, byte b, byte a = 255)
    {
        var surface = new Surface(width, height);
        for (int row = 0; row < height; row++)
            for (int column = 0; column < width; column++)
            {
                int index = row * surface.Stride + column * 4;
                surface.Pixels[index] = r;
                surface.Pixels[index + 1] = g;
                surface.Pixels[index + 2] = b;
                surface.Pixels[index + 3] = a;
            }
        return surface;
    }

    /// <summary>Black on the left, white on the right — the sharpest edge a small fixture can hold.</summary>
    private static Surface Step(int size = 8, byte alpha = 255)
    {
        var surface = new Surface(size, size);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                byte value = (byte)(x < size / 2 ? 0 : 255);
                int index = y * surface.Stride + x * 4;
                surface.Pixels[index] = value;
                surface.Pixels[index + 1] = value;
                surface.Pixels[index + 2] = value;
                surface.Pixels[index + 3] = alpha;
            }
        return surface;
    }

    private static byte Pixel(Surface surface, int x, int y, int channel) =>
        surface.Pixels[y * surface.Stride + x * 4 + channel];

    private static (byte r, byte g, byte b) Rgb(Surface surface, int x, int y) =>
        (Pixel(surface, x, y, 0), Pixel(surface, x, y, 1), Pixel(surface, x, y, 2));

    /// <summary>One row, one color per pixel, so a table of expected bytes reads straight off the row.</summary>
    private static Surface Row(params (byte r, byte g, byte b)[] colors)
    {
        var surface = new Surface(colors.Length, 1);
        for (int x = 0; x < colors.Length; x++)
        {
            surface.Pixels[x * 4] = colors[x].r;
            surface.Pixels[x * 4 + 1] = colors[x].g;
            surface.Pixels[x * 4 + 2] = colors[x].b;
            surface.Pixels[x * 4 + 3] = 255;
        }
        return surface;
    }

    [Fact]
    public void OnlyFiveLightRowsCarryAClippingRead()
    {
        Assert.Equal(CameraRawEngine.ClippingHighlights, CameraRawEngine.LightClippingOf(nameof(CameraRawSettings.Exposure)));
        Assert.Equal(CameraRawEngine.ClippingHighlights, CameraRawEngine.LightClippingOf(nameof(CameraRawSettings.Highlights)));
        Assert.Equal(CameraRawEngine.ClippingHighlights, CameraRawEngine.LightClippingOf(nameof(CameraRawSettings.Whites)));
        Assert.Equal(CameraRawEngine.ClippingShadows, CameraRawEngine.LightClippingOf(nameof(CameraRawSettings.Shadows)));
        Assert.Equal(CameraRawEngine.ClippingShadows, CameraRawEngine.LightClippingOf(nameof(CameraRawSettings.Blacks)));
        // Contrast sits between two highlights rows on the Mac's own list and carries nothing (Controls:171).
        Assert.Equal(CameraRawEngine.ClippingNone, CameraRawEngine.LightClippingOf(nameof(CameraRawSettings.Contrast)));
        Assert.Equal(CameraRawEngine.ClippingNone, CameraRawEngine.LightClippingOf(nameof(CameraRawSettings.Temperature)));
        Assert.Equal(CameraRawEngine.ClippingNone, CameraRawEngine.LightClippingOf(nameof(CameraRawSettings.Texture)));
    }

    [Fact]
    public void HighlightsViewLightsOnlyTheClippedChannels()
    {
        var surface = Row((255, 250, 128), (255, 255, 255), (0, 30, 30), (254, 254, 254));
        CameraRawEngine.Apply(surface, new CameraRawSettings(), clipping: CameraRawEngine.ClippingHighlights);
        Assert.Equal((255, 0, 0), Rgb(surface, 0, 0));
        Assert.Equal((255, 255, 255), Rgb(surface, 1, 0));
        Assert.Equal((0, 0, 0), Rgb(surface, 2, 0));
        Assert.Equal((0, 0, 0), Rgb(surface, 3, 0));
    }

    [Fact]
    public void ShadowsViewCutsClippedChannelsOutOfWhite()
    {
        var surface = Row((255, 250, 128), (0, 30, 30), (0, 0, 0), (254, 254, 254));
        CameraRawEngine.Apply(surface, new CameraRawSettings(), clipping: CameraRawEngine.ClippingShadows);
        Assert.Equal((255, 255, 255), Rgb(surface, 0, 0));
        Assert.Equal((0, 255, 255), Rgb(surface, 1, 0));
        Assert.Equal((0, 0, 0), Rgb(surface, 2, 0));
        Assert.Equal((255, 255, 255), Rgb(surface, 3, 0));
    }

    [Fact]
    public void OneStepOffPureIsNotClipped()
    {
        // The kernel compares against 0.5/255 and 254.5/255, so 254 and 1 are both still clean.
        var high = Row((254, 254, 254), (255, 255, 255));
        CameraRawEngine.Apply(high, new CameraRawSettings(), clipping: CameraRawEngine.ClippingHighlights);
        Assert.Equal((0, 0, 0), Rgb(high, 0, 0));
        Assert.Equal((255, 255, 255), Rgb(high, 1, 0));
        var low = Row((1, 1, 1), (0, 0, 0));
        CameraRawEngine.Apply(low, new CameraRawSettings(), clipping: CameraRawEngine.ClippingShadows);
        Assert.Equal((255, 255, 255), Rgb(low, 0, 0));
        Assert.Equal((0, 0, 0), Rgb(low, 1, 0));
    }

    [Fact]
    public void TheClipMapIsReadAfterTheGradeNotBefore()
    {
        var untouched = Row((255, 128, 128));
        CameraRawEngine.Apply(untouched, new CameraRawSettings { Exposure = 0 }, clipping: CameraRawEngine.ClippingHighlights);
        Assert.Equal((255, 0, 0), Rgb(untouched, 0, 0));
        var pulledDown = Row((255, 128, 128));
        CameraRawEngine.Apply(pulledDown, new CameraRawSettings { Exposure = -0.5 }, clipping: CameraRawEngine.ClippingHighlights);
        Assert.Equal((0, 0, 0), Rgb(pulledDown, 0, 0));
    }

    [Fact]
    public void TheClippingViewPaintsEvenWithNothingToGrade()
    {
        // The identity shortcut returns the untouched picture, so this is the witness that the view itself
        // keeps that pass alive: a mid pixel has to go black, and pure white has to stay white.
        var surface = Row((128, 128, 128), (255, 255, 255));
        CameraRawEngine.Apply(surface, new CameraRawSettings(), clipping: CameraRawEngine.ClippingHighlights);
        Assert.Equal((0, 0, 0), Rgb(surface, 0, 0));
        Assert.Equal((255, 255, 255), Rgb(surface, 1, 0));
    }

    /// <summary>Everything but the light numbers: the passes a view is supposed to walk past. The three geometry
    /// numbers are here because the keystone alone is a no-op on a small picture — measured: on an 8×8 black/white
    /// step a −25 vertical leaves every byte where it is, while 64×64 moves 460 of them.</summary>
    private static CameraRawSettings GradedEverywhere() => new()
    {
        Exposure = 1,
        CurveRgb = [new CurvePoint(0, 0), new CurvePoint(0.5, 0.8), new CurvePoint(1, 1)],
        MixerHue = { [3] = 20 },
        GradingMidtonesSat = 30,
        Texture = 40, Clarity = 30, Dehaze = 20, Glow = 50, VignetteAmount = -40, GrainAmount = 60,
        SharpenAmount = 80, NoiseLuminance = 40, NoiseColor = 30,
        OpticsDistortion = -30, OpticsRemoveChromaticAberration = true,
        GeometryUpright = CameraRawSettings.GeometryUprightGuided, GeometryVertical = -25,
        GeometryRotate = 10, GeometryOffsetX = 20, GeometryScale = 50,
        CalibrationRedHue = 20, CalibrationProcess = 5,
    };

    [Fact]
    public void TheClippingViewStopsAtTheLightPass()
    {
        // Exposure stays at zero on purpose: with the frame half pure white and half pure black, the clip map
        // keeps the boundary, so a warp that leaked through would move it. At +1 the whole frame saturates and
        // there is no structure left for a leak to disturb.
        var busy = GradedEverywhere();
        busy.Exposure = 0;
        var lightOnly = Step(64);
        CameraRawEngine.Apply(lightOnly, new CameraRawSettings(), clipping: CameraRawEngine.ClippingHighlights);
        var withViews = Step(64);
        CameraRawEngine.Apply(withViews, busy, clipping: CameraRawEngine.ClippingHighlights);
        Assert.Equal(lightOnly.Pixels.ToList(), withViews.Pixels.ToList());
        // …and the same extra settings with no view on do change the step, or the equality above measures nothing.
        var graded = Step(64);
        CameraRawEngine.Apply(graded, busy);
        Assert.NotEqual(lightOnly.Pixels.ToList(), graded.Pixels.ToList());
    }

    [Fact]
    public void TheSharpenMaskViewStopsAtTheLightPassToo()
    {
        var withMask = Step(64);
        CameraRawEngine.Apply(withMask, new CameraRawSettings { Exposure = 1, SharpenRadius = 10 }, sharpenMask: true);
        var busy = GradedEverywhere();
        busy.SharpenRadius = 10;
        var maskedBusy = Step(64);
        CameraRawEngine.Apply(maskedBusy, busy, sharpenMask: true);
        Assert.Equal(withMask.Pixels.ToList(), maskedBusy.Pixels.ToList());
        // The light pass is not skipped: pull the exposure out and the mask's own grays move with it.
        var dim = Step(64);
        CameraRawEngine.Apply(dim, new CameraRawSettings { Exposure = -3, SharpenRadius = 10 }, sharpenMask: true);
        Assert.NotEqual(withMask.Pixels.ToList(), dim.Pixels.ToList());
    }

    /// <summary>A 3×3 white block on black — the one fixture where the ring radius can show up, because a
    /// straight edge always puts exactly three of the eight ring cells across the step.</summary>
    private static Surface Block(int size = 16, int centre = 8)
    {
        var surface = new Surface(size, size);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                byte value = (byte)(Math.Abs(x - centre) <= 1 && Math.Abs(y - centre) <= 1 ? 255 : 0);
                int index = y * surface.Stride + x * 4;
                surface.Pixels[index] = value;
                surface.Pixels[index + 1] = value;
                surface.Pixels[index + 2] = value;
                surface.Pixels[index + 3] = 255;
            }
        return surface;
    }

    [Fact]
    public void AFlatAreaIsUnsharpenedSoTheMaskIsBlack()
    {
        var flat = Solid(4, 4, 200, 90, 40);
        CameraRawEngine.Apply(flat, new CameraRawSettings { SharpenRadius = 10, SharpenDetail = 50 }, sharpenMask: true);
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
                Assert.Equal((byte)0, Pixel(flat, x, y, 0));
    }

    [Fact]
    public void TheMaskPrintsTheRingDifference()
    {
        var surface = Step();
        CameraRawEngine.Apply(surface, new CameraRawSettings { SharpenRadius = 10, SharpenDetail = 0 }, sharpenMask: true);
        Assert.Equal((byte)137, Pixel(surface, 3, 3, 0));   // interior of the edge: 3 of 8 ring cells differ
        Assert.Equal((byte)146, Pixel(surface, 3, 0, 0));   // top row: the ring is cut to 5 cells, so the mean rises
        Assert.Equal((byte)0, Pixel(surface, 0, 3, 0));     // flat black, nothing to sharpen
        Assert.Equal((byte)255, Pixel(surface, 3, 3, 3));   // alpha untouched
    }

    [Fact]
    public void TheDefaultDetailIsWhatASingleSliderTestReallyGrades()
    {
        // Detail starts at 25, so the boost is 0.75 rather than the 0.5 a hand-worked row would use. Written
        // down because it is the number a slider-only call produces, not the number the formula's name suggests.
        var surface = Step();
        CameraRawEngine.Apply(surface, new CameraRawSettings { SharpenRadius = 10 }, sharpenMask: true);
        Assert.Equal((byte)205, Pixel(surface, 3, 3, 0));
        Assert.Equal((byte)219, Pixel(surface, 3, 0, 0));
    }

    [Fact]
    public void DetailOneHundredSaturatesTheMask()
    {
        var surface = Step();
        CameraRawEngine.Apply(surface, new CameraRawSettings { SharpenRadius = 10, SharpenDetail = 100 }, sharpenMask: true);
        Assert.Equal((byte)255, Pixel(surface, 3, 3, 0));
    }

    [Fact]
    public void MaskingOnlyKillsThatEdgeOnceTheThresholdCatchesTheRing()
    {
        // At the kernel's own detail the same edge is 0.536 in, so masking 70 erases it; at the default 25 the
        // edge is 0.804 in, and masking 70 only takes it down to 41. Both rows are golden numbers.
        foreach (int masking in new[] { 70, 100 })
        {
            var plain = Step();
            CameraRawEngine.Apply(plain, new CameraRawSettings { SharpenRadius = 10, SharpenDetail = 0, SharpenMasking = masking },
                                  sharpenMask: true);
            Assert.Equal((byte)0, Pixel(plain, 3, 3, 0));
        }
        var detailed = Step();
        CameraRawEngine.Apply(detailed, new CameraRawSettings { SharpenRadius = 10, SharpenMasking = 70 }, sharpenMask: true);
        Assert.Equal((byte)41, Pixel(detailed, 3, 3, 0));
        // The divisor's 0.04 floor is out of reach here: masking tops out at 100, which leaves a threshold of
        // 0.35 and so a denominator of 0.175. Recorded rather than asserted, because asserting it would only
        // restate the clamp the line already is.
    }

    [Fact]
    public void TheMaskCarriesTheLayerAlpha()
    {
        var surface = Step(8, 128);
        CameraRawEngine.Apply(surface, new CameraRawSettings { SharpenRadius = 10, SharpenDetail = 0 }, sharpenMask: true);
        Assert.Equal((byte)69, Pixel(surface, 3, 3, 0));   // round(0.535714… × 128)
        Assert.Equal((byte)73, Pixel(surface, 3, 0, 0));   // the cut-ring version, 0.571428… × 128
        Assert.Equal((byte)128, Pixel(surface, 3, 3, 3));
    }

    [Fact]
    public void ARadiusOfOneHundredReadsTheRingThreePixelsOut()
    {
        var near = Block();
        CameraRawEngine.Apply(near, new CameraRawSettings { SharpenRadius = 10, SharpenDetail = 0 }, sharpenMask: true);
        Assert.Equal((byte)0, Pixel(near, 8, 8, 0));      // every cell one out is still inside the block
        Assert.Equal((byte)137, Pixel(near, 7, 8, 0));

        var far = Block();
        CameraRawEngine.Apply(far, new CameraRawSettings { SharpenRadius = 100, SharpenDetail = 0 }, sharpenMask: true);
        Assert.Equal((byte)255, Pixel(far, 8, 8, 0));     // the ring three out is all black: maximum edge
        Assert.Equal((byte)46, Pixel(far, 6, 8, 0));
    }

    // --- The permanent indicators (AdjustPixels.c:502-524) ---

    [Fact]
    public void ClippedShadowsArePaintedBlue()
    {
        var surface = Row((0, 0, 0));
        CameraRawEngine.ClipOverlay(surface, shadows: true, highlights: false);
        Assert.Equal((0, 0, 166), Rgb(surface, 0, 0));    // b = 0 × 0.35 + 0.65
    }

    [Fact]
    public void ClippedHighlightsArePaintedRed()
    {
        var surface = Row((255, 255, 255));
        CameraRawEngine.ClipOverlay(surface, shadows: false, highlights: true);
        Assert.Equal((255, 89, 89), Rgb(surface, 0, 0));  // r stays lit, the others fall to 0.35
    }

    [Fact]
    public void OnlyAPureClipCountsOneStepOffIsClean()
    {
        var surface = Row((1, 1, 1), (254, 254, 254), (128, 128, 128));
        CameraRawEngine.ClipOverlay(surface, shadows: true, highlights: true);
        Assert.Equal((1, 1, 1), Rgb(surface, 0, 0));
        Assert.Equal((254, 254, 254), Rgb(surface, 1, 0));
        Assert.Equal((128, 128, 128), Rgb(surface, 2, 0));
    }

    [Fact]
    public void AnyClippedChannelTintsTheWholePixel()
    {
        var surface = Row((255, 255, 0));
        CameraRawEngine.ClipOverlay(surface, shadows: true, highlights: false);
        Assert.Equal((89, 89, 166), Rgb(surface, 0, 0));
    }

    [Fact]
    public void TheHighlightTestReadsWhatTheShadowBranchJustWrote()
    {
        // A pixel clipped in both directions ends up blue only: the second branch tests the painted values,
        // and 0.35 / 0.65 are no longer clips. Reading the original values instead would paint it red as well.
        var surface = Row((255, 255, 0));
        CameraRawEngine.ClipOverlay(surface, shadows: true, highlights: true);
        Assert.Equal((89, 89, 166), Rgb(surface, 0, 0));
    }

    [Fact]
    public void TheOverlayRidesTheLayersOwnAlpha()
    {
        var surface = Solid(1, 1, 0, 0, 0, 128);
        CameraRawEngine.ClipOverlay(surface, shadows: true, highlights: false);
        Assert.Equal((byte)0, Pixel(surface, 0, 0, 0));
        Assert.Equal((byte)83, Pixel(surface, 0, 0, 2));   // round(0.65 × 128)
        Assert.Equal((byte)128, Pixel(surface, 0, 0, 3));
    }

    [Fact]
    public void AskingForNoOverlayChangesNoByte()
    {
        var surface = Row((0, 0, 0), (255, 255, 255));
        var bytes = surface.Pixels.ToList();
        CameraRawEngine.ClipOverlay(surface, shadows: false, highlights: false);
        Assert.Equal(bytes, surface.Pixels.ToList());
    }
}
