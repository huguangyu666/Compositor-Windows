namespace Compositor.Core;

public sealed partial class CanvasCompositor
{
    // Coverage baking needs source/effects lookup, but no document-sized composition buffer.
    private CanvasCompositor(ProjectSnapshot snapshot, Surface workspace)
    {
        Snapshot = snapshot; canvas = workspace; Scale = 1; cacheLayers = false;
        documentWidth = snapshot.Manifest.Width; documentHeight = snapshot.Manifest.Height;
        RebuildSnapshot(snapshot);
    }

    internal static Dictionary<Guid, Surface> BakeDeletionMasks(ProjectSnapshot snapshot, IReadOnlyList<Guid> targets, CancellationToken cancellation)
    {
        var renderer = new CanvasCompositor(snapshot, new Surface(1, 1));
        var result = new Dictionary<Guid, Surface>();
        foreach (var id in targets)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!snapshot.Images.TryGetValue(id, out var image)) continue;
            var layer = renderer.byId[id];
            var output = image.Clone();
            if (!renderer.liveSources.TryGetValue(id, out var chain) || chain.Count == 0) Array.Clear(output.Pixels);
            else
            {
                var transform = layer.Transform;
                var corners = TransformDrag.HandlePositions(transform);
                // Effect windows are requested at document pixel centers; target-grid centers can
                // land between them. An outer pixel keeps every transformed sample inside the window.
                int left = (int)Math.Floor(corners.Min(p => p.X)) - 1, top = (int)Math.Floor(corners.Min(p => p.Y)) - 1;
                var requested = new RectI(left, top, (int)Math.Ceiling(corners.Max(p => p.X)) - left + 1,
                    (int)Math.Ceiling(corners.Max(p => p.Y)) - top + 1);
                double cos = Math.Cos(transform.Radians), sin = Math.Sin(transform.Radians);
                var center = transform.Center;
                foreach (var (sourceId, source, placement, opacity, mask) in chain)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var record = renderer.byId[sourceId];
                    var effects = renderer.RenderedEffectsFor(record, source, requested, generalMapping: true);
                    var coverage = effects ?? new SourceWindow(source);
                    var sourceMap = new DeletionCoverageMap(effects is null ? placement : renderer.PlacementTransform(record), coverage.Width, coverage.Height);
                    var maskMap = mask is null ? default : new DeletionCoverageMap(record.MaskPlacement ?? placement, mask.Width, mask.Height);
                    // Baking must keep the masked appearance, so it samples the source's mask
                    // exactly as the composite does — including the edge tone a plate placed
                    // apart shows past its own pixels.
                    byte maskOutside = mask is null ? (byte)0 : OutsideTone(mask, record.MaskPlacement ?? placement, record.Transform);
                    for (int y = 0; y < output.Height; y++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        double unitY = (y + .5) / output.Height;
                        double localY = ((transform.FlipY ? 1 - unitY : unitY) - .5) * transform.Size.Height;
                        for (int x = 0; x < output.Width; x++)
                        {
                            double unitX = (x + .5) / output.Width;
                            double localX = ((transform.FlipX ? 1 - unitX : unitX) - .5) * transform.Size.Width;
                            var point = new PointD(center.X + localX * cos - localY * sin, center.Y + localX * sin + localY * cos);
                            var sample = sourceMap.At(point);
                            byte alpha = SampleMaskAlpha(coverage, sample.X, sample.Y);
                            float amount = alpha / 255f * (float)opacity;
                            int index = y * output.Stride + x * 4;
                            if (amount < 1)
                                for (int channel = 0; channel < 4; channel++) output.Pixels[index + channel] = (byte)MathF.Round(output.Pixels[index + channel] * amount);
                            if (mask is null || effects is not null) continue;
                            var maskPoint = maskMap.At(point);
                            byte maskAlpha = SampleMask(mask, maskPoint.X, maskPoint.Y, maskOutside);
                            if (maskAlpha < 255)
                                for (int channel = 0; channel < 4; channel++) output.Pixels[index + channel] = (byte)(output.Pixels[index + channel] * maskAlpha / 255);
                        }
                    }
                }
            }
            result.Add(id, output);
        }
        return result;
    }

    private readonly struct DeletionCoverageMap(LayerTransform transform, int width, int height)
    {
        private readonly double cos = Math.Cos(transform.Radians), sin = Math.Sin(transform.Radians);
        private readonly PointD center = transform.Center;
        public PointD At(PointD point)
        {
            double dx = point.X - center.X, dy = point.Y - center.Y;
            double u = (dx * cos + dy * sin) / transform.Size.Width + .5;
            double v = (-dx * sin + dy * cos) / transform.Size.Height + .5;
            return new((transform.FlipX ? 1 - u : u) * width, (transform.FlipY ? 1 - v : v) * height);
        }
    }
}
