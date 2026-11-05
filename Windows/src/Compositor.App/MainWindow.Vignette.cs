using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Compositor.App;

/// <summary>
/// Filter ▸ Vignette (Filters.swift:223-233): a chosen colour blended into the edges by
/// <c>adjust_colored_vignette</c> (AdjustPixels.c:461-500). Five sliders and one colour chip, no pickers and no
/// presets (FilterSheet.swift:106-128). The falloff is measured against the document's canvas, not the layer's
/// own rectangle, and while the canvas is known the colour also paints into pixels that have no coverage yet —
/// the two branches of the Mac kernel.
/// </summary>
public sealed partial class MainWindow
{
    private VignettePreview? vignettePreview;
#if DEBUG
    /// <summary>The form the panel built, so a probe can drive the colour chip through the same door the chip
    /// uses rather than reaching into a flyout that only exists once opened. Mirrors `ditherFormProbe`.</summary>
    private VignetteForm? vignetteFormProbe;
#endif

    /// <summary>Same session shape as Lens Correction: the layer frozen once, every tick re-running on that
    /// freeze rather than on the previous tick's vignette, and the whole panel living in one undo step.</summary>
    private sealed class VignettePreview
    {
        public required EditorDocument Owner;
        public required Guid LayerId;
        public required string Name;
        public required Surface Capture;
        public required MaskSurface? Clip;
        public required LayerTransform Placement;
        public required double CanvasWidth;
        public required double CanvasHeight;
        public readonly object SessionKey = new();
        public VignetteForm Form = null!;
        public Surface? Published;
        public Task? Running;
        public bool Busy;
        public bool Queued;
        public bool Closed;
        /// <summary>Whether the sheet's Preview switch is on. The Mac keeps the settings but drops the pending job when
        /// preview is false (Filters.swift:517-536), so a row moved while the picture is hidden reaches no
        /// canvas at all.</summary>
        public bool PreviewOn = true;
        public int PreviewEpoch;
    }

    private void OnVignette(object sender, RoutedEventArgs e)
    {
        if (FloatingPanelIsFinishing("vignette")) return;
        if (AnotherFilterSheetOpen) { RefuseSecondFilterSheet(); return; }
        if (!CanEditLayerCommands || document is not { } owner || SelectedLayer is not { } layer) return;
        if (owner.Snapshot.Images.GetValueOrDefault(layer.Id) is not { } source
            || owner.Snapshot.Manifest.Layers.FirstOrDefault(record => record.Id == layer.Id) is not { } record)
        {
            SetStatus(Loc.T("S_PaintNeedsLayer"));
            return;
        }
        var session = vignettePreview = new VignettePreview
        {
            Owner = owner, LayerId = layer.Id, Name = record.Name, Capture = source.Clone(),
            Clip = owner.SelectionCoverageForLayer(layer.Id, source.Width, source.Height),
            Placement = CopyTransform(record.Transform),
            CanvasWidth = owner.Snapshot.Manifest.Width, CanvasHeight = owner.Snapshot.Manifest.Height,
        };
        var form = new VignetteForm(this, session);
        session.Form = form;
#if DEBUG
        vignetteFormProbe = form;
#endif
        ShowFloatingPanel("vignette", Loc.T("M_VignetteDialog"), form.BuildPanel(), FinishVignetteAsync,
            keepOpenWhileFinishing: true);
    }

    private void PreviewVignette(VignettePreview session)
    {
        if (!ReferenceEquals(vignettePreview, session) || session.Closed || !session.PreviewOn) return;
        if (session.Busy) { session.Queued = true; return; }
        session.Running = RunVignetteTicksAsync(session);
    }

    private async Task RunVignetteTicksAsync(VignettePreview session)
    {
        do
        {
            session.Busy = true;
            session.Queued = false;
            try { await ApplyVignetteAsync(session, session.Form.Current()); }
            finally { session.Busy = false; }
        }
        while (session.Queued && session.PreviewOn && !session.Closed && ReferenceEquals(vignettePreview, session));
    }

    private async Task<bool> ApplyVignetteAsync(VignettePreview session, VignetteForm.Values values, bool commit = false)
    {
        var owner = session.Owner;
        var previewEpoch = session.PreviewEpoch;
        if (windowClosed || !ReferenceEquals(document, owner)) return false;
        var capture = session.Capture;
        var clip = session.Clip;
        var frame = VignetteFilter.FrameOf(session.Placement, session.CanvasWidth, session.CanvasHeight,
            capture.Width, capture.Height);
        Surface? result;
        string? failure = null;
        try
        {
            result = await Task.Run(() =>
            {
                var painted = capture.Clone();
                // fillsClear is 1 because a filter run always knows the canvas (Filters.swift:230 reads
                // `job.canvas == nil ? 0 : 1`), which is the branch where the colour also lands on clear pixels.
                VignetteFilter.Apply(painted, frame.X, frame.Y, frame.Width, frame.Height, true,
                    values.Amount, values.Midpoint, values.Roundness, values.Feather, values.Highlights,
                    values.Red, values.Green, values.Blue);
                return clip is null ? painted : DitherEngine.ClipToCoverage(capture, painted, clip);
            });
        }
        catch (Exception ex)
        {
            App.Log($"vignette failed: {ex}", LogLevel.Error);
            result = null;
            failure = ex.Message;
        }
        if (result is null)
        {
            DispatcherQueue.TryEnqueue(() => session.Form.ShowPreviewError(failure));
            return false;
        }
        var published = await PublishVignetteAsync(owner, session, result, commit, previewEpoch);
        DispatcherQueue.TryEnqueue(() => session.Form.ShowPreviewError(null));
        return published;
    }

    private Task<bool> PublishVignetteAsync(EditorDocument owner, VignettePreview session, Surface result, bool commit,
        int previewEpoch)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        DispatcherQueue.TryEnqueue(() =>
        {
            // Publishing runs in a dispatcher callback, where a throw would take the process down with it
            // rather than reaching the unhandled handler.
            try
            {
                if (windowClosed || !ReferenceEquals(document, owner)
                    || (!commit && (session.Closed || !session.PreviewOn || previewEpoch != session.PreviewEpoch)))
                {
                    done.TrySetResult(false);
                    return;
                }
                MarkLayerBandDirty(session.LayerId);
                owner.ReplaceLayerImage(session.LayerId, result, session.SessionKey);
                session.Published = result;
                done.TrySetResult(true);
            }
            catch (Exception ex)
            {
                App.Log($"vignette publish failed: {ex}", LogLevel.Error);
                done.TrySetResult(false);
            }
        });
        return done.Task;
    }

    private void RestoreVignetteCapture(VignettePreview session)
    {
        // Retire work already running, including when Preview is turned back on before it finishes.
        session.PreviewEpoch++;
        session.Queued = false;
        if (!ReferenceEquals(document, session.Owner) || windowClosed) return;
        MarkLayerBandDirty(session.LayerId);
        session.Owner.ReplaceLayerImage(session.LayerId, session.Capture, session.SessionKey);
        session.Published = session.Capture;
    }

    private async Task FinishVignetteAsync(bool commit)
    {
        if (vignettePreview is not { } session) return;
        session.Closed = true;
        while (session.Running is { } work)
        {
            try { await work; } catch { }
            if (!ReferenceEquals(session.Running, work)) continue;
            break;
        }
        if (!ReferenceEquals(document, session.Owner) || windowClosed)
        { if (ReferenceEquals(vignettePreview, session)) vignettePreview = null; return; }
        var values = session.Form.Current();
        if (commit && values.Amount > 0)
        {
            if (await ApplyVignetteAsync(session, values, commit: true)) SetStatus(Loc.F("S_Vignetted", session.Name));
            else SetStatus(Loc.T("S_VignetteFailed"));
        }
        else if (session.Published is not null)
        {
            // Amount 0 is the identity for this filter (Filters.swift:604), so OK closes the way Cancel does —
            // back inside the step the ticks opened, which then collapses and leaves no history at all.
            RestoreVignetteCapture(session);
        }
        session.Owner.EndTransform(session.SessionKey);
        if (ReferenceEquals(vignettePreview, session)) vignettePreview = null;
    }

    /// <summary>The panel's five sliders and one colour chip, in the sheet's order
    /// (FilterSheet.swift:106-128), with the Mac's ranges and its two help lines.</summary>
    private sealed class VignetteForm
    {
        private readonly MainWindow owner;
        private readonly VignettePreview session;
        private readonly CheckBox preview = new() { Content = Loc.T("L_DitherPreview"), IsChecked = true };
        private TextBlock? notice;
        private StackPanel panel = new() { Spacing = 8 };
        private double amount = VignetteFilter.DefaultAmount;
        private double midpoint = VignetteFilter.DefaultMidpoint;
        private double roundness = VignetteFilter.DefaultRoundness;
        private double feather = VignetteFilter.DefaultFeather;
        private double highlights = VignetteFilter.DefaultHighlights;
        private double red, green, blue;

        public VignetteForm(MainWindow owner, VignettePreview session)
        {
            this.owner = owner;
            this.session = session;
        }

        public record struct Values(double Amount, double Midpoint, double Roundness, double Feather,
            double Highlights, double Red, double Green, double Blue);

        public Values Current() => new(amount, midpoint, roundness, feather, highlights, red, green, blue);

        /// <summary>The sheet's Preview switch, handed out so a test drives the same control the user does.</summary>
        public CheckBox PreviewSwitch => preview;

        /// <summary>The colour chip's entry point: the chip and a test both come through here, so a probe that
        /// sets a colour is testing the wiring rather than a private field.</summary>
        public void PickColour(Windows.UI.Color picked)
        {
            red = picked.R / 255.0;
            green = picked.G / 255.0;
            blue = picked.B / 255.0;
            if (session.PreviewOn) owner.PreviewVignette(session);
        }

        private void Row(string key, double minimum, double maximum, double value, Action<double> write)
        {
            var row = owner.SliderRow(Loc.T(key), minimum, maximum, value, number =>
            {
                write(number);
                if (session.PreviewOn) owner.PreviewVignette(session);
            }, markBand: () => owner.MarkLayerBandDirty(session.LayerId));
            Help(row, key);
            panel.Children.Add(row);
        }

        public UIElement BuildPanel()
        {
            var chip = owner.ColorChipRow(Loc.T("L_VignetteColor"),
                Windows.UI.Color.FromArgb(255, (byte)Math.Round(red * 255), (byte)Math.Round(green * 255),
                    (byte)Math.Round(blue * 255)), PickColour, () => owner.MarkLayerBandDirty(session.LayerId));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText((UIElement)chip.Children[1],
                Loc.T("H_VignetteColor"));
            panel.Children.Add(chip);
            Row("L_VignetteAmount", 0, 100, amount, value => amount = value);
            Row("L_VignetteMidpoint", 0, 100, midpoint, value => midpoint = value);
            Row("L_VignetteRoundness", -100, 100, roundness, value => roundness = value);
            Row("L_VignetteFeather", 0, 100, feather, value => feather = value);
            Row("L_VignetteHighlights", 0, 100, highlights, value => highlights = value);
            notice = new TextBlock
            {
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
                Visibility = Visibility.Collapsed, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
            };
            panel.Children.Add(notice);
            panel.Children.Add(preview);
            if (session.Clip is not null)
                panel.Children.Add(new TextBlock
                {
                    Text = Loc.T("L_FilterLimitedToSelection"), Opacity = 0.7,
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                });
            preview.Checked += (_, _) =>
            {
                session.PreviewOn = true;
                owner.PreviewVignette(session);
            };
            preview.Unchecked += (_, _) =>
            {
                session.PreviewOn = false;
                owner.RestoreVignetteCapture(session);
            };
            return panel;
        }

        /// <summary>The two help sentences the sheet hangs on Amount and Highlights (FilterSheet.swift:123, :128).
        /// A SliderRow is label + slider + readout, and the reader only gets what the control reports, so the help
        /// belongs on the slider in the middle rather than on the grid around it.</summary>
        private static void Help(UIElement row, string key)
        {
            var help = key switch
            {
                "L_VignetteAmount" => "H_VignetteAmount",
                "L_VignetteHighlights" => "H_VignetteHighlights",
                _ => null,
            };
            if (help is null) return;
            if (row is Grid grid && grid.Children.Count > 1)
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(grid.Children[1], Loc.T(help));
        }

        public void ShowPreviewError(string? message)
        {
            if (notice is null) return;
            notice.Text = message is null ? string.Empty : Loc.F("S_PreviewFailed", message);
            notice.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

}
