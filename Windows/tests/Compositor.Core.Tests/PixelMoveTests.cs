using Xunit;

namespace Compositor.Core.Tests;

public class PixelMoveTests
{
    private static (EditorDocument Doc, Guid Id, Surface Pixels) Scene(int canvasWidth = 12, int canvasHeight = 8, int width = 12, int height = 8)
    {
        var id = Guid.NewGuid(); var pixels = new Surface(width, height);
        var doc = new EditorDocument(new ProjectSnapshot
        {
            Manifest = new() { DocumentId = Guid.NewGuid(), Width = canvasWidth, Height = canvasHeight, ActiveLayerId = id,
                Layers = [new() { Id = id, Name = "Pixels", IsVisible = true, Transform = new() { Size = new(width, height) },
                    ImageFile = ProjectValidator.Uuid(id) + ".png" }] },
            Images = new Dictionary<Guid, Surface> { [id] = pixels }, Masks = new Dictionary<Guid, MaskSurface>()
        });
        doc.SetSelection(Selection.Rectangle(canvasWidth, canvasHeight, 2, 2, 1, 1));
        return (doc, id, pixels);
    }

    private static void Set(Surface pixels, int x, int y, params byte[] rgba) => rgba.CopyTo(pixels.Pixels, y * pixels.Stride + x * 4);
    private static byte[] At(Surface pixels, int x, int y) => pixels.Pixels.AsSpan(y * pixels.Stride + x * 4, 4).ToArray();
    private static Surface Render(PixelMoveResult result) => new CanvasCompositor(result.Snapshot).Render();

    [Fact]
    public void PreviewLeavesDocumentUntouchedAndCommitMovesPixelsAndSelectionInOneUndo()
    {
        var (doc, id, pixels) = Scene(); Set(pixels, 2, 2, 255, 0, 0, 255); Set(pixels, 1, 1, 0, 0, 255, 255);
        var snapshot = doc.Snapshot; var selection = doc.ActiveSelection; var bytes = pixels.Pixels.ToArray();
        var edit = Assert.IsType<PixelMoveEdit>(doc.BeginPixelMove(id)); edit.ApplyOffset(new(5, 0));
        var window = new Surface(12, 8); edit.DrawPixels(window, new(0, 0, 12, 8));
        Assert.Equal(new byte[4], At(window, 2, 2)); Assert.Equal(new byte[] { 255, 0, 0, 255 }, At(window, 7, 2));
        Assert.Same(selection, doc.ActiveSelection); Assert.Same(snapshot, doc.Snapshot); Assert.Equal(bytes, pixels.Pixels);
        Assert.False(doc.CanUndo); Assert.False(doc.IsDirty);
        var result = Assert.IsType<PixelMoveResult>(edit.PrepareCommit(new(5, 0))); Assert.True(doc.CommitPixelMove(result));
        Assert.Equal(new RectI(7, 2, 1, 1), doc.ActiveSelection!.OutlineBounds); Assert.True(doc.Undo());
        Assert.Same(pixels, doc.Snapshot.Images[id]); Assert.Same(selection, doc.ActiveSelection); Assert.False(doc.CanUndo);
        Assert.True(doc.Redo()); Assert.Equal(new byte[] { 255, 0, 0, 255 }, At(new CanvasCompositor(doc.Snapshot).Render(), 7, 2));
    }

    [Fact]
    public void DuplicateUsesSourceOverInsideTheSameLayer()
    {
        var (doc, id, pixels) = Scene(); Set(pixels, 2, 2, 64, 0, 0, 128); Set(pixels, 7, 2, 0, 0, 128, 128);
        var result = doc.BeginPixelMove(id, duplicate: true)!.PrepareCommit(new(5, 0))!; var image = Render(result);
        Assert.Equal(new byte[] { 64, 0, 0, 128 }, At(image, 2, 2));
        Assert.Equal(new byte[] { 64, 0, 64, 192 }, At(image, 7, 2));
        Assert.True(doc.CommitPixelMove(result)); Assert.Single(doc.Snapshot.Manifest.Layers); Assert.Equal(id, doc.Snapshot.Manifest.ActiveLayerId);
    }

    [Fact]
    public void PartialCoverageMultipliesAllPremultipliedChannels()
    {
        var (doc, id, pixels) = Scene(); Set(pixels, 2, 2, 100, 50, 25, 200);
        var mask = new MaskSurface(12, 8); mask.Pixels[2 * 12 + 2] = 128; doc.SetSelection(Selection.FromCoverage(mask));
        var image = Render(doc.BeginPixelMove(id)!.PrepareCommit(new(5, 0))!);
        Assert.Equal(new byte[] { 50, 25, 12, 100 }, At(image, 2, 2));
        Assert.Equal(new byte[] { 50, 25, 13, 100 }, At(image, 7, 2));
    }

    [Fact]
    public void HolesAndFeatherSurviveTheMove()
    {
        var (doc, id, pixels) = Scene(20, 12, 20, 12);
        for (int x = 0; x < 20; x++) Set(pixels, x, 4, 255, 0, 0, 255);
        var mask = new MaskSurface(20, 12); mask.Pixels[4 * 20 + 3] = 255; mask.Pixels[4 * 20 + 5] = 255;
        var selection = Selection.FromCoverage(mask); doc.SetSelection(selection);
        var result = doc.BeginPixelMove(id)!.PrepareCommit(new(8, 0))!; var image = Render(result);
        Assert.Equal(new byte[4], At(image, 3, 4)); Assert.Equal(new byte[] { 255, 0, 0, 255 }, At(image, 4, 4));
        Assert.False(result.Selection.Contains(new(12.5, 4.5))); Assert.True(result.Selection.Contains(new(11.5, 4.5)));
        doc.SetSelection(selection.Feathered(2)); result = doc.BeginPixelMove(id)!.PrepareCommit(new(0, 3))!;
        Assert.Equal(2, result.Selection.Feather); Assert.Same(doc.ActiveSelection!.OutlineCoverage, result.Selection.OutlineCoverage);
        Assert.InRange(At(Render(result), 3, 7)[3], (byte)1, (byte)254);
    }

    [Fact]
    public void OffsetsAreRoundedFromTheOriginAndPreviousTargetsAreRestored()
    {
        var (doc, id, pixels) = Scene(); Set(pixels, 2, 2, 255, 0, 0, 255); var edit = doc.BeginPixelMove(id)!;
        edit.ApplyOffset(new(3.5, -.5)); Assert.Equal(new PointD(4, -1), edit.Offset);
        edit.ApplyOffset(new(8, 1)); edit.ApplyOffset(new(5, 0));
        var image = Render(edit.PrepareCommit(new(5, 0))!);
        Assert.Equal(new byte[4], At(image, 6, 1)); Assert.Equal(new byte[4], At(image, 10, 3));
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, At(image, 7, 2));
    }

    [Fact]
    public void ReturnToOriginDoesNotChangePixelsHistoryOrRedo()
    {
        var (doc, id, pixels) = Scene(); Set(pixels, 2, 2, 255, 0, 0, 255); doc.RenameLayer(id, "Renamed"); Assert.True(doc.Undo());
        var edit = doc.BeginPixelMove(id)!; edit.ApplyOffset(new(5, 0));
        Assert.Null(edit.PrepareCommit(new(.49, -.49))); Assert.True(doc.CanRedo); Assert.False(doc.CanUndo); Assert.False(doc.IsDirty);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, At(doc.Snapshot.Images[id], 2, 2));
    }

    [Theory]
    [InlineData(90, false, false)]
    [InlineData(0, true, false)]
    [InlineData(90, true, true)]
    public void MovesOnTheSourcePixelGridWithRotationScaleAndFlips(double rotation, bool flipX, bool flipY)
    {
        var (doc, id, pixels) = Scene(32, 24, 4, 4); var layer = doc.Snapshot.Manifest.Layers[0];
        layer.Transform = new() { Origin = new(8, 8), Size = new(8, 8), Rotation = rotation, FlipX = flipX, FlipY = flipY, Sampling = LayerSampling.Nearest };
        Set(pixels, 1, 1, 255, 0, 0, 255); doc.SetSelection(Selection.Rectangle(32, 24, 6, 6, 12, 12));
        var before = new CanvasCompositor(doc.Snapshot).Render(); var result = doc.BeginPixelMove(id)!.PrepareCommit(new(4, 2))!;
        var after = Render(result);
        for (int y = 0; y < 22; y++) for (int x = 0; x < 28; x++) Assert.Equal(At(before, x, y), At(after, x + 4, y + 2));
        var placement = result.Snapshot.Manifest.Layers[0].Transform;
        Assert.Equal(rotation, placement.Rotation); Assert.Equal(flipX, placement.FlipX); Assert.Equal(flipY, placement.FlipY);
        Assert.Equal(LayerSampling.Nearest, placement.Sampling);
    }

    [Fact]
    public void FractionalSourceOffsetResamplesEvenWhenLayerSamplingIsNearest()
    {
        var (doc, id, pixels) = Scene(20, 12, 4, 4); Set(pixels, 1, 1, 255, 0, 0, 255);
        doc.Snapshot.Manifest.Layers[0].Transform = new() { Origin = new(4, 4), Size = new(8, 8), Sampling = LayerSampling.Nearest };
        doc.SetSelection(Selection.Rectangle(20, 12, 4, 4, 8, 8));
        var result = doc.BeginPixelMove(id)!.PrepareCommit(new(1, 0))!;
        Assert.Equal(LayerSampling.Nearest, result.Snapshot.Manifest.Layers[0].Transform.Sampling);
        Assert.Contains(result.Snapshot.Images[id].Pixels.Where((_, i) => i % 4 == 3), alpha => alpha is > 0 and < 255);
    }

    [Fact]
    public void OffCanvasMovementPreservesLogicalSelectionButClipsPixelsToTheFixedWorkingGrid()
    {
        var (doc, id, pixels) = Scene(); Set(pixels, 2, 2, 255, 0, 0, 255);
        var result = doc.BeginPixelMove(id)!.PrepareCommit(new(30, 0))!;
        Assert.Equal(new RectI(32, 2, 1, 1), result.Selection.OutlineBounds); Assert.False(result.Selection.IsEmpty);
        Assert.All(result.Snapshot.Images[id].Pixels, value => Assert.Equal(0, value));
        Assert.True(doc.CommitPixelMove(result)); Assert.True(doc.Undo()); Assert.Equal(new RectI(2, 2, 1, 1), doc.ActiveSelection!.OutlineBounds);
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)255)]
    public void AttachedMaskStaysAlignedAndGrowsWhite(byte value)
    {
        var (doc, id, pixels) = Scene(20, 12, 4, 4); Set(pixels, 2, 2, 255, 0, 0, 255); Set(pixels, 0, 0, 0, 0, 255, 255);
        var mask = new MaskSurface(1, 1); mask.Pixels[0] = value; ((Dictionary<Guid, MaskSurface>)doc.Snapshot.Masks)[id] = mask;
        doc.Snapshot.Manifest.Layers[0].MaskFile = ProjectValidator.Uuid(id) + ".mask.png";
        var result = doc.BeginPixelMove(id)!.PrepareCommit(new(6, 0))!;
        var expanded = result.Snapshot.Masks[id]; var transform = result.Snapshot.Manifest.Layers[0].Transform;
        var old = BrushEngine.DocumentToPixel(transform, expanded.Width, expanded.Height, new(2.5, 2.5));
        var next = BrushEngine.DocumentToPixel(transform, expanded.Width, expanded.Height, new(8.5, 2.5));
        Assert.Equal(value, expanded.Pixels[(int)old.Y * expanded.Width + (int)old.X]);
        Assert.Equal(255, expanded.Pixels[(int)next.Y * expanded.Width + (int)next.X]); Assert.Equal(value, mask.Pixels[0]);
    }

    [Fact]
    public void IndependentMaskAndLayerPropertiesStayUnchanged()
    {
        var (doc, id, pixels) = Scene(); Set(pixels, 2, 2, 255, 0, 0, 255); var layer = doc.Snapshot.Manifest.Layers[0];
        var mask = new MaskSurface(2, 2); ((Dictionary<Guid, MaskSurface>)doc.Snapshot.Masks)[id] = mask;
        layer.MaskFile = ProjectValidator.Uuid(id) + ".mask.png"; layer.MaskPlacement = new() { Origin = new(1, 1), Size = new(3, 3) };
        layer.MaskLinked = false; layer.MaskEnabled = false; layer.Opacity = .4; layer.BlendMode = LayerBlendMode.Screen;
        layer.Text = new() { Content = "Rasterize on commit" };
        layer.Shape = System.Text.Json.JsonSerializer.SerializeToElement(new { kind = "rectangle" });
        layer.Effects = System.Text.Json.JsonSerializer.SerializeToElement(new { dropShadow = new { radius = 2 } });
        var result = doc.BeginPixelMove(id)!.PrepareCommit(new(5, 0))!; var next = result.Snapshot.Manifest.Layers[0];
        Assert.Same(mask, result.Snapshot.Masks[id]); Assert.Equal(layer.MaskPlacement, next.MaskPlacement);
        Assert.Equal(layer.Name, next.Name); Assert.Equal(layer.Opacity, next.Opacity); Assert.Equal(layer.BlendMode, next.BlendMode);
        Assert.Equal(layer.MaskEnabled, next.MaskEnabled); Assert.Equal(layer.MaskLinked, next.MaskLinked);
        Assert.True(System.Text.Json.JsonElement.DeepEquals(layer.Effects!.Value, next.Effects!.Value));
        Assert.Null(next.Text); Assert.Null(next.Shape); Assert.NotNull(layer.Text); Assert.NotNull(layer.Shape);
    }

    [Fact]
    public void CommitKeepsFaintAlphaAndOwnsItsPixelsAndMetadata()
    {
        var (doc, id, pixels) = Scene(); Set(pixels, 2, 2, 1, 0, 0, 1);
        var edit = doc.BeginPixelMove(id)!; var result = edit.PrepareCommit(new(5, 0))!;
        Assert.Equal(1, result.Snapshot.Images[id].Width); Assert.Equal(1, result.Snapshot.Images[id].Height);
        Assert.Equal(new PointD(7, 2), result.Snapshot.Manifest.Layers[0].Transform.Origin);
        var bytes = result.Snapshot.Images[id].Pixels.ToArray(); edit.ApplyOffset(new(1, 1)); Assert.Equal(bytes, result.Snapshot.Images[id].Pixels);
        Assert.True(doc.CommitPixelMove(result)); doc.Snapshot.Manifest.Layers[0].Name = "Changed";
        Assert.Equal("Pixels", result.Snapshot.Manifest.Layers[0].Name);
    }

    [Theory]
    [InlineData("selection")]
    [InlineData("manifest")]
    [InlineData("pixels")]
    [InlineData("mask")]
    [InlineData("replacement")]
    public void StaleResultsNeverEnterHistory(string change)
    {
        var (doc, id, pixels) = Scene(); Set(pixels, 2, 2, 255, 0, 0, 255);
        var mask = new MaskSurface(1, 1); ((Dictionary<Guid, MaskSurface>)doc.Snapshot.Masks)[id] = mask;
        doc.Snapshot.Manifest.Layers[0].MaskFile = ProjectValidator.Uuid(id) + ".mask.png";
        var result = doc.BeginPixelMove(id)!.PrepareCommit(new(5, 0))!;
        switch (change)
        {
            case "selection": doc.SetSelection(doc.ActiveSelection!.Translated(1, 0)); break;
            case "manifest": doc.Snapshot.Manifest.Layers[0].Opacity = .5; break;
            case "pixels": pixels.MarkModified(); break;
            case "mask": mask.MarkModified(); break;
            case "replacement": ((Dictionary<Guid, Surface>)doc.Snapshot.Images)[id] = pixels.Clone(); break;
        }
        Assert.False(doc.CommitPixelMove(result)); Assert.False(doc.CanUndo); Assert.False(doc.IsDirty);
    }

    [Fact]
    public void EntryRejectsMissingEmptyHiddenGroupAdjustmentAndMaskTargets()
    {
        var (doc, id, _) = Scene(); Assert.Null(doc.BeginPixelMove(Guid.NewGuid())); Assert.Null(doc.BeginPixelMove(id, onMask: true));
        var selection = doc.ActiveSelection; doc.SetSelection(null); Assert.Null(doc.BeginPixelMove(id));
        doc.SetSelection(Selection.FromCoverage(new MaskSurface(12, 8))); Assert.Null(doc.BeginPixelMove(id));
        doc.SetSelection(selection); var layer = doc.Snapshot.Manifest.Layers[0]; layer.IsVisible = false; Assert.Null(doc.BeginPixelMove(id));
        layer.IsVisible = true; layer.IsGroup = true; Assert.Null(doc.BeginPixelMove(id));
        layer.IsGroup = false; layer.Adjustment = new() { Kind = AdjustmentKind.Levels }; Assert.Null(doc.BeginPixelMove(id));
    }

    [Fact]
    public void TransparentButOverlappingSelectionStillMovesItsOutline()
    {
        var (doc, id, _) = Scene(); var result = doc.BeginPixelMove(id)!.PrepareCommit(new(5, 0))!;
        Assert.True(doc.CommitPixelMove(result)); Assert.Equal(new RectI(7, 2, 1, 1), doc.ActiveSelection!.OutlineBounds);
        Assert.True(doc.CanUndo);
    }

    [Fact]
    public void SparseDraftDoesNotAllocateAFullDocumentSurfaceAndReusesTouchedTiles()
    {
        var (doc, id, pixels) = Scene(4096, 4096, 32, 32); Set(pixels, 2, 2, 255, 0, 0, 255);
        var edit = doc.BeginPixelMove(id)!; edit.ApplyOffset(new(1, 1));
        Assert.Equal(new RectI(0, 0, 4096, 4096), edit.GridBounds); Assert.InRange(edit.AllocatedPixels, 1, 256 * 256);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) edit.ApplyOffset(new(i % 32, i % 16));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 2 * 1024 * 1024);
        Assert.InRange(edit.AllocatedPixels, 1, 256 * 256); Assert.Same(pixels, doc.Snapshot.Images[id]);
    }

    [Fact]
    public void LimitsAndCancellationRefuseWorkBeforeChangingTheDocument()
    {
        var (doc, id, pixels) = Scene(2000, 10, 256, 1); Set(pixels, 0, 0, 255, 0, 0, 255);
        doc.Snapshot.Manifest.Layers[0].Transform.Size = new(1, 1); doc.SetSelection(Selection.Rectangle(2000, 10, 0, 0, 1, 1));
        var edit = doc.BeginPixelMove(id)!;
        Assert.Throws<InvalidOperationException>(() => edit.ApplyOffset(new(200, 0)));
        Assert.Equal(PointD.Zero, edit.Offset); Assert.Equal(0, edit.AllocatedPixels);
        var canceled = new CancellationToken(true); Assert.Throws<OperationCanceledException>(() => edit.PrepareCommit(new(1, 0), canceled));
        Assert.Throws<ArgumentOutOfRangeException>(() => edit.ApplyOffset(new(double.NaN, 0)));
        Assert.False(doc.CanUndo); Assert.False(doc.IsDirty); Assert.Same(pixels, doc.Snapshot.Images[id]);
    }

    [Fact]
    public void StridedSourceAndUnselectedPixelsOutsideCanvasArePreserved()
    {
        var (doc, id, _) = Scene(); var pixels = new Surface(12, 8, new byte[8 * 56], 56);
        ((Dictionary<Guid, Surface>)doc.Snapshot.Images)[id] = pixels;
        doc.Snapshot.Manifest.Layers[0].Transform.Origin = new(-4, 0);
        Set(pixels, 0, 1, 0, 0, 255, 255); Set(pixels, 6, 2, 255, 0, 0, 255);
        var result = doc.BeginPixelMove(id)!.PrepareCommit(new(5, 0))!;
        var image = result.Snapshot.Images[id]; var transform = result.Snapshot.Manifest.Layers[0].Transform;
        var left = BrushEngine.DocumentToPixel(transform, image.Width, image.Height, new(-3.5, 1.5));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, At(image, (int)left.X, (int)left.Y));
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, At(Render(result), 7, 2)); Assert.Equal(new byte[] { 255, 0, 0, 255 }, At(pixels, 6, 2));
    }

    [Fact]
    public void SerialPreviewsReusePixelsMetadataAndWarmCompositorWithoutGhosts()
    {
        var (doc, id, pixels) = Scene(1024, 1024, 1024, 1024); Set(pixels, 2, 2, 255, 0, 0, 255);
        var edit = doc.BeginPixelMove(id)!; var first = edit.Preview(new(4, 0)); var compositor = new CanvasCompositor(first.Snapshot);
        compositor.Render(); var image = first.Snapshot.Images[id]; var metadata = first.Snapshot.Manifest;
        var next = edit.Preview(new(10, 0)); Assert.Same(image, next.Snapshot.Images[id]); Assert.Same(metadata, next.Snapshot.Manifest);
        compositor.SetSnapshot(next.Snapshot); var dirty = compositor.ExpandPixelDirty(next.DirtyBounds);
        var rendered = compositor.Render(dirty.X, dirty.Y, dirty.X + dirty.Width, dirty.Y + dirty.Height);
        Assert.Equal(new byte[4], At(rendered, 6, 2)); Assert.Equal(new byte[] { 255, 0, 0, 255 }, At(rendered, 12, 2));
        Assert.Equal(new CanvasCompositor(next.Snapshot).Render().Pixels, rendered.Pixels);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) edit.Preview(new(i % 32 + 1, 0));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 2 * 1024 * 1024);
        Assert.Same(pixels, doc.Snapshot.Images[id]); Assert.False(doc.CanUndo);
    }

    [Fact]
    public void AlphaTrimIncludesUnpatchedRowSegmentsAndFaintExtremePixels()
    {
        var (doc, id, pixels) = Scene(1024, 1024, 1024, 1024);
        Set(pixels, 200, 200, 255, 0, 0, 255); Set(pixels, 900, 600, 1, 0, 0, 1); Set(pixels, 800, 500, 0, 1, 0, 1);
        doc.SetSelection(Selection.Rectangle(1024, 1024, 200, 200, 1, 1));
        var result = doc.BeginPixelMove(id)!.PrepareCommit(new(400, 0))!;
        Assert.Equal(new PointD(600, 200), result.Snapshot.Manifest.Layers[0].Transform.Origin);
        var image = result.Snapshot.Images[id]; Assert.Equal(301, image.Width); Assert.Equal(401, image.Height);
        Assert.Equal(new byte[] { 1, 0, 0, 1 }, At(image, 300, 400)); Assert.Equal(new byte[] { 0, 1, 0, 1 }, At(image, 200, 300));
        var outside = new Surface(2, 2); doc.BeginPixelMove(id)!.DrawPixels(outside, new(2000, 2000, 2, 2));
        Assert.All(outside.Pixels, value => Assert.Equal(0, value));
    }
}
