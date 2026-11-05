using Compositor.Core;
using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// Hand-computed against the macOS rules (Guides.swift:66-119): the opacity is the majors' alpha in hundredths
/// and the subdivisions are that times 28/45, so 45% means 28% and 90% means 56%. Nothing reads the class back
/// at itself — every expected number below is written out.
/// </summary>
public class GridAppearanceTests
{
    [Fact]
    public void DefaultsAreTheMacOSSwatchAndStyle()
    {
        var appearance = GridAppearance.Default;
        Assert.Equal(GridAppearance.Preset.LightGray, appearance.ColorPreset);
        Assert.Equal(GridAppearance.LineStyle.Lines, appearance.Style);
        Assert.Equal(45, appearance.Opacity);
        Assert.Equal(new AdjustmentColor(.7, .7, .7), appearance.Color);
        Assert.Equal(.45, appearance.MajorAlpha, 12);
        Assert.Equal(.28, appearance.SubdivisionAlpha, 12);   // .45 * 28 / 45
        Assert.Equal(new GridAppearance(), appearance);
    }

    [Fact]
    public void SubdivisionsScaleWithTheMajorsInsteadOfStayingPut()
    {
        // 90% majors ⇒ 90 * 28/45 = 56%, and 1% ⇒ 28/4500: the fainter pair is a ratio, not a constant.
        Assert.Equal(.56, new GridAppearance(opacity: 90).SubdivisionAlpha, 12);
        Assert.Equal(.9, new GridAppearance(opacity: 90).MajorAlpha, 12);
        Assert.Equal(28d / 4500d, new GridAppearance(opacity: 1).SubdivisionAlpha, 12);
    }

    [Fact]
    public void OpacityOutsideTheRangeIsPulledBackIntoIt()
    {
        Assert.Equal(1, new GridAppearance(opacity: 0).Opacity);
        Assert.Equal(1, new GridAppearance(opacity: -40).Opacity);
        Assert.Equal(100, new GridAppearance(opacity: 250).Opacity);
        Assert.Equal(.01, new GridAppearance(opacity: int.MinValue).MajorAlpha, 12);
    }

    [Fact]
    public void CustomColorIsUsedOnlyWhileCustomIsChosenAndSurvivesBeingLeft()
    {
        var picked = new GridAppearance(GridAppearance.Preset.Custom, new AdjustmentColor(.1, .2, .3));
        Assert.Equal(new AdjustmentColor(.1, .2, .3), picked.Color);
        // Switching to a preset shows the preset's own color, and the pick is still there to come back to.
        var switched = new GridAppearance(GridAppearance.Preset.Cyan, picked.CustomColor);
        Assert.Equal(new AdjustmentColor(0, 1, 1), switched.Color);
        Assert.Equal(new AdjustmentColor(.1, .2, .3), switched.CustomColor);
    }

    [Fact]
    public void UnusableCustomColorsFallBackToTheDefaultGray()
    {
        Assert.Equal(new AdjustmentColor(.7, .7, .7),
            new GridAppearance(GridAppearance.Preset.Custom, new AdjustmentColor(2, .5, .5)).Color);
        Assert.Equal(new AdjustmentColor(.7, .7, .7),
            new GridAppearance(GridAppearance.Preset.Custom, new AdjustmentColor(double.NaN, 0, 0)).Color);
        Assert.Equal(new AdjustmentColor(.7, .7, .7),
            new GridAppearance(GridAppearance.Preset.Custom).Color);
    }

    [Fact]
    public void EachPresetIsTheMacOSColorAndEveryOneRoundTripsThroughItsName()
    {
        Assert.Equal(new AdjustmentColor(.29, .78, 1), GridAppearance.PresetColor(GridAppearance.Preset.LightBlue));
        Assert.Equal(new AdjustmentColor(1, .4, .4), GridAppearance.PresetColor(GridAppearance.Preset.LightRed));
        Assert.Equal(new AdjustmentColor(.25, .8, .25), GridAppearance.PresetColor(GridAppearance.Preset.Green));
        Assert.Equal(new AdjustmentColor(.2, .4, 1), GridAppearance.PresetColor(GridAppearance.Preset.MediumBlue));
        Assert.Equal(new AdjustmentColor(1, 1, 0), GridAppearance.PresetColor(GridAppearance.Preset.Yellow));
        Assert.Equal(new AdjustmentColor(1, 0, 1), GridAppearance.PresetColor(GridAppearance.Preset.Magenta));
        Assert.Equal(new AdjustmentColor(0, 1, 1), GridAppearance.PresetColor(GridAppearance.Preset.Cyan));
        Assert.Equal(new AdjustmentColor(0, 0, 0), GridAppearance.PresetColor(GridAppearance.Preset.Black));

        foreach (GridAppearance.Preset preset in Enum.GetValues<GridAppearance.Preset>())
            Assert.Equal(preset, GridAppearance.ParsePreset(GridAppearance.RawName(preset)));
        foreach (GridAppearance.LineStyle style in Enum.GetValues<GridAppearance.LineStyle>())
            Assert.Equal(style, GridAppearance.ParseStyle(GridAppearance.RawName(style)));

        // A preference file that holds anything else is a first launch, not an error (EditorSession.swift:289-291).
        Assert.Equal(GridAppearance.Preset.LightGray, GridAppearance.ParsePreset("Chartreuse"));
        Assert.Equal(GridAppearance.Preset.LightGray, GridAppearance.ParsePreset(null));
        Assert.Equal(GridAppearance.LineStyle.Lines, GridAppearance.ParseStyle("  "));
    }

    [Fact]
    public void DashPatternsAreTheMacOSScreenPoints()
    {
        Assert.Empty(GridAppearance.DashesFor(GridAppearance.LineStyle.Lines));
        Assert.Equal(new double[] { 4, 3 }, GridAppearance.DashesFor(GridAppearance.LineStyle.DashedLines));
        Assert.Equal(new double[] { 1, 2 }, GridAppearance.DashesFor(GridAppearance.LineStyle.Dots));
    }

    [Fact]
    public void TwoAppearancesCompareOnWhatTheyDraw()
    {
        Assert.Equal(new GridAppearance(opacity: 30).GetHashCode(), new GridAppearance(opacity: 30).GetHashCode());
        Assert.NotEqual(new GridAppearance(opacity: 30), new GridAppearance(opacity: 31));
        Assert.NotEqual(new GridAppearance(GridAppearance.Preset.Yellow), new GridAppearance(GridAppearance.Preset.Cyan));
        Assert.NotEqual(
            new GridAppearance(GridAppearance.Preset.Custom, new AdjustmentColor(.1, .2, .3)),
            new GridAppearance(GridAppearance.Preset.Custom, new AdjustmentColor(.1, .2, .4)));
    }
}
