using System.Collections;
using Xunit;

namespace Compositor.Core.Tests;

public class HistoryTimelineTests
{
    private static object[] Timeline(EditorDocument doc)
    {
        var property = typeof(EditorDocument).GetProperty("HistoryStates"); Assert.NotNull(property);
        return ((IEnumerable)property.GetValue(doc)!).Cast<object>().ToArray();
    }
    private static T Value<T>(object row, string name) => (T)row.GetType().GetProperty(name)!.GetValue(row)!;
    private static bool Go(EditorDocument doc, Guid id)
    {
        var method = typeof(EditorDocument).GetMethod("GoToHistory"); Assert.NotNull(method);
        return (bool)method.Invoke(doc, [id])!;
    }

    [Fact]
    public void CoalescedEditsHaveStableStatesAndNamesAcrossUndoRedo()
    {
        var doc = EditorDocument.Create(12, 10); var id = doc.Snapshot.Manifest.ActiveLayerId!.Value;
        var initial = Value<Guid>(Assert.Single(Timeline(doc)), "Id");
        object key = new(); doc.MoveLayerBy(id, 1, 0, key); doc.MoveLayerBy(id, 2, 0, key); doc.EndTransform(key);
        var rows = Timeline(doc); Assert.Equal(2, rows.Length); var moved = Value<Guid>(rows[1], "Id");
        Assert.Equal("MoveLayerBy", Value<string>(rows[1], "Operation"));
        doc.Undo(); rows = Timeline(doc); Assert.True(Value<bool>(rows[0], "IsCurrent")); Assert.True(Value<bool>(rows[1], "IsFuture"));
        doc.Redo(); Assert.Equal(moved, Value<Guid>(Timeline(doc).Single(r => Value<bool>(r, "IsCurrent")), "Id"));
        Assert.True(Go(doc, initial)); Assert.Equal(0, doc.Snapshot.Manifest.Layers[0].Transform.Origin.X);
    }

    [Fact]
    public void JumpRestoresPixelsSelectionAndRedoWithoutNewSteps()
    {
        var doc = EditorDocument.Create(12, 10); var id = doc.Snapshot.Manifest.ActiveLayerId!.Value;
        var original = doc.Snapshot.Images[id]; var initial = Value<Guid>(Timeline(doc)[0], "Id");
        doc.FillPixels(id, 255, 0, 0); var filled = doc.Snapshot.Images[id];
        var selection = Selection.Rectangle(12, 10, 2, 2, 5, 5); doc.SetSelectionUndoable(selection);
        doc.MoveLayerBy(id, 3, 4); var latest = Value<Guid>(Timeline(doc)[^1], "Id");
        int count = Timeline(doc).Length; int changes = 0; doc.Changed += () => changes++;
        Assert.True(Go(doc, initial)); Assert.Same(original, doc.Snapshot.Images[id]); Assert.Null(doc.ActiveSelection);
        Assert.Equal(1, changes); Assert.False(doc.CanUndo); Assert.True(doc.CanRedo);
        Assert.True(Go(doc, latest)); Assert.Same(filled, doc.Snapshot.Images[id]); Assert.Same(selection, doc.ActiveSelection);
        Assert.Equal(new PointD(3, 4), doc.Snapshot.Manifest.Layers[0].Transform.Origin); Assert.Equal(count, Timeline(doc).Length);
    }

    [Fact]
    public void NewBranchAndBudgetInvalidateOldTargets()
    {
        var doc = EditorDocument.Create(12, 10); var id = doc.Snapshot.Manifest.ActiveLayerId!.Value;
        doc.MoveLayerBy(id, 1, 0); doc.RenameLayer(id, "Old branch"); var abandoned = Value<Guid>(Timeline(doc)[^1], "Id");
        doc.Undo(); doc.SetOpacity(id, .5); Assert.False(Go(doc, abandoned)); Assert.False(doc.CanRedo);
        var oldest = Value<Guid>(Timeline(doc)[0], "Id"); doc.UndoByteBudget = 1; doc.MoveLayerBy(id, 1, 0);
        Assert.Equal(2, Timeline(doc).Length); Assert.False(Go(doc, oldest)); Assert.True(Value<bool>(Timeline(doc)[0], "IsBaseline"));
    }

    [Fact]
    public void CanceledKeyedEditDoesNotLeaveAGhostState()
    {
        var doc = EditorDocument.Create(12, 10); var id = doc.Snapshot.Manifest.ActiveLayerId!.Value;
        var original = Value<Guid>(Timeline(doc)[0], "Id"); object key = new();
        doc.SetOpacity(id, .4, key); doc.SetOpacity(id, 1, key); doc.EndTransform(key);
        Assert.Equal(original, Value<Guid>(Assert.Single(Timeline(doc)), "Id")); Assert.False(doc.CanUndo);
    }

    [Fact]
    public void TextPreviewCannotNavigateAndCommitsOnlyOneState()
    {
        var doc = EditorDocument.Create(80, 60); var text = doc.AddTextLayer(new() { Content = "Before", FontSize = 12 }, new(2, 3))!;
        doc = new(doc.Snapshot); var original = Value<Guid>(Timeline(doc)[0], "Id");
        Assert.True(doc.BeginTextDraft(text.Id, text.Transform.Origin, text.Transform, text.Text!));
        Assert.True(doc.UpdateTextDraftContent("After")); Assert.False(Go(doc, original));
        Assert.True(doc.FinishTextDraft()); Assert.Equal(2, Timeline(doc).Length);
        Assert.True(Go(doc, original)); Assert.Equal("Before", doc.Snapshot.Manifest.Layers.Single(l => l.Id == text.Id).Text!.Content);
    }
}
