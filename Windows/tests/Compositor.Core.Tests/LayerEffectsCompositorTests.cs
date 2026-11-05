using Xunit;
using System.Linq;
using System.Text.Json;

namespace Compositor.Core.Tests;

/// <summary>
/// Layer effects in the CPU compositor: a layer's effects render replaces its plain draw —
/// placed through the transform grown by the render's inset, following the layer's own mask
/// inside the render and its opacity, blend mode, folder and live masks around it — while
/// layers without effects draw exactly as they did before the effects path existed.
/// </summary>
public class LayerEffectsCompositorTests
{
    private static JsonElement EffectsJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static ProjectSnapshot Snapshot(int width, int height, params ProjectLayerRecord[] layers) =>
        new()
        {
            Manifest = new ProjectManifest
            {
                DocumentId = Guid.NewGuid(),
                Width = width,
                Height = height,
                Layers = [.. layers],
            },
            Images = new Dictionary<Guid, Surface>(),
            Masks = new Dictionary<Guid, MaskSurface>(),
        };

    private static ProjectLayerRecord Layer(string name, int x, int y, int width, int height)
    {
        var id = Guid.NewGuid();
        return new ProjectLayerRecord
        {
            Id = id,
            Name = name,
            IsVisible = true,
            ImageFile = ProjectValidator.Uuid(id) + ".png",
            Transform = new LayerTransform
            {
                Origin = new PointD(x, y),
                Size = new SizeD(width, height),
            },
        };
    }

    private static Surface Solid(int width, int height, byte r, byte g, byte b, byte a = 255)
    {
        var surface = new Surface(width, height);
        for (int row = 0; row < height; row++)
        {
            for (int column = 0; column < width; column++)
            {
                int index = row * surface.Stride + column * 4;
                surface.Pixels[index] = r;
                surface.Pixels[index + 1] = g;
                surface.Pixels[index + 2] = b;
                surface.Pixels[index + 3] = a;
            }
        }
        return surface;
    }

    private static void WithImage(ProjectSnapshot snapshot, ProjectLayerRecord layer, Surface image) =>
        ((Dictionary<Guid, Surface>)snapshot.Images)[layer.Id] = image;

    private static byte Pixel(Surface surface, int x, int y, int channel) =>
        surface.Pixels[y * surface.Stride + x * 4 + channel];

    /// <summary>A shadow straight down 2px, no blur, full-opacity pure red.</summary>
    private const string RedShadowDownTwo = """{"shadow":{"angle":90,"distance":2,"blur":0,"red":1,"green":0,"blue":0,"opacity":1}}""";

    [Fact]
    public void ShadowOnCanvasTintsTheAreaOutsideTheLayer()
    {
        // A 2×2 white layer at (2,2) on an 8×8 canvas; its shadow drops 2px below it.
        var layer = Layer("shadowed", 2, 2, 2, 2);
        layer.Effects = EffectsJson(RedShadowDownTwo);
        var snapshot = Snapshot(8, 8, layer);
        WithImage(snapshot, layer, Solid(2, 2, 255, 255, 255));

        var canvas = new CanvasCompositor(snapshot).Render();

        // The layer's own pixels: white, opaque.
        Assert.Equal(255, Pixel(canvas, 2, 2, 0));
        Assert.Equal(255, Pixel(canvas, 2, 2, 1));
        Assert.Equal(255, Pixel(canvas, 2, 2, 3));
        // Below the layer, outside its box: the pure shadow color, opaque.
        Assert.Equal(255, Pixel(canvas, 2, 4, 0));
        Assert.Equal(0, Pixel(canvas, 2, 4, 1));
        Assert.Equal(0, Pixel(canvas, 2, 4, 2));
        Assert.Equal(255, Pixel(canvas, 2, 4, 3));
        Assert.Equal(255, Pixel(canvas, 3, 5, 0));
        Assert.Equal(255, Pixel(canvas, 3, 5, 3));
        // Two rows below the layer: the shadow has stopped; the canvas is empty there.
        Assert.Equal(0, Pixel(canvas, 2, 6, 3));
        Assert.Equal(0, Pixel(canvas, 0, 0, 3));
        Assert.Equal(0, Pixel(canvas, 7, 7, 3));
    }

    [Fact]
    public void HalfOpacityLayerHalvesShadowAndPixelsTogether()
    {
        // The effects follow the layer's opacity: 50% halves the pixels and the shadow alike,
        // as LayerRenderer.swift draws the grown effects image with the layer's own opacity.
        var layer = Layer("shadowed", 2, 2, 2, 2);
        layer.Effects = EffectsJson(RedShadowDownTwo);
        layer.Opacity = 0.5;
        var snapshot = Snapshot(8, 8, layer);
        WithImage(snapshot, layer, Solid(2, 2, 255, 255, 255));

        var canvas = new CanvasCompositor(snapshot).Render();

        Assert.Equal(128, Pixel(canvas, 2, 2, 0)); // white at half: 255×0.5 = 127.5 → 128
        Assert.Equal(128, Pixel(canvas, 2, 2, 3));
        Assert.Equal(128, Pixel(canvas, 2, 4, 0)); // the shadow halves with it
        Assert.Equal(0, Pixel(canvas, 2, 4, 1));
        Assert.Equal(128, Pixel(canvas, 2, 4, 3));
    }

    [Fact]
    public void LayerWithoutEffectsRendersByteForByteAsBefore()
    {
        // The regression lock: no Effects JSON, an empty object, and a manifest that carries
        // only a disabled shadow all composite to the same bytes — the pre-effects render.
        var plain = Layer("plain", 2, 2, 2, 2);
        var snapshot = Snapshot(8, 8, plain);
        WithImage(snapshot, plain, Solid(2, 2, 255, 255, 255));
        byte[] without = new CanvasCompositor(snapshot).Render().Pixels.ToArray();

        plain.Effects = EffectsJson("{}");
        byte[] empty = new CanvasCompositor(snapshot).Render().Pixels.ToArray();

        plain.Effects = EffectsJson("""{"shadow":{"angle":90,"distance":2,"blur":0,"red":1,"green":0,"blue":0,"opacity":1,"enabled":false}}""");
        byte[] disabled = new CanvasCompositor(snapshot).Render().Pixels.ToArray();

        Assert.Equal(without, empty);
        Assert.Equal(without, disabled);
        // And those bytes are the plain layer's: white in its box, empty everywhere else.
        var flat = new Surface(8, 8, without, 8 * 4);
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                bool inside = x is >= 2 and < 4 && y is >= 2 and < 4;
                Assert.Equal(inside ? (byte)255 : (byte)0, Pixel(flat, x, y, 3));
                if (inside)
                {
                    Assert.Equal(255, Pixel(flat, x, y, 0));
                }
            }
        }
    }

    [Fact]
    public void CompositingWithEffectsIsReadOnlySoUndoIsNotInvolved()
    {
        // The compositor only reads the document: pixels, masks and the Effects JSON come out
        // untouched (no revision bumps), so undo has nothing to do with a composite, and
        // repeated renders — cached, cache-hit and fresh — agree byte for byte.
        var layer = Layer("shadowed", 2, 2, 2, 2);
        layer.Effects = EffectsJson(RedShadowDownTwo);
        layer.MaskFile = ProjectValidator.Uuid(layer.Id) + ".mask.png";
        var snapshot = Snapshot(8, 8, layer);
        WithImage(snapshot, layer, Solid(2, 2, 255, 255, 255));
        ((Dictionary<Guid, MaskSurface>)snapshot.Masks)[layer.Id] = new MaskSurface(2, 1, [255, 255]);
        var sourceBefore = snapshot.Images[layer.Id].Pixels.ToArray();
        var maskBefore = snapshot.Masks[layer.Id].Pixels.ToArray();

        var compositor = new CanvasCompositor(snapshot);
        byte[] first = compositor.Render().Pixels.ToArray();
        byte[] again = compositor.Render().Pixels.ToArray();
        byte[] fresh = new CanvasCompositor(snapshot).Render().Pixels.ToArray();

        Assert.Equal(first, again);
        Assert.Equal(first, fresh);
        Assert.Equal(sourceBefore, snapshot.Images[layer.Id].Pixels);
        Assert.Equal(0, snapshot.Images[layer.Id].Revision);
        Assert.Equal(maskBefore, snapshot.Masks[layer.Id].Pixels);
        Assert.Equal(0, snapshot.Masks[layer.Id].Revision);
        Assert.Equal(RedShadowDownTwo, layer.Effects!.Value.GetRawText());
    }

    [Fact]
    public void ShadowFollowsTheShownShapeThroughTheLayerMask()
    {
        // The layer's own mask goes into the effects render: a mask hiding the layer's left
        // column takes the shadow under it away too, instead of erasing the placed buffer
        // outside the mask's own rectangle.
        var layer = Layer("shadowed", 2, 2, 2, 2);
        layer.Effects = EffectsJson(RedShadowDownTwo);
        layer.MaskFile = ProjectValidator.Uuid(layer.Id) + ".mask.png";
        var snapshot = Snapshot(8, 8, layer);
        WithImage(snapshot, layer, Solid(2, 2, 255, 255, 255));
        ((Dictionary<Guid, MaskSurface>)snapshot.Masks)[layer.Id] = new MaskSurface(2, 1, [0, 255]);

        var canvas = new CanvasCompositor(snapshot).Render();

        // The shown shape is the right column only: pixels and shadow exist there alone.
        Assert.Equal(0, Pixel(canvas, 2, 2, 3)); // hidden left column: nothing at all
        Assert.Equal(255, Pixel(canvas, 3, 2, 0)); // shown right column: white
        Assert.Equal(255, Pixel(canvas, 3, 2, 3));
        Assert.Equal(0, Pixel(canvas, 2, 4, 3)); // no shadow under the hidden column
        Assert.Equal(255, Pixel(canvas, 3, 4, 0)); // red shadow under the shown one
        Assert.Equal(255, Pixel(canvas, 3, 4, 3));
    }

    [Fact]
    public void RegionRenderAfterAnEffectsMoveMatchesAFullRender()
    {
        // Moving an effects layer refreshes its grown bounds and invalidates its caches, so a
        // region re-composite over the moved footprint matches a fresh full render.
        var layer = Layer("shadowed", 2, 2, 2, 2);
        layer.Effects = EffectsJson(RedShadowDownTwo);
        var snapshot = Snapshot(12, 12, layer);
        WithImage(snapshot, layer, Solid(2, 2, 255, 255, 255));

        var compositor = new CanvasCompositor(snapshot);
        _ = compositor.Render(); // warm the caches

        snapshot.Manifest.Layers[0].Transform.Origin = new PointD(4, 4);
        var viaRegion = compositor.Render(0, 0, 12, 12);

        var viaFull = new CanvasCompositor(snapshot).Render();
        for (int i = 0; i < 12 * 12; i++)
        {
            Assert.True(
                viaRegion.Pixels.AsSpan(i * 4, 4).SequenceEqual(viaFull.Pixels.AsSpan(i * 4, 4)),
                $"pixel ({i % 12}, {i / 12}) differs between region and full render");
        }
    }

    // --- IsValid: the save path's gate (SetLayerEffects refuses an invalid set) ---

    [Fact]
    public void EffectKindsAloneAreValidWithDefaults()
    {
        // The regression this locks: a null Stroke used to sink the whole set, so no layer
        // without a stroke could ever save its effects. Absent kinds are tolerated, each
        // present one judged by its own ranges (LayerEffects.swift's isValid).
        Assert.True(new LayerEffects().IsValid);
        Assert.True(new LayerEffects { Stroke = new StrokeEffect() }.IsValid);
        Assert.True(new LayerEffects { Shadow = new ShadowEffect() }.IsValid);
        Assert.True(new LayerEffects { ColorOverlay = new ColorOverlayEffect() }.IsValid);
        Assert.True(new LayerEffects { InnerShadow = new InnerShadowEffect() }.IsValid);
        Assert.True(new LayerEffects { OuterGlow = new OuterGlowEffect() }.IsValid);
        Assert.True(new LayerEffects { InnerGlow = new InnerGlowEffect() }.IsValid);
    }

    [Fact]
    public void OutOfRangeEffectSettingsAreInvalid()
    {
        // Each kind's own bounds (LayerEffects.swift): stroke and glow size at most 500,
        // distance at most 5000, blur at most 500, angle within ±360, opacity and RGB in 0–1.
        Assert.False(new LayerEffects { Stroke = new StrokeEffect { Size = -1 } }.IsValid);
        Assert.False(new LayerEffects { Stroke = new StrokeEffect { Size = 501 } }.IsValid);
        Assert.False(new LayerEffects { Stroke = new StrokeEffect { Opacity = 1.01 } }.IsValid);
        Assert.False(new LayerEffects { Stroke = new StrokeEffect { Red = -0.01 } }.IsValid);
        Assert.False(new LayerEffects { Stroke = new StrokeEffect { Green = 1.01 } }.IsValid);
        Assert.False(new LayerEffects { Stroke = new StrokeEffect { Size = double.NaN } }.IsValid);
        Assert.False(new LayerEffects { Shadow = new ShadowEffect { Angle = -361 } }.IsValid);
        Assert.False(new LayerEffects { Shadow = new ShadowEffect { Angle = 361 } }.IsValid);
        Assert.False(new LayerEffects { Shadow = new ShadowEffect { Distance = -1 } }.IsValid);
        Assert.False(new LayerEffects { Shadow = new ShadowEffect { Distance = 5001 } }.IsValid);
        Assert.False(new LayerEffects { Shadow = new ShadowEffect { Blur = 501 } }.IsValid);
        Assert.False(new LayerEffects { Shadow = new ShadowEffect { Blue = 1.01 } }.IsValid);
        Assert.False(new LayerEffects { ColorOverlay = new ColorOverlayEffect { Opacity = -0.01 } }.IsValid);
        Assert.False(new LayerEffects { ColorOverlay = new ColorOverlayEffect { Blue = 2 } }.IsValid);
        Assert.False(new LayerEffects { InnerShadow = new InnerShadowEffect { Angle = 400 } }.IsValid);
        Assert.False(new LayerEffects { InnerShadow = new InnerShadowEffect { Distance = 5001 } }.IsValid);
        Assert.False(new LayerEffects { InnerShadow = new InnerShadowEffect { Blur = -1 } }.IsValid);
        Assert.False(new LayerEffects { OuterGlow = new OuterGlowEffect { Size = 501 } }.IsValid);
        Assert.False(new LayerEffects { InnerGlow = new InnerGlowEffect { Opacity = 1.5 } }.IsValid);
    }

    [Fact]
    public void BoundaryEffectSettingsAreValid()
    {
        // The ends of each range are as valid as the middle (LayerEffects.swift's closed ranges).
        Assert.True(new LayerEffects { Stroke = new StrokeEffect { Size = 0 } }.IsValid);
        Assert.True(new LayerEffects { Stroke = new StrokeEffect { Size = 500 } }.IsValid);
        Assert.True(new LayerEffects { Shadow = new ShadowEffect { Angle = -360, Distance = 0, Blur = 0 } }.IsValid);
        Assert.True(new LayerEffects { Shadow = new ShadowEffect { Angle = 360, Distance = 5000, Blur = 500 } }.IsValid);
        Assert.True(new LayerEffects { ColorOverlay = new ColorOverlayEffect { Opacity = 0 } }.IsValid);
        Assert.True(new LayerEffects { InnerShadow = new InnerShadowEffect { Angle = -360, Distance = 5000, Blur = 500 } }.IsValid);
        Assert.True(new LayerEffects { OuterGlow = new OuterGlowEffect { Size = 500 } }.IsValid);
        Assert.True(new LayerEffects { InnerGlow = new InnerGlowEffect { Size = 0, Opacity = 1 } }.IsValid);
    }

    // --- Clipping by a source with effects: LiveMaskRenderer.coverage's shape ---

    [Fact]
    public void ClippingMaskCoverageIncludesTheSourceEffects()
    {
        // LiveMaskRenderer.coverage extracts the alpha of the source drawn through
        // drawLiveComposite's effects path (LiveLayerMask.swift), so the source's shadow
        // spreads the clip: the target shows over the shadow rows too, not just the
        // source's own box.
        var source = Layer("source", 2, 2, 2, 2);
        source.Effects = EffectsJson(RedShadowDownTwo);
        var target = Layer("target", 0, 0, 8, 8);
        target.MaskSourceId = source.Id;
        var snapshot = Snapshot(8, 8, source, target);
        WithImage(snapshot, source, Solid(2, 2, 255, 255, 255));
        WithImage(snapshot, target, Solid(8, 8, 0, 0, 255));

        var canvas = new CanvasCompositor(snapshot).Render();

        Assert.Equal(255, Pixel(canvas, 2, 2, 2)); // over the source's box: blue
        Assert.Equal(255, Pixel(canvas, 2, 4, 2)); // over its shadow: blue as well — the fix
        Assert.Equal(0, Pixel(canvas, 2, 4, 0));   // the red shadow under the blue is covered
        Assert.Equal(255, Pixel(canvas, 3, 5, 3));
        Assert.Equal(0, Pixel(canvas, 2, 6, 3));   // the shadow stopped; so did the clip
        Assert.Equal(0, Pixel(canvas, 0, 0, 3));   // outside it all: nothing
    }

    [Fact]
    public void ClippingMaskCoverageFollowsTheSourceMaskInsideItsEffects()
    {
        // The source's own mask is baked into its effects render, so the clip follows the
        // shown shape alone: the hidden column's shadow clips nothing — and the mask is
        // not applied a second time around the render.
        var source = Layer("source", 2, 2, 2, 2);
        source.Effects = EffectsJson(RedShadowDownTwo);
        source.MaskFile = ProjectValidator.Uuid(source.Id) + ".mask.png";
        var target = Layer("target", 0, 0, 8, 8);
        target.MaskSourceId = source.Id;
        var snapshot = Snapshot(8, 8, source, target);
        WithImage(snapshot, source, Solid(2, 2, 255, 255, 255));
        WithImage(snapshot, target, Solid(8, 8, 0, 0, 255));
        ((Dictionary<Guid, MaskSurface>)snapshot.Masks)[source.Id] = new MaskSurface(2, 1, [0, 255]);

        var canvas = new CanvasCompositor(snapshot).Render();

        Assert.Equal(0, Pixel(canvas, 2, 2, 3));   // hidden column: nothing — source, shadow, clip
        Assert.Equal(255, Pixel(canvas, 3, 2, 2)); // shown column: blue over white
        Assert.Equal(0, Pixel(canvas, 2, 4, 3));   // no shadow, no coverage under the hidden column
        Assert.Equal(255, Pixel(canvas, 3, 4, 2)); // the shown column's shadow: blue over it
    }

    [Fact]
    public void ClippingByASourceWithoutRenderableEffectsUsesItsPlainAlpha()
    {
        // A source whose Effects JSON carries nothing renderable (an empty object, every
        // kind disabled) clips by its plain pixel alpha, byte for byte as a source with no
        // Effects JSON at all.
        var source = Layer("source", 2, 2, 2, 2);
        var target = Layer("target", 0, 0, 8, 8);
        target.MaskSourceId = source.Id;
        var snapshot = Snapshot(8, 8, source, target);
        WithImage(snapshot, source, Solid(2, 2, 255, 255, 255));
        WithImage(snapshot, target, Solid(8, 8, 0, 0, 255));
        byte[] plain = new CanvasCompositor(snapshot).Render().Pixels.ToArray();

        source.Effects = EffectsJson("{}");
        byte[] empty = new CanvasCompositor(snapshot).Render().Pixels.ToArray();

        source.Effects = EffectsJson("""{"shadow":{"angle":90,"distance":2,"blur":0,"red":1,"green":0,"blue":0,"opacity":1,"enabled":false}}""");
        byte[] disabled = new CanvasCompositor(snapshot).Render().Pixels.ToArray();

        Assert.Equal(plain, empty);
        Assert.Equal(plain, disabled);
        // And the plain clip: blue only over the source's own box, none where its shadow would be.
        var flat = new Surface(8, 8, plain, 8 * 4);
        Assert.Equal(255, Pixel(flat, 2, 2, 2));
        Assert.Equal(0, Pixel(flat, 2, 4, 3));
    }

    [Fact]
    public void ChangingTheSourceEffectsRefreshesTheClippedLayer()
    {
        // The clipped layer's cache keys on its source's effects render — the coverage it is
        // clipped by — so dropping the shadow retracts the clip on the next render of the
        // same compositor instead of keeping the stale one.
        var source = Layer("source", 2, 2, 2, 2);
        source.Effects = EffectsJson(RedShadowDownTwo);
        var target = Layer("target", 0, 0, 8, 8);
        target.MaskSourceId = source.Id;
        var snapshot = Snapshot(8, 8, source, target);
        WithImage(snapshot, source, Solid(2, 2, 255, 255, 255));
        WithImage(snapshot, target, Solid(8, 8, 0, 0, 255));

        var compositor = new CanvasCompositor(snapshot);
        _ = compositor.Render();
        Assert.Equal(255, Pixel(compositor.Render(), 2, 4, 2)); // clipped over the shadow

        source.Effects = EffectsJson("""{"shadow":{"angle":90,"distance":0,"blur":0,"red":1,"green":0,"blue":0,"opacity":1}}""");
        var canvas = compositor.Render();

        Assert.Equal(0, Pixel(canvas, 2, 4, 3));   // the shadow is gone; the clip went with it
        Assert.Equal(255, Pixel(canvas, 2, 2, 2)); // the source's own box still clips
    }
}
