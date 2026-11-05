using Xunit;

namespace Compositor.Core.Tests;

public class SelectionCommandTests
{
    [Fact]
    public void EllipseAntialiasSoftensTheBoundaryAndKeepsTheInterior()
    {
        var hard = Selection.Ellipse(5, 5, 2.5, 2.5, 1.25, 1.25);
        var smooth = Selection.Ellipse(5, 5, 2.5, 2.5, 1.25, 1.25, true);
        Assert.Equal(255, hard.CoverageAt(1, 2)); Assert.InRange(smooth.CoverageAt(1, 2), 1, 254);
        Assert.Equal(255, smooth.CoverageAt(2, 2)); Assert.Equal(smooth.CoverageAt(1, 2), smooth.CoverageAt(3, 2));
        Assert.Equal(new RectI(1, 1, 3, 3), smooth.Bounds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptySelectionRefusesPixelAndMaskStrokesWithoutCopyOrHistory(bool onMask)
    {
        var doc = EditorDocument.Create(8, 8); var id = doc.Snapshot.Manifest.ActiveLayerId!.Value;
        doc.AddRasterMask(id, new(8, 8)); doc = new(doc.Snapshot);
        var empty = new Selection(new(8, 8), new(0, 0, 0, 0)); doc.SetSelection(empty);
        doc.SetSelectionUndoable(Selection.Rectangle(8, 8, 0, 0, 8, 8)); doc.Undo();
        var image = doc.Snapshot.Images[id]; var mask = doc.Snapshot.Masks[id];
        Assert.False(doc.BeginStroke(new object(), id, new(2, 1, 1, 255, 0, 0), new(4, 4), onMask));
        Assert.Same(image, doc.Snapshot.Images[id]); Assert.Same(mask, doc.Snapshot.Masks[id]);
        Assert.False(doc.CanUndo); Assert.True(doc.CanRedo); Assert.False(doc.IsDirty);
    }

    [Fact]
    public void ContractingPastTheMiddleKeepsAnEmptySelectionThatBlocksPainting()
    {
        var selection = Selection.Rectangle(8, 8, 2, 2, 4, 4).Feathered(2).Contracted(5);
        Assert.NotNull(selection); Assert.Equal(new RectI(0, 0, 0, 0), selection!.Bounds);
        Assert.Equal(2, selection.Feather);
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) Assert.Equal(0, selection.CoverageAt(x, y));
    }

    [Fact]
    public void EmptyCoverageHasNoPhantomBoundsEvenWithFeather()
    {
        var empty = Selection.FromCoverage(new(8, 8));
        Assert.Equal(new RectI(0, 0, 0, 0), empty.Bounds);
        Assert.Equal(new RectI(0, 0, 0, 0), empty.Feathered(3).Bounds);
    }

    [Fact]
    public void InvertingNoSelectionIsANoOpAndInvertingAllDeselects()
    {
        Assert.Null(SelectionCommands.Invert(null, 8, 8));
        Assert.Null(SelectionCommands.Invert(Selection.Rectangle(8, 8, 0, 0, 8, 8), 8, 8));
        var doc = EditorDocument.Create(8, 8);
        Assert.False(doc.SetSelectionUndoable(SelectionCommands.Invert(null, 8, 8)));
        Assert.False(doc.CanUndo); Assert.False(doc.IsDirty);
    }

    [Fact]
    public void InversionKeepsFeatherAndAnExplicitEmptySelectionInvertsToAll()
    {
        var current = Selection.Rectangle(8, 8, 2, 2, 4, 4).Feathered(2);
        var inverted = SelectionCommands.Invert(current, 8, 8)!;
        Assert.Equal(2, inverted.Feather); Assert.Equal(0, inverted.Coverage.Pixels[3 * 8 + 3]); Assert.Equal(255, inverted.Coverage.Pixels[0]);
        var all = SelectionCommands.Invert(new(new(8, 8), new(0, 0, 0, 0)), 8, 8)!;
        Assert.All(all.Coverage.Pixels, value => Assert.Equal(255, value));
    }

    [Theory]
    [InlineData(SelectionModification.Expand, 1)]
    [InlineData(SelectionModification.Contract, 5)]
    [InlineData(SelectionModification.Feather, 2)]
    public void ModificationAndHistoryPreserveThePreviousCoverageAndFeather(SelectionModification operation, int amount)
    {
        var doc = EditorDocument.Create(8, 8); var original = Selection.Rectangle(8, 8, 2, 2, 4, 4).Feathered(3);
        var bytes = (byte[])original.Coverage.Pixels.Clone(); doc.SetSelection(original);
        var modified = SelectionCommands.Modify(original, operation, amount)!;
        Assert.NotNull(modified); Assert.True(doc.SetSelectionUndoable(modified));
        Assert.Equal(bytes, original.Coverage.Pixels); Assert.True(doc.Undo()); Assert.Same(original, doc.ActiveSelection);
        Assert.True(doc.Redo()); Assert.Same(modified, doc.ActiveSelection);
        if (operation == SelectionModification.Contract) Assert.Equal(new RectI(0, 0, 0, 0), modified.Bounds);
        Assert.Equal(operation == SelectionModification.Feather ? Math.Sqrt(13) : 3, modified.Feather, 6);
    }

    [Theory]
    [InlineData(SelectionModification.Expand)]
    [InlineData(SelectionModification.Contract)]
    [InlineData(SelectionModification.Feather)]
    public void EmptyOrInvalidModificationLeavesSelectionAndRedoUntouched(SelectionModification operation)
    {
        var doc = EditorDocument.Create(8, 8); var empty = new Selection(new(8, 8), new(0, 0, 0, 0));
        doc.SetSelectionUndoable(empty); doc.SetSelectionUndoable(null); doc.Undo();
        Assert.Same(empty, SelectionCommands.Modify(empty, operation, 2));
        Assert.False(doc.SetSelectionUndoable(SelectionCommands.Modify(empty, operation, 2))); Assert.True(doc.CanRedo);
        var full = Selection.Rectangle(8, 8, 0, 0, 8, 8);
        Assert.Same(full, SelectionCommands.Modify(full, operation, 0)); Assert.Same(full, SelectionCommands.Modify(full, operation, 501));
    }

    [Fact]
    public void SamplingInputFreezesMetadataAndAssetDictionariesWithoutCopyingPixels()
    {
        var doc = EditorDocument.Create(4, 3); var id = doc.Snapshot.Manifest.ActiveLayerId!.Value;
        var input = SelectionSampleInput.Capture(doc.Snapshot);
        doc.Snapshot.Manifest.Layers[0].Transform.Origin = new(10, 10);
        ((Dictionary<Guid, Surface>)doc.Snapshot.Images)[id] = new(1, 1, [0, 255, 0, 255], 4);
        var sample = input.Render(true, id); Assert.Equal(4, sample.Width); Assert.Equal(3, sample.Height);
        Assert.All(sample.Pixels, value => Assert.Equal(255, value));
        Assert.Equal(sample.Pixels, input.Render(false, id).Pixels);
        Assert.All(input.Render(false, null).Pixels, value => Assert.Equal(0, value));
    }

    [Fact]
    public void WorkerWandUsesTheCapturedSampleAndKeepsContiguousMatching()
    {
        var sample = new Surface(3, 1, [255, 0, 0, 255, 0, 0, 255, 255, 255, 0, 0, 255], 12);
        var contiguous = SelectionSampling.Wand(sample, new(0, 0), new WandSettings { Tolerance = 0 });
        Assert.Equal(new RectI(0, 0, 1, 1), contiguous!.Bounds);
        var all = SelectionSampling.Wand(sample, new(0, 0), new WandSettings { Tolerance = 0, Contiguous = false });
        Assert.Equal(new byte[] { 255, 0, 255 }, all!.Coverage.Pixels);
        Assert.Null(SelectionSampling.Wand(sample, new(-1, 0), new()));
    }

    [Fact]
    public void ColorRangePreviewComesFromTheSameFullCoverageAsTheSelection()
    {
        var sample = new Surface(4, 2);
        for (int i = 0; i < 8; i++) { sample.Pixels[i * 4 + (i % 4 < 2 ? 0 : 2)] = 255; sample.Pixels[i * 4 + 3] = 255; }
        var result = SelectionSampling.ColorRange(sample, [255, 0, 0], [], 0, false, 4, 2);
        Assert.Equal(new RectI(0, 0, 2, 2), result.Selection!.Bounds);
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(result.Selection.Coverage.Pixels[i], result.Preview!.Pixels[i * 4]);
            Assert.Equal(result.Preview.Pixels[i * 4], result.Preview.Pixels[i * 4 + 1]); Assert.Equal(255, result.Preview.Pixels[i * 4 + 3]);
        }
        Assert.Null(SelectionSampling.ColorRange(sample, [], [], 0, false).Preview);
        Assert.Null(SelectionSampling.ColorRange(sample, [0, 255, 0], [], 0, false).Selection);
    }
}
