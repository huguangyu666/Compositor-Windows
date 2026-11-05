namespace Compositor.Core;

public sealed partial class TileStroke
{
    internal void PaintMaskGrid(MaskSurface working, MaskStrokeGrid grid, RectI bounds, RectI region, Selection? selection, bool erase)
    {
        if (grid.CanPaintDirect && bounds == grid.SourceBounds)
        {
            PaintMaskRegion(working, grid.Original, region.X, region.Y, region.X + region.Width, region.Y + region.Height, selection, erase);
            return;
        }
        int left = Math.Max(bounds.X, region.X), top = Math.Max(bounds.Y, region.Y);
        int right = Math.Min(bounds.X + bounds.Width, region.X + region.Width), bottom = Math.Min(bounds.Y + bounds.Height, region.Y + region.Height);
        if (left >= right || top >= bottom) return;
        float cap = (float)Math.Clamp(brush.Opacity, 0, 1);
        byte value = erase ? (byte)0 : (byte)((brush.Red * 30 + brush.Green * 59 + brush.Blue * 11) / 100);
        var origin = grid.DocumentPoint(left + .5, top + .5);
        for (int y = top; y < bottom; y++)
        {
            double dx = origin.X, dy = origin.Y;
            for (int x = left; x < right; x++)
            {
                float density = DensityAt(x, y, y / TileSize, y % TileSize);
                float coverage = density <= 0 ? 0 : (1 - MathF.Exp(-density)) * cap * grid.CoverageAt(new(dx, dy), selection);
                byte original = grid.BaseAt(x, y);
                working.Pixels[(y - bounds.Y) * working.Width + x - bounds.X] = (byte)MathF.Round(original * (1 - coverage) + value * coverage);
                dx += grid.Column.X; dy += grid.Column.Y;
            }
            origin = new(origin.X + grid.Row.X, origin.Y + grid.Row.Y);
        }
        working.MarkModified(new(left - bounds.X, top - bounds.Y, right - left, bottom - top));
    }

    internal RectI TailBounds()
    {
        if (tail.Count == 0) return default;
        int left = int.MaxValue, top = int.MaxValue, right = 0, bottom = 0;
        foreach (var (x, y) in tail.Keys)
        {
            left = Math.Min(left, x * TileSize); top = Math.Min(top, y * TileSize);
            right = Math.Max(right, (x + 1) * TileSize); bottom = Math.Max(bottom, (y + 1) * TileSize);
        }
        return RectI.Normalize(left, top, right, bottom, surfaceWidth, surfaceHeight);
    }
}
