#if DEBUG
using System.Collections.Generic;
using Compositor.Core;
using Microsoft.UI.Xaml;

namespace Compositor.App;

/// <summary>
/// Filter ▸ Add Noise, measured against the Mac (Filters.swift:214-224, FilterSheet.swift:93-102): the
/// grain is drawn once when the sheet opens and Amount only scales it, so a pixel that speckled up at
/// 10% speckles up at 20% — a redrawn seed would scramble that. Uniform and Gaussian are the two
/// distributions, Monochromatic moves all channels of a pixel by one draw, and OK has no identity rule:
/// the Mac's silent-commit list (Filters.swift:600-610) has no addNoise entry, so OK at the slider's
/// floor still takes the step.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Opaque mid-gray: far enough from 0 and 255 that Amount 10 and 20 (spreads 12.75 and 25.5)
    /// can never clamp, which is what makes the sign check below a claim about the seed rather than an
    /// accident of rounding.</summary>
    private static EditorDocument AddNoiseScene(int side = 48, byte level = 128)
    {
        var scene = EditorDocument.Create(side, side);
        var image = scene.Snapshot.Images[scene.Snapshot.Manifest.ActiveLayerId!.Value]!;
        for (int i = 0; i < image.Pixels.Length; i += 4)
        {
            image.Pixels[i] = image.Pixels[i + 1] = image.Pixels[i + 2] = level;
            image.Pixels[i + 3] = 255;
        }
        return scene;
    }

    private static (int up, int down, long magnitude) NoiseDrift(Surface from, Surface to)
    {
        int up = 0, down = 0;
        long magnitude = 0;
        for (int i = 0; i < from.Pixels.Length; i += 4)
        {
            if (from.Pixels[i + 3] == 0) continue;
            int delta = to.Pixels[i] - from.Pixels[i];
            magnitude += Math.Abs(delta);
            if (delta > 0) up++; else if (delta < 0) down++;
        }
        return (up, down, magnitude);
    }

    private async Task VerifyAddNoiseFilterAsync(Dictionary<string, object> report)
    {
        var scene = AddNoiseScene();
        OpenDocument(scene); await WaitForFramesAsync();
        var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        var before = scene.Snapshot.Images[id]!.Clone();
        Surface Current() => scene.Snapshot.Images[id]!;

        // --- The grain is one draw held for the whole sheet ---
        OnAddNoiseFilter(this, new RoutedEventArgs());
        Require(FloatingPanelIsOpen("addNoiseFilter"), "the Add Noise command opened no panel.");
        await WaitForFramesAsync(); RootGrid.UpdateLayout();
        var body = FloatingPanelBody("addNoiseFilter")!;
        Require(SliderNamed(body, Loc.T("L_Amount")) is not null
            && CheckBoxNamed(body, Loc.T("L_Gaussian")) is not null
            && CheckBoxNamed(body, Loc.T("L_Monochromatic")) is not null,
            $"the panel is missing one of the Mac's three rows: slider={SliderNamed(body, Loc.T("L_Amount")) is not null} gaussian={CheckBoxNamed(body, Loc.T("L_Gaussian")) is not null} mono={CheckBoxNamed(body, Loc.T("L_Monochromatic")) is not null}");
        Require(!scene.CanUndo, "opening the panel changed the document before anything was touched.");
        // The form opens at the Mac's default 10%, so a row set to 10 fires nothing; the fixture moves
        // the row to 20 and 40 instead, which is the same claim about one draw scaled twice.
        var amountRow = SliderNamed(body, Loc.T("L_Amount"))!;
        amountRow.Value = 20;
        Require(await WaitUntilAsync(() => addNoisePreview is { Busy: false, Queued: false }
                && Differ(Current(), before) >= 0, 8000),
            "the amount never reached the layer with the session idle.");
        var atTen = Current().Clone();
        amountRow.Value = 40;
        Require(await WaitUntilAsync(() => addNoisePreview is { Busy: false, Queued: false }
                && Differ(Current(), atTen) >= 0, 8000),
            "doubling the amount produced no bigger grain.");
        var atTwenty = Current().Clone();
        var ten = NoiseDrift(before, atTen);
        var twenty = NoiseDrift(before, atTwenty);
        Require(ten.up + ten.down > 40 && twenty.up + twenty.down > 40,
            $"almost nothing speckled ({ten.up + ten.down} of {before.Pixels.Length / 4} pixels), so the sign check below is noise.");
        int agreed = 0, flipped = 0;
        for (int i = 0; i < before.Pixels.Length; i += 4)
        {
            int a = atTen.Pixels[i] - before.Pixels[i];
            int b = atTwenty.Pixels[i] - before.Pixels[i];
            if (a == 0 || b == 0) continue;                       // rounding floor, no direction to compare
            if ((a > 0) == (b > 0)) agreed++; else flipped++;
        }
        Require(flipped == 0,
            $"{flipped} pixels speckled one way at 10% and the other at 20% — the sheet redraws its grain instead of "
            + $"scaling one draw ({agreed} agreed).");
        Require(twenty.magnitude > ten.magnitude,
            "doubling the amount did not scale the grain's size, so the seed check above measures nothing.");
        // Preview off puts the untouched picture back without closing the sheet; on brings the same grain back.
        addNoiseFormProbe!.PreviewSwitch.IsChecked = false;
        Require(await WaitUntilAsync(() => Differ(Current(), before) < 0, 8000),
            "taking Preview off did not put the untouched pixels back on screen.");
        addNoiseFormProbe!.PreviewSwitch.IsChecked = true;
        Require(await WaitUntilAsync(() => Differ(Current(), atTwenty) >= 0, 8000),
            "putting Preview back on did not bring the same grain back.");
        CloseFloatingPanel("addNoiseFilter", commit: false);
        Require(await WaitUntilAsync(() => addNoisePreview is null, 8000), "the cancel never finished.");
        Require(Differ(Current(), before) < 0 && !scene.CanUndo,
            "Cancel either left the grain on the layer or left history behind.");
        report["addNoiseGrainIsOneDrawHeldForTheWholeSheet"] = "passed";

        // --- What the two switches say they do ---
        scene = AddNoiseScene();
        OpenDocument(scene); await WaitForFramesAsync();
        id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        before = scene.Snapshot.Images[id]!.Clone();
        OnAddNoiseFilter(this, new RoutedEventArgs());
        await WaitForFramesAsync();
        body = FloatingPanelBody("addNoiseFilter")!;
        SliderNamed(body, Loc.T("L_Amount"))!.Value = 40;
        Require(await WaitUntilAsync(() => addNoisePreview is { Busy: false, Queued: false }
                && Differ(Current(), before) >= 0, 8000), "the 40% grain never reached the layer.");
        var color = Current().Clone();
        // A gray pixel with three independent draws stops being gray; Monochromatic promises it stays.
        int crossedR = 0, crossedG = 0;
        for (int i = 0; i < color.Pixels.Length; i += 4)
        {
            if (color.Pixels[i] != color.Pixels[i + 1]) crossedR++;
            if (color.Pixels[i + 1] != color.Pixels[i + 2]) crossedG++;
        }
        Require(crossedR + crossedG > 40,
            "three independent draws on a gray pixel produced no channel disagreement at all, so the next check measures nothing.");
        CheckBoxNamed(body, Loc.T("L_Monochromatic"))!.IsChecked = true;
        Require(await WaitUntilAsync(() => addNoisePreview is { Busy: false, Queued: false }, 8000),
            "the monochromatic tick never settled.");
        int monoCrossed = 0;
        for (int i = 0; i < Current().Pixels.Length; i += 4)
            if (Current().Pixels[i] != Current().Pixels[i + 1] || Current().Pixels[i + 1] != Current().Pixels[i + 2])
                monoCrossed++;
        Require(monoCrossed == 0,
            $"{monoCrossed} gray pixels left gray under Monochromatic; one draw has to move all channels alike.");
        CheckBoxNamed(body, Loc.T("L_Gaussian"))!.IsChecked = true;
        Require(await WaitUntilAsync(() => addNoisePreview is { Busy: false, Queued: false }, 8000),
            "the gaussian tick never settled.");
        Require(Differ(Current(), color) >= 0,
            "Gaussian distribution produced the identical picture as Uniform, so the switch changes nothing.");
        CloseFloatingPanel("addNoiseFilter", commit: false);
        await WaitUntilAsync(() => addNoisePreview is null, 8000);
        report["addNoiseSwitchesDoWhatTheySay"] = "passed";

        // --- OK has no identity rule, and the commit is one undo step ---
        scene = AddNoiseScene();
        OpenDocument(scene); await WaitForFramesAsync();
        id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        before = scene.Snapshot.Images[id]!.Clone();
        OnAddNoiseFilter(this, new RoutedEventArgs());
        await WaitForFramesAsync();
        var said = StatusText.Text;
        // The Mac's silent-commit list (Filters.swift:600-610) carries vignette, tonal contrast, bloom and the
        // like, and no addNoise entry: OK at the slider's floor runs the commit path anyway. The port's session
        // coalescing then drops a step whose picture equals the capture — at 0.1% (a 0.13-level spread) no byte
        // moves, so the visible difference between committing and quietly cancelling here is the commit line,
        // which only the commit path writes.
        SliderNamed(FloatingPanelBody("addNoiseFilter")!, Loc.T("L_Amount"))!.Value = 0.1;
        Require(await WaitUntilAsync(() => addNoisePreview is { Busy: false, Queued: false }, 8000),
            "the floor tick never settled.");
        CloseFloatingPanel("addNoiseFilter", commit: true);
        Require(await WaitUntilAsync(() => addNoisePreview is null, 8000), "the floor commit never finished.");
        Require(StatusText.Text != said && !StatusText.Text.Contains(Loc.T("S_AddNoiseFailed")),
            $"an OK at the floor did not run the commit path ({said} -> {StatusText.Text}).");
        // A grain that moves bytes lands as one step that one undo takes back.
        scene = AddNoiseScene();
        OpenDocument(scene); await WaitForFramesAsync();
        id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        before = scene.Snapshot.Images[id]!.Clone();
        OnAddNoiseFilter(this, new RoutedEventArgs());
        await WaitForFramesAsync();
        SliderNamed(FloatingPanelBody("addNoiseFilter")!, Loc.T("L_Amount"))!.Value = 20;
        Require(await WaitUntilAsync(() => addNoisePreview is { Busy: false, Queued: false }
                && Differ(Current(), before) >= 0, 8000), "the grain never reached the layer.");
        CloseFloatingPanel("addNoiseFilter", commit: true);
        Require(await WaitUntilAsync(() => addNoisePreview is null, 8000), "the commit never finished.");
        Require(scene.CanUndo, "the committed grain left no step to undo.");
        var committed = Current().Clone();
        OnUndo(this, new RoutedEventArgs());
        Require(await WaitUntilAsync(() => Differ(Current(), before) < 0, 8000),
            "one undo did not take the committed grain back.");
        Require(Differ(committed, before) >= 0, "the frame one undo removed was never different, so the check above proves nothing.");
        report["addNoiseOkHasNoIdentityRuleAndCommitsAsOneStep"] = "passed";

        // --- A selection keeps the noise inside itself ---
        var clipped = AddNoiseScene();
        OpenDocument(clipped); await WaitForFramesAsync();
        var layerId = clipped.Snapshot.Manifest.ActiveLayerId!.Value;
        var original = clipped.Snapshot.Images[layerId]!.Clone();
        var untouched = (byte[])original.Pixels.Clone();
        clipped.SetSelection(Selection.Rectangle(48, 48, 0, 0, 24, 48));
        OnAddNoiseFilter(this, new RoutedEventArgs());
        await WaitForFramesAsync();
        body = FloatingPanelBody("addNoiseFilter")!;
        Require(ContainsText(body, Loc.T("L_FilterLimitedToSelection")), "the panel ran a clipped filter without saying so.");
        SliderNamed(body, Loc.T("L_Amount"))!.Value = 40;
        CloseFloatingPanel("addNoiseFilter", commit: true);
        Require(await WaitUntilAsync(() => addNoisePreview is null
                && Differ(clipped.Snapshot.Images[layerId]!, original) >= 0, 8000),
            "the clipped grain never reached the layer.");
        int inside = 0, outside = 0;
        var result = clipped.Snapshot.Images[layerId]!;
        for (int y = 0; y < 48; y++)
            for (int x = 0; x < 48; x++)
            {
                int at = y * result.Stride + x * 4;
                bool changed = Enumerable.Range(0, 4).Any(channel => result.Pixels[at + channel] != untouched[at + channel]);
                if (x < 24) { if (changed) inside++; }
                else if (changed) outside++;
            }
        Require(outside == 0, $"{outside} pixels outside the selection were touched by the noise.");
        Require(inside > 40, $"{inside} pixels inside the selection changed, so the check above proves nothing.");
        report["addNoiseStaysInsideTheSelection"] = "passed";
    }
}
#endif
