using Xunit;

namespace Compositor.Core.Tests;

public class LayerTransparencyTests
{
    [Theory]
    [InlineData(0, false, false)]
    [InlineData(37, true, false)]
    [InlineData(90, false, true)]
    public void TrimmingKeepsVisiblePixelCoordinatesAndOneUndo(double rotation, bool flipX, bool flipY)
    {
        var document = EditorDocument.Create(80, 60);
        var image = new Surface(8, 6);
        image.Pixels[(2 * 8 + 3) * 4 + 3] = 255;
        image.Pixels[(3 * 8 + 4) * 4 + 3] = 1;
        var layer = document.ImportImage(image, "Star");
        layer.Transform = new() { Origin = new(20, 10), Size = new(40, 30), Rotation = rotation, FlipX = flipX, FlipY = flipY };
        var original = layer.Transform;
        var center = BrushEngine.PixelToDocument(original, 8, 6, new(3.5, 2.5));
        Assert.True(document.TrimLayerTransparency(layer.Id));
        var trimmed = document.Snapshot.Images[layer.Id];
        Assert.Equal((2, 2), (trimmed.Width, trimmed.Height));
        var placed = document.Snapshot.Manifest.Layers.Single(l => l.Id == layer.Id).Transform;
        var after = BrushEngine.PixelToDocument(placed, 2, 2, new(.5, .5));
        Assert.Equal(center.X, after.X, 8); Assert.Equal(center.Y, after.Y, 8);
        Assert.True(document.Undo()); Assert.Same(image, document.Snapshot.Images[layer.Id]);
        Assert.True(document.Redo()); Assert.Equal((2, 2), (document.Snapshot.Images[layer.Id].Width, document.Snapshot.Images[layer.Id].Height));
    }

    [Fact]
    public void AttachedMaskIsCroppedWithTheSourceAndTheCompositeIsUnchanged()
    {
        var document = EditorDocument.Create(8, 6);
        var layer = document.Snapshot.Manifest.Layers[0];
        var mask = new MaskSurface(8, 6); mask.Pixels[2 * 8 + 3] = 255; mask.Pixels[3 * 8 + 4] = 1;
        Assert.True(document.AddRasterMask(layer.Id, mask));
        var before = new CanvasCompositor(document.Snapshot).Render().Pixels;
        Assert.True(document.TrimLayerTransparency(layer.Id));
        Assert.Equal((2, 2), (document.Snapshot.Images[layer.Id].Width, document.Snapshot.Images[layer.Id].Height));
        Assert.Equal((2, 2), (document.Snapshot.Masks[layer.Id].Width, document.Snapshot.Masks[layer.Id].Height));
        Assert.Equal(before, new CanvasCompositor(document.Snapshot).Render().Pixels);
        Assert.True(document.Undo()); Assert.Same(mask, document.Snapshot.Masks[layer.Id]);
        Assert.True(document.Redo()); Assert.Equal(before, new CanvasCompositor(document.Snapshot).Render().Pixels);
    }

    [Fact]
    public void EmptyOrAlreadyTightLayersDoNotAddHistory()
    {
        var document = EditorDocument.Create(8, 6);
        var id = document.Snapshot.Manifest.Layers[0].Id;
        Assert.False(document.TrimLayerTransparency(id)); Assert.False(document.CanUndo);
        Array.Clear(document.Snapshot.Images[id].Pixels);
        Assert.False(document.TrimLayerTransparency(id)); Assert.False(document.CanUndo);
    }
}
