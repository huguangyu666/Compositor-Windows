#if DEBUG
using System.Collections.Generic;
using Compositor.Core;
using Microsoft.UI.Xaml;

namespace Compositor.App;

/// <summary>
/// Filter ▸ Tonal Contrast, measured against the Mac (Filters.swift:242-256, FilterSheet.swift:132-137):
/// detail is what a pixel differs from its own blurred copy, so flat areas cannot be crushed, the band a pixel
/// belongs to is read off the blurred copy, and OK with nothing to push behaves as Cancel
/// (Filters.swift:606-607).
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>A hard vertical edge at x=41 in an 81×81 layer: the only place this filter has detail to work
    /// on, which is what makes "flat stays flat" and "the selection holds" both falsifiable here. The size is
    /// not decoration — the base is a Gaussian, so a 21 px frame gives every pixel edge influence and the flat
    /// claim below could only ever pass for the wrong reason. The exact overshoot is left to the Core tests,
    /// whose base is hand-built: the panel's base comes from the port's own Gaussian and this machine has no
    /// Core Image to compare it against.</summary>
    private static EditorDocument TonalContrastScene(int side = 81)
    {
        var scene = EditorDocument.Create(side, side);
        var layer = scene.Snapshot.Manifest.Layers[0];
        layer.Transform = new() { Origin = new(0, 0), Size = new(side, side) };
        var image = new Surface(side, side);
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
            {
                int at = y * image.Stride + x * 4;
                byte level = (byte)(x < 41 ? 40 : 200);
                image.Pixels[at] = image.Pixels[at + 1] = image.Pixels[at + 2] = level;
                image.Pixels[at + 3] = 255;
            }
        ((Dictionary<Guid, Surface>)scene.Snapshot.Images)[layer.Id] = image;
        return scene;
    }

    private static bool TonalRow(Microsoft.UI.Xaml.DependencyObject body, string key) =>
        SliderNamed(body, Loc.T(key)) is not null;

    private async Task VerifyTonalContrastAsync(Dictionary<string, object> report)
    {
        var scene = TonalContrastScene();
        OpenDocument(scene); await WaitForFramesAsync();
        var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        var before = scene.Snapshot.Images[id]!.Clone();
        Surface Current() => scene.Snapshot.Images[id]!;

        // --- The panel, the edge it lifts, the flat it leaves, and a Cancel that leaves no trace ---
        OnTonalContrast(this, new RoutedEventArgs());
        Require(FloatingPanelIsOpen("tonalContrast"), "the Tonal Contrast command opened no panel.");
        await WaitForFramesAsync(); RootGrid.UpdateLayout();
        RequireSurfaceControlsNamed(report, "tonalContrastPanelAccessibility", FloatingPanelRoot("tonalContrast"));
        var body = FloatingPanelBody("tonalContrast")!;
        foreach (var key in new[] { "L_TonalAmount", "L_TonalShadows", "L_TonalMidtones",
            "L_TonalHighlights", "L_TonalRadius" })
            Require(TonalRow(body, key), $"the panel built no {Loc.T(key)} row.");
        Require(!scene.CanUndo, "opening the panel changed the document before anything was touched.");
        SliderNamed(body, Loc.T("L_TonalRadius"))!.Value = 4;
        SliderNamed(body, Loc.T("L_TonalMidtones"))!.Value = 100;
        // "reached the layer" has to include the session having published the last thing asked of it. The panel
        // runs a tick of its own defaults when it opens, so waiting only for "the picture moved" can be answered
        // by that earlier frame — and the clone below is then the frame the switch-back has to miss, which is
        // what made this red 1 run in 9 once the base was computed faster (§8 of
        // Windows/docs/filterpreview-4k-perf-2026-10-21.md, closed as #92).
        Require(await WaitUntilAsync(() => tonalPreview is { Busy: false, Queued: false } && Differ(Current(), before) >= 0, 8000),
            "the midtones row never reached the layer with the session idle.");
        var lifted = Current().Clone();
        // The whole point of a local-contrast filter, stated where it can fail: a pixel that equals its own
        // blur has no detail, and no amount of midtones may move it. 20+ px from the edge with σ=4 is flat
        // to within a hundredth of a level, so this is a claim and not a rounding accident.
        for (int y = 0; y < 81; y += 8)
            foreach (int x in new[] { 10, 25, 57, 71 })
                Require(Differ(PixelSurface(Current(), x, y), PixelSurface(before, x, y)) < 0,
                    $"({x},{y}) sits {Math.Abs(x - 41)} px from the edge with radius 4, yet the filter moved it.");
        Require(Differ(PixelSurface(Current(), 40, 40), PixelSurface(before, 40, 40)) >= 0,
            "the pixel at the edge itself did not move, so the flat claim above proves nothing.");
        Require(scene.CanUndo, "the previewed detail is not a step the user can undo.");
        var switchRow = tonalFormProbe!.PreviewSwitch;
        switchRow.IsChecked = false;
        Require(await WaitUntilAsync(() => Differ(Current(), before) < 0, 8000),
            "taking Preview off did not put the untouched pixels back on screen.");
        switchRow.IsChecked = true;
        Require(await WaitUntilAsync(() => Differ(Current(), lifted) < 0, 8000),
            "putting Preview back on did not bring the lifted edge back.");
        CloseFloatingPanel("tonalContrast", commit: false);
        Require(await WaitUntilAsync(() => tonalPreview is null, 8000), "the cancel never finished.");
        Require(Differ(Current(), before) < 0 && !scene.CanUndo,
            "Cancel either left the detail on the layer or left history behind.");
        report["tonalContrastPanelLiftsDetailAndCancelsAsOneStep"] = "passed";

        // --- OK with nothing to push is Cancel: the rule is wider than Amount == 0 ---
        OnTonalContrast(this, new RoutedEventArgs());
        body = FloatingPanelBody("tonalContrast")!;
        SliderNamed(body, Loc.T("L_TonalAmount"))!.Value = 100;
        SliderNamed(body, Loc.T("L_TonalRadius"))!.Value = 4;
        Require(await WaitUntilAsync(() => tonalPreview is { Busy: false, Queued: false } && Differ(Current(), before) >= 0, 8000),
            "the amount never reached the layer with the session idle.");
        var pushed = Current().Clone();
        SliderNamed(body, Loc.T("L_TonalShadows"))!.Value = 0;
        SliderNamed(body, Loc.T("L_TonalMidtones"))!.Value = 0;
        SliderNamed(body, Loc.T("L_TonalHighlights"))!.Value = 0;
        Require(await WaitUntilAsync(() => Differ(Current(), before) < 0, 8000),
            "silencing all three bands did not take the picture back; the kernel's own guard says it should.");
        Require(SliderNamed(FloatingPanelBody("tonalContrast")!, Loc.T("L_TonalAmount"))!.Value == 100,
            "the amount moved while the bands were being silenced, so OK below would not be testing the band rule.");
        var said = StatusText.Text;
        CloseFloatingPanel("tonalContrast", commit: true);
        Require(await WaitUntilAsync(() => tonalPreview is null, 8000), "the silent-commit never finished.");
        Require(Differ(Current(), before) < 0 && !scene.CanUndo,
            "OK with every band at zero committed something (Filters.swift:606-607).");
        Require(Differ(pushed, before) >= 0, "the frame that had detail never had any, so the check above proves nothing.");
        // EndTransform drops a session that ends where it started, so the two lines above would also hold if the
        // identity rule itself were wrong and a silent OK ran the commit path anyway. The status line is the only
        // place that difference is visible: the Mac leaves it alone (closing the panel is the feedback).
        Require(StatusText.Text == said,
            $"a silent OK announced a filter the picture never got ({said} -> {StatusText.Text}).");
        report["tonalContrastOkWithSilentBandsIsCancel"] = "passed";

        // --- A selection keeps the result inside itself ---
        var clipped = TonalContrastScene();
        OpenDocument(clipped); await WaitForFramesAsync();
        var layerId = clipped.Snapshot.Manifest.ActiveLayerId!.Value;
        var original = clipped.Snapshot.Images[layerId]!.Clone();
        var untouched = (byte[])original.Pixels.Clone();
        // Left of the edge only, so the pixels whose detail the filter feeds are inside and the flat bright
        // half outside is exactly what an unclipped run would move.
        clipped.SetSelection(Selection.Rectangle(81, 81, 0, 0, 44, 81));
        OnTonalContrast(this, new RoutedEventArgs());
        body = FloatingPanelBody("tonalContrast")!;
        Require(ContainsText(body, Loc.T("L_FilterLimitedToSelection")), "the panel ran a clipped filter without saying so.");
        SliderNamed(body, Loc.T("L_TonalRadius"))!.Value = 4;
        SliderNamed(body, Loc.T("L_TonalMidtones"))!.Value = 100;
        CloseFloatingPanel("tonalContrast", commit: true);
        Require(await WaitUntilAsync(() => tonalPreview is null
                && Differ(clipped.Snapshot.Images[layerId]!, original) >= 0, 8000),
            "the clipped detail never reached the layer.");
        var result = clipped.Snapshot.Images[layerId]!;
        int inside = 0, outside = 0;
        for (int y = 0; y < 81; y++)
            for (int x = 0; x < 81; x++)
            {
                int at = y * result.Stride + x * 4;
                bool changed = Enumerable.Range(0, 4).Any(channel => result.Pixels[at + channel] != untouched[at + channel]);
                if (x < 44) { if (changed) inside++; }
                else if (changed) outside++;
            }
        Require(outside == 0, $"{outside} pixels outside the selection were touched by the filter.");
        Require(inside > 0, "nothing inside the selection changed, so the check above proves nothing.");
        report["tonalContrastStaysInsideTheSelection"] = "passed";
    }

    /// <summary>#92 with the timing forced instead of hunted. The panel runs a tick of its own defaults the moment
    /// it opens, and <see cref="WaitUntilAsync"/> only looks every 50 ms, so a precondition that asks nothing but
    /// "has the picture moved" is answered by that opening frame while the tick the rows just queued is still
    /// being worked out. A frame cloned then is one the switch-back can never bring back, because the session
    /// republishes the values the rows hold now. Radius 40 on a 321 px layer keeps that queued tick hundreds of
    /// milliseconds long, which makes the intermediate frame impossible to miss instead of a 1-in-9 coincidence.
    /// This group passes because the wait below also asks for the session to be idle; with the weak form of the
    /// wait it goes red every run.</summary>
    private async Task VerifyTonalContrastRearmAfterTwoTicksAsync(Dictionary<string, object> report)
    {
        var scene = TonalContrastScene(321);
        OpenDocument(scene); await WaitForFramesAsync();
        var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        var before = scene.Snapshot.Images[id]!.Clone();
        Surface Current() => scene.Snapshot.Images[id]!;

        OnTonalContrast(this, new RoutedEventArgs());
        var body = FloatingPanelBody("tonalContrast")!;
        var ticksAtOpen = tonalTickSequence;
        SliderNamed(body, Loc.T("L_TonalRadius"))!.Value = 40;
        SliderNamed(body, Loc.T("L_TonalMidtones"))!.Value = 100;
        Require(await WaitUntilAsync(() => tonalPreview is { Busy: false, Queued: false }
                && Differ(Current(), before) >= 0, 8000),
            "the two rows never reached the layer with the session idle.");
        var latest = Current().Clone();

        var switchRow = tonalFormProbe!.PreviewSwitch;
        switchRow.IsChecked = false;
        Require(await WaitUntilAsync(() => Differ(Current(), before) < 0, 8000),
            "taking Preview off did not put the untouched pixels back on screen.");
        switchRow.IsChecked = true;
        var back = await WaitUntilAsync(() => Differ(Current(), latest) < 0, 8000);
        var ticks = tonalTickSequence - ticksAtOpen;
        report["tonalContrastRearmFrames"] = new { back, ticks, latest = TonalFingerprint(latest),
            current = TonalFingerprint(Current()) };
        Require(back, "putting Preview back on never reproduced the frame cloned above, and the session had since "
            + $"published {ticks} tick(s) — the clone was an earlier tick's, which is #92's stale read.");
        // Two published frames have to exist for the above to have had anything stale to read. One means the rows
        // coalesced away the intermediate frame and the group proved nothing.
        Require(ticks >= 2, $"only {ticks} tick(s) were published after the panel opened, so this run never held "
            + "an earlier frame to mistake for the latest one.");
        CloseFloatingPanel("tonalContrast", commit: false);
        Require(await WaitUntilAsync(() => tonalPreview is null, 8000), "the cancel never finished.");
        Require(Differ(Current(), before) < 0 && !scene.CanUndo,
            "Cancel either left the detail on the layer or left history behind.");
        report["tonalContrastRearmUsesTheLatestTick"] = "passed";
    }

    private static Surface PixelSurface(Surface surface, int x, int y)
    {
        var one = new Surface(1, 1);
        int at = y * surface.Stride + x * 4;
        for (int channel = 0; channel < 4; channel++) one.Pixels[channel] = surface.Pixels[at + channel];
        return one;
    }
}
#endif
