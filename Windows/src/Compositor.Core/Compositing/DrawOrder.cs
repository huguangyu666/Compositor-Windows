namespace Compositor.Core;

/// <summary>
/// The layer hierarchy worked out from only what shapes it: each layer's id, folder and
/// visibility. Folders are pass-through — what's inside is drawn straight onto what is below,
/// never composited as a unit — so drawing order is every visible non-group layer, bottom to top.
/// </summary>
public static class DrawOrder
{
    public sealed class Result
    {
        /// <summary>Every layer and folder, depth first, each folder before what's inside it.</summary>
        public required IReadOnlyList<Guid> Order { get; init; }

        /// <summary>The ones that show: visible, in folders that are.</summary>
        public required IReadOnlySet<Guid> Visible { get; init; }

        /// <summary>The layers that draw, folders left out, bottom to top.</summary>
        public required IReadOnlyList<Guid> Drawn { get; init; }
    }

    public static Result Resolve(IReadOnlyList<ProjectLayerRecord> layers)
    {
        // Guid.Empty stands in for "no parent"; real layer ids are never empty.
        var children = new Dictionary<Guid, List<ProjectLayerRecord>>();
        foreach (var layer in layers)
        {
            var key = layer.ParentId ?? Guid.Empty;
            if (!children.TryGetValue(key, out var list)) children[key] = list = new List<ProjectLayerRecord>();
            list.Add(layer);
        }
        var order = new List<Guid>(layers.Count);
        var visible = new HashSet<Guid>();
        var drawn = new List<Guid>(layers.Count);
        Visit(children, Guid.Empty, depth: 0, shown: true, order, visible, drawn);
        return new Result { Order = order, Visible = visible, Drawn = drawn };
    }

    private static void Visit(
        Dictionary<Guid, List<ProjectLayerRecord>> children,
        Guid parent,
        int depth,
        bool shown,
        List<Guid> order,
        HashSet<Guid> visible,
        List<Guid> drawn)
    {
        // Group ancestors permit room for leaf nodes at the deepest level, as on the Mac.
        if (depth > 64 || !children.TryGetValue(parent, out var siblings)) return;
        foreach (var layer in siblings)
        {
            bool effective = shown && layer.IsVisible;
            order.Add(layer.Id);
            if (effective)
            {
                visible.Add(layer.Id);
                if (!layer.IsGroupOrFalse) drawn.Add(layer.Id);
            }
            if (layer.IsGroupOrFalse) Visit(children, layer.Id, depth + 1, effective, order, visible, drawn);
        }
    }
}

/// <summary>
/// A folder's opacity multiplies into everything inside it: a layer at 50% in a folder at 50%
/// shows at 25%, while the layer itself still reads 50% in the panel. The layer's own blend mode
/// still applies; folders do not introduce one of their own.
/// </summary>
public static class EffectiveOpacity
{
    public static double Resolve(Guid id, IReadOnlyDictionary<Guid, ProjectLayerRecord> byId)
    {
        double opacity = 1;
        int depth = 0;
        Guid? current = id;
        while (current is { } nodeId && depth < 64 && byId.TryGetValue(nodeId, out var node))
        {
            opacity *= node.OpacityOrOne;
            current = node.ParentId;
            depth += 1;
        }
        return opacity;
    }

    public static Dictionary<Guid, double> All(IReadOnlyList<ProjectLayerRecord> layers)
    {
        var byId = layers.ToDictionary(layer => layer.Id, layer => layer);
        return layers.ToDictionary(layer => layer.Id, layer => Resolve(layer.Id, byId));
    }
}
