namespace Compositor.Core;

/// <summary>
/// The arithmetic behind Camera Raw's Targeted Adjustment: the vertical drag on the picture that moves the curve
/// region or the color family under the pointer. The kernel of it is the Mac's
/// <c>beginCameraRawDrag</c> / <c>dragCameraRaw</c> pair (CameraRaw.swift:528-560), whose defining property is
/// that every move is measured from the press — the drag keeps a snapshot of the settings the press found and
/// rewrites the whole group from it, so a drag that arrives at the same place twice lands in the same place
/// rather than accumulating.
/// </summary>
public static class CameraRawTargetedDrag
{
    /// <summary>Document pixels to slider units. The Mac's <c>delta = Double(drag.startY - point.y) * 0.35</c>
    /// (:536) — up is positive, and the number is in the document's own pixels, not in points, so a drag costs
    /// the same physical distance at any zoom.</summary>
    public const double PixelsToSlider = 0.35;

    public static double Delta(double startY, double currentY) => (startY - currentY) * PixelsToSlider;

    /// <summary>The curve branch on the point page: the channel the panel is showing, nudged at the tone under
    /// the pointer, by the delta over 100 because a curve point's y is 0…1 while the delta is in slider units
    /// (<c>curve.nudged(_:near:by:)</c>, CameraRawColor.swift:79-100 and CameraRaw.swift:544).</summary>
    public static List<CurvePoint> Nudge(IReadOnlyList<CurvePoint> points, double tone, double delta)
    {
        if (points.Count == 0) return [];
        var moved = points.Select(point => new CurvePoint(point.X, point.Y)).ToList();
        // The nearest point by x, and the first one wins a tie — the Mac's `indices.min(by:)` is a strict
        // less-than over the indices in order (CameraRawColor.swift:88).
        int index = 0;
        double best = double.MaxValue;
        for (int candidate = 0; candidate < points.Count; candidate++)
        {
            double distance = Math.Abs(points[candidate].X - tone);
            if (distance < best)
            {
                best = distance;
                index = candidate;
            }
        }

        // Only y moves, and the two Swift branches are the same statement, so an endpoint is dragged exactly as
        // far as a middle point (:89-92) — which is how a targeted drag can pull the ends of a curve down.
        moved[index] = new CurvePoint(moved[index].X, Math.Clamp(moved[index].Y + delta, 0, 1));
        return moved;
    }

    /// <summary>The mixer branch: each family moves by the delta scaled by how much of that hue it owns, and
    /// only families with a nonzero share move at all (<c>dragCameraRaw</c>, CameraRaw.swift:546-557). The
    /// weights are <see cref="CameraRawMixer.WeightsForHue"/> — the drag's own copy of the kernel's window,
    /// without the kernel's normalization.</summary>
    public static void ApplyMixer(CameraRawSettings settings, double hueDegrees, double delta, int tab = 0)
    {
        var weights = CameraRawMixer.WeightsForHue(hueDegrees);
        double[] target = tab switch
        {
            1 => settings.MixerSaturation,
            2 => settings.MixerLuminance,
            _ => settings.MixerHue,
        };
        for (int index = 0; index < weights.Length; index++)
        {
            if (weights[index] <= 0) continue;
            target[index] = Math.Clamp(target[index] + delta * weights[index], CameraRawSettings.ToneMin, CameraRawSettings.ToneMax);
        }
    }

    /// <summary>Which of the four parametric regions a tone belongs to — 0 shadows, 1 darks, 2 lights,
    /// 3 highlights — by the dividers, in the order the Mac tests them
    /// (<c>region(for:)</c>, CameraRawColor.swift:103-108). The tone is 0…1 and the dividers are percentages,
    /// which is the same comparison the panel's graph makes when it decides what a press in the middle of the
    /// box is grabbing (CameraRawColorControls.swift:150-159) — there the pointer is divided by the width first,
    /// so the two scales meet in the same place.</summary>
    public static int Region(double tone, double shadowSplit, double darkSplit, double lightSplit)
    {
        if (tone < shadowSplit / 100) return 0;
        if (tone < darkSplit / 100) return 1;
        if (tone < lightSplit / 100) return 2;
        return 3;
    }

    /// <summary>The curve branch on the parametric page: the region that owns the tone under the pointer takes
    /// the whole delta — no weights, no sharing — and the number it is added to is the one the press found,
    /// because the drag started from the snapshot (<c>dragCameraRaw</c>, CameraRaw.swift:540-543).</summary>
    public static void ApplyCurveRegion(CameraRawSettings settings, double tone, double delta)
    {
        int region = Region(tone, settings.CurveShadowSplit, settings.CurveDarkSplit, settings.CurveLightSplit);
        double Start() => region switch
        {
            0 => settings.CurveShadows,
            1 => settings.CurveDarks,
            2 => settings.CurveLights,
            _ => settings.CurveHighlights,
        };
        double moved = Math.Clamp(Start() + delta, CameraRawSettings.ToneMin, CameraRawSettings.ToneMax);
        switch (region)
        {
            case 0: settings.CurveShadows = moved; break;
            case 1: settings.CurveDarks = moved; break;
            case 2: settings.CurveLights = moved; break;
            default: settings.CurveHighlights = moved; break;
        }
    }

    /// <summary>The panel's own drag, which is a different law from the picture's: a full height of the graph is
    /// 200 slider units and the amount is the value the region had when the press landed, plus how far the
    /// pointer has moved since, rounded to whole units
    /// (<c>continueDrag</c>'s region branch, CameraRawColorControls.swift:197-199). Rounding is half away from
    /// zero, which is what Swift's <c>Double.rounded()</c> does.</summary>
    public static double RegionDrag(double start, double translationHeight, double graphHeight)
        => Math.Round(Math.Clamp(start - translationHeight / Math.Max(graphHeight, 1) * 200,
               CameraRawSettings.ToneMin, CameraRawSettings.ToneMax), MidpointRounding.AwayFromZero);

    /// <summary>Dragging a divider along the bottom of the graph. The pointer's x becomes a percentage floored
    /// to 2 and capped at 98, and then each divider is kept in order by a two-point gap — the middle one is
    /// squeezed from both sides, the outer two only from the side that would let them cross
    /// (<c>continueDrag</c>'s divider branch, CameraRawColorControls.swift:187-196). The three numbers are
    /// written back in place, like the Mac's <c>update</c>, so a second divider already moved this step counts.</summary>
    public static void DividerDrag(int index, double x, double width, double[] splits)
    {
        double value = Math.Min(98, Math.Max(2, x / Math.Max(width, 1) * 100));
        switch (index)
        {
            case 0: splits[0] = Math.Min(value, splits[1] - 2); break;
            case 1: splits[1] = Math.Min(splits[2] - 2, Math.Max(splits[0] + 2, value)); break;
            default: splits[2] = Math.Max(value, splits[1] + 2); break;
        }
    }
}
