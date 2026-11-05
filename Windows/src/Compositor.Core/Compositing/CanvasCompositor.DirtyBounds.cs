namespace Compositor.Core;

public sealed partial class CanvasCompositor
{
    /// <summary>Expands a requested presentation band for outstanding effect-source edits.
    /// Call on the serial render owner after SetSnapshot and before Render, and present
    /// the returned band. Unknown inputs and live-mask consumers conservatively use their
    /// whole footprints; known pixel edits map the effect halo through the layer placement.</summary>
    public RectI ExpandPixelDirty(RectI requested)
    {
        var result = ExpandMaskGeometryDirty(requested, out bool changed);
        foreach (var layer in Snapshot.Manifest.Layers)
        {
            if (layer.ImageFile is null || !Snapshot.Images.TryGetValue(layer.Id, out var source)) continue;
            var cache = EffectsFor(layer);
            if (cache.Effects is null && cache.Source is null) continue;
            bool known = TryGetEffectChanges(layer, source, cache, out var dirty);
            bool full = !known
                || cache.RenderedHash != cache.Hash || cache.PlacementKey != MixTransform(FnvOffset, layer.Transform);
            if (!full && dirty.Width == 0 && dirty.Height == 0) continue;
            // Live-mask chains can feed hidden sources into consumers outside this layer's
            // drawn order. Keep the established closure fallback until their regional path
            // has its own pixel-equivalence coverage.
            full |= Snapshot.Manifest.Layers.Any(candidate => candidate.MaskSourceId == layer.Id);
            RectI band;
            if (full)
            {
                var footprint = ChangedLayerBounds(Snapshot, Snapshot, layer.Id);
                int x0 = (int)Math.Floor(footprint.X * Scale), y0 = (int)Math.Floor(footprint.Y * Scale);
                band = new(x0, y0, (int)Math.Ceiling((footprint.X + footprint.Width) * Scale) - x0,
                    (int)Math.Ceiling((footprint.Y + footprint.Height) * Scale) - y0);
                if (cache.LastBounds.Width > 0 && cache.LastBounds.Height > 0) band = RectI.Union(band, cache.LastBounds);
            }
            else
            {
                if (dirty.Width == 0 || dirty.Height == 0) continue;
                int inset = cache.Effects is null ? 0 : LayerEffectsRenderer.Margin(cache.Effects);
                int reach = cache.Effects is null ? 4 : LayerEffectsRenderer.Reach(cache.Effects, source.Width + inset * 2, source.Height + inset * 2) + 4;
                double left = Math.Max(-inset, dirty.X - reach), top = Math.Max(-inset, dirty.Y - reach);
                double right = Math.Min(source.Width + inset, dirty.X + dirty.Width + reach), bottom = Math.Min(source.Height + inset, dirty.Y + dirty.Height + reach);
                var transform = Scaled(layer.Transform);
                var points = new[] { new PointD(left, top), new(right, top), new(left, bottom), new(right, bottom) }
                    .Select(point => BrushEngine.PixelToDocument(transform, source.Width, source.Height, point)).ToArray();
                int x0 = (int)Math.Floor(points.Min(point => point.X)), y0 = (int)Math.Floor(points.Min(point => point.Y));
                band = new(x0, y0, (int)Math.Ceiling(points.Max(point => point.X)) - x0, (int)Math.Ceiling(points.Max(point => point.Y)) - y0);
            }
            if (band.Width > 0 && band.Height > 0) result = RectI.Union(result, band);
            changed = true;
        }
        int grow = changed ? (int)Math.Ceiling(Snapshot.Manifest.Layers.Sum(layer => layer.Adjustment is { } adjustment
            ? AdjustmentEngine.SamplingMargin(adjustment) * Scale : 0)) + 2 : 0;
        return RectI.Normalize(result.X - grow, result.Y - grow, result.X + result.Width + grow,
            result.Y + result.Height + grow, canvas.Width, canvas.Height);
    }

    /// <summary>Conservative old/new footprint of a pixel or mask draft, including effects,
    /// folder/live-mask consumers and sampling adjustments above it. Coordinates are document
    /// pixels. Grid expansion or broad dependencies may still require the entire canvas.</summary>
    public static RectI ChangedLayerBounds(ProjectSnapshot previous, ProjectSnapshot next, Guid layerId)
        => ChangedLayersBounds(previous, next, [layerId]);

    /// <summary>A group transform resolves dependencies once for the entire member set.</summary>
    public static RectI ChangedLayersBounds(ProjectSnapshot previous, ProjectSnapshot next, IEnumerable<Guid> layerIds)
    {
        if (previous.Manifest.Width != next.Manifest.Width || previous.Manifest.Height != next.Manifest.Height)
            return new(0, 0, next.Manifest.Width, next.Manifest.Height);
        var ids = layerIds.ToArray();
        var old = AffectedBounds(previous, ids);
        var current = AffectedBounds(next, ids);
        if (old.Width == 0 || old.Height == 0) return current;
        if (current.Width == 0 || current.Height == 0) return old;
        return RectI.Union(old, current);
    }

    private static RectI AffectedBounds(ProjectSnapshot snapshot, IEnumerable<Guid> layerIds)
    {
        var manifest = snapshot.Manifest;
        var records = manifest.Layers.ToDictionary(layer => layer.Id);
        var order = DrawOrder.Resolve(manifest.Layers);
        var affected = layerIds.ToHashSet();
        // A mask can affect descendants or chains of clipping sources. Iterate to closure,
        // rather than restricting the dirty band to the selected layer's own placement.
        bool added;
        do
        {
            added = false;
            foreach (var layer in manifest.Layers)
                if (!affected.Contains(layer.Id) && (layer.ParentId is { } parent && affected.Contains(parent)
                    || layer.MaskSourceId is { } source && affected.Contains(source)))
                    added |= affected.Add(layer.Id);
        } while (added);
        RectI result = new(0, 0, 0, 0);
        bool belowChanged = false;
        double halo = 0;
        foreach (var id in order.Drawn)
        {
            var layer = records[id];
            if (affected.Contains(id))
            {
                var transform = layer.Transform;
                if (layer.Effects is { } json && layer.ImageFile is not null && snapshot.Images.TryGetValue(id, out var image))
                {
                    var effects = LayerEffectsRenderer.Visible(json);
                    int inset = effects is null ? 0 : LayerEffectsRenderer.Margin(effects);
                    double w = image.Width + 2d * inset, h = image.Height + 2d * inset;
                    if (w <= Limits.MaxSide && h <= Limits.MaxSide && w * h <= Limits.MaxSurfacePixels)
                    {
                        var size = new SizeD(transform.Size.Width * w / image.Width, transform.Size.Height * h / image.Height);
                        transform = new() { Origin = new(transform.Center.X - size.Width / 2, transform.Center.Y - size.Height / 2),
                            Size = size, Rotation = transform.Rotation, FlipX = transform.FlipX, FlipY = transform.FlipY, Sampling = transform.Sampling };
                    }
                }
                var (x0, y0, x1, y1) = Resampler.BoundingRect(transform, manifest.Width, manifest.Height);
                var footprint = new RectI(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
                if (footprint.Width > 0 && footprint.Height > 0)
                    result = result.Width == 0 || result.Height == 0 ? footprint : RectI.Union(result, footprint);
                belowChanged = true;
            }
            if (belowChanged && layer.Adjustment is { } adjustment) halo += AdjustmentEngine.SamplingMargin(adjustment);
        }
        if (result.Width == 0 || result.Height == 0) return result;
        int grow = (int)Math.Ceiling(halo) + 2;
        return RectI.Normalize(result.X - grow, result.Y - grow, result.X + result.Width + grow,
            result.Y + result.Height + grow, manifest.Width, manifest.Height);
    }
}
