using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// Tests for the persistent selection feather: the scalar the Mac's DocumentSelection
/// carries beside its path, kept across Expand/Contract/Invert and combined
/// root-sum-of-squares by Feather (Document/Selection.swift:325-331).
/// </summary>
public class SelectionFeatherTests
{
    [Fact]
    public void NewSelectionsStartWithZeroFeather()
    {
        // The marquee factories and the wand's shared entry all begin unfeathered, as the
        // Mac's applySelection builds a fresh DocumentSelection without one.
        var rectangle = Selection.Rectangle(16, 16, 4, 4, 8, 8);
        Assert.Equal(0, rectangle.Feather);

        var ellipse = Selection.Ellipse(16, 16, 8, 8, 4, 3);
        Assert.Equal(0, ellipse.Feather);

        var wandStyle = Selection.FromCoverage(rectangle.Coverage);
        Assert.Equal(0, wandStyle.Feather);

        // Zero feather means the hard edge samples at full and nothing outside it, and the
        // bounds stay the mask's own tight rectangle.
        Assert.Equal(255, rectangle.CoverageAt(4, 8));
        Assert.Equal(0, rectangle.CoverageAt(3, 8));
        Assert.Equal(new RectI(4, 4, 8, 8), rectangle.Bounds);
    }

    [Fact]
    public void FeatherComposesRootSumOfSquaresAndKeepsTheMask()
    {
        var selection = Selection.Rectangle(16, 16, 4, 4, 8, 8);

        // Two soft edges spread a little less than their sum, as blurs do (Mac
        // featherSelection): 3 then 4 softens to 5, not 7.
        var feathered = selection.Feathered(3).Feathered(4);
        Assert.Equal(5.0, feathered.Feather, 6);

        // The fade is a scalar beside the mask, never burned in: the coverage is the
        // original hard mask, still sampling 255 inside and 0 outside at the outline.
        Assert.Same(selection.Coverage, feathered.Coverage);
        Assert.Equal(255, feathered.Coverage.Pixels[8 * 16 + 4]);
        Assert.Equal(0, feathered.Coverage.Pixels[8 * 16 + 3]);

        // The combined fade caps at 250, the Mac's ceiling.
        Assert.Equal(250.0, selection.Feathered(250).Feathered(240).Feather, 6);
    }

    [Fact]
    public void ExpandAndContractPreserveTheFeather()
    {
        var selection = Selection.Rectangle(16, 16, 4, 4, 8, 8).Feathered(2);

        // The Mac's resizeSelection carries the feather over untouched (only Feather
        // itself combines amounts); the geometry changes, the fade does not.
        var expanded = selection.Expanded(1);
        Assert.Equal(2.0, expanded.Feather, 6);
        // The outline really grew: at feather 2 the sample at the new outline is already in
        // the softened ramp, so it is between the old hard edge and the new one — well above
        // the untouched pixel further out, and the fully-inside center stays 255.
        Assert.Equal(255, expanded.CoverageAt(8, 8));
        Assert.True(expanded.CoverageAt(3, 8) > 0, "the outline grew over (3,8)");

        var contracted = selection.Contracted(1);
        Assert.NotNull(contracted);
        Assert.Equal(2.0, contracted!.Feather, 6);
    }

    [Fact]
    public void InvertPreservesTheFeather()
    {
        var selection = Selection.Rectangle(16, 16, 4, 4, 8, 8).Feathered(3);

        var inverse = selection.Inverted(16, 16);

        Assert.Equal(3.0, inverse.Feather, 6);
        Assert.Equal(0, inverse.CoverageAt(8, 8));   // the old inside is now out
        Assert.Equal(255, inverse.CoverageAt(15, 8)); // far outside the old rect is fully in
    }

    [Fact]
    public void FeatherSoftensSamplingWithoutTouchingTheMask()
    {
        var selection = Selection.Rectangle(16, 16, 4, 4, 8, 8);

        var feathered = selection.Feathered(1);

        // Sampling applies the fade: the boundary pixel sits between full and empty, the
        // center stays fully selected, and the far outside stays empty.
        Assert.True(feathered.CoverageAt(4, 8) is > 0 and < 255);
        Assert.Equal(255, feathered.CoverageAt(8, 8));
        Assert.Equal(0, feathered.CoverageAt(0, 0));

        // The bounds grow with the visible falloff — two feathers' worth, the Mac's
        // coverageBounds of ceil(feather * 2) per side.
        Assert.Equal(new RectI(2, 2, 12, 12), feathered.Bounds);
    }
}
