namespace Compositor.Core;

// Describe the complete visible stack without copying its source pixels. Unknown operations
// reject the whole frame, so hardware rendering never silently omits visible work.
internal sealed record GpuPreviewScene(ProjectSnapshot Snapshot, IReadOnlyList<ProjectLayerRecord> Layers)
{
    internal static GpuPreviewScene? TryCreate(ProjectSnapshot snapshot)
    {
        var manifest = snapshot.Manifest;
        var order = DrawOrder.Resolve(manifest.Layers);
        var byId = manifest.Layers.ToDictionary(layer => layer.Id);
        var layers = new List<ProjectLayerRecord>();
        foreach (var id in order.Visible)
        {
            var layer = byId[id];
            bool masked = layer.MaskFile is not null && layer.MaskEnabled != false;
            if (masked && !snapshot.Masks.ContainsKey(id)) return null;
        }
        foreach (var id in order.Drawn)
        {
            var layer = byId[id];
            if (layer.Adjustment is { } adjustment)
            {
                if (!Enum.IsDefined(adjustment.Kind) || !layer.Transform.IsValid) return null;
            }
            else if (layer.ImageFile is not null && snapshot.Images.ContainsKey(id))
            {
                if (!layer.Transform.IsValid) return null;
            }
            else continue;
            layers.Add(layer);
        }
        return layers.Count > 0 ? new(snapshot, layers) : null;
    }

    // Call from the serial render worker. This table depends only on seed and canvas size,
    // and is released after upload; the renderer retains only budgeted GPU textures.
    internal Dictionary<uint, float[]> PrepareGaussianNoise(IReadOnlySet<uint> resident, long byteBudget)
    {
        var seeds = Layers.Where(layer => layer.Adjustment is { Kind: AdjustmentKind.AddNoise, NoiseGaussian: true })
            .Select(layer => layer.Adjustment!.NoiseSeed ?? 0).Where(seed => !resident.Contains(seed)).Distinct().ToArray();
        long bytes = (long)Snapshot.Manifest.Width * Snapshot.Manifest.Height * 16;
        if (seeds.LongLength * bytes > byteBudget) throw new NotSupportedException("GPU noise preparation exceeds its memory budget.");
        return seeds.ToDictionary(seed => seed, seed => AdjustmentEngine.GaussianNoiseBasis(Snapshot.Manifest.Width, Snapshot.Manifest.Height, seed));
    }
}
