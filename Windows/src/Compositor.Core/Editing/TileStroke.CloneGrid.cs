namespace Compositor.Core;

public sealed partial class TileStroke
{
    /// <summary>Clone Stamp over a growth grid: the base pixel comes through the grid
    /// (transparent past the source), and the sample reads the frozen source — the layer's
    /// own pixels at their grid rectangle, or an all-layers composite in document space —
    /// so a stamp copies from offset points past the old asset's edge. The blending is
    /// PaintCloneRegion's exactly: out-of-sample or zero coverage keeps the base pixel.</summary>
    internal void PaintCloneGrid(Surface working, ImageStrokeGrid grid, Surface sample, RectI bounds, RectI region,
        Selection? selection, PointD offset, bool canvasSample)
    {
        if (grid.CanPaintDirect && bounds == grid.SourceBounds && !canvasSample && ReferenceEquals(sample, grid.Original))
        {
            PaintCloneRegion(working, grid.Original, sample, region, selection, offset, false);
            return;
        }
        int left = Math.Max(bounds.X, region.X), top = Math.Max(bounds.Y, region.Y);
        int right = Math.Min(bounds.X + bounds.Width, region.X + region.Width), bottom = Math.Min(bounds.Y + bounds.Height, region.Y + region.Height);
        if (left >= right || top >= bottom) return;
        float cap = (float)Math.Clamp(brush.Opacity, 0, 1);
        var origin = grid.DocumentPoint(left + .5, top + .5);
        var shifted = BrushEngine.DocumentToPixel(grid.Placement, grid.Width, grid.Height, new(origin.X + offset.X, origin.Y + offset.Y));
        // The document offset as a constant grid shift: the mapping is affine, so every row
        // and column sees the same distance between a stamp's target and its source.
        double gridX = shifted.X - left - .5, gridY = shifted.Y - top - .5;
        var pixel = new byte[4];
        var body = grid.Original;
        int sourceLeft = canvasSample ? 0 : grid.SourceBounds.X, sourceTop = canvasSample ? 0 : grid.SourceBounds.Y;
        int sourceWidth = canvasSample ? sample.Width : grid.SourceBounds.Width, sourceHeight = canvasSample ? sample.Height : grid.SourceBounds.Height;
        var window = new SourceWindow(sample);
        for (int y = top; y < bottom; y++)
        {
            double docX = origin.X, docY = origin.Y;
            int at = (y - bounds.Y) * working.Stride + (left - bounds.X) * 4;
            for (int x = left; x < right; x++, at += 4)
            {
                float density = DensityAt(x, y, y / TileSize, y % TileSize);
                float coverage = density <= 0 ? 0 : (1 - MathF.Exp(-density)) * cap;
                if (selection is not null) coverage *= selection.CoverageAt((int)docX, (int)docY) / 255f;
                double sx = canvasSample ? docX + offset.X : x + .5 + gridX; // grid- or document-space source
                double sy = canvasSample ? docY + offset.Y : y + .5 + gridY;
                double u = sx - sourceLeft, v = sy - sourceTop; // center-space inside the sample
                if (coverage <= 0 || u < 0 || v < 0 || u >= sourceWidth || v >= sourceHeight)
                {
                    var baseAt = grid.BaseAt(x, y);
                    if (baseAt.X >= 0 && body is not null) Array.Copy(body.Pixels, (int)baseAt.Y * body.Stride + (int)baseAt.X * 4, working.Pixels, at, 4);
                    else working.Pixels.AsSpan(at, 4).Clear();
                }
                else
                {
                    Resampler.SampleTo(window, Math.Clamp(u, .5, sample.Width - .5), Math.Clamp(v, .5, sample.Height - .5), LayerSampling.Smooth, pixel, 0);
                    var baseAt = grid.BaseAt(x, y);
                    float keep = 1 - coverage * pixel[3] / 255f;
                    for (int channel = 0; channel < 4; channel++)
                    {
                        float source = baseAt.X >= 0 && body is not null ? body.Pixels[(int)baseAt.Y * body.Stride + (int)baseAt.X * 4 + channel] : 0;
                        working.Pixels[at + channel] = (byte)Math.Clamp(MathF.Round(source * keep + pixel[channel] * coverage), 0, 255);
                    }
                }
                docX += grid.Column.X; docY += grid.Column.Y;
            }
            origin = new(origin.X + grid.Row.X, origin.Y + grid.Row.Y);
        }
        working.MarkModified(new(left - bounds.X, top - bounds.Y, right - left, bottom - top));
    }
}
