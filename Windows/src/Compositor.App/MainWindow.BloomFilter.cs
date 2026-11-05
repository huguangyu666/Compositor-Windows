using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Compositor.App;

/// <summary>
/// Filter ▸ Bloom / Glow (Filters.swift:236-241, FilterSheet.swift:130-131): the layer's own light
/// blurred and screened back over it, on a grid grown by the radius's reach (bloomMargin is
/// radius×3+2, Filters.swift:386) so the glow runs past the layer's edge — the same grown-grid session
/// as the blur sheets, down to Cancel putting grid AND placement back and the mask riding along.
/// Two rows — Amount 0…100 and Radius 1…150 — and one rule the blurs do not have: Amount 0 is the
/// identity and OK closes the way Cancel does, without an undo step (the Mac's silent-commit list
/// names bloomGlow, Filters.swift:605).
/// </summary>
public sealed partial class MainWindow
{
    private BloomPreview? bloomPreview;
#if DEBUG
    /// <summary>The form the panel built, so a test drives the same door the sliders use.</summary>
    private BloomForm? bloomFormProbe;
#endif

    private sealed class BloomPreview
    {
        public required EditorDocument Owner;
        public required Guid LayerId;
        public required string Name;
        public required Surface Capture;               // the layer's own pixels, frozen
        public required LayerTransform Placement;      // where they sat
        public Surface Grown = null!;                  // the padded grid the glow spreads over
        public LayerTransform GrownPlacement = null!;
        public int GrownMargin;                        // clear room on every side; only ever grows
        public MaskSurface? Clip;                      // coverage sampled on the GROWN grid
        public readonly object SessionKey = new();
        public BloomForm Form = null!;
        public Surface? Published;
        public Task? Running;
        public bool Busy;
        public bool Queued;
        public bool Closed;
        /// <summary>Whether the sheet's Preview switch is on (Filters.swift:517-536).</summary>
        public bool PreviewOn = true;
        public int PreviewEpoch;
    }

    private void OnBloomFilter(object sender, RoutedEventArgs e)
    {
        if (FloatingPanelIsFinishing("bloomGlow")) return;
        if (AnotherFilterSheetOpen) { RefuseSecondFilterSheet(); return; }
        if (!CanEditLayerCommands || document is not { } owner || SelectedLayer is not { } layer) return;
        if (owner.Snapshot.Images.GetValueOrDefault(layer.Id) is not { } source
            || owner.Snapshot.Manifest.Layers.FirstOrDefault(record => record.Id == layer.Id) is not { } record)
        {
            SetStatus(Loc.T("S_PaintNeedsLayer"));
            return;
        }
        var session = new BloomPreview
        {
            Owner = owner, LayerId = layer.Id, Name = record.Name, Capture = source.Clone(),
            Placement = record.Transform,
        };
        // The Mac opens the edit already grown for the settings it opens with (Filters.swift:379).
        if (!TryGrowBloom(session, BlurMargin(BloomForm.DefaultRadius)))
        {
            SetStatus(Loc.T("S_BloomTooLarge"));
            return;
        }
        bloomPreview = session;
        var form = new BloomForm(this, session);
        session.Form = form;
#if DEBUG
        bloomFormProbe = form;
#endif
        ShowFloatingPanel("bloomGlow", Loc.T("M_BloomDialog"), form.BuildPanel(), FinishBloomAsync,
            keepOpenWhileFinishing: true);
    }

    private bool TryGrowBloom(BloomPreview session, int margin)
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
            App.Log($"bloom grow failed: {ex}", LogLevel.Error);
            return false;
        }
    }

    private void PreviewBloom(BloomPreview session)
    {
        if (!ReferenceEquals(bloomPreview, session) || session.Closed || !session.PreviewOn) return;
        if (session.Busy) { session.Queued = true; return; }
        session.Running = RunBloomTicksAsync(session);
    }

    private async Task RunBloomTicksAsync(BloomPreview session)
    {
        do
        {
            session.Busy = true;
            session.Queued = false;
            try
            {
                var values = session.Form.Current();
                TryGrowBloom(session, BlurMargin(values.Radius));
                await ApplyBloomAsync(session, values.Amount, values.Radius);
            }
            finally { session.Busy = false; }
        }
        while (session.Queued && session.PreviewOn && !session.Closed && ReferenceEquals(bloomPreview, session));
    }

    private async Task<bool> ApplyBloomAsync(BloomPreview session, double amount, double radius, bool commit = false)
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
                BloomFilter.Run(painted, amount, radius);
                return session.Clip is null
                    ? painted
                    : DitherEngine.ClipToCoverage(session.Grown, painted, session.Clip);
            });
        }
        catch (Exception ex)
        {
            App.Log($"bloom failed: {ex}", LogLevel.Error);
            result = null;
            failure = ex.Message;
        }
        if (result is null)
        {
            DispatcherQueue.TryEnqueue(() => session.Form.ShowPreviewError(failure));
            return false;
        }
        var published = await PublishBloomAsync(owner, session, result, commit, previewEpoch);
        DispatcherQueue.TryEnqueue(() => session.Form.ShowPreviewError(null));
        return published;
    }

    private Task<bool> PublishBloomAsync(EditorDocument owner, BloomPreview session, Surface result,
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
                    // The mask still covers the ORIGINAL grid (the preview grew the layer's own): carry
                    // from the frozen placement, as the blur sheets learned.
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
                App.Log($"bloom publish failed: {ex}", LogLevel.Error);
                done.TrySetResult(false);
            }
        });
        return done.Task;
    }

    private void RestoreBloomCapture(BloomPreview session)
    {
        session.PreviewEpoch++;
        session.Queued = false;
        if (!ReferenceEquals(document, session.Owner) || windowClosed) return;
        MarkLayerBandDirty(session.LayerId);
        session.Owner.ReplaceLayerGrid(session.LayerId, session.Capture, session.Placement, session.SessionKey);
        session.Published = session.Capture;
    }

    private async Task FinishBloomAsync(bool commit)
    {
        if (bloomPreview is not { } session) return;
        session.Closed = true;
        while (session.Running is { } work)
        {
            try { await work; } catch { }
            if (!ReferenceEquals(session.Running, work)) continue;
            break;
        }
        if (!ReferenceEquals(document, session.Owner) || windowClosed)
        { if (ReferenceEquals(bloomPreview, session)) bloomPreview = null; return; }
        var values = session.Form.Current();
        if (commit && !values.IsIdentity)
        {
            if (await ApplyBloomAsync(session, values.Amount, values.Radius, commit: true))
                SetStatus(Loc.F("S_Bloomed", session.Name));
            else SetStatus(Loc.T("S_BloomFailed"));
        }
        else if (session.Published is not null)
        {
            // Amount 0 is the identity (Filters.swift:605), so OK closes the way Cancel does. The trim
            // would have cut the whole-glow grid back to the layer's box anyway — an empty glow spreads
            // nothing into the padding — but the Mac keeps that grid out of the layer entirely, so the
            // capture and placement go back and the step collapses out of history, not a commit.
            RestoreBloomCapture(session);
        }
        session.Owner.EndTransform(session.SessionKey);
        if (ReferenceEquals(bloomPreview, session)) bloomPreview = null;
    }

    /// <summary>The sheet's two rows in the Mac's order (FilterSheet.swift:130-131): Amount 0…100 and
    /// Radius 1…150 px, the same pair the live layer effects' Bloom already names.</summary>
    private sealed class BloomForm
    {
        public const double DefaultAmount = BloomFilter.DefaultAmount;
        public const double DefaultRadius = BloomFilter.DefaultRadius;
        private readonly MainWindow owner;
        private readonly BloomPreview session;
        private readonly CheckBox preview = new() { Content = Loc.T("L_DitherPreview"), IsChecked = true };
        private TextBlock? notice;
        private readonly StackPanel panel = new() { Spacing = 8 };
        private double amount = DefaultAmount;
        private double radius = DefaultRadius;

        public BloomForm(MainWindow owner, BloomPreview session)
        {
            this.owner = owner;
            this.session = session;
        }

        public record struct Values(double Amount, double Radius)
        {
            /// <summary>The Mac's identity rule for this filter (Filters.swift:605): no amount, nothing to glow.</summary>
            public bool IsIdentity => Amount <= 0;
        }

        public Values Current() => new(amount, radius);

        public CheckBox PreviewSwitch => preview;

        public UIElement BuildPanel()
        {
            panel.Children.Add(owner.SliderRow(Loc.T("L_Amount"), 0, 100, amount, number =>
            {
                amount = number;
                if (session.PreviewOn) owner.PreviewBloom(session);
            }, markBand: () => owner.MarkLayerBandDirty(session.LayerId)));
            panel.Children.Add(owner.SliderRow(Loc.T("L_Radius"), 1, 150, radius, number =>
            {
                radius = number;
                if (session.PreviewOn) owner.PreviewBloom(session);
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
                owner.PreviewBloom(session);
            };
            preview.Unchecked += (_, _) =>
            {
                session.PreviewOn = false;
                owner.RestoreBloomCapture(session);
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
