using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private ComboBox quickTextFont = null!;
    private NumberBox quickTextSize = null!;
    private ColorPicker quickTextColor = null!;
    private Button quickTextColorButton = null!;
    private readonly List<ToggleButton> quickTextAlign = [];
    private bool quickTextSync;
    private (EditorDocument Owner, Guid Draft)? quickColorDraft;

    private void InitializeQuickTextControls()
    {
        quickTextFont = NameQuickControl(new ComboBox { Width = 170, ItemsSource = InstalledFontNames }, "QuickTextFont", "QT_TextFont");
        quickTextSize = QuickNumber("QuickTextSize", "QT_TextSize", 1, 2000, 72);
        quickTextColor = NameQuickControl(new ColorPicker { IsAlphaEnabled = false }, "QuickTextColorPicker", "QT_TextColor");
        quickTextColorButton = QuickButton("QuickTextColor", "QT_TextColor", () => { });
        quickTextColorButton.Width = 36; quickTextColorButton.Padding = new(6);
        var flyout = new Flyout { Content = quickTextColor }; quickTextColorButton.Flyout = flyout;
        flyout.Opening += (_, _) =>
        {
            if (document is { ActiveTextDraft: null } owner && SelectedLayer is { Text: not null } layer
                && CanEditSelectionState && cropRectangle is null && owner.BeginTextDraft(layer.Id, layer.Transform.Origin, layer.Transform, layer.Text))
                quickColorDraft = (owner, owner.ActiveTextDraft!.Id);
        };
        flyout.Closed += (_, _) =>
        {
            var pending = quickColorDraft; quickColorDraft = null;
            if (pending is { } color && ReferenceEquals(document, color.Owner) && color.Owner.ActiveTextDraft?.Id == color.Draft) CommitInlineText();
        };
        quickTextFont.SelectionChanged += (_, _) =>
        {
            if (!quickTextSync && quickTextFont.SelectedItem is string font) ApplyQuickTextStyle(style =>
            { var range = QuickTextRange(); style.SetFont(font, range.Start, range.Length); });
        };
        quickTextSize.ValueChanged += (_, args) =>
        {
            if (!quickTextSync && double.IsFinite(args.NewValue)) ApplyQuickTextStyle(style => style.FontSize = args.NewValue);
        };
        quickTextColor.ColorChanged += (_, args) =>
        {
            if (quickTextSync) return;
            var color = args.NewColor;
            if (ApplyQuickTextStyle(style => { var range = QuickTextRange(); style.SetColor(color.R / 255d, color.G / 255d, color.B / 255d, range.Start, range.Length); }))
            { foregroundColor = color; RefreshPaletteChips(); }
        };
        TextControls.Children.Insert(0, quickTextFont); TextControls.Children.Insert(1, quickTextSize); TextControls.Children.Insert(2, quickTextColorButton);
        foreach (var (name, label, text) in new[] { ("QuickTextLeft", "QT_TextLeft", "≡"), ("QuickTextCenter", "QT_TextCenter", "≡"), ("QuickTextRight", "QT_TextRight", "≡") })
        {
            int index = quickTextAlign.Count;
            var button = NameQuickControl(new ToggleButton { Width = 32, Height = 32, MinWidth = 0, Padding = new(0), Content = text }, name, label);
            // Vary the line lengths and anchoring so the three icons remain distinct without text labels.
            var lines = new StackPanel { Spacing = 3, Width = 18, HorizontalAlignment = HorizontalAlignment.Center };
            for (int row = 0; row < 3; row++) lines.Children.Add(new Border { Height = 1, Width = row == 1 ? 10 : 18,
                HorizontalAlignment = (HorizontalAlignment)index, Background = new SolidColorBrush(Microsoft.UI.Colors.White) });
            button.Content = lines;
            void Change(object sender, RoutedEventArgs args)
            {
                if (quickTextSync) return;
                if (button.IsChecked == true) ApplyQuickTextStyle(style => style.Alignment = (global::Compositor.Core.TextAlignment)index);
                else RefreshQuickTextControls();
            }
            button.Checked += Change; button.Unchecked += Change; quickTextAlign.Add(button);
            TextControls.Children.Insert(TextControls.Children.Count - 1, button);
        }
        TextDefaultsButton.Content = Loc.T("QT_TextMore");
    }

    private (int Start, int Length) QuickTextRange() => document?.ActiveTextDraft is { } draft
        ? inlineEditor is { } editor ? (editor.SelectionStart, editor.SelectionLength) : draft.Selection : (0, 0);

    private bool ApplyQuickTextStyle(Action<LayerTextStyle> change)
    {
        if (quickTextSync || document is not { } owner || !CanEditSelectionState) return false;
        var layer = selectedLayerIds.Count <= 1 ? SelectedLayer : null;
        var current = owner.ActiveTextDraft?.Style ?? layer?.Text ?? textDefaults;
        var style = ManifestJson.Clone(current); change(style);
        if (!style.IsValid) { RefreshQuickTextControls(); return false; }
        bool accepted = true;
        quickTextSync = true;
        try
        {
            if (owner.ActiveTextDraft is not null) { accepted = owner.UpdateTextDraft(style); if (accepted) PlaceInlineText(); }
            else if (layer?.Text is not null) accepted = owner.UpdateTextLayer(layer.Id, style, layer.Transform);
            else { style.ColorRuns = null; style.FontRuns = null; textDefaults = style; }
        }
        finally { quickTextSync = false; }
        RefreshQuickTextControls(); return accepted;
    }

    private void RefreshQuickTextControls()
    {
        if (quickTextSync || quickTextFont is null) return;
        var style = document?.ActiveTextDraft?.Style ?? (selectedLayerIds.Count <= 1 ? SelectedLayer?.Text : null) ?? textDefaults;
        var range = QuickTextRange(); int at = Math.Min(range.Start, Math.Max(0, style.Content.Length - 1));
        var color = document?.ActiveTextDraft is not null || SelectedLayer?.Text is not null
            ? style.ColorAt(at) : (foregroundColor.R / 255d, foregroundColor.G / 255d, foregroundColor.B / 255d);
        var displayColor = Windows.UI.Color.FromArgb(255, (byte)Math.Round(color.Item1 * 255), (byte)Math.Round(color.Item2 * 255), (byte)Math.Round(color.Item3 * 255));
        quickTextSync = true;
        try
        {
            string face = style.FontNameAt(at); quickTextFont.SelectedItem = InstalledFontNames.Contains(face) ? face : null; quickTextFont.PlaceholderText = face;
            quickTextSize.Value = style.FontSize; quickTextColor.Color = displayColor;
            quickTextColorButton.Content = new Border { Width = 22, Height = 16, Background = new SolidColorBrush(displayColor), BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray), BorderThickness = new(1) };
            for (int i = 0; i < quickTextAlign.Count; i++) quickTextAlign[i].IsChecked = (int)style.Alignment == i;
            bool enabled = document is not null && CanEditSelectionState && selectedLayerIds.Count <= 1
                && (SelectedLayer is not { Text: not null } selectedText || document.CanEditLayer(selectedText.Id, LayerLocks.Pixels | LayerLocks.Transparency));
            quickTextFont.IsEnabled = quickTextSize.IsEnabled = quickTextColorButton.IsEnabled = enabled;
            foreach (var button in quickTextAlign) button.IsEnabled = enabled;
        }
        finally { quickTextSync = false; }
    }
}
