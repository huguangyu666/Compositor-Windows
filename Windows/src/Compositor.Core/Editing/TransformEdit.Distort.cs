namespace Compositor.Core;

public sealed partial class TransformEdit
{
    private TransformPreview? lastDistortPreview;

    /// <summary>Distort.swift: prepare full-resolution pixels on the serial worker; publication owns history.</summary>
    public TransformPreview? PrepareDistortCommit(LayerTransform value, PointD[] corners, CancellationToken cancellationToken = default) =>
        DistortPreview(value, corners, true, cancellationToken);

    private TransformPreview? DistortPreview(LayerTransform value, PointD[] corners, bool commit, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!value.IsValid || !DistortWarp.IsUsable(corners)) return null;
        if (!commit && lastDistortPreview is { } cached && cached.Value.Equals(value) && cached.Corners!.AsSpan().SequenceEqual(corners)) return cached;
        var ordinary = Preview(value)!; var manifest = ordinary.Snapshot.Manifest;
        var resultImages = images.ToDictionary(p => p.Key, p => p.Value.Image);
        var resultMasks = masks.ToDictionary(p => p.Key, p => p.Value.Mask);
        int? limit = commit ? null : 2048;
        foreach (var layer in manifest.Layers.Where(l => members.Contains(l.Id)))
        {
            token.ThrowIfCancellationRequested();
            if (TargetsMask)
            {
                ValidateWarpBudget(resultImages, resultMasks, layer.Id, corners, limit, true);
                var moved = DistortWarp.WarpMask(resultMasks[layer.Id], value, corners, MaskDisplay.Background(resultMasks[layer.Id]), limit, token);
                resultMasks[layer.Id] = moved.Mask;
                layer.MaskPlacement = SamePlacement(moved.Transform, layer.Transform) ? null : moved.Transform;
                continue;
            }
            var target = layer.Transform;
            var shape = IsGroup ? DistortWarp.Carried(target, value, corners) : corners;
            if (!DistortWarp.IsUsable(shape)) continue;
            ValidateWarpBudget(resultImages, resultMasks, layer.Id, shape, limit, false);
            var warped = DistortWarp.Warp(images[layer.Id].Image, target, shape, limit, token);
            var source = basis.Layers.First(l => l.Id == layer.Id);
            RectI crop = new(0, 0, warped.Image.Width, warped.Image.Height);
            if (commit) crop = AlphaBounds(warped.Image, token);
            resultImages[layer.Id] = Crop(warped.Image, crop, token);
            layer.Transform = CropPlacement(warped.Transform, warped.Image.Width, warped.Image.Height, crop);
            if (masks.TryGetValue(layer.Id, out var owned))
            {
                if (source.MaskLinked != false && source.MaskPlacement is null)
                {
                    ValidateWarpBudget(resultImages, resultMasks, layer.Id, shape, limit, true);
                    var mask = DistortWarp.WarpMask(owned.Mask, target, shape, 0, limit, token);
                    resultMasks[layer.Id] = owned.Mask.Width == 1 && owned.Mask.Height == 1 ? owned.Mask : Crop(mask.Mask, crop, token);
                    layer.MaskPlacement = null;
                }
                else if (source.MaskLinked != false && layer.MaskPlacement is { } placed
                    && DistortWarp.Carried(placed, target, shape) is { } carried && DistortWarp.IsConvex(carried))
                {
                    ValidateWarpBudget(resultImages, resultMasks, layer.Id, carried, limit, true);
                    var mask = DistortWarp.WarpMask(owned.Mask, placed, carried, MaskDisplay.Background(owned.Mask), limit, token);
                    resultMasks[layer.Id] = mask.Mask; layer.MaskPlacement = mask.Transform;
                }
                else layer.MaskPlacement = Copy(source.MaskPlacement ?? source.Transform);
            }
        }
        if (resultImages.Sum(p => p.Value.PixelCount) + resultMasks.Sum(p => p.Value.PixelCount) > Limits.DocumentPixelBudget)
            throw new InvalidOperationException("The distorted assets exceed the document pixel budget.");
        var snapshot = new ProjectSnapshot { Manifest = manifest, Images = resultImages, Masks = resultMasks };
        if (commit) ProjectValidator.Validate(snapshot.Manifest);
        var result = new TransformPreview(this, snapshot, Copy(value), CanvasCompositor.ChangedLayersBounds(Source, snapshot, DirtyLayerIds))
            { Corners = corners.ToArray(), IsDistortCommit = commit };
        token.ThrowIfCancellationRequested();
        if (!commit) lastDistortPreview = result;
        return result;
    }

    private static void ValidateWarpBudget(Dictionary<Guid, Surface> pixels, Dictionary<Guid, MaskSurface> masks,
        Guid id, PointD[] shape, int? limit, bool mask)
    {
        double width = Math.Ceiling(shape.Max(p => p.X)) - Math.Floor(shape.Min(p => p.X));
        double height = Math.Ceiling(shape.Max(p => p.Y)) - Math.Floor(shape.Min(p => p.Y));
        double factor = limit is { } cap ? Math.Min(1, cap / Math.Max(width, height)) : 1;
        long count = (long)Math.Ceiling(width * factor) * (long)Math.Ceiling(height * factor);
        long total = pixels.Sum(p => p.Key == id && !mask ? count : p.Value.PixelCount)
            + masks.Sum(p => p.Key == id && mask ? count : p.Value.PixelCount);
        if (total > Limits.DocumentPixelBudget) throw new InvalidOperationException("The distorted assets exceed the document pixel budget.");
    }

    private static RectI AlphaBounds(Surface image, CancellationToken token)
    {
        int left = image.Width, top = image.Height, right = 0, bottom = 0;
        for (int y = 0; y < image.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < image.Width; x++) if (image.Pixels[y * image.Stride + x * 4 + 3] != 0)
            { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x + 1); bottom = y + 1; }
        }
        return right <= left ? new(0, 0, image.Width, image.Height) : new(left, top, right - left, bottom - top);
    }
    private static Surface Crop(Surface image, RectI crop, CancellationToken token)
    {
        if (crop == new RectI(0, 0, image.Width, image.Height)) return image;
        var result = new Surface(crop.Width, crop.Height);
        for (int y = 0; y < crop.Height; y++)
        { token.ThrowIfCancellationRequested(); image.Pixels.AsSpan((y + crop.Y) * image.Stride + crop.X * 4, crop.Width * 4).CopyTo(result.Pixels.AsSpan(y * result.Stride)); }
        return result;
    }
    private static MaskSurface Crop(MaskSurface mask, RectI crop, CancellationToken token)
    {
        if (crop == new RectI(0, 0, mask.Width, mask.Height)) return mask;
        var result = new MaskSurface(crop.Width, crop.Height);
        for (int y = 0; y < crop.Height; y++)
        { token.ThrowIfCancellationRequested(); mask.Pixels.AsSpan((y + crop.Y) * mask.Width + crop.X, crop.Width).CopyTo(result.Pixels.AsSpan(y * result.Width)); }
        return result;
    }
    private static LayerTransform CropPlacement(LayerTransform value, int width, int height, RectI crop) => new()
    { Origin = new(value.Origin.X + crop.X * value.Size.Width / width, value.Origin.Y + crop.Y * value.Size.Height / height),
        Size = new(crop.Width * value.Size.Width / width, crop.Height * value.Size.Height / height), Sampling = value.Sampling };
}
