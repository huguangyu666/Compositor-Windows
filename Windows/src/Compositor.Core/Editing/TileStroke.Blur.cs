namespace Compositor.Core;

public sealed partial class TileStroke
{
    internal void PaintBlurRegion(Surface working, Surface original, SourceWindow sample, RectI region, Selection? selection)
    {
        float cap = (float)Math.Clamp(brush.Opacity, 0, 1);
        var origin = BrushEngine.PixelToDocument(transform, surfaceWidth, surfaceHeight, new(region.X + .5, region.Y + .5));
        var column = BrushEngine.PixelToDocument(transform, surfaceWidth, surfaceHeight, new(region.X + 1.5, region.Y + .5));
        var row = BrushEngine.PixelToDocument(transform, surfaceWidth, surfaceHeight, new(region.X + .5, region.Y + 1.5));
        var dx = new PointD(column.X - origin.X, column.Y - origin.Y); var dy = new PointD(row.X - origin.X, row.Y - origin.Y);
        for (int y = region.Y; y < region.Y + region.Height; y++)
        {
            double docX = origin.X, docY = origin.Y;
            for (int x = region.X; x < region.X + region.Width; x++)
            {
                float density = DensityAt(x, y, y / TileSize, y % TileSize);
                float coverage = density <= 0 ? 0 : (1 - MathF.Exp(-density)) * cap;
                if (selection is not null) coverage *= selection.CoverageAt((int)docX, (int)docY) / 255f;
                int at = y * working.Stride + x * 4, sourceAt = y * original.Stride + x * 4;
                int sampleAt = y * sample.Stride + x * 4 + sample.Offset;
                float keep = 1 - coverage * sample.Pixels[sampleAt + 3] / 255f;
                for (int channel = 0; channel < 4; channel++)
                    working.Pixels[at + channel] = (byte)Math.Clamp(MathF.Round(original.Pixels[sourceAt + channel] * keep
                        + sample.Pixels[sampleAt + channel] * coverage), 0, 255);
                docX += dx.X; docY += dx.Y;
            }
            origin = new(origin.X + dy.X, origin.Y + dy.Y);
        }
        working.MarkModified(region);
    }
}
