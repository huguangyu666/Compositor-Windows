namespace Compositor.Core;

/// <summary>
/// The white-balance solve ported from the macOS <c>CameraRawSettings</c> statics
/// (Document/CameraRaw.swift:265-323). Temperature and tint are relative offsets on an already-rendered
/// layer, so there is no kelvin here: the question is which two slider positions turn one given pixel
/// neutral, and the answer is the same 2×2 system the gains apply in reverse.
/// </summary>
public static class CameraRawWhiteBalance
{
    /// <summary>The straight sRGB decode the Mac uses before solving (<c>CameraRaw.swift:297-299</c>).</summary>
    private static double Decode(double encoded) =>
        encoded <= 0.04045 ? encoded / 12.92 : Math.Pow((encoded + 0.055) / 1.055, 2.4);

    /// <summary>
    /// Temperature and tint in the panel's own −100…100 units, for a pixel already in linear light. Null when a
    /// channel is missing, when the two axes are parallel, or when the answer is not a number: the Mac returns
    /// nil in exactly those cases and leaves the sliders where they were.
    /// </summary>
    public static (double Temperature, double Tint)? Neutralize(double red, double green, double blue)
    {
        if (red <= 1e-4 || green <= 1e-4 || blue <= 1e-4) return null;
        double a1 = CameraRawSettings.TemperatureGain * red;
        double b1 = CameraRawSettings.TintRedBlue * red + CameraRawSettings.TintGreen * green;
        double c1 = green - red;
        double a2 = -CameraRawSettings.TemperatureGain * blue;
        double b2 = CameraRawSettings.TintRedBlue * blue + CameraRawSettings.TintGreen * green;
        double c2 = green - blue;
        double determinant = a1 * b2 - a2 * b1;
        if (Math.Abs(determinant) <= 1e-8) return null;
        double warm = (c1 * b2 - c2 * b1) / determinant;
        double magenta = (a1 * c2 - a2 * c1) / determinant;
        if (!double.IsFinite(warm) || !double.IsFinite(magenta)) return null;
        return (warm * 100, magenta * 100);
    }

    /// <summary>The same solve for a pixel as the layer stores it — straight sRGB, 0…1 — which is what a click
    /// on the canvas reads (<c>CameraRaw.swift:292</c>).</summary>
    public static (double Temperature, double Tint)? NeutralizeStraight(double red, double green, double blue) =>
        Neutralize(Decode(red), Decode(green), Decode(blue));

    /// <summary>
    /// Gray-world balance of a surface: the average of the opaque pixels in linear light, solved. Premultiplied
    /// bytes are divided by their own alpha and capped at 1 first, exactly as <c>averageLinear</c> does, and a
    /// surface with no coverage returns null rather than a balance of nothing.
    /// </summary>
    public static (double Temperature, double Tint)? AutoBalance(Surface surface)
    {
        double red = 0, green = 0, blue = 0, count = 0;
        for (int y = 0; y < surface.Height; y++)
        {
            int row = y * surface.Stride;
            for (int x = 0; x < surface.Width; x++)
            {
                int index = row + x * 4;
                double alpha = surface.Pixels[index + 3];
                if (alpha == 0) continue;
                red += Decode(Math.Min(1.0, surface.Pixels[index] / alpha));
                green += Decode(Math.Min(1.0, surface.Pixels[index + 1] / alpha));
                blue += Decode(Math.Min(1.0, surface.Pixels[index + 2] / alpha));
                count += 1;
            }
        }
        if (count == 0) return null;
        return Neutralize(red / count, green / count, blue / count);
    }
}
