using Compositor.Core;
using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// What each of Camera Raw's ten panel eyes takes away. The Mac never erases the draft: it renders through
/// <c>renderSettings()</c> (Filters.swift:451-461), which hands the settings through
/// <c>applying(showsLight:…)</c> (CameraRaw.swift:171-204) — Light zeroes six numbers, Color four, Effects the
/// six amounts it lists, and the remaining seven groups are replaced whole by their defaults. The field lists
/// here are that function read one assignment at a time; two independent implementations agreeing, not a run of
/// the Mac.
/// </summary>
public class CameraRawGroupVisibilityTests
{
    private static bool[] Show(params int[] hidden)
    {
        var shown = Enumerable.Repeat(true, CameraRawSettings.Groups).ToArray();
        foreach (int group in hidden) shown[group] = false;
        return shown;
    }

    /// <summary>One number in every group, so "hiding this one also cleared that one" has somewhere to show up.</summary>
    private static CameraRawSettings Graded() => new()
    {
        Exposure = 2, Temperature = 40, Texture = 30,
        CurveShadows = 20, CurveRgb = [new CurvePoint(0, 0), new CurvePoint(0.5, 0.4), new CurvePoint(1, 1)],
        CurveDarkSplit = 40, RefineSaturation = 10,
        MixerHue = { [3] = 15 }, MixerPoints = [new CameraRawMixerPoint(120, 0.5, 0.5, 10, 0, 0, 30, 0.4, 0.4, false)],
        GradingMidtonesSat = 20, SharpenAmount = 50, OpticsDistortion = -20,
        GeometryUpright = CameraRawSettings.GeometryUprightGuided,
        GeometryGuides = [new CameraRawGeometryGuide(0.1, 0.1, 0.5, 0.9)],
        GeometryVertical = -25, CalibrationRedHue = 12,
    };

    [Fact]
    public void HidingLightZeroesItsSixNumbersAndLeavesWhiteBalanceAlone()
    {
        var hidden = Graded().Applying(Show(CameraRawSettings.GroupLight));
        Assert.Equal(0, hidden.Exposure);
        Assert.True(hidden.AdjustsLightColor, "the color group's own numbers were cleared with the light group.");
        Assert.Equal(40, hidden.Temperature);
        Assert.Equal(2, Graded().Exposure, 9);
    }

    [Fact]
    public void HidingColorZeroesFourNumbersButKeepsTheWhiteBalanceMode()
    {
        var graded = Graded();
        graded.WhiteBalance = CameraRawSettings.WhiteBalanceAuto;
        var hidden = graded.Applying(Show(CameraRawSettings.GroupColor));
        Assert.Equal(0, hidden.Temperature);
        Assert.Equal(CameraRawSettings.WhiteBalanceAuto, hidden.WhiteBalance);
    }

    [Fact]
    public void HidingEffectsZeroesTheSixAmountsAndNotTheirSubControls()
    {
        // The Mac's list is texture, clarity, dehaze, glow, vignetteAmount, grainAmount — the midpoint, style,
        // size and roughness of those same sliders stay where they were, because hiding an amount that is
        // already doing nothing cannot make a sub-control visible.
        var graded = Graded();
        graded.VignetteMidpoint = 20; graded.GrainSize = 60; graded.GlowStyle = 2;
        var hidden = graded.Applying(Show(CameraRawSettings.GroupEffects));
        Assert.Equal(0, hidden.Texture);
        Assert.Equal(0, hidden.Glow);
        Assert.Equal(0, hidden.VignetteAmount);
        Assert.Equal(0, hidden.GrainAmount);
        Assert.Equal(20, hidden.VignetteMidpoint, 9);
        Assert.Equal(60, hidden.GrainSize, 9);
        Assert.Equal(2, hidden.GlowStyle);
    }

    [Fact]
    public void HidingCurveReturnsEveryCurveFieldIncludingTheAnchorsAndDividers()
    {
        var hidden = Graded().Applying(Show(CameraRawSettings.GroupCurve));
        Assert.Equal(0, hidden.CurveShadows);
        Assert.Equal(50, hidden.CurveDarkSplit, 9);
        Assert.Equal(0, hidden.RefineSaturation);
        Assert.Equal(CameraRawSettings.LinearCurve(), hidden.CurveRgb);
        Assert.False(hidden.AdjustsCurve);
    }

    [Fact]
    public void HidingTheMixerTakesThePickedPointsWithIt()
    {
        var hidden = Graded().Applying(Show(CameraRawSettings.GroupMixer));
        Assert.Empty(hidden.MixerPoints);
        Assert.Equal(0, hidden.MixerHue[3]);
        Assert.False(hidden.AdjustsMixer);
    }

    [Fact]
    public void HidingGeometryTakesTheGuideLinesAndTheUprightModeWithThem()
    {
        var hidden = Graded().Applying(Show(CameraRawSettings.GroupGeometry));
        Assert.Empty(hidden.GeometryGuides);
        Assert.Equal(CameraRawSettings.GeometryUprightOff, hidden.GeometryUpright);
        Assert.Equal(0, hidden.GeometryVertical);
        Assert.False(hidden.AdjustsGeometry);
    }

    [Fact]
    public void EachGroupTakesOnlyItsOwnNumbers()
    {
        var graded = Graded();
        for (int group = 0; group < CameraRawSettings.Groups; group++)
        {
            var hidden = graded.Applying(Show(group));
            AssertGroupCleared(group, hidden);
            for (int other = 0; other < CameraRawSettings.Groups; other++)
            {
                if (other == group) continue;
                AssertGroupIntact(other, hidden);
            }
        }
    }

    [Fact]
    public void NothingHiddenIsTheSameGradeAndEverythingHiddenIsNoGradeAtAll()
    {
        var graded = Graded();
        var all = graded.Applying(Show());
        Assert.Equal(graded.Exposure, all.Exposure, 9);
        Assert.Equal(graded.CurveRgb.Count, all.CurveRgb.Count);
        Assert.Equal(graded.GeometryGuides.Count, all.GeometryGuides.Count);

        var none = graded.Applying(Show(Enumerable.Range(0, CameraRawSettings.Groups).ToArray()));
        Assert.True(none.IsIdentity, "with all ten eyes off the render should be reading an untouched picture.");
        foreach (int group in Enumerable.Range(0, CameraRawSettings.Groups))
        {
            AssertGroupCleared(group, none);
        }
    }

    [Fact]
    public void AGroupThePanelDidNotMentionCountsAsShown()
    {
        // The Mac's parameters default to true; a short list must not be read as "hide the rest".
        var hidden = Graded().Applying([false, true, true]);
        Assert.Equal(0, hidden.Exposure);
        Assert.Equal(30, hidden.Texture, 9);
        Assert.Equal(20, hidden.CurveShadows, 9);
    }

    [Fact]
    public void LightAndColorAreSeparateGroupsEvenThoughOnePassOfTheKernelServesThemBoth()
    {
        // The Mac has two predicates (CameraRaw.swift:103-108) because the panel has two eyes, and one
        // `adjust_light_color` branch in the kernel. An untouched panel offers no eye at all.
        var light = new CameraRawSettings { Exposure = 1 };
        var color = new CameraRawSettings { Saturation = 10 };
        Assert.True(light.AdjustsLight);
        Assert.False(light.AdjustsColor);
        Assert.True(light.AdjustsLightColor);
        Assert.False(color.AdjustsLight);
        Assert.True(color.AdjustsColor);
        Assert.True(light.AdjustsGroup(CameraRawSettings.GroupLight));
        Assert.False(light.AdjustsGroup(CameraRawSettings.GroupColor));
        Assert.True(color.AdjustsGroup(CameraRawSettings.GroupColor));
        Assert.False(color.AdjustsGroup(CameraRawSettings.GroupCurve));
        Assert.False(new CameraRawSettings().AdjustsGroup(CameraRawSettings.GroupLight));
        Assert.False(new CameraRawSettings().AdjustsGroup(CameraRawSettings.Groups + 1));
    }

    private static void AssertGroupCleared(int group, CameraRawSettings actual)
    {
        switch (group)
        {
            case CameraRawSettings.GroupLight: Assert.Equal(0, actual.Exposure); break;
            case CameraRawSettings.GroupColor: Assert.Equal(0, actual.Temperature); break;
            case CameraRawSettings.GroupEffects: Assert.Equal(0, actual.Texture); break;
            case CameraRawSettings.GroupCurve: Assert.Equal(0, actual.CurveShadows); break;
            case CameraRawSettings.GroupMixer: Assert.Equal(0, actual.MixerHue[3]); break;
            case CameraRawSettings.GroupGrading: Assert.Equal(0, actual.GradingMidtonesSat); break;
            case CameraRawSettings.GroupDetail: Assert.Equal(0, actual.SharpenAmount); break;
            case CameraRawSettings.GroupOptics: Assert.Equal(0, actual.OpticsDistortion); break;
            case CameraRawSettings.GroupGeometry: Assert.Equal(0, actual.GeometryVertical); break;
            case CameraRawSettings.GroupCalibration: Assert.Equal(0, actual.CalibrationRedHue); break;
        }
    }

    private static void AssertGroupIntact(int group, CameraRawSettings actual)
    {
        switch (group)
        {
            case CameraRawSettings.GroupLight: Assert.Equal(2, actual.Exposure, 9); break;
            case CameraRawSettings.GroupColor: Assert.Equal(40, actual.Temperature, 9); break;
            case CameraRawSettings.GroupEffects: Assert.Equal(30, actual.Texture, 9); break;
            case CameraRawSettings.GroupCurve: Assert.Equal(20, actual.CurveShadows, 9); break;
            case CameraRawSettings.GroupMixer: Assert.Equal(15, actual.MixerHue[3], 9); break;
            case CameraRawSettings.GroupGrading: Assert.Equal(20, actual.GradingMidtonesSat, 9); break;
            case CameraRawSettings.GroupDetail: Assert.Equal(50, actual.SharpenAmount, 9); break;
            case CameraRawSettings.GroupOptics: Assert.Equal(-20, actual.OpticsDistortion, 9); break;
            case CameraRawSettings.GroupGeometry: Assert.Equal(-25, actual.GeometryVertical, 9); break;
            case CameraRawSettings.GroupCalibration: Assert.Equal(12, actual.CalibrationRedHue, 9); break;
        }
    }
}
