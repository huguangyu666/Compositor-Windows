using Compositor.Core;
using Xunit;

namespace Compositor.Segmentation.Tests;

public sealed class SamEdgeRefinementTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FitsTheOriginalColorEdgeInsteadOfKeepingAnOffsetModelEdge(bool soft)
    {
        var (image, mask) = OffsetEdge();
        var source = image.Pixels.ToArray(); var originalMask = mask.Pixels.ToArray();
        var result = SamEdgeRefinement.Refine(image, mask, soft, null, default);
        for (int y = 12; y < 52; y++)
        {
            Assert.Equal((byte)255, result.Pixels[y * 96 + 46]);
            Assert.Equal((byte)0, result.Pixels[y * 96 + 49]);
        }
        Assert.Equal(source, image.Pixels); Assert.Equal(originalMask, mask.Pixels);
    }

    [Fact]
    public void UsesTheOriginalMixedPixelForCoverageInsteadOfBlurringBothSides()
    {
        var (image, mask) = OffsetEdge();
        for (int y = 0; y < image.Height; y++)
        {
            int p = y * image.Stride + 48 * 4;
            image.Pixels[p] = 120; image.Pixels[p + 1] = 100; image.Pixels[p + 2] = 120;
        }
        var result = SamEdgeRefinement.Refine(image, mask, true, null, default);
        Assert.InRange(result.Pixels[32 * 96 + 48], (byte)100, (byte)155);
        Assert.Equal((byte)255, result.Pixels[32 * 96 + 46]);
        Assert.Equal((byte)0, result.Pixels[32 * 96 + 50]);
    }

    [Fact]
    public void ExplicitPointsRemainConstraintsDuringBoundaryFitting()
    {
        var (image, mask) = OffsetEdge();
        var points = new SelectionPoint[] { new(new(49, 32)), new(new(46, 32), false) };
        var result = SamEdgeRefinement.Refine(image, mask, true, points, default);
        Assert.Equal((byte)255, result.Pixels[32 * 96 + 49]);
        Assert.Equal((byte)0, result.Pixels[32 * 96 + 46]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EdgeFittingRemovesDetachedIslandsUnlessExplicitlyIncluded(bool includeDetail)
    {
        var (image, mask) = OffsetEdge();
        for (int y = 30; y < 33; y++)
            for (int x = 51; x < 54; x++)
            {
                int p = y * image.Stride + x * 4;
                image.Pixels[p] = 40; image.Pixels[p + 2] = 200;
            }
        var points = new List<SelectionPoint> { new(new(24, 32)) };
        if (includeDetail) points.Add(new(new(52, 31)));
        var result = SamEdgeRefinement.Refine(image, mask, true, points, default);
        Assert.Equal((byte)255, result.Pixels[32 * 96 + 46]);
        for (int y = 30; y < 33; y++)
            for (int x = 51; x < 54; x++) Assert.Equal(includeDetail ? (byte)255 : (byte)0, result.Pixels[y * 96 + x]);
    }

    [Theory]
    [InlineData(512)]
    [InlineData(513)]
    public void ModelScaleBoundaryCanCorrectAnOffsetBeyondSixPixels(int edge)
    {
        var image = new Surface(1024, 64); var mask = new MaskSurface(1024, 64);
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 1024; x++)
            {
                int p = y * image.Stride + x * 4;
                image.Pixels[p] = x < edge ? (byte)40 : (byte)200;
                image.Pixels[p + 1] = 100;
                image.Pixels[p + 2] = x < edge ? (byte)200 : (byte)40;
                image.Pixels[p + 3] = 255;
                if (x < edge + 7) mask.Pixels[y * 1024 + x] = 255;
            }
        var result = SamEdgeRefinement.Refine(image, mask, false, null, default);
        Assert.Equal((byte)255, result.Pixels[32 * 1024 + edge - 1]);
        Assert.Equal((byte)0, result.Pixels[32 * 1024 + edge]);
    }

    [Fact]
    public void EmptyAndFullMasksStayExactAndCancellationPrecedesWork()
    {
        var image = new Surface(9, 7); var mask = new MaskSurface(9, 7);
        Assert.Equal(mask.Pixels, SamEdgeRefinement.Refine(image, mask, true, null, default).Pixels);
        Array.Fill(mask.Pixels, (byte)255);
        Assert.Equal(mask.Pixels, SamEdgeRefinement.Refine(image, mask, true, null, default).Pixels);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => SamEdgeRefinement.Refine(image, mask, true, null, cancel.Token));
    }

    private static (Surface, MaskSurface) OffsetEdge()
    {
        var image = new Surface(96, 64); var mask = new MaskSurface(96, 64);
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 96; x++)
            {
                int p = y * image.Stride + x * 4;
                image.Pixels[p] = x < 48 ? (byte)40 : (byte)200;
                image.Pixels[p + 1] = 100;
                image.Pixels[p + 2] = x < 48 ? (byte)200 : (byte)40;
                image.Pixels[p + 3] = 255;
                if (x < 51) mask.Pixels[y * 96 + x] = 255;
            }
        return (image, mask);
    }

    [Fact]
    public void NarrowBandCutsAgreeWithExhaustiveMinimumEnergy()
    {
        var random = new Random(4381);
        for (int trial = 0; trial < 32; trial++)
        {
            int count = random.Next(2, 9), source = count, sink = count + 1;
            var fg = new int[count]; var bg = new int[count];
            var links = new List<(int From, int To, int Weight)>();
            var graph = new SamEdgeRefinement.CutGraph(count + 2, count * count * 4 + count * 4 + 8);
            for (int i = 0; i < count; i++)
            {
                fg[i] = random.Next(6); bg[i] = random.Next(6);
                graph.Add(source, i, bg[i], 0); graph.Add(i, sink, fg[i], 0);
                for (int j = 0; j < i; j++)
                    if (random.Next(2) == 0)
                    {
                        int weight = random.Next(6); graph.Add(i, j, weight, weight); links.Add((i, j, weight));
                    }
            }
            var result = graph.Solve(source, sink, default);
            int bits = 0; for (int i = 0; i < count; i++) if (result[i]) bits |= 1 << i;
            int minimum = Enumerable.Range(0, 1 << count).Min(Energy);
            Assert.True(minimum == Energy(bits), $"trial={trial} fg={string.Join(',', fg)} bg={string.Join(',', bg)} links={string.Join(';', links)} bits={bits} expected={minimum} actual={Energy(bits)}");

            int Energy(int selection)
            {
                int cost = 0;
                for (int i = 0; i < count; i++) cost += (selection & (1 << i)) != 0 ? fg[i] : bg[i];
                foreach (var (a, b, weight) in links) if (((selection >> a) & 1) != ((selection >> b) & 1)) cost += weight;
                return cost;
            }
        }
    }
}
