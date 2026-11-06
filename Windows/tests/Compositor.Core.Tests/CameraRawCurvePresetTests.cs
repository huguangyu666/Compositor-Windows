using Compositor.Core;
using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// Numeric locks for the Camera Raw point page's presets: the three point lists the Mac installs, the fact that
/// choosing Custom installs nothing, the read-back that decides what the picker shows, and the sixteen-point cap
/// that page carries while the model takes more. Transcribed from `CameraRawCurveSettings.linear／
/// mediumContrast／strongContrast` (CameraRawColor.swift:35-37) and `CurvePreset`
/// (CameraRawColorControls.swift:244-259); the add guard is :167 there. These are the Mac's own literals rather
/// than a computed golden, so the claim is "port and original agree on the list", not "the Mac was run".
/// </summary>
public class CameraRawCurvePresetTests
{
    private static CurvePoint[] Points(int preset) => CameraRawCurvePresets.Points(preset)!.ToArray();

    [Fact]
    public void TheThreePresetShapesAreTheListsTheMacShips()
    {
        Assert.Equal(new[] { new CurvePoint(0, 0), new CurvePoint(1, 1) },
            Points(CameraRawCurvePresets.Linear));
        Assert.Equal(new[] { new CurvePoint(0, 0), new CurvePoint(0.25, 0.18),
                             new CurvePoint(0.75, 0.82), new CurvePoint(1, 1) },
            Points(CameraRawCurvePresets.MediumContrast));
        Assert.Equal(new[] { new CurvePoint(0, 0), new CurvePoint(0.25, 0.10),
                             new CurvePoint(0.75, 0.90), new CurvePoint(1, 1) },
            Points(CameraRawCurvePresets.StrongContrast));
    }

    [Fact]
    public void ChoosingCustomInstallsNothing()
    {
        // `CurvePreset.points` is nil for .custom and `applyPreset` returns before storing (Ctrls:226-229), so
        // the picker reading Custom is a report about the curve, not an action on it.
        Assert.Null(CameraRawCurvePresets.Points(CameraRawCurvePresets.Custom));
    }

    [Fact]
    public void ThePickerReadsBackTheShapeTheCurveAlreadyIs()
    {
        Assert.Equal(CameraRawCurvePresets.Linear, CameraRawCurvePresets.Matching(Points(CameraRawCurvePresets.Linear)));
        Assert.Equal(CameraRawCurvePresets.MediumContrast,
            CameraRawCurvePresets.Matching(Points(CameraRawCurvePresets.MediumContrast)));
        Assert.Equal(CameraRawCurvePresets.StrongContrast,
            CameraRawCurvePresets.Matching(Points(CameraRawCurvePresets.StrongContrast)));
    }

    [Fact]
    public void ACurveOneHundredthOffAPresetIsCustomRatherThanTheNearestPreset()
    {
        // The Mac compares whole lists with ==, so there is no tolerance to fall back on.
        var nudged = Points(CameraRawCurvePresets.MediumContrast);
        nudged[1] = new CurvePoint(0.25, 0.19);
        Assert.Equal(CameraRawCurvePresets.Custom, CameraRawCurvePresets.Matching(nudged));
        // And a curve that contains another preset's points in a different order is not that preset either.
        var reordered = Points(CameraRawCurvePresets.StrongContrast).AsEnumerable().Reverse().ToArray();
        Assert.Equal(CameraRawCurvePresets.Custom, CameraRawCurvePresets.Matching(reordered));
    }

    [Fact]
    public void ADifferentNumberOfPointsIsNeverAMatch()
    {
        // Medium Contrast with a point taken out is four long by one short of two, which is the count check, not
        // the coordinates, that decides it.
        var shortened = new[] { new CurvePoint(0, 0), new CurvePoint(0.25, 0.18), new CurvePoint(1, 1) };
        Assert.Equal(CameraRawCurvePresets.Custom, CameraRawCurvePresets.Matching(shortened));
        var padded = Points(CameraRawCurvePresets.Linear).Append(new CurvePoint(0.5, 0.4)).ToArray();
        Assert.Equal(CameraRawCurvePresets.Custom, CameraRawCurvePresets.Matching(padded));
        Assert.Equal(CameraRawCurvePresets.Custom, CameraRawCurvePresets.Matching(Array.Empty<CurvePoint>()));
    }

    [Fact]
    public void TheCameraRawPointPageStopsAtSixteenAnchors()
    {
        // The Mac's press refuses a seventeenth (Ctrls:167) while the model itself accepts up to 32
        // (Curves.swift:11) and Image ▸ Curves' editor offers 32 (CurvesControls.swift:43) — so this number is
        // the page's, and it is the one the port's add guard has to use.
        Assert.Equal(16, CameraRawCurvePresets.PointsMax);
        Assert.Equal(4, CameraRawCurvePresets.Names.Length);
    }
}
