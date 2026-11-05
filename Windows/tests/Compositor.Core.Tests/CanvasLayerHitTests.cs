using Xunit;

namespace Compositor.Core.Tests;

public class CanvasLayerHitTests
{
    private static IReadOnlyList<Guid> Hits(ProjectSnapshot source, PointD point)
    {
        return TransformPress.LayersAt(source, point);
    }

    [Fact]
    public void ReturnsAllVisibleHitsInTopToBottomOrderAndPassesThroughHoles()
    {
        var doc = EditorDocument.Create(20, 20); var bottom = doc.Snapshot.Manifest.ActiveLayerId!.Value;
        var image = new Surface(20, 20); for (int i = 3; i < image.Pixels.Length; i += 4) image.Pixels[i] = 255;
        var top = doc.ImportImage(image, "Top").Id;
        Assert.Equal(new[] { top, bottom }, Hits(doc.Snapshot, new(5, 5)));
        image.Pixels[5 * image.Stride + 5 * 4 + 3] = 0;
        Assert.Equal(new[] { bottom }, Hits(doc.Snapshot, new(5, 5)));
        doc.SetVisible(top, false); Assert.Equal(new[] { bottom }, Hits(doc.Snapshot, new(6, 6)));
        Assert.Empty(Hits(doc.Snapshot, new(-10, -10)));
    }

    [Fact]
    public void MasksFolderVisibilityAndRotationUseTheSameRulesAsAutoSelect()
    {
        var doc = EditorDocument.Create(20, 20); var bottom = doc.Snapshot.Manifest.ActiveLayerId!.Value;
        var image = new Surface(10, 10); Array.Fill(image.Pixels, (byte)255); var top = doc.ImportImage(image, "Top").Id;
        var layer = doc.Snapshot.Manifest.Layers.Single(l => l.Id == top); layer.Transform.Rotation = 35;
        Assert.Equal(top, Hits(doc.Snapshot, layer.Transform.Center)[0]);
        doc.AddHideAllMask(top); Assert.Equal(new[] { bottom }, Hits(doc.Snapshot, layer.Transform.Center));
        doc.SetMaskEnabled(top, false); var folder = doc.GroupLayers([top])!; doc.SetVisible(folder.Id, false);
        Assert.Equal(new[] { bottom }, Hits(doc.Snapshot, layer.Transform.Center));
    }
}
