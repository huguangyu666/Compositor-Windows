using Xunit;

namespace Compositor.Core.Tests;

public class EffectMaskRegionTests
{
    [Fact]
    public void OversizedMaskedEffectWindowMatchesIndependentFullEffectPixels()
    {
        var doc = EditorDocument.Create(320, 240); var layer = doc.Snapshot.Manifest.Layers[0];
        var source = new Surface(4100, 4090); Array.Fill(source.Pixels, (byte)255);
        ((Dictionary<Guid, Surface>)doc.Snapshot.Images)[layer.Id] = source;
        layer.Transform = new() { Origin = new(-1890, -1920), Size = new(4100, 4090), Rotation = 17, FlipX = true };
        var mask = new MaskSurface(4100, 4090); Array.Fill(mask.Pixels, (byte)255); doc.AddRasterMask(layer.Id, mask);
        var effects = EffectRegionTests.Effects("shadow"); doc.SetLayerEffects(layer.Id, effects);
        var compositor = new CanvasCompositor(doc.Snapshot); compositor.Render();
        var rect = new RectI(2050, 2040, 6, 7);
        for (int y = rect.Y; y < rect.Y + rect.Height; y++) Array.Clear(mask.Pixels, y * mask.Width + rect.X, rect.Width);
        mask.MarkModified(rect);
        var band = compositor.ExpandPixelDirty(new(140, 110, 8, 9));
        var actual = compositor.Render(band.X, band.Y, band.X + band.Width, band.Y + band.Height);
        var full = LayerEffectsRenderer.Render(source, mask, effects).Result;
        var manifest = ManifestJson.Deserialize(ManifestJson.Serialize(doc.Snapshot.Manifest)); var referenceLayer = manifest.Layers[0];
        var center = layer.Transform.Center;
        var size = new SizeD(layer.Transform.Size.Width * full.Width / source.Width, layer.Transform.Size.Height * full.Height / source.Height);
        referenceLayer.Transform = new() { Origin = new(center.X - size.Width / 2, center.Y - size.Height / 2), Size = size,
            Rotation = layer.Transform.Rotation, FlipX = layer.Transform.FlipX, Sampling = layer.Transform.Sampling };
        referenceLayer.Effects = null; referenceLayer.MaskFile = null;
        var expected = new CanvasCompositor(new ProjectSnapshot { Manifest = manifest,
            Images = new Dictionary<Guid, Surface> { [layer.Id] = full }, Masks = new Dictionary<Guid, MaskSurface>() }).Render();
        Assert.Equal(expected.Pixels, actual.Pixels);
    }

    [Theory]
    [InlineData("shadow", false)]
    [InlineData("outer", false)]
    [InlineData("inner", false)]
    [InlineData("inner-shadow", false)]
    [InlineData("outside", false)]
    [InlineData("inside", false)]
    [InlineData("overlay", false)]
    [InlineData("stack", false)]
    [InlineData("shadow", true)]
    [InlineData("stack", true)]
    public void MaskEditsAndCoalescedSourceEditsMatchFreshFullFrame(string kind, bool apart)
    {
        var doc = EditorDocument.Create(720, 560); var layer = doc.Snapshot.Manifest.Layers[0];
        var image = EffectRegionTests.Pattern(301, 211); ((Dictionary<Guid, Surface>)doc.Snapshot.Images)[layer.Id] = image;
        layer.Transform = new() { Origin = new(140, 120), Size = new(330, 240), Rotation = 23, FlipX = true };
        var mask = new MaskSurface(101, 79); new Random(3702).NextBytes(mask.Pixels); doc.AddRasterMask(layer.Id, mask);
        if (apart) layer.MaskPlacement = new() { Origin = new(170, 100), Size = new(280, 290), Rotation = -17, FlipY = true };
        doc.SetLayerEffects(layer.Id, EffectRegionTests.Effects(kind));
        var compositor = new CanvasCompositor(doc.Snapshot); compositor.Render();
        foreach (var rect in new[] { new RectI(48, 38, 3, 4), new(0, 0, 2, 3), new(99, 76, 2, 3) })
        {
            for (int y = rect.Y; y < rect.Y + rect.Height; y++)
            for (int x = rect.X; x < rect.X + rect.Width; x++) mask.Pixels[y * mask.Width + x] ^= 255;
            mask.MarkModified(rect);
            if (rect.X == 0) { var pixels = new RectI(100, 75, 3, 4); EffectRegionTests.Change(image, pixels); image.MarkModified(pixels); }
            var band = compositor.ExpandPixelDirty(new(320, 250, 1, 1));
            Assert.Equal(new CanvasCompositor(doc.Snapshot).Render().Pixels,
                compositor.Render(band.X, band.Y, band.X + band.Width, band.Y + band.Height).Pixels);
            Assert.True(band.Width * band.Height < 720 * 560);
        }
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("replacement")]
    [InlineData("geometry")]
    [InlineData("removed")]
    [InlineData("disabled")]
    public void UnprovenMaskInputsStillUseConservativeFallback(string change)
    {
        var doc = EditorDocument.Create(320, 240); var layer = doc.Snapshot.Manifest.Layers[0];
        var image = EffectRegionTests.Pattern(201, 151); ((Dictionary<Guid, Surface>)doc.Snapshot.Images)[layer.Id] = image;
        layer.Transform = new() { Origin = new(40, 40), Size = new(201, 151) };
        var mask = new MaskSurface(101, 79); Array.Fill(mask.Pixels, (byte)255); doc.AddRasterMask(layer.Id, mask);
        doc.SetLayerEffects(layer.Id, EffectRegionTests.Effects("shadow"));
        var compositor = new CanvasCompositor(doc.Snapshot); compositor.Render();
        switch (change)
        {
            case "unknown": Array.Clear(mask.Pixels); mask.MarkModified(); break;
            case "replacement": doc.ReplaceLayerMask(layer.Id, new(101, 79)); break;
            case "geometry": layer.MaskPlacement = new() { Origin = new(100, 70), Size = new(101, 79), Rotation = 17 }; break;
            case "removed": doc.RemoveMask(layer.Id); break;
            default: doc.SetMaskEnabled(layer.Id, false); break;
        }
        compositor.SetSnapshot(doc.Snapshot); var band = compositor.ExpandPixelDirty(new(100, 100, 1, 1));
        Assert.Equal(new CanvasCompositor(doc.Snapshot).Render().Pixels,
            compositor.Render(band.X, band.Y, band.X + band.Width, band.Y + band.Height).Pixels);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KnownMaskEditsStillRefreshLiveMaskConsumers(bool hidden)
    {
        var doc = EditorDocument.Create(320, 240); var layer = doc.Snapshot.Manifest.Layers[0];
        ((Dictionary<Guid, Surface>)doc.Snapshot.Images)[layer.Id] = EffectRegionTests.Pattern(320, 240);
        var mask = new MaskSurface(320, 240); Array.Fill(mask.Pixels, (byte)255); doc.AddRasterMask(layer.Id, mask);
        doc.SetLayerEffects(layer.Id, EffectRegionTests.Effects("shadow")); layer.IsVisible = !hidden;
        var consumer = new ProjectLayerRecord { Id = Guid.NewGuid(), Name = "Consumer", ImageFile = "consumer.png", IsVisible = true,
            MaskSourceId = layer.Id, Transform = new() { Size = new(320, 240) } };
        doc.Snapshot.Manifest.Layers.Add(consumer); var pixels = new Surface(320, 240); Array.Fill(pixels.Pixels, (byte)255);
        ((Dictionary<Guid, Surface>)doc.Snapshot.Images)[consumer.Id] = pixels;
        var compositor = new CanvasCompositor(doc.Snapshot); compositor.Render();
        for (int y = 70; y < 74; y++) Array.Clear(mask.Pixels, y * 320 + 100, 4);
        mask.MarkModified(new(100, 70, 4, 4));
        var band = compositor.ExpandPixelDirty(new(100, 70, 4, 4));
        Assert.Equal(new CanvasCompositor(doc.Snapshot).Render().Pixels,
            compositor.Render(band.X, band.Y, band.X + band.Width, band.Y + band.Height).Pixels);
    }

    [Fact]
    public void MaskPaintingUsesTheMasksOwnPlacement()
    {
        var doc = EditorDocument.Create(100, 100); var layer = doc.Snapshot.Manifest.Layers[0];
        var mask = new MaskSurface(32, 32); Array.Fill(mask.Pixels, (byte)255); doc.AddRasterMask(layer.Id, mask);
        layer.MaskPlacement = new() { Origin = new(40, 20), Size = new(32, 32) };
        var key = new object(); doc.BeginStroke(key, layer.Id, new(2, 1, 1, 0, 0, 0), new(50, 30), onMask: true);
        // Document (50,30) is mask-local (10,10) under the mask's own placement. The stroke grid
        // grows to the mask's full reach and is published with a new placement, so index the
        // published mask by that placement rather than the original 32-pixel stride.
        var working = doc.Snapshot.Masks[layer.Id];
        var placement = doc.Snapshot.Manifest.Layers[0].MaskPlacement!;
        int gridX = (int)((50 - placement.Origin.X) / placement.Size.Width * working.Width);
        int gridY = (int)((30 - placement.Origin.Y) / placement.Size.Height * working.Height);
        Assert.True(working.Pixels[gridY * working.Width + gridX] < 255);
        Assert.Equal(255, mask.Pixels[10 * 32 + 10]); // the mask this stroke forked from stays intact
        doc.EndStroke(key);
    }

    [Fact]
    public void MaskStrokeFirstDabAndMovingDabKeepARegionalEffectFrame()
    {
        var doc = EditorDocument.Create(1024, 1024); var layer = doc.Snapshot.Manifest.Layers[0];
        ((Dictionary<Guid, Surface>)doc.Snapshot.Images)[layer.Id] = EffectRegionTests.Pattern(1024, 1024);
        var original = new MaskSurface(1024, 1024); Array.Fill(original.Pixels, (byte)255);
        doc.AddRasterMask(layer.Id, original); doc.SetLayerEffects(layer.Id, EffectRegionTests.Effects("shadow"));
        var compositor = new CanvasCompositor(doc.Snapshot); compositor.Render();
        var key = new object();
        Assert.True(doc.BeginStroke(key, layer.Id, new(12, .5, .8, 0, 0, 0), new(300, 300), onMask: true));
        compositor.SetSnapshot(doc.Snapshot);
        var band = compositor.ExpandPixelDirty(new(287, 287, 26, 26));
        Assert.True(band.Width * band.Height < 1024 * 1024 / 4, $"First mask dab expanded to {band}.");
        Assert.Equal(new CanvasCompositor(doc.Snapshot).Render().Pixels,
            compositor.Render(band.X, band.Y, band.X + band.Width, band.Y + band.Height).Pixels);
        doc.ContinueStroke(key, new(306, 300));
        long before = GC.GetAllocatedBytesForCurrentThread();
        band = compositor.ExpandPixelDirty(new(287, 287, 32, 26));
        var actual = compositor.Render(band.X, band.Y, band.X + band.Width, band.Y + band.Height);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(bytes < 2 * 1024 * 1024, $"Moving mask dab allocated {bytes} bytes.");
        Assert.Equal(new CanvasCompositor(doc.Snapshot).Render().Pixels, actual.Pixels);
        doc.EndStroke(key); Assert.True(doc.Undo()); Assert.Same(original, doc.Snapshot.Masks[layer.Id]);
        Assert.All(original.Pixels, value => Assert.Equal(255, value));
        Assert.True(doc.Redo());
    }
}
