#if DEBUG
using System.IO;
using Compositor.Core;
using Microsoft.UI.Xaml;

namespace Compositor.App;

/// <summary>
/// Filter ▸ Gaussian Blur, measured against the Mac (Filters.swift:203-205, :384-418, :616-649): the
/// blur runs on a grid padded by the radius's own room, so the picture dissolves against transparency
/// rather than its edge; OK cuts that back to what has coverage with the placement that keeps the same
/// document rectangle; the attached mask rides onto the new grid, edge tone and all; a Cancel puts both
/// grid and placement back; the clip the preview blends through speaks of the GROWN grid, because the
/// grow rebuilt the mapping with it.
/// </summary>
public sealed partial class MainWindow
{
    private async Task VerifyGaussianBlurFilterAsync(Dictionary<string, object> report)
    {
        var directory = Path.Combine(Path.GetTempPath(), "compositor-gaussian-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            // A black square in the middle of a white layer: the blur's spread is the whole claim, and
            // it has to show in the padding that did not exist before the sheet opened.
            static EditorDocument SquareScene(int side = 60, int box = 20)
            {
                var scene = EditorDocument.Create(side, side);
                var layer = scene.Snapshot.Manifest.Layers[0];
                layer.Transform = new() { Origin = new(0, 0), Size = new(side, side) };
                var image = new Surface(side, side);
                Array.Fill(image.Pixels, (byte)255);
                int x0 = (side - box) / 2, x1 = x0 + box;
                for (int y = x0; y < x1; y++)
                    for (int x = x0; x < x1; x++)
                    { int at = y * image.Stride + x * 4; for (int c = 0; c < 4; c++) image.Pixels[at + c] = 0; }
                for (int i = 3; i < image.Pixels.Length; i += 4) image.Pixels[i] = 255;
                ((Dictionary<Guid, Surface>)scene.Snapshot.Images)[layer.Id] = image;
                return scene;
            }
            (double, double) DocumentCenter(EditorDocument doc, Guid id)
            {
                var t = doc.Snapshot.Manifest.Layers.First(r => r.Id == id).Transform;
                return (t.Center.X, t.Center.Y);
            }

            // --- The blur grows the layer, and OK leaves it grown to the coverage the picture needs ---
            var scene = SquareScene();
            OpenDocument(scene); await WaitForFramesAsync();
            var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
            var before = scene.Snapshot.Images[id]!.Clone();
            var (centerX, centerY) = DocumentCenter(scene, id);
            Surface Current() => scene.Snapshot.Images[id]!;

            OnGaussianBlurFilter(this, new RoutedEventArgs());
            Require(FloatingPanelIsOpen("gaussianBlur"), "the Gaussian Blur command opened no panel.");
            await WaitForFramesAsync(); RootGrid.UpdateLayout();
            var body = FloatingPanelBody("gaussianBlur")!;
            Require(SliderNamed(body, Loc.T("L_Radius")) is not null, "the sheet built no Radius row.");
            Require(!scene.CanUndo, "opening the sheet changed the document before anything was touched.");
            var radiusRow = SliderNamed(body, Loc.T("L_Radius"))!;
            radiusRow.Value = 8;
            Require(await WaitUntilAsync(() => gaussianPreview is { Busy: false, Queued: false }
                    && Current().Width > before.Width, 8000),
                "the sheet never put the grown grid on the layer.");
            var grownWidth = Current().Width;
            Require(grownWidth == before.Width + 2 * BlurMargin(8),
                $"the grid grew by the wrong room: {grownWidth - before.Width} for a radius of 8, expected {2 * BlurMargin(8)}.");
            // The document rectangle stayed: the grown placement is wider but centered on the same middle.
            var (grownCenterX, grownCenterY) = DocumentCenter(scene, id);
            Require(Math.Abs(grownCenterX - centerX) < 1e-6 && Math.Abs(grownCenterY - centerY) < 1e-6,
                "growing moved the layer on the canvas.");
            // The corner of the ORIGINAL grid — white in the scene — now carries the black square's blur:
            // the picture dissolved into the padding, which is the Mac's whole reason for the grow.
            var paddingByte = Current().Pixels[(30 * Current().Stride + 3 * 4)];
            Require(paddingByte < 250,
                "the padding stayed pure white: the blur did not actually spread into the room made for it.");
            CloseFloatingPanel("gaussianBlur", commit: true);
            Require(await WaitUntilAsync(() => gaussianPreview is null, 8000), "the commit never finished.");
            var trimmed = Current().Clone();
            Require(trimmed.Width < grownWidth, "OK did not cut the blurred grid back to what has coverage.");
            Require(trimmed.Width > before.Width,
                "the blurred layer did not end up bigger than it was: alpha spread out of the old grid.");
            var (finalCenterX, finalCenterY) = DocumentCenter(scene, id);
            Require(Math.Abs(finalCenterX - centerX) < 1e-4 && Math.Abs(finalCenterY - centerY) < 1e-4,
                "the trimmed grid is no longer centred where the layer was.");
            Require(scene.CanUndo, "the committed blur left no step to undo.");
            var committed = Current().Clone();
            OnUndo(this, new RoutedEventArgs());
            Require(await WaitUntilAsync(() => Differ(Current(), before) < 0, 8000),
                "one undo did not restore both the old pixels and the old placement.");
            var (restoredX, restoredY) = DocumentCenter(scene, id);
            Require(Math.Abs(restoredX - centerX) < 1e-6 && Current().Width == before.Width,
                "the undo restored the picture but not the grid.");
            Require(Differ(committed, before) >= 0, "the frame one undo removed was never different.");
            report["gaussianBlurGrowsBlursAndTrimsAroundItsMiddle"] = "passed";

            // --- Cancel puts the grid AND the placement back, leaving one clean step ---
            scene = SquareScene();
            OpenDocument(scene); await WaitForFramesAsync();
            id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
            before = scene.Snapshot.Images[id]!.Clone();
            OnGaussianBlurFilter(this, new RoutedEventArgs());
            await WaitForFramesAsync();
            SliderNamed(FloatingPanelBody("gaussianBlur")!, Loc.T("L_Radius"))!.Value = 12;
            Require(await WaitUntilAsync(() => gaussianPreview is { Busy: false, Queued: false }
                    && scene.Snapshot.Images[id]!.Width > before.Width, 8000), "the preview grid never landed.");
            CloseFloatingPanel("gaussianBlur", commit: false);
            Require(await WaitUntilAsync(() => gaussianPreview is null, 8000), "the cancel never finished.");
            Require(Differ(scene.Snapshot.Images[id]!, before) < 0 && !scene.CanUndo,
                "Cancel left a bigger grid, a moved placement, or a history step behind.");
            report["cancelRestoresTheWholeGridNotJustThePixels"] = "passed";

            // --- A selection blurs inside itself, on the grown grid's coordinates ---
            scene = SquareScene();
            OpenDocument(scene); await WaitForFramesAsync();
            id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
            before = scene.Snapshot.Images[id]!.Clone();
            // Left half selected in DOCUMENT units; the grow maps the clip through the grown placement,
            // so pixels outside the selection must keep their pre-blur bytes even in the padding.
            scene.SetSelection(Selection.Rectangle(60, 60, 0, 0, 30, 60));
            OnGaussianBlurFilter(this, new RoutedEventArgs());
            await WaitForFramesAsync();
            SliderNamed(FloatingPanelBody("gaussianBlur")!, Loc.T("L_Radius"))!.Value = 8;
            Require(await WaitUntilAsync(() => gaussianPreview is { Busy: false, Queued: false }
                    && Current().Width > before.Width, 8000), "the clipped tick never landed.");
            // The reference is the session's own unblurred grown grid: the clip keeps THAT outside the
            // selection, so every untouched pixel must equal it byte for byte, and the box's edge INSIDE
            // the selection must not.
            var grown = gaussianPreview!.Grown;
            var result = Current();
            int outside = 0, inside = 0;
            for (int y = 0; y < result.Height; y++)
                for (int x = 0; x < result.Width; x++)
                {
                    var document = BrushEngine.PixelToDocument(gaussianPreview.GrownPlacement,
                        result.Width, result.Height, new PointD(x + .5, y + .5));
                    bool inSelection = document.X >= 0 && document.X < 30 && document.Y >= 0 && document.Y < 60;
                    int at = y * result.Stride + x * 4, refAt = y * grown.Stride + x * 4;
                    bool changed = Enumerable.Range(0, 4).Any(c => result.Pixels[at + c] != grown.Pixels[refAt + c]);
                    if (inSelection) { if (changed) inside++; }
                    else if (changed) outside++;
                }
            Require(outside == 0, $"{outside} pixels outside the selection were blurred — the clip spoke of the wrong grid.");
            Require(inside > 400, $"only {inside} pixels inside the selection changed, so the check above proves nothing.");
            CloseFloatingPanel("gaussianBlur", commit: false);
            Require(await WaitUntilAsync(() => gaussianPreview is null, 8000), "the clipped fixture never closed.");
            report["aSelectionKeepsTheBlurInsideItself"] = "passed";

            // --- The attached mask rides onto the new grid, edge tone and sleep state kept ---
            scene = SquareScene();
            OpenDocument(scene); await WaitForFramesAsync();
            id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
            var mask = new MaskSurface(60, 60);
            for (int y = 0; y < 60; y++)
                for (int x = 0; x < 60; x++)
                    mask.Pixels[y * 60 + x] = x is >= 15 and <= 25 ? (byte)0 : (byte)255;  // a black bar: hides, rest reveals
            Require(scene.ReplaceLayerMask(id, mask), "the mask fixture did not land.");
            // The mask's border is mostly reveal, so the grown padding must come in as reveal (255).
            OnGaussianBlurFilter(this, new RoutedEventArgs());
            await WaitForFramesAsync();
            SliderNamed(FloatingPanelBody("gaussianBlur")!, Loc.T("L_Radius"))!.Value = 6;
            Require(await WaitUntilAsync(() => gaussianPreview is { Busy: false, Queued: false }
                    && Current().Width > 60, 8000), "the grown preview never landed.");
            CloseFloatingPanel("gaussianBlur", commit: true);
            Require(await WaitUntilAsync(() => gaussianPreview is null, 8000), "the masked commit never finished.");
            var carried = scene.Snapshot.Masks[id]!;
            Require(carried.Width == Current().Width && carried.Height == Current().Height,
                "the mask did not ride onto the committed grid.");
            Require(carried.Pixels[0] == 255, "the padding took a tone the old mask's own border never showed.");
            // The old mask's black bar at x 15–25 rides with the mapping: sampled at the grown grid's
            // middle row, the bar's reveal/hide pattern must still be there, shifted by the margin.
            int row = carried.Height / 2;
            bool barSurvived = Enumerable.Range(0, carried.Width).Count(x => carried.Pixels[row * carried.Width + x] == 0) > 5;
            Require(barSurvived, "the mask's own picture did not ride onto the new grid.");
            Require(scene.Snapshot.Manifest.Layers.First(r => r.Id == id).MaskEnabled != false,
                "the mask got switched off by the commit.");
            report["theAttachedMaskRidesOntoTheGrownGrid"] = "passed";

            // --- The sheet bars the tab switch the way its siblings do (the Mac's filterEdit term) ---
            var otherTab = new ProjectWorkspaceTab { Document = EditorDocument.Create(64, 48) };
            workspace.Tabs.Add(otherTab);
            try
            {
                OnGaussianBlurFilter(this, new RoutedEventArgs());
                await WaitForFramesAsync();
                Require(ProjectEditsInFlight, "the Gaussian sheet left the tab gate open.");
                Require(!SwitchProjectTab(otherTab.Id), "a tab switch ran across the open Gaussian sheet.");
                CloseFloatingPanel("gaussianBlur", commit: false);
                Require(await WaitUntilAsync(() => gaussianPreview is null, 8000), "the barred sheet never closed.");
                if (await WaitUntilAsync(() => !ProjectEditsInFlight, 8000))
                    Require(SwitchProjectTab(otherTab.Id), "the gate stayed barred after the sheet closed.");
                else Require(!SwitchProjectTab(otherTab.Id), "in-flight work still bars the gate: "
                    + $"pending render={renderInputs.HasPending}");
            }
            finally { workspace.Tabs.Remove(otherTab); }
            report["theGaussianSheetRefusesTabSwitch"] = "passed";
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }
}
#endif
