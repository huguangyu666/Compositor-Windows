using Compositor.Core;
using Compositor.Core.Psd;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Compositor.App;

/// <summary>
/// The PSD conversion sheet: the Mac reads a layered file, puts the list of converted features
/// up, and applies nothing until the user continues — Cancel skips that one file and the import
/// moves on to the next. The port collects the same lines in Core (EditorDocument.CollectPsdConversions)
/// during the background decode, then gates the batch on this sheet before the layers are added.
/// A file with nothing to report never shows it, exactly like the Mac.
/// </summary>
public sealed partial class MainWindow
{
    private async Task<bool> ConfirmPsdConversionsAsync(string fileName, IReadOnlyList<PsdConversion> conversions)
    {
        var rows = new StackPanel { Spacing = 6 };
        rows.Children.Add(new TextBlock
        {
            Text = Loc.T("PsdConversion_Intro"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72,
        });
        foreach (var line in conversions)
        {
            rows.Children.Add(new TextBlock { Text = line.LayerName, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            rows.Children.Add(new TextBlock { Text = line.Message, TextWrapping = TextWrapping.Wrap });
        }
        var dialog = new ContentDialog
        {
            Title = Loc.F("PsdConversion_Title", fileName),
            Content = new ScrollViewer { MaxHeight = 420, Content = rows },
            PrimaryButtonText = Loc.T("PsdConversion_Import"),
            CloseButtonText = Loc.T("Dlg_SheetCancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        return await ShowProjectDialogAsync(dialog) == ContentDialogResult.Primary;
    }
}
