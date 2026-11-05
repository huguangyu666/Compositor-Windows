namespace Compositor.Core;

/// <summary>
/// The magic wand's pixel matcher, ported from WandPixels.c: the sample box around the
/// click is averaged per channel (halves rounded up), and a pixel matches when all four
/// (premultiplied) channels lie within the tolerance. Contiguous mode scanline-flood-fills
/// from the seed; otherwise every matching pixel selects. The port keeps the coverage mask
/// and skips the C path tracer — the port's Selection is a mask, not a path.
/// </summary>
public static class WandPixels
{
    /// <summary>Fills `mask` with 255 where pixels match; returns the selected count, or 0
    /// when the seed lies outside the source.</summary>
    public static long WandMask(Surface source, int seedX, int seedY, int radius, int tolerance, bool contiguous, MaskSurface mask,
        CancellationToken cancellation = default)
    {
        int width = source.Width, height = source.Height;
        if (width == 0 || height == 0) return 0;
        Array.Clear(mask.Pixels);
        if ((uint)seedX >= (uint)width || (uint)seedY >= (uint)height) return 0;
        int stride = source.Stride;

        int x0 = Math.Max(0, seedX - radius), x1 = Math.Min(width - 1, seedX + radius);
        int y0 = Math.Max(0, seedY - radius), y1 = Math.Min(height - 1, seedY + radius);
        long sums0 = 0, sums1 = 0, sums2 = 0, sums3 = 0;
        long samples = 0;
        for (int y = y0; y <= y1; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            int row = y * stride;
            for (int x = x0; x <= x1; x++, samples++)
            {
                int index = row + x * 4;
                sums0 += source.Pixels[index];
                sums1 += source.Pixels[index + 1];
                sums2 += source.Pixels[index + 2];
                sums3 += source.Pixels[index + 3];
            }
        }
        Span<int> reference = [(int)((sums0 + samples / 2) / samples), (int)((sums1 + samples / 2) / samples),
                               (int)((sums2 + samples / 2) / samples), (int)((sums3 + samples / 2) / samples)];

        long count = 0;
        if (!contiguous)
        {
            for (int y = 0; y < height; y++)
            {
                cancellation.ThrowIfCancellationRequested();
                int row = y * stride;
                int outRow = y * width;
                for (int x = 0; x < width; x++)
                {
                    if (Matches(source.Pixels, row + x * 4, reference, tolerance))
                    {
                        mask.Pixels[outRow + x] = 255;
                        count++;
                    }
                }
            }
            return count;
        }

        // Scanline flood fill: each popped seed fills its whole horizontal run, then pushes
        // one seed per matching run in the rows directly above and below it.
        var stack = new Stack<(int X, int Y)>();
        stack.Push((seedX, seedY));
        while (stack.Count > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            var (x, y) = stack.Pop();
            int row = y * stride;
            int outRow = y * width;
            if (mask.Pixels[outRow + x] != 0 || !Matches(source.Pixels, row + x * 4, reference, tolerance)) continue;
            int left = x, right = x;
            while (left > 0 && mask.Pixels[outRow + left - 1] == 0 && Matches(source.Pixels, row + (left - 1) * 4, reference, tolerance)) left--;
            while (right + 1 < width && mask.Pixels[outRow + right + 1] == 0 && Matches(source.Pixels, row + (right + 1) * 4, reference, tolerance)) right++;
            Array.Fill(mask.Pixels, (byte)255, outRow + left, right - left + 1);
            count += right - left + 1;
            for (int side = 0; side < 2; side++)
            {
                int ny = side == 0 ? y - 1 : y + 1;
                if (ny < 0 || ny >= height) continue;
                int nrow = ny * stride;
                int nout = ny * width;
                bool inRun = false;
                for (int nx = left; nx <= right; nx++)
                {
                    bool candidate = mask.Pixels[nout + nx] == 0 && Matches(source.Pixels, nrow + nx * 4, reference, tolerance);
                    if (candidate && !inRun) stack.Push((nx, ny));
                    inRun = candidate;
                }
            }
        }
        return count;
    }

    private static bool Matches(byte[] pixels, int index, Span<int> reference, int tolerance)
    {
        for (int c = 0; c < 4; c++)
        {
            int d = pixels[index + c] - reference[c];
            if (d < -tolerance || d > tolerance) return false;
        }
        return true;
    }

    /// <summary>
    /// Color Range (Select &gt; Color Range, WandPixels.c's color_range_mask): every pixel
    /// whose un-premultiplied color lies within `fuzziness` of any include color and none
    /// of the exclude colors. Include/exclude are flat RGB triplets. Transparent pixels
    /// only match when inverted — the empty color matches nothing else.
    /// </summary>
    public static long ColorRangeMask(Surface source, byte[] include, byte[] exclude, int fuzziness, bool invert, MaskSurface mask,
        CancellationToken cancellation = default)
    {
        int width = source.Width, height = source.Height;
        int stride = source.Stride;
        int includeCount = include.Length / 3, excludeCount = exclude.Length / 3;
        long count = 0;
        Span<int> rgb = stackalloc int[3];
        for (int y = 0; y < height; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            int row = y * stride;
            int outRow = y * width;
            for (int x = 0; x < width; x++)
            {
                int index = row + x * 4;
                int matches = 0;
                byte alpha = source.Pixels[index + 3];
                if (alpha > 0)
                {
                    for (int c = 0; c < 3; c++) rgb[c] = (source.Pixels[index + c] * 255 + alpha / 2) / alpha;
                    matches = Near(rgb, include, includeCount, fuzziness) && !Near(rgb, exclude, excludeCount, fuzziness) ? 1 : 0;
                }
                if (invert) matches = matches == 0 ? 1 : 0;
                mask.Pixels[outRow + x] = matches == 1 ? (byte)255 : (byte)0;
                count += matches;
            }
        }
        return count;
    }

    private static bool Near(Span<int> rgb, byte[] colors, int count, int fuzziness)
    {
        for (int i = 0; i < count; i++)
        {
            int offset = i * 3;
            if (Math.Abs(rgb[0] - colors[offset]) <= fuzziness
                && Math.Abs(rgb[1] - colors[offset + 1]) <= fuzziness
                && Math.Abs(rgb[2] - colors[offset + 2]) <= fuzziness) return true;
        }
        return false;
    }

    /// <summary>
    /// The color-range mask downscaled to fit the panel's preview box (max side kept,
    /// nearest neighbor — the mask is binary so interpolation adds nothing). The same
    /// parameters as ColorRangeSelect, so what the preview shows is what gets selected.
    /// </summary>
    public static MaskSurface ColorRangePreview(Surface sample, byte[] include, byte[] exclude, int fuzziness, bool invert, int maxWidth, int maxHeight)
    {
        int width = sample.Width, height = sample.Height;
        var mask = new MaskSurface(width, height);
        ColorRangeMask(sample, include, exclude, fuzziness, invert, mask);
        double scale = Math.Min(Math.Min((double)maxWidth / width, (double)maxHeight / height), 1.0);
        int previewWidth = Math.Max(1, (int)(width * scale));
        int previewHeight = Math.Max(1, (int)(height * scale));
        if (previewWidth == width && previewHeight == height) return mask;
        var preview = new MaskSurface(previewWidth, previewHeight);
        for (int y = 0; y < previewHeight; y++)
        {
            int sourceY = Math.Min(height - 1, (int)(y / scale));
            int outRow = y * previewWidth;
            int sourceRow = sourceY * width;
            for (int x = 0; x < previewWidth; x++)
            {
                preview.Pixels[outRow + x] = mask.Pixels[sourceRow + Math.Min(width - 1, (int)(x / scale))];
            }
        }
        return preview;
    }
}

/// <summary>The magic wand's options (EditorSession's WandSettings).</summary>
public sealed record WandSettings
{
    public int Tolerance { get; init; } = 32;
    public int SampleRadius { get; init; }
    public bool Contiguous { get; init; } = true;
    public bool SampleAllLayers { get; init; }
}
