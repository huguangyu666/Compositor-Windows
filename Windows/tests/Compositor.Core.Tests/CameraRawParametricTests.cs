using Compositor.Core;
using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// Numeric locks for the Camera Raw curve's Parametric page: which region a tone belongs to, the two drags
/// inside the graph (a region at 200 slider units per full height, a divider that cannot pass its neighbour),
/// the floors the kernel puts on the dividers, and the fact that the graph and the kernel do not read the same
/// three numbers. Transcribed from <c>region(for:)</c> and <c>normalized</c> (CameraRawColor.swift:103-125),
/// <c>parametric</c> / <c>bend</c> (:52-74) and the graph's own gesture
/// (CameraRawColorControls.swift:150-159, 187-199); the numbers are the ones
/// Windows/tools/parametric-golden.py prints. Two independent implementations agreeing, not a run of the Mac.
/// </summary>
public class CameraRawParametricTests
{
    /// <summary>The rec709 tone of the green the probe's bands are painted with (CameraRaw.swift:634).</summary>
    private const double GreenTone = 0.2126 * 60 / 255 + 0.7152 * 220 / 255 + 0.0722 * 90 / 255;

    private static CameraRawSettings Curve(double shadows = 0, double darks = 0, double lights = 0,
        double highlights = 0, double shadowSplit = 25, double darkSplit = 50, double lightSplit = 75) => new()
    {
        CurveShadows = shadows,
        CurveDarks = darks,
        CurveLights = lights,
        CurveHighlights = highlights,
        CurveShadowSplit = shadowSplit,
        CurveDarkSplit = darkSplit,
        CurveLightSplit = lightSplit,
    };

    /// <summary>The graph's curve at an exact tone. The panel draws 64 samples, but the function underneath is
    /// the same one, so the tones the golden names are probed on a hundred-step grid instead of rounding a
    /// pointer position onto the panel's sixty-four.</summary>
    private static double Graph(CameraRawSettings settings, double tone) =>
        CameraRawEngine.ParametricSamples(settings, 101)[(int)Math.Round(tone * 100)];

    [Fact]
    public void AToneExactlyOnADividerBelongsToTheRegionAboveIt()
    {
        // The Mac's three tests are strict `<` (CameraRawColor.swift:104-106), so 0.25 is not in the shadows
        // and 0.5 is not in the darks.
        Assert.Equal(0, CameraRawTargetedDrag.Region(0.0, 25, 50, 75));
        Assert.Equal(0, CameraRawTargetedDrag.Region(0.249999, 25, 50, 75));
        Assert.Equal(1, CameraRawTargetedDrag.Region(0.25, 25, 50, 75));
        Assert.Equal(2, CameraRawTargetedDrag.Region(0.5, 25, 50, 75));
        Assert.Equal(2, CameraRawTargetedDrag.Region(0.749999, 25, 50, 75));
        Assert.Equal(3, CameraRawTargetedDrag.Region(0.75, 25, 50, 75));
        Assert.Equal(3, CameraRawTargetedDrag.Region(1.0, 25, 50, 75));
        // And the same test against a moved pair of dividers, in the panel's own scale.
        Assert.Equal(1, CameraRawTargetedDrag.Region(0.25, 10, 50, 90));
        Assert.Equal(1, CameraRawTargetedDrag.Region(0.4, 10, 50, 90));
    }

    [Fact]
    public void TheParametricDragMovesTheOneRegionThePressLandedInAndNoOther()
    {
        var settings = new CameraRawSettings();
        CameraRawTargetedDrag.ApplyCurveRegion(settings, GreenTone, -7);
        // 0.692541 is under the light divider at 0.75, so the whole delta goes to Lights and none of it is shared.
        Assert.Equal(-7.0, settings.CurveLights, 9);
        Assert.Equal(0.0, settings.CurveShadows, 9);
        Assert.Equal(0.0, settings.CurveDarks, 9);
        Assert.Equal(0.0, settings.CurveHighlights, 9);
        Assert.True(settings.AdjustsCurve);
    }

    [Fact]
    public void ARegionDragIsTwentySliderUnitsForAFullGraphHeightNotTwoHundredPixels()
    {
        // The panel's own law (CameraRawColorControls.swift:198): a tenth of the box is 20 units, and the
        // 0.35 that the picture uses does not appear anywhere here.
        Assert.Equal(20.0, CameraRawTargetedDrag.RegionDrag(0, -25.5, 255), 9);
        Assert.Equal(-20.0, CameraRawTargetedDrag.RegionDrag(0, 25.5, 255), 9);
        Assert.Equal(100.0, CameraRawTargetedDrag.RegionDrag(90, -25.5, 255), 9);
        Assert.Equal(-100.0, CameraRawTargetedDrag.RegionDrag(-90, 25.5, 255), 9);
        Assert.Equal(100.0, CameraRawTargetedDrag.RegionDrag(0, -2000, 255), 9);
    }

    [Fact]
    public void AHalfUnitOfGraphDragRoundsAwayFromZeroOnlyWhenItIsExactlyAHalf()
    {
        // Swift's `Double.rounded()` is true nearest with ties away from zero (:199). A tenth-of-a-box
        // measurement is not a tie: 0.6375/255*200 is 0.49999999999999994, so it goes to 0, while
        // 1.0/400*200 is exactly 0.5 and goes to 1. This row is the one that caught my golden, whose
        // `floor(x + 0.5)` said 1 for the first case and disagreed with the port.
        Assert.Equal(0.0, CameraRawTargetedDrag.RegionDrag(0, -0.6375, 255), 9);
        Assert.Equal(0.0, CameraRawTargetedDrag.RegionDrag(0, 0.6375, 255), 9);
        Assert.Equal(1.0, CameraRawTargetedDrag.RegionDrag(0, -1.0, 400), 9);
        Assert.Equal(-1.0, CameraRawTargetedDrag.RegionDrag(0, 1.0, 400), 9);
    }

    [Fact]
    public void ADividerStopsTwoPointsFromItsNeighbourOnTheSideThatWouldCross()
    {
        double[] splits = [25, 50, 75];
        CameraRawTargetedDrag.DividerDrag(0, 0.6, 1, splits);
        Assert.Equal(48.0, splits[0], 9);
        Assert.Equal(50.0, splits[1], 9);
        splits = [25, 50, 75];
        CameraRawTargetedDrag.DividerDrag(1, 0.9, 1, splits);
        Assert.Equal(73.0, splits[1], 9);
        Assert.Equal(75.0, splits[2], 9);
        splits = [25, 50, 75];
        CameraRawTargetedDrag.DividerDrag(2, 0.1, 1, splits);
        Assert.Equal(52.0, splits[2], 9);
        // The pointer's own floor and ceiling are 2 and 98, whatever the neighbours say (:188).
        splits = [25, 50, 75];
        CameraRawTargetedDrag.DividerDrag(0, 0.005, 1, splits);
        Assert.Equal(2.0, splits[0], 9);
        splits = [25, 50, 75];
        CameraRawTargetedDrag.DividerDrag(2, 0.999, 1, splits);
        Assert.Equal(98.0, splits[2], 9);
        // A middle divider parked exactly where it is asked to stay is not moved at all.
        splits = [10, 50, 90];
        CameraRawTargetedDrag.DividerDrag(1, 0.5, 1, splits);
        Assert.Equal(50.0, splits[1], 9);
    }

    [Fact]
    public void EachDividerFloorIsThePreviousDividerPlusTwo()
    {
        // CameraRawColor.swift:117-119, which the kernel applies and the panel's graph does not.
        Assert.Equal((5.0, 7.0, 9.0), CameraRawEngine.NormalizedSplits(Curve(shadowSplit: 0, darkSplit: 0, lightSplit: 0)));
        Assert.Equal((5.0, 7.0, 9.0), CameraRawEngine.NormalizedSplits(Curve(shadowSplit: 5, darkSplit: 6, lightSplit: 7)));
        Assert.Equal((60.0, 62.0, 64.0), CameraRawEngine.NormalizedSplits(Curve(shadowSplit: 60, darkSplit: 30, lightSplit: 20)));
        Assert.Equal((90.0, 95.0, 98.0), CameraRawEngine.NormalizedSplits(Curve(shadowSplit: 100, darkSplit: 100, lightSplit: 100)));
        // A divider that is not a number falls back to the one the Mac defaults to.
        Assert.Equal((25.0, 40.0, 60.0), CameraRawEngine.NormalizedSplits(
            Curve(shadowSplit: double.NaN, darkSplit: 40, lightSplit: 60)));
    }

    [Fact]
    public void DarksMinusFiftyOneDipsTheQuarterToneToJustUnderPointOneFive()
    {
        // The Mac's own doc comment (CameraRawColor.swift:63) says Darks -51 dips about 0.1 a quarter of the
        // way up; a quarter is anchor 8 of 33, so the smooth curve passes through the bend exactly.
        Assert.Equal(0.143760778, Graph(Curve(darks: -51), 0.25), 9);
        Assert.Equal(0.199541927, Graph(Curve(darks: -51), 0.3), 9);
        Assert.Equal(0.130682778, Graph(Curve(shadows: 30), 0.1), 9);
        Assert.Equal(0.931489742, Graph(Curve(highlights: 30), 0.9), 9);
    }

    [Fact]
    public void FourZerosLeaveTheToneAloneAndADividerWithNothingToBendIsInert()
    {
        Assert.Equal(0.4, Graph(Curve(), 0.4), 9);
        // Shadows at zero bends by 2**0, which is the identity, so where its divider sits cannot be seen.
        var stored = Curve(shadowSplit: 2, lights: 50);
        Assert.Equal(Graph(stored, 0.03), Graph(Curve(shadowSplit: 5, lights: 50), 0.03), 9);
        // The same pair of dividers with Shadows doing the work is not inert: the graph is drawn from the
        // stored number while the kernel floors it (0.030000000 against 0.034606043).
        Assert.Equal(0.03, Graph(Curve(shadows: 30, shadowSplit: 2), 0.03), 9);
        Assert.Equal(0.034606043, Graph(Curve(shadows: 30, shadowSplit: 5), 0.03), 9);
    }

    [Fact]
    public void TheKernelPutsAnAmountBeyondItsRangeBackInsideBeforeItBends()
    {
        // `normalized` clamps the four amounts to +-100 on the way into the kernel, so a 150 that a panel or a
        // file let through paints the same pixels as a 100. The graph deliberately does not clamp, so this is
        // the render's side only. The pixel has to sit deep inside the shadows: right under the divider the two
        // exponents agree to within a fraction of one byte (60 in, 102 out, for both), and the comparison would
        // then be an identity rather than a claim.
        var wide = Solid(4, 4, 10, 10, 10);
        var capped = Solid(4, 4, 10, 10, 10);
        var untouched = Solid(4, 4, 10, 10, 10);
        CameraRawEngine.Apply(wide, Curve(shadows: 150));
        CameraRawEngine.Apply(capped, Curve(shadows: 100));
        // The witness: this bend has to move the pixel at all, or the equality below compares two no-ops.
        Assert.NotEqual(Pixel(untouched, 1, 1, 0), Pixel(capped, 1, 1, 0));
        for (int channel = 0; channel < 3; channel++)
        {
            Assert.Equal(Pixel(capped, 1, 1, channel), Pixel(wide, 1, 1, channel));
        }
    }

    [Fact]
    public void TheKernelFloorsADividerThatTheGraphStillShowsAtItsOwnNumber()
    {
        // The graph's curve was checked above to follow the stored divider; the render must not. A shadow
        // divider dragged to 2 is floored to 5, so it paints exactly what a 5 paints — and a 20 paints
        // something else, which is what makes the equality below a claim rather than an identity.
        var stored = Solid(2, 2, 10, 10, 10);
        var floored = Solid(2, 2, 10, 10, 10);
        var wider = Solid(2, 2, 10, 10, 10);
        CameraRawEngine.Apply(stored, Curve(shadows: 100, shadowSplit: 2));
        CameraRawEngine.Apply(floored, Curve(shadows: 100, shadowSplit: 5));
        CameraRawEngine.Apply(wider, Curve(shadows: 100, shadowSplit: 20));
        for (int channel = 0; channel < 4; channel++)
        {
            Assert.Equal(Pixel(floored, 0, 0, channel), Pixel(stored, 0, 0, channel));
        }
        Assert.NotEqual(Pixel(wider, 0, 0, 0), Pixel(floored, 0, 0, 0));
    }

    private static Surface Solid(int width, int height, byte r, byte g, byte b, byte a = 255)
    {
        var surface = new Surface(width, height);
        for (int row = 0; row < height; row++)
        {
            for (int column = 0; column < width; column++)
            {
                int index = row * surface.Stride + column * 4;
                surface.Pixels[index] = r;
                surface.Pixels[index + 1] = g;
                surface.Pixels[index + 2] = b;
                surface.Pixels[index + 3] = a;
            }
        }
        return surface;
    }

    private static byte Pixel(Surface surface, int x, int y, int channel) =>
        surface.Pixels[y * surface.Stride + x * 4 + channel];
}
