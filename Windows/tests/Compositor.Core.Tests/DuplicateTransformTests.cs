using Xunit;

namespace Compositor.Core.Tests;

public class DuplicateTransformTests
{
    [Fact]
    public void DiscardedDuplicateDoesNotMutateDocumentDirtyHistoryOrRedo()
    {
        var doc = Scene(); var id = doc.Snapshot.Manifest.Layers[0].Id;
        doc.RenameLayer(id, "Renamed"); Assert.True(doc.Undo());
        var snapshot = doc.Snapshot; var before = ManifestJson.Serialize(snapshot.Manifest);
        var edit = Assert.IsType<TransformEdit>(doc.BeginDuplicateTransformDraft([id]));
        var preview = edit.Preview(edit.UpdateNumber(edit.Value, TransformField.X, 50)!)!;
        Assert.Equal(2, preview.Snapshot.Manifest.Layers.Count);
        Assert.Same(snapshot, doc.Snapshot); Assert.Equal(before, ManifestJson.Serialize(doc.Snapshot.Manifest));
        Assert.False(doc.CanUndo); Assert.True(doc.CanRedo); Assert.False(doc.IsDirty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DuplicateAndMoveCommitInOneUndoIncludingZeroDisplacement(bool move)
    {
        var doc = Scene(); var snapshot = doc.Snapshot; var before = ManifestJson.Serialize(snapshot.Manifest);
        var id = snapshot.Manifest.Layers[0].Id; var edit = Assert.IsType<TransformEdit>(doc.BeginDuplicateTransformDraft([id]));
        var value = move ? edit.UpdateNumber(edit.Value, TransformField.X, 45)! : edit.Value;
        var preview = edit.Preview(value)!; var copyId = Assert.Single(edit.LayerIds);
        Assert.NotEqual(id, copyId); Assert.Equal(snapshot.Manifest.Layers[0].Transform, preview.Snapshot.Manifest.Layers[0].Transform);
        Assert.True(doc.CommitTransform(preview));
        Assert.Equal(copyId, doc.Snapshot.Manifest.ActiveLayerId); Assert.Same(snapshot.Images[id], doc.Snapshot.Images[copyId]);
        Assert.Equal(value, doc.Snapshot.Manifest.Layers.Single(l => l.Id == copyId).Transform);
        var after = ManifestJson.Serialize(doc.Snapshot.Manifest);
        Assert.True(doc.Undo()); Assert.Equal(before, ManifestJson.Serialize(doc.Snapshot.Manifest)); Assert.False(doc.CanUndo); Assert.False(doc.IsDirty);
        Assert.True(doc.Redo()); Assert.Equal(after, ManifestJson.Serialize(doc.Snapshot.Manifest));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void StaleOriginalMetadataPixelsOrMasksRejectTheWholeCopy(int kind)
    {
        var doc = Scene(); var layer = doc.Snapshot.Manifest.Layers[0];
        layer.MaskFile = ProjectValidator.Uuid(layer.Id) + ".mask.png";
        ((Dictionary<Guid, MaskSurface>)doc.Snapshot.Masks)[layer.Id] = new(1, 1);
        var edit = Assert.IsType<TransformEdit>(doc.BeginDuplicateTransformDraft([layer.Id])); var preview = edit.Preview(edit.Value)!;
        if (kind == 0) layer.Name = "External";
        else if (kind == 1) doc.Snapshot.Images[layer.Id].MarkModified();
        else doc.Snapshot.Masks[layer.Id].MarkModified();
        Assert.False(doc.CommitTransform(preview)); Assert.Single(doc.Snapshot.Manifest.Layers); Assert.False(doc.CanUndo);
    }

    [Fact]
    public void FolderDuplicatesHiddenAssetsButTransformsOnlyVisiblePixelMembers()
    {
        var doc = Scene(); var pixel = doc.Snapshot.Manifest.Layers[0];
        var folder = Record("Folder", true); pixel.ParentId = folder.Id;
        var hidden = Record("Hidden"); hidden.ParentId = folder.Id; hidden.IsVisible = false;
        var clipped = Record("Clipped"); clipped.ParentId = folder.Id; clipped.MaskSourceId = pixel.Id;
        doc.Snapshot.Manifest.Layers.Insert(0, folder); doc.Snapshot.Manifest.Layers.AddRange([hidden, clipped]);
        ((Dictionary<Guid, Surface>)doc.Snapshot.Images)[hidden.Id] = new(8, 8);
        ((Dictionary<Guid, Surface>)doc.Snapshot.Images)[clipped.Id] = new(8, 8);
        doc.Snapshot.Manifest.ActiveLayerId = folder.Id;
        var edit = Assert.IsType<TransformEdit>(doc.BeginDuplicateTransformDraft([folder.Id, pixel.Id]));
        var next = edit.UpdateNumber(edit.Value, TransformField.X, edit.Value.Origin.X + 30)!;
        var preview = edit.Preview(next)!; var copyFolder = preview.Snapshot.Manifest.Layers.Single(l => l.Name == "Folder copy");
        var copies = preview.Snapshot.Manifest.Layers.Where(l => l.ParentId == copyFolder.Id).ToArray();
        Assert.Equal(2, edit.LayerIds.Count); Assert.Equal(3, copies.Length);
        Assert.Equal(hidden.Transform, copies.Single(l => l.Name == "Hidden").Transform);
        Assert.Equal(pixel.Transform.Origin.X + 30, copies.Single(l => l.Name == "Pixels").Transform.Origin.X);
        Assert.Equal(copies.Single(l => l.Name == "Pixels").Id, copies.Single(l => l.Name == "Clipped").MaskSourceId);
        Assert.True(doc.CommitTransform(preview)); ProjectValidator.Validate(doc.Snapshot.Manifest);
        Assert.True(doc.Undo()); Assert.Equal(4, doc.LayerCount); Assert.False(doc.CanUndo);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CopyPreservesMaskTargetAndLinkage(bool maskTarget)
    {
        var doc = Scene(); var layer = doc.Snapshot.Manifest.Layers[0];
        layer.MaskFile = ProjectValidator.Uuid(layer.Id) + ".mask.png"; layer.MaskLinked = false;
        layer.MaskPlacement = new() { Origin = new(4, 7), Size = new(16, 16) };
        ((Dictionary<Guid, MaskSurface>)doc.Snapshot.Masks)[layer.Id] = new(8, 8);
        var edit = Assert.IsType<TransformEdit>(doc.BeginDuplicateTransformDraft([layer.Id], maskTarget));
        var value = edit.UpdateNumber(edit.Value, TransformField.X, 60)!; var preview = edit.Preview(value)!;
        var copy = preview.Snapshot.Manifest.Layers.Single(l => edit.LayerIds.Contains(l.Id));
        Assert.Equal(maskTarget, edit.TargetsMask);
        Assert.Equal(maskTarget ? layer.Transform : value, copy.Transform);
        Assert.Equal(maskTarget ? value : layer.MaskPlacement, copy.MaskPlacement);
        Assert.True(doc.CommitTransform(preview)); Assert.True(doc.Undo()); Assert.False(doc.CanUndo);
    }

    [Fact]
    public void DirtyRegionIncludesDuplicatedFolderEffectsAndAdjustment()
    {
        var doc = Scene(); var layer = doc.Snapshot.Manifest.Layers[0];
        var folder = Record("Folder", true); layer.ParentId = folder.Id;
        layer.Effects = new LayerEffects { Stroke = new() { Size = 4, Red = 1, Opacity = 1 } }.Serialize();
        var adjustment = Record("Invert"); adjustment.ParentId = folder.Id; adjustment.Adjustment = new() { Kind = AdjustmentKind.Invert };
        doc.Snapshot.Manifest.Layers.Insert(0, folder); doc.Snapshot.Manifest.Layers.Add(adjustment);
        var edit = Assert.IsType<TransformEdit>(doc.BeginDuplicateTransformDraft([folder.Id]));
        var preview = edit.Preview(edit.UpdateNumber(edit.Value, TransformField.X, 60)!)!;
        var compositor = new CanvasCompositor(doc.Snapshot); compositor.Render(); compositor.SetSnapshot(preview.Snapshot);
        var dirty = preview.DirtyBounds;
        var partial = compositor.Render(dirty.X, dirty.Y, dirty.X + dirty.Width, dirty.Y + dirty.Height);
        Assert.Equal(new CanvasCompositor(preview.Snapshot).Render().Pixels, partial.Pixels);
    }

    private static ProjectLayerRecord Record(string name, bool group = false) => new()
    { Id = Guid.NewGuid(), Name = name, IsVisible = true, IsGroup = group, Transform = new() { Origin = new(10, 12), Size = new(16, 16) } };
    private static EditorDocument Scene()
    {
        var doc = EditorDocument.Create(96, 80); var layer = doc.Snapshot.Manifest.Layers[0]; layer.Name = "Pixels";
        layer.Transform = new() { Origin = new(10, 12), Size = new(16, 16) };
        ((Dictionary<Guid, Surface>)doc.Snapshot.Images)[layer.Id] = EditorDocument.Create(8, 8).Snapshot.Images.Values.Single();
        return doc;
    }
}
