namespace Compositor.Core;

public sealed record TransformPressResult(Guid LayerId, bool Picked);

public static class TransformPress
{
    public static IReadOnlyList<Guid> LayersAt(ProjectSnapshot source, PointD point)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) return [];
        var byId = source.Manifest.Layers.ToDictionary(l => l.Id);
        return DrawOrder.Resolve(source.Manifest.Layers).Drawn.Reverse()
            .Where(id => HasContent(source, byId, byId[id], point)).ToArray();
    }
    // EditorCanvas.transformPressLayer: an edited mask/group box can differ from pixel geometry.
    public static TransformPressResult? Pick(ProjectSnapshot source, PointD point, Guid? activeId,
        LayerTransform? editedTransform, bool group, bool editing, bool autoSelect, bool control = false, bool shift = false,
        bool maskTarget = false)
    {
        var byId = source.Manifest.Layers.ToDictionary(l => l.Id);
        var order = DrawOrder.Resolve(source.Manifest.Layers);
        var under = order.Drawn.AsEnumerable().Reverse().Select(id => byId[id])
            .FirstOrDefault(l => HasContent(source, byId, l, point));
        var active = activeId is { } id && byId.TryGetValue(id, out var layer) && !layer.IsGroupOrFalse
            && source.Images.ContainsKey(id) && order.Visible.Contains(id) ? layer : null;
        bool picks = !editing, auto = autoSelect != control;
        if (picks && control && (shift || !autoSelect) && under is not null) return new(under.Id, true);
        if (group && activeId is { } groupId && byId.ContainsKey(groupId)
            && (editedTransform?.Contains(point) == true || !(picks && auto) || under is null)) return new(groupId, false);
        if (active is not null && (editedTransform ?? active.Transform).Contains(point))
        {
            if (picks && auto && under is not null && under.Id != active.Id
                && (!maskTarget || order.Drawn.TakeWhile(id => id != under.Id).Count()
                    > order.Drawn.TakeWhile(id => id != active.Id).Count())) return new(under.Id, true);
            return new(active.Id, false);
        }
        if (picks && auto && under is not null) return new(under.Id, true);
        return active is null ? null : new(active.Id, false);
    }

    private static bool HasContent(ProjectSnapshot source, IReadOnlyDictionary<Guid, ProjectLayerRecord> byId,
        ProjectLayerRecord layer, PointD point)
    {
        if (EffectiveOpacity.Resolve(layer.Id, byId) <= 0 || !HasPixel(layer) || !Reveals(layer)) return false;
        var parent = layer.ParentId;
        for (int depth = 0; parent is { } id && depth < 64 && byId.TryGetValue(id, out var folder); depth++)
        {
            if (!Reveals(folder)) return false;
            parent = folder.ParentId;
        }
        var clip = layer.MaskSourceId;
        for (int depth = 0; clip is { } id && depth < 256; depth++)
        {
            if (!byId.TryGetValue(id, out var plate) || EffectiveOpacity.Resolve(id, byId) <= 0
                || !HasPixel(plate) || !Reveals(plate)) return false;
            clip = plate.MaskSourceId;
        }
        return true;

        bool HasPixel(ProjectLayerRecord record)
        {
            if (!source.Images.TryGetValue(record.Id, out var image) || !record.Transform.Contains(point)) return false;
            var pixel = record.Transform.PixelOf(point, new(image.Width, image.Height));
            int x = (int)Math.Floor(pixel.X), y = (int)Math.Floor(pixel.Y);
            return (uint)x < (uint)image.Width && (uint)y < (uint)image.Height
                && image.Pixels[y * image.Stride + x * 4 + 3] != 0;
        }

        bool Reveals(ProjectLayerRecord record)
        {
            if (record.MaskFile is null || record.MaskEnabled == false || !source.Masks.TryGetValue(record.Id, out var mask)) return true;
            var placement = record.MaskPlacement ?? record.Transform;
            var pixel = placement.PixelOf(point, new(mask.Width, mask.Height));
            if (pixel.X >= 0 && pixel.Y >= 0 && pixel.X < mask.Width && pixel.Y < mask.Height)
                return mask.Pixels[(int)pixel.Y * mask.Width + (int)pixel.X] != 0;
            // Independent masks carry their edge tone outside their own rectangle.
            var covered = record.Transform;
            bool apart = placement.Origin != covered.Origin || placement.Size != covered.Size
                || placement.Rotation != covered.Rotation || placement.FlipX != covered.FlipX || placement.FlipY != covered.FlipY;
            return apart && MaskDisplay.Background(mask) != 0;
        }
    }
}
