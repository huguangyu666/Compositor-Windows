namespace Compositor.Core;

/// <summary>
/// The grown grid a spreading filter works on (Filters.swift:393-418): the layer's pixels padded out
/// by the blur's own reach so the picture dissolves into transparency instead of clamping at its own
/// edge, placed by the transform that keeps the same document rectangle — the grown grid's center in
/// the original's pixel coordinates is the original's own center, so growing widens the placement
/// around the same middle and never moves the layer. Only ever grows (Filters.swift:393-399): easing
/// the amount back off keeps the room the biggest setting asked for rather than rebuilding.
/// On OK the result trims back to what has coverage (Filters.swift:153-171, only for the spreading
/// kinds), which the same centering rule puts back on the canvas.
/// </summary>
public static class GrownLayerGrid
{
    /// <summary>The layer padded by <paramref name="margin"/> on every side, with the placement that
    /// keeps its document middle. The margin rounds up, as the Mac's growForBlur does.</summary>
    public static (Surface Image, LayerTransform Placement) Grow(Surface source, LayerTransform placement, int margin)
    {
        if (margin <= 0) return (source, placement);
        int width = source.Width + 2 * margin, height = source.Height + 2 * margin;
        if (width > Limits.MaxSide || height > Limits.MaxSide || (long)width * height > Limits.MaxSurfacePixels)
            throw new InvalidOperationException("The grown filter grid exceeds the raster limits.");
        var grown = new Surface(width, height);
        // Empty context, the layer drawn inside it (the Mac's grow: the padding starts clear, so the
        // blur has transparent pixels to spread into, not garbage).
        for (int y = 0; y < source.Height; y++)
            source.Pixels.AsSpan(y * source.Stride, source.Width * 4)
                .CopyTo(grown.Pixels.AsSpan((y + margin) * grown.Stride + margin * 4, source.Width * 4));
        return (grown, Placed(placement, source.Width, source.Height, -margin, -margin, width, height));
    }

    /// <summary>The blurred grown grid cut back to what has coverage, placed where the cropped piece
    /// sits on the canvas (the Mac's <c>trimmed</c>, Filters.swift:153-171). A grid with nothing to cut
    /// — or nothing left — comes back untouched: an empty result must not collapse the layer to a
    /// pixel, it is committed as the full grid with its placement.</summary>
    public static (Surface Image, LayerTransform Placement) Trim(Surface grown, LayerTransform placement)
    {
        if (CoverageBounds(grown) is not { } crop || crop == new RectI(0, 0, grown.Width, grown.Height)) return (grown, placement);
        var cropped = grown.Crop(crop.X, crop.Y, crop.Width, crop.Height);
        return (cropped, Placed(placement, grown.Width, grown.Height, crop.X, crop.Y, crop.Width, crop.Height));
    }

    internal static RectI? CoverageBounds(Surface grown, MaskSurface? mask = null)
    {
        bool uniform = mask is { Width: 1, Height: 1 };
        if (mask is not null && !uniform && (mask.Width != grown.Width || mask.Height != grown.Height))
            throw new ArgumentException("An attached mask must match the image grid.", nameof(mask));
        int left = grown.Width, top = grown.Height, right = 0, bottom = 0;
        for (int y = 0; y < grown.Height; y++)
        {
            int row = y * grown.Stride, start = 0, end = grown.Width;
            bool Transparent(int x) => grown.Pixels[row + x * 4 + 3] == 0
                || mask is not null && mask.Pixels[uniform ? 0 : y * mask.Width + x] == 0;
            while (start < end && Transparent(start)) start++;
            if (start == end) continue;
            while (end > start && Transparent(end - 1)) end--;
            left = Math.Min(left, start); right = Math.Max(right, end);
            top = Math.Min(top, y); bottom = Math.Max(bottom, y + 1);
        }
        return right <= left || bottom <= top ? null : new RectI(left, top, right - left, bottom - top);
    }

    /// <summary>The placement of the piece <c>(x, y, width, height)</c> of a grid of
    /// <c>(gridWidth, gridHeight)</c> — the same centering rule whether growing (negative origin) or
    /// trimming (positive): the piece's own document middle stays put and the size scales with the
    /// pixel ratio. Mirrors the Mac's expand/crop math and the port's own floating-merge Placement.</summary>
    internal static LayerTransform Placed(LayerTransform source, int gridWidth, int gridHeight,
        int x, int y, int width, int height)
    {
        var center = BrushEngine.PixelToDocument(source, gridWidth, gridHeight,
            new PointD(x + width / 2d, y + height / 2d));
        var size = new SizeD((double)width * source.Size.Width / gridWidth, (double)height * source.Size.Height / gridHeight);
        var result = new LayerTransform
        {
            Origin = new PointD(center.X - size.Width / 2, center.Y - size.Height / 2),
            Size = size,
            Rotation = source.Rotation, FlipX = source.FlipX, FlipY = source.FlipY, Sampling = source.Sampling,
        };
        if (!result.IsValid) throw new InvalidOperationException("The grown filter placement is invalid.");
        return result;
    }
}
