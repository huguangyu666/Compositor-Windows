namespace Compositor.Core;

/// <summary>
/// The 24 blend modes, computed per channel on gamma-encoded sRGB values the way Photoshop does
/// (and the way the macOS app forces Core Image to). Non-separable modes use the W3C
/// luminance/saturation/color decomposition; Subtract and Divide follow Core Image.
/// </summary>
public static class BlendMath
{
    /// <summary>
    /// One channel: backdrop color, source color, both 0–1 (straight, not premultiplied),
    /// returns the blended color 0–1.
    /// </summary>
    public static float Blend(LayerBlendMode mode, float backdrop, float source) => mode switch
    {
        LayerBlendMode.Normal => source,
        LayerBlendMode.Darken => MathF.Min(backdrop, source),
        LayerBlendMode.Multiply => backdrop * source,
        LayerBlendMode.ColorBurn => ColorBurn(backdrop, source),
        LayerBlendMode.LinearBurn => Math.Clamp(backdrop + source - 1f, 0f, 1f),
        LayerBlendMode.Lighten => MathF.Max(backdrop, source),
        LayerBlendMode.Screen => backdrop + source - backdrop * source,
        LayerBlendMode.ColorDodge => ColorDodge(backdrop, source),
        LayerBlendMode.LinearDodge => Math.Clamp(backdrop + source, 0f, 1f),
        LayerBlendMode.Overlay => HardLight(source, backdrop), // the same exchange Hard Light makes
        LayerBlendMode.SoftLight => SoftLight(backdrop, source),
        LayerBlendMode.HardLight => HardLight(backdrop, source),
        LayerBlendMode.VividLight => VividLight(backdrop, source),
        LayerBlendMode.LinearLight => LinearLight(backdrop, source),
        LayerBlendMode.PinLight => PinLight(backdrop, source),
        LayerBlendMode.HardMix => VividLight(backdrop, source) < 0.5f ? 0f : 1f,
        LayerBlendMode.Difference => MathF.Abs(backdrop - source),
        LayerBlendMode.Exclusion => backdrop + source - 2f * backdrop * source,
        // CISubtractBlendMode subtracts the background sample from the input image's sample
        // (Apple's filter reference and CIBlendKernel.subtract both say so), and the Mac
        // hands the layer in as inputImage and the canvas as inputBackgroundImage — so the
        // layer takes away from the canvas beneath it.
        LayerBlendMode.Subtract => Math.Clamp(source - backdrop, 0f, 1f),
        LayerBlendMode.Divide => source <= 0f ? 1f : Math.Clamp(backdrop / source, 0f, 1f),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), "Non-separable modes go through BlendColor."),
    };

    /// <summary>
    /// One pixel's three channels at once; the non-separable modes (Hue, Saturation, Color,
    /// Luminosity) need all channels together. Writes the blend result into <paramref name="result"/>.
    /// </summary>
    public static void BlendColor(LayerBlendMode mode, (float R, float G, float B) backdrop, (float R, float G, float B) source, Span<float> result)
    {
        switch (mode)
        {
            case LayerBlendMode.Hue:
                SetLuminosity(SetSaturation(source, Saturation(backdrop)), Luminance(backdrop), result);
                break;
            case LayerBlendMode.Saturation:
                SetLuminosity(SetSaturation(backdrop, Saturation(source)), Luminance(backdrop), result);
                break;
            case LayerBlendMode.Color:
                SetLuminosity(source, Luminance(backdrop), result);
                break;
            case LayerBlendMode.Luminosity:
                SetLuminosity(backdrop, Luminance(source), result);
                break;
            default:
                result[0] = Blend(mode, backdrop.R, source.R);
                result[1] = Blend(mode, backdrop.G, source.G);
                result[2] = Blend(mode, backdrop.B, source.B);
                break;
        }
    }

    private static float ColorBurn(float backdrop, float source)
    {
        if (backdrop >= 1f) return 1f;
        if (source <= 0f) return 0f;
        return Math.Clamp(1f - (1f - backdrop) / source, 0f, 1f);
    }

    private static float ColorDodge(float backdrop, float source)
    {
        if (backdrop <= 0f) return 0f;
        if (source >= 1f) return 1f;
        return Math.Clamp(backdrop / (1f - source), 0f, 1f);
    }

    private static float HardLight(float backdrop, float source) =>
        source <= 0.5f ? backdrop * source * 2f : Screen(backdrop, 2f * source - 1f);

    private static float Screen(float backdrop, float source) => backdrop + source - backdrop * source;

    private static float VividLight(float backdrop, float source) =>
        source <= 0.5f ? ColorBurn(backdrop, source * 2f) : ColorDodge(backdrop, 2f * (source - 0.5f));

    private static float LinearLight(float backdrop, float source) =>
        source <= 0.5f
            ? Math.Clamp(backdrop + 2f * source - 1f, 0f, 1f)
            : Math.Clamp(backdrop + 2f * (source - 0.5f), 0f, 1f);

    private static float PinLight(float backdrop, float source) =>
        source <= 0.5f ? MathF.Min(backdrop, 2f * source) : MathF.Max(backdrop, 2f * source - 1f);

    private static float SoftLight(float backdrop, float source)
    {
        // The W3C (Photoshop) formula: a dark or light source pushes the backdrop toward it.
        float darkened = ((1f - 2f * source) * backdrop * backdrop) + (2f * source * backdrop);
        float g = backdrop <= 0.25f ? ((16f * backdrop - 12f) * backdrop + 4f) * backdrop : MathF.Sqrt(backdrop);
        float lightened = backdrop + (2f * source - 1f) * (g - backdrop);
        return source <= 0.5f ? darkened : lightened;
    }

    private static float Luminance((float R, float G, float B) color) =>
        0.3f * color.R + 0.59f * color.G + 0.11f * color.B;

    private static float Saturation((float R, float G, float B) color) =>
        MathF.Max(color.R, MathF.Max(color.G, color.B)) - MathF.Min(color.R, MathF.Min(color.G, color.B));

    private static void ClipColor(Span<float> color)
    {
        float luminance = Luminance((color[0], color[1], color[2]));
        float minimum = MathF.Min(color[0], MathF.Min(color[1], color[2]));
        float maximum = MathF.Max(color[0], MathF.Max(color[1], color[2]));
        if (minimum < 0f)
        {
            for (int i = 0; i < 3; i++) color[i] = luminance + (color[i] - luminance) * luminance / (luminance - minimum);
        }
        if (maximum > 1f)
        {
            for (int i = 0; i < 3; i++) color[i] = luminance + (color[i] - luminance) * (1f - luminance) / (maximum - luminance);
        }
    }

    private static void SetLuminosity((float R, float G, float B) color, float luminance, Span<float> result)
    {
        float difference = luminance - Luminance(color);
        result[0] = color.R + difference;
        result[1] = color.G + difference;
        result[2] = color.B + difference;
        ClipColor(result);
    }

    private static (float R, float G, float B) SetSaturation((float R, float G, float B) color, float saturation)
    {
        // Scale every channel's distance above the minimum to the wanted saturation — the
        // maximum channel lands on it exactly, so its value is written directly to avoid a
        // rounding pass through the scale factor. No buffers: this runs per pixel.
        float minimum = MathF.Min(color.R, MathF.Min(color.G, color.B));
        float maximum = MathF.Max(color.R, MathF.Max(color.G, color.B));
        if (maximum <= minimum) return (0f, 0f, 0f);
        float scale = saturation / (maximum - minimum);
        float r = (color.R - minimum) * scale;
        float g = (color.G - minimum) * scale;
        float b = (color.B - minimum) * scale;
        if (maximum == color.R) r = saturation;
        else if (maximum == color.G) g = saturation;
        else b = saturation;
        return (r, g, b);
    }
}
