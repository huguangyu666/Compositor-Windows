using Xunit;

namespace Compositor.Core.Tests;

public class TransformEditTests
{
    private static (EditorDocument Doc, Guid Id) Single()
    {
        var doc = EditorDocument.Create(160, 120);
        var layer = doc.Snapshot.Manifest.Layers[0];
        layer.Transform = new() { Origin = new(20.25, 30.5), Size = new(40, 20), Rotation = 30 };
        ((Dictionary<Guid, Surface>)doc.Snapshot.Images)[layer.Id] = new(20, 10);
        return (doc, layer.Id);
    }

    [Theory]
    [InlineData(TransformField.X, -12.125)]
    [InlineData(TransformField.Y, 11.875)]
    [InlineData(TransformField.Width, 32.5)]
    [InlineData(TransformField.Height, 42.25)]
    public void TypedFractionsStayExactAndDimensionsKeepOrigin(TransformField field, double number)
    {
        var (doc, id) = Single();
        var edit = doc.BeginTransformDraft([id])!;
        var next = edit.UpdateNumber(edit.Value, field, number)!;
        Assert.Equal(number, Read(next, field));
        if (field is TransformField.Width or TransformField.Height) Assert.Equal(edit.Value.Origin, next.Origin);
        Assert.Equal(edit.Value.Rotation, next.Rotation);
        Assert.Equal(new SizeD(40, 20), edit.Value.Size);
    }

    [Theory]
    [InlineData(TransformField.Width, 60, 60, 30)]
    [InlineData(TransformField.Height, 50, 100, 50)]
    public void DimensionRatioLockUsesTheCurrentDraft(TransformField field, double number, double w, double h)
    {
        var (doc, id) = Single(); var edit = doc.BeginTransformDraft([id])!;
        var next = edit.UpdateNumber(edit.Value, field, number, lockRatio: true)!;
        Assert.Equal(new SizeD(w, h), next.Size);
        Assert.Equal(edit.Value.Origin, next.Origin);
    }

    [Fact]
    public void ScaleUsesSourcePixelsAndPreservesCenterWithoutCompounding()
    {
        var (doc, id) = Single(); var edit = doc.BeginTransformDraft([id])!;
        Assert.Equal(200, edit.ScalePercent(edit.Value));
        var first = edit.UpdateNumber(edit.Value, TransformField.Scale, 50)!;
        var next = edit.UpdateNumber(first, TransformField.Scale, 150)!;
        Assert.Equal(new SizeD(30, 15), next.Size);
        Assert.Equal(edit.Value.Center, first.Center);
        Assert.Equal(edit.Value.Center, next.Center);
    }

    [Theory]
    [InlineData(810, 90)]
    [InlineData(-450, -90)]
    public void TypedRotationUsesSignedRemainder(double number, double expected)
    {
        var (doc, id) = Single(); var edit = doc.BeginTransformDraft([id])!;
        var value = edit.UpdateNumber(edit.Value, TransformField.Rotation, number)!;
        Assert.Equal(expected, value.Rotation);
    }

    [Theory]
    [InlineData(TransformField.X, double.NaN)]
    [InlineData(TransformField.Y, double.PositiveInfinity)]
    [InlineData(TransformField.Width, .5)]
    [InlineData(TransformField.Height, 300001)]
    [InlineData(TransformField.Scale, 0)]
    [InlineData(TransformField.X, 1000001)]
    public void InvalidValuesDoNotBecomePreviewOrHistory(TransformField field, double number)
    {
        var (doc, id) = Single(); var edit = doc.BeginTransformDraft([id])!;
        Assert.Null(edit.UpdateNumber(edit.Value, field, number));
        Assert.False(doc.CanUndo); Assert.False(doc.IsDirty);
    }

    [Fact]
    public void PreviewDiscardDoesNotChangeDocumentAndEachFrameRetainsItsOwnMetadata()
    {
        var (doc, id) = Single(); var original = ManifestJson.Serialize(doc.Snapshot.Manifest);
        var edit = doc.BeginTransformDraft([id])!;
        var first = edit.Preview(edit.UpdateNumber(edit.Value, TransformField.X, 4.5)!)!;
        var next = edit.Preview(edit.UpdateNumber(first.Value, TransformField.Y, 9.75)!)!;
        Assert.Equal(30.5, first.Snapshot.Manifest.Layers[0].Transform.Origin.Y);
        Assert.Equal(9.75, next.Snapshot.Manifest.Layers[0].Transform.Origin.Y);
        Assert.Equal(original, ManifestJson.Serialize(doc.Snapshot.Manifest));
        Assert.Same(doc.Snapshot.Images[id], first.Snapshot.Images[id]);
        Assert.False(doc.CanUndo); Assert.False(doc.IsDirty);
    }

    [Fact]
    public void CommitIsOneUndoAndDetachesTheRenderedDraft()
    {
        var (doc, id) = Single(); var original = doc.Snapshot.Manifest.Layers[0].Transform;
        var edit = doc.BeginTransformDraft([id])!;
        var value = edit.UpdateNumber(edit.Value, TransformField.Width, 85.5)!;
        value.FlipX = value.FlipY = true; value.Sampling = LayerSampling.Nearest;
        var preview = edit.Preview(value)!;
        Assert.True(doc.CommitTransform(preview));
        Assert.Equal(value, doc.Snapshot.Manifest.Layers[0].Transform);
        doc.Snapshot.Manifest.Layers[0].Transform.Origin = new(1, 2);
        Assert.Equal(value.Origin, preview.Snapshot.Manifest.Layers[0].Transform.Origin);
        Assert.True(doc.Undo()); Assert.Equal(original, doc.Snapshot.Manifest.Layers[0].Transform);
        Assert.False(doc.CanUndo); Assert.True(doc.Redo());
    }

    [Fact]
    public void UnchangedCommitPreservesRedo()
    {
        var (doc, id) = Single(); doc.RenameLayer(id, "Other"); Assert.True(doc.Undo());
        var edit = doc.BeginTransformDraft([id])!;
        Assert.False(doc.CommitTransform(edit.Preview(edit.Value)!));
        Assert.False(doc.CanUndo); Assert.True(doc.CanRedo); Assert.False(doc.IsDirty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StaleMetadataOrPixelsRejectCommit(bool pixels)
    {
        var (doc, id) = Single(); var edit = doc.BeginTransformDraft([id])!;
        var preview = edit.Preview(edit.UpdateNumber(edit.Value, TransformField.X, 5)!)!;
        if (pixels) doc.Snapshot.Images[id].MarkModified();
        else doc.Snapshot.Manifest.Layers[0].Name = "Changed externally";
        Assert.False(doc.CommitTransform(preview)); Assert.False(doc.CanUndo);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void PixelTransformCarriesLinkedMaskAndPinsUnlinkedMask(bool linked, bool placed)
    {
        var (doc, id) = Single(); var layer = doc.Snapshot.Manifest.Layers[0];
        AddMask(doc, id, linked);
        if (placed) layer.MaskPlacement = new() { Origin = new(4, 7), Size = new(12, 8), Rotation = -10 };
        var oldMask = layer.MaskPlacement ?? layer.Transform;
        var edit = doc.BeginTransformDraft([id])!;
        var next = edit.UpdateNumber(edit.Value, TransformField.Scale, 300)!;
        var result = edit.Preview(next)!.Snapshot.Manifest.Layers[0];
        if (linked && !placed) Assert.Null(result.MaskPlacement);
        else Assert.Equal(linked ? oldMask.Following(layer.Transform, next) : oldMask, result.MaskPlacement);
    }

    [Fact]
    public void UniformMaskHasNoIndependentPlacementAfterPixelTransform()
    {
        var (doc, id) = Single(); AddMask(doc, id, false);
        ((Dictionary<Guid, MaskSurface>)doc.Snapshot.Masks)[id] = MaskSurface.Solid(true);
        var edit = doc.BeginTransformDraft([id])!;
        Assert.Null(edit.Preview(edit.UpdateNumber(edit.Value, TransformField.X, 3)!)!.Snapshot.Manifest.Layers[0].MaskPlacement);
    }

    [Fact]
    public void UnlinkedMaskTargetEditsOnlyMaskAndClearsPlacementWhenItMatchesLayer()
    {
        var (doc, id) = Single(); AddMask(doc, id, false);
        var edit = doc.BeginTransformDraft([id], onMask: true)!;
        Assert.True(edit.TargetsMask);
        Assert.Equal(edit.Value.Size, edit.PixelSize);
        var next = edit.UpdateNumber(edit.Value, TransformField.X, 4.5)!;
        var preview = edit.Preview(next)!;
        Assert.Equal(doc.Snapshot.Manifest.Layers[0].Transform, preview.Snapshot.Manifest.Layers[0].Transform);
        Assert.Equal(next, preview.Snapshot.Manifest.Layers[0].MaskPlacement);
        Assert.True(doc.CommitTransform(preview)); Assert.True(doc.Undo());
        var reset = doc.BeginTransformDraft([id], true)!;
        Assert.Null(reset.Preview(reset.Value)!.Snapshot.Manifest.Layers[0].MaskPlacement);
    }

    [Fact]
    public void SelectingLinkedMaskStillTransformsPixels()
    {
        var (doc, id) = Single(); AddMask(doc, id, true);
        Assert.False(doc.BeginTransformDraft([id], true)!.TargetsMask);
    }

    [Fact]
    public void IndependentlyPlacedMaskScaleUsesLayerSizeAsItsHundredPercentBasis()
    {
        var (doc, id) = Single(); AddMask(doc, id, false);
        var layer = doc.Snapshot.Manifest.Layers[0];
        layer.MaskPlacement = new() { Origin = new(4, 7), Size = new(12, 8) };
        var edit = doc.BeginTransformDraft([id], true)!;
        Assert.Equal(layer.Transform.Size, edit.PixelSize);
        Assert.Equal(30, edit.ScalePercent(edit.Value));
        var next = edit.UpdateNumber(edit.Value, TransformField.Scale, 100)!;
        Assert.Equal(layer.Transform.Size, next.Size); Assert.Equal(edit.Value.Center, next.Center);
    }

    [Fact]
    public void MaskSamplingWithoutIndependentPlacementDoesNotCreateEmptyUndo()
    {
        var (doc, id) = Single(); AddMask(doc, id, false);
        var edit = doc.BeginTransformDraft([id], true)!; var value = edit.Value; value.Sampling = LayerSampling.Nearest;
        Assert.False(doc.CommitTransform(edit.Preview(value)!)); Assert.False(doc.CanUndo);
    }

    [Fact]
    public void FolderAndMultiselectionIncludeOnlyVisiblePixelDescendantsWithoutDuplicates()
    {
        var (doc, id) = Single(); var layers = doc.Snapshot.Manifest.Layers;
        var folder = new ProjectLayerRecord { Id = Guid.NewGuid(), Name = "Folder", IsVisible = true, IsGroup = true, Transform = new() };
        layers.Insert(0, folder); layers[1].ParentId = folder.Id;
        var other = AddPixel(doc, new(80, 15), new(12, 30), folder.Id);
        var hidden = AddPixel(doc, new(0, 0), new(100, 90), folder.Id); hidden.IsVisible = false;
        var adjustment = new ProjectLayerRecord { Id = Guid.NewGuid(), Name = "Exposure", IsVisible = true, ParentId = folder.Id,
            Transform = new(), Adjustment = new() { Kind = AdjustmentKind.Exposure } }; layers.Add(adjustment);
        var edit = doc.BeginTransformDraft([folder.Id, id])!;
        Assert.True(edit.IsGroup); Assert.Equal(new[] { id, other.Id }, edit.LayerIds);
        Assert.Equal(0, edit.Value.Rotation); Assert.Equal(edit.Value.Size, edit.PixelSize);
        var value = edit.UpdateNumber(edit.Value, TransformField.X, edit.Value.Origin.X + 9.25)!;
        var preview = edit.Preview(value)!;
        var moved = preview.Snapshot.Manifest.Layers.First(l => l.Id == id).Transform.Origin;
        Assert.Equal(29.5, moved.X, 8); Assert.Equal(30.5, moved.Y, 8);
        Assert.Equal(hidden.Transform, preview.Snapshot.Manifest.Layers.First(l => l.Id == hidden.Id).Transform);
        Assert.True(doc.CommitTransform(preview)); Assert.True(doc.Undo()); Assert.False(doc.CanUndo);
    }

    [Fact]
    public void GroupBoxUsesRotatedCornersAndFollowingPreservesMemberFlipAndSampling()
    {
        var (doc, id) = Single(); var first = doc.Snapshot.Manifest.Layers[0];
        first.Transform.FlipX = true; first.Transform.Sampling = LayerSampling.Nearest;
        var second = AddPixel(doc, new(80, 15), new(12, 30));
        var edit = doc.BeginTransformDraft([id, second.Id])!;
        var corners = TransformDrag.HandlePositions(first.Transform).Concat(TransformDrag.HandlePositions(second.Transform)).ToArray();
        Assert.Equal(corners.Min(p => p.X), edit.Value.Origin.X, 8);
        Assert.Equal(corners.Max(p => p.Y) - corners.Min(p => p.Y), edit.Value.Size.Height, 8);
        var value = edit.UpdateNumber(edit.Value, TransformField.Rotation, 75)!; value.FlipY = true;
        var preview = edit.Preview(value)!;
        Assert.Equal(first.Transform.Following(edit.Value, value), preview.Snapshot.Manifest.Layers[0].Transform);
        Assert.True(preview.Snapshot.Manifest.Layers[0].Transform.FlipX);
        Assert.Equal(LayerSampling.Nearest, preview.Snapshot.Manifest.Layers[0].Transform.Sampling);
    }

    [Fact]
    public void HiddenOrEmptyTargetsRefuseWithoutHistory()
    {
        var (doc, id) = Single(); doc.Snapshot.Manifest.Layers[0].IsVisible = false;
        Assert.Null(doc.BeginTransformDraft([id])); Assert.Null(doc.BeginTransformDraft([]));
        Assert.False(doc.CanUndo);
    }

    [Fact]
    public void UndoSupersedesDraftEvenWhenItsContentMatchesAgain()
    {
        var (doc, id) = Single(); var edit = doc.BeginTransformDraft([id])!;
        var preview = edit.Preview(edit.UpdateNumber(edit.Value, TransformField.X, 8)!)!;
        doc.RenameLayer(id, "Changed"); doc.Undo();
        Assert.False(doc.CommitTransform(preview)); Assert.True(doc.CanRedo); Assert.False(doc.CanUndo);
    }

    [Theory]
    [InlineData(ShapeKind.Rectangle, 8)]
    [InlineData(ShapeKind.Ellipse, 0)]
    [InlineData(ShapeKind.Line, 0)]
    public void ShapeRedrawAtCommitKeepsStyleAndPinsImplicitMask(ShapeKind kind, double radius)
    {
        var (doc, id) = Single(); var layer = doc.Snapshot.Manifest.Layers[0];
        var style = new ShapeStyle { Kind = kind, Red = 1, CornerRadius = radius, LineWidth = 3 };
        layer.Shape = style.ToElement(); AddMask(doc, id, true);
        var original = doc.Snapshot.Images[id]; var edit = doc.BeginTransformDraft([id])!;
        var value = edit.UpdateNumber(edit.Value, TransformField.Width, 60)!;
        var preview = edit.Preview(value)!;
        Assert.True(doc.CommitTransform(preview));
        Assert.Equal(60, doc.Snapshot.Images[id].Width); Assert.Equal(20, doc.Snapshot.Images[id].Height);
        Assert.Equal(ShapeRasterizer.Draw(style, 60, 20).Pixels, doc.Snapshot.Images[id].Pixels);
        Assert.Equal(value, doc.Snapshot.Manifest.Layers[0].MaskPlacement);
        Assert.True(doc.Undo()); Assert.Same(original, doc.Snapshot.Images[id]);
    }

    [Fact]
    public void RoundedShapePreviewReusesPixelsAtTheSameDraftSizeAndCapsLongEdge()
    {
        var (doc, id) = Single(); doc.Snapshot.Manifest.Layers[0].Shape = new ShapeStyle { Kind = ShapeKind.Rectangle, Red = 1, CornerRadius = 8 }.ToElement();
        var edit = doc.BeginTransformDraft([id])!;
        var value = edit.UpdateNumber(edit.Value, TransformField.Width, 5000)!;
        var first = edit.Preview(value)!; var second = edit.Preview(value)!;
        Assert.Equal(2048, first.Snapshot.Images[id].Width);
        Assert.Same(first.Snapshot.Images[id], second.Snapshot.Images[id]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirtyBandMatchesCompleteCompositeIncludingEffectAndClipConsumer(bool mask)
    {
        var (doc, id) = Single(); var layer = doc.Snapshot.Manifest.Layers[0];
        doc.Snapshot.Images[id].Pixels.AsSpan().Fill(255);
        layer.Effects = new LayerEffects { Stroke = new() { Size = 4, Red = 1, Opacity = 1 } }.Serialize();
        if (mask) AddMask(doc, id, false);
        var consumer = AddPixel(doc, new(15, 20), new(65, 50)); consumer.MaskSourceId = id;
        doc.Snapshot.Images[consumer.Id].Pixels.AsSpan().Fill(180);
        var edit = doc.BeginTransformDraft([id], mask)!;
        var preview = edit.Preview(edit.UpdateNumber(edit.Value, TransformField.X, 95)!)!;
        var active = new CanvasCompositor(doc.Snapshot); active.Render(); active.SetSnapshot(preview.Snapshot);
        var band = preview.DirtyBounds; var partial = active.Render(band.X, band.Y, band.X + band.Width, band.Y + band.Height);
        var full = new CanvasCompositor(preview.Snapshot).Render();
        Assert.Equal(full.Pixels, partial.Pixels);
        Assert.True(band.Width < doc.Snapshot.Manifest.Width);
    }

    private static void AddMask(EditorDocument doc, Guid id, bool linked)
    {
        var layer = doc.Snapshot.Manifest.Layers.First(l => l.Id == id);
        layer.MaskFile = ProjectValidator.Uuid(id) + ".mask.png"; layer.MaskLinked = linked;
        var mask = new MaskSurface(20, 10); mask.Pixels.AsSpan().Fill(255); mask.Pixels[50] = 0;
        ((Dictionary<Guid, MaskSurface>)doc.Snapshot.Masks)[id] = mask;
    }

    private static ProjectLayerRecord AddPixel(EditorDocument doc, PointD origin, SizeD size, Guid? parent = null)
    {
        var id = Guid.NewGuid();
        var layer = new ProjectLayerRecord { Id = id, Name = "Other", IsVisible = true, ParentId = parent,
            ImageFile = ProjectValidator.Uuid(id) + ".png", Transform = new() { Origin = origin, Size = size } };
        doc.Snapshot.Manifest.Layers.Add(layer); ((Dictionary<Guid, Surface>)doc.Snapshot.Images)[id] = new(12, 30);
        return layer;
    }

    private static double Read(LayerTransform value, TransformField field) => field switch
    { TransformField.X => value.Origin.X, TransformField.Y => value.Origin.Y, TransformField.Width => value.Size.Width, _ => value.Size.Height };
}
