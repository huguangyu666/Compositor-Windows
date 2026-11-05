using Compositor.Core;
using Windows.Foundation;

namespace Compositor.App;

/// <summary>
/// Camera Raw's Point Color page: the eyedropper that files a color off the picture, the index of the point
/// the rows below edit, and the index the preview dims by. The picker's arithmetic is the Mac's
/// (<c>sampleCameraRawPointColor</c>, CameraRaw.swift:562-576) and so is the threading of the visualize index
/// (<c>pointColorVisualizeIndex</c>, Filters.swift:319-323); what the pixels do with those numbers is
/// <see cref="CameraRawMixer"/>.
/// </summary>
public sealed partial class MainWindow
{
    private bool cameraRawSamplesPointColor;
    private int cameraRawPointIndex;
    private Action? cameraRawMixerRefresh; // the band's own chip-and-rows bookkeeping, set when it is built

    // Which page and which component or family the mixer is showing. On the Mac these live on the edit session
    // too (Filters.swift:308-313): they choose what the panel lists, never what the grade stores, so a page
    // switch moves no pixel and the choice survives rebuilding the band for another layer.
    private int cameraRawMixerPage = CameraRawSettings.MixerPageHsl;
    private int cameraRawMixerTab;
    private int cameraRawMixerSwatch;

    /// <summary>The clicked pixel, filed the way the eyedropper files it. A color is stored — not a percentage
    /// of the layer — and the number is read off the pristine capture rather than the graded preview, as on
    /// the Mac, which samples <c>edit.preparedPreview ?? edit.previewSource</c> (CameraRaw.swift:611): the
    /// port reads the same layer the panel opened on, because a picked color that moved with its own grade
    /// would move again on every slider.</summary>
    internal bool SampleCameraRawPointColor(PointD documentPoint)
    {
        if (!cameraRawSamplesPointColor || cameraRawDraft is not { } draft) return false;
        if (cameraRawOriginal is not { } pixels) return false;
        if (document?.Snapshot.Manifest.Layers.FirstOrDefault(record => record.Id == cameraRawSessionLayer) is not { } record)
        {
            return false;
        }
        var point = record.Transform.PixelOf(documentPoint, new SizeD(pixels.Width, pixels.Height));
        int x = (int)Math.Floor(point.X), y = (int)Math.Floor(point.Y);
        if (x < 0 || y < 0 || x >= pixels.Width || y >= pixels.Height) return false;
        int at = y * pixels.Stride + x * 4;
        if (pixels.Pixels[at + 3] == 0) return false; // cameraRawSample's `guard bytes[3] > 0` (:620)
        var (_, hue, saturation, luminance) = CameraRawMixer.Sample(
            pixels.Pixels[at], pixels.Pixels[at + 1], pixels.Pixels[at + 2], pixels.Pixels[at + 3]);
        if (cameraRawPointIndex >= 0 && cameraRawPointIndex < draft.MixerPoints.Count)
        {
            // Re-picking replaces the color being edited and keeps its three shifts, so the user can chase a
            // color that turned out to be the wrong one without losing the move they already asked for (:566-570).
            draft.MixerPoints[cameraRawPointIndex] = draft.MixerPoints[cameraRawPointIndex] with
            {
                Hue = hue,
                Saturation = saturation,
                Luminance = luminance,
            };
        }
        else if (draft.MixerPoints.Count < CameraRawMixer.PointsMax)
        {
            draft.MixerPoints.Add(new CameraRawMixerPoint(hue, saturation, luminance, 0, 0, 0, 30, 0.4, 0.4, false));
            cameraRawPointIndex = draft.MixerPoints.Count - 1;
        }
        // A ninth color is refused without a word, as on the Mac (:571 has no else branch): the row of chips
        // does not gain one, and that is the whole announcement.
        else
        {
            return true;
        }
        // SwiftUI re-renders the page from the settings; this band builds its chips and rows by hand, so a
        // pick that changed the list has to ask for them again (and the row values follow the selected index).
        cameraRawMixerRefresh?.Invoke();
        PreviewCameraRaw(document, cameraRawSessionLayer);
        return true;
    }

    /// <summary>The point the preview dims by, or -1: only the selected point, and only while its own flag is
    /// on (Filters.swift:319-323). The committed grade never passes one, which is what makes the Mac's caption
    /// — "it is not kept when you press OK" — true in this port too.</summary>
    private int CameraRawPointVisualizeIndex(CameraRawSettings settings) =>
        cameraRawPointIndex >= 0 && cameraRawPointIndex < settings.MixerPoints.Count
            && settings.MixerPoints[cameraRawPointIndex].Visualize ? cameraRawPointIndex : -1;

    /// <summary>The chip's fill: the Mac paints each picked color with <c>Color(hue:saturation:brightness:)</c>
    /// off the stored numbers (CameraRawColorControls.swift:368) — the same hue/saturation/value triple the
    /// eyedropper files, so the chip shows the color as it was sampled, not as the grade left it.</summary>
    private static Windows.UI.Color MixerPointChipColor(CameraRawMixerPoint point)
    {
        double hue = ((point.Hue % 360.0) + 360.0) % 360.0;
        double chroma = point.Luminance * point.Saturation;
        double slope = 1.0 - Math.Abs(hue / 60.0 % 2.0 - 1.0);
        double second = chroma * Math.Clamp(slope, 0.0, 1.0);
        double match = point.Luminance - chroma;
        (double red, double green, double blue) = hue switch
        {
            < 60 => (chroma, second, 0.0),
            < 120 => (second, chroma, 0.0),
            < 180 => (0.0, chroma, second),
            < 240 => (0.0, second, chroma),
            < 300 => (second, 0.0, chroma),
            _ => (chroma, 0.0, second),
        };
        byte Channel(double value) => (byte)Math.Round(Math.Clamp(value + match, 0.0, 1.0) * 255);
        return Microsoft.UI.ColorHelper.FromArgb(255, Channel(red), Channel(green), Channel(blue));
    }

    /// <summary>Visualize is a field of the point, and the point is a record, so switching it writes the point
    /// back and re-previews: the dim is a preview-only pass (the committed grade passes no index), which is why
    /// the Mac's caption can promise it is "not kept when you press OK".</summary>
    private void SetMixerPointVisualize(bool on)
    {
        if (cameraRawDraft is not { } draft || cameraRawPointIndex < 0 || cameraRawPointIndex >= draft.MixerPoints.Count)
        {
            return;
        }
        draft.MixerPoints[cameraRawPointIndex] = draft.MixerPoints[cameraRawPointIndex] with { Visualize = on };
        if (document is { } doc) PreviewCameraRaw(doc, cameraRawSessionLayer);
    }

    /// <summary>Takes the picker off when the session ends, so a click on the canvas afterwards belongs to the
    /// tool again.</summary>
    private void ClearCameraRawPointColor()
    {
        cameraRawSamplesPointColor = false;
        cameraRawPointIndex = 0;
        cameraRawMixerRefresh = null;
    }
}
