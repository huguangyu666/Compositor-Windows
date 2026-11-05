using Compositor.Core;

namespace Compositor.App;

/// <summary>
/// Camera Raw's curve Parametric page: which page the band is showing, the four tonal regions the page edits,
/// and the drag inside the graph that moves them. The page and the grab live on the session like the Mac keeps
/// them on <c>FilterEdit</c> (Filters.swift:308, and the drag as the private <c>@State var drag</c> in
/// CameraRawColorControls.swift:9-10); the arithmetic is in
/// <see cref="CameraRawTargetedDrag.RegionDrag"/> and <see cref="CameraRawTargetedDrag.DividerDrag"/>.
///
/// This drag is a different law from the one on the picture. The picture measures document pixels at 0.35 units
/// each; the graph measures its own height and calls a full height 200 units, rounded to whole units, and it
/// keeps the number the region had when the press landed rather than a snapshot of everything else
/// (CameraRawColorControls.swift:197-199 against CameraRaw.swift:536). Both are the Mac's; neither stands in
/// for the other.
/// </summary>
public sealed partial class MainWindow
{
    private int cameraRawCurvePage = CameraRawSettings.CurvePageParametric;

    /// <summary>What a press in the graph grabbed: a divider or a region, which one, the value the region had,
    /// and the y it was pressed at, because a region is moved by how far the pointer travels from there.
    /// The Mac holds the same four facts in its <c>Drag</c> enum.</summary>
    private (bool Divider, int Index, double Start, double PressY)? cameraRawGraphDrag;

    /// <summary>The graph's own box, in the units the point page already uses for it.</summary>
    private const double CurveGraphSize = 255;

    /// <summary>The band along the bottom that a press belongs to the dividers rather than to a region. The Mac's
    /// is 18 points of a 150-point-tall graph (CameraRawColorControls.swift:153, with the frame at :31), so the
    /// port takes the same fraction of its taller box — 30.6 of 255 — rather than the number 18, which on this
    /// box would be a visibly narrower target for the same gesture. The ticks the graph draws are the same
    /// fraction at 8 points.</summary>
    private const double CurveGraphBand = CurveGraphSize * 18 / 150;
    private const double CurveGraphTick = CurveGraphSize * 8 / 150;

    private static double CurveRegionValue(CameraRawSettings settings, int region) => region switch
    {
        0 => settings.CurveShadows,
        1 => settings.CurveDarks,
        2 => settings.CurveLights,
        _ => settings.CurveHighlights,
    };

    private static void SetCurveRegionValue(CameraRawSettings settings, int region, double value)
    {
        switch (region)
        {
            case 0: settings.CurveShadows = value; break;
            case 1: settings.CurveDarks = value; break;
            case 2: settings.CurveLights = value; break;
            default: settings.CurveHighlights = value; break;
        }
    }

    /// <summary>Deciding what the press is on (<c>parametricDrag(at:in:)</c>, CameraRawColorControls.swift:150-159):
    /// the bottom band takes the nearest divider, and a tie goes to the earlier one because the Mac's
    /// <c>indices.min</c> is a strict less-than; anywhere else the tone of the column under the pointer picks the
    /// region, and the region's current number is remembered as the amount's starting point.</summary>
    internal bool BeginCameraRawCurveGraphDrag(double x, double y)
    {
        if (cameraRawDraft is not { } draft) return false;
        double[] splits = [draft.CurveShadowSplit, draft.CurveDarkSplit, draft.CurveLightSplit];
        double percent = x / CurveGraphSize * 100;
        if (y > CurveGraphSize - CurveGraphBand)
        {
            int index = 0;
            double best = double.MaxValue;
            for (int candidate = 0; candidate < splits.Length; candidate++)
            {
                double distance = Math.Abs(splits[candidate] - percent);
                if (distance < best)
                {
                    best = distance;
                    index = candidate;
                }
            }
            cameraRawGraphDrag = (true, index, 0, y);
            return true;
        }
        int region = CameraRawTargetedDrag.Region(x / CurveGraphSize, splits[0], splits[1], splits[2]);
        cameraRawGraphDrag = (false, region, CurveRegionValue(draft, region), y);
        return true;
    }

    /// <summary>Every move writes the one number this press grabbed, from the value it had at the press, and
    /// leaves the rest of the settings where they are — the Mac's <c>update</c> mutates the live settings
    /// instead of restoring a snapshot, which is what makes a divider drag and a region drag repeatable without
    /// discarding anything else the panel has done. The rows and the preview follow, as they do for the
    /// picture's drag.</summary>
    internal void ContinueCameraRawCurveGraphDrag(double x, double y)
    {
        if (cameraRawGraphDrag is not { } grab || cameraRawDraft is null || document is not { } doc) return;
        var settings = cameraRawDraft.Clone();
        if (grab.Divider)
        {
            double[] splits = [settings.CurveShadowSplit, settings.CurveDarkSplit, settings.CurveLightSplit];
            CameraRawTargetedDrag.DividerDrag(grab.Index, x, CurveGraphSize, splits);
            settings.CurveShadowSplit = splits[0];
            settings.CurveDarkSplit = splits[1];
            settings.CurveLightSplit = splits[2];
        }
        else
        {
            SetCurveRegionValue(settings, grab.Index,
                CameraRawTargetedDrag.RegionDrag(grab.Start, y - grab.PressY, CurveGraphSize));
        }
        cameraRawDraft = settings;
        cameraRawPanelRefresh?.Invoke();
        PreviewCameraRaw(doc, cameraRawSessionLayer);
    }

    /// <summary>The release lets go of the grab and keeps every number the drag reached
    /// (<c>.onEnded { _ in drag = nil }</c>, CameraRawColorControls.swift:99).</summary>
    internal void EndCameraRawCurveGraphDrag() => cameraRawGraphDrag = null;

    /// <summary>The graph's curve, in the 64 samples the Mac's graph stroke uses (CameraRawColorControls.swift:74),
    /// read through <see cref="CameraRawEngine.ParametricSamples"/> so the panel cannot show a curve the kernel
    /// would not build from the same four numbers.</summary>
    internal IReadOnlyList<double> CameraRawParametricSamples() =>
        CameraRawEngine.ParametricSamples(cameraRawDraft ?? new CameraRawSettings(), 64);

    /// <summary>The three dividers where the graph draws its ticks, as stored — not as the kernel will floor
    /// them, which is the Mac's own split and is documented on <see cref="CameraRawEngine.ParametricSamples"/>.</summary>
    internal double[] CameraRawCurveSplits()
    {
        var settings = cameraRawDraft ?? new CameraRawSettings();
        return [settings.CurveShadowSplit, settings.CurveDarkSplit, settings.CurveLightSplit];
    }
}
