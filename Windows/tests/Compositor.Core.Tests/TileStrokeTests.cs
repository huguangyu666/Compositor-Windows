using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// Locks the behavior docs/brush-performance.md promises for the ported brush: output
/// independent of pointer-event count, crossings blended by density integration, and a
/// tail that can be replaced without double-counting.
/// </summary>
public class TileStrokeTests
{
    private static readonly LayerTransform Identity = new() { Origin = new PointD(0, 0), Size = new SizeD(64, 64) };

    private static byte Pixel(Surface surface, int x, int y, int channel) =>
        surface.Pixels[y * surface.Stride + x * 4 + channel];

    [Fact]
    public void OutputIsIndependentOfPointerEventCount()
    {
        // The same straight path painted as one 40 px segment and as forty 1 px segments
        // must land within a couple of LSBs everywhere.
        var brush = new BrushSettings(8, Hardness: 0, Opacity: 1, 255, 0, 0);

        var oneSegment = new Surface(64, 64);
        var stroke = new TileStroke(brush, Identity, 64, 64);
        stroke.SweepSegment(new PointD(10, 32));
        stroke.SweepSegment(new PointD(50, 32));
        stroke.PaintRegion(oneSegment, new Surface(64, 64), 0, 0, 64, 64, null, erase: false);

        var fortySegments = new Surface(64, 64);
        var dense = new TileStroke(brush, Identity, 64, 64);
        dense.SweepSegment(new PointD(10, 32));
        for (int i = 0; i < 40; i++)
        {
            dense.SweepSegment(new PointD(10 + i + 1, 32)); // ends at 50, same as the one-segment path
        }
        dense.PaintRegion(fortySegments, new Surface(64, 64), 0, 0, 64, 64, null, erase: false);

        // The saturated core must match pixel for pixel.
        for (int y = 29; y < 36; y++)
        {
            for (int x = 20; x < 40; x++)
            {
                // 1 LSB of float summation order is within the saturation guarantee.
                Assert.InRange(Pixel(fortySegments, x, y, 0), 254, 255);
                Assert.InRange(Pixel(oneSegment, x, y, 0), 254, 255);
            }
        }
        // The gradient band's total deposit agrees within 2%: the dab phase along the path
        // may differ, the integrated density must not.
        long SumRed(Surface surface)
        {
            long total = 0;
            for (int i = 0; i < surface.Pixels.Length; i += 4) total += surface.Pixels[i];
            return total;
        }
        long single = SumRed(oneSegment), denseTotal = SumRed(fortySegments);
        Assert.InRange(denseTotal, (long)(single * 0.98), (long)(single * 1.02) + 1);
    }

    [Fact]
    public void CrossingStrokesBlendWithoutACrease()
    {
        // Two soft strokes crossing at right angles: the crossing pixel is at least as
        // covered as either branch alone — the density sum, not a max, so no sharp crease.
        var brush = new BrushSettings(10, Hardness: 0, Opacity: 1, 0, 0, 255);
        var surface = new Surface(64, 64);
        var stroke = new TileStroke(brush, Identity, 64, 64);
        stroke.SweepSegment(new PointD(32, 8));
        stroke.SweepSegment(new PointD(32, 56));
        stroke.RetireTail();
        stroke.SweepSegment(new PointD(8, 32));
        stroke.SweepSegment(new PointD(56, 32));
        stroke.PaintRegion(surface, new Surface(64, 64), 0, 0, 64, 64, null, erase: false);

        int crossing = Pixel(surface, 32, 32, 2);
        int branch = Pixel(surface, 32, 12, 2); // on the vertical branch, away from the crossing
        Assert.True(crossing >= branch - 2, $"crossing {crossing} should be >= branch {branch}");
        Assert.True(crossing >= 254, $"crossing should saturate at full opacity, got {crossing}");
    }

    [Fact]
    public void OpacityCapsTheWholeAccumulatedStroke()
    {
        // Painting back and forth over the same line many times can never exceed the
        // stroke's opacity setting: the deposit saturates and the cap holds.
        var brush = new BrushSettings(8, Hardness: 1, Opacity: 0.4, 255, 255, 255);
        var surface = new Surface(64, 64);
        var stroke = new TileStroke(brush, Identity, 64, 64);
        stroke.SweepSegment(new PointD(12, 32));
        for (int pass = 0; pass < 8; pass++)
        {
            stroke.SweepSegment(new PointD(52, 32));
            stroke.SweepSegment(new PointD(12, 32));
            stroke.RetireTail();
        }
        stroke.PaintRegion(surface, new Surface(64, 64), 0, 0, 64, 64, null, erase: false);

        Assert.Equal(102, Pixel(surface, 32, 32, 0)); // 0.4 × 255
        Assert.Equal(102, Pixel(surface, 32, 32, 3));
    }

    [Fact]
    public void SoftStrokeCoverageSitsBelowTheCapAtTheHalfFalloff()
    {
        // A single soft segment at half opacity: the midtone band gets partial coverage,
        // proving density (not a saturated max) drives the gradient.
        var brush = new BrushSettings(12, Hardness: 0, Opacity: 0.5, 0, 255, 0);
        var surface = new Surface(64, 64);
        var stroke = new TileStroke(brush, Identity, 64, 64);
        stroke.SweepSegment(new PointD(10, 32));
        stroke.SweepSegment(new PointD(54, 32));
        stroke.PaintRegion(surface, new Surface(64, 64), 0, 0, 64, 64, null, erase: false);

        int center = Pixel(surface, 32, 32, 1); // saturated band: 0.5 × 255
        int rim = Pixel(surface, 32, 43, 1); // at 0.92 of the radius, inside the density gradient
        Assert.InRange(center, 127, 128); // (1 - e^-20) rounds at the half LSB
        Assert.InRange(rim, 1, 60);
    }
}
