using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// The selection builders that can spend a long time on a large document take a cancellation token,
/// checked at row boundaries. A pre-cancelled token proves the check is on the path rather than
/// beside it; the mid-flight case (a request superseded while its sweeps run) is exercised against
/// the real 4K pipeline by the workspace probe.
/// </summary>
public sealed class SelectionCancellationTests
{
    private static CancellationTokenSource Cancelled()
    {
        var source = new CancellationTokenSource();
        source.Cancel();
        return source;
    }

    private static Selection Box(int documentSide = 1200) => Selection.Rectangle(documentSide, documentSide, 200, 200, 600, 600);

    [Fact]
    public void ExpandHonoursACancelledToken()
    {
        using var cancellation = Cancelled();
        Assert.Throws<OperationCanceledException>(() => Box().Expanded(8, cancellation.Token));
        Assert.Equal(new RectI(200, 200, 600, 600), Box().Expanded(0).Bounds);
    }

    [Fact]
    public void ContractHonoursACancelledToken()
    {
        using var cancellation = Cancelled();
        Assert.Throws<OperationCanceledException>(() => Box().Contracted(8, cancellation.Token));
    }

    [Fact]
    public void ModifyPassesTheTokenToBothReshapes()
    {
        using var cancellation = Cancelled();
        Assert.Throws<OperationCanceledException>(() => SelectionCommands.Modify(Box(), SelectionModification.Expand, 8, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => SelectionCommands.Modify(Box(), SelectionModification.Contract, 8, cancellation.Token));
        // Feather is a scalar on the outline, so it stays a pure no-cost operation.
        Assert.Equal(8d, SelectionCommands.Modify(Box(), SelectionModification.Feather, 8, cancellation.Token)!.Feather);
    }

    [Fact]
    public void InvertAndFromCoverageHonourACancelledToken()
    {
        using var cancellation = Cancelled();
        Assert.Throws<OperationCanceledException>(() => Box().Inverted(1200, 1200, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => SelectionCommands.Invert(Box(), 1200, 1200, cancellation.Token));
        var coverage = new MaskSurface(1200, 1200);
        coverage.Pixels[7 * 1200 + 7] = 255;
        Assert.Throws<OperationCanceledException>(() => Selection.FromCoverage(coverage, cancellation.Token));
    }

    [Fact]
    public void LassoRasterizeHonoursACancelledTokenAndStillFillsWithoutOne()
    {
        var points = new List<PointD>();
        for (int step = 0; step < 240; step++)
        {
            double angle = step * Math.PI / 120;
            points.Add(new PointD(600 + 300 * Math.Cos(angle), 600 + 300 * Math.Sin(angle)));
        }

        using var cancellation = Cancelled();
        Assert.Throws<OperationCanceledException>(() => LassoSelection.Rasterize(1200, 1200, points, true, cancellation.Token));
        var filled = LassoSelection.Rasterize(1200, 1200, points, true);
        Assert.True(filled.CoverageAt(600, 600) == 255, "the polygon centre lost its coverage");
        Assert.True(filled.CoverageAt(1199, 0) == 0, "coverage leaked outside the polygon");
    }

    [Fact]
    public void WandAndColorRangeScansHonourACancelledToken()
    {
        var sample = new Surface(900, 700);
        for (int i = 0; i < sample.Pixels.Length; i++) sample.Pixels[i] = 128;
        var mask = new MaskSurface(900, 700);
        using var cancellation = Cancelled();

        Assert.Throws<OperationCanceledException>(() => WandPixels.WandMask(sample, 10, 10, 2, 8, contiguous: false, mask, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => WandPixels.WandMask(sample, 10, 10, 2, 8, contiguous: true, mask, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => WandPixels.ColorRangeMask(sample, [128, 128, 128], [], 12, false, mask, cancellation.Token));

        // Without cancellation the same calls still produce the expected coverage.
        long filled = WandPixels.WandMask(sample, 10, 10, 2, 8, contiguous: false, mask);
        Assert.Equal(900L * 700L, filled);
    }
}
