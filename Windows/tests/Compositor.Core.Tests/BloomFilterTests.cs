using Compositor.Core;
using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// The Bloom / Glow kernel, ported from Core Image's CIBloom (Filters.swift:236-241). The claims are
/// the Mac's own test (CompositorTests/FinishingFilterTests.swift:54-77): a white dot on black lights
/// the dark area NEAR it more than the far corner, the whole picture keeps its alpha, and on a
/// transparent layer the glow stays VISIBLE beyond the bright pixels — which means the blurred alpha
/// screens into the transparent padding. This machine has no Core Image, so the properties are the
/// documented composition (blur, scale by Amount÷50, screen), not a byte-for-byte run of the Mac;
/// cross-platform pixel diffs stay on the handoff's external-conditions list.
/// </summary>
public class BloomFilterTests
{
    private static Surface DotField(bool transparent, int size = 65, int x0 = 30, int y0 = 30, int side = 5)
    {
        var image = new Surface(size, size);
        if (!transparent)
            for (int i = 0; i < image.Pixels.Length; i += 4)
            { image.Pixels[i + 3] = 255; }        // black, opaque
        for (int y = y0; y < y0 + side; y++)
            for (int x = x0; x < x0 + side; x++)
            {
                int at = y * image.Stride + x * 4;
                image.Pixels[at] = image.Pixels[at + 1] = image.Pixels[at + 2] = 255;
                image.Pixels[at + 3] = 255;       // white, opaque: premultiplied is the same bytes
            }
        return image;
    }

    private static byte Red(Surface image, int x, int y) => image.Pixels[y * image.Stride + x * 4];
    private static byte Alpha(Surface image, int x, int y) => image.Pixels[y * image.Stride + x * 4 + 3];

    [Fact]
    public void LightSpreadsFromBrightPixelsAndOpacityIsKept()
    {
        // The Mac's fixture: a 5×5 white dot in a black opaque field, Amount 100, Radius 12.
        var image = DotField(transparent: false);
        BloomFilter.Run(image, BloomFilter.DefaultAmount * 2.5, 12);   // amount 100
        Assert.True(Red(image, 40, 32) > 0, "the pixel five columns off the light stayed dark.");
        Assert.True(Red(image, 40, 32) > Red(image, 2, 2),
            $"the glow is not stronger near the light than in the corner: {Red(image, 40, 32)} vs {Red(image, 2, 2)}.");
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
                Assert.Equal(255, Alpha(image, x, y));     // screening an opaque picture keeps it opaque
    }

    [Fact]
    public void TheGlowStaysVisibleBeyondATransparentLayersLight()
    {
        var image = DotField(transparent: true);
        BloomFilter.Run(image, 100, 12);
        Assert.True(Alpha(image, 40, 32) > 0,
            "the blurred alpha did not screen into the darkness — the Mac's test says it must be visible.");
        // The premultiplied invariant holds everywhere: screening color and alpha together cannot push
        // a channel past its own alpha (color ≤ alpha for every pixel).
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
                for (int c = 0; c < 3; c++)
                    Assert.True(image.Pixels[y * image.Stride + x * 4 + c] <= Alpha(image, x, y),
                        $"({x},{y}) channel {c} outran its alpha: a premultiplied pixel composites wrong.");
    }

    [Fact]
    public void ZeroAmountIsTheIdentity()
    {
        var image = DotField(transparent: true);
        var bytes = (byte[])image.Pixels.Clone();
        BloomFilter.Run(image, 0, 12);
        Assert.True(image.Pixels.AsSpan().SequenceEqual(bytes));   // and the sheet treats OK at 0 as Cancel
    }

    [Fact]
    public void TheStreakFallsOffWithDistanceAndGrowsWithAmount()
    {
        var near = DotField(transparent: false); BloomFilter.Run(near, 100, 12);
        var soft = DotField(transparent: false); BloomFilter.Run(soft, 40, 12);
        Assert.True(Red(near, 40, 32) > Red(soft, 40, 32),
            "Amount 100 lit the dark area no brighter than Amount 40.");
        // Same amount, falloff along a row leaving the dot's right edge at x=34.
        int previous = 256;
        foreach (int x in new[] { 35, 38, 42, 48 })
        {
            int value = Red(near, x, 32);
            Assert.True(value < previous, $"the glow did not fall off with distance at x={x}: {previous} -> {value}.");
            previous = value;
        }
    }
}
