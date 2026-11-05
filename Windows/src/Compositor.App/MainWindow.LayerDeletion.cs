using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Compositor.App;

public sealed partial class MainWindow
{
    // Shared ownership for deletion and selection-plane preparation, including new masks.
    private CancellationTokenSource? deletionCancellation;
    private bool IsDeletePending => deletionCancellation is not null;
#if DEBUG
    private Func<Task>? beforeLayerDeletionBake;
#endif

    private async Task<bool> DeleteSelectedLayersAsync()
    {
        // Deletion joins the serial queue after pending selection input, while active drafts
        // and other editing operations still refuse it.
        if (IsDeletePending || !CanEditSelectionState || pixelMove is not null || pixelNudgePending > 0
            || cropRectangle is not null || document is not { } owner) return false;
        // Native selection can be newer than the editor while its earlier render is retiring.
        var ids = LayerList.SelectedItems.OfType<ListViewItem>().Select(item => ((LayerRow)item.Tag).Layer.Id).ToArray();
        if (ids.Length == 0) return false;
        var primary = selectedLayerIds.SetEquals(ids) && selectedLayerId is { } selected && ids.Contains(selected) ? selectedLayerId : ids[^1];
        layerSelectionRequest++; layerSelectionPointerTarget = null;
        using var cancellation = new CancellationTokenSource();
        deletionCancellation = cancellation;
        bool Current() => !windowClosed && ReferenceEquals(document, owner)
            && ReferenceEquals(deletionCancellation, cancellation) && !cancellation.IsCancellationRequested;
        SetBusy(true); RefreshFileImportAvailability();
        try
        {
            var captured = new TaskCompletionSource<LayerDeletion?>(TaskCreationOptions.RunContinuationsAsynchronously);
            QueueCanvasInput(owner, () =>
            {
                try
                {
                    if (!Current()) { captured.TrySetResult(null); return; }
                    // Adopt the visible selection before capturing metadata, so cancelling the
                    // dependency choice also leaves the editor and native rows in agreement.
                    selectedLayerIds.Clear(); selectedLayerIds.UnionWith(ids);
                    if (selectedLayerId != primary || ids.Length != 1) { paintOnMask = false; PaintMaskCheck.IsChecked = false; }
                    selectedLayerId = primary;
                    selectingLayer = true;
                    try { owner.SetActiveLayer(primary); } finally { selectingLayer = false; }
                    RefreshLayerPanel();
                    captured.TrySetResult(owner.BeginLayerDeletion(ids));
                }
                catch (Exception ex) { captured.TrySetException(ex); }
            });
            var edit = await captured.Task.WaitAsync(cancellation.Token);
            if (!Current() || edit is null) return false;
            bool bake = false;
            if (edit.DependentLayerIds.Count > 0)
            {
                var dialog = new ContentDialog
                {
                    Title = Loc.T("Dlg_DeleteLiveSource"),
                    Content = new TextBlock { Text = Loc.T("Dlg_DeleteLiveBody")
                        + (edit.HasAdjustmentDependents ? "\n\n" + Loc.T("Dlg_DeleteLiveAdjustments") : ""), TextWrapping = TextWrapping.Wrap },
                    PrimaryButtonText = Loc.T("Dlg_BakeAndDelete"),
                    SecondaryButtonText = Loc.T("Dlg_UnlinkAndDelete"),
                    CloseButtonText = Loc.T("T_Dlg_Cancel"),
                    DefaultButton = ContentDialogButton.Primary, XamlRoot = RootGrid.XamlRoot,
                };
                var answer = await ShowProjectDialogAsync(dialog);
                if (!Current() || answer == ContentDialogResult.None) return false;
                bake = answer == ContentDialogResult.Primary;
            }
            LayerDeletionResult prepared;
            if (bake)
            {
                SetStatus(Loc.T("S_BakingDeletion"));
#if DEBUG
                var beforeBake = beforeLayerDeletionBake;
#endif
                prepared = await Task.Run(
#if DEBUG
                async () =>
#else
                () =>
#endif
                {
#if DEBUG
                    if (beforeBake is not null) await beforeBake();
#endif
                    return edit.Prepare(bake: true, cancellation.Token);
                }, cancellation.Token);
            }
            else prepared = edit.Prepare(bake: false, cancellation.Token);
            if (!Current()) return false;
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            QueueCanvasInput(owner, () =>
            {
                try
                {
                    bool applied = Current() && owner.CommitLayerDeletion(prepared);
                    if (applied)
                    {
                        selectedLayerIds.Clear(); selectedLayerId = owner.Snapshot.Manifest.ActiveLayerId;
                        if (selectedLayerId is { } id) selectedLayerIds.Add(id);
                        paintOnMask = false; PaintMaskCheck.IsChecked = false;
                        RefreshLayerPanel();
                    }
                    completed.TrySetResult(applied);
                }
                catch (Exception ex) { completed.TrySetException(ex); }
            });
            bool committed = await completed.Task.WaitAsync(cancellation.Token);
            if (Current()) SetStatus(committed ? Loc.F("S_LayersDeleted", edit.RemovedCount) : Loc.T("S_DeleteSourceChanged"));
            return committed;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return false; }
        catch (Exception ex)
        {
            if (Current()) { App.Log($"Layer deletion failed: {ex}"); SetStatus(Loc.T("S_LayerDeletionFailed")); }
            return false;
        }
        finally
        {
            if (ReferenceEquals(deletionCancellation, cancellation))
            {
                deletionCancellation = null;
                if (!windowClosed) { SetBusy(false); RefreshFileImportAvailability(); }
            }
        }
    }

    private void RetireDeletion()
    {
        var cancellation = deletionCancellation;
        deletionCancellation = null;
        cancellation?.Cancel();
        if (cancellation is not null && !windowClosed) { SetBusy(false); RefreshFileImportAvailability(); }
    }
}
