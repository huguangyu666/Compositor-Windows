#if DEBUG
using Compositor.Core;
using Windows.System;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task VerifyOpacityKeysAsync(Dictionary<string, object> report)
    {
        async Task Tool(CanvasTool value)
        {
            ToolCombo.SelectedIndex = (int)value;
            Require(await WaitUntilAsync(() => tool == value && toolChangeApplied == toolChangeRevision, 2000), "The opacity-key tool did not settle.");
            await WaitForFramesAsync(); pendingOpacityDigit = null;
        }
        bool Digit(int digit, long time) => HandleOpacityKey(VirtualKey.Number0 + digit, 0, typing: false, at: time);
        var scene = EditorDocument.Create(32, 32); OpenDocument(scene); await WaitForFramesAsync();
        foreach (var target in new[] { CanvasTool.Brush, CanvasTool.Eraser, CanvasTool.Clone, CanvasTool.Blur, CanvasTool.Healing, CanvasTool.Gradient })
        {
            await Tool(target);
            Require(Digit(4, 1000) && Digit(5, 1100), "A supported tool refused opacity digits.");
            double actual = target == CanvasTool.Gradient ? GradientOpacitySlider.Value : BrushOpacitySlider.Value;
            Require(actual == 45 && !scene.CanUndo && !scene.IsDirty, $"{target} opacity digits changed history or did not reach 45%.");
        }
        report["opacityDigitsReachAllBrushToolsAndGradientWithoutHistory"] = "passed";

        await Tool(CanvasTool.Brush);
        Require(Digit(0, 2000) && BrushOpacitySlider.Value == 100 && Digit(5, 2599) && BrushOpacitySlider.Value == 5,
            "Leading zero or the 599ms pair changed its percentage.");
        Require(Digit(4, 3000) && Digit(5, 3600) && BrushOpacitySlider.Value == 50, "The 600ms boundary still combined digits.");
        pendingOpacityDigit = null;
        Require(Digit(0, 4000) && Digit(0, 4100) && BrushOpacitySlider.Value == 1, "00 did not retain the original 1% minimum.");
        report["opacityDigitsKeepLeadingZeroTimeoutAndMinimum"] = "passed";

        scene.ImportImage(new Surface(8, 8), "Second"); scene = new(scene.Snapshot); OpenDocument(scene); await WaitForFramesAsync();
        await Tool(CanvasTool.Move); LayerList.SelectAll();
        Require(await WaitUntilAsync(() => selectedLayerIds.Count == 2, 2000), "The opacity multi-selection fixture did not select both rows.");
        await WaitForFramesAsync(); var warm = compositor; var rows = layerRows.Values.ToArray(); var images = scene.Snapshot.Images;
        Require(Digit(5, 5000), "Move refused layer opacity digits."); await WaitForFramesAsync();
        AssertUploadedFrame(scene); Require(scene.Snapshot.Manifest.Layers.All(layer => layer.Opacity == .5), "Move did not set every selected layer to 50%.");
        Require(Digit(5, 5100), "Move refused the second opacity digit."); await WaitForFramesAsync();
        Require(scene.Snapshot.Manifest.Layers.All(layer => layer.Opacity == .55) && ReferenceEquals(images, scene.Snapshot.Images)
            && ReferenceEquals(warm, compositor) && rows.All(row => layerRows.Values.Contains(row)), "Multi-layer opacity changed sources or discarded warm caches.");
        AssertUploadedFrame(scene); OnUndo(this, new()); await WaitForFramesAsync();
        Require(scene.Snapshot.Manifest.Layers.All(layer => layer.Opacity == .5), "One multi-layer digit was not one Undo.");
        OnUndo(this, new()); await WaitForFramesAsync();
        Require(!scene.CanUndo && scene.Snapshot.Manifest.Layers.All(layer => (layer.Opacity ?? 1) == 1), "Layer opacity digits left extra history.");
        report["moveOpacityDigitsUpdateSelectedLayersAtomicallyAndKeepCaches"] = "passed";

        await Tool(CanvasTool.Brush); pendingOpacityDigit = null;
        bool hadBinding = shortcutChords.TryGetValue("canvas:OpacityDigit.5", out var previousBinding);
        try
        {
            shortcutChords["canvas:OpacityDigit.5"] = new(VirtualKey.O, 0);
            Require(!Digit(5, 6000) && HandleOpacityKey(VirtualKey.O, 0, false, 6000) && BrushOpacitySlider.Value == 50,
                "Rebinding an opacity digit left the old key active or lost the new key.");
        }
        finally
        {
            if (hadBinding) shortcutChords["canvas:OpacityDigit.5"] = previousBinding;
            else shortcutChords.Remove("canvas:OpacityDigit.5");
        }
        report["opacityDigitsUseExistingRebindTable"] = "passed";

        pendingOpacityDigit = null; BrushOpacitySlider.Value = 75;
        foreach (var modifier in new[] { VirtualKeyModifiers.Control, VirtualKeyModifiers.Menu, VirtualKeyModifiers.Windows })
            Require(!HandleOpacityKey(VirtualKey.Number2, modifier, false, 7000), "A modified opacity digit intercepted a menu/system chord.");
        Require(!HandleOpacityKey(VirtualKey.Number2, 0, true, 7000) && BrushOpacitySlider.Value == 75, "Opacity digits intercepted text input.");
        // The active-stroke guard needs a valid single painting target, independently
        // of the earlier multi-layer opacity fixture.
        SelectLayerRow(scene.Snapshot.Manifest.ActiveLayerId!.Value); await WaitForFramesAsync();
        BeginPointerStroke(new(16, 16)); await WaitForFramesAsync();
        Require(pointerStroke is not null && !Digit(2, 7000) && BrushOpacitySlider.Value == 75, "Opacity digits edited an active stroke's settings.");
        FinishPointerStroke(); await WaitForFramesAsync();
        await Tool(CanvasTool.RectSelect); Require(!Digit(2, 8000), "A selection tool consumed opacity digits.");
        await Tool(CanvasTool.Move); Require(BeginTransformInspector(persistent: true), "The busy opacity fixture did not start its transform.");
        Require(!Digit(2, 9000), "A transform draft accepted a content opacity edit."); QueueTransformCancel(); await WaitForFramesAsync();
        pendingOpacityDigit = null;
        report["opacityDigitsRespectTextModifiersStrokeAndDraftGuards"] = "passed";
    }
}
#endif
