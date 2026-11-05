using Xunit;

namespace Compositor.Core.Tests;

public class EffectRenderingPerformanceTests
{
    [Theory]
    [InlineData("outside")]
    [InlineData("inside")]
    [InlineData("inner-shadow")]
    [InlineData("inner-glow")]
    [InlineData("outer-glow")]
    [InlineData("overlay")]
    [InlineData("shadow")]
    public void EveryEffectUsesThePaddedGridAndLeavesInputsUntouched(string kind)
    {
        var source = Solid(8, 8, 0, 0, 255, 255); var before = source.Pixels.ToArray();
        var mask = new MaskSurface(2, 1, [0, 255]);
        var effects = kind switch
        {
            "outside" => new LayerEffects { Stroke = new() { Size = 2, Red = 1 } },
            "inside" => new LayerEffects { Stroke = new() { Size = 2, Red = 1, Inside = true } },
            "inner-shadow" => new LayerEffects { InnerShadow = new() { Blur = 8 } },
            "inner-glow" => new LayerEffects { InnerGlow = new() { Size = 8, Opacity = 1, Red = 1 } },
            "outer-glow" => new LayerEffects { OuterGlow = new() { Size = 8, Opacity = 1, Red = 1 } },
            "overlay" => new LayerEffects { ColorOverlay = new() { Red = 1 } },
            _ => new LayerEffects { Shadow = new() { Blur = 8 } },
        };
        var (result, inset) = LayerEffectsRenderer.Render(source, mask, effects);
        Assert.Equal(8 + inset * 2, result.Width); Assert.Equal(result.Width, result.Height);
        Assert.Equal(before, source.Pixels); Assert.Equal(new byte[] { 0, 255 }, mask.Pixels);
        Assert.Equal(0, source.Revision); Assert.Equal(0, mask.Revision);
        for (int i = 0; i < result.Pixels.Length; i += 4)
            for (int channel = 0; channel < 3; channel++) Assert.True(result.Pixels[i + channel] <= result.Pixels[i + 3]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StrokeIsTheSharpRingBetweenShapeAndMorphology(bool inside)
    {
        var (result, inset) = LayerEffectsRenderer.Render(Solid(12, 12, 0, 0, 255, 255), null,
            new() { Stroke = new() { Size = 2, Inside = inside, Red = 1 } });
        Assert.Equal(0, Pixel(result, inset + 6, inset + 6, 0));
        Assert.Equal(255, Pixel(result, inset + 6, inset + 6, 2));
        Assert.Equal(inside ? 255 : 0, Pixel(result, inset, inset + 6, 0));
        Assert.Equal(inside ? 0 : 255, Pixel(result, inset - 2, inset + 6, 0));
        Assert.Equal(0, Pixel(result, Math.Max(0, inset - 3), inset + 6, 3));
    }

    [Fact]
    public void OutsideStrokeDoesNotTintSemitransparentInterior()
    {
        var (result, inset) = LayerEffectsRenderer.Render(Solid(12, 12, 0, 0, 128, 128), null,
            new() { Stroke = new() { Size = 2, Red = 1 } });
        Assert.Equal(0, Pixel(result, inset + 6, inset + 6, 0));
        Assert.Equal(128, Pixel(result, inset + 6, inset + 6, 3));
        Assert.Equal(128, Pixel(result, inset - 1, inset + 6, 0));
    }

    [Fact]
    public void OpaqueRowCopyHonorsSourceStrideAndRevealMask()
    {
        const int width = 5, height = 3, stride = width * 4 + 16;
        var bytes = Enumerable.Repeat((byte)37, stride * height).ToArray();
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        { int p = y * stride + x * 4; bytes[p] = (byte)(x + y); bytes[p + 1] = 90; bytes[p + 2] = 120; bytes[p + 3] = 255; }
        var source = new Surface(width, height, bytes, stride);
        var (result, inset) = LayerEffectsRenderer.Render(source, MaskSurface.Solid(true), new() { Shadow = new() { Blur = 8 } });
        for (int y = 0; y < height; y++)
            Assert.Equal(bytes.AsSpan(y * stride, width * 4).ToArray(), result.Pixels.AsSpan((y + inset) * result.Stride + inset * 4, width * 4).ToArray());
        Assert.Equal(37, source.Pixels[width * 4]);
    }

    [Theory]
    [InlineData("inner-shadow")]
    [InlineData("inner-glow")]
    [InlineData("outer-glow")]
    [InlineData("overlay")]
    public void PartialAlphaEffectsUseMacMultiplicativeCoverage(string kind)
    {
        var effects = kind switch
        {
            "inner-shadow" => new LayerEffects { InnerShadow = new() { Blur = 0, Distance = 0, Opacity = 1 } },
            "inner-glow" => new LayerEffects { InnerGlow = new() { Size = 2, Red = 1, Opacity = 1 } },
            "outer-glow" => new LayerEffects { OuterGlow = new() { Size = 2, Red = 1, Opacity = 1 } },
            _ => new LayerEffects { ColorOverlay = new() { Red = 1, Opacity = 1 } },
        };
        var (result, inset) = LayerEffectsRenderer.Render(Solid(32, 32, 0, 0, 128, 128), null, effects);
        Assert.Equal(kind == "overlay" ? 192 : 160, Pixel(result, inset + 16, inset + 16, 3));
        if (kind is "inner-glow" or "inner-shadow") Assert.Equal(0, Pixel(result, inset - 1, inset + 16, 3));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(.1)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(40)]
    [InlineData(500)]
    public void GaussianMatchesMacSigmaAndBoundsApproximationError(double blur)
    {
        int width = blur >= 500 ? 1601 : 161, height = blur >= 500 ? 9 : 129;
        var input = new byte[width * height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++) input[y * width + x] = (byte)(x < width / 2 ? 0 : 255);
        var expected = ReferenceGaussian(input, width, height, blur / 2);
        var actual = input.ToArray(); EffectGaussian.Apply(actual, width, height, blur);
        if (blur <= 4) Assert.Equal(expected, actual);
        else
        {
            double max = 0, squared = 0;
            for (int i = 0; i < actual.Length; i++)
            { double delta = Math.Abs(actual[i] - expected[i]); max = Math.Max(max, delta); squared += delta * delta; }
            Assert.True(max <= 9, $"blur {blur}: max difference {max}");
            Assert.True(Math.Sqrt(squared / actual.Length) <= 3, $"blur {blur}: RMS {Math.Sqrt(squared / actual.Length)}");
        }
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 17)]
    [InlineData(19, 1)]
    [InlineData(15, 13)]
    public void LargeBlurPreservesConstantAtClampedEdges(int width, int height)
    {
        var input = Enumerable.Repeat((byte)137, width * height).ToArray();
        EffectGaussian.Apply(input, width, height, 500);
        Assert.All(input, value => Assert.Equal(137, value));
    }

    [Fact]
    public void GaussianImpulseIsSymmetricAndTranslationInvariantInsidePadding()
    {
        const int width = 101;
        var centered = new byte[width * width]; centered[50 * width + 50] = 255;
        var shifted = new byte[width * width]; shifted[53 * width + 52] = 255;
        EffectGaussian.Apply(centered, width, width, 8); EffectGaussian.Apply(shifted, width, width, 8);
        for (int y = 20; y <= 80; y++)
        for (int x = 20; x <= 80; x++)
        {
            Assert.Equal(centered[y * width + x], centered[y * width + 100 - x]);
            Assert.Equal(centered[y * width + x], centered[(100 - y) * width + x]);
            Assert.Equal(centered[y * width + x], shifted[(y + 3) * width + x + 2]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WarmEffectsCacheInvalidatesAfterPixelOrMaskRevision(bool maskPaint)
    {
        var doc = EditorDocument.Create(96, 96); var layer = doc.Snapshot.Manifest.Layers[0];
        doc.SetLayerEffects(layer.Id, new() { Shadow = new() { Blur = 8, Distance = 3 } });
        layer.MaskFile = "mask.png";
        var mask = new MaskSurface(96, 96); Array.Fill(mask.Pixels, (byte)255);
        ((Dictionary<Guid, MaskSurface>)doc.Snapshot.Masks)[layer.Id] = mask;
        var compositor = new CanvasCompositor(doc.Snapshot); var before = compositor.Render().Pixels.ToArray();
        if (maskPaint) { Array.Fill(mask.Pixels, (byte)0, 30 * 96, 96); mask.MarkModified(); }
        else { Array.Clear(doc.Snapshot.Images[layer.Id].Pixels, 30 * 96 * 4, 96 * 4); doc.Snapshot.Images[layer.Id].MarkModified(); }
        var after = compositor.Render();
        Assert.NotEqual(before, after.Pixels);
        Assert.Equal(new CanvasCompositor(doc.Snapshot).Render().Pixels, after.Pixels);
    }

    [Fact]
    public void CombinedEffectsRemainPremultipliedWithMaskAndTransparentPixels()
    {
        var source = Solid(12, 8, 30, 50, 100, 128);
        var (result, _) = LayerEffectsRenderer.Render(source, new MaskSurface(2, 1, [0, 255]), new()
        {
            Shadow = new() { Blur = 8 }, Stroke = new() { Size = 3, Inside = true, Red = .7 },
            ColorOverlay = new() { Green = .9, Opacity = .4 }, InnerGlow = new() { Size = 4, Opacity = .6 },
            InnerShadow = new() { Blur = 8 }, OuterGlow = new() { Size = 8, Opacity = .6 },
        });
        for (int i = 0; i < result.Pixels.Length; i += 4)
            for (int channel = 0; channel < 3; channel++) Assert.True(result.Pixels[i + channel] <= result.Pixels[i + 3]);
    }

    private static Surface Solid(int width, int height, byte r, byte g, byte b, byte a)
    {
        var result = new Surface(width, height);
        for (int i = 0; i < result.Pixels.Length; i += 4)
        { result.Pixels[i] = r; result.Pixels[i + 1] = g; result.Pixels[i + 2] = b; result.Pixels[i + 3] = a; }
        return result;
    }
    private static byte Pixel(Surface surface, int x, int y, int channel) => surface.Pixels[y * surface.Stride + x * 4 + channel];

    // Independent separable Gaussian reference: the Mac CPU path uses blur/2, with
    // clamped-to-extent input. Small-kernel precision remains locked byte for byte.
    internal static byte[] ReferenceGaussian(byte[] input, int width, int height, double sigma)
    {
        if (sigma <= 0) return input.ToArray();
        sigma = Math.Max(.1, sigma); int radius = (int)Math.Ceiling(3 * sigma);
        var kernel = Enumerable.Range(-radius, radius * 2 + 1).Select(x => Math.Exp(-x * (double)x / (2 * sigma * sigma))).ToArray();
        double weight = kernel.Sum(); for (int k = 0; k < kernel.Length; k++) kernel[k] /= weight;
        var horizontal = new byte[input.Length]; var result = new byte[input.Length];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            double value = 0;
            for (int k = -radius; k <= radius; k++) value += input[y * width + Math.Clamp(x + k, 0, width - 1)] * kernel[k + radius];
            horizontal[y * width + x] = (byte)Math.Round(value, MidpointRounding.AwayFromZero);
        }
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            double value = 0;
            for (int k = -radius; k <= radius; k++) value += horizontal[Math.Clamp(y + k, 0, height - 1) * width + x] * kernel[k + radius];
            result[y * width + x] = (byte)Math.Round(value, MidpointRounding.AwayFromZero);
        }
        return result;
    }
}
