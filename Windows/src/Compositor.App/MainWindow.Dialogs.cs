using Compositor.Core;
using Compositor.Core.Psd;
using Compositor.Renderer;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

/// <summary>MainWindow's dialog construction: selection amount, dither, distort, layer
/// effects, keyboard shortcut reference, trim, image size, and export — the menu commands
/// that build their UI inline instead of living in a panel.</summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// The external-change prompt the macOS controller shows before it would replace unsaved work
    /// (ProjectController+ExternalChanges.swift:89-96). Two answers and no third: Revert takes the
    /// package on disk, Keep Mine stays with the editor. Closing it answers nothing, which leaves the
    /// change pending and the next save refused.
    /// </summary>
    private async Task<ContentDialogResult> AskExternalConflictAsync()
    {
        string name = projectPath is null ? Loc.T("M_Untitled")
            : System.IO.Path.GetFileName(projectPath.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        var dialog = new ContentDialog
        {
            Title = Loc.F("M_ExternalConflictTitle", name),
            Content = new TextBlock { Text = Loc.T("M_ExternalConflictBody"), TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = Loc.T("Dlg_Revert"),
            SecondaryButtonText = Loc.T("Dlg_KeepMine"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        return await ShowProjectDialogAsync(dialog);
    }

    private async Task PromptSelectionModificationAsync(SelectionModification operation, int maximum)
    {
        if (!CanEditSelection || document is not { } owner) return;
        var pending = selectionWorkResult; if (pending is not null) await pending;
        if (!ReferenceEquals(owner, document) || !CanEditSelection || owner.ActiveSelection is not { IsEmpty: false } original)
        { SetStatus(Loc.T("S_SelectionMaskNeedsSelection")); return; }
        int? amount;
        selectionAmountPending = true;
        RefreshTransformAvailability(true);
        try { amount = await PromptSelectionAmountAsync(operation, maximum); }
        finally { selectionAmountPending = false; RefreshTransformAvailability(true); }
        if (amount is not { } value || !ReferenceEquals(owner, document) || !ReferenceEquals(owner.ActiveSelection, original)) return;
        ApplySelectionModification(operation, value);
    }

    /// <summary>The amount panel shared by Expand/Contract/Feather, in the window's floating host (#37): the
    /// Mac's is a panel too (LassoControls.swift:182-232), with a slider beside the field and OK out of reach
    /// until the number is whole and inside the range. It answers null when cancelled, when the field was
    /// cleared, and when the document went away under it — the last of those is the one a modal prompt could
    /// not face, because nothing could take the window away while it waited.</summary>
    private Task<int?> PromptSelectionAmountAsync(SelectionModification operation, int maximum)
    {
        var answer = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var remembered = Math.Clamp(lastSelectionAmounts.GetValueOrDefault(operation, 1), 1, maximum);
        var box = new NumberBox
        {
            Value = remembered, Minimum = 1, Maximum = maximum,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 96,
        };
        var slider = new Slider { Minimum = 1, Maximum = maximum, Value = remembered, Width = 130 };
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock { Text = Loc.F("M_SelectionAmountDescription", maximum), TextWrapping = TextWrapping.Wrap });
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        line.Children.Add(slider);
        line.Children.Add(box);
        body.Children.Add(line);
        bool Sync(double value)
        {
            // The Mac's amount is a whole number in 1...maximum, and anything else leaves OK out of reach
            // rather than rounding whatever the field happens to hold.
            bool valid = !double.IsNaN(value) && value >= 1 && value <= maximum && value == Math.Floor(value);
            SetFloatingPanelConfirmEnabled("selectionAmount", valid);
            return valid;
        }
        box.ValueChanged += (_, args) =>
        {
            if (double.IsNaN(args.NewValue)) { Sync(double.NaN); return; }
            slider.Value = Math.Clamp(args.NewValue, 1, maximum);
            Sync(args.NewValue);
        };
        slider.ValueChanged += (_, args) => { box.Value = args.NewValue; };
        ShowFloatingPanel("selectionAmount", Loc.T("M_SelectionAmountTitle"), body,
            commit =>
            {
                if (commit && !double.IsNaN(box.Value)) lastSelectionAmounts[operation] = (int)box.Value;
                answer.TrySetResult(commit && !double.IsNaN(box.Value) ? (int)box.Value : null);
                return Task.CompletedTask;
            },
            onAbandon: () => answer.TrySetResult(null));
        Sync(remembered);
        return answer.Task;
    }


    /// <summary>View ▸ Grid Settings… (CompositorApp.swift:131-132). The sheet edits a copy and shows every
    /// change on the canvas as it goes, exactly as GridSettingsSheet.swift:119-123 previews on each edit; only
    /// OK lets it write, and Cancel hands back what the sheet opened with.</summary>
    private async void OnGridSettings(object sender, RoutedEventArgs e) => await ShowGridSettingsAsync();

    private async Task ShowGridSettingsAsync()
    {
        if (document is null) return;
        // The grid goes on for as long as the sheet is open, whatever the switch said, so every edit has
        // something to change on the canvas; the switch's own value comes back on the way out
        // (ProjectController.swift:136-137, :155).
        var shownBefore = showsGrid;
        SetGridVisible(true);
        var sheet = new GridSettingsForm(this, LayoutGrid, GridAppearance);
        var dialog = sheet.Build();
        ContentDialogResult choice;
        try
        {
#if DEBUG
            gridSettingsSheetProbe = sheet;
#endif
            try { choice = await ShowProjectDialogAsync(dialog); }
            finally
            {
#if DEBUG
                gridSettingsSheetProbe = null;
#endif
            }
        }
        finally { SetGridVisible(shownBefore); }
        sheet.Finish(choice);
    }

    /// <summary>
    /// The six fields of View ▸ Grid Settings… and the two things they do: preview the values they hold on the
    /// canvas, and on OK hand them over for writing. The Mac rebuilds its copy of the pair on every keystroke
    /// (GridSettingsSheet.swift:29) and disables OK while the pair is unusable (:108-115) — the same rule here.
    /// </summary>
    private sealed class GridSettingsForm
    {
        private readonly MainWindow owner;
        private readonly (LayoutGrid Grid, GridAppearance Appearance) openedWith;
        private readonly ComboBox presetCombo = new() { Width = 150 };
        private readonly ComboBox styleCombo = new() { Width = 150 };
        private readonly NumberBox opacity = new() { Minimum = GridAppearance.OpacityMin, Maximum = GridAppearance.OpacityMax, Width = 90 };
        private readonly NumberBox spacing = new() { Minimum = LayoutGrid.SpacingMin, Maximum = LayoutGrid.SpacingMax, Width = 110 };
        private readonly NumberBox subdivisions = new() { Minimum = LayoutGrid.SubdivisionMin, Maximum = LayoutGrid.SubdivisionMax, Width = 90 };
        private readonly TextBlock caption = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        private ContentDialog dialog = null!;
        private AdjustmentColor customColor;
        private GridAppearance.Preset pickedPreset;
        private bool ready;

        public GridSettingsForm(MainWindow owner, LayoutGrid grid, GridAppearance appearance)
        {
            this.owner = owner;
            openedWith = (grid, appearance);
            customColor = appearance.CustomColor;
            pickedPreset = appearance.ColorPreset;
            foreach (GridAppearance.Preset preset in Enum.GetValues<GridAppearance.Preset>())
                presetCombo.Items.Add(new ComboBoxItem { Content = Loc.T($"GridPreset_{preset}") });
            foreach (GridAppearance.LineStyle style in Enum.GetValues<GridAppearance.LineStyle>())
                styleCombo.Items.Add(new ComboBoxItem { Content = Loc.T($"GridStyle_{style}") });
            presetCombo.SelectedIndex = (int)appearance.ColorPreset;
            styleCombo.SelectedIndex = (int)appearance.Style;
            opacity.Value = appearance.Opacity;
            spacing.Value = grid.Spacing;
            subdivisions.Value = grid.Subdivisions;
        }

        /// <summary>Handlers go on after the initial values, because a NumberBox reports the value it is given
        /// as a change and the sheet would otherwise preview — and mark itself dirty — while it is still opening.</summary>
        public ContentDialog Build()
        {
            presetCombo.SelectionChanged += (_, _) => { pickedPreset = (GridAppearance.Preset)Math.Max(0, presetCombo.SelectedIndex); Preview(); };
            styleCombo.SelectionChanged += (_, _) => Preview();
            opacity.ValueChanged += (_, _) => Preview();
            spacing.ValueChanged += (_, _) => Preview();
            subdivisions.ValueChanged += (_, _) => Preview();
            dialog = new ContentDialog
            {
                Title = Loc.T("M_GridSettings"),
                Content = new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        Row(Loc.T("L_GridColor"), presetCombo),
                        owner.ColorChipRow(Loc.T("L_GridCustomColor"), ToWinColor(customColor), PickCustomColor, null),
                        Row(Loc.T("L_GridStyle"), styleCombo),
                        Row(Loc.T("L_GridOpacity"), opacity),
                        Row(Loc.T("L_GridlineEvery"), spacing, new TextBlock { Text = Loc.T("L_GridPixels"), VerticalAlignment = VerticalAlignment.Center }),
                        Row(Loc.T("L_GridSubdivisions"), subdivisions),
                        caption,
                    },
                },
                PrimaryButtonText = Loc.T("Dlg_Confirm"),
                SecondaryButtonText = Loc.T("Dlg_RestoreDefaults"),
                CloseButtonText = Loc.T("Dlg_SheetCancel"),
                DefaultButton = ContentDialogButton.Primary,
            };
            dialog.SecondaryButtonClick += (_, _) => RestoreDefaults();
            ready = true;
            Show();
            return dialog;
        }

        /// <summary>
        /// A pick in the swatch is the Mac's Custom path: the color is stored, the preset moves to Custom, and
        /// the preset that was in use is kept inside the custom color so switching back finds it
        /// (GridSettingsSheet.swift:32-45). Public because the swatch's picker is the other caller and a probe
        /// cannot raise a flyout's color change.
        /// </summary>
        public void PickCustomColor(Windows.UI.Color picked)
        {
            customColor = new AdjustmentColor(picked.R / 255.0, picked.G / 255.0, picked.B / 255.0);
            pickedPreset = GridAppearance.Preset.Custom;
            presetCombo.SelectedItem = presetCombo.Items[(int)GridAppearance.Preset.Custom];
            Preview();
        }

        /// <summary>The sheet's Restore Defaults button (GridSettingsSheet.swift:98-106), reachable for the same
        /// reason as the swatch: a probe has no button click to raise.</summary>
        public void RestoreDefaults()
        {
            var defaults = GridAppearance.Default;
            customColor = defaults.CustomColor;
            pickedPreset = defaults.ColorPreset;
            presetCombo.SelectedIndex = (int)defaults.ColorPreset;
            styleCombo.SelectedIndex = (int)defaults.Style;
            opacity.Value = defaults.Opacity;
            spacing.Value = LayoutGrid.Default.Spacing;
            subdivisions.Value = LayoutGrid.Default.Subdivisions;
        }

        private void Preview()
        {
            if (!ready) return;
            owner.SetGridSettings(CurrentGrid, CurrentAppearance, persist: false);
            Show();
        }

        private void Show()
        {
            // Judged on what the two fields hold, not on what the model agreed to keep: asking for 64 parts in
            // 2 pixels is unusable even though the grid would clamp it down to 2 (GridSettingsSheet.swift:24-27).
            bool valid = Number(subdivisions) <= Number(spacing);
            caption.Text = valid
                ? Loc.F("S_GridSubdivisionEvery", CurrentGrid.Step.ToString("0.##"))
                : Loc.T("S_GridRangeHint");
            caption.Foreground = new SolidColorBrush(valid ? Microsoft.UI.Colors.Gray : Microsoft.UI.Colors.Orange);
            dialog.IsPrimaryButtonEnabled = valid;
        }

        private int Number(NumberBox box) => (int)Math.Round(double.IsNaN(box.Value) ? box.Minimum : box.Value);

        private LayoutGrid CurrentGrid => new(Number(spacing), Number(subdivisions));

        private GridAppearance CurrentAppearance => new(
            pickedPreset, customColor,
            (GridAppearance.LineStyle)Math.Max(0, styleCombo.SelectedIndex), Number(opacity));

        /// <summary>OK writes; anything else — Cancel, the escape key, or the sheet never answering — is the
        /// Mac's Cancel, which puts back what was there before the sheet opened (GridSettingsSheet.swift:97).</summary>
        public void Finish(ContentDialogResult choice)
        {
            if (choice == ContentDialogResult.Primary) owner.SetGridSettings(CurrentGrid, CurrentAppearance, persist: true);
            else owner.SetGridSettings(openedWith.Grid, openedWith.Appearance, persist: false);
        }

        /// <summary>What the sheet would draw right now — after the model has had its say about the ranges.</summary>
        public (LayoutGrid Grid, GridAppearance Appearance) Current => (CurrentGrid, CurrentAppearance);

        /// <summary>The two things the sheet shows the user about validity, and the five fields that decide them.
        /// Setting one of those goes through the same handler a click or a keystroke would.</summary>
        public bool OkEnabled => dialog.IsPrimaryButtonEnabled;
        public string CaptionText => caption.Text;
        public ComboBox PresetField => presetCombo;
        public ComboBox StyleField => styleCombo;
        public NumberBox OpacityField => opacity;
        public NumberBox SpacingField => spacing;
        public NumberBox SubdivisionsField => subdivisions;
        public AdjustmentColor CustomColorHeld => customColor;

        private static StackPanel Row(string label, UIElement field, params UIElement[] after)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock { Text = label, Width = 110, VerticalAlignment = VerticalAlignment.Center });
            // The word is drawn beside the field, but a screen reader only gets what the field itself reports,
            // and these reported nothing. One line here is every row in the sheet — the same place the fix went
            // for the slider rows and the number rows.
            if (field is Microsoft.UI.Xaml.FrameworkElement element)
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, label);
            row.Children.Add(field);
            foreach (var extra in after) row.Children.Add(extra);
            return row;
        }

        private static Windows.UI.Color ToWinColor(AdjustmentColor color) => Microsoft.UI.ColorHelper.FromArgb(255,
            (byte)Math.Round(Math.Clamp(color.Red, 0, 1) * 255),
            (byte)Math.Round(Math.Clamp(color.Green, 0, 1) * 255),
            (byte)Math.Round(Math.Clamp(color.Blue, 0, 1) * 255));
    }

    /// <summary>Filter ▸ Dither opens in the window's floating panel rather than a ContentDialog (#37): the
    /// Mac keeps its filter sheet floating (FloatingPanelController "filterPanel"), and a preview you cannot
    /// see while you drag it is not a preview. The panel is non-modal, so the answer now comes from its two
    /// buttons instead of an awaited dialog result — and the canvas stays live underneath it.</summary>
    private void OnDither(object sender, RoutedEventArgs e)
    {
        if (FloatingPanelIsFinishing("filter")) return;
        if (AnotherFilterSheetOpen) { RefuseSecondFilterSheet(); return; }
        if (!CanEditLayerCommands || document is not { } owner || SelectedLayer is not { } layer) return;
        if (BeginDitherPreview(owner, layer.Id) is not { } session)
        {
            SetStatus(Loc.T("S_PaintNeedsLayer"));
            return;
        }
        var form = new DitherForm(this, session);
#if DEBUG
        ditherFormProbe = form;
#endif
        ShowFloatingPanel("filter", Loc.T("M_DitherDialog"), form.BuildPanel(), FinishDitherPanelAsync,
            keepOpenWhileFinishing: true);
    }

    private async Task FinishDitherPanelAsync(bool commit)
    {
#if DEBUG
        ditherFormProbe = null;
        ditherProbeFinish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
#endif
        // The panel previewed as it was set, so OK only has to close the step it wrote into; anything that
        // ended without a kept preview — Cancel, a replaced document — hands the pixels back first.
        var applied = await FinishDitherPreviewAsync(commit);
#if DEBUG
        ditherProbeFinish?.TrySetResult(applied);
#endif
    }

    private async void OnDistort(object sender, RoutedEventArgs e)
    {
        if (!CanEditLayerCommands || document is not { } owner || SelectedLayer is not { } layer
            || owner.BeginTransformDraft([layer.Id], paintOnMask) is not { } edit) return;
        var current = DistortWarp.CornersOf(edit.Value);
        string[] labels = ["L_DistortTopLeft", "L_DistortTopRight", "L_DistortBottomRight", "L_DistortBottomLeft"];
        var boxes = current.Select(corner =>
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var xBox = new NumberBox { Value = corner.X, Width = 90, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
            var yBox = new NumberBox { Value = corner.Y, Width = 90, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
            panel.Children.Add(new TextBlock { Text = Loc.T("L_DistortX"), VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(xBox);
            panel.Children.Add(new TextBlock { Text = Loc.T("L_DistortY"), VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(yBox);
            return (Panel: panel, X: xBox, Y: yBox);
        }).ToList();
        var rows = new StackPanel { Spacing = 6 };
        for (int i = 0; i < 4; i++)
        {
            rows.Children.Add(new TextBlock { Text = Loc.T(labels[i]), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            rows.Children.Add(boxes[i].Panel);
        }
        var dialog = new ContentDialog
        {
            Title = Loc.T("M_DistortDialog"),
            Content = new ScrollViewer { MaxHeight = 420, Content = rows },
            PrimaryButtonText = Loc.T("Dlg_Confirm"),
            CloseButtonText = Loc.T("M_Close"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        if (await ShowProjectDialogAsync(dialog) != ContentDialogResult.Primary) return;
        var corners = boxes.Select(box => new PointD(
            double.IsNaN(box.X.Value) ? box.X.Minimum : box.X.Value,
            double.IsNaN(box.Y.Value) ? box.Y.Minimum : box.Y.Value)).ToArray();
        if (!BeginDistortCornersCommand(owner, edit, corners)) SetStatus(Loc.T("S_DistortUnusable"));
    }

    /// <summary>The effect kinds in the panel order the macOS app lists them, each with the
    /// resource key holding its display name.</summary>
    private static readonly (LayerEffectKind Kind, string Key)[] EffectKinds =
    [
        (LayerEffectKind.Stroke, "L_EffectStroke"),
        (LayerEffectKind.Shadow, "L_EffectShadow"),
        (LayerEffectKind.ColorOverlay, "L_EffectColorOverlay"),
        (LayerEffectKind.InnerShadow, "L_EffectInnerShadow"),
        (LayerEffectKind.OuterGlow, "L_EffectOuterGlow"),
        (LayerEffectKind.InnerGlow, "L_EffectInnerGlow"),
    ];

    private static string EffectKindLabel(LayerEffectKind kind) => kind switch
    {
        LayerEffectKind.Stroke => Loc.T("L_EffectStroke"),
        LayerEffectKind.Shadow => Loc.T("L_EffectShadow"),
        LayerEffectKind.ColorOverlay => Loc.T("L_EffectColorOverlay"),
        LayerEffectKind.InnerShadow => Loc.T("L_EffectInnerShadow"),
        LayerEffectKind.OuterGlow => Loc.T("L_EffectOuterGlow"),
        LayerEffectKind.InnerGlow => Loc.T("L_EffectInnerGlow"),
        _ => "",
    };

    /// <summary>How far the effects reach past the layer's own pixels, as the Mac renderer's
    /// margin(for:) computes it — the band a preview write must mark dirty.</summary>
    private static double EffectsMargin(LayerEffects effects)
    {
        double margin = 0;
        if (effects.Stroke is { } stroke && !stroke.Inside) margin = Math.Max(margin, stroke.Size);
        if (effects.Shadow is { } shadow) margin = Math.Max(margin, shadow.Distance + shadow.Blur * 3);
        if (effects.OuterGlow is { } glow) margin = Math.Max(margin, glow.Size * 3);
        return Math.Ceiling(margin) + 2;
    }

    /// <summary>
    /// The layer effects dialog: one checkbox per effect kind (the Mac's add/remove), and the
    /// selected kind's sliders beneath — the same rows and ranges as the macOS EffectsSheet.
    /// Every change previews live on the canvas under one coalesced undo step; Cancel restores
    /// what the layer carried when the dialog opened, as the sheet's Cancel does.
    /// </summary>
    /// <summary>The layer effects sheet opens in the floating host (#37), as it does on the Mac
    /// (EffectsSheet.swift:4 in a FloatingPanelController): every change already previews under the session key,
    /// so the panel has to sit beside a canvas the user can still look at and still work on.</summary>
    private void OnLayerEffects(object sender, RoutedEventArgs e) => OpenLayerEffects(SelectedEffect?.Kind);

    private void OpenLayerEffects(LayerEffectKind? preferredKind = null)
    {
        if (IsFileImporting) return;
        if (document is null || SelectedLayer is not { } layer) return;
        if (effectsPanelLayer == layer.Id && effectsPanelSelectKind is { } selectKind)
        {
            if (preferredKind is { } requested) selectKind(requested);
            return;
        }
        // The Mac's canEditEffects guard: no groups, and only layers that carry pixels.
        if (layer.IsGroupOrFalse || !document.Snapshot.Images.ContainsKey(layer.Id))
        {
            SetStatus(Loc.T("S_PaintNeedsLayer"));
            return;
        }
        Guid layerId = layer.Id;
        var owner = document;
        LayerEffects? original = LayerEffects.Parse(layer.Effects);
        var working = original ?? new LayerEffects();
        var sessionKey = new object();
        bool previewed = false;

        void Apply()
        {
            // The band covers where the effects reach, not just the layer's pixels, and is
            // marked before the write so the PixelsChanged it raises renders exactly it.
            MarkInteractionDirty(BoundsOf(layer), grow: EffectsMargin(working));
            if (document.SetLayerEffects(layerId, working.IsEmpty ? null : working, sessionKey)) previewed = true;
        }

        void ApplyKindDefaults(LayerEffectKind kind)
        {
            // The Mac's addEffect: a new stroke or overlay takes the background color (the
            // foreground is usually what the layer is painted in), and glows start white.
            switch (kind)
            {
                case LayerEffectKind.Stroke when working.Stroke is { } stroke:
                    stroke.Red = backgroundColor.R / 255.0; stroke.Green = backgroundColor.G / 255.0; stroke.Blue = backgroundColor.B / 255.0;
                    break;
                case LayerEffectKind.ColorOverlay when working.ColorOverlay is { } overlay:
                    overlay.Red = backgroundColor.R / 255.0; overlay.Green = backgroundColor.G / 255.0; overlay.Blue = backgroundColor.B / 255.0;
                    break;
                case LayerEffectKind.OuterGlow when working.OuterGlow is { } outer:
                    outer.Red = outer.Green = outer.Blue = 1; outer.Opacity = 0.75;
                    break;
                case LayerEffectKind.InnerGlow when working.InnerGlow is { } inner:
                    inner.Red = inner.Green = inner.Blue = 1; inner.Opacity = 0.75; inner.Size = 10;
                    break;
            }
        }

        // The six enable toggles, two per row.
        var toggles = new Grid { ColumnSpacing = 16, RowSpacing = 6 };
        toggles.ColumnDefinitions.Add(new ColumnDefinition());
        toggles.ColumnDefinitions.Add(new ColumnDefinition());
        for (int row = 0; row < 3; row++) toggles.RowDefinitions.Add(new RowDefinition());
        var kindCombo = new ComboBox { Name = "LayerEffectKind", Width = 170 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(kindCombo, Loc.T("L_EffectKind"));
        var parameters = new StackPanel { Spacing = 8, MinWidth = 320 };
        LayerEffectKind selected = preferredKind is { } preferred && working.Contains(preferred) ? preferred : working.IsEmpty
            ? LayerEffectKind.Stroke
            : Array.Find(EffectKinds, entry => working.Contains(entry.Kind)).Kind;
        List<LayerEffectKind> presentKinds = [];
        bool syncing = false;

        // Declared as a delegate and filled in below, so RefreshKinds can call it before
        // the parameter rows' builder is defined.
        Action RebuildParameters = () => { };

        void RefreshKinds()
        {
            syncing = true;
            presentKinds = [.. EffectKinds.Select(entry => entry.Kind).Where(working.Contains)];
            if (!presentKinds.Contains(selected)) selected = presentKinds.Count > 0 ? presentKinds[0] : LayerEffectKind.Stroke;
            kindCombo.ItemsSource = presentKinds.Select(EffectKindLabel).ToList();
            kindCombo.SelectedIndex = presentKinds.IndexOf(selected);
            syncing = false;
            RebuildParameters();
        }

        for (int index = 0; index < EffectKinds.Length; index++)
        {
            var kind = EffectKinds[index].Kind;
            var check = new CheckBox { Content = EffectKindLabel(kind), IsChecked = working.Contains(kind) };
            Grid.SetColumn(check, index % 2);
            Grid.SetRow(check, index / 2);
            check.Checked += (_, _) =>
            {
                if (working.Contains(kind)) return;
                working = working.WithKind(kind, present: true);
                ApplyKindDefaults(kind);
                selected = kind;
                RefreshKinds();
                Apply();
            };
            check.Unchecked += (_, _) =>
            {
                if (!working.Contains(kind)) return;
                working = working.WithKind(kind, present: false);
                RefreshKinds();
                Apply();
            };
            toggles.Children.Add(check);
            // A row that carries its effect can be dragged onto another layer, the Mac's LayerEffectRow
            // (NativeLayerList.swift:381-390). WinUI starts no drag on its own, so the press is watched and
            // the drag is asked for once the pointer has moved past the same threshold the mask thumbnail's
            // own gesture uses; a click that never moves stays a click on the checkbox.
            var press = new Windows.Foundation.Point(double.NaN, double.NaN);
            check.PointerPressed += (_, e) =>
            {
                press = working.Contains(kind) ? e.GetCurrentPoint(check).Position : new(double.NaN, double.NaN);
            };
            check.PointerMoved += async (_, e) =>
            {
                if (double.IsNaN(press.X)) return;
                var now = e.GetCurrentPoint(check).Position;
                if ((now.X - press.X) * (now.X - press.X) + (now.Y - press.Y) * (now.Y - press.Y) < 9) return;
                press = new(double.NaN, double.NaN);
                var start = e.GetCurrentPoint(check);
                var drag = ReferenceEquals(document, owner) ? BeginLayerEffectDrag(kind, layerId) : null;
                if (drag is null) return;
                try
                {
                    // The await returns when the drag is over, which is what makes it the cleanup point:
                    // WinUI has no DragEnded on an element, so a cancelled drag would otherwise leave the
                    // window believing an effect row is still in flight.
                    await check.StartDragAsync(start);
                }
                catch (InvalidOperationException refused)
                {
                    App.Log($"Effect drag refused ({kind} of {layerId}): {refused.Message}");
                }
                finally
                {
                    EndLayerPanelDrag(drag);
                }
            };
            check.DragStarting += (sender, e) =>
            {
                if (!working.Contains(kind) || !ReferenceEquals(document, owner) || CurrentLayerPanelDrag?.Effect != (kind, layerId))
                {
                    e.Cancel = true;
                    return;
                }
                e.Data.SetText($"{EffectDragProperty}|{kind}|{layerId:N}");
            };
        }

        // One slider row, as the Mac's slider(_:range:inputRange:): the thumb spans the
        // slider's range while the number accepts the wider input range.
        UIElement NumberRow(string label, double minimum, double sliderMax, double inputMax, double value, Action<double> set)
        {
            var slider = new Slider { Minimum = minimum, Maximum = sliderMax, Value = Math.Clamp(value, minimum, sliderMax), Width = 130 };
            var box = new NumberBox { Minimum = minimum, Maximum = inputMax, Value = Math.Clamp(value, minimum, inputMax),
                Width = 76, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
            slider.ValueChanged += (_, args) => { box.Value = args.NewValue; set(args.NewValue); };
            box.ValueChanged += (_, args) =>
            {
                if (double.IsNaN(args.NewValue)) return; // cleared input
                double clamped = Math.Clamp(args.NewValue, minimum, inputMax);
                slider.Value = Math.Min(clamped, sliderMax); // the thumb pins until dragged again
                set(clamped);
            };
            return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
            {
                new TextBlock { Text = label, Width = 64, VerticalAlignment = VerticalAlignment.Center },
                slider, box,
            } };
        }

        // The effect's color: a chip that opens the app's ColorPicker in a flyout.
        UIElement ColorRow(string label, Action<Windows.UI.Color> set, Windows.UI.Color initial)
        {
            var fill = new Rectangle { Width = 36, Height = 18, Fill = new SolidColorBrush(initial),
                Stroke = new SolidColorBrush(Microsoft.UI.Colors.Gray) };
            var chip = new Button { Content = fill, Padding = new Thickness(2) };
            var picker = new ColorPicker { Color = initial };
            picker.ColorChanged += (_, args) =>
            {
                fill.Fill = new SolidColorBrush(args.NewColor);
                set(args.NewColor);
            };
            Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase.SetAttachedFlyout(chip, new Flyout { Content = picker });
            chip.Click += (source, _) => Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase.ShowAttachedFlyout((FrameworkElement)source);
            return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
            {
                new TextBlock { Text = label, Width = 64, VerticalAlignment = VerticalAlignment.Center },
                chip,
            } };
        }

        void SetColor(byte red, byte green, byte blue, Action<double, double, double> set)
        {
            set(red / 255.0, green / 255.0, blue / 255.0);
            Apply();
        }

        RebuildParameters = () =>
        {
            parameters.Children.Clear();
            if (!working.Contains(selected)) return;
            void Color(double r, double g, double b, Action<double, double, double> set) =>
                parameters.Children.Add(ColorRow(Loc.T("L_Color"),
                    color => SetColor(color.R, color.G, color.B, set),
                    Microsoft.UI.ColorHelper.FromArgb(255, (byte)Math.Round(Math.Clamp(r, 0, 1) * 255),
                        (byte)Math.Round(Math.Clamp(g, 0, 1) * 255), (byte)Math.Round(Math.Clamp(b, 0, 1) * 255))));
            switch (selected)
            {
                case LayerEffectKind.Stroke when working.Stroke is { } stroke:
                    var position = new ComboBox { Width = 120, SelectedIndex = stroke.Inside ? 1 : 0,
                        Items = { new ComboBoxItem { Content = Loc.T("L_EffectOutside") }, new ComboBoxItem { Content = Loc.T("L_EffectInside") } } };
                    position.SelectionChanged += (_, args) =>
                    {
                        stroke.Inside = args.AddedItems.Count > 0 && ReferenceEquals(args.AddedItems[0], position.Items[1]);
                        Apply();
                    };
                    parameters.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
                    {
                        new TextBlock { Text = Loc.T("L_EffectPosition"), Width = 64, VerticalAlignment = VerticalAlignment.Center },
                        position,
                    } });
                    Color(stroke.Red, stroke.Green, stroke.Blue, (r, g, b) => { stroke.Red = r; stroke.Green = g; stroke.Blue = b; });
                    parameters.Children.Add(NumberRow(Loc.T("L_EffectSize"), 0, 20, 500, stroke.Size,
                        value => { stroke.Size = value; Apply(); }));
                    parameters.Children.Add(NumberRow(Loc.T("L_EffectOpacity"), 0, 100, 100, stroke.Opacity * 100,
                        value => { stroke.Opacity = value / 100; Apply(); }));
                    break;
                case LayerEffectKind.Shadow when working.Shadow is { } shadow:
                    Color(shadow.Red, shadow.Green, shadow.Blue, (r, g, b) => { shadow.Red = r; shadow.Green = g; shadow.Blue = b; });
                    parameters.Children.Add(NumberRow(Loc.T("L_EffectOpacity"), 0, 100, 100, shadow.Opacity * 100,
                        value => { shadow.Opacity = value / 100; Apply(); }));
                    parameters.Children.Add(NumberRow(Loc.T("L_Angle"), -180, 180, 180, shadow.Angle,
                        value => { shadow.Angle = value; Apply(); }));
                    parameters.Children.Add(NumberRow(Loc.T("L_Distance"), 0, 100, 5000, shadow.Distance,
                        value => { shadow.Distance = value; Apply(); }));
                    parameters.Children.Add(NumberRow(Loc.T("L_Blur"), 0, 100, 500, shadow.Blur,
                        value => { shadow.Blur = value; Apply(); }));
                    break;
                case LayerEffectKind.ColorOverlay when working.ColorOverlay is { } overlay:
                    Color(overlay.Red, overlay.Green, overlay.Blue, (r, g, b) => { overlay.Red = r; overlay.Green = g; overlay.Blue = b; });
                    parameters.Children.Add(NumberRow(Loc.T("L_EffectOpacity"), 0, 100, 100, overlay.Opacity * 100,
                        value => { overlay.Opacity = value / 100; Apply(); }));
                    break;
                case LayerEffectKind.InnerShadow when working.InnerShadow is { } inner:
                    Color(inner.Red, inner.Green, inner.Blue, (r, g, b) => { inner.Red = r; inner.Green = g; inner.Blue = b; });
                    parameters.Children.Add(NumberRow(Loc.T("L_EffectOpacity"), 0, 100, 100, inner.Opacity * 100,
                        value => { inner.Opacity = value / 100; Apply(); }));
                    parameters.Children.Add(NumberRow(Loc.T("L_Angle"), -180, 180, 180, inner.Angle,
                        value => { inner.Angle = value; Apply(); }));
                    parameters.Children.Add(NumberRow(Loc.T("L_Distance"), 0, 50, 5000, inner.Distance,
                        value => { inner.Distance = value; Apply(); }));
                    parameters.Children.Add(NumberRow(Loc.T("L_Blur"), 0, 100, 500, inner.Blur,
                        value => { inner.Blur = value; Apply(); }));
                    break;
                case LayerEffectKind.OuterGlow when working.OuterGlow is { } glow:
                    Color(glow.Red, glow.Green, glow.Blue, (r, g, b) => { glow.Red = r; glow.Green = g; glow.Blue = b; });
                    parameters.Children.Add(NumberRow(Loc.T("L_EffectSize"), 0, 100, 500, glow.Size,
                        value => { glow.Size = value; Apply(); }));
                    parameters.Children.Add(NumberRow(Loc.T("L_EffectOpacity"), 0, 100, 100, glow.Opacity * 100,
                        value => { glow.Opacity = value / 100; Apply(); }));
                    break;
                case LayerEffectKind.InnerGlow when working.InnerGlow is { } innerGlow:
                    Color(innerGlow.Red, innerGlow.Green, innerGlow.Blue, (r, g, b) => { innerGlow.Red = r; innerGlow.Green = g; innerGlow.Blue = b; });
                    parameters.Children.Add(NumberRow(Loc.T("L_EffectSize"), 0, 100, 500, innerGlow.Size,
                        value => { innerGlow.Size = value; Apply(); }));
                    parameters.Children.Add(NumberRow(Loc.T("L_EffectOpacity"), 0, 100, 100, innerGlow.Opacity * 100,
                        value => { innerGlow.Opacity = value / 100; Apply(); }));
                    break;
            }
        };

        kindCombo.SelectionChanged += (_, _) =>
        {
            if (syncing || kindCombo.SelectedIndex < 0 || kindCombo.SelectedIndex >= presentKinds.Count) return;
            selected = presentKinds[kindCombo.SelectedIndex];
            RebuildParameters();
        };
        RefreshKinds();

        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(toggles);
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Children =
            {
                new TextBlock { Text = Loc.T("L_EffectKind"), VerticalAlignment = VerticalAlignment.Center },
                kindCombo,
            },
        });
        body.Children.Add(parameters);
        // The Mac keeps the live effect editor's session so that a copy dropped onto the layer it is open on
        // can be committed first (LayerEffects.swift:333-336, "a later Cancel cannot undo the copy"). The
        // panel's own session is the same thing here, so it is handed to the window while it is up.
        effectsPanelLayer = layerId;
        effectsPanelSelectKind = kind => kindCombo.SelectedIndex = presentKinds.IndexOf(kind);
        effectsPanelCommit = () =>
        {
            if (!ReferenceEquals(document, owner)) return;
            Apply();
            owner.EndTransform(sessionKey);
        };
        ShowFloatingPanel("effects", Loc.T("M_LayerEffectsDialog"), body, FinishEffectsPanelAsync,
            onAbandon: () => { effectsPanelLayer = null; effectsPanelCommit = null; effectsPanelSelectKind = null; });
        return;

        Task FinishEffectsPanelAsync(bool commit)
        {
            effectsPanelSelectKind = null;
            effectsPanelLayer = null;
            effectsPanelCommit = null;
            // A panel that is still on screen after its document is gone has no layer to answer for: writing
            // the old session key into whoever is open now would close a stranger's undo step.
            if (!ReferenceEquals(document, owner)) return Task.CompletedTask;
            if (commit)
            {
                // Everything already previewed under the session key; this only catches a
                // control whose last change never fired (and is a no-op when it did).
                Apply();
                document.EndTransform(sessionKey);
                if (previewed) SetStatus(Loc.F("S_LayerEffectsSet", layer.Name));
            }
            else if (previewed)
            {
                // Cancel puts back only what this panel touched, as the sheet's Cancel does; the same session
                // key folds the restore into the step it undoes, and closing the session drops that step
                // entirely when the layer ended up exactly as it was.
                MarkInteractionDirty(BoundsOf(layer), grow: Math.Max(EffectsMargin(working), EffectsMargin(original ?? new LayerEffects())));
                document.SetLayerEffects(layerId, original, sessionKey);
                document.EndTransform(sessionKey);
            }
            return Task.CompletedTask;
        }
    }

    private async void OnTrim(object sender, RoutedEventArgs e)
    {
        if (IsFileImporting) return;
        if (document is null) return;
        var basedOn = new ComboBox
        {
            Width = 160,
            SelectedIndex = (int)trimOptions.BasedOn,
            Items = { new ComboBoxItem { Content = Loc.T("L_TrimTransparent") }, new ComboBoxItem { Content = Loc.T("L_TrimTopLeft") }, new ComboBoxItem { Content = Loc.T("L_TrimBottomRight") } },
        };
        var top = new CheckBox { Content = Loc.T("L_TrimTop"), IsChecked = trimOptions.Top };
        var bottom = new CheckBox { Content = Loc.T("L_TrimBottom"), IsChecked = trimOptions.Bottom };
        var left = new CheckBox { Content = Loc.T("L_TrimLeft"), IsChecked = trimOptions.Left };
        var right = new CheckBox { Content = Loc.T("L_TrimRight"), IsChecked = trimOptions.Right };
        var tolerance = new NumberBox { Minimum = 0, Maximum = 255, Value = trimOptions.Tolerance, Width = 90 };
        var sides = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { left, top, right, bottom } };
        var dialog = new ContentDialog
        {
            Title = Loc.T("M_TrimDialog"),
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = Loc.T("L_TrimBasedOn"), VerticalAlignment = VerticalAlignment.Center }, basedOn } },
                    sides,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = Loc.T("L_TrimTolerance"), VerticalAlignment = VerticalAlignment.Center }, tolerance } },
                },
            },
            PrimaryButtonText = Loc.T("Dlg_Confirm"),
            CloseButtonText = Loc.T("M_Close"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        if (await ShowProjectDialogAsync(dialog) != ContentDialogResult.Primary) return;
        trimOptions = new EditorDocument.TrimOptions
        {
            BasedOn = (EditorDocument.TrimBasedOn)Math.Clamp(basedOn.SelectedIndex, 0, 2),
            Top = top.IsChecked == true,
            Bottom = bottom.IsChecked == true,
            Left = left.IsChecked == true,
            Right = right.IsChecked == true,
            Tolerance = double.IsNaN(tolerance.Value) ? 0 : (int)Math.Clamp(tolerance.Value, 0, 255),
        };
        var sizeBefore = (document.Snapshot.Manifest.Width, document.Snapshot.Manifest.Height);
        if (document.TrimCanvas(trimOptions))
        {
            var manifest = document.Snapshot.Manifest;
            SetStatus(Loc.F("S_Trimmed", $"{manifest.Width} × {manifest.Height}"));
        }
        else
        {
            SetStatus(Loc.F("S_TrimNothing", $"{sizeBefore.Item1} × {sizeBefore.Item2}"));
        }
    }

    private async void OnImageSize(object sender, RoutedEventArgs e)
    {
        if (IsFileImporting) return;
        if (document is null) return;
        var manifest = document.Snapshot.Manifest;
        double resolution = manifest.Resolution ?? 72;
        var widthBox = new NumberBox { Value = manifest.Width, MinWidth = 120, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var heightBox = new NumberBox { Value = manifest.Height, MinWidth = 120, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var lockRatio = new CheckBox { Content = Loc.T("Dlg_LockRatio"), IsChecked = true };
        var unitCombo = new ComboBox { Width = 140, MinWidth = 120,
            ItemsSource = new[] { Loc.T("U_Pixels"), Loc.T("U_Percent"), Loc.T("U_Inches"), Loc.T("U_Centimeters") },
            SelectedIndex = 0 };
        double ratio = (double)manifest.Width / manifest.Height;

        // Unit conversions, the macOS ImageSizeSheet closures: displayed values derive from
        // pixels (Percent against the original dimension, Inches/Centimeters from the
        // resolution), and an edit folds back the same way.
        double Displayed(double pixels, int original) => UnitKey() switch
        {
            "Percent" => pixels / original * 100,
            "Inches" => pixels / resolution,
            "Centimeters" => pixels / resolution * 2.54,
            _ => pixels,
        };
        double Pixels(double displayed, int original) => UnitKey() switch
        {
            "Percent" => displayed / 100 * original,
            "Inches" => displayed * resolution,
            "Centimeters" => displayed / 2.54 * resolution,
            _ => displayed,
        };
        string UnitKey() => unitCombo.SelectedIndex switch
        {
            1 => "Percent",
            2 => "Inches",
            3 => "Centimeters",
            _ => "Pixels",
        };

        // A unit switch repopulates both boxes from the same underlying pixel values.
        double widthPixels = manifest.Width, heightPixels = manifest.Height;
        void Repopulate()
        {
            syncingImageSize = true;
            widthBox.Value = Math.Round(Displayed(widthPixels, manifest.Width) * 100) / 100;
            heightBox.Value = Math.Round(Displayed(heightPixels, manifest.Height) * 100) / 100;
            syncingImageSize = false;
        }
        unitCombo.SelectionChanged += (_, _) => Repopulate();

        void Sync(object sender2, object _)
        {
            if (syncingImageSize) return;
            var changed = (NumberBox)sender2;
            bool isWidth = ReferenceEquals(changed, widthBox);
            int original = isWidth ? manifest.Width : manifest.Height;
            double pixels = Pixels(changed.Value, original);
            if (pixels < 1) { Repopulate(); return; }
            if (isWidth) widthPixels = pixels; else heightPixels = pixels;
            double otherPixels = isWidth
                ? pixels * heightPixels / widthPixels
                : pixels * widthPixels / heightPixels;
            if (lockRatio.IsChecked == true && ratio > 0 && pixels > 0)
            {
                // The lock keeps the ORIGINAL ratio; fold the sibling back through it.
                otherPixels = isWidth ? pixels / ratio : pixels * ratio;
            }
            if (otherPixels < 1) { Repopulate(); return; }
            syncingImageSize = true;
            if (isWidth) heightPixels = otherPixels; else widthPixels = otherPixels;
            var otherBox = isWidth ? heightBox : widthBox;
            otherBox.Value = Math.Round(Displayed(otherPixels, isWidth ? manifest.Height : manifest.Width) * 100) / 100;
            syncingImageSize = false;
        }
        widthBox.ValueChanged += Sync;
        heightBox.ValueChanged += Sync;
        var dialog = new ContentDialog
        {
            Title = Loc.T("M_ImageSize"),
            Content = new StackPanel { Spacing = 12, Children =
            {
                unitCombo,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = Loc.T("Dlg_Width"), VerticalAlignment = VerticalAlignment.Center }, widthBox } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = Loc.T("Dlg_Height"), VerticalAlignment = VerticalAlignment.Center }, heightBox } },
                lockRatio,
            } },
            PrimaryButtonText = Loc.T("Dlg_Confirm"),
            CloseButtonText = Loc.T("T_Dlg_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        if (await ShowProjectDialogAsync(dialog) != ContentDialogResult.Primary) return;
        int width = Math.Max(1, (int)Math.Round(widthPixels));
        int height = Math.Max(1, (int)Math.Round(heightPixels));
        if (document.ResizeImage(width, height))
        {
            FitToWindow();
            SetStatus(Loc.F("S_Resized", width, height));
        }
        else
        {
            SetStatus(Loc.T("S_ShapeTooBig"));
        }
    }

    private async void OnCanvasSize(object sender, RoutedEventArgs e)
    {
        if (IsFileImporting) return;
        if (document is not { } owner) return;
        var manifest = owner.Snapshot.Manifest;
        var widthBox = new NumberBox { Value = manifest.Width, MinWidth = 120, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var heightBox = new NumberBox { Value = manifest.Height, MinWidth = 120, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        int anchor = 4; // Row-major 0–8, the macOS CanvasSizeOptions.anchor default: the middle.

        var anchorGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
        for (int column = 0; column < 3; column++) anchorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        for (int row = 0; row < 3; row++) anchorGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
        for (int index = 0; index < 9; index++)
        {
            var choice = new RadioButton
            {
                GroupName = "CanvasSizeAnchor",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsChecked = index == anchor,
                Content = new Border { Width = 10, Height = 10, Background = new SolidColorBrush(Microsoft.UI.Colors.Gray) },
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(choice, Loc.F("Dlg_AnchorPosition", index / 3 + 1, index % 3 + 1));
            int captured = index;
            choice.Checked += (_, _) => anchor = captured;
            Grid.SetRow(choice, index / 3);
            Grid.SetColumn(choice, index % 3);
            anchorGrid.Children.Add(choice);
        }

        var fillCombo = new ComboBox
        {
            Width = 180,
            ItemsSource = new[]
            {
                Loc.T("Dlg_FillTransparent"), Loc.T("Dlg_FillForeground"), Loc.T("Dlg_FillBackground"),
                Loc.T("Dlg_FillWhite"), Loc.T("Dlg_FillBlack"),
            },
            SelectedIndex = 0,
        };

        var dialog = new ContentDialog
        {
            Title = Loc.T("M_CanvasSize"),
            Content = new StackPanel { Spacing = 12, Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = Loc.T("Dlg_Width"), VerticalAlignment = VerticalAlignment.Center }, widthBox } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = Loc.T("Dlg_Height"), VerticalAlignment = VerticalAlignment.Center }, heightBox } },
                new TextBlock { Text = Loc.T("Dlg_Anchor") },
                anchorGrid,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = Loc.T("Dlg_ExtensionFill"), VerticalAlignment = VerticalAlignment.Center }, fillCombo } },
            } },
            PrimaryButtonText = Loc.T("Dlg_Confirm"),
            CloseButtonText = Loc.T("T_Dlg_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        if (await ShowProjectDialogAsync(dialog) != ContentDialogResult.Primary) return;
        int width = Math.Max(1, (int)Math.Round(widthBox.Value));
        int height = Math.Max(1, (int)Math.Round(heightBox.Value));
        (byte R, byte G, byte B)? fill = fillCombo.SelectedIndex switch
        {
            1 => (foregroundColor.R, foregroundColor.G, foregroundColor.B),
            2 => (backgroundColor.R, backgroundColor.G, backgroundColor.B),
            3 => (255, 255, 255),
            4 => (0, 0, 0),
            _ => null,
        };
        ApplyCanvasSize(width, height, anchor, fill);
    }

    /// <summary>
    /// The work the Canvas Size dialog's Confirm button does, split out so the workspace probe can
    /// drive the same path without building a dialog. The picker is the only thing left above it.
    /// </summary>
    private void ApplyCanvasSize(int width, int height, int anchor, (byte R, byte G, byte B)? extensionFill)
    {
        if (document is not { } owner) return;
        if (owner.SetCanvasSize(width, height, anchor, extensionFill))
        {
            FitToWindow();
            SetStatus(Loc.F("S_CanvasSized", width, height));
        }
        else
        {
            SetStatus(Loc.T("S_CanvasSizeRefused"));
        }
    }

    /// <summary>
    /// What an export writes and what it then says about itself. Split out because the picker a user answered
    /// cannot be driven from a probe, and the two claims worth keeping — that a PNG leaves with the document's
    /// resolution in hand, and that the status line reports what was actually put in the file — live here and
    /// nowhere else. The JPEG path carries quality and a matte and no resolution, as the original's does; the
    /// PNG path takes the manifest resolution, or plain WIC bytes when the project never had one.
    /// </summary>
    private static async Task<(byte[] Bytes, string Status)> ExportBytesAndStatusAsync(
        ProjectSnapshot snapshot, string fileName, bool jpeg, double quality, Windows.UI.Color matte)
    {
        var surface = await Task.Run(() => new CanvasCompositor(snapshot).Render());
        double? resolution = snapshot.Manifest.Resolution;
        byte[] bytes = jpeg
            ? await WicImaging.EncodeJpegAsync(surface, quality, matte.R, matte.G, matte.B)
            : await WicImaging.EncodePngForExportAsync(surface, resolution);
        string status = jpeg
            ? Loc.F("S_Exported", fileName, surface.Width, surface.Height, quality)
            : resolution is { } dpi
                ? Loc.F("S_ExportedPngDpi", fileName, surface.Width, surface.Height, dpi)
                : Loc.F("S_ExportedPng", fileName, surface.Width, surface.Height);
        return (bytes, status);
    }

    private async Task ExportAsync(bool jpeg)
    {
        if (IsFileImporting) return;
        var requestedOwner = document;
        await FinishTransformBeforeOutputAsync();
        if (windowClosed || !ReferenceEquals(document, requestedOwner)) return;
        if (!HealingAllowsOutput()) return;
        if (!await ApplyGradientDraftAsync() || windowClosed || !ReferenceEquals(document, requestedOwner)) return;
        if (document is null) return;
        double quality = lastJpegQuality;
        Windows.UI.Color matte = lastJpegMatte;
        if (jpeg)
        {
            var qualitySlider = new Slider { Minimum = 0, Maximum = 100, Value = lastJpegQuality, Width = 220 };
            var percent = new TextBlock { Text = $"{Math.Round(lastJpegQuality):0}%", VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
            qualitySlider.ValueChanged += (_, args) => percent.Text = $"{Math.Round(args.NewValue):0}%";
            var matteFill = new Rectangle { Width = 18, Height = 18, Fill = new SolidColorBrush(matte), Stroke = new SolidColorBrush(Microsoft.UI.Colors.Gray) };
            var matteChip = new Button
            {
                Content = matteFill,
                Padding = new Thickness(2),
                Tag = matteFill,
            };
            ToolTipService.SetToolTip(matteChip, Loc.T("L_MatteTip"));
            matteChip.Click += (sender, _) =>
            {
                Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase.ShowAttachedFlyout((FrameworkElement)sender);
            };
            Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase.SetAttachedFlyout(matteChip, new Flyout
            {
                Content = BuildMattePicker(matteFill),
            });
            var dialog = new ContentDialog
            {
                Title = Loc.T("Dlg_ExportJpeg"),
                Content = new StackPanel { Spacing = 12, Children =
                {
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = Loc.T("L_Quality"), VerticalAlignment = VerticalAlignment.Center }, qualitySlider, percent } },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = Loc.T("L_Matte"), VerticalAlignment = VerticalAlignment.Center }, matteChip } },
                } },
                PrimaryButtonText = Loc.T("Dlg_Export"),
                CloseButtonText = Loc.T("T_Dlg_Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot,
            };
            if (await ShowProjectDialogAsync(dialog) != ContentDialogResult.Primary) return;
            quality = qualitySlider.Value;
            if (matteFill.Fill is SolidColorBrush brush) matte = brush.Color;
            lastJpegQuality = quality;
            lastJpegMatte = matte;
        }
        var picker = new FileSavePicker
        {
            SettingsIdentifier = "imageExport",
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
        };
        if (jpeg)
        {
            picker.FileTypeChoices.Add(Loc.T("M_FileTypeJpeg"), new List<string> { ".jpg" });
            picker.SuggestedFileName = "export.jpg";
        }
        else
        {
            picker.FileTypeChoices.Add(Loc.T("M_FileTypePng"), new List<string> { ".png" });
            picker.SuggestedFileName = "export.png";
        }
        InitializeWithWindow(picker);
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        SetBusy(true);
        try
        {
            // The render is pure CPU work over an immutable snapshot — run it off the UI
            // thread; the encoding that follows stays in this async context.
            var (bytes, status) = await ExportBytesAndStatusAsync(
                document.Snapshot, System.IO.Path.GetFileName(file.Path), jpeg, quality, matte);
            await File.WriteAllBytesAsync(file.Path, bytes);
            SetStatus(status);
        }
        catch (Exception ex)
        {
            Report(Loc.F("S_CannotExport", file.Name), ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>The matte color's picker, updating the dialog chip as it moves.</summary>
    private ColorPicker BuildMattePicker(Rectangle fill)
    {
        var picker = new ColorPicker { Color = lastJpegMatte };
        picker.ColorChanged += (_, args) =>
        {
            fill.Fill = new SolidColorBrush(args.NewColor);
        };
        return picker;
    }

    // --- Text tool: dialog-based placement and re-editing; the Mac's on-canvas editor is separate. ---

    private bool textDialogOpen;

    /// <summary>The Text tool's options-strip command: the port's stand-in for the Mac's character panel.</summary>
    private async void OnTextDefaults(object sender, RoutedEventArgs args)
    {
        if (document is not { } owner || textDialogOpen || !CanEditLayerCommands) return;
        textDialogOpen = true;
        try
        {
            await ShowTextDialogAsync(owner, PointD.Zero, defaultsOnly: true);
        }
        finally
        {
            textDialogOpen = false;
        }
    }

    /// <summary>
    /// Gives a face to the letters the editor has selected, or to the whole text when nothing is selected — the
    /// macOS route through <c>previewFont</c> and <c>LayerTextStyle.setFont(_:in:)</c>. Selection and content are
    /// both UTF-16 units here, the same units the runs are written in, so no translation is needed. False when the
    /// style or the preview was refused, which leaves the draft exactly as it stood.
    /// </summary>
    /// <summary>
    /// What the macOS palette does while text is being typed: the picked color goes to the selected letters,
    /// or to the whole text when there is no selection (<c>ColorPalette.swift:40-112</c> through
    /// <c>changeTextStyle</c> and <c>LayerTextStyle.setColor(_:in:)</c>). Painting into a mask leaves the
    /// letters alone — that is the original's <c>isMaskSelected</c> branch, not an omission here.
    /// </summary>
    private bool ApplyPaletteColorToDraft()
    {
        if (document is not { } owner || owner.ActiveTextDraft is null || paintOnMask) return false;
        return ApplyDraftColor(owner, foregroundColor);
    }

    /// <summary>Applies a color to the draft the editor is showing, over its selection when it has one. The
    /// preview is the same rewrite every keystroke takes, so what the palette shows is what the commit leaves.</summary>
    private bool ApplyDraftColor(EditorDocument owner, Windows.UI.Color color)
    {
        if (owner.ActiveTextDraft is not { } draft) return false;
        var style = ManifestJson.Clone(draft.Style);
        style.SetColor(color.R / 255.0, color.G / 255.0, color.B / 255.0,
            inlineEditor?.SelectionStart ?? 0, inlineEditor?.SelectionLength ?? 0);
        if (!style.IsValid) return false;
        if (!owner.UpdateTextDraft(style)) return false;
        PlaceInlineText();
        return true;
    }

    private bool ApplyDraftFont(EditorDocument owner, string face)
    {
        if (owner.ActiveTextDraft is not { } draft) return false;
        var style = ManifestJson.Clone(draft.Style);
        // SetFont refuses a face the document would not take, so the host does not repeat that rule.
        style.SetFont(face, inlineEditor?.SelectionStart ?? 0, inlineEditor?.SelectionLength ?? 0);
        if (!style.IsValid) return false;
        if (!owner.UpdateTextDraft(style)) return false;
        PlaceInlineText();
        return true;
    }

    /// <summary>The installed face names for the text dialog, a static list built once.</summary>
    private static readonly string[] InstalledFontNames = BuildInstalledFontNames();

    private static string[] BuildInstalledFontNames()
    {
        try
        {
            return new System.Drawing.Text.InstalledFontCollection().Families
                .Select(family => family.Name)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            App.Log($"font list failed: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Places new text or edits the uppermost visible text under the pointer. New text takes
    /// the foreground color and puts its first baseline on the click; editing keeps its color.
    /// </summary>
    private async Task OpenTextDialogAsync(PointD point)
    {
        if (document is null || textDialogOpen) return;
        // Text that already exists is edited on the canvas, as in the original (InlineTextEditor.swift:519-524).
        // New text still comes through the dialog for now: the sheet collects font, size and colour before the
        // first glyph exists, and the inline host has no equivalent palette yet.
        if (document.TextLayerAt(point) is { } existing)
        {
            CommitInlineText();
            OpenInlineText(existing);
            return;
        }
        textDialogOpen = true;
        try
        {
            await ShowTextDialogAsync(document, point);
        }
        finally
        {
            textDialogOpen = false;
        }
    }

    /// <summary>
    /// The port's only font chooser. <paramref name="defaultsOnly"/> is the Text tool's own use of it — the Mac
    /// sets these from a character panel the port does not have yet — which collects the face, size, tracking,
    /// leading and alignment and hands them to the remembered defaults instead of laying down a layer.
    /// </summary>
    private async Task ShowTextDialogAsync(EditorDocument textDocument, PointD point, bool defaultsOnly = false)
    {
        var target = defaultsOnly ? null : textDocument.TextLayerAt(point);
        var existing = target?.Text;
        if (target is not null)
        {
            selectedLayerId = target.Id;
            textDocument.SetActiveLayer(target.Id);
        }
        var content = new TextBox { Text = existing?.Content ?? "", AcceptsReturn = true, MinHeight = 96, MinWidth = 320, MaxLength = 100_000 };
        var fontNames = InstalledFontNames.ToList();
        string preferred = existing?.FontName ?? "Segoe UI";
        if (!fontNames.Contains(preferred, StringComparer.OrdinalIgnoreCase)) fontNames.Insert(0, preferred);
        var fonts = new ComboBox { Width = 220, ItemsSource = fontNames, SelectedItem = fontNames.First(name => name.Equals(preferred, StringComparison.OrdinalIgnoreCase)) };
        var size = new NumberBox { Minimum = 1, Maximum = 2000, Value = existing?.FontSize ?? 72, Width = 110, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var tracking = new NumberBox { Minimum = -100, Maximum = 1000, Value = existing?.Tracking ?? 0, Width = 110, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var leading = new NumberBox { Minimum = 0, Maximum = 5000, Value = existing?.Leading ?? 0, Width = 110, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var alignment = new ComboBox
        {
            Width = 140,
            ItemsSource = new[] { Loc.T("L_AlignLeft"), Loc.T("L_AlignCenter"), Loc.T("L_AlignRight") },
            SelectedIndex = (int)(existing?.Alignment ?? global::Compositor.Core.TextAlignment.Left),
        };
        StackPanel textRow(string label, UIElement control)
        {
            var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            stack.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Width = 72 });
            stack.Children.Add(control);
            return stack;
        }
        var dialog = new ContentDialog
        {
            Title = Loc.T("M_TextDialog"),
            Content = new ScrollViewer
            {
                MaxHeight = 420,
                Content = new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        content,
                        textRow(Loc.T("L_TextFont"), fonts),
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 16,
                            Children =
                            {
                                textRow(Loc.T("L_TextSize"), size),
                                textRow(Loc.T("L_TextTracking"), tracking),
                                textRow(Loc.T("L_TextLeading"), leading),
                            },
                        },
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 16,
                            Children =
                            {
                                textRow(Loc.T("L_TextAlign"), alignment),
                            },
                        },
                    },
                },
            },
            PrimaryButtonText = Loc.T("Dlg_Confirm"),
            CloseButtonText = Loc.T("M_Close"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        if (await ShowProjectDialogAsync(dialog) != ContentDialogResult.Primary) return;
        if (!ReferenceEquals(document, textDocument)) return;
        string text = content.Text;
        if (!defaultsOnly && target is null && string.IsNullOrWhiteSpace(text)) return;
        string fontName = fonts.SelectedItem as string ?? preferred;
        var style = new LayerTextStyle
        {
            Content = text,
            FontName = fontName,
            FontSize = double.IsNaN(size.Value) ? 72 : Math.Clamp(size.Value, 1, 2000),
            Red = existing?.Red ?? foregroundColor.R / 255.0,
            Green = existing?.Green ?? foregroundColor.G / 255.0,
            Blue = existing?.Blue ?? foregroundColor.B / 255.0,
            Alignment = (Compositor.Core.TextAlignment)Math.Clamp(alignment.SelectedIndex, 0, 2),
            Tracking = double.IsNaN(tracking.Value) ? 0 : Math.Clamp(tracking.Value, -100, 1000),
            Leading = double.IsNaN(leading.Value) ? 0 : Math.Clamp(leading.Value, 0, 5000),
            BoxSize = existing?.BoxSize,
            ColorRuns = text == existing?.Content ? existing.ColorRuns : null,
            FontRuns = text == existing?.Content && fontName == existing.FontName ? existing.FontRuns : null,
        };
        if (defaultsOnly)
        {
            var remembered = ManifestJson.Clone(style);
            remembered.ColorRuns = null;
            remembered.FontRuns = null;
            remembered.BoxSize = null; // the remembered defaults carry a face and a size, not a box of their own
            textDefaults = remembered;
            // Editing the settings of text that is being typed edits that text, as the Mac's palette does.
            if (textDocument.ActiveTextDraft is { } typing)
            {
                var applied = ManifestJson.Clone(remembered);
                applied.Content = typing.Style.Content;
                applied.BoxSize = typing.Style.BoxSize;
                applied.Red = typing.Style.Red;
                applied.Green = typing.Style.Green;
                applied.Blue = typing.Style.Blue;
                applied.FontName = typing.Style.FontName;
                applied.FontRuns = typing.Style.FontRuns;
                if (!textDocument.UpdateTextDraft(applied)) Report(Loc.T("S_TextRefused"), null);
                // The face is the one setting a text style carries per letter, so it is the one that follows a
                // selection instead of retypes the whole layer (the Mac's previewFont → setFont(in:)).
                else if (!ApplyDraftFont(textDocument, fontName)) Report(Loc.T("S_TextRefused"), null);
            }
            SetStatus(Loc.T("S_TextDefaultsSet"));
            return;
        }
        if (target is not null)
        {
            if (textDocument.UpdateTextLayer(target.Id, style)) SetStatus(Loc.F("S_TextUpdated", target.Name));
            else Report(Loc.T("S_TextRefused"), null);
            return;
        }
        ProjectLayerRecord? layer = null;
        try
        {
            double baseline = TextRasterizer.FirstBaseline(style);
            var origin = new PointD(point.X - LayerTextStyle.Padding, point.Y - baseline);
            layer = textDocument.AddTextLayer(style, origin);
        }
        catch (ProjectFormatException)
        {
            layer = null; // refused below, like an over-budget shape
        }
        if (layer is null) Report(Loc.T("S_TextRefused"), null);
        else SetStatus(Loc.F("S_TextAdded", layer.Name));
    }
}
