namespace Compositor.Core;

public sealed partial class TileStroke
{
    internal void PaintCloneRegion(Surface working, Surface original, Surface sample, RectI region,
        Selection? selection, PointD offset, bool canvasSample)
    {
        float cap = (float)Math.Clamp(brush.Opacity, 0, 1);
        var origin = BrushEngine.PixelToDocument(transform, surfaceWidth, surfaceHeight, new(region.X + .5, region.Y + .5));
        var column = BrushEngine.PixelToDocument(transform, surfaceWidth, surfaceHeight, new(region.X + 1.5, region.Y + .5));
        var row = BrushEngine.PixelToDocument(transform, surfaceWidth, surfaceHeight, new(region.X + .5, region.Y + 1.5));
        var dx = new PointD(column.X - origin.X, column.Y - origin.Y); var dy = new PointD(row.X - origin.X, row.Y - origin.Y);
        var shifted = BrushEngine.DocumentToPixel(transform, surfaceWidth, surfaceHeight, new(origin.X + offset.X, origin.Y + offset.Y));
        double gridX = shifted.X - region.X - .5, gridY = shifted.Y - region.Y - .5;
        var window = new SourceWindow(sample); var pixel = new byte[4];
        for (int y = region.Y; y < region.Y + region.Height; y++)
        {
            double docX = origin.X, docY = origin.Y;
            for (int x = region.X; x < region.X + region.Width; x++)
            {
                float density = DensityAt(x, y, y / TileSize, y % TileSize);
                float coverage = density <= 0 ? 0 : (1 - MathF.Exp(-density)) * cap;
                if (selection is not null) coverage *= selection.CoverageAt((int)docX, (int)docY) / 255f;
                double sx = canvasSample ? docX + offset.X : x + .5 + gridX;
                double sy = canvasSample ? docY + offset.Y : y + .5 + gridY;
                int at = y * working.Stride + x * 4, sourceAt = y * original.Stride + x * 4;
                if (coverage <= 0 || sx < 0 || sy < 0 || sx >= sample.Width || sy >= sample.Height)
                    original.Pixels.AsSpan(sourceAt, 4).CopyTo(working.Pixels.AsSpan(at, 4));
                else
                {
                    Resampler.SampleTo(window, Math.Clamp(sx, .5, sample.Width - .5), Math.Clamp(sy, .5, sample.Height - .5), LayerSampling.Smooth, pixel, 0);
                    float keep = 1 - coverage * pixel[3] / 255f;
                    for (int channel = 0; channel < 4; channel++)
                        working.Pixels[at + channel] = (byte)Math.Clamp(MathF.Round(original.Pixels[sourceAt + channel] * keep + pixel[channel] * coverage), 0, 255);
                }
                docX += dx.X; docY += dx.Y;
            }
            origin = new(origin.X + dy.X, origin.Y + dy.Y);
        }
        working.MarkModified(region);
    }
}
