using Xunit;

namespace Compositor.Core.Tests;

public class BrushPointerPathTests
{
    [Fact]
    public void SlackStringRejectsJitterButKeepsThePointerForRelease()
    {
        var path = new BrushPointerPath(new(0, 0), 10);
        Assert.Null(path.Move(new(6, 2), 1)); Assert.Equal(new PointD(0, 0), path.LastPainted);
        Assert.Equal(new PointD(6, 2), path.Pointer);
        Assert.Equal(new PointD(10, 0), path.Move(new(20, 0), 1));
        Assert.Equal(new PointD(20, 0), path.Pointer);
    }

    [Theory]
    [InlineData(.5, 40, 20)]
    [InlineData(1, 20, 10)]
    [InlineData(2, 20, 15)]
    public void StringLengthUsesTheCurrentZoom(double zoom, double input, double expected)
    {
        var path = new BrushPointerPath(new(0, 0), 10);
        Assert.Equal(new PointD(expected, 0), path.Move(new(input, 0), zoom));
    }

    [Fact]
    public void DiagonalStringMovesOnlyByTheExcessDistance()
    {
        var path = new BrushPointerPath(new(0, 0), 10);
        var point = path.Move(new(20, 20), 1)!.Value;
        Assert.Equal(20 - 10 / Math.Sqrt(2), point.X, 10); Assert.Equal(point.X, point.Y);
    }

    [Fact]
    public void ShiftLatchesAnAxisUntilReleasedAndCanStartMidStroke()
    {
        var path = new BrushPointerPath(new(0, 0)); path.ResetAxis(new(0, 0), true);
        Assert.Null(path.Move(new(1, 1), 1, true));
        Assert.Equal(new PointD(10, 0), path.Move(new(10, 2), 1, true));
        Assert.Equal(new PointD(2, 0), path.Move(new(2, 30), 1, true));
        Assert.Equal(new PointD(2, 30), path.Move(new(2, 30), 1));
        Assert.Equal(new PointD(3, 30), path.Move(new(3, 30), 1));
        Assert.Equal(new PointD(3, 40), path.Move(new(4, 40), 1, true));
    }

    [Fact]
    public void AxisConstraintRunsBeforeSmoothingAndRetainsTheConstrainedEndpoint()
    {
        var path = new BrushPointerPath(new(0, 0), 5); path.ResetAxis(new(0, 0), true);
        Assert.Equal(new PointD(5, 0), path.Move(new(10, 1), 1, true));
        Assert.Equal(new PointD(10, 0), path.Pointer);
    }

    [Fact]
    public void ZeroSmoothingPassesThroughAndInvalidPointsDoNotPoisonThePath()
    {
        var path = new BrushPointerPath(new(1, 2));
        Assert.Equal(new PointD(4, 6), path.Move(new(4, 6), 1)); Assert.Null(path.Move(new(double.NaN, 0), 1));
        Assert.Equal(new PointD(4, 6), path.Pointer); Assert.Null(path.Move(new(4, 6), 1));
    }
}
