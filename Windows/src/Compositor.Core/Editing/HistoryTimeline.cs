namespace Compositor.Core;

public sealed record EditorHistoryState(Guid Id, string Operation, bool IsCurrent, bool IsFuture, bool IsBaseline);

public sealed partial class EditorDocument
{
    public Guid CurrentHistoryId { get; private set; } = Guid.NewGuid();
    private string historyOperation = "Initial";
    private bool navigatingHistory;
    public event Action? HistoryChanged;

    // Names and identities only; the existing stacks remain the sole owners of mementos.
    public IReadOnlyList<EditorHistoryState> HistoryStates
    {
        get
        {
            var rows = new List<EditorHistoryState>(undo.Count + redo.Count + 1);
            foreach (var state in undo.Reverse()) rows.Add(new(state.HistoryId, state.HistoryOperation, false, false, rows.Count == 0));
            rows.Add(new(CurrentHistoryId, historyOperation, true, false, rows.Count == 0));
            foreach (var state in redo) rows.Add(new(state.HistoryId, state.HistoryOperation, false, true, false));
            return rows;
        }
    }

    private void StartHistoryState(string operation)
    { CurrentHistoryId = Guid.NewGuid(); historyOperation = operation; NotifyHistoryChanged(); }

    private void RestoreHistoryIdentity(MementoState state)
    { CurrentHistoryId = state.HistoryId; historyOperation = state.HistoryOperation; }

    private void NotifyHistoryChanged()
    { if (!suppressUndoHistory && !navigatingHistory) HistoryChanged?.Invoke(); }

    public bool GoToHistory(Guid target)
    {
        if (ActiveTextDraft is not null) return false;
        if (target == CurrentHistoryId) return true;
        var older = undo.ToArray(); var newer = redo.ToArray();
        int index = Array.FindIndex(older, state => state.HistoryId == target);
        bool backwards = index >= 0;
        if (!backwards) index = Array.FindIndex(newer, state => state.HistoryId == target);
        if (index < 0) return false;
        navigatingHistory = true;
        try
        { for (int i = 0; i <= index; i++) Revert(backwards ? undo : redo, backwards ? redo : undo); }
        finally { navigatingHistory = false; }
        TrimUndoHistory(); NotifyHistoryChanged(); Changed?.Invoke(); SelectionChanged?.Invoke();
        return true;
    }
}
