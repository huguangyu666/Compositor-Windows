using Xunit;

namespace Compositor.Core.Tests;

public class GradientEditTests
{
    private static readonly GradientSettings BlackToWhite = new(GradientShape.Linear, GradientStyle.ForegroundToBackground,
        new GradientColor(0, 0, 0), new GradientColor(255, 255, 255));

    [Fact]
    public void LinearClampsPastBothEndsAndInterpolatesAtPixelCenters()
    {
        var doc = EditorDocument.Create(5, 1);
        var id = doc.Snapshot.Manifest.Layers[0].Id;
        var preview = doc.BeginGradient(id)!.Preview(new(1.5, .5), new(3.5, .5), BlackToWhite)!;
        Assert.Equal(new byte[] { 0, 0, 128, 255, 255 }, Reds(preview.Snapshot.Images[id]));
        Assert.All(doc.Snapshot.Images[id].Pixels, value => Assert.Equal(255, value));
        Assert.False(doc.IsDirty);
        Assert.False(doc.CanUndo);
    }

    [Theory]
    [InlineData(false, 0, 128, 255)]
    [InlineData(true, 255, 128, 0)]
    public void VerticalGradientAndReverse(bool reverse, byte first, byte middle, byte last)
    {
        var doc = EditorDocument.Create(1, 3);
        var id = doc.Snapshot.Manifest.Layers[0].Id;
        var preview = doc.BeginGradient(id)!.Preview(new(.5, .5), new(.5, 2.5), BlackToWhite with { Reversed = reverse })!;
        Assert.Equal(new[] { first, middle, last }, Reds(preview.Snapshot.Images[id]));
    }

    [Fact]
    public void RadialUsesDistanceFromStartAndClampsOutsideRadius()
    {
        var doc = EditorDocument.Create(3, 3);
        var id = doc.Snapshot.Manifest.Layers[0].Id;
        var preview = doc.BeginGradient(id)!.Preview(new(1.5, 1.5), new(2.5, 1.5), BlackToWhite with { Shape = GradientShape.Radial })!;
        Assert.Equal(0, Reds(preview.Snapshot.Images[id])[4]);
        Assert.Equal(255, Reds(preview.Snapshot.Images[id])[0]);
        Assert.Equal(255, Reds(preview.Snapshot.Images[id])[5]);
    }

    [Fact]
    public void TransparentEndpointAndOpacityUseSourceOverPremultipliedPixels()
    {
        var doc = EditorDocument.Create(3, 1);
        var id = doc.Snapshot.Manifest.Layers[0].Id;
        doc.Snapshot.Images[id].Pixels.AsSpan().Clear();
        var settings = BlackToWhite with { Foreground = new(255, 0, 0), Style = GradientStyle.ForegroundToTransparent, Opacity = .5 };
        var preview = doc.BeginGradient(id)!.Preview(new(.5, .5), new(2.5, .5), settings)!;
        Assert.Equal(new byte[] { 128, 0, 0, 128, 64, 0, 0, 64, 0, 0, 0, 0 }, preview.Snapshot.Images[id].Pixels);
        var next = doc.BeginGradient(id)!.Preview(new(.5, .5), new(2.5, .5), settings with { Reversed = true })!;
        Assert.Equal(new byte[] { 0, 64, 128 }, Reds(next.Snapshot.Images[id]));
    }

    [Fact]
    public void SelectionCoverageAndFeatherRestrictThePaint()
    {
        var doc = EditorDocument.Create(3, 1);
        var id = doc.Snapshot.Manifest.Layers[0].Id;
        doc.SetSelection(Selection.FromCoverage(new MaskSurface(3, 1, [0, 128, 255])));
        var black = BlackToWhite with { Background = new(0, 0, 0) };
        var preview = doc.BeginGradient(id)!.Preview(new(.5, .5), new(2.5, .5), black)!;
        Assert.Equal(new byte[] { 255, 127, 0 }, Reds(preview.Snapshot.Images[id]));
        var feather = Selection.Rectangle(7, 3, 2, 0, 3, 3).Feathered(2);
        doc = EditorDocument.Create(7, 3);
        id = doc.Snapshot.Manifest.Layers[0].Id;
        doc.SetSelection(feather);
        preview = doc.BeginGradient(id)!.Preview(new(.5, .5), new(6.5, .5), black)!;
        Assert.Equal((byte)(255 - feather.CoverageAt(1, 1)), Reds(preview.Snapshot.Images[id])[8]);
    }

    [Fact]
    public void PreviewAlwaysStartsFromOriginalAndCommitIsOneUndoStep()
    {
        var doc = EditorDocument.Create(3, 1);
        var original = doc.Snapshot;
        var id = original.Manifest.Layers[0].Id;
        var edit = doc.BeginGradient(id)!;
        var settings = BlackToWhite with { Opacity = .5 };
        var first = edit.Preview(new(.5, .5), new(2.5, .5), settings)!;
        var second = edit.Preview(new(.5, .5), new(2.5, .5), settings)!;
        Assert.Equal(first.Snapshot.Images[id].Pixels, second.Snapshot.Images[id].Pixels);
        Assert.True(doc.CommitGradient(second));
        Assert.Equal(second.Snapshot.Images[id].Pixels, doc.Snapshot.Images[id].Pixels);
        Assert.True(doc.Undo());
        Assert.Same(original.Images[id], doc.Snapshot.Images[id]);
        Assert.False(doc.CanUndo);
        Assert.True(doc.Redo());
        Assert.Equal(second.Snapshot.Images[id].Pixels, doc.Snapshot.Images[id].Pixels);
    }

    [Fact]
    public void ExpandedPixelsKeepTheirDocumentPlacementAndExistingMask()
    {
        var doc = EditorDocument.Create(5, 1);
        var layer = doc.Snapshot.Manifest.Layers[0];
        layer.Transform = new() { Origin = new(2, 0), Size = new(1, 1) };
        ((Dictionary<Guid, Surface>)doc.Snapshot.Images)[layer.Id] = new Surface(1, 1);
        layer.MaskFile = ProjectValidator.Uuid(layer.Id) + ".mask.png";
        ((Dictionary<Guid, MaskSurface>)doc.Snapshot.Masks)[layer.Id] = MaskSurface.Solid(true);
        var preview = doc.BeginGradient(layer.Id)!.Preview(new(.5, .5), new(4.5, .5), BlackToWhite)!;
        var resultLayer = preview.Snapshot.Manifest.Layers[0];
        Assert.Equal(new PointD(0, 0), resultLayer.Transform.Origin);
        Assert.Equal(new SizeD(5, 1), resultLayer.Transform.Size);
        Assert.Null(resultLayer.MaskPlacement);
        Assert.Equal(new byte[] { 255, 255, 255, 255, 255 }, preview.Snapshot.Masks[layer.Id].Pixels);
        Assert.Equal(5, preview.Snapshot.Images[layer.Id].Width);
        Assert.Null(layer.MaskPlacement);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FlippedAndRotatedPixelGridMapsBackToDocument(bool flip)
    {
        var doc = EditorDocument.Create(3, 3);
        var layer = doc.Snapshot.Manifest.Layers[0];
        layer.Transform.Rotation = flip ? 0 : 90;
        layer.Transform.FlipX = flip;
        var preview = doc.BeginGradient(layer.Id)!.Preview(new(.5, .5), new(2.5, .5), BlackToWhite)!;
        var reds = Reds(preview.Snapshot.Images[layer.Id]);
        Assert.Equal(flip ? new byte[] { 255, 128, 0, 255, 128, 0, 255, 128, 0 }
            : new byte[] { 255, 255, 255, 128, 128, 128, 0, 0, 0 }, reds);
    }

    [Fact]
    public void MaskUsesRedChannelAndIndependentPlacementWithoutChangingImage()
    {
        var doc = EditorDocument.Create(3, 1);
        var layer = doc.Snapshot.Manifest.Layers[0];
        layer.MaskFile = ProjectValidator.Uuid(layer.Id) + ".mask.png";
        layer.MaskEnabled = true;
        layer.MaskPlacement = new() { Origin = new(1, 0), Size = new(1, 1) };
        ((Dictionary<Guid, MaskSurface>)doc.Snapshot.Masks)[layer.Id] = MaskSurface.Solid(true);
        var settings = BlackToWhite with { Foreground = new(0, 255, 0), Background = new(255, 0, 255) };
        var preview = doc.BeginGradient(layer.Id, onMask: true)!.Preview(new(.5, .5), new(2.5, .5), settings)!;
        Assert.Equal(new byte[] { 0, 128, 255 }, preview.Snapshot.Masks[layer.Id].Pixels);
        Assert.Same(doc.Snapshot.Images[layer.Id], preview.Snapshot.Images[layer.Id]);
        Assert.Equal(new PointD(0, 0), preview.Snapshot.Manifest.Layers[0].MaskPlacement!.Origin);
        Assert.True(doc.CommitGradient(preview));
        Assert.True(doc.Undo());
        Assert.Equal(new PointD(1, 0), doc.Snapshot.Manifest.Layers[0].MaskPlacement!.Origin);
    }

    [Theory]
    [InlineData("hidden")]
    [InlineData("group")]
    [InlineData("adjustment")]
    [InlineData("empty")]
    [InlineData("disabled-mask")]
    [InlineData("missing-mask")]
    public void RefusedTargetsDoNotOpenHistory(string reason)
    {
        var doc = EditorDocument.Create(3, 1);
        var layer = doc.Snapshot.Manifest.Layers[0];
        if (reason == "hidden") layer.IsVisible = false;
        if (reason == "group") layer.IsGroup = true;
        if (reason == "adjustment") layer.Adjustment = new() { Kind = AdjustmentKind.Invert };
        if (reason == "empty") doc.SetSelection(new Selection(new MaskSurface(3, 1), new(0, 0, 0, 0)));
        if (reason == "disabled-mask") { layer.MaskEnabled = false; ((Dictionary<Guid, MaskSurface>)doc.Snapshot.Masks)[layer.Id] = MaskSurface.Solid(true); }
        Assert.Null(doc.BeginGradient(layer.Id, reason.EndsWith("mask")));
        Assert.False(doc.CanUndo);
        Assert.False(doc.IsDirty);
    }

    [Fact]
    public void ShortLineAndZeroOpacityAreNoOpsAndStaleDraftCannotOverwriteChanges()
    {
        var doc = EditorDocument.Create(3, 1);
        var id = doc.Snapshot.Manifest.Layers[0].Id;
        var edit = doc.BeginGradient(id)!;
        Assert.Null(edit.Preview(new(0, 0), new(.49, 0), BlackToWhite));
        Assert.Null(edit.Preview(new(0, 0), new(double.NaN, 0), BlackToWhite));
        Assert.Null(edit.Preview(new(0, 0), new(2, 0), BlackToWhite with { Opacity = 0 }));
        Assert.False(doc.CanUndo);
        var preview = edit.Preview(new(.5, .5), new(2.5, .5), BlackToWhite)!;
        doc.Snapshot.Manifest.Layers[0].Transform.Origin = new(1, 0);
        Assert.False(doc.CommitGradient(preview));
        Assert.False(doc.CanUndo);
    }

    [Fact]
    public void ScaledLayerRetainsItsPixelDensityAndDoesNotPaintBeyondCanvas()
    {
        var doc = EditorDocument.Create(4, 1);
        var layer = doc.Snapshot.Manifest.Layers[0];
        layer.Transform = new() { Origin = new(-2, 0), Size = new(8, 1) };
        var preview = doc.BeginGradient(layer.Id)!.Preview(new(0, .5), new(4, .5), BlackToWhite)!;
        Assert.Equal(new byte[] { 255, 64, 191, 255 }, Reds(preview.Snapshot.Images[layer.Id]));
        Assert.Equal(layer.Transform, preview.Snapshot.Manifest.Layers[0].Transform);
    }

    [Fact]
    public void CanvasClipAntialiasesAPixelPartlyOutsideTheDocument()
    {
        var doc = EditorDocument.Create(1, 1);
        var layer = doc.Snapshot.Manifest.Layers[0];
        layer.Transform = new() { Origin = new(-1, 0), Size = new(2, 1) };
        doc.Snapshot.Images[layer.Id].Pixels.AsSpan().Clear();
        var black = BlackToWhite with { Background = new(0, 0, 0) };
        var preview = doc.BeginGradient(layer.Id)!.Preview(new(0, .5), new(1, .5), black)!;
        Assert.Equal(new byte[] { 0, 0, 0, 128 }, preview.Snapshot.Images[layer.Id].Pixels);
    }

    [Fact]
    public void GrownLinkedMaskRendersAtItsOwnPlacementAndFollowsLaterMoves()
    {
        var doc = EditorDocument.Create(5, 1);
        var layer = doc.Snapshot.Manifest.Layers[0];
        layer.Transform = new() { Origin = new(0, 0), Size = new(1, 1), Sampling = LayerSampling.Nearest };
        ((Dictionary<Guid, Surface>)doc.Snapshot.Images)[layer.Id] = new Surface(1, 1, [255, 255, 255, 255], 4);
        layer.MaskFile = ProjectValidator.Uuid(layer.Id) + ".mask.png";
        ((Dictionary<Guid, MaskSurface>)doc.Snapshot.Masks)[layer.Id] = MaskSurface.Solid(true);
        var preview = doc.BeginGradient(layer.Id, true)!.Preview(new(.5, .5), new(4.5, .5), BlackToWhite)!;
        Assert.Equal(0, new CanvasCompositor(preview.Snapshot).Render().Pixels[3]);
        Assert.True(doc.CommitGradient(preview));
        Assert.True(doc.MoveLayerBy(layer.Id, 1, 0));
        Assert.Equal(new PointD(1, 0), doc.Snapshot.Manifest.Layers[0].MaskPlacement!.Origin);
        Assert.Equal(0, new CanvasCompositor(doc.Snapshot).Render().Pixels[7]);
    }

    [Fact]
    public void PixelGradientExpandsAttachedMaskWithoutStretchingItsCoverage()
    {
        var doc = EditorDocument.Create(5, 1);
        var layer = doc.Snapshot.Manifest.Layers[0];
        layer.Transform = new() { Origin = new(1, 0), Size = new(2, 1), Sampling = LayerSampling.Nearest };
        ((Dictionary<Guid, Surface>)doc.Snapshot.Images)[layer.Id] = new Surface(2, 1);
        layer.MaskFile = ProjectValidator.Uuid(layer.Id) + ".mask.png";
        ((Dictionary<Guid, MaskSurface>)doc.Snapshot.Masks)[layer.Id] = new MaskSurface(2, 1, [0, 255]);
        var preview = doc.BeginGradient(layer.Id)!.Preview(new(.5, .5), new(4.5, .5), BlackToWhite)!;
        var rendered = new CanvasCompositor(preview.Snapshot).Render();
        Assert.Equal(new byte[] { 255, 0, 255, 255, 255 }, Enumerable.Range(0, 5).Select(i => rendered.Pixels[i * 4 + 3]).ToArray());
        Assert.True(preview.Snapshot.Manifest.Layers[0].MaskLinked ?? true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MaskGradientWithFlippedGridStillRendersInDocumentDirection(bool flip)
    {
        var doc = EditorDocument.Create(3, 1);
        var layer = doc.Snapshot.Manifest.Layers[0];
        layer.Transform.FlipX = flip;
        layer.MaskFile = ProjectValidator.Uuid(layer.Id) + ".mask.png";
        ((Dictionary<Guid, MaskSurface>)doc.Snapshot.Masks)[layer.Id] = MaskSurface.Solid(true);
        var preview = doc.BeginGradient(layer.Id, true)!.Preview(new(.5, .5), new(2.5, .5), BlackToWhite)!;
        var rendered = new CanvasCompositor(preview.Snapshot).Render();
        Assert.Equal(new byte[] { 0, 128, 255 }, Reds(rendered));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlacedMaskLinkageControlsMoveAndTransformFollow(bool linked)
    {
        var doc = EditorDocument.Create(10, 10);
        var layer = doc.Snapshot.Manifest.Layers[0];
        layer.Transform = new() { Origin = new(0, 0), Size = new(2, 2) };
        layer.MaskFile = ProjectValidator.Uuid(layer.Id) + ".mask.png";
        layer.MaskLinked = linked;
        layer.MaskPlacement = new() { Origin = new(0, 0), Size = new(6, 2) };
        ((Dictionary<Guid, MaskSurface>)doc.Snapshot.Masks)[layer.Id] = new MaskSurface(6, 2);
        var original = layer.MaskPlacement;
        var key = new object();
        doc.BeginTransform(key, layer.Id);
        Assert.True(doc.UpdateTransform(key, layer.Id, new() { Origin = new(0, 0), Size = new(2, 2), Rotation = 90 }));
        doc.EndTransform(key);
        var result = doc.Snapshot.Manifest.Layers[0].MaskPlacement!;
        if (linked)
        {
            Assert.Equal(90, result.Rotation, 8);
            Assert.Equal(-2, result.Origin.X, 8);
            Assert.Equal(2, result.Origin.Y, 8);
        }
        else Assert.Equal(original, result);
        Assert.True(doc.Undo());
        Assert.Equal(original, doc.Snapshot.Manifest.Layers[0].MaskPlacement);
        Assert.True(doc.ScaleLayer(layer.Id, 200));
        var scaled = doc.Snapshot.Manifest.Layers[0].MaskPlacement!;
        Assert.Equal(linked ? new SizeD(60, 20) : original.Size, scaled.Size);
    }

    [Fact]
    public void DroppedAndNoOpDraftPreserveRedoAndSelectionChangesInvalidateDraft()
    {
        var doc = EditorDocument.Create(3, 1);
        var id = doc.Snapshot.Manifest.Layers[0].Id;
        doc.CommitGradient(doc.BeginGradient(id)!.Preview(new(.5, .5), new(2.5, .5), BlackToWhite)!);
        Assert.True(doc.Undo());
        var edit = doc.BeginGradient(id)!;
        _ = edit.Preview(new(.5, .5), new(2.5, .5), BlackToWhite);
        Assert.True(doc.CanRedo);
        Assert.False(doc.CanUndo);
        Assert.False(doc.IsDirty);
        var white = BlackToWhite with { Foreground = new(255, 255, 255) };
        Assert.Null(edit.Preview(new(.5, .5), new(2.5, .5), white));
        var preview = edit.Preview(new(.5, .5), new(2.5, .5), BlackToWhite)!;
        doc.SetSelection(Selection.Rectangle(3, 1, 1, 0, 2, 1));
        Assert.False(doc.CommitGradient(preview));
        Assert.True(doc.CanRedo);
        Assert.False(doc.CanUndo);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FolderAndAdjustmentMasksArePaintableAndHiddenAncestorsAreRefused(bool folder)
    {
        var doc = EditorDocument.Create(3, 1);
        var layer = doc.Snapshot.Manifest.Layers[0];
        if (folder) layer.IsGroup = true;
        else layer.Adjustment = new() { Kind = AdjustmentKind.Invert };
        layer.MaskFile = ProjectValidator.Uuid(layer.Id) + ".mask.png";
        ((Dictionary<Guid, MaskSurface>)doc.Snapshot.Masks)[layer.Id] = MaskSurface.Solid(true);
        Assert.NotNull(doc.BeginGradient(layer.Id, true));
        Assert.Null(doc.BeginGradient(layer.Id));
        var parent = new ProjectLayerRecord { Id = Guid.NewGuid(), IsGroup = true, IsVisible = false, Name = "Hidden" };
        doc.Snapshot.Manifest.Layers.Add(parent);
        layer.ParentId = parent.Id;
        Assert.Null(doc.BeginGradient(layer.Id, true));
        Assert.False(doc.CanUndo);
    }

    [Fact]
    public void BlankLayerGetsRasterAssetAndOversizedGridIsRefusedBeforeAllocation()
    {
        var doc = EditorDocument.Create(3, 1);
        var layer = doc.Snapshot.Manifest.Layers[0];
        ((Dictionary<Guid, Surface>)doc.Snapshot.Images).Clear();
        layer.ImageFile = null;
        var preview = doc.BeginGradient(layer.Id)!.Preview(new(.5, .5), new(2.5, .5), BlackToWhite)!;
        Assert.Equal(new byte[] { 0, 128, 255 }, Reds(preview.Snapshot.Images[layer.Id]));
        Assert.Equal(ProjectValidator.Uuid(layer.Id) + ".png", preview.Snapshot.Manifest.Layers[0].ImageFile);
        layer.Transform.Size = new(300_000, 1);
        Assert.Null(doc.BeginGradient(layer.Id));
        Assert.False(doc.CanUndo);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MaskExpansionPreservesEdgeBackgroundOutsideCanvas(bool revealing)
    {
        var doc = EditorDocument.Create(3, 1);
        var layer = doc.Snapshot.Manifest.Layers[0];
        layer.MaskFile = ProjectValidator.Uuid(layer.Id) + ".mask.png";
        layer.MaskPlacement = new() { Origin = new(-1, 0), Size = new(1, 1) };
        ((Dictionary<Guid, MaskSurface>)doc.Snapshot.Masks)[layer.Id] = MaskSurface.Solid(revealing);
        var preview = doc.BeginGradient(layer.Id, true)!.Preview(new(.5, .5), new(2.5, .5), BlackToWhite)!;
        Assert.Equal(new byte[] { revealing ? (byte)255 : (byte)0, 0, 128, 255 }, preview.Snapshot.Masks[layer.Id].Pixels);
    }

    private static byte[] Reds(Surface surface) => Enumerable.Range(0, surface.Width * surface.Height)
        .Select(i => surface.Pixels[i / surface.Width * surface.Stride + i % surface.Width * 4]).ToArray();
}
