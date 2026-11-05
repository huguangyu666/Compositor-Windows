using Xunit;

namespace Compositor.Core.Tests;

public class SelectionTranslationTests
{
    [Fact]
    public void TranslationCanLeaveTheCanvasAndComeBackWithoutLosingCoverage()
    {
        var original = Selection.Rectangle(8, 8, 2, 2, 4, 4).Feathered(2);
        var outside = original.Translated(10, -12);
        Assert.False(outside.IsEmpty); Assert.Equal(new RectI(12, -10, 4, 4), outside.OutlineBounds);
        Assert.Equal(new RectI(8, 0, 0, 0), outside.Bounds); Assert.Equal(2, outside.Feather);
        Assert.Same(original.OutlineCoverage, outside.OutlineCoverage);
        Assert.All(outside.Coverage.Pixels, value => Assert.Equal(0, value));
        var restored = outside.Translated(-10, 12);
        Assert.Same(original.Coverage, restored.Coverage); Assert.Equal(original.Bounds, restored.Bounds);
        Assert.Equal(original.CoverageAt(3, 3), restored.CoverageAt(3, 3));
        Assert.Equal(original.Coverage.Pixels, restored.Coverage.Pixels);
    }

    [Theory]
    [InlineData(3, 2)]
    [InlineData(-3, -2)]
    [InlineData(20, 0)]
    public void VisibleCoverageProjectsTheLogicalOutlineAtDocumentResolution(int dx, int dy)
    {
        var original = Selection.Rectangle(8, 8, 2, 2, 4, 4);
        var moved = original.Translated(dx, dy);
        Assert.Equal(8, moved.Coverage.Width); Assert.Equal(8, moved.Coverage.Height);
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
        {
            byte expected = original.CoverageAt(x - dx, y - dy);
            Assert.Equal(expected, moved.CoverageAt(x, y)); Assert.Equal(expected, moved.Coverage.Pixels[y * 8 + x]);
        }
        Assert.Equal(0, moved.CoverageAt(-1, 3)); Assert.Equal(0, moved.CoverageAt(8, 3));
    }

    [Fact]
    public void HitTestUsesTheHardOutlineIncludingOutsideTheDocumentAndItsHoles()
    {
        var mask = new MaskSurface(8, 8); mask.Pixels[2 * 8 + 2] = 255; mask.Pixels[2 * 8 + 4] = 255;
        var moved = Selection.FromCoverage(mask).Feathered(3).Translated(-4, 0);
        Assert.True(moved.Contains(new(-1.5, 2.5))); Assert.False(moved.Contains(new(-.5, 2.5)));
        Assert.True(moved.Contains(new(.5, 2.5))); Assert.False(moved.Contains(new(.5, 3.5)));
        Assert.False(moved.Contains(new(double.NaN, 2)));
    }

    [Fact]
    public void TranslationPreservesPartialAntialiasCoverageAndDoesNotChangeTheSource()
    {
        var original = Selection.Ellipse(8, 8, 3, 3, 2.25, 2.25, true);
        var bytes = original.Coverage.Pixels.ToArray(); var moved = original.Translated(2, 1);
        for (int y = 0; y < 7; y++) for (int x = 0; x < 6; x++) Assert.Equal(original.CoverageAt(x, y), moved.CoverageAt(x + 2, y + 1));
        Assert.Equal(bytes, original.Coverage.Pixels); Assert.Same(original.OutlineCoverage, moved.OutlineCoverage);
    }

    [Fact]
    public void FeatherIsAppliedAfterProjectionAtTheNewCanvasEdge()
    {
        var original = Selection.Rectangle(8, 8, 2, 2, 4, 4).Feathered(3);
        var moved = original.Translated(-3, 0); var clipped = Selection.Rectangle(8, 8, -1, 2, 4, 4).Feathered(3);
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) Assert.Equal(clipped.CoverageAt(x, y), moved.CoverageAt(x, y));
        Assert.Equal(3, moved.Feather); Assert.Equal(Selection.Rectangle(8, 8, 2, 2, 4, 4).Coverage.Pixels, original.Coverage.Pixels);
    }

    [Fact]
    public void ContoursKeepTheOriginalShapeOutsideTheCanvas()
    {
        var original = Selection.Rectangle(8, 8, 2, 2, 4, 4); var moved = original.Translated(10, -12);
        var expected = SelectionContours.Build(original).Select(edge => new SelectionEdge(edge.X0 + 10, edge.Y0 - 12, edge.X1 + 10, edge.Y1 - 12)).ToArray();
        Assert.Equal(expected, SelectionContours.Build(moved));
    }

    [Fact]
    public void UndoDistinguishesOffCanvasOutlinesAndRetainsSharedCoverageWithinTheBudget()
    {
        var doc = EditorDocument.Create(1024, 1024); doc.UndoByteBudget = 1024;
        var original = Selection.Rectangle(1024, 1024, 2, 2, 20, 20); doc.SetSelection(original);
        for (int i = 1; i <= 40; i++) Assert.True(doc.SetSelectionUndoable(original.Translated(2000 + i, -2000)));
        for (int i = 39; i >= 0; i--)
        {
            Assert.True(doc.Undo()); Assert.Equal(i == 0 ? new PointD(0, 0) : new PointD(2000 + i, -2000), doc.ActiveSelection!.OutlineOffset);
        }
        Assert.Same(original, doc.ActiveSelection); Assert.False(doc.CanUndo);
    }

    [Fact]
    public void EquivalentTranslatedCoveragePreservesRedoWithoutMaterializingHistory()
    {
        var doc = EditorDocument.Create(8, 8); var original = Selection.Rectangle(8, 8, 1, 1, 3, 3);
        doc.SetSelection(original.Translated(2, 1)); doc.SetSelectionUndoable(null); doc.Undo();
        Assert.False(doc.SetSelectionUndoable(Selection.Rectangle(8, 8, 3, 2, 3, 3))); Assert.True(doc.CanRedo);
        Assert.Same(original.OutlineCoverage, doc.ActiveSelection!.OutlineCoverage);
    }

    [Fact]
    public void NoOpAndEmptyTranslationsReuseTheOriginal()
    {
        var original = Selection.Rectangle(8, 8, 1, 1, 3, 3); var empty = Selection.FromCoverage(new(8, 8));
        Assert.Same(original, original.Translated(0, 0)); Assert.Same(empty, empty.Translated(2, 3));
    }

    [Fact]
    public void FeatherAndContractPreserveTheUnclippedOutline()
    {
        var original = Selection.Rectangle(12, 12, 2, 2, 8, 8); var moved = original.Translated(20, -20);
        var feathered = moved.Feathered(3); Assert.Equal(3, feathered.Feather); Assert.Same(original.Coverage, feathered.Translated(-20, 20).Coverage);
        var contracted = moved.Contracted(1); Assert.False(contracted.IsEmpty);
        Assert.Equal(original.Contracted(1).Coverage.Pixels, contracted.Translated(-20, 20).Coverage.Pixels);
    }

    [Fact]
    public void ExpandCanEnterTheCanvasFromOutsideAndClipsItsResult()
    {
        var outside = Selection.Rectangle(8, 8, 0, 2, 4, 4).Translated(10, 0);
        var expanded = outside.Expanded(3); Assert.Equal(255, expanded.CoverageAt(7, 3)); Assert.Equal(0, expanded.CoverageAt(6, 3));
        Assert.Equal(new PointD(0, 0), expanded.OutlineOffset); Assert.True(expanded.OutlineBounds.X >= 0);
        var edge = Selection.Rectangle(8, 8, 0, 2, 2, 4).Translated(3, 0).Expanded(2);
        Assert.Equal(255, edge.CoverageAt(1, 3));
    }

    [Fact]
    public void MaterializedProjectedMasksStillCountAgainstTheHistoryBudget()
    {
        var doc = EditorDocument.Create(128, 128); doc.UndoByteBudget = 1024;
        var original = Selection.Rectangle(128, 128, 2, 2, 20, 20); doc.SetSelection(original);
        for (int i = 1; i <= 4; i++)
        {
            var moved = original.Translated(i, i); _ = moved.Coverage;
            Assert.True(doc.SetSelectionUndoable(moved));
        }
        Assert.True(doc.Undo()); Assert.False(doc.Undo());
    }

    [Fact]
    public void AnOversizedContentMementoKeepsOnlyTheNewestHistoryStep()
    {
        var doc = EditorDocument.Create(128, 128); doc.UndoByteBudget = 1024;
        var id = doc.Snapshot.Manifest.ActiveLayerId!.Value;
        for (int i = 0; i < 4; i++) doc.ReplaceLayerImage(id, new(128, 128), new object());
        Assert.True(doc.Undo()); Assert.False(doc.CanUndo);
    }
}
