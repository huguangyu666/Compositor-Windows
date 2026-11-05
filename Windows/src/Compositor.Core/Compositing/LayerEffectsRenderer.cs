using System.Text.Json;

namespace Compositor.Core;

/// <summary>
/// The layer-effects renderer, ported from LayerEffectsRenderer.swift's CPU path: shadow,
/// outer glow, stroke (outside/inside), color overlay, inner glow and inner shadow, drawn
/// around the layer's shown shape (its pixels through its mask) with a margin for spread.
/// Sampling is nearest-neighbor from alpha coverages; large blurs use a sliding-box
/// Gaussian approximation, while small kernels stay exact.
/// </summary>
public static class LayerEffectsRenderer
{
#if DEBUG
    // Attribution for the 4K paint measurement: which entry point owns a frame's allocations.
    public static long RenderRegionBytes, RenderBytes, UpdateBytes;
    public static double DebugWindowMs, DebugBlurMs; public static int DebugBlurCalls;
#endif
    /// <summary>Updates an existing effects surface after a known source-grid edit.</summary>
    public static RectI Update(Surface result, Surface shown, MaskSurface? mask, LayerEffects effects, RectI sourceDirty)
        => Update(result, shown, mask, effects, sourceDirty, null);

    internal static RectI Update(Surface result, Surface shown, MaskSurface? mask, LayerEffects effects, RectI sourceDirty, EffectScratch? scratch)
    {
        int inset = Margin(effects);
        if (result.Width != shown.Width + inset * 2 || result.Height != shown.Height + inset * 2)
            throw new ArgumentException("The effects surface no longer fits the source and settings.", nameof(result));
        var dirty = RectI.Normalize(sourceDirty.X, sourceDirty.Y, sourceDirty.X + sourceDirty.Width,
            sourceDirty.Y + sourceDirty.Height, shown.Width, shown.Height);
        if (dirty.Width == 0 || dirty.Height == 0) return new(0, 0, 0, 0);
        int reach = Reach(effects, result.Width, result.Height);
        var output = Grow(new(dirty.X + inset, dirty.Y + inset, dirty.Width, dirty.Height), reach, result.Width, result.Height);
        // The output halo covers changed pixels; the second halo supplies all of its inputs.
        // Clip at the original plane edge so Gaussian clamping stays identical to a full render.
        var window = Grow(output, reach, result.Width, result.Height);
        if (scratch is not null && EffectScratch.MaximumBytes(window.Width, window.Height) > 2 * 1024 * 1024) scratch = null;
        scratch?.Resize(window.Width, window.Height);
        var fresh = RenderWindow(shown, mask, effects, inset, window, scratch);
        for (int y = output.Y; y < output.Y + output.Height; y++)
            fresh.Pixels.AsSpan((y - window.Y) * fresh.Stride + (output.X - window.X) * 4, output.Width * 4)
                .CopyTo(result.Pixels.AsSpan(y * result.Stride + output.X * 4, output.Width * 4));
        result.MarkModified();
        return output;
    }

    private static RectI Grow(RectI rect, int reach, int width, int height) => RectI.Normalize(
        rect.X - reach, rect.Y - reach, rect.X + rect.Width + reach, rect.Y + rect.Height + reach, width, height);

    internal static RectI RegionWindow(Surface shown, LayerEffects effects, RectI output)
    {
        int inset = Margin(effects), width = shown.Width + inset * 2, height = shown.Height + inset * 2;
        var required = Grow(output, Reach(effects, width, height), width, height);
        // A one-pixel tap-bound fluctuation during motion must not rebuild every plane.
        // Round memory dimensions only; output coordinates and full-plane filters stay exact.
        int windowWidth = Math.Min(width, (required.Width + 15) / 16 * 16);
        int windowHeight = Math.Min(height, (required.Height + 15) / 16 * 16);
        return new(Math.Min(required.X, width - windowWidth), Math.Min(required.Y, height - windowHeight), windowWidth, windowHeight);
    }

    /// <summary>Renders only the inputs needed by output, in the original padded plane's
    /// coordinates. Pixels outside output are workspace; callers must not sample them.</summary>
    internal static (Surface Result, RectI Window) RenderRegion(Surface shown, MaskSurface? mask,
        LayerEffects effects, RectI output, EffectScratch? scratch = null)
    {
#if DEBUG
        long regionBefore = GC.GetAllocatedBytesForCurrentThread();
#endif
        var window = RegionWindow(shown, effects, output);
        if (scratch is not null && EffectScratch.MaximumBytes(window.Width, window.Height) > 2 * 1024 * 1024) scratch = null;
        scratch?.Resize(window.Width, window.Height);
        var regionResult = (RenderWindow(shown, mask, effects, Margin(effects), window, scratch), window);
#if DEBUG
        RenderRegionBytes += GC.GetAllocatedBytesForCurrentThread() - regionBefore;
#endif
        return regionResult;
    }

    internal static int Reach(LayerEffects effects, int width, int height)
    {
        int reach = 0;
        if (effects.Stroke is { } stroke) reach = Math.Max(reach, Math.Max(1, (int)Math.Round(stroke.Size, MidpointRounding.AwayFromZero)));
        if (effects.Shadow is { } shadow) reach = Math.Max(reach, ShadowReach(shadow.Blur, shadow.Offset));
        if (effects.InnerShadow is { } inner) reach = Math.Max(reach, ShadowReach(inner.Blur, inner.Offset));
        if (effects.OuterGlow is { } outer) reach = Math.Max(reach, EffectGaussian.Reach(outer.Size, width, height));
        if (effects.InnerGlow is { } glow) reach = Math.Max(reach, EffectGaussian.Reach(glow.Size, width, height));
        return reach;

        /// <summary>
    /// The coverage shift a shadow's offset produces. The macOS path hands Core Graphics a
    /// fractional <c>offsetBy(dx:dy:)</c> (Document/LayerEffects.swift:456, :533) and the rasterizer
    /// decides, so rounding to whole pixels here is an approximation with no exact counterpart to
    /// match — deliberately not "corrected" to away-from-zero without a reference byte to check it.
    /// </summary>
    int ShadowReach(double blur, (double X, double Y) offset) => EffectGaussian.Reach(blur, width, height)
            + (int)Math.Max(Math.Abs(Math.Round(offset.X)), Math.Abs(Math.Round(offset.Y)));
    }

    /// <summary>
    /// The manifest's Effects JSON as the renderer takes it: parsed, with the kinds it marks
    /// disabled (<c>enabled: false</c>) dropped — the macOS <c>visible</c> filter, where a
    /// missing enabled means visible. Null when nothing visible remains.
    /// </summary>
    public static LayerEffects? Visible(JsonElement? effects)
    {
        var parsed = LayerEffects.Parse(effects);
        if (parsed is null || effects is not { } element || element.ValueKind != JsonValueKind.Object) return parsed;
        if (Disabled(element, "stroke")) parsed.Stroke = null;
        if (Disabled(element, "shadow")) parsed.Shadow = null;
        if (Disabled(element, "colorOverlay")) parsed.ColorOverlay = null;
        if (Disabled(element, "innerShadow")) parsed.InnerShadow = null;
        if (Disabled(element, "outerGlow")) parsed.OuterGlow = null;
        if (Disabled(element, "innerGlow")) parsed.InnerGlow = null;
        return parsed.IsEmpty ? null : parsed;
    }

    private static bool Disabled(JsonElement element, string kind) =>
        element.TryGetProperty(kind, out var value) && value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty("enabled", out var flag) && flag.ValueKind == JsonValueKind.False;

    /// <summary>The margin the effects need around the layer, in canvas pixels.</summary>
    public static int Margin(LayerEffects effects)
    {
        double margin = 0;
        if (effects.Stroke is { } stroke && !stroke.Inside) margin = Math.Max(margin, stroke.Size);
        if (effects.Shadow is { } shadow) margin = Math.Max(margin, shadow.Distance + shadow.Blur * 3);
        if (effects.OuterGlow is { } glow) margin = Math.Max(margin, glow.Size * 3);
        return (int)Math.Ceiling(margin) + 2;
    }

    /// <summary>
    /// Renders `shown` (the layer as displayed, premultiplied RGBA) with `effects` into a
    /// bigger surface with `inset` pixels of room on every side. The layer's own mask —
    /// stretched over the source grid, as the macOS render clips by it — hides part of the
    /// layer before the effects are made, so they follow the shape that is actually shown.
    /// The layer's own pixels are drawn last over the outside effects, then the inside
    /// effects over them; the result replaces the layer wholesale at draw time.
    /// </summary>
    public static (Surface Result, int Inset) Render(Surface shown, MaskSurface? mask, LayerEffects effects)
    {
        int inset = Margin(effects);
#if DEBUG
        long fullBefore = GC.GetAllocatedBytesForCurrentThread();
#endif
        var fullResult = (RenderWindow(shown, mask, effects, inset, new(0, 0, shown.Width + inset * 2, shown.Height + inset * 2)), inset);
#if DEBUG
        RenderBytes += GC.GetAllocatedBytesForCurrentThread() - fullBefore;
#endif
        return fullResult;
    }

    private static Surface RenderWindow(Surface shown, MaskSurface? mask, LayerEffects effects, int inset, RectI window, EffectScratch? scratch = null)
    {
#if DEBUG
        long rw0 = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        int width = window.Width, height = window.Height;
        var result = scratch?.Result ?? new Surface(width, height);
        if (scratch is not null) Array.Clear(result.Pixels);

        // The layer's shown shape: its pixels through its mask, as an alpha coverage + straight RGB.
        var shapeAlpha = CoverageAlpha(shown, mask, window, inset, 0, 0, out var maskOnly, out bool fullyOpaque,
            includeMask: true, reuse: scratch?.Shape, reuseMask: mask is null ? null : scratch?.Mask);
        void Blur(byte[] coverage, double blur, bool borrowOutput = false)
        {
            // Zero stays zero under convolution. Continue the caller's effect math:
            // an empty shifted plane can still create a visible inner shadow.
            if (blur > 0 && double.IsFinite(blur) && coverage.AsSpan().IndexOfAnyExcept((byte)0) < 0) return;
            borrowOutput &= EffectGaussian.UsesFloatWorkspace(blur, shown.Width + inset * 2, shown.Height + inset * 2);
#if DEBUG
            long b0 = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
            try
            {
                EffectGaussian.ApplyWindow(coverage, width, height, blur, shown.Width + inset * 2, shown.Height + inset * 2,
                    scratch, borrowOutput ? result.Pixels : null);
            }
            finally
            {
                // Only a still-blank outside-effect output is lent. Restore its zero
                // bytes before source-over or the black-shadow alpha-only fill reads it.
                if (borrowOutput) Array.Clear(result.Pixels);
            }
#if DEBUG
            DebugBlurMs += (System.Diagnostics.Stopwatch.GetTimestamp() - b0) * 1000d / System.Diagnostics.Stopwatch.Frequency;
            DebugBlurCalls++;
#endif
        }
        byte[] ShapeCopy()
        {
            var copy = scratch?.Coverage ?? new byte[shapeAlpha.Length]; shapeAlpha.CopyTo(copy, 0); return copy;
        }

        if (effects.Shadow is { } shadow && shadow.Opacity > 0)
        {
            var (dx, dy) = shadow.Offset;
            var coverage = CoverageAlpha(shown, mask, window, inset, (int)Math.Round(dx), (int)Math.Round(dy), out _, out _, reuse: scratch?.Coverage);
            Blur(coverage, shadow.Blur, borrowOutput: true);
            FillColor(result, coverage, effects.Color(LayerEffectKind.Shadow), effects.Opacity(LayerEffectKind.Shadow), replace: true);
        }
        if (effects.OuterGlow is { } glow && glow.Opacity > 0 && glow.Size > 0)
        {
            var coverage = ShapeCopy();
            Blur(coverage, glow.Size, borrowOutput: effects.Shadow is not { Opacity: > 0 });
            for (int i = 0; i < coverage.Length; i++) coverage[i] = Product(coverage[i], 255 - shapeAlpha[i]);
            FillColor(result, coverage, effects.Color(LayerEffectKind.OuterGlow), glow.Opacity);
        }
        if (effects.Stroke is { Inside: false } stroke && stroke.Size > 0 && stroke.Opacity > 0)
        {
            // Mac strokeCoverage: a sharp square ring, grown shape minus original shape.
            var coverage = ShapeCopy();
            Extreme(coverage, width, height, Math.Max(1, (int)Math.Round(stroke.Size, MidpointRounding.AwayFromZero)), smallest: false, scratch);
            for (int i = 0; i < coverage.Length; i++) coverage[i] = (byte)Math.Max(0, coverage[i] - shapeAlpha[i]);
            FillColor(result, coverage, effects.Color(LayerEffectKind.Stroke), stroke.Opacity);
        }
        Blit(result, shapeAlpha, maskOnly, shown, window, inset, fullyOpaque);

        if (effects.ColorOverlay is { } overlay && overlay.Opacity > 0)
        {
            // Same byte rule as LayerEffects.Color: ties break away from zero, as Swift's rounded().
            FillColor(result, shapeAlpha,
                ((byte)Math.Round(overlay.Red * 255, MidpointRounding.AwayFromZero),
                 (byte)Math.Round(overlay.Green * 255, MidpointRounding.AwayFromZero),
                 (byte)Math.Round(overlay.Blue * 255, MidpointRounding.AwayFromZero)), overlay.Opacity);
        }
        if (effects.InnerGlow is { } innerGlow && innerGlow.Opacity > 0 && innerGlow.Size > 0)
        {
            var coverage = ShapeCopy();
            Blur(coverage, innerGlow.Size);
            for (int i = 0; i < coverage.Length; i++) coverage[i] = Product(shapeAlpha[i], 255 - coverage[i]);
            FillColor(result, coverage, effects.Color(LayerEffectKind.InnerGlow), innerGlow.Opacity);
        }
        if (effects.InnerShadow is { } inner && inner.Opacity > 0)
        {
            var (dx, dy) = inner.Offset;
            // The inner shadow darkens where the shifted shape fails to cover the layer.
            var shifted = CoverageAlpha(shown, mask, window, inset, (int)Math.Round(dx), (int)Math.Round(dy), out _, out _, reuse: scratch?.Coverage);
            Blur(shifted, inner.Blur);
            var coverage = shifted;
            for (int i = 0; i < coverage.Length; i++)
            {
                coverage[i] = Product(shapeAlpha[i], 255 - shifted[i]);
            }
            FillColor(result, coverage, effects.Color(LayerEffectKind.InnerShadow), inner.Opacity);
        }
        if (effects.Stroke is { } insideStroke && insideStroke.Inside && insideStroke.Size > 0 && insideStroke.Opacity > 0)
        {
            var coverage = ShapeCopy();
            Extreme(coverage, width, height, Math.Max(1, (int)Math.Round(insideStroke.Size, MidpointRounding.AwayFromZero)), smallest: true, scratch);
            for (int i = 0; i < coverage.Length; i++) coverage[i] = (byte)Math.Max(0, shapeAlpha[i] - coverage[i]);
            FillColor(result, coverage, effects.Color(LayerEffectKind.Stroke), insideStroke.Opacity);
        }
#if DEBUG
        DebugWindowMs += (System.Diagnostics.Stopwatch.GetTimestamp() - rw0) * 1000d / System.Diagnostics.Stopwatch.Frequency;
#endif
        return result;
    }

    /// <summary>The layer's shown alpha coverage: pixels through the mask, at layer
    /// resolution. The mask is stretched over the whole grid, as the macOS render clips by
    /// it in the image's own rect; <paramref name="maskOnly"/> comes back holding the
    /// stretched mask alone, for drawing the pixels over the effects beneath.</summary>
    private static byte[] CoverageAlpha(Surface shown, MaskSurface? mask, RectI window, int inset, int dx, int dy,
        out byte[]? maskOnly, out bool fullyOpaque, bool includeMask = false, byte[]? reuse = null, byte[]? reuseMask = null)
    {
        fullyOpaque = true;
        var coverage = reuse ?? new byte[window.Width * window.Height];
        if (reuse is not null) Array.Clear(coverage);
        maskOnly = null;
        if (mask is not null && includeMask)
        {
            maskOnly = reuseMask ?? new byte[coverage.Length];
            if (reuseMask is not null) Array.Clear(maskOnly);
        }
        // A fully hidden mask also makes shifted source coverage zero. Both planes
        // have already been cleared; never let Blit take the opaque-copy shortcut.
        if (mask is { Width: > 0, Height: > 0 } && mask.Pixels.AsSpan().IndexOfAnyExcept((byte)0) < 0)
        { fullyOpaque = false; return coverage; }
        for (int y = 0; y < window.Height; y++)
        {
            int sy = window.Y + y - inset - dy;
            if ((uint)sy >= (uint)shown.Height) continue;
            int my = mask is { } ? Math.Clamp((int)((sy + 0.5) * mask.Height / shown.Height), 0, mask.Height - 1) : 0;
            for (int x = 0; x < window.Width; x++)
            {
                int sx = window.X + x - inset - dx;
                if ((uint)sx >= (uint)shown.Width) continue;
                int index = sy * shown.Stride + sx * 4;
                byte alpha = shown.Pixels[index + 3];
                if (mask is { })
                {
                    int mx = Math.Clamp((int)((sx + 0.5) * mask.Width / shown.Width), 0, mask.Width - 1);
                    byte value = mask.Pixels[my * mask.Width + mx];
                    if (maskOnly is not null) maskOnly[y * window.Width + x] = value;
                    alpha = (byte)(alpha * value / 255);
                }
                coverage[y * window.Width + x] = alpha;
                if (alpha != 255) fullyOpaque = false;
            }
        }
        return coverage;
    }

    private static byte Product(int left, int right) => (byte)((left * right + 127) / 255);

    /// <summary>LayerEffectsRenderer.extreme: two monotonic-deque sweeps, independent
    /// of stroke reach. Erosion samples outside the plane as zero, like the Mac.</summary>
    private static void Extreme(byte[] coverage, int width, int height, int radius, bool smallest, EffectScratch? scratch = null)
    {
        var horizontal = scratch?.Horizontal ?? new byte[coverage.Length]; var queue = scratch?.Queue ?? new int[Math.Max(width, height)];
        void Sweep(byte[] source, byte[] destination, int lines, int count, int lineStep, int elementStep)
        {
            for (int line = 0; line < lines; line++)
            {
                int start = line * lineStep, head = 0, tail = 0, next = 0;
                for (int center = 0; center < count; center++)
                {
                    while (next <= Math.Min(count - 1, center + radius))
                    {
                        byte value = source[start + next * elementStep];
                        while (tail > head)
                        {
                            byte previous = source[start + queue[tail - 1] * elementStep];
                            if (smallest ? previous < value : previous > value) break;
                            tail--;
                        }
                        queue[tail++] = next++;
                    }
                    while (head < tail && queue[head] < center - radius) head++;
                    bool outside = center < radius || center + radius >= count;
                    destination[start + center * elementStep] = smallest && outside ? (byte)0 : source[start + queue[head] * elementStep];
                }
            }
        }
        Sweep(coverage, horizontal, height, width, width, 1);
        Sweep(horizontal, coverage, width, height, 1, width);
    }

    private static void FillColor(Surface result, byte[] coverage, (byte R, byte G, byte B) color, double opacity, bool replace = false)
    {
        if (replace)
        {
            // The first shadow fills a new transparent surface; black needs only alpha.
            bool black = color == (0, 0, 0);
            for (int y = 0; y < result.Height; y++)
            for (int x = 0; x < result.Width; x++)
            {
                int index = y * result.Stride + x * 4;
                double value = coverage[y * result.Width + x] * opacity;
                result.Pixels[index + 3] = (byte)Math.Round(value, MidpointRounding.AwayFromZero);
                if (black) continue;
                result.Pixels[index] = (byte)Math.Round(color.R * value / 255, MidpointRounding.AwayFromZero);
                result.Pixels[index + 1] = (byte)Math.Round(color.G * value / 255, MidpointRounding.AwayFromZero);
                result.Pixels[index + 2] = (byte)Math.Round(color.B * value / 255, MidpointRounding.AwayFromZero);
            }
            return;
        }
        for (int y = 0; y < result.Height; y++)
        {
            int row = y * result.Stride;
            for (int x = 0; x < result.Width; x++)
            {
                int index = row + x * 4;
                // Source-over the effect color at coverage × opacity.
                double a = coverage[y * result.Width + x] / 255.0 * opacity;
                result.Pixels[index] = (byte)Math.Round(color.R * a + result.Pixels[index] * (1 - a), MidpointRounding.AwayFromZero);
                result.Pixels[index + 1] = (byte)Math.Round(color.G * a + result.Pixels[index + 1] * (1 - a), MidpointRounding.AwayFromZero);
                result.Pixels[index + 2] = (byte)Math.Round(color.B * a + result.Pixels[index + 2] * (1 - a), MidpointRounding.AwayFromZero);
                result.Pixels[index + 3] = (byte)Math.Round(Math.Min(255, result.Pixels[index + 3] + 255 * a * (1 - result.Pixels[index + 3] / 255.0)), MidpointRounding.AwayFromZero);
            }
        }
    }

    /// <summary>Draws the layer's shown pixels (through its mask) over the result. The
    /// source is premultiplied by its own alpha already, so only the mask scales it again —
    /// multiplying by the shown alpha as well would darken semi-transparent pixels, which
    /// the macOS draw (a plain source-over of the masked image) does not do.</summary>
    private static void Blit(Surface result, byte[] shapeAlpha, byte[]? maskOnly, Surface shown, RectI window, int inset, bool fullyOpaque)
    {
        int x0 = Math.Max(0, inset - window.X), y0 = Math.Max(0, inset - window.Y);
        int x1 = Math.Min(window.Width, inset + shown.Width - window.X), y1 = Math.Min(window.Height, inset + shown.Height - window.Y);
        if (fullyOpaque)
        {
            if (x1 <= x0) return;
            for (int y = y0; y < y1; y++)
                shown.Pixels.AsSpan((y + window.Y - inset) * shown.Stride + (x0 + window.X - inset) * 4, (x1 - x0) * 4)
                    .CopyTo(result.Pixels.AsSpan(y * result.Stride + x0 * 4, (x1 - x0) * 4));
            return;
        }
        for (int y = y0; y < y1; y++)
        {
            int outRow = y * result.Stride;
            int inRow = (y + window.Y - inset) * shown.Stride;
            for (int x = x0; x < x1; x++)
            {
                int outIndex = outRow + x * 4;
                int inIndex = inRow + (x + window.X - inset) * 4;
                int alpha = shapeAlpha[y * window.Width + x];
                if (alpha == 0) continue;
                byte mask = maskOnly is null ? (byte)255 : maskOnly[y * window.Width + x];
                int remaining = 255 - alpha;
                for (int c = 0; c < 4; c++)
                {
                    // Source-over: the layer's own pixels over whatever effects are beneath.
                    // Byte fractions over 255 have no half ties. Keep the same nearest
                    // rounding and unchecked byte conversion without floating-point work.
                    int value = shown.Pixels[inIndex + c] * mask + result.Pixels[outIndex + c] * remaining;
                    result.Pixels[outIndex + c] = (byte)((value + 127) / 255);
                }
            }
        }
    }
}
