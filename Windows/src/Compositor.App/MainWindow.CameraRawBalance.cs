using Compositor.Core;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Compositor.App;

/// <summary>
/// Camera Raw's white balance beyond the two sliders: the Auto answer computed from the layer's own average,
/// and the eyedropper that turns one clicked pixel neutral. Both end in the same solve
/// (<c>CameraRawWhiteBalance</c>), which is the Mac's 2×2 system inverted — the gains the light pass
/// multiplies are the ones this divides by, so a solved pixel really does come out neutral.
/// </summary>
public sealed partial class MainWindow
{
    private bool cameraRawSamplesWhiteBalance;
    private bool cameraRawBalanceWriting;
    private TextBlock? cameraRawBalanceNote;
    private Action<double, double>? cameraRawPushBalance;
    private int cameraRawBalanceTicket; // bumped per Auto run; only the newest answer may land

    /// <summary>
    /// White Balance &gt; Auto. The mode is stored before the scan so the picker moves on the click, then the
    /// average is taken off the UI thread and applied only if this session is still the one on screen and Auto
    /// is still what the user left selected (<c>CameraRaw.swift:443-461</c>). A layer with no coverage gets no
    /// answer at all: the sliders stay where they were rather than being solved from nothing.
    /// </summary>
    private void ApplyAutoWhiteBalance(EditorDocument doc, Guid layerId)
    {
        if (cameraRawSessionKey is not { } sessionKey || cameraRawOriginal is not { } pixels
            || cameraRawPushBalance is not { } push)
        {
            return;
        }
        var ticket = ++cameraRawBalanceTicket;
        _ = Task.Run(() =>
        {
            var solved = CameraRawWhiteBalance.AutoBalance(pixels);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!ReferenceEquals(cameraRawSessionKey, sessionKey) || cameraRawBalanceTicket != ticket
                    || cameraRawDraft is not { } draft || draft.WhiteBalance != CameraRawSettings.WhiteBalanceAuto)
                {
                    return;
                }
                if (solved is not { } answer)
                {
                    SetStatus(Loc.T("S_WhiteBalanceNoAnswer"));
                    return;
                }
                draft.Temperature = Math.Clamp(answer.Temperature, CameraRawSettings.ToneMin, CameraRawSettings.ToneMax);
                draft.Tint = Math.Clamp(answer.Tint, CameraRawSettings.ToneMin, CameraRawSettings.ToneMax);
                push(answer.Temperature, answer.Tint);
                SetStatus(Loc.T("S_WhiteBalanceAutoApplied"));
            });
        });
    }

    /// <summary>
    /// White Balance &gt; the eyedropper: the clicked point of the document becomes the neutral reference for the
    /// session (<c>CameraRaw.swift:420-441</c>). The point is mapped into the layer's own pixel grid first,
    /// because the panel grades that grid — a rotated or mirrored layer is sampled where it was clicked, not
    /// where its pixels happen to start. False means nothing was applied: no session, an empty click, a pixel
    /// with no coverage, or a color the two axes cannot neutralize.
    /// </summary>
    internal bool SampleCameraRawWhiteBalance(PointD documentPoint)
    {
        if (cameraRawSessionKey is null || !cameraRawSamplesWhiteBalance) return false;
        if (cameraRawDraft is not { } draft || cameraRawOriginal is not { } pixels
            || cameraRawPushBalance is not { } push)
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
        if (CameraRawWhiteBalance.NeutralizeStraight(
                Math.Min(1.0, pixels.Pixels[index] / alpha), Math.Min(1.0, pixels.Pixels[index + 1] / alpha),
                Math.Min(1.0, pixels.Pixels[index + 2] / alpha)) is not { } solved)
        {
            return false;
        }
        draft.Temperature = Math.Clamp(solved.Temperature, CameraRawSettings.ToneMin, CameraRawSettings.ToneMax);
        draft.Tint = Math.Clamp(solved.Tint, CameraRawSettings.ToneMin, CameraRawSettings.ToneMax);
        draft.WhiteBalance = CameraRawSettings.WhiteBalanceCustom; // the Mac drops Auto the moment a pixel is picked
        push(solved.Temperature, solved.Tint);
        return true;
    }

    /// <summary>Takes the selector off, as the Mac does when the session ends: a click on the canvas after that
    /// belongs to the tool again.</summary>
    private void ClearCameraRawWhiteBalance()
    {
        cameraRawSamplesWhiteBalance = false;
        cameraRawBalanceWriting = false;
        cameraRawBalanceTicket++;
        cameraRawPushBalance = null;
        if (cameraRawBalanceNote is { } note) note.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        cameraRawBalanceNote = null;
    }
}
