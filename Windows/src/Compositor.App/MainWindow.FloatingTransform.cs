using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private sealed class FloatingTransformSession(EditorDocument owner, FloatingSelectionEdit edit, DuplicateSelectionState selection)
    {
        public readonly EditorDocument Owner = owner;
        public readonly FloatingSelectionEdit Edit = edit;
        public readonly DuplicateSelectionState BeforeSelection = selection;
        public readonly CancellationTokenSource Cancellation = new();
        public readonly TaskCompletionSource<bool> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ProjectSnapshot? Display;
        public bool Finishing;
    }
    private sealed record FloatingTransformRequest(FloatingTransformSession Session, LayerTransform Value, PointD[]? Corners, int Revision,
        bool Finishing, CancellationToken Token);
    private FloatingTransformSession? floatingTransform;
    private TaskCompletionSource documentRetired = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly MatrixTransform floatingOutlineTransform = new();
    private int floatingOutlineRevision;
#if DEBUG
    private int floatingOutlineBuilds, floatingOutlinePublications, floatingOutlinePublishedRevision, floatingOutlineRuns, floatingOutlineProcessedRuns;
    private double floatingOutlineLastMs;
#endif
    private FloatingTransformRequest? CurrentFloatingTransformRequest => floatingTransform is { } session && transformInspectorValue is { } value
        ? new(session, CopyTransform(value), transformCorners?.ToArray(), transformDraftRevision, session.Finishing, session.Cancellation.Token) : null;
    private bool CanStartTransform => !IsFileImporting && distortCompletion is null && document is not null && floatingDuplicate is null && selectionMove is null && pixelMove is null && pixelNudgePending == 0
        && pointerStroke is null && healingStroke is null && !healingFinishing && gradientEdit is null && !colorRangeSampling
        && !selectionAmountPending && !renamingLayer && !textDialogOpen && selectionWorkResult is null
        && StatusProgress.Visibility != Visibility.Visible
        && selectedLayerIds.All(id => document.CanEditLayer(id, LayerLocks.Position, branch: true));

    private bool BeginFloatingTransform()
    {
        if (!CanStartTransform || document is not { } owner || SelectedLayer is not { } layer || selectedLayerIds.Count > 1 || paintOnMask
            || owner.BeginFloatingSelection(layer.Id) is not { } edit) return false;
        var selected = new HashSet<Guid>(selectedLayerIds); selected.Add(layer.Id);
        floatingTransform = new(owner, edit, new(selectedLayerId, selected, new(collapsedGroups)));
        transformInspectorValue = edit.Value; transformPersistent = true;
        transformDraftRevision++; frameRevision++;
        MarkFloatingTransformDirty(edit.Value); RefreshTransformInspector(); UpdateTransformHandles(); return true;
    }

    private void MarkFloatingTransformDirty(LayerTransform value)
    {
        if (document is not { } owner) return;
        var (x0, y0, x1, y1) = Resampler.BoundingRect(value, owner.Snapshot.Manifest.Width, owner.Snapshot.Manifest.Height);
        // Even a wholly off-canvas draft needs a worker to retire the last visible pixels.
        var trigger = x1 > x0 && y1 > y0 ? new RectI(x0, y0, x1 - x0, y1 - y0) : new(0, 0, 1, 1);
        MarkInteractionDirty(trigger, grow: 0);
    }

    private void PublishFloatingTransform(FloatingTransformRequest request, FloatingSelectionPreview? preview, FloatingSelectionResult? result)
    {
        var session = request.Session;
        if (!ReferenceEquals(session, floatingTransform) || !ReferenceEquals(document, session.Owner) || request.Revision != transformDraftRevision) return;
        if (!request.Finishing)
        {
            bool first = session.Display is null; session.Display = preview!.Snapshot;
            transformInspectorEdit = session.Edit.Inspector;
            selectedLayerId = session.Edit.FloatingLayerId; selectedLayerIds.Clear(); selectedLayerIds.Add(selectedLayerId.Value);
            if (first) RefreshLayerPanel();
            else if (layerRows.TryGetValue(selectedLayerId.Value, out var grid))
            { var row = (LayerRow)grid.Tag; row.Update(SelectedLayer!, row.Depth); }
            RefreshTransformInspector(); RefreshSelectionOutline(); UpdateTransformHandles(); return;
        }
        floatingTransform = null;
        bool committed = false;
        try
        {
            ClearTransformDraft(keepFloatingDuplicate: true); RestoreDuplicateSelection(session.BeforeSelection);
            committingTransform = true;
            try { committed = result is not null && session.Owner.CommitFloatingSelection(result); }
            finally { committingTransform = false; }
#if DEBUG
            beforeInteractionCompletionUi?.Invoke("floating-publish");
#endif
            if (committed)
            {
                selectedLayerId = session.Edit.SourceLayerId; selectedLayerIds.Clear(); selectedLayerIds.Add(selectedLayerId.Value);
                SetStatus(Loc.T("S_TransformApplied"));
            }
            else SetStatus(Loc.T("Tr_Unavailable"));
            // A finishing request that produced no result at all used to leave the status untouched,
            // so the draft disappeared with nothing on screen to say the apply did not happen.
            renderSnapshot = null; RefreshLayerPanel(); RefreshSelectionOutline(); RefreshTransformInspector(true); UpdateTransformHandles();
        }
        finally { session.Completion.TrySetResult(committed); session.Cancellation.Dispose(); }
    }

    private void DiscardFloatingTransform()
    {
        if (directOutlineEnabled) CancelDirectProjection();
        CancelFloatingOutlineWork();
        if (floatingTransform is not { } session) return;
        floatingTransform = null;
        try
        {
            session.Cancellation.Cancel();
#if DEBUG
            beforeInteractionCompletionUi?.Invoke("floating-cancel");
#endif
            RestoreDuplicateSelection(session.BeforeSelection);
        }
        finally { session.Completion.TrySetResult(false); session.Cancellation.Dispose(); }
    }

    private void UpdateFloatingSelectionOutline()
    {
        if (directOutlineEnabled) { UpdateDirectOutlinePlacement(); return; }
        if (floatingTransform is not { } session || transformInspectorValue is not { } value || document?.ActiveSelection is not { } selection)
        {
            floatingOutlineRevision++; CancelFloatingOutlineWork();
            if (selectionOutlineGeometry is { } root) ReplaceSelectionOutlineData(root);
            SelectionOutline.RenderTransform = outlineTranslation; return;
        }
        var offset = selection.OutlineOffset;
        if (transformCorners is { } corners)
        {
            // DistortWarp.mapPath has no single outline for a folded shape.
            SelectionOutline.Visibility = DistortWarp.IsConvex(corners) ? Visibility.Visible : Visibility.Collapsed;
            if (!DistortWarp.IsConvex(corners) || selectionOutlineGeometry is not { } source || selectionOutlineEdges is not { } edges)
            {
                floatingOutlineRevision++; CancelFloatingOutlineWork(); return;
            }
            QueueFloatingOutline(session, source, edges, value, offset, corners);
            return;
        }
        floatingOutlineRevision++; CancelFloatingOutlineWork();
        var original = session.Edit.Value;
        if (selectionOutlineGeometry is { } affineRoot) ReplaceSelectionOutlineData(affineRoot);
        SelectionOutline.Visibility = Visibility.Visible;
        PointD Map(PointD p) => BrushEngine.PixelToDocument(value, 1, 1,
            BrushEngine.DocumentToPixel(original, 1, 1, new(p.X + offset.X, p.Y + offset.Y)));
        var origin = Map(PointD.Zero); var x = Map(new(1, 0)); var y = Map(new(0, 1));
        floatingOutlineTransform.Matrix = new(x.X - origin.X, x.Y - origin.Y, y.X - origin.X, y.Y - origin.Y, origin.X, origin.Y);
        SelectionOutline.RenderTransform = floatingOutlineTransform;
    }

    private async void QueueAfterTransform(EditorDocument owner, Action apply)
    {
        try
        {
            await FinishTransformBeforeOutputAsync();
            if (!windowClosed && ReferenceEquals(document, owner)) QueueCanvasInput(owner, apply);
        }
        catch (Exception ex) { App.Log($"Transform continuation failed: {ex}"); SetStatus(Loc.T("Tr_Unavailable")); }
    }

    private bool CancelFloatingPointerForFocus()
    {
        if (floatingDuplicate is { Released: true }) return false;
        if (CancelFloatingDuplicate()) return true;
        if (floatingTransform is not { Finishing: false } session || !transformPointerDraftActive || placementFinishing
            || transformDrag is not { } drag) return false;
        // EditorCanvas.resignFirstResponder restores the grab's placement, retaining a
        // persistent transform. A normal release has already marked placementFinishing.
        transformPointerDraftActive = placementDragActive = transformDragActive = false;
        transformDrag = null; pendingTransformPoint = null;
        SnapLineX.Visibility = SnapLineY.Visibility = Visibility.Collapsed;
        QueueCanvasInput(session.Owner, () =>
        {
            if (ReferenceEquals(session, floatingTransform) && !session.Finishing)
            { transformCorners = drag.OriginalCorners?.ToArray(); SetTransformDraft(CopyTransform(drag.Original)); }
        });
        return true;
    }
}
