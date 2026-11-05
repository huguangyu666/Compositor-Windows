using Xunit;

namespace Compositor.Core.Tests;

public class SelectionPlanePreparationTests
{
    private static EditorDocument Scene(bool mask)
    {
        var document = EditorDocument.Create(16, 16); var layer = document.Snapshot.Manifest.Layers[0];
        layer.Transform = new() { Origin = new(2, 3), Size = new(12, 10), Rotation = 17, FlipX = true };
        if (mask)
        {
            document.AddRasterMask(layer.Id, MaskSurface.Solid(revealing: true));
            layer.MaskPlacement = new() { Origin = new(1, 2), Size = new(12, 12), Rotation = -11, FlipY = true };
        }
        document = new(document.Snapshot); document.SetSelection(Selection.Rectangle(16, 16, 4, 4, 6, 7).Feathered(2));
        return document;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreparationMatchesTheEstablishedKernelAndDoesNotMutateLiveState(bool mask)
    {
        var document = Scene(mask); var id = document.Snapshot.Manifest.Layers[0].Id;
        var source = document.Snapshot; var selection = document.ActiveSelection;
        var reference = new EditorDocument(new ProjectSnapshot { Manifest = ManifestJson.Deserialize(ManifestJson.Serialize(source.Manifest)), Images = source.Images, Masks = source.Masks });
        reference.SetSelection(selection);
        Assert.True(mask ? reference.FillMask(id, 37) : reference.CutSelectionMasked(id, selection!));
        var edit = document.BeginSelectionPlaneEdit(id, mask, 37)!;
        var result = edit.Prepare()!;
        Assert.Same(source, document.Snapshot); Assert.False(document.CanUndo); Assert.False(document.IsDirty);
        Assert.True(document.CommitSelectionPlaneEdit(result));
        Assert.Equal(mask ? reference.Snapshot.Masks[id].Pixels : reference.Snapshot.Images[id].Pixels,
            mask ? document.Snapshot.Masks[id].Pixels : document.Snapshot.Images[id].Pixels);
        Assert.Same(selection, document.ActiveSelection);
        Assert.True(document.Undo()); Assert.False(document.CanUndo);
        Assert.Same(source.Images[id], document.Snapshot.Images[id]);
        if (mask) Assert.Same(source.Masks[id], document.Snapshot.Masks[id]);
        Assert.True(document.Redo()); Assert.False(document.CommitSelectionPlaneEdit(result));
    }

    [Fact]
    public void CancelledPreparationAndChangedSourceNeverCreateHistory()
    {
        var document = Scene(false); var id = document.Snapshot.Manifest.Layers[0].Id;
        var edit = document.BeginSelectionPlaneEdit(id, false, 0)!;
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => { edit.Prepare(cancellation.Token); });
        Assert.False(document.CanUndo);
        var result = edit.Prepare()!;
        document.Snapshot.Images[id].MarkModified();
        Assert.False(document.CommitSelectionPlaneEdit(result)); Assert.False(document.CanUndo);
    }

    [Fact]
    public void ChangedSelectionOrMetadataRejectsThePreparedPlane()
    {
        var document = Scene(true); var id = document.Snapshot.Manifest.Layers[0].Id;
        var result = document.BeginSelectionPlaneEdit(id, true, 0)!.Prepare()!;
        document.SetSelection(Selection.Rectangle(16, 16, 6, 6, 2, 2));
        Assert.False(document.CommitSelectionPlaneEdit(result)); Assert.False(document.CanUndo);
        result = document.BeginSelectionPlaneEdit(id, true, 0)!.Prepare()!;
        document.RenameLayer(id, "Changed");
        Assert.False(document.CommitSelectionPlaneEdit(result));
        Assert.True(document.Undo()); Assert.False(document.CanUndo);
    }
}
