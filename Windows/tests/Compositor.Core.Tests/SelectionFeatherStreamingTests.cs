using Xunit;

namespace Compositor.Core.Tests;

public class SelectionFeatherStreamingTests
{
    [Fact]
    public void ColdFeatherUsesOneOutputAndBoundedRows()
    {
        _ = Selection.Rectangle(8, 8, 2, 2, 4, 4).Feathered(128).CoverageAt(4, 4);
        var selection = Selection.Rectangle(1024, 1024, 256, 256, 512, 512).Feathered(128);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(255, selection.CoverageAt(512, 512));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 1400000, $"Cold feather allocated {allocated} bytes; expected one 1MiB output and bounded rows.");
    }

    [Fact]
    public void CompleteCoverageMatchesLegacyAtEdgesAndRoundingBoundaries()
    {
        var random = new Random(78123);
        foreach (var (width, height) in new[] { (1, 1), (1, 37), (41, 1), (2, 3), (7, 9), (32, 31), (137, 139) })
        foreach (double feather in new[] { .01, 1, 2, 3, 7, 16, 128, 250, 1000 })
        {
            var mask = new MaskSurface(width, height);
            random.NextBytes(mask.Pixels);
            var original = mask.Pixels.ToArray();
            var selection = new Selection(mask, new(0, 0, width, height), feather);
            var expected = SelectionFeatherReference.Render(mask, feather);
            Assert.Equal(expected.Pixels, Sample(selection, width, height));
            Assert.Equal(original, mask.Pixels);
            Assert.Same(mask, selection.Coverage);
        }
    }

    [Fact]
    public void ProjectionAndClippingStillPrecedeFeather()
    {
        var original = Selection.Ellipse(37, 29, 18.5, 13.5, 14.3, 9.8, true).Feathered(7);
        var source = original.Coverage.Pixels.ToArray();
        foreach (var (dx, dy) in new[] { (0, 0), (-15, 9), (20, -10), (100, 0) })
        {
            var moved = original.Translated(dx, dy);
            var expected = SelectionFeatherReference.Render(moved.Coverage, moved.Feather);
            Assert.Equal(expected.Pixels, Sample(moved, 37, 29));
        }
        Assert.Equal(source, original.Coverage.Pixels);
    }

    [Fact]
    public void PublishedCacheNeedsNoFurtherAllocationsAndSelectionsStayIndependent()
    {
        var first = Selection.Rectangle(128, 129, 10, 20, 70, 60).Feathered(16);
        var second = Selection.Ellipse(128, 129, 70, 70, 40, 50, true).Feathered(250);
        var expected = Sample(first, 128, 129);
        _ = Sample(second, 128, 129);
        long before = GC.GetAllocatedBytesForCurrentThread();
        int total = 0;
        for (int y = 0; y < 129; y++) for (int x = 0; x < 128; x++) total += first.CoverageAt(x, y);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(expected.Sum(value => (int)value), total);
        Assert.Equal(0, allocated);
        Assert.Equal(expected, Sample(first, 128, 129));
    }

    [Fact]
    public async Task ConcurrentFirstReadersPublishCompleteIndependentResults()
    {
        var selection = Selection.Ellipse(193, 197, 81, 90, 67, 59, true).Feathered(16);
        var expected = SelectionFeatherReference.Render(selection.Coverage, selection.Feather).Pixels;
        using var start = new ManualResetEventSlim();
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            start.Wait();
            return Sample(selection, 193, 197);
        })).ToArray();
        start.Set();
        foreach (var result in await Task.WhenAll(readers)) Assert.Equal(expected, result);
    }

    [Fact]
    public void EmptyAndOutOfBoundsSamplesDoNotBuildFeather()
    {
        var empty = new Selection(new MaskSurface(64, 64), new(0, 0, 0, 0), 128);
        var full = Selection.Rectangle(64, 64, 0, 0, 64, 64).Feathered(128);
        long before = GC.GetAllocatedBytesForCurrentThread();
        byte a = empty.CoverageAt(32, 32), b = full.CoverageAt(-1, 32), c = full.CoverageAt(64, 32);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, a + b + c);
        Assert.Equal(0, allocated);
    }

    private static byte[] Sample(Selection selection, int width, int height)
    {
        var result = new byte[width * height];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) result[y * width + x] = selection.CoverageAt(x, y);
        return result;
    }
}
