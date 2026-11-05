using Compositor.Core;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private bool BeginDistortCornersCommand(EditorDocument owner, TransformEdit edit, PointD[] corners)
    {
        if (!CanEditLayerCommands || !ReferenceEquals(owner, document) || !DistortWarp.IsUsable(corners)) return false;
        QueueCanvasInput(owner, () =>
        {
            if (!CanEditLayerCommands) return;
            transformInspectorEdit = edit; transformInspectorValue = edit.Value;
            SetTransformCorners(corners); CommitTransformNow();
        });
        return true;
    }
    private void PublishDistortCommit(TransformRequest request, TransformPreview? preview)
    {
        if (distortCompletion is not { } completion || document is not { } owner
            || !ReferenceEquals(request.Edit, transformInspectorEdit) || request.Revision != transformDraftRevision) return;
        distortCompletion = null;
        var duplicateSelection = transformDuplicateSelection;
        bool committed = false;
        try
        {
            ClearTransformDraft(keepDuplicateSelection: true, keepFloatingDuplicate: true);
            committingTransform = true;
            try { committed = preview is not null && owner.CommitTransform(preview); }
            finally { committingTransform = false; }
#if DEBUG
            beforeInteractionCompletionUi?.Invoke("distort-publish");
#endif
            if (!committed && duplicateSelection is not null) RestoreDuplicateSelection(duplicateSelection);
            renderSnapshot = null; RefreshLayerPanel(); RefreshSelectionOutline(); RefreshTransformInspector(true); UpdateTransformHandles();
            SetStatus(Loc.T(committed ? "S_TransformApplied" : "Tr_Unavailable"));
        }
        finally { completion.TrySetResult(committed); }
    }
}
