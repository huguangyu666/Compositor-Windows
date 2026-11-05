using Compositor.Core;
using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// Numeric locks for the Camera Raw mixer's targeted points: the <c>point_weight</c> lobe product and the point
/// loop of <c>adjust_camera_raw_curve_color</c> (AdjustPixels.c:720-729, 776-810), the packing and the ranges of
/// <c>CameraRawPointColor</c> (CameraRawColor.swift:177-183, 208-220), and the eyedropper's reading of the clicked
/// pixel (<c>cameraRawSample</c>, CameraRaw.swift:609-637). Every expected byte comes from
/// Windows/tools/targeted-golden.py, which mirrors those lines. That is two independent implementations agreeing
/// on a number, not a run of the Mac: this machine has no C toolchain, so the original cannot be executed here.
/// </summary>
public class CameraRawMixerTests
{
    private static (byte R, byte G, byte B, byte A) Grade((byte R, byte G, byte B, byte A) pixel,
        CameraRawSettings settings, int visualize = -1)
    {
        var surface = new Surface(1, 1);
        surface.Pixels[0] = pixel.R;
        surface.Pixels[1] = pixel.G;
        surface.Pixels[2] = pixel.B;
        surface.Pixels[3] = pixel.A;
        CameraRawEngine.Apply(surface, settings, visualize);
        return (surface.Pixels[0], surface.Pixels[1], surface.Pixels[2], surface.Pixels[3]);
    }

    private static readonly (byte R, byte G, byte B, byte A) Green = (60, 220, 90, 255);
    private static readonly (byte R, byte G, byte B, byte A) Red = (220, 70, 70, 255);
    private static readonly (byte R, byte G, byte B, byte A) Blue = (70, 90, 220, 255);

    /// <summary>Blue's neighbours by one, and the reason the dimming rows read it: 0.35 of an even channel is an
    /// exact rounding tie, and a tie's byte turns on the last unit in the table the Mac keeps as float32 while
    /// this port and the mirror keep doubles. 24.85, 31.85 and 77.35 are not ties.</summary>
    private static readonly (byte R, byte G, byte B, byte A) DeepBlue = (71, 91, 221, 255);

    /// <summary>A point filed the way the eyedropper files it: the clicked pixel's hue and (max+min)/2, but its
    /// saturation as chroma over the max channel (CameraRaw.swift:635) rather than the HSL fraction the kernel
    /// compares against (AdjustPixels.c:687). For this green the two are 0.7273 and 0.6957, so a fresh pick sits
    /// a little off the color it was taken from and its weight there is 0.92 rather than 1.</summary>
    private static CameraRawMixerPoint PickedOnGreen(double hueShift = 0, double saturationShift = 0,
        double luminanceShift = 0, double hueRange = 30, double saturationRange = 0.4, double luminanceRange = 0.4,
        bool visualize = false) =>
        new(131.25, 160.0 / 220.0, 140.0 / 255.0, hueShift, saturationShift, luminanceShift,
            hueRange, saturationRange, luminanceRange, visualize);

    [Fact]
    public void APickedPointMovesTheColorItWasPickedOn()
    {
        var grade = Grade(Green, new CameraRawSettings { MixerPoints = [PickedOnGreen(hueShift: 50)] });
        Assert.Equal((byte)60, grade.R);
        Assert.Equal((byte)220, grade.G);
        Assert.Equal((byte)127, grade.B);
        Assert.Equal((byte)255, grade.A);
    }

    [Fact]
    public void ThePointIsNotCenteredOnThePixelItWasPickedFrom()
    {
        // The same shift asked at the point's own center, in the kernel's space, moves the pixel further than
        // the picker's number does — and half way to the edge of its reach at the full shift lands on exactly
        // the same byte as the dead-center point at half the shift (AdjustPixels.c:727, 780-782).
        var kernelCenter = new CameraRawMixerPoint(131.25, 160.0 / 230.0, 140.0 / 255.0, 50, 0, 0, 30, 0.4, 0.4, false);
        var halfReach = new CameraRawMixerPoint(131.25 + 15, 160.0 / 230.0, 140.0 / 255.0, 100, 0, 0, 30, 0.4, 0.4, false);
        var edge = new CameraRawMixerPoint(131.25 + 30, 160.0 / 230.0, 140.0 / 255.0, 100, 0, 0, 30, 0.4, 0.4, false);
        Assert.Equal((byte)130, Grade(Green, new CameraRawSettings { MixerPoints = [kernelCenter] }).B);
        Assert.Equal((byte)130, Grade(Green, new CameraRawSettings { MixerPoints = [halfReach] }).B);
        Assert.Equal(Green, Grade(Green, new CameraRawSettings { MixerPoints = [edge] }));
        Assert.Equal((byte)127, Grade(Green, new CameraRawSettings { MixerPoints = [PickedOnGreen(hueShift: 50)] }).B);
    }

    [Fact]
    public void EachShiftHasItsOwnArmOfTheKernel()
    {
        // Saturation is multiplied and luminance is added, at a quarter of the slider (AdjustPixels.c:781-782).
        var desaturated = Grade(Green, new CameraRawSettings { MixerPoints = [PickedOnGreen(saturationShift: -60)] });
        Assert.Equal((byte)104, desaturated.R);
        Assert.Equal((byte)176, desaturated.G);
        var lightened = Grade(Green, new CameraRawSettings { MixerPoints = [PickedOnGreen(luminanceShift: 80)] });
        Assert.Equal((byte)140, lightened.R);
        Assert.Equal((byte)234, lightened.G);
    }

    [Fact]
    public void TheDimDecisionReadsTheColorBeforeThePointMovedIt()
    {
        // A point whose center sits 15 degrees *before* this pixel covers it with weight 0.5, and the full hue
        // shift pushes the pixel to exactly the far edge of that coverage: reading the shifted color instead
        // would find weight zero there and dim it, and 0.35 of these bytes is (21, 77, 46)
        // (AdjustPixels.c:761 keeps sourceHue, and :808 hands those to the test).
        var shifted = new CameraRawMixerPoint(131.25 - 15, 160.0 / 230.0, 140.0 / 255.0, 100, 0, 0, 30, 0.4, 0.4, true);
        var grade = Grade(Green, new CameraRawSettings { MixerPoints = [shifted] }, visualize: 0);
        Assert.Equal((byte)60, grade.R);
        Assert.Equal((byte)220, grade.G);
        Assert.Equal((byte)130, grade.B);
    }

    [Fact]
    public void APickedDeepBlueMovesItsOwnColorByTheSameRule()
    {
        // The second color, so the picker-space finding is not an artifact of one pixel: deep blue files
        // 150/221 against the kernel's 150/218, a lobe of 0.9766 rather than the green's 0.9209.
        var point = new CameraRawMixerPoint(232, 150.0 / 221.0, 146.0 / 255.0, 50, 0, 0, 30, 0.4, 0.4, false);
        var grade = Grade(DeepBlue, new CameraRawSettings { MixerPoints = [point] });
        Assert.Equal((byte)88, grade.R);
        Assert.Equal((byte)71, grade.G);
        Assert.Equal((byte)221, grade.B);
    }

    [Fact]
    public void AColorOutsideThePointsOwnRangeIsLeftAlone()
    {
        // A 0.05 half-width of saturation around 0.05 cannot reach this green's 0.6957, and one lobe going
        // negative kills the product rather than clipping it (AdjustPixels.c:727).
        var narrow = new CameraRawMixerPoint(118.8, 0.05, 0.5, 50, 0, 0, 30, 0.05, 0.4, false);
        Assert.Equal(Green, Grade(Green, new CameraRawSettings { MixerPoints = [narrow] }));
        // The narrowest hue range the panel allows is 5 degrees: four away still moves the pixel, six does not.
        var close = new CameraRawMixerPoint(131.25 + 4, 160.0 / 220.0, 140.0 / 255.0, 50, 0, 0, 5, 0.4, 0.4, false);
        Assert.Equal((byte)97, Grade(Green, new CameraRawSettings { MixerPoints = [close] }).B);
        var missed = new CameraRawMixerPoint(131.25 + 6, 160.0 / 220.0, 140.0 / 255.0, 50, 0, 0, 5, 0.4, 0.4, false);
        Assert.Equal(Green, Grade(Green, new CameraRawSettings { MixerPoints = [missed] }));
    }

    [Fact]
    public void TheKernelFloorsAHalfWidthThatNormalizationNeverLetsThrough()
    {
        // AdjustPixels.c:721-723 floors each half-width at 0.01 turns; normalization clamps hue range to five
        // degrees, which is 0.0139, so the panel cannot ask for the smaller number. The floor is therefore
        // defensive, and this is the only place it can be read: handed the floats directly, both halves weigh
        // the same.
        var points = new double[] { 0.4, 0.7, 0.5, 0, 0, 0, 0.005, 0.4, 0.4 };
        var floored = new double[] { 0.4, 0.7, 0.5, 0, 0, 0, 0.01, 0.4, 0.4 };
        Assert.Equal(CameraRawMixer.Weight(0.405, 0.7, 0.5, floored, 0),
            CameraRawMixer.Weight(0.405, 0.7, 0.5, points, 0), 12);
    }

    [Fact]
    public void PointsStackInTheOrderTheyWerePicked()
    {
        // The second point reads the color the first left, so swapping them is not the same grade
        // (AdjustPixels.c:776-783 mutates h, s and l in place).
        var forward = new CameraRawSettings
        {
            MixerPoints = [PickedOnGreen(hueShift: 60), PickedOnGreen(hueShift: -60, saturationShift: 40)],
        };
        var backward = new CameraRawSettings
        {
            MixerPoints = [PickedOnGreen(hueShift: -60, saturationShift: 40), PickedOnGreen(hueShift: 60)],
        };
        var one = Grade(Green, forward);
        var two = Grade(Green, backward);
        Assert.Equal((byte)47, one.R);
        Assert.Equal((byte)233, one.G);
        Assert.Equal((byte)110, one.B);
        Assert.Equal((byte)37, two.R);
        Assert.Equal((byte)249, two.G);
        Assert.NotEqual(one, two);
    }

    [Fact]
    public void AZeroShiftPointIsNotAnAdjustment()
    {
        // Picking a color is filing it; nothing moves until one of the three shifts is moved
        // (CameraRawColor.swift:159-162).
        var settings = new CameraRawSettings { MixerPoints = [PickedOnGreen()] };
        Assert.False(settings.AdjustsMixer);
        Assert.True(settings.IsIdentity);
        Assert.Equal(Green, Grade(Green, settings));
        Assert.True(new CameraRawSettings { MixerPoints = [PickedOnGreen(hueShift: 1)] }.AdjustsMixer);
    }

    [Fact]
    public void OnlyTheFirstEightPointsAreEverRead()
    {
        var settings = new CameraRawSettings
        {
            MixerPoints =
            [
                .. Enumerable.Repeat(PickedOnGreen(hueShift: 10), CameraRawMixer.PointsMax),
                PickedOnGreen(hueShift: -90),
            ],
        };
        // The ninth would undo eight of the first if the prefix were not taken (CameraRawColor.swift:190).
        Assert.Equal((byte)133, Grade(Green, settings).B);
        Assert.Equal(CameraRawMixer.PointsMax, CameraRawMixer.Normalize(settings.MixerPoints).Count);
    }

    [Fact]
    public void ThePointLoopRunsOnTopOfTheFamiliesInTheSamePass()
    {
        // Greens' luminance family (+30) and the point's own −50 both bite on the same pixel, in the order the
        // kernel lays them out: families first, then the points (AdjustPixels.c:763-783).
        var settings = new CameraRawSettings { MixerPoints = [PickedOnGreen(luminanceShift: -50)] };
        settings.MixerLuminance[3] = 30;
        var grade = Grade(Green, settings);
        Assert.Equal((byte)40, grade.R);
        Assert.Equal((byte)216, grade.G);
        Assert.Equal((byte)73, grade.B);
    }

    [Fact]
    public void TheFamiliesStillMoveWhatTheyMovedBeforeThePointsExisted()
    {
        var reds = new CameraRawSettings();
        reds.MixerHue[0] = 50;
        var grade = Grade(Red, reds);
        Assert.Equal((byte)220, grade.R);
        Assert.Equal((byte)100, grade.G);
        Assert.Equal((byte)70, grade.B);
        // Where three families overlap one hue, the weight sum normalizes the three deltas (:772).
        var across = new CameraRawSettings();
        across.MixerHue[4] = 40;
        across.MixerHue[5] = -40;
        across.MixerSaturation[5] = 60;
        var blue = Grade(Blue, across);
        Assert.Equal((byte)35, blue.R);
        Assert.Equal((byte)100, blue.G);
        Assert.Equal((byte)255, blue.B);
    }

    [Fact]
    public void VisualizeDimsWhatThePointDoesNotCoverAndSparesWhatItDoes()
    {
        // 0.35 on every channel, decided on the pixel before anything moved it (AdjustPixels.c:808-810).
        var settings = new CameraRawSettings { MixerPoints = [PickedOnGreen(hueShift: 30, visualize: true)] };
        Assert.Equal((byte)112, Grade(Green, settings, visualize: 0).B);
        var outside = Grade(DeepBlue, settings, visualize: 0);
        Assert.Equal((byte)25, outside.R);
        Assert.Equal((byte)32, outside.G);
        Assert.Equal((byte)77, outside.B);
        // The same pixel with the very same points, and only the index removed: nothing is dimmed. That pair is
        // what says the dimming came from the index rather than from the point list.
        var unvisualized = Grade(DeepBlue, settings);
        Assert.Equal(DeepBlue, unvisualized);
    }

    [Fact]
    public void AnIndexOutsideThePointListDimsNothingAndMinusOneNeverDoes()
    {
        var settings = new CameraRawSettings { MixerPoints = [PickedOnGreen(hueShift: 30, visualize: true)] };
        Assert.Equal(DeepBlue, Grade(DeepBlue, settings, visualize: 5));
        Assert.Equal(DeepBlue, Grade(DeepBlue, settings, visualize: -1));
        // Filters.swift:319-323 hands out an index only for the selected point whose own flag is on, and
        // AdjustPixels.c:808 checks it against the count the kernel was given.
        Assert.NotEqual(DeepBlue.B, Grade(DeepBlue, settings, visualize: 0).B);
    }

    [Fact]
    public void AnEmptyGradeStillRoundTripsToTheSameBytesAndThePassReallyRan()
    {
        // The kernel converts to HSL and back on every pixel it runs at all, whether or not anything is set.
        // Whether that costs a byte is measured here rather than assumed: each row files a point on the very
        // pixel it grades, with no shift, so nothing is asked of the color — and the paired deep-blue row in
        // the same settings is the witness that the pass ran, because the only thing that can dim it is this
        // step (AdjustPixels.c:759-785, and the guard that decides the step happens at all is CameraRaw.swift:230).
        // Without that witness this test passed a mutation round that deleted the whole pass, which is how it
        // grew one.
        var rows = new[]
        {
            (pixel: Green, point: PickedOnGreen(visualize: true)),
            (pixel: Red, point: new CameraRawMixerPoint(0, 150.0 / 220.0, 145.0 / 255.0, 0, 0, 0, 30, 0.4, 0.4, true)),
            (pixel: Blue, point: new CameraRawMixerPoint(232, 150.0 / 220.0, 145.0 / 255.0, 0, 0, 0, 30, 0.4, 0.4, true)),
            (pixel: DeepBlue, point: new CameraRawMixerPoint(232, 150.0 / 221.0, 146.0 / 255.0, 0, 0, 0, 30, 0.4, 0.4, true)),
        };
        foreach (var (pixel, point) in rows)
        {
            Assert.Equal(pixel, Grade(pixel, new CameraRawSettings { MixerPoints = [point] }, visualize: 0));
        }
        var witness = new CameraRawMixerPoint(131.25, 160.0 / 220.0, 140.0 / 255.0, 0, 0, 0, 30, 0.4, 0.4, true);
        var dimmed = Grade(DeepBlue, new CameraRawSettings { MixerPoints = [witness] }, visualize: 0);
        Assert.Equal((25, 32, 77, 255), ((int)dimmed.R, (int)dimmed.G, (int)dimmed.B, (int)dimmed.A));
    }

    [Fact]
    public void AnEmptyPixelIsNotTouchedAtAll()
    {
        var empty = Grade((0, 0, 0, 0), new CameraRawSettings { MixerPoints = [PickedOnGreen(hueShift: 50)] });
        Assert.Equal((0, 0, 0, 0), ((int)empty.R, (int)empty.G, (int)empty.B, (int)empty.A));
    }

    [Fact]
    public void TheSamplerReadsTheClickedPixelInTheKernelsOwnSpace()
    {
        var (tone, hue, saturation, luminance) = CameraRawMixer.Sample(60, 220, 90, 255);
        // The drag's branch reads tone — the weighted luminance — where the point stores (max+min)/2.
        Assert.Equal(0.2126 * 60.0 / 255.0 + 0.7152 * 220.0 / 255.0 + 0.0722 * 90.0 / 255.0, tone, 9);
        Assert.Equal(131.25, hue, 4);
        Assert.Equal(160.0 / 220.0, saturation, 6);
        Assert.Equal(140.0 / 255.0, luminance, 6);
        // Channels are divided by their own alpha first, so a half-covered pixel reads its color, not its
        // shade: (64, 32, 0) at alpha 128 is the same 0.5, 0.25, 0 that (128, 64, 0) at full alpha is
        // (CameraRaw.swift:621-623).
        var covered = CameraRawMixer.Sample(64, 32, 0, 128);
        Assert.Equal(30.0, covered.Hue, 4);
        Assert.Equal(1.0, covered.Saturation, 6);
        Assert.Equal(0.25, covered.Luminance, 6);
        // A pixel with no chroma has no hue to pick, and the Mac's own guard calls that zero
        // (CameraRaw.swift:626-633).
        var gray = CameraRawMixer.Sample(140, 140, 140, 255);
        Assert.Equal(0.0, gray.Hue, 6);
        Assert.Equal(0.0, gray.Saturation, 6);
        Assert.Equal(140.0 / 255.0, gray.Luminance, 6);
    }

    [Fact]
    public void EveryFieldLandsOnItsOwnRangeEndRatherThanOnZero()
    {
        // ImageAdjustmentPixels.clamp's fallbacks (CameraRawColor.swift:210-218), one per field.
        var wild = new CameraRawMixerPoint(720, 2, -3, 400, -400, double.NaN, 0, 9, 9, true);
        var tamed = CameraRawMixer.NormalizePoint(wild);
        Assert.Equal(360, tamed.Hue);
        Assert.Equal(1, tamed.Saturation);
        Assert.Equal(0, tamed.Luminance);
        Assert.Equal(100, tamed.HueShift);
        Assert.Equal(-100, tamed.SaturationShift);
        Assert.Equal(0, tamed.LuminanceShift);
        Assert.Equal(5, tamed.HueRange);
        Assert.Equal(1, tamed.SaturationRange);
        Assert.Equal(1, tamed.LuminanceRange);
        Assert.True(tamed.Visualize);
    }

    [Fact]
    public void CloneCopiesThePointListRatherThanSharingIt()
    {
        var settings = new CameraRawSettings { MixerPoints = [PickedOnGreen()] };
        var copy = settings.Clone();
        copy.MixerPoints.Add(PickedOnGreen(hueShift: 20));
        Assert.Single(settings.MixerPoints);
        Assert.Equal(2, copy.MixerPoints.Count);
    }

    [Fact]
    public void ThePointListSurvivesTheSettingsRoundTrip()
    {
        var settings = new CameraRawSettings
        {
            MixerPoints = [PickedOnGreen(hueShift: 40, hueRange: 12, saturationRange: 0.2, visualize: true)],
        };
        var restored = System.Text.Json.JsonSerializer.Deserialize<CameraRawSettings>(
            System.Text.Json.JsonSerializer.Serialize(settings))!;
        Assert.Equal(settings.MixerPoints, restored.MixerPoints);
        Assert.True(restored.AdjustsMixer);
    }
}
