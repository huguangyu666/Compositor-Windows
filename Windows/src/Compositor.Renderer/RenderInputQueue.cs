namespace Compositor.Renderer;

/// <summary>UI-owned input queue. Mutations drain before a serial render starts;
/// input arriving while its worker reads pixels waits for the next frame.</summary>
public sealed class RenderInputQueue
{
    private readonly Queue<(object Owner, Action Apply)> pending = new();
    public bool IsBusy { get; private set; }
    public bool IsApplying { get; private set; }
    public bool HasPending => pending.Count != 0;

    public void Enqueue(object owner, Action apply) => pending.Enqueue((owner, apply));
    public void Clear() => pending.Clear();

    public bool TryBeginFrame(object owner)
    {
        if (IsBusy) return false;
        IsBusy = IsApplying = true;
        try
        {
            while (pending.TryDequeue(out var input))
                if (ReferenceEquals(input.Owner, owner)) input.Apply();
            return true;
        }
        catch
        {
            EndFrame();
            throw;
        }
    }

    public void StartRender()
    {
        if (!IsApplying) throw new InvalidOperationException("Input must be applied before rendering.");
        IsApplying = false;
    }

    public void EndFrame() => IsBusy = IsApplying = false;
}
