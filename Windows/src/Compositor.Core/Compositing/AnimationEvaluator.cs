namespace Compositor.Core;

/// <summary>Evaluates frozen metadata. Raster assets and the editable source remain unchanged.</summary>
public static class AnimationEvaluator
{
    public static double Sample(IReadOnlyList<AnimationKeyframe> keys, int frame, double fallback)
    {
        if (keys.Count == 0) return fallback;
        if (frame <= keys[0].Frame) return keys[0].Value;
        if (frame >= keys[^1].Frame) return keys[^1].Value;
        int low = 0, high = keys.Count - 1;
        while (high - low > 1) { int middle = (low + high) / 2; if (keys[middle].Frame <= frame) low = middle; else high = middle; }
        var left = keys[low]; var right = keys[high];
        double amount = (double)(frame - left.Frame) / (right.Frame - left.Frame);
        amount = left.Interpolation switch { "hold" => 0, "smooth" => amount * amount * (3 - 2 * amount), _ => amount };
        return left.Value + (right.Value - left.Value) * amount;
    }

    public static ProjectSnapshot Evaluate(ProjectSnapshot source, int frame)
    {
        var manifest = source.Manifest;
        if (manifest.Timeline is not { } timeline) return source;
        if (frame < 0 || frame >= timeline.DurationFrames) throw new ArgumentOutOfRangeException(nameof(frame));
        var tracks = timeline.Tracks.ToDictionary(track => track.LayerId);
        var layers = new List<ProjectLayerRecord>(manifest.Layers.Count);
        foreach (var original in manifest.Layers)
        {
            if (!tracks.TryGetValue(original.Id, out var track)) { layers.Add(original); continue; }
            var layer = original.CopyForAnimation(); var basis = original.Transform;
            layer.IsVisible &= frame >= track.InFrame && frame < track.OutFrame;
            layer.Opacity = Sample(track.Opacity, frame, original.OpacityOrOne);
            double width = Math.Clamp(basis.Size.Width * Sample(track.ScaleX, frame, 1), 1, 300_000);
            double height = Math.Clamp(basis.Size.Height * Sample(track.ScaleY, frame, 1), 1, 300_000);
            double x = Sample(track.PositionX, frame, basis.Center.X), y = Sample(track.PositionY, frame, basis.Center.Y);
            layer.Transform.Size = new(width, height);
            layer.Transform.Origin = new(x - width / 2, y - height / 2);
            layer.Transform.Rotation = Sample(track.Rotation, frame, basis.Rotation);
            if (original.MaskPlacement is { } mask && original.MaskLinked != false)
            {
                double cosine = Math.Cos(-basis.Radians), sine = Math.Sin(-basis.Radians);
                double dx = mask.Center.X - basis.Center.X, dy = mask.Center.Y - basis.Center.Y;
                var unit = new PointD(.5 + (dx * cosine - dy * sine) / basis.Size.Width, .5 + (dx * sine + dy * cosine) / basis.Size.Height);
                var center = layer.Transform.Point(unit); var size = new SizeD(mask.Size.Width * width / basis.Size.Width, mask.Size.Height * height / basis.Size.Height);
                layer.MaskPlacement = TransformEdit.Copy(mask); layer.MaskPlacement.Size = size;
                layer.MaskPlacement.Origin = new(center.X - size.Width / 2, center.Y - size.Height / 2);
                layer.MaskPlacement.Rotation += layer.Transform.Rotation - basis.Rotation;
            }
            layers.Add(layer);
        }
        return new ProjectSnapshot
        {
            Manifest = new ProjectManifest { Format = manifest.Format, Version = manifest.Version, ColorSpace = manifest.ColorSpace,
                Resolution = manifest.Resolution, DocumentId = manifest.DocumentId, Width = manifest.Width, Height = manifest.Height,
                ActiveLayerId = manifest.ActiveLayerId, Guides = manifest.Guides, Timeline = timeline, Layers = layers },
            Images = source.Images, Masks = source.Masks,
        };
    }
}
