using Xunit;

namespace Compositor.Core.Tests;

public class LayerOpacityTests
{
    [Fact]
    public void SelectedFolderAndChildTakeOpacityTogetherWithoutEditingOtherLayers()
    {
        var scene = EditorDocument.Create(16, 16); var background = scene.Snapshot.Manifest.Layers[0].Id;
        var folder = scene.GroupLayers([background])!;
        var outside = scene.ImportImage(new Surface(4, 4), "Outside"); outside.ParentId = null;
        scene = new(scene.Snapshot); var source = scene.Snapshot.Images;
        var selection = Selection.Rectangle(16, 16, 1, 1, 4, 4); scene.SetSelection(selection);
        Assert.True(scene.SetLayerOpacities([folder.Id, background, background, Guid.NewGuid()], .35));
        Assert.Equal(.35, scene.Snapshot.Manifest.Layers.Single(layer => layer.Id == folder.Id).Opacity);
        Assert.Equal(.35, scene.Snapshot.Manifest.Layers.Single(layer => layer.Id == background).Opacity);
        Assert.Equal(1, scene.Snapshot.Manifest.Layers.Single(layer => layer.Id == outside.Id).Opacity ?? 1);
        Assert.Same(source, scene.Snapshot.Images); Assert.Same(selection, scene.ActiveSelection);
        Assert.True(scene.Undo()); Assert.False(scene.CanUndo);
        Assert.All(scene.Snapshot.Manifest.Layers, layer => Assert.Equal(1, layer.Opacity ?? 1));
        Assert.True(scene.Redo()); Assert.Equal(.35, scene.Snapshot.Manifest.Layers.Single(layer => layer.Id == folder.Id).Opacity);
    }

    [Fact]
    public void UnchangedInvalidAndMissingTargetsPreserveHistoryAndRedo()
    {
        var scene = EditorDocument.Create(16, 16); var id = scene.Snapshot.Manifest.Layers[0].Id;
        scene.SetLayerOpacities([id], .5); scene.Undo(); Assert.True(scene.CanRedo);
        Assert.False(scene.SetLayerOpacities([id], 1)); Assert.False(scene.SetLayerOpacities([Guid.NewGuid()], .5));
        Assert.False(scene.SetLayerOpacities([], .5)); Assert.False(scene.SetLayerOpacities([id], double.NaN));
        Assert.False(scene.SetLayerOpacities([id], double.PositiveInfinity));
        Assert.True(scene.CanRedo); Assert.False(scene.CanUndo); Assert.False(scene.IsDirty);
    }
}
