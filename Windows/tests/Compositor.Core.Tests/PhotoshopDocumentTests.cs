using Compositor.Core.Psd;
using Xunit;

namespace Compositor.Core.Tests;

public sealed class PhotoshopDocumentTests
{
    [Fact]
    public void OpensLayeredSourceAtItsOwnSizeWithoutBackgroundOrImportHistory()
    {
        var group = Guid.NewGuid(); var id = Guid.NewGuid();
        var pixels = new Surface(3, 2); pixels.Pixels[0] = 40; pixels.Pixels[3] = 80;
        var mask = new MaskSurface(3, 2); mask.Pixels.AsSpan().Fill(120);
        var source = new PsdDocument { Width = 80, Height = 60, Layers = [
            new() { Id = group, Name = "Folder", IsGroup = true, Offset = PointD.Zero },
            new() { Id = id, ParentId = group, Name = "Original", Pixels = pixels, Mask = mask, Offset = new(5, 7), BlendKey = "mul ", Opacity = .5 },
        ] };
        var doc = EditorDocument.FromPhotoshop(source);
        Assert.Equal(80, doc.Snapshot.Manifest.Width); Assert.Equal(60, doc.Snapshot.Manifest.Height); Assert.Equal(2, doc.LayerCount);
        var layer = doc.Snapshot.Manifest.Layers.Single(l => l.Id == id);
        Assert.Equal(group, layer.ParentId); Assert.Equal(LayerBlendMode.Multiply, layer.BlendMode); Assert.Equal(.5, layer.Opacity);
        Assert.Same(pixels, doc.Snapshot.Images[id]); Assert.Equal(80, doc.Snapshot.Images[id].Pixels[3]); Assert.Same(mask, doc.Snapshot.Masks[id]);
        Assert.Equal(new PointD(5, 7), layer.Transform.Origin); Assert.True(doc.IsDirty); Assert.False(doc.CanUndo);
        doc.RenameLayer(id, "Changed"); Assert.True(doc.Undo()); Assert.False(doc.CanUndo);
        Assert.Equal("Original", doc.Snapshot.Manifest.Layers.Single(l => l.Id == id).Name);
        ProjectValidator.Validate(doc.Snapshot.Manifest);
    }
}
