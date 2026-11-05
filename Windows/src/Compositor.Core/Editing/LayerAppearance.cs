namespace Compositor.Core;

public sealed partial class EditorDocument
{
    /// <summary>Sets the selected layers, including folders, as one appearance edit.</summary>
    public bool SetLayerOpacities(IEnumerable<Guid> ids, double opacity)
    {
        if (!double.IsFinite(opacity) || ActiveTextDraft is not null) return false;
        opacity = Math.Clamp(opacity, 0, 1);
        var selected = ids.ToHashSet();
        var changed = Snapshot.Manifest.Layers.Where(layer => selected.Contains(layer.Id) && (layer.Opacity ?? 1) != opacity).ToArray();
        if (changed.Length == 0) return false;
        BeginChange(); coalesceKey = null;
        foreach (var layer in changed) layer.Opacity = opacity;
        EndChange(); return true;
    }
}
