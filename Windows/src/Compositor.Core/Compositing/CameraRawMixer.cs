namespace Compositor.Core;

/// <summary>
/// The Color Mixer's targeted points: the colors the user picks on the picture and the arithmetic that
/// turns them into a nudge of hue, saturation and luminance. The kernel this mirrors is
/// <c>point_weight</c> and the point loop of <c>adjust_camera_raw_curve_color</c>
/// (AdjustPixels.c:720-729, 776-783); the packing and the ranges come from
/// <c>CameraRawMixerSettings.pointFloats</c> and <c>CameraRawPointColor.normalized</c>
/// (CameraRawColor.swift:177-183, 208-220).
/// </summary>
public static class CameraRawMixer
{
    public const int PointsMax = 8;

    /// <summary>The nine floats one point is handed to the kernel as, in the order
    /// <c>pointFloats</c> lays them out: the picked color, the three shifts, then the three half-widths.
    /// <c>Visualize</c> is deliberately absent — it never reaches the pixels as data, only as the index
    /// the preview dims by (<c>pointColorVisualizeIndex</c>, Filters.swift:319-323).</summary>
    public static double[] PointFloats(CameraRawMixerPoint point) =>
    [
        point.Hue / 360, point.Saturation, point.Luminance,
        point.HueShift / 100, point.SaturationShift / 100, point.LuminanceShift / 100,
        point.HueRange / 360, point.SaturationRange, point.LuminanceRange,
    ];

    /// <summary>Eight points, each limited to its own range: what a ninth point does is nothing at all,
    /// and what a wild number does is land on the range's end rather than on zero
    /// (<c>Array(points.prefix(8)).map(\\.normalized)</c>, CameraRawColor.swift:190).</summary>
    public static List<CameraRawMixerPoint> Normalize(IReadOnlyList<CameraRawMixerPoint> points) =>
        points.Take(PointsMax).Select(NormalizePoint).ToList();

    public static CameraRawMixerPoint NormalizePoint(CameraRawMixerPoint point) => new(
        CameraRawSettings.SliderValue(point.Hue, 0, 360, 0),
        CameraRawSettings.SliderValue(point.Saturation, 0, 1, 0),
        CameraRawSettings.SliderValue(point.Luminance, 0, 1, 0),
        CameraRawSettings.SliderValue(point.HueShift, -100, 100, 0),
        CameraRawSettings.SliderValue(point.SaturationShift, -100, 100, 0),
        CameraRawSettings.SliderValue(point.LuminanceShift, -100, 100, 0),
        CameraRawSettings.SliderValue(point.HueRange, 5, 180, 30),
        CameraRawSettings.SliderValue(point.SaturationRange, 0.05, 1, 0.4),
        CameraRawSettings.SliderValue(point.LuminanceRange, 0.05, 1, 0.4),
        point.Visualize);

    /// <summary>How much point <paramref name="index"/> covers the color at hand, in the kernel's float
    /// space. Three lobes multiply, and any one of them going negative kills the whole weight rather than
    /// clipping it to zero — which is why a point's reach is a plateau with a hard shoulder, not a bell
    /// (<c>point_weight</c>, AdjustPixels.c:720-729). The half-widths are floored at 0.01 so a range of
    /// zero cannot divide by nothing.</summary>
    public static double Weight(double hue, double saturation, double luminance,
        IReadOnlyList<double> points, int index)
    {
        int at = index * 9;
        double hueHalf = Math.Max(points[at + 6], 0.01);
        double satHalf = Math.Max(points[at + 7], 0.01);
        double lumHalf = Math.Max(points[at + 8], 0.01);
        double hueWeight = 1 - CircularDistance(hue, points[at]) / hueHalf;
        double satWeight = 1 - Math.Abs(saturation - points[at + 1]) / satHalf;
        double lumWeight = 1 - Math.Abs(luminance - points[at + 2]) / lumHalf;
        if (hueWeight < 0 || satWeight < 0 || lumWeight < 0) return 0;
        return hueWeight * satWeight * lumWeight;
    }

    /// <summary>Distance around the wheel in turns, so red is next to magenta as well as to yellow
    /// (<c>circular_distance</c>, AdjustPixels.c:713-716).</summary>
    public static double CircularDistance(double a, double b)
    {
        double d = Math.Abs(a - b);
        return d > 0.5 ? 1 - d : d;
    }

    /// <summary>The clicked pixel as the eyedropper stores it (CameraRaw.swift:609-637): hue in degrees,
    /// saturation and luminance as fractions, all read off the *un-premultiplied* channel values, and a
    /// fully transparent pixel is refused before any of this happens.</summary>
    public static (double Tone, double Hue, double Saturation, double Luminance) Sample(byte red, byte green, byte blue, byte alpha)
    {
        double r = Math.Min(1.0, red / (double)alpha), g = Math.Min(1.0, green / (double)alpha), b = Math.Min(1.0, blue / (double)alpha);
        double maxc = Math.Max(r, Math.Max(g, b)), minc = Math.Min(r, Math.Min(g, b));
        double chroma = maxc - minc;
        double hue = 0;
        if (chroma > 1e-6)
        {
            double segment = maxc == r ? (g - b) / chroma % 6.0 : maxc == g ? (b - r) / chroma + 2.0 : (r - g) / chroma + 4.0;
            hue = segment * 60.0;
            if (hue < 0) hue += 360.0;
        }
        // `tone` is the rec709 luminance the targeted drag keys on as "the tone under the pointer" (CameraRaw.swift:634),
        // and it is a different quantity from the luminance a picked point stores (:636): one is weighted, one is (max+min)/2.
        return (0.2126 * r + 0.7152 * g + 0.0722 * b, hue, maxc == 0 ? 0 : chroma / maxc, (maxc + minc) / 2);
    }

    /// <summary>How far each of the eight families shares a hue, in degrees: the window is ±40 and neighbours
    /// overlap (<c>weights(forHue:)</c>, CameraRawColor.swift:165-171). This is the drag's copy of the same
    /// window the kernel uses at AdjustPixels.c:764-766 — the very same <c>1 − distance / 40</c> once degrees
    /// become the kernel's fractions — with one real difference kept as the Mac has it: the drag scales each
    /// family by its own weight and never normalizes, while the kernel divides its three deltas by the weight
    /// sum once that sum exceeds one (:772). Dead center on a family the drag therefore asks for the full delta.
    /// (CameraRaw.swift:546-557, which is the only place this list is used.)</summary>
    public static double[] WeightsForHue(double degrees)
    {
        var weights = new double[CameraRawSettings.MixerFamilies];
        for (int index = 0; index < weights.Length; index++)
        {
            double distance = Math.Abs(degrees - CameraRawSettings.MixerCenters[index]);
            if (distance > 180) distance = 360 - distance;
            weights[index] = Math.Max(0, 1 - distance / 40);
        }
        return weights;
    }
}

/// <summary>One color picked on the picture, in the numbers the Mac files: hue in degrees 0…360,
/// saturation and luminance as 0…1 fractions, the three shifts as slider numbers −100…100, and the three
/// half-widths — hue in degrees 5…180 (default 30), saturation and luminance in 0.05…1 (default 0.4).
/// <c>Visualize</c> dims everything outside this point's range, but only while the panel is open: the
/// committed render never carries it, because it travels as a job field rather than as a setting
/// (<c>CameraRawPointColor</c>, CameraRawColor.swift:196-221; Filters.swift:462-471).</summary>
public readonly record struct CameraRawMixerPoint(double Hue, double Saturation, double Luminance,
    double HueShift, double SaturationShift, double LuminanceShift,
    double HueRange, double SaturationRange, double LuminanceRange, bool Visualize)
{
    /// <summary>The point a fresh pick starts from before the clicked pixel's color is written in:
    /// only the three ranges have defaults (CameraRawColor.swift:203-205).</summary>
    public static readonly CameraRawMixerPoint Default = new(0, 0, 0, 0, 0, 0, 30, 0.4, 0.4, false);
}
