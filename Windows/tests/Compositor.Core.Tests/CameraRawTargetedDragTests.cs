using Compositor.Core;
using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// Numeric locks for Camera Raw's targeted drag: the 0.35 from document pixels to slider units, the forty-degree
/// window that decides which families move and by how much, the press snapshot that keeps the move repeatable,
/// and the curve point nearest the tone under the pointer. Mirrored line for line from
/// <c>beginCameraRawDrag</c> / <c>dragCameraRaw</c> (CameraRaw.swift:528-560),
/// <c>weights(forHue:)</c> (CameraRawColor.swift:165-171) and <c>nudged(_:near:by:)</c> (:79-100);
/// the numbers come from the drag section of Windows/tools/targeted-golden.py. Two independent implementations
/// agreeing, not a run of the Mac.
/// </summary>
public class CameraRawTargetedDragTests
{
    /// <summary>The rec709 tone of the green the probe's bands are painted with (CameraRaw.swift:634).</summary>
    private const double GreenTone = 0.2126 * 60 / 255 + 0.7152 * 220 / 255 + 0.0722 * 90 / 255;

    private static double[] DragMixer(double hue, double delta, int tab = 0)
    {
        var settings = new CameraRawSettings();
        CameraRawTargetedDrag.ApplyMixer(settings, hue, delta, tab);
        return tab switch { 1 => settings.MixerSaturation, 2 => settings.MixerLuminance, _ => settings.MixerHue };
    }

    [Fact]
    public void ADragMeasuresTwentyPixelsDownAsSevenSliderUnits()
    {
        Assert.Equal(-7.0, CameraRawTargetedDrag.Delta(30, 50), 9);
        Assert.Equal(7.0, CameraRawTargetedDrag.Delta(30, 10), 9);
    }

    [Fact]
    public void ADragMovesEveryFamilyThatSharesTheHueItStartedOn()
    {
        // 150 degrees sits between Greens (120) and Aquas (180), thirty degrees from each: both take a
        // quarter of the delta and neither neighbour reaches far enough to join in.
        var moved = DragMixer(150, -7);
        Assert.Equal(-1.75, moved[3], 9);
        Assert.Equal(-1.75, moved[4], 9);
        Assert.Equal(new[] { 0, 1, 2, 5, 6, 7 }, Enumerable.Range(0, 8).Where(i => moved[i] == 0).ToArray());
    }

    [Fact]
    public void TheWeightWindowIsFortyDegreesWideAndFoldsAroundTheWheel()
    {
        // Reds at 0 degrees is dead center for a 350-degree hue (10 the long way round, not 350), and a hue
        // exactly forty degrees from a family gets nothing at all: the Mac's `> 0` test runs on the weight,
        // so the edge is excluded rather than halved (CameraRaw.swift:548).
        var folded = DragMixer(350, -7);
        Assert.Equal(-5.25, folded[0], 9);
        Assert.Equal(0, folded[1], 9);
        var atTheEdge = DragMixer(100, -7);
        Assert.Equal(-3.5, atTheEdge[3], 9);
        Assert.Equal(0, atTheEdge[2], 9);
        // A hue on the boundary between reds and oranges moves both, and the overlap is not normalized away.
        var straddling = DragMixer(0, -7);
        Assert.Equal(-7.0, straddling[0], 9);
        Assert.Equal(-1.75, straddling[1], 9);
    }

    [Fact]
    public void ALongerDragIsProportionalRatherThanAccumulated()
    {
        // Two moves of the same drag read the press snapshot each time, so forty pixels down is exactly twice
        // twenty, and a second press at the start of the same drag restarts from what it finds (CameraRaw.swift:537).
        var twice = DragMixer(150, -14);
        Assert.Equal(-3.5, twice[3], 9);
        Assert.Equal(-3.5, twice[4], 9);
        var settings = new CameraRawSettings { MixerHue = { [3] = -1.75 } };
        CameraRawTargetedDrag.ApplyMixer(settings, 150, -7);
        Assert.Equal(-3.5, settings.MixerHue[3], 9);
    }

    [Fact]
    public void TheDragStopsAtItsOwnEndOfTheSlider()
    {
        // Each family is clamped on its own: at three hundred pixels of drag the family dead center on the hue
        // has long since run out of slider while its quarter-weighted neighbour is only three quarters down
        // (CameraRaw.swift:551-555).
        var pinned = DragMixer(0, -300);
        Assert.Equal(-100, pinned[0]);
        Assert.Equal(-75.0, pinned[1], 9);
        var bothPinned = DragMixer(0, -500);
        Assert.Equal(-100, bothPinned[0]);
        Assert.Equal(-100, bothPinned[1]);
        var over = DragMixer(0, 500);
        Assert.Equal(100, over[0]);
    }

    [Fact]
    public void TheCurveBranchMovesThePointNearestTheToneUnderThePointer()
    {
        var linear = new List<CurvePoint> { new(0, 0), new(1, 1) };
        var pulled = CameraRawTargetedDrag.Nudge(linear, GreenTone, -7.0 / 100);
        Assert.Equal(new CurvePoint(0, 0), pulled[0]);
        Assert.Equal(0.93, pulled[1].Y, 9);
        Assert.Equal(1.0, pulled[1].X, 12);
        // A push upward from an endpoint has nowhere to go, and the Mac clamps rather than rejecting the drag.
        Assert.Equal(1.0, CameraRawTargetedDrag.Nudge(linear, GreenTone, 7.0 / 100)[1].Y);
        Assert.Equal(0.3, CameraRawTargetedDrag.Nudge(linear, GreenTone, -0.7)[1].Y, 9);
        // The nearest by x, first wins a tie, and only that point's y moves (CameraRawColor.swift:88-92).
        var middle = new List<CurvePoint> { new(0, 0), new(0.5, 0.5), new(1, 1) };
        var nudged = CameraRawTargetedDrag.Nudge(middle, 0.5, 0.1);
        Assert.Equal(new CurvePoint(0, 0), nudged[0]);
        Assert.Equal(new CurvePoint(1, 1), nudged[2]);
        Assert.Equal(0.6, nudged[1].Y, 9);
        Assert.Empty(CameraRawTargetedDrag.Nudge(Array.Empty<CurvePoint>(), 0.5, 0.1));
    }

    [Fact]
    public void AClampedNudgeOfALinearCurveIsStillNoAdjustment()
    {
        // Pushing the top endpoint up leaves the curve linear, so the grade does not claim an adjustment it
        // never made (CameraRawColor.swift:39-41 and the port's AdjustsCurve).
        var settings = new CameraRawSettings();
        settings.CurveRgb = CameraRawTargetedDrag.Nudge(settings.CurveRgb, GreenTone, 0.07);
        Assert.False(settings.AdjustsCurve);
        Assert.True(settings.IsIdentity);
        settings.CurveRgb = CameraRawTargetedDrag.Nudge(settings.CurveRgb, GreenTone, -0.07);
        Assert.True(settings.AdjustsCurve);
        Assert.False(settings.IsIdentity);
    }
}
