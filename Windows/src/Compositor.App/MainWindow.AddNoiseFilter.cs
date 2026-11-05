using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Compositor.App;

/// <summary>
/// Filter ▸ Add Noise (Filters.swift:214-224): uniform or Gaussian per-pixel noise over the layer,
/// strength as Photoshop's percentage, brightness-only possible. One row and two switches, no
/// presets (FilterSheet.swift:93-102). The grain is fixed while the sheet is open (the Mac's
/// Filters.swift:301 — one seed per edit, so nudging Amount scales the same pattern instead of
/// reshuffling it) and it keeps the layer's own grid: the blur filters grow it, Add Noise
/// (<c>blurMargin</c> 0) does not. Unlike the other sheets there is no identity rule: the Mac's
/// OK-commit list (Filters.swift:600-610) has no addNoise entry, so OK always commits, even at the
/// slider's floor.
/// </summary>
public sealed partial class MainWindow
{
    private AddNoiseFilterPreview? addNoisePreview;
#if DEBUG
    /// <summary>The form the panel built, so a test drives the same door the sliders use.</summary>
    private AddNoiseForm? addNoiseFormProbe;
#endif

    /// <summary>The same session shape as Vignette and Tonal Contrast: the layer frozen once, every
    /// tick re-running on that freeze and on the session's fixed seed, the panel living in one undo
    /// step.</summary>
    private sealed class AddNoiseFilterPreview
    {
        public required EditorDocument Owner;
        public required Guid LayerId;
        public required string Name;
        public required Surface Capture;
        public required MaskSurface? Clip;
        /// <summary>Drawn once when the sheet opens; every tick and the commit read this same grain
        /// (the Mac's let seed). The undo-coalesce key below keeps the ticks in one history step.</summary>
        public readonly uint Seed = (uint)Random.Shared.Next();
        public readonly object SessionKey = new();
        public AddNoiseForm Form = null!;
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

    private void OnAddNoiseFilter(object sender, RoutedEventArgs e)
    {
        if (FloatingPanelIsFinishing("addNoiseFilter")) return;
        if (!CanEditLayerCommands || document is not { } owner || SelectedLayer is not { } layer) return;
        if (owner.Snapshot.Images.GetValueOrDefault(layer.Id) is not { } source
            || owner.Snapshot.Manifest.Layers.FirstOrDefault(record => record.Id == layer.Id) is not { } record)
        {
            SetStatus(Loc.T("S_PaintNeedsLayer"));
            return;
        }
        var session = addNoisePreview = new AddNoiseFilterPreview
        {
            Owner = owner, LayerId = layer.Id, Name = record.Name, Capture = source.Clone(),
            Clip = owner.SelectionCoverageForLayer(layer.Id, source.Width, source.Height),
        };
        var form = new AddNoiseForm(this, session);
        session.Form = form;
#if DEBUG
        addNoiseFormProbe = form;
#endif
        ShowFloatingPanel("addNoiseFilter", Loc.T("M_AddNoiseDialog"), form.BuildPanel(), FinishAddNoiseFilterAsync,
            keepOpenWhileFinishing: true);
    }

    private void PreviewAddNoise(AddNoiseFilterPreview session)
    {
        if (!ReferenceEquals(addNoisePreview, session) || session.Closed || !session.PreviewOn) return;
        if (session.Busy) { session.Queued = true; return; }
        session.Running = RunAddNoiseTicksAsync(session);
    }

    private async Task RunAddNoiseTicksAsync(AddNoiseFilterPreview session)
    {
        do
        {
            session.Busy = true;
            session.Queued = false;
            try { await ApplyAddNoiseAsync(session, session.Form.Current()); }
            finally { session.Busy = false; }
        }
        while (session.Queued && session.PreviewOn && !session.Closed && ReferenceEquals(addNoisePreview, session));
    }

    private async Task<bool> ApplyAddNoiseAsync(AddNoiseFilterPreview session, AddNoiseForm.Values values,
        bool commit = false)
    {
        var owner = session.Owner;
        var previewEpoch = session.PreviewEpoch;
        if (windowClosed || !ReferenceEquals(document, owner)) return false;
        var capture = session.Capture;
        var clip = session.Clip;
        Surface? result;
        string? failure = null;
        try
        {
            result = await Task.Run(() =>
            {
                // Every tick re-noises the frozen capture, never the previous tick: the grain lands on the
                // original pixels at whatever strength the row says now. Same grid — Add Noise has no
                // margin — so the whole layer is the region.
                var painted = capture.Clone();
                AdjustmentEngine.ApplyAddNoise(painted, 0, 0, painted.Width, painted.Height,
                    values.Amount, values.Gaussian, values.Monochromatic, session.Seed);
                return clip is null ? painted : DitherEngine.ClipToCoverage(capture, painted, clip);
            });
        }
        catch (Exception ex)
        {
            App.Log($"add noise failed: {ex}", LogLevel.Error);
            result = null;
            failure = ex.Message;
        }
        if (result is null)
        {
            DispatcherQueue.TryEnqueue(() => session.Form.ShowPreviewError(failure));
            return false;
        }
        var published = await PublishAddNoiseAsync(owner, session, result, commit, previewEpoch);
        DispatcherQueue.TryEnqueue(() => session.Form.ShowPreviewError(null));
        return published;
    }

    private Task<bool> PublishAddNoiseAsync(EditorDocument owner, AddNoiseFilterPreview session, Surface result,
        bool commit, int previewEpoch)
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
                App.Log($"add noise publish failed: {ex}", LogLevel.Error);
                done.TrySetResult(false);
            }
        });
        return done.Task;
    }

    private void RestoreAddNoiseCapture(AddNoiseFilterPreview session)
    {
        // Retire work already running, including when Preview is turned back on before it finishes.
        session.PreviewEpoch++;
        session.Queued = false;
        if (!ReferenceEquals(document, session.Owner) || windowClosed) return;
        MarkLayerBandDirty(session.LayerId);
        session.Owner.ReplaceLayerImage(session.LayerId, session.Capture, session.SessionKey);
        session.Published = session.Capture;
    }

    private async Task FinishAddNoiseFilterAsync(bool commit)
    {
        if (addNoisePreview is not { } session) return;
        session.Closed = true;
        while (session.Running is { } work)
        {
            try { await work; } catch { }
            if (!ReferenceEquals(session.Running, work)) continue;
            break;
        }
        if (!ReferenceEquals(document, session.Owner) || windowClosed)
        { if (ReferenceEquals(addNoisePreview, session)) addNoisePreview = null; return; }
        if (commit)
        {
            // No identity rule for this filter (see the class summary): OK runs the same kernel one last
            // time at full size and lands it, even at the slider's floor.
            if (await ApplyAddNoiseAsync(session, session.Form.Current(), commit: true))
                SetStatus(Loc.F("S_AddNoised", session.Name));
            else SetStatus(Loc.T("S_AddNoiseFailed"));
        }
        else if (session.Published is not null)
        {
            RestoreAddNoiseCapture(session);
        }
        session.Owner.EndTransform(session.SessionKey);
        if (ReferenceEquals(addNoisePreview, session)) addNoisePreview = null;
    }

    /// <summary>The sheet's rows in the Mac's order (FilterSheet.swift:93-102): Amount, Distribution,
    /// Monochromatic. The Mac's Distribution is a two-segment picker; the port's adjustment panel
    /// already renders the same choice as the Gaussian check (MainWindow.Panels.cs:2121), so the
    /// sheet uses that control and keeps one vocabulary for the same settings.</summary>
    private sealed class AddNoiseForm
    {
        private readonly MainWindow owner;
        private readonly AddNoiseFilterPreview session;
        private readonly CheckBox preview = new() { Content = Loc.T("L_DitherPreview"), IsChecked = true };
        private TextBlock? notice;
        private readonly StackPanel panel = new() { Spacing = 8 };
        private double amount = 10;
        private bool gaussian;
        private bool monochromatic;

        public AddNoiseForm(MainWindow owner, AddNoiseFilterPreview session)
        {
            this.owner = owner;
            this.session = session;
        }

        public record struct Values(double Amount, bool Gaussian, bool Monochromatic);

        public Values Current() => new(amount, gaussian, monochromatic);

        /// <summary>The sheet's Preview switch, handed out so a test drives the control the user does.</summary>
        public CheckBox PreviewSwitch => preview;

        public UIElement BuildPanel()
        {
            panel.Children.Add(owner.SliderRow(Loc.T("L_Amount"), 0.1, 400, amount, number =>
            {
                amount = number;
                if (session.PreviewOn) owner.PreviewAddNoise(session);
            }, markBand: () => owner.MarkLayerBandDirty(session.LayerId)));
            var distribution = owner.CheckRow(Loc.T("L_Gaussian"), gaussian, value =>
            {
                gaussian = value;
                if (session.PreviewOn) owner.PreviewAddNoise(session);
            });
            panel.Children.Add(distribution);
            panel.Children.Add(owner.CheckRow(Loc.T("L_Monochromatic"), monochromatic, value =>
            {
                monochromatic = value;
                if (session.PreviewOn) owner.PreviewAddNoise(session);
            }));
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
                owner.PreviewAddNoise(session);
            };
            preview.Unchecked += (_, _) =>
            {
                session.PreviewOn = false;
                owner.RestoreAddNoiseCapture(session);
            };
            return panel;
        }

        public void ShowPreviewError(string? message)
        {
            if (notice is null) return;
            notice.Text = message is null ? string.Empty : Loc.F("S_PreviewFailed", message);
            notice.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
