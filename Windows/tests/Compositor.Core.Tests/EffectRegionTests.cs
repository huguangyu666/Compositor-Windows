using Xunit;

namespace Compositor.Core.Tests;

public class EffectRegionTests
{
    [Theory]
    [InlineData("shadow", false)]
    [InlineData("shadow", true)]
    [InlineData("outer", false)]
    [InlineData("outer", true)]
    [InlineData("inner", false)]
    [InlineData("inner", true)]
    [InlineData("inner-shadow", false)]
    [InlineData("inner-shadow", true)]
    [InlineData("outside", false)]
    [InlineData("outside", true)]
    [InlineData("inside", false)]
    [InlineData("inside", true)]
    [InlineData("overlay", false)]
    [InlineData("overlay", true)]
    [InlineData("stack", false)]
    [InlineData("stack", true)]
    public void LocalEditsMatchFullRenderIncludingMaskStrideAndEdges(string kind, bool edge)
    {
        var effects = Effects(kind);
        var source = Pattern(301, 211);
        var mask = new MaskSurface(17, 9);
        new Random(90).NextBytes(mask.Pixels);
        var (result, inset) = LayerEffectsRenderer.Render(source, mask, effects);
        var dirty = edge ? new RectI(0, 0, 4, 5) : new RectI(149, 101, 7, 9);
        Change(source, dirty);
        var input = source.Pixels.ToArray(); var beforeMask = mask.Pixels.ToArray();
        var updated = LayerEffectsRenderer.Update(result, source, mask, effects, dirty);
        Assert.True(updated.Width * updated.Height < result.Width * result.Height);
        Assert.Equal(LayerEffectsRenderer.Render(source, mask, effects).Result.Pixels, result.Pixels);
        Assert.Equal(input, source.Pixels); Assert.Equal(beforeMask, mask.Pixels);
        Assert.True(updated.X <= dirty.X + inset && updated.Y <= dirty.Y + inset);
    }

    [Theory]
    [InlineData(1, 1, 500)]
    [InlineData(11, 7, 40)]
    [InlineData(160, 1, 8)]
    [InlineData(161, 129, 4)]
    [InlineData(501, 201, 8)]
    [InlineData(501, 201, 40)]
    public void RepeatedUpdatesPreserveGaussianChoiceAndGlobalBoundary(int width, int height, double blur)
    {
        var source = Pattern(width, height);
        var effects = new LayerEffects { InnerShadow = new() { Blur = blur, Distance = 37, Angle = 213 },
            InnerGlow = new() { Size = blur, Opacity = .63, Red = .8 } };
        var result = LayerEffectsRenderer.Render(source, null, effects).Result;
        foreach (var dirty in new[] { new RectI(0, 0, 1, 1), new(width / 2, height / 2, 1, 1), new(width - 1, height - 1, 1, 1) })
        {
            Change(source, dirty);
            LayerEffectsRenderer.Update(result, source, null, effects, dirty);
            Assert.Equal(LayerEffectsRenderer.Render(source, null, effects).Result.Pixels, result.Pixels);
        }
    }

    [Fact]
    public void LocalUpdateDoesNotAllocateDocumentSizedPlanes()
    {
        var source = Pattern(1024, 1024); var effects = Effects("shadow");
        var result = LayerEffectsRenderer.Render(source, null, effects).Result;
        var dirty = new RectI(500, 500, 8, 8); Change(source, dirty);
        LayerEffectsRenderer.Update(result, source, null, effects, dirty);
        long before = GC.GetAllocatedBytesForCurrentThread();
        LayerEffectsRenderer.Update(result, source, null, effects, dirty);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 2 * 1024 * 1024);
    }

    internal static LayerEffects Effects(string kind) => kind switch
    {
        "shadow" => new() { Shadow = new() { Blur = 8, Distance = 19, Angle = 213, Red = .7, Opacity = .8 } },
        "outer" => new() { OuterGlow = new() { Size = 8, Red = .7, Opacity = .8 } },
        "inner" => new() { InnerGlow = new() { Size = 8, Red = .7, Opacity = .8 } },
        "inner-shadow" => new() { InnerShadow = new() { Blur = 8, Distance = 19, Angle = 213, Red = .7, Opacity = .8 } },
        "outside" => new() { Stroke = new() { Size = 5, Red = .7, Opacity = .8 } },
        "inside" => new() { Stroke = new() { Size = 5, Inside = true, Red = .7, Opacity = .8 } },
        "overlay" => new() { ColorOverlay = new() { Red = .7, Opacity = .8 } },
        _ => new() { Shadow = new() { Blur = 40, Distance = 31, Angle = 17 }, OuterGlow = new() { Size = 8 },
            InnerShadow = new() { Blur = 4, Distance = 7 }, InnerGlow = new() { Size = 8 },
            Stroke = new() { Size = 5 }, ColorOverlay = new() { Red = .7, Opacity = .2 } },
    };

    internal static Surface Pattern(int width, int height)
    {
        int stride = width * 4 + 12;
        var source = new Surface(width, height, new byte[stride * height], stride); var random = new Random(73);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int p = y * stride + x * 4; int alpha = random.Next(256); source.Pixels[p + 3] = (byte)alpha;
            for (int c = 0; c < 3; c++) source.Pixels[p + c] = (byte)random.Next(alpha + 1);
        }
        return source;
    }

    internal static void Change(Surface source, RectI dirty)
    {
        for (int y = dirty.Y; y < dirty.Y + dirty.Height; y++)
        for (int x = dirty.X; x < dirty.X + dirty.Width; x++)
        {
            int p = y * source.Stride + x * 4;
            source.Pixels[p] = source.Pixels[p + 1] = source.Pixels[p + 2] = 0;
            source.Pixels[p + 3] = (byte)(255 - source.Pixels[p + 3]);
        }
    }
}
