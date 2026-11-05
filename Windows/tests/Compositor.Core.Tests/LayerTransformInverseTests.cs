using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// The inverse of <see cref="LayerTransform.Point"/> — the direction editing text in place needs.
/// Every expectation below is computed by hand from the placement's own definition (centre + (0.5,0.5)),
/// not read back out of the implementation, so a sign slip in the un-rotation cannot hide.
/// </summary>
public class LayerTransformInverseTests
{
    private static LayerTransform Placed(double rotation = 0) =>
        new() { Origin = new PointD(10, 20), Size = new SizeD(100, 50), Rotation = rotation };

    [Fact]
    public void UnrotatedPlacementDividesBySizeFromTheCentre()
    {
        // Centre is (60, 45). (35, 30) is 25 left and 15 above it: a quarter across, a fifth down.
        var unit = Placed().InversePoint(new PointD(35, 30));
        Assert.Equal(.25, unit.X, 8);
        Assert.Equal(.2, unit.Y, 8);
        // And the corners, computed as centre minus half the extent.
        var topLeft = Placed().InversePoint(new PointD(10, 20));
        Assert.Equal(0, topLeft.X, 8);
        Assert.Equal(0, topLeft.Y, 8);
        var bottomRight = Placed().InversePoint(new PointD(110, 70));
        Assert.Equal(1, bottomRight.X, 8);
        Assert.Equal(1, bottomRight.Y, 8);
    }

    [Fact]
    public void QuarterTurnMapsTheUnrotatedTopLeftToTheHandComputedCorner()
    {
        // Rotating 90° about (60, 45) sends the unit (0,0) corner to (85, −5); asking back must return (0,0).
        var quarter = Placed(90);
        var rotatedCorner = quarter.Point(new PointD(0, 0));
        Assert.Equal(85, rotatedCorner.X, 8);
        Assert.Equal(-5, rotatedCorner.Y, 8);
        var unit = quarter.InversePoint(new PointD(85, -5));
        Assert.Equal(0, unit.X, 8);
        Assert.Equal(0, unit.Y, 8);
        // The centre of the box is its own answer at any angle.
        var centre = quarter.InversePoint(new PointD(60, 45));
        Assert.Equal(.5, centre.X, 8);
        Assert.Equal(.5, centre.Y, 8);
        // An interior point, hand-carried through the turn: unit (.25, .2) is (−25, −15) from centre before
        // rotating, and a quarter turn sends (x, y) to (−y, x), so the document point is (60+15, 45−25).
        var interior = quarter.Point(new PointD(.25, .2));
        Assert.Equal(75, interior.X, 8);
        Assert.Equal(20, interior.Y, 8);
        var backInside = quarter.InversePoint(new PointD(75, 20));
        Assert.Equal(.25, backInside.X, 8);
        Assert.Equal(.2, backInside.Y, 8);
    }

    [Fact]
    public void HalfTurnUnflipsBothAxesAboutTheCentre()
    {
        // At 180° the unit (0,0) corner lands opposite: (110, 70).
        var half = Placed(180);
        var halfCorner = half.Point(new PointD(0, 0));
        Assert.Equal(110, halfCorner.X, 8);
        Assert.Equal(70, halfCorner.Y, 8);
        var halfBack = half.InversePoint(new PointD(110, 70));
        Assert.Equal(0, halfBack.X, 8);
        Assert.Equal(0, halfBack.Y, 8);
        var halfOpposite = half.InversePoint(new PointD(10, 20));
        Assert.Equal(1, halfOpposite.X, 8);
        Assert.Equal(1, halfOpposite.Y, 8);
    }

    [Fact]
    public void APlacedLargerThanTheTextKeepsTheUnitAndDividesInTheCaller()
    {
        // Point text measured 40×20 placed on a layer whose box is 100×50: the placement's unit answer is
        // what the caller multiplies by the text's logical size, so the caret lands where the pixels are.
        var document = Placed().InversePoint(new PointD(35, 32.5));
        Assert.Equal(.25, document.X, 8);
        Assert.Equal(.25, document.Y, 8);
        var logical = new SizeD(40, 20);
        Assert.Equal(10, document.X * logical.Width, 8);
        Assert.Equal(5, document.Y * logical.Height, 8);
    }

    [Fact]
    public void PointsOutsideTheBoxSurviveInsteadOfClampingOrWrapping()
    {
        // Past the right edge (160 is half a width beyond it → 1.5), and above the top (75 above centre on a
        // box 50 tall → −1). First draft of this test said −0.5 and the implementation said −1: the
        // implementation was right and the arithmetic here was the slip, so the division is written out.
        var unit = Placed().InversePoint(new PointD(160, -30));
        Assert.Equal(1.5, unit.X, 8);
        Assert.Equal(-1, unit.Y, 8);
    }
}
