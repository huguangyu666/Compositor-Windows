using Compositor.Core;
using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// Hand-computed against the macOS rules (Guides.swift:36-61): a 64-pixel major split eight ways is a line
/// every 8 pixels, counted from the origin, each landing on a whole pixel. Nothing here reads LayoutGrid back
/// at itself — the expected lists are written out.
/// </summary>
public class LayoutGridTests
{
    [Fact]
    public void DefaultsAreTheMacOSGrid()
    {
        var grid = LayoutGrid.Default;
        Assert.Equal(64, grid.Spacing);
        Assert.Equal(8, grid.Subdivisions);
        Assert.Equal(8, grid.Step);
        Assert.Equal(new LayoutGrid(), grid);
    }

    [Fact]
    public void LinesStopAtTheLastWholeStepInsideTheLength()
    {
        // floor(100 / 8 + 0.001) = 12, so thirteen lines: 0 through 96. The 100th pixel carries no line
        // because a 13th step would sit past the edge.
        Assert.Equal(Enumerable.Range(0, 13).Select(index => (double)index * 8), new LayoutGrid().Lines(100));
        Assert.Equal(new double[] { 0 }, new LayoutGrid().Lines(-1));
    }

    [Fact]
    public void CountingFromTheOriginKeepsUnevenStepsOnTheMajors()
    {
        // Seven pixels split twice is a 3.5 step. Accumulating 3.5 would drift; counting and rounding puts
        // the majors exactly where the spacing says, and 3.5 rounds away from zero — as Swift's rounded().
        var grid = new LayoutGrid(spacing: 7, subdivisions: 2);
        Assert.Equal(3.5, grid.Step);
        Assert.Equal(new double[] { 0, 4, 7, 11, 14 }, grid.Lines(14));
        Assert.True(grid.IsMajor(14));
        Assert.False(grid.IsMajor(11));
    }

    [Fact]
    public void OutOfRangeRequestsClampToWhatCanStillBeDrawn()
    {
        var tiny = new LayoutGrid(spacing: 1, subdivisions: 999);
        Assert.Equal(2, tiny.Spacing);
        Assert.Equal(2, tiny.Subdivisions);   // never more parts than there are pixels to hold them
        Assert.Equal(1, tiny.Step);

        Assert.Equal(4096, new LayoutGrid(spacing: 99_999).Spacing);
        Assert.Equal(64, new LayoutGrid(spacing: 64, subdivisions: 100).Subdivisions);
        Assert.Equal(10, new LayoutGrid(spacing: 10, subdivisions: 64).Subdivisions);
        Assert.Equal(1, new LayoutGrid(spacing: 64, subdivisions: 0).Subdivisions);
    }

    [Fact]
    public void MajorsAreJudgedOnTheRoundedPixelSoAHairOffStillCounts()
    {
        var grid = new LayoutGrid();
        Assert.True(grid.IsMajor(0));
        Assert.True(grid.IsMajor(128));
        Assert.False(grid.IsMajor(96));
        Assert.True(grid.IsMajor(63.6));  // 63.6 is a 64 line drawn at a hair's offset
    }

    [Fact]
    public void TwoGridsCompareByWhatTheyDraw()
    {
        Assert.Equal(new LayoutGrid(64, 8).GetHashCode(), new LayoutGrid(64, 8).GetHashCode());
        Assert.NotEqual(new LayoutGrid(64, 8), new LayoutGrid(64, 16));
    }
}
