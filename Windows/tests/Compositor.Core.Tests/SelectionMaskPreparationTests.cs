using Xunit;

namespace Compositor.Core.Tests;

public class SelectionMaskPreparationTests
{
    private static EditorDocument Scene(bool folder = false)
    {
        var scene = EditorDocument.Create(16, 16); var id = scene.Snapshot.Manifest.Layers[0].Id;
        if (folder) scene.GroupLayers([id]);
        scene = new(scene.Snapshot);
        var layer = scene.Snapshot.Manifest.Layers.Single(layer => layer.Id == scene.Snapshot.Manifest.ActiveLayerId);
        layer.Transform = new() { Origin = new(1, 3), Size = new(12, 10), Rotation = 17, FlipX = true, FlipY = true };
        scene.SetSelection(Selection.Rectangle(16, 16, 4, 4, 6, 7).Feathered(2));
        return scene;
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void PreparedSelectionMaskMatchesKernelAndCommitsWithSelectionInOneUndo(bool folder, bool revealing)
    {
        var scene = Scene(folder); var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        var source = scene.Snapshot; var selection = scene.ActiveSelection;
        var reference = new EditorDocument(new ProjectSnapshot { Manifest = ManifestJson.Deserialize(ManifestJson.Serialize(source.Manifest)), Images = source.Images, Masks = source.Masks });
        reference.SetSelection(selection); Assert.True(reference.AddSelectionMask(id, revealing));
        var edit = scene.BeginSelectionMaskEdit(id, revealing)!; var result = edit.Prepare()!;
        Assert.Same(source, scene.Snapshot); Assert.Same(selection, scene.ActiveSelection); Assert.False(scene.CanUndo);
        Assert.True(scene.CommitSelectionPlaneEdit(result)); Assert.Null(scene.ActiveSelection);
        Assert.Equal(reference.Snapshot.Masks[id].Pixels, scene.Snapshot.Masks[id].Pixels);
        Assert.All(source.Images, image => Assert.Same(image.Value, scene.Snapshot.Images[image.Key]));
        Assert.True(scene.Undo()); Assert.False(scene.CanUndo); Assert.Same(selection, scene.ActiveSelection); Assert.False(scene.Snapshot.Masks.ContainsKey(id));
        Assert.True(scene.Redo()); Assert.Null(scene.ActiveSelection); Assert.False(scene.CommitSelectionPlaneEdit(result));
    }

    [Fact]
    public void CancelledOrStalePreparationCannotConsumeSelectionOrCreateMask()
    {
        var scene = Scene(); var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        var edit = scene.BeginSelectionMaskEdit(id, true)!;
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => { edit.Prepare(cancellation.Token); });
        Assert.False(scene.CanUndo); Assert.NotNull(scene.ActiveSelection);
        var result = edit.Prepare()!; scene.Snapshot.Images[id].MarkModified();
        Assert.False(scene.CommitSelectionPlaneEdit(result)); Assert.Empty(scene.Snapshot.Masks); Assert.NotNull(scene.ActiveSelection);
        result = scene.BeginSelectionMaskEdit(id, true)!.Prepare()!;
        scene.SetSelection(Selection.Rectangle(16, 16, 1, 1, 2, 2)); Assert.False(scene.CommitSelectionPlaneEdit(result));
        result = scene.BeginSelectionMaskEdit(id, true)!.Prepare()!;
        scene.AddRasterMask(id, MaskSurface.Solid(revealing: true)); Assert.False(scene.CommitSelectionPlaneEdit(result));
    }

    [Fact]
    public void EmptySelectionStillCreatesAHiddenMask()
    {
        var scene = Scene(); var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        scene.SetSelection(new(new(16, 16), new(0, 0, 0, 0)));
        var result = scene.BeginSelectionMaskEdit(id, true)!.Prepare()!;
        Assert.True(scene.CommitSelectionPlaneEdit(result)); Assert.Null(scene.ActiveSelection);
        Assert.All(scene.Snapshot.Masks[id].Pixels, pixel => Assert.Equal(0, pixel));
    }
}
