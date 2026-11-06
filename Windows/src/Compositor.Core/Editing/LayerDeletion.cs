namespace Compositor.Core;

/// <summary>A captured deletion, including folders and surviving live-mask consumers.</summary>
public sealed class LayerDeletion
{
    private readonly ProjectSnapshot source;
    private readonly string manifest;
    private readonly Dictionary<Guid, (Surface Image, int Revision)> images;
    private readonly Dictionary<Guid, (MaskSurface Mask, int Revision)> masks;
    internal readonly ProjectSnapshot Frozen;
    internal readonly HashSet<Guid> Removed;

    internal LayerDeletion(ProjectSnapshot snapshot, HashSet<Guid> removed)
    {
        source = snapshot; Removed = removed;
        manifest = ManifestJson.Serialize(snapshot.Manifest);
        Frozen = new() { Manifest = ManifestJson.Deserialize(manifest),
            Images = snapshot.Images.ToDictionary(), Masks = snapshot.Masks.ToDictionary() };
        images = snapshot.Images.ToDictionary(p => p.Key, p => (p.Value, p.Value.Revision));
        masks = snapshot.Masks.ToDictionary(p => p.Key, p => (p.Value, p.Value.Revision));
        DependentLayerIds = Array.AsReadOnly(Frozen.Manifest.Layers
            .Where(layer => !removed.Contains(layer.Id) && layer.MaskSourceId is { } id && removed.Contains(id))
            .Select(layer => layer.Id).ToArray());
        HasAdjustmentDependents = Frozen.Manifest.Layers.Any(layer => DependentLayerIds.Contains(layer.Id) && layer.Adjustment is not null);
    }

    public IReadOnlyList<Guid> DependentLayerIds { get; }
    public bool HasAdjustmentDependents { get; }
    public int RemovedCount => Removed.Count;

    public LayerDeletionResult Prepare(bool bake, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        return new(this, bake ? CanvasCompositor.BakeDeletionMasks(Frozen, DependentLayerIds, cancellation) : new());
    }

    internal bool IsCurrent(ProjectSnapshot current) => ReferenceEquals(source, current)
        && manifest == ManifestJson.Serialize(current.Manifest)
        && current.Images.Count == images.Count && current.Masks.Count == masks.Count
        && images.All(p => current.Images.TryGetValue(p.Key, out var value) && ReferenceEquals(value, p.Value.Image) && value.Revision == p.Value.Revision)
        && masks.All(p => current.Masks.TryGetValue(p.Key, out var value) && ReferenceEquals(value, p.Value.Mask) && value.Revision == p.Value.Revision);
}

public sealed class LayerDeletionResult
{
    internal LayerDeletionResult(LayerDeletion edit, Dictionary<Guid, Surface> baked) { Edit = edit; Baked = baked; }
    internal LayerDeletion Edit { get; }
    internal Dictionary<Guid, Surface> Baked { get; }
}

public sealed partial class EditorDocument
{
    public LayerDeletion? BeginLayerDeletion(IEnumerable<Guid> ids)
    {
        var layers = Snapshot.Manifest.Layers;
        var valid = layers.Select(layer => layer.Id).ToHashSet();
        var removed = ids.Where(valid.Contains).ToHashSet();
        if (removed.Count == 0) return null;
        bool added;
        do
        {
            added = false;
            foreach (var layer in layers)
                if (layer.ParentId is { } parent && removed.Contains(parent) && removed.Add(layer.Id)) added = true;
        } while (added);
        if (removed.Any(id => !CanEditLayer(id, LayerLocks.None))) return null;
        return new(Snapshot, removed);
    }

    /// <summary>Deletes layers and removes their outgoing clipping dependencies in one Undo.</summary>
    public bool DeleteLayers(IEnumerable<Guid> ids) => BeginLayerDeletion(ids) is { } edit && CommitLayerDeletion(edit.Prepare(bake: false));

    public bool CommitLayerDeletion(LayerDeletionResult result)
    {
        var edit = result.Edit;
        if (!edit.IsCurrent(Snapshot) || edit.Removed.Any(id => !CanEditLayer(id, LayerLocks.None))
            || edit.DependentLayerIds.Any(id => !CanEditPixels(id))) return false;
        var manifest = ManifestJson.Deserialize(ManifestJson.Serialize(Snapshot.Manifest));
        int activeIndex = manifest.Layers.FindIndex(layer => layer.Id == manifest.ActiveLayerId);
        if (manifest.ActiveLayerId is { } active && edit.Removed.Contains(active))
            manifest.ActiveLayerId = manifest.Layers.Skip(Math.Max(0, activeIndex)).FirstOrDefault(layer => !edit.Removed.Contains(layer.Id))?.Id
                ?? manifest.Layers.LastOrDefault(layer => !edit.Removed.Contains(layer.Id))?.Id;
        manifest.Layers.RemoveAll(layer => edit.Removed.Contains(layer.Id));
        manifest.Timeline?.Tracks.RemoveAll(track => edit.Removed.Contains(track.LayerId));
        var remainingImages = Snapshot.Images.Where(p => !edit.Removed.Contains(p.Key)).ToDictionary();
        var remainingMasks = Snapshot.Masks.Where(p => !edit.Removed.Contains(p.Key)).ToDictionary();
        foreach (var layer in manifest.Layers)
        {
            if (layer.MaskSourceId is not { } source || !edit.Removed.Contains(source)) continue;
            layer.MaskSourceId = null;
            if (result.Baked.TryGetValue(layer.Id, out var pixels))
            {
                remainingImages[layer.Id] = pixels;
                layer.Text = null; layer.Shape = null;
            }
        }
        ProjectValidator.Validate(manifest);
        BeginChange(); coalesceKey = null;
        Snapshot = new() { Manifest = manifest, Images = remainingImages, Masks = remainingMasks };
        EndChange();
        return true;
    }
}
