using Xunit;
using System.Linq;

namespace Compositor.Core.Tests;

/// <summary>
/// Mirrors the solo-mask eraser group of the transform probe at Core level: an eraser dab on
/// a mask placed apart lands on the published mask through the stroke grid's placement, which
/// grows with the stroke (8d565f8). The old `Pixels[0]` read pinned the pre-growth 3×3 grid.
/// </summary>
public class MaskGridIndexTests
{
    [Fact]
    public void EraserDabOnPlacedMaskLandsWhereItsPlacementSays()
    {
        var doc = EditorDocument.Create(200, 200);
        var layer = doc.Snapshot.Manifest.Layers[0];
        var mask = new MaskSurface(3, 3, [255, 255, 255, 255, 0, 255, 255, 255, 255]);
        doc.AddRasterMask(layer.Id, mask);
        layer.MaskLinked = false;
        layer.MaskPlacement = new LayerTransform { Origin = new PointD(30, 20), Size = new SizeD(3, 3), Sampling = LayerSampling.Nearest };
        var key = new object();

        // Document (31.5, 21.5) is mask-local (1.5, 1.5) before the growth; a 12-pixel eraser
        // dab centered there must darken the published mask at that same document point.
        Assert.True(doc.BeginStroke(key, layer.Id, new(12, .5, .8, 0, 0, 0), new(31.5, 21.5), onMask: true, erase: true));
        doc.EndStroke(key);

        var published = doc.Snapshot.Masks[layer.Id];
        var placement = doc.Snapshot.Manifest.Layers[0].MaskPlacement!;
        int x = (int)Math.Floor((31.5 - placement.Origin.X) / placement.Size.Width * published.Width);
        int y = (int)Math.Floor((21.5 - placement.Origin.Y) / placement.Size.Height * published.Height);
        Assert.True(published.Pixels[y * published.Width + x] < 255, "the dab did not land at its document point");
    }
}
