using Xunit;

namespace Compositor.Core.Tests;

public class LassoSelectionTests
{
    [Fact]
    public void PolygonFillsItsInteriorAndClipsToTheCanvas()
    {
        PointD[] points = [new(-2, -2), new(4, -2), new(4, 4), new(-2, 4)];
        var selection = LassoSelection.Rasterize(8, 8, points, antialiased: false);
        Assert.Equal(new RectI(0, 0, 4, 4), selection.Bounds);
        Assert.Equal(255, selection.CoverageAt(1, 1));
        Assert.Equal(0, selection.CoverageAt(4, 1));
        Assert.Equal(0, selection.Feather);
    }

    [Fact]
    public void ConcaveAndSelfCrossingOutlinesUseNonzeroWinding()
    {
        PointD[] concave = [new(0, 0), new(5, 0), new(5, 2), new(2, 2), new(2, 5), new(0, 5)];
        var selection = LassoSelection.Rasterize(6, 6, concave, false);
        Assert.Equal(255, selection.CoverageAt(3, 1));
        Assert.Equal(255, selection.CoverageAt(1, 3));
        Assert.Equal(0, selection.CoverageAt(3, 3));
        PointD[] doubled = [new(0, 0), new(4, 0), new(4, 4), new(0, 4), new(0, 0), new(4, 0), new(4, 4), new(0, 4)];
        Assert.Equal(255, LassoSelection.Rasterize(6, 6, doubled, false).CoverageAt(2, 2));
        PointD[] crossed = [new(0, 0), new(6, 6), new(0, 6), new(6, 0)];
        var crossing = LassoSelection.Rasterize(6, 6, crossed, false);
        Assert.Equal(255, crossing.CoverageAt(2, 1));
        Assert.Equal(0, crossing.CoverageAt(0, 3));
    }

    [Fact]
    public void AntialiasingOnlySoftensBoundaryPixels()
    {
        PointD[] triangle = [new(0, 0), new(4, 0), new(0, 4)];
        var smooth = LassoSelection.Rasterize(6, 6, triangle);
        var hard = LassoSelection.Rasterize(6, 6, triangle, false);
        Assert.Equal(255, smooth.CoverageAt(1, 1));
        Assert.Equal(128, smooth.CoverageAt(1, 2));
        Assert.Equal(0, hard.CoverageAt(1, 2));
        Assert.All(hard.Coverage.Pixels, pixel => Assert.True(pixel is 0 or 255));
    }

    [Fact]
    public void CombinationResetsFeatherAndKeepsExplicitEmptySelections()
    {
        var current = Selection.Rectangle(8, 8, 0, 0, 4, 4).Feathered(4);
        var incoming = Selection.Rectangle(8, 8, 2, 0, 4, 4);
        var added = LassoSelection.Combine(current, incoming, LassoSelectionMode.Add)!;
        Assert.Equal(255, added.CoverageAt(1, 1));
        Assert.Equal(255, added.CoverageAt(5, 1));
        Assert.Equal(0, added.Feather);
        var removed = LassoSelection.Combine(current, incoming, LassoSelectionMode.Subtract)!;
        Assert.Equal(255, removed.CoverageAt(1, 1));
        Assert.Equal(0, removed.CoverageAt(3, 1));
        var empty = LassoSelection.Combine(current, current, LassoSelectionMode.Subtract);
        Assert.NotNull(empty);
        Assert.Equal(new RectI(0, 0, 0, 0), empty.Bounds);
        Assert.All(empty.Coverage.Pixels, pixel => Assert.Equal(0, pixel));
        Assert.Null(LassoSelection.Combine(null, incoming, LassoSelectionMode.Subtract));
        Assert.Same(incoming, LassoSelection.Combine(null, incoming, LassoSelectionMode.Add));
        Assert.Same(incoming, LassoSelection.Combine(current, incoming, LassoSelectionMode.Replace));
    }

    [Fact]
    public void DegenerateAndNonfiniteOutlinesAreExplicitlyEmpty()
    {
        PointD[][] outlines = [[], [new(1, 1), new(2, 2)], [new(0, 0), new(double.NaN, 2), new(2, 0)]];
        foreach (var points in outlines)
        {
            var selection = LassoSelection.Rasterize(4, 4, points);
            Assert.Equal(new RectI(0, 0, 0, 0), selection.Bounds);
            Assert.All(selection.Coverage.Pixels, pixel => Assert.Equal(0, pixel));
        }
    }

    [Fact]
    public void SubtractingEverythingPreventsPaintingThroughAnEmptySelection()
    {
        var document = EditorDocument.Create(8, 8);
        var layer = document.Snapshot.Manifest.Layers[0];
        var selection = Selection.Rectangle(8, 8, 0, 0, 8, 8);
        document.SetSelection(LassoSelection.Combine(selection, selection, LassoSelectionMode.Subtract));
        var pixels = (byte[])document.Snapshot.Images[layer.Id].Pixels.Clone();
        Assert.False(document.BeginStroke(new object(), layer.Id, new BrushSettings(3, 1, 1, 255, 0, 0), new PointD(4, 4)));
        Assert.Equal(pixels, document.Snapshot.Images[layer.Id].Pixels);
    }
}
