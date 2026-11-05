#if DEBUG
using System.Collections.Generic;
using Compositor.Core;
using Microsoft.UI.Xaml;

namespace Compositor.App;

/// <summary>
/// The Vignette filter's panel, measured against the Mac (Filters.swift:223-233, FilterSheet.swift:106-128):
/// edges take the chosen colour while the middle is left alone, one undo step, OK at Amount 0 behaving as
/// Cancel (Filters.swift:604), and a selection holding the result inside itself.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>A 21×21 canvas whose single layer fills it, so the canvas frame the falloff reads is exactly
    /// the image — and a flat grey field, so every moved pixel is the kernel's doing and not the picture's.</summary>
    private static EditorDocument VignetteScene(int side = 21)
    {
        var scene = EditorDocument.Create(side, side);
        var layer = scene.Snapshot.Manifest.Layers[0];
        layer.Transform = new() { Origin = new(0, 0), Size = new(side, side) };
        var image = new Surface(side, side);
        for (int i = 0; i < image.Pixels.Length; i += 4)
        {
            image.Pixels[i] = image.Pixels[i + 1] = image.Pixels[i + 2] = 128;
            image.Pixels[i + 3] = 255;
        }
        ((Dictionary<Guid, Surface>)scene.Snapshot.Images)[layer.Id] = image;
        return scene;
    }

    private async Task VerifyVignetteAsync(Dictionary<string, object> report)
    {
        var scene = VignetteScene();
        OpenDocument(scene); await WaitForFramesAsync();
        var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        var before = scene.Snapshot.Images[id]!.Clone();
        Surface Current() => scene.Snapshot.Images[id]!;

        // --- The panel, its five sliders and one chip, and a Cancel that leaves no trace ---
        OnVignette(this, new RoutedEventArgs());
        Require(FloatingPanelIsOpen("vignette"), "the Vignette command opened no panel.");
        // The sweep walks the visual tree, and a panel that has not been laid out yet has almost nothing in
        // one — without this the count was 3 on a panel that builds six or seven named controls, and "all named"
        // was measuring a near-empty tree.
        await WaitForFramesAsync(); RootGrid.UpdateLayout();
        RequireSurfaceControlsNamed(report, "vignettePanelAccessibility", FloatingPanelRoot("vignette"));
        var body = FloatingPanelBody("vignette")!;
        foreach (var key in new[] { "L_VignetteColor", "L_VignetteAmount", "L_VignetteMidpoint",
            "L_VignetteRoundness", "L_VignetteFeather", "L_VignetteHighlights" })
            Require(SliderNamed(body, Loc.T(key)) is not null || ButtonNamed(body, Loc.T(key)) is not null,
                $"the panel is missing its {Loc.T(key)} row.");
        Require(!scene.CanUndo, "opening the panel changed the document before anything was touched.");
        var amount = SliderNamed(body, Loc.T("L_VignetteAmount"))!;
        var midpoint = SliderNamed(body, Loc.T("L_VignetteMidpoint"))!;
        var feather = SliderNamed(body, Loc.T("L_VignetteFeather"))!;
        amount.Value = 100;
        // Idle as well as moved, for the same reason as the Tonal Contrast group above (#92): the panel has already
        // run a tick of the layer's stored amounts, and a clone of that frame is the wrong thing to hand to the
        // switch-back below.
        Require(await WaitUntilAsync(() => vignettePreview is { Busy: false, Queued: false }
                && Differ(Current(), before) >= 0, 8000), "the amount never reached the layer with the session idle.");
        Require(scene.CanUndo, "the previewed vignette is not a step the user can undo.");
        // The sheet's own Preview switch (FilterSheet.swift:143) hides the result without touching the settings,
        // so a switch that only redraws the same picture proves nothing about the freeze it has to read from.
        var shown = Current().Clone();
        var switchRow = vignetteFormProbe!.PreviewSwitch;
        switchRow.IsChecked = false;
        Require(await WaitUntilAsync(() => Differ(Current(), before) < 0, 8000),
            "taking Preview off did not put the untouched pixels back on screen.");
        switchRow.IsChecked = true;
        Require(await WaitUntilAsync(() => Differ(Current(), shown) < 0, 8000),
            "putting Preview back on did not bring the vignette back.");
        CloseFloatingPanel("vignette", commit: false);
        Require(await WaitUntilAsync(() => vignettePreview is null, 8000), "the cancel never finished.");
        Require(Differ(Current(), before) < 0 && !scene.CanUndo,
            "Cancel either left the vignette on the layer or left history behind.");
        report["vignettePanelPaintsTheEdgesAndCancelsAsOneStep"] = "passed";

        // --- OK keeps the colour the chip was given, at the kernel's own numbers ---
        OnVignette(this, new RoutedEventArgs());
        body = FloatingPanelBody("vignette")!;
        SliderNamed(body, Loc.T("L_VignetteAmount"))!.Value = 100;
        midpoint = SliderNamed(body, Loc.T("L_VignetteMidpoint"))!;
        midpoint.Value = 0;
        feather = SliderNamed(body, Loc.T("L_VignetteFeather"))!;
        feather.Value = 100;
        SliderNamed(body, Loc.T("L_VignetteRoundness"))!.Value = 100;
        SliderNamed(body, Loc.T("L_VignetteHighlights"))!.Value = 0;
        vignetteFormProbe!.PickColour(Windows.UI.Color.FromArgb(255, 0, 0, 255));
        Require(await WaitUntilAsync(() => Differ(Current(), before) >= 0, 8000), "the chip's colour never reached the layer.");
        CloseFloatingPanel("vignette", commit: true);
        Require(await WaitUntilAsync(() => vignettePreview is null, 8000), "the commit never finished.");
        var committed = Current();
        // Amount 100, midpoint 0, feather 100 on a 21 px square: the corner is mask 0.993 and the middle is mask 0,
        // so grey 128 becomes (1, 1, 254) at the corner and stays 128 in the middle (vignette-golden.py).
        AssertPixel(committed, 0, 0, 1, 1, 254, 255);
        AssertPixel(committed, 10, 10, 128, 128, 128, 255);
        OnUndo(this, new()); await WaitForFramesAsync();
        Require(Differ(Current(), before) < 0 && !scene.CanUndo,
            "one OK stood as more than one undo step, or it did not give the layer back.");
        report["vignetteOkBakesTheColourTheChipWasGiven"] = "passed";

        // --- OK with Amount back at zero is Cancel (Filters.swift:604) ---
        OnVignette(this, new RoutedEventArgs());
        body = FloatingPanelBody("vignette")!;
        amount = SliderNamed(body, Loc.T("L_VignetteAmount"))!;
        amount.Value = 100;
        Require(await WaitUntilAsync(() => Differ(Current(), before) >= 0, 8000), "the vignette never landed.");
        amount = SliderNamed(body, Loc.T("L_VignetteAmount"))!;
        amount.Value = 0;
        Require(await WaitUntilAsync(() => Differ(Current(), before) < 0, 8000),
            "going back to zero did not put the untouched pixels back on screen.");
        var said = StatusText.Text;
        CloseFloatingPanel("vignette", commit: true);
        Require(await WaitUntilAsync(() => vignettePreview is null, 8000), "the zero-commit never finished.");
        Require(Differ(Current(), before) < 0 && !scene.CanUndo,
            "OK at zero committed something; the Mac closes that case as cancelFilter() without a step.");
        // EndTransform drops a session that ends where it started, so the line above would also hold with the zero
        // rule removed and the commit path running anyway. The status line is where that difference shows.
        Require(StatusText.Text == said,
            $"an OK that changed nothing announced a vignette the layer never got ({said} -> {StatusText.Text}).");
        report["vignetteOkAtZeroIsCancel"] = "passed";

        // --- A selection keeps the result inside itself ---
        var clipped = VignetteScene();
        OpenDocument(clipped); await WaitForFramesAsync();
        var layerId = clipped.Snapshot.Manifest.ActiveLayerId!.Value;
        var original = clipped.Snapshot.Images[layerId]!.Clone();
        var untouched = (byte[])original.Pixels.Clone();
        // The left third only. The vignette would darken the right edge as much as the left, so "outside is
        // untouched" is a claim this fixture can actually fail — the lesson LC-a learned the hard way.
        clipped.SetSelection(Selection.Rectangle(21, 21, 0, 0, 7, 21));
        OnVignette(this, new RoutedEventArgs());
        body = FloatingPanelBody("vignette")!;
        Require(ContainsText(body, Loc.T("L_FilterLimitedToSelection")), "the panel ran a clipped filter without saying so.");
        SliderNamed(body, Loc.T("L_VignetteAmount"))!.Value = 100;
        SliderNamed(body, Loc.T("L_VignetteMidpoint"))!.Value = 0;
        SliderNamed(body, Loc.T("L_VignetteFeather"))!.Value = 100;
        CloseFloatingPanel("vignette", commit: true);
        Require(await WaitUntilAsync(() => vignettePreview is null
                && Differ(clipped.Snapshot.Images[layerId]!, original) >= 0, 8000),
            "the clipped vignette never reached the layer.");
        var result = clipped.Snapshot.Images[layerId]!;
        int inside = 0, outside = 0;
        for (int y = 0; y < 21; y++)
            for (int x = 0; x < 21; x++)
            {
                int at = y * result.Stride + x * 4;
                bool changed = Enumerable.Range(0, 4).Any(channel => result.Pixels[at + channel] != untouched[at + channel]);
                if (x < 7) { if (changed) inside++; }
                else if (changed) outside++;
            }
        Require(outside == 0, $"{outside} pixels outside the selection were touched by the vignette.");
        Require(inside > 0, "nothing inside the selection changed, so the check above proves nothing.");
        report["vignetteStaysInsideTheSelection"] = "passed";
    }

    private static void AssertPixel(Surface surface, int x, int y, byte r, byte g, byte b, byte a)
    {
        int at = y * surface.Stride + x * 4;
        Require(surface.Pixels[at] == r && surface.Pixels[at + 1] == g && surface.Pixels[at + 2] == b
                && surface.Pixels[at + 3] == a,
            $"({x},{y}) holds ({surface.Pixels[at]},{surface.Pixels[at + 1]},{surface.Pixels[at + 2]},{surface.Pixels[at + 3]}) "
            + $"instead of ({r},{g},{b},{a}).");
    }
}
#endif
