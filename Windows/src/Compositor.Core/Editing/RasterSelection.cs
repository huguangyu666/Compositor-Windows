namespace Compositor.Core;

public static class RasterSelection
{
    public static Selection? FromImage(int width, int height, Surface image, LayerTransform transform, bool antialiased = true) =>
        Build(width, height, image.Width, image.Height, image.Pixels, image.Stride, 4, 3, false, transform, antialiased);
    public static Selection? FromMask(int width, int height, MaskSurface mask, LayerTransform transform, bool antialiased = true) =>
        Build(width, height, mask.Width, mask.Height, mask.Pixels, mask.Width, 1, 0, true, transform, antialiased);

    // MaskTracing traces thresholded source pixel edges before transforming the outline.
    // Inverse sampling that union preserves holes without allocating a PathFigure per pixel.
    private static Selection? Build(int width, int height, int sourceWidth, int sourceHeight, byte[] pixels,
        int stride, int channels, int offset, bool dark, LayerTransform transform, bool antialiased)
    {
        bool Selected(byte value) => dark ? value < 128 : value >= 128;
        bool any = false;
        for (int y = 0; y < sourceHeight && !any; y++)
        for (int x = 0; x < sourceWidth; x++)
            if (Selected(pixels[y * stride + x * channels + offset])) { any = true; break; }
        if (!any) return null; // No traced source outline: keep the current selection.
        var mask = new MaskSurface(width, height);
        var corners = new[] { transform.Point(new(0, 0)), transform.Point(new(1, 0)), transform.Point(new(1, 1)), transform.Point(new(0, 1)) };
        int x0 = (int)Math.Clamp(Math.Floor(corners.Min(p => p.X)), 0, width), x1 = (int)Math.Clamp(Math.Ceiling(corners.Max(p => p.X)), 0, width);
        int y0 = (int)Math.Clamp(Math.Floor(corners.Min(p => p.Y)), 0, height), y1 = (int)Math.Clamp(Math.Ceiling(corners.Max(p => p.Y)), 0, height);
        var mapping = new RasterMapping(transform, sourceWidth, sourceHeight);
        int samples = antialiased ? 4 : 1;
        for (int y = y0; y < y1; y++)
        for (int x = x0; x < x1; x++)
        {
            int hits = 0;
            for (int sy = 0; sy < samples; sy++)
            for (int sx = 0; sx < samples; sx++)
            {
                var (u, v) = mapping.At(x + (sx + .5) / samples, y + (sy + .5) / samples);
                int px = (int)Math.Floor(u), py = (int)Math.Floor(v);
                if ((uint)px < (uint)sourceWidth && (uint)py < (uint)sourceHeight && Selected(pixels[py * stride + px * channels + offset])) hits++;
            }
            mask.Pixels[y * width + x] = (byte)((hits * 255 + samples * samples / 2) / (samples * samples));
        }
        var result = Selection.FromCoverage(mask);
        return result.Bounds.Width <= 0 || result.Bounds.Height <= 0 ? new(mask, new(0, 0, 0, 0)) : result;
    }
}
