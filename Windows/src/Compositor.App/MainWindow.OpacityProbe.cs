#if DEBUG
using Compositor.Core;
using Windows.System;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task VerifyOpacityWorkflowAsync(Dictionary<string, object> report)
    {
        var scene = EditorDocument.Create(32, 32); OpenDocument(scene); await WaitForFramesAsync();
        OpacityBox.Value = 65; await WaitForFramesAsync();
        OpacityBox.Value = 40; await WaitForFramesAsync();
        OnUndo(this, new()); await WaitForFramesAsync();
        double restored = SelectedLayer?.Opacity ?? 1;
        report["opacityAfterUndoSecondFieldEdit"] = restored;
        Require(Math.Abs(restored - .65) < .001, $"Separate opacity field edits merged into one Undo (restored {restored}).");
        OnUndo(this, new()); await WaitForFramesAsync();
        Require(!scene.CanUndo && (SelectedLayer?.Opacity ?? 1) == 1, "Opacity field edits left extra history.");
        report["opacityNumericEditsUndoIndividually"] = "passed";

        scene = EditorDocument.Create(64, 64); scene.ImportImage(new Surface(8, 8, Enumerable.Repeat((byte)255, 8 * 8 * 4).ToArray(), 32), "Small");
        scene = new(scene.Snapshot); OpenDocument(scene); await WaitForFramesAsync();
        Require(BeginOpacityEdit(), "The opacity slider could not start its gesture.");
        OpacitySlider.Value = 80; await WaitForFramesAsync(); var warm = compositor; var rows = layerRows.Values.ToArray();
        OpacitySlider.Value = 60; await WaitForFramesAsync(); OpacitySlider.Value = 35; await WaitForFramesAsync(); EndOpacityEdit();
        Require(ReferenceEquals(warm, compositor) && rows.All(row => layerRows.Values.Contains(row)), "Opacity ticks rebuilt the compositor or layer rows.");
        AssertUploadedFrame(scene); OnUndo(this, new()); await WaitForFramesAsync();
        Require(!scene.CanUndo && (SelectedLayer?.Opacity ?? 1) == 1, "Slider ticks did not undo as one gesture.");
        report["opacitySliderKeepsWarmFramesAndOneUndo"] = "passed";

        scene = EditorDocument.Create(32, 32); OpenDocument(scene); await WaitForFramesAsync();
        Require(BeginOpacityScrub(100), "The opacity label did not start its scrub.");
        MoveOpacityScrub(100.5); Require(!scene.CanUndo, "Sub-threshold label movement created history.");
        MoveOpacityScrub(90.5); MoveOpacityScrub(80.25); EndOpacityEdit(); await WaitForFramesAsync();
        Require(Math.Abs((SelectedLayer?.Opacity ?? 1) - .8025) < .000001, "The scrub changed one-percent-per-point or fractional values.");
        OnUndo(this, new()); await WaitForFramesAsync(); Require(!scene.CanUndo, "One label scrub added multiple Undo steps.");
        Require(BeginOpacityScrub(100), "The no-op scrub did not start.");
        MoveOpacityScrub(90); MoveOpacityScrub(100); EndOpacityEdit(); await WaitForFramesAsync();
        Require(!scene.CanUndo && !scene.IsDirty, "Returning the label scrub to its start retained a no-op Undo.");
        report["opacityLabelScrubPreservesFractionsThresholdAndNoOpHistory"] = "passed";

        OpacityBox.Value = 50; await WaitForFramesAsync();
        Require(HandleOpacityFieldKey(VirtualKey.Up, 0) && HandleOpacityFieldKey(VirtualKey.Down, VirtualKeyModifiers.Shift), "Opacity arrows were not handled.");
        await WaitForFramesAsync(); Require(Math.Abs((SelectedLayer?.Opacity ?? 1) - .41) < .001, "Opacity arrows lost one/ten-percent stepping.");
        OnUndo(this, new()); await WaitForFramesAsync(); Require(Math.Abs((SelectedLayer?.Opacity ?? 1) - .51) < .001, "Shift arrow Undo skipped another edit.");
        OnUndo(this, new()); await WaitForFramesAsync(); Require(Math.Abs((SelectedLayer?.Opacity ?? 1) - .5) < .001, "Arrow Undo skipped the typed value.");
        OpacityBox.Text = "invalid"; FinishOpacityField(); await WaitForFramesAsync();
        Require(OpacityBox.Value == 50 && (SelectedLayer?.Opacity ?? 1) == .5, "Invalid opacity text changed content or failed to restore the field.");
        OpacityBox.Text = "37.5"; Require(HandleOpacityFieldKey(VirtualKey.Enter, 0), "Enter did not commit the opacity field.");
        await WaitForFramesAsync(); Require((SelectedLayer?.Opacity ?? 1) == .375 && OpacitySlider.Value == 37.5, "Typed fractional opacity did not commit.");
        report["opacityFieldArrowsFractionsAndInvalidText"] = "passed";

        scene = EditorDocument.Create(32, 32); var bottom = scene.Snapshot.Manifest.Layers[0].Id;
        var top = scene.ImportImage(new Surface(8, 8), "Top").Id; scene = new(scene.Snapshot);
        OpenDocument(scene); await WaitForFramesAsync(); Require(BeginOpacityEdit(), "The layer-switch fixture did not start.");
        OpacitySlider.Value = 75; await WaitForFramesAsync(); SelectLayerRow(bottom); await WaitForFramesAsync();
        Require(opacityEdit is null && SelectedLayer?.Id == bottom, "Selecting another layer retained the old opacity gesture.");
        OpacityBox.Value = 45; await WaitForFramesAsync(); OnUndo(this, new()); await WaitForFramesAsync();
        Require((scene.Snapshot.Manifest.Layers.Single(layer => layer.Id == bottom).Opacity ?? 1) == 1
            && scene.Snapshot.Manifest.Layers.Single(layer => layer.Id == top).Opacity == .75, "Opacity Undo crossed layer boundaries.");
        Require(BeginOpacityScrub(100), "The owner-retirement scrub did not start."); MoveOpacityScrub(90);
        var replacement = EditorDocument.Create(16, 16); OpenDocument(replacement); MoveOpacityScrub(80); EndOpacityEdit(); await WaitForFramesAsync();
        Require(!replacement.CanUndo && (SelectedLayer?.Opacity ?? 1) == 1, "Retired opacity work changed the replacement document.");
        report["opacitySessionsEndAtLayerAndDocumentBoundaries"] = "passed";
    }
}
#endif
