using Xunit;

namespace Compositor.Core.Tests;

public class LayerDeletionTests
{
    private static EditorDocument Scene()
    {
        var document = EditorDocument.Create(8, 8);
        document.ImportImage(new Surface(8, 8), "Middle");
        document.ImportImage(new Surface(8, 8), "Top");
        return new EditorDocument(document.Snapshot);
    }

    [Fact]
    public void BatchDeletionRemovesAllSelectedAssetsAndUndoesOnce()
    {
        var document = Scene();
        var ids = document.Snapshot.Manifest.Layers.Select(layer => layer.Id).ToArray();
        var image = document.Snapshot.Images[ids[1]];
        Assert.True(document.DeleteLayers([ids[0], ids[1], ids[1], Guid.NewGuid()]));
        Assert.Equal(ids[2], Assert.Single(document.Snapshot.Manifest.Layers).Id);
        Assert.False(document.Snapshot.Images.ContainsKey(ids[1]));
        Assert.True(document.Undo()); Assert.False(document.CanUndo);
        Assert.Same(image, document.Snapshot.Images[ids[1]]);
        Assert.Equal(ids, document.Snapshot.Manifest.Layers.Select(layer => layer.Id));
        Assert.True(document.Redo()); Assert.Single(document.Snapshot.Manifest.Layers);
    }

    [Fact]
    public void FolderAndSelectedDescendantsAreOneDeletionWithANeighborTarget()
    {
        var document = Scene();
        var middle = document.Snapshot.Manifest.Layers[1].Id;
        var folder = document.GroupLayers([middle])!;
        document.SetActiveLayer(middle);
        document = new EditorDocument(document.Snapshot);
        var next = document.Snapshot.Manifest.Layers.Single(layer => layer.Name == "Top").Id;
        Assert.True(document.DeleteLayers([folder.Id, middle]));
        Assert.Equal(next, document.Snapshot.Manifest.ActiveLayerId);
        Assert.DoesNotContain(document.Snapshot.Manifest.Layers, layer => layer.Id == middle || layer.Id == folder.Id);
        ProjectValidator.Validate(document.Snapshot.Manifest);
        Assert.True(document.Undo()); Assert.False(document.CanUndo);
    }

    [Fact]
    public void DeletingAClippingSourceUnlinksSurvivingTargetsAndRestoresLinksOnUndo()
    {
        var document = Scene(); var layers = document.Snapshot.Manifest.Layers;
        layers[1].MaskSourceId = layers[0].Id; layers[2].MaskSourceId = layers[1].Id;
        var removed = layers[0].Id; var survivor = layers[1].Id;
        document = new EditorDocument(document.Snapshot);
        Assert.True(document.DeleteLayer(removed));
        Assert.Null(document.Snapshot.Manifest.Layers[0].MaskSourceId);
        Assert.Equal(survivor, document.Snapshot.Manifest.Layers[1].MaskSourceId);
        ProjectValidator.Validate(document.Snapshot.Manifest);
        Assert.True(document.Undo()); Assert.Equal(removed, document.Snapshot.Manifest.Layers[1].MaskSourceId);
        Assert.False(document.CanUndo);
    }

    [Fact]
    public void MissingIdsLeaveHistoryAndRedoUntouchedAndDeletingEverythingIsValid()
    {
        var document = Scene(); var id = document.Snapshot.Manifest.Layers[0].Id;
        document.SetVisible(id, false); document.Undo();
        Assert.False(document.DeleteLayer(Guid.NewGuid()));
        Assert.False(document.DeleteLayers([]));
        Assert.False(document.CanUndo); Assert.True(document.CanRedo);
        Assert.True(document.DeleteLayers(document.Snapshot.Manifest.Layers.Select(layer => layer.Id).ToArray()));
        Assert.Null(document.Snapshot.Manifest.ActiveLayerId); Assert.Empty(document.Snapshot.Images);
        ProjectValidator.Validate(document.Snapshot.Manifest);
        Assert.True(document.Undo()); Assert.Equal(3, document.LayerCount);
    }

    [Fact]
    public void APreparedDeletionRejectsMetadataOrPixelChanges()
    {
        var document = Scene(); var id = document.Snapshot.Manifest.Layers[0].Id;
        var pending = document.BeginLayerDeletion([id])!;
        var prepared = pending.Prepare(bake: false);
        document.RenameLayer(id, "Changed");
        Assert.False(document.CommitLayerDeletion(prepared));
        Assert.Equal(3, document.LayerCount);
        document = new EditorDocument(document.Snapshot);
        pending = document.BeginLayerDeletion([id])!;
        prepared = pending.Prepare(bake: false);
        document.Snapshot.Images[id].MarkModified();
        Assert.False(document.CommitLayerDeletion(prepared));
        Assert.False(document.CanUndo);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BakingKeepsClippedPixelsAndTargetMaskWithoutDrawingHiddenSources(bool effects)
    {
        var document = Scene(); var layers = document.Snapshot.Manifest.Layers;
        var source = layers[0]; var target = layers[1];
        layers.RemoveAt(2);
        source.IsVisible = false; source.Opacity = .5;
        source.Transform = new() { Origin = new(2, 2), Size = new(4, 4), Sampling = LayerSampling.Nearest };
        target.Transform = new() { Size = new(8, 8), FlipX = true, Sampling = LayerSampling.Nearest };
        var sourcePixels = new Surface(4, 4); sourcePixels.Pixels.AsSpan().Fill(255);
        var targetPixels = new Surface(8, 8); targetPixels.Pixels.AsSpan().Fill(255);
        ((Dictionary<Guid, Surface>)document.Snapshot.Images)[source.Id] = sourcePixels;
        ((Dictionary<Guid, Surface>)document.Snapshot.Images)[target.Id] = targetPixels;
        target.MaskSourceId = source.Id;
        document.SetActiveLayer(target.Id);
        document.AddRasterMask(target.Id, MaskSurface.Solid(revealing: true));
        if (effects)
        {
            document.SetLayerEffects(source.Id, new LayerEffects { Stroke = new StrokeEffect { Size = 1, Red = 1, Green = 0, Blue = 0 } });
        }
        document = new EditorDocument(document.Snapshot);
        var before = new CanvasCompositor(document.Snapshot).Render().Pixels.ToArray();
        var ownMask = document.Snapshot.Masks[target.Id];
        var plan = document.BeginLayerDeletion([source.Id])!;
        Assert.Equal(target.Id, Assert.Single(plan.DependentLayerIds));
        var prepared = plan.Prepare(bake: true);
        Assert.True(document.CommitLayerDeletion(prepared));
        ProjectValidator.Validate(document.Snapshot.Manifest);
        Assert.Equal(before, new CanvasCompositor(document.Snapshot).Render().Pixels);
        Assert.Same(ownMask, document.Snapshot.Masks[target.Id]);
        Assert.Null(document.Snapshot.Manifest.Layers[0].MaskSourceId);
        Assert.True(document.Undo()); Assert.False(document.CanUndo);
        Assert.Same(targetPixels, document.Snapshot.Images[target.Id]);
        Assert.True(document.Redo());
        Assert.Equal(before, new CanvasCompositor(document.Snapshot).Render().Pixels);
    }

    [Fact]
    public void CancelledBakingLeavesPixelsAndHistoryUntouched()
    {
        var document = Scene(); var layers = document.Snapshot.Manifest.Layers;
        layers[1].MaskSourceId = layers[0].Id;
        var plan = document.BeginLayerDeletion([layers[0].Id])!;
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => plan.Prepare(bake: true, cancellation.Token));
        Assert.Equal(3, document.LayerCount); Assert.False(document.CanUndo);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public void BakingUsesTargetGridRotationSourceMasksAndFolderOpacity(int rotation)
    {
        var document = Scene(); var sourceId = document.Snapshot.Manifest.Layers[0].Id;
        var targetId = document.Snapshot.Manifest.Layers[1].Id;
        document.AddFolder(); var folder = document.Snapshot.Manifest.Layers[^1]; folder.Opacity = .5;
        var source = document.Snapshot.Manifest.Layers[0]; source.IsVisible = false; source.ParentId = folder.Id; source.Opacity = .5;
        source.Transform = new() { Origin = new(2, 2), Size = new(4, 4), FlipY = true, Rotation = 90, Sampling = LayerSampling.Nearest };
        var target = document.Snapshot.Manifest.Layers[1]; target.MaskSourceId = sourceId;
        target.Transform = new() { Size = new(8, 8), Rotation = rotation, FlipX = true, Sampling = LayerSampling.Nearest };
        var pixels = new Surface(4, 4); pixels.Pixels.AsSpan().Fill(255);
        for (int i = 0; i < 16; i++) pixels.Pixels[i * 4 + 3] = (byte)(i * 17);
        ((Dictionary<Guid, Surface>)document.Snapshot.Images)[sourceId] = pixels;
        var white = new Surface(8, 8); white.Pixels.AsSpan().Fill(255);
        ((Dictionary<Guid, Surface>)document.Snapshot.Images)[targetId] = white;
        document.AddRasterMask(sourceId, new MaskSurface(2, 2, [0, 128, 255, 192]));
        document.SetActiveLayer(targetId);
        document = new EditorDocument(document.Snapshot);
        var expected = new CanvasCompositor(document.Snapshot).Render().Pixels.ToArray();
        Assert.True(document.CommitLayerDeletion(document.BeginLayerDeletion([folder.Id])!.Prepare(bake: true)));
        Assert.Equal(expected, new CanvasCompositor(document.Snapshot).Render().Pixels);
        ProjectValidator.Validate(document.Snapshot.Manifest);
    }

    [Fact]
    public void AdjustmentDependentsAreReportedAndUnlinkedWithoutInventingPixels()
    {
        var document = Scene(); var source = document.Snapshot.Manifest.Layers[0].Id;
        var adjustment = document.Snapshot.Manifest.Layers[1];
        adjustment.ImageFile = null; adjustment.Adjustment = new() { Kind = AdjustmentKind.Invert }; adjustment.MaskSourceId = source;
        ((Dictionary<Guid, Surface>)document.Snapshot.Images).Remove(adjustment.Id);
        var plan = document.BeginLayerDeletion([source])!;
        Assert.True(plan.HasAdjustmentDependents);
        Assert.True(document.CommitLayerDeletion(plan.Prepare(bake: true)));
        var remaining = document.Snapshot.Manifest.Layers.Single(layer => layer.Id == adjustment.Id);
        Assert.NotNull(remaining.Adjustment); Assert.Null(remaining.MaskSourceId); Assert.Null(remaining.ImageFile);
        ProjectValidator.Validate(document.Snapshot.Manifest);
    }
}
