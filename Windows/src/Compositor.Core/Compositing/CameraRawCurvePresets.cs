namespace Compositor.Core;

/// <summary>
/// The curve point page's four presets: the three shapes the picker can install and the answer it shows when the
/// curve on the layer is none of them. The Mac keeps this in the view (`CurvePreset`,
/// Compositor/UI/CameraRawColorControls.swift:244-259) with the point lists coming from the settings struct
/// (Document/CameraRawColor.swift:35-37); the port puts the data here so the picker's read-back and the write are
/// the same three lists rather than two copies of them.
/// </summary>
public static class CameraRawCurvePresets
{
    /// <summary>The picker's items, in the Mac's order — and the Mac's own words, like the mixer's page names.</summary>
    public static readonly string[] Names = ["Custom", "Linear", "Medium Contrast", "Strong Contrast"];

    /// <summary>How many anchors the Camera Raw point page will add: the Mac's press refuses a seventeenth point
    /// (`guard points.count &lt; 16`, CameraRawColorControls.swift:167) even though the model accepts up to 32
    /// (`Curves.isValid`, Document/Curves.swift:11) and Image ▸ Curves' own editor offers 32 (:43 there).</summary>
    public const int PointsMax = 16;

    public const int Custom = 0, Linear = 1, MediumContrast = 2, StrongContrast = 3;

    /// <summary>The three shapes, or null for Custom — which the Mac spells `nil` and `applyPreset` then ignores,
    /// so choosing Custom leaves the curve exactly as it is (CameraRawColorControls.swift:226-229).</summary>
    public static List<CurvePoint>? Points(int preset) => preset switch
    {
        Linear => [new CurvePoint(0, 0), new CurvePoint(1, 1)],
        MediumContrast => [new CurvePoint(0, 0), new CurvePoint(0.25, 0.18),
                           new CurvePoint(0.75, 0.82), new CurvePoint(1, 1)],
        StrongContrast => [new CurvePoint(0, 0), new CurvePoint(0.25, 0.10),
                           new CurvePoint(0.75, 0.90), new CurvePoint(1, 1)],
        _ => null,
    };

    /// <summary>Which preset the given curve is: straight equality against the three lists, tried in the Mac's
    /// order, and Custom when none matches (<c>CurvePreset.matching</c>, :254-259). It is an equality test, not a
    /// tolerance — a curve nudged a hundredth off Medium Contrast reads back as Custom, which is what the picker
    /// then shows.</summary>
    public static int Matching(IReadOnlyList<CurvePoint> points)
    {
        for (int preset = Linear; preset <= StrongContrast; preset++)
        {
            var shape = Points(preset)!;
            if (shape.Count != points.Count) continue;
            bool same = true;
            for (int index = 0; index < shape.Count; index++)
            {
                if (shape[index] != points[index]) { same = false; break; }
            }
            if (same) return preset;
        }
        return Custom;
    }
}
