namespace Compositor.Core;

public sealed partial class CanvasCompositor
{
    private ulong EffectsMaskGeometryKey(ProjectLayerRecord layer)
    {
        if (!layerMasks.TryGetValue(layer.Id, out var mask) || mask is null) return FnvOffset;
        ulong key = Mix(Mix(Mix(FnvOffset, 1), (ulong)mask.Width), (ulong)mask.Height);
        if (layer.MaskPlacement is { } placement && !SameGeometry(placement, layer.Transform))
        {
            key = MixTransform(MixTransform(key, placement), layer.Transform);
            // A plate's edge tone covers everything its pixels do not, so the whole layer's
            // coverage turns over when that tone flips — not just the pixels a stroke touched.
            key = Mix(key, MaskDisplay.Background(mask));
        }
        return key;
    }

    private void RememberMask(ProjectLayerRecord layer, EffectsCache cache)
    {
        cache.Mask = layerMasks.GetValueOrDefault(layer.Id);
        cache.MaskRevision = cache.Mask?.Revision ?? 0;
        cache.MaskGeometryKey = EffectsMaskGeometryKey(layer);
    }

    private bool TryGetEffectChanges(ProjectLayerRecord layer, Surface source, EffectsCache cache, out RectI dirty)
    {
        if (!source.TryGetChangesFrom(cache.Source, cache.SourceRevision, out dirty)
            || cache.MaskGeometryKey != EffectsMaskGeometryKey(layer)) return false;
        if (layerMasks.GetValueOrDefault(layer.Id) is not { } mask) return cache.MaskKey == EffectsMaskKey(layer);
        if (!mask.TryGetChangesFrom(cache.Mask, cache.MaskRevision, out var maskDirty)) return false;
        if (maskDirty.Width == 0 || maskDirty.Height == 0) return true;
        // The Mac maps mask patches into the effect source grid, including an independently
        // placed mask. Nearest coverage and pixel-center tests need one conservative pixel.
        PointD Map(PointD point)
        {
            if (layer.MaskPlacement is not { } placement || SameGeometry(placement, layer.Transform))
                return new(point.X * source.Width / mask.Width, point.Y * source.Height / mask.Height);
            var document = BrushEngine.PixelToDocument(placement, mask.Width, mask.Height, point);
            return BrushEngine.DocumentToPixel(layer.Transform, source.Width, source.Height, document);
        }
        var a = Map(new(maskDirty.X, maskDirty.Y));
        var b = Map(new(maskDirty.X + maskDirty.Width, maskDirty.Y));
        var c = Map(new(maskDirty.X, maskDirty.Y + maskDirty.Height));
        var d = Map(new(maskDirty.X + maskDirty.Width, maskDirty.Y + maskDirty.Height));
        var region = RectI.Normalize((int)Math.Floor(Math.Clamp(Math.Min(Math.Min(a.X, b.X), Math.Min(c.X, d.X)), -1, source.Width + 1)) - 1,
            (int)Math.Floor(Math.Clamp(Math.Min(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y)), -1, source.Height + 1)) - 1,
            (int)Math.Ceiling(Math.Clamp(Math.Max(Math.Max(a.X, b.X), Math.Max(c.X, d.X)), -1, source.Width + 1)) + 1,
            (int)Math.Ceiling(Math.Clamp(Math.Max(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y)), -1, source.Height + 1)) + 1, source.Width, source.Height);
        if (region.Width > 0 && region.Height > 0)
            dirty = dirty.Width == 0 || dirty.Height == 0 ? region : RectI.Union(dirty, region);
        return true;
    }
}
