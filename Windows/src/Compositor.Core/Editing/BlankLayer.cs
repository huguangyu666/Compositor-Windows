namespace Compositor.Core;

public sealed partial class EditorDocument
{
    /// <summary>Adds an asset-free layer above the active sibling, or at the top of its folder.</summary>
    public ProjectLayerRecord? AddBlankLayer()
    {
        if (ActiveTextDraft is not null || LayerCount >= Limits.MaxLayers) return null;
        var manifest = Snapshot.Manifest;
        var active = manifest.ActiveLayerId is { } id ? Find(id) : null;
        var layer = new ProjectLayerRecord
        {
            Id = Guid.NewGuid(), Name = UniqueNumberedName("Layer"), IsVisible = true,
            ParentId = active is { IsGroupOrFalse: true } ? active.Id : active?.ParentId,
            Transform = new() { Origin = PointD.Zero, Size = new(manifest.Width, manifest.Height) },
        };
        int insertion = active is null ? manifest.Layers.Count : manifest.Layers.IndexOf(active) + 1;
        if (active is { IsGroupOrFalse: true })
        {
            var children = Descendants(active.Id);
            insertion = Math.Max(insertion, manifest.Layers.FindLastIndex(item => children.Contains(item.Id)) + 1);
        }
        BeginChange(); manifest.Layers.Insert(insertion, layer); manifest.ActiveLayerId = layer.Id; EndChange();
        return layer;
    }

    // A blank layer acquires pixels only when a raster operation needs them. Keep this
    // detached until ReplaceLayerImage publishes it with the operation's single Undo.
    private Surface? RasterForPainting(ProjectLayerRecord layer)
    {
        if (layer.IsGroupOrFalse || layer.Adjustment is not null) return null;
        if (Snapshot.Images.TryGetValue(layer.Id, out var image)) return image;
        double width = Math.Max(1, Math.Round(layer.Transform.Size.Width)), height = Math.Max(1, Math.Round(layer.Transform.Size.Height));
        if (!double.IsFinite(width) || !double.IsFinite(height) || width > Limits.MaxSide || height > Limits.MaxSide
            || width * height > Limits.MaxSurfacePixels
            || Snapshot.Images.Sum(pair => pair.Value.PixelCount) + Snapshot.Masks.Sum(pair => pair.Value.PixelCount)
                + width * height > Limits.DocumentPixelBudget) return null;
        return new Surface((int)width, (int)height);
    }
}
