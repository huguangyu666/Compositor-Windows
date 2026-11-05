using Compositor.Core;
using Windows.System;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private sealed class FloatingDuplicateGesture(EditorDocument owner, TransformDragState drag, PointD point,
        VirtualKeyModifiers modifiers)
    {
        public readonly EditorDocument Owner = owner;
        public Task<bool> Prior = Task.FromResult(false);
        public readonly TransformDragState Drag = drag;
        public readonly TaskCompletionSource<bool> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public PointD Point = point;
        public VirtualKeyModifiers Modifiers = modifiers;
        public bool Released;
    }
    private FloatingDuplicateGesture? floatingDuplicate;

    private bool BeginFloatingDuplicate(TransformDragState drag, PointD point)
    {
        var session = floatingTransform;
        if (session is null && transformCorners is null || document is not { } owner) return false;
        var modifiers = (pendingShift ? VirtualKeyModifiers.Shift : 0) | (pendingOption ? VirtualKeyModifiers.Menu : 0)
            | (pendingMoveFree ? VirtualKeyModifiers.Control : 0);
        var gesture = floatingDuplicate = new(owner, drag with { OriginalCorners = null }, point, modifiers);
        // Merge the preceding draft before applying any motion of this new copy grab.
        transformPointerDraftActive = placementDragActive = transformDragActive = false;
        transformDrag = null; pendingTransformPoint = null; placementFinishing = false;
        CommitTransformNow(); gesture.Prior = session?.Completion.Task ?? distortCompletion!.Task;
        ResumeFloatingDuplicate(gesture); return true;
    }

    private async void ResumeFloatingDuplicate(FloatingDuplicateGesture gesture)
    {
        try
        {
            await gesture.Prior;
            if (!ReferenceEquals(floatingDuplicate, gesture) || !ReferenceEquals(document, gesture.Owner) || windowClosed) return;
            QueueCanvasInput(gesture.Owner, () =>
            {
                if (!ReferenceEquals(floatingDuplicate, gesture)) return;
                floatingDuplicate = null;
                bool started = false;
                try
                {
                    started = BeginDuplicateTransformPointer(gesture.Drag);
                    if (!started) return;
                    // EditorCanvas retains the original grab even after the floating merge.
                    pendingShift = gesture.Modifiers.HasFlag(VirtualKeyModifiers.Shift);
                    pendingOption = gesture.Modifiers.HasFlag(VirtualKeyModifiers.Menu);
                    pendingMoveFree = gesture.Modifiers.HasFlag(VirtualKeyModifiers.Control);
                    ApplyPendingTransformDraft(gesture.Point);
                    if (gesture.Released) FinishPlacementDrag();
                }
                finally { gesture.Completion.TrySetResult(started); }
            });
        }
        catch (Exception ex)
        {
            App.Log($"Floating duplicate continuation failed: {ex}");
            if (ReferenceEquals(floatingDuplicate, gesture)) CancelFloatingDuplicate();
            gesture.Completion.TrySetResult(false);
        }
    }

    private bool CancelFloatingDuplicate()
    {
        if (floatingDuplicate is not { } gesture) return false;
        floatingDuplicate = null; gesture.Completion.TrySetResult(false);
        if (floatingTransform is null && !windowClosed) { RefreshTransformInspector(true); UpdateTransformHandles(); }
        return true;
    }
}
