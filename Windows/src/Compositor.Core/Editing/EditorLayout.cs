using System.Text.Json.Nodes;

namespace Compositor.Core;

public static partial class EditorAutomation
{
    private static Guid? ApplyLayout(EditorDocument owner, JsonObject args)
    {
        var manifest = owner.Snapshot.Manifest;
        string? operation = args["operation"]?.GetValue<string>();
        if (operation == "crop")
        {
            int x = Integer(args, "x", 0, manifest.Width - 1), y = Integer(args, "y", 0, manifest.Height - 1);
            int width = Integer(args, "width", 1, manifest.Width - x), height = Integer(args, "height", 1, manifest.Height - y);
            var selection = owner.ActiveSelection?.Reframed(x, y, width, height);
            if (!owner.SetCanvasBounds(x, y, width, height)) throw new ArgumentException("Invalid crop bounds.");
            owner.SetSelection(selection);
            return manifest.ActiveLayerId;
        }
        if (args["layerIds"] is not JsonArray { Count: >= 1 and <= 256 } supplied)
            throw new ArgumentException("Supply 1–256 distinct layer UUIDs.");
        var byId = manifest.Layers.ToDictionary(l => l.Id);
        var ids = supplied.Select(node => Guid.TryParse(node?.GetValue<string>(), out var id) && byId.ContainsKey(id)
            ? id : throw new ArgumentException("Choose existing layer UUIDs.")).ToHashSet();
        if (ids.Count != supplied.Count) throw new ArgumentException("layerIds must be distinct.");
        bool HasAncestor(ProjectLayerRecord layer, HashSet<Guid> ancestors)
        {
            var parent = layer.ParentId;
            while (parent is { } id) { if (ancestors.Contains(id)) return true; parent = byId[id].ParentId; }
            return false;
        }
        var roots = manifest.Layers.Where(l => ids.Contains(l.Id) && !HasAncestor(l, ids)).ToArray();
        if (roots.Any(l => !owner.CanEditLayer(l.Id, operation is "align" or "distribute" ? LayerLocks.Position : LayerLocks.None, branch: true)))
            throw new ArgumentException("Unlock the selected layers before changing their layout.");
        if (operation == "group")
        {
            var folder = owner.GroupLayers(roots.Select(l => l.Id)) ?? throw new ArgumentException("These layers cannot be grouped.");
            if (args["name"] is { } name) owner.RenameLayer(folder.Id, Name(name.GetValue<string>()));
            return folder.Id;
        }
        if (operation == "ungroup")
        {
            if (supplied.Count != 1 || !roots[0].IsGroupOrFalse) throw new ArgumentException("Choose one folder to ungroup.");
            if (manifest.Layers.Any(l => l.MaskSourceId == roots[0].Id)) throw new ArgumentException("Resolve live-mask dependencies before ungrouping.");
            if (!owner.Ungroup(roots[0].Id)) throw new ArgumentException("The folder cannot be ungrouped.");
            return manifest.ActiveLayerId;
        }
        if (operation is not ("align" or "distribute")) throw new ArgumentException("Unknown layout operation.");
        var entries = roots.Select(root =>
        {
            HashSet<Guid> rootId = [root.Id];
            var members = manifest.Layers.Where(l => l.Id == root.Id || HasAncestor(l, rootId)).ToArray();
            var corners = members.Where(l => !l.IsGroupOrFalse && owner.Snapshot.Images.ContainsKey(l.Id))
                .SelectMany(l => new[] { l.Transform.Point(new(0, 0)), l.Transform.Point(new(1, 0)), l.Transform.Point(new(1, 1)), l.Transform.Point(new(0, 1)) }).ToArray();
            if (corners.Length == 0) throw new ArgumentException("Each layout root needs drawable content.");
            return (Root: root, Members: members, Left: corners.Min(p => p.X), Top: corners.Min(p => p.Y), Right: corners.Max(p => p.X), Bottom: corners.Max(p => p.Y));
        }).ToArray();
        void Move(ProjectLayerRecord[] members, double dx, double dy)
        {
            if (Math.Abs(dx) < 1e-9 && Math.Abs(dy) < 1e-9) return;
            foreach (var layer in members)
            {
                var old = layer.Transform;
                var next = TransformEdit.Copy(old); next.Origin = new(old.Origin.X + dx, old.Origin.Y + dy);
                if (!next.IsValid) throw new ArgumentException("Layout exceeds the supported coordinate bounds.");
                // Independent masks stay in document space; linked masks and folder masks follow.
                if (owner.Snapshot.Masks.TryGetValue(layer.Id, out var mask) && (mask.Width > 1 || mask.Height > 1))
                {
                    if (layer.MaskLinked == false) layer.MaskPlacement ??= TransformEdit.Copy(old);
                    else if (layer.MaskPlacement is { } placement)
                    {
                        var moved = TransformEdit.Copy(placement); moved.Origin = new(placement.Origin.X + dx, placement.Origin.Y + dy);
                        if (!moved.IsValid) throw new ArgumentException("Mask placement exceeds the supported coordinate bounds.");
                        layer.MaskPlacement = moved;
                    }
                }
                layer.Transform = next;
            }
        }
        if (operation == "align")
        {
            string target = args["relativeTo"]?.GetValue<string>() ?? "layers";
            if (target is not ("canvas" or "layers") || target == "layers" && entries.Length < 2)
                throw new ArgumentException("Align at least two roots, or use relativeTo=canvas.");
            string? alignment = args["alignment"]?.GetValue<string>();
            if (alignment is not ("left" or "center" or "right" or "top" or "middle" or "bottom")) throw new ArgumentException("Choose an advertised alignment.");
            double left = target == "canvas" ? 0 : entries.Min(e => e.Left), top = target == "canvas" ? 0 : entries.Min(e => e.Top);
            double right = target == "canvas" ? manifest.Width : entries.Max(e => e.Right), bottom = target == "canvas" ? manifest.Height : entries.Max(e => e.Bottom);
            foreach (var e in entries)
                Move(e.Members, alignment switch { "left" => left - e.Left, "center" => (left + right - e.Left - e.Right) / 2, "right" => right - e.Right, _ => 0 },
                    alignment switch { "top" => top - e.Top, "middle" => (top + bottom - e.Top - e.Bottom) / 2, "bottom" => bottom - e.Bottom, _ => 0 });
        }
        else
        {
            if (entries.Length < 3) throw new ArgumentException("Distribute at least three independent roots.");
            string? axis = args["axis"]?.GetValue<string>(); string spacing = args["spacing"]?.GetValue<string>() ?? "gaps";
            if (axis is not ("horizontal" or "vertical") || spacing is not ("gaps" or "centers")) throw new ArgumentException("Choose an advertised axis and spacing.");
            var ordered = entries.Select(e => (e.Members, Start: axis == "horizontal" ? e.Left : e.Top, End: axis == "horizontal" ? e.Right : e.Bottom))
                .OrderBy(e => spacing == "centers" ? (e.Start + e.End) / 2 : e.Start).ToArray();
            double cursor = spacing == "centers" ? (ordered[0].Start + ordered[0].End) / 2 : ordered[0].End;
            double step = spacing == "centers" ? ((ordered[^1].Start + ordered[^1].End) / 2 - cursor) / (ordered.Length - 1)
                : (ordered[^1].End - ordered[0].Start - ordered.Sum(e => e.End - e.Start)) / (ordered.Length - 1);
            for (int i = 1; i < ordered.Length - 1; i++)
            {
                var e = ordered[i]; cursor += step;
                double delta = cursor - (spacing == "centers" ? (e.Start + e.End) / 2 : e.Start);
                Move(e.Members, axis == "horizontal" ? delta : 0, axis == "vertical" ? delta : 0);
                if (spacing == "gaps") cursor += e.End - e.Start;
            }
        }
        return manifest.ActiveLayerId;
    }
}
