#if DEBUG
using System.Text;
using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Compositor.App;

public sealed partial class MainWindow
{
    /// <summary>The sheet View ▸ Grid Settings… has open right now. A probe drives its fields through their own
    /// change handlers, which is the same code a keystroke runs; production never reads this back.</summary>
    private GridSettingsForm? gridSettingsSheetProbe;

    /// <summary>
    /// Device pixels the viewport pass draws at a document point, read out of the same offscreen target the
    /// C10d group uses: the grid is a view overlay, so this is the only way to see what a preview did.
    /// </summary>
    private byte[] GridPatch(int documentX, int documentY, int radius, int tallRadius = -1)
    {
        if (tallRadius < 0) tallRadius = radius;
        var view = viewport.ToView(new PointD(documentX, documentY));
        using var target = new Microsoft.Graphics.Canvas.CanvasRenderTarget(Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice(),
            (float)viewport.Width, (float)viewport.Height, (float)(96 * viewport.BackingScale));
        using (var drawing = target.CreateDrawingSession())
        {
            drawing.Clear(Microsoft.UI.ColorHelper.FromArgb(255, 27, 27, 27));
            DrawViewportContent(drawing);
        }
        int x = Math.Max(0, (int)Math.Round(view.X * viewport.BackingScale) - radius);
        int y = Math.Max(0, (int)Math.Round(view.Y * viewport.BackingScale) - tallRadius);
        return target.GetPixelBytes(x, y, radius * 2 + 1, tallRadius * 2 + 1);
    }

    /// <summary>The same high/low row profile as the C10d group, for the one claim the style picker makes: a
    /// Dashed or Dots major line has gaps, a Lines one does not. Both patches are the same document point.</summary>
    private static string GridRows(byte[] on, byte[] off, int width)
    {
        int rows = on.Length / (width * 4), max = 0;
        var delta = new int[rows];
        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < width; column++)
            {
                int start = (row * width + column) * 4;
                for (int channel = 0; channel < 3; channel++)
                    delta[row] = Math.Max(delta[row], Math.Abs(on[start + channel] - off[start + channel]));
            }
            max = Math.Max(max, delta[row]);
        }
        if (max == 0) return new string('.', rows);
        var map = new StringBuilder();
        foreach (var d in delta) map.Append(d * 2 > max ? '#' : '.');
        return map.ToString();
    }

    /// <summary>
    /// C10e: View ▸ Grid Settings…. Everything is driven through the sheet's own fields, and every claim is one
    /// the sheet has to keep true at the same time: the canvas shows what the fields hold before OK, Cancel gives
    /// back what the sheet opened with and writes nothing, and OK is the only way into the files — which are then
    /// read back from disk, not from memory (GridSettingsSheet.swift:97-123).
    /// </summary>
    private async Task VerifyGridSettingsAsync(Dictionary<string, object> report)
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"compositor-grid-settings-probe-{Environment.ProcessId}");
        var previous = Environment.GetEnvironmentVariable("COMPOSITOR_PREFERENCES_DIRECTORY");
        var wasGrid = showsGrid; var wasZoom = viewport.Zoom;
        var scene = EditorDocument.Create(128, 96);
        Directory.CreateDirectory(scratch);
        Environment.SetEnvironmentVariable("COMPOSITOR_PREFERENCES_DIRECTORY", scratch);
        OpenDocument(scene);
        await WaitForFramesAsync();
        try
        {
            // The group owns its fixture. Writing the Mac's defaults straight into the scratch files — rather
            // than through the code path under test — keeps "OK is the only writer" a claim the later assertions
            // can attribute to OK alone, and the previous grid group leaves the running numbers wherever its own
            // reload test put them.
            foreach (var (key, value) in new (string, string)[]
                     {
                         ("show-grid", "true"), ("grid-spacing", "64"), ("grid-subdivisions", "8"),
                         ("grid-color", "Light Gray"), ("grid-custom-color", "#B3B3B3"),
                         ("grid-style", "Lines"), ("grid-opacity", "45"),
                     })
                File.WriteAllText(Path.Combine(scratch, key + ".txt"), value);
            ApplyViewVisibilityOptions();
            SetViewportZoom(2); await WaitForFramesAsync();
            // (64, 44) sits on a solid major, (8, 44) on a dotted subdivision, and 19 device rows at this zoom
            // stay clear of any horizontal line, which is what the row profile needs.
            Require(showsGrid && LayoutGrid.Step == 8 && GridAppearance.Opacity == 45
                    && GridAppearance.ColorPreset == GridAppearance.Preset.LightGray,
                "the fixture did not put the Mac's defaults on the canvas");
            // Held as the yardstick for "Cancel gives it back": the color the file round-tripped is the one the
            // session has, and a hex string cannot carry 0.7 exactly (the Mac loses the same fraction).
            var opened = GridAppearance;
            GridToggle.IsChecked = false; OnToggleShowGrid(GridToggle, new RoutedEventArgs()); await WaitForFramesAsync();
            var nothingAt8 = GridPatch(8, 44, 2); var nothingStrip = GridPatch(64, 44, 1, 9);
            var hiddenAt32 = GridPatch(32, 44, 2); var hiddenAt64 = GridPatch(64, 44, 2);
            GridToggle.IsChecked = true; OnToggleShowGrid(GridToggle, new RoutedEventArgs()); await WaitForFramesAsync();
            Require(!GridPatch(8, 44, 2).AsSpan().SequenceEqual(nothingAt8), "no subdivision showed at x=8, so the patches cannot tell anything apart");
            var solidRows = GridRows(GridPatch(64, 44, 1, 9), nothingStrip, 3);
            report["gridMajorLinesRows"] = solidRows;
            Require(!solidRows.Contains('.'), $"the default Lines style drew gaps in a major line ({solidRows}).");
            var visibleAt64 = GridPatch(64, 44, 2);   // the default grid showing, for the comparison after Cancel

            // Cancel, tested from a hidden grid so the sheet's own visibility is part of the claim: the Mac
            // turns the grid on for the duration of the sheet and puts the switch back when it closes
            // (ProjectController.swift:136-137, :155).
            byte[] previewed = Array.Empty<byte>();
            byte[] shownWhileOpen = Array.Empty<byte>();
            string? spacingFileDuringPreview = null;
            bool openedOnTheRunningValues = false;
            var sheetInventory = "the sheet was never swept";
            var sheetBare = -1;
            var sheetSwept = -1;
            var sheetMissing = "";
            // A missing file is a value of its own here: "OK never wrote it" has to be reported by the
            // assertion, not by a FileNotFoundException out of the read.
            string? Read(string name) => File.Exists(Path.Combine(scratch, name + ".txt"))
                ? File.ReadAllText(Path.Combine(scratch, name + ".txt")).Trim() : null;
            GridToggle.IsChecked = false; OnToggleShowGrid(GridToggle, new RoutedEventArgs()); await WaitForFramesAsync();
            projectDialogProbe = async dialog =>
            {
                var sheet = gridSettingsSheetProbe ?? throw new InvalidOperationException("Grid Settings opened without a sheet to drive.");
                openedOnTheRunningValues = sheet.Current.Grid.Equals(LayoutGrid) && sheet.Current.Appearance.Equals(GridAppearance);
                shownWhileOpen = GridPatch(32, 44, 2);          // nothing was showing before the sheet opened
                sheet.SpacingField.Value = 128;                 // majors every 128, so a line every 16: x=8 is off the grid
                await WaitForFramesAsync();
                previewed = GridPatch(8, 44, 2);
                // Read here rather than after the dialog closes: Cancel puts the numbers back, so a preview that
                // wrote files itself would be cleaned up by the very path the next assertions test.
                spacingFileDuringPreview = Read("grid-spacing");
                // C13e: counted through the dialog's own content, and only while it is open — closing tears it
                // down. Walking from the dialog reported controls=0, which is a measurement failure and not a
                // clean sheet: a ContentDialog hangs its content in a popup, so the tree under the dialog object
                // is not the tree the reader sees. Content is the panel the form built, and it is there.
                sheetInventory = dialog.Content is DependencyObject content
                    ? AccessibilityInventory(content) : "the sheet's content was never attached";
                if (dialog.Content is DependencyObject swept)
                {
                    var sheetSweep = AccessibleControls(swept);
                    sheetBare = sheetSweep.Unnamed.Count;
                    sheetSwept = sheetSweep.Named + sheetSweep.Unnamed.Count;
                    sheetMissing = string.Join(", ", sheetSweep.Unnamed);
                }
                return ContentDialogResult.None;                // the Cancel button
            };
            await ShowGridSettingsAsync();
            Require(openedOnTheRunningValues, "the sheet did not open holding what the window is drawing");
            Require(!shownWhileOpen.AsSpan().SequenceEqual(hiddenAt32),
                "the sheet never made the grid visible, so its preview had nowhere to show");
            Require(previewed.AsSpan().SequenceEqual(nothingAt8),
                "the previewed 128-pixel spacing kept drawing a line at x=8 where its new step has none");
            Require(spacingFileDuringPreview == "64",
                $"the preview wrote the spacing file before OK ever happened ({spacingFileDuringPreview})");
            Require(LayoutGrid.Equals(new LayoutGrid(64, 8)) && GridAppearance.Equals(opened),
                "Cancel left the previewed spacing in the running session");
            Require(Read("grid-spacing") == "64", "Cancel wrote the previewed spacing over the fixture's own file");
            Require(!showsGrid && GridToggle.IsChecked == false, "the sheet left the grid switched on after it closed");
            Require(GridPatch(64, 44, 2).AsSpan().SequenceEqual(hiddenAt64), "the grid was still drawing after the sheet went away");
            // Back to visible for the two groups below, where the default line has to be exactly what it was.
            GridToggle.IsChecked = true; OnToggleShowGrid(GridToggle, new RoutedEventArgs()); await WaitForFramesAsync();
            Require(GridPatch(64, 44, 2).AsSpan().SequenceEqual(visibleAt64),
                "Cancel did not put the original major line back on the canvas");
            report["gridSettingsPreviewAndCancel"] = "passed";
            report["gridSheetAccessibility"] = sheetInventory;
            // Asked outside the dialog's own callback on purpose: a throw inside it is posted to the dispatcher
            // and never reaches this probe, so the failure would surface as some later group's message instead.
            Require(sheetBare == 0,
                $"the Grid Settings sheet builds {sheetBare} controls that report no name to a reader: [{sheetMissing}]");
            // The same claim would also be true of a sweep that found nothing: an empty tree has no unnamed
            // control either. Six is the sheet's own row count — preset, style, opacity, spacing, subdivisions,
            // and the custom-colour chip — so this half proves the walk was over the sheet.
            Require(sheetSwept >= 6,
                $"the sweep never walked the sheet: it counted {sheetSwept} controls where the form builds 6");
            report["gridSheetControlsAreNamed"] = "passed";

            // OK: the same four fields, kept, and five files behind them.
            projectDialogProbe = async dialog =>
            {
                var sheet = gridSettingsSheetProbe!;
                sheet.SpacingField.Value = 32;
                sheet.OpacityField.Value = 90;
                sheet.PresetField.SelectedIndex = (int)GridAppearance.Preset.Cyan;
                sheet.StyleField.SelectedIndex = (int)GridAppearance.LineStyle.Dots;
                await WaitForFramesAsync();
                return ContentDialogResult.Primary;  // OK
            };
            await ShowGridSettingsAsync();
            Require(LayoutGrid.Spacing == 32 && LayoutGrid.Step == 4 && GridAppearance.Opacity == 90
                    && GridAppearance.ColorPreset == GridAppearance.Preset.Cyan
                    && GridAppearance.Style == GridAppearance.LineStyle.Dots,
                "OK did not keep the sheet's values in the running session");
            Require(Read("grid-spacing") == "32" && Read("grid-subdivisions") == "8"
                    && Read("grid-color") == "Cyan" && Read("grid-style") == "Dots" && Read("grid-opacity") == "90",
                "OK did not write the sheet's values to their files");
            // Dots has to be a rhythm in the pixels, not just a stored word: at 32-pixel spacing x=32 is the
            // major, and the same point with the grid hidden is the baseline.
            GridToggle.IsChecked = false; OnToggleShowGrid(GridToggle, new RoutedEventArgs()); await WaitForFramesAsync();
            var nothingAt32 = GridPatch(32, 44, 1, 9);
            GridToggle.IsChecked = true; OnToggleShowGrid(GridToggle, new RoutedEventArgs()); await WaitForFramesAsync();
            var dottedRows = GridRows(GridPatch(32, 44, 1, 9), nothingAt32, 3);
            report["gridMajorDotsRows"] = dottedRows;
            Require(dottedRows.Contains('.') && dottedRows.Contains('#'), $"the Dots style drew no rhythm in the major line ({dottedRows}).");
            // Forced to the opposite of the files, so only a real read can bring them back.
            showsGrid = false; gridSpacing = 64; gridSubdivisions = 8; GridAppearance = GridAppearance.Default;
            ApplyViewVisibilityOptions();
            Require(showsGrid && gridSpacing == 32 && GridAppearance.Opacity == 90
                    && GridAppearance.ColorPreset == GridAppearance.Preset.Cyan
                    && GridAppearance.Style == GridAppearance.LineStyle.Dots && LayoutGrid.Step == 4,
                "the grid settings did not come back from disk");
            report["gridSettingsOkWritesAndReloads"] = "passed";

            // Validity, Restore Defaults, and the Custom color: the three things the sheet decides for itself.
            string rangeHint = Loc.T("S_GridRangeHint");
            projectDialogProbe = async dialog =>
            {
                var sheet = gridSettingsSheetProbe!;
                sheet.SpacingField.Value = 2;
                sheet.SubdivisionsField.Value = 8;
                await WaitForFramesAsync();
                Require(!sheet.OkEnabled, "OK stayed enabled with 8 parts in 2 pixels");
                Require(sheet.CaptionText == rangeHint, $"the sheet explained the wrong problem ({sheet.CaptionText}).");
                sheet.RestoreDefaults();
                Require(sheet.OkEnabled && sheet.Current.Grid.Equals(LayoutGrid.Default)
                        && sheet.Current.Appearance.Equals(GridAppearance.Default),
                    "Restore Defaults did not put the Mac's opening values back");
                sheet.PickCustomColor(Microsoft.UI.ColorHelper.FromArgb(255, 10, 200, 30));
                Require(sheet.Current.Appearance.ColorPreset == GridAppearance.Preset.Custom
                        && sheet.CustomColorHeld.Red < .04 && sheet.CustomColorHeld.Green > .78,
                    "the swatch did not move the sheet to its Custom color");
                Require(sheet.PresetField.SelectedIndex == (int)GridAppearance.Preset.Custom,
                    "the swatch changed the color while the preset picker still showed the old one");
                await WaitForFramesAsync();
                return ContentDialogResult.Primary;
            };
            await ShowGridSettingsAsync();
            Require(GridAppearance.ColorPreset == GridAppearance.Preset.Custom && LayoutGrid.Spacing == 64
                    && GridAppearance.Opacity == 45,
                "the restored defaults and the custom pick were not what OK kept");
            // The picked color has to be the one on the canvas: the same major column at the same zoom and the
            // same document, with only the sheet's color changed since Cancel put the gray back.
            Require(!GridPatch(64, 44, 2).AsSpan().SequenceEqual(visibleAt64),
                "the Custom color never reached the canvas, only the files");
            Require(Read("grid-color") == "Custom" && Read("grid-custom-color") == "#0AC81E"
                    && Read("grid-spacing") == "64" && Read("grid-opacity") == "45",
                "OK did not persist the custom color or the restored defaults");
            GridAppearance = GridAppearance.Default; gridSpacing = 128; showsGrid = false;
            ApplyViewVisibilityOptions();
            Require(GridAppearance.ColorPreset == GridAppearance.Preset.Custom
                    && GridAppearance.CustomColor.Equals(new AdjustmentColor(10 / 255.0, 200 / 255.0, 30 / 255.0))
                    && gridSpacing == 64 && showsGrid,
                "the custom color did not survive a read from disk");
            report["gridSettingsValidityRestoreAndCustomColor"] = "passed";
        }
        finally
        {
            projectDialogProbe = null;
            gridSettingsSheetProbe = null;
            Environment.SetEnvironmentVariable("COMPOSITOR_PREFERENCES_DIRECTORY", previous);
            showsGrid = wasGrid; GridToggle.IsChecked = wasGrid;
            LayoutGrid = LayoutGrid.Default; GridAppearance = GridAppearance.Default;
            gridSpacing = 64; gridSubdivisions = 8;
            snapXs = snapYs = null;
            SetViewportZoom(wasZoom);
            OpenDocument(EditorDocument.Create(64, 48));
            try { Directory.Delete(scratch, recursive: true); }
            catch (Exception ex) { App.Log($"Grid settings probe cleanup failed for {scratch}: {ex.Message}"); }
        }
    }
}
#endif
