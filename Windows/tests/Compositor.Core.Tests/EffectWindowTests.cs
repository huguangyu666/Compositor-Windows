using Xunit;

namespace Compositor.Core.Tests;

public class EffectWindowTests
{
    [Theory]
    [InlineData("shadow")]
    [InlineData("outer")]
    [InlineData("inner")]
    [InlineData("inner-shadow")]
    [InlineData("outside")]
    [InlineData("inside")]
    [InlineData("overlay")]
    [InlineData("stack")]
    public void RequestedWindowsMatchFullPlaneWithMaskStrideAndEdges(string kind)
    {
        var source = EffectRegionTests.Pattern(301, 211); var effects = EffectRegionTests.Effects(kind);
        var mask = new MaskSurface(17, 9); new Random(17).NextBytes(mask.Pixels);
        var full = LayerEffectsRenderer.Render(source, mask, effects).Result;
        var input = source.Pixels.ToArray(); var maskInput = mask.Pixels.ToArray();
        foreach (var output in new[] { new RectI(0, 0, 12, 13), new(140, 110, 17, 19),
            new(full.Width - 13, full.Height - 11, 13, 11) })
        {
            var (result, window) = LayerEffectsRenderer.RenderRegion(source, mask, effects, output);
            for (int y = output.Y; y < output.Y + output.Height; y++)
                Assert.Equal(full.Pixels.AsSpan(y * full.Stride + output.X * 4, output.Width * 4).ToArray(),
                    result.Pixels.AsSpan((y - window.Y) * result.Stride + (output.X - window.X) * 4, output.Width * 4).ToArray());
        }
        Assert.Equal(input, source.Pixels); Assert.Equal(maskInput, mask.Pixels);
    }

    [Theory]
    [InlineData(1, 1, 500)]
    [InlineData(160, 1, 8)]
    [InlineData(161, 129, 4)]
    [InlineData(501, 201, 40)]
    public void WindowKeepsFullPlaneGaussianChoice(int width, int height, double blur)
    {
        var source = EffectRegionTests.Pattern(width, height);
        var effects = new LayerEffects { InnerShadow = new() { Blur = blur, Distance = 37, Angle = 213 } };
        var full = LayerEffectsRenderer.Render(source, null, effects).Result;
        var output = new RectI(2 + width / 2, 2 + height / 2, 1, 1);
        var (result, window) = LayerEffectsRenderer.RenderRegion(source, null, effects, output);
        Assert.Equal(full.Pixels.AsSpan(output.Y * full.Stride + output.X * 4, 4).ToArray(),
            result.Pixels.AsSpan((output.Y - window.Y) * result.Stride + (output.X - window.X) * 4, 4).ToArray());
    }

    [Fact]
    public void RepeatedMovingWindowsReuseSmallScratchAndClearPreviousPixels()
    {
        var source = EffectRegionTests.Pattern(701, 501); var effects = EffectRegionTests.Effects("stack");
        var scratch = new EffectScratch(); Surface? previous = null;
        foreach (var output in new[] { new RectI(300, 250, 9, 11), new(310, 260, 10, 12), new(290, 240, 9, 11) })
        {
            EffectRegionTests.Change(source, new(output.X, output.Y, 4, 5));
            var (result, window) = LayerEffectsRenderer.RenderRegion(source, null, effects, output, scratch);
            if (previous is not null) Assert.Same(previous, result);
            previous = result;
            var full = LayerEffectsRenderer.Render(source, null, effects).Result;
            for (int y = output.Y; y < output.Y + output.Height; y++)
                Assert.Equal(full.Pixels.AsSpan(y * full.Stride + output.X * 4, output.Width * 4).ToArray(),
                    result.Pixels.AsSpan((y - window.Y) * result.Stride + (output.X - window.X) * 4, output.Width * 4).ToArray());
            Assert.True(EffectScratch.MaximumBytes(window.Width, window.Height) <= 2 * 1024 * 1024);
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        LayerEffectsRenderer.RenderRegion(source, null, effects, new(280, 230, 9, 11), scratch);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 4096);
    }

    [Theory]
    [InlineData(LayerSampling.Nearest)]
    [InlineData(LayerSampling.Smooth)]
    [InlineData(LayerSampling.HighQuality)]
    public void CroppedMemoryKeepsGlobalSamplingCoordinates(LayerSampling sampling)
    {
        var source = EffectRegionTests.Pattern(101, 79);
        foreach (double rotation in new[] { 0, 17, 90, 180 })
        foreach (bool flipX in new[] { false, true })
        foreach (bool flipY in new[] { false, true })
        foreach (var origin in new[] { new PointD(24, 18), new PointD(24.25, 18.75), new PointD(-12.5, 7.5) })
        foreach (var size in new[] { new SizeD(101, 79), new SizeD(163.7, 115.3), new SizeD(70.2, 49.9) })
        foreach (var region in new[] { new RectI(0, 0, 17, 19), new(73, 59, 13, 11), new(159, 121, 21, 19) })
        {
            var transform = new LayerTransform { Origin = origin, Size = size, Rotation = rotation,
                FlipX = flipX, FlipY = flipY, Sampling = sampling };
            var required = Resampler.RequiredSourceRegion(source.Width, source.Height, transform, region);
            var crop = new Surface(required.Width, required.Height, new byte[(required.Width * 4 + 8) * required.Height], required.Width * 4 + 8);
            for (int y = 0; y < crop.Height; y++)
                source.Pixels.AsSpan((y + required.Y) * source.Stride + required.X * 4, crop.Width * 4)
                    .CopyTo(crop.Pixels.AsSpan(y * crop.Stride, crop.Width * 4));
            var expected = new Surface(180, 140); var actual = new Surface(180, 140);
            Resampler.Draw(source, transform, expected, region.X, region.Y, region.X + region.Width, region.Y + region.Height);
            Resampler.Draw(new SourceWindow(crop, source.Width, source.Height, required.X, required.Y), transform,
                actual, region.X, region.Y, region.X + region.Width, region.Y + region.Height);
            Assert.Equal(expected.Pixels, actual.Pixels);
        }
    }
}
