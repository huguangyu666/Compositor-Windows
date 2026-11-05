namespace Compositor.Core;

public sealed partial class TileStroke
{
    /// <summary>Blur over a growth grid: the blurred sample window comes from the frozen
    /// source in source pixels (its radius stays tied to the original grid), the base
    /// reads through the grid — transparent past the source — and coverage blends the same
    /// source-over way PaintBlurRegion does. Outside the window the sample holds nothing:
    /// the transparent-edge rule keeps the grown area feathering instead of smearing.</summary>
    internal void PaintBlurGrid(Surface working, ImageStrokeGrid grid, SourceWindow sample, RectI sampleRegion, RectI bounds, RectI region, Selection? selection)
    {
        float cap = (float)Math.Clamp(brush.Opacity, 0, 1);
        int left = Math.Max(bounds.X, region.X), top = Math.Max(bounds.Y, region.Y);
        int right = Math.Min(bounds.X + bounds.Width, region.X + region.Width), bottom = Math.Min(bounds.Y + bounds.Height, region.Y + region.Height);
        if (left >= right || top >= bottom) return;
        var origin = grid.DocumentPoint(left + .5, top + .5);
        var body = grid.Original;
        // Source pixels sit at their grid rectangle: the same point in either space, one
        // window whose byte address covers every row the stamp can reach.
        for (int y = top; y < bottom; y++)
        {
            double docX = origin.X, docY = origin.Y;
            int at = (y - bounds.Y) * working.Stride + (left - bounds.X) * 4;
            for (int x = left; x < right; x++, at += 4)
            {
                float density = DensityAt(x, y, y / TileSize, y % TileSize);
                float coverage = density <= 0 ? 0 : (1 - MathF.Exp(-density)) * cap;
                if (selection is not null) coverage *= selection.CoverageAt((int)docX, (int)docY) / 255f;
                var baseAt = grid.BaseAt(x, y);
                float sourceR = 0, sourceG = 0, sourceB = 0, sourceA = 0;
                if (baseAt.X >= 0 && body is not null)
                {
                    int from = (int)baseAt.Y * body.Stride + (int)baseAt.X * 4;
                    sourceR = body.Pixels[from]; sourceG = body.Pixels[from + 1];
                    sourceB = body.Pixels[from + 2]; sourceA = body.Pixels[from + 3];
                }
                float blurredR = 0, blurredG = 0, blurredB = 0, blurredA = 0;
                int sx = x - grid.SourceBounds.X, sy = y - grid.SourceBounds.Y;
                // The window holds exactly the requested stamp rect (radius margin included);
                // pixels past it read transparent, the same edge rule the source carries.
                if (sx >= sampleRegion.X && sy >= sampleRegion.Y && sx < sampleRegion.X + sampleRegion.Width && sy < sampleRegion.Y + sampleRegion.Height)
                {
                    int index = sy * sample.Stride + sx * 4 + sample.Offset;
                    blurredR = sample.Pixels[index]; blurredG = sample.Pixels[index + 1];
                    blurredB = sample.Pixels[index + 2]; blurredA = sample.Pixels[index + 3];
                }
                float keep = 1 - coverage * blurredA / 255f;
                working.Pixels[at] = (byte)Math.Clamp(MathF.Round(sourceR * keep + blurredR * coverage), 0, 255);
                working.Pixels[at + 1] = (byte)Math.Clamp(MathF.Round(sourceG * keep + blurredG * coverage), 0, 255);
                working.Pixels[at + 2] = (byte)Math.Clamp(MathF.Round(sourceB * keep + blurredB * coverage), 0, 255);
                working.Pixels[at + 3] = (byte)Math.Clamp(MathF.Round(sourceA * keep + blurredA * coverage), 0, 255);
                docX += grid.Column.X; docY += grid.Column.Y;
            }
            origin = new(origin.X + grid.Row.X, origin.Y + grid.Row.Y);
        }
        working.MarkModified(new(left - bounds.X, top - bounds.Y, right - left, bottom - top));
    }
}
