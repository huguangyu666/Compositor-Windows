namespace Compositor.Core;

public sealed partial class EditorDocument
{
    public bool SetSelectionUndoable(Selection? selection)
    {
        if (ReferenceEquals(selection, ActiveSelection) || selection is not null && ActiveSelection is { } current
            && selection.SameOutlineAs(current)) return false;
        if (!suppressUndoHistory) { undo.Push(CaptureSelectionState()); redo.Clear(); StartHistoryState(selection is null ? "Deselect" : "Selection"); }
        pendingRedoRestore = null; coalesceKey = null;
        ActiveSelection = selection; IsDirty = true;
        if (!suppressUndoHistory) TrimUndoHistory();
        SelectionChanged?.Invoke(); return true;
    }

    // Selection-only edits share immutable coverage and retain no manifest or asset dictionaries.
    private MementoState CaptureSelectionState() => new()
    {
        Manifest = [], Images = [], Masks = [], Dirty = IsDirty, Selection = ActiveSelection,
        HistoryId = CurrentHistoryId, HistoryOperation = historyOperation,
        SelectionOnly = true, ActiveLayerId = Snapshot.Manifest.ActiveLayerId,
    };
}
