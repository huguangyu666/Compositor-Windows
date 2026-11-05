using Xunit;

namespace Compositor.Core.Tests;

public class EffectStrokeStartTests
{
    [Fact]
    public void FirstDabRetainsWarmEffectsAndRendersOnlyItsHaloWithoutChangingHistory()
    {
        var doc = EditorDocument.Create(1024, 1024); var id = doc.Snapshot.Manifest.Layers[0].Id;
        doc.SetLayerEffects(id, new() { Shadow = new() { Blur = 40, Distance = 20 } });
        var original = doc.Snapshot.Images[id]; var pixels = original.Pixels.ToArray();
        var compositor = new CanvasCompositor(doc.Snapshot); compositor.Render();
        var key = new object();
        Assert.True(doc.BeginStroke(key, id, new(12, .5, .8, 200, 30, 50), new(300, 300), erase: true));
        compositor.SetSnapshot(doc.Snapshot);
        var band = compositor.ExpandPixelDirty(new(287, 287, 26, 26));
        Assert.True(band.Width < 200 && band.Height < 200, $"First dab expanded to {band}.");
        long before = GC.GetAllocatedBytesForCurrentThread();
        var frame = compositor.Render(band.X, band.Y, band.X + band.Width, band.Y + band.Height);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 2 * 1024 * 1024);
        Assert.Equal(new CanvasCompositor(doc.Snapshot).Render().Pixels, frame.Pixels);
        Assert.Equal(pixels, original.Pixels); Assert.Equal(0, original.Revision);
        Assert.True(doc.EndStroke(key)); Assert.True(doc.Undo());
        Assert.Same(original, doc.Snapshot.Images[id]); Assert.Equal(pixels, doc.Snapshot.Images[id].Pixels);
        Assert.True(doc.Redo()); Assert.Equal(frame.Pixels, new CanvasCompositor(doc.Snapshot).Render().Pixels);
    }

    [Theory]
    [InlineData("shadow")]
    [InlineData("outer")]
    [InlineData("inner")]
    [InlineData("inner-shadow")]
    [InlineData("outside")]
    [InlineData("inside")]
    [InlineData("overlay")]
    [InlineData("stack")]
    public void CoalescedStartsMatchFullPixelsWithMaskStrideAndTransformedSource(string kind)
    {
        var doc = EditorDocument.Create(512, 400); var layer = doc.Snapshot.Manifest.Layers[0];
        var source = EffectRegionTests.Pattern(201, 151); var original = source.Pixels.ToArray();
        ((Dictionary<Guid, Surface>)doc.Snapshot.Images)[layer.Id] = source;
        layer.Transform = new() { Origin = new(70, 40), Size = new(260, 220), Rotation = 23, FlipX = true, FlipY = true };
        var mask = new MaskSurface(17, 9); new Random(42).NextBytes(mask.Pixels);
        ((Dictionary<Guid, MaskSurface>)doc.Snapshot.Masks)[layer.Id] = mask; layer.MaskFile = "mask.png";
        doc.SetLayerEffects(layer.Id, EffectRegionTests.Effects(kind));
        var compositor = new CanvasCompositor(doc.Snapshot); compositor.Render();
        foreach (var point in new[] { new PointD(150, 130), new PointD(220, 170) })
        {
            var key = new object();
            Assert.True(doc.BeginStroke(key, layer.Id, new(12, .5, .8, 200, 30, 50), point, erase: true));
            Assert.True(doc.EndStroke(key));
        }
        compositor.SetSnapshot(doc.Snapshot);
        var band = compositor.ExpandPixelDirty(new(207, 157, 26, 26));
        var actual = compositor.Render(band.X, band.Y, band.X + band.Width, band.Y + band.Height);
        Assert.Equal(new CanvasCompositor(doc.Snapshot).Render().Pixels, actual.Pixels);
        Assert.Equal(original, source.Pixels); Assert.Equal(0, source.Revision);
        Assert.True(doc.Undo()); Assert.True(doc.Undo()); Assert.Same(source, doc.Snapshot.Images[layer.Id]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CloneAndBlurWorkingCopiesPreserveTheFirstEffectsBand(bool blur)
    {
        var doc = EditorDocument.Create(512, 512); var id = doc.Snapshot.Manifest.Layers[0].Id;
        ((Dictionary<Guid, Surface>)doc.Snapshot.Images)[id] = EffectRegionTests.Pattern(512, 512);
        doc.SetLayerEffects(id, EffectRegionTests.Effects("shadow"));
        var compositor = new CanvasCompositor(doc.Snapshot); compositor.Render(); var key = new object();
        if (blur)
        {
            Assert.True(doc.BeginBlurStroke(id, 3, key)); Assert.True(doc.BlurStampApply(id, new(200, 200), 12, .5));
            doc.EndBlurStroke();
        }
        else
        {
            Assert.True(doc.BeginCloneStroke(id, new(140, 170), new(200, 200), 12, .5, false, key));
            doc.EndCloneStroke();
        }
        compositor.SetSnapshot(doc.Snapshot);
        var band = compositor.ExpandPixelDirty(new(187, 187, 26, 26));
        Assert.True(band.Width < 150 && band.Height < 150);
        Assert.Equal(new CanvasCompositor(doc.Snapshot).Render().Pixels,
            compositor.Render(band.X, band.Y, band.X + band.Width, band.Y + band.Height).Pixels);
    }
}
