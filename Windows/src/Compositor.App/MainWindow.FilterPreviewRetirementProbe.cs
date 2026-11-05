#if DEBUG
using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task VerifyFilterPreviewRetirementAsync(Dictionary<string, object> report)
    {
        var observations = new List<object>();
        var failed = new List<string>();
        foreach (string filter in new[] { "tonalContrast", "vignette", "lensCorrection", CameraRawPanelKey })
        {
            var scene = EditorDocument.Create(512, 512);
            var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
            var source = scene.Snapshot.Images[id];
            uint random = 12345;
            for (int i = 0; i < source.Pixels.Length; i += 4)
            {
                random = random * 1664525 + 1013904223;
                source.Pixels[i] = (byte)(random >> 24);
                source.Pixels[i + 1] = (byte)(i / 4);
                source.Pixels[i + 2] = (byte)(i / (4 * source.Width));
                source.Pixels[i + 3] = 255;
            }
            OpenDocument(scene); await WaitForFramesAsync();
            var capture = source.Clone();
            Action open;
            Func<bool> settled;
            Action<bool> close;
            switch (filter)
            {
                case "tonalContrast":
                    open = () => OnTonalContrast(this, new RoutedEventArgs());
                    settled = () => tonalPreview is { Busy: false, Queued: false };
                    close = commit => CloseFloatingPanel(filter, commit);
                    break;
                case "vignette":
                    open = () => OnVignette(this, new RoutedEventArgs());
                    settled = () => vignettePreview is { Busy: false, Queued: false };
                    close = commit => CloseFloatingPanel(filter, commit);
                    break;
                case "lensCorrection":
                    open = () => OnLensCorrection(this, new RoutedEventArgs());
                    settled = () => lensPreview is { Busy: false, Queued: false };
                    close = commit => CloseFloatingPanel(filter, commit);
                    break;
                default:
                    open = () => OpenCameraRawPanel(scene, id);
                    settled = () => !cameraRawPreviewBusy && cameraRawPreviewQueuedDoc is null;
                    close = commit => PressChip(CameraRawSheetButton(confirm: commit));
                    break;
            }
            bool Closed() => filter switch
            {
                "tonalContrast" => tonalPreview is null,
                "vignette" => vignettePreview is null,
                "lensCorrection" => lensPreview is null,
                _ => !FloatingPanelIsOpen(CameraRawPanelKey),
            };
            open();
            Require(await WaitUntilAsync(settled, 10000), $"{filter}: opening preview did not settle.");
            var body = FloatingPanelBody(filter)!;
            var toggle = ((Panel)body).Children.OfType<CheckBox>().Single(box => Equals(box.Content, Loc.T("L_DitherPreview")));
            string row = filter switch { "tonalContrast" => "L_TonalMidtones", "vignette" => "L_VignetteAmount",
                "lensCorrection" => "L_RemoveDistortion", _ => "L_Exposure" };
            SliderNamed(body, Loc.T(row))!.Value = filter == CameraRawPanelKey ? 2 : 100;
            Require(!settled(), $"{filter}: fixture did not catch a real running preview.");
            toggle.IsChecked = false;
            bool restoredImmediately = source.Pixels.AsSpan().SequenceEqual(scene.Snapshot.Images[id].Pixels);
            Require(await WaitUntilAsync(settled, 10000), $"{filter}: hidden preview did not retire.");
            await WaitForFramesAsync();
            bool stayedOriginal = capture.Pixels.AsSpan().SequenceEqual(scene.Snapshot.Images[id].Pixels);
            if (!restoredImmediately || !stayedOriginal) failed.Add(filter);
            toggle.IsChecked = true;
            Require(await WaitUntilAsync(() => settled() && !capture.Pixels.AsSpan().SequenceEqual(scene.Snapshot.Images[id].Pixels), 10000),
                $"{filter}: enabling Preview did not render the requested values.");
            // A visibility check alone is insufficient: an old tick can finish after off/on. Observe every
            // publish while the replacement is queued, so a stale intermediate frame cannot pass unnoticed.
            var oldResult = scene.Snapshot.Images[id].Clone();
            toggle.IsChecked = false;
            toggle.IsChecked = true;
            Require(!settled(), $"{filter}: off/on did not start a real preview.");
            toggle.IsChecked = false;
            SliderNamed(body, Loc.T(row))!.Value = filter == CameraRawPanelKey ? -1 : 40;
            bool retiredFramePublished = false;
            void Observe() => retiredFramePublished |= oldResult.Pixels.AsSpan().SequenceEqual(scene.Snapshot.Images[id].Pixels);
            scene.Changed += Observe;
            scene.PixelsChanged += Observe;
            try
            {
                toggle.IsChecked = true;
                Require(await WaitUntilAsync(settled, 10000), $"{filter}: replacement preview did not settle.");
            }
            finally { scene.Changed -= Observe; scene.PixelsChanged -= Observe; }
            bool latestVisible = !capture.Pixels.AsSpan().SequenceEqual(scene.Snapshot.Images[id].Pixels)
                && !oldResult.Pixels.AsSpan().SequenceEqual(scene.Snapshot.Images[id].Pixels);
            var latestResult = scene.Snapshot.Images[id].Clone();
            observations.Add(new { filter, restoredImmediately, stayedOriginal, retiredFramePublished, latestVisible });
            Require(!retiredFramePublished && latestVisible, $"{filter}: off/on published a retired tick or lost the latest values.");
            close(false);
            Require(await WaitUntilAsync(Closed, 10000), $"{filter}: cancel did not finish.");
            await WaitForFramesAsync();
            Require(capture.Pixels.AsSpan().SequenceEqual(scene.Snapshot.Images[id].Pixels) && !scene.CanUndo,
                $"{filter}: cancel after hidden work left pixels or history behind.");

            // OK still applies the current values with Preview hidden, and records exactly one undo step.
            open();
            Require(await WaitUntilAsync(settled, 10000), $"{filter}: second opening did not settle.");
            body = FloatingPanelBody(filter)!;
            toggle = ((Panel)body).Children.OfType<CheckBox>().Single(box => Equals(box.Content, Loc.T("L_DitherPreview")));
            SliderNamed(body, Loc.T(row))!.Value = filter == CameraRawPanelKey ? 2 : 100;
            Require(!settled(), $"{filter}: hidden commit fixture did not catch a running preview.");
            toggle.IsChecked = false;
            SliderNamed(body, Loc.T(row))!.Value = filter == CameraRawPanelKey ? -1 : 40;
            close(true);
            Require(await WaitUntilAsync(() => Closed() && !capture.Pixels.AsSpan().SequenceEqual(scene.Snapshot.Images[id].Pixels), 10000),
                $"{filter}: OK with Preview off did not apply the current values.");
            Require(latestResult.Pixels.AsSpan().SequenceEqual(scene.Snapshot.Images[id].Pixels),
                $"{filter}: hidden commit applied different values from the requested preview.");
            Require(scene.Undo() && capture.Pixels.AsSpan().SequenceEqual(scene.Snapshot.Images[id].Pixels) && !scene.CanUndo,
                $"{filter}: hidden commit did not undo in one step.");
        }
        report["filterPreviewRetirementObservations"] = observations;
        Require(failed.Count == 0, "Running previews overwrote the original after Preview was disabled: " + string.Join(", ", failed));
        report["filterPreviewOffRetiresInFlightResults"] = "passed";
    }
}
#endif
