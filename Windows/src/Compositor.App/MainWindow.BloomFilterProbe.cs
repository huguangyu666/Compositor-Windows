#if DEBUG
using System.IO;
using Compositor.Core;
using Microsoft.UI.Xaml;

namespace Compositor.App;

/// <summary>
/// Filter ▸ Bloom / Glow, measured against the Mac (Filters.swift:236-241, FilterSheet.swift:130-131,
/// :605): bright pixels cast light that stays visible past the layer's edge — the glow runs on the
/// grown grid and OK trims the result back — and Amount 0 is the identity the way vignette's is, so OK
/// there closes like Cancel without an undo step (the Mac's silent-commit list names bloomGlow).
/// </summary>
public sealed partial class MainWindow
{
    private async Task VerifyBloomFilterAsync(Dictionary<string, object> report)
    {
        var directory = Path.Combine(Path.GetTempPath(), "compositor-bloom-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            static EditorDocument LightScene(int side = 40)
            {
                var scene = EditorDocument.Create(side, side);
                var image = scene.Snapshot.Images[scene.Snapshot.Manifest.ActiveLayerId!.Value]!;
                for (int i = 0; i < image.Pixels.Length; i += 4) { image.Pixels[i] = image.Pixels[i + 1] = image.Pixels[i + 2] = 8; image.Pixels[i + 3] = 255; }
                for (int y = 18; y < 22; y++)
                    for (int x = 18; x < 22; x++)
                    { int at = y * image.Stride + x * 4; image.Pixels[at] = image.Pixels[at + 1] = image.Pixels[at + 2] = 255; }
                return scene;
            }

            var scene = LightScene();
            OpenDocument(scene); await WaitForFramesAsync();
            var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
            var before = scene.Snapshot.Images[id]!.Clone();
            Surface Current() => scene.Snapshot.Images[id]!;

            // --- The glow lights the dark around the light, on the grown grid, and commits trimmed ---
            OnBloomFilter(this, new RoutedEventArgs());
            Require(FloatingPanelIsOpen("bloomGlow"), "the Bloom command opened no panel.");
            await WaitForFramesAsync(); RootGrid.UpdateLayout();
            var body = FloatingPanelBody("bloomGlow")!;
            Require(SliderNamed(body, Loc.T("L_Amount")) is not null && SliderNamed(body, Loc.T("L_Radius")) is not null,
                "the sheet built neither the Mac's Amount nor its Radius row (FilterSheet.swift:130-131).");
            Require(!scene.CanUndo, "opening the sheet changed the document before anything was touched.");
            var amountRow = SliderNamed(body, Loc.T("L_Amount"))!;
            amountRow.Value = 100;                       // radius already holds the opening 24
            Require(await WaitUntilAsync(() => bloomPreview is { Busy: false, Queued: false }
                    && Current().Width > before.Width, 8000),
                "the glow never reached the grown grid.");
            var grown = Current().Clone();
            Require(grown.Width == before.Width + 2 * BlurMargin(24),
                $"the grid grew by {grown.Width - before.Width} px for a radius-24 glow, expected {2 * BlurMargin(24)}.");
            // The 40 px layer sits centered in the grown grid (the light was centered in the layer);
            // ten pixels right of the grown light's edge must read brighter than the far corner, which
            // the 74 px padding keeps dark.
            int grownCenter = grown.Width / 2;
            int near = grown.Pixels[(grownCenter * grown.Stride + (grownCenter + 10) * 4)];
            int corner = grown.Pixels[(2 * grown.Stride + 2 * 4)];
            Require(near > 8 && near > corner,
                $"the dark area near the light is no brighter than the corner ({near} vs {corner}): no glow.");
            CloseFloatingPanel("bloomGlow", commit: true);
            Require(await WaitUntilAsync(() => bloomPreview is null, 8000), "the commit never finished.");
            var committed = Current().Clone();
            Require(committed.Width > before.Width && committed.Width < grown.Width,
                $"OK left a {committed.Width} px grid (layer {before.Width}, grown {grown.Width}) — no trim or full grid.");
            // The trim is symmetric around the centered light, so the same ten-pixels-right-of-center
            // point is found in the committed grid at its own center.
            Require(committed.Pixels[((committed.Height / 2) * committed.Stride + (committed.Width / 2 + 10) * 4)] > 8,
                "the glow that was on screen before OK is gone after the commit.");
            Require(scene.CanUndo, "the committed glow left no step to undo.");
            var glowBytes = committed.Clone();
            OnUndo(this, new RoutedEventArgs());
            Require(await WaitUntilAsync(() => Differ(Current(), before) < 0 && Current().Width == before.Width, 8000),
                "one undo did not restore both the pixels and the placement.");
            Require(Differ(glowBytes, before) >= 0, "the frame one undo removed never glowed.");
            report["theGlowLightsTheDarkAroundTheLightAndCommitsTrimmed"] = "passed";

            // --- Amount 0 is the identity: OK closes like Cancel, no step, no bigger grid ---
            scene = LightScene();
            OpenDocument(scene); await WaitForFramesAsync();
            id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
            before = scene.Snapshot.Images[id]!.Clone();
            OnBloomFilter(this, new RoutedEventArgs());
            await WaitForFramesAsync();
            var said = StatusText.Text;
            body = FloatingPanelBody("bloomGlow")!;
            SliderNamed(body, Loc.T("L_Amount"))!.Value = 0;
            Require(await WaitUntilAsync(() => bloomPreview is { Busy: false, Queued: false }, 8000),
                "the zero tick never settled.");
            CloseFloatingPanel("bloomGlow", commit: true);
            Require(await WaitUntilAsync(() => bloomPreview is null, 8000), "the silent OK never finished.");
            Require(StatusText.Text == said && !scene.CanUndo && Differ(Current(), before) < 0
                    && Current().Width == before.Width,
                $"OK at Amount 0 committed something ({said} -> {StatusText.Text}, width {Current().Width}).");
            report["okAtZeroAmountClosesTheWayCancelDoes"] = "passed";

            // --- Cancel puts the whole grid back, no history ---
            scene = LightScene();
            OpenDocument(scene); await WaitForFramesAsync();
            id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
            before = scene.Snapshot.Images[id]!.Clone();
            OnBloomFilter(this, new RoutedEventArgs());
            await WaitForFramesAsync();
            SliderNamed(FloatingPanelBody("bloomGlow")!, Loc.T("L_Amount"))!.Value = 60;
            Require(await WaitUntilAsync(() => bloomPreview is { Busy: false, Queued: false }
                    && Current().Width > before.Width, 8000), "the preview grid never landed.");
            CloseFloatingPanel("bloomGlow", commit: false);
            Require(await WaitUntilAsync(() => bloomPreview is null, 8000), "the cancel never finished.");
            Require(Differ(Current(), before) < 0 && !scene.CanUndo,
                "Cancel left a bigger grid, a moved placement, or a history step behind.");
            report["cancelRestoresTheGlowsWholeGrid"] = "passed";

            // --- The sheet bars the tab switch while it is open (canSwitch, and the settle covers it) ---
            var otherTab = new ProjectWorkspaceTab { Document = EditorDocument.Create(64, 48) };
            workspace.Tabs.Add(otherTab);
            try
            {
                OnBloomFilter(this, new RoutedEventArgs());
                await WaitForFramesAsync();
                Require(ProjectEditsInFlight, "the Bloom sheet left the tab gate open.");
                Require(!SwitchProjectTab(otherTab.Id), "a tab switch ran across the open Bloom sheet.");
                var walked = await ConfirmCloseAllProjectsAsync();
                Require(walked && bloomPreview is null && !FloatingPanelIsOpen("bloomGlow"),
                    $"the quit did not settle the sheet before walking (walked={walked}, panel={FloatingPanelIsOpen("bloomGlow")}).");
                ResetTabsForProbe();
            }
            finally
            {
                if (workspace.Tabs.Contains(otherTab)) workspace.Tabs.Remove(otherTab);
            }
            report["theBloomSheetRefusesSwitchAndQuitSettlesIt"] = "passed";

            // One filter sheet at a time — the Mac's filterEdit == nil (Filters.swift:481; the menu greys
            // every Filter command through the same while one is open). Two sheets stacked would each
            // hold their own frozen capture of the same layer and the later OK would publish onto the
            // picture the earlier one already rewrote; the original refuses the second. Driven through
            // the real menu entries so the panels themselves are not bypassed.
            foreach (var (openFirst, firstKey, openSecond, secondKey) in new (Action, string, Action, string)[]
            {
                (() => OnTonalContrast(this, new RoutedEventArgs()), "tonalContrast",
                 () => OnGaussianBlurFilter(this, new RoutedEventArgs()), "gaussianBlur"),
                (() => OnBloomFilter(this, new RoutedEventArgs()), "bloomGlow",
                 () => OnVignette(this, new RoutedEventArgs()), "vignette"),
                (() => OnLensCorrection(this, new RoutedEventArgs()), "lensCorrection",
                 () => OnMotionBlurFilter(this, new RoutedEventArgs()), "motionBlur"),
            })
            {
                ResetTabsForProbe();
                var owned = EditorDocument.Create(48, 48);
                OpenDocument(owned); await WaitForFramesAsync();
                openFirst();
                Require(FloatingPanelIsOpen(firstKey), $"the {firstKey} fixture opened no panel.");
                openSecond();
                await WaitForFramesAsync();
                Require(!FloatingPanelIsOpen(secondKey),
                    $"the {secondKey} sheet opened over the open {firstKey} — the Mac refuses a second filter edit.");
                Require(FloatingPanelIsOpen(firstKey),
                    $"the refused {secondKey} took the open {firstKey} sheet down with it.");
                CloseFloatingPanel(firstKey, commit: false);
                await WaitUntilAsync(() => tonalPreview is null && vignettePreview is null && lensPreview is null
                    && gaussianPreview is null && motionBlurPreview is null && bloomPreview is null, 8000);
            }
            ResetTabsForProbe();
            report["aSecondFilterSheetNeverOpensOverTheFirst"] = "passed";
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }
}
#endif
