using Xunit;
using System.Linq;

namespace Compositor.Core.Tests;

/// <summary>
/// C05c: Canvas Size. The offsets are the macOS anchor rule written out by hand
/// (Document/CanvasSize.swift:69-75: floor of the delta times the anchor row/column over two),
/// so a change in the anchor is visible as a change in these numbers.
/// </summary>
public class CanvasSizeTests
{
    private static EditorDocument Document(int width = 16, int height = 16)
    {
        var document = EditorDocument.Create(width, height);
        var surface = new Surface(4, 4);
        for (int i = 0; i < surface.Pixels.Length; i += 4) { surface.Pixels[i] = 10; surface.Pixels[i + 3] = 255; }
        var record = document.ImportImage(surface, "Block");
        record.Transform = new LayerTransform { Origin = new PointD(0, 0), Size = new SizeD(4, 4) };
        return document;
    }

    private static ProjectLayerRecord Layer(EditorDocument document, string name) =>
        document.Snapshot.Manifest.Layers.Single(layer => layer.Name == name);

    [Theory]
    [InlineData(0, 0, 0)] // top left: the new canvas hangs down-right, content stays
    [InlineData(4, 2, 2)]  // middle: half the four-pixel delta on each side, floor
    [InlineData(8, 4, 4)]  // bottom right: the whole delta lands up-left
    public void CanvasSizeMovesContentByTheAnchorsOffset(int anchor, double expectedX, double expectedY)
    {
        var document = Document();
        int layersBefore = document.Snapshot.Manifest.Layers.Count;
        Assert.True(document.SetCanvasSize(20, 20, anchor));
        Assert.Equal(20, document.Snapshot.Manifest.Width);
        Assert.Equal(expectedX, Layer(document, "Block").Transform.Origin.X);
        Assert.Equal(expectedY, Layer(document, "Block").Transform.Origin.Y);
        // No fill was asked for, so nothing is added to the layer stack.
        Assert.Equal(layersBefore, document.Snapshot.Manifest.Layers.Count);
        Assert.True(document.Undo());
        Assert.Equal(16, document.Snapshot.Manifest.Width);
        Assert.Equal(0, Layer(document, "Block").Transform.Origin.X);
    }

    [Fact]
    public void CanvasSizeShrinkingFromTheMiddleRemovesPixelsFromTopLeft()
    {
        var document = Document();
        Assert.True(document.SetCanvasSize(12, 12, 4));
        // floor((12-16)*1/2) = -2: the box slides up-left, so the crop is taken from top/left.
        Assert.Equal(-2, Layer(document, "Block").Transform.Origin.X);
        Assert.Equal(12, document.Snapshot.Manifest.Height);
    }

    [Fact]
    public void CanvasSizeExtensionFillCoversOnlyTheNewlyGainedArea()
    {
        var document = Document();
        Assert.True(document.SetCanvasSize(24, 24, 4, (255, 0, 0)));
        var extension = document.Snapshot.Manifest.Layers.First();
        Assert.Equal("Canvas Extension", extension.Name);
        Assert.Equal(24, document.Snapshot.Manifest.Width);
        var pixels = document.Snapshot.Images[extension.Id];
        // Anchor middle puts the old 16×16 at (4,4): outside it the color, inside it transparent.
        Assert.Equal(255, pixels.Pixels[0]);
        Assert.Equal(255, pixels.Pixels[3]);
        Assert.Equal(255, pixels.Pixels[(23 * 24 + 23) * 4]);
        Assert.Equal(0, pixels.Pixels[(10 * 24 + 10) * 4 + 3]);
        Assert.Equal(0, pixels.Pixels[(5 * 24 + 5) * 4 + 3]);
        // Bottom of the stack, so it cannot cover the artwork.
        Assert.Equal(0, document.Snapshot.Manifest.Layers.IndexOf(extension));
        Assert.Equal(1, LayerCount(document, "Canvas Extension"));
        Assert.Equal(1, LayerCount(document, "Block"));
    }

    private static int LayerCount(EditorDocument document, string name) =>
        document.Snapshot.Manifest.Layers.Count(layer => layer.Name == name);

    [Fact]
    public void CanvasSizeMovesGuidesAndPlacedMasksWithTheContent()
    {
        var document = Document();
        var block = Layer(document, "Block");
        var opaque = new MaskSurface(4, 4, Enumerable.Repeat((byte)255, 16).ToArray());
        Assert.True(document.AddRasterMask(block.Id, opaque));
        block.MaskPlacement = new LayerTransform { Origin = new PointD(0, 0), Size = new SizeD(4, 4) };
        document.Snapshot.Manifest.Guides =
        [
            new CanvasGuide { Id = Guid.NewGuid(), Axis = GuideAxis.Vertical, Position = 5 },
            new CanvasGuide { Id = Guid.NewGuid(), Axis = GuideAxis.Horizontal, Position = 6 },
        ];

        Assert.True(document.SetCanvasSize(20, 20, 4));
        Assert.Equal(7, document.Snapshot.Manifest.Guides![0].Position);
        Assert.Equal(8, document.Snapshot.Manifest.Guides![1].Position);
        Assert.Equal(2, Layer(document, "Block").MaskPlacement!.Origin.X);
    }

    [Fact]
    public void CanvasSizeRefusesBadInputWithoutTouchingTheDocument()
    {
        var document = Document();
        var undoableBefore = document.CanUndo;
        Assert.False(document.SetCanvasSize(0, 20));
        Assert.False(document.SetCanvasSize(20, 0));
        Assert.False(document.SetCanvasSize(20, 20, anchor: 9));
        Assert.False(document.SetCanvasSize(Limits.MaxSide + 1, 20));
        Assert.Equal(16, document.Snapshot.Manifest.Width);
        Assert.Equal(0, Layer(document, "Block").Transform.Origin.X);
        Assert.Equal(undoableBefore, document.CanUndo);
    }

    [Fact]
    public void CanvasSizeToTheSameNumbersIsANoOp()
    {
        var document = Document();
        var before = ManifestJson.Serialize(document.Snapshot.Manifest);
        Assert.True(document.SetCanvasSize(16, 16, 4));
        // The document already carries history from the fixture, so "no step added" is shown by
        // the manifest not changing at all rather than by an empty undo stack.
        Assert.Equal(before, ManifestJson.Serialize(document.Snapshot.Manifest));
        Assert.Equal(0, Layer(document, "Block").Transform.Origin.X);
    }

    [Fact]
    public void CanvasSizeRefusesWhenAPlacedMaskWouldLeaveTheSupportedRange()
    {
        var document = Document();
        var block = Layer(document, "Block");
        var opaque = new MaskSurface(4, 4, Enumerable.Repeat((byte)255, 16).ToArray());
        Assert.True(document.AddRasterMask(block.Id, opaque));
        // Put the mask's own origin at the supported edge so the shift that growing from the
        // bottom-right anchor asks for (floor((20-16)*2/2) = +4) lands out of range.
        block.MaskPlacement = new LayerTransform { Origin = new PointD(1_000_000, 0), Size = new SizeD(4, 4) };

        Assert.False(document.SetCanvasSize(20, 20, 8));
        // Nothing moved: the pre-pass rejected the change before BeginChange.
        Assert.Equal(16, document.Snapshot.Manifest.Width);
        Assert.Equal(0, Layer(document, "Block").Transform.Origin.X);
    }
}
