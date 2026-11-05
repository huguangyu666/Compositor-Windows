using Xunit;

namespace Compositor.Core.Tests;

public class EffectCacheRegionTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(23, false)]
    [InlineData(23, true)]
    public void ExpandedBandMatchesWholeFrameWithRotatedScaledFlippedEffects(double rotation, bool flip)
    {
        var doc = EditorDocument.Create(420, 320); var layer = doc.Snapshot.Manifest.Layers[0];
        var source = EffectRegionTests.Pattern(201, 151); ((Dictionary<Guid, Surface>)doc.Snapshot.Images)[layer.Id] = source;
        layer.Transform = new() { Origin = new(70, 50), Size = new(260, 220), Rotation = rotation, FlipX = flip, FlipY = flip };
        doc.SetLayerEffects(layer.Id, EffectRegionTests.Effects("stack"));
        var compositor = new CanvasCompositor(doc.Snapshot); compositor.Render();
        var dirty = new RectI(100, 70, 4, 5); EffectRegionTests.Change(source, dirty); source.MarkModified(dirty);
        var band = compositor.ExpandPixelDirty(new(180, 160, 1, 1));
        var actual = compositor.Render(band.X, band.Y, band.X + band.Width, band.Y + band.Height);
        Assert.Equal(new CanvasCompositor(doc.Snapshot).Render().Pixels, actual.Pixels);
        Assert.True(band.Width * band.Height < 420 * 320);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("mask")]
    [InlineData("replacement")]
    [InlineData("settings")]
    [InlineData("disabled")]
    [InlineData("geometry")]
    public void UnprovenInputsFallBackWithoutLeavingOldPixels(string change)
    {
        var doc = EditorDocument.Create(320, 240); var layer = doc.Snapshot.Manifest.Layers[0];
        var source = EffectRegionTests.Pattern(201, 151); ((Dictionary<Guid, Surface>)doc.Snapshot.Images)[layer.Id] = source;
        layer.Transform = new() { Origin = new(40, 40), Size = new(201, 151) };
        doc.SetLayerEffects(layer.Id, EffectRegionTests.Effects("shadow"));
        var mask = new MaskSurface(201, 151); Array.Fill(mask.Pixels, (byte)255);
        ((Dictionary<Guid, MaskSurface>)doc.Snapshot.Masks)[layer.Id] = mask; layer.MaskFile = "mask.png";
        var compositor = new CanvasCompositor(doc.Snapshot); compositor.Render();
        switch (change)
        {
            case "unknown": Array.Clear(source.Pixels); source.MarkModified(); break;
            case "mask": Array.Clear(mask.Pixels); mask.MarkModified(); break;
            case "replacement": ((Dictionary<Guid, Surface>)doc.Snapshot.Images)[layer.Id] = new(201, 151); break;
            case "disabled": doc.SetLayerEffects(layer.Id, null); break;
            case "geometry": layer.Transform = new() { Origin = new(50, 60), Size = new(171, 121), Rotation = 19 }; break;
            default: doc.SetLayerEffects(layer.Id, EffectRegionTests.Effects("outer")); break;
        }
        compositor.SetSnapshot(doc.Snapshot);
        var band = compositor.ExpandPixelDirty(new(100, 100, 1, 1));
        Assert.Equal(new CanvasCompositor(doc.Snapshot).Render().Pixels,
            compositor.Render(band.X, band.Y, band.X + band.Width, band.Y + band.Height).Pixels);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LiveMaskConsumersAndFolderMasksRemainCorrect(bool hidden)
    {
        var doc = EditorDocument.Create(360, 260); var layer = doc.Snapshot.Manifest.Layers[0];
        var source = EffectRegionTests.Pattern(160, 110); ((Dictionary<Guid, Surface>)doc.Snapshot.Images)[layer.Id] = source;
        layer.Transform = new() { Origin = new(50, 40), Size = new(200, 160) }; layer.IsVisible = !hidden;
        doc.SetLayerEffects(layer.Id, EffectRegionTests.Effects("shadow"));
        var folder = new ProjectLayerRecord { Id = Guid.NewGuid(), Name = "Folder", IsGroup = true, IsVisible = true, Opacity = .7,
            MaskFile = "folder.png", Transform = new() { Size = new(360, 260) } };
        var consumer = new ProjectLayerRecord { Id = Guid.NewGuid(), Name = "Consumer", IsVisible = true, ImageFile = "consumer.png",
            Transform = new() { Size = new(360, 260) }, ParentId = folder.Id, MaskSourceId = layer.Id };
        doc.Snapshot.Manifest.Layers.Add(folder); doc.Snapshot.Manifest.Layers.Add(consumer);
        ((Dictionary<Guid, MaskSurface>)doc.Snapshot.Masks)[folder.Id] = new(2, 1, [180, 255]);
        var pixels = new Surface(360, 260); Array.Fill(pixels.Pixels, (byte)255);
        ((Dictionary<Guid, Surface>)doc.Snapshot.Images)[consumer.Id] = pixels;
        var compositor = new CanvasCompositor(doc.Snapshot); compositor.Render();
        var dirty = new RectI(80, 55, 12, 12); EffectRegionTests.Change(source, dirty); source.MarkModified(dirty);
        var band = compositor.ExpandPixelDirty(new(150, 100, 1, 1));
        Assert.Equal(new CanvasCompositor(doc.Snapshot).Render().Pixels,
            compositor.Render(band.X, band.Y, band.X + band.Width, band.Y + band.Height).Pixels);
    }

    [Fact]
    public void SameWindowReusesScratchAcrossDifferentPixelsAndClearsPreviousEffects()
    {
        var source = EffectRegionTests.Pattern(701, 501); var effects = EffectRegionTests.Effects("stack");
        var result = LayerEffectsRenderer.Render(source, null, effects).Result; var scratch = new EffectScratch();
        var dirty = new RectI(350, 250, 8, 8);
        for (int i = 0; i < 4; i++)
        {
            EffectRegionTests.Change(source, dirty);
            LayerEffectsRenderer.Update(result, source, null, effects, dirty, scratch);
            Assert.Equal(LayerEffectsRenderer.Render(source, null, effects).Result.Pixels, result.Pixels);
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        LayerEffectsRenderer.Update(result, source, null, effects, dirty, scratch);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 4096);
    }

    [Fact]
    public void CoalescedStrokeReusesTheEffectOutputInsteadOfAllocatingFullPlanes()
    {
        var doc = EditorDocument.Create(1024, 1024); var layer = doc.Snapshot.Manifest.Layers[0];
        doc.SetLayerEffects(layer.Id, EffectRegionTests.Effects("shadow")); var key = new object();
        doc.BeginStroke(key, layer.Id, new(12, .5, .8, 200, 30, 50), new(200, 200));
        var compositor = new CanvasCompositor(doc.Snapshot); compositor.Render();
        doc.ContinueStroke(key, new(205, 201)); doc.ContinueStroke(key, new(210, 202));
        long before = GC.GetAllocatedBytesForCurrentThread();
        var band = compositor.ExpandPixelDirty(new(190, 185, 40, 40));
        var result = compositor.Render(band.X, band.Y, band.X + band.Width, band.Y + band.Height);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 2 * 1024 * 1024, $"Allocated {allocated} bytes");
        Assert.Equal(new CanvasCompositor(doc.Snapshot).Render().Pixels, result.Pixels);
        doc.EndStroke(key);
    }
}
