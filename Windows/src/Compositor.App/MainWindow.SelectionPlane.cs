using Compositor.Core;

namespace Compositor.App;

public sealed partial class MainWindow
{
#if DEBUG
    private Action? beforePlaneClearCompute;
    private Action? beforeSelectionMaskCompute;
#endif

    private Task<bool> ClearSelectionPlaneAsync(EditorDocument owner, Guid id, bool onMask, Selection selection, byte level)
        => ApplySelectionPlaneAsync(owner, id, onMask, selection, level);

    private async Task<bool> ApplySelectionPlaneAsync(EditorDocument owner, Guid id, bool onMask, Selection selection, byte level, bool createMask = false)
    {
        using var cancellation = new CancellationTokenSource();
        deletionCancellation = cancellation;
        bool Current() => !windowClosed && ReferenceEquals(document, owner)
            && ReferenceEquals(deletionCancellation, cancellation) && !cancellation.IsCancellationRequested;
        SetBusy(true); RefreshFileImportAvailability();
        try
        {
            var captured = new TaskCompletionSource<SelectionPlaneEdit?>(TaskCreationOptions.RunContinuationsAsynchronously);
            QueueCanvasInput(owner, () =>
            {
                try
                {
                    if (!Current() || !ReferenceEquals(selection, owner.ActiveSelection)
                        || !createMask && !DrawOrder.Resolve(owner.Snapshot.Manifest.Layers).Visible.Contains(id))
                    { captured.TrySetResult(null); return; }
                    selectedLayerIds.Clear(); selectedLayerIds.Add(id); selectedLayerId = id;
                    if (!createMask) { paintOnMask = onMask; SyncPaintTargetControls(); }
                    selectingLayer = true;
                    try { owner.SetActiveLayer(id); } finally { selectingLayer = false; }
                    RefreshLayerPanel();
                    captured.TrySetResult(createMask ? owner.BeginSelectionMaskEdit(id, level != 0) : owner.BeginSelectionPlaneEdit(id, onMask, level));
                }
                catch (Exception ex) { captured.TrySetException(ex); }
            });
            var edit = await captured.Task.WaitAsync(cancellation.Token);
            if (!Current() || edit is null) return false;
            SetStatus(Loc.T(createMask ? "S_CreatingSelectionMask" : "S_ClearingSelection"));
#if DEBUG
            var beforeCompute = createMask ? beforeSelectionMaskCompute : beforePlaneClearCompute;
#endif
            var prepared = await Task.Run(() =>
            {
#if DEBUG
                beforeCompute?.Invoke();
#endif
                return edit.Prepare(cancellation.Token);
            }, cancellation.Token);
            if (!Current()) return false;
            if (prepared is null) { SetStatus(Loc.T(createMask ? "S_SelectionMaskUnavailable" : "S_PlaneEditUnchanged")); return false; }
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            QueueCanvasInput(owner, () =>
            {
                try
                {
                    bool applied = Current() && owner.CommitSelectionPlaneEdit(prepared);
                    if (applied && createMask)
                    {
                        effectSelection = null; maskAloneLayerId = null; paintOnMask = true;
                        SyncPaintTargetControls(); RefreshLayerPanel();
                    }
                    completed.TrySetResult(applied);
                }
                catch (Exception ex) { completed.TrySetException(ex); }
            });
            bool changed = await completed.Task.WaitAsync(cancellation.Token);
            if (Current()) SetStatus(changed
                ? Loc.F(createMask ? level != 0 ? "S_RevealSelectionMaskAdded" : "S_HideSelectionMaskAdded" : onMask ? "S_MaskFilled" : "S_PixelsCleared", edit.LayerName)
                : Loc.T(createMask ? "S_MaskSourceChanged" : "S_DeleteSourceChanged"));
            return changed;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return false; }
        catch (Exception ex)
        {
            if (Current()) { App.Log($"Selection plane edit failed: {ex}"); Report(Loc.T(createMask ? "S_SelectionMaskFailed" : "S_DeletePlaneFailed"), ex); }
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
}
