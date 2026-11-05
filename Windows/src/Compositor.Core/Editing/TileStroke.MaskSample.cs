namespace Compositor.Core;

public sealed partial class TileStroke
{
    internal void PaintMaskSample(MaskSurface working, MaskSurface original, MaskBlurWindow sample, RectI region, Selection? selection)
    {
        float cap = (float)Math.Clamp(brush.Opacity, 0, 1);
        var origin = BrushEngine.PixelToDocument(transform, surfaceWidth, surfaceHeight, new(region.X + .5, region.Y + .5));
        var column = BrushEngine.PixelToDocument(transform, surfaceWidth, surfaceHeight, new(region.X + 1.5, region.Y + .5));
        var row = BrushEngine.PixelToDocument(transform, surfaceWidth, surfaceHeight, new(region.X + .5, region.Y + 1.5));
        var dx = new PointD(column.X - origin.X, column.Y - origin.Y); var dy = new PointD(row.X - origin.X, row.Y - origin.Y);
        for (int y = region.Y; y < region.Y + region.Height; y++)
        {
            double px = origin.X, py = origin.Y;
            for (int x = region.X; x < region.X + region.Width; x++)
            {
                float density = DensityAt(x, y, y / TileSize, y % TileSize);
                float coverage = density <= 0 ? 0 : (1f - MathF.Exp(-density)) * cap;
                if (selection is not null) coverage *= selection.CoverageAt((int)px, (int)py) / 255f;
                int at = y * working.Width + x;
                byte value = sample.Pixels[(y - sample.Bounds.Y) * sample.Bounds.Width + x - sample.Bounds.X];
                working.Pixels[at] = (byte)MathF.Round(original.Pixels[at] * (1 - coverage) + value * coverage);
                px += dx.X; py += dx.Y;
            }
            origin = new(origin.X + dy.X, origin.Y + dy.Y);
        }
        working.MarkModified(region);
    }
}
