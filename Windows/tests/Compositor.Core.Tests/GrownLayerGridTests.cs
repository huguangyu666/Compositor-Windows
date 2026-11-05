using Compositor.Core;
using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// The grown-grid plumbing for spreading filters (Filters.swift:393-418 and :153-171): padding a layer
/// keeps its document rectangle because the grown grid's middle lands exactly where the original's did,
/// and trimming a piece puts that piece back on the same middle. Both directions use the port's own
/// unit/flip-aware mapping (BrushEngine.PixelToDocument), so a rotated or flipped layer must hold too.
/// </summary>
public class GrownLayerGridTests
{
    private static Surface Opaque(int width, int height, byte alpha = 255)
    {
        var image = new Surface(width, height);
        for (int i = 0; i < image.Pixels.Length; i += 4)
        {
            image.Pixels[i] = 10; image.Pixels[i + 1] = 20; image.Pixels[i + 2] = 30;
            image.Pixels[i + 3] = alpha;
        }
        return image;
    }

    private static LayerTransform Placed(double x, double y, double w, double h, double rotation = 0, bool flipX = false)
        => new() { Origin = new PointD(x, y), Size = new SizeD(w, h), Rotation = rotation, FlipX = flipX };

    [Fact]
    public void GrowingPadsEverySideAndKeepsTheDocumentRectangle()
    {
        var source = Opaque(4, 2);
        var placement = Placed(100, 50, 80, 40);
        var (grown, expanded) = GrownLayerGrid.Grow(source, placement, 3);

        Assert.Equal(10, grown.Width);
        Assert.Equal(8, grown.Height);
        // The padding starts clear and the layer sits inside it.
        Assert.Equal(0, grown.Pixels[0]);
        Assert.Equal(10, grown.Pixels[(3 * grown.Stride + 3 * 4)]);
        // The middle of the grown grid, in the original's pixel coordinates, is the original's middle:
        // (−1.5, −1.5, 10, 8) centers on (2,1) just as (0,0,4,2) does, so the placement grew around the
        // same document rectangle and only got wider by the margin ratio.
        Assert.Equal(100 - 80 * 3d / 4, expanded.Origin.X, 6);
        Assert.Equal(50 - 40 * 3d / 2, expanded.Origin.Y, 6);
        Assert.Equal(80 * 10d / 4, expanded.Size.Width, 6);
        Assert.Equal(40 * 8d / 2, expanded.Size.Height, 6);
        Assert.Equal(placement.Rotation, expanded.Rotation);
    }

    [Fact]
    public void GrowingAroundARotatedFlippedLayerHoldsItsCorners()
    {
        var source = Opaque(4, 2);
        var placement = Placed(100, 50, 80, 40, rotation: 33, flipX: true);
        var (grown, expanded) = GrownLayerGrid.Grow(source, placement, 5);

        // The document point of any pixel of the original grid must still be the document point of the
        // same content in the grown grid: the grow is content-preserving, so the corner checks pin it.
        PointD BeforeOriginal(int px, int py) => BrushEngine.PixelToDocument(placement, 4, 2, new PointD(px + .5, py + .5));
        PointD AfterGrown(int px, int py) => BrushEngine.PixelToDocument(expanded, 14, 12, new PointD(px + 5 + .5, py + 5 + .5));
        foreach (var (x, y) in new[] { (0, 0), (4, 0), (0, 2), (4, 2), (2, 1) })
            Assert.Equal(BeforeOriginal(x, y), AfterGrown(x, y), new PointDTolerance());
    }

    [Fact]
    public void ZeroMarginIsTheLayerItself()
    {
        var source = Opaque(3, 3);
        var placement = Placed(0, 0, 30, 30);
        var (grown, expanded) = GrownLayerGrid.Grow(source, placement, 0);
        Assert.Same(source, grown);
        Assert.Same(placement, expanded);
        // The Mac's trim returns the grid untouched when there is nothing to cut (crop == full).
        Assert.Equal(grown.Width, GrownLayerGrid.Trim(grown, expanded).Image.Width);
    }

    [Fact]
    public void TrimCutsToCoverageAndRecentersThePiece()
    {
        // A 10×10 grid with content only in (2,3)–(7,6): the trim crop is that box, and its document
        // middle — in the grown grid's units, (4.5, 4.5) — has to be the document middle of the result.
        var grown = new Surface(10, 10);
        for (int y = 3; y < 7; y++)
            for (int x = 2; x < 8; x++)
            { int at = y * grown.Stride + x * 4; grown.Pixels[at] = 5; grown.Pixels[at + 3] = 200; }
        var placement = Placed(40, 20, 100, 100);
        var (image, result) = GrownLayerGrid.Trim(grown, placement);

        Assert.Equal(6, image.Width);
        Assert.Equal(4, image.Height);
        Assert.Equal(5, image.Pixels[0]);
        var middleBefore = BrushEngine.PixelToDocument(placement, 10, 10, new PointD(2 + 6 / 2d, 3 + 4 / 2d));
        var middleAfter = BrushEngine.PixelToDocument(result, 6, 4, new PointD(3, 2));
        Assert.Equal(middleBefore.X, middleAfter.X, 6);
        Assert.Equal(middleBefore.Y, middleAfter.Y, 6);
    }

    [Fact]
    public void AnEmptyGridDoesNotCollapseTheLayer()
    {
        var grown = new Surface(6, 6);  // every alpha zero — a blur of nothing
        var placement = Placed(10, 10, 60, 60);
        var (image, result) = GrownLayerGrid.Trim(grown, placement);
        Assert.Same(grown, image);
        Assert.Same(placement, result);
    }

    [Fact]
    public void BlurOnTheGrownGridSpreadsIntoThePaddingAndTrimsToTheGrownCoverage()
    {
        // The Mac's whole claim for the spreading filters (Filters.swift:626): the blurred result is
        // the grown grid cropped to what the blur left with coverage — bigger than the layer, because
        // alpha spread into the padding, and smaller than the grid, because the corners stayed clear.
        var source = Opaque(24, 24);
        var placement = Placed(0, 0, 240, 240);
        double radius = 4;
        int margin = (int)Math.Ceiling(radius * 3) + 2;
        var (grown, grownPlacement) = GrownLayerGrid.Grow(source, placement, margin);
        AdjustmentEngine.ApplyGaussianBlur(radius, grown, 0, 0, grown.Width, grown.Height);
        // A normalized separable kernel over a flat premultiplied field leaves the interior exactly as
        // it was; only the edges dissolve. The centre in grown coordinates is (12+margin, 12+margin).
        Assert.Equal(255, grown.Pixels[(12 + margin) * grown.Stride + (12 + margin) * 4 + 3]);
        var (trimmed, trimmedPlacement) = GrownLayerGrid.Trim(grown, grownPlacement);

        Assert.True(trimmed.Width > source.Width && trimmed.Width < grown.Width,
            $"the blurred layer neither spread into the padding nor stayed inside the grid ({trimmed.Width}).");
        // The spread is symmetric, so the trim's document middle is still the grow's middle, still the
        // original's middle.
        var originalMiddle = BrushEngine.PixelToDocument(placement, 24, 24, new PointD(12, 12));
        var finalMiddle = BrushEngine.PixelToDocument(trimmedPlacement, trimmed.Width, trimmed.Height,
            new PointD(trimmed.Width / 2d, trimmed.Height / 2d));
        Assert.Equal(originalMiddle.X, finalMiddle.X, 4);
        Assert.Equal(originalMiddle.Y, finalMiddle.Y, 4);
    }

    private sealed class PointDTolerance : IEqualityComparer<PointD>
    {
        public bool Equals(PointD a, PointD b) => Math.Abs(a.X - b.X) < 1e-6 && Math.Abs(a.Y - b.Y) < 1e-6;
        public int GetHashCode(PointD p) => HashCode.Combine(p.X, p.Y);
    }
}
