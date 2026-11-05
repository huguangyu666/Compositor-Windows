namespace Compositor.Core;

public sealed partial class EditorDocument
{
    /// <summary>Remove empty margins from a raster layer and its attached mask in one undo step.</summary>
    public bool TrimLayerTransparency(Guid id)
    {
        var layer = Find(id);
        if (layer is null || layer.IsGroupOrFalse || layer.Text is not null || layer.Shape is not null
            || layer.Adjustment is not null || layer.Effects is not null || !Snapshot.Images.TryGetValue(id, out var image)) return false;
        MaskSurface? mask = layer.MaskFile is not null ? Snapshot.Masks.GetValueOrDefault(id) : null;
        bool uniform = mask is { Width: 1, Height: 1 };
        if (mask is not null && (!uniform && (mask.Width != image.Width || mask.Height != image.Height)
            || layer.MaskPlacement is { } independent && !independent.Equals(layer.Transform))) return false;
        if (GrownLayerGrid.CoverageBounds(image, layer.MaskEnabled == false ? null : mask) is not { } crop
            || crop == new RectI(0, 0, image.Width, image.Height)) return false;
        var placement = GrownLayerGrid.Placed(layer.Transform, image.Width, image.Height, crop.X, crop.Y, crop.Width, crop.Height);
        var cropped = image.Crop(crop.X, crop.Y, crop.Width, crop.Height);
        var masks = new Dictionary<Guid, MaskSurface>(Snapshot.Masks);
        if (mask is not null && !uniform)
        {
            var cutMask = new MaskSurface(crop.Width, crop.Height);
            for (int y = 0; y < crop.Height; y++)
                mask.Pixels.AsSpan((y + crop.Y) * mask.Width + crop.X, crop.Width).CopyTo(cutMask.Pixels.AsSpan(y * crop.Width));
            masks[id] = cutMask;
        }
        BeginChange();
        if (mask is not null && layer.MaskPlacement is not null) layer.MaskPlacement = placement;
        layer.Transform = placement;
        Snapshot = new ProjectSnapshot { Manifest = Snapshot.Manifest,
            Images = new Dictionary<Guid, Surface>(Snapshot.Images) { [id] = cropped }, Masks = masks };
        EndChange();
        return true;
    }
}
