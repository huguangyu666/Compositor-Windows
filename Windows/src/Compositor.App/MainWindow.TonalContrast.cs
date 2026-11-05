using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Compositor.App;

/// <summary>
/// Filter ▸ Tonal Contrast (Filters.swift:242-256): local detail — the layer minus a blurred copy of itself —
/// pushed by whichever of the three tone bands the blurred copy says a pixel is in. Five sliders, no colour
/// (FilterSheet.swift:132-137). Its identity rule is wider than the other filters': Amount 0 is nothing, but so
/// is every one of the three tones at 0 (Filters.swift:606-607).
/// </summary>
public sealed partial class MainWindow
{
    private TonalContrastPreview? tonalPreview;
#if DEBUG
    /// <summary>The form the panel built, so a test drives the same door the sliders use.</summary>
    private TonalContrastForm? tonalFormProbe;
#endif
#if DEBUG
    /// <summary>How many times this window has put a computed tick onto a layer. The probe counts it to say out
    /// loud that a run really did publish an intermediate frame; the log line carries it so a red reads as an
    /// ordering instead of a screenshot guess. Debug only: the fingerprint walks the layer's pixels, and on a 4K
    /// layer that is work the publish path cannot be asked to do.</summary>
    private int tonalTickSequence;
    /// <summary>A cheap stand-in for "which frame is this": enough of the picture to tell the panel's opening
    /// tick from the one a row just queued, sampled every fourth byte so it costs nothing on a preview tick.</summary>
    internal static string TonalFingerprint(Surface surface)
    {
        ulong hash = 14695981039346656037;
        var pixels = surface.Pixels;
        hash ^= (ulong)pixels.Length; hash *= 1099511628211;
        for (int i = 0; i < pixels.Length; i += 4) { hash ^= pixels[i]; hash *= 1099511628211; }
        return hash.ToString("X16");
    }
#endif

    /// <summary>The same session shape as Vignette and Lens Correction: the layer frozen once, every tick
    /// re-running on that freeze, the panel amount living in one undo step.</summary>
    private sealed class TonalContrastPreview
    {
        public required EditorDocument Owner;
        public required Guid LayerId;
        public required string Name;
        public required Surface Capture;
        public required MaskSurface? Clip;
        public readonly object SessionKey = new();
        public TonalContrastForm Form = null!;
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

    private void OnTonalContrast(object sender, RoutedEventArgs e)
    {
        if (FloatingPanelIsFinishing("tonalContrast")) return;
        if (AnotherFilterSheetOpen) { RefuseSecondFilterSheet(); return; }
        if (!CanEditLayerCommands || document is not { } owner || SelectedLayer is not { } layer) return;
        if (owner.Snapshot.Images.GetValueOrDefault(layer.Id) is not { } source
            || owner.Snapshot.Manifest.Layers.FirstOrDefault(record => record.Id == layer.Id) is not { } record)
        {
            SetStatus(Loc.T("S_PaintNeedsLayer"));
            return;
        }
        var session = tonalPreview = new TonalContrastPreview
        {
            Owner = owner, LayerId = layer.Id, Name = record.Name, Capture = source.Clone(),
            Clip = owner.SelectionCoverageForLayer(layer.Id, source.Width, source.Height),
        };
        var form = new TonalContrastForm(this, session);
        session.Form = form;
#if DEBUG
        tonalFormProbe = form;
#endif
        ShowFloatingPanel("tonalContrast", Loc.T("M_TonalContrastDialog"), form.BuildPanel(), FinishTonalContrastAsync,
            keepOpenWhileFinishing: true);
    }

    private void PreviewTonalContrast(TonalContrastPreview session)
    {
        if (!ReferenceEquals(tonalPreview, session) || session.Closed || !session.PreviewOn) return;
        if (session.Busy) { session.Queued = true; return; }
        session.Running = RunTonalContrastTicksAsync(session);
    }

    private async Task RunTonalContrastTicksAsync(TonalContrastPreview session)
    {
        do
        {
            session.Busy = true;
            session.Queued = false;
            try { await ApplyTonalContrastAsync(session, session.Form.Current()); }
            finally { session.Busy = false; }
        }
        while (session.Queued && session.PreviewOn && !session.Closed && ReferenceEquals(tonalPreview, session));
    }

    private async Task<bool> ApplyTonalContrastAsync(TonalContrastPreview session, TonalContrastForm.Values values,
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
                // The base is rebuilt every tick from the frozen capture: a blurred previous tick would make the
                // second tick's detail partly its own edit. The Mac scales the radius by the preview factor
                // (Filters.swift:243); this port's filter previews run at the layer's own size, so the factor is 1.
                var painted = capture.Clone();
                TonalContrastFilter.Run(painted, values.Amount, values.Radius, values.Shadows, values.Midtones,
                    values.Highlights);
                return clip is null ? painted : DitherEngine.ClipToCoverage(capture, painted, clip);
            });
        }
        catch (Exception ex)
        {
            App.Log($"tonal contrast failed: {ex}", LogLevel.Error);
            result = null;
            failure = ex.Message;
        }
        if (result is null)
        {
            DispatcherQueue.TryEnqueue(() => session.Form.ShowPreviewError(failure));
            return false;
        }
        var published = await PublishTonalContrastAsync(owner, session, result, commit, previewEpoch);
        DispatcherQueue.TryEnqueue(() => session.Form.ShowPreviewError(null));
        return published;
    }

    private Task<bool> PublishTonalContrastAsync(EditorDocument owner, TonalContrastPreview session, Surface result,
        bool commit, int previewEpoch)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        DispatcherQueue.TryEnqueue(() =>
        {
            // Publishing runs in a dispatcher callback, where a throw would take the process down with it
            // rather than reaching the unhandled handler.
            try
            {
#if DEBUG
                App.Log($"tonal land {++tonalTickSequence} tick {TonalFingerprint(result)} commit={commit}",
                    LogLevel.Diagnostic);
#endif
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
                App.Log($"tonal contrast publish failed: {ex}", LogLevel.Error);
                done.TrySetResult(false);
            }
        });
        return done.Task;
    }

    private void RestoreTonalContrastCapture(TonalContrastPreview session)
    {
        // Retire work already running, including when Preview is turned back on before it finishes.
        session.PreviewEpoch++;
        session.Queued = false;
#if DEBUG
        App.Log($"tonal restore {TonalFingerprint(session.Capture)}", LogLevel.Diagnostic);
#endif
        if (!ReferenceEquals(document, session.Owner) || windowClosed) return;
        MarkLayerBandDirty(session.LayerId);
        session.Owner.ReplaceLayerImage(session.LayerId, session.Capture, session.SessionKey);
        session.Published = session.Capture;
    }

    private async Task FinishTonalContrastAsync(bool commit)
    {
        if (tonalPreview is not { } session) return;
        session.Closed = true;
        while (session.Running is { } work)
        {
            try { await work; } catch { }
            if (!ReferenceEquals(session.Running, work)) continue;
            break;
        }
        if (!ReferenceEquals(document, session.Owner) || windowClosed)
        { if (ReferenceEquals(tonalPreview, session)) tonalPreview = null; return; }
        var values = session.Form.Current();
        if (commit && !values.IsIdentity)
        {
            if (await ApplyTonalContrastAsync(session, values, commit: true)) SetStatus(Loc.F("S_TonalContrasted", session.Name));
            else SetStatus(Loc.T("S_TonalContrastFailed"));
        }
        else if (session.Published is not null)
        {
            // Amount 0 or three silent bands is the identity (Filters.swift:606-607), so OK closes the way
            // Cancel does: back inside the step the ticks opened, which then collapses out of history.
            RestoreTonalContrastCapture(session);
        }
        session.Owner.EndTransform(session.SessionKey);
        if (ReferenceEquals(tonalPreview, session)) tonalPreview = null;
    }

    /// <summary>The sheet's five rows in its own order (FilterSheet.swift:132-137): Amount and Radius in their
    /// positive ranges, the three tone strengths −100…100.</summary>
    private sealed class TonalContrastForm
    {
        private readonly MainWindow owner;
        private readonly TonalContrastPreview session;
        private readonly CheckBox preview = new() { Content = Loc.T("L_DitherPreview"), IsChecked = true };
        private TextBlock? notice;
        private StackPanel panel = new() { Spacing = 8 };
        private double amount = TonalContrastFilter.DefaultAmount;
        private double shadows = TonalContrastFilter.DefaultShadows;
        private double midtones = TonalContrastFilter.DefaultMidtones;
        private double highlights = TonalContrastFilter.DefaultHighlights;
        private double radius = TonalContrastFilter.DefaultRadius;

        public TonalContrastForm(MainWindow owner, TonalContrastPreview session)
        {
            this.owner = owner;
            this.session = session;
        }

        public record struct Values(double Amount, double Shadows, double Midtones, double Highlights, double Radius)
        {
            /// <summary>The Mac's own identity test, wider than the other filters': no amount, or no band left to
            /// push (Filters.swift:606-607).</summary>
            public bool IsIdentity => Amount <= 0 || (Shadows == 0 && Midtones == 0 && Highlights == 0);
        }

        public Values Current() => new(amount, shadows, midtones, highlights, radius);

        /// <summary>The sheet's Preview switch, handed out so a test drives the control the user does.</summary>
        public CheckBox PreviewSwitch => preview;

        private void Row(string key, double minimum, double maximum, double value, Action<double> write)
        {
            panel.Children.Add(owner.SliderRow(Loc.T(key), minimum, maximum, value, number =>
            {
                write(number);
                if (session.PreviewOn) owner.PreviewTonalContrast(session);
            }, markBand: () => owner.MarkLayerBandDirty(session.LayerId)));
        }

        public UIElement BuildPanel()
        {
            Row("L_TonalAmount", 0, 100, amount, value => amount = value);
            Row("L_TonalShadows", -100, 100, shadows, value => shadows = value);
            Row("L_TonalMidtones", -100, 100, midtones, value => midtones = value);
            Row("L_TonalHighlights", -100, 100, highlights, value => highlights = value);
            Row("L_TonalRadius", 1, 100, radius, value => radius = value);
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
                owner.PreviewTonalContrast(session);
            };
            preview.Unchecked += (_, _) =>
            {
                session.PreviewOn = false;
                owner.RestoreTonalContrastCapture(session);
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
