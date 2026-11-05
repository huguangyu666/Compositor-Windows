using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Compositor.App;

/// <summary>
/// Filter ▸ Lens Correction: one "Remove Distortion" slider over the same radial warp the Camera Raw Optics
/// group runs (Filters.swift:257-266 hands `lens_distort` `distortion / 100 * lensStrength`). The Mac's sheet
/// shows nothing else for this filter (FilterSheet.swift:138-141) but its switch, and OK with the slider still
/// at zero is Cancel — `cancelFilter()` without an undo step (Filters.swift:603).
/// </summary>
public sealed partial class MainWindow
{
    private LensCorrectionPreview? lensPreview;

    /// <summary>The live preview session, the same shape as the Dither panel's: the layer as it was found,
    /// frozen once, so every tick warps that rather than the previous tick's warp — distorting a distortion
    /// is not the picture OK is meant to write.</summary>
    private sealed class LensCorrectionPreview
    {
        public required EditorDocument Owner;
        public required Guid LayerId;
        public required string Name;
        public required Surface Capture;
        public required MaskSurface? Clip;
        public readonly object SessionKey = new();
        public LensCorrectionForm Form = null!;
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

    private void OnLensCorrection(object sender, RoutedEventArgs e)
    {
        if (FloatingPanelIsFinishing("lensCorrection")) return;
        if (AnotherFilterSheetOpen) { RefuseSecondFilterSheet(); return; }
        if (!CanEditLayerCommands || document is not { } owner || SelectedLayer is not { } layer) return;
        if (owner.Snapshot.Images.GetValueOrDefault(layer.Id) is not { } source
            || owner.Snapshot.Manifest.Layers.FirstOrDefault(record => record.Id == layer.Id) is not { } record)
        {
            SetStatus(Loc.T("S_PaintNeedsLayer"));
            return;
        }
        var session = lensPreview = new LensCorrectionPreview
        {
            Owner = owner, LayerId = layer.Id, Name = record.Name, Capture = source.Clone(),
            Clip = owner.SelectionCoverageForLayer(layer.Id, source.Width, source.Height),
        };
        var form = new LensCorrectionForm(this, session);
        session.Form = form;
        ShowFloatingPanel("lensCorrection", Loc.T("M_LensCorrectionDialog"), form.BuildPanel(), FinishLensCorrectionAsync,
            keepOpenWhileFinishing: true);
    }

    private void PreviewLensCorrection(LensCorrectionPreview session)
    {
        if (!ReferenceEquals(lensPreview, session) || session.Closed || !session.PreviewOn) return;
        if (session.Busy) { session.Queued = true; return; }
        session.Running = RunLensCorrectionTicksAsync(session);
    }

    private async Task RunLensCorrectionTicksAsync(LensCorrectionPreview session)
    {
        do
        {
            session.Busy = true;
            session.Queued = false;
            try { await ApplyLensCorrectionAsync(session, session.Form.Amount); }
            finally { session.Busy = false; }
        }
        while (session.Queued && session.PreviewOn && !session.Closed && ReferenceEquals(lensPreview, session));
    }

    /// <summary>Warps the capture and publishes it as part of the session's one undo step. The selection is
    /// applied after the warp, and everything outside it comes from the capture — never from the live layer,
    /// which by the second tick holds an earlier tick's result.</summary>
    private async Task<bool> ApplyLensCorrectionAsync(LensCorrectionPreview session, double amount, bool commit = false)
    {
        var owner = session.Owner;
        var previewEpoch = session.PreviewEpoch;
        if (windowClosed || !ReferenceEquals(document, owner)) return false;
        var capture = session.Capture;
        var clip = session.Clip;
        double k = CameraRawOptics.SliderK(amount);
        Surface? result;
        string? failure = null;
        try
        {
            result = await Task.Run(() =>
            {
                // At k = 0 the Mac still builds a fresh destination and writes the image back unchanged
                // (Filters.swift:261-264), so the answer is a copy of the capture rather than the capture.
                var warped = k == 0 ? capture.Clone() : new Surface(capture.Width, capture.Height);
                if (k != 0) CameraRawOptics.Distort(capture, warped, k);
                return clip is null ? warped : DitherEngine.ClipToCoverage(capture, warped, clip);
            });
        }
        catch (Exception ex)
        {
            App.Log($"lens correction failed: {ex}", LogLevel.Error);
            result = null;
            failure = ex.Message;
        }
        if (result is null)
        {
            DispatcherQueue.TryEnqueue(() => session.Form.ShowPreviewError(failure));
            return false;
        }
        var published = await PublishLensCorrectionAsync(owner, session, result, commit, previewEpoch);
        DispatcherQueue.TryEnqueue(() => session.Form.ShowPreviewError(null));
        return published;
    }

    private Task<bool> PublishLensCorrectionAsync(EditorDocument owner, LensCorrectionPreview session, Surface result, bool commit,
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
                App.Log($"lens correction publish failed: {ex}", LogLevel.Error);
                done.TrySetResult(false);
            }
        });
        return done.Task;
    }

    /// <summary>Hides the warp behind the untouched pixels while the panel stays open.</summary>
    private void RestoreLensCorrectionCapture(LensCorrectionPreview session)
    {
        // Retire work already running, including when Preview is turned back on before it finishes.
        session.PreviewEpoch++;
        session.Queued = false;
        if (!ReferenceEquals(document, session.Owner) || windowClosed) return;
        MarkLayerBandDirty(session.LayerId);
        session.Owner.ReplaceLayerImage(session.LayerId, session.Capture, session.SessionKey);
        session.Published = session.Capture;
    }

    private async Task FinishLensCorrectionAsync(bool commit)
    {
        if (lensPreview is not { } session) return;
        // Closed first: a tick still in flight has lost the right to write, because what happens below is
        // either the layer going back to the capture or the commit's own run taking its place.
        session.Closed = true;
        while (session.Running is { } work)
        {
            try { await work; } catch { }
            if (!ReferenceEquals(session.Running, work)) continue;
            break;
        }
        if (!ReferenceEquals(document, session.Owner) || windowClosed)
        { if (ReferenceEquals(lensPreview, session)) lensPreview = null; return; }
        var amount = session.Form.Amount;
        if (commit && amount != 0)
        {
            if (await ApplyLensCorrectionAsync(session, amount, commit: true)) SetStatus(Loc.F("S_LensCorrected", session.Name));
            else SetStatus(Loc.T("S_LensCorrectionFailed"));
        }
        else if (session.Published is not null)
        {
            // Zero is Cancel (Filters.swift:603), and a cancelled panel has to leave nothing behind: the layer
            // goes back inside the step the ticks opened, and closing an empty step drops it from history.
            // The Mac says nothing on this path either — closing the panel is the feedback.
            RestoreLensCorrectionCapture(session);
        }
        session.Owner.EndTransform(session.SessionKey);
        if (ReferenceEquals(lensPreview, session)) lensPreview = null;
    }

    /// <summary>The sheet's one slider, its explanation and its Preview switch (FilterSheet.swift:138-143),
    /// plus the same "limited to the selection" line every filter panel shows.</summary>
    private sealed class LensCorrectionForm
    {
        private readonly MainWindow owner;
        private readonly LensCorrectionPreview session;
        private readonly CheckBox preview = new() { Content = Loc.T("L_DitherPreview"), IsChecked = true };
        private TextBlock? notice;
        private double amount;

        public LensCorrectionForm(MainWindow owner, LensCorrectionPreview session)
        {
            this.owner = owner;
            this.session = session;
        }

        public double Amount => amount;

        public UIElement BuildPanel()
        {
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(owner.SliderRow(Loc.T("L_RemoveDistortion"), -100, 100, 0, value =>
            {
                amount = value;
                if (session.PreviewOn) owner.PreviewLensCorrection(session);
            }, markBand: () => owner.MarkLayerBandDirty(session.LayerId)));
            panel.Children.Add(new TextBlock
            {
                Text = Loc.T("L_LensCorrectionHint"), FontSize = 12, Opacity = 0.7,
                TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
            });
            notice = new TextBlock
            {
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
                Visibility = Visibility.Collapsed, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
            };
            panel.Children.Add(notice);
            panel.Children.Add(preview);
            // The line the other filter panels show for the same fact (FilterSheet.swift:148-150).
            if (session.Clip is not null)
                panel.Children.Add(new TextBlock
                {
                    Text = Loc.T("L_FilterLimitedToSelection"), Opacity = 0.7,
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                });
            preview.Checked += (_, _) =>
            {
                session.PreviewOn = true;
                owner.PreviewLensCorrection(session);
            };
            preview.Unchecked += (_, _) =>
            {
                session.PreviewOn = false;
                owner.RestoreLensCorrectionCapture(session);
            };
            return panel;
        }

        /// <summary>Failure says so where the slider is, as the Mac's `previewError` line does
        /// (FilterSheet.swift:144-146); a cleared line means the last run answered.</summary>
        public void ShowPreviewError(string? message)
        {
            if (notice is null) return;
            notice.Text = message is null ? string.Empty : Loc.F("S_PreviewFailed", message);
            notice.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
