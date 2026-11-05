using Compositor.Core;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Compositor.App;

/// <summary>
/// Camera Raw's Upright guides: the line the user drags on the canvas, and the coordinate space it is stored in.
/// The solve that turns drawn lines into a bend is the Mac's and lives in
/// <see cref="CameraRawGeometry.GuidedCorrections"/>, so the panel, the grade and these three functions all read
/// the same angles.
/// </summary>
public sealed partial class MainWindow
{
    private bool cameraRawDrawsGuide;
    private CameraRawGeometryGuide? cameraRawGuideDraft;
    private bool cameraRawGuideWriting;
    private Action? cameraRawGuideRefresh; // the band's own visibility bookkeeping, set when it is built

    /// <summary>A document point into the layer's own pixel grid, then into the guide's space: normalized 0…1 with
    /// y counted down from the top, because that is what the Mac's picker hands over
    /// (<c>CameraRaw.swift:601-607</c> inverts <c>previewMapping</c> into top-down pixel rows and divides; the
    /// struct's own comment in <c>CameraRawGeometryCalibration.swift:14</c> claims the lower-left instead). Null when
    /// there is no session, no layer record, or the point is off the layer.</summary>
    private (double X, double Y)? CameraRawGuidePoint(PointD documentPoint)
    {
        if (cameraRawOriginal is not { } pixels) return null;
        if (document?.Snapshot.Manifest.Layers.FirstOrDefault(record => record.Id == cameraRawSessionLayer) is not { } record)
        {
            return null;
        }
        var point = record.Transform.PixelOf(documentPoint, new SizeD(pixels.Width, pixels.Height));
        if (point.X < 0 || point.Y < 0 || point.X >= pixels.Width || point.Y >= pixels.Height) return null;
        return (point.X / pixels.Width, point.Y / pixels.Height);
    }

    /// <summary>Pressing opens a draft line with both ends where the press landed. False when nothing is armed or
    /// the point missed the layer; either way the click never reaches the tool.</summary>
    internal bool BeginCameraRawGuide(PointD documentPoint)
    {
        if (cameraRawDraft is null || !cameraRawDrawsGuide || CameraRawGuidePoint(documentPoint) is not { } at)
        {
            return false;
        }
        cameraRawGuideDraft = new CameraRawGeometryGuide(at.X, at.Y, at.X, at.Y);
        return true;
    }

    /// <summary>The drag moves the far end only, and only while drawing stays armed: unchecking Draw Guides
    /// mid-drag stops the line where it is (<c>CameraRaw.swift:584-589</c>). A point that leaves the layer is
    /// dropped rather than clamped, so the line keeps its last position inside the picture.</summary>
    internal void ContinueCameraRawGuide(PointD documentPoint)
    {
        if (!cameraRawDrawsGuide) return;
        if (cameraRawGuideDraft is not { } line || CameraRawGuidePoint(documentPoint) is not { } at) return;
        cameraRawGuideDraft = line with { EndX = at.X, EndY = at.Y };
    }

    /// <summary>Committing a line also chooses Guided, because a drawn line that left the mode on Off would be
    /// filed away unread (<c>CameraRaw.swift:591-599</c>). The list keeps the line as drawn — a click without a
    /// drag is a zero-length guide, and it is normalization that drops it, at the same moment the Mac drops it.</summary>
    internal bool CommitCameraRawGuide()
    {
        if (cameraRawDraft is not { } draft || cameraRawGuideDraft is not { } line || document is not { } doc)
        {
            return false;
        }
        cameraRawGuideDraft = null;
        draft.GeometryGuides.Add(line);
        draft.GeometryUpright = CameraRawSettings.GeometryUprightGuided;
        PreviewCameraRaw(doc, cameraRawSessionLayer);
        cameraRawGuideRefresh?.Invoke();
        return true;
    }

    /// <summary>Takes the guide drawing off when the session ends, so a click on the canvas afterwards belongs to
    /// the tool again.</summary>
    private void ClearCameraRawGuides()
    {
        cameraRawDrawsGuide = false;
        cameraRawGuideDraft = null;
        cameraRawGuideWriting = false;
        cameraRawGuideRefresh = null;
    }
}
