namespace Compositor.Core;

public sealed partial class CanvasCompositor
{
    private readonly Dictionary<Guid, ulong> renderedMaskGeometry = new();

    private RectI ExpandMaskGeometryDirty(RectI requested, out bool changed)
    {
        changed = false;
        foreach (var layer in Snapshot.Manifest.Layers)
        {
            ulong previous = renderedMaskGeometry.GetValueOrDefault(layer.Id, FnvOffset);
            if (previous == EffectsMaskGeometryKey(layer)) continue;
            var footprint = ChangedLayerBounds(Snapshot, Snapshot, layer.Id);
            if (footprint.Width == 0 || footprint.Height == 0) continue;
            int left = (int)Math.Floor(footprint.X * Scale), top = (int)Math.Floor(footprint.Y * Scale);
            var band = new RectI(left, top, (int)Math.Ceiling((footprint.X + footprint.Width) * Scale) - left,
                (int)Math.Ceiling((footprint.Y + footprint.Height) * Scale) - top);
            requested = RectI.Union(requested, band); changed = true;
        }
        return requested;
    }

    private void RememberRenderedMaskGeometry()
    {
        renderedMaskGeometry.Clear();
        foreach (var layer in Snapshot.Manifest.Layers)
            if (layer.MaskFile is not null) renderedMaskGeometry[layer.Id] = EffectsMaskGeometryKey(layer);
    }
}
