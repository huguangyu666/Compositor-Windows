using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Compositor.App;

/// <summary>
/// Filter ▸ Motion Blur (Filters.swift:206-212, FilterSheet.swift:90-92): the picture smeared along a
/// direction, on a grid grown by half the streak plus two pixels (the Mac's blurMargin, Filters.swift:387)
/// so the travel has somewhere to run past the layer's edge. Two rows — Angle −90…90, counterclockwise
/// from horizontal as in Photoshop, and Distance 1…2000 — and no identity rule: the Mac's silent-commit
/// list (Filters.swift:600-610) has no motionBlur entry, so OK always takes the step.
/// The grown-grid session is the Gaussian sheet's shape: the preview shows the bigger grid at the
/// bigger placement (Cancel puts both back), raising the distance re-grows and only ever grows, the
/// clip is sampled on the grown grid, and the attached mask rides onto the committed one.
/// </summary>
public sealed partial class MainWindow
{
    private MotionBlurPreview? motionBlurPreview;
#if DEBUG
    /// <summary>The form the panel built, so a test drives the same door the sliders use.</summary>
    private MotionBlurForm? motionBlurFormProbe;
#endif

    /// <summary>What the streak needs around the layer (Filters.swift:387): half the distance plus two,
    /// rounded up — the port's own AdjustmentEngine.SamplingMargin for the live adjustment.</summary>
    private static int MotionMargin(double distance) => (int)Math.Ceiling(distance / 2 + 2);

    private sealed class MotionBlurPreview
    {
        public required EditorDocument Owner;
        public required Guid LayerId;
        public required string Name;
        public required Surface Capture;               // the layer's own pixels, frozen
        public required LayerTransform Placement;      // where they sat
        public Surface Grown = null!;                  // the padded grid the streak runs over
        public LayerTransform GrownPlacement = null!;
        public int GrownMargin;                        // clear room on every side; only ever grows
        public MaskSurface? Clip;                      // coverage sampled on the GROWN grid
        public readonly object SessionKey = new();
        public MotionBlurForm Form = null!;
        public Surface? Published;
        public Task? Running;
        public bool Busy;
        public bool Queued;
        public bool Closed;
        /// <summary>Whether the sheet's Preview switch is on (Filters.swift:517-536).</summary>
        public bool PreviewOn = true;
        public int PreviewEpoch;
    }

    private void OnMotionBlurFilter(object sender, RoutedEventArgs e)
    {
        if (FloatingPanelIsFinishing("motionBlur")) return;
        if (AnotherFilterSheetOpen) { RefuseSecondFilterSheet(); return; }
        if (!CanEditLayerCommands || document is not { } owner || SelectedLayer is not { } layer) return;
        if (owner.Snapshot.Images.GetValueOrDefault(layer.Id) is not { } source
            || owner.Snapshot.Manifest.Layers.FirstOrDefault(record => record.Id == layer.Id) is not { } record)
        {
            SetStatus(Loc.T("S_PaintNeedsLayer"));
            return;
        }
        var session = new MotionBlurPreview
        {
            Owner = owner, LayerId = layer.Id, Name = record.Name, Capture = source.Clone(),
            Placement = record.Transform,
        };
        // The Mac opens the edit already grown for the settings it opens with (Filters.swift:379):
        // the default streak's margin, before the first tick.
        if (!TryGrowMotion(session, MotionMargin(MotionBlurForm.DefaultDistance)))
        {
            SetStatus(Loc.T("S_MotionBlurTooLarge"));
            return;
        }
        motionBlurPreview = session;
        var form = new MotionBlurForm(this, session);
        session.Form = form;
#if DEBUG
        motionBlurFormProbe = form;
#endif
        ShowFloatingPanel("motionBlur", Loc.T("M_MotionBlurDialog"), form.BuildPanel(), FinishMotionBlurAsync,
            keepOpenWhileFinishing: true);
    }

    private bool TryGrowMotion(MotionBlurPreview session, int margin)
    {
        if (margin <= session.GrownMargin) return true;
        try
        {
            var (grown, placement) = GrownLayerGrid.Grow(session.Capture, session.Placement, margin);
            session.Grown = grown;
            session.GrownPlacement = placement;
            session.GrownMargin = margin;
            session.Clip = session.Owner.SelectionCoverageOnGrid(session.LayerId, placement, grown.Width, grown.Height);
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"motion blur grow failed: {ex}", LogLevel.Error);
            return false;
        }
    }

    private void PreviewMotionBlur(MotionBlurPreview session)
    {
        if (!ReferenceEquals(motionBlurPreview, session) || session.Closed || !session.PreviewOn) return;
        if (session.Busy) { session.Queued = true; return; }
        session.Running = RunMotionBlurTicksAsync(session);
    }

    private async Task RunMotionBlurTicksAsync(MotionBlurPreview session)
    {
        do
        {
            session.Busy = true;
            session.Queued = false;
            try
            {
                var values = session.Form.Current();
                // Only the distance needs room; the angle turns the streak inside whatever grid is there.
                TryGrowMotion(session, MotionMargin(values.Distance));
                await ApplyMotionBlurAsync(session, values.Angle, values.Distance);
            }
            finally { session.Busy = false; }
        }
        while (session.Queued && session.PreviewOn && !session.Closed && ReferenceEquals(motionBlurPreview, session));
    }

    private async Task<bool> ApplyMotionBlurAsync(MotionBlurPreview session, double angle, double distance, bool commit = false)
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
                var painted = session.Grown.Clone();
                AdjustmentEngine.ApplyMotionBlur(angle, distance, painted, 0, 0, painted.Width, painted.Height);
                return session.Clip is null
                    ? painted
                    : DitherEngine.ClipToCoverage(session.Grown, painted, session.Clip);
            });
        }
        catch (Exception ex)
        {
            App.Log($"motion blur failed: {ex}", LogLevel.Error);
            result = null;
            failure = ex.Message;
        }
        if (result is null)
        {
            DispatcherQueue.TryEnqueue(() => session.Form.ShowPreviewError(failure));
            return false;
        }
        var published = await PublishMotionBlurAsync(owner, session, result, commit, previewEpoch);
        DispatcherQueue.TryEnqueue(() => session.Form.ShowPreviewError(null));
        return published;
    }

    private Task<bool> PublishMotionBlurAsync(EditorDocument owner, MotionBlurPreview session, Surface result,
        bool commit, int previewEpoch)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        DispatcherQueue.TryEnqueue(() =>
        {
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
                    var (image, placement) = GrownLayerGrid.Trim(result, session.GrownPlacement);
                    // The mask still covers the ORIGINAL grid (the preview grew the layer's own), so the
                    // carry maps from the frozen placement — the Gaussian sheet's lesson, same rule here.
                    owner.CarryAttachedMask(session.LayerId, session.Placement, session.Capture.Width,
                        session.Capture.Height, placement, image.Width, image.Height, session.SessionKey);
                    owner.ReplaceLayerGrid(session.LayerId, image, placement, session.SessionKey);
                    session.Published = image;
                }
                else
                {
                    owner.ReplaceLayerGrid(session.LayerId, result, session.GrownPlacement, session.SessionKey);
                    session.Published = result;
                }
                done.TrySetResult(true);
            }
            catch (Exception ex)
            {
                App.Log($"motion blur publish failed: {ex}", LogLevel.Error);
                done.TrySetResult(false);
            }
        });
        return done.Task;
    }

    private void RestoreMotionCapture(MotionBlurPreview session)
    {
        session.PreviewEpoch++;
        session.Queued = false;
        if (!ReferenceEquals(document, session.Owner) || windowClosed) return;
        MarkLayerBandDirty(session.LayerId);
        session.Owner.ReplaceLayerGrid(session.LayerId, session.Capture, session.Placement, session.SessionKey);
        session.Published = session.Capture;
    }

    private async Task FinishMotionBlurAsync(bool commit)
    {
        if (motionBlurPreview is not { } session) return;
        session.Closed = true;
        while (session.Running is { } work)
        {
            try { await work; } catch { }
            if (!ReferenceEquals(session.Running, work)) continue;
            break;
        }
        if (!ReferenceEquals(document, session.Owner) || windowClosed)
        { if (ReferenceEquals(motionBlurPreview, session)) motionBlurPreview = null; return; }
        if (commit)
        {
            var values = session.Form.Current();
            if (await ApplyMotionBlurAsync(session, values.Angle, values.Distance, commit: true))
                SetStatus(Loc.F("S_MotionBlurred", session.Name));
            else SetStatus(Loc.T("S_MotionBlurFailed"));
        }
        else if (session.Published is not null)
        {
            RestoreMotionCapture(session);
        }
        session.Owner.EndTransform(session.SessionKey);
        if (ReferenceEquals(motionBlurPreview, session)) motionBlurPreview = null;
    }

    /// <summary>The sheet's two rows in the Mac's order (FilterSheet.swift:90-92): Angle −90…90°,
    /// Distance 1…2000 px — the same pair the live Motion Blur adjustment draws.</summary>
    private sealed class MotionBlurForm
    {
        public const double DefaultAngle = 0;
        public const double DefaultDistance = 10;
        private readonly MainWindow owner;
        private readonly MotionBlurPreview session;
        private readonly CheckBox preview = new() { Content = Loc.T("L_DitherPreview"), IsChecked = true };
        private TextBlock? notice;
        private readonly StackPanel panel = new() { Spacing = 8 };
        private double angle = DefaultAngle;
        private double distance = DefaultDistance;

        public MotionBlurForm(MainWindow owner, MotionBlurPreview session)
        {
            this.owner = owner;
            this.session = session;
        }

        public record struct Values(double Angle, double Distance);

        public Values Current() => new(angle, distance);

        public CheckBox PreviewSwitch => preview;

        public UIElement BuildPanel()
        {
            panel.Children.Add(owner.SliderRow(Loc.T("L_Angle"), -90, 90, angle, number =>
            {
                angle = number;
                if (session.PreviewOn) owner.PreviewMotionBlur(session);
            }, markBand: () => owner.MarkLayerBandDirty(session.LayerId)));
            panel.Children.Add(owner.SliderRow(Loc.T("L_Distance"), 1, 2000, distance, number =>
            {
                distance = number;
                if (session.PreviewOn) owner.PreviewMotionBlur(session);
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
                owner.PreviewMotionBlur(session);
            };
            preview.Unchecked += (_, _) =>
            {
                session.PreviewOn = false;
                owner.RestoreMotionCapture(session);
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
