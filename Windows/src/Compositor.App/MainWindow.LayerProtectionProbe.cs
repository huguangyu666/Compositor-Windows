#if DEBUG
using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task VerifyLayerProtectionAsync(Dictionary<string, object> report)
    {
        static void Toggle(ToggleButton button) => ((IToggleProvider)new ToggleButtonAutomationPeer(button).GetPattern(PatternInterface.Toggle)).Toggle();
        static void Invoke(MenuFlyoutItem item) => ((IInvokeProvider)new MenuFlyoutItemAutomationPeer(item).GetPattern(PatternInterface.Invoke)).Invoke();
        async Task Wait(Func<bool> predicate)
        {
            for (int i = 0; i < 400 && !predicate(); i++) await Task.Delay(5);
            Require(predicate(), "Layer protection operation did not settle."); await WaitForFramesAsync();
        }
        var scene = EditorDocument.Create(100, 80); OpenDocument(scene); await WaitForFramesAsync();
        var bottom = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        Toggle(layerLockButtons[LayerLocks.All]); await Wait(() => scene.GetLayerLocks(bottom) == LayerLocks.All);
        Require(!scene.MoveLayerBy(bottom, 5, 0) && !scene.CanUndo && !scene.IsDirty, "Locking moved content or created history/dirty state.");
        Require(layerRows[bottom].Tag is LayerRow row && row.Label.Text.Contains("\U0001F512"), "The locked row has no visible indicator.");
        Toggle(layerLockButtons[LayerLocks.All]); await Wait(() => scene.GetLayerLocks(bottom) == LayerLocks.None);
        Toggle(layerLockButtons[LayerLocks.Position]); await Wait(() => scene.GetLayerLocks(bottom) == LayerLocks.Position);
        Require(scene.BeginTransformDraft([bottom]) is null && scene.FillPixels(bottom, 255, 0, 0), "Position lock did not distinguish paint from transform.");
        report["nativeLockControls"] = "passed";

        var reloaded = new EditorDocument(scene.Snapshot); OpenDocument(reloaded); await WaitForFramesAsync();
        Require(reloaded.GetLayerLocks(bottom) == LayerLocks.Position && layerLockButtons[LayerLocks.Position].IsChecked == true,
            "Local per-project protection did not reload.");
        Require(!ManifestJson.Serialize(reloaded.Snapshot.Manifest).Contains("lock", StringComparison.OrdinalIgnoreCase), "Editor protection changed the shared project format.");
        Toggle(layerLockButtons[LayerLocks.Position]); await Wait(() => reloaded.GetLayerLocks(bottom) == LayerLocks.None);
        report["localProtectionReload"] = "passed";

        var image = new Surface(100, 80); Array.Fill(image.Pixels, (byte)255); var top = reloaded.ImportImage(image, "Foreground").Id;
        scene = new(reloaded.Snapshot); OpenDocument(scene); await WaitForFramesAsync();
        var menu = BuildCanvasLayerPicker(new(20, 20)); var entries = menu.Items.OfType<MenuFlyoutItem>().ToArray();
        Require(entries.Length == 2 && entries[0].Name.EndsWith(top.ToString("N")) && entries[1].Name.EndsWith(bottom.ToString("N")), "Canvas menu lost draw order.");
        menu.ShowAt(CanvasStage, new FlyoutShowOptions { Position = new(100, 100) }); await Task.Delay(120);
        await SaveWorkspaceCaptureAsync("layer-picker.png");
        Invoke(entries[1]); menu.Hide(); await Wait(() => selectedLayerId == bottom);
        Require(selectedLayerIds.SetEquals([bottom]) && scene.Snapshot.Manifest.ActiveLayerId == bottom && !scene.CanUndo,
            "Canvas picking changed history or left the layer panel out of sync.");
        await SaveWorkspaceCaptureAsync("layer-locks.png");
        report["nativeCanvasLayerPicker"] = "passed";

        var stale = BuildCanvasLayerPicker(new(20, 20)).Items.OfType<MenuFlyoutItem>().First();
        scene.SetOpacity(top, .5); await WaitForFramesAsync(); Invoke(stale); await WaitForFramesAsync();
        Require(selectedLayerId == bottom, "A stale canvas menu selected a changed document.");
        var blank = BuildCanvasLayerPicker(new(-100, -100)).Items.OfType<MenuFlyoutItem>().ToArray();
        Require(blank.Length == 1 && !blank[0].IsEnabled, "An empty canvas point changed the selection.");
        report["staleAndEmptyCanvasMenu"] = "passed";
    }
}
#endif
