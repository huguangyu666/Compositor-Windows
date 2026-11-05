namespace Compositor.Core;

/// <summary>
/// The Tonal Contrast filter's kernel, ported from <c>adjust_tonal_contrast</c> (AdjustPixels.c:641-672) and its
/// <c>tonal_smooth</c> (:636-639). Local detail — the pixel minus a blurred copy of itself — pushed by a
/// shadow/midtone/highlight weight read off that blurred copy's own luminance.
/// </summary>
public static class TonalContrastFilter
{
    /// <summary>The sheet's defaults (Filters.swift:65-69). Radius 1…100 px, the three tones −100…100.</summary>
    public const double DefaultAmount = 50, DefaultShadows = 40, DefaultMidtones = 60,
        DefaultHighlights = 30, DefaultRadius = 16;

    /// <summary>smoothstep between two edges, clamped outside (AdjustPixels.c:636-639).</summary>
    public static double Smooth(double low, double high, double value)
    {
        double t = CameraRawEngine.Clamp01((value - low) / (high - low));
        return t * t * (3.0 - 2.0 * t);
    }

    /// <summary>The tone weights of one pixel, from the blurred copy's luminance: how much of the amount the
    /// shadows, the midtones and the highlights each take. They sum to 1 by construction, and the midtones are
    /// what is left rather than their own curve (:661-663).</summary>
    public static (double Shadows, double Midtones, double Highlights) Weights(double baseLuminance)
    {
        double shadow = 1.0 - Smooth(0.15, 0.5, baseLuminance);
        double highlight = Smooth(0.5, 0.85, baseLuminance);
        return (shadow, 1.0 - shadow - highlight, highlight);
    }

    /// <summary>Amount and the three tones as the sheet holds them; <paramref name="blurred"/> is the same
    /// picture on its own grid, and the pixel it has no coverage of is skipped (:653).</summary>
    public static void Apply(Surface surface, Surface blurred, double amount,
        double shadows, double midtones, double highlights)
    {
        if (amount <= 0 || (shadows == 0 && midtones == 0 && highlights == 0)) return;
        double strength = amount / 50.0;
        for (int y = 0; y < surface.Height; y++)
            for (int x = 0; x < surface.Width; x++)
            {
                int index = y * surface.Stride + x * 4;
                int at = y * blurred.Stride + x * 4;
                double alpha = surface.Pixels[index + 3];
                double baseAlpha = blurred.Pixels[at + 3];
                if (alpha == 0 || baseAlpha == 0) continue;
                double r = Math.Min(1.0, surface.Pixels[index] / alpha);
                double g = Math.Min(1.0, surface.Pixels[index + 1] / alpha);
                double b = Math.Min(1.0, surface.Pixels[index + 2] / alpha);
                double lum = CameraRawEngine.Rec709(r, g, b);
                double baseLum = CameraRawEngine.Rec709(
                    Math.Min(1.0, blurred.Pixels[at] / baseAlpha),
                    Math.Min(1.0, blurred.Pixels[at + 1] / baseAlpha),
                    Math.Min(1.0, blurred.Pixels[at + 2] / baseAlpha));
                var (shadowWeight, midtoneWeight, highlightWeight) = Weights(baseLum);
                double weight = (shadows * shadowWeight + midtones * midtoneWeight + highlights * highlightWeight) / 100.0;
                // 0.18·tanh(detail·6) saturates the push so an extreme edge cannot blow past a fixed step, and
                // 4·lum·(1−lum) folds the effect off pure black and pure white, where there is nothing to reveal.
                double delta = 0.18 * Math.Tanh((lum - baseLum) * 6.0) * weight * strength * (4.0 * lum * (1.0 - lum));
                CameraRawEngine.WritePremultiplied(surface, index,
                    CameraRawEngine.Clamp01(r + delta), CameraRawEngine.Clamp01(g + delta),
                    CameraRawEngine.Clamp01(b + delta), alpha);
            }
    }

    /// <summary>The whole filter step: build the blurred base and run the kernel over it. The Mac gets the base
    /// from Core Image (`applyingGaussianBlur(sigma: radius)`, Filters.swift:243); the port uses its own
    /// separable float Gaussian, <see cref="AdjustmentEngine.ApplyGaussianBlur"/> — the same one the Gaussian
    /// Blur adjustment is built on, and carrying the same standing caveat that no machine here can compare the
    /// two pixel for pixel. The kernel above is tested against a hand-built base, so its own arithmetic does not
    /// depend on that choice.</summary>
    public static void Run(Surface surface, double amount, double radius, double shadows, double midtones,
        double highlights)
    {
        if (amount <= 0 || (shadows == 0 && midtones == 0 && highlights == 0)) return;
        var blurred = surface.Clone();
        AdjustmentEngine.ApplyGaussianBlur(radius, blurred, 0, 0, surface.Width, surface.Height);
        Apply(surface, blurred, amount, shadows, midtones, highlights);
    }
}
