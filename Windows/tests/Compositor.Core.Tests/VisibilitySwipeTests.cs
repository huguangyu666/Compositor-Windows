using Xunit;

namespace Compositor.Core.Tests;

public class VisibilitySwipeTests
{
    private static EditorDocument Scene()
    {
        var document = EditorDocument.Create(8, 8);
        document.ImportImage(new Surface(4, 4), "Second");
        document.ImportImage(new Surface(4, 4), "Third");
        document.Snapshot.Manifest.Layers[1].IsVisible = false;
        return new EditorDocument(document.Snapshot);
    }

    [Fact]
    public void OneSwipeGivesVisitedLayersTheSameStateAndUndoesOnce()
    {
        var document = Scene();
        var ids = document.Snapshot.Manifest.Layers.Select(layer => layer.Id).ToArray();
        var images = document.Snapshot.Images.ToDictionary(pair => pair.Key, pair => pair.Value);
        var active = document.Snapshot.Manifest.ActiveLayerId;
        var key = new object();
        Assert.True(document.SetVisible(ids[2], false, key));
        Assert.False(document.SetVisible(ids[1], false, key));
        Assert.True(document.SetVisible(ids[0], false, key));
        Assert.False(document.SetVisible(ids[2], false, key));
        document.EndTransform(key);
        Assert.All(document.Snapshot.Manifest.Layers, layer => Assert.False(layer.IsVisible));
        Assert.Equal(active, document.Snapshot.Manifest.ActiveLayerId);
        Assert.All(images, pair => Assert.Same(pair.Value, document.Snapshot.Images[pair.Key]));
        Assert.True(document.Undo());
        Assert.False(document.CanUndo);
        Assert.False(document.IsDirty);
        Assert.Equal(new[] { true, false, true }, document.Snapshot.Manifest.Layers.Select(layer => layer.IsVisible));
        Assert.True(document.Redo());
        Assert.All(document.Snapshot.Manifest.Layers, layer => Assert.False(layer.IsVisible));
    }

    [Fact]
    public void ShowingAHiddenStartAndEndingThenStartingAgainAreSeparateSteps()
    {
        var document = Scene();
        var id = document.Snapshot.Manifest.Layers[1].Id;
        var show = new object();
        Assert.True(document.SetVisible(id, true, show));
        document.EndTransform(show);
        var hide = new object();
        Assert.True(document.SetVisible(id, false, hide));
        document.EndTransform(hide);
        Assert.True(document.Undo());
        Assert.True(document.Snapshot.Manifest.Layers[1].IsVisible);
        Assert.True(document.Undo());
        Assert.False(document.Snapshot.Manifest.Layers[1].IsVisible);
        Assert.False(document.CanUndo);
    }

    [Fact]
    public void RepeatedAndMissingTargetsDoNotAddHistoryOrClearRedo()
    {
        var document = Scene();
        var id = document.Snapshot.Manifest.Layers[0].Id;
        document.SetVisible(id, false);
        document.Undo();
        var key = new object();
        Assert.False(document.SetVisible(id, true, key));
        Assert.False(document.SetVisible(Guid.NewGuid(), false, key));
        document.EndTransform(key);
        Assert.False(document.CanUndo);
        Assert.True(document.CanRedo);
    }

    [Fact]
    public void AnOrdinaryVisibilityEditDoesNotJoinAnOpenSwipe()
    {
        var document = Scene();
        var layers = document.Snapshot.Manifest.Layers;
        var key = new object();
        document.SetVisible(layers[0].Id, false, key);
        document.SetVisible(layers[1].Id, true);
        document.SetVisible(layers[2].Id, false, key);
        document.EndTransform(key);
        Assert.True(document.Undo());
        Assert.True(document.Snapshot.Manifest.Layers[2].IsVisible);
        Assert.True(document.Undo());
        Assert.False(document.Snapshot.Manifest.Layers[1].IsVisible);
        Assert.True(document.Undo());
        Assert.True(document.Snapshot.Manifest.Layers[0].IsVisible);
        Assert.False(document.CanUndo);
    }
}
