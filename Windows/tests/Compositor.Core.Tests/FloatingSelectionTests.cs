using Xunit;

namespace Compositor.Core.Tests;

public class FloatingSelectionTests
{
    private static (EditorDocument Doc, Guid Id, Surface Pixels) Scene(int width = 12, int height = 8)
    {
        var id = Guid.NewGuid(); var pixels = new Surface(width, height);
        var doc = new EditorDocument(new ProjectSnapshot
        {
            Manifest = new() { DocumentId = Guid.NewGuid(), Width = width, Height = height, ActiveLayerId = id,
                Layers = [new() { Id = id, Name = "Pixels", IsVisible = true, Transform = new() { Size = new(width, height) },
                    ImageFile = ProjectValidator.Uuid(id) + ".png" }] },
            Images = new Dictionary<Guid, Surface> { [id] = pixels }, Masks = new Dictionary<Guid, MaskSurface>()
        });
        doc.SetSelection(Selection.Rectangle(width, height, 2, 2, 2, 2));
        return (doc, id, pixels);
    }

    [Fact]
    public void SelectedPixelsHaveAFloatingTransformEntry()
    {
        var (doc, id, _) = Scene();
        Assert.NotNull(doc.BeginFloatingSelection(id));
    }

    private static void Set(Surface pixels, int x, int y, params byte[] rgba) => rgba.CopyTo(pixels.Pixels, y * pixels.Stride + x * 4);
    private static byte[] At(Surface pixels, int x, int y) => pixels.Pixels.AsSpan(y * pixels.Stride + x * 4, 4).ToArray();

    [Fact]
    public void PreviewCutsOntoATemporarySiblingWithoutChangingLiveState()
    {
        var (doc, id, pixels) = Scene(); Set(pixels, 2, 2, 100, 50, 25, 200); Set(pixels, 1, 1, 0, 0, 255, 255);
        var layer = doc.Snapshot.Manifest.Layers[0]; layer.Opacity = .4; layer.BlendMode = LayerBlendMode.Multiply;
        var before = doc.Snapshot; var selection = doc.ActiveSelection; var bytes = pixels.Pixels.ToArray();
        var edit = doc.BeginFloatingSelection(id)!; var prepared = edit.Prepare();
        var floating = prepared.Manifest.Layers[1];
        Assert.Equal(edit.FloatingLayerId, floating.Id); Assert.Equal(floating.Id, prepared.Manifest.ActiveLayerId);
        Assert.Equal("Floating Selection", floating.Name); Assert.Equal(.4, floating.Opacity); Assert.Equal(LayerBlendMode.Multiply, floating.BlendMode);
        Assert.Null(floating.MaskFile); Assert.Null(floating.MaskSourceId); Assert.Null(floating.Effects);
        Assert.Equal(new PointD(2, 2), floating.Transform.Origin); Assert.Equal(new SizeD(2, 2), floating.Transform.Size);
        Assert.Equal(new byte[] { 100, 50, 25, 200 }, At(prepared.Images[floating.Id], 0, 0));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, At(prepared.Images[id], 0, 0));
        var value = edit.Value; value.Origin = new(7, 2); var preview = edit.Preview(value);
        Assert.Equal(value, preview.Snapshot.Manifest.Layers[1].Transform);
        Assert.Same(prepared.Images[floating.Id], preview.Snapshot.Images[floating.Id]);
        Assert.Same(before, doc.Snapshot); Assert.Same(selection, doc.ActiveSelection); Assert.Equal(bytes, pixels.Pixels);
        Assert.False(doc.CanUndo); Assert.False(doc.IsDirty);
    }

    [Fact]
    public void MergeMovesPixelsAndSelectionTogetherAsOneUndo()
    {
        var (doc, id, pixels) = Scene(); Set(pixels, 2, 2, 255, 0, 0, 255); Set(pixels, 1, 1, 0, 0, 255, 255);
        var originalSelection = doc.ActiveSelection; var edit = doc.BeginFloatingSelection(id)!;
        var value = edit.Value; value.Origin = new(7, 2); var result = edit.PrepareCommit(value)!;
        Assert.Single(result.Snapshot.Manifest.Layers); Assert.Equal(id, result.Snapshot.Manifest.ActiveLayerId);
        var rendered = new CanvasCompositor(result.Snapshot).Render();
        Assert.Equal(new byte[4], At(rendered, 2, 2)); Assert.Equal(new byte[] { 255, 0, 0, 255 }, At(rendered, 7, 2));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, At(rendered, 1, 1));
        Assert.True(doc.CommitFloatingSelection(result)); Assert.Equal(new RectI(7, 2, 2, 2), doc.ActiveSelection!.OutlineBounds);
        Assert.True(doc.Undo()); Assert.Same(pixels, doc.Snapshot.Images[id]); Assert.Same(originalSelection, doc.ActiveSelection); Assert.False(doc.CanUndo);
        Assert.True(doc.Redo()); Assert.Equal(new byte[] { 255, 0, 0, 255 }, At(new CanvasCompositor(doc.Snapshot).Render(), 7, 2));
    }

    [Fact]
    public void ReturningToTheInitialPlacementLeavesSoftPixelsAndRedoExact()
    {
        var (doc, id, pixels) = Scene(); Set(pixels, 2, 2, 100, 50, 25, 200);
        doc.SetSelection(doc.ActiveSelection!.Feathered(2)); doc.RenameLayer(id, "Renamed"); Assert.True(doc.Undo());
        var edit = doc.BeginFloatingSelection(id)!; var moved = edit.Value; moved.Origin = new(7, 2); edit.Preview(moved);
        Assert.Null(edit.PrepareCommit(edit.Value)); Assert.True(doc.CanRedo); Assert.False(doc.CanUndo);
        Assert.Equal(new byte[] { 100, 50, 25, 200 }, At(doc.Snapshot.Images[id], 2, 2));
    }

    [Fact]
    public void DocumentGridLiftIgnoresLayerMasksOpacityAndEffects()
    {
        var (doc, id, pixels) = Scene(); Set(pixels, 2, 2, 100, 50, 25, 200);
        var layer = doc.Snapshot.Manifest.Layers[0]; layer.MaskFile = ProjectValidator.Uuid(id) + "-mask.png";
        ((Dictionary<Guid, MaskSurface>)doc.Snapshot.Masks)[id] = new(1, 1); layer.Opacity = .1;
        var prepared = doc.BeginFloatingSelection(id)!.Prepare(); var floating = prepared.Manifest.ActiveLayerId!.Value;
        Assert.Equal(new byte[] { 100, 50, 25, 200 }, At(prepared.Images[floating], 0, 0));
    }

    [Fact]
    public void CutAndLiftMultiplyAllPremultipliedChannelsByCoverage()
    {
        var (doc, id, pixels) = Scene(); Set(pixels, 2, 2, 100, 50, 25, 200); Set(pixels, 0, 0, 0, 0, 1, 1);
        var mask = new MaskSurface(12, 8); mask.Pixels[2 * 12 + 2] = 128; doc.SetSelection(Selection.FromCoverage(mask));
        var edit = doc.BeginFloatingSelection(id)!; var prepared = edit.Prepare();
        Assert.Equal(new byte[] { 50, 25, 13, 100 }, At(prepared.Images[edit.FloatingLayerId], 0, 0));
        Assert.Equal(new byte[] { 50, 25, 12, 100 }, At(new CanvasCompositor(new()
        { Manifest = prepared.Manifest, Images = prepared.Images.Where(p => p.Key != edit.FloatingLayerId).ToDictionary(), Masks = prepared.Masks }).Render(), 2, 2));
    }

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(90, true, false)]
    [InlineData(33, false, true)]
    public void LiftUsesDocumentResolutionAndDisplayedSourceGeometry(double rotation, bool flipX, bool flipY)
    {
        var (doc, id, _) = Scene(20, 20); var pixels = new Surface(4, 3);
        for (int y = 0; y < 3; y++) for (int x = 0; x < 4; x++) Set(pixels, x, y, (byte)(x * 50), (byte)(y * 60), 0, 255);
        ((Dictionary<Guid, Surface>)doc.Snapshot.Images)[id] = pixels; var layer = doc.Snapshot.Manifest.Layers[0];
        layer.Transform = new() { Origin = new(5, 5), Size = new(8, 6), Rotation = rotation, FlipX = flipX, FlipY = flipY, Sampling = LayerSampling.Smooth };
        doc.SetSelection(Selection.Rectangle(20, 20, 4, 4, 11, 11));
        var reference = new Surface(11, 11); var placed = new LayerTransform { Origin = new(1, 1), Size = new(8, 6), Rotation = rotation,
            FlipX = flipX, FlipY = flipY, Sampling = LayerSampling.Smooth };
        Resampler.Draw(pixels, placed, reference, 0, 0, 11, 11);
        var edit = doc.BeginFloatingSelection(id)!; var prepared = edit.Prepare();
        Assert.Equal(reference.Pixels, prepared.Images[edit.FloatingLayerId].Pixels);
        Assert.Equal(layer.Transform.Rotation, prepared.Manifest.Layers[0].Transform.Rotation);
        Assert.Equal(flipX, prepared.Manifest.Layers[0].Transform.FlipX); Assert.Equal(flipY, prepared.Manifest.Layers[0].Transform.FlipY);
    }

    [Fact]
    public void AttachedMaskGrowsWhiteWhileIndependentMaskKeepsItsPlacement()
    {
        foreach (bool independent in new[] { false, true })
        {
            var (doc, id, pixels) = Scene(); Set(pixels, 2, 2, 255, 0, 0, 255); Set(pixels, 0, 0, 0, 0, 255, 255);
            var layer = doc.Snapshot.Manifest.Layers[0]; layer.MaskFile = ProjectValidator.Uuid(id) + "-mask.png";
            var mask = new MaskSurface(1, 1); mask.Pixels[0] = 37; ((Dictionary<Guid, MaskSurface>)doc.Snapshot.Masks)[id] = mask; layer.MaskEnabled = false; layer.MaskLinked = false;
            if (independent) layer.MaskPlacement = new() { Origin = new(1, 1), Size = new(5, 5), Rotation = 17 };
            var edit = doc.BeginFloatingSelection(id)!; var value = edit.Value; value.Origin = new(15, 2); var result = edit.PrepareCommit(value)!;
            var merged = result.Snapshot.Manifest.Layers[0]; Assert.False(merged.MaskEnabled); Assert.False(merged.MaskLinked);
            if (independent) { Assert.Same(mask, result.Snapshot.Masks[id]); Assert.Equal(layer.MaskPlacement, merged.MaskPlacement); }
            else { Assert.Null(merged.MaskPlacement); Assert.Equal((byte)37, result.Snapshot.Masks[id].Pixels[0]); Assert.Contains((byte)255, result.Snapshot.Masks[id].Pixels); }
        }
    }

    [Fact]
    public void FloatingMergeKeepsTheUnionExtentIncludingTransparentPixels()
    {
        var (doc, id, pixels) = Scene(); Set(pixels, 0, 0, 0, 0, 255, 255); Set(pixels, 2, 2, 255, 0, 0, 255);
        var edit = doc.BeginFloatingSelection(id)!; var value = edit.Value; value.Origin = new(15, 2);
        var result = edit.PrepareCommit(value)!; Assert.Equal(17, result.Snapshot.Images[id].Width); Assert.Equal(4, result.Snapshot.Images[id].Height);
        Assert.Equal(new SizeD(17, 4), result.Snapshot.Manifest.Layers[0].Transform.Size);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void StaleSourceManifestSelectionOrAssetCannotPublish(int kind)
    {
        var (doc, id, pixels) = Scene(); Set(pixels, 2, 2, 255, 0, 0, 255);
        var edit = doc.BeginFloatingSelection(id)!; var value = edit.Value; value.Origin = new(7, 2); var result = edit.PrepareCommit(value)!;
        if (kind == 0) doc.SetSelection(Selection.Rectangle(12, 8, 0, 0, 1, 1));
        if (kind == 1) doc.Snapshot.Manifest.Layers[0].Name = "Changed";
        if (kind == 2) pixels.MarkModified();
        if (kind == 3) ((Dictionary<Guid, Surface>)doc.Snapshot.Images)[id] = pixels.Clone();
        if (kind == 4) doc.RenameLayer(id, "Changed");
        Assert.False(doc.CommitFloatingSelection(result));
    }

    [Fact]
    public void EmptyMaskHiddenGroupAndAdjustmentTargetsRejectEntry()
    {
        var (doc, id, _) = Scene(); Assert.Null(doc.BeginFloatingSelection(id, onMask: true));
        doc.SetSelection(null); Assert.Null(doc.BeginFloatingSelection(id));
        doc.SetSelection(Selection.Rectangle(12, 8, 0, 0, 0, 0)); Assert.Null(doc.BeginFloatingSelection(id));
        doc.SetSelection(Selection.Rectangle(12, 8, 0, 0, 1, 1));
        doc.Snapshot.Manifest.Layers[0].IsVisible = false; Assert.Null(doc.BeginFloatingSelection(id));
        doc.Snapshot.Manifest.Layers[0].IsVisible = true; doc.Snapshot.Manifest.Layers[0].IsGroup = true; Assert.Null(doc.BeginFloatingSelection(id));
    }

    [Fact]
    public void CancellationLeavesLivePixelsAndHistoryUntouchedAndCanRetry()
    {
        var (doc, id, pixels) = Scene(); Set(pixels, 2, 2, 255, 0, 0, 255); var edit = doc.BeginFloatingSelection(id)!;
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => edit.Prepare(cancellation.Token));
        Assert.False(doc.CanUndo); Assert.Equal(new byte[] { 255, 0, 0, 255 }, At(pixels, 2, 2));
        Assert.Equal(2, edit.Prepare().Manifest.Layers.Count);
    }

    [Fact]
    public void TransformedOutlineKeepsHolesFlipsAndFeatherInDocumentPixels()
    {
        var mask = new MaskSurface(20, 20); mask.Pixels[2 * 20 + 2] = 255; mask.Pixels[2 * 20 + 4] = 255;
        var selection = Selection.FromCoverage(mask).Feathered(2);
        var original = new LayerTransform { Origin = new(2, 2), Size = new(3, 1) };
        var target = new LayerTransform { Origin = new(7, 4), Size = new(6, 2), FlipX = true };
        var moved = selection.Transformed(original, target);
        Assert.Equal(new RectI(7, 4, 6, 2), moved.OutlineBounds); Assert.Equal(2, moved.Feather);
        Assert.True(moved.Contains(new(7.5, 4.5))); Assert.False(moved.Contains(new(9.5, 4.5))); Assert.True(moved.Contains(new(11.5, 4.5)));
        Assert.Same(mask, selection.OutlineCoverage); Assert.NotSame(mask, moved.OutlineCoverage);
    }

    [Fact]
    public void TransformedOutlineRetainsOffCanvasShapeAndCanTranslateBack()
    {
        var selection = Selection.Rectangle(12, 8, 2, 2, 2, 2);
        var original = new LayerTransform { Origin = new(2, 2), Size = new(2, 2) };
        var target = new LayerTransform { Origin = new(-5, -5), Size = new(4, 4), Rotation = 90 };
        var moved = selection.Transformed(original, target);
        Assert.False(moved.IsEmpty); Assert.Equal(new RectI(-5, -5, 4, 4), moved.OutlineBounds); Assert.Equal(default, moved.Bounds);
        var returned = moved.Translated(7, 7); Assert.Equal(new RectI(2, 2, 4, 4), returned.OutlineBounds); Assert.True(returned.Contains(new(2.5, 2.5)));
        Assert.Equal(4, moved.OutlineCoverage.Width); Assert.Equal(4, moved.OutlineCoverage.Height);
    }

    [Fact]
    public void TransformedOutlineRejectsInvalidOrOversizePlacementsBeforeAllocating()
    {
        var selection = Selection.Rectangle(12, 8, 2, 2, 2, 2); var original = new LayerTransform { Origin = new(2, 2), Size = new(2, 2) };
        Assert.Throws<ArgumentOutOfRangeException>(() => selection.Transformed(original, new() { Size = new(30001, 2) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => selection.Transformed(original, new() { Origin = new(double.NaN, 0), Size = new(2, 2) }));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => selection.Transformed(original, original, cancellation.Token));
    }

    [Fact]
    public void DistortedFloatingPixelsMergeAndTheConvexOutlineFollows()
    {
        var (doc, id, pixels) = Scene(); Set(pixels, 0, 0, 0, 0, 255, 255); Set(pixels, 2, 2, 255, 0, 0, 255);
        var edit = doc.BeginFloatingSelection(id)!; PointD[] corners = [new(7, 2), new(9, 2), new(9, 4), new(7, 4)];
        var preview = edit.Preview(edit.Value, corners); var image = new CanvasCompositor(preview.Snapshot).Render();
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, At(image, 7, 2)); Assert.Equal(new byte[4], At(image, 2, 2));
        var result = Assert.IsType<FloatingSelectionResult>(edit.PrepareCommit(edit.Value, corners));
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, At(new CanvasCompositor(result.Snapshot).Render(), 7, 2));
        Assert.Equal(new RectI(7, 2, 2, 2), result.Selection!.OutlineBounds); Assert.True(doc.CommitFloatingSelection(result));
    }

    [Fact]
    public void FoldedFloatingWarpDropsTheSelectionAsInTheSource()
    {
        var (doc, id, pixels) = Scene(); Set(pixels, 2, 2, 255, 0, 0, 255);
        var edit = doc.BeginFloatingSelection(id)!; PointD[] corners = [new(2, 2), new(4, 2), new(1, 1), new(2, 4)];
        Assert.True(DistortWarp.IsUsable(corners)); Assert.False(DistortWarp.IsConvex(corners));
        var original = doc.ActiveSelection; var result = Assert.IsType<FloatingSelectionResult>(edit.PrepareCommit(edit.Value, corners));
        Assert.Null(result.Selection); Assert.True(doc.CommitFloatingSelection(result)); Assert.Null(doc.ActiveSelection);
        Assert.True(doc.Undo()); Assert.Same(original, doc.ActiveSelection); Assert.Same(pixels, doc.Snapshot.Images[id]);
    }

    [Fact]
    public void FloatingWarpMirrorsPixelReadsAndRejectsCollapsedCorners()
    {
        var (doc, id, pixels) = Scene(); Set(pixels, 2, 2, 255, 0, 0, 255);
        var edit = doc.BeginFloatingSelection(id)!; var value = edit.Value; value.FlipX = true;
        PointD[] corners = [new(7, 2), new(9, 2), new(9, 4), new(7, 4)];
        var result = edit.PrepareCommit(value, corners)!;
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, At(new CanvasCompositor(result.Snapshot).Render(), 8, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => edit.Preview(value, [PointD.Zero, PointD.Zero, PointD.Zero, PointD.Zero]));
    }

    [Theory]
    [InlineData(LayerSampling.Nearest)] [InlineData(LayerSampling.Smooth)] [InlineData(LayerSampling.HighQuality)]
    public void EnlargedFloatingPixelsFillTheEntirePlacedRectangle(LayerSampling sampling)
    {
        var (doc, id, pixels) = Scene();
        for (int y = 2; y < 4; y++) for (int x = 2; x < 4; x++) Set(pixels, x, y, 255, 0, 0, 255);
        Set(pixels, 0, 0, 0, 0, 255, 255);
        var edit = doc.BeginFloatingSelection(id)!; var value = edit.Value; value.Origin = new(7, 2); value.Size = new(4, 4); value.Sampling = sampling;
        var rendered = new CanvasCompositor(edit.PrepareCommit(value)!.Snapshot).Render();
        for (int y = 2; y < 6; y++) for (int x = 7; x < 11; x++) Assert.Equal(new byte[] { 255, 0, 0, 255 }, At(rendered, x, y));
    }

    [Fact]
    public void WarmFloatingPreviewsOnlyDirtyTheirOldAndNewPlacementAndReusePixels()
    {
        var (doc, id, pixels) = Scene(1024, 1024); Array.Fill(pixels.Pixels, (byte)255);
        doc.SetSelection(Selection.Rectangle(1024, 1024, 300, 300, 32, 32));
        var edit = doc.BeginFloatingSelection(id)!; var first = edit.Preview(edit.Value); var compositor = new CanvasCompositor(first.Snapshot); compositor.Render();
        var value = edit.Value; value.Origin = new(350, 300); var next = edit.Preview(value);
        Assert.Equal(new RectI(298, 298, 86, 36), next.DirtyBounds);
        Assert.Same(first.Snapshot.Images[id], next.Snapshot.Images[id]); Assert.Same(first.Snapshot.Images[edit.FloatingLayerId], next.Snapshot.Images[edit.FloatingLayerId]);
        Assert.NotSame(first.Snapshot.Manifest, next.Snapshot.Manifest); Assert.Equal(edit.Value, first.Snapshot.Manifest.Layers[1].Transform);
        compositor.SetSnapshot(next.Snapshot); var dirty = compositor.ExpandPixelDirty(next.DirtyBounds);
        var partial = compositor.Render(dirty.X, dirty.Y, dirty.X + dirty.Width, dirty.Y + dirty.Height);
        Assert.Equal(new CanvasCompositor(next.Snapshot).Render().Pixels, partial.Pixels);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) { value.Origin = new(350 + i % 32, 300); edit.Preview(value); }
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 3 * 1024 * 1024);
        Assert.False(doc.CanUndo); Assert.Same(pixels, doc.Snapshot.Images[id]);
    }

    [Fact]
    public void OffCanvasPreviewFootprintsDoNotExpandVisibleDirtyBandsToTheOrigin()
    {
        var (doc, id, pixels) = Scene(1024, 1024); Array.Fill(pixels.Pixels, (byte)255);
        doc.SetSelection(Selection.Rectangle(1024, 1024, 300, 300, 32, 32));
        var edit = doc.BeginFloatingSelection(id)!; edit.Preview(edit.Value); var value = edit.Value; value.Origin = new(-64, -64);
        Assert.Equal(new RectI(298, 298, 36, 36), edit.Preview(value).DirtyBounds);
        value.Origin = new(400, 300); Assert.Equal(new RectI(398, 298, 36, 36), edit.Preview(value).DirtyBounds);
    }

    [Fact]
    public void OriginalFloatingMergeRetiresLiveEffectsButCancelAndUndoKeepThem()
    {
        var (doc, id, pixels) = Scene(); Set(pixels, 2, 2, 255, 0, 0, 255);
        var layer = doc.Snapshot.Manifest.Layers[0]; layer.Effects = System.Text.Json.JsonSerializer.SerializeToElement(new { dropShadow = new { radius = 2 } });
        layer.Text = new() { Content = "Rasterize" }; layer.Shape = System.Text.Json.JsonSerializer.SerializeToElement(new { kind = "rectangle" });
        var edit = doc.BeginFloatingSelection(id)!; var prepared = edit.Prepare();
        Assert.NotNull(prepared.Manifest.Layers[0].Effects); Assert.Null(prepared.Manifest.Layers[1].Effects); Assert.NotNull(layer.Text);
        Assert.Null(edit.PrepareCommit(edit.Value)); Assert.NotNull(layer.Effects); Assert.False(doc.CanUndo);
        var value = edit.Value; value.Origin = new(7, 2); var result = edit.PrepareCommit(value)!;
        Assert.Null(result.Snapshot.Manifest.Layers[0].Effects); Assert.Null(result.Snapshot.Manifest.Layers[0].Text); Assert.Null(result.Snapshot.Manifest.Layers[0].Shape);
        Assert.True(doc.CommitFloatingSelection(result)); Assert.True(doc.Undo()); Assert.NotNull(doc.Snapshot.Manifest.Layers[0].Effects); Assert.NotNull(doc.Snapshot.Manifest.Layers[0].Text);
    }

    [Fact]
    public void FullyClearedSourceKeepsTheOriginalBrushTileExtent()
    {
        var (doc, id, _) = Scene(512, 512); var pixels = new Surface(2, 2); Set(pixels, 0, 0, 255, 0, 0, 255);
        ((Dictionary<Guid, Surface>)doc.Snapshot.Images)[id] = pixels; doc.Snapshot.Manifest.Layers[0].Transform = new() { Origin = new(2, 2), Size = new(2, 2) };
        doc.SetSelection(Selection.Rectangle(512, 512, 2, 2, 1, 1)); var edit = doc.BeginFloatingSelection(id)!; var prepared = edit.Prepare();
        Assert.Equal(new SizeD(256, 256), prepared.Manifest.Layers[0].Transform.Size); Assert.Equal(PointD.Zero, prepared.Manifest.Layers[0].Transform.Origin);
        Assert.Equal(256, prepared.Images[id].Width); Assert.Equal(256, prepared.Images[id].Height); Assert.All(prepared.Images[id].Pixels, b => Assert.Equal((byte)0, b));
        var value = edit.Value; value.Origin = new(260, 2); var result = edit.PrepareCommit(value)!;
        Assert.Equal(261, result.Snapshot.Images[id].Width); Assert.Equal(256, result.Snapshot.Images[id].Height);
        Assert.Null(edit.PrepareCommit(edit.Value)); Assert.Same(pixels, doc.Snapshot.Images[id]); Assert.False(doc.CanUndo);
    }

    [Fact]
    public void FloatingPreparationHonorsSourceStrideAndKeepsFaintOutsideCanvasPixels()
    {
        var (doc, id, _) = Scene(); var pixels = new Surface(12, 8, new byte[8 * 56], 56);
        ((Dictionary<Guid, Surface>)doc.Snapshot.Images)[id] = pixels; doc.Snapshot.Manifest.Layers[0].Transform.Origin = new(-4, 0);
        Set(pixels, 0, 1, 0, 0, 1, 1); Set(pixels, 6, 2, 255, 0, 0, 255);
        doc.SetSelection(Selection.Rectangle(12, 8, 2, 2, 1, 1)); var edit = doc.BeginFloatingSelection(id)!;
        var value = edit.Value; value.Origin = new(7, 2); var result = edit.PrepareCommit(value)!;
        var image = result.Snapshot.Images[id]; var placed = result.Snapshot.Manifest.Layers[0].Transform;
        var point = BrushEngine.DocumentToPixel(placed, image.Width, image.Height, new(-3.5, 1.5));
        Assert.Equal(new byte[] { 0, 0, 1, 1 }, At(image, (int)point.X, (int)point.Y));
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, At(new CanvasCompositor(result.Snapshot).Render(), 7, 2));
    }

    [Fact]
    public void WarpPreviewCapsItsRasterAndCachesCopiedCornerValues()
    {
        var (doc, id, pixels) = Scene(); Set(pixels, 2, 2, 255, 0, 0, 255);
        var edit = doc.BeginFloatingSelection(id)!; PointD[] corners = [new(10, 20), new(4110, 20), new(4110, 1020), new(10, 1020)];
        var first = edit.Preview(edit.Value, corners); var image = first.Snapshot.Images[edit.FloatingLayerId];
        Assert.Equal(2048, image.Width); Assert.Equal(500, image.Height);
        Assert.Equal(new SizeD(4100, 1000), first.Snapshot.Manifest.Layers[1].Transform.Size);
        Assert.Same(image, edit.Preview(edit.Value, corners).Snapshot.Images[edit.FloatingLayerId]);
        corners[1] = new(4120, 20); Assert.NotSame(image, edit.Preview(edit.Value, corners).Snapshot.Images[edit.FloatingLayerId]);
        Assert.Equal(new SizeD(4100, 1000), first.Snapshot.Manifest.Layers[1].Transform.Size);
    }

    [Fact]
    public void DocumentBudgetAndOversizeMergeFailBeforePublishingAnything()
    {
        var (doc, id, pixels) = Scene(); Set(pixels, 2, 2, 255, 0, 0, 255);
        var extra = Guid.NewGuid(); var assets = (Dictionary<Guid, Surface>)doc.Snapshot.Images;
        // A virtual counter checks preflight without allocating a budget-sized buffer.
        assets[extra] = new Surface((int)Limits.DocumentPixelBudget, 1, [], 0);
        var overBudget = doc.BeginFloatingSelection(id)!; Assert.Throws<InvalidOperationException>(() => overBudget.Prepare()); assets.Remove(extra);
        var edit = doc.BeginFloatingSelection(id)!; var value = edit.Value; value.Size = new(30001, 2);
        Assert.Throws<ArgumentOutOfRangeException>(() => edit.PrepareCommit(value));
        Assert.Same(pixels, doc.Snapshot.Images[id]); Assert.False(doc.CanUndo); Assert.False(doc.IsDirty);
    }

    [Fact]
    public void FoldedWarpDrawsTheSecondTriangleOverTheFirst()
    {
        var (doc, id, pixels) = Scene();
        for (int x = 2; x < 4; x++) { Set(pixels, x, 2, 255, 0, 0, 255); Set(pixels, x, 3, 0, 0, 255, 255); }
        var edit = doc.BeginFloatingSelection(id)!; PointD[] corners = [new(5, 5), new(9, 5), new(7, 7), new(5, 0)];
        Assert.False(DistortWarp.IsConvex(corners));
        var rendered = new CanvasCompositor(edit.Preview(edit.Value, corners).Snapshot).Render();
        // Both halves cover this center. The source draws TL-BR-BL last, sampling its blue bottom edge.
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, At(rendered, 6, 5));
    }

    [Fact]
    public void MirroredFoldUsesTheImagesMirroredDiagonal()
    {
        var (doc, id, pixels) = Scene();
        for (int y = 2; y < 4; y++) for (int x = 2; x < 4; x++) Set(pixels, x, y, 255, 0, 0, 255);
        var edit = doc.BeginFloatingSelection(id)!; PointD[] corners = [new(5, 5), new(9, 5), new(7, 7), new(5, 0)];
        Assert.Equal((byte)255, At(new CanvasCompositor(edit.Preview(edit.Value, corners).Snapshot).Render(), 6, 5)[3]);
        var flipped = edit.Value; flipped.FlipX = true;
        Assert.Equal(new byte[4], At(new CanvasCompositor(edit.Preview(flipped, corners).Snapshot).Render(), 6, 5));
    }
}
