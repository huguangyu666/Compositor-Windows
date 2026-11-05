namespace Compositor.Core;

/// <summary>A virtual RGBA grid covering the source and the canvas, as BrushStroke.swift
/// does for images: the pointer paints anywhere on the canvas, only the source plus touched
/// 256-pixel tiles become pixels, and the commit trims to the source ∪ painted alpha so one
/// dab far from the layer grows the asset by its own reach instead of the whole canvas.</summary>
internal sealed class ImageStrokeGrid
{
    internal readonly Surface? Original;
    internal readonly BrushSettings Brush;
    internal readonly LayerTransform OriginalPlacement, Placement;
    internal readonly RectI SourceBounds;
    internal readonly int Width, Height;
    internal readonly PointD Column, Row;
    private readonly int canvasWidth, canvasHeight;
    private readonly long otherPixels;
    private readonly double halfX, halfY;

    /// <summary>Whether the working plane can be the source itself: the grid is the source
    /// and its placement covers the canvas untouched, so no new area exists to materialize.</summary>
    internal bool CanPaintDirect => SourceBounds == new RectI(0, 0, Width, Height)
        && Original?.Width == Width && Original?.Height == Height && OriginalPlacement.Origin == PointD.Zero
        && OriginalPlacement.Rotation % 360 == 0 && OriginalPlacement.Size.Width == canvasWidth && OriginalPlacement.Size.Height == canvasHeight;

    private ImageStrokeGrid(Surface? original, BrushSettings brush, LayerTransform placed, int sw, int sh,
        int left, int top, int width, int height, int canvasWidth, int canvasHeight, long otherPixels)
    {
        Original = original; Brush = brush; OriginalPlacement = placed;
        SourceBounds = new(-left, -top, sw, sh); Width = width; Height = height;
        this.canvasWidth = canvasWidth; this.canvasHeight = canvasHeight; this.otherPixels = otherPixels;
        Placement = TransformFor(new(0, 0, width, height));
        double c = Math.Cos(placed.Radians), s = Math.Sin(placed.Radians);
        double sx = placed.Size.Width / sw * (placed.FlipX ? -1 : 1), sy = placed.Size.Height / sh * (placed.FlipY ? -1 : 1);
        Column = new(sx * c, sx * s); Row = new(-sy * s, sy * c);
        halfX = (Math.Abs(Column.X) + Math.Abs(Row.X)) / 2; halfY = (Math.Abs(Column.Y) + Math.Abs(Row.Y)) / 2;
    }

    internal static ImageStrokeGrid? Create(ProjectSnapshot source, ProjectLayerRecord layer, Surface? original, BrushSettings brush)
    {
        var placed = layer.Transform;
        if (!placed.IsValid || !double.IsFinite(brush.Radius) || brush.Radius is <= 0 or > 1050
            || !double.IsFinite(brush.Hardness) || !double.IsFinite(brush.Opacity) || brush.Opacity <= 0) return null;
        // A layer without pixels yet starts from its placement's size, as BrushStroke does
        // when `layer.asset` is nil; painting then trims to the touched tiles alone.
        int sw = original?.Width ?? (int)Math.Round(placed.Size.Width), sh = original?.Height ?? (int)Math.Round(placed.Size.Height);
        if (sw is < 1 or > Limits.MaxSide || sh is < 1 or > Limits.MaxSide) return null;
        var corners = new[] { new PointD(0, 0), new(source.Manifest.Width, 0), new(0, source.Manifest.Height), new(source.Manifest.Width, source.Manifest.Height) }
            .Select(point => BrushEngine.DocumentToPixel(placed, sw, sh, point)).ToArray();
        double left = Math.Floor(Math.Min(0, corners.Min(point => point.X)) + 1e-8);
        double top = Math.Floor(Math.Min(0, corners.Min(point => point.Y)) + 1e-8);
        double width = Math.Ceiling(Math.Max(sw, corners.Max(point => point.X)) - 1e-8) - left;
        double height = Math.Ceiling(Math.Max(sh, corners.Max(point => point.Y)) - 1e-8) - top;
        if (width is < 1 or > 1_000_000_000 || height is < 1 or > 1_000_000_000) return null;
        var copy = new LayerTransform { Origin = placed.Origin, Size = placed.Size, Rotation = placed.Rotation,
            FlipX = placed.FlipX, FlipY = placed.FlipY, Sampling = placed.Sampling };
        // As makeRasterEdit on the Mac: the stroke's plane fits what the document budget has
        // left once every other layer's pixels are counted; an attached mask grows with the
        // image, so its room counts against the same plane.
        long others = source.Images.Sum(pair => pair.Key == layer.Id ? 0 : pair.Value.PixelCount)
            + source.Masks.Sum(pair => pair.Key == layer.Id && layer.MaskPlacement is null ? 0 : pair.Value.PixelCount);
        var grid = new ImageStrokeGrid(original, brush, copy, sw, sh, (int)left, (int)top,
            (int)width, (int)height, source.Manifest.Width, source.Manifest.Height, others);
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

    // The plane is image bytes (plus a re-edged attached mask of the same shape): its area,
    // its sides, and the placement carrying it must fit the document budget before pixels
    // exist. This is allocateTile's gate applied to the union the stroke would materialize.
    internal bool CanMaterialize(RectI bounds) => bounds.Width is > 0 and <= Limits.MaxSide && bounds.Height is > 0 and <= Limits.MaxSide
        && (long)bounds.Width * bounds.Height <= Limits.MaxSurfacePixels
        && otherPixels + (long)bounds.Width * bounds.Height <= Limits.DocumentPixelBudget
        && TransformFor(bounds).IsValid;

    // Preflight the segment before TileStroke allocates density tiles or changes pixels.
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

    internal PointD BaseAt(int x, int y)
    {
        if (Original is null) return new(-1, -1); // a blank layer: transparent everywhere
        int sx = x - SourceBounds.X, sy = y - SourceBounds.Y;
        if ((uint)sx < (uint)SourceBounds.Width && (uint)sy < (uint)SourceBounds.Height)
        {
            if (Original.Width == SourceBounds.Width && Original.Height == SourceBounds.Height) return new(sx, sy);
            return new(Math.Clamp((int)((sx + .5) * Original.Width / SourceBounds.Width), 0, Original.Width - 1),
                Math.Clamp((int)((sy + .5) * Original.Height / SourceBounds.Height), 0, Original.Height - 1));
        }
        return new(-1, -1); // outside the source: transparent
    }

    /// <summary>A fresh working plane over `bounds`: transparent past the source, the source's
    /// own bytes (nearest-sampled when the grid and the source disagree) inside it, plus any
    /// earlier plane's painted pixels copied at their grid place.</summary>
    internal Surface Materialize(RectI bounds, Surface? previous = null, RectI previousBounds = default)
    {
        if (previous is null && Original is { } source && bounds == SourceBounds && source.Width == bounds.Width && source.Height == bounds.Height) return source.CloneForEditing();
        var output = new Surface(bounds.Width, bounds.Height);
        if (Original is { } body)
        {
            // The plane always holds the source's rectangle: copy its rows whole when the grid
            // and the source agree, nearest-sample the source in when the canvas demanded more
            // grid pixels than the asset had.
            bool direct = body.Width == SourceBounds.Width && body.Height == SourceBounds.Height;
            for (int y = SourceBounds.Y; y < SourceBounds.Y + SourceBounds.Height; y++)
            {
                int at = (y - bounds.Y) * output.Stride + (SourceBounds.X - bounds.X) * 4;
                if (direct) body.Pixels.AsSpan((y - SourceBounds.Y) * body.Stride, SourceBounds.Width * 4).CopyTo(output.Pixels.AsSpan(at, SourceBounds.Width * 4));
                else for (int x = 0; x < SourceBounds.Width; x++)
                {
                    var src = BaseAt(SourceBounds.X + x, y);
                    Array.Copy(body.Pixels, (int)src.Y * body.Stride + (int)src.X * 4, output.Pixels, at + x * 4, 4);
                }
            }
        }
        if (previous is not null)
            for (int y = 0; y < previous.Height; y++) previous.Pixels.AsSpan(y * previous.Stride, previous.Width * 4)
                .CopyTo(output.Pixels.AsSpan((y + previousBounds.Y - bounds.Y) * output.Stride + (previousBounds.X - bounds.X) * 4, previous.Width * 4));
        output.MarkModified();
        return output;
    }

    /// <summary>The commit's crop: the source ∪ every painted pixel's rectangle, the same
    /// union brush_alpha_bounds builds per tile on the Mac. Painting inside the source's own
    /// frame can never widen it, so only the bands outside the frame are scanned: a
    /// canvas-sized layer commits without walking the whole plane.</summary>
    internal RectI CommittedBounds(RectI current, Surface working)
    {
        // A layer that has no pixels yet has no body to keep: the commit trims to painted
        // alpha alone, exactly the Mac's `bounds ?? committedBounds` with a nil source.
        bool body = Original is not null;
        int left = body ? SourceBounds.X : int.MaxValue, top = body ? SourceBounds.Y : int.MaxValue;
        int right = body ? SourceBounds.X + SourceBounds.Width : int.MinValue, bottom = body ? SourceBounds.Y + SourceBounds.Height : int.MinValue;
        int x0 = Math.Max(current.X, 0), y0 = Math.Max(current.Y, 0);
        int x1 = Math.Min(current.X + current.Width, Width), y1 = Math.Min(current.Y + current.Height, Height);
        if (!body) Extend(x0, x1, y0, y1);
        else
        {
            Extend(x0, SourceBounds.X, y0, y1);                                    // left of the frame
            Extend(SourceBounds.X + SourceBounds.Width, x1, y0, y1);               // right
            Extend(SourceBounds.X, SourceBounds.X + SourceBounds.Width, y0, SourceBounds.Y);     // above
            Extend(SourceBounds.X, SourceBounds.X + SourceBounds.Width, SourceBounds.Y + SourceBounds.Height, y1); // below
        }
        if (right <= left || bottom <= top) return default;
        return new(left, top, right - left, bottom - top);

        void Extend(int fromX, int toX, int fromY, int toY)
        {
            fromX = Math.Max(fromX, x0); toX = Math.Min(toX, x1);
            fromY = Math.Max(fromY, y0); toY = Math.Min(toY, y1);
            if (toX <= fromX || toY <= fromY) return;
            for (int y = fromY; y < toY; y++)
            {
                int row = (y - current.Y) * working.Stride;
                for (int x = fromX; x < toX; x++)
                {
                    if (working.Pixels[row + (x - current.X) * 4 + 3] == 0) continue;
                    if (x < left) left = x;
                    if (y < top) top = y;
                    if (x + 1 > right) right = x + 1;
                    if (y + 1 > bottom) bottom = y + 1;
                }
            }
        }
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
