using Compositor.Core;
using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// Numeric locks for the numeric half of the Camera Raw geometry step, ported from
/// Document/CameraRawGeometryCalibration.swift:25-172 and the Constrain Crop branch that reads
/// brush_alpha_bounds (BrushPixels.c:3). Every expected number comes from
/// Windows/tools/geometry-golden.py, which mirrors that math line for line.
///
/// The corners are pure double arithmetic, so they are locked to the Mac's digits. The pixels cannot be: the Mac
/// hands its corners to Core Image's CIPerspectiveTransform, and this machine runs neither it nor a Mac. The warp
/// here is the port's own inverse-homography sampler, so the pixel locks are the ones no sampler can get wrong —
/// a translation that lands on whole pixels — and the directional claims are stated as such.
/// </summary>
public class CameraRawGeometryTests
{
    private static CameraRawSettings Geometry(double vertical = 0, double horizontal = 0, double rotate = 0,
        double aspect = 0, double scale = 0, double offsetX = 0, double offsetY = 0, bool rectilinear = false,
        bool crop = false) => new()
    {
        GeometryVertical = vertical,
        GeometryHorizontal = horizontal,
        GeometryRotate = rotate,
        GeometryAspect = aspect,
        GeometryScale = scale,
        GeometryOffsetX = offsetX,
        GeometryOffsetY = offsetY,
        GeometryProjection = rectilinear ? CameraRawSettings.GeometryProjectionRectilinear
            : CameraRawSettings.GeometryProjectionPerspective,
        GeometryConstrainCrop = crop,
    };

    private static void ExpectCorners(PointD[] corners, double[][] expected, string caseName)
    {
        Assert.Equal(4, corners.Length);
        for (int index = 0; index < 4; index++)
        {
            Assert.True(Math.Abs(corners[index].X - expected[index][0]) < 1e-6
                    && Math.Abs(corners[index].Y - expected[index][1]) < 1e-6,
                $"{caseName} corner {index} is ({corners[index].X:F6}, {corners[index].Y:F6}), the Mac's is "
                + $"({expected[index][0]:F6}, {expected[index][1]:F6}).");
        }
    }

    [Fact]
    public void TheCornersAreTheMacsCornerMathCaseByCase()
    {
        // TL, TR, BR, BL in the surface's own top-down coordinates. Rows 1-3 of the mirror script's output.
        var cases = new (string Name, CameraRawSettings Settings, int Width, int Height, double[][] Corners)[]
        {
            ("translateOnly", Geometry(offsetX: 20, offsetY: 20), 100, 100,
                new[] { new[] { 3.0, -3.0 }, new[] { 103.0, -3.0 }, new[] { 103.0, 103.0 }, new[] { 3.0, 103.0 } }),
            ("verticalKeystone", Geometry(vertical: 100), 100, 60,
                new[] { new[] { -18.0, 0.0 }, new[] { 118.0, 0.0 }, new[] { 100.0, 60.0 }, new[] { 0.0, 60.0 } }),
            ("horizontalKeystone", Geometry(horizontal: -100), 100, 60,
                new[] { new[] { 0.0, 0.0 }, new[] { 100.0, 0.0 }, new[] { 89.2, 60.0 }, new[] { 10.8, 60.0 } }),
            ("rectilinearHalfStrength", Geometry(vertical: 100, rectilinear: true), 100, 60,
                new[] { new[] { -9.9, 0.0 }, new[] { 109.9, 0.0 }, new[] { 100.0, 60.0 }, new[] { 0.0, 60.0 } }),
            ("rotatePlus45", Geometry(rotate: 45), 100, 60,
                new[] { new[] { -6.568542, 44.142136 }, new[] { 64.142136, -26.568542 },
                    new[] { 106.568542, 15.857864 }, new[] { 35.857864, 86.568542 } }),
            ("rotateMinus45", Geometry(rotate: -45), 100, 60,
                new[] { new[] { 35.857864, -26.568542 }, new[] { 106.568542, 44.142136 },
                    new[] { 64.142136, 86.568542 }, new[] { -6.568542, 15.857864 } }),
            ("aspectStretched", Geometry(aspect: 100), 100, 60,
                new[] { new[] { -25.0, 10.0 }, new[] { 125.0, 10.0 }, new[] { 125.0, 50.0 }, new[] { -25.0, 50.0 } }),
            ("aspectSqueezed", Geometry(aspect: -100), 100, 60,
                new[] { new[] { 25.0, -30.0 }, new[] { 75.0, -30.0 }, new[] { 75.0, 90.0 }, new[] { 25.0, 90.0 } }),
            ("scaleZoomIn", Geometry(scale: 50), 100, 60,
                new[] { new[] { -25.0, -15.0 }, new[] { 125.0, -15.0 }, new[] { 125.0, 75.0 }, new[] { -25.0, 75.0 } }),
            ("everythingTogether", Geometry(30, -20, 10, -25, 40, -15, 25), 100, 60,
                new[] { new[] { -25.465549, -4.128598 }, new[] { 108.202408, -34.912947 },
                    new[] { 112.802480, 68.819662 }, new[] { -2.624867, 95.403114 } }),
            ("rotatedThenZoomed", Geometry(rotate: 20, scale: 100), 80, 80,
                new[] { new[] { -62.537021, -7.813798 }, new[] { 87.813798, -62.537021 },
                    new[] { 142.537021, 87.813798 }, new[] { -7.813798, 142.537021 } }),
        };
        foreach (var (name, settings, width, height, corners) in cases)
            ExpectCorners(CameraRawGeometry.Corners(settings, width, height), corners, name);
    }

    [Fact]
    public void ScaleAtMinusHundredPinchesEveryCornerOntoTheCenter()
    {
        var corners = CameraRawGeometry.Corners(Geometry(scale: -100), 100, 60);
        ExpectCorners(corners, new[] { new[] { 50.0, 30.0 }, new[] { 50.0, 30.0 }, new[] { 50.0, 30.0 }, new[] { 50.0, 30.0 } },
            "scaleZoomOut");
        Assert.False(DistortWarp.IsUsable(corners), "the collapsed quad reported as usable; the warp would invert it.");
        // The port's own decision, not the Mac's: a quad with no area is refused and the picture is left alone.
        var source = new Surface(100, 60);
        Assert.True(ReferenceEquals(source, CameraRawGeometry.Apply(source, Geometry(scale: -100))),
            "a collapsed geometry still replaced the layer instead of refusing it.");
    }

    [Fact]
    public void OnlyTheSevenNumbersCountAsAnAdjustment()
    {
        Assert.False(Geometry().AdjustsGeometry);
        Assert.False(Geometry(crop: true).AdjustsGeometry);
        Assert.False(Geometry(rectilinear: true).AdjustsGeometry);
        Assert.True(Geometry(offsetX: 1).AdjustsGeometry);
        Assert.True(Geometry(rotate: -1).AdjustsGeometry);
        Assert.True(Geometry().IsIdentity);
        Assert.True(Geometry(crop: true, rectilinear: true).IsIdentity);
    }

    [Fact]
    public void NormalizeStopsRotateAtFortyFiveAndTheRestAtHundred()
    {
        var settings = Geometry(vertical: 400, horizontal: -400, rotate: 90, aspect: -400, scale: 400,
            offsetX: -400, offsetY: 400);
        CameraRawGeometry.Normalize(settings);
        Assert.Equal(100, settings.GeometryVertical);
        Assert.Equal(-100, settings.GeometryHorizontal);
        Assert.Equal(45, settings.GeometryRotate);
        Assert.Equal(-100, settings.GeometryAspect);
        Assert.Equal(100, settings.GeometryScale);
        Assert.Equal(-100, settings.GeometryOffsetX);
        Assert.Equal(100, settings.GeometryOffsetY);

        settings = new CameraRawSettings { GeometryRotate = double.NaN, GeometryVertical = double.PositiveInfinity };
        CameraRawGeometry.Normalize(settings);
        Assert.Equal(0, settings.GeometryRotate);
        Assert.Equal(0, settings.GeometryVertical);
    }

    [Fact]
    public void CloneCarriesEveryGeometryField()
    {
        var settings = Geometry(11, -12, 13, -14, 15, -16, 17, rectilinear: true, crop: true);
        var copy = settings.Clone();
        Assert.Equal((11, -12, 13, -14, 15, -16, 17),
            (copy.GeometryVertical, copy.GeometryHorizontal, copy.GeometryRotate, copy.GeometryAspect,
                copy.GeometryScale, copy.GeometryOffsetX, copy.GeometryOffsetY));
        Assert.Equal(CameraRawSettings.GeometryProjectionRectilinear, copy.GeometryProjection);
        Assert.True(copy.GeometryConstrainCrop);
        Assert.Equal(0.55, CameraRawSettings.GeometryProjectionStrength(copy.GeometryProjection));
        Assert.Equal(1.0, CameraRawSettings.GeometryProjectionStrength(CameraRawSettings.GeometryProjectionPerspective));
    }

    private static Surface Speckled(int width, int height)
    {
        var surface = new Surface(width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int at = y * surface.Stride + x * 4;
                surface.Pixels[at] = (byte)((x * 31 + y * 17) % 9 == 0 ? 240 : 30);
                surface.Pixels[at + 1] = (byte)((x * 7 + y * 13) % 5 == 0 ? 200 : 45);
                surface.Pixels[at + 2] = 90;
                surface.Pixels[at + 3] = 255;
            }
        return surface;
    }

    private static (int, int, int, int) Read(Surface surface, int x, int y)
    {
        int at = y * surface.Stride + x * 4;
        return (surface.Pixels[at], surface.Pixels[at + 1], surface.Pixels[at + 2], surface.Pixels[at + 3]);
    }

    private static CameraRawGeometryGuide Line(double startX, double startY, double endX, double endY) =>
        new(startX, startY, endX, endY);

    private static void ExpectCorrections((double Vertical, double Horizontal, double Rotate) actual,
        double vertical, double horizontal, double rotate, string caseName)
    {
        Assert.True(Math.Abs(actual.Vertical - vertical) < 1e-9 && Math.Abs(actual.Horizontal - horizontal) < 1e-9
                && Math.Abs(actual.Rotate - rotate) < 1e-9,
            $"{caseName} solves ({actual.Vertical}, {actual.Horizontal}, {actual.Rotate}), the Mac's is "
            + $"({vertical}, {horizontal}, {rotate}).");
    }

    [Fact]
    public void TheGuidedSolveReadsDirectionAndNothingElse()
    {
        // Numbers from Windows/tools/geometry-golden.py, the guidedCorrections block.
        ExpectCorrections(CameraRawGeometry.GuidedCorrections([Line(0.1, 0.5, 0.9, 0.52)]),
            0, 0, -1.4320961841646476, "one level line");
        ExpectCorrections(CameraRawGeometry.GuidedCorrections([Line(0.5, 0.1, 0.52, 0.9)]),
            0, 0, 1.4320961841646351, "one steep line");
        ExpectCorrections(CameraRawGeometry.GuidedCorrections([Line(0, 0, 1, 2)]),
            0, 0, 26.56505117707799, "a line past 45 folds back into the range");
        ExpectCorrections(CameraRawGeometry.GuidedCorrections([Line(0.1, 0.5, 0.9, 0.5), Line(0.5, 0.1, 0.5, 0.9)]),
            25, 0, 0, "level plus upright: the fixed 25 of keystone, not an amount read off the line");
        ExpectCorrections(CameraRawGeometry.GuidedCorrections([Line(0.1, 0.5, 0.9, 0.5), Line(0.4, 0.9, 0.6, 0.1)]),
            -25, 0, 0, "an upright leaning the other way signs the keystone");
        ExpectCorrections(CameraRawGeometry.GuidedCorrections([Line(0.1, 0.5, 0.9, 0.5), Line(0.5, 0.1, 0.9, 0.3)]),
            0, 25, 0, "a second horizontal-ish line moves the other axis, signed by which way it tilts");
        ExpectCorrections(CameraRawGeometry.GuidedCorrections([Line(0.1, 0.5, 0.9, 0.5), Line(0.5, 0.3, 0.9, 0.1)]),
            0, -25, 0, "tilting that line the other way signs it");
        ExpectCorrections(CameraRawGeometry.GuidedCorrections([Line(0.5, 0.5, 0.5001, 0.5)]), 0, 0, 0,
            "a line no longer than a hundredth of the frame has no direction to read");
        ExpectCorrections(CameraRawGeometry.GuidedCorrections([]), 0, 0, 0, "no lines");
    }

    [Fact]
    public void GuidedAddsToTheSlidersInsteadOfReplacingThem()
    {
        var settings = Geometry(vertical: 10, horizontal: -20, rotate: 5);
        settings.GeometryUpright = CameraRawSettings.GeometryUprightGuided;
        settings.GeometryGuides = [Line(0.1, 0.5, 0.9, 0.5), Line(0.5, 0.1, 0.5, 0.9)];
        ExpectCorrections(CameraRawGeometry.EffectiveCorrections(settings), 35, -20, 5, "manual 10 plus guided 25");

        var corners = CameraRawGeometry.Corners(settings, 100, 60);
        ExpectCorners(corners, new[] { new[] { -8.700434, 5.021027 }, new[] { 103.471089, -4.792709 },
            new[] { 100.272627, 55.716310 }, new[] { 4.956718, 64.055372 } }, "guided-adds-to-manual");
    }

    [Fact]
    public void ALevelAndAnUprightLineSquareOffTheFrame()
    {
        var settings = new CameraRawSettings
        {
            GeometryUpright = CameraRawSettings.GeometryUprightGuided,
            GeometryGuides = [Line(0.1, 0.5, 0.9, 0.5), Line(0.5, 0.1, 0.5, 0.9)],
        };
        ExpectCorners(CameraRawGeometry.Corners(settings, 100, 60),
            new[] { new[] { -4.5, 0.0 }, new[] { 104.5, 0.0 }, new[] { 100.0, 60.0 }, new[] { 0.0, 60.0 } },
            "guided-level-and-vertical");
    }

    [Fact]
    public void ChoosingGuidedWithNothingDrawnBendsNothing()
    {
        var empty = new CameraRawSettings { GeometryUpright = CameraRawSettings.GeometryUprightGuided };
        Assert.False(empty.AdjustsGeometry);
        Assert.True(empty.IsIdentity);
        var source = Speckled(40, 24);
        Assert.True(ReferenceEquals(source, CameraRawGeometry.Apply(source, empty)),
            "a Guided choice with no line still replaced the layer.");

        // A line too short to read a direction off is dropped by normalization — a click, not a drag.
        var clicked = new CameraRawSettings
        {
            GeometryUpright = CameraRawSettings.GeometryUprightGuided,
            GeometryGuides = [Line(0.5, 0.5, 0.5001, 0.5)],
        };
        Assert.False(clicked.AdjustsGeometry);
        CameraRawGeometry.Normalize(clicked);
        Assert.Empty(clicked.GeometryGuides);
    }

    [Fact]
    public void OffLeavesDrawnLinesUnreadButKeepsThem()
    {
        var settings = new CameraRawSettings { GeometryGuides = [Line(0.1, 0.5, 0.9, 0.52)] };
        Assert.False(settings.AdjustsGeometry);
        Assert.False(settings.GeometryUsesGuides);
        CameraRawGeometry.Normalize(settings);
        Assert.Single(settings.GeometryGuides); // Off is a mode, not a delete
    }

    [Fact]
    public void CloneCopiesTheGuideListRatherThanSharingIt()
    {
        var settings = new CameraRawSettings
        {
            GeometryUpright = CameraRawSettings.GeometryUprightGuided,
            GeometryGuides = [Line(0.1, 0.5, 0.9, 0.5)],
        };
        var copy = settings.Clone();
        Assert.Equal(CameraRawSettings.GeometryUprightGuided, copy.GeometryUpright);
        Assert.Equal(settings.GeometryGuides, copy.GeometryGuides);
        copy.GeometryGuides.Add(Line(0.5, 0.1, 0.5, 0.9));
        Assert.Single(settings.GeometryGuides);
        Assert.False(new CameraRawSettings().Clone().GeometryUsesGuides);
    }

    [Fact]
    public void AGuidedKeystoneUncoversTheTopCornersItPullsIn()
    {
        // Level plus a right-leaning upright line is the -25 keystone the solve always produces.
        var settings = new CameraRawSettings
        {
            GeometryUpright = CameraRawSettings.GeometryUprightGuided,
            GeometryGuides = [Line(0.1, 0.5, 0.9, 0.5), Line(0.4, 0.9, 0.6, 0.1)],
        };
        var frame = CameraRawGeometry.Apply(Speckled(100, 100), settings);
        Assert.Equal(0, Read(frame, 2, 0).Item4);
        Assert.Equal(255, Read(frame, 10, 0).Item4);
        Assert.Equal(255, Read(frame, 0, 99).Item4);
    }

    [Fact]
    public void AWholePixelHorizontalOffsetMovesThePictureAndNoSamplerCanArgueWithThat()
    {
        var source = Speckled(100, 100);
        var frame = CameraRawGeometry.Apply(source, Geometry(offsetX: 20));
        Assert.Equal((100, 100), (frame.Width, frame.Height));
        // Offset X at 20 is 15% of the frame: three whole pixels to the right. The corners it produces are a
        // translated copy of the frame, so the source row lengths survive and the sample lands on pixel centers —
        // exact under any filter, which is why this is where the port is held to the Mac's bytes.
        for (int y = 0; y < 100; y++)
            for (int x = 3; x < 100; x++)
                Assert.Equal(Read(source, x - 3, y), Read(frame, x, y));
        for (int x = 0; x < 3; x++)
            for (int y = 0; y < 100; y++)
                Assert.Equal(0, Read(frame, x, y).Item4);
    }

    [Fact]
    public void OffsetYMagnifiesThePictureRatherThanSlidingIt()
    {
        // Read straight off the Mac's corner numbers: Offset Y pushes the top edge up by the shift and the bottom
        // edge down by the same amount, so a 100-pixel frame ends up sampling its source across 106 rows. It is
        // not a translation, and the port inherits that — locked here because a "fix" to make it slide would
        // silently change what every Offset Y session does.
        var source = new Surface(100, 100);
        for (int y = 0; y < 100; y++)
            for (int x = 0; x < 100; x++)
            {
                int at = y * source.Stride + x * 4;
                byte value = y < 50 ? (byte)255 : (byte)0;
                source.Pixels[at] = source.Pixels[at + 1] = source.Pixels[at + 2] = value;
                source.Pixels[at + 3] = 255;
            }
        var frame = CameraRawGeometry.Apply(source, Geometry(offsetY: 20));
        // Frame row Y samples source row (Y + 3.5) × 100/106 − 0.5, so the white/black crossover of a layer that
        // halves at row 50 lands at row 49.5. A three-pixel slide would put that boundary at 46.5, and row 47
        // would read black.
        Assert.True(Read(frame, 50, 47).Item1 > 200, $"row 47 reads {Read(frame, 50, 47)}, which is a translation's landing place.");
        Assert.True(Read(frame, 50, 49).Item1 > 200, $"the magnified boundary is not between rows 49 and 50: row 49 is {Read(frame, 50, 49)}.");
        Assert.True(Read(frame, 50, 50).Item1 < 128, $"the magnified boundary is not between rows 49 and 50: row 50 is {Read(frame, 50, 50)}.");
        Assert.Equal((0, 0, 100, 100), CameraRawGeometry.BoundsOf(frame));
    }

    [Fact]
    public void ADeepKeystoneUncoversTheTopCornersAndKeepsTheBottomOnes()
    {
        // Vertical at −100 pulls the top edge in by 18% of the width each side; the bottom edge stays put.
        // Coverage is the part of this a filter cannot argue about, so it is what is asserted.
        var frame = CameraRawGeometry.Apply(Speckled(100, 100), Geometry(vertical: -100));
        Assert.Equal(0, Read(frame, 0, 0).Item4);
        Assert.Equal(0, Read(frame, 12, 0).Item4);
        Assert.Equal(255, Read(frame, 50, 0).Item4);
        Assert.Equal(255, Read(frame, 0, 99).Item4);
        Assert.Equal((0, 0, 100, 100), CameraRawGeometry.BoundsOf(frame));

        // Rectilinear is the same trapezoid at 55% of the depth: 9.9 columns in, so column 12 is covered again
        // while the very corner is still outside the picture.
        var gentler = CameraRawGeometry.Apply(Speckled(100, 100), Geometry(vertical: -100, rectilinear: true));
        Assert.Equal(255, Read(gentler, 12, 0).Item4);
        Assert.Equal(0, Read(gentler, 0, 0).Item4);
    }

    [Fact]
    public void BoundsReadTheOpaqueEdgesTopDownAndAnEmptyPictureIsZero()
    {
        var source = Speckled(100, 100);
        var frame = CameraRawGeometry.Apply(source, Geometry(offsetX: 20));
        Assert.Equal((3, 0, 100, 100), CameraRawGeometry.BoundsOf(frame));
        Assert.Equal((0, 0, 0, 0), CameraRawGeometry.BoundsOf(new Surface(100, 100)));
    }

    [Fact]
    public void ConstrainCropTrimsOnlyWhenThereIsAnEmptyEdgeToTrim()
    {
        Assert.Null(CameraRawGeometry.CropFit((0, 0, 100, 100), 100, 100));
        Assert.Null(CameraRawGeometry.CropFit((0, 0, 0, 0), 100, 100));
        var band = CameraRawGeometry.CropFit((10, 20, 90, 30), 100, 100)!.Value;
        Assert.Equal(new RectI(10, 20, 80, 10), band.Crop);
        Assert.Equal(1.25, band.Scale);
        Assert.Equal(0.0, band.Draw.X, 6);
        Assert.Equal(43.75, band.Draw.Y, 6);
        Assert.Equal(100.0, band.Draw.Width, 6);
        Assert.Equal(12.5, band.Draw.Height, 6);

        // A frame shifted three pixels right: trimmed to its content and fitted back with nothing to spare.
        var shifted = CameraRawGeometry.CropFit((3, 0, 100, 100), 100, 100)!.Value;
        Assert.Equal(new RectI(3, 0, 97, 100), shifted.Crop);
        Assert.Equal(1.0, shifted.Scale);
        Assert.Equal(1.5, shifted.Draw.X, 6);
        Assert.Equal(97.0, shifted.Draw.Width, 6);
    }

    [Fact]
    public void ConstrainCropFitsWhatIsLeftBackIntoTheSameFrame()
    {
        var source = Speckled(100, 60);
        // Scale at −50 shrinks the picture to half about the center, so the frame is empty out to row 15 and
        // column 25; trimming to the content and fitting it back fills the whole frame at exactly 2x.
        var shrunk = CameraRawGeometry.Apply(source, Geometry(scale: -50));
        Assert.Equal((25, 15, 75, 45), CameraRawGeometry.BoundsOf(shrunk));
        var frame = CameraRawGeometry.Apply(source, Geometry(scale: -50, crop: true));
        Assert.Equal((100, 60), (frame.Width, frame.Height));
        var refit = CameraRawGeometry.BoundsOf(frame);
        // The fit lands a hair shy of the frame's far edge — the resampler's documented soft edge, measured here
        // rather than smoothed over: 99 columns and 59 rows of coverage out of a 50×30 band that was trimmed.
        Assert.True(refit.Right - refit.Left >= 96 && refit.Bottom - refit.Top >= 56 && refit.Left == 0 && refit.Top == 0,
            $"Constrain Crop refitted to {refit}, which is not the frame it was asked to fill.");
        // And the empty corners the shrink left behind are painted over by the fit: the same spot that held
        // nothing now holds the middle of the layer.
        Assert.Equal(0, Read(shrunk, 10, 10).Item4);
        Assert.True(Read(frame, 10, 10).Item4 > 0, "Constrain Crop kept the empty corner empty.");
    }

    [Fact]
    public void GeometryRunsAheadOfThePassesThatReadPosition()
    {
        var both = Speckled(64, 64);
        var staged = Speckled(64, 64);
        // The vignette is the position-owning pass: bend first and the corners it lifts are other pixels than
        // when it runs first. The two orders only agree if the passes really do run in one order, so a swap shows.
        var geometryOnly = Geometry(offsetX: 20, offsetY: 20);
        var opticsOnly = new CameraRawSettings { OpticsVignetteAmount = 100, OpticsVignetteMidpoint = 25 };
        staged = CameraRawGeometry.Apply(staged, geometryOnly);
        CameraRawEngine.Apply(staged, opticsOnly);
        CameraRawEngine.Apply(both, new CameraRawSettings
        {
            GeometryOffsetX = 20, GeometryOffsetY = 20, OpticsVignetteAmount = 100, OpticsVignetteMidpoint = 25,
        });
        Assert.Equal(staged.Pixels, both.Pixels);
    }

    [Fact]
    public void AGradeThatOnlyBendsGeometryStillPublishesAWholeFrame()
    {
        var source = Speckled(40, 24);
        var frame = CameraRawGeometry.Apply(source, Geometry(vertical: 50, rotate: 10));
        Assert.Equal((40, 24), (frame.Width, frame.Height));
        // Directional, because the sampler is the port's: a keystone has to move the top edge inward, keep the
        // frame's own size, and leave the corners the bend pushes out transparent.
        Assert.Equal(0, Read(frame, 0, 0).Item4);
        Assert.True(CameraRawGeometry.BoundsOf(frame).Right > 0, "the keystone produced no coverage at all.");
        Assert.NotEqual(source.Pixels, frame.Pixels);
    }
}
