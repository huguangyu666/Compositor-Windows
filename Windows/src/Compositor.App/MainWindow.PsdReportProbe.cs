#if DEBUG
using Compositor.Core;
using Compositor.Core.Psd;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Compositor.App;

public sealed partial class MainWindow
{
    /// <summary>
    /// The PSD conversion sheet in the app's own import route: a supported file opens nothing, a
    /// file with lines to report shows them before anything applies, and Cancel skips exactly that
    /// file while the batch moves on. The dialog is captured through the probe seam, so every
    /// assertion runs against the real ContentDialog the user would see, not a parallel fake.
    /// </summary>
    private async Task VerifyPsdConversionSheetAsync(Dictionary<string, object> report)
    {
        var directory = Path.Combine(Path.GetTempPath(), "compositor-psd-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string cleanPath = Path.Combine(directory, "Clean.psd"), reportPath = Path.Combine(directory, "Report.psd");
            await File.WriteAllBytesAsync(cleanPath, FileImportBlendPsd("mul "));   // supported: nothing to report
            await File.WriteAllBytesAsync(reportPath, FileImportBlendPsd("zzzz")); // unsupported: one blend line

            // A file with no conversion lines must never open the sheet.
            var scene = EditorDocument.Create(4, 4); OpenDocument(scene); await WaitForFramesAsync();
            bool opened = false;
            projectDialogProbe = _ => { opened = true; return Task.FromResult(ContentDialogResult.Primary); };
            var result = await ImportFilesAsync(scene, [cleanPath]); await WaitForFramesAsync();
            Require(!opened && result.ImportedFiles == 1 && scene.LayerCount == 3, "A silent PSD import still opened the conversion sheet.");
            report["psdConversionSheetStaysClosedWithoutLines"] = "passed";

            // Lines to report: the sheet names the file, shows the layer and the message with the
            // intro, and only after the answer does the batch publish the layers. The chrome texts
            // come from the same resource keys the dialog reads, so the check holds in any UI language.
            scene = EditorDocument.Create(4, 4); OpenDocument(scene); await WaitForFramesAsync();
            ContentDialog? shown = null;
            projectDialogProbe = dialog => { shown = dialog; return Task.FromResult(ContentDialogResult.Primary); };
            result = await ImportFilesAsync(scene, [reportPath]); await WaitForFramesAsync();
            var text = FlattenDialogText(shown);
            Require(shown is not null && result.ImportedFiles == 1 && scene.LayerCount == 3
                && text.Contains("Report.psd", StringComparison.Ordinal)
                && text.Contains(Loc.T("PsdConversion_Intro"), StringComparison.Ordinal)
                && text.Contains("Blend", StringComparison.Ordinal)
                && text.Contains("Blend mode", StringComparison.Ordinal) && text.Contains("zzzz", StringComparison.Ordinal)
                && text.Contains(Loc.T("PsdConversion_Import"), StringComparison.Ordinal)
                && text.Contains(Loc.T("Dlg_SheetCancel"), StringComparison.Ordinal),
                $"conversion sheet did not report the pending import: {text}");
            report["psdConversionSheetListsLinesBeforeApply"] = "passed";

            // Cancel skips exactly this file: nothing imported, the document untouched, no step to undo.
            scene = EditorDocument.Create(4, 4); OpenDocument(scene); await WaitForFramesAsync();
            projectDialogProbe = _ => Task.FromResult(ContentDialogResult.None);
            result = await ImportFilesAsync(scene, [reportPath]); await WaitForFramesAsync();
            Require(result.ImportedFiles == 0 && result.Failures.Count == 0 && scene.LayerCount == 1 && !scene.CanUndo,
                "Cancelling the conversion sheet applied the file anyway.");
            // The skip is the gate's decision and not a dead path: the same file lands once continued.
            projectDialogProbe = _ => Task.FromResult(ContentDialogResult.Primary);
            result = await ImportFilesAsync(scene, [reportPath]); await WaitForFramesAsync();
            Require(result.ImportedFiles == 1 && scene.LayerCount == 3,
                "the same file behaved differently once the sheet was continued.");
            report["psdConversionSheetCancelSkipsFile"] = "passed";

            // A batch keeps going past a cancelled file: the second lands, the first stays out.
            scene = EditorDocument.Create(4, 4); OpenDocument(scene); await WaitForFramesAsync();
            int prompts = 0;
            projectDialogProbe = _ => { prompts++; return Task.FromResult(prompts == 1 ? ContentDialogResult.None : ContentDialogResult.Primary); };
            result = await ImportFilesAsync(scene, [reportPath, cleanPath]); await WaitForFramesAsync();
            Require(prompts == 1 && result.ImportedFiles == 1
                && !scene.Snapshot.Manifest.Layers.Any(layer => layer.Name == "Report")
                && scene.Snapshot.Manifest.Layers.Any(layer => layer.Name == "Clean"),
                "a cancelled file blocked the rest of the batch.");
            report["psdConversionSheetCancelContinuesBatch"] = "passed";
        }
        finally { projectDialogProbe = null; Directory.Delete(directory, true); }
    }

    private static string FlattenDialogText(ContentDialog? dialog) => dialog is null ? ""
        : string.Join(" ", new[] { dialog.Title?.ToString() ?? "", dialog.Content is ScrollViewer { Content: StackPanel rows }
            ? string.Join(" ", rows.Children.OfType<Microsoft.UI.Xaml.Controls.TextBlock>().Select(block => block.Text))
            : dialog.Content?.ToString() ?? "", dialog.PrimaryButtonText, dialog.CloseButtonText });
}
#endif
