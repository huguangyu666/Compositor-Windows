using Xunit;

namespace Compositor.Core.Tests;

public class EffectEmptyCoverageTests
{
    [Fact]
    public void HiddenMaskNeverCopiesOpaquePixelsOrOverlayColor()
    {
        var source = new Surface(16, 16); source.Pixels.AsSpan().Fill(255);
        var effects = new LayerEffects { Shadow = new() { Blur = 8, Red = 1 },
            ColorOverlay = new() { Red = 1 }, InnerShadow = new() { Opacity = 1 } };
        var result = LayerEffectsRenderer.Render(source, new MaskSurface(3, 5), effects).Result;
        Assert.True(result.Pixels.AsSpan().IndexOfAnyExcept((byte)0) < 0);
        Assert.True(source.Pixels.AsSpan().IndexOfAnyExcept((byte)255) < 0);
    }

    [Fact]
    public void EmptyInnerGlowDoesNotAllocateGaussianWorkspace()
    {
        var source = new Surface(512, 512);
        var effects = new LayerEffects { InnerGlow = new() { Size = 40, Red = 1, Opacity = .7 } };
        LayerEffectsRenderer.Render(source, null, effects);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var result = LayerEffectsRenderer.Render(source, null, effects).Result;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        long limit = (long)result.Width * result.Height * 7 + 65536;
        Assert.True(allocated < limit, $"Empty inner glow allocated {allocated} bytes; limit {limit}.");
        Assert.True(result.Pixels.AsSpan().IndexOfAnyExcept((byte)0) < 0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(40)]
    public void EmptyShiftedCoverageStillProducesVisibleInnerShadow(double blur)
    {
        var source = new Surface(16, 16); source.Pixels.AsSpan().Fill(255);
        var (result, inset) = LayerEffectsRenderer.Render(source, null,
            new() { InnerShadow = new() { Distance = 100, Angle = 0, Blur = blur, Opacity = 1 } });
        int pixel = (inset + 8) * result.Stride + (inset + 8) * 4;
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, result.Pixels.AsSpan(pixel, 4).ToArray());
    }
}
