#if DEBUG
using Compositor.Core;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task VerifyLayerDragLifetimeAsync(Dictionary<string, object> report)
    {
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        static EditorDocument Scene()
        {
            var scene = EditorDocument.Create(32, 24); var id = scene.Snapshot.Manifest.Layers[0].Id;
            scene.SetLayerEffects(id, new() { Stroke = new() { Enabled = false } });
            scene.ImportImage(new Surface(8, 8), "Target");
            return new(scene.Snapshot);
        }
        Point InRow(Guid id, double fraction = .5)
        {
            RootGrid.UpdateLayout(); var item = layerRows[id];
            Require(item.ActualHeight > 0, "The drop target is not measured.");
            return item.TransformToVisual(RootGrid).TransformPoint(new(item.ActualWidth / 2, item.ActualHeight * fraction));
        }
        List<object> Items(Guid id) => new() { layerRows[id] };
        LayerPanelDrag BeginRows(Guid id)
        {
            SelectLayerRow(id); RefreshLayerPanel();
            return BeginLayerRowsDrag(Items(id)) ?? throw new InvalidOperationException("The production row drag refused its source.");
        }

        foreach (bool effect in new[] { false, true })
        {
            var scene = Scene(); var source = scene.Snapshot.Manifest.Layers[0].Id; var target = scene.Snapshot.Manifest.Layers[1].Id;
            OpenDocument(scene); await WaitForFramesAsync();
            var drag = effect ? BeginLayerEffectDrag(LayerEffectKind.Stroke, source)! : BeginRows(source);
            Require(drag is not null, "The drag fixture did not begin.");
            var replacement = new EditorDocument(scene.Snapshot); OpenDocument(replacement); await WaitForFramesAsync();
            Require(layerPanelDrag is null && LayerPanelDropOperation(InRow(target), false) == DataPackageOperation.None
                && !CompleteLayerPanelDrop(InRow(target), effect ? DataPackageOperation.Copy : DataPackageOperation.Move)
                && !replacement.CanUndo && !scene.CanUndo, "Document replacement retained an old drag or allowed it to change matching layer IDs.");
        }
        report["documentReplacementRetiresLayerPanelDrag"] = "passed";

        var active = Scene(); OpenDocument(active); await WaitForFramesAsync();
        var sourceId = active.Snapshot.Manifest.Layers[0].Id; var targetId = active.Snapshot.Manifest.Layers[1].Id;
        var oldRows = Items(sourceId); var old = BeginRows(sourceId);
        CompleteLayerRowsDrag(oldRows);
        Require(layerPanelDrag is null && LayerPanelDropOperation(InRow(targetId), false) == DataPackageOperation.None && !active.CanUndo,
            "Native drag cancellation retained its payload or created history.");
        var oldEffect = BeginLayerEffectDrag(LayerEffectKind.Stroke, sourceId)!;
        OpenDocument(new EditorDocument(active.Snapshot)); await WaitForFramesAsync();
        var newerRows = BeginRows(sourceId); CompleteLayerRowsDrag(oldRows); EndLayerPanelDrag(oldEffect);
        Require(ReferenceEquals(newerRows, layerPanelDrag), "Old drag completion cleared the newer native session.");
        var newerEffect = BeginLayerEffectDrag(LayerEffectKind.Stroke, sourceId)!;
        CompleteLayerRowsDrag(Items(sourceId)); EndLayerPanelDrag(oldEffect);
        Require(ReferenceEquals(newerEffect, layerPanelDrag), "An unrelated completion cleared the newer effect session.");
        EndLayerPanelDrag(newerEffect);
        report["cancelAndOldCompletionCannotReuseOrClearNewPayload"] = "passed";

        active = Scene(); OpenDocument(active); await WaitForFramesAsync();
        sourceId = active.Snapshot.Manifest.Layers[0].Id; targetId = active.Snapshot.Manifest.Layers[1].Id;
        Require(LayerPanelDropOperation(InRow(targetId), false) == DataPackageOperation.None, "An unrelated drag was offered a layer placement.");
        var warm = compositor; int frames = presentedFrameCount;
        BeginRows(sourceId);
        Require(LayerPanelDropOperation(InRow(targetId), false) == DataPackageOperation.Move
            && LayerPanelDropOperation(InRow(targetId), true) == DataPackageOperation.Copy && !active.CanUndo
            && ReferenceEquals(warm, compositor) && frames == presentedFrameCount, "Hover changed pixels/history or did not offer move/copy.");
        Require(CompleteLayerPanelDrop(InRow(targetId), DataPackageOperation.Move), "The captured row drag did not move."); await WaitForFramesAsync();
        Require(((LayerRow)layerItems[0].Tag).Layer.Id == sourceId && layerPanelDrag is null, "The row drop landed incorrectly or retained its session.");
        AssertUploadedFrame(active); OnUndo(this, new()); await WaitForFramesAsync(); Require(!active.CanUndo, "The row drop did not undo once.");
        BeginRows(sourceId); Require(CompleteLayerPanelDrop(InRow(targetId), DataPackageOperation.Copy), "The captured copy drag did not copy."); await WaitForFramesAsync();
        Require(active.LayerCount == 3 && layerPanelDrag is null, "Copy lost the original or retained its session.");
        OnUndo(this, new()); await WaitForFramesAsync(); Require(active.LayerCount == 2 && !active.CanUndo, "The row copy did not undo once.");
        report["ownedRowDropKeepsMoveCopyAndSingleUndo"] = "passed";

        var effectDrag = BeginLayerEffectDrag(LayerEffectKind.Stroke, sourceId)!;
        Require(LayerPanelDropOperation(InRow(sourceId), false) == DataPackageOperation.None
            && LayerPanelDropOperation(InRow(targetId), false) == DataPackageOperation.Copy, "An effect offered its source as a destination or refused its target.");
        Require(CompleteLayerPanelDrop(InRow(targetId), DataPackageOperation.Copy), "The captured effect drag did not copy."); await WaitForFramesAsync();
        Require(LayerEffects.Parse(active.Snapshot.Manifest.Layers.Single(layer => layer.Id == targetId).Effects)?.Stroke?.Enabled == false
            && LayerEffects.Parse(active.Snapshot.Manifest.Layers.Single(layer => layer.Id == sourceId).Effects)?.Stroke is not null
            && SelectedEffect == (targetId, LayerEffectKind.Stroke), "The effect drop lost visibility/source parameters or targeted the wrong effect.");
        OnUndo(this, new()); await WaitForFramesAsync(); Require(!active.CanUndo, "Effect drop did not undo once.");
        report["ownedEffectDropCopiesSettingsAndRetiresSession"] = "passed";

        BeginRows(sourceId);
        var outside = LayerList.TransformToVisual(RootGrid).TransformPoint(new(-5, 10));
        Require(LayerPanelDropOperation(outside, false) == DataPackageOperation.None
            && !CompleteLayerPanelDrop(outside, DataPackageOperation.Move) && layerPanelDrag is null && !active.CanUndo,
            "An outside drop modified the document or retained the session.");
        BeginRows(sourceId);
        Require(!CompleteLayerPanelDrop(InRow(targetId), DataPackageOperation.None) && layerPanelDrag is null && !active.CanUndo,
            "An unaccepted drop was committed.");
        report["outsideAndUnacceptedDropsRetireWithoutEditing"] = "passed";
    }
}
#endif
