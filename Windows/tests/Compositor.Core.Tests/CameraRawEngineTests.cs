using Compositor.Core;
using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// Numeric locks for the Camera Raw kernel, ported from the native adjust_camera_raw
/// in macOS AdjustPixels.c.
/// </summary>
public class CameraRawEngineTests
{
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

    private static CameraRawSettings Settings(double exposure = 0, double contrast = 0,
        double highlights = 0, double shadows = 0, double whites = 0, double blacks = 0,
        double temperature = 0, double tint = 0, double vibrance = 0, double saturation = 0) => new()
    {
        Exposure = exposure,
        Contrast = contrast,
        Highlights = highlights,
        Shadows = shadows,
        Whites = whites,
        Blacks = blacks,
        Temperature = temperature,
        Tint = tint,
        Vibrance = vibrance,
        Saturation = saturation,
    };

    [Fact]
    public void IdentitySettingsChangeNothing()
    {
        var surface = Solid(4, 4, 128, 90, 40);

        CameraRawEngine.Apply(surface, Settings());

        Assert.Equal(128, Pixel(surface, 2, 2, 0));
        Assert.Equal(90, Pixel(surface, 2, 2, 1));
        Assert.Equal(40, Pixel(surface, 2, 2, 2));
    }

    [Fact]
    public void ExposurePlusOneStopsLiftsMidGray()
    {
        // 128/255 ≈ 0.502 sRGB → 0.216 linear ×2 → 0.432 → 0.684 sRGB ≈ 174.
        var surface = Solid(2, 2, 128, 128, 128);

        CameraRawEngine.Apply(surface, Settings(exposure: 1));

        Assert.Equal(176, Pixel(surface, 0, 0, 0));
        Assert.Equal(176, Pixel(surface, 1, 1, 2));
    }

    [Fact]
    public void ContrastPivotsAtMidGray()
    {
        var surface = Solid(2, 1, 128, 64, 255);
        surface.Pixels[4 + 0] = 64;
        surface.Pixels[4 + 1] = 64;
        surface.Pixels[4 + 2] = 64;

        CameraRawEngine.Apply(surface, Settings(contrast: 100));

        // The pivot is preserved by the math itself; the written pixel rounds half away from
        // zero, like the Mac's roundf, so 0.5-exactly lands one unit up.
        Assert.InRange(Pixel(surface, 0, 0, 0), 128, 129);
        Assert.True(Pixel(surface, 1, 0, 0) < 64, "darker gray pulled down");
    }

    [Fact]
    public void HighlightsLiftOnlyTheBrightHalf()
    {
        // Gray pixels: Rec.709 luminance equals the channel value, so the kernel math is
        // directly readable — 200/255 = 0.784 lifts to ≈0.854 with +100 highlights.
        var surface = Solid(1, 2, 200, 200, 200);
        surface.Pixels[4] = 60;  // dim pixel (bottom): all channels
        surface.Pixels[5] = 60;
        surface.Pixels[6] = 60;

        CameraRawEngine.Apply(surface, Settings(highlights: 100));

        Assert.Equal(218, Pixel(surface, 0, 0, 0)); // gray 0.784 → 0.784 + 0.324×(1−0.784)
        Assert.Equal(60, Pixel(surface, 0, 1, 0));  // dim untouched (weight 0 below mid)
    }

    [Fact]
    public void BlacksCrushTheBottomQuarter()
    {
        var surface = Solid(1, 2, 200, 200, 200);
        surface.Pixels[4] = 40;  // dim pixel: all channels
        surface.Pixels[5] = 40;
        surface.Pixels[6] = 40;

        CameraRawEngine.Apply(surface, Settings(blacks: -100));

        Assert.Equal(200, Pixel(surface, 0, 0, 0)); // above 0.25 → untouched
        Assert.Equal(16, Pixel(surface, 0, 1, 0));  // 0.25+(0.157−0.25)×2 = 0.064
    }

    [Fact]
    public void WarmTemperatureSeparatesRedAndBlue()
    {
        var surface = Solid(1, 1, 128, 128, 128);

        CameraRawEngine.Apply(surface, Settings(temperature: 100));

        Assert.True(Pixel(surface, 0, 0, 0) > Pixel(surface, 0, 0, 2), "warm pushes red over blue");
    }

    [Fact]
    public void VibranceHelpsLowSaturationMoreThanHigh()
    {
        var surface = new Surface(2, 1);
        // 30% saturation vs 90% saturation of the same hue.
        surface.Pixels[0] = 179; surface.Pixels[1] = 128; surface.Pixels[2] = 128; surface.Pixels[3] = 255;
        surface.Pixels[4] = 230; surface.Pixels[5] = 25;  surface.Pixels[6] = 25;  surface.Pixels[7] = 255;

        CameraRawEngine.Apply(surface, Settings(vibrance: 50));

        int lowBefore = 179 - 128, lowAfter = Pixel(surface, 0, 0, 0) - Pixel(surface, 0, 0, 1);
        int highBefore = 230 - 25, highAfter = Pixel(surface, 1, 0, 0) - Pixel(surface, 1, 0, 1);
        double lowGain = lowAfter / (double)lowBefore;
        double highGain = highAfter / (double)highBefore;
        Assert.True(lowGain > highGain, $"low-sat gain {lowGain:F2} should exceed high-sat gain {highGain:F2}");
    }

    [Fact]
    public void TransparencyIsPreserved()
    {
        var surface = Solid(1, 1, 255, 0, 0, a: 128);

        CameraRawEngine.Apply(surface, Settings(exposure: 1));

        Assert.Equal(128, Pixel(surface, 0, 0, 3));
    }

    [Fact]
    public void ToneCurveLiftsTheMidpoint()
    {
        var surface = Solid(1, 1, 128, 128, 128);
        var settings = new CameraRawSettings { CurveRgb = [new(0, 0), new(0.5, 0.55), new(1, 1)] };

        CameraRawEngine.Apply(surface, settings);

        // The spline passes through the point, so 128 lands at 0.55 × 255 ≈ 140.
        Assert.True(Math.Abs(Pixel(surface, 0, 0, 0) - 140) <= 2, $"got {Pixel(surface, 0, 0, 0)}");
    }

    [Fact]
    public void ParametricDarksDipTheQuarterTone()
    {
        var surface = Solid(1, 1, 64, 64, 64);
        var settings = new CameraRawSettings { CurveDarks = -51 };

        CameraRawEngine.Apply(surface, settings);

        // Fitted to Photoshop: Darks −51 dips a quarter-tone by about 0.1.
        Assert.True(Pixel(surface, 0, 0, 0) < 0.18 * 255, $"got {Pixel(surface, 0, 0, 0)}");
    }

    [Fact]
    public void RefineSaturationSteersBetweenColorAndBrightness()
    {
        // −100 turns the composite curve into brightness alone: a gray stays gray.
        var gray = Solid(1, 1, 128, 128, 128);
        CameraRawEngine.Apply(gray, new CameraRawSettings
        {
            CurveRgb = [new(0, 0), new(0.5, 0.55), new(1, 1)],
            RefineSaturation = -100,
        });
        Assert.Equal(Pixel(gray, 0, 0, 0), Pixel(gray, 0, 0, 1));
        Assert.Equal(Pixel(gray, 0, 0, 1), Pixel(gray, 0, 0, 2));
    }

    [Fact]
    public void MixerSaturationBoostsOnlyTheNeighboringHues()
    {
        var surface = Solid(2, 1, 255, 0, 0);
        surface.Pixels[4] = 60;  // second pixel: a desaturated red (60, 40, 40)
        surface.Pixels[5] = 40;
        surface.Pixels[6] = 40;
        var settings = new CameraRawSettings();
        settings.MixerSaturation[0] = 100; // Reds

        CameraRawEngine.Apply(surface, settings);

        // Pure red is already at saturation one; the desaturated red gains strength.
        Assert.Equal((byte)255, Pixel(surface, 0, 0, 0));
        Assert.True(Pixel(surface, 1, 0, 0) > 60 && Pixel(surface, 1, 0, 2) < 40,
            $"got ({Pixel(surface, 1, 0, 0)}, {Pixel(surface, 1, 0, 1)}, {Pixel(surface, 1, 0, 2)})");
    }

    [Fact]
    public void ClarityWidensTheEdgeContrast()
    {
        // A vertical gray step at x = 16; clarity pushes the dark side down, bright side up.
        var surface = Solid(32, 8, 100, 100, 100);
        for (int y = 0; y < 8; y++)
        {
            for (int x = 16; x < 32; x++)
            {
                int index = y * surface.Stride + x * 4;
                surface.Pixels[index] = surface.Pixels[index + 1] = surface.Pixels[index + 2] = 155;
            }
        }
        var settings = new CameraRawSettings { Clarity = 100 };

        CameraRawEngine.Apply(surface, settings);

        Assert.True(Pixel(surface, 12, 4, 0) < 100, $"dark side got {Pixel(surface, 12, 4, 0)}");
        Assert.True(Pixel(surface, 19, 4, 0) > 155, $"bright side got {Pixel(surface, 19, 4, 0)}");
        // The deep interior sits further than the coarse band's reach: unchanged.
        Assert.Equal(100, Pixel(surface, 2, 4, 0));
    }

    [Fact]
    public void VignetteDarkensCornersAndSparesTheCenter()
    {
        var surface = Solid(64, 64, 255, 255, 255);
        var settings = new CameraRawSettings { VignetteAmount = -100, VignetteFeather = 50 };

        CameraRawEngine.Apply(surface, settings);

        Assert.Equal(255, Pixel(surface, 32, 32, 0));
        Assert.True(Pixel(surface, 0, 0, 0) < 128, $"corner got {Pixel(surface, 0, 0, 0)}");
    }

    [Fact]
    public void GrainIsReproduciblePerSeed()
    {
        var first = Solid(16, 16, 128, 128, 128);
        var second = Solid(16, 16, 128, 128, 128);
        var settings = new CameraRawSettings { GrainAmount = 50, GrainSeed = 7 };

        CameraRawEngine.Apply(first, settings);
        CameraRawEngine.Apply(second, settings);
        Assert.Equal(first.Pixels, second.Pixels);

        var other = Solid(16, 16, 128, 128, 128);
        CameraRawEngine.Apply(other, new CameraRawSettings { GrainAmount = 50, GrainSeed = 8 });
        Assert.NotEqual(first.Pixels, other.Pixels);
    }

    [Fact]
    public void PhaseTwoSettingsRoundTripThroughJson()
    {
        var settings = new CameraRawSettings
        {
            CurveRgb = [new(0, 0), new(0.5, 0.6), new(1, 1)],
            CurveDarks = -30,
            RefineSaturation = 25,
        };
        settings.MixerHue[1] = 40;
        settings.MixerLuminance[0] = -20;
        settings.Texture = 30;
        settings.VignetteAmount = -50;
        settings.GrainAmount = 25;
        settings.GrainSeed = 11;

        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        var restored = System.Text.Json.JsonSerializer.Deserialize<CameraRawSettings>(json);

        Assert.NotNull(restored);
        Assert.Equal(settings.CurveRgb, restored!.CurveRgb);
        Assert.Equal(settings.CurveDarks, restored.CurveDarks);
        Assert.Equal(settings.MixerHue[1], restored.MixerHue[1]);
        Assert.Equal(settings.VignetteAmount, restored.VignetteAmount);
        Assert.Equal(settings.GrainSeed, restored.GrainSeed);
        Assert.True(restored.AdjustsCurve && restored.AdjustsMixer && restored.AdjustsEffects);
        Assert.False(restored.IsIdentity);
    }

    // --- Phase three: grading wheels, glow, detail ---

    [Fact]
    public void ShadowGradeWheelPushesBlueIntoTheShadows()
    {
        var surface = Solid(1, 1, 128, 128, 128);
        var settings = new CameraRawSettings { GradingShadowsHue = 240, GradingShadowsSat = 50 };

        CameraRawEngine.Apply(surface, settings);

        // Hue 240 tints (0, 0, 1): red and green fall together, blue rises.
        Assert.Equal(Pixel(surface, 0, 0, 0), Pixel(surface, 0, 0, 1));
        int blueLift = Pixel(surface, 0, 0, 2) - Pixel(surface, 0, 0, 1);
        Assert.InRange(blueLift, 20, 32); // the tonal wheels split the weight at mid-gray
    }

    [Fact]
    public void BalanceShiftsTheWheelCrossover()
    {
        var neutral = Solid(1, 1, 128, 128, 128);
        CameraRawEngine.Apply(neutral, new CameraRawSettings { GradingShadowsHue = 240, GradingShadowsSat = 100 });
        int liftNeutral = Pixel(neutral, 0, 0, 2) - 128;

        var favorsHighlights = Solid(1, 1, 128, 128, 128);
        CameraRawEngine.Apply(favorsHighlights, new CameraRawSettings
        {
            GradingShadowsHue = 240,
            GradingShadowsSat = 100,
            GradingBalance = 100,
        });
        int liftHighlights = Pixel(favorsHighlights, 0, 0, 2) - 128;

        Assert.True(liftHighlights < liftNeutral,
            $"balance +100 lift {liftHighlights} should fall below {liftNeutral}");
    }

    [Fact]
    public void GlobalWheelTintsEverything()
    {
        var surface = Solid(1, 2, 40, 40, 40);
        surface.Pixels[4] = 220;
        surface.Pixels[5] = 220;
        surface.Pixels[6] = 220;
        CameraRawEngine.Apply(surface, new CameraRawSettings { GradingGlobalHue = 120, GradingGlobalSat = 80 });

        // The global wheel carries weight one on every pixel, dark and bright alike.
        Assert.True(Pixel(surface, 0, 0, 1) > Pixel(surface, 0, 0, 0) + 10);
        Assert.True(Pixel(surface, 0, 1, 1) > Pixel(surface, 0, 1, 0) + 10);
    }

    [Fact]
    public void GlowLightsOnlyAboveTheThreshold()
    {
        var bright = Solid(8, 8, 240, 240, 240);
        var dark = Solid(8, 8, 40, 40, 40);
        var settings = new CameraRawSettings { Glow = 50 };

        CameraRawEngine.Apply(bright, settings);
        CameraRawEngine.Apply(dark, settings);

        Assert.True(Pixel(bright, 4, 4, 0) > 240, $"bright got {Pixel(bright, 4, 4, 0)}");
        Assert.Equal(40, Pixel(dark, 4, 4, 0));
        Assert.Equal(40, Pixel(dark, 0, 0, 1));
    }

    [Fact]
    public void HalationFringeIsRed()
    {
        var bright = Solid(8, 8, 200, 200, 200);
        CameraRawEngine.Apply(bright, new CameraRawSettings { Glow = 30, GlowStyle = 2 });

        Assert.True(Pixel(bright, 4, 4, 0) - 200 > Pixel(bright, 4, 4, 1) - 200,
            $"red gain {Pixel(bright, 4, 4, 0) - 200}, green gain {Pixel(bright, 4, 4, 1) - 200}");
    }

    [Fact]
    public void SharpeningWidensEdgeContrastAndSparesFlats()
    {
        var surface = Solid(16, 8, 100, 100, 100);
        for (int y = 0; y < 8; y++)
        {
            for (int x = 8; x < 16; x++)
            {
                int index = y * surface.Stride + x * 4;
                surface.Pixels[index] = surface.Pixels[index + 1] = surface.Pixels[index + 2] = 155;
            }
        }
        CameraRawEngine.Apply(surface, new CameraRawSettings { SharpenAmount = 80 });

        int darkSide = Pixel(surface, 7, 4, 0), brightSide = Pixel(surface, 8, 4, 0);
        Assert.True(brightSide - darkSide >= 58, $"edge delta {brightSide - darkSide}");
        Assert.Equal(100, Pixel(surface, 0, 4, 0));
        Assert.Equal(155, Pixel(surface, 15, 4, 0));
    }

    [Fact]
    public void LuminanceNoiseReductionSoftensTheEdgeAndSparesFlats()
    {
        var surface = Solid(16, 4, 40, 40, 40);
        for (int y = 0; y < 4; y++)
        {
            for (int x = 8; x < 16; x++)
            {
                int index = y * surface.Stride + x * 4;
                surface.Pixels[index] = surface.Pixels[index + 1] = surface.Pixels[index + 2] = 220;
            }
        }
        CameraRawEngine.Apply(surface, new CameraRawSettings { NoiseLuminance = 100, NoiseLuminanceDetail = 0 });

        // Full-strength reduction pulls each side of the step toward the local blur, so the
        // 180-step narrows; the flats sit further than the blur's reach and stay exact.
        int boundary = Pixel(surface, 8, 1, 0) - Pixel(surface, 7, 1, 0);
        Assert.InRange(boundary, 10, 90);
        Assert.Equal(40, Pixel(surface, 0, 1, 0));
        Assert.Equal(220, Pixel(surface, 15, 1, 0));
    }

    [Fact]
    public void PhaseThreeSettingsRoundTripThroughJson()
    {
        var settings = new CameraRawSettings
        {
            GradingShadowsHue = 240,
            GradingShadowsSat = 50,
            GradingHighlightsLum = -20,
            GradingBlending = 30,
            GradingBalance = 25,
            Glow = 40,
            GlowStyle = 2,
            SharpenAmount = 80,
            NoiseColor = 30,
        };

        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        var restored = System.Text.Json.JsonSerializer.Deserialize<CameraRawSettings>(json);

        Assert.NotNull(restored);
        Assert.Equal(240, restored!.GradingShadowsHue);
        Assert.Equal(2, restored.GlowStyle);
        Assert.Equal(80, restored.SharpenAmount);
        Assert.True(restored.AdjustsGrading && restored.AdjustsDetail && restored.AdjustsEffects);
        Assert.False(restored.IsIdentity);
    }

    [Fact]
    public void CloneIsDeepForCurvesAndMixers()
    {
        // The panel keeps editing the draft on the UI thread while a cloned copy grades on a
        // background thread: the clone must not see those edits.
        var settings = new CameraRawSettings
        {
            Exposure = 1.25,
            Temperature = -30,
            GrainSeed = 777,
        };
        settings.CurveRgb.Add(new CurvePoint(0.5, 0.75));
        settings.MixerHue[3] = 42;

        var clone = settings.Clone();

        settings.CurveRgb[^1] = new CurvePoint(0.5, 0.1);
        settings.MixerHue[3] = -42;
        settings.Exposure = -4;

        Assert.Equal(0.75, clone.CurveRgb[^1].Y);
        Assert.Equal(42, clone.MixerHue[3]);
        Assert.Equal(1.25, clone.Exposure);
        Assert.Equal(777u, clone.GrainSeed);
    }

    // --- Calibration. The expected bytes are transcribed from AdjustPixels.c:1117 (see
    // Windows/tools/calibration-golden.py, which mirrors that kernel and its four helpers line for line);
    // the Mac is the reference, not this engine re-running itself.

    [Fact]
    public void CalibrationRedHueTurnsOnlyTheRedPrimary()
    {
        var surface = new Surface(2, 1);
        WritePixel(surface, 0, 255, 60, 60);
        WritePixel(surface, 1, 60, 200, 60);

        CameraRawEngine.Apply(surface, new CameraRawSettings { CalibrationRedHue = 100 });

        Assert.Equal((255, 109, 60), ReadPixel(surface, 0));
        Assert.Equal((60, 200, 60), ReadPixel(surface, 1));
    }

    [Fact]
    public void CalibrationSaturationScalesTheDominantChannel()
    {
        var up = Solid(1, 1, 200, 100, 100);
        var down = Solid(1, 1, 200, 100, 100);

        CameraRawEngine.Apply(up, new CameraRawSettings { CalibrationRedSaturation = 100 });
        CameraRawEngine.Apply(down, new CameraRawSettings { CalibrationRedSaturation = -100 });

        Assert.Equal((222, 78, 78), ReadPixel(up, 0));
        Assert.Equal((178, 122, 122), ReadPixel(down, 0));
    }

    [Fact]
    public void CalibrationGreenAndBlueHaveTheirOwnRows()
    {
        var green = Solid(1, 1, 60, 200, 60);
        var blue = Solid(1, 1, 40, 80, 255);

        CameraRawEngine.Apply(green, new CameraRawSettings { CalibrationGreenSaturation = -100 });
        CameraRawEngine.Apply(blue, new CameraRawSettings { CalibrationBlueHue = -100 });

        Assert.Equal((91, 169, 91), ReadPixel(green, 0));
        Assert.Equal((40, 134, 255), ReadPixel(blue, 0));
    }

    [Fact]
    public void CalibrationShadowTintSparesTheBrightHalf()
    {
        var dark = Solid(1, 1, 30, 30, 60);
        var light = Solid(1, 1, 200, 200, 250);
        var magenta = Solid(1, 1, 30, 30, 60);
        var boundary = Solid(1, 1, 70, 70, 130);

        CameraRawEngine.Apply(dark, new CameraRawSettings { CalibrationShadowTint = 100 });
        CameraRawEngine.Apply(light, new CameraRawSettings { CalibrationShadowTint = 100 });
        CameraRawEngine.Apply(magenta, new CameraRawSettings { CalibrationShadowTint = -100 });
        CameraRawEngine.Apply(boundary, new CameraRawSettings { CalibrationShadowTint = 100 });

        Assert.Equal((41, 30, 60), ReadPixel(dark, 0));
        Assert.Equal((200, 200, 250), ReadPixel(light, 0));
        Assert.Equal((30, 41, 60), ReadPixel(magenta, 0));
        // Lightness 0.392: the tone just above the Mac's third-of-range cutoff. Widening that cutoff to the
        // half is the mutation this pixel exists to catch — m1 left every other case here untouched.
        Assert.Equal((70, 70, 130), ReadPixel(boundary, 0));
    }

    [Fact]
    public void CalibrationProcessVersionScalesEverySlider()
    {
        var current = Solid(1, 1, 255, 60, 60);
        var earliest = Solid(1, 1, 255, 60, 60);
        var shadowCurrent = Solid(1, 1, 30, 30, 60);
        var shadowEarliest = Solid(1, 1, 30, 30, 60);

        CameraRawEngine.Apply(current, new CameraRawSettings { CalibrationRedHue = 100 });
        CameraRawEngine.Apply(earliest, new CameraRawSettings { CalibrationRedHue = 100, CalibrationProcess = 1 });
        CameraRawEngine.Apply(shadowCurrent, new CameraRawSettings { CalibrationShadowTint = 100 });
        CameraRawEngine.Apply(shadowEarliest, new CameraRawSettings { CalibrationShadowTint = 100, CalibrationProcess = 1 });

        Assert.Equal((255, 109, 60), ReadPixel(current, 0));
        Assert.Equal((255, 87, 60), ReadPixel(earliest, 0));
        Assert.Equal((41, 30, 60), ReadPixel(shadowCurrent, 0));
        Assert.Equal((36, 30, 60), ReadPixel(shadowEarliest, 0));
    }

    [Fact]
    public void CalibrationProcessAloneIsNotAnAdjustment()
    {
        // Picking Version 1 in the panel must not warp the picture by itself: the Mac's `calibration.adjusts`
        // counts only the seven sliders.
        var surface = Solid(1, 1, 200, 100, 100);

        CameraRawEngine.Apply(surface, new CameraRawSettings { CalibrationProcess = 1 });

        Assert.Equal((200, 100, 100), ReadPixel(surface, 0));
        Assert.True(new CameraRawSettings { CalibrationProcess = 1 }.IsIdentity);
    }

    [Fact]
    public void CalibrationKeepsPremultipliedAlpha()
    {
        var surface = new Surface(1, 1);
        surface.Pixels[0] = 128;
        surface.Pixels[1] = 30;
        surface.Pixels[2] = 30;
        surface.Pixels[3] = 128;

        CameraRawEngine.Apply(surface, new CameraRawSettings { CalibrationRedHue = 100 });

        Assert.Equal((128, 55, 30), ReadPixel(surface, 0));
        Assert.Equal(128, surface.Pixels[3]);
    }

    [Fact]
    public void CalibrationReadsTheSourceNotTheGradedPixel()
    {
        // Calibration is the first pass on the Mac (CameraRaw.swift:239, ahead of adjust_camera_raw at :242),
        // so a primary slider is chosen by the source's dominant channel. This pixel is blue-dominant as drawn
        // and only turns red-dominant once the temperature gains are multiplied in.
        var withRed = Solid(1, 1, 128, 128, 158);
        var without = Solid(1, 1, 128, 128, 158);
        var withBlue = Solid(1, 1, 128, 128, 158);

        CameraRawEngine.Apply(withRed, new CameraRawSettings { Temperature = 100, CalibrationRedHue = 100 });
        CameraRawEngine.Apply(without, new CameraRawSettings { Temperature = 100 });
        CameraRawEngine.Apply(withBlue, new CameraRawSettings { Temperature = 100, CalibrationBlueHue = 100 });

        Assert.Equal(ReadPixel(without, 0), ReadPixel(withRed, 0));
        Assert.NotEqual(ReadPixel(without, 0), ReadPixel(withBlue, 0));
    }

    [Fact]
    public void NonFiniteCalibrationSliderFallsBackToNeutral()
    {
        var surface = Solid(1, 1, 255, 60, 60);

        CameraRawEngine.Apply(surface, new CameraRawSettings { CalibrationRedHue = double.NaN });

        Assert.Equal((255, 60, 60), ReadPixel(surface, 0));
    }

    [Fact]
    public void CalibrationSettingsRoundTripAndClone()
    {
        var settings = new CameraRawSettings
        {
            CalibrationProcess = 3,
            CalibrationShadowTint = -40,
            CalibrationBlueSaturation = 65,
        };

        var restored = System.Text.Json.JsonSerializer.Deserialize<CameraRawSettings>(
            System.Text.Json.JsonSerializer.Serialize(settings));
        var clone = settings.Clone();
        settings.CalibrationBlueSaturation = -65;

        Assert.NotNull(restored);
        Assert.Equal(3, restored!.CalibrationProcess);
        Assert.Equal(-40, restored.CalibrationShadowTint);
        Assert.Equal(65, restored.CalibrationBlueSaturation);
        Assert.Equal(65, clone.CalibrationBlueSaturation);
        Assert.True(restored.AdjustsCalibration);
        Assert.False(restored.IsIdentity);
    }

    private static void WritePixel(Surface surface, int x, byte r, byte g, byte b)
    {
        int index = x * 4;
        surface.Pixels[index] = r;
        surface.Pixels[index + 1] = g;
        surface.Pixels[index + 2] = b;
        surface.Pixels[index + 3] = 255;
    }

    private static (byte, byte, byte) ReadPixel(Surface surface, int x)
    {
        // Every calibration case above is a single row, so the column index is the byte offset.
        int index = x * 4;
        return (surface.Pixels[index], surface.Pixels[index + 1], surface.Pixels[index + 2]);
    }
}
