namespace Compositor.Core;

/// <summary>A virtual source-resolution grid covering the source and canvas, as in
/// BrushStroke.swift. Only the source plus touched 256-pixel tiles become a mask asset.</summary>
internal sealed class MaskStrokeGrid
{
    internal readonly MaskSurface Original;
    internal readonly BrushSettings Brush;
    internal readonly LayerTransform OriginalPlacement, Placement;
    internal readonly RectI SourceBounds;
    internal readonly int Width, Height;
    internal readonly PointD Column, Row;
    private readonly int canvasWidth, canvasHeight;
    private readonly long otherPixels;
    private readonly byte background;
    private readonly double halfX, halfY;
    internal bool CanPaintDirect => SourceBounds == new RectI(0, 0, Width, Height)
        && Original.Width == Width && Original.Height == Height && OriginalPlacement.Origin == PointD.Zero
        && OriginalPlacement.Rotation % 360 == 0 && OriginalPlacement.Size.Width == canvasWidth && OriginalPlacement.Size.Height == canvasHeight;

    private MaskStrokeGrid(MaskSurface original, BrushSettings brush, LayerTransform placed, int sw, int sh,
        int left, int top, int width, int height, int canvasWidth, int canvasHeight, long otherPixels)
    {
        Original = original; Brush = brush; OriginalPlacement = placed;
        SourceBounds = new(-left, -top, sw, sh); Width = width; Height = height;
        this.canvasWidth = canvasWidth; this.canvasHeight = canvasHeight; this.otherPixels = otherPixels;
        background = MaskDisplay.Background(original);
        Placement = TransformFor(new(0, 0, width, height));
        double c = Math.Cos(placed.Radians), s = Math.Sin(placed.Radians);
        double sx = placed.Size.Width / sw * (placed.FlipX ? -1 : 1), sy = placed.Size.Height / sh * (placed.FlipY ? -1 : 1);
        Column = new(sx * c, sx * s); Row = new(-sy * s, sy * c);
        halfX = (Math.Abs(Column.X) + Math.Abs(Row.X)) / 2; halfY = (Math.Abs(Column.Y) + Math.Abs(Row.Y)) / 2;
    }

    internal static MaskStrokeGrid? Create(ProjectSnapshot source, ProjectLayerRecord layer, MaskSurface original, BrushSettings brush)
    {
        var placed = layer.MaskPlacement ?? layer.Transform;
        if (!placed.IsValid || !double.IsFinite(brush.Radius) || brush.Radius is <= 0 or > 1050
            || !double.IsFinite(brush.Hardness) || !double.IsFinite(brush.Opacity) || brush.Opacity <= 0) return null;
        var image = source.Images.GetValueOrDefault(layer.Id);
        bool independent = layer.MaskPlacement is not null, solid = independent && original.Width <= 2 && original.Height <= 2;
        double sw = independent && !solid ? original.Width : independent || image is null ? Math.Round(placed.Size.Width) : image.Width;
        double sh = independent && !solid ? original.Height : independent || image is null ? Math.Round(placed.Size.Height) : image.Height;
        if (sw is < 1 or > Limits.MaxSide || sh is < 1 or > Limits.MaxSide) return null;
        var corners = new[] { new PointD(0, 0), new(source.Manifest.Width, 0), new(0, source.Manifest.Height), new(source.Manifest.Width, source.Manifest.Height) }
            .Select(point => BrushEngine.DocumentToPixel(placed, (int)sw, (int)sh, point)).ToArray();
        double left = Math.Floor(Math.Min(0, corners.Min(point => point.X)) + 1e-8);
        double top = Math.Floor(Math.Min(0, corners.Min(point => point.Y)) + 1e-8);
        double width = Math.Ceiling(Math.Max(sw, corners.Max(point => point.X)) - 1e-8) - left;
        double height = Math.Ceiling(Math.Max(sh, corners.Max(point => point.Y)) - 1e-8) - top;
        if (width is < 1 or > 1_000_000_000 || height is < 1 or > 1_000_000_000) return null;
        var copy = new LayerTransform { Origin = placed.Origin, Size = placed.Size, Rotation = placed.Rotation,
            FlipX = placed.FlipX, FlipY = placed.FlipY, Sampling = placed.Sampling };
        long other = source.Images.Sum(pair => pair.Value.PixelCount) + source.Masks.Sum(pair => pair.Key == layer.Id ? 0 : pair.Value.PixelCount);
        var grid = new MaskStrokeGrid(original, brush, copy, (int)sw, (int)sh, (int)left, (int)top,
            (int)width, (int)height, source.Manifest.Width, source.Manifest.Height, other);
        return grid.CanMaterialize(grid.SourceBounds) ? grid : null;
    }

    internal PointD DocumentPoint(double x, double y) => BrushEngine.PixelToDocument(OriginalPlacement, SourceBounds.Width, SourceBounds.Height,
        new(x - SourceBounds.X, y - SourceBounds.Y));

    internal LayerTransform TransformFor(RectI bounds)
    {
        if (bounds == SourceBounds) return OriginalPlacement;
        var center = DocumentPoint(bounds.X + bounds.Width / 2d, bounds.Y + bounds.Height / 2d);
        var size = new SizeD(bounds.Width * OriginalPlacement.Size.Width / SourceBounds.Width, bounds.Height * OriginalPlacement.Size.Height / SourceBounds.Height);
        return new() { Origin = new(center.X - size.Width / 2, center.Y - size.Height / 2), Size = size,
            Rotation = OriginalPlacement.Rotation, FlipX = OriginalPlacement.FlipX, FlipY = OriginalPlacement.FlipY, Sampling = OriginalPlacement.Sampling };
    }

    internal bool CanMaterialize(RectI bounds) => bounds.Width is > 0 and <= Limits.MaxSide && bounds.Height is > 0 and <= Limits.MaxSide
        && (long)bounds.Width * bounds.Height <= Limits.MaxSurfacePixels && otherPixels + (long)bounds.Width * bounds.Height <= Limits.DocumentPixelBudget
        && TransformFor(bounds).IsValid;

    // Preflight the segment before TileStroke allocates density tiles or changes coverage.
    internal RectI CandidateBounds(RectI current, PointD from, PointD to)
    {
        var a = BrushEngine.DocumentToPixel(Placement, Width, Height, from); var b = BrushEngine.DocumentToPixel(Placement, Width, Height, to);
        double rx = Math.Max(.5, Brush.Radius * Width / Math.Max(1, Placement.Size.Width));
        double ry = Math.Max(.5, Brush.Radius * Height / Math.Max(1, Placement.Size.Height));
        int x0 = (int)Math.Clamp(Math.Floor((Math.Min(a.X, b.X) - rx) / 256) * 256, 0, Width);
        int y0 = (int)Math.Clamp(Math.Floor((Math.Min(a.Y, b.Y) - ry) / 256) * 256, 0, Height);
        int x1 = (int)Math.Clamp(Math.Ceiling((Math.Max(a.X, b.X) + rx + 1) / 256) * 256, 0, Width);
        int y1 = (int)Math.Clamp(Math.Ceiling((Math.Max(a.Y, b.Y) + ry + 1) / 256) * 256, 0, Height);
        return x1 <= x0 || y1 <= y0 ? current : RectI.Union(current, new(x0, y0, x1 - x0, y1 - y0));
    }

    internal byte BaseAt(int x, int y)
    {
        int sx = x - SourceBounds.X, sy = y - SourceBounds.Y;
        if ((uint)sx >= (uint)SourceBounds.Width || (uint)sy >= (uint)SourceBounds.Height) return background;
        if (Original.Width == SourceBounds.Width && Original.Height == SourceBounds.Height) return Original.Pixels[sy * Original.Width + sx];
        return MaskDisplay.Sample(Original, (sx + .5) * Original.Width / SourceBounds.Width,
            (sy + .5) * Original.Height / SourceBounds.Height, OriginalPlacement.Sampling);
    }

    internal MaskSurface Materialize(RectI bounds, MaskSurface? previous = null, RectI previousBounds = default)
    {
        if (previous is null && bounds == SourceBounds && Original.Width == bounds.Width && Original.Height == bounds.Height) return Original.CloneForEditing();
        var output = new MaskSurface(bounds.Width, bounds.Height);
        if (Original.Width == 1 && Original.Height == 1) output.Pixels.AsSpan().Fill(Original.Pixels[0]);
        else
        {
            output.Pixels.AsSpan().Fill(background);
            for (int y = SourceBounds.Y; y < SourceBounds.Y + SourceBounds.Height; y++)
            {
                int at = (y - bounds.Y) * bounds.Width + SourceBounds.X - bounds.X;
                if (Original.Width == SourceBounds.Width && Original.Height == SourceBounds.Height)
                    Original.Pixels.AsSpan((y - SourceBounds.Y) * Original.Width, Original.Width).CopyTo(output.Pixels.AsSpan(at, Original.Width));
                else for (int x = 0; x < SourceBounds.Width; x++) output.Pixels[at + x] = BaseAt(SourceBounds.X + x, y);
            }
        }
        if (previous is not null)
            for (int y = 0; y < previous.Height; y++) previous.Pixels.AsSpan(y * previous.Width, previous.Width)
                .CopyTo(output.Pixels.AsSpan((y + previousBounds.Y - bounds.Y) * bounds.Width + previousBounds.X - bounds.X, previous.Width));
        return output;
    }

    internal float CoverageAt(PointD center, Selection? selection)
    {
        if (center.X + halfX <= 0 || center.Y + halfY <= 0 || center.X - halfX >= canvasWidth || center.Y - halfY >= canvasHeight) return 0;
        if (center.X - halfX >= -1e-8 && center.Y - halfY >= -1e-8 && center.X + halfX <= canvasWidth + 1e-8 && center.Y + halfY <= canvasHeight + 1e-8)
            return selection?.CoverageAt((int)center.X, (int)center.Y) / 255f ?? 1;
        float total = 0;
        for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++)
        {
            double u = (x + .5) / 4 - .5, v = (y + .5) / 4 - .5;
            double px = center.X + Column.X * u + Row.X * v, py = center.Y + Column.Y * u + Row.Y * v;
            if (px >= 0 && py >= 0 && px < canvasWidth && py < canvasHeight) total += selection?.CoverageAt((int)px, (int)py) / 255f ?? 1;
        }
        return total / 16;
    }
}
