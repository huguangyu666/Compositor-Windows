using Xunit;

namespace Compositor.Core.Tests;

public class MaskThumbnailTests
{
    [Theory]
    [InlineData(LayerSampling.Nearest, false, false)]
    [InlineData(LayerSampling.Nearest, false, true)]
    [InlineData(LayerSampling.Nearest, true, false)]
    [InlineData(LayerSampling.Nearest, true, true)]
    [InlineData(LayerSampling.Smooth, false, false)]
    [InlineData(LayerSampling.Smooth, false, true)]
    [InlineData(LayerSampling.Smooth, true, false)]
    [InlineData(LayerSampling.Smooth, true, true)]
    [InlineData(LayerSampling.HighQuality, false, false)]
    [InlineData(LayerSampling.HighQuality, false, true)]
    [InlineData(LayerSampling.HighQuality, true, false)]
    [InlineData(LayerSampling.HighQuality, true, true)]
    public void MaskDisplayRowsMatchResampledReferenceInsidePartialBands(LayerSampling sampling, bool flipX, bool flipY)
    {
        var mask = new MaskSurface(6, 4); for (int i = 0; i < mask.Pixels.Length; i++) mask.Pixels[i] = (byte)(i * 41);
        foreach (var origin in new[] { new PointD(-2, -1), new PointD(2, 3), new PointD(1000000, -1000000) })
        {
            var reference = new Surface(14, 10); Array.Fill(reference.Pixels, (byte)19); var rows = reference.Clone();
            var transform = new LayerTransform { Origin = origin, Size = new(6, 4), Sampling = sampling, FlipX = flipX, FlipY = flipY };
            var band = new RectI(1, 1, 12, 8);
            MaskDisplay.DrawReference(mask, transform, reference, band); MaskDisplay.Draw(mask, transform, rows, band);
            Assert.Equal(reference.Pixels, rows.Pixels);
        }
    }
    [Fact]
    public void ImageLoadsThresholdedAlphaAndRetainsAHole()
    {
        var image = new Surface(3, 3);
        for (int i = 3; i < image.Pixels.Length; i += 4) image.Pixels[i] = 128;
        image.Pixels[4 * 4 + 3] = 127;
        var selection = RasterSelection.FromImage(8, 8, image, new() { Origin = new(2, 1), Size = new(3, 3) });
        Assert.NotNull(selection); Assert.Equal(new RectI(2, 1, 3, 3), selection.Bounds);
        Assert.Equal(255, selection.Coverage.Pixels[1 * 8 + 2]); Assert.Equal(0, selection.Coverage.Pixels[2 * 8 + 3]);
        Assert.Equal(0, selection.Coverage.Pixels[0]);
    }

    [Fact]
    public void MaskLoadsDarkPixelsAtItsOwnPlacement()
    {
        var mask = new MaskSurface(2, 1, [127, 128]);
        var selection = RasterSelection.FromMask(8, 8, mask, new() { Origin = new(3, 4), Size = new(4, 2) });
        Assert.NotNull(selection); Assert.Equal(new RectI(3, 4, 2, 2), selection.Bounds);
        Assert.Equal(255, selection.Coverage.Pixels[4 * 8 + 3]); Assert.Equal(0, selection.Coverage.Pixels[4 * 8 + 5]);
    }

    [Fact]
    public void RasterSelectionPreservesSourcePixelsAndHandlesRotationAndFlip()
    {
        var mask = new MaskSurface(2, 1, [0, 255]);
        var transform = new LayerTransform { Origin = new(2.5, 3.5), Size = new(2, 1), Rotation = 90, FlipX = true };
        var selection = RasterSelection.FromMask(8, 8, mask, transform, false)!;
        Assert.Equal(0, selection.Coverage.Pixels[3 * 8 + 3]); Assert.Equal(255, selection.Coverage.Pixels[4 * 8 + 3]);
        Assert.Equal(new byte[] { 0, 255 }, mask.Pixels);
    }

    [Fact]
    public void EmptySourceReturnsNoOutlineButOutsideSourceReturnsClippedEmptyOutline()
    {
        var mask = new MaskSurface(1, 1, [255]); var transform = new LayerTransform { Origin = new(20, 20), Size = new(1, 1) };
        Assert.Null(RasterSelection.FromMask(8, 8, mask, transform));
        mask.Pixels[0] = 0;
        var selection = RasterSelection.FromMask(8, 8, mask, transform)!;
        Assert.Equal(new RectI(0, 0, 0, 0), selection.Bounds);
    }

    [Fact]
    public void FractionalPixelEdgesAreAntialiasedAfterThresholding()
    {
        var image = new Surface(1, 1); image.Pixels[3] = 128;
        var selection = RasterSelection.FromImage(4, 4, image, new() { Origin = new(.5, .5), Size = new(1, 1) })!;
        Assert.Equal(64, selection.Coverage.Pixels[0]); Assert.Equal(64, selection.Coverage.Pixels[5]);
    }

    [Theory]
    [InlineData(127, 0)]
    [InlineData(128, 255)]
    public void MaskBackgroundUsesBinaryPerimeterMean(int gray, int expected)
    {
        var mask = new MaskSurface(3, 3); Array.Fill(mask.Pixels, (byte)gray); mask.Pixels[4] = (byte)(255 - gray);
        Assert.Equal(expected, MaskDisplay.Background(mask));
    }

    [Fact]
    public void MaskDisplayShowsRawGrayIgnoringTheCompositeAndFillsOutsideWithEdgeTone()
    {
        var mask = new MaskSurface(3, 3); Array.Fill(mask.Pixels, (byte)255); mask.Pixels[4] = 73;
        var output = new Surface(8, 8);
        MaskDisplay.Draw(mask, new() { Origin = new(2, 2), Size = new(3, 3), Sampling = LayerSampling.Nearest }, output, new(0, 0, 8, 8));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, output.Pixels[..4]);
        Assert.Equal(new byte[] { 73, 73, 73, 255 }, output.Pixels.AsSpan(3 * output.Stride + 3 * 4, 4).ToArray());
    }

    [Fact]
    public void MaskBackgroundCacheTracksKnownAndUnknownRevisionChanges()
    {
        var mask = new MaskSurface(3, 3); Assert.Equal(0, MaskDisplay.Background(mask));
        Array.Fill(mask.Pixels, (byte)255); mask.MarkModified(new(0, 0, 3, 3)); Assert.Equal(255, MaskDisplay.Background(mask));
        Array.Clear(mask.Pixels); mask.MarkModified(); Assert.Equal(0, MaskDisplay.Background(mask));
    }

    [Fact]
    public void MaskDisplayUpdatesOnlyTheRequestedBandAndUsesPlacementFlips()
    {
        var mask = new MaskSurface(2, 1, [0, 255]); var output = new Surface(6, 4); Array.Fill(output.Pixels, (byte)19);
        MaskDisplay.Draw(mask, new() { Origin = new(2, 1), Size = new(2, 1), FlipX = true, Sampling = LayerSampling.Nearest }, output, new(2, 1, 2, 1));
        Assert.Equal(19, output.Pixels[0]); Assert.Equal(255, output.Pixels[output.Stride + 8]); Assert.Equal(0, output.Pixels[output.Stride + 12]);
    }

    [Fact]
    public void MaskCopySharesImmutableAssetCopiesPlacementAndKeepsFlagsInOneUndo()
    {
        var doc = EditorDocument.Create(20, 20); var from = doc.Snapshot.Manifest.Layers[0]; var target = doc.ImportImage(new(3, 3), "Target").Id;
        doc.AddRasterMask(from.Id, new(3, 3)); from.MaskLinked = false; from.MaskEnabled = false;
        from.MaskPlacement = new() { Origin = new(4, 5), Size = new(7, 8), Rotation = 37, FlipY = true };
        var asset = doc.Snapshot.Masks[from.Id];
        Assert.True(doc.CopyMask(from.Id, target)); var copied = doc.Snapshot.Manifest.Layers.Single(l => l.Id == target);
        Assert.Same(asset, doc.Snapshot.Masks[target]); Assert.Equal(from.MaskPlacement, copied.MaskPlacement); Assert.NotSame(from.MaskPlacement, copied.MaskPlacement);
        Assert.False(copied.MaskEnabled); Assert.False(copied.MaskLinked); Assert.Equal(target, doc.Snapshot.Manifest.ActiveLayerId);
        Assert.True(doc.Undo()); Assert.False(doc.Snapshot.Masks.ContainsKey(target)); Assert.True(doc.Redo()); Assert.Same(asset, doc.Snapshot.Masks[target]);
    }

    [Fact]
    public void MaskCopyRejectsSelfMissingSourceAndFolderWithoutOpeningHistory()
    {
        var doc = EditorDocument.Create(8, 8); var from = doc.Snapshot.Manifest.Layers[0].Id; doc.AddRasterMask(from, new(1, 1, [255]));
        var folder = doc.GroupLayers(from)!.Id; var before = ManifestJson.Serialize(doc.Snapshot.Manifest);
        Assert.False(doc.CopyMask(from, from)); Assert.False(doc.CopyMask(Guid.NewGuid(), from)); Assert.False(doc.CopyMask(from, folder));
        Assert.Equal(before, ManifestJson.Serialize(doc.Snapshot.Manifest));
        Assert.True(doc.Undo()); Assert.DoesNotContain(doc.Snapshot.Manifest.Layers, l => l.Id == folder);
    }

    [Fact]
    public void SoloMaskReusesCanvasExpandsOnBackgroundChangeAndBypassesVisibilityAndEnable()
    {
        var doc = EditorDocument.Create(8, 8); var layer = doc.Snapshot.Manifest.Layers[0];
        layer.IsVisible = false; layer.MaskEnabled = false; layer.MaskPlacement = new() { Origin = new(2, 2), Size = new(3, 3), Sampling = LayerSampling.Nearest };
        layer.MaskFile = ProjectValidator.Uuid(layer.Id) + ".mask.png";
        var mask = new MaskSurface(3, 3, [255, 255, 255, 255, 73, 255, 255, 255, 255]);
        ((Dictionary<Guid, MaskSurface>)doc.Snapshot.Masks)[layer.Id] = mask;
        var compositor = new CanvasCompositor(doc.Snapshot); var canvas = compositor.Render(); var band = new RectI(3, 3, 1, 1);
        Assert.Same(canvas, compositor.RenderMaskAlone(layer.Id, ref band)); Assert.Equal(new RectI(0, 0, 8, 8), band);
        Assert.Equal(255, canvas.Pixels[0]); Assert.Equal(73, canvas.Pixels[3 * canvas.Stride + 12]);
        mask.Pixels[4] = 99; mask.MarkModified(new(1, 1, 1, 1)); band = new(3, 3, 1, 1);
        compositor.RenderMaskAlone(layer.Id, ref band); Assert.Equal(new RectI(3, 3, 1, 1), band); Assert.Equal(99, canvas.Pixels[3 * canvas.Stride + 12]);
        Array.Clear(mask.Pixels); mask.MarkModified(); band = new(3, 3, 1, 1);
        compositor.RenderMaskAlone(layer.Id, ref band); Assert.Equal(new RectI(0, 0, 8, 8), band); Assert.Equal(0, canvas.Pixels[0]);
        compositor.Render(); Assert.Equal(0, canvas.Pixels[3]);
    }

    [Fact]
    public void MaskDisplayRespectsTheCompositorScale()
    {
        var mask = new MaskSurface(2, 2, [0, 64, 128, 255]); var output = new Surface(2, 2);
        MaskDisplay.Draw(mask, new() { Size = new(4, 4), Sampling = LayerSampling.Nearest }, output, new(0, 0, 2, 2), .5);
        Assert.Equal(new byte[] { 0, 64, 128, 255 }, Enumerable.Range(0, 4).Select(i => output.Pixels[i * 4]).ToArray());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(.5)]
    public void SoloMaskPlacementChangesRefreshOldAndNewFootprintsAtRenderScale(double scale)
    {
        var doc = EditorDocument.Create(200, 140); var layer = doc.Snapshot.Manifest.Layers[0];
        layer.Transform = new() { Origin = new(120, 90), Size = new(20, 20) };
        layer.MaskPlacement = new() { Origin = new(30, 20), Size = new(10, 8), Sampling = LayerSampling.Nearest };
        layer.MaskFile = ProjectValidator.Uuid(layer.Id) + ".mask.png"; layer.MaskLinked = false;
        var mask = new MaskSurface(10, 8); Array.Fill(mask.Pixels, (byte)255);
        for (int y = 1; y < 7; y++) for (int x = 1; x < 9; x++) mask.Pixels[y * 10 + x] = (byte)(x * 13 + y * 9);
        ((Dictionary<Guid, MaskSurface>)doc.Snapshot.Masks)[layer.Id] = mask;
        var compositor = new CanvasCompositor(doc.Snapshot, scale); var band = new RectI(0, 0, 1, 1);
        var output = compositor.RenderMaskAlone(layer.Id, ref band);
        var edit = TransformEdit.Create(doc.Snapshot, [layer.Id], true)!;
        foreach (var value in new LayerTransform[]
        {
            new() { Origin = new(34, 20), Size = new(10, 8), Sampling = LayerSampling.Nearest },
            new() { Origin = new(34, 20), Size = new(10, 8), FlipX = true, Sampling = LayerSampling.Nearest },
            new() { Origin = new(34, 20), Size = new(10, 8), FlipX = true, Sampling = LayerSampling.Smooth },
            new() { Origin = new(34.25, 22.5), Size = new(14, 12), Rotation = 37, Sampling = LayerSampling.HighQuality },
            new() { Origin = new(-40, -40), Size = new(10, 8), Sampling = LayerSampling.Nearest },
            edit.Value,
        })
        {
            var preview = edit.Preview(value)!;
            compositor.SetSnapshot(preview.Snapshot);
            // The composite dirty band follows the distant pixel layer. Solo display must
            // also cover the independent mask's previous and current placements.
            int x0 = (int)Math.Floor(preview.DirtyBounds.X * scale), y0 = (int)Math.Floor(preview.DirtyBounds.Y * scale);
            band = new(x0, y0, (int)Math.Ceiling((preview.DirtyBounds.X + preview.DirtyBounds.Width) * scale) - x0,
                (int)Math.Ceiling((preview.DirtyBounds.Y + preview.DirtyBounds.Height) * scale) - y0);
            Assert.Same(output, compositor.RenderMaskAlone(layer.Id, ref band));
            var expected = new Surface(output.Width, output.Height);
            MaskDisplay.Draw(mask, value, expected, new(0, 0, output.Width, output.Height), scale);
            Assert.Equal(expected.Pixels, output.Pixels);
            Assert.True(band.Width * band.Height < output.Width * output.Height);
        }
    }

    [Fact]
    public void SoloMaskRetainsPlacementValuesAcrossInPlaceEditsAndKeepsStationaryBandsSmall()
    {
        var doc = EditorDocument.Create(100, 100); var layer = doc.Snapshot.Manifest.Layers[0];
        layer.MaskPlacement = new() { Origin = new(30, 20), Size = new(3, 3), Sampling = LayerSampling.Nearest };
        var mask = new MaskSurface(3, 3, [255, 255, 255, 255, 0, 255, 255, 255, 255]);
        ((Dictionary<Guid, MaskSurface>)doc.Snapshot.Masks)[layer.Id] = mask;
        var compositor = new CanvasCompositor(doc.Snapshot); var band = new RectI(31, 21, 1, 1);
        var output = compositor.RenderMaskAlone(layer.Id, ref band);
        layer.MaskPlacement.Origin = new(40, 20); band = new(0, 0, 0, 0);
        compositor.RenderMaskAlone(layer.Id, ref band);
        Assert.Equal(255, output.Pixels[21 * output.Stride + 31 * 4]);
        Assert.Equal(0, output.Pixels[21 * output.Stride + 41 * 4]);
        Assert.InRange(band.Width * band.Height, 1, 100);
        mask.Pixels[4] = 99; mask.MarkModified(new(1, 1, 1, 1)); band = new(41, 21, 1, 1);
        compositor.RenderMaskAlone(layer.Id, ref band);
        Assert.Equal(new RectI(41, 21, 1, 1), band); Assert.Equal(99, output.Pixels[21 * output.Stride + 41 * 4]);
    }

    [Fact]
    public void MaskCopyCanReplaceAPixelMaskFromAFolderAndKeepsPixelGeometry()
    {
        var doc = EditorDocument.Create(8, 8); var folder = doc.GroupLayers(doc.Snapshot.Manifest.Layers[0].Id)!;
        var mask = new MaskSurface(1, 1, [255]); doc.AddRasterMask(folder.Id, mask);
        var target = doc.ImportImage(new(2, 2), "Target"); var old = new MaskSurface(1, 1, [0]); doc.AddRasterMask(target.Id, old);
        var transform = target.Transform; var image = doc.Snapshot.Images[target.Id];
        Assert.True(doc.CopyMask(folder.Id, target.Id)); Assert.Same(mask, doc.Snapshot.Masks[target.Id]); Assert.Equal(folder.Transform, target.MaskPlacement);
        Assert.Same(image, doc.Snapshot.Images[target.Id]); Assert.Same(transform, target.Transform);
        Assert.True(doc.Undo()); Assert.Same(old, doc.Snapshot.Masks[target.Id]); Assert.True(doc.Redo()); Assert.Same(mask, doc.Snapshot.Masks[target.Id]);
    }
}
