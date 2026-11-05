namespace Compositor.Core;

public sealed partial class Selection
{
    /// <summary>Transforms the hard outline once; feather remains a document-pixel scalar.
    /// The bounded raster keeps the logical shape, including pixels outside the canvas.</summary>
    public Selection Transformed(LayerTransform original, LayerTransform value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!original.IsValid || !value.IsValid) throw new ArgumentOutOfRangeException(nameof(value));
        if (IsEmpty) return this;
        if (original.Size == value.Size && original.Rotation == value.Rotation && original.FlipX == value.FlipX && original.FlipY == value.FlipY)
        {
            double dx = value.Origin.X - original.Origin.X, dy = value.Origin.Y - original.Origin.Y;
            if (Math.Abs(dx - Math.Round(dx)) < 1e-8 && Math.Abs(dy - Math.Round(dy)) < 1e-8)
                return Translated(checked((int)Math.Round(dx)), checked((int)Math.Round(dy)));
        }
        PointD Map(PointD point) => BrushEngine.PixelToDocument(value, 1, 1, BrushEngine.DocumentToPixel(original, 1, 1, point));
        PointD Inverse(PointD point) => BrushEngine.PixelToDocument(original, 1, 1, BrushEngine.DocumentToPixel(value, 1, 1, point));
        return RasterizeTransformed(Map, p => Inverse(p), true, cancellationToken);
    }

    internal Selection? Warped(LayerTransform original, LayerTransform value, PointD[] corners, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Source mapPath returns no selection for a folded perspective.
        if (!DistortWarp.IsConvex(corners)) return null;
        var forward = DistortWarp.Forward(corners); var inverse = DistortWarp.Inverse(corners);
        PointD Map(PointD point)
        {
            var unit = BrushEngine.DocumentToPixel(original, 1, 1, point);
            return forward(new(value.FlipX ? 1 - unit.X : unit.X, value.FlipY ? 1 - unit.Y : unit.Y));
        }
        PointD? Inverse(PointD point)
        {
            if (inverse(point) is not { } unit) return null;
            return BrushEngine.PixelToDocument(original, 1, 1, new(value.FlipX ? 1 - unit.X : unit.X, value.FlipY ? 1 - unit.Y : unit.Y));
        }
        return RasterizeTransformed(Map, Inverse, false, cancellationToken);
    }

    private Selection RasterizeTransformed(Func<PointD, PointD> map, Func<PointD, PointD?> inverse, bool affine, CancellationToken cancellationToken)
    {
        var bounds = OutlineBounds;
        var corners = new[] { new PointD(bounds.X, bounds.Y), new(bounds.X + bounds.Width, bounds.Y),
            new(bounds.X + bounds.Width, bounds.Y + bounds.Height), new(bounds.X, bounds.Y + bounds.Height) }.Select(map).ToArray();
        double minX = Math.Floor(corners.Min(p => p.X) + 1e-8), minY = Math.Floor(corners.Min(p => p.Y) + 1e-8);
        double width = Math.Ceiling(corners.Max(p => p.X) - 1e-8) - minX, height = Math.Ceiling(corners.Max(p => p.Y) - 1e-8) - minY;
        if (!double.IsFinite(minX + minY + width + height) || Math.Abs(minX) > 1_000_000_000 || Math.Abs(minY) > 1_000_000_000
            || width is < 1 or > Limits.MaxSide || height is < 1 or > Limits.MaxSide || width * height > Limits.MaxSurfacePixels)
            throw new ArgumentOutOfRangeException(nameof(map), "The transformed selection exceeds the supported limits.");
        int left = (int)minX, top = (int)minY; var coverage = new MaskSurface((int)width, (int)height);
        var origin = inverse(new(left, top)) ?? PointD.Zero; var px = inverse(new(left + 1, top)) ?? PointD.Zero; var py = inverse(new(left, top + 1)) ?? PointD.Zero;
        double xx = px.X - origin.X, xy = px.Y - origin.Y, yx = py.X - origin.X, yy = py.Y - origin.Y;
        int x0 = coverage.Width, y0 = coverage.Height, x1 = 0, y1 = 0;
        for (int y = 0; y < coverage.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < coverage.Width; x++)
            {
                int amount = 0;
                // The Windows mask is the source outline; this approximates CGPath AA.
                for (int sy = 0; sy < 4; sy++) for (int sx = 0; sx < 4; sx++)
                {
                    double u = x + (sx + .5) / 4, v = y + (sy + .5) / 4;
                    var point = affine ? new PointD(origin.X + u * xx + v * yx, origin.Y + u * xy + v * yy) : inverse(new(left + u, top + v));
                    if (point is { } p && double.IsFinite(p.X) && double.IsFinite(p.Y) && Math.Abs(p.X) <= 1_000_000_000 && Math.Abs(p.Y) <= 1_000_000_000)
                        amount += HardCoverageAt((int)Math.Floor(p.X), (int)Math.Floor(p.Y));
                }
                byte selected = (byte)((amount + 8) / 16); coverage.Pixels[y * coverage.Width + x] = selected;
                if (selected == 0) continue;
                x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x + 1); y1 = Math.Max(y1, y + 1);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(coverage, x1 <= x0 || y1 <= y0 ? default : new(x0, y0, x1 - x0, y1 - y0), Feather, left, top, documentWidth, documentHeight);
    }
}
