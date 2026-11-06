using System.Text.Json;

namespace Compositor.Core;

internal sealed record LayerDuplication(ProjectSnapshot Snapshot, IReadOnlyList<Guid> Roots, IReadOnlyDictionary<Guid, Guid> Mapping);

public sealed partial class EditorDocument
{
    // SelectionClipboard.swift: prune selected descendants, insert each subtree, then place
    // multiple roots together above the highest original in top-first panel order.
    internal static LayerDuplication? PrepareDuplication(ProjectSnapshot source, IEnumerable<Guid> selected)
    {
        var selectedIds = selected.ToHashSet();
        var original = source.Manifest.Layers; var byId = original.ToDictionary(l => l.Id);
        var roots = original.Where(l => selectedIds.Contains(l.Id) && !HasSelectedParent(l)).ToArray();
        if (roots.Length == 0) return null;
        var manifest = ManifestJson.Deserialize(ManifestJson.Serialize(source.Manifest));
        var images = new Dictionary<Guid, Surface>(source.Images); var masks = new Dictionary<Guid, MaskSurface>(source.Masks);
        var rootCopies = new Dictionary<Guid, Guid>(); var mapping = new Dictionary<Guid, Guid>();
        var children = original.GroupBy(l => l.ParentId).ToDictionary(g => g.Key ?? Guid.Empty, g => g.ToArray());
        foreach (var root in roots)
        {
            var included = new HashSet<Guid>(); var pending = new Stack<Guid>(); pending.Push(root.Id);
            while (pending.TryPop(out var id))
            {
                if (!included.Add(id)) continue;
                if (children.TryGetValue(id, out var nested)) foreach (var child in nested) pending.Push(child.Id);
            }
            if (manifest.Layers.Count + included.Count > Limits.MaxLayers) continue;
            var local = included.ToDictionary(id => id, _ => Guid.NewGuid());
            var copies = original.Where(l => included.Contains(l.Id)).Select(layer =>
            {
                var copy = JsonSerializer.Deserialize<ProjectLayerRecord>(JsonSerializer.Serialize(layer, MementoOptions), MementoOptions)!;
                copy.Id = local[layer.Id]; if (layer.Id == root.Id) copy.Name += " copy";
                if (copy.ImageFile is not null) copy.ImageFile = ProjectValidator.Uuid(copy.Id) + ".png";
                if (copy.MaskFile is not null) copy.MaskFile = ProjectValidator.Uuid(copy.Id) + ".mask.png";
                if (copy.ParentId is { } parent && local.TryGetValue(parent, out var parentCopy)) copy.ParentId = parentCopy;
                if (copy.MaskSourceId is { } maskSource && local.TryGetValue(maskSource, out var maskCopy)) copy.MaskSourceId = maskCopy;
                if (source.Images.TryGetValue(layer.Id, out var image)) images[copy.Id] = image;
                if (source.Masks.TryGetValue(layer.Id, out var mask)) masks[copy.Id] = mask;
                return copy;
            }).ToArray();
            manifest.Layers.InsertRange(manifest.Layers.FindIndex(l => l.Id == root.Id) + 1, copies);
            foreach (var pair in local) mapping.Add(pair.Key, pair.Value);
            rootCopies.Add(root.Id, local[root.Id]);
        }
        if (rootCopies.Count == 0) return null;
        if (rootCopies.Count > 1)
        {
            var panel = new List<Guid>();
            Visit(Guid.Empty, 0);
            var originals = panel.Where(rootCopies.ContainsKey).ToArray();
            var top = originals[0]; var parent = byId[top].ParentId; var below = top;
            foreach (var id in originals.AsEnumerable().Reverse())
            {
                var copy = manifest.Layers.Single(l => l.Id == rootCopies[id]);
                manifest.Layers.Remove(copy); copy.ParentId = parent;
                manifest.Layers.Insert(manifest.Layers.FindIndex(l => l.Id == below) + 1, copy);
                AdoptDuplicateClipping(copy, manifest.Layers); ReleaseDetachedClipping(manifest.Layers);
                below = copy.Id;
            }
            void Visit(Guid parentId, int depth)
            {
                if (depth > 64 || !children.TryGetValue(parentId, out var siblings)) return;
                foreach (var layer in siblings.AsEnumerable().Reverse())
                { panel.Add(layer.Id); if (layer.IsGroupOrFalse) Visit(layer.Id, depth + 1); }
            }
        }
        manifest.ActiveLayerId = source.Manifest.ActiveLayerId is { } active && rootCopies.TryGetValue(active, out var activeCopy)
            ? activeCopy : rootCopies.Values.First();
        if (manifest.Timeline is { } timeline)
            timeline.Tracks.AddRange(source.Manifest.Timeline!.Tracks.Where(track => mapping.ContainsKey(track.LayerId))
                .Select(track => track.Clone(mapping[track.LayerId])));
        try { ProjectValidator.Validate(manifest); }
        catch (ProjectFormatException) { return null; }
        return new(new() { Manifest = manifest, Images = images, Masks = masks }, Array.AsReadOnly(rootCopies.Values.ToArray()),
            new System.Collections.ObjectModel.ReadOnlyDictionary<Guid, Guid>(mapping));

        bool HasSelectedParent(ProjectLayerRecord layer)
        {
            var parent = layer.ParentId;
            for (int depth = 0; depth < 64 && parent is { } id && byId.TryGetValue(id, out var ancestor); depth++)
            { if (selectedIds.Contains(id)) return true; parent = ancestor.ParentId; }
            return false;
        }
    }

    // LiveLayerMask.swift: a root placed inside a contiguous clipping stack joins its base.
    private static void AdoptDuplicateClipping(ProjectLayerRecord layer, List<ProjectLayerRecord> layers)
    {
        if (layer.IsGroupOrFalse) return;
        var siblings = layers.Where(l => l.ParentId == layer.ParentId).ToArray();
        int index = Array.FindIndex(siblings, l => l.Id == layer.Id);
        if (index <= 0 || index + 1 >= siblings.Length || siblings[index + 1].MaskSourceId is not { } source || source == layer.Id) return;
        var below = siblings[index - 1];
        if (below.Id == source || below.MaskSourceId == source) layer.MaskSourceId = source;
    }
}
