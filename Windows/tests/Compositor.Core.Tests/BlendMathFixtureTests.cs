using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// Subtract, Divide and Soft Light had no tests at all: the audit trail says the direction of
/// Subtract and the continuity of Soft Light were each argued about twice, so the arithmetic is
/// pinned here with hand-computed values. The expected numbers below come from the formulas the
/// implementation cites — Core Image's CISubtractBlendMode (input minus backdrop) and the
/// W3C/Photoshop soft-light with its D(b) branch — worked out in fractions that are exact in
/// binary, not read back off the code. Cross-end pixels from a real Mac run are still separate
/// evidence (external item X01).
/// </summary>
public sealed class BlendMathFixtureTests
{
    [Theory]
    // clamp(source - backdrop, 0, 1): the layer takes away from the canvas beneath it.
    [InlineData(0.25f, 0.75f, 0.5f)]
    [InlineData(0.75f, 0.25f, 0f)]
    [InlineData(0f, 1f, 1f)]
    [InlineData(1f, 1f, 0f)]
    [InlineData(0.5f, 0.5f, 0f)]
    [InlineData(0.125f, 0.625f, 0.5f)]
    public void SubtractTakesTheCanvasFromTheLayer(float backdrop, float source, float expected)
        => Assert.Equal(expected, BlendMath.Blend(LayerBlendMode.Subtract, backdrop, source), 6);

    [Theory]
    // source == 0 means "nothing taken" and the reader maps it to white, not black.
    [InlineData(0.5f, 0f, 1f)]
    [InlineData(0f, 0f, 1f)]
    [InlineData(0.25f, 1f, 0.25f)]
    [InlineData(1f, 0.25f, 1f)]
    [InlineData(0.5f, 0.125f, 1f)]
    public void DivideIsBackdropOverLayerWithClamp(float backdrop, float source, float expected)
        => Assert.Equal(expected, BlendMath.Blend(LayerBlendMode.Divide, backdrop, source), 6);

    [Theory]
    // Cs <= 0.5: b + (2s - 1) * b * (1 - b), which the port writes as (1-2s)b^2 + 2sb.
    [InlineData(0.25f, 0.5f, 0.25f)]    // 2s = 1: (1-2s) vanishes, so the canvas is left alone
    [InlineData(0.5f, 0f, 0.25f)]       // a black source: 1*0.25 + 0 = 0.25, mid grey pushed down
    [InlineData(1f, 0.25f, 1f)]         // (1-0.5)*1 + 0.5*1 = 1: a dark source cannot dim pure white
    [InlineData(0.25f, 0.25f, 0.15625f)]// (1-0.5)*0.0625 + 0.5*0.25 = 0.03125 + 0.125
    // Cs > 0.5: b + (2s - 1) * (D(b) - b), with D(b) = ((16b - 12)b + 4)b below a quarter.
    [InlineData(0.25f, 0.75f, 0.375f)]  // D(0.25) = ((4-12)*0.25 + 4)*0.25 = 0.5, so 0.25 + 0.5*0.25
    [InlineData(0.125f, 0.875f, 0.2890625f)] // D(0.125) = 0.34375, so 0.125 + 0.75*0.21875
    [InlineData(0.64f, 0.75f, 0.72f)]   // D(0.64) = sqrt(0.64) = 0.8, so 0.64 + 0.5*0.16
    [InlineData(0.5f, 0.5f, 0.5f)]      // continuity at the branch point (the a59f3a6 fix)
    public void SoftLightFollowsTheW3cCurve(float backdrop, float source, float expected)
        => Assert.Equal(expected, BlendMath.Blend(LayerBlendMode.SoftLight, backdrop, source), 6);

    [Fact]
    public void SoftLightNeverLeavesTheUnitRangeOrFlipsDirection()
    {
        // A dark source may only push the canvas down, a light one only up, and neither may
        // overshoot the unit range — the property the two branches of the curve exist to hold.
        for (int b = 0; b <= 100; b++)
        {
            float backdrop = b / 100f;
            for (int s = 1; s <= 50; s++)
                Assert.True(BlendMath.Blend(LayerBlendMode.SoftLight, backdrop, s / 100f) <= backdrop + 1e-5f,
                    $"soft light with source {s / 100f} brightened backdrop {backdrop}");
            for (int s = 51; s <= 100; s++)
                Assert.True(BlendMath.Blend(LayerBlendMode.SoftLight, backdrop, s / 100f) >= backdrop - 1e-5f,
                    $"soft light with source {s / 100f} darkened backdrop {backdrop}");
            Assert.InRange(BlendMath.Blend(LayerBlendMode.SoftLight, backdrop, 0.13f), 0f, 1f);
            Assert.InRange(BlendMath.Blend(LayerBlendMode.SoftLight, backdrop, 0.87f), 0f, 1f);
        }
    }

    [Fact]
    public void SubtractiveModesClampInsideTheCanvas()
    {
        foreach (float backdrop in new[] { 0f, 0.2f, 0.75f, 1f })
        foreach (float source in new[] { 0f, 0.3f, 0.6f, 1f })
        {
            Assert.InRange(BlendMath.Blend(LayerBlendMode.Subtract, backdrop, source), 0f, 1f);
            Assert.True(BlendMath.Blend(LayerBlendMode.Subtract, backdrop, source) == Math.Clamp(source - backdrop, 0f, 1f),
                $"subtract did not take the canvas ({backdrop}) away from the layer ({source})");
        }
    }

    [Fact]
    public void CompositorAppliesSubtractLayerOverCanvas()
    {
        // End to end, so the argument order the compositor hands BlendMath is pinned too: the
        // canvas is the backdrop and the layer is the input, matching the Mac's Core Image call.
        int side = 4;
        var canvas = Solid(side, 200, 200, 200);
        var layer = Solid(side, 60, 60, 60);
        var background = Layer("Background", canvas);
        var top = Layer("Takes away", layer, LayerBlendMode.Subtract);
        var snapshot = new ProjectSnapshot
        {
            Manifest = Manifest(side, background, top),
            Images = new Dictionary<Guid, Surface> { [background.Id] = canvas, [top.Id] = layer },
            Masks = new Dictionary<Guid, MaskSurface>(),
        };
        var result = new CanvasCompositor(snapshot).Render();
        // Straight colours before premultiplication: 0.2353 - 0.7843 clamps to 0, so the composite
        // is black wherever the layer is opaque.
        for (int i = 0; i < result.Pixels.Length; i += 4)
        {
            Assert.Equal(0, result.Pixels[i]);
            Assert.Equal(0, result.Pixels[i + 1]);
            Assert.Equal(0, result.Pixels[i + 2]);
            Assert.Equal(255, result.Pixels[i + 3]);
        }
    }

    private static Surface Solid(int side, byte r, byte g, byte b)
    {
        var surface = new Surface(side, side);
        for (int i = 0; i < surface.Pixels.Length; i += 4)
        {
            surface.Pixels[i] = r;
            surface.Pixels[i + 1] = g;
            surface.Pixels[i + 2] = b;
            surface.Pixels[i + 3] = 255;
        }
        return surface;
    }

    private static ProjectLayerRecord Layer(string name, Surface image, LayerBlendMode mode = LayerBlendMode.Normal)
    {
        var id = Guid.NewGuid();
        return new ProjectLayerRecord
        {
            Id = id,
            Name = name,
            IsVisible = true,
            Opacity = 1,
            BlendMode = mode,
            ImageFile = ProjectValidator.Uuid(id) + ".png",
            Transform = new LayerTransform { Origin = new PointD(0, 0), Size = new SizeD(image.Width, image.Height) },
        };
    }

    private static ProjectManifest Manifest(int side, params ProjectLayerRecord[] layers)
    {
        // The manifest lists the backdrop first, exactly as the canvas stacks it.
        var manifest = new ProjectManifest
        {
            DocumentId = Guid.NewGuid(),
            Width = side,
            Height = side,
            ActiveLayerId = layers[^1].Id,
            Layers = [.. layers],
        };
        return manifest;
    }
}
