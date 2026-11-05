using Compositor.Core;
using Compositor.Core.Psd;
using Compositor.Renderer;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using System.Collections.Generic;
using System.Diagnostics;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Compositor.App;

/// <summary>MainWindow's right-hand panels: the adjustment panel (levels, curves,
/// hue/saturation, exposure) and the Camera Raw session — its draft state, preview
/// pipeline, and the point-curve editor canvas.</summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// Filter ▸ Camera Raw Filter… is the door (CompositorApp.swift:283-288 puts every non-adjustment
    /// FilterKind in the Filter menu, and Filters.swift:16 names this one "Camera Raw Filter"). The Mac
    /// shows the panel because a filter-edit session exists (ContentView.swift:249-257), never because a
    /// pixel layer happens to be selected — and Camera Raw is not in AdjustmentKind
    /// (LayerAdjustment.swift:4-10), so the property dock has no claim on it either. Opening it here is
    /// also what keeps a resident panel off the layers list: the body sits in the same dock the panel
    /// shares with levels and curves.
    /// </summary>
    private void OnCameraRawFilter(object sender, RoutedEventArgs e)
    {
        if (!CanEditLayerCommands || document is not { } owner || SelectedLayer is not { } layer) return;
        if (layer.ImageFile is null || owner.Snapshot.Images.GetValueOrDefault(layer.Id) is null)
        {
            SetStatus(Loc.T("S_PaintNeedsLayer"));
            return;
        }
        ShowCameraRawEditor(owner, layer.Id);
    }

    /// <summary>Builds the session and drops its body into the window's right edge, full height — the shape the
    /// Mac gives this one filter and only this one (`ContentView.swift:253` picks
    /// `.dockedToMainWindowRight` for `kind == .cameraRaw` and `.automatic` for every other sheet). A command
    /// while a session is already open is refused rather than restarted, because `beginFilter` guards on
    /// `filterEdit == nil` (Filters.swift:481) and silently cancelling the draft the user is working on is not
    /// what the original does. The refusal is a status line instead of the Mac's beep, as elsewhere in this
    /// port.</summary>
    private void ShowCameraRawEditor(EditorDocument owner, Guid layerId)
    {
        if (FloatingPanelIsOpen(CameraRawPanelKey))
        {
            SetStatus(Loc.T("S_FilterEditOpen"));
            return;
        }
        if (AnotherFilterSheetOpen) { RefuseSecondFilterSheet(); return; }
        var panel = BuildCameraRawPanel(owner, layerId);
        ShowFloatingPanel(CameraRawPanelKey, Loc.T("M_CameraRawFilterDialog"), panel, FinishCameraRawAsync,
            onAbandon: () => { if (document is { } retired) EndCameraRawSession(retired); },
            placement: FloatingPanelPlacement.DockedToMainWindowRight, keepOpenWhileFinishing: true);
    }

    /// <summary>One answer per sheet, as the Mac's bottom row (FilterSheet.swift:152-167): Cancel puts the
    /// frozen capture back inside the session's own undo step, OK writes the full-resolution render the eyes
    /// allow, and either way the session ends and the panel goes with it (Filters.swift:583, 616).</summary>
    private async Task FinishCameraRawAsync(bool commit)
    {
        var doc = document;
        if (doc is null || cameraRawSessionKey is not { } sessionKey || cameraRawOriginal is not { } pixels) return;
        var layerId = cameraRawSessionLayer;
        if (!commit)
        {
            if (cameraRawDraft is { IsIdentity: false })
            {
                MarkLayerBandDirty(layerId);
                doc.ReplaceLayerImage(layerId, cameraRawOriginal, sessionKey);
            }
            EndCameraRawSession(doc);
            SetStatus(Loc.T("S_CamRawReset"));
            return;
        }
        if (cameraRawDraft is not { IsIdentity: false } draft)
        {
            // Nothing to push: OK is Cancel, and it says so the way the port's other sheets do.
            EndCameraRawSession(doc);
            SetStatus(Loc.T("S_CamRawReset"));
            return;
        }
        // OK is the same render the eyes drive: the Mac commits renderSettings(), not the draft
        // (Filters.swift:600, 620), so a hidden group does not come back when the panel closes.
        var render = draft.Clone().Applying(cameraRawShows);
        SetBusy(true);
        try
        {
            var graded = await Task.Run(() =>
            {
                var working = pixels.Clone();
                CameraRawEngine.Apply(working, render);
                // OK is the exact result: it grades the pristine capture at full size and, with a selection
                // live, leaves everything outside it as the capture had it.
                return cameraRawClip is { } clip ? DitherEngine.ClipToCoverage(pixels, working, clip) : working;
            });
            SetBusy(false);
            if (ReferenceEquals(document, doc) && sessionKey == cameraRawSessionKey)
            {
                MarkLayerBandDirty(layerId);
                doc.ReplaceLayerImage(layerId, graded, sessionKey);
                EndCameraRawSession(doc);
                cameraRawOriginal = graded;
                SetStatus(Loc.T("S_CamRawApplied"));
            }
        }
        catch (Exception ex)
        {
            SetBusy(false);
            App.Log($"camera raw apply failed: {ex}", LogLevel.Error);
        }
    }

    /// <summary>Takes the preview off the layer without touching the draft: the sheet's own Preview switch hides
    /// the result and keeps every number (FilterSheet.swift:143-144 writes through `updateFilter(preview:)`).</summary>
    private void RestoreCameraRawCapture()
    {
        cameraRawPreviewTicket++;
        cameraRawPreviewQueuedDoc = null;
        if (document is not { } doc || cameraRawSessionKey is not { } sessionKey || cameraRawOriginal is not { } pixels) return;
        MarkLayerBandDirty(cameraRawSessionLayer);
        doc.ReplaceLayerImage(cameraRawSessionLayer, pixels, sessionKey);
    }

    /// <summary>The sheet's key in the window's floating-panel host. Named because the door, the host and the
    /// probes all have to ask for the same sheet.</summary>
    internal const string CameraRawPanelKey = "cameraRawFilter";
    /// <summary>Whether the sheet's Preview switch is on, for the same reason as the filter forms' own PreviewOn flag (Filters.swift:517-536): the draft keeps the number, the canvas keeps the untouched picture.</summary>
    private bool cameraRawPreviewOn = true;
    private CameraRawSettings? cameraRawDraft;
    private Surface? cameraRawOriginal;
    private object? cameraRawSessionKey;
    private Guid cameraRawSessionLayer;
    /// <summary>The copy a preview tick grades: the layer averaged down to no more than
    /// <see cref="PreviewLimit"/> on its longest side, or the layer itself when it already fits.</summary>
    private Surface? cameraRawPreviewSource;
    /// <summary>The active selection mapped into the layer's pixels, or null when there is none. Every filter on
    /// the Mac runs through this clip (Filters.swift:268-269) and the sheet says so out loud
    /// (FilterSheet.swift:144-149); the Windows Camera Raw panel had neither, so a selection was ignored in
    /// silence.</summary>
    private MaskSurface? cameraRawClip;
    private TextBlock? cameraRawNotice;

    /// <summary>
    /// The Camera Raw panel: ten sliders previewing a draft grade on the layer's pixels.
    /// Preview ticks join one coalesced undo step, so dragging writes no undo history;
    /// Apply commits the whole session as that single step, Reset cancels out of it.
    /// </summary>
    private StackPanel BuildCameraRawPanel(EditorDocument doc, Guid layerId)
    {

        // Every row marks the band before it applies its number, which makes this the one place that can do what
        // the Mac's `assign` does for a slider that carries no clipping mode — take the Option view off, whatever
        // just moved (CameraRawControls.swift:330-334). The five light rows that do carry one set theirs after
        // this returns, and the mask view is not here: that one belongs to the Detail band alone.
        Action mark = () =>
        {
            SetCameraRawClipping(CameraRawEngine.ClippingNone, false);
            MarkLayerBandDirty(layerId);
        };
        var panel = new StackPanel { Spacing = 8 };
        var layer = doc.Snapshot.Manifest.Layers.FirstOrDefault(candidate => candidate.Id == layerId);
        if (layer?.ImageFile is null)
        {
            cameraRawSessionKey = null;
            panel.Children.Add(new TextBlock { Text = Loc.T("L_NoPixels"), TextWrapping = TextWrapping.Wrap });
            return panel;
        }
        // A fresh session per rebuild; close the previous one so the next preview tick
        // opens a new undo step instead of joining a stale key.
        if (cameraRawSessionKey is { } previous) doc.EndTransform(previous);
        cameraRawSessionKey = new object();
        cameraRawSessionLayer = layerId;
        cameraRawDraft = new CameraRawSettings();
        cameraRawOriginal = doc.Snapshot.Images[layer.Id];
        cameraRawPreviewSource = PreviewCopy(cameraRawOriginal);
        cameraRawClip = doc.SelectionCoverageForLayer(layer.Id, cameraRawOriginal.Width, cameraRawOriginal.Height);

        // The ten eyes. Like the Mac's flags they live on the session, not on the settings
        // (Filters.swift:298-307), so a panel opened again shows every group; and like the Mac they change only
        // what is *rendered* — the draft keeps every number, which is why an eye closed and opened again brings
        // the same grade back (renderSettings(), Filters.swift:451-461).
        cameraRawShows = Enumerable.Repeat(true, CameraRawSettings.Groups).ToArray();
        var eyes = new List<(int Group, Button Eye)>();

        Button MakeEye(int group)
        {
            var eye = new Button();
            eye.Click += (_, _) =>
            {
                cameraRawShows[group] = !cameraRawShows[group];
                RefreshCameraRawEyes();
                PreviewCameraRaw(doc, layerId);
            };
            eyes.Add((group, eye));
            return eye;
        }

        // The Mac draws the eye as an SF Symbol and puts the words in the accessibility label and the help
        // ("Hide Light in the preview"); this panel has no symbol font to borrow, so the same two words are the
        // button's face too — the label describing the action, as the Mac's does, is what a reader gets.
        UIElement BandHeader(string title, Button eye) => new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center },
                eye,
            },
        };

        void RefreshCameraRawEyes()
        {
            var settings = cameraRawDraft ?? new CameraRawSettings();
            foreach (var (group, eye) in eyes)
            {
                // Only a group that is doing something offers an eye (CameraRawControls.swift:139-148).
                eye.Visibility = settings.AdjustsGroup(group) ? Visibility.Visible : Visibility.Collapsed;
                var name = CameraRawSettings.GroupNames[group];
                var label = Loc.F(cameraRawShows[group] ? "A_HideGroup" : "A_ShowGroup", name);
                eye.Content = label;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(eye, label);
                ToolTipService.SetToolTip(eye,
                    Loc.F(cameraRawShows[group] ? "H_HideGroupPreview" : "H_ShowGroupPreview", name));
            }
        }

        void AddGroupLabel(string title, int group)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center },
                    MakeEye(group),
                },
            };
            panel.Children.Add(row);
        }

        // One band, one eye. The header used to be the bare title string, which every probe read; it keeps the
        // same text inside the header and carries it in Tag so a finder does not have to unpick the panel.
        void AddBand(string headerKey, int group, UIElement content)
        {
            var band = new Expander
            {
                Header = BandHeader(Loc.T(headerKey), MakeEye(group)),
                Content = content,
                Tag = Loc.T(headerKey),
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(band, Loc.T(headerKey));
            // The disclosure row's toggle button is what a reader lands on, and a composite header (the title
            // plus that group's eye) gives it nothing to announce — the eight unnamed "ExpanderToggleButton"
            // entries the sweep reported the moment this sheet came on screen. It only exists after the
            // template has been applied, which is after the first layout, so it is named there once. A
            // subclass would not do: it falls outside the implicit TargetType="Expander" style, and the
            // template then applies that style to its own ContentControl, which fails the process.
            var bandTitle = Loc.T(headerKey);
            var bandTogglesNamed = false;
            band.LayoutUpdated += (_, _) =>
            {
                if (bandTogglesNamed) return;
                if (FindBandToggle(band) is not { } toggle) return;
                bandTogglesNamed = true;
                if (string.IsNullOrWhiteSpace(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(toggle)))
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, bandTitle);
            };
            panel.Children.Add(band);
        }

        static Microsoft.UI.Xaml.Controls.Primitives.ToggleButton? FindBandToggle(Microsoft.UI.Xaml.DependencyObject root)
        {
            for (int i = 0, n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i < n; i++)
            {
                var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
                if (child is Microsoft.UI.Xaml.Controls.Primitives.ToggleButton toggle) return toggle;
                if (FindBandToggle(child) is { } found) return found;
            }

            return null;
        }

        void AddGradeSlider(string label, double minimum, double maximum, Action<double> set,
                            int clipping = CameraRawEngine.ClippingNone)
        {
            panel.Children.Add(SliderRow(label, minimum, maximum, 0, value =>
            {
                set(value);
                SetCameraRawClipping(clipping, KeyHeld(Windows.System.VirtualKey.Menu));
                PreviewCameraRaw(doc, layerId);
            }, markBand: mark));
        }

        // The scope sits above every band (UI/CameraRawControls.swift:16-31): one frame showing either the three
        // ribbons or the vectorscope. Which one is chosen is a session fact, not a setting, and the Mac reaches
        // it through a right-click menu — the same two commands here, on the same flyout.
        cameraRawScope = null;
        cameraRawScopeMode = ScopeHistogram;
        var scopeCanvas = new CameraRawScopeCanvas();
        cameraRawScopeCanvas = scopeCanvas;
        var scopeMenu = new MenuFlyout();
        var scopeHistogramItem = new MenuFlyoutItem { Text = Loc.T("L_ScopeHistogram") };
        scopeHistogramItem.Click += (_, _) => SetCameraRawScopeMode(ScopeHistogram);
        var scopeVectorscopeItem = new MenuFlyoutItem { Text = Loc.T("L_ScopeVectorscope") };
        scopeVectorscopeItem.Click += (_, _) => SetCameraRawScopeMode(ScopeVectorscope);
        scopeMenu.Items.Add(scopeHistogramItem);
        scopeMenu.Items.Add(scopeVectorscopeItem);
        scopeCanvas.ContextFlyout = scopeMenu;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(scopeCanvas, Loc.T("H_ScopeHint"));
        cameraRawScopeRefresh = () =>
        {
            scopeCanvas.Show(cameraRawScope, cameraRawScopeMode == ScopeVectorscope);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(scopeCanvas, Loc.T(
                cameraRawScopeMode == ScopeVectorscope ? "L_ScopeVectorscope" : "A_ScopeHistogram"));
        };
        cameraRawScopeRefresh();
        panel.Children.Add(scopeCanvas);

        // The row the Mac puts directly under that frame (UI/CameraRawControls.swift:40-46): the shadow indicator
        // on the left, the highlight one on the right, each a filled triangle that turns blue or red while it is
        // showing. The original draws them with an SF Symbol and keeps the words in the tooltip and the
        // narrator's label; this port has no symbol font to lean on, so the glyph is a plain "▲" and the name is
        // what a reader hears. Both are session state: neither reaches OK nor the project file.
        cameraRawShowsShadowClipping = false;
        cameraRawShowsHighlightClipping = false;
        var clipRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var offClip = new SolidColorBrush(Microsoft.UI.Colors.White) { Opacity = 0.55 };
        Windows.UI.Color OffClipTint = offClip.Color;
        var shadowClipButton = new Button { Content = "▲", MinWidth = 40 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(shadowClipButton, Loc.T("A_ShadowClipping"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(shadowClipButton, Loc.T("H_ShadowClipping"));
        var highlightClipButton = new Button { Content = "▲", MinWidth = 40 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(highlightClipButton, Loc.T("A_HighlightClipping"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(highlightClipButton, Loc.T("H_HighlightClipping"));
        shadowClipButton.Click += (_, _) => ToggleCameraRawClipping(highlights: false);
        highlightClipButton.Click += (_, _) => ToggleCameraRawClipping(highlights: true);
        clipRow.Children.Add(shadowClipButton);
        clipRow.Children.Add(highlightClipButton);
        panel.Children.Add(clipRow);
        cameraRawClipRefresh = () =>
        {
            // The Mac shows the state by the triangle's colour, not by a control state (Controls:75-77).
            shadowClipButton.Foreground = new SolidColorBrush(cameraRawShowsShadowClipping
                ? Microsoft.UI.Colors.MediumBlue : OffClipTint);
            highlightClipButton.Foreground = new SolidColorBrush(cameraRawShowsHighlightClipping
                ? Microsoft.UI.Colors.Red : OffClipTint);
        };
        cameraRawClipRefresh();

        AddGroupLabel(Loc.T("L_GroupLight"), CameraRawSettings.GroupLight);
        AddGradeSlider(Loc.T("L_Exposure"), -5, 5, value => cameraRawDraft!.Exposure = value,
                       CameraRawEngine.LightClippingOf(nameof(CameraRawSettings.Exposure)));
        AddGradeSlider(Loc.T("L_Contrast"), -100, 100, value => cameraRawDraft!.Contrast = value);
        AddGradeSlider(Loc.T("L_Highlights"), -100, 100, value => cameraRawDraft!.Highlights = value,
                       CameraRawEngine.LightClippingOf(nameof(CameraRawSettings.Highlights)));
        AddGradeSlider(Loc.T("L_Shadows"), -100, 100, value => cameraRawDraft!.Shadows = value,
                       CameraRawEngine.LightClippingOf(nameof(CameraRawSettings.Shadows)));
        AddGradeSlider(Loc.T("L_Whites"), -100, 100, value => cameraRawDraft!.Whites = value,
                       CameraRawEngine.LightClippingOf(nameof(CameraRawSettings.Whites)));
        AddGradeSlider(Loc.T("L_Blacks"), -100, 100, value => cameraRawDraft!.Blacks = value,
                       CameraRawEngine.LightClippingOf(nameof(CameraRawSettings.Blacks)));

        // --- White balance ---
        AddGroupLabel(Loc.T("L_GroupColor"), CameraRawSettings.GroupColor);
        // The Mac's row (UI/CameraRawControls.swift:184-208): a mode picker, the eyedropper that turns one
        // clicked pixel of the original layer neutral, and the caption saying what a click will do while the
        // selector is armed. Choosing a mode moves no pixel by itself — Auto's answer arrives a moment later
        // and lands on the two sliders below, which is why those two rows are the only ones built by hand here.
        var balanceRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var balanceMode = new ComboBox
        {
            Width = 120,
            ItemsSource = CameraRawSettings.WhiteBalanceNames,
            SelectedIndex = cameraRawDraft.WhiteBalance == CameraRawSettings.WhiteBalanceAuto ? 1 : 0,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(balanceMode, Loc.T("L_WhiteBalance"));
        var balanceSelector = new ToggleButton { Content = Loc.T("L_WhiteBalanceSelector") };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(balanceSelector, Loc.T("A_WhiteBalanceSelector"));
        var balanceNote = new TextBlock
        {
            Text = Loc.T("L_WhiteBalanceClickHint"), Opacity = 0.7, Visibility = Visibility.Collapsed,
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
        };
        balanceSelector.Checked += (_, _) => { cameraRawSamplesWhiteBalance = true; balanceNote.Visibility = Visibility.Visible; };
        balanceSelector.Unchecked += (_, _) => { cameraRawSamplesWhiteBalance = false; balanceNote.Visibility = Visibility.Collapsed; };
        cameraRawBalanceNote = balanceNote;
        balanceRow.Children.Add(balanceMode);
        balanceRow.Children.Add(balanceSelector);
        panel.Children.Add(balanceRow);
        panel.Children.Add(balanceNote);

        Slider BalanceSlider(string label, Action<double> set)
        {
            var grid = SliderRow(label, -100, 100, 0, value =>
            {
                set(value);
                // Touching either slider is a manual edit, so the mode drops back to Custom — the Mac does the
                // same in `assign` (CameraRawControls.swift:331-335). The solve writes through the flag instead,
                // because Auto's answer arriving must not turn the picker off.
                if (!cameraRawBalanceWriting) cameraRawDraft!.WhiteBalance = CameraRawSettings.WhiteBalanceCustom;
                PreviewCameraRaw(doc, layerId);
            }, markBand: mark);
            panel.Children.Add(grid);
            return grid.Children.OfType<Slider>().Single();
        }

        var temperatureSlider = BalanceSlider(Loc.T("L_Temperature"), value => cameraRawDraft!.Temperature = value);
        var tintSlider = BalanceSlider(Loc.T("L_Tint"), value => cameraRawDraft!.Tint = value);
        cameraRawPushBalance = (temperature, tint) =>
        {
            cameraRawBalanceWriting = true;
            try
            {
                // The solve answers in unclamped units — a strongly colored pixel solves past ±100 routinely —
                // and the sliders, like the Mac's normalization, are the ceiling.
                temperatureSlider.Value = Math.Clamp(temperature, CameraRawSettings.ToneMin, CameraRawSettings.ToneMax);
                tintSlider.Value = Math.Clamp(tint, CameraRawSettings.ToneMin, CameraRawSettings.ToneMax);
            }
            finally { cameraRawBalanceWriting = false; }
        };
        balanceMode.SelectionChanged += (_, _) =>
        {
            cameraRawDraft!.WhiteBalance = balanceMode.SelectedIndex == 1
                ? CameraRawSettings.WhiteBalanceAuto : CameraRawSettings.WhiteBalanceCustom;
            if (cameraRawDraft.WhiteBalance == CameraRawSettings.WhiteBalanceAuto) ApplyAutoWhiteBalance(doc, layerId);
            else PreviewCameraRaw(doc, layerId);
        };

        AddGradeSlider(Loc.T("L_Vibrance"), -100, 100, value => cameraRawDraft!.Vibrance = value);
        AddGradeSlider(Loc.T("L_Saturation"), -100, 100, value => cameraRawDraft!.Saturation = value);

        // The grain pattern stays put while the panel is open, as on the Mac.
        cameraRawDraft.GrainSeed = (uint)Random.Shared.Next();

        List<CurvePoint> DraftCurve(int channel) => channel switch
        {
            1 => cameraRawDraft!.CurveRed,
            2 => cameraRawDraft!.CurveGreen,
            3 => cameraRawDraft!.CurveBlue,
            _ => cameraRawDraft!.CurveRgb,
        };

        void StoreCurve(int channel, List<CurvePoint> points)
        {
            var stored = points.Select(point => new CurvePoint(point.X / 255.0, point.Y / 255.0)).ToList();
            switch (channel)
            {
                case 1: cameraRawDraft!.CurveRed = stored; break;
                case 2: cameraRawDraft!.CurveGreen = stored; break;
                case 3: cameraRawDraft!.CurveBlue = stored; break;
                default: cameraRawDraft!.CurveRgb = stored; break;
            }
            PreviewCameraRaw(doc, layerId);
        }

        // --- Curve: the Mac's two pages (CameraRawColorControls.swift:13-62) ---
        // The page decides what the rest of the band is. On Parametric the graph draws the curve the four tonal
        // regions make, with the three dividers along its bottom edge, and the rows under it are those regions,
        // listed brightest first as the Mac lists them. On Point the graph draws the anchors of one channel and
        // the rows are that channel's Refine Saturation. Both canvases are built once and shown by page, because
        // a drag inside the graph must not destroy the control that is holding the pointer.
        var curveSection = new StackPanel { Spacing = 8 };
        var curvePage = new ComboBox
        {
            Width = 150,
            ItemsSource = CameraRawSettings.CurvePageNames,
            SelectedIndex = Math.Clamp(cameraRawCurvePage, 0, CameraRawSettings.CurvePageNames.Length - 1),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(curvePage, Loc.T("L_CurvePage"));
        var curveChannel = new ComboBox
        {
            Width = 120,
            ItemsSource = new[] { Loc.T("Chan_RGB"), Loc.T("Chan_Red"), Loc.T("Chan_Green"), Loc.T("Chan_Blue") },
            SelectedIndex = Math.Clamp(cameraRawPointChannel, 0, 3),
        };
        var curveCanvas = new CurveEditCanvas(
            load: () => DraftCurve(curveChannel.SelectedIndex).Select(point => new CurvePoint(point.X * 255, point.Y * 255)).ToList(),
            save: points => StoreCurve(curveChannel.SelectedIndex, points));
        var curveGraph = new CurveGraphCanvas(
            samples: CameraRawParametricSamples,
            splits: CameraRawCurveSplits,
            press: BeginCameraRawCurveGraphDrag,
            move: ContinueCameraRawCurveGraphDrag,
            release: EndCameraRawCurveGraphDrag);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(curveChannel, Loc.T("L_CurveChannel"));
        var curveAmountRows = new StackPanel { Spacing = 8 };
        // The point page's two extras (CameraRawColorControls.swift:41-52): the selected point's input and output
        // in the 0…255 the curve is authored in, and the preset picker, which *reads back* what the channel's
        // points already are rather than remembering what was last chosen.
        var curveInOut = new TextBlock
        {
            FontSize = 11,
            Opacity = 0.7,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(curveInOut, Loc.T("L_CurvePointReadout"));
        var curvePreset = new ComboBox
        {
            Width = 170,
            ItemsSource = CameraRawCurvePresets.Names,
            SelectedIndex = CameraRawCurvePresets.Custom,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(curvePreset, Loc.T("L_CurvePreset"));
        bool readingBackPreset = false;
        // The two Targeted Adjustment arms (CameraRawColorControls.swift:56-63 and :296-303): each button owns
        // the canvas for its own group, and arming one takes the other off — the Mac's own handlers clear the
        // other flag first, so the two can never both be listening.
        var curveTarget = new ToggleButton { Content = Loc.T("L_TargetedAdjustment") };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(curveTarget, Loc.T("A_TargetedCurve"));
        ToggleButton? mixerTarget = null;
        var refineRow = SliderRow(Loc.T("L_RefineSat"), -100, 100, cameraRawDraft!.RefineSaturation, value =>
        {
            cameraRawDraft!.RefineSaturation = value;
            PreviewCameraRaw(doc, layerId);
        }, markBand: mark);

        void RebuildCurveAmounts()
        {
            curveAmountRows.Children.Clear();
            for (int row = 0; row < CameraRawSettings.CurveRegionNames.Length; row++)
            {
                // The rows are the brightest region first, while the dividers cut the range in the other
                // direction, so the display order is the reverse of the region index (Ctrls:36-39).
                int region = CameraRawSettings.CurveRegionNames.Length - 1 - row;
                curveAmountRows.Children.Add(SliderRow(CameraRawSettings.CurveRegionNames[row], -100, 100,
                    CurveRegionValue(cameraRawDraft!, region), tick =>
                    {
                        SetCurveRegionValue(cameraRawDraft!, region, tick);
                        PreviewCameraRaw(doc, layerId);
                    }, markBand: mark));
            }
        }

        // A collapsed row leaves what is inside it reporting Visible, and on WinUI a child of these rows has no
        // Parent to ask, so nothing downstream can tell that the row is gone. The page therefore sets the state
        // on the row and on each control in it — the same reason CR-e sets the eyedropper's own Visibility.
        static void SetRowShown(UIElement? row, bool shown)
        {
            if (row is null) return;
            row.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            if (row is Grid grid)
            {
                foreach (var child in grid.Children.OfType<UIElement>())
                {
                    child.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
                }
            }
        }

        void UpdateCurveReadout()
        {
            // The In／Out line is the selected point's own numbers (Ctrls:41-46), and the picker says what the
            // channel's points currently *are* (:47), so both follow every edit rather than only a click.
            var selected = curveCanvas.SelectedPoint;
            curveInOut.Text = selected is { } point && cameraRawCurvePage == CameraRawSettings.CurvePagePoint
                ? Loc.F("CE_InOut", Math.Round(point.X), Math.Round(point.Y))
                : string.Empty;
            readingBackPreset = true;
            curvePreset.SelectedIndex = CameraRawCurvePresets.Matching(DraftCurve(cameraRawPointChannel));
            readingBackPreset = false;
        }

        void ShowCurvePage()
        {
            bool pointPage = cameraRawCurvePage == CameraRawSettings.CurvePagePoint;
            curveChannel.Visibility = pointPage ? Visibility.Visible : Visibility.Collapsed;
            curveCanvas.Visibility = pointPage ? Visibility.Visible : Visibility.Collapsed;
            curveCanvas.StatusLabel.Visibility = pointPage ? Visibility.Visible : Visibility.Collapsed;
            curveGraph.Visibility = pointPage ? Visibility.Collapsed : Visibility.Visible;
            curveGraph.StatusLabel.Visibility = pointPage ? Visibility.Collapsed : Visibility.Visible;
            curveAmountRows.Visibility = pointPage ? Visibility.Collapsed : Visibility.Visible;
            RebuildCurveAmounts();
            foreach (var row in curveAmountRows.Children.OfType<UIElement>())
            {
                SetRowShown(row, !pointPage);
            }
            // Refine Saturation belongs to the composite curve only (Ctrls:54-56): the Mac shows it on the point
            // page and only while that page is editing RGB.
            SetRowShown(refineRow, pointPage && cameraRawPointChannel == 0);
            SetRowShown(curveInOut, pointPage);
            curvePreset.Visibility = pointPage ? Visibility.Visible : Visibility.Collapsed;
            curveCanvas.Reload();
            curveGraph.Reload();
            UpdateCurveReadout();
        }

        curvePage.SelectionChanged += (_, _) =>
        {
            cameraRawCurvePage = curvePage.SelectedIndex;
            // A grab decided on one page means nothing on the other, so the page change lets go — the same rule
            // the Mac applies when the channel changes (Ctrls:28).
            EndCameraRawCurveGraphDrag();
            ShowCurvePage();
        };
        curvePreset.SelectionChanged += (_, _) =>
        {
            if (readingBackPreset) return;
            var shape = CameraRawCurvePresets.Points(curvePreset.SelectedIndex);
            // Custom installs nothing (Ctrls:226-229): it is what the picker reads back, not a request to write.
            if (shape is null) return;
            StoreCurve(curveChannel.SelectedIndex,
                shape.Select(point => new CurvePoint(point.X * 255, point.Y * 255)).ToList());
            // The Mac does not take the selection away here — `selected` is an index, and it now names whatever
            // sits at that index in the new curve (Ctrls:118 guards it by range). The readout follows that.
            ShowCurvePage();
        };
        curveChannel.SelectionChanged += (_, _) =>
        {
            cameraRawPointChannel = curveChannel.SelectedIndex;
            // The selection and any live drag belong to the channel they were made on (Ctrls:28).
            curveCanvas.ClearSelection();
            ShowCurvePage();
        };
        curveTarget.Checked += (_, _) =>
        {
            cameraRawTargetsCurve = true;
            cameraRawTargetsMixer = false;
            if (mixerTarget is { } other) other.IsChecked = false;
        };
        curveTarget.Unchecked += (_, _) =>
        {
            cameraRawTargetsCurve = false;
            // The drag ends with the arm that owns it, as on the Mac, where the release clears it and the
            // button only decides what the next press does (EditorCanvas.swift:2029).
            EndCameraRawTargetedDrag();
        };
        curveSection.Children.Add(curvePage);
        curveSection.Children.Add(curveChannel);
        curveSection.Children.Add(curveCanvas);
        curveSection.Children.Add(curveCanvas.StatusLabel);
        curveSection.Children.Add(curveGraph);
        curveSection.Children.Add(curveGraph.StatusLabel);
        curveSection.Children.Add(curveInOut);
        curveSection.Children.Add(curvePreset);
        curveSection.Children.Add(curveAmountRows);
        curveSection.Children.Add(refineRow);
        curveSection.Children.Add(curveTarget);
        AddBand("L_Curve", CameraRawSettings.GroupCurve, curveSection);
        curveCanvas.SelectionChanged = UpdateCurveReadout;
        cameraRawCurveCanvas = curveCanvas;
        cameraRawCurveReadout = curveInOut;
        ShowCurvePage();

        // --- Color Mixer ---
        // The Mac's three pages (CameraRawColorControls.swift:272-303): HSL lists all eight families of one
        // component, Color edits one family at a time, Point Color edits a color picked on the picture. The page
        // and the component are the panel's own state, as they are on the Mac (Filters.swift:308-313), so
        // switching pages moves no pixel and the choice survives a rebuild for another layer.
        var mixerSection = new StackPanel { Spacing = 8 };
        var mixerPage = new ComboBox
        {
            Width = 150,
            ItemsSource = CameraRawSettings.MixerPageNames,
            SelectedIndex = Math.Clamp(cameraRawMixerPage, 0, CameraRawSettings.MixerPageNames.Length - 1),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(mixerPage, Loc.T("L_MixerPage"));
        var mixerComponent = new ComboBox
        {
            Width = 150,
            ItemsSource = CameraRawSettings.MixerComponentNames,
            SelectedIndex = Math.Clamp(cameraRawMixerTab, 0, CameraRawSettings.MixerComponentNames.Length - 1),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(mixerComponent, Loc.T("L_MixerComponent"));
        // The Mac's Color page is a row of eight swatches; a picker says the same thing to a reader and to a
        // keyboard without eight buttons that differ only by color.
        var mixerFamily = new ComboBox
        {
            Width = 140,
            ItemsSource = CameraRawSettings.MixerNames,
            SelectedIndex = Math.Clamp(cameraRawMixerSwatch, 0, CameraRawSettings.MixerFamilies - 1),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(mixerFamily, Loc.T("L_MixerFamily"));
        var mixerRows = new StackPanel { Spacing = 8 };
        var pointPickerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var pointPicker = new ToggleButton { Content = Loc.T("L_PointColorPicker") };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(pointPicker, Loc.T("A_PointColorPicker"));
        var pointChips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        pointPickerRow.Children.Add(pointPicker);
        pointPickerRow.Children.Add(pointChips);
        var pointHint = new TextBlock
        {
            Text = Loc.T("L_PointColorClickHint"), Opacity = 0.7, FontSize = 11,
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap, Visibility = Visibility.Collapsed,
        };
        var pointRows = new StackPanel { Spacing = 8 };
        var visualizeRange = new CheckBox { Content = Loc.T("L_VisualizeRange") };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(visualizeRange, Loc.T("L_VisualizeRange"));
        // The picked color and the six numbers that move it. Each row writes one field of one point and leaves
        // the rest of the point exactly as it was (CameraRawColorControls.swift:376-389 edits the selected index
        // only, and the point list is indexed by the picker's own selection, never by the row's position).
        void PointSlider(string label, double minimum, double maximum, double value, Func<double, CameraRawMixerPoint> write)
        {
            pointRows.Children.Add(SliderRow(label, minimum, maximum, value, tick =>
            {
                int index = cameraRawPointIndex;
                if (cameraRawDraft?.MixerPoints is not { } points || index < 0 || index >= points.Count) return;
                points[index] = write(tick);
                PreviewCameraRaw(doc, layerId);
            }, markBand: mark));
        }
        CameraRawMixerPoint PointWith(Func<CameraRawMixerPoint, CameraRawMixerPoint> change)
        {
            var point = cameraRawDraft!.MixerPoints[cameraRawPointIndex];
            return change(point);
        }
        void RebuildPointRows()
        {
            pointRows.Children.Clear();
            visualizeRange.Visibility = Visibility.Collapsed;
            if (cameraRawDraft is not { } draft) return;
            if (cameraRawPointIndex < 0 || cameraRawPointIndex >= draft.MixerPoints.Count) return;
            var point = draft.MixerPoints[cameraRawPointIndex];
            PointSlider(Loc.T("L_PointHueShift"), -100, 100, point.HueShift,
                value => PointWith(current => current with { HueShift = value }));
            PointSlider(Loc.T("L_PointSaturationShift"), -100, 100, point.SaturationShift,
                value => PointWith(current => current with { SaturationShift = value }));
            PointSlider(Loc.T("L_PointLuminanceShift"), -100, 100, point.LuminanceShift,
                value => PointWith(current => current with { LuminanceShift = value }));
            PointSlider(Loc.T("L_PointHueRange"), 5, 180, point.HueRange,
                value => PointWith(current => current with { HueRange = value }));
            PointSlider(Loc.T("L_PointSaturationRange"), 0.05, 1, point.SaturationRange,
                value => PointWith(current => current with { SaturationRange = value }));
            PointSlider(Loc.T("L_PointLuminanceRange"), 0.05, 1, point.LuminanceRange,
                value => PointWith(current => current with { LuminanceRange = value }));
            visualizeRange.IsChecked = point.Visualize;
        }
        void RebuildPointChips()
        {
            pointChips.Children.Clear();
            if (cameraRawDraft is not { } draft) return;
            for (int index = 0; index < draft.MixerPoints.Count; index++)
            {
                var point = draft.MixerPoints[index];
                var chip = new Button
                {
                    Content = $"{index + 1}",
                    Width = 30,
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(MixerPointChipColor(point)),
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(chip, $"{Loc.T("A_PointColorChip")} {index + 1}");
                int captured = index;
                chip.Click += (_, _) =>
                {
                    // Choosing a chip moves the six rows and the dim, not the grade (Filters.swift:313).
                    cameraRawPointIndex = captured;
                    RebuildPointRows();
                    PreviewCameraRaw(doc, layerId);
                };
                pointChips.Children.Add(chip);
            }
        }
        void RebuildMixer()
        {
            mixerRows.Children.Clear();
            pointRows.Children.Clear();
            bool pointPage = mixerPage.SelectedIndex == CameraRawSettings.MixerPagePoint;
            bool hslPage = mixerPage.SelectedIndex == CameraRawSettings.MixerPageHsl;
            mixerComponent.Visibility = hslPage ? Visibility.Visible : Visibility.Collapsed;
            mixerFamily.Visibility = pointPage || hslPage ? Visibility.Collapsed : Visibility.Visible;
            pointPickerRow.Visibility = pointPage ? Visibility.Visible : Visibility.Collapsed;
            pointPicker.Visibility = pointPage ? Visibility.Visible : Visibility.Collapsed;
            pointHint.Visibility = pointPage && cameraRawSamplesPointColor ? Visibility.Visible : Visibility.Collapsed;
            visualizeRange.Visibility = Visibility.Collapsed;
            if (pointPage)
            {
                RebuildPointChips();
                RebuildPointRows();
                mixerRows.Children.Add(pointRows);
                mixerRows.Children.Add(visualizeRange);
                return;
            }
            if (hslPage)
            {
                // Eight rows of one component, each named by both halves of what it is: a reader hears the
                // family and the component, which is the Mac's help text folded into the row ("Hue of Greens.",
                // :318) — the same way the calibration band names its six primary rows.
                int tab = mixerComponent.SelectedIndex;
                for (int index = 0; index < CameraRawSettings.MixerFamilies; index++)
                {
                    var label = $"{CameraRawSettings.MixerNames[index]} {CameraRawSettings.MixerComponentNames[tab]}";
                    var value = tab switch
                    {
                        1 => cameraRawDraft!.MixerSaturation[index],
                        2 => cameraRawDraft!.MixerLuminance[index],
                        _ => cameraRawDraft!.MixerHue[index],
                    };
                    int captured = index;
                    mixerRows.Children.Add(SliderRow(label, -100, 100, value, tick =>
                    {
                        switch (tab)
                        {
                            case 1: cameraRawDraft!.MixerSaturation[captured] = tick; break;
                            case 2: cameraRawDraft!.MixerLuminance[captured] = tick; break;
                            default: cameraRawDraft!.MixerHue[captured] = tick; break;
                        }
                        PreviewCameraRaw(doc, layerId);
                    }, markBand: mark));
                }
                return;
            }
            int band = Math.Max(0, mixerFamily.SelectedIndex);
            mixerRows.Children.Add(SliderRow(Loc.T("L_Hue"), -100, 100, cameraRawDraft!.MixerHue[band], value =>
            {
                cameraRawDraft!.MixerHue[band] = value;
                PreviewCameraRaw(doc, layerId);
            }, markBand: mark));
            mixerRows.Children.Add(SliderRow(Loc.T("L_Saturation"), -100, 100, cameraRawDraft!.MixerSaturation[band], value =>
            {
                cameraRawDraft!.MixerSaturation[band] = value;
                PreviewCameraRaw(doc, layerId);
            }, markBand: mark));
            mixerRows.Children.Add(SliderRow(Loc.T("L_Luminance"), -100, 100, cameraRawDraft!.MixerLuminance[band], value =>
            {
                cameraRawDraft!.MixerLuminance[band] = value;
                PreviewCameraRaw(doc, layerId);
            }, markBand: mark));
        }
        mixerPage.SelectionChanged += (_, _) =>
        {
            cameraRawMixerPage = mixerPage.SelectedIndex;
            RebuildMixer();
        };
        mixerComponent.SelectionChanged += (_, _) =>
        {
            cameraRawMixerTab = mixerComponent.SelectedIndex;
            RebuildMixer();
        };
        mixerFamily.SelectionChanged += (_, _) =>
        {
            cameraRawMixerSwatch = mixerFamily.SelectedIndex;
            RebuildMixer();
        };
        pointPicker.Checked += (_, _) =>
        {
            cameraRawSamplesPointColor = true;
            pointHint.Visibility = Visibility.Visible;
        };
        pointPicker.Unchecked += (_, _) =>
        {
            cameraRawSamplesPointColor = false;
            pointHint.Visibility = Visibility.Collapsed;
        };
        visualizeRange.Checked += (_, _) => SetMixerPointVisualize(true);
        visualizeRange.Unchecked += (_, _) => SetMixerPointVisualize(false);
        cameraRawMixerRefresh = () =>
        {
            RebuildPointChips();
            RebuildPointRows();
        };
        // The mixer's own Targeted Adjustment arm, the same button shape as the curve band's and mutually
        // exclusive with it (CameraRawColorControls.swift:296-303).
        mixerTarget = new ToggleButton { Content = Loc.T("L_TargetedAdjustment") };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(mixerTarget, Loc.T("A_TargetedMixer"));
        mixerTarget.Checked += (_, _) =>
        {
            cameraRawTargetsMixer = true;
            cameraRawTargetsCurve = false;
            if (curveTarget.IsChecked == true) curveTarget.IsChecked = false;
        };
        mixerTarget.Unchecked += (_, _) =>
        {
            cameraRawTargetsMixer = false;
            EndCameraRawTargetedDrag();
        };
        // A drag rewrites whole groups of the settings, so the rows have to be re-read from them: the Mac's
        // panel is a view over the settings, this one is built by hand.
        cameraRawPanelRefresh = () =>
        {
            RebuildMixer();
            // A drag that moved numbers moves the curve band's rows and its graph too. ShowCurvePage rebuilds the
            // amount rows and reloads both canvases in place, so a drag that is holding the pointer keeps the
            // control that owns it.
            ShowCurvePage();
        };
        RebuildMixer();
        mixerSection.Children.Add(mixerPage);
        mixerSection.Children.Add(mixerComponent);
        mixerSection.Children.Add(mixerFamily);
        mixerSection.Children.Add(pointPickerRow);
        mixerSection.Children.Add(pointHint);
        mixerSection.Children.Add(mixerRows);
        mixerSection.Children.Add(mixerTarget);
        AddBand("L_ColorMixer", CameraRawSettings.GroupMixer, mixerSection);

        // --- Effects ---
        var effects = new StackPanel { Spacing = 8 };
        effects.Children.Add(SliderRow(Loc.T("L_Texture"), -100, 100, 0, value =>
        {
            cameraRawDraft!.Texture = value;
            PreviewCameraRaw(doc, layerId);
        }, markBand: mark));
        effects.Children.Add(SliderRow(Loc.T("L_Clarity"), -100, 100, 0, value =>
        {
            cameraRawDraft!.Clarity = value;
            PreviewCameraRaw(doc, layerId);
        }, markBand: mark));
        effects.Children.Add(SliderRow(Loc.T("L_Dehaze"), -100, 100, 0, value =>
        {
            cameraRawDraft!.Dehaze = value;
            PreviewCameraRaw(doc, layerId);
        }, markBand: mark));
        effects.Children.Add(SliderRow(Loc.T("L_Glow"), 0, 100, 0, value =>
        {
            cameraRawDraft!.Glow = value;
            PreviewCameraRaw(doc, layerId);
        }, markBand: mark));
        var glowStyle = new ComboBox { Width = 120, ItemsSource = new[] { Loc.T("L_Diffusion"), Loc.T("L_Bloom"), Loc.T("L_Halation") }, SelectedIndex = 0 };
        // The Mac's own word for this picker is "Style" (CameraRawControls.swift:231); the row it sits under
        // ("Glow") is not what a reader hears from the combo itself.
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(glowStyle, Loc.T("L_GlowStyle"));
        glowStyle.SelectionChanged += (_, _) =>
        {
            cameraRawDraft!.GlowStyle = Math.Max(0, glowStyle.SelectedIndex);
            PreviewCameraRaw(doc, layerId);
        };
        effects.Children.Add(glowStyle);
        effects.Children.Add(SliderRow(Loc.T("L_GlowRange"), 0, 100, 0, value =>
        {
            cameraRawDraft!.GlowRange = value;
            PreviewCameraRaw(doc, layerId);
        }, markBand: mark));
        effects.Children.Add(SliderRow(Loc.T("L_GlowSpread"), 0, 100, 0, value =>
        {
            cameraRawDraft!.GlowSpread = value;
            PreviewCameraRaw(doc, layerId);
        }, markBand: mark));
        effects.Children.Add(SliderRow(Loc.T("L_GlowWarmth"), -100, 100, 0, value =>
        {
            cameraRawDraft!.GlowWarmth = value;
            PreviewCameraRaw(doc, layerId);
        }, markBand: mark));
        effects.Children.Add(SliderRow(Loc.T("L_Vignette"), -100, 100, 0, value =>
        {
            cameraRawDraft!.VignetteAmount = value;
            PreviewCameraRaw(doc, layerId);
        }, markBand: mark));
        effects.Children.Add(SliderRow(Loc.T("L_Grain"), 0, 100, 0, value =>
        {
            cameraRawDraft!.GrainAmount = value;
            PreviewCameraRaw(doc, layerId);
        }, markBand: mark));
        AddBand("L_Effects", CameraRawSettings.GroupEffects, effects);

        // --- Color Grading ---
        var gradingSection = new StackPanel { Spacing = 8 };
        var gradingWheel = new ComboBox { Width = 120, ItemsSource = new[] { Loc.T("L_Shadows"), Loc.T("L_Midtones"), Loc.T("L_Highlights"), Loc.T("L_Global") }, SelectedIndex = 0 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(gradingWheel, Loc.T("L_GradingWheel")); // CameraRawColorControls.swift:435
        var gradingSliders = new StackPanel { Spacing = 8 };
        (double Hue, double Sat, double Lum) Wheel(int index) => index switch
        {
            1 => (cameraRawDraft!.GradingMidtonesHue, cameraRawDraft.GradingMidtonesSat, cameraRawDraft.GradingMidtonesLum),
            2 => (cameraRawDraft!.GradingHighlightsHue, cameraRawDraft.GradingHighlightsSat, cameraRawDraft.GradingHighlightsLum),
            3 => (cameraRawDraft!.GradingGlobalHue, cameraRawDraft.GradingGlobalSat, cameraRawDraft.GradingGlobalLum),
            _ => (cameraRawDraft!.GradingShadowsHue, cameraRawDraft.GradingShadowsSat, cameraRawDraft.GradingShadowsLum),
        };
        void SetWheel(int index, double hue, double sat, double lum)
        {
            switch (index)
            {
                case 1:
                    cameraRawDraft!.GradingMidtonesHue = hue;
                    cameraRawDraft.GradingMidtonesSat = sat;
                    cameraRawDraft.GradingMidtonesLum = lum;
                    break;
                case 2:
                    cameraRawDraft!.GradingHighlightsHue = hue;
                    cameraRawDraft.GradingHighlightsSat = sat;
                    cameraRawDraft.GradingHighlightsLum = lum;
                    break;
                case 3:
                    cameraRawDraft!.GradingGlobalHue = hue;
                    cameraRawDraft.GradingGlobalSat = sat;
                    cameraRawDraft.GradingGlobalLum = lum;
                    break;
                default:
                    cameraRawDraft!.GradingShadowsHue = hue;
                    cameraRawDraft.GradingShadowsSat = sat;
                    cameraRawDraft.GradingShadowsLum = lum;
                    break;
            }
        }
        void RebuildGrading()
        {
            gradingSliders.Children.Clear();
            int wheel = Math.Max(0, gradingWheel.SelectedIndex);
            var (hue, sat, lum) = Wheel(wheel);
            gradingSliders.Children.Add(SliderRow(Loc.T("L_Hue"), 0, 360, hue, value =>
            {
                var (_, s, l) = Wheel(wheel);
                SetWheel(wheel, value, s, l);
                PreviewCameraRaw(doc, layerId);
            }, markBand: mark));
            gradingSliders.Children.Add(SliderRow(Loc.T("L_Sat"), 0, 100, sat, value =>
            {
                var (h, _, l) = Wheel(wheel);
                SetWheel(wheel, h, value, l);
                PreviewCameraRaw(doc, layerId);
            }, markBand: mark));
            gradingSliders.Children.Add(SliderRow(Loc.T("L_Lum"), -100, 100, lum, value =>
            {
                var (h, s, _) = Wheel(wheel);
                SetWheel(wheel, h, s, value);
                PreviewCameraRaw(doc, layerId);
            }, markBand: mark));
        }
        gradingWheel.SelectionChanged += (_, _) => RebuildGrading();
        RebuildGrading();
        gradingSection.Children.Add(gradingWheel);
        gradingSection.Children.Add(gradingSliders);
        gradingSection.Children.Add(SliderRow(Loc.T("L_Blending"), 0, 100, 50, value =>
        {
            cameraRawDraft!.GradingBlending = value;
            PreviewCameraRaw(doc, layerId);
        }, markBand: mark));
        gradingSection.Children.Add(SliderRow(Loc.T("L_Balance"), -100, 100, 0, value =>
        {
            cameraRawDraft!.GradingBalance = value;
            PreviewCameraRaw(doc, layerId);
        }, markBand: mark));
        AddBand("L_ColorGrading", CameraRawSettings.GroupGrading, gradingSection);

        // --- Detail ---
        // Only the Masking row asks the keyboard about Option; every other number in this band puts the mask view
        // away, whatever the modifier is doing (CameraRawDetailOpticsControls.swift:70-77).
        void SetMaskView(bool maskingSlider)
            => SetCameraRawSharpenMask(maskingSlider, KeyHeld(Windows.System.VirtualKey.Menu));
        var detail = new StackPanel { Spacing = 8 };
        detail.Children.Add(SliderRow(Loc.T("L_Sharpening"), 0, 150, 0, value =>
        {
            cameraRawDraft!.SharpenAmount = value;
            SetMaskView(false);
            PreviewCameraRaw(doc, layerId);
        }, markBand: mark));
        detail.Children.Add(SliderRow(Loc.T("L_Radius"), 0, 100, 10, value =>
        {
            cameraRawDraft!.SharpenRadius = value;
            SetMaskView(false);
            PreviewCameraRaw(doc, layerId);
        }, markBand: mark));
        detail.Children.Add(SliderRow(Loc.T("L_Detail"), 0, 100, 25, value =>
        {
            cameraRawDraft!.SharpenDetail = value;
            SetMaskView(false);
            PreviewCameraRaw(doc, layerId);
        }, markBand: mark));
        detail.Children.Add(SliderRow(Loc.T("L_Masking"), 0, 100, 0, value =>
        {
            cameraRawDraft!.SharpenMasking = value;
            SetMaskView(true);
            PreviewCameraRaw(doc, layerId);
        }, markBand: mark));
        detail.Children.Add(SliderRow(Loc.T("L_NRLuminance"), 0, 100, 0, value =>
        {
            cameraRawDraft!.NoiseLuminance = value;
            SetMaskView(false);
            PreviewCameraRaw(doc, layerId);
        }, markBand: mark));
        detail.Children.Add(SliderRow(Loc.T("L_NRDetail"), 0, 100, 50, value =>
        {
            cameraRawDraft!.NoiseLuminanceDetail = value;
            SetMaskView(false);
            PreviewCameraRaw(doc, layerId);
        }, markBand: mark));
        detail.Children.Add(SliderRow(Loc.T("L_NRContrast"), 0, 100, 0, value =>
        {
            cameraRawDraft!.NoiseLuminanceContrast = value;
            SetMaskView(false);
            PreviewCameraRaw(doc, layerId);
        }, markBand: mark));
        detail.Children.Add(SliderRow(Loc.T("L_NRColor"), 0, 100, 0, value =>
        {
            cameraRawDraft!.NoiseColor = value;
            SetMaskView(false);
            PreviewCameraRaw(doc, layerId);
        }, markBand: mark));
        detail.Children.Add(SliderRow(Loc.T("L_NRColorDetail"), 0, 100, 50, value =>
        {
            cameraRawDraft!.NoiseColorDetail = value;
            SetMaskView(false);
            PreviewCameraRaw(doc, layerId);
        }, markBand: mark));
        detail.Children.Add(SliderRow(Loc.T("L_NRSmoothness"), 0, 100, 50, value =>
        {
            cameraRawDraft!.NoiseColorSmoothness = value;
            SetMaskView(false);
            PreviewCameraRaw(doc, layerId);
        }, markBand: mark));
        AddBand("L_Detail", CameraRawSettings.GroupDetail, detail);

        // --- Optics ---
        // The Mac's band (UI/CameraRawDetailOpticsControls.swift:86-137). Its two hue rows each carry a Low and a
        // High handle in one line; here they are six rows, because a reader announces a row and nothing else, and
        // a bare "Low" says nothing about which fringe it belongs to. The same reasoning puts "Lens" on the
        // vignette pair: the Effects band already owns a row called "Vignette".
        var optics = new StackPanel { Spacing = 8 };
        Slider OpticsSlider(Panel host, string label, double minimum, double maximum, double value, Action<double> set)
        {
            var grid = SliderRow(label, minimum, maximum, value, tick =>
            {
                set(tick);
                PreviewCameraRaw(doc, layerId);
            }, markBand: mark);
            host.Children.Add(grid);
            return grid.Children.OfType<Slider>().Single();
        }

        var opticsChromatic = new CheckBox { Content = Loc.T("L_RemoveChromaticAberration") };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(opticsChromatic, Loc.T("L_RemoveChromaticAberration"));
        opticsChromatic.IsChecked = cameraRawDraft.OpticsRemoveChromaticAberration;
        opticsChromatic.Checked += (_, _) =>
        {
            cameraRawDraft!.OpticsRemoveChromaticAberration = true;
            PreviewCameraRaw(doc, layerId);
        };
        opticsChromatic.Unchecked += (_, _) =>
        {
            cameraRawDraft!.OpticsRemoveChromaticAberration = false;
            PreviewCameraRaw(doc, layerId);
        };

        var opticsProfile = new CheckBox { Content = Loc.T("L_LensProfile") };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(opticsProfile, Loc.T("L_LensProfile"));
        opticsProfile.IsChecked = cameraRawDraft.OpticsLensProfile;
        var profileSection = new StackPanel
        {
            Spacing = 8, Visibility = cameraRawDraft.OpticsLensProfile ? Visibility.Visible : Visibility.Collapsed,
        };
        profileSection.Children.Add(new TextBlock
        {
            Text = Loc.T("L_NoLensMetadata"), Opacity = 0.7, FontSize = 11,
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
        });
        OpticsSlider(profileSection, Loc.T("L_ProfileDistortion"), 0, 100,
            cameraRawDraft.OpticsProfileDistortion, value => cameraRawDraft!.OpticsProfileDistortion = value);
        OpticsSlider(profileSection, Loc.T("L_ProfileVignetting"), 0, 100,
            cameraRawDraft.OpticsProfileVignetting, value => cameraRawDraft!.OpticsProfileVignetting = value);
        opticsProfile.Checked += (_, _) =>
        {
            cameraRawDraft!.OpticsLensProfile = true;
            profileSection.Visibility = Visibility.Visible;
            PreviewCameraRaw(doc, layerId);
        };
        opticsProfile.Unchecked += (_, _) =>
        {
            cameraRawDraft!.OpticsLensProfile = false;
            profileSection.Visibility = Visibility.Collapsed;
            PreviewCameraRaw(doc, layerId);
        };
        optics.Children.Add(opticsChromatic);
        optics.Children.Add(opticsProfile);
        optics.Children.Add(profileSection);

        optics.Children.Add(new TextBlock { Text = Loc.T("L_ManualOptics"), FontSize = 12, Opacity = 0.85 });
        var manualDistortion = OpticsSlider(optics, Loc.T("L_Distortion"), -100, 100,
            cameraRawDraft.OpticsDistortion, value => cameraRawDraft!.OpticsDistortion = value);

        var defringeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        defringeRow.Children.Add(new TextBlock { Text = Loc.T("L_Defringe"), VerticalAlignment = VerticalAlignment.Center });
        var defringeSelector = new ToggleButton { Content = Loc.T("L_DefringeSelector") };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(defringeSelector, Loc.T("A_DefringeSelector"));
        var defringeNote = new TextBlock
        {
            Text = Loc.T("L_DefringeClickHint"), Opacity = 0.7, Visibility = Visibility.Collapsed,
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
        };
        defringeSelector.Checked += (_, _) => { cameraRawSamplesDefringe = true; defringeNote.Visibility = Visibility.Visible; };
        defringeSelector.Unchecked += (_, _) => { cameraRawSamplesDefringe = false; defringeNote.Visibility = Visibility.Collapsed; };
        cameraRawDefringeNote = defringeNote;
        defringeRow.Children.Add(defringeSelector);
        optics.Children.Add(defringeRow);
        optics.Children.Add(defringeNote);

        var purpleAmount = OpticsSlider(optics, Loc.T("L_PurpleAmount"), 0, 100,
            cameraRawDraft.OpticsPurpleAmount, value => cameraRawDraft!.OpticsPurpleAmount = value);
        var purpleHueLow = OpticsSlider(optics, Loc.T("L_PurpleHueLow"), 0, 360,
            cameraRawDraft.OpticsPurpleHueLow, value => cameraRawDraft!.OpticsPurpleHueLow = value);
        var purpleHueHigh = OpticsSlider(optics, Loc.T("L_PurpleHueHigh"), 0, 360,
            cameraRawDraft.OpticsPurpleHueHigh, value => cameraRawDraft!.OpticsPurpleHueHigh = value);
        var greenAmount = OpticsSlider(optics, Loc.T("L_GreenAmount"), 0, 100,
            cameraRawDraft.OpticsGreenAmount, value => cameraRawDraft!.OpticsGreenAmount = value);
        var greenHueLow = OpticsSlider(optics, Loc.T("L_GreenHueLow"), 0, 360,
            cameraRawDraft.OpticsGreenHueLow, value => cameraRawDraft!.OpticsGreenHueLow = value);
        var greenHueHigh = OpticsSlider(optics, Loc.T("L_GreenHueHigh"), 0, 360,
            cameraRawDraft.OpticsGreenHueHigh, value => cameraRawDraft!.OpticsGreenHueHigh = value);
        var lensVignette = OpticsSlider(optics, Loc.T("L_LensVignetting"), -100, 100,
            cameraRawDraft.OpticsVignetteAmount, value => cameraRawDraft!.OpticsVignetteAmount = value);
        var lensMidpoint = OpticsSlider(optics, Loc.T("L_LensMidpoint"), 0, 100,
            cameraRawDraft.OpticsVignetteMidpoint, value => cameraRawDraft!.OpticsVignetteMidpoint = value);

        // What the eyedropper lands on is the band and amount it solved, written through the rows so the panel
        // shows exactly what the grade is using — the same ceiling the Mac's normalization puts on the pick.
        cameraRawPushFringe = (purple, low, high, amount) =>
        {
            (purple ? purpleHueLow : greenHueLow).Value = low;
            (purple ? purpleHueHigh : greenHueHigh).Value = high;
            (purple ? purpleAmount : greenAmount).Value = amount;
        };
        AddBand("L_Optics", CameraRawSettings.GroupOptics, optics);

        // --- Geometry ---
        // The Mac's band (UI/CameraRawGeometryCalibrationControls.swift:4-51), including Upright ▸ Guided and its
        // drawn lines. The rows keep the Mac's plain words, because inside this band each of them says exactly one
        // thing.
        var geometry = new StackPanel { Spacing = 8 };
        // A bend can pinch the four corners onto one point — Scale at −100 does — and the port refuses to warp a
        // quad with no area. The Mac hands that case to Core Image and says nothing; here the band says it.
        bool GeometryRefused() =>
            cameraRawDraft is { AdjustsGeometry: true } draft && cameraRawOriginal is { } capture
            && !DistortWarp.IsUsable(CameraRawGeometry.Corners(draft, capture.Width, capture.Height));
        var geometryRefused = new TextBlock
        {
            Text = Loc.T("L_GeometryRefused"), Opacity = 0.7, FontSize = 11, Visibility = Visibility.Collapsed,
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
        };
        Slider GeometrySlider(string label, double minimum, double maximum, double value, Action<double> set)
        {
            var grid = SliderRow(label, minimum, maximum, value, tick =>
            {
                set(tick);
                PreviewCameraRaw(doc, layerId);
                geometryRefused.Visibility = GeometryRefused() ? Visibility.Visible : Visibility.Collapsed;
            }, markBand: mark);
            geometry.Children.Add(grid);
            return grid.Children.OfType<Slider>().Single();
        }

        // Upright ▸ Guided is the only mode with lines to draw, so the draw button, the caption that explains a
        // drag, and the Clear button all appear and disappear with it. Committing a line picks Guided on the user's
        // behalf, which is why the row also has to be able to move the picker from the guide side of the wiring.
        var uprightPicker = new ComboBox
        {
            Width = 140,
            ItemsSource = CameraRawSettings.GeometryUprightNames,
            SelectedIndex = cameraRawDraft.GeometryUpright == CameraRawSettings.GeometryUprightGuided ? 1 : 0,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(uprightPicker, Loc.T("L_Upright"));
        var drawGuides = new ToggleButton { Content = Loc.T("L_DrawGuides"), Visibility = Visibility.Collapsed };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(drawGuides, Loc.T("A_DrawGuides"));
        var clearGuides = new Button { Content = Loc.T("L_ClearGuides"), Visibility = Visibility.Collapsed };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(clearGuides, Loc.T("A_ClearGuides"));
        var guideHint = new TextBlock
        {
            Text = Loc.T("L_GuidesDragHint"), Opacity = 0.7, FontSize = 11, Visibility = Visibility.Collapsed,
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
        };
        void RefreshGuideUi()
        {
            bool guided = cameraRawDraft!.GeometryUpright == CameraRawSettings.GeometryUprightGuided;
            drawGuides.Visibility = guided ? Visibility.Visible : Visibility.Collapsed;
            clearGuides.Visibility = guided && cameraRawDraft.GeometryGuides.Count > 0
                ? Visibility.Visible : Visibility.Collapsed;
            guideHint.Visibility = cameraRawDrawsGuide ? Visibility.Visible : Visibility.Collapsed;
            if (uprightPicker.SelectedIndex != (guided ? 1 : 0))
            {
                cameraRawGuideWriting = true;
                try { uprightPicker.SelectedIndex = guided ? 1 : 0; }
                finally { cameraRawGuideWriting = false; }
            }
        }
        cameraRawGuideRefresh = RefreshGuideUi;
        drawGuides.Checked += (_, _) => { cameraRawDrawsGuide = true; guideHint.Visibility = Visibility.Visible; };
        drawGuides.Unchecked += (_, _) => { cameraRawDrawsGuide = false; guideHint.Visibility = Visibility.Collapsed; };
        clearGuides.Click += (_, _) =>
        {
            // Only the list is emptied (CameraRawGeometryCalibrationControls.swift:32-34): drawing stays armed, so
            // the user who just cleared a bad line can drag the next one without going back for the button.
            cameraRawDraft!.GeometryGuides.Clear();
            PreviewCameraRaw(doc, layerId);
            RefreshGuideUi();
        };
        uprightPicker.SelectionChanged += (_, _) =>
        {
            if (cameraRawGuideWriting) return;
            cameraRawDraft!.GeometryUpright = uprightPicker.SelectedIndex == 1
                ? CameraRawSettings.GeometryUprightGuided : CameraRawSettings.GeometryUprightOff;
            // Leaving Guided stops the drawing, as on the Mac (CameraRawGeometryCalibrationControls.swift:57) —
            // the lines themselves stay, so coming back to Guided still has them.
            if (uprightPicker.SelectedIndex != 1)
            {
                cameraRawDrawsGuide = false;
                drawGuides.IsChecked = false;
            }
            PreviewCameraRaw(doc, layerId);
            RefreshGuideUi();
        };
        var uprightRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        uprightRow.Children.Add(uprightPicker);
        uprightRow.Children.Add(drawGuides);
        uprightRow.Children.Add(clearGuides);
        geometry.Children.Add(uprightRow);
        geometry.Children.Add(new TextBlock
        {
            Text = Loc.T("L_GuidesModeCaption"), Opacity = 0.7, FontSize = 11,
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
        });
        geometry.Children.Add(guideHint);
        RefreshGuideUi();

        var geometryProjection = new ComboBox
        {
            Width = 140,
            ItemsSource = CameraRawSettings.GeometryProjectionNames,
            SelectedIndex = cameraRawDraft.GeometryProjection == CameraRawSettings.GeometryProjectionRectilinear ? 1 : 0,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(geometryProjection, Loc.T("L_Projection"));
        geometryProjection.SelectionChanged += (_, _) =>
        {
            cameraRawDraft!.GeometryProjection = geometryProjection.SelectedIndex == 1
                ? CameraRawSettings.GeometryProjectionRectilinear : CameraRawSettings.GeometryProjectionPerspective;
            PreviewCameraRaw(doc, layerId);
        };
        geometry.Children.Add(geometryProjection);
        geometry.Children.Add(new TextBlock
        {
            Text = Loc.T("L_GeometryProjectionCaption"), Opacity = 0.7, FontSize = 11,
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
        });
        GeometrySlider(Loc.T("L_GeometryVertical"), -100, 100, cameraRawDraft.GeometryVertical,
            value => cameraRawDraft!.GeometryVertical = value);
        GeometrySlider(Loc.T("L_GeometryHorizontal"), -100, 100, cameraRawDraft.GeometryHorizontal,
            value => cameraRawDraft!.GeometryHorizontal = value);
        GeometrySlider(Loc.T("L_GeometryRotate"), -45, 45, cameraRawDraft.GeometryRotate,
            value => cameraRawDraft!.GeometryRotate = value);
        GeometrySlider(Loc.T("L_GeometryAspect"), -100, 100, cameraRawDraft.GeometryAspect,
            value => cameraRawDraft!.GeometryAspect = value);
        GeometrySlider(Loc.T("L_GeometryScale"), -100, 100, cameraRawDraft.GeometryScale,
            value => cameraRawDraft!.GeometryScale = value);
        GeometrySlider(Loc.T("L_GeometryOffsetX"), -100, 100, cameraRawDraft.GeometryOffsetX,
            value => cameraRawDraft!.GeometryOffsetX = value);
        GeometrySlider(Loc.T("L_GeometryOffsetY"), -100, 100, cameraRawDraft.GeometryOffsetY,
            value => cameraRawDraft!.GeometryOffsetY = value);
        var geometryCrop = new CheckBox { Content = Loc.T("L_ConstrainCrop") };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(geometryCrop, Loc.T("L_ConstrainCrop"));
        geometryCrop.IsChecked = cameraRawDraft.GeometryConstrainCrop;
        geometryCrop.Checked += (_, _) =>
        {
            cameraRawDraft!.GeometryConstrainCrop = true;
            PreviewCameraRaw(doc, layerId);
        };
        geometryCrop.Unchecked += (_, _) =>
        {
            cameraRawDraft!.GeometryConstrainCrop = false;
            PreviewCameraRaw(doc, layerId);
        };
        geometry.Children.Add(geometryCrop);
        geometry.Children.Add(geometryRefused);
        AddBand("L_Geometry", CameraRawSettings.GroupGeometry, geometry);

        // --- Calibration ---
        // The Mac labels these rows "Hue" and "Saturation" under a "Red Primary" heading; here the heading is
        // folded into each row's own text, because a reader announces the row and nothing else, and six sliders
        // called "Hue" or "Saturation" in one panel would be indistinguishable.
        var calibration = new StackPanel { Spacing = 8 };
        var calibrationSummary = new TextBlock { Opacity = 0.7, FontSize = 11, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap };
        var calibrationProcess = new ComboBox
        {
            Width = 120,
            ItemsSource = CameraRawSettings.CalibrationProcessNames,
            SelectedIndex = Math.Clamp(cameraRawDraft.CalibrationProcess - 1, 0, CameraRawSettings.CalibrationProcessNames.Length - 1),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(calibrationProcess, Loc.T("L_Process"));
        var processSummaries = Enumerable.Range(1, CameraRawSettings.CalibrationProcessNames.Length)
            .Select(index => Loc.T($"L_ProcessV{index}Summary")).ToArray();
        calibrationProcess.SelectionChanged += (_, _) =>
        {
            cameraRawDraft!.CalibrationProcess = Math.Clamp(calibrationProcess.SelectedIndex + 1, 1, processSummaries.Length);
            calibrationSummary.Text = processSummaries[cameraRawDraft.CalibrationProcess - 1];
            PreviewCameraRaw(doc, layerId);
        };
        calibrationSummary.Text = processSummaries[cameraRawDraft.CalibrationProcess - 1];
        calibration.Children.Add(calibrationProcess);
        calibration.Children.Add(calibrationSummary);
        calibration.Children.Add(SliderRow(Loc.T("L_ShadowTint"), -100, 100, cameraRawDraft.CalibrationShadowTint, value =>
        {
            cameraRawDraft!.CalibrationShadowTint = value;
            PreviewCameraRaw(doc, layerId);
        }, markBand: mark));
        calibration.Children.Add(SliderRow(Loc.T("L_RedHue"), -100, 100, cameraRawDraft.CalibrationRedHue, value =>
        {
            cameraRawDraft!.CalibrationRedHue = value;
            PreviewCameraRaw(doc, layerId);
        }, markBand: mark));
        calibration.Children.Add(SliderRow(Loc.T("L_RedSaturation"), -100, 100, cameraRawDraft.CalibrationRedSaturation, value =>
        {
            cameraRawDraft!.CalibrationRedSaturation = value;
            PreviewCameraRaw(doc, layerId);
        }, markBand: mark));
        calibration.Children.Add(SliderRow(Loc.T("L_GreenHue"), -100, 100, cameraRawDraft.CalibrationGreenHue, value =>
        {
            cameraRawDraft!.CalibrationGreenHue = value;
            PreviewCameraRaw(doc, layerId);
        }, markBand: mark));
        calibration.Children.Add(SliderRow(Loc.T("L_GreenSaturation"), -100, 100, cameraRawDraft.CalibrationGreenSaturation, value =>
        {
            cameraRawDraft!.CalibrationGreenSaturation = value;
            PreviewCameraRaw(doc, layerId);
        }, markBand: mark));
        calibration.Children.Add(SliderRow(Loc.T("L_BlueHue"), -100, 100, cameraRawDraft.CalibrationBlueHue, value =>
        {
            cameraRawDraft!.CalibrationBlueHue = value;
            PreviewCameraRaw(doc, layerId);
        }, markBand: mark));
        calibration.Children.Add(SliderRow(Loc.T("L_BlueSaturation"), -100, 100, cameraRawDraft.CalibrationBlueSaturation, value =>
        {
            cameraRawDraft!.CalibrationBlueSaturation = value;
            PreviewCameraRaw(doc, layerId);
        }, markBand: mark));
        AddBand("L_Calibration", CameraRawSettings.GroupCalibration, calibration);

        // The sheet's own Preview switch (FilterSheet.swift:143-144), above the two lines the Mac puts between
        // the controls and the buttons: the grade's own failure in orange, and a quiet note while a selection is
        // holding the filter back. They live in the panel rather than the status bar because the panel is what
        // the user is watching while it goes wrong.
        var cameraRawPreview = new CheckBox { Content = Loc.T("L_DitherPreview"), IsChecked = true };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(cameraRawPreview, Loc.T("L_DitherPreview"));
        cameraRawPreview.Checked += (_, _) =>
        {
            cameraRawPreviewOn = true;
            PreviewCameraRaw(doc, layerId);
        };
        cameraRawPreview.Unchecked += (_, _) =>
        {
            cameraRawPreviewOn = false;
            RestoreCameraRawCapture();
        };
        panel.Children.Add(cameraRawPreview);
        cameraRawNotice = new TextBlock
        {
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
            Visibility = Visibility.Collapsed,
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
        };
        panel.Children.Add(cameraRawNotice);
        if (cameraRawClip is not null)
            panel.Children.Add(new TextBlock
            {
                Text = Loc.T("L_FilterLimitedToSelection"), Opacity = 0.7,
                TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
            });

        cameraRawEyesRefresh = RefreshCameraRawEyes;
        RefreshCameraRawEyes();
        // Opening the sheet computes a preview: the Mac grades one as soon as the edit exists
        // (Filters.swift:496-511), and the histogram's ribbons are counted from that publish
        // (CameraRaw.swift:400-409). Nothing else asks for it — while the panel opened on layer selection the
        // refresh path covered for it, and a command-driven door does not.
        PreviewCameraRaw(doc, layerId);
        // Once for the whole window: releasing Option has to take both views off even when no slider moved.
        AttachCameraRawOptionMonitor();
        return panel;
    }

    private void EndCameraRawSession(EditorDocument doc)
    {
        if (cameraRawSessionKey is { } spent) doc.EndTransform(spent);
        cameraRawSessionKey = null;
        cameraRawPreviewQueuedDoc = null; // a closed session must not grade the document it came from
        cameraRawDraft = new CameraRawSettings();
        cameraRawPreviewOn = true;
        // The Option clipping view and the two histogram indicators belong to the edit, not to the window
        // (Filters.swift:336-339 holds them on FilterEdit, and ContentView.swift:250 tears the sheet down with
        // it). Left on the window they armed a preview no panel was showing, and the next session inherited them.
        SetCameraRawClipping(CameraRawEngine.ClippingNone, false);
        cameraRawSharpenMask = false;
        cameraRawShowsShadowClipping = false;
        cameraRawShowsHighlightClipping = false;
        ClearCameraRawWhiteBalance();
        ClearCameraRawOptics();
        ClearCameraRawPointColor();
        ClearCameraRawGuides();
        ClearCameraRawTargetedDrag();
    }

    /// <summary>The Mac's <c>FilterEdit.previewLimit</c> (Filters.swift:359): a live preview is worked out on a
    /// copy no larger than this on its longest side. Camera Raw is a pass over every pixel, so a 4K layer costs
    /// four times the work for pixels the panel is about to throw away — and the copy is made once per panel,
    /// not once per tick.</summary>
    internal const int PreviewLimit = 2048;

    /// <summary>How much to shrink the preview copy by: the exact ratio when the layer is larger than the limit,
    /// or an integer block when it is so large that averaging whole blocks is both exact and cheaper. 1 when the
    /// layer already fits, so nothing is softened. This is also where the Mac's exemptions live
    /// (Filters.swift:430-431) — noise, grain, dither and the two AI kinds preview at full size there, because a
    /// pattern made small and enlarged reads coarser than the picture the user is about to get. That is why the
    /// Dither panel still works at full resolution here as well: the slow 4K Atkinson tick is faithful, not a
    /// port gap.</summary>
    internal static double PreviewScale(int width, int height) =>
        Math.Min(1d, (double)PreviewLimit / Math.Max(width, height));

    private static Surface PreviewCopy(Surface source)
    {
        double scale = PreviewScale(source.Width, source.Height);
        if (scale >= 1d) return source;
        int block = (int)(1 / scale);
        // An exact block (4096 by 2) averages whole squares; anything else is resampled to its preview size,
        // which is the Mac's <c>BrushRaster.draw</c> into the smaller context (Filters.swift:436-440).
        if (block > 1 && source.Width % block == 0 && source.Height % block == 0) return DitherEngine.AverageDown(source, block);
        int width = Math.Max(1, (int)Math.Round(source.Width * scale)), height = Math.Max(1, (int)Math.Round(source.Height * scale));
        var small = new Surface(width, height);
        Resampler.Draw(source, new LayerTransform
        {
            Origin = PointD.Zero,
            Size = new SizeD(width, height),
            Sampling = LayerSampling.HighQuality,
        }, small, 0, 0, width, height);
        return small;
    }

    /// <summary>The graded preview put back at the layer's own pixel size. The published surface has to keep the
    /// layer's dimensions, so the enlargement happens here rather than in the compositor — the one place this
    /// port differs in shape from the Mac, which hands the small image to the GPU with a wider
    /// <c>previewMapping</c>. What lands on screen is the same picture either way.</summary>
    private static Surface RestoreToSize(Surface graded, Surface target)
    {
        if (graded.Width == target.Width && graded.Height == target.Height) return graded;
        var full = new Surface(target.Width, target.Height);
        Resampler.Draw(graded, new LayerTransform
        {
            Origin = PointD.Zero,
            Size = new SizeD(target.Width, target.Height),
            Sampling = LayerSampling.Smooth,
        }, full, 0, 0, target.Width, target.Height);
        return full;
    }

    /// <summary>Puts the panel's orange line up, or takes it down. A preview that fails must say so where the
    /// slider is: until now the only trace went to the log.</summary>
    private void ShowCameraRawNotice(string message)
    {
        if (cameraRawNotice is null) return;
        cameraRawNotice.Text = message;
        cameraRawNotice.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private int cameraRawPreviewTicket; // bumped by every request; results apply only when current
    private bool cameraRawPreviewBusy;
    private EditorDocument? cameraRawPreviewQueuedDoc;
    private Guid cameraRawPreviewQueuedLayer;

    /// <summary>
    /// Grades a preview of the original pixels and swaps it in, off the UI thread: a 12 MP
    /// grade runs hundreds of milliseconds, which used to run inline on every slider tick and
    /// froze the panel. One grade runs at a time; ticks that arrive while it runs coalesce into a
    /// single re-run for the document that asked last. That document has to be remembered rather
    /// than carried in by the running tick: switching layers mid-preview used to drain the queue
    /// back into the document the *old* session belonged to, whose result the session guard then
    /// dropped — so the first slider move of the new session was graded by nobody and never painted.
    /// </summary>
    private void PreviewCameraRaw(EditorDocument doc, Guid layerId)
    {
        if (cameraRawDraft is not { } draft || cameraRawOriginal is not { } original || cameraRawSessionKey is not { } sessionKey)
        {
            return;
        }
        // The eyes follow the draft, not the render: a group has something to hide as soon as it has a number,
        // whether or not this tick is going to wait for the one in flight.
        cameraRawEyesRefresh?.Invoke();
        // A row moved while Preview is off changes the draft and nothing else: the Mac clears the pending
        // job and bumps the revision so the canvas shows the untouched layer (Filters.swift:528-532).
        if (!cameraRawPreviewOn) return;
        if (cameraRawPreviewBusy)
        {
            cameraRawPreviewQueuedDoc = doc;
            cameraRawPreviewQueuedLayer = layerId;
            return;
        }
        cameraRawPreviewBusy = true;
        // The panel keeps editing; the worker grades a frozen copy — of what the eyes allow rendered.
        var settings = draft.Clone().Applying(cameraRawShows);
        var visualizePointColor = CameraRawPointVisualizeIndex(settings);
        // Both Option views are frozen into this tick exactly like the eyes are, and neither is part of the
        // settings — which is what keeps them off the layer: OK builds its own render and asks for no view.
        var clipping = cameraRawClipping;
        var sharpenMaskView = cameraRawSharpenMask;
        var shadowClip = cameraRawShowsShadowClipping;
        var highlightClip = cameraRawShowsHighlightClipping;
        var ticket = ++cameraRawPreviewTicket;
        _ = Task.Run(() =>
        {
            Surface? preview = null;
            CameraRawScope? scope = null;
            Exception? failure = null;
            try
            {
                // The grade runs on the frozen preview copy — a quarter of the pixels at 4K — and is enlarged
                // back into the layer's own grid, because the mask, the thumbnail and the effects grid are all
                // sized to those pixels.
                var source = cameraRawPreviewSource ?? original;
                // The grade the histogram counts is the grade with nothing painted over it — no view and no
                // visualize range either (CameraRaw.swift:400-409, "The histogram is counted before this").
                var plain = source.Clone();
                CameraRawEngine.Apply(plain, settings);
                scope = CameraRawScope.Make(plain);
                if (clipping == CameraRawEngine.ClippingNone && !sharpenMaskView)
                {
                    preview = visualizePointColor < 0 ? plain : source.Clone();
                    if (visualizePointColor >= 0) CameraRawEngine.Apply(preview, settings, visualizePointColor);
                    // The indicators sit on the grade; the count above already happened, so the histogram keeps
                    // showing the picture without them (CameraRaw.swift:411-412).
                    CameraRawEngine.ClipOverlay(preview, shadowClip, highlightClip);
                }
                else
                {
                    // A view replaces the picture rather than covering it, so the frame needs its own render —
                    // two passes in this case, which is what the Mac spends as well. And it outranks the
                    // indicators: with a view up, the overlay never runs (CameraRaw.swift:410).
                    preview = source.Clone();
                    CameraRawEngine.Apply(preview, settings, visualizePointColor, clipping, sharpenMaskView);
                }
                preview = RestoreToSize(preview, original);
                if (cameraRawClip is { } clip) preview = DitherEngine.ClipToCoverage(original, preview, clip);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            DispatcherQueue.TryEnqueue(() =>
            {
                cameraRawPreviewBusy = false;
                try
                {
                    if (cameraRawPreviewQueuedDoc is { } queuedDoc)
                    {
                        var queuedLayer = cameraRawPreviewQueuedLayer;
                        cameraRawPreviewQueuedDoc = null;
                        PreviewCameraRaw(queuedDoc, queuedLayer); // re-grade for the document that asked last
                        return;
                    }
                    if (failure is not null)
                    {
                        App.Log($"camera raw preview failed: {failure}");
                        ShowCameraRawNotice(Loc.F("S_PreviewFailed", failure.Message));
                        return;
                    }
                    ShowCameraRawNotice(string.Empty);
                    if (cameraRawPreviewOn && ticket == cameraRawPreviewTicket && cameraRawSessionKey == sessionKey)
                    {
                        // A tick that made no scope leaves the last one standing: the Mac stores only a non-nil
                        // answer (Filters.swift:564), and an empty layer must not blank the panel's histogram.
                        if (scope is not null) cameraRawScope = scope;
                        cameraRawScopeRefresh?.Invoke();
                        MarkLayerBandDirty(layerId);
                        doc.ReplaceLayerImage(layerId, preview!, sessionKey);
                    }
                }
                catch (Exception published) { App.Log($"camera raw preview publish failed: {published}", LogLevel.Error); }
            });
        });
    }

    /// <summary>
    /// A 255×255 point-curve editor: click adds a point, drag moves it (x stays between its
    /// neighbors), dragging it out of the canvas or a right-click removes it, and the
    /// endpoints cannot be removed — the Curves panel's rules. `load` produces an editable
    /// copy in canvas space; every mutation is handed back through `save`, which persists
    /// and repairs it (sorted, x 2.55 apart, endpoints pinned — the macOS repair()).
    /// </summary>
    private sealed class CurveEditCanvas : Canvas
    {
        private const double Size = 255;
        private readonly Func<List<CurvePoint>> _load;
        private readonly Action<List<CurvePoint>> _save;
        private readonly TextBlock _status = new() { FontSize = 11, Opacity = 0.7 };
        private List<CurvePoint> _points = new();
        private int _dragIndex = -1;
        private int _selected = -1;

        public CurveEditCanvas(Func<List<CurvePoint>> load, Action<List<CurvePoint>> save)
        {
            _load = load;
            _save = save;
            Width = Size;
            Height = Size;
            Background = new SolidColorBrush(Microsoft.UI.Colors.Black);
            _status.Text = Loc.T("CE_Hint");
            PointerPressed += OnPressed;
            PointerMoved += OnMoved;
            PointerReleased += (_, _) => EndDrag();
            PointerCaptureLost += (_, _) => EndDrag();
            Reload();
        }

        public UIElement StatusLabel => _status;

        public void Reload()
        {
            _points = _load();
            _dragIndex = -1;
            Redraw();
        }

        private void EndDrag() => _dragIndex = -1;

        private void Redraw()
        {
            Children.Clear();
            var diagonal = new Microsoft.UI.Xaml.Shapes.Line
            {
                X1 = 0, Y1 = Size, X2 = Size, Y2 = 0,
                Stroke = new SolidColorBrush(Microsoft.UI.Colors.DimGray),
                StrokeDashArray = new DoubleCollection { 4, 3 },
            };
            Children.Add(diagonal);
            var polyline = new Microsoft.UI.Xaml.Shapes.Polyline
            {
                Stroke = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue),
                StrokeThickness = 1.5,
            };
            foreach (var point in _points)
            {
                polyline.Points.Add(new Windows.Foundation.Point(point.X, Size - point.Y));
            }
            Children.Add(polyline);
            for (int i = 0; i < _points.Count; i++)
            {
                var dot = new Ellipse
                {
                    Width = 7,
                    Height = 7,
                    // The Mac paints the *selected* point in the accent colour and keeps it selected after the
                    // release (CameraRawColorControls.swift:86, :166, :173) — the highlight is not a drag flag.
                    Fill = new SolidColorBrush(i == _selected ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.DodgerBlue),
                };
                Canvas.SetLeft(dot, _points[i].X - 3.5);
                Canvas.SetTop(dot, Size - _points[i].Y - 3.5);
                Children.Add(dot);
            }
        }

        /// <summary>The point a reader would ask about: the one the last press took, or -1 for none. It survives
        /// the release and is cleared by the two things the Mac clears it with — changing channel and removing a
        /// point (:28, :210).</summary>
        public int SelectedIndex => _selected;

        /// <summary>The selected point in the 0…255 the readout speaks, or null when nothing is selected.</summary>
        public CurvePoint? SelectedPoint => _selected >= 0 && _selected < _points.Count ? _points[_selected] : null;

        public Action? SelectionChanged;

        public void ClearSelection()
        {
            _selected = -1;
            Redraw();
            SelectionChanged?.Invoke();
        }

        private void Persist(string message)
        {
            _save(Repair(_points));
            if (message.Length > 0) _status.Text = message;
            _points = _load();
            Redraw();
        }

        /// <summary>Sorted, x at least 2.55 apart, endpoints at the corners: the macOS repair().</summary>
        private static List<CurvePoint> Repair(List<CurvePoint> points)
        {
            var sorted = points.Where(point => double.IsFinite(point.X) && double.IsFinite(point.Y))
                .OrderBy(point => point.X).ToList();
            if (sorted.Count < 2) return [new CurvePoint(0, 0), new CurvePoint(255, 255)];
            sorted[0] = new CurvePoint(0, Math.Clamp(sorted[0].Y, 0, 255));
            sorted[^1] = new CurvePoint(255, Math.Clamp(sorted[^1].Y, 0, 255));
            var kept = new List<CurvePoint> { sorted[0] };
            foreach (var point in sorted.Skip(1).Take(sorted.Count - 2))
            {
                double x = Math.Clamp(point.X, 2.55, 252.45);
                if (x <= kept[^1].X + 2.55) continue;
                kept.Add(new CurvePoint(x, Math.Clamp(point.Y, 0, 255)));
            }
            kept.Add(sorted[^1]);
            return kept;
        }

        private void OnPressed(object sender, PointerRoutedEventArgs args)
        {
            var current = args.GetCurrentPoint(this);
            switch (PressPoint(current.Position.X, current.Position.Y, current.Properties.IsRightButtonPressed))
            {
                case PressOutcome.Handled:
                    args.Handled = true;
                    break;
                case PressOutcome.Capture:
                    CapturePointer(args.Pointer);
                    args.Handled = true;
                    break;
            }
        }

        public enum PressOutcome { Stop, Handled, Capture }

        /// <summary>What a press on the graph does: take the nearest point within reach, or add one there and take
        /// that (<c>pointDrag</c>, CameraRawColorControls.swift:162-175), or remove one on a right click where
        /// the Mac uses a double click (:100-103). Split out from the pointer event so a probe can drive the same
        /// decision — the port cannot inject a pointer, and this function is a function of the location.</summary>
        public PressOutcome PressPoint(double x, double y, bool rightButton)
        {
            int hit = -1;
            for (int i = 0; i < _points.Count; i++)
            {
                double dx = _points[i].X - x, dy = (Size - _points[i].Y) - y;
                if (dx * dx + dy * dy <= 64)
                {
                    hit = i;
                    break;
                }
            }
            if (rightButton)
            {
                if (hit > 0 && hit < _points.Count - 1)
                {
                    _points.RemoveAt(hit);
                    // Removing takes the selection away, as the Mac's removePoint does (`selected = nil`, :210).
                    _selected = -1;
                    Persist(Loc.F("CE_PointRemovedLeft", _points.Count));
                    SelectionChanged?.Invoke();
                }
                else
                {
                    _status.Text = Loc.T("CE_EndpointsFixed");
                }
                return PressOutcome.Handled;
            }
            if (hit >= 0)
            {
                _dragIndex = hit;
                _selected = hit;
                SelectionChanged?.Invoke();
            }
            else if (_points.Count < CameraRawCurvePresets.PointsMax)
            {
                double added = Math.Clamp(Math.Round(x), 1, 254);
                double level = Math.Clamp(Size - y, 0, 255);
                int insert = 0;
                while (insert < _points.Count && _points[insert].X < added) insert++;
                if (insert > 0 && insert < _points.Count && (added <= _points[insert - 1].X + 2.55 || added >= _points[insert].X - 2.55))
                {
                    _status.Text = Loc.T("CE_TooClose");
                    return PressOutcome.Stop;
                }
                _points.Insert(insert, new CurvePoint(added, level));
                _dragIndex = insert;
                _selected = insert;
                Persist(Loc.F("CE_PointAdded", added, level));
                SelectionChanged?.Invoke();
            }
            else
            {
                _status.Text = Loc.F("CE_MaxPoints", CameraRawCurvePresets.PointsMax);
            }
            return PressOutcome.Capture;
        }

        private void OnMoved(object sender, PointerRoutedEventArgs args)
        {
            if (_dragIndex < 0) return;
            var position = args.GetCurrentPoint(this).Position;
            MovePoint(position.X, position.Y);
        }

        /// <summary>Moving the point a press took: y anywhere, x only between its neighbours, and dragging out of
        /// the box removes an interior point — the Mac's <c>continueDrag</c> point branch plus its own removal
        /// affordance (CameraRawColorControls.swift:180-186; the port removes on a drag out, where the Mac uses a
        /// double click).</summary>
        public void MovePoint(double x, double y)
        {
            if (x < -8 || y < -8 || x > Size + 8 || y > Size + 8)
            {
                if (_dragIndex > 0 && _dragIndex < _points.Count - 1)
                {
                    _points.RemoveAt(_dragIndex);
                    _selected = -1;
                    Persist(Loc.T("CE_PointRemoved"));
                    SelectionChanged?.Invoke();
                }
                EndDrag();
                Redraw();
                return;
            }
            double moved = Math.Clamp(x, _points[_dragIndex - 1 < 0 ? 0 : _dragIndex - 1].X + 2.55,
                _points[Math.Min(_dragIndex + 1, _points.Count - 1)].X - 2.55);
            double level = Math.Clamp(Size - y, 0, 255);
            bool interior = _dragIndex > 0 && _dragIndex < _points.Count - 1;
            _points[_dragIndex] = new CurvePoint(interior ? moved : _points[_dragIndex].X, level);
            Persist(string.Empty);
            SelectionChanged?.Invoke();
        }
    }

    /// <summary>Camera Raw's parametric graph: the curve the four tonal regions make, and the three dividers
    /// along its bottom edge that say where each region ends. It only draws and forwards a pointer — the numbers
    /// are decided in <see cref="CameraRawTargetedDrag"/> and the drag's owner in
    /// MainWindow.CameraRawParametric.cs, so the picture the panel shows and the pixels the kernel paints come
    /// from the same functions (<see cref="CameraRawEngine.ParametricSamples"/>, and the dividers as stored,
    /// which is the Mac's own split between the graph and the render).</summary>
    private sealed class CurveGraphCanvas : Canvas
    {
        private const double Size = 255;
        private readonly Func<IReadOnlyList<double>> _samples;
        private readonly Func<double[]> _splits;
        private readonly Func<double, double, bool> _press;
        private readonly Action<double, double> _move;
        private readonly Action _release;
        private readonly TextBlock _status = new() { FontSize = 11, Opacity = 0.7 };
        private bool _dragging;

        public CurveGraphCanvas(Func<IReadOnlyList<double>> samples, Func<double[]> splits,
            Func<double, double, bool> press, Action<double, double> move, Action release)
        {
            _samples = samples;
            _splits = splits;
            _press = press;
            _move = move;
            _release = release;
            Width = Size;
            Height = Size;
            Background = new SolidColorBrush(Microsoft.UI.Colors.Black);
            _status.Text = Loc.T("CE_ParametricHint");
            PointerPressed += (_, args) =>
            {
                var position = args.GetCurrentPoint(this).Position;
                if (!_press(position.X, position.Y)) return;
                _dragging = true;
                CapturePointer(args.Pointer);
                args.Handled = true;
            };
            PointerMoved += (_, args) =>
            {
                if (!_dragging) return;
                var position = args.GetCurrentPoint(this).Position;
                _move(position.X, position.Y);
            };
            PointerReleased += (_, _) => EndPress();
            PointerCaptureLost += (_, _) => EndPress();
            Reload();
        }

        public UIElement StatusLabel => _status;

        private void EndPress()
        {
            if (!_dragging) return;
            _dragging = false;
            _release();
        }

        public void Reload()
        {
            Children.Clear();
            var diagonal = new Microsoft.UI.Xaml.Shapes.Line
            {
                X1 = 0, Y1 = Size, X2 = Size, Y2 = 0,
                Stroke = new SolidColorBrush(Microsoft.UI.Colors.DimGray),
                StrokeDashArray = new DoubleCollection { 4, 3 },
            };
            Children.Add(diagonal);
            var samples = _samples();
            if (samples.Count > 1)
            {
                var polyline = new Microsoft.UI.Xaml.Shapes.Polyline
                {
                    Stroke = new SolidColorBrush(Microsoft.UI.Colors.White),
                    StrokeThickness = 1.5,
                };
                for (int index = 0; index < samples.Count; index++)
                {
                    polyline.Points.Add(new Windows.Foundation.Point(
                        index / (double)(samples.Count - 1) * Size, (1 - samples[index]) * Size));
                }
                Children.Add(polyline);
            }
            // The dividers are ticks against the bottom edge, three units wide and eight points of the Mac's
            // 150-point graph tall (CameraRawColorControls.swift:75-81), which on this box is CurveGraphTick.
            foreach (var split in _splits())
            {
                double x = Math.Clamp(split / 100, 0, 1) * Size;
                Children.Add(new Microsoft.UI.Xaml.Shapes.Line
                {
                    X1 = x, Y1 = Size - CurveGraphTick, X2 = x, Y2 = Size,
                    Stroke = new SolidColorBrush(Microsoft.UI.Colors.White),
                    StrokeThickness = 3,
                });
            }
        }
    }

    private object? adjustmentDragKey;
    private readonly LevelsHistogramCache levelsHistogramCache = new();
    private CancellationTokenSource? levelsHistogramCancellation;
    private Action? refreshLevelsHistogram;
    private Action? deactivateLevelsHistogram;
    private sealed record LevelsPanelView(LayerAdjustment Settings, UIElement Panel, Action Refresh, Action Deactivate);
    private readonly Dictionary<Guid, LevelsPanelView> levelsPanelViews = new();
    private readonly LinkedList<Guid> levelsPanelOrder = new();
    private EditorDocument? adjustmentPanelDocument;
    private Guid? adjustmentPanelLayer;
    private LayerAdjustment? adjustmentPanelSettings;
    private bool histogramRefreshQueued;

    private void CancelLevelsHistogram()
    {
        levelsHistogramCancellation?.Cancel();
        levelsHistogramCancellation?.Dispose();
        levelsHistogramCancellation = null;
    }

    private void QueueLevelsHistogramRefresh()
    {
        if (refreshLevelsHistogram is null || histogramRefreshQueued) return;
        histogramRefreshQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        { histogramRefreshQueued = false; refreshLevelsHistogram?.Invoke(); });
    }

    /// <summary>
    /// Builds the scalar sliders for the selected layer's adjustment. Kinds whose settings
    /// live in nested blocks (Levels, Curves, Gradient Map, Grain, Black & White, Color
    /// Balance) say so instead of offering half an editor.
    /// </summary>
    private void RebuildAdjustmentPanel(ProjectLayerRecord? selected)
    {
        if (ReferenceEquals(adjustmentPanelDocument, document) && adjustmentPanelLayer == selected?.Id
            && ReferenceEquals(adjustmentPanelSettings, selected?.Adjustment))
        {
            refreshLevelsHistogram?.Invoke();
            return;
        }
        deactivateLevelsHistogram?.Invoke();
        CancelLevelsHistogram();
        refreshLevelsHistogram = null;
        deactivateLevelsHistogram = null;
        if (!ReferenceEquals(adjustmentPanelDocument, document))
        { levelsPanelViews.Clear(); levelsPanelOrder.Clear(); }
        if (document is { } owner)
        {
            var ids = owner.Snapshot.Manifest.Layers.Select(layer => layer.Id).ToHashSet();
            foreach (var removed in levelsPanelViews.Keys.Where(id => !ids.Contains(id)).ToArray())
            { levelsPanelViews.Remove(removed); levelsPanelOrder.Remove(removed); }
        }
        adjustmentPanelDocument = document;
        adjustmentPanelLayer = selected?.Id;
        adjustmentPanelSettings = selected?.Adjustment;
        AdjustmentPanel.Children.Clear();
        var adjustment = selected?.Adjustment;
        if (adjustment is null || selected is null)
        {
            AdjustmentPanel.Visibility = Visibility.Collapsed;
            return;
        }
        AdjustmentPanel.Visibility = Visibility.Visible;
        var title = new TextBlock
        {
            Text = Loc.F("S_AdjustmentSuffix", Loc.Name("Adjustment", Enum.GetName(adjustment.Kind)!)),
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };
        AdjustmentPanel.Children.Add(title);

        EditorDocument doc = document!; // RefreshLayerPanel returned early when there is no document
        Guid layerId = selected.Id;
        Action mark = () => MarkLayerBandDirty(layerId);

        switch (adjustment.Kind)
        {
            case AdjustmentKind.Levels:
                if (levelsPanelViews.TryGetValue(layerId, out var cached) && ReferenceEquals(cached.Settings, adjustment))
                {
                    AdjustmentPanel.Children.Add(cached.Panel);
                    refreshLevelsHistogram = cached.Refresh; deactivateLevelsHistogram = cached.Deactivate;
                    cached.Refresh();
                    levelsPanelOrder.Remove(layerId); levelsPanelOrder.AddFirst(layerId);
                }
                else
                {
                    var panel = BuildLevelsPanel(doc, layerId, adjustment);
                    AdjustmentPanel.Children.Add(panel);
                    levelsPanelViews[layerId] = new(adjustment, panel, refreshLevelsHistogram!, deactivateLevelsHistogram!);
                    levelsPanelOrder.Remove(layerId); levelsPanelOrder.AddFirst(layerId);
                    while (levelsPanelOrder.Count > 4)
                    { levelsPanelViews.Remove(levelsPanelOrder.Last!.Value); levelsPanelOrder.RemoveLast(); }
                }
                break;
            case AdjustmentKind.Curves:
                AdjustmentPanel.Children.Add(BuildCurvesPanel(doc, layerId, adjustment));
                break;
            case AdjustmentKind.HueSaturation:
                AdjustmentPanel.Children.Add(HueSaturationRows(doc, layerId, adjustment));
                break;
            case AdjustmentKind.Exposure:
                AdjustmentPanel.Children.Add(ExposureRows(doc, layerId, adjustment));
                break;
            case AdjustmentKind.GaussianBlur:
                AdjustmentPanel.Children.Add(SliderRow(Loc.T("L_Radius"), 0.1, 250, adjustment.GaussianRadius, value =>
                    doc.MutateAdjustment(layerId, a => a.BlurRadius = value, adjustmentDragKey), markBand: mark));
                break;
            case AdjustmentKind.MotionBlur:
                AdjustmentPanel.Children.Add(SliderRow(Loc.T("L_Angle"), -90, 90, adjustment.ResolvedMotionAngle, value =>
                    doc.MutateAdjustment(layerId, a => a.MotionAngle = value, adjustmentDragKey), markBand: mark));
                AdjustmentPanel.Children.Add(SliderRow(Loc.T("L_Distance"), 1, 2000, adjustment.ResolvedMotionDistance, value =>
                    doc.MutateAdjustment(layerId, a => a.MotionDistance = value, adjustmentDragKey), markBand: mark));
                break;
            case AdjustmentKind.AddNoise:
                AdjustmentPanel.Children.Add(SliderRow(Loc.T("L_Amount"), 0.1, 400, adjustment.ResolvedNoiseAmount, value =>
                    doc.MutateAdjustment(layerId, a => a.NoiseAmount = value, adjustmentDragKey), markBand: mark));
                AdjustmentPanel.Children.Add(CheckRow(Loc.T("L_Gaussian"), adjustment.NoiseGaussian == true, value =>
                    doc.MutateAdjustment(layerId, a => a.NoiseGaussian = value)));
                AdjustmentPanel.Children.Add(CheckRow(Loc.T("L_Monochromatic"), adjustment.NoiseMonochromatic == true, value =>
                    doc.MutateAdjustment(layerId, a => a.NoiseMonochromatic = value)));
                break;
            case AdjustmentKind.GradientMap:
                AdjustmentPanel.Children.Add(GradientMapRows(doc, layerId, adjustment));
                break;
            case AdjustmentKind.BlackWhite:
                AdjustmentPanel.Children.Add(BlackWhiteRows(doc, layerId, adjustment));
                break;
            case AdjustmentKind.ColorBalance:
                AdjustmentPanel.Children.Add(ColorBalanceRows(doc, layerId, adjustment));
                break;
            case AdjustmentKind.Grain:
                AdjustmentPanel.Children.Add(GrainRows(doc, layerId, adjustment));
                break;
            default:
                AdjustmentPanel.Children.Add(new TextBlock
                {
                    Text = Loc.T("M_AdjustmentNeedsProject"),
                    FontSize = 12,
                    Opacity = 0.7,
                    TextWrapping = TextWrapping.Wrap,
                });
                break;
        }
    }

    /// <summary>
    /// C06: Gradient Map. Core already ramps between two colors (AdjustmentEngine's gradient map),
    /// so the panel is the two endpoints plus the reversed switch — the same three things the macOS
    /// sheet exposes (UI/FilterSheet.swift:28-30).
    /// </summary>
    private StackPanel GradientMapRows(EditorDocument doc, Guid layerId, LayerAdjustment adjustment)
    {
        Action mark = () => MarkLayerBandDirty(layerId);
        var panel = new StackPanel { Spacing = 8 };
        var settings = adjustment.GradientMapSettings ?? new GradientMapSettings();
        panel.Children.Add(ColorChipRow(Loc.T("L_Shadows"), ToColor(settings.Shadows), color =>
            doc.MutateAdjustment(layerId, a =>
            {
                var next = a.GradientMapSettings ?? new GradientMapSettings();
                next.Shadows = FromColor(color);
                a.GradientMapSettings = next;
            }, adjustmentDragKey), mark));
        panel.Children.Add(ColorChipRow(Loc.T("L_Highlights"), ToColor(settings.Highlights), color =>
            doc.MutateAdjustment(layerId, a =>
            {
                var next = a.GradientMapSettings ?? new GradientMapSettings();
                next.Highlights = FromColor(color);
                a.GradientMapSettings = next;
            }, adjustmentDragKey), mark));
        panel.Children.Add(CheckRow(Loc.T("L_Reversed"), settings.Reversed, value =>
            doc.MutateAdjustment(layerId, a =>
            {
                var next = a.GradientMapSettings ?? new GradientMapSettings();
                next.Reversed = value;
                a.GradientMapSettings = next;
            }, adjustmentDragKey)));
        return panel;
    }

    /// <summary>
    /// C06: Black &amp; White. Six color-family weights on the Core's own -200…300 range, plus the
    /// tint switch with its hue and saturation (UI/FilterSheet.swift:31-47).
    /// </summary>
    private StackPanel BlackWhiteRows(EditorDocument doc, Guid layerId, LayerAdjustment adjustment)
    {
        Action mark = () => MarkLayerBandDirty(layerId);
        var panel = new StackPanel { Spacing = 8 };
        var settings = adjustment.BlackWhiteSettings ?? new BlackWhiteSettings();
        var families = new (string Raw, double Value, Action<BlackWhiteSettings, double> Set)[]
        {
            ("Reds", settings.Reds, (next, value) => next.Reds = value),
            ("Yellows", settings.Yellows, (next, value) => next.Yellows = value),
            ("Greens", settings.Greens, (next, value) => next.Greens = value),
            ("Cyans", settings.Cyans, (next, value) => next.Cyans = value),
            ("Blues", settings.Blues, (next, value) => next.Blues = value),
            ("Magentas", settings.Magentas, (next, value) => next.Magentas = value),
        };
        foreach (var (raw, value, assign) in families)
        {
            panel.Children.Add(SliderRow(Loc.Name("Range", raw), BlackWhiteSettings.Min, BlackWhiteSettings.Max, value, tick =>
                doc.MutateAdjustment(layerId, a =>
                {
                    var next = a.BlackWhiteSettings ?? new BlackWhiteSettings();
                    assign(next, tick);
                    a.BlackWhiteSettings = next;
                }, adjustmentDragKey), markBand: mark));
        }
        panel.Children.Add(CheckRow(Loc.T("L_Tint"), settings.Tint, value =>
            doc.MutateAdjustment(layerId, a =>
            {
                var next = a.BlackWhiteSettings ?? new BlackWhiteSettings();
                next.Tint = value;
                a.BlackWhiteSettings = next;
            }, adjustmentDragKey)));
        panel.Children.Add(SliderRow(Loc.T("L_TintHue"), 0, 360, settings.TintHue, tick =>
            doc.MutateAdjustment(layerId, a =>
            {
                var next = a.BlackWhiteSettings ?? new BlackWhiteSettings();
                next.TintHue = tick;
                a.BlackWhiteSettings = next;
            }, adjustmentDragKey), markBand: mark));
        panel.Children.Add(SliderRow(Loc.T("L_TintSaturation"), 0, 100, settings.TintSaturation, tick =>
            doc.MutateAdjustment(layerId, a =>
            {
                var next = a.BlackWhiteSettings ?? new BlackWhiteSettings();
                next.TintSaturation = tick;
                a.BlackWhiteSettings = next;
            }, adjustmentDragKey), markBand: mark));
        return panel;
    }

    /// <summary>
    /// C06: Color Balance. Nine sliders — three tonal ranges by three opposing pairs — plus preserve
    /// luminosity (UI/FilterSheet.swift:49-62). The macOS sheet puts the ranges in a picker; showing
    /// all nine at once keeps every parameter reachable without a second click.
    /// </summary>
    private StackPanel ColorBalanceRows(EditorDocument doc, Guid layerId, LayerAdjustment adjustment)
    {
        Action mark = () => MarkLayerBandDirty(layerId);
        var panel = new StackPanel { Spacing = 8 };
        var settings = adjustment.ColorBalanceSettings ?? new ColorBalanceSettings();
        var rows = new (string RangeKey, string PairKey, double Value, Action<ColorBalanceSettings, double> Set)[]
        {
            ("L_Shadows", "L_PairCyanRed", settings.ShadowCyanRed, (next, value) => next.ShadowCyanRed = value),
            ("L_Shadows", "L_PairMagentaGreen", settings.ShadowMagentaGreen, (next, value) => next.ShadowMagentaGreen = value),
            ("L_Shadows", "L_PairYellowBlue", settings.ShadowYellowBlue, (next, value) => next.ShadowYellowBlue = value),
            ("L_Midtones", "L_PairCyanRed", settings.MidCyanRed, (next, value) => next.MidCyanRed = value),
            ("L_Midtones", "L_PairMagentaGreen", settings.MidMagentaGreen, (next, value) => next.MidMagentaGreen = value),
            ("L_Midtones", "L_PairYellowBlue", settings.MidYellowBlue, (next, value) => next.MidYellowBlue = value),
            ("L_Highlights", "L_PairCyanRed", settings.HighlightCyanRed, (next, value) => next.HighlightCyanRed = value),
            ("L_Highlights", "L_PairMagentaGreen", settings.HighlightMagentaGreen, (next, value) => next.HighlightMagentaGreen = value),
            ("L_Highlights", "L_PairYellowBlue", settings.HighlightYellowBlue, (next, value) => next.HighlightYellowBlue = value),
        };
        foreach (var (rangeKey, pairKey, value, assign) in rows)
        {
            string label = Loc.F("L_BalanceRow", Loc.T(rangeKey), Loc.T(pairKey));
            panel.Children.Add(SliderRow(label, ColorBalanceSettings.Min, ColorBalanceSettings.Max, value, tick =>
                doc.MutateAdjustment(layerId, a =>
                {
                    var next = a.ColorBalanceSettings ?? new ColorBalanceSettings();
                    assign(next, tick);
                    a.ColorBalanceSettings = next;
                }, adjustmentDragKey), markBand: mark));
        }
        panel.Children.Add(CheckRow(Loc.T("L_PreserveLuminosity"), settings.PreserveLuminosity, value =>
            doc.MutateAdjustment(layerId, a =>
            {
                var next = a.ColorBalanceSettings ?? new ColorBalanceSettings();
                next.PreserveLuminosity = value;
                a.ColorBalanceSettings = next;
            }, adjustmentDragKey)));
        return panel;
    }

    /// <summary>
    /// C06: Grain. Amount, size and roughness as sliders, the seed as a number box so a specific
    /// render can be asked for again (UI/FilterSheet.swift:63-66; Core fixes the seed at add time,
    /// so this is the first place a user can change it).
    /// </summary>
    private StackPanel GrainRows(EditorDocument doc, Guid layerId, LayerAdjustment adjustment)
    {
        Action mark = () => MarkLayerBandDirty(layerId);
        var panel = new StackPanel { Spacing = 8 };
        var settings = adjustment.GrainSettings ?? new GrainSettings();
        panel.Children.Add(SliderRow(Loc.T("L_Amount"), GrainSettings.AmountMin, GrainSettings.AmountMax, settings.Amount, tick =>
            doc.MutateAdjustment(layerId, a =>
            {
                var next = a.GrainSettings ?? new GrainSettings();
                next.Amount = tick;
                a.GrainSettings = next;
            }, adjustmentDragKey), markBand: mark));
        panel.Children.Add(SliderRow(Loc.T("L_GrainSize"), GrainSettings.SizeMin, GrainSettings.SizeMax, settings.Size, tick =>
            doc.MutateAdjustment(layerId, a =>
            {
                var next = a.GrainSettings ?? new GrainSettings();
                next.Size = tick;
                a.GrainSettings = next;
            }, adjustmentDragKey), markBand: mark));
        panel.Children.Add(SliderRow(Loc.T("L_Roughness"), GrainSettings.RoughnessMin, GrainSettings.RoughnessMax, settings.Roughness, tick =>
            doc.MutateAdjustment(layerId, a =>
            {
                var next = a.GrainSettings ?? new GrainSettings();
                next.Roughness = tick;
                a.GrainSettings = next;
            }, adjustmentDragKey), markBand: mark));
        panel.Children.Add(NumberRow(Loc.T("L_Seed"), settings.Seed, value =>
            doc.MutateAdjustment(layerId, a =>
            {
                var next = a.GrainSettings ?? new GrainSettings();
                next.Seed = (uint)Math.Clamp(value, 0, uint.MaxValue);
                a.GrainSettings = next;
            }, adjustmentDragKey)));
        return panel;
    }

    /// <summary>A labeled number box, the Grain seed's entry.</summary>
    private Grid NumberRow(string label, double value, Action<double> apply)
    {
        var box = new NumberBox
        {
            Value = value,
            MinWidth = 120,
            SmallChange = 1,
            LargeChange = 10,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        box.ValueChanged += (_, args) =>
        {
            if (double.IsFinite(args.NewValue) && args.NewValue != args.OldValue) apply(args.NewValue);
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, label);
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        Grid.SetColumn(name, 0);
        Grid.SetColumn(box, 1);
        grid.Children.Add(name);
        grid.Children.Add(box);
        return grid;
    }

    /// <summary>A labeled chip that opens the attached ColorPicker flyout, like the effects dialog.</summary>
    private Grid ColorChipRow(string label, Windows.UI.Color initial, Action<Windows.UI.Color> apply, Action? markBand)
    {
        var fill = new Rectangle
        {
            Width = 36,
            Height = 18,
            Fill = new SolidColorBrush(initial),
            Stroke = new SolidColorBrush(Microsoft.UI.Colors.Gray),
        };
        var chip = new Button { Content = fill, Padding = new Thickness(2) };
        // Same as the slider rows: the word is drawn beside the control, but a reader only gets what the
        // control itself reports, and a chip whose content is a rectangle reports nothing.
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(chip, label);
        var picker = new ColorPicker { Color = initial, IsAlphaEnabled = false, IsMoreButtonVisible = false };
        picker.ColorChanged += (_, args) =>
        {
            fill.Fill = new SolidColorBrush(args.NewColor);
            markBand?.Invoke();
            apply(args.NewColor);
        };
        Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase.SetAttachedFlyout(chip, new Flyout { Content = picker });
        chip.Click += (source, _) => Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase.ShowAttachedFlyout((FrameworkElement)source);
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        Grid.SetColumn(name, 0);
        Grid.SetColumn(chip, 1);
        grid.Children.Add(name);
        grid.Children.Add(chip);
        return grid;
    }

    private static Windows.UI.Color ToColor(AdjustmentColor color) => Windows.UI.Color.FromArgb(255,
        (byte)Math.Round(Math.Clamp(color.Red, 0, 1) * 255),
        (byte)Math.Round(Math.Clamp(color.Green, 0, 1) * 255),
        (byte)Math.Round(Math.Clamp(color.Blue, 0, 1) * 255));

    private static AdjustmentColor FromColor(Windows.UI.Color color) => new(color.R / 255.0, color.G / 255.0, color.B / 255.0);

    /// <summary>
    /// The Levels panel, laid out like the macOS one: a histogram of the content the
    /// adjustment acts on (RenderUntil), a channel picker, and the five range sliders.
    /// </summary>
    private UIElement BuildLevelsPanel(EditorDocument doc, Guid layerId, LayerAdjustment adjustment)
    {

        Action mark = () => MarkLayerBandDirty(layerId);
        var panel = new StackPanel { Spacing = 8 };
        var channels = new[] { Loc.T("Chan_RGB"), Loc.T("Chan_Red"), Loc.T("Chan_Green"), Loc.T("Chan_Blue") };
        var channelCombo = new ComboBox { Width = 120, ItemsSource = channels, SelectedIndex = 0 };
        panel.Children.Add(channelCombo);

        var histogram = new Canvas { Width = 256, Height = 72, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        panel.Children.Add(new Viewbox { Child = histogram, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly });
        var histogramStatus = new TextBlock { Text = Loc.T("L_HistogramLoading"), FontSize = 11, Opacity = .7, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(histogramStatus);
        LevelsHistogramInput? requestedInput = null;
        LevelsHistogramResult? histogramResult = null;
        double[][]? histogramBins = null;
        var autoButtons = new List<Button>();
        Action? refreshHistogram = null;

        var sliders = new StackPanel { Spacing = 6 };
        panel.Children.Add(sliders);

        void RebuildSliders()
        {
            sliders.Children.Clear();
            var levels = doc.Snapshot.Manifest.Layers.FirstOrDefault(candidate => candidate.Id == layerId)?.Adjustment?.Levels;
            if (levels is null) return;
            int channel = Math.Max(0, channelCombo.SelectedIndex);
            var range = levels.Ranges[channel];
            sliders.Children.Add(SliderRow(Loc.T("L_Black"), 0, 254, range.Black, value =>
                doc.MutateLevels(layerId, l => { l.Ranges[channel].Black = Math.Round(value); l.Ranges[channel] = l.Ranges[channel].Normalized; }, adjustmentDragKey), markBand: mark));
            sliders.Children.Add(SliderRow(Loc.T("L_Gamma"), 0.1, 9.99, range.Gamma, value =>
                doc.MutateLevels(layerId, l => { l.Ranges[channel].Gamma = value; l.Ranges[channel] = l.Ranges[channel].Normalized; }, adjustmentDragKey), markBand: mark));
            sliders.Children.Add(SliderRow(Loc.T("L_White"), 1, 255, range.White, value =>
                doc.MutateLevels(layerId, l => { l.Ranges[channel].White = Math.Round(value); l.Ranges[channel] = l.Ranges[channel].Normalized; }, adjustmentDragKey), markBand: mark));
            sliders.Children.Add(SliderRow(Loc.T("L_OutBlack"), 0, 255, range.OutputBlack, value =>
                doc.MutateLevels(layerId, l => { l.Ranges[channel].OutputBlack = Math.Round(value); l.Ranges[channel] = l.Ranges[channel].Normalized; }, adjustmentDragKey), markBand: mark));
            sliders.Children.Add(SliderRow(Loc.T("L_OutWhite"), 0, 255, range.OutputWhite, value =>
                doc.MutateLevels(layerId, l => { l.Ranges[channel].OutputWhite = Math.Round(value); l.Ranges[channel] = l.Ranges[channel].Normalized; }, adjustmentDragKey), markBand: mark));
        }
        var autoRow = new StackPanel { Spacing = 4 };
        autoRow.Children.Add(new TextBlock { Text = Loc.T("L_AutoLevels"), VerticalAlignment = VerticalAlignment.Center });
        foreach ((LevelsAutoMode mode, string key) in new[] { (LevelsAutoMode.Contrast, "L_AutoContrast"), (LevelsAutoMode.Color, "L_AutoColor"), (LevelsAutoMode.Neutral, "L_AutoNeutral") })
        {
            var current = mode;
            var button = new Button { Content = Loc.T(key), IsEnabled = false };
            button.Click += (_, _) =>
            {
                if (histogramResult is null) return;
                mark();
                if (!doc.AutoLevels(layerId, current, histogramResult)) { RefreshHistogram(); return; }
                RebuildSliders();
            };
            autoRow.Children.Add(button);
            autoButtons.Add(button);
        }
        panel.Children.Add(autoRow);


        void DrawHistogram()
        {
            histogram.Children.Clear();
            if (histogramBins is null) return;
            int channel = Math.Clamp(channelCombo.SelectedIndex, 0, 3);
            var color = channel switch { 1 => Microsoft.UI.Colors.Red, 2 => Microsoft.UI.Colors.LimeGreen,
                3 => Microsoft.UI.Colors.DodgerBlue, _ => Microsoft.UI.Colors.Gray };
            DrawHistogramRow(histogram, histogramBins[channel], LevelsHistogram.DisplayScale(histogramBins[channel]), color);
        }

        void RefreshHistogram()
        {
            if (!ReferenceEquals(document, doc) || adjustmentPanelLayer != layerId) return;
            LevelsHistogramInput input;
            try { input = LevelsHistogramInput.Capture(doc.Snapshot, layerId); }
            catch (ArgumentException)
            {
                CancelLevelsHistogram(); requestedInput = null; histogramResult = null; histogramBins = null;
                histogram.Children.Clear();
                foreach (var button in autoButtons) button.IsEnabled = false;
                return;
            }
            if (histogramResult?.Matches(input) == true || requestedInput?.HasSameInput(input) == true) return;
            requestedInput = input;
            CancelLevelsHistogram();
            var cancellation = levelsHistogramCancellation = new CancellationTokenSource();
            var token = cancellation.Token;
            histogramResult = null; histogramBins = null; histogram.Children.Clear();
            histogramStatus.Text = Loc.T("L_HistogramLoading");
            foreach (var button in autoButtons) button.IsEnabled = false;
            _ = LoadHistogramAsync(input, token);
        }

        async Task LoadHistogramAsync(LevelsHistogramInput input, CancellationToken token)
        {
            try
            {
                var result = await levelsHistogramCache.GetAsync(input, token).ConfigureAwait(false);
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (token.IsCancellationRequested || !ReferenceEquals(document, doc)
                        || !ReferenceEquals(refreshLevelsHistogram, refreshHistogram)
                        || !ReferenceEquals(requestedInput, input)) return;
                    if (!result.Matches(doc.Snapshot, layerId))
                    { requestedInput = null; RefreshHistogram(); return; }
                    histogramResult = result; histogramBins = result.CopyBins(); requestedInput = null;
                    histogramStatus.Text = Loc.T("L_HistogramInput");
                    foreach (var button in autoButtons) button.IsEnabled = true;
                    DrawHistogram();
                });
            }
            catch (OperationCanceledException)
            {
                // Pixel revisions may revoke work before the coalesced UI refresh runs.
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (token.IsCancellationRequested || !ReferenceEquals(document, doc)
                        || !ReferenceEquals(requestedInput, input)) return;
                    requestedInput = null; RefreshHistogram();
                });
            }
            catch (Exception ex)
            {
                App.Log($"Levels histogram failed: {ex}");
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (token.IsCancellationRequested || !ReferenceEquals(requestedInput, input)) return;
                    requestedInput = null;
                    histogramStatus.Text = Loc.T("L_HistogramFailed");
                });
            }
        }

        channelCombo.SelectionChanged += (_, _) =>
        {
            RebuildSliders();
            DrawHistogram();
        };

        RebuildSliders();
        refreshHistogram = RefreshHistogram;
        refreshLevelsHistogram = refreshHistogram;
        // A canceled pending request must restart when this cached view becomes active.
        // A completed result may be reused only after RefreshHistogram checks its input.
        deactivateLevelsHistogram = () => { if (histogramResult is null) requestedInput = null; };
        RefreshHistogram();
        return panel;
    }

    private static void DrawHistogramRow(Canvas canvas, double[] bins, double peak, Windows.UI.Color color)
    {
        if (peak <= 0) return;
        var bars = new GeometryGroup();
        for (int i = 0; i < 256; i++)
        {
            double value = bins[i];
            if (value <= 0) continue;
            double barHeight = Math.Clamp(value / peak * canvas.Height, 1, canvas.Height);
            bars.Children.Add(new RectangleGeometry { Rect = new Rect(i, canvas.Height - barHeight, 1, barHeight) });
        }
        canvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = bars, Fill = new SolidColorBrush(color),
            Opacity = .7, IsHitTestVisible = false });
    }

    /// <summary>
    /// The Curves panel: a channel picker and a 255×255 edit canvas. Click adds a point,
    /// drag moves it (x stays between its neighbors), dragging a point out of the canvas or
    /// a right-click removes it; the endpoints cannot be removed, as on the Mac.
    /// </summary>
    private UIElement BuildCurvesPanel(EditorDocument doc, Guid layerId, LayerAdjustment adjustment)
    {

        Action mark = () => MarkLayerBandDirty(layerId);
        var panel = new StackPanel { Spacing = 8 };
        var channels = new[] { Loc.T("Chan_RGB"), Loc.T("Chan_Red"), Loc.T("Chan_Green"), Loc.T("Chan_Blue") };
        var channelCombo = new ComboBox { Width = 120, ItemsSource = channels, SelectedIndex = 0 };
        panel.Children.Add(channelCombo);

        const double size = 255;
        var canvas = new Canvas
        {
            Width = size,
            Height = size,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Black),
        };
        panel.Children.Add(new Viewbox { Child = canvas, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly });
        var status = new TextBlock { FontSize = 11, Opacity = 0.7, Text = Loc.T("CE_Hint"), TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(status);

        int dragIndex = -1;
        bool removed = false;

        List<CurvePoint> Points()
        {
            var curves = doc.Snapshot.Manifest.Layers.FirstOrDefault(candidate => candidate.Id == layerId)?.Adjustment?.Curves;
            if (curves is null) return new List<CurvePoint>();
            return curves.Channels[Math.Clamp(channelCombo.SelectedIndex, 0, 3)];
        }

        void Redraw()
        {
            canvas.Children.Clear();
            // The diagonal reference.
            var diagonal = new Microsoft.UI.Xaml.Shapes.Line
            {
                X1 = 0, Y1 = size, X2 = size, Y2 = 0,
                Stroke = new SolidColorBrush(Microsoft.UI.Colors.DimGray),
                StrokeDashArray = new DoubleCollection { 4, 3 },
            };
            canvas.Children.Add(diagonal);
            // 0/255 ticks at the corners (output values run bottom-up).
            foreach (var (label, x, y) in new[] { ("0", 2.0, size - 14.0), ("255", size - 26.0, 2.0) })
            {
                var tick = new TextBlock { Text = label, FontSize = 9, Foreground = new SolidColorBrush(Microsoft.UI.Colors.DimGray) };
                Canvas.SetLeft(tick, x);
                Canvas.SetTop(tick, y);
                canvas.Children.Add(tick);
            }
            var points = Points();
            if (points.Count < 2) return;
            var polyline = new Microsoft.UI.Xaml.Shapes.Polyline
            {
                Stroke = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue),
                StrokeThickness = 1.5,
            };
            foreach (var point in points)
            {
                polyline.Points.Add(new Windows.Foundation.Point(point.X, size - point.Y));
            }
            canvas.Children.Add(polyline);
            for (int i = 0; i < points.Count; i++)
            {
                var dot = new Ellipse
                {
                    Width = 7,
                    Height = 7,
                    Fill = new SolidColorBrush(i == dragIndex ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.DodgerBlue),
                };
                Canvas.SetLeft(dot, points[i].X - 3.5);
                Canvas.SetTop(dot, size - points[i].Y - 3.5);
                canvas.Children.Add(dot);
            }
        }

        void Commit(string? message)
        {
            Redraw();
            status.Text = message ?? status.Text;
        }

        canvas.PointerPressed += (_, args) =>
        {
            var position = args.GetCurrentPoint(canvas).Position;
            var points = Points();
            removed = false;
            int hit = -1;
            for (int i = 0; i < points.Count; i++)
            {
                double dx = points[i].X - position.X, dy = (size - points[i].Y) - position.Y;
                if (dx * dx + dy * dy <= 64) { hit = i; break; }
            }
            if (args.GetCurrentPoint(canvas).Properties.IsRightButtonPressed)
            {
                if (hit > 0 && hit < points.Count - 1)
                {
                    var key = new object();
                    mark();
                    doc.MutateCurves(layerId, curves => curves.Channels[Math.Clamp(channelCombo.SelectedIndex, 0, 3)].RemoveAt(hit), key);
                    doc.EndTransform(key);
                    dragIndex = -1;
                    Commit(Loc.F("CE_PointRemovedLeft", points.Count - 1));
                }
                else
                {
                    Commit(Loc.T("CE_EndpointsFixed"));
                }
                args.Handled = true;
                return;
            }
            if (hit >= 0)
            {
                dragIndex = hit;
            }
            else if (points.Count < 32)
            {
                // Add a point at the click, keeping x strictly increasing.
                double x = Math.Clamp(Math.Round(position.X), 1, 254);
                double y = Math.Clamp(size - position.Y, 0, 255);
                int insert = 0;
                while (insert < points.Count && points[insert].X < x) insert++;
                var key = new object();
                mark();
                doc.MutateCurves(layerId, curves =>
                {
                    var list = curves.Channels[Math.Clamp(channelCombo.SelectedIndex, 0, 3)];
                    if (insert > 0 && insert < list.Count && (x <= list[insert - 1].X || x >= list[insert].X)) return;
                    list.Insert(insert, new CurvePoint(x, y));
                }, key);
                dragIndex = points.FindIndex(point => point.X == x);
                Commit(Loc.F("CE_PointAdded", x, y));
            }
            else
            {
                Commit(Loc.F("CE_MaxPoints", 32));
                dragIndex = -1;
            }
            canvas.CapturePointer(args.Pointer);
            Redraw();
            args.Handled = true;
        };

        canvas.PointerMoved += (_, args) =>
        {
            if (dragIndex < 0 || removed) return;
            var position = args.GetCurrentPoint(canvas).Position;
            var points = Points();
            if (dragIndex >= points.Count) return;
            var key = adjustmentDragKey ??= new object();
            double x = Math.Clamp(Math.Round(position.X), 0, 255);
            double y = Math.Clamp(Math.Round(size - position.Y), 0, 255);
            int dragged = dragIndex;
            mark();
            doc.MutateCurves(layerId, curves =>
            {
                var list = curves.Channels[Math.Clamp(channelCombo.SelectedIndex, 0, 3)];
                if (dragged == 0) { list[0] = new CurvePoint(0, y); return; }
                if (dragged == list.Count - 1) { list[^1] = new CurvePoint(255, y); return; }
                double minX = list[dragged - 1].X + 1, maxX = list[dragged + 1].X - 1;
                if (minX > maxX) return;
                list[dragged] = new CurvePoint(Math.Clamp(x, minX, maxX), y);
            }, key);
            Commit(Loc.F("CE_PointMoved", points[dragIndex].X, points[dragIndex].Y));
            Redraw();
            args.Handled = true;
        };

        canvas.PointerReleased += (_, args) =>
        {
            if (dragIndex >= 0)
            {
                // A point dragged out of the canvas is removed; endpoints just slide.
                var position = args.GetCurrentPoint(canvas).Position;
                var points = Points();
                if (dragIndex > 0 && dragIndex < points.Count - 1
                    && (position.Y < -12 || position.Y > size + 12 || position.X < -12 || position.X > size + 12))
                {
                    int victim = dragIndex;
                    var key = new object();
                    doc.MutateCurves(layerId, curves => curves.Channels[Math.Clamp(channelCombo.SelectedIndex, 0, 3)].RemoveAt(victim), key);
                    doc.EndTransform(key);
                    Commit(Loc.T("CE_PointRemoved"));
                }
                else if (adjustmentDragKey is not null)
                {
                    doc.EndTransform(adjustmentDragKey);
                }
            }
            dragIndex = -1;
            canvas.ReleasePointerCapture(args.Pointer);
            Redraw();
            args.Handled = true;
        };

        channelCombo.SelectionChanged += (_, _) => Redraw();
        Redraw();
        return panel;
    }

    private StackPanel HueSaturationRows(EditorDocument doc, Guid layerId, LayerAdjustment adjustment)
    {

        Action mark = () => MarkLayerBandDirty(layerId);
        var panel = new StackPanel { Spacing = 8 };

        // The seven ranges; the band picker selects which one the sliders and the hue bar edit.
        var ranges = new[] { ColorRange.Master, ColorRange.Reds, ColorRange.Yellows, ColorRange.Greens, ColorRange.Cyans, ColorRange.Blues, ColorRange.Magentas };
        var bandCombo = new ComboBox { Width = 150, ItemsSource = ranges.Select(range => Loc.Name("Range", Enum.GetName(range)!)).ToList(), SelectedIndex = 0 };
        panel.Children.Add(bandCombo);

        // The hue bar: a 0–360 gradient with the selected band's four handles on it.
        var hueBar = new Canvas { Width = 360, Height = 30 };
        panel.Children.Add(new Viewbox { Child = hueBar, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly });
        var handles = new Ellipse[4];
        for (int i = 0; i < 4; i++)
        {
            handles[i] = new Ellipse { Width = 12, Height = 26, StrokeThickness = 2, Stroke = new SolidColorBrush(Microsoft.UI.Colors.White) };
            hueBar.Children.Add(handles[i]);
        }

        var sliders = new StackPanel { Spacing = 6 };
        panel.Children.Add(sliders);
        panel.Children.Add(CheckRow(Loc.T("L_Colorize"), adjustment.Colorize, value =>
            doc.MutateAdjustment(layerId, a => a.Colorize = value)));

        // The settings object is materialized from the scalar fields the first time a
        // per-range control is touched — before that the macOS scalars are the truth.
        void EnsureSettings(LayerAdjustment a)
        {
            a.HsvSettings ??= HueSaturationSettings.FromScalars(a.Hue, a.Saturation, a.Lightness, a.Colorize);
        }

        ColorRange SelectedRange()
        {
            int index = Math.Clamp(bandCombo.SelectedIndex, 0, ranges.Length - 1);
            return ranges[index];
        }

        string RangeKey(ColorRange range) => Loc.Name("Range", Enum.GetName(range)!);

        HueBand BandOf(LayerAdjustment a, ColorRange range) =>
            a.HsvSettings != null && a.HsvSettings.Bands.TryGetValue(RangeKey(range), out var band)
                ? band
                : ColorRangeDefaults.Band(range);

        void RebuildSliders()
        {
            sliders.Children.Clear();
            var current = doc.Snapshot.Manifest.Layers.FirstOrDefault(candidate => candidate.Id == layerId)?.Adjustment;
            if (current is null) return;
            EnsureSettings(current);
            var range = SelectedRange();

            sliders.Children.Add(SliderRow(Loc.T("L_Hue"), -180, 180, current.Hue, value =>
                doc.MutateAdjustment(layerId, a => a.Hue = value, adjustmentDragKey), markBand: mark));
            sliders.Children.Add(SliderRow(Loc.T("L_Saturation"), -100, 100, current.Saturation, value =>
                doc.MutateAdjustment(layerId, a => a.Saturation = value, adjustmentDragKey), markBand: mark));
            sliders.Children.Add(SliderRow(Loc.T("L_Lightness"), -100, 100, current.Lightness, value =>
                doc.MutateAdjustment(layerId, a => a.Lightness = value, adjustmentDragKey), markBand: mark));
        }

        void RebuildHueBar()
        {
            var current = doc.Snapshot.Manifest.Layers.FirstOrDefault(candidate => candidate.Id == layerId)?.Adjustment;
            if (current is null) return;
            var range = SelectedRange();
            var band = BandOf(current, range);

            // Gradient background: 36 hue stops.
            hueBar.Children.Clear();
            var gradient = new LinearGradientBrush();
            gradient.StartPoint = new Windows.Foundation.Point(0, 0.5);
            gradient.EndPoint = new Windows.Foundation.Point(1, 0.5);
            for (int stop = 0; stop <= 36; stop++)
            {
                gradient.GradientStops.Add(new GradientStop
                {
                    Color = HsvColor(stop * 10, 1, 1),
                    Offset = stop / 36.0,
                });
            }
            hueBar.Children.Add(new Rectangle { Width = 360, Height = 30, Fill = gradient });

            // The four handles: falloffStart, rangeStart, rangeEnd, falloffEnd.
            double[] values = { band.FalloffStart, band.RangeStart, band.RangeEnd, band.FalloffEnd };
            for (int i = 0; i < 4; i++)
            {
                double x = values[i] / 360.0 * 360;
                Canvas.SetLeft(handles[i], x - 6);
                Canvas.SetTop(handles[i], 2);
                handles[i].Fill = new SolidColorBrush(i is 1 or 2 ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Gray);
            }
        }

        bandCombo.SelectionChanged += (_, _) =>
        {
            RebuildSliders();
            RebuildHueBar();
        };

        // Handle dragging: press near a handle to grab it, drag to move, with the macOS
        // setHandle order/limit rules enforced by HueBand.WithHandle.
        int dragHandle = -1;
        hueBar.PointerPressed += (_, args) =>
        {
            var position = args.GetCurrentPoint(hueBar).Position;
            dragHandle = -1;
            var current = doc.Snapshot.Manifest.Layers.FirstOrDefault(candidate => candidate.Id == layerId)?.Adjustment;
            if (current is null) return;
            var band = BandOf(current, SelectedRange());
            double[] values = { band.FalloffStart, band.RangeStart, band.RangeEnd, band.FalloffEnd };
            double x = position.X / 360.0 * 360;
            for (int i = 0; i < 4; i++)
            {
                if (Math.Abs(values[i] - x) <= 360.0 / hueBar.ActualWidth * 8 || Math.Abs(((values[i] - x + 360) % 360)) <= 8)
                {
                    dragHandle = i;
                    break;
                }
            }
            if (dragHandle >= 0) hueBar.CapturePointer(args.Pointer);
            args.Handled = true;
        };
        hueBar.PointerMoved += (_, args) =>
        {
            if (dragHandle < 0) return;
            var position = args.GetCurrentPoint(hueBar).Position;
            double degrees = Math.Clamp(position.X / hueBar.ActualWidth, 0, 1) * 360;
            adjustmentDragKey ??= new object();
            mark();
            doc.MutateAdjustment(layerId, a =>
            {
                EnsureSettings(a);
                a.HsvSettings!.Range = SelectedRange();
                var key = RangeKey(SelectedRange());
                a.HsvSettings.Bands[key] = BandOf(a, SelectedRange()).WithHandle(dragHandle, degrees);
            }, adjustmentDragKey);
            RebuildHueBar();
            args.Handled = true;
        };
        hueBar.PointerReleased += (_, args) =>
        {
            if (dragHandle >= 0)
            {
                dragHandle = -1;
                adjustmentDragKey = null;
                RebuildSliders();
            }
            hueBar.ReleasePointerCapture(args.Pointer);
            args.Handled = true;
        };

        RebuildSliders();
        RebuildHueBar();
        return panel;
    }

    private static Windows.UI.Color HsvColor(double hueDegrees, double saturation, double lightness)
    {
        double c = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        double sector = hueDegrees / 60;
        double second = c * (1 - Math.Abs(sector % 2 - 1));
        double m = lightness - c / 2;
        (double r, double g, double b) = ((int)sector) switch
        {
            0 => (c, second, 0.0),
            1 => (second, c, 0.0),
            2 => (0.0, c, second),
            3 => (0.0, second, c),
            4 => (second, 0.0, c),
            _ => (c, 0.0, second),
        };
        return Microsoft.UI.ColorHelper.FromArgb(255,
            (byte)Math.Clamp(Math.Round((r + m) * 255), 0, 255),
            (byte)Math.Clamp(Math.Round((g + m) * 255), 0, 255),
            (byte)Math.Clamp(Math.Round((b + m) * 255), 0, 255));
    }

    private StackPanel ExposureRows(EditorDocument doc, Guid layerId, LayerAdjustment adjustment)
    {

        Action mark = () => MarkLayerBandDirty(layerId);
        var panel = new StackPanel { Spacing = 8 };
        var exposure = adjustment.ExposureSettings ?? new ExposureSettings();
        panel.Children.Add(SliderRow(Loc.T("L_Exposure"), -20, 20, exposure.Exposure, value =>
            doc.MutateAdjustment(layerId, a =>
            {
                var settings = a.ExposureSettings ?? new ExposureSettings();
                settings.Exposure = value;
                a.ExposureSettings = settings;
            }, adjustmentDragKey), markBand: mark));
        panel.Children.Add(SliderRow(Loc.T("L_Offset"), -0.5, 0.5, exposure.Offset, value =>
            doc.MutateAdjustment(layerId, a =>
            {
                var settings = a.ExposureSettings ?? new ExposureSettings();
                settings.Offset = value;
                a.ExposureSettings = settings;
            }, adjustmentDragKey), markBand: mark));
        panel.Children.Add(SliderRow(Loc.T("L_Gamma"), 0.01, 9.99, exposure.Gamma, value =>
            doc.MutateAdjustment(layerId, a =>
            {
                var settings = a.ExposureSettings ?? new ExposureSettings();
                settings.Gamma = value;
                a.ExposureSettings = settings;
            }, adjustmentDragKey), markBand: mark));
        return panel;
    }

    /// <param name="markBand">Marks the layer's band dirty before the apply — a slider tick
    /// then renders its rectangle, not the whole canvas.</param>
    private Grid SliderRow(string label, double minimum, double maximum, double value, Action<double> apply, Action<double>? preview = null, Action? markBand = null)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });

        var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        Grid.SetColumn(name, 0);

        var slider = new Slider { Minimum = minimum, Maximum = maximum, Value = value, MinWidth = 0 };
        // The label is drawn beside the slider, but a screen reader only gets what the control reports, and a
        // bare slider reports its value. Rows are built in a loop, so this one line is what names every
        // parameter slider in every panel.
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(slider, label);
        Grid.SetColumn(slider, 1);

        var number = new TextBlock { Text = $"{value:0.##}", VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
        Grid.SetColumn(number, 2);

        slider.ValueChanged += (_, args) =>
        {
            number.Text = $"{args.NewValue:0.##}";
            markBand?.Invoke();
            preview?.Invoke(args.NewValue);
            adjustmentDragKey ??= new object();
            apply(args.NewValue);
        };
        slider.PointerCaptureLost += (_, _) => adjustmentDragKey = null;

        grid.Children.Add(name);
        grid.Children.Add(slider);
        grid.Children.Add(number);
        return grid;
    }

    private CheckBox CheckRow(string label, bool value, Action<bool> apply)
    {
        var check = new CheckBox { Content = label, IsChecked = value };
        // Same reasoning as SliderRow's one line: a row built in a panel loop should be findable by
        // what it says, by the probes and by a screen reader alike.
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(check, label);
        check.Checked += (_, _) => apply(true);
        check.Unchecked += (_, _) => apply(false);
        return check;
    }
}
