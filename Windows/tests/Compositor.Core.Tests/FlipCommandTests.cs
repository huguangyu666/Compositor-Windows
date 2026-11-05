using Xunit;
using System.Linq;

namespace Compositor.Core.Tests;

/// <summary>
/// C05b: the flip commands. The macOS original mirrors placements analytically
/// (Document/LayerFlip.swift:6-17), so the expected numbers here are the mirror arithmetic
/// written out by hand, not values read back from the implementation.
/// </summary>
public class FlipCommandTests
{
    private static Surface Solid(int width, int height, byte r, byte g, byte b, byte a = 255)
    {
        var surface = new Surface(width, height);
        for (int i = 0; i < surface.Pixels.Length; i += 4)
        {
            surface.Pixels[i] = r; surface.Pixels[i + 1] = g; surface.Pixels[i + 2] = b; surface.Pixels[i + 3] = a;
        }
        return surface;
    }

    private static EditorDocument Document(params (string Name, int X, int Y, int Width, int Height)[] layers)
    {
        var document = EditorDocument.Create(64, 48);
        foreach (var (name, x, y, width, height) in layers)
        {
            var record = document.ImportImage(Solid(width, height, 10, 20, 30), name);
            record.Transform = new LayerTransform { Origin = new PointD(x, y), Size = new SizeD(width, height) };
        }
        return document;
    }

    private static ProjectLayerRecord Layer(EditorDocument document, string name) =>
        document.Snapshot.Manifest.Layers.Single(layer => layer.Name == name);

    [Fact]
    public void MirroredFlipsThePlacementAcrossTheAxisAndNegatesRotation()
    {
        var transform = new LayerTransform { Origin = new PointD(10, 4), Size = new SizeD(20, 8), Rotation = 12 };
        var horizontally = transform.Mirrored(horizontally: true, axis: 32);
        // The middle 20 crosses to 44, so a width-20 box starts at 34; the y row is untouched.
        Assert.Equal(34, horizontally.Origin.X);
        Assert.Equal(4, horizontally.Origin.Y);
        Assert.True(horizontally.FlipX);
        Assert.False(horizontally.FlipY);
        Assert.Equal(-12, horizontally.Rotation);
        Assert.Equal(new SizeD(20, 8), horizontally.Size);

        var vertically = transform.Mirrored(horizontally: false, axis: 24);
        Assert.Equal(10, vertically.Origin.X);
        Assert.Equal(36, vertically.Origin.Y); // middle 8 crosses to 40, minus half height
        Assert.False(vertically.FlipX);
        Assert.True(vertically.FlipY);

        var twice = horizontally.Mirrored(horizontally: true, axis: 32);
        Assert.Equal(transform.Origin, twice.Origin);
        Assert.Equal(transform.FlipX, twice.FlipX);
        Assert.Equal(transform.Rotation, twice.Rotation);
    }

    [Fact]
    public void FlipLayersMirrorsOneLayerAboutItsOwnMiddle()
    {
        var document = Document(("Left", 2, 2, 8, 8));
        var left = Layer(document, "Left");
        document.SetActiveLayer(left.Id);

        Assert.True(document.FlipLayers([left.Id], horizontally: true));
        Assert.True(Layer(document, "Left").Transform.FlipX);
        // Mirrored about its own center, the box cannot move: only the picture inside it turns over.
        Assert.Equal(2, Layer(document, "Left").Transform.Origin.X);
        Assert.True(document.Undo());
        Assert.False(Layer(document, "Left").Transform.FlipX);
    }

    [Fact]
    public void FlipLayersMirrorsSeveralAboutTheBoxAroundThem()
    {
        var document = Document(("Near", 0, 0, 8, 8), ("Far", 20, 0, 8, 8));
        var near = Layer(document, "Near"); var far = Layer(document, "Far");

        Assert.True(document.FlipLayers([near.Id, far.Id], horizontally: true));
        // Box 0..28 has its middle at 14: [0,8] mirrors onto [20,28] and [20,28] onto [0,8].
        Assert.Equal(20, Layer(document, "Near").Transform.Origin.X);
        Assert.Equal(0, Layer(document, "Far").Transform.Origin.X);
        Assert.True(document.Undo());
        Assert.Equal(0, Layer(document, "Near").Transform.Origin.X);
        Assert.Equal(20, Layer(document, "Far").Transform.Origin.X);
    }

    [Fact]
    public void FlipLayersMovesALinkedMaskButNotAnUnlinkedOne()
    {
        var document = Document(("Near", 0, 0, 8, 8), ("Far", 20, 0, 8, 8));
        var near = Layer(document, "Near"); var far = Layer(document, "Far");
        var opaque = new MaskSurface(8, 8, Enumerable.Repeat((byte)255, 64).ToArray());
        Assert.True(document.AddRasterMask(near.Id, opaque));
        Assert.True(document.AddRasterMask(far.Id, opaque));
        near.MaskPlacement = new LayerTransform { Origin = new PointD(0, 0), Size = new SizeD(8, 8) };
        far.MaskPlacement = new LayerTransform { Origin = new PointD(20, 0), Size = new SizeD(8, 8) };
        near.MaskLinked = false;
        far.MaskLinked = true;

        // Flipping the pair about the box middle (14) moves both layers; only the linked mask follows.
        Assert.True(document.FlipLayers([near.Id, far.Id], horizontally: true));
        Assert.Equal(20, Layer(document, "Near").Transform.Origin.X);
        Assert.Equal(0, Layer(document, "Near").MaskPlacement!.Origin.X);
        Assert.Equal(0, Layer(document, "Far").Transform.Origin.X);
        Assert.Equal(0, Layer(document, "Far").MaskPlacement!.Origin.X);
        Assert.True(Layer(document, "Near").Transform.FlipX);
        Assert.True(document.Undo());
        Assert.Equal(0, Layer(document, "Near").Transform.Origin.X);
        Assert.False(Layer(document, "Near").Transform.FlipX);
    }

    [Fact]
    public void FlipCanvasMirrorsLayersMasksSelectionAndGuidesInOneStep()
    {
        var document = Document(("Left", 2, 2, 8, 8));
        var left = Layer(document, "Left");
        Assert.True(document.AddRasterMask(left.Id, new MaskSurface(8, 8, Enumerable.Repeat((byte)255, 64).ToArray())));
        left.MaskPlacement = new LayerTransform { Origin = new PointD(2, 2), Size = new SizeD(8, 8) };
        document.SetSelection(Selection.Rectangle(64, 48, 0, 0, 8, 8));
        document.Snapshot.Manifest.Guides =
        [
            new CanvasGuide { Id = Guid.NewGuid(), Axis = GuideAxis.Vertical, Position = 5 },
            new CanvasGuide { Id = Guid.NewGuid(), Axis = GuideAxis.Horizontal, Position = 7 },
        ];
        var before = document.Snapshot.Manifest.Guides[1].Position;

        Assert.True(document.FlipCanvas(horizontally: true));
        Assert.Equal(54, Layer(document, "Left").Transform.Origin.X); // 64 - 2 - 8
        Assert.Equal(54, Layer(document, "Left").MaskPlacement!.Origin.X); // unlinked or not, the canvas carries it
        Assert.Equal(2, Layer(document, "Left").Transform.Origin.Y); // a horizontal flip leaves y alone
        var guides = document.Snapshot.Manifest.Guides!;
        Assert.Equal(59, guides[0].Position); // vertical guide mirrors: 64 - 5
        Assert.Equal(before, guides[1].Position); // a horizontal guide stays put
        Assert.Equal(0, document.ActiveSelection!.CoverageAt(4, 4));
        Assert.Equal(255, document.ActiveSelection.CoverageAt(56, 4));

        Assert.True(document.Undo());
        Assert.Equal(2, Layer(document, "Left").Transform.Origin.X);
        Assert.Equal(5, document.Snapshot.Manifest.Guides![0].Position);
        Assert.Equal(255, document.ActiveSelection!.CoverageAt(4, 4));
    }

    [Fact]
    public void SelectionMirroredReversesColumnsAndKeepsFeather()
    {
        var coverage = new MaskSurface(8, 8);
        for (int y = 0; y < 8; y++) coverage.Pixels[y * 8 + 1] = 200; // a left-hand stripe
        var selection = Selection.FromCoverage(coverage);

        var mirrored = selection.Mirrored(horizontally: true);
        Assert.Equal(0, mirrored.CoverageAt(1, 3));
        Assert.Equal(200, mirrored.CoverageAt(6, 3));
        Assert.Equal(selection.Feather, mirrored.Feather);

        var flipped = selection.Mirrored(horizontally: false);
        Assert.Equal(200, flipped.CoverageAt(1, 4)); // rows swap, columns do not
        Assert.Equal(0, flipped.CoverageAt(6, 4));
    }
}
