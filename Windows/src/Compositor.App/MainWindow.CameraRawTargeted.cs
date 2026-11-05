using Compositor.Core;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Compositor.App;

/// <summary>
/// Camera Raw's Targeted Adjustment: the two arms (the curve and the mixer) that take over the canvas, the drag
/// they own, and the release that ends it. The numbers live in <see cref="CameraRawTargetedDrag"/>; this half is
/// the session state the Mac keeps on <c>FilterEdit</c> (Filters.swift:315-318) — which arm is armed, and what the
/// press found — plus the two rules that make a drag repeatable: it samples the pixel under the press, and it
/// rewrites the group from the snapshot the press took.
/// </summary>
public sealed partial class MainWindow
{
    private bool cameraRawTargetsCurve;
    private bool cameraRawTargetsMixer;
    private int cameraRawPointChannel;
    private (double StartY, CameraRawSettings Settings, double Tone, double Hue)? cameraRawDrag;

    /// <summary>The band's own bookkeeping, so a drag that moves numbers moves the rows too: the Mac's panel is a
    /// view over the settings and re-renders by itself, while this port builds its rows by hand.</summary>
    private Action? cameraRawPanelRefresh;

    /// <summary>The curve band's point canvas, kept for the same reason as the hook above: its press and move are
    /// decisions about a location, and a probe that cannot inject a pointer has to drive the same two functions
    /// the pointer handlers call. The band owns it and clears it with the session.</summary>
    /// <summary>The ten eyes, per session: which groups the render may read. The numbers stay in the draft,
    /// so opening an eye again brings the grade back unchanged (Filters.swift:298-307).</summary>
    internal bool[] cameraRawShows = Enumerable.Repeat(true, CameraRawSettings.Groups).ToArray();

    /// <summary>The band's eye row, refreshed with every preview: a group that stops adjusting something loses
    /// its eye and one that starts gains it, as the Mac's condition does while it re-renders.</summary>
    private Action? cameraRawEyesRefresh;

    private CurveEditCanvas? cameraRawCurveCanvas;

    /// <summary>The point page's In／Out line, handed over with the canvas for the same reason: what it says is a
    /// fact about the selection, and the probe reads it instead of pressing.</summary>
    private TextBlock? cameraRawCurveReadout;

    /// <summary>Pressing on the picture while an arm is armed starts the drag from what that pixel *is*: its tone
    /// picks which curve point moves, its hue picks which families move (CameraRaw.swift:528-532). A press on
    /// nothing — off the layer, or a pixel with no alpha — leaves no drag, and the release that finds no drag
    /// clears nothing, which is the Mac's guard order (:530 and EditorCanvas.swift:1762-1766).</summary>
    internal bool BeginCameraRawTargetedDrag(PointD documentPoint)
    {
        if (cameraRawDraft is not { } draft || !(cameraRawTargetsCurve || cameraRawTargetsMixer)) return false;
        if (cameraRawOriginal is not { } pixels) return false;
        if (document?.Snapshot.Manifest.Layers.FirstOrDefault(record => record.Id == cameraRawSessionLayer) is not { } record)
        {
            return false;
        }
        var point = record.Transform.PixelOf(documentPoint, new SizeD(pixels.Width, pixels.Height));
        int x = (int)Math.Floor(point.X), y = (int)Math.Floor(point.Y);
        if (x < 0 || y < 0 || x >= pixels.Width || y >= pixels.Height) return false;
        int at = y * pixels.Stride + x * 4;
        if (pixels.Pixels[at + 3] == 0) return false;
        var sample = CameraRawMixer.Sample(pixels.Pixels[at], pixels.Pixels[at + 1], pixels.Pixels[at + 2], pixels.Pixels[at + 3]);
        cameraRawDrag = (documentPoint.Y, draft.Clone(), sample.Tone, sample.Hue);
        return true;
    }

    /// <summary>Every move recomputes from the press: the snapshot goes back into the draft whole, then the one
    /// group this arm owns is rewritten from it (<c>dragCameraRaw</c>, CameraRaw.swift:534-559). The curve arm
    /// branches on the page the band is showing, as the Mac does — the parametric page moves the tonal region
    /// that owns the tone, the point page nudges the curve point nearest it.</summary>
    internal void DragCameraRawTargeted(PointD documentPoint)
    {
        if (cameraRawDrag is not { } drag || cameraRawDraft is null || document is not { } doc) return;
        double delta = CameraRawTargetedDrag.Delta(drag.StartY, documentPoint.Y);
        var settings = drag.Settings.Clone();
        if (cameraRawTargetsCurve)
        {
            if (cameraRawCurvePage == CameraRawSettings.CurvePageParametric)
            {
                // The parametric branch (CameraRaw.swift:540-543): the region that owns the tone takes the whole
                // delta, from the number the press found it at.
                CameraRawTargetedDrag.ApplyCurveRegion(settings, drag.Tone, delta);
            }
            else
            {
                List<CurvePoint> channel = cameraRawPointChannel switch
                {
                    1 => settings.CurveRed,
                    2 => settings.CurveGreen,
                    3 => settings.CurveBlue,
                    _ => settings.CurveRgb,
                };
                var nudged = CameraRawTargetedDrag.Nudge(channel, drag.Tone, delta / 100);
                switch (cameraRawPointChannel)
                {
                    case 1: settings.CurveRed = nudged; break;
                    case 2: settings.CurveGreen = nudged; break;
                    case 3: settings.CurveBlue = nudged; break;
                    default: settings.CurveRgb = nudged; break;
                }
            }
        }
        else
        {
            CameraRawTargetedDrag.ApplyMixer(settings, drag.Hue, delta, cameraRawMixerTab);
        }
        cameraRawDraft = settings;
        cameraRawPanelRefresh?.Invoke();
        PreviewCameraRaw(doc, cameraRawSessionLayer);
    }

    /// <summary>The release ends the drag whatever else it does — the Mac's mouseUp clears it before anything
    /// else it handles (EditorCanvas.swift:2029) — and leaves the numbers the last move reached.</summary>
    internal void EndCameraRawTargetedDrag() => cameraRawDrag = null;

    /// <summary>Takes both arms off when the session ends, so a click on the canvas afterwards belongs to the tool
    /// again, and drops the row-refresh hook along with the band that set it.</summary>
    private void ClearCameraRawTargetedDrag()
    {
        cameraRawTargetsCurve = false;
        cameraRawTargetsMixer = false;
        cameraRawDrag = null;
        cameraRawGraphDrag = null;
        cameraRawPanelRefresh = null;
        cameraRawEyesRefresh = null;
        cameraRawCurveCanvas = null;
        cameraRawCurveReadout = null;
        cameraRawScopeRefresh = null;
        cameraRawScope = null;
        cameraRawScopeCanvas = null;
        cameraRawShowsShadowClipping = false;
        cameraRawShowsHighlightClipping = false;
        cameraRawClipRefresh = null;
        cameraRawClipping = CameraRawEngine.ClippingNone;
        cameraRawSharpenMask = false;
    }

    /// <summary>The two views that replace the picture while Option is held on a slider. Both live on the session,
    /// never in the settings, which is what keeps them out of the layer: OK builds its own render and never asks
    /// for a view (<c>Filters.swift:140, 147</c>; the commit job at :620 leaves the four flags at their defaults).</summary>
    private int cameraRawClipping = CameraRawEngine.ClippingNone;
    private bool cameraRawSharpenMask;
    private bool cameraRawOptionMonitor;

    /// <summary>What a light slider's number change does to the clipping view: the mode that slider carries, but
    /// only while Option is held — the Mac reads the modifier at the moment the number moves
    /// (<c>CameraRawControls.swift:330-334</c>). A probe cannot press Option, so it hands the flag in and the
    /// slider below reads it off the keyboard.</summary>
    internal void SetCameraRawClipping(int clipping, bool optionDown)
    {
        cameraRawClipping = clipping != CameraRawEngine.ClippingNone && optionDown
            ? clipping
            : CameraRawEngine.ClippingNone;
    }

    /// <summary>The mask belongs to the Masking slider alone; the other three in that group switch it off,
    /// whatever the keyboard is doing (<c>CameraRawDetailOpticsControls.swift:70-77</c>).</summary>
    internal void SetCameraRawSharpenMask(bool maskingSlider, bool optionDown)
        => cameraRawSharpenMask = maskingSlider && optionDown;

    /// <summary>Releasing Option takes both views off even if no slider moved in between, because the Mac watches
    /// the modifier itself rather than trusting the slider's release (<c>CameraRawControls.swift:353-362</c>).
    /// Attached once, and a no-op outside a session with a view showing.</summary>
    private void AttachCameraRawOptionMonitor()
    {
        if (cameraRawOptionMonitor) return;
        cameraRawOptionMonitor = true;
        Content.KeyUp += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Menu) ReleaseCameraRawViews();
        };
    }

    internal void ReleaseCameraRawViews()
    {
        if (cameraRawClipping == CameraRawEngine.ClippingNone && !cameraRawSharpenMask) return;
        cameraRawClipping = CameraRawEngine.ClippingNone;
        cameraRawSharpenMask = false;
        if (document is { } doc && cameraRawSessionKey is not null) PreviewCameraRaw(doc, cameraRawSessionLayer);
    }

    /// <summary>The pair the preview worker reads: a view for this tick, or none. The committed render passes
    /// neither, and the probe reads the same two fields the worker froze for its own tick.</summary>
    internal int CameraRawClippingView => cameraRawClipping;
    internal bool CameraRawShowsSharpenMask => cameraRawSharpenMask;
}
