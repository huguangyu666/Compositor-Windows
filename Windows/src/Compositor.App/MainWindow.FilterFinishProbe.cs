#if DEBUG
using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task VerifyFilterFinishFeedbackAsync(Dictionary<string, object> report)
    {
        var observations = new List<object>();
        foreach (string key in new[] { "tonalContrast", "vignette", "lensCorrection", "filter", CameraRawPanelKey })
        {
            var scene = EditorDocument.Create(128, 128);
            var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
            var image = scene.Snapshot.Images[id];
            for (int i = 0; i < image.Pixels.Length; i += 4)
            {
                image.Pixels[i] = (byte)(i / 4);
                image.Pixels[i + 1] = (byte)(i / 512);
                image.Pixels[i + 2] = (byte)(i * 31);
                image.Pixels[i + 3] = 255;
            }
            var capture = image.Clone();
            OpenDocument(scene); await WaitForFramesAsync();
            Func<bool> settled;
            switch (key)
            {
                case "tonalContrast":
                    OnTonalContrast(this, new RoutedEventArgs());
                    SliderNamed(FloatingPanelBody(key)!, Loc.T("L_TonalMidtones"))!.Value = 100;
                    settled = () => tonalPreview is { Busy: false, Queued: false }; break;
                case "vignette":
                    OnVignette(this, new RoutedEventArgs());
                    SliderNamed(FloatingPanelBody(key)!, Loc.T("L_VignetteAmount"))!.Value = 100;
                    settled = () => vignettePreview is { Busy: false, Queued: false }; break;
                case "lensCorrection":
                    OnLensCorrection(this, new RoutedEventArgs());
                    SliderNamed(FloatingPanelBody(key)!, Loc.T("L_RemoveDistortion"))!.Value = 100;
                    settled = () => lensPreview is { Busy: false, Queued: false }; break;
                case "filter":
                    OnDither(this, new RoutedEventArgs()); ditherFormProbe!.SetValue("tones", 3);
                    settled = () => ditherPreview is { Busy: false, Queued: false }; break;
                default:
                    OpenCameraRawPanel(scene, id);
                    SliderNamed(FloatingPanelBody(key)!, Loc.T("L_Exposure"))!.Value = 2;
                    settled = () => !cameraRawPreviewBusy && cameraRawPreviewQueuedDoc is null; break;
            }
            Require(await WaitUntilAsync(settled, 10000), $"{key}: preview did not settle before confirming.");
            await WaitForFramesAsync();
            var panel = floatingPanels[key];
            Require(Equals(panel.Cancel.Content, Loc.T("Dlg_SheetCancel")) && Equals(panel.Confirm.Content, Loc.T("Dlg_OK")),
                $"{key}: the answer row is not Cancel / OK.");
            var originalFinish = panel.Finish!;
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int calls = 0;
            panel.Finish = async commit =>
            {
                calls++;
                try { await release.Task; await originalFinish(commit); }
                finally { finished.TrySetResult(); }
            };
            try
            {
                PressChip(panel.Confirm);
                Require(await WaitUntilAsync(() => calls == 1, 3000), $"{key}: confirm did not reach the finish callback.");
                Require(FloatingPanelIsOpen(key) && VisualTreeHelper.GetParent(panel.Root) is not null,
                    $"{key}: the panel disappeared before its commit finished.");
                await WaitForFramesAsync();
                var controls = FilterFinishDescendants(panel.Root).OfType<Control>().Where(control =>
                    control is Button or Slider or NumberBox or ComboBox or CheckBox or TextBox).ToArray();
                Require(controls.Length > 0 && controls.All(control => !control.IsEnabled),
                    $"{key}: controls still accept edits while the commit is pending.");
                Require(FilterFinishDescendants(panel.Root).OfType<TextBlock>().Any(label =>
                        label.Visibility == Visibility.Visible && label.Text == Loc.T("S_FilterApplying", "Applying…")),
                    $"{key}: the pending commit has no Applying indicator.");
                switch (key)
                {
                    case "tonalContrast": OnTonalContrast(this, new RoutedEventArgs()); break;
                    case "vignette": OnVignette(this, new RoutedEventArgs()); break;
                    case "lensCorrection": OnLensCorrection(this, new RoutedEventArgs()); break;
                    case "filter": OnDither(this, new RoutedEventArgs()); break;
                    default: OnCameraRawFilter(this, new RoutedEventArgs()); break;
                }
                Require(ReferenceEquals(FloatingPanelRoot(key), panel.Root), $"{key}: reopening replaced a committing panel.");
                CloseFloatingPanel(key, true); CloseFloatingPanel(key, false);
                Require(calls == 1 && FloatingPanelIsOpen(key), $"{key}: a second answer interrupted or repeated the commit.");
                observations.Add(new { key, disabledControls = controls.Length, staysOpen = true, finishCalls = calls });
            }
            finally { release.TrySetResult(); await finished.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
            Require(await WaitUntilAsync(() => !FloatingPanelIsOpen(key), 3000), $"{key}: completed commit left the panel open.");
            Require(!capture.Pixels.AsSpan().SequenceEqual(scene.Snapshot.Images[id].Pixels), $"{key}: confirm did not apply the filter.");
            Require(scene.Undo() && capture.Pixels.AsSpan().SequenceEqual(scene.Snapshot.Images[id].Pixels) && !scene.CanUndo,
                $"{key}: confirm did not remain one undo step.");
        }
        report["filterFinishFeedbackObservations"] = observations;
        report["filterCommitKeepsDisabledPanelVisible"] = "passed";
        await VerifyFilterFinishRetirementAsync(report);
    }

    private async Task VerifyFilterFinishRetirementAsync(Dictionary<string, object> report)
    {
        foreach (string key in new[] { "tonalContrast", "vignette", "lensCorrection" })
        {
            OpenDocument(EditorDocument.Create(64, 64)); await WaitForFramesAsync();
            Action open;
            Action<Task> hold;
            Func<object?> current;
            switch (key)
            {
                case "tonalContrast":
                    open = () => OnTonalContrast(this, new RoutedEventArgs());
                    hold = work => tonalPreview!.Running = work; current = () => tonalPreview; break;
                case "vignette":
                    open = () => OnVignette(this, new RoutedEventArgs());
                    hold = work => vignettePreview!.Running = work; current = () => vignettePreview; break;
                default:
                    open = () => OnLensCorrection(this, new RoutedEventArgs());
                    hold = work => lensPreview!.Running = work; current = () => lensPreview; break;
            }
            open();
            var panel = floatingPanels[key];
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finish = panel.Finish!;
            panel.Finish = async commit => { try { await finish(commit); } finally { done.TrySetResult(); } };
            hold(release.Task);
            try
            {
                CloseFloatingPanel(key, true);
                Require(FloatingPanelIsOpen(key), $"{key}: fixture failed to hold a pending commit.");
                OpenDocument(EditorDocument.Create(64, 64));
                Require(!FloatingPanelIsOpen(key), $"{key}: retired commit still covers the new document.");
                open();
                var replacement = current();
                var replacementRoot = FloatingPanelRoot(key);
                release.TrySetResult();
                await done.Task.WaitAsync(TimeSpan.FromSeconds(10)); await WaitForFramesAsync();
                Require(replacement is not null && ReferenceEquals(current(), replacement)
                        && ReferenceEquals(FloatingPanelRoot(key), replacementRoot)
                        && replacementRoot is not null && VisualTreeHelper.GetParent(replacementRoot) is not null,
                    $"{key}: retired commit removed the replacement panel or cleared its session.");
                CloseFloatingPanel(key, false);
                Require(await WaitUntilAsync(() => !FloatingPanelIsOpen(key) && current() is null, 5000),
                    $"{key}: replacement panel did not cancel cleanly.");
            }
            finally { release.TrySetResult(); await done.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        }
        report["retiredFilterCommitPreservesReplacement"] = "passed";
    }

    private static IEnumerable<DependencyObject> FilterFinishDescendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in FilterFinishDescendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
#endif
