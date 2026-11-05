namespace Compositor.Core;

/// <summary>Prepares a selection-constrained pixel or mask edit without mutating the open document.</summary>
public sealed class SelectionPlaneEdit
{
    private readonly ProjectSnapshot source;
    private readonly Selection selection;
    private readonly string manifest;
    private readonly Dictionary<Guid, (Surface Image, int Revision)> images;
    private readonly Dictionary<Guid, (MaskSurface Mask, int Revision)> masks;
    private readonly byte level;
    internal Guid LayerId { get; }
    internal bool OnMask { get; }
    internal bool CreatesMask { get; }
    public string LayerName { get; }

    internal SelectionPlaneEdit(ProjectSnapshot source, Selection selection, Guid id, bool onMask, byte level, bool createsMask = false)
    {
        this.source = source; this.selection = selection; this.level = level;
        LayerId = id; OnMask = onMask; CreatesMask = createsMask;
        manifest = ManifestJson.Serialize(source.Manifest);
        LayerName = source.Manifest.Layers.First(layer => layer.Id == id).Name;
        images = source.Images.ToDictionary(p => p.Key, p => (p.Value, p.Value.Revision));
        masks = source.Masks.ToDictionary(p => p.Key, p => (p.Value, p.Value.Revision));
    }

    public SelectionPlaneResult? Prepare(CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        // Reuse the established kernels on detached metadata. The temporary editor has no UI
        // subscribers; its private history keeps asset references, without copying another plane.
        var working = new EditorDocument(new ProjectSnapshot
        {
            Manifest = ManifestJson.Deserialize(manifest),
            Images = images.ToDictionary(p => p.Key, p => p.Value.Image),
            Masks = masks.ToDictionary(p => p.Key, p => p.Value.Mask),
        });
        working.SetSelection(selection);
        bool changed = CreatesMask ? working.AddSelectionMask(LayerId, level != 0, cancellation)
            : OnMask ? working.FillMask(LayerId, level, cancellation) : working.CutSelectionMasked(LayerId, selection, cancellation);
        cancellation.ThrowIfCancellationRequested();
        return changed ? new(this, OnMask ? null : working.Snapshot.Images[LayerId], OnMask ? working.Snapshot.Masks[LayerId] : null) : null;
    }

    internal bool IsCurrent(ProjectSnapshot current, Selection? activeSelection) => ReferenceEquals(source, current)
        && ReferenceEquals(selection, activeSelection) && manifest == ManifestJson.Serialize(current.Manifest)
        && current.Images.Count == images.Count && current.Masks.Count == masks.Count
        && images.All(p => current.Images.TryGetValue(p.Key, out var image) && ReferenceEquals(image, p.Value.Image) && image.Revision == p.Value.Revision)
        && masks.All(p => current.Masks.TryGetValue(p.Key, out var mask) && ReferenceEquals(mask, p.Value.Mask) && mask.Revision == p.Value.Revision);
}

public sealed class SelectionPlaneResult
{
    internal SelectionPlaneResult(SelectionPlaneEdit edit, Surface? image, MaskSurface? mask) { Edit = edit; Image = image; Mask = mask; }
    internal SelectionPlaneEdit Edit { get; }
    internal Surface? Image { get; }
    internal MaskSurface? Mask { get; }
}

public sealed partial class EditorDocument
{
    public SelectionPlaneEdit? BeginSelectionPlaneEdit(Guid id, bool onMask, byte level)
    {
        if (!onMask && KeepsAlpha(id)) return null;
        if (ActiveSelection is not { IsEmpty: false } selection || WhyPaintRefused(id, onMask) is not null
            || (onMask ? !Snapshot.Masks.ContainsKey(id) : !Snapshot.Images.ContainsKey(id))) return null;
        return new(Snapshot, selection, id, onMask, level);
    }

    public SelectionPlaneEdit? BeginSelectionMaskEdit(Guid id, bool revealing)
    {
        if (ActiveSelection is not { } selection || Find(id) is not { MaskFile: null }) return null;
        return new(Snapshot, selection, id, true, revealing ? (byte)255 : (byte)0, createsMask: true);
    }

    public bool CommitSelectionPlaneEdit(SelectionPlaneResult result) => result.Edit.IsCurrent(Snapshot, ActiveSelection)
        && (result.Edit.CreatesMask ? AddRasterMask(result.Edit.LayerId, result.Mask!, clearSelection: true)
            : result.Mask is { } mask ? ReplaceLayerMask(result.Edit.LayerId, mask) : ReplaceLayerImage(result.Edit.LayerId, result.Image!));
}
