#if DEBUG
using System.IO;
using Compositor.Core;
using Microsoft.UI.Xaml;

namespace Compositor.App;

/// <summary>
/// Filter ▸ Motion Blur, measured against the Mac (Filters.swift:206-212, FilterSheet.swift:90-92):
/// the streak runs ALONG its angle (a dot becomes a line, not a blob), needs room the distance gives it
/// (half the streak plus two, Filters.swift:387), keeps the selection outside untouched on the grown
/// grid, and its sheet both refuses a tab switch (canSwitch) and settles itself silently when the
/// window closes (settlePendingEdits).
/// </summary>
public sealed partial class MainWindow
{
    private async Task VerifyMotionBlurFilterAsync(Dictionary<string, object> report)
    {
        var directory = Path.Combine(Path.GetTempPath(), "compositor-motion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            // An opaque black layer with one white dot near the left edge: the streak's direction is
            // readable off it, and its reach past the dot lands in the padding a blur has to grow.
            static EditorDocument DotScene(int side = 60, int x = 5, int y = 30)
            {
                var scene = EditorDocument.Create(side, side);
                var image = scene.Snapshot.Images[scene.Snapshot.Manifest.ActiveLayerId!.Value]!;
                for (int i = 0; i < image.Pixels.Length; i += 4)
                {
                    image.Pixels[i] = image.Pixels[i + 1] = image.Pixels[i + 2] = 0;
                    image.Pixels[i + 3] = 255;
                }
                int at = y * image.Stride + x * 4;
                for (int c = 0; c < 4; c++) image.Pixels[at + c] = 255;
                return scene;
            }
            int Bright(Surface image, int x, int y) => image.Pixels[y * image.Stride + x * 4];

            // --- A horizontal angle makes a horizontal line, on the grown grid, and commits back ---
            var scene = DotScene();
            OpenDocument(scene); await WaitForFramesAsync();
            var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
            var before = scene.Snapshot.Images[id]!.Clone();
            Surface Current() => scene.Snapshot.Images[id]!;

            OnMotionBlurFilter(this, new RoutedEventArgs());
            Require(FloatingPanelIsOpen("motionBlur"), "the Motion Blur command opened no panel.");
            await WaitForFramesAsync(); RootGrid.UpdateLayout();
            var body = FloatingPanelBody("motionBlur")!;
            Require(SliderNamed(body, Loc.T("L_Angle")) is not null && SliderNamed(body, Loc.T("L_Distance")) is not null,
                "the sheet built neither the Mac's Angle nor its Distance row (FilterSheet.swift:90-92).");
            Require(!scene.CanUndo, "opening the sheet changed the document before anything was touched.");
            var distanceRow = SliderNamed(body, Loc.T("L_Distance"))!;
            distanceRow.Value = 24;                       // angle 0 already holds; moving distance ticks
            Require(await WaitUntilAsync(() => motionBlurPreview is { Busy: false, Queued: false }
                    && Current().Width > before.Width, 8000),
                "the distance never reached the grown grid.");
            var grown = Current();
            Require(grown.Width == before.Width + 2 * MotionMargin(24),
                $"the grid grew by {grown.Width - before.Width} px for a 24 px streak, expected {2 * MotionMargin(24)}.");
            int margin = MotionMargin(24), dotY = 30 + margin, dotX = 5 + margin;
            int alongRow = 0, alongColumn = 0;
            for (int x = 0; x < grown.Width; x++) if (Bright(grown, x, dotY) > 8) alongRow++;
            for (int y = 0; y < grown.Height; y++) if (Bright(grown, dotX, y) > 8) alongColumn++;
            Require(alongRow > 6 && alongColumn <= 2,
                $"angle 0 smeared the dot into a {alongRow}×{alongColumn} blob instead of a horizontal streak.");
            CloseFloatingPanel("motionBlur", commit: true);
            Require(await WaitUntilAsync(() => motionBlurPreview is null, 8000), "the commit never finished.");
            var committed = Current().Clone();
            Require(committed.Width > before.Width && committed.Width < grown.Width,
                $"OK left a {committed.Width} px grid (layer {before.Width}, grown {grown.Width}) — no trim or full grid.");
            Require(scene.CanUndo, "the committed streak left no step to undo.");
            var beforePlacement = before.Width;
            OnUndo(this, new RoutedEventArgs());
            Require(await WaitUntilAsync(() => Current().Width == beforePlacement
                    && Differ(Current(), before) < 0, 8000),
                "one undo did not restore both the pixels and the placement.");
            report["aHorizontalAngleStreaksHorizontallyOnTheGrownGrid"] = "passed";

            // --- Turning the angle turns the streak (same fixture, ninety degrees over) ---
            scene = DotScene();
            OpenDocument(scene); await WaitForFramesAsync();
            id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
            before = scene.Snapshot.Images[id]!.Clone();
            OnMotionBlurFilter(this, new RoutedEventArgs());
            await WaitForFramesAsync();
            body = FloatingPanelBody("motionBlur")!;
            SliderNamed(body, Loc.T("L_Angle"))!.Value = 90;
            SliderNamed(body, Loc.T("L_Distance"))!.Value = 24;
            Require(await WaitUntilAsync(() => motionBlurPreview is { Busy: false, Queued: false }
                    && Current().Width > before.Width, 8000), "the turned streak never landed.");
            grown = Current();
            dotX = 5 + margin; dotY = 30 + margin;
            alongRow = 0; alongColumn = 0;
            for (int x = 0; x < grown.Width; x++) if (Bright(grown, x, dotY) > 8) alongRow++;
            for (int y = 0; y < grown.Height; y++) if (Bright(grown, dotX, y) > 8) alongColumn++;
            Require(alongColumn > 6 && alongRow <= 2,
                $"angle 90 made a {alongRow}×{alongColumn} smear, not the vertical streak the same distance gives.");
            CloseFloatingPanel("motionBlur", commit: false);
            Require(await WaitUntilAsync(() => motionBlurPreview is null, 8000), "the turned fixture never closed.");
            report["turningTheAngleTurnsTheStreak"] = "passed";

            // --- Cancel puts the grid AND the placement back, no history ---
            scene = DotScene();
            OpenDocument(scene); await WaitForFramesAsync();
            id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
            before = scene.Snapshot.Images[id]!.Clone();
            OnMotionBlurFilter(this, new RoutedEventArgs());
            await WaitForFramesAsync();
            SliderNamed(FloatingPanelBody("motionBlur")!, Loc.T("L_Distance"))!.Value = 40;
            Require(await WaitUntilAsync(() => motionBlurPreview is { Busy: false, Queued: false }
                    && Current().Width > before.Width, 8000), "the preview grid never landed.");
            CloseFloatingPanel("motionBlur", commit: false);
            Require(await WaitUntilAsync(() => motionBlurPreview is null, 8000), "the cancel never finished.");
            Require(Differ(Current(), before) < 0 && !scene.CanUndo,
                "Cancel left a bigger grid, a moved placement, or a history step behind.");
            report["cancelRestoresTheStreaksWholeGrid"] = "passed";

            // --- A selection keeps the streak inside itself, on the grown grid's coordinates ---
            scene = DotScene();
            OpenDocument(scene); await WaitForFramesAsync();
            id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
            scene.SetSelection(Selection.Rectangle(60, 60, 0, 0, 20, 60));   // left third, document units
            OnMotionBlurFilter(this, new RoutedEventArgs());
            await WaitForFramesAsync();
            SliderNamed(FloatingPanelBody("motionBlur")!, Loc.T("L_Distance"))!.Value = 24;
            Require(await WaitUntilAsync(() => motionBlurPreview is { Busy: false, Queued: false }
                    && Current().Width > 60, 8000), "the clipped tick never landed.");
            var reference = motionBlurPreview!.Grown;
            var result = Current();
            int outside = 0, inside = 0;
            for (int y = 0; y < result.Height; y++)
                for (int x = 0; x < result.Width; x++)
                {
                    var document = BrushEngine.PixelToDocument(motionBlurPreview.GrownPlacement,
                        result.Width, result.Height, new PointD(x + .5, y + .5));
                    bool inSelection = document.X >= 0 && document.X < 20 && document.Y >= 0 && document.Y < 60;
                    int at = y * result.Stride + x * 4, refAt = y * reference.Stride + x * 4;
                    bool changed = Enumerable.Range(0, 4).Any(c => result.Pixels[at + c] != reference.Pixels[refAt + c]);
                    if (inSelection) { if (changed) inside++; }
                    else if (changed) outside++;
                }
            Require(outside == 0, $"{outside} pixels outside the selection were smeared — the clip spoke of the wrong grid.");
            Require(inside > 4, $"only {inside} pixels inside the selection changed, so the check above proves nothing.");
            CloseFloatingPanel("motionBlur", commit: false);
            await WaitUntilAsync(() => motionBlurPreview is null, 8000);
            report["aSelectionKeepsTheStreakInsideItself"] = "passed";

            // --- The sheet bars the tab switch and the window close settles it, no prompt ---
            var otherTab = new ProjectWorkspaceTab { Document = EditorDocument.Create(64, 48) };
            workspace.Tabs.Add(otherTab);
            try
            {
                OnMotionBlurFilter(this, new RoutedEventArgs());
                await WaitForFramesAsync();
                Require(ProjectEditsInFlight, "the Motion sheet left the tab gate open.");
                Require(!SwitchProjectTab(otherTab.Id), "a tab switch ran across the open Motion sheet.");
                var walked = await ConfirmCloseAllProjectsAsync();
                Require(walked && motionBlurPreview is null && !FloatingPanelIsOpen("motionBlur"),
                    $"the quit did not settle the sheet before walking (walked={walked}, preview={motionBlurPreview is not null}, "
                    + $"panel={FloatingPanelIsOpen("motionBlur")}).");
                ResetTabsForProbe();
            }
            finally
            {
                closeConfirmProbe = null;
                if (workspace.Tabs.Contains(otherTab)) workspace.Tabs.Remove(otherTab);
            }
            report["theMotionSheetRefusesSwitchAndQuitSettlesIt"] = "passed";
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }
}
#endif
