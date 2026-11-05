namespace Compositor.Core;

/// <summary>
/// The Camera Raw panel's RGB histogram and hue/saturation vectorscope of one picture, ported from
/// <c>CameraRawScope</c> (macOS CameraRaw.swift:333-391). The three channel rows are the Levels histogram
/// without the selection coverage, and the scope is counted on the grade as it renders — never on a view.
/// </summary>
public sealed class CameraRawScope
{
    public const int BinCount = 256;
    public const int ScopeSide = 64;

    public required double[] Red { get; init; }
    public required double[] Green { get; init; }
    public required double[] Blue { get; init; }

    /// <summary>Density from the center outward, indexed <c>row * ScopeSide + column</c>.</summary>
    public required double[] Vectorscope { get; init; }

    /// <summary>The shared vertical scale that keeps the three ribbons comparable (:343-348).</summary>
    public double Peak => Math.Max(Math.Max(LevelsHistogram.DisplayScale(Red), LevelsHistogram.DisplayScale(Green)),
                                   LevelsHistogram.DisplayScale(Blue));

    /// <summary>The vectorscope's own busiest cell, which is what the panel scales its squares against.</summary>
    public double VectorPeak => Vectorscope.Max();

    /// <summary>
    /// One neutral pixel plots at the center; a fully saturated one reaches out to 0.48 of the side. Grays are
    /// left out entirely (chroma below 1e-4 has no hue to read), as is a pixel with no alpha
    /// (<c>CameraRaw.swift:366-386</c>). A picture with no pixels has no scope at all, and the panel keeps the
    /// last one it had (:351 returns nil, and Filters.swift:564 only stores a non-nil answer).
    /// </summary>
    public static CameraRawScope? Make(Surface surface)
    {
        if (surface.Width == 0 || surface.Height == 0) return null;
        var bins = LevelsHistogram.Sum(surface, null);
        var scope = new double[ScopeSide * ScopeSide];
        for (int y = 0; y < surface.Height; y++)
        {
            int row = y * surface.Stride;
            for (int x = 0; x < surface.Width; x++)
            {
                int index = row + x * 4;
                double alpha = surface.Pixels[index + 3];
                if (alpha == 0) continue;
                double r = Math.Min(1.0, surface.Pixels[index] / alpha);
                double g = Math.Min(1.0, surface.Pixels[index + 1] / alpha);
                double b = Math.Min(1.0, surface.Pixels[index + 2] / alpha);
                double max = Math.Max(r, Math.Max(g, b));
                double min = Math.Min(r, Math.Min(g, b));
                double chroma = max - min;
                if (chroma <= 1e-4 || max <= 1e-4) continue;
                double hue = (max == r ? (g - b) / chroma : max == g ? 2 + (b - r) / chroma : 4 + (r - g) / chroma) / 6.0;
                if (hue < 0) hue += 1;
                double angle = hue * 2 * Math.PI;
                double saturation = chroma / max;
                int column = Math.Clamp((int)((0.5 + Math.Cos(angle) * saturation * 0.48) * ScopeSide), 0, ScopeSide - 1);
                int cellRow = Math.Clamp((int)((0.5 + Math.Sin(angle) * saturation * 0.48) * ScopeSide), 0, ScopeSide - 1);
                scope[cellRow * ScopeSide + column] += alpha / 255.0;
            }
        }
        return new CameraRawScope
        {
            Red = bins[1], Green = bins[2], Blue = bins[3], Vectorscope = scope,
        };
    }
}
