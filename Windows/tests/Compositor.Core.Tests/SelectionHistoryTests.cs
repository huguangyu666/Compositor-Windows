using Xunit;

namespace Compositor.Core.Tests;

public class SelectionHistoryTests
{
    [Fact]
    public void SelectionHistoryUndoRedoUsesOnlySelectionNotificationsAndSameSnapshot()
    {
        var doc = EditorDocument.Create(16, 16); var original = doc.Snapshot;
        int contentChanges = 0, selectionChanges = 0;
        doc.Changed += () => contentChanges++; doc.SelectionChanged += () => selectionChanges++;
        var selection = Selection.Rectangle(16, 16, 2, 3, 4, 5);
        Assert.True(doc.SetSelectionUndoable(selection)); Assert.True(doc.IsDirty); Assert.True(doc.CanUndo);
        Assert.True(doc.Undo()); Assert.Null(doc.ActiveSelection); Assert.False(doc.IsDirty); Assert.False(doc.CanUndo);
        Assert.True(doc.Redo()); Assert.Same(selection, doc.ActiveSelection); Assert.True(doc.IsDirty);
        Assert.Same(original, doc.Snapshot); Assert.Equal(0, contentChanges); Assert.Equal(3, selectionChanges);
    }

    [Fact]
    public void EquivalentSelectionAndNoSourceOutlinePreserveRedo()
    {
        var doc = EditorDocument.Create(16, 16); var selection = Selection.Rectangle(16, 16, 2, 3, 4, 5);
        doc.SetSelectionUndoable(selection); doc.SetSelectionUndoable(null); Assert.True(doc.Undo()); Assert.True(doc.CanRedo);
        Assert.False(doc.SetSelectionUndoable(selection)); Assert.False(doc.SetSelectionUndoable(Selection.Rectangle(16, 16, 2, 3, 4, 5)));
        Assert.True(doc.CanRedo); Assert.True(doc.Redo()); Assert.Null(doc.ActiveSelection);
    }

    [Fact]
    public void SelectionAndContentStepsInterleaveAndRestoreTheirCapturedPrimary()
    {
        var doc = EditorDocument.Create(16, 16); var first = doc.Snapshot.Manifest.Layers[0].Id;
        var other = doc.ImportImage(new(2, 2), "Other").Id; doc.SetActiveLayer(first);
        var selection = Selection.Rectangle(16, 16, 2, 3, 4, 5); doc.SetSelectionUndoable(selection);
        doc.SetActiveLayer(other); doc.RenameLayer(other, "Renamed"); doc.SetSelection(null);
        Assert.True(doc.Undo()); Assert.Same(selection, doc.ActiveSelection); Assert.Equal(other, doc.Snapshot.Manifest.ActiveLayerId);
        Assert.True(doc.Undo()); Assert.Null(doc.ActiveSelection); Assert.Equal(first, doc.Snapshot.Manifest.ActiveLayerId);
        Assert.True(doc.Redo()); Assert.Same(selection, doc.ActiveSelection);
        Assert.True(doc.Redo()); Assert.Equal("Renamed", doc.Snapshot.Manifest.Layers.Single(l => l.Id == other).Name);
    }

    [Fact]
    public void SelectionCoverageHistoryRespectsThePixelByteBudget()
    {
        var doc = EditorDocument.Create(8, 8); doc.UndoByteBudget = 100;
        for (int x = 0; x < 4; x++) doc.SetSelectionUndoable(Selection.Rectangle(8, 8, x, 0, 1, 1));
        Assert.True(doc.Undo()); Assert.Equal(new RectI(2, 0, 1, 1), doc.ActiveSelection!.Bounds); Assert.False(doc.CanUndo);
    }
}
