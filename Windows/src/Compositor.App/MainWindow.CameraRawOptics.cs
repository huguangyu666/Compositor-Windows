using Compositor.Core;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Compositor.App;

/// <summary>
/// Camera Raw's defringe eyedropper: one clicked fringe becomes the hue band that removes it. The solve is the
/// Mac's (<c>CameraRaw.swift:461-494</c>) and lives in <see cref="CameraRawOptics"/>, so the picker reads the same
/// hue the kernel will compare against, and the band it writes is the one the pixels get.
/// </summary>
public sealed partial class MainWindow
{
    private bool cameraRawSamplesDefringe;
    private TextBlock? cameraRawDefringeNote;
    private Action<bool, double, double, double>? cameraRawPushFringe;

    /// <summary>
    /// Defringe &gt; the eyedropper. The clicked document point is mapped into the layer's own pixel grid first,
    /// because the panel grades that grid, and the pixel under it is read straight out of the capture the session
    /// started from — not out of the preview already on screen (<c>CameraRaw.swift:465-477</c>). False means
    /// nothing was applied: no session, no click armed, no coverage, or a point outside the layer.
    /// </summary>
    internal bool SampleCameraRawDefringe(PointD documentPoint)
    {
        if (cameraRawSessionKey is null || !cameraRawSamplesDefringe) return false;
        if (cameraRawDraft is not { } draft || cameraRawOriginal is not { } pixels || cameraRawPushFringe is not { } push)
        {
            return false;
        }
        if (document?.Snapshot.Manifest.Layers.FirstOrDefault(record => record.Id == cameraRawSessionLayer) is not { } record)
        {
            return false;
        }
        var point = record.Transform.PixelOf(documentPoint, new SizeD(pixels.Width, pixels.Height));
        int x = (int)Math.Floor(point.X), y = (int)Math.Floor(point.Y);
        if (x < 0 || y < 0 || x >= pixels.Width || y >= pixels.Height) return false;
        int index = y * pixels.Stride + x * 4;
        double alpha = pixels.Pixels[index + 3];
        if (alpha == 0) return false;
        double hue = CameraRawOptics.HueDegrees(
            Math.Min(1.0, pixels.Pixels[index] / alpha), Math.Min(1.0, pixels.Pixels[index + 1] / alpha),
            Math.Min(1.0, pixels.Pixels[index + 2] / alpha));
        var solved = CameraRawOptics.SolvedFringe(hue, draft.OpticsPurpleAmount, draft.OpticsGreenAmount);
        if (solved.Purple)
        {
            draft.OpticsPurpleHueLow = solved.Low;
            draft.OpticsPurpleHueHigh = solved.High;
            draft.OpticsPurpleAmount = solved.Amount;
        }
        else
        {
            draft.OpticsGreenHueLow = solved.Low;
            draft.OpticsGreenHueHigh = solved.High;
            draft.OpticsGreenAmount = solved.Amount;
        }
        // The Mac hands the whole optics struct to `normalized` right after a pick (CameraRaw.swift:491), so a
        // fringe clicked near hue 0 bands to 0…25 rather than -25…25, and a band whose handles were dragged out
        // of order is put back. The rows are shown what that left.
        CameraRawOptics.Normalize(draft);
        push(solved.Purple,
            solved.Purple ? draft.OpticsPurpleHueLow : draft.OpticsGreenHueLow,
            solved.Purple ? draft.OpticsPurpleHueHigh : draft.OpticsGreenHueHigh,
            solved.Purple ? draft.OpticsPurpleAmount : draft.OpticsGreenAmount);
        return true;
    }

    /// <summary>Takes the fringe picker off, as the Mac does when the session ends.</summary>
    private void ClearCameraRawOptics()
    {
        cameraRawSamplesDefringe = false;
        cameraRawPushFringe = null;
        if (cameraRawDefringeNote is { } note) note.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        cameraRawDefringeNote = null;
    }
}
