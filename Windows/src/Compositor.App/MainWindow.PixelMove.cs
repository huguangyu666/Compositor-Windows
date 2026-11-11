using Compositor.Core;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private sealed class PixelMoveSession(EditorDocument owner, PixelMoveEdit edit, Selection original, PointD? anchor, bool duplicate)
    {
        public readonly EditorDocument Owner = owner;
        public readonly PixelMoveEdit Edit = edit;
        public readonly Selection Original = original;
        public readonly PointD? Anchor = anchor;
        public readonly bool Duplicate = duplicate;
        public readonly TaskCompletionSource<bool> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public PointD Offset;
        public long Revision;
        public bool Finishing;
    }
    private sealed record PixelMoveRequest(PixelMoveSession Session, PointD Offset, long Revision, bool Finishing);
    private PixelMoveSession? pixelMove;
    private long pixelMoveRevision, pixelNudgeEpoch;
    private int pixelNudgePending;
    private Task pixelNudgeWork = Task.CompletedTask;
    private PixelMoveRequest? CurrentPixelMoveRequest => pixelMove is { } move ? new(move, move.Offset, move.Revision, move.Finishing) : null;
    private Selection? DisplayedSelection => pixelMove is { } move ? move.Original.Translated((int)move.Offset.X, (int)move.Offset.Y)
        : objectSelectionDraft?.Result ?? document?.ActiveSelection;

    private bool HandlePixelMovePress(PointD point, VirtualKeyModifiers modifiers)
    {
        if (!CanEditSelection || selectionWorkResult is not null || !modifiers.HasFlag(VirtualKeyModifiers.Control)
            || tool is not (CanvasTool.RectSelect or CanvasTool.EllipseSelect or CanvasTool.Lasso or CanvasTool.Wand)
            || lassoPoints is not null || document?.ActiveSelection?.Contains(point) != true) return false;
        // The source consumes Cmd-inside even if the pixel target cannot be edited.
        TryBeginPixelMove(point, modifiers); return true;
    }

    private bool TryBeginPixelMove(PointD point, VirtualKeyModifiers modifiers)
    {
        if (!modifiers.HasFlag(VirtualKeyModifiers.Control) || tool is not (CanvasTool.RectSelect or CanvasTool.EllipseSelect or CanvasTool.Lasso or CanvasTool.Wand)
            || !CanEditSelection || selectionWorkResult is not null || lassoPoints is not null || document?.ActiveSelection?.Contains(point) != true) return false;
        return BeginPixelMoveSession(point, modifiers.HasFlag(VirtualKeyModifiers.Menu));
    }
    private bool BeginPixelMoveSession(PointD? anchor, bool duplicate)
    {
        if (document is not { } owner || SelectedLayer is not { } layer || selectedLayerIds.Count > 1 || paintOnMask || pixelMove is not null
            || owner.ActiveSelection is not { } selection || owner.BeginPixelMove(layer.Id, duplicate) is not { } edit) return false;
        pixelMove = new(owner, edit, selection, anchor, duplicate) { Revision = ++pixelMoveRevision };
        RefreshTransformAvailability(true); MarkPixelMoveDirty(); RefreshSelectionOutline(); return true;
    }
    private void MovePixelsTo(PointD point, VirtualKeyModifiers modifiers)
    {
        if (pixelMove is not { Finishing: false, Anchor: { } anchor } move || !ReferenceEquals(move.Owner, document)) return;
        double dx = point.X - anchor.X, dy = point.Y - anchor.Y;
        if (modifiers.HasFlag(VirtualKeyModifiers.Shift)) { if (Math.Abs(dx) >= Math.Abs(dy)) dy = 0; else dx = 0; }
        var offset = new PointD(Math.Round(dx, MidpointRounding.AwayFromZero), Math.Round(dy, MidpointRounding.AwayFromZero));
        if (offset == move.Offset) return;
        MarkPixelMoveDirty(pump: false); move.Offset = offset; move.Revision = ++pixelMoveRevision;
        MarkPixelMoveDirty(); RefreshSelectionOutline();
    }
    private void MarkPixelMoveDirty(bool pump = true)
    {
        if (pixelMove is not { } move || !ReferenceEquals(document, move.Owner)) return;
        var current = move.Original.Translated((int)move.Offset.X, (int)move.Offset.Y).Bounds;
        MarkInteractionDirty(RectI.Union(move.Original.Bounds, current), grow: 2); if (pump) PumpRender();
    }
    private Task FinishPixelMoveAsync()
    {
        if (pixelMove is not { } move) return Task.CompletedTask;
        if (move.Offset == PointD.Zero) { CancelPixelMove(); return move.Completion.Task; }
        if (!move.Finishing) { move.Finishing = true; move.Revision = ++pixelMoveRevision; MarkPixelMoveDirty(); }
        return move.Completion.Task;
    }
    private void PublishPixelMove(PixelMoveRequest request, PixelMoveResult? result)
    {
        var move = request.Session;
        if (!request.Finishing || !ReferenceEquals(move, pixelMove) || request.Revision != move.Revision || !ReferenceEquals(document, move.Owner)) return;
        pixelMove = null; committingTransform = true;
        bool committed = false;
        try
        {
            try { committed = result is not null && move.Owner.CommitPixelMove(result); }
            finally { committingTransform = false; }
#if DEBUG
            beforeInteractionCompletionUi?.Invoke("pixel-publish");
#endif
            RefreshSelectionOutline(); RefreshTransformAvailability(true);
            if (committed) SetStatus(Loc.T(move.Duplicate ? "S_SelectedPixelsDuplicated" : "S_SelectedPixelsMoved"));
            else { renderSnapshot = null; RenderCanvas(); SetStatus(Loc.T("S_SelectedPixelsFailed")); }
        }
        finally { move.Completion.TrySetResult(committed); }
    }
    private void CancelPixelMove(bool focusLoss = false)
    {
        if (pixelMove is not { } move || focusLoss && (move.Finishing || move.Anchor is null)) return;
        pixelMove = null; pixelMoveRevision++; frameRevision++;
        try
        {
            if (ReferenceEquals(document, move.Owner))
            {
#if DEBUG
                beforeInteractionCompletionUi?.Invoke("pixel-cancel");
#endif
                renderSnapshot = null;
                var dirty = compositor is { } active ? CanvasCompositor.ChangedLayersBounds(active.Snapshot, move.Owner.Snapshot, move.Edit.DirtyLayerIds)
                    : new RectI(0, 0, move.Owner.Snapshot.Manifest.Width, move.Owner.Snapshot.Manifest.Height);
                MarkInteractionDirty(dirty, grow: 2); RefreshSelectionOutline(); RefreshTransformAvailability(true); PumpRender();
            }
        }
        finally { move.Completion.TrySetResult(false); }
    }
    private bool NudgePixels(VirtualKey key, VirtualKeyModifiers modifiers, bool textInput = false)
    {
        if (key is not (VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down) || textInput || renamingLayer || textDialogOpen
            || !modifiers.HasFlag(VirtualKeyModifiers.Control) || modifiers.HasFlag(VirtualKeyModifiers.Menu) || lassoPoints is not null
            || document?.ActiveSelection is not { IsEmpty: false } || (!CanEditSelection && pixelNudgePending == 0)
            || pixelMove is { Anchor: not null } || selectedLayerIds.Count > 1 || paintOnMask) return false;
        int amount = modifiers.HasFlag(VirtualKeyModifiers.Shift) ? 10 : 1;
        QueuePixelNudge(new(key == VirtualKey.Left ? -amount : key == VirtualKey.Right ? amount : 0,
            key == VirtualKey.Up ? -amount : key == VirtualKey.Down ? amount : 0)); return true;
    }
    private async void QueuePixelNudge(PointD offset)
    {
        var owner = document; var retired = documentRetired.Task; long epoch = pixelNudgeEpoch; var preceding = pixelNudgeWork;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pixelNudgeWork = completion.Task; pixelNudgePending++;
        try
        {
            if (!await WaitForInteractionWorkAsync(preceding, retired)) return;
            if (windowClosed || epoch != pixelNudgeEpoch || !ReferenceEquals(document, owner) || !CanEditSelectionCore || !BeginPixelMoveSession(null, false)) return;
            pixelMove!.Offset = offset; pixelMove.Revision = ++pixelMoveRevision; RefreshSelectionOutline();
            await WaitForInteractionWorkAsync(FinishPixelMoveAsync(), retired);
        }
        finally { pixelNudgePending--; completion.TrySetResult(); RefreshTransformAvailability(true); }
    }
    private bool HandlePixelMoveKey(KeyRoutedEventArgs args)
    {
        if (args.OriginalSource is TextBox or RichEditBox || renamingLayer || textDialogOpen) return false;
        if (pixelMove is { Finishing: false } && IsCancelPress(args.Key)) { pixelNudgeEpoch++; CancelPixelMove(); args.Handled = true; return true; }
        var modifiers = CurrentThumbnailModifiers();
        if (args.KeyStatus.IsMenuKeyDown) modifiers |= VirtualKeyModifiers.Menu;
        // The arrows are rebindable, so the chord is translated before the seam: NudgePixels keeps reading the
        // Control-and-Shift pair it was written with, and an arrow whose command has moved is swallowed here.
        var (nudged, lost) = CanvasTranslate(new ShortcutChord(args.Key, modifiers));
        if (lost) { args.Handled = true; return true; }
        if (!NudgePixels(nudged.Key, nudged.Modifiers)) return false;
        args.Handled = true; return true;
    }
}
