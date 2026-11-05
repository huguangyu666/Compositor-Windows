using Xunit;

namespace Compositor.Core.Tests;

/// <summary>Tests for crop/canvas bounds, selections and the brush engine.</summary>
public class EditingToolTests
{
    private static Surface Solid(int width, int height, byte r, byte g, byte b, byte a = 255)
    {
        var surface = new Surface(width, height);
        for (int row = 0; row < height; row++)
        {
            for (int column = 0; column < width; column++)
            {
                int index = row * surface.Stride + column * 4;
                surface.Pixels[index] = r;
                surface.Pixels[index + 1] = g;
                surface.Pixels[index + 2] = b;
                surface.Pixels[index + 3] = a;
            }
        }
        return surface;
    }

    private static byte Pixel(Surface surface, int x, int y, int channel) =>
        surface.Pixels[y * surface.Stride + x * 4 + channel];

    private static EditorDocument DocumentWithLayer(int canvas = 16, int image = 16)
    {
        var document = EditorDocument.Create(canvas, canvas);
        var layer = document.Snapshot.Manifest.Layers[0];
        document.ReplaceLayerImage(layer.Id, Solid(image, image, 0, 0, 0));
        return document;
    }

    // --- Crop / canvas bounds ---

    [Fact]
    public void CropShiftsLayersAndShrinksTheCanvas()
    {
        var document = DocumentWithLayer();
        var layer = document.Snapshot.Manifest.Layers[0];

        Assert.True(document.SetCanvasBounds(4, 6, 8, 8));
        Assert.Equal(8, document.Snapshot.Manifest.Width);
        Assert.Equal(8, document.Snapshot.Manifest.Height);
        // The layer's pixels stay put; its origin follows the canvas.
        Assert.Equal(-4, layer.Transform.Origin.X);
        Assert.Equal(-6, layer.Transform.Origin.Y);

        var canvas = new CanvasCompositor(document.Snapshot).Render();
        Assert.Equal(8, canvas.Width);
        Assert.Equal(0, Pixel(canvas, 0, 0, 0)); // still the layer's black pixels
        Assert.Equal(255, Pixel(canvas, 0, 0, 3));

        document.Undo();
        Assert.Equal(16, document.Snapshot.Manifest.Width);
        Assert.Equal(0, document.Snapshot.Manifest.Layers[0].Transform.Origin.X);
    }

    [Fact]
    public void CropShiftsGuidesAndMaskPlacement()
    {
        var document = DocumentWithLayer();
        var layer = document.Snapshot.Manifest.Layers[0];
        document.AddRasterMask(layer.Id, MaskSurface.Solid(revealing: true));
        layer.MaskLinked = false;
        layer.MaskPlacement = new LayerTransform { Origin = new PointD(10, 10), Size = new SizeD(4, 4) };
        document.Snapshot.Manifest.Guides =
        [
            new CanvasGuide { Id = Guid.NewGuid(), Axis = GuideAxis.Horizontal, Position = 12 },
            new CanvasGuide { Id = Guid.NewGuid(), Axis = GuideAxis.Vertical, Position = 10 },
        ];

        document.SetCanvasBounds(6, 4, 8, 8);

        Assert.Equal(4, layer.MaskPlacement!.Origin.X); // 10 − x offset 6
        Assert.Equal(6, layer.MaskPlacement!.Origin.Y); // 10 − y offset 4
        Assert.Equal(8, document.Snapshot.Manifest.Guides![0].Position); // horizontal follows y
        Assert.Equal(4, document.Snapshot.Manifest.Guides![1].Position); // vertical follows x
    }

    // --- Selections ---

    [Fact]
    public void RectangularSelectionCoversItsRectangle()
    {
        var selection = Selection.Rectangle(8, 8, 2, 3, 4, 2);

        Assert.Equal(255, selection.CoverageAt(2, 3));
        Assert.Equal(255, selection.CoverageAt(5, 4));
        Assert.Equal(0, selection.CoverageAt(1, 3)); // outside on the left
        Assert.Equal(0, selection.CoverageAt(6, 3)); // outside on the right
        Assert.Equal(2, selection.Bounds.X);
        Assert.Equal(3, selection.Bounds.Y);
        Assert.Equal(4, selection.Bounds.Width);
    }

    [Fact]
    public void ExpandedSelectionGrowsTheOutlineWithARoundEdge()
    {
        var selection = Selection.Rectangle(10, 10, 2, 2, 4, 4);

        var expanded = selection.Expanded(1);

        Assert.Equal(new RectI(1, 1, 6, 6), expanded.Bounds);
        Assert.Equal(255, expanded.CoverageAt(2, 1)); // above the top edge
        Assert.Equal(255, expanded.CoverageAt(1, 2)); // left of the left edge
        Assert.Equal(0, expanded.CoverageAt(1, 1)); // corners stay round at this radius
        Assert.Equal(0, expanded.CoverageAt(0, 0));
    }

    [Fact]
    public void ContractedSelectionShrinksAndCanEmptyOut()
    {
        var selection = Selection.Rectangle(10, 10, 2, 2, 4, 4);

        var contracted = selection.Contracted(1);
        Assert.NotNull(contracted);
        Assert.Equal(new RectI(3, 3, 2, 2), contracted!.Bounds);

        // Contracting past the middle keeps an explicit empty selection, as on the Mac.
        var empty = selection.Contracted(3);
        Assert.True(empty.IsEmpty); Assert.Equal(new RectI(0, 0, 0, 0), empty.Bounds);

        // Contracting pulls away from the canvas edges on every side the rect touches.
        var touching = Selection.Rectangle(6, 6, 0, 0, 5, 6);
        var away = touching.Contracted(1);
        Assert.NotNull(away);
        Assert.Equal(new RectI(2, 2, 2, 2), away!.Bounds);
        Assert.Equal(0, away.CoverageAt(0, 0));
    }

    [Fact]
    public void FeatheredSelectionKeepsTheCenterAndSoftensTheEdge()
    {
        var selection = Selection.Rectangle(16, 16, 4, 4, 8, 8);

        var feathered = selection.Feathered(1);

        Assert.Equal(255, feathered.CoverageAt(8, 8)); // center fully selected
        Assert.True(feathered.CoverageAt(4, 8) is > 0 and < 255); // the edge fades
        Assert.Equal(0, feathered.CoverageAt(0, 0)); // far outside stays empty
        Assert.True(feathered.Bounds.Width > selection.Bounds.Width); // the spread grows the bounds
    }

    [Fact]
    public void EllipticalSelectionKeepsItsCenterAndTrimsCorners()
    {
        var selection = Selection.Ellipse(16, 16, 8, 8, 4, 3);

        Assert.Equal(255, selection.CoverageAt(8, 8)); // center
        Assert.Equal(255, selection.CoverageAt(11, 8)); // right vertex inside
        Assert.Equal(0, selection.CoverageAt(8, 4)); // just above the top
        Assert.Equal(0, selection.CoverageAt(4, 5)); // corner is outside the ellipse
    }

    // --- Brush ---

    [Fact]
    public void HardBrushStampsASolidDisc()
    {
        var document = DocumentWithLayer();
        var layerId = document.Snapshot.Manifest.Layers[0].Id;

        var strokeKey = new object();
        Assert.True(document.BeginStroke(strokeKey, layerId, new BrushSettings(3, 1, 1, 255, 0, 0), new PointD(8, 8)));
        document.EndStroke(strokeKey);

        var surface = document.Snapshot.Images[layerId];
        Assert.Equal(255, Pixel(surface, 8, 8, 0)); // red at the center
        Assert.Equal(255, Pixel(surface, 8, 8, 3));
        Assert.Equal(0, Pixel(surface, 1, 1, 0)); // corner untouched
    }

    [Fact]
    public void EraserRemovesCoverage()
    {
        var document = DocumentWithLayer();
        var layerId = document.Snapshot.Manifest.Layers[0].Id;

        var strokeKey = new object();
        document.BeginStroke(strokeKey, layerId, new BrushSettings(3, 1, 1, 0, 0, 0), new PointD(8.5, 8.5), erase: true);
        document.EndStroke(strokeKey);

        var surface = document.Snapshot.Images[layerId];
        Assert.Equal(0, Pixel(surface, 8, 8, 3)); // erased to transparent
        Assert.Equal(255, Pixel(surface, 1, 1, 3)); // outside the dab: opaque
    }

    [Fact]
    public void AStrokeIsOneUndoStep()
    {
        var document = DocumentWithLayer();
        var layerId = document.Snapshot.Manifest.Layers[0].Id;

        var strokeKey = new object();
        document.BeginStroke(strokeKey, layerId, new BrushSettings(2, 1, 1, 255, 255, 255), new PointD(2, 2));
        document.ContinueStroke(strokeKey, new PointD(6, 2));
        document.ContinueStroke(strokeKey, new PointD(10, 2));
        document.EndStroke(strokeKey);

        document.Undo();
        var surface = document.Snapshot.Images[layerId];
        Assert.Equal(0, Pixel(surface, 8, 8, 0)); // the whole stroke reverts at once
    }

    [Fact]
    public void SelectionConstrainsTheBrush()
    {
        var document = DocumentWithLayer();
        var layerId = document.Snapshot.Manifest.Layers[0].Id;
        document.SetSelection(Selection.Rectangle(16, 16, 0, 0, 8, 16)); // left half only

        var strokeKey = new object();
        document.BeginStroke(strokeKey, layerId, new BrushSettings(4, 1, 1, 255, 0, 0), new PointD(10.5, 8.5));
        document.EndStroke(strokeKey);

        var surface = document.Snapshot.Images[layerId];
        // The dab's center sits in the unselected half; only pixels left of x = 8 are
        // selected, so only they get painted.
        Assert.Equal(0, Pixel(surface, 10, 8, 0));
        Assert.Equal(255, Pixel(surface, 7, 8, 0)); // inside the dab and the selection
        Assert.Equal(0, Pixel(surface, 8, 8, 0)); // inside the dab, outside the selection
    }

    [Fact]
    public void PaintFollowsTheLayerTransform()
    {
        // A layer moved to (4, 4): painting at document (5, 5) lands at layer pixel (1, 1).
        var document = DocumentWithLayer();
        var layerId = document.Snapshot.Manifest.Layers[0].Id;
        document.MoveLayerBy(layerId, 4, 4);

        var strokeKey = new object();
        document.BeginStroke(strokeKey, layerId, new BrushSettings(2, 1, 1, 0, 255, 0), new PointD(5.5, 5.5));
        document.EndStroke(strokeKey);

        var surface = document.Snapshot.Images[layerId];
        Assert.Equal(255, Pixel(surface, 1, 1, 1)); // green at layer pixel (1, 1)
        Assert.Equal(0, Pixel(surface, 8, 8, 1)); // canvas center untouched
        Assert.Equal(255, Pixel(surface, 1, 1, 3));
    }

    [Fact]
    public void MaskPaintingWritesTheMaskNotThePixels()
    {
        var document = DocumentWithLayer();
        var layerId = document.Snapshot.Manifest.Layers[0].Id;
        var fullMask = new MaskSurface(16, 16);
        Array.Fill(fullMask.Pixels, (byte)255);
        document.AddRasterMask(layerId, fullMask);

        var strokeKey = new object();
        document.BeginStroke(strokeKey, layerId, new BrushSettings(4, 1, 1, 0, 0, 0), new PointD(8.5, 8.5), onMask: true);
        document.EndStroke(strokeKey);

        var mask = document.Snapshot.Masks[layerId];
        Assert.Equal(0, mask.Pixels[8 * mask.Width + 8]); // black brush hides at the center
        Assert.Equal(255, mask.Pixels[0]); // white elsewhere

        var surface = document.Snapshot.Images[layerId];
        Assert.Equal(255, Pixel(surface, 8, 8, 3)); // the pixels themselves are untouched
    }

    [Fact]
    public void MaskPaintingCreatesAMaskWhenAbsent()
    {
        var document = DocumentWithLayer();
        var layerId = document.Snapshot.Manifest.Layers[0].Id;
        Assert.Null(document.Snapshot.Manifest.Layers[0].MaskFile);

        var strokeKey = new object();
        document.BeginStroke(strokeKey, layerId, new BrushSettings(2, 1, 1, 255, 255, 255), new PointD(4.5, 4.5), onMask: true);
        document.EndStroke(strokeKey);

        Assert.NotNull(document.Snapshot.Manifest.Layers[0].MaskFile);
        Assert.True(document.Snapshot.Manifest.Layers[0].MaskEnabled);
        var mask = document.Snapshot.Masks[layerId];
        Assert.Equal(255, mask.Pixels[4 * mask.Width + 4]); // white brush on a white mask
    }

    [Fact]
    public void SoftBrushHasAFalloff()
    {
        var document = DocumentWithLayer();
        var layerId = document.Snapshot.Manifest.Layers[0].Id;

        var strokeKey = new object();
        document.BeginStroke(strokeKey, layerId, new BrushSettings(8, 0, 1, 255, 0, 0), new PointD(8.5, 8.5));
        document.EndStroke(strokeKey);

        var surface = document.Snapshot.Images[layerId];
        byte center = Pixel(surface, 8, 8, 0);
        byte edge = Pixel(surface, 12, 8, 0); // half a radius out from the dab's center
        Assert.Equal(255, center);
        Assert.True(edge < center, $"edge {edge} should be softer than center {center}");
    }
}
