using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Compositor.App;

/// <summary>
/// Filter ▸ Gaussian Blur (Filters.swift:203-205, FilterSheet.swift:88-89): the picture dissolves
/// against transparency, not against its own edge. The Mac gives the layer a bigger grid first —
/// three standard deviations plus two pixels of clear room on every side (<c>blurMargin</c>,
/// Filters.swift:384-390) — blurs THAT, and on OK cuts it back to what has coverage with the
/// placement that puts the piece on the same document rectangle (Filters.swift:616-627). Raising the
/// radius re-grows from the original and only ever grows (Filters.swift:393-399): easing it back
/// keeps the room the biggest setting asked for instead of rebuilding every tick. One row —
/// Radius 0.1–250 — and no identity rule: the Mac's silent-commit list (Filters.swift:600-610) has
/// no gaussianBlur entry, so OK always takes the step (a flat field at a tiny radius may leave
/// nothing to change, and the session's own collapse drops the empty step).
/// A disabled attached mask rides onto the new grid asleep and wakes asleep (Filters.swift:642-649).
/// </summary>
public sealed partial class MainWindow
{
    private GaussianBlurPreview? gaussianPreview;
#if DEBUG
    /// <summary>The form the panel built, so a test drives the same door the slider uses.</summary>
    private GaussianBlurForm? gaussianFormProbe;
#endif

    /// <summary>The session shape of the other sheets — the layer frozen once, every tick re-running
    /// on that freeze — plus the grown grid the blur needs: the picture shown while the sheet is open
    /// is genuinely the bigger grid at the bigger placement, so Cancel has to put both back.</summary>
    private sealed class GaussianBlurPreview
    {
        public required EditorDocument Owner;
        public required Guid LayerId;
        public required string Name;
        public required Surface Capture;               // the layer's own pixels, frozen
        public required LayerTransform Placement;      // where they sat
        public Surface Grown = null!;                  // the padded grid the blur runs over
        public LayerTransform GrownPlacement = null!;
        public int GrownMargin;                        // clear room on every side; only ever grows
        public MaskSurface? Clip;                      // coverage sampled on the GROWN grid
        public readonly object SessionKey = new();
        public GaussianBlurForm Form = null!;
        public Surface? Published;
        public Task? Running;
        public bool Busy;
        public bool Queued;
        public bool Closed;
        /// <summary>Whether the sheet's Preview switch is on (Filters.swift:517-536).</summary>
        public bool PreviewOn = true;
        public int PreviewEpoch;
    }

    /// <summary>The room a radius needs around the layer (Filters.swift:386): three standard
    /// deviations plus two, rounded up — the port's own AdjustmentEngine.SamplingMargin is the same
    /// rule for the live adjustment.</summary>
    private static int BlurMargin(double radius) => (int)Math.Ceiling(radius * 3 + 2);

    private void OnGaussianBlurFilter(object sender, RoutedEventArgs e)
    {
        if (FloatingPanelIsFinishing("gaussianBlur")) return;
        if (AnotherFilterSheetOpen) { RefuseSecondFilterSheet(); return; }
        if (!CanEditLayerCommands || document is not { } owner || SelectedLayer is not { } layer) return;
        if (owner.Snapshot.Images.GetValueOrDefault(layer.Id) is not { } source
            || owner.Snapshot.Manifest.Layers.FirstOrDefault(record => record.Id == layer.Id) is not { } record)
        {
            SetStatus(Loc.T("S_PaintNeedsLayer"));
            return;
        }
        var session = new GaussianBlurPreview
        {
            Owner = owner, LayerId = layer.Id, Name = record.Name, Capture = source.Clone(),
            Placement = record.Transform,
        };
        // The Mac opens the edit already grown for the settings it opens with (Filters.swift:379):
        // radius 1's margin of 5, before the first tick. Growing can refuse for size (the Mac's
        // tooLarge throw, :405) — the sheet does not open, the layer is untouched.
        if (!TryGrow(session, BlurMargin(GaussianBlurForm.DefaultRadius)))
        {
            SetStatus(Loc.T("S_GaussianBlurTooLarge"));
            return;
        }
        gaussianPreview = session;
        var form = new GaussianBlurForm(this, session);
        session.Form = form;
#if DEBUG
        gaussianFormProbe = form;
#endif
        ShowFloatingPanel("gaussianBlur", Loc.T("M_GaussianBlurDialog"), form.BuildPanel(), FinishGaussianBlurAsync,
            keepOpenWhileFinishing: true);
    }

    /// <summary>Re-pad from the frozen capture at the radius's room. Only ever grows
    /// (Filters.swift:393-399): a smaller radius keeps the bigger grid the previous tick needed, so
    /// easing off never rebuilds and a tick only re-runs the blur.</summary>
    private bool TryGrow(GaussianBlurPreview session, int margin)
    {
        if (margin <= session.GrownMargin) return true;
        try
        {
            var (grown, placement) = GrownLayerGrid.Grow(session.Capture, session.Placement, margin);
            session.Grown = grown;
            session.GrownPlacement = placement;
            session.GrownMargin = margin;
            // The clip speaks of the grid the blur runs over, so it is re-sampled whenever the grid
            // changes (the Mac's prepare rebuilds the grown mapping the same moment, Filters.swift:418).
            session.Clip = session.Owner.SelectionCoverageOnGrid(session.LayerId, placement, grown.Width, grown.Height);
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"gaussian blur grow failed: {ex}", LogLevel.Error);
            return false;
        }
    }

    private void PreviewGaussianBlur(GaussianBlurPreview session)
    {
        if (!ReferenceEquals(gaussianPreview, session) || session.Closed || !session.PreviewOn) return;
        if (session.Busy) { session.Queued = true; return; }
        session.Running = RunGaussianBlurTicksAsync(session);
    }

    private async Task RunGaussianBlurTicksAsync(GaussianBlurPreview session)
    {
        do
        {
            session.Busy = true;
            session.Queued = false;
            try
            {
                var values = session.Form.Current();
                // Raising the radius re-grows before the tick; the room only ever grows, and a refused
                // grow leaves the previous grid in place — the blur simply runs over what is there.
                TryGrow(session, BlurMargin(values.Radius));
                await ApplyGaussianBlurAsync(session, values.Radius);
            }
            finally { session.Busy = false; }
        }
        while (session.Queued && session.PreviewOn && !session.Closed && ReferenceEquals(gaussianPreview, session));
    }

    private async Task<bool> ApplyGaussianBlurAsync(GaussianBlurPreview session, double radius, bool commit = false)
    {
        var owner = session.Owner;
        var previewEpoch = session.PreviewEpoch;
        if (windowClosed || !ReferenceEquals(document, owner)) return false;
        Surface? result;
        string? failure = null;
        try
        {
            result = await Task.Run(() =>
            {
                // Every tick re-blurs the frozen grown grid, never the previous tick's result. The
                // kernel clamps at the GROWN surface's edge, but the padding is the kernel's own
                // radius plus two, so the picture only ever blurs against transparency.
                var painted = session.Grown.Clone();
                AdjustmentEngine.ApplyGaussianBlur(radius, painted, 0, 0, painted.Width, painted.Height);
                return session.Clip is null
                    ? painted
                    : DitherEngine.ClipToCoverage(session.Grown, painted, session.Clip);
            });
        }
        catch (Exception ex)
        {
            App.Log($"gaussian blur failed: {ex}", LogLevel.Error);
            result = null;
            failure = ex.Message;
        }
        if (result is null)
        {
            DispatcherQueue.TryEnqueue(() => session.Form.ShowPreviewError(failure));
            return false;
        }
        var published = await PublishGaussianBlurAsync(owner, session, result, commit, previewEpoch);
        DispatcherQueue.TryEnqueue(() => session.Form.ShowPreviewError(null));
        return published;
    }

    private Task<bool> PublishGaussianBlurAsync(EditorDocument owner, GaussianBlurPreview session, Surface result,
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
                if (commit)
                {
                    // OK: cut back to what has coverage and put the piece on the same document middle
                    // (Filters.swift:616-627), then ride the attached mask onto the new grid before it
                    // is replaced — the Mac carries from the layer's CURRENT transform onto the made
                    // placement inside the same step (Filters.swift:642-649).
                    var (image, placement) = GrownLayerGrid.Trim(result, session.GrownPlacement);
                    // The mask still covers the ORIGINAL grid: unlike the Mac, whose layer record stays
                    // untouched until commit, this port's preview publishes the grown grid onto the layer
                    // every tick — so the carry maps from the frozen original placement, which is what the
                    // mask's own grid still describes. (Filters.swift:645: current.transform, the record
                    // that has been unchanged all session.)
                    owner.CarryAttachedMask(session.LayerId, session.Placement, session.Capture.Width,
                        session.Capture.Height, placement, image.Width, image.Height, session.SessionKey);
                    owner.ReplaceLayerGrid(session.LayerId, image, placement, session.SessionKey);
                    session.Published = image;
                }
                else
                {
                    // The preview shows the grown grid whole: it IS the picture the user is weighing,
                    // and the trim happens only on OK — the Mac keeps the last grown preview up until
                    // the committed piece replaces it (Filters.swift:353-356, :619-621).
                    owner.ReplaceLayerGrid(session.LayerId, result, session.GrownPlacement, session.SessionKey);
                    session.Published = result;
                }
                done.TrySetResult(true);
            }
            catch (Exception ex)
            {
                App.Log($"gaussian blur publish failed: {ex}", LogLevel.Error);
                done.TrySetResult(false);
            }
        });
        return done.Task;
    }

    private void RestoreGaussianCapture(GaussianBlurPreview session)
    {
        // Retire work already running, including when Preview is turned back on before it finishes.
        session.PreviewEpoch++;
        session.Queued = false;
        if (!ReferenceEquals(document, session.Owner) || windowClosed) return;
        MarkLayerBandDirty(session.LayerId);
        // The grid AND the placement go back: what was parked was a bigger picture at a bigger box.
        session.Owner.ReplaceLayerGrid(session.LayerId, session.Capture, session.Placement, session.SessionKey);
        session.Published = session.Capture;
    }

    private async Task FinishGaussianBlurAsync(bool commit)
    {
        if (gaussianPreview is not { } session) return;
        session.Closed = true;
        while (session.Running is { } work)
        {
            try { await work; } catch { }
            if (!ReferenceEquals(session.Running, work)) continue;
            break;
        }
        if (!ReferenceEquals(document, session.Owner) || windowClosed)
        { if (ReferenceEquals(gaussianPreview, session)) gaussianPreview = null; return; }
        if (commit)
        {
            if (await ApplyGaussianBlurAsync(session, session.Form.Current().Radius, commit: true))
                SetStatus(Loc.F("S_GaussianBlurred", session.Name));
            else SetStatus(Loc.T("S_GaussianBlurFailed"));
        }
        else if (session.Published is not null)
        {
            RestoreGaussianCapture(session);
        }
        session.Owner.EndTransform(session.SessionKey);
        if (ReferenceEquals(gaussianPreview, session)) gaussianPreview = null;
    }

    /// <summary>The sheet's one row (FilterSheet.swift:88-89): Radius, 0.1–250 layer pixels, the blur's
    /// standard deviation — the same row the live Gaussian Blur adjustment already draws.</summary>
    private sealed class GaussianBlurForm
    {
        public const double DefaultRadius = 1;
        private readonly MainWindow owner;
        private readonly GaussianBlurPreview session;
        private readonly CheckBox preview = new() { Content = Loc.T("L_DitherPreview"), IsChecked = true };
        private TextBlock? notice;
        private readonly StackPanel panel = new() { Spacing = 8 };
        private double radius = DefaultRadius;

        public GaussianBlurForm(MainWindow owner, GaussianBlurPreview session)
        {
            this.owner = owner;
            this.session = session;
        }

        public record struct Values(double Radius);

        public Values Current() => new(radius);

        /// <summary>The sheet's Preview switch, handed out so a test drives the control the user does.</summary>
        public CheckBox PreviewSwitch => preview;

        public UIElement BuildPanel()
        {
            panel.Children.Add(owner.SliderRow(Loc.T("L_Radius"), 0.1, 250, radius, number =>
            {
                radius = number;
                if (session.PreviewOn) owner.PreviewGaussianBlur(session);
            }, markBand: () => owner.MarkLayerBandDirty(session.LayerId)));
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
                owner.PreviewGaussianBlur(session);
            };
            preview.Unchecked += (_, _) =>
            {
                session.PreviewOn = false;
                owner.RestoreGaussianCapture(session);
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
