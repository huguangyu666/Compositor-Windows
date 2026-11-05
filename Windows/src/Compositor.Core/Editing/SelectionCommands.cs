namespace Compositor.Core;

public enum SelectionModification { Expand, Contract, Feather }

public static class SelectionCommands
{
    public static Selection? Invert(Selection? current, int width, int height, CancellationToken cancellation = default)
    {
        if (current is null) return null;
        var inverse = current.Inverted(width, height, cancellation);
        return inverse.Coverage.Pixels.AsSpan().IndexOfAnyExcept((byte)0) < 0 ? null : inverse;
    }

    public static Selection? Modify(Selection? current, SelectionModification operation, int amount, CancellationToken cancellation = default)
    {
        if (current is null || current.IsEmpty || amount < 1 || amount > (operation == SelectionModification.Feather ? 250 : 500)) return current;
        return operation switch
        {
            SelectionModification.Expand => current.Expanded(amount, cancellation),
            SelectionModification.Contract => current.Contracted(amount, cancellation),
            SelectionModification.Feather => current.Feathered(amount),
            _ => current,
        };
    }
}
