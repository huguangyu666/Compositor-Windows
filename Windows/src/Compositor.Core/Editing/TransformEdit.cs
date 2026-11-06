namespace Compositor.Core;

public enum TransformField { X, Y, Width, Height, Scale, Rotation }

public sealed class TransformPreview
{
    internal TransformPreview(TransformEdit edit, ProjectSnapshot snapshot, LayerTransform value, RectI dirtyBounds)
    { Edit = edit; Snapshot = snapshot; Value = value; DirtyBounds = dirtyBounds; }
    internal TransformEdit Edit { get; }
    public ProjectSnapshot Snapshot { get; }
    public LayerTransform Value { get; }
    public RectI DirtyBounds { get; }
    internal PointD[]? Corners;
    internal bool IsDistortCommit;
}

public sealed partial class TransformEdit
{
    internal readonly ProjectSnapshot Source;
    internal readonly string Manifest;
    private readonly ProjectManifest basis;
    private readonly LayerTransform initial;
    private readonly Dictionary<Guid, (Surface Image, int Revision)> images;
    private readonly Dictionary<Guid, (MaskSurface Mask, int Revision)> masks;
    private readonly Dictionary<Guid, (Surface Image, int Revision)> sourceImages;
    private readonly Dictionary<Guid, (MaskSurface Mask, int Revision)> sourceMasks;
    private readonly LayerDuplication? duplication;
    private readonly HashSet<Guid> members;
    private readonly Dictionary<Guid, (int Width, int Height, Surface Image)> shapePreviews = new();

    private TransformEdit(ProjectSnapshot source, IReadOnlyList<Guid> ids, LayerTransform value, SizeD pixelSize, bool mask, bool group,
        ProjectSnapshot? validationSource = null, LayerDuplication? duplicate = null)
    {
        Source = validationSource ?? source; Manifest = ManifestJson.Serialize(Source.Manifest);
        basis = ManifestJson.Deserialize(duplicate is null ? Manifest : ManifestJson.Serialize(source.Manifest));
        duplication = duplicate;
        initial = Copy(value); PixelSize = pixelSize; TargetsMask = mask; IsGroup = group;
        LayerIds = Array.AsReadOnly(ids.ToArray()); members = ids.ToHashSet();
        images = source.Images.ToDictionary(p => p.Key, p => (p.Value, p.Value.Revision));
        masks = source.Masks.ToDictionary(p => p.Key, p => (p.Value, p.Value.Revision));
        sourceImages = duplicate is null ? images : Source.Images.ToDictionary(p => p.Key, p => (p.Value, p.Value.Revision));
        sourceMasks = duplicate is null ? masks : Source.Masks.ToDictionary(p => p.Key, p => (p.Value, p.Value.Revision));
        DirtyLayerIds = LayerIds;
        if (duplicate is not null)
        {
            var originalLayers = Source.Manifest.Layers.ToDictionary(l => l.Id);
            DirtyLayerIds = Array.AsReadOnly(duplicate.Mapping.Values.Concat(basis.Layers
                .Where(l => originalLayers.TryGetValue(l.Id, out var original) && original.MaskSourceId != l.MaskSourceId).Select(l => l.Id)).ToArray());
        }
    }

    /// <summary>The upright group box or the single pixel/mask placement at the start of the edit.</summary>
    public LayerTransform Value => Copy(initial);
    public SizeD PixelSize { get; }
    public bool TargetsMask { get; }
    public bool IsGroup { get; }
    public IReadOnlyList<Guid> LayerIds { get; }
    public IReadOnlyList<Guid> DirtyLayerIds { get; }
    public ProjectSnapshot? DuplicateSnapshot => duplication?.Snapshot;
    public IReadOnlyList<Guid> DuplicateSelectedIds => duplication?.Roots ?? Array.Empty<Guid>();
    public IReadOnlyDictionary<Guid, Guid>? DuplicateMapping => duplication?.Mapping;

    internal static TransformEdit? Create(ProjectSnapshot source, IEnumerable<Guid> selected, bool onMask,
        ProjectSnapshot? validationSource = null, LayerDuplication? duplicate = null)
    {
        var selection = selected.ToHashSet();
        var layers = source.Manifest.Layers;
        var byId = layers.ToDictionary(l => l.Id);
        if (selection.Count == 0 || selection.Any(id => !byId.ContainsKey(id))) return null;
        var visible = DrawOrder.Resolve(layers).Visible;
        bool group = selection.Count > 1 || byId[selection.First()].IsGroupOrFalse;
        if (!group)
        {
            var layer = byId[selection.First()];
            if (!visible.Contains(layer.Id) || !source.Images.TryGetValue(layer.Id, out var image)) return null;
            bool mask = onMask && layer.MaskLinked == false && source.Masks.ContainsKey(layer.Id);
            var value = mask ? layer.MaskPlacement ?? layer.Transform : layer.Transform;
            return value.IsValid ? new(source, [layer.Id], value, mask ? layer.Transform.Size : new(image.Width, image.Height), mask, false, validationSource, duplicate) : null;
        }
        var members = layers.Where(layer => !layer.IsGroupOrFalse && source.Images.ContainsKey(layer.Id) && visible.Contains(layer.Id)
            && HasSelectedAncestor(layer)).ToArray();
        if (members.Length == 0) return null;
        var corners = members.SelectMany(layer => TransformDrag.HandlePositions(layer.Transform)).ToArray();
        double left = corners.Min(p => p.X), top = corners.Min(p => p.Y);
        var box = new LayerTransform { Origin = new(left, top), Size = new(Math.Max(1, corners.Max(p => p.X) - left), Math.Max(1, corners.Max(p => p.Y) - top)) };
        return box.IsValid ? new(source, members.Select(l => l.Id).ToArray(), box, box.Size, false, true, validationSource, duplicate) : null;

        bool HasSelectedAncestor(ProjectLayerRecord layer)
        {
            Guid? current = layer.Id;
            for (int depth = 0; depth < 64 && current is { } id && byId.TryGetValue(id, out var node); depth++)
            {
                if (selection.Contains(id)) return true;
                current = node.ParentId;
            }
            return false;
        }
    }

    /// <summary>TransformInspector.swift: dimensions keep the origin; Scale uses the original pixel basis and center.</summary>
    public LayerTransform? UpdateNumber(LayerTransform value, TransformField field, double number, bool lockRatio = false)
    {
        if (!double.IsFinite(number) || !value.IsValid) return null;
        var next = Copy(value);
        switch (field)
        {
            case TransformField.X: next.Origin = new(number, value.Origin.Y); break;
            case TransformField.Y: next.Origin = new(value.Origin.X, number); break;
            case TransformField.Width:
                next.Size = new(number, lockRatio ? value.Size.Height * number / value.Size.Width : value.Size.Height); break;
            case TransformField.Height:
                next.Size = new(lockRatio ? value.Size.Width * number / value.Size.Height : value.Size.Width, number); break;
            case TransformField.Scale:
                if (number <= 0) return null;
                next.Size = new(PixelSize.Width * number / 100, PixelSize.Height * number / 100);
                next.Origin = new(value.Center.X - next.Size.Width / 2, value.Center.Y - next.Size.Height / 2); break;
            case TransformField.Rotation: next.Rotation = number % 360; break;
            default: return null;
        }
        return next.IsValid ? next : null;
    }

    public double ScalePercent(LayerTransform value) => value.Size.Width / Math.Max(1, PixelSize.Width) * 100;

    /// <summary>Metadata is detached per frame; pixel assets remain shared. No live record, pixel or undo state is edited.</summary>
    public TransformPreview? Preview(LayerTransform value, PointD[]? corners = null)
    {
        if (corners is not null) return DistortPreview(value, corners, false);
        if (!value.IsValid) return null;
        var manifest = CopyManifest(basis);
        var previewImages = new Dictionary<Guid, Surface>(images.ToDictionary(p => p.Key, p => p.Value.Image));
        foreach (var layer in manifest.Layers.Where(l => members.Contains(l.Id)))
        {
            if (TargetsMask) layer.MaskPlacement = SamePlacement(value, layer.Transform) ? null : Copy(value);
            else
            {
                var moved = IsGroup ? layer.Transform.Following(initial, value) : Copy(value);
                if (!moved.IsValid) continue; // The Mac leaves an invalid group member at its original placement.
                layer.MaskPlacement = MovedMask(layer, moved);
                layer.Transform = moved;
                if (ShapeStyle.FromElement(layer.Shape) is { Kind: ShapeKind.Rectangle, CornerRadius: > 0 } style)
                {
                    var image = images[layer.Id].Image;
                    if (Math.Abs(moved.Size.Width - image.Width) >= .5 || Math.Abs(moved.Size.Height - image.Height) >= .5)
                    {
                        double factor = Math.Min(1, 2048 / Math.Max(moved.Size.Width, moved.Size.Height));
                        int w = Math.Max(1, Round(moved.Size.Width * factor)), h = Math.Max(1, Round(moved.Size.Height * factor));
                        if (!shapePreviews.TryGetValue(layer.Id, out var cached) || cached.Width != w || cached.Height != h)
                        {
                            style.CornerRadius *= factor;
                            cached = (w, h, ShapeRasterizer.Draw(style, w, h)); shapePreviews[layer.Id] = cached;
                        }
                        previewImages[layer.Id] = cached.Image;
                        if (masks.ContainsKey(layer.Id) && layer.MaskPlacement is null) layer.MaskPlacement = Copy(moved);
                    }
                }
            }
        }
        var snapshot = new ProjectSnapshot { Manifest = manifest, Images = previewImages, Masks = masks.ToDictionary(p => p.Key, p => p.Value.Mask) };
        var dirty = CanvasCompositor.ChangedLayersBounds(Source, snapshot, DirtyLayerIds);
        return new(this, snapshot, Copy(value), dirty);
    }

    private LayerTransform? MovedMask(ProjectLayerRecord layer, LayerTransform moved)
    {
        if (!masks.TryGetValue(layer.Id, out var asset) || asset.Mask.Width <= 1 && asset.Mask.Height <= 1) return null;
        var placement = layer.MaskLinked == false ? layer.MaskPlacement ?? layer.Transform : layer.MaskPlacement?.Following(layer.Transform, moved);
        return placement is null || SamePlacement(placement, moved) ? null : Copy(placement);
    }

    internal bool IsCurrent(ProjectSnapshot current) => ReferenceEquals(current, Source)
        && ManifestJson.Serialize(current.Manifest) == Manifest
        && current.Images.Count == sourceImages.Count && current.Masks.Count == sourceMasks.Count
        && sourceImages.All(p => current.Images.TryGetValue(p.Key, out var image) && ReferenceEquals(image, p.Value.Image) && image.Revision == p.Value.Revision)
        && sourceMasks.All(p => current.Masks.TryGetValue(p.Key, out var mask) && ReferenceEquals(mask, p.Value.Mask) && mask.Revision == p.Value.Revision);

    internal ProjectSnapshot PrepareCommit(TransformPreview preview)
    {
        var manifest = ManifestJson.Deserialize(ManifestJson.Serialize(preview.Snapshot.Manifest));
        // Only rounded rectangles use preview pixels. At commit all live shapes redraw at the final size.
        var committedImages = images.ToDictionary(p => p.Key, p => p.Value.Image);
        if (!TargetsMask)
        foreach (var layer in manifest.Layers.Where(l => members.Contains(l.Id)))
        {
            if (ShapeStyle.FromElement(layer.Shape) is not { } style) continue;
            int w = Round(layer.Transform.Size.Width), h = Round(layer.Transform.Size.Height);
            var image = committedImages[layer.Id];
            if (w == image.Width && h == image.Height || w > Limits.MaxSide || h > Limits.MaxSide || (long)w * h > Limits.MaxSurfacePixels) continue;
            long budget = committedImages.Sum(p => p.Key == layer.Id ? (long)w * h : p.Value.PixelCount) + masks.Sum(p => p.Value.Mask.PixelCount);
            if (budget > Limits.DocumentPixelBudget) continue;
            if (masks.ContainsKey(layer.Id) && layer.MaskPlacement is null) layer.MaskPlacement = Copy(layer.Transform);
            committedImages[layer.Id] = ShapeRasterizer.Draw(style, w, h);
        }
        return new() { Manifest = manifest, Images = committedImages, Masks = masks.ToDictionary(p => p.Key, p => p.Value.Mask) };
    }

    private static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);
    internal static LayerTransform Copy(LayerTransform value) => new()
    { Origin = value.Origin, Size = value.Size, Rotation = value.Rotation, FlipX = value.FlipX, FlipY = value.FlipY, Sampling = value.Sampling };
    internal static bool SamePlacement(LayerTransform a, LayerTransform b) => a.Origin == b.Origin && a.Size == b.Size
        && a.Rotation == b.Rotation && a.FlipX == b.FlipX && a.FlipY == b.FlipY;
    private static ProjectManifest CopyManifest(ProjectManifest source) => new()
    {
        Format = source.Format, Version = source.Version, ColorSpace = source.ColorSpace, Resolution = source.Resolution,
        DocumentId = source.DocumentId, Width = source.Width, Height = source.Height, ActiveLayerId = source.ActiveLayerId,
        Guides = source.Guides?.Select(g => new CanvasGuide { Id = g.Id, Axis = g.Axis, Position = g.Position }).ToList(),
        Timeline = source.Timeline?.Clone(),
        Layers = source.Layers.Select(l => new ProjectLayerRecord
        {
            Id = l.Id, Name = l.Name, IsVisible = l.IsVisible, Transform = Copy(l.Transform), ImageFile = l.ImageFile,
            ParentId = l.ParentId, IsGroup = l.IsGroup, Opacity = l.Opacity, BlendMode = l.BlendMode,
            MaskFile = l.MaskFile, MaskEnabled = l.MaskEnabled, MaskSourceId = l.MaskSourceId,
            MaskPlacement = l.MaskPlacement is { } placement ? Copy(placement) : null, MaskLinked = l.MaskLinked,
            Adjustment = l.Adjustment, Shape = l.Shape, Effects = l.Effects, Text = l.Text,
        }).ToList(),
    };
}

public sealed partial class EditorDocument
{
    public TransformEdit? BeginDuplicateTransformDraft(IEnumerable<Guid> selected, bool onMask = false)
    {
        var ids = selected.ToArray();
        if (ids.Any(id => !CanEditLayer(id, LayerLocks.Position, branch: true))) return null;
        if (TransformEdit.Create(Snapshot, ids, onMask) is null) return null;
        var duplicate = PrepareDuplication(Snapshot, ids);
        return duplicate is null ? null : TransformEdit.Create(duplicate.Snapshot, duplicate.Roots, onMask, Snapshot, duplicate);
    }
    public TransformEdit? BeginTransformDraft(IEnumerable<Guid> selected, bool onMask = false)
    {
        var ids = selected.ToArray();
        return ids.Any(id => !CanEditLayer(id, LayerLocks.Position, branch: true)) ? null : TransformEdit.Create(Snapshot, ids, onMask);
    }
    public bool CommitTransform(TransformPreview preview)
    {
        var edit = preview.Edit;
        if (!edit.IsCurrent(Snapshot) || preview.Corners is null && edit.DuplicateSnapshot is null && edit.Value.Equals(preview.Value)
            || preview.Corners is null && ManifestJson.Serialize(preview.Snapshot.Manifest) == edit.Manifest) return false;
        if (preview.Corners is not null && !preview.IsDistortCommit) return false;
        var committed = preview.Corners is null ? edit.PrepareCommit(preview) : preview.Snapshot;
        if (!LayerProtectionAllows(committed)) return false;
        BeginChange(); coalesceKey = null; Snapshot = committed; EndChange();
        return true;
    }
}
