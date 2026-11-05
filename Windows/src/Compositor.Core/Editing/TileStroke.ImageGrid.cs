namespace Compositor.Core;

public sealed partial class TileStroke
{
    /// <summary>An image stroke over a growth grid: the working plane's pixels outside the
    /// stroke are the base's (transparent past the source), inside it the brush color blends
    /// source-over, or erase scales the premultiplied pixel down — the same math as
    /// PaintRegion, reading the base through the grid instead of a same-shaped surface.</summary>
    internal void PaintImageGrid(Surface working, ImageStrokeGrid grid, RectI bounds, RectI region, Selection? selection, bool erase, bool keepAlpha = false)
    {
        if (!keepAlpha && grid.CanPaintDirect && bounds == grid.SourceBounds && grid.Original is { } source)
        {
            PaintRegion(working, source, region.X, region.Y, region.X + region.Width, region.Y + region.Height, selection, erase);
            return;
        }
        int left = Math.Max(bounds.X, region.X), top = Math.Max(bounds.Y, region.Y);
        int right = Math.Min(bounds.X + bounds.Width, region.X + region.Width), bottom = Math.Min(bounds.Y + bounds.Height, region.Y + region.Height);
        if (left >= right || top >= bottom) return;
        float cap = (float)Math.Clamp(brush.Opacity, 0, 1);
        float red = brush.Red, green = brush.Green, blue = brush.Blue;
        var body = grid.Original; // null for a blank layer: every base pixel reads transparent
        var origin = grid.DocumentPoint(left + .5, top + .5);
        for (int y = top; y < bottom; y++)
        {
            double dx = origin.X, dy = origin.Y;
            int at = (y - bounds.Y) * working.Stride + (left - bounds.X) * 4;
            for (int x = left; x < right; x++, at += 4)
            {
                float density = DensityAt(x, y, y / TileSize, y % TileSize);
                float coverage = density <= 0 ? 0 : (1 - MathF.Exp(-density)) * cap * grid.CoverageAt(new(dx, dy), selection);
                var baseAt = grid.BaseAt(x, y);
                float sourceR = 0, sourceG = 0, sourceB = 0, sourceA = 0;
                if (body is not null && baseAt.X >= 0)
                {
                    int from = (int)baseAt.Y * body.Stride + (int)baseAt.X * 4;
                    sourceR = body.Pixels[from]; sourceG = body.Pixels[from + 1];
                    sourceB = body.Pixels[from + 2]; sourceA = body.Pixels[from + 3];
                }
                if (coverage <= 0)
                {
                    // Outside the stroke the working pixels are the base's.
                    working.Pixels[at] = (byte)sourceR; working.Pixels[at + 1] = (byte)sourceG;
                    working.Pixels[at + 2] = (byte)sourceB; working.Pixels[at + 3] = (byte)sourceA;
                    dx += grid.Column.X; dy += grid.Column.Y;
                    continue;
                }
                if (erase)
                {
                    float keep = 1 - coverage;
                    working.Pixels[at] = (byte)MathF.Round(sourceR * keep);
                    working.Pixels[at + 1] = (byte)MathF.Round(sourceG * keep);
                    working.Pixels[at + 2] = (byte)MathF.Round(sourceB * keep);
                    working.Pixels[at + 3] = (byte)MathF.Round(sourceA * keep);
                }
                else
                {
                    float keep = 1 - coverage;
                    working.Pixels[at] = (byte)Math.Min(255, red * coverage * (keepAlpha ? sourceA / 255 : 1) + sourceR * keep);
                    working.Pixels[at + 1] = (byte)Math.Min(255, green * coverage * (keepAlpha ? sourceA / 255 : 1) + sourceG * keep);
                    working.Pixels[at + 2] = (byte)Math.Min(255, blue * coverage * (keepAlpha ? sourceA / 255 : 1) + sourceB * keep);
                    working.Pixels[at + 3] = (byte)(keepAlpha ? sourceA : Math.Min(255, MathF.Round(coverage * 255) + sourceA * keep));
                }
                dx += grid.Column.X; dy += grid.Column.Y;
            }
            origin = new(origin.X + grid.Row.X, origin.Y + grid.Row.Y);
        }
        working.MarkModified(new(left - bounds.X, top - bounds.Y, right - left, bottom - top));
    }
}
