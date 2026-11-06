using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.System;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private CancellationTokenSource? outlineCancellation;
    // One token per queued build, cancelled together when the selection changes underneath us, the
    // document is replaced or the window closes. UI-thread only; the token itself crosses threads.
    private readonly List<CancellationTokenSource> selectionCancellations = [];
    private Task<Selection?>? selectionWorkResult;
    private long selectionWorkRequest;
    private long selectionWorkEpoch;
    private bool selectionWorkUndoable;
    private bool publishingSelection;
    private bool marqueePreviewPending;
    private LassoSelectionMode marqueeMode;
    private bool marqueeConstrainArmed = true;
    private bool selectionAmountPending;
#if DEBUG
    // Probe-only observation of the worker side of cancellation (see VerifySelectionCancellationAsync).
    internal int abandonedSelectionBuilds;
    internal double lastAbandonedBuildMs;
#endif

    private bool CanEditSelection => CanEditSelectionCore && pixelMove is null && pixelNudgePending == 0;
    private bool CanEditSelectionCore => !renderInputs.HasPending && CanEditSelectionState;
    private bool CanEditSelectionState => !TimelinePreviewActive && CanEditSelectionBaseState;
    private bool CanEditSelectionBaseState => !timelineExporting && !IsFileImporting && document is not null && selectionMove is null && pointerStroke is null && healingStroke is null && !healingFinishing
        && floatingDuplicate is null && floatingTransform is null && transformInspectorEdit is null && gradientEdit is null && !renamingLayer && !textDialogOpen
        && !colorRangeSampling && !selectionAmountPending && StatusProgress.Visibility != Visibility.Visible;

    private LassoSelectionMode SelectionModeAtPress(VirtualKeyModifiers modifiers) => modifiers.HasFlag(VirtualKeyModifiers.Menu)
        ? LassoSelectionMode.Subtract : modifiers.HasFlag(VirtualKeyModifiers.Shift) ? LassoSelectionMode.Add
        : selectionMode;

    private void BeginMarqueeSelection(PointD point, VirtualKeyModifiers modifiers)
    {
        if (!CanEditSelection) return;
        CompleteMarqueePreview(); marqueeMode = SelectionModeAtPress(modifiers);
        marqueeConstrainArmed = !modifiers.HasFlag(VirtualKeyModifiers.Shift);
        dragStart = new(Math.Round(point.X, MidpointRounding.AwayFromZero), Math.Round(point.Y, MidpointRounding.AwayFromZero));
    }

    private bool MarqueeSquare(VirtualKeyModifiers modifiers)
    {
        bool shift = modifiers.HasFlag(VirtualKeyModifiers.Shift);
        if (!shift) marqueeConstrainArmed = true;
        return shift && marqueeConstrainArmed;
    }

    private void OnSelectionChanged()
    {
        RefreshAiActions();
        RetireBlendPreview();
        EndOpacityEdit();
        RefreshMaskButtonHint();
        FinishVisibilitySwipe(closeHistory: false);
        if (!publishingSelection) CancelPixelMove();
        UpdateTitle();
        if (!publishingSelection) { if (selectionMove is not null) ClearSelectionMove(); CancelColorRange(false); CancelSelectionWork(); }
        CancelHealingStroke();
        CancelGradientDraft();
        RefreshSelectionOutline();
        RefreshQuickToolControls();
    }

    private void CancelSelectionWork()
    {
        selectionWorkRequest++;
        selectionWorkEpoch++;
        selectionWorkResult = null;
        // The epoch test already stops an abandoned build from publishing, but the worker would
        // still run its distance transform to the end. Cancel so the remaining rows are skipped.
        // Sources are not disposed: a worker may still be reading its token.
        foreach (var live in selectionCancellations) live.Cancel();
        selectionCancellations.Clear();
        CompleteMarqueePreview();
    }

    private void CompleteMarqueePreview()
    {
        if (lassoPreviewPending)
        {
            lassoPreviewPending = false;
            LassoPreview.Points.Clear(); LassoPreview.Visibility = Visibility.Collapsed;
        }
        if (!marqueePreviewPending) return;
        marqueePreviewPending = false;
        DragPreview.Visibility = ShapePreviewEllipse.Visibility = Visibility.Collapsed;
    }

    private void UpdateSelectionStrokes()
    {
        double width = 1 / viewport.PointsPerPixel;
        SelectionOutline.StrokeThickness = DragPreview.StrokeThickness = ShapePreviewEllipse.StrokeThickness = LassoPreview.StrokeThickness = width;
    }

    private async void QueueSelectionWork(Func<Selection?, CancellationToken, Selection?> build, bool combinesPrevious, string status, bool undoable = true,
        Func<Selection?, string>? describe = null)
    {
        if (document is not { } owner) return;
        long request = ++selectionWorkRequest;
        if (!undoable && selectionWorkUndoable) selectionWorkEpoch++;
        long epoch = selectionWorkEpoch;
        var previousTask = selectionWorkResult;
        var previousSelection = owner.ActiveSelection;
        // Consecutive Add/Subtract releases consume the preceding result even when its
        // UI publication was superseded. Replace releases can start independently.
        // Finished selection commands wait for the prior UI publication, preserving each
        // history step. Non-undoable previews can still supersede one another.
        // A new job therefore never cancels the one before it: only CancelSelectionWork does.
        var cancellation = new CancellationTokenSource();
        selectionCancellations.Add(cancellation);
        var token = cancellation.Token;
#if DEBUG
        var abandonedWatch = System.Diagnostics.Stopwatch.StartNew();
#endif
        var completion = new TaskCompletionSource<Selection?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = Task.Run(async () =>
        {
            var previous = previousTask is not null && (combinesPrevious || undoable)
                ? await previousTask.ConfigureAwait(false) : previousSelection;
            if (epoch != Volatile.Read(ref selectionWorkEpoch)) return previous;
            try
            {
                return build(combinesPrevious || undoable ? previous : previousSelection, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
#if DEBUG
                // The probe asserts a superseded build stopped early, which needs the worker's own
                // duration; production keeps only the epoch/reference guards.
                Interlocked.Increment(ref abandonedSelectionBuilds);
                lastAbandonedBuildMs = abandonedWatch.Elapsed.TotalMilliseconds;
#endif
                return previous; // superseded mid-build: publish nothing new
            }
        });
        selectionWorkResult = completion.Task; selectionWorkUndoable = undoable;
        Selection? result = previousSelection;
        SetStatus(Loc.T("S_SelectionWorking", "Building selection…"));
        try
        {
            result = await work;
            if (windowClosed || epoch != selectionWorkEpoch || !ReferenceEquals(owner, document)
                || (undoable ? !selectionWorkUndoable : request != selectionWorkRequest)) return;
            if (ReferenceEquals(selectionWorkResult, completion.Task)) selectionWorkResult = null;
            publishingSelection = true;
            try { if (undoable) owner.SetSelectionUndoable(result); else owner.SetSelection(result); }
            finally { publishingSelection = false; }
            if (request == selectionWorkRequest) SetStatus(describe?.Invoke(result) ?? status);
        }
        catch (Exception ex)
        {
            App.Log($"Selection work failed: {ex}");
            // A failed command leaves the last published selection in place. The next
            // Add/Subtract must consume it, rather than the state at this job's submission.
            if (epoch == selectionWorkEpoch && ReferenceEquals(owner, document)) result = owner.ActiveSelection;
            if (request == selectionWorkRequest && ReferenceEquals(owner, document))
            { selectionWorkResult = null; CompleteMarqueePreview(); SetStatus(Loc.T("S_SelectionFailed", "Could not build selection.")); }
        }
        finally
        {
            selectionCancellations.Remove(cancellation);
            completion.TrySetResult(result);
        }
    }

    private async void QueueSelectionHistory(bool redo)
    {
        if (document is not { } owner) return;
        var preceding = selectionWorkResult;
        if (preceding is null)
        {
            QueueCanvasInput(owner, () => SetStatus(Loc.T(ApplyHistory(redo) ? redo ? "S_Redo" : "S_Undo" : redo ? "S_NoRedo" : "S_NoUndo")));
            return;
        }
        long request = ++selectionWorkRequest, epoch = selectionWorkEpoch;
        var completion = new TaskCompletionSource<Selection?>(TaskCreationOptions.RunContinuationsAsynchronously);
        selectionWorkResult = completion.Task; selectionWorkUndoable = true;
        try
        {
            await preceding;
            while (workerBusy && !windowClosed && ReferenceEquals(owner, document) && epoch == selectionWorkEpoch) await Task.Delay(1);
            if (windowClosed || !ReferenceEquals(owner, document) || epoch != selectionWorkEpoch) return;
            // The render reader has finished, and selection-only history keeps this queue alive.
            publishingSelection = true;
            try
            {
                bool applied = ApplyHistory(redo);
                if (request == selectionWorkRequest) SetStatus(Loc.T(applied ? redo ? "S_Redo" : "S_Undo" : redo ? "S_NoRedo" : "S_NoUndo"));
            }
            finally { publishingSelection = false; }
        }
        finally
        {
            if (ReferenceEquals(selectionWorkResult, completion.Task)) selectionWorkResult = null;
            completion.TrySetResult(owner.ActiveSelection);
        }
    }

    private void SelectWithWand(PointD point, VirtualKeyModifiers modifiers)
    {
        if (!CanEditSelection || document is not { } owner) return;
        var manifest = owner.Snapshot.Manifest;
        if (point.X < 0 || point.Y < 0 || point.X >= manifest.Width || point.Y >= manifest.Height) return;
        var input = SelectionSampleInput.Capture(owner.Snapshot); var activeId = owner.Snapshot.Manifest.ActiveLayerId;
        var settings = wandSettings with { }; var mode = SelectionModeAtPress(modifiers);
        QueueSelectionWork((previous, token) =>
        {
            var incoming = SelectionSampling.Wand(input.Render(settings.SampleAllLayers, activeId), point, settings, token);
            return incoming is null ? mode == LassoSelectionMode.Replace ? null : previous : LassoSelection.Combine(previous, incoming, mode);
        }, mode != LassoSelectionMode.Replace, Loc.T("S_WandEmpty"), describe: selection => selection is null
            ? Loc.T("S_WandEmpty") : Loc.F("S_WandSelected", selection.Bounds.Width * selection.Bounds.Height));
    }

    private void ApplySelectionModification(SelectionModification operation, int amount)
    {
        if (!CanEditSelection) return;
        string key = operation == SelectionModification.Expand ? "S_SelectionExpanded"
            : operation == SelectionModification.Contract ? "S_SelectionContracted" : "S_SelectionFeathered";
        QueueSelectionWork((previous, token) => SelectionCommands.Modify(previous, operation, amount, token), true, Loc.F(key, amount));
    }

    private void FinishMarqueeSelection(PointD anchor, PointD point, bool square)
    {
        if (!CanEditSelection || document is not { } owner) return;
        Rect box = DragBoxRect(anchor, point, square);
        int x = (int)Math.Floor(box.X), y = (int)Math.Floor(box.Y);
        int width = (int)Math.Ceiling(box.Width), height = (int)Math.Ceiling(box.Height);
        var mode = marqueeMode;
        if (width < 1 || height < 1)
        {
            if (mode == LassoSelectionMode.Replace) QueueSelectionWork((_, _) => null, false, Loc.T("S_Deselected"));
            return;
        }
        int documentWidth = owner.Snapshot.Manifest.Width, documentHeight = owner.Snapshot.Manifest.Height;
        bool ellipse = tool == CanvasTool.EllipseSelect;
        bool antialias = LassoAntialiasCheck.IsChecked == true;
        ShowMarqueePreview(anchor, point, square);
        marqueePreviewPending = true;
        QueueSelectionWork((previous, _) => LassoSelection.Combine(previous, ellipse
            ? Selection.Ellipse(documentWidth, documentHeight, x + width / 2.0, y + height / 2.0, width / 2.0, height / 2.0, antialias)
            : Selection.Rectangle(documentWidth, documentHeight, x, y, width, height), mode), mode != LassoSelectionMode.Replace,
            Loc.T(ellipse ? "S_ToolEllipseSelectHint" : "S_ToolRectSelectHint"));
    }

    private void ShowMarqueePreview(PointD anchor, PointD point, bool square)
    {
        UpdateSelectionStrokes();
        Rect box = DragBoxRect(anchor, point, square);
        if (tool == CanvasTool.RectSelect) ShowDragPreview(box);
        else
        {
            ShapePreviewEllipse.Visibility = Visibility.Visible;
            Canvas.SetLeft(ShapePreviewEllipse, box.X); Canvas.SetTop(ShapePreviewEllipse, box.Y);
            ShapePreviewEllipse.Width = box.Width; ShapePreviewEllipse.Height = box.Height;
        }
    }
}
