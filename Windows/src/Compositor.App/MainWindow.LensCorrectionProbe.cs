#if DEBUG
using Compositor.Core;
using Microsoft.UI.Xaml;

namespace Compositor.App;

/// <summary>
/// Filter ▸ Lens Correction, measured against the Mac (Filters.swift:257-266, FilterSheet.swift:138-143):
/// one slider over the warp kernel, one undo step, Cancel taking the layer back with no history behind it,
/// OK at zero behaving as Cancel (Filters.swift:603), and a selection holding the result inside itself.
/// </summary>
public sealed partial class MainWindow
{
    private static EditorDocument LensCorrectionScene(int side = 41, int line = 10)
    {
        var scene = EditorDocument.Create(side, side);
        var layer = scene.Snapshot.Manifest.Layers[0];
        layer.Transform = new() { Origin = new(0, 0), Size = new(side, side) };
        var image = new Surface(side, side);
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
            {
                int at = y * image.Stride + x * 4;
                image.Pixels[at] = (byte)(x == line ? 255 : 0);
                image.Pixels[at + 1] = 0;
                // A second line in blue on the far side. The red channel is what the geometry reads (the Core
                // centroids are red-only), and the selection group needs something OUTSIDE the band that the
                // warp visibly moves — over an all-black right half, "nothing outside changed" is true with or
                // without the clip, and the round that drops the clip passed on exactly that hole.
                image.Pixels[at + 2] = (byte)(x == 30 ? 255 : 0);
                image.Pixels[at + 3] = 255;
            }
        ((Dictionary<Guid, Surface>)scene.Snapshot.Images)[layer.Id] = image;
        return scene;
    }

    /// <summary>The panel's own body. Searching from the root stops at the host's ScrollViewer, which is neither
    /// a Panel nor a ContentControl, and a slider below it is then "missing" from a panel that built it.</summary>
    private Microsoft.UI.Xaml.DependencyObject LensCorrectionBody() => FloatingPanelBody("lensCorrection")!;

    /// <summary>The red-weighted mean column of one row, the same reading the Core test makes of the kernel.</summary>
    private static double LensLineColumn(Surface surface, int row)
    {
        double sum = 0, weighted = 0;
        for (int x = 0; x < surface.Width; x++)
        {
            double value = surface.Pixels[row * surface.Stride + x * 4];
            sum += value;
            weighted += x * value;
        }
        return sum == 0 ? -1 : weighted / sum;
    }

    private async Task VerifyLensCorrectionAsync(Dictionary<string, object> report)
    {
        // --- The panel, the preview, and a Cancel that leaves no trace ---
        var scene = LensCorrectionScene();
        OpenDocument(scene); await WaitForFramesAsync();
        var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        var before = scene.Snapshot.Images[id]!.Clone();
        OnLensCorrection(this, new RoutedEventArgs());
        Require(FloatingPanelIsOpen("lensCorrection"), "the Lens Correction command opened no panel.");
        // The sweep walks the visual tree, and a panel that has not been laid out yet has almost nothing in
        // one — without this the count was 3 on a panel that builds six or seven named controls, and "all named"
        // was measuring a near-empty tree.
        await WaitForFramesAsync(); RootGrid.UpdateLayout();
        RequireSurfaceControlsNamed(report, "lensCorrectionPanelAccessibility", FloatingPanelRoot("lensCorrection"));
        var slider = SliderNamed(LensCorrectionBody(), Loc.T("L_RemoveDistortion"));
        Require(slider is not null, "the panel built no Remove Distortion slider.");
        Require(!scene.CanUndo, "opening the panel changed the document before anything was touched.");
        slider!.Value = 100;
        Require(await WaitUntilAsync(() => Differ(scene.Snapshot.Images[id]!, before) >= 0, 8000),
            "the slider never reached the layer.");
        Require(scene.CanUndo, "the previewed warp is not a step the user can undo.");
        CloseFloatingPanel("lensCorrection", commit: false);
        Require(await WaitUntilAsync(() => Differ(scene.Snapshot.Images[id]!, before) < 0 && !scene.CanUndo, 8000),
            "Cancel either left the warp on the layer or left history behind.");
        report["lensCorrectionPanelPreviewsAndCancelsAsOneStep"] = "passed";

        // --- OK bakes the warp, and it is the kernel the Core numbers describe ---
        OnLensCorrection(this, new RoutedEventArgs());
        slider = SliderNamed(LensCorrectionBody(), Loc.T("L_RemoveDistortion"));
        Require(slider is not null, "the second panel built no slider.");
        slider!.Value = 100;
        Require(await WaitUntilAsync(() => Differ(scene.Snapshot.Images[id]!, before) >= 0, 8000), "the warp never landed.");
        var previewed = scene.Snapshot.Images[id]!.Clone();
        CloseFloatingPanel("lensCorrection", commit: true);
        // Closing the panel only starts OK's render — a second pass on a worker. Read the layer before it lands
        // and you read the preview frame, and a Ctrl+Z asked for in that window undoes the preview step instead;
        // that race is what this group first failed on, with the layer still holding the warp after one undo.
        Require(await WaitUntilAsync(() => lensPreview is null, 8000), "the commit never finished.");
        await WaitForFramesAsync();
        var committed = scene.Snapshot.Images[id]!;
        Require(Differ(committed, before) >= 0, "OK wrote the untouched layer.");
        // The published frame has to be the same grade the panel was showing, not a re-run that disagrees with it,
        // and the middle row has to sit where the warp puts a line at x=10: 0.5 px inward at full positive amount
        // (lens-correction-golden.py), which no other strength or sign produces.
        Require(Differ(committed, previewed) < 0, "OK re-rendered something else than the preview was holding.");
        Require(Math.Abs(LensLineColumn(committed, 20) - 9.513) < 0.35,
            $"the committed warp put the line at {LensLineColumn(committed, 20):0.###}, not at the 9.5 the slider asks for.");
        OnUndo(this, new()); await WaitForFramesAsync();
        Require(Differ(scene.Snapshot.Images[id]!, before) < 0, "one undo after OK did not give the untouched layer back.");
        Require(!scene.CanUndo, "the panel's preview and its commit stood as two undo steps; one filter is one step.");
        report["lensCorrectionOkBakesTheWarpAndUndoesAsOneStep"] = "passed";

        // --- OK with the slider back at zero is Cancel (Filters.swift:603) ---
        OnLensCorrection(this, new RoutedEventArgs());
        slider = SliderNamed(LensCorrectionBody(), Loc.T("L_RemoveDistortion"));
        Require(slider is not null, "the third panel built no slider.");
        slider!.Value = 100;
        Require(await WaitUntilAsync(() => Differ(scene.Snapshot.Images[id]!, before) >= 0, 8000), "the warp never landed.");
        // Re-fetched on purpose: writing through the control the panel built earlier is the trap CR-i and CR-l
        // both fell into — a row replaced by a rebuild takes the write and answers nothing.
        slider = SliderNamed(LensCorrectionBody(), Loc.T("L_RemoveDistortion"));
        Require(slider is not null, "the slider disappeared between the two writes.");
        slider!.Value = 0;
        Require(await WaitUntilAsync(() => Differ(scene.Snapshot.Images[id]!, before) < 0, 8000),
            "coming back to zero did not put the untouched pixels back on screen.");
        var said = StatusText.Text;
        CloseFloatingPanel("lensCorrection", commit: true);
        Require(await WaitUntilAsync(() => lensPreview is null, 8000), "the zero-commit never finished.");
        await WaitForFramesAsync();
        Require(Differ(scene.Snapshot.Images[id]!, before) < 0 && !scene.CanUndo,
            "OK at zero committed something; the Mac closes that case as cancelFilter() without a step.");
        // EndTransform drops a session that ends where it started, so the line above would also hold with the
        // zero rule removed and the commit path running anyway. The status line is where that difference shows.
        Require(StatusText.Text == said,
            $"an OK that changed nothing announced a correction the layer never got ({said} -> {StatusText.Text}).");
        report["lensCorrectionOkAtZeroIsCancel"] = "passed";

        // --- A selection keeps the result inside itself ---
        var clipped = LensCorrectionScene();
        OpenDocument(clipped); await WaitForFramesAsync();
        var layerId = clipped.Snapshot.Manifest.ActiveLayerId!.Value;
        var original = clipped.Snapshot.Images[layerId]!.Clone();
        var untouched = (byte[])original.Pixels.Clone();
        // The selection has to sit on top of the thing that moves: the line lives at x=10 and the warp shifts
        // it a few pixels, so a band over the empty right half would legitimately change nothing and the
        // "outside is untouched" half of the claim below would be measuring an untouched layer.
        clipped.SetSelection(Selection.Rectangle(41, 41, 0, 0, 14, 41));
        OnLensCorrection(this, new RoutedEventArgs());
        slider = SliderNamed(LensCorrectionBody(), Loc.T("L_RemoveDistortion"));
        Require(slider is not null, "the panel built no slider over a selection.");
        Require(ContainsText(LensCorrectionBody(), Loc.T("L_FilterLimitedToSelection")),
            "the panel ran a clipped filter without saying so.");
        slider!.Value = 100;
        CloseFloatingPanel("lensCorrection", commit: true);
        Require(await WaitUntilAsync(() => lensPreview is null
                && Differ(clipped.Snapshot.Images[layerId]!, original) >= 0, 8000),
            "the clipped warp never reached the layer.");
        var result = clipped.Snapshot.Images[layerId]!;
        int outside = 0, inside = 0;
        for (int y = 0; y < 41; y++)
            for (int x = 0; x < 41; x++)
            {
                int at = y * result.Stride + x * 4;
                bool changed = Enumerable.Range(0, 4).Any(channel => result.Pixels[at + channel] != untouched[at + channel]);
                if (x < 14) { if (changed) inside++; }
                else if (changed) outside++;
            }
        Require(outside == 0, $"{outside} pixels outside the selection were touched by the filter.");
        Require(inside > 0, "nothing inside the selection changed, so the check above proves nothing.");
        report["lensCorrectionStaysInsideTheSelection"] = "passed";
    }
}
#endif
