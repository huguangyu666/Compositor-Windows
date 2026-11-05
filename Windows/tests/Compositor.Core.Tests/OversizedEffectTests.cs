using Xunit;

namespace Compositor.Core.Tests;

public class OversizedEffectTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LargeEffectsUseBoundedWindowsAndMatchIndependentFullReference(bool liveMask)
    {
        var doc = EditorDocument.Create(320, 240); var layer = doc.Snapshot.Manifest.Layers[0];
        var source = new Surface(4100, 4090);
        for (int y = 0; y < source.Height; y++)
        for (int x = 0; x < source.Width; x++)
        {
            int p = y * source.Stride + x * 4; byte a = (byte)((x * 7 + y * 13) % 256);
            source.Pixels[p] = (byte)(a / 3); source.Pixels[p + 1] = (byte)(a / 2); source.Pixels[p + 2] = a; source.Pixels[p + 3] = a;
        }
        ((Dictionary<Guid, Surface>)doc.Snapshot.Images)[layer.Id] = source;
        layer.Transform = new() { Origin = new(-1890, -1920), Size = new(4100, 4090), Rotation = 17, FlipX = true };
        layer.IsVisible = !liveMask;
        doc.SetLayerEffects(layer.Id, EffectRegionTests.Effects("shadow"));
        var mask = new MaskSurface(17, 13); new Random(12).NextBytes(mask.Pixels);
        ((Dictionary<Guid, MaskSurface>)doc.Snapshot.Masks)[layer.Id] = mask; layer.MaskFile = "mask.png";
        if (liveMask)
        {
            var consumer = new ProjectLayerRecord { Id = Guid.NewGuid(), Name = "Consumer", IsVisible = true,
                ImageFile = "consumer.png", Transform = new() { Size = new(320, 240) }, MaskSourceId = layer.Id };
            doc.Snapshot.Manifest.Layers.Add(consumer);
            var pixels = new Surface(320, 240); Array.Fill(pixels.Pixels, (byte)255);
            ((Dictionary<Guid, Surface>)doc.Snapshot.Images)[consumer.Id] = pixels;
        }
        var compositor = new CanvasCompositor(doc.Snapshot); compositor.Render();
        var dirty = new RectI(2050, 2040, 6, 7);
        ChangeAndRender(); // warm the small workspace after the initial larger request
        long before = GC.GetAllocatedBytesForCurrentThread();
        ChangeAndRender();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 4 * 1024 * 1024, $"A small 4K edit allocated {allocated} bytes.");
        AssertReference();
        compositor.Render(); // fill the placed cache after a narrower pixel update
        before = GC.GetAllocatedBytesForCurrentThread();
        compositor.Render(); // unchanged placed pixels must bypass the effect renderer
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 4096);
        var uncached = new CanvasCompositor(doc.Snapshot, cacheLayers: false);
        Assert.Equal(compositor.Render(0, 0, 0, 0).Pixels, uncached.Render().Pixels);
        before = GC.GetAllocatedBytesForCurrentThread(); uncached.Render();
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 4096);

        // Unknown edits, mask changes, settings, movement and replacement must not reuse stale windows.
        source.Pixels[dirty.Y * source.Stride + dirty.X * 4 + 3] = 255; source.MarkModified();
        RenderFallback(); AssertReference();
        Array.Fill(mask.Pixels, (byte)255); mask.MarkModified(); RenderFallback(); AssertReference();
        doc.SetLayerEffects(layer.Id, EffectRegionTests.Effects("outer")); compositor.SetSnapshot(doc.Snapshot);
        RenderFallback(); AssertReference();
        layer.Transform.Origin = new(-1883, -1924); compositor.SetSnapshot(doc.Snapshot);
        RenderFallback(); AssertReference();
        layer.Transform.Rotation = 180; compositor.SetSnapshot(doc.Snapshot);
        RenderFallback(); AssertReference();
        var replacement = new Surface(source.Width, source.Height);
        ((Dictionary<Guid, Surface>)doc.Snapshot.Images)[layer.Id] = replacement;
        compositor.SetSnapshot(new ProjectSnapshot { Manifest = doc.Snapshot.Manifest, Images = doc.Snapshot.Images, Masks = doc.Snapshot.Masks });
        RenderFallback(); AssertReference();

        void ChangeAndRender()
        {
            EffectRegionTests.Change(source, dirty); source.MarkModified(dirty);
            var band = compositor.ExpandPixelDirty(new(140, 110, 8, 9));
            compositor.Render(band.X, band.Y, band.X + band.Width, band.Y + band.Height);
        }
        void RenderFallback()
        {
            var band = compositor.ExpandPixelDirty(new(140, 110, 8, 9));
            compositor.Render(band.X, band.Y, band.X + band.Width, band.Y + band.Height);
        }
        void AssertReference()
        {
            var manifest = ManifestJson.Deserialize(ManifestJson.Serialize(doc.Snapshot.Manifest));
            var referenceLayer = manifest.Layers.Single(l => l.Id == layer.Id);
            var effect = LayerEffectsRenderer.Render(doc.Snapshot.Images[layer.Id], mask, LayerEffectsRenderer.Visible(layer.Effects)!);
            var center = layer.Transform.Center;
            double width = layer.Transform.Size.Width * effect.Result.Width / source.Width;
            double height = layer.Transform.Size.Height * effect.Result.Height / source.Height;
            referenceLayer.Transform = new() { Origin = new(center.X - width / 2, center.Y - height / 2), Size = new(width, height),
                Rotation = layer.Transform.Rotation, FlipX = layer.Transform.FlipX, FlipY = layer.Transform.FlipY,
                Sampling = layer.Transform.Sampling };
            referenceLayer.Effects = null; referenceLayer.MaskFile = null;
            var images = new Dictionary<Guid, Surface>(doc.Snapshot.Images) { [layer.Id] = effect.Result };
            var masks = new Dictionary<Guid, MaskSurface>(doc.Snapshot.Masks); masks.Remove(layer.Id);
            var expected = new CanvasCompositor(new ProjectSnapshot { Manifest = manifest, Images = images, Masks = masks }).Render();
            Assert.Equal(expected.Pixels, compositor.Render(0, 0, 0, 0).Pixels);
        }
    }
}
