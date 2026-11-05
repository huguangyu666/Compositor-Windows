using Xunit;

namespace Compositor.Core.Tests;

public class EffectOutputWorkspaceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FirstOutsideBlurDoesNotAllocateAnotherFullFloatPlane(bool glow)
    {
        var source = EffectRegionTests.Pattern(256, 256);
        var mask = new MaskSurface(17, 9); new Random(7301).NextBytes(mask.Pixels);
        var effects = glow ? new LayerEffects { OuterGlow = new() { Size = 40, Red = .7, Opacity = .8 } }
            : new LayerEffects { Shadow = new() { Blur = 40, Distance = 20 } };
        LayerEffectsRenderer.Render(source, mask, effects);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var result = LayerEffectsRenderer.Render(source, mask, effects).Result;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        long limit = (long)result.Width * result.Height * 8 + result.Width * 256L + 65536;
        Assert.True(allocated < limit, $"Outside blur allocated {allocated} bytes; limit {limit} for {result.Width}x{result.Height}.");
    }

    [Fact]
    public void BlackShadowLeavesNoFloatBitsInTransparentOutputRgb()
    {
        var source = new Surface(64, 64); source.Pixels.AsSpan().Fill(255);
        var (result, inset) = LayerEffectsRenderer.Render(source, null, new() { Shadow = new() { Blur = 40, Distance = 20 } });
        int visibleShadow = 0;
        for (int y = 0; y < result.Height; y++)
        for (int x = 0; x < result.Width; x++)
        {
            if (x >= inset && x < inset + source.Width && y >= inset && y < inset + source.Height) continue;
            int i = y * result.Stride + x * 4;
            Assert.Equal(0, result.Pixels[i]); Assert.Equal(0, result.Pixels[i + 1]); Assert.Equal(0, result.Pixels[i + 2]);
            if (result.Pixels[i + 3] != 0) visibleShadow++;
        }
        Assert.True(visibleShadow > 100, "The black-shadow test did not render a visible halo.");
    }

    [Fact]
    public void ReturnedPublicResultStaysOwnedAcrossLaterRenders()
    {
        var source = EffectRegionTests.Pattern(71, 83);
        var effects = new LayerEffects { Shadow = new() { Blur = 40 } };
        var first = LayerEffectsRenderer.Render(source, null, effects).Result;
        var before = first.Pixels.ToArray();
        source.Pixels.AsSpan().Clear();
        var next = LayerEffectsRenderer.Render(source, null, effects).Result;
        Assert.NotSame(first.Pixels, next.Pixels);
        Assert.Equal(before, first.Pixels);
        Assert.All(next.Pixels, value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReusedRetainedOutputClearsPriorShadowAndFloatWorkspace(bool hideWithMask)
    {
        var scratch = new EffectScratch(); var source = EffectRegionTests.Pattern(71, 83);
        var effects = new LayerEffects { Shadow = new() { Blur = 8 } };
        var output = new RectI(30, 30, 19, 17);
        var mask = hideWithMask ? new MaskSurface(9, 7) : null; mask?.Pixels.AsSpan().Fill(255);
        var first = LayerEffectsRenderer.RenderRegion(source, mask, effects, output, scratch).Result;
        Assert.Contains(first.Pixels, value => value != 0);
        var sourceBytes = source.Pixels.ToArray(); var expected = first.Pixels.ToArray();
        if (mask is null) source.Pixels.AsSpan().Clear(); else mask.Pixels.AsSpan().Clear();
        var next = LayerEffectsRenderer.RenderRegion(source, mask, effects, output, scratch).Result;
        Assert.Same(first, next);
        Assert.All(next.Pixels, value => Assert.Equal(0, value));
        sourceBytes.CopyTo(source.Pixels, 0); mask?.Pixels.AsSpan().Fill(255);
        var restored = LayerEffectsRenderer.RenderRegion(source, mask, effects, output, scratch).Result;
        Assert.Same(first, restored); Assert.Equal(expected, restored.Pixels);
    }

    [Fact]
    public void ConcurrentPublicRendersDoNotShareOutputWorkspace()
    {
        var source = EffectRegionTests.Pattern(71, 83);
        var effects = new LayerEffects { Shadow = new() { Blur = 40 }, OuterGlow = new() { Size = 8, Opacity = .7, Red = 1 } };
        var expected = LayerEffectsRenderer.Render(source, null, effects).Result;
        Parallel.For(0, 12, _ =>
        {
            var actual = LayerEffectsRenderer.Render(source, null, effects).Result;
            Assert.NotSame(expected.Pixels, actual.Pixels); Assert.Equal(expected.Pixels, actual.Pixels);
        });
    }
}
