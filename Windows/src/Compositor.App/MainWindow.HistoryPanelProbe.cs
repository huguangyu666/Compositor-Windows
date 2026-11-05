#if DEBUG
using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task VerifyHistoryPanelAsync(Dictionary<string, object> report)
    {
        async Task Wait(Func<bool> ready)
        {
            for (int i = 0; i < 400 && !ready(); i++) await Task.Delay(5);
            Require(ready(), "History panel operation did not settle."); await WaitForFramesAsync();
        }
        async Task SelectState(Guid id)
        {
            var row = historyList.Items.Cast<HistoryRow>().Single(row => row.Id == id);
            historyList.ScrollIntoView(row); RootGrid.UpdateLayout(); await Task.Delay(30);
            historyList.SelectedItem = row;
            await Wait(() => document?.CurrentHistoryId == id);
        }
        var scene = EditorDocument.Create(96, 64); OpenDocument(scene); await WaitForFramesAsync();
        var id = scene.Snapshot.Manifest.ActiveLayerId!.Value; Guid initial = scene.CurrentHistoryId;
        var original = scene.Snapshot.Images[id];
        scene.FillPixels(id, 220, 30, 40); scene.MoveLayerBy(id, 8, 4); scene.RenameLayer(id, "Foreground");
        Guid latest = scene.CurrentHistoryId; string final = ManifestJson.Serialize(scene.Snapshot.Manifest);
        ((IToggleProvider)new ToggleButtonAutomationPeer(historyTab).GetPattern(PatternInterface.Toggle)).Toggle();
        await Wait(() => historyVisible && historyList.Items.Count == 4);
        Require(historyList.Items.Cast<HistoryRow>().Select(row => row.Label).Contains("1  " + Loc.T("H_Fill")), "The fill step has no clear operation label.");
        RootGrid.UpdateLayout();
        await Wait(() => FilterFinishDescendants(historyList).OfType<TextBlock>().Any(text => text.Text.Contains(Loc.T("H_Fill"))));
        await SelectState(initial);
        Require(ReferenceEquals(original, scene.Snapshot.Images[id]) && !scene.CanUndo && scene.CanRedo, "History row did not restore source pixels and redo.");
        Require(selectedLayerIds.SetEquals([id]) && selectedLayerId == id, "History navigation left stale layer selection.");
        await SelectState(latest);
        Require(final == ManifestJson.Serialize(scene.Snapshot.Manifest) && !scene.CanRedo, "History redo failed to restore the latest state.");
        await Task.Delay(250); RootGrid.UpdateLayout();
        await SaveWorkspaceCaptureAsync("history-panel.png");
        report["nativeTimelineNavigation"] = "passed";

        scene.SetSelectionUndoable(Selection.Rectangle(96, 64, 3, 4, 20, 18)); await WaitForSelectionReadyAsync();
        Guid selected = scene.CurrentHistoryId; await Wait(() => historyList.Items.Cast<HistoryRow>().Any(row => row.Id == selected));
        await SelectState(latest); Require(scene.ActiveSelection is null, "Timeline retained a later selection.");
        await SelectState(selected); Require(scene.ActiveSelection?.Bounds.Width == 20, "Timeline failed to restore selection coverage.");
        await SelectState(latest); scene.SetOpacity(id, .5); await WaitForFramesAsync();
        await Wait(() => historyList.Items.Cast<HistoryRow>().All(row => row.Id != selected));
        Require(!scene.CanRedo, "A branched history retained redo.");
        report["selectionAndBranchHistory"] = "passed";

        var other = EditorDocument.Create(32, 24); OpenDocument(other); await WaitForFramesAsync();
        await Wait(() => historyList.Items.Count == 1 && historyListOwner == other);
        NavigateHistory(initial); await WaitForFramesAsync();
        Require(other.CurrentHistoryId != initial && !other.CanUndo, "Another document accepted a foreign history row.");
        OpenDocument(scene); await WaitForFramesAsync(); await Wait(() => historyListOwner == scene);
        Require(historyList.Items.Count == scene.HistoryStates.Count, "History panel did not rebind to the returning document.");
        report["documentHistoryOwnership"] = "passed";

        var text = scene.AddTextLayer(new() { Content = "Draft", FontSize = 16 }, new(5, 10))!;
        await WaitForFramesAsync(); Guid before = scene.CurrentHistoryId;
        OpenInlineText(text); await WaitForFramesAsync(); NavigateHistory(initial); await WaitForFramesAsync();
        Require(scene.ActiveTextDraft is not null && scene.CurrentHistoryId == before, "Timeline navigation discarded an open text draft.");
        CloseInlineText(cancelling: true); ShowHistoryPanel(false); await WaitForFramesAsync();
        Require(LayerList.Visibility == Visibility.Visible && LayerAppearance.Visibility == Visibility.Visible && historyHost.Visibility == Visibility.Collapsed,
            "Returning to Layers left the editing controls hidden.");
        report["draftProtectionAndLayersReturn"] = "passed";
    }
}
#endif
