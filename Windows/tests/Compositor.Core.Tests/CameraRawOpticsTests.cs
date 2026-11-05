using Compositor.Core;
using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// Numeric locks for the Camera Raw optics step, ported line for line from the macOS kernels
/// adjust_camera_raw_optics calls (AdjustPixels.c:1018-1115 and LensPixels.c:4). Every expected byte comes from
/// Windows/tools/optics-golden.py, which mirrors those kernels and the Swift settings around them. That is two
/// independent implementations agreeing on a number, not a run of the Mac: this machine has no C toolchain, so
/// the Swift/C original cannot be executed here.
/// </summary>
public class CameraRawOpticsTests
{
    private static Surface Solid(int width, int height, byte r, byte g, byte b, byte a = 255)
    {
        var surface = new Surface(width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int at = y * surface.Stride + x * 4;
                surface.Pixels[at] = r;
                surface.Pixels[at + 1] = g;
                surface.Pixels[at + 2] = b;
                surface.Pixels[at + 3] = a;
            }
        return surface;
    }

    /// <summary>A purple half against a green half: the two fringes the defringe bands are named for, with a hard
    /// edge the distort and chromatic passes can be read against.</summary>
    private static Surface Fringey(int width = 8, int height = 8)
    {
        var surface = new Surface(width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                bool purple = x < width / 2;
                int at = y * surface.Stride + x * 4;
                surface.Pixels[at] = (byte)(purple ? 200 : 100);
                surface.Pixels[at + 1] = (byte)(purple ? 60 : 200);
                surface.Pixels[at + 2] = (byte)(purple ? 220 : 60);
                surface.Pixels[at + 3] = 255;
            }
        return surface;
    }

    /// <summary>White left of the middle, black right of it, the way the Mac's own light test reads an edge.</summary>
    private static Surface HalfEdge(int size = 8)
    {
        var surface = new Surface(size, size);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int at = y * surface.Stride + x * 4;
                byte v = x < size / 2 ? (byte)255 : (byte)0;
                surface.Pixels[at] = surface.Pixels[at + 1] = surface.Pixels[at + 2] = v;
                surface.Pixels[at + 3] = 255;
            }
        return surface;
    }

    private static (int R, int G, int B, int A) Pixel(Surface surface, int x, int y)
    {
        int at = y * surface.Stride + x * 4;
        return (surface.Pixels[at], surface.Pixels[at + 1], surface.Pixels[at + 2], surface.Pixels[at + 3]);
    }

    private static (int, int, int) Defringed((int, int, int) pixel, double purple = 0, double purpleLow = 270,
        double purpleHigh = 310, double green = 0, double greenLow = 60, double greenHigh = 120)
    {
        double r = pixel.Item1 / 255.0, g = pixel.Item2 / 255.0, b = pixel.Item3 / 255.0;
        CameraRawOptics.Defringe(ref r, ref g, ref b, purple, purpleLow, purpleHigh, green, greenLow, greenHigh);
        return ((int)Math.Round(r * 255, MidpointRounding.AwayFromZero),
            (int)Math.Round(g * 255, MidpointRounding.AwayFromZero),
            (int)Math.Round(b * 255, MidpointRounding.AwayFromZero));
    }

    private static (int, int, int) VignetteAt(int x, int y, double amount, double midpoint, int size = 8)
    {
        double r = 0.5, g = 0.5, b = 0.5;
        CameraRawOptics.VignetteCorrect(ref r, ref g, ref b, x, y, size, size, amount, midpoint);
        return ((int)Math.Round(r * 255, MidpointRounding.AwayFromZero),
            (int)Math.Round(g * 255, MidpointRounding.AwayFromZero),
            (int)Math.Round(b * 255, MidpointRounding.AwayFromZero));
    }

    [Theory]
    [InlineData(292.500000, 200, 60, 220)]
    [InlineData(102.857143, 100, 200, 60)]
    [InlineData(187.500000, 60, 200, 220)]
    [InlineData(334.285714, 200, 60, 120)]
    [InlineData(0.0, 140, 140, 140)]
    public void HueIsWhatTheKernelCallsIt(double expected, byte r, byte g, byte b) =>
        Assert.True(Math.Abs(CameraRawOptics.HueDegrees(r / 255.0, g / 255.0, b / 255.0) - expected) < 1e-6,
            $"{r},{g},{b} is hue {CameraRawOptics.HueDegrees(r / 255.0, g / 255.0, b / 255.0)}, the Mac's is {expected}.");

    [Fact]
    public void AnAchromaticPixelHasNoHueToRemove()
    {
        double r = 140 / 255.0, g = 140 / 255.0, b = 140 / 255.0;
        CameraRawOptics.Defringe(ref r, ref g, ref b, 100, 0, 360, 100, 0, 360);
        Assert.Equal((140, 140, 140), ((int)Math.Round(r * 255), (int)Math.Round(g * 255), (int)Math.Round(b * 255)));
    }

    [Fact]
    public void DefringePullsAFringeTowardItsOwnLuminance()
    {
        Assert.Equal((164, 75, 177), Defringed((200, 60, 220), purple: 50));
        Assert.Equal((128, 90, 134), Defringed((200, 60, 220), purple: 100));
        Assert.Equal((148, 178, 136), Defringed((100, 200, 60), green: 100));
    }

    [Fact]
    public void DefringeOnlyTouchesHuesInsideItsOwnBand()
    {
        Assert.Equal((100, 200, 60), Defringed((100, 200, 60), purple: 100));
        Assert.Equal((200, 60, 220), Defringed((200, 60, 220), green: 100));
        Assert.Equal((200, 60, 220), Defringed((200, 60, 220), purple: 0));
    }

    [Fact]
    public void TwoBandsCoveringTheSamePixelTakeTheBiggerAmount()
    {
        // The purple band widened to 60…310 covers the pixel the green band also claims, and 80 beats 20.
        Assert.Equal((186, 66, 203), Defringed((200, 60, 220), purple: 20, purpleLow: 60, purpleHigh: 310, green: 80));
    }

    [Fact]
    public void TheKernelCanWrapABandButNormalizedNeverLetsItAsk()
    {
        // Called past normalization, a band with low 300 and high 60 is the wrap the C writes (AdjustPixels.c:837):
        // hue 334 is inside it, and ordering the pair to 60…300 would put the same pixel outside. The panel cannot
        // ask for this — Normalize orders every pair — so the branch is the kernel's own, kept because the pixel
        // math it mirrors keeps it.
        Assert.Equal((126, 84, 102), Defringed((200, 60, 120), purple: 100, purpleLow: 300, purpleHigh: 60));
        Assert.Equal((200, 60, 120), Defringed((200, 60, 120), purple: 100, purpleLow: 60, purpleHigh: 300));
    }

    [Fact]
    public void NormalizeOrdersEachHuePairAndClampsEachSlider()
    {
        var settings = new CameraRawSettings
        {
            OpticsPurpleHueLow = 310, OpticsPurpleHueHigh = 270,
            OpticsGreenHueLow = 120, OpticsGreenHueHigh = 60,
        };
        CameraRawOptics.Normalize(settings);
        Assert.Equal((270, 310), ((int)settings.OpticsPurpleHueLow, (int)settings.OpticsPurpleHueHigh));
        Assert.Equal((60, 120), ((int)settings.OpticsGreenHueLow, (int)settings.OpticsGreenHueHigh));

        settings = new CameraRawSettings { OpticsPurpleHueLow = -15, OpticsPurpleHueHigh = 385 };
        CameraRawOptics.Normalize(settings);
        Assert.Equal((0, 360), ((int)settings.OpticsPurpleHueLow, (int)settings.OpticsPurpleHueHigh));
    }

    [Fact]
    public void AnUnreadableSliderFallsBackToItsOwnDefault()
    {
        var settings = new CameraRawSettings
        {
            OpticsDistortion = double.NaN, OpticsProfileDistortion = double.PositiveInfinity,
            OpticsVignetteMidpoint = double.NaN, OpticsPurpleHueLow = double.NaN,
        };
        CameraRawOptics.Normalize(settings);
        Assert.Equal(0, settings.OpticsDistortion);
        Assert.Equal(100, settings.OpticsProfileDistortion);
        Assert.Equal(50, settings.OpticsVignetteMidpoint);
        Assert.Equal(270, settings.OpticsPurpleHueLow);
    }

    [Fact]
    public void TheProfileSlidersMeanNothingUntilTheirToggleIsOn()
    {
        var off = new CameraRawSettings { OpticsProfileDistortion = 100, OpticsProfileVignetting = 100 };
        Assert.Equal(0, CameraRawOptics.DistortionK(off));
        Assert.False(off.AdjustsOptics);

        var on = new CameraRawSettings { OpticsLensProfile = true, OpticsProfileDistortion = 100 };
        Assert.Equal(0.35, CameraRawOptics.DistortionK(on), 9);
        Assert.True(on.AdjustsOptics);
    }

    [Fact]
    public void ManualAndProfileDistortionBothReachOnlyTheLensStrength()
    {
        var manual = new CameraRawSettings { OpticsDistortion = 100 };
        Assert.Equal(0.35, CameraRawOptics.DistortionK(manual), 9);
        var both = new CameraRawSettings { OpticsDistortion = 100, OpticsLensProfile = true, OpticsProfileDistortion = 100 };
        Assert.Equal(0.70, CameraRawOptics.DistortionK(both), 9);
        Assert.Equal(0.70, CameraRawOptics.DistortionK(both, CameraRawOptics.ProfileStrength), 9);
    }

    [Fact]
    public void VignetteReachesOutFromTheMidpointItWasGiven()
    {
        Assert.Equal((128, 128, 128), VignetteAt(4, 4, 100, 50));
        Assert.Equal((255, 255, 255), VignetteAt(7, 7, 100, 50));
        Assert.Equal((0, 0, 0), VignetteAt(7, 7, -100, 50));
        Assert.Equal((165, 165, 165), VignetteAt(4, 4, 100, 0));
        Assert.Equal((129, 129, 129), VignetteAt(7, 7, 100, 100));
    }

    [Fact]
    public void TheProfileVignetteStacksOnTopOfTheManualAmount()
    {
        // 100 + 1.0*35 saturates the corner to white; -100 + 35 leaves 45 of the mid gray.
        Assert.Equal((255, 255, 255), VignetteAt(7, 7, 135, 50));
        Assert.Equal((45, 45, 45), VignetteAt(7, 7, -65, 50));
    }

    [Fact]
    public void DistortionWithNothingToBendStillWritesTheWholeLayer()
    {
        var source = HalfEdge();
        var destination = new Surface(8, 8);
        CameraRawOptics.Distort(source, destination, 0);
        Assert.Equal(source.Pixels, destination.Pixels);
    }

    [Fact]
    public void BarrelDistortionMovesTheEdgeByTheAmountTheKernelSamples()
    {
        var surface = HalfEdge();
        CameraRawOptics.Distort(surface.Clone(), surface, 0.35);
        Assert.Equal((238, 238, 238, 255), Pixel(surface, 3, 0));
        Assert.Equal((17, 17, 17, 255), Pixel(surface, 4, 0));
        Assert.Equal((254, 254, 254, 255), Pixel(surface, 3, 3));
        Assert.Equal((1, 1, 1, 255), Pixel(surface, 4, 3));
        Assert.Equal((0, 0, 0, 255), Pixel(surface, 7, 0));
    }

    [Fact]
    public void PincushionDistortionLetsTheCornersFadeBecauseNothingIsRenormalized()
    {
        // The kernel drops neighbors that fall outside the layer instead of spreading their weight
        // (LensPixels.c:19-33), so pulling the picture outward leaves the corners partly transparent.
        var surface = HalfEdge();
        CameraRawOptics.Distort(surface.Clone(), surface, -0.35);
        Assert.Equal((1, 1, 1, 1), Pixel(surface, 0, 0));
        Assert.Equal((74, 74, 74, 74), Pixel(surface, 1, 0));
        Assert.Equal((133, 133, 133, 133), Pixel(surface, 3, 0));
        Assert.Equal((0, 0, 0, 133), Pixel(surface, 4, 0));
        Assert.Equal((255, 255, 255, 255), Pixel(surface, 1, 1));
    }

    [Fact]
    public void ChromaticAberrationHasNothingToPullApartOnANeutralEdge()
    {
        var surface = HalfEdge();
        var before = (byte[])surface.Pixels.Clone();
        CameraRawOptics.ChromaticAberration(surface, CameraRawOptics.ChromaticStrength);
        Assert.Equal(before, surface.Pixels);
    }

    [Fact]
    public void ChromaticAberrationSamplesItsRowFromBeforeItsOwnRewrite()
    {
        var big = Fringey(32, 32);
        CameraRawOptics.ChromaticAberration(big, CameraRawOptics.ChromaticStrength);
        // The shift reaches a whole pixel only near the frame, and there red comes from a column this very pass
        // has already rewritten. Pixel 17 samples column 16, whose red the copy still holds at 100; reading the
        // live buffer instead would hand it the 200 that 16 just took on, and the pull would walk inward.
        Assert.Equal((200, 60, 60, 255), Pixel(big, 15, 0));
        Assert.Equal((200, 200, 60, 255), Pixel(big, 16, 0));
        Assert.Equal((100, 200, 60, 255), Pixel(big, 17, 0));
    }

    [Fact]
    public void ChromaticAberrationKeepsAGreenPixelAndItsOwnAlpha()
    {
        var surface = new Surface(8, 8);
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                int at = y * surface.Stride + x * 4;
                if (x == 0) continue; // one empty column at the frame
                surface.Pixels[at] = 200;
                surface.Pixels[at + 1] = 60;
                surface.Pixels[at + 2] = 220;
                surface.Pixels[at + 3] = 255;
            }
        CameraRawOptics.ChromaticAberration(surface, CameraRawOptics.ChromaticStrength);
        // Red is sampled from the empty column, and the kernel divides by max(1, alpha) rather than by the zero
        // it found: the byte is 0 either way, and the guard is what keeps 0/0 out of the arithmetic.
        Assert.Equal((0, 60, 220, 255), Pixel(surface, 1, 0));
        Assert.Equal((0, 0, 0, 0), Pixel(surface, 0, 0));
        Assert.Equal((200, 60, 220, 255), Pixel(surface, 4, 4));
    }

    [Fact]
    public void TheVignetteRowAloneGradesTheLayerAndLiftsTheCorners()
    {
        var surface = Fringey(32, 32);
        CameraRawEngine.Apply(surface, new CameraRawSettings { OpticsVignetteAmount = 100 });
        Assert.Equal((255, 255, 255, 255), Pixel(surface, 0, 0));
        Assert.Equal((246, 223, 249, 255), Pixel(surface, 15, 0));
        Assert.Equal((100, 200, 60, 255), Pixel(surface, 16, 16));
    }

    [Fact]
    public void EachOpticsRowOnItsOwnIsAnAdjustmentAndTheProfilePairIsNot()
    {
        Assert.True(new CameraRawSettings { OpticsRemoveChromaticAberration = true }.AdjustsOptics);
        Assert.True(new CameraRawSettings { OpticsLensProfile = true }.AdjustsOptics);
        Assert.True(new CameraRawSettings { OpticsDistortion = 100 }.AdjustsOptics);
        Assert.True(new CameraRawSettings { OpticsPurpleAmount = 10 }.AdjustsOptics);
        Assert.True(new CameraRawSettings { OpticsGreenAmount = 10 }.AdjustsOptics);
        Assert.True(new CameraRawSettings { OpticsVignetteAmount = 100 }.AdjustsOptics);
        Assert.False(new CameraRawSettings { OpticsProfileDistortion = 0, OpticsProfileVignetting = 0 }.AdjustsOptics);
        Assert.False(new CameraRawSettings { OpticsVignetteMidpoint = 0 }.AdjustsOptics);
        Assert.False(new CameraRawSettings { OpticsPurpleHueLow = 10, OpticsPurpleHueHigh = 20 }.AdjustsOptics);
        Assert.False(new CameraRawSettings { OpticsVignetteAmount = 100 }.IsIdentity);
    }

    [Fact]
    public void ProfileSlidersAloneLeaveTheGradeAlone()
    {
        var surface = Fringey();
        var before = (byte[])surface.Pixels.Clone();
        CameraRawEngine.Apply(surface, new CameraRawSettings { OpticsProfileDistortion = 0, OpticsVignetteMidpoint = 0 });
        Assert.Equal(before, surface.Pixels);
    }

    [Fact]
    public void DefringingRunsOnThePixelsDistortionLeftBehind()
    {
        var surface = Fringey();
        CameraRawOptics.Apply(surface, new CameraRawSettings { OpticsDistortion = 100, OpticsPurpleAmount = 100, OpticsGreenAmount = 100 });
        // (3,0) is the seam after a 0.35 pull, so the defringe sees a mix of the two fringes rather than either
        // one: 135 is the blended pixel desaturated, and 129 — the value the pure purple defringes to — is what
        // running the passes the other way round would leave there.
        Assert.Equal((135, 94, 141, 255), Pixel(surface, 3, 0));
        Assert.Equal((143, 174, 129, 255), Pixel(surface, 4, 0));
        Assert.Equal((128, 90, 134, 255), Pixel(surface, 0, 0));
    }

    [Fact]
    public void TheComposedPassMatchesTheMacForEveryKernelAtOnce()
    {
        var surface = Fringey();
        CameraRawOptics.Apply(surface, new CameraRawSettings
        {
            OpticsRemoveChromaticAberration = true, OpticsLensProfile = true, OpticsDistortion = 100,
            OpticsPurpleAmount = 100, OpticsGreenAmount = 100, OpticsVignetteAmount = 50,
        });
        Assert.Equal((236, 230, 237, 255), Pixel(surface, 0, 0));
        Assert.Equal((128, 90, 134, 255), Pixel(surface, 2, 2));
        Assert.Equal((131, 92, 137, 255), Pixel(surface, 3, 2));
        Assert.Equal((148, 178, 136, 255), Pixel(surface, 5, 2));
        Assert.Equal((239, 243, 237, 255), Pixel(surface, 7, 7));
    }

    [Fact]
    public void TheGradeSeesNormalizedValuesWhileTheDraftKeepsWhatWasTyped()
    {
        var draft = new CameraRawSettings { OpticsPurpleAmount = 100, OpticsPurpleHueLow = 300, OpticsPurpleHueHigh = 60 };
        var surface = Fringey();
        CameraRawOptics.Apply(surface, draft);
        Assert.Equal((300, 60), ((int)draft.OpticsPurpleHueLow, (int)draft.OpticsPurpleHueHigh));
        // Ordered, the band is 60…300, which claims both fringes in this layer, so both are defringed.
        Assert.Equal((128, 90, 134, 255), Pixel(surface, 0, 0));
        Assert.Equal((148, 178, 136, 255), Pixel(surface, 4, 0));
    }

    [Fact]
    public void TheEngineRunsOpticsBeforeSharpening()
    {
        var both = Fringey(32, 32);
        var staged = Fringey(32, 32);
        var opticsOnly = new CameraRawSettings { OpticsDistortion = 100 };
        var detailOnly = new CameraRawSettings { SharpenAmount = 40, SharpenRadius = 4 };
        CameraRawEngine.Apply(staged, opticsOnly);
        CameraRawEngine.Apply(staged, detailOnly);
        CameraRawEngine.Apply(both, new CameraRawSettings { OpticsDistortion = 100, SharpenAmount = 40, SharpenRadius = 4 });
        Assert.Equal(staged.Pixels, both.Pixels);
    }

    [Theory]
    [InlineData(292.500000, true, 267.500000, 317.500000)]
    [InlineData(102.857143, false, 77.857143, 127.857143)]
    [InlineData(0.0, false, -25.0, 25.0)]
    [InlineData(187.500000, false, 162.500000, 212.500000)]
    public void ThePickerCentersTheCloserBandOnTheHueItWasGiven(double hue, bool purple, double low, double high)
    {
        var solved = CameraRawOptics.SolvedFringe(hue, 0, 0);
        Assert.Equal(purple, solved.Purple);
        Assert.True(Math.Abs(solved.Low - low) < 1e-6 && Math.Abs(solved.High - high) < 1e-6,
            $"hue {hue} should band {low}…{high}, got {solved.Low}…{solved.High}.");
        Assert.Equal(50, solved.Amount);
    }

    [Fact]
    public void ThePickerLeavesAnAmountTheUserAlreadySetAlone()
    {
        Assert.Equal(50, CameraRawOptics.SolvedFringe(292.5, 0, 30).Amount);
        Assert.Equal(80, CameraRawOptics.SolvedFringe(292.5, 80, 30).Amount);
        Assert.Equal(70, CameraRawOptics.SolvedFringe(102.857143, 80, 70).Amount);
    }

    [Fact]
    public void ThePickerBandClampsIntoTheSliderItHasToLandOn()
    {
        // A red pixel (hue 0) bands -25…25, and the same normalization the Mac applies to the pick clamps it.
        var solved = CameraRawOptics.SolvedFringe(0.0, 0, 0);
        var settings = new CameraRawSettings { OpticsGreenHueLow = solved.Low, OpticsGreenHueHigh = solved.High };
        CameraRawOptics.Normalize(settings);
        Assert.Equal((0, 25), ((int)settings.OpticsGreenHueLow, (int)settings.OpticsGreenHueHigh));
    }

    [Fact]
    public void CloneCarriesEveryOpticsSliderAndToggle()
    {
        var settings = new CameraRawSettings
        {
            OpticsRemoveChromaticAberration = true, OpticsLensProfile = true, OpticsProfileDistortion = 30,
            OpticsProfileVignetting = 40, OpticsDistortion = -50, OpticsPurpleAmount = 60, OpticsPurpleHueLow = 100,
            OpticsPurpleHueHigh = 140, OpticsGreenAmount = 20, OpticsGreenHueLow = 10, OpticsGreenHueHigh = 30,
            OpticsVignetteAmount = 70, OpticsVignetteMidpoint = 80,
        };
        var copy = settings.Clone();
        Assert.Equal(settings.OpticsRemoveChromaticAberration, copy.OpticsRemoveChromaticAberration);
        Assert.Equal(settings.OpticsLensProfile, copy.OpticsLensProfile);
        Assert.Equal((30, 40, -50, 60, 100, 140, 20, 10, 30, 70, 80),
            (copy.OpticsProfileDistortion, copy.OpticsProfileVignetting, copy.OpticsDistortion, copy.OpticsPurpleAmount,
                copy.OpticsPurpleHueLow, copy.OpticsPurpleHueHigh, copy.OpticsGreenAmount, copy.OpticsGreenHueLow,
                copy.OpticsGreenHueHigh, copy.OpticsVignetteAmount, copy.OpticsVignetteMidpoint));
        Assert.False(new CameraRawSettings().Clone().AdjustsOptics);
    }

    [Fact]
    public void MidpointAloneGradesNothingEvenWhenTheVignetteToggleIsOn()
    {
        var surface = Fringey();
        var before = (byte[])surface.Pixels.Clone();
        // Midpoint is not in the Mac's `adjusts` (CameraRawDetailOptics.swift:65-68), so the whole step is skipped.
        Assert.False(new CameraRawSettings { OpticsVignetteMidpoint = 0 }.AdjustsOptics);
        CameraRawEngine.Apply(surface, new CameraRawSettings { OpticsVignetteMidpoint = 0 });
        Assert.Equal(before, surface.Pixels);
    }
}
