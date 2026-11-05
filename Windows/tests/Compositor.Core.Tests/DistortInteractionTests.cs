using Xunit;

namespace Compositor.Core.Tests;

public class DistortInteractionTests
{
    private static readonly PointD[] Shape = [new(3, 4), new(13, 2), new(11, 13), new(5, 11)];
    private static EditorDocument Scene()
    {
        var doc = EditorDocument.Create(40, 30); var layer = doc.Snapshot.Manifest.Layers[0];
        layer.Transform = new() { Origin = new(4, 5), Size = new(8, 6) };
        var image = new Surface(8, 6); image.Pixels.AsSpan().Fill(255);
        for (int i = 0; i < image.Pixels.Length; i += 4) image.Pixels[i] = (byte)(i % 255);
        ((Dictionary<Guid, Surface>)doc.Snapshot.Images)[layer.Id] = image; return doc;
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void CornerAndEdgeGrabsKeepFractionalDeltasAndDoNotMutateTheirBasis(int index)
    {
        var drag = new TransformDragState(new(), new(10, 10), TransformDragMode.Distort, index, Shape);
        var moved = TransformDrag.UpdatedCorners(drag, new(12.25, 9.5))!;
        for (int i = 0; i < 4; i++)
            Assert.Equal(i == index / 2 || index % 2 == 1 && i == (index / 2 + 1) % 4
                ? new(Shape[i].X + 2.25, Shape[i].Y - .5) : Shape[i], moved[i]);
        Assert.Equal(new(3, 4), Shape[0]);
    }

    [Theory]
    [InlineData(5, 3, 5, 0)] [InlineData(3, 5, 0, 5)] [InlineData(4, -4, 4, 0)]
    public void ShiftBodyGrabLocksDominantAxis(double x, double y, double dx, double dy)
    {
        var drag = new TransformDragState(new(), PointD.Zero, TransformDragMode.Move, OriginalCorners: Shape);
        var moved = TransformDrag.UpdatedCorners(drag, new(x, y), shift: true)!;
        for (int i = 0; i < 4; i++) Assert.Equal(new(Shape[i].X + dx, Shape[i].Y + dy), moved[i]);
    }

    [Fact]
    public void DistortedHitTestUsesTheLiveEdgesAndHasNoRotationHandle()
    {
        var handles = TransformDrag.HandlePositions(Shape);
        for (int i = 0; i < 8; i++) Assert.Equal((TransformDragMode.Resize, i), TransformDrag.HitTest(Shape, handles[i], .01));
        Assert.Null(TransformDrag.HitTest(Shape, new(8, -24), 2));
        Assert.Equal((TransformDragMode.Resize, 1), TransformDrag.HitTest(Shape, new(5.5, 3.5), .01));
    }

    [Fact]
    public void DistortPreviewIsDetachedCachedAndCannotBeCommittedAtPreviewResolution()
    {
        var doc = Scene(); var source = doc.Snapshot; var id = source.Manifest.Layers[0].Id;
        var edit = doc.BeginTransformDraft([id])!; var first = edit.Preview(edit.Value, Shape)!;
        Assert.Same(first, edit.Preview(edit.Value, Shape));
        Assert.False(doc.CommitTransform(first)); Assert.Same(source, doc.Snapshot); Assert.False(doc.CanUndo);
        var secondShape = Shape.ToArray(); secondShape[0] = new(1, 2);
        var second = edit.Preview(edit.Value, secondShape)!;
        Assert.NotSame(first.Snapshot.Images[id], second.Snapshot.Images[id]);
        Assert.Equal(new(3, 2), first.Snapshot.Manifest.Layers[0].Transform.Origin);
    }

    [Fact]
    public void PreparedDistortCommitsOneUndoAndRestoresExactAssetReferences()
    {
        var doc = Scene(); var source = doc.Snapshot; var id = source.Manifest.Layers[0].Id;
        var edit = doc.BeginTransformDraft([id])!; var prepared = edit.PrepareDistortCommit(edit.Value, Shape)!;
        Assert.Same(source, doc.Snapshot); Assert.True(doc.CommitTransform(prepared));
        var pixels = doc.Snapshot.Images[id]; Assert.True(doc.Undo()); Assert.False(doc.CanUndo);
        Assert.Same(source.Images[id], doc.Snapshot.Images[id]); Assert.True(doc.Redo()); Assert.Same(pixels, doc.Snapshot.Images[id]);
    }

    [Fact]
    public void UnusableOrCancelledPreparationDoesNotOpenHistory()
    {
        var doc = Scene(); var edit = doc.BeginTransformDraft([doc.Snapshot.Manifest.Layers[0].Id])!;
        Assert.Null(edit.PrepareDistortCommit(edit.Value, [new(1, 1), new(2, 2), new(3, 3), new(4, 4)]));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => edit.PrepareDistortCommit(edit.Value, Shape, cancel.Token));
        Assert.False(doc.CanUndo);
    }

    [Fact]
    public void StaleAssetRevisionRejectsThePreparedWarp()
    {
        var doc = Scene(); var id = doc.Snapshot.Manifest.Layers[0].Id; var edit = doc.BeginTransformDraft([id])!;
        var preview = edit.PrepareDistortCommit(edit.Value, Shape)!; doc.Snapshot.Images[id].MarkModified();
        Assert.False(doc.CommitTransform(preview)); Assert.False(doc.CanUndo);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LinkedAttachedGrayMaskWarpsWithoutRgbaBufferOverrun(bool disabled)
    {
        var doc = Scene(); var layer = doc.Snapshot.Manifest.Layers[0]; var mask = new MaskSurface(8, 6);
        mask.Pixels.AsSpan().Fill(100); ((Dictionary<Guid, MaskSurface>)doc.Snapshot.Masks)[layer.Id] = mask;
        layer.MaskFile = ProjectValidator.Uuid(layer.Id) + ".mask.png"; layer.MaskEnabled = !disabled;
        Assert.True(doc.DistortLayer(layer.Id, Shape));
        Assert.Null(doc.Snapshot.Manifest.Layers[0].MaskPlacement);
        Assert.Equal(doc.Snapshot.Images[layer.Id].Width, doc.Snapshot.Masks[layer.Id].Width);
        Assert.Contains((byte)100, doc.Snapshot.Masks[layer.Id].Pixels);
        Assert.True(doc.Undo()); Assert.Same(mask, doc.Snapshot.Masks[layer.Id]);
    }

    [Fact]
    public void IndependentMaskOnlyWarpPreservesPixelsAndUsesMaskBackground()
    {
        var doc = Scene(); var layer = doc.Snapshot.Manifest.Layers[0]; var pixels = doc.Snapshot.Images[layer.Id];
        var mask = new MaskSurface(8, 6); mask.Pixels.AsSpan().Fill(255); mask.Pixels[10] = 0;
        ((Dictionary<Guid, MaskSurface>)doc.Snapshot.Masks)[layer.Id] = mask;
        layer.MaskFile = ProjectValidator.Uuid(layer.Id) + ".mask.png"; layer.MaskLinked = false;
        var edit = doc.BeginTransformDraft([layer.Id], onMask: true)!;
        var result = edit.PrepareDistortCommit(edit.Value, Shape)!; Assert.True(doc.CommitTransform(result));
        Assert.Same(pixels, doc.Snapshot.Images[layer.Id]); Assert.NotNull(doc.Snapshot.Manifest.Layers[0].MaskPlacement);
        Assert.Equal((byte)255, doc.Snapshot.Masks[layer.Id].Pixels[0]); Assert.True(doc.Undo()); Assert.Same(mask, doc.Snapshot.Masks[layer.Id]);
    }

    [Fact]
    public void GroupMembersFollowOnePerspectiveAndCommitOneHistoryStep()
    {
        var doc = Scene(); var first = doc.Snapshot.Manifest.Layers[0];
        doc.Snapshot.Manifest.Version = 11; doc.Snapshot.Manifest.Layers.Add(new() { Id = Guid.NewGuid(), Name = "Folder", IsGroup = true, IsVisible = true,
            Transform = new() { Size = new(40, 30) } });
        var group = doc.Snapshot.Manifest.Layers[^1]; first.ParentId = group.Id;
        var second = new ProjectLayerRecord { Id = Guid.NewGuid(), Name = "Second", IsVisible = true,
            ParentId = group.Id, Transform = new() { Origin = new(14, 5), Size = new(4, 6) } };
        second.ImageFile = ProjectValidator.Uuid(second.Id) + ".png"; doc.Snapshot.Manifest.Layers.Add(second);
        var image = new Surface(4, 6); image.Pixels.AsSpan().Fill(255); ((Dictionary<Guid, Surface>)doc.Snapshot.Images)[second.Id] = image;
        var edit = doc.BeginTransformDraft([group.Id])!; Assert.True(edit.IsGroup);
        var corners = DistortWarp.CornersOf(edit.Value); corners[0] = new(1, 3);
        var result = edit.PrepareDistortCommit(edit.Value, corners)!;
        Assert.NotSame(doc.Snapshot.Images[first.Id], result.Snapshot.Images[first.Id]);
        Assert.NotSame(image, result.Snapshot.Images[second.Id]);
        Assert.True(doc.CommitTransform(result)); Assert.True(doc.Undo()); Assert.False(doc.CanUndo); Assert.Same(image, doc.Snapshot.Images[second.Id]);
    }

    [Fact]
    public void LinkedPlacedMaskCarriesTheSamePerspectiveAndItsOutsideBackground()
    {
        var doc = Scene(); var layer = doc.Snapshot.Manifest.Layers[0]; var mask = new MaskSurface(4, 3);
        mask.Pixels.AsSpan().Fill(255); mask.Pixels[5] = 0;
        ((Dictionary<Guid, MaskSurface>)doc.Snapshot.Masks)[layer.Id] = mask;
        layer.MaskFile = ProjectValidator.Uuid(layer.Id) + ".mask.png"; layer.MaskLinked = true;
        layer.MaskPlacement = new() { Origin = new(5, 6), Size = new(4, 3) };
        var carried = DistortWarp.Carried(layer.MaskPlacement, layer.Transform, Shape);
        Assert.True(doc.DistortLayer(layer.Id, Shape)); var result = doc.Snapshot.Manifest.Layers[0].MaskPlacement!;
        Assert.Equal(new(Math.Floor(carried.Min(p => p.X)), Math.Floor(carried.Min(p => p.Y))), result.Origin);
        Assert.Equal((byte)255, doc.Snapshot.Masks[layer.Id].Pixels[0]);
        Assert.True(doc.Undo()); Assert.Same(mask, doc.Snapshot.Masks[layer.Id]);
    }

    [Fact]
    public void UniformAttachedMaskRetainsItsOriginalAsset()
    {
        var doc = Scene(); var layer = doc.Snapshot.Manifest.Layers[0]; var mask = new MaskSurface(1, 1, [177]);
        ((Dictionary<Guid, MaskSurface>)doc.Snapshot.Masks)[layer.Id] = mask;
        layer.MaskFile = ProjectValidator.Uuid(layer.Id) + ".mask.png";
        Assert.True(doc.DistortLayer(layer.Id, Shape)); Assert.Same(mask, doc.Snapshot.Masks[layer.Id]);
        Assert.Null(doc.Snapshot.Manifest.Layers[0].MaskPlacement);
    }

    [Fact]
    public void OversizedWarpRejectsBeforeOpeningHistoryOrReplacingAssets()
    {
        var doc = Scene(); var source = doc.Snapshot; var edit = doc.BeginTransformDraft([source.Manifest.Layers[0].Id])!;
        Assert.Throws<InvalidOperationException>(() => edit.PrepareDistortCommit(edit.Value,
            [new(0, 0), new(30000, 0), new(30000, 30000), new(0, 30000)]));
        Assert.Same(source, doc.Snapshot); Assert.False(doc.CanUndo); Assert.False(doc.IsDirty);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DistortedPixelsAndGrayMaskRoundTripThroughTheActualProjectStore(bool linked)
    {
        var doc = Scene(); var layer = doc.Snapshot.Manifest.Layers[0]; var mask = new MaskSurface(8, 6);
        new Random(7301).NextBytes(mask.Pixels); ((Dictionary<Guid, MaskSurface>)doc.Snapshot.Masks)[layer.Id] = mask;
        layer.MaskFile = ProjectValidator.Uuid(layer.Id) + ".mask.png"; layer.MaskLinked = linked;
        layer.MaskPlacement = new() { Origin = new(5, 6), Size = new(8, 6) };
        Assert.True(doc.DistortLayer(layer.Id, Shape));
        string directory = Path.Combine(Path.GetTempPath(), "compositor-distort-test-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "roundtrip.comp");
        try
        {
            await ProjectStore.SaveAsync(doc.Snapshot, path); var loaded = await ProjectStore.LoadAsync(path);
            Assert.Equal(ManifestJson.Serialize(doc.Snapshot.Manifest), ManifestJson.Serialize(loaded.Manifest));
            Assert.Equal(doc.Snapshot.Images[layer.Id].Pixels, loaded.Images[layer.Id].Pixels);
            Assert.Equal(doc.Snapshot.Masks[layer.Id].Pixels, loaded.Masks[layer.Id].Pixels);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void DistortingPixelsKeepsAnUnlinkedMaskAtItsDocumentPlacement()
    {
        var doc = EditorDocument.Create(40, 30); var layer = doc.Snapshot.Manifest.Layers[0];
        layer.Transform = new() { Origin = new(4, 5), Size = new(8, 6) };
        ((Dictionary<Guid, Surface>)doc.Snapshot.Images)[layer.Id] = new(8, 6);
        var mask = new MaskSurface(3, 2); mask.Pixels.AsSpan().Fill(127);
        ((Dictionary<Guid, MaskSurface>)doc.Snapshot.Masks)[layer.Id] = mask;
        layer.MaskFile = ProjectValidator.Uuid(layer.Id) + ".mask.png"; layer.MaskLinked = false;
        layer.MaskPlacement = new() { Origin = new(10, 11), Size = new(3, 2) };
        var placement = layer.MaskPlacement;
        Assert.True(doc.DistortLayer(layer.Id, [new(2, 4), new(14, 3), new(12, 13), new(5, 12)]));
        Assert.Same(mask, doc.Snapshot.Masks[layer.Id]);
        Assert.Equal(placement, doc.Snapshot.Manifest.Layers[0].MaskPlacement);
    }
}
