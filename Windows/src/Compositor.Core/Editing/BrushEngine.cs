namespace Compositor.Core;

/// <summary>A brush stroke's settings. Radius is in document pixels; hardness and opacity run 0–1.</summary>
public sealed record BrushSettings(double Radius, double Hardness, double Opacity, byte Red, byte Green, byte Blue);

/// <summary>
/// A stroke in progress, ported from the macOS brush architecture (docs/brush-performance.md):
/// a continuous round tip sweeps the smoothed pointer path, and soft coverage accumulates by
/// integrating paint deposition over the distance travelled — the result equals source-over
/// tips at 2.5% of diameter spacing (1.5% for hard tips), so crossings and corners blend
/// smoothly and the outcome is independent of pointer-event count. Deposited density lives in
/// 256×256 tiles; the permanent tiles and the provisional tail are separate, so replacing a
/// tail can never double-count. The opacity setting caps the entire accumulated stroke.
/// </summary>
public sealed partial class TileStroke
{
    private const int TileSize = 256;
    private const float MaxDensity = 20f; // exp(-20) < 1/255: saturated for 8-bit output

    private readonly Dictionary<(int X, int Y), float[]> permanent = new();
    private readonly Dictionary<(int X, int Y), float[]> tail = new();
    private readonly BrushSettings brush;
    private readonly LayerTransform transform;
    private readonly int surfaceWidth;
    private readonly int surfaceHeight;

    public TileStroke(BrushSettings brush, LayerTransform transform, int surfaceWidth, int surfaceHeight)
    {
        this.brush = brush;
        this.transform = transform;
        this.surfaceWidth = surfaceWidth;
        this.surfaceHeight = surfaceHeight;
    }

    /// <summary>The tail's tiles join the permanent coverage before a new segment is laid.</summary>
    public void RetireTail()
    {
        foreach (var ((tileX, tileY), densities) in tail)
        {
            var key = (tileX, tileY);
            if (!permanent.TryGetValue(key, out var target))
            {
                permanent[key] = densities;
                continue;
            }
            for (int i = 0; i < densities.Length; i++)
            {
                target[i] = MathF.Min(MaxDensity, target[i] + densities[i]);
            }
        }
        tail.Clear();
    }

    private int segmentMinX, segmentMinY, segmentMaxX, segmentMaxY;
    private PointD? sweepPosition;

    /// <summary>
    /// Continues the sweep to `to`, depositing into the tail. Dab spacing follows the tip
    /// kind: 2.5% of diameter for soft tips, 1.5% for hard ones. The tip is one continuous
    /// sweep along the pointer path: segments join without restamping the shared endpoint,
    /// so the deposit never depends on how the path was split into events.
    /// </summary>
    /// <returns>The surface-space rectangle the segment deposited into, for repainting.</returns>
    public RectI SweepSegment(PointD to)
    {
        PointD from = sweepPosition ?? to;
        bool hardTip = brush.Hardness >= 1;
        double spacing = Math.Max(0.05, brush.Radius * 2 * (hardTip ? 0.015 : 0.025));
        double distance = Math.Sqrt((to.X - from.X) * (to.X - from.X) + (to.Y - from.Y) * (to.Y - from.Y));
        int steps = Math.Max(1, (int)Math.Ceiling(distance / spacing));
        int firstStep = sweepPosition is null ? 0 : 1; // the shared endpoint is already deposited
        segmentMinX = surfaceWidth; segmentMinY = surfaceHeight;
        segmentMaxX = segmentMaxY = 0;
        for (int step = firstStep; step <= steps; step++)
        {
            double t = steps == 0 ? 0 : (double)step / steps;
            var point = new PointD(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t);
            DepositDab(point);
        }
        sweepPosition = to;
        if (segmentMaxX <= segmentMinX) return new RectI(0, 0, 0, 0);
        return RectI.Normalize(segmentMinX, segmentMinY, segmentMaxX, segmentMaxY, surfaceWidth, surfaceHeight);
    }

    private void DepositDab(PointD documentPoint)
    {
        // The dab's pixel-space radii follow the layer's scale, so the tip stays round in
        // document space on stretched or rotated layers.
        double scaleX = surfaceWidth / Math.Max(1, transform.Size.Width);
        double scaleY = surfaceHeight / Math.Max(1, transform.Size.Height);
        double radiusX = Math.Max(0.5, brush.Radius * scaleX);
        double radiusY = Math.Max(0.5, brush.Radius * scaleY);
        var pixel = BrushEngine.DocumentToPixel(transform, surfaceWidth, surfaceHeight, documentPoint);

        int x0 = Math.Max(0, (int)Math.Floor(pixel.X - radiusX));
        int x1 = Math.Min(surfaceWidth, (int)Math.Ceiling(pixel.X + radiusX) + 1);
        int y0 = Math.Max(0, (int)Math.Floor(pixel.Y - radiusY));
        int y1 = Math.Min(surfaceHeight, (int)Math.Ceiling(pixel.Y + radiusY) + 1);
        if (x0 >= x1 || y0 >= y1) return;

        double hardness = Math.Clamp(brush.Hardness, 0, 1);
        segmentMinX = Math.Min(segmentMinX, x0);
        segmentMinY = Math.Min(segmentMinY, y0);
        segmentMaxX = Math.Max(segmentMaxX, x1);
        segmentMaxY = Math.Max(segmentMaxY, y1);

        for (int y = y0; y < y1; y++)
        {
            int tileY = y / TileSize;
            int withinY = y % TileSize;
            int rowWithin = withinY * TileSize;
            for (int x = x0; x < x1; x++)
            {
                double dx = (x + 0.5 - pixel.X) / radiusX;
                double dy = (y + 0.5 - pixel.Y) / radiusY;
                double squared = dx * dx + dy * dy;
                if (squared > 1) continue;
                double distance = Math.Sqrt(squared);
                double falloff = distance <= hardness ? 1 : 1 - (distance - hardness) / (1 - hardness);
                if (falloff <= 0) continue;
                // Source-over accumulation in density space: −ln(1 − coverage) per dab, so
                // overlaps sum like stacked translucent strokes instead of maxing out.
                var key = (x / TileSize, tileY);
                if (!tail.TryGetValue(key, out var densities))
                {
                    tail[key] = densities = new float[TileSize * TileSize];
                }
                int within = rowWithin + x % TileSize;
                densities[within] = MathF.Min(MaxDensity, densities[within] + (float)-Math.Log(1 - falloff));
            }
        }
    }

    /// <summary>
    /// Rebuilds the working surface over `region` from the base pixels plus the stroke's
    /// accumulated coverage: coverage = (1 − e^−density) capped by the stroke opacity. Paint
    /// applies source-over with the brush color; erase scales the premultiplied pixel down.
    /// </summary>
    public void PaintRegion(Surface working, Surface baseSurface, int x0, int y0, int x1, int y1, Selection? selection, bool erase)
    {
        working.MarkModified(RectI.Normalize(x0, y0, x1, y1, working.Width, working.Height));
        float cap = (float)Math.Clamp(brush.Opacity, 0, 1);
        float red = brush.Red, green = brush.Green, blue = brush.Blue;
        // The pixel→document map is affine, so the selection's document position steps
        // linearly across a row: differenced once here instead of running the transform's
        // trig per pixel (this loop is the repaint hot path under a stroke).
        int minX = Math.Max(0, x0), minY = Math.Max(0, y0);
        double stepDocX = 0, stepDocY = 0, rowDocX = 0, rowDocY = 0, rowStepDocX = 0, rowStepDocY = 0;
        if (selection is not null)
        {
            var origin = BrushEngine.PixelToDocument(transform, working.Width, working.Height, new PointD(minX + 0.5, minY + 0.5));
            var stepXPoint = BrushEngine.PixelToDocument(transform, working.Width, working.Height, new PointD(minX + 1.5, minY + 0.5));
            var stepYPoint = BrushEngine.PixelToDocument(transform, working.Width, working.Height, new PointD(minX + 0.5, minY + 1.5));
            stepDocX = stepXPoint.X - origin.X;
            stepDocY = stepXPoint.Y - origin.Y;
            rowDocX = origin.X;
            rowDocY = origin.Y;
            rowStepDocX = stepYPoint.X - origin.X;
            rowStepDocY = stepYPoint.Y - origin.Y;
        }
        for (int y = minY; y < Math.Min(working.Height, y1); y++)
        {
            int row = y * working.Stride;
            int tileY = y / TileSize;
            int withinY = y % TileSize;
            int baseRow = y * baseSurface.Stride;
            double docX = rowDocX, docY = rowDocY;
            for (int x = minX; x < Math.Min(working.Width, x1); x++)
            {
                float density = DensityAt(x, y, tileY, withinY);
                int index = row + x * 4;
                int baseIndex = baseRow + x * 4;
                float coverage = 0;
                if (density > 0)
                {
                    coverage = (1f - MathF.Exp(-density)) * cap;
                    if (selection is not null)
                    {
                        coverage *= selection.CoverageAt((int)docX, (int)docY) / 255f;
                    }
                }
                docX += stepDocX;
                docY += stepDocY;
                if (coverage <= 0)
                {
                    // Outside the stroke the working pixels are the base's.
                    working.Pixels[index] = baseSurface.Pixels[baseIndex];
                    working.Pixels[index + 1] = baseSurface.Pixels[baseIndex + 1];
                    working.Pixels[index + 2] = baseSurface.Pixels[baseIndex + 2];
                    working.Pixels[index + 3] = baseSurface.Pixels[baseIndex + 3];
                    continue;
                }
                working.Pixels[index] = baseSurface.Pixels[baseIndex];
                working.Pixels[index + 1] = baseSurface.Pixels[baseIndex + 1];
                working.Pixels[index + 2] = baseSurface.Pixels[baseIndex + 2];
                working.Pixels[index + 3] = baseSurface.Pixels[baseIndex + 3];
                if (erase)
                {
                    float keep = 1 - coverage;
                    for (int channel = 0; channel < 4; channel++)
                    {
                        working.Pixels[index + channel] = (byte)MathF.Round(working.Pixels[index + channel] * keep);
                    }
                }
                else
                {
                    float keep = 1 - coverage;
                    working.Pixels[index] = (byte)Math.Min(255, red * coverage + working.Pixels[index] * keep);
                    working.Pixels[index + 1] = (byte)Math.Min(255, green * coverage + working.Pixels[index + 1] * keep);
                    working.Pixels[index + 2] = (byte)Math.Min(255, blue * coverage + working.Pixels[index + 2] * keep);
                    working.Pixels[index + 3] = (byte)Math.Min(255, MathF.Round(coverage * 255) + working.Pixels[index + 3] * keep);
                }
            }
            rowDocX += rowStepDocX;
            rowDocY += rowStepDocY;
        }
    }

    /// <summary>Mask painting: the brush color's luminance lerps the gray value by the coverage.</summary>
    public void PaintMaskRegion(MaskSurface working, MaskSurface baseMask, int x0, int y0, int x1, int y1, Selection? selection, bool erase)
    {
        working.MarkModified(RectI.Normalize(x0, y0, x1, y1, working.Width, working.Height));
        float cap = (float)Math.Clamp(brush.Opacity, 0, 1);
        byte value = erase ? (byte)0 : (byte)Math.Clamp((brush.Red * 30 + brush.Green * 59 + brush.Blue * 11) / 100, 0, 255);
        // The same affine row stepping as PaintRegion: one difference instead of per-pixel trig.
        int minX = Math.Max(0, x0), minY = Math.Max(0, y0);
        double stepDocX = 0, stepDocY = 0, rowDocX = 0, rowDocY = 0, rowStepDocX = 0, rowStepDocY = 0;
        if (selection is not null)
        {
            var origin = BrushEngine.PixelToDocument(transform, working.Width, working.Height, new PointD(minX + 0.5, minY + 0.5));
            var stepXPoint = BrushEngine.PixelToDocument(transform, working.Width, working.Height, new PointD(minX + 1.5, minY + 0.5));
            var stepYPoint = BrushEngine.PixelToDocument(transform, working.Width, working.Height, new PointD(minX + 0.5, minY + 1.5));
            stepDocX = stepXPoint.X - origin.X;
            stepDocY = stepXPoint.Y - origin.Y;
            rowDocX = origin.X;
            rowDocY = origin.Y;
            rowStepDocX = stepYPoint.X - origin.X;
            rowStepDocY = stepYPoint.Y - origin.Y;
        }
        for (int y = minY; y < Math.Min(working.Height, y1); y++)
        {
            int tileY = y / TileSize;
            int withinY = y % TileSize;
            double docX = rowDocX, docY = rowDocY;
            for (int x = minX; x < Math.Min(working.Width, x1); x++)
            {
                float density = DensityAt(x, y, tileY, withinY);
                float coverage = density <= 0 ? 0 : (1f - MathF.Exp(-density)) * cap;
                if (selection is not null)
                {
                    coverage *= selection.CoverageAt((int)docX, (int)docY) / 255f;
                }
                docX += stepDocX;
                docY += stepDocY;
                int index = y * working.Width + x;
                working.Pixels[index] = (byte)MathF.Round(baseMask.Pixels[index] * (1 - coverage) + value * coverage);
            }
            rowDocX += rowStepDocX;
            rowDocY += rowStepDocY;
        }
    }

    internal int SurfaceWidth => surfaceWidth;

    internal int SurfaceHeight => surfaceHeight;

    private float DensityAt(int x, int y, int tileY, int withinY)
    {
        var key = (x / TileSize, tileY);
        int within = withinY * TileSize + x % TileSize;
        float density = 0;
        if (permanent.TryGetValue(key, out var settled)) density += settled[within];
        if (tail.TryGetValue(key, out var provisional)) density += provisional[within];
        return density;
    }

    /// <summary>The bounding rectangle of everything the stroke has deposited so far.</summary>
    public RectI Bounds()
    {
        if (permanent.Count == 0 && tail.Count == 0) return new RectI(0, 0, 0, 0);
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        foreach (var ((tileX, tileY), _) in permanent.Concat(tail))
        {
            minX = Math.Min(minX, tileX * TileSize);
            minY = Math.Min(minY, tileY * TileSize);
            maxX = Math.Max(maxX, (tileX + 1) * TileSize);
            maxY = Math.Max(maxY, (tileY + 1) * TileSize);
        }
        return RectI.Normalize(minX, minY, maxX, maxY, surfaceWidth, surfaceHeight);
    }
}

/// <summary>
/// Stateless brush helpers: the document↔layer pixel mapping the stroke shares with the
/// resampler.
/// </summary>
public static class BrushEngine
{
    /// <summary>The document band a round stroke can change, including source-pixel
    /// quantization and the presentation filter's footprint on a placed grid.</summary>
    public static RectI StrokePresentationBounds(LayerTransform transform, int pixelWidth, int pixelHeight, PointD from, PointD to, double radius)
    {
        double sx = Math.Max(1, transform.Size.Width / Math.Max(1, pixelWidth));
        double sy = Math.Max(1, transform.Size.Height / Math.Max(1, pixelHeight));
        double filter = transform.Sampling switch { LayerSampling.Nearest => .5, LayerSampling.Smooth => 1, _ => 2 };
        double reach = radius + (filter + .5) * Math.Sqrt(sx * sx + sy * sy) + 1;
        int left = (int)Math.Floor(Math.Min(from.X, to.X) - reach), top = (int)Math.Floor(Math.Min(from.Y, to.Y) - reach);
        return new(left, top, (int)Math.Ceiling(Math.Max(from.X, to.X) + reach) - left,
            (int)Math.Ceiling(Math.Max(from.Y, to.Y) + reach) - top);
    }

    /// <summary>The same inverse mapping the resampler draws with: document pixel → layer pixel.</summary>
    public static PointD DocumentToPixel(LayerTransform transform, int imageWidth, int imageHeight, PointD documentPoint)
    {
        var center = transform.Center;
        double localX = documentPoint.X - center.X, localY = documentPoint.Y - center.Y;
        double radians = transform.Radians;
        double cos = Math.Cos(radians), sin = Math.Sin(radians);
        double boxX = localX * cos + localY * sin;
        double boxY = -localX * sin + localY * cos;
        double unitX = boxX / transform.Size.Width + 0.5;
        double unitY = boxY / transform.Size.Height + 0.5;
        double pixelX = (transform.FlipX ? 1 - unitX : unitX) * imageWidth;
        double pixelY = (transform.FlipY ? 1 - unitY : unitY) * imageHeight;
        return new PointD(pixelX, pixelY);
    }

    /// <summary>The forward mapping: layer pixel center → document point.</summary>
    public static PointD PixelToDocument(LayerTransform transform, int imageWidth, int imageHeight, PointD pixelPoint)
    {
        double unitX = pixelPoint.X / imageWidth, unitY = pixelPoint.Y / imageHeight;
        if (transform.FlipX) unitX = 1 - unitX;
        if (transform.FlipY) unitY = 1 - unitY;
        return transform.Point(new PointD(unitX, unitY));
    }
}
