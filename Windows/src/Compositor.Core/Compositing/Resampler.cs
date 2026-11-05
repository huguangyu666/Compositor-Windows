using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Compositor.Core;

/// <summary>
/// Draws a source surface through a layer transform into a document-space rectangle, by
/// inverse mapping each destination pixel into the source. Samples with the layer's quality:
/// nearest, bilinear, or Catmull-Rom bicubic (high quality — exact wherever the transform
/// lands on whole source pixels). Pixels outside the source read as transparent; a half-pixel
/// soft edge falls where the scaled image ends, as CoreGraphics draws one.
///
/// Sampling convention: the continuous coordinate s runs over [0, W], where pixel i covers
/// [i, i+1) and its center sits at i + 0.5. Every sampler works center-indexed (u = s − 0.5),
/// so a sample that lands on a pixel center reproduces that pixel exactly, which keeps
/// identity transforms and whole-degree rotations pixel-perfect.
///
/// Three paths, by transform shape:
/// - axis-aligned (no rotation): the source coordinate s = A + B·(x + 0.5) is linear in the
///   destination column (and row), so the filter taps and weights are precomputed once per
///   column and row — the per-pixel loop is multiply-accumulate only. An exact 1:1 placement
///   degrades to row copies.
/// - rotated: general inverse mapping with a 512-bucket CatmullRom weight table.
/// </summary>
public static class Resampler
{
    public static void Draw(Surface source, LayerTransform transform, Surface destination, int x0, int y0, int x1, int y1, LayerSampling? samplingOverride = null)
        => Draw(new SourceWindow(source), transform, destination, x0, y0, x1, y1, samplingOverride);

    internal static void Draw(SourceWindow source, LayerTransform transform, Surface destination, int x0, int y0, int x1, int y1, LayerSampling? samplingOverride = null)
    {
        var sampling = samplingOverride ?? transform.Sampling;
        double sin = Math.Sin(transform.Radians);
        if (Math.Abs(sin) < 1e-9)
        {
            DrawAxisAligned(source, transform, sampling, destination, x0, y0, x1, y1);
        }
        else
        {
            DrawRotated(source, transform, sampling, destination, x0, y0, x1, y1);
        }
    }

    /// <summary>The transformed rectangle's integer bounds, clamped to the canvas.</summary>
    public static (int X0, int Y0, int X1, int Y1) BoundingRect(LayerTransform transform, int canvasWidth, int canvasHeight)
    {
        double radians = transform.Radians;
        double cos = Math.Cos(radians), sin = Math.Sin(radians);
        double halfWidth = transform.Size.Width / 2, halfHeight = transform.Size.Height / 2;
        var center = transform.Center;
        double[] cornersX =
        {
            halfWidth * cos - halfHeight * sin,
            halfWidth * cos + halfHeight * sin,
            -halfWidth * cos + halfHeight * sin,
            -halfWidth * cos - halfHeight * sin,
        };
        double[] cornersY =
        {
            halfWidth * sin + halfHeight * cos,
            halfWidth * sin - halfHeight * cos,
            -halfWidth * sin + halfHeight * cos,
            -halfWidth * sin - halfHeight * cos,
        };
        double minX = center.X + cornersX.Min(), maxX = center.X + cornersX.Max();
        double minY = center.Y + cornersY.Min(), maxY = center.Y + cornersY.Max();
        int x0 = Math.Max(0, (int)Math.Floor(minX));
        int y0 = Math.Max(0, (int)Math.Floor(minY));
        int x1 = Math.Min(canvasWidth, (int)Math.Ceiling(maxX));
        int y1 = Math.Min(canvasHeight, (int)Math.Ceiling(maxY));
        return (x0, y0, Math.Max(x0, x1), Math.Max(y0, y1));
    }

    /// <summary>All source taps needed by a destination rectangle, including the bicubic
    /// border. Keep full-plane coordinates even when the returned memory window is small.</summary>
    internal static RectI RequiredSourceRegion(int width, int height, LayerTransform transform, RectI destination, bool generalMapping = false)
    {
        double cos = Math.Cos(transform.Radians), sin = Math.Sin(transform.Radians);
        var center = transform.Center;
        bool axis = !generalMapping && Math.Abs(sin) < 1e-9;
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        foreach (double y in new[] { destination.Y + .5, destination.Y + destination.Height - .5 })
        foreach (double x in new[] { destination.X + .5, destination.X + destination.Width - .5 })
        {
            double dx = x - center.X, dy = y - center.Y;
            // Match the axis path, including its established 180-degree convention.
            double u = (dx * cos + (axis ? 0 : dy * sin)) / transform.Size.Width + .5;
            double v = (axis ? dy : -dx * sin + dy * cos) / transform.Size.Height + .5;
            double sx = (transform.FlipX ? 1 - u : u) * width;
            double sy = (transform.FlipY ? 1 - v : v) * height;
            minX = Math.Min(minX, sx); maxX = Math.Max(maxX, sx);
            minY = Math.Min(minY, sy); maxY = Math.Max(maxY, sy);
        }
        int x0 = Math.Clamp((int)Math.Floor(Math.Clamp(minX - .5, -2, width + 2)) - 1, 0, width - 1);
        int y0 = Math.Clamp((int)Math.Floor(Math.Clamp(minY - .5, -2, height + 2)) - 1, 0, height - 1);
        int x1 = Math.Clamp((int)Math.Floor(Math.Clamp(maxX - .5, -2, width + 2)) + 3, x0 + 1, width);
        int y1 = Math.Clamp((int)Math.Floor(Math.Clamp(maxY - .5, -2, height + 2)) + 3, y0 + 1, height);
        return new(x0, y0, x1 - x0, y1 - y0);
    }

    // --- Axis-aligned: per-column/per-row precomputed taps ---

    private static void DrawAxisAligned(SourceWindow source, LayerTransform transform, LayerSampling sampling, Surface destination, int x0, int y0, int x1, int y1)
    {
        double cos = Math.Cos(transform.Radians); // ±1 within floating noise
        var center = transform.Center;
        bool flipX = transform.FlipX, flipY = transform.FlipY;
        double sourceWidth = source.Width, sourceHeight = source.Height;

        // Source coordinate per destination pixel: s = flip ? Sw − u·Sw : u·Sw with
        // u = (x + 0.5 − cx)·cos/w + 0.5 — s = A + B·(x + 0.5), linear in x. The flip folds
        // into the sign of B.
        double stepX = (flipX ? -1 : 1) * cos * sourceWidth / transform.Size.Width;
        double startX = flipX
            ? sourceWidth / 2 + center.X * cos * sourceWidth / transform.Size.Width
            : sourceWidth / 2 - center.X * cos * sourceWidth / transform.Size.Width;
        double stepY = (flipY ? -1 : 1) * sourceHeight / transform.Size.Height;
        double startY = flipY
            ? sourceHeight / 2 + center.Y * sourceHeight / transform.Size.Height
            : sourceHeight / 2 - center.Y * sourceHeight / transform.Size.Height;

        // An exact 1:1 placement (the common fresh-import case) is a row copy.
        if (Math.Abs(stepX - 1) < 1e-9 && Math.Abs(stepY - 1) < 1e-9 && !flipX && !flipY
            && Math.Abs(startX - Math.Round(startX)) < 1e-9
            && Math.Abs(startY - Math.Round(startY)) < 1e-9)
        {
            BlitRows(source, destination, x0, y0, x1, y1, (int)Math.Round(startX), (int)Math.Round(startY));
            return;
        }

        switch (sampling)
        {
            case LayerSampling.Nearest:
                DrawNearestAxisAligned(source, stepX, startX, stepY, startY, destination, x0, y0, x1, y1);
                break;
            case LayerSampling.Smooth:
                DrawWeightedAxisAligned(source, stepX, startX, stepY, startY, destination, x0, y0, x1, y1, taps: 2);
                break;
            default:
                DrawWeightedAxisAligned(source, stepX, startX, stepY, startY, destination, x0, y0, x1, y1, taps: 4);
                break;
        }
    }

    /// <summary>An exact 1:1 axis-aligned placement is a row copy — the common case for
    /// freshly imported, untransformed layers.</summary>
    private static void BlitRows(SourceWindow source, Surface destination, int x0, int y0, int x1, int y1, int sourceXOffset, int sourceYOffset)
    {
        if (!ReferenceEquals(source.Pixels, destination.Pixels))
        {
            long clippedLeft = Math.Max((long)x0, -(long)sourceXOffset);
            long clippedRight = Math.Min((long)x1, (long)source.Width - sourceXOffset);
            long clippedTop = Math.Max((long)y0, -(long)sourceYOffset);
            long clippedBottom = Math.Min((long)y1, (long)source.Height - sourceYOffset);
            if (clippedLeft >= clippedRight || clippedTop >= clippedBottom) return;
            int left = (int)clippedLeft, right = (int)clippedRight;
            int top = (int)clippedTop, bottom = (int)clippedBottom;
            int bytes = (right - left) * 4;
            for (int y = top; y < bottom; y++)
            {
                int sourceIndex = (y + sourceYOffset) * source.Stride + source.Offset + (left + sourceXOffset) * 4;
                source.Pixels.AsSpan(sourceIndex, bytes).CopyTo(destination.Pixels.AsSpan(y * destination.Stride + left * 4, bytes));
            }
            return;
        }

        // Keep the established forward-copy order when source and destination alias.
        for (int y = y0; y < y1; y++)
        {
            // s = offset + (y + 0.5) with step 1: source row/column = target + offset.
            int sourceY = y + sourceYOffset;
            if ((uint)sourceY >= (uint)source.Height)
            {
                continue;
            }
            int targetRow = y * destination.Stride;
            int sourceRow = sourceY * source.Stride + source.Offset;
            for (int x = x0; x < x1; x++)
            {
                int sourceX = x + sourceXOffset;
                if ((uint)sourceX >= (uint)source.Width)
                {
                    continue;
                }
                int sourceIndex = sourceRow + sourceX * 4;
                int targetIndex = targetRow + x * 4;
                destination.Pixels[targetIndex] = source.Pixels[sourceIndex];
                destination.Pixels[targetIndex + 1] = source.Pixels[sourceIndex + 1];
                destination.Pixels[targetIndex + 2] = source.Pixels[sourceIndex + 2];
                destination.Pixels[targetIndex + 3] = source.Pixels[sourceIndex + 3];
            }
        }
    }

    private static void DrawNearestAxisAligned(
        SourceWindow source, double stepX, double startX, double stepY, double startY,
        Surface destination, int x0, int y0, int x1, int y1)
    {
        var columns = new int[x1 - x0];
        var columnValid = new bool[x1 - x0];
        for (int x = x0; x < x1; x++)
        {
            double s = startX + stepX * (x + 0.5);
            columnValid[x - x0] = s >= 0 && s < source.Width;
            columns[x - x0] = Math.Clamp((int)Math.Floor(s), 0, source.Width - 1);
        }
        for (int y = y0; y < y1; y++)
        {
            double s = startY + stepY * (y + 0.5);
            if (!(s >= 0 && s < source.Height))
            {
                continue; // transparent row
            }
            int row = (int)s * source.Stride + source.Offset;
            int targetRow = y * destination.Stride;
            for (int x = x0; x < x1; x++)
            {
                if (!columnValid[x - x0]) continue;
                int sourceIndex = row + columns[x - x0] * 4;
                int targetIndex = targetRow + x * 4;
                destination.Pixels[targetIndex] = source.Pixels[sourceIndex];
                destination.Pixels[targetIndex + 1] = source.Pixels[sourceIndex + 1];
                destination.Pixels[targetIndex + 2] = source.Pixels[sourceIndex + 2];
                destination.Pixels[targetIndex + 3] = source.Pixels[sourceIndex + 3];
            }
        }
    }

    /// <summary>Separable weighted resampling: the filter taps along x depend only on the
    /// destination column (and along y on the row), so each draw precomputes the clamped
    /// indices and weights once and the per-pixel loop is pure multiply-accumulate —
    /// mathematically identical to the direct 2D convolution it replaces. A pixel is
    /// transparent where its center falls outside the source's half-pixel border, matching
    /// the rotated path's Inside check.</summary>
    private static void DrawWeightedAxisAligned(
        SourceWindow source, double stepX, double startX, double stepY, double startY,
        Surface destination, int x0, int y0, int x1, int y1, int taps)
    {
        int width = source.Width, height = source.Height;
        var columnIndices = new int[(x1 - x0) * taps];
        var columnWeights = new float[(x1 - x0) * taps];
        var columnValid = new bool[x1 - x0];
        for (int x = x0; x < x1; x++)
        {
            double s = startX + stepX * (x + 0.5);
            columnValid[x - x0] = s >= -0.5 && s <= width - 0.5;
            WriteTaps(columnIndices, columnWeights, (x - x0) * taps, s - 0.5, width, taps);
        }
        var rowIndices = new int[(y1 - y0) * taps];
        var rowWeights = new float[(y1 - y0) * taps];
        var rowValid = new bool[y1 - y0];
        for (int y = y0; y < y1; y++)
        {
            double s = startY + stepY * (y + 0.5);
            rowValid[y - y0] = s >= -0.5 && s <= height - 0.5;
            WriteTaps(rowIndices, rowWeights, (y - y0) * taps, s - 0.5, height, taps);
        }

        if (Sse41.IsSupported)
        {
            // Aliased draws must retain their established forward read/write order.
            if (!ReferenceEquals(source.Pixels, destination.Pixels) && Math.Abs(stepY) < taps)
                DrawWeightedChannelRows(source, destination, x0, y0, x1, y1, taps,
                    columnIndices, columnWeights, columnValid, rowIndices, rowWeights, rowValid);
            else
                DrawWeightedChannels(source, destination, x0, y0, x1, y1, taps,
                    columnIndices, columnWeights, columnValid, rowIndices, rowWeights, rowValid);
            return;
        }

        Span<float> horizontal = stackalloc float[taps];
        for (int y = y0; y < y1; y++)
        {
            if (!rowValid[y - y0]) continue;
            int rowBase = (y - y0) * taps;
            int targetRow = y * destination.Stride;
            for (int x = x0; x < x1; x++)
            {
                if (!columnValid[x - x0]) continue;
                int columnBase = (x - x0) * taps;
                int targetIndex = targetRow + x * 4;
                for (int channel = 0; channel < 4; channel++)
                {
                    float vertical = 0f;
                    for (int j = 0; j < taps; j++)
                    {
                        int sourceRow = rowIndices[rowBase + j];
                        int sourceRowBase = sourceRow * source.Stride + source.Offset;
                        float horizontalSum = 0f;
                        for (int k = 0; k < taps; k++)
                        {
                            // Each tap goes through its own clamped index: at the edges the
                            // indices stop being contiguous.
                            horizontalSum += columnWeights[columnBase + k] * source.Pixels[sourceRowBase + columnIndices[columnBase + k] * 4 + channel];
                        }
                        horizontal[j] = horizontalSum;
                        vertical += rowWeights[rowBase + j] * horizontalSum;
                    }
                    destination.Pixels[targetIndex + channel] = (byte)Math.Clamp(MathF.Round(vertical), 0, 255);
                }
            }
        }
    }

    private static void DrawWeightedChannelRows(SourceWindow source, Surface destination, int x0, int y0, int x1, int y1, int taps,
        int[] columnIndices, float[] columnWeights, bool[] columnValid, int[] rowIndices, float[] rowWeights, bool[] rowValid)
    {
        int width = x1 - x0;
        var rows = ArrayPool<Vector128<float>>.Shared.Rent(checked(width * taps));
        Span<int> cachedRows = stackalloc int[taps]; cachedRows.Fill(-1);
        Span<int> offsets = stackalloc int[taps];
        try
        {
            for (int y = y0; y < y1; y++)
            {
                if (!rowValid[y - y0]) continue;
                int rowBase = (y - y0) * taps, targetRow = y * destination.Stride;
                for (int j = 0; j < taps; j++)
                {
                    int sourceRow = rowIndices[rowBase + j], slot = sourceRow % taps;
                    int offset = slot * width; offsets[j] = offset;
                    if (cachedRows[slot] == sourceRow) continue;
                    int sourceBase = sourceRow * source.Stride + source.Offset;
                    for (int column = 0; column < width; column++)
                    {
                        if (!columnValid[column]) continue;
                        int columnBase = column * taps; var horizontal = Vector128<float>.Zero;
                        for (int k = 0; k < taps; k++)
                        {
                            uint packed = BinaryPrimitives.ReadUInt32LittleEndian(source.Pixels.AsSpan(sourceBase + columnIndices[columnBase + k] * 4, 4));
                            var channels = Sse2.ConvertToVector128Single(Sse41.ConvertToVector128Int32(Vector128.CreateScalar(packed).AsByte()));
                            horizontal = Sse.Add(horizontal, Sse.Multiply(Vector128.Create(columnWeights[columnBase + k]), channels));
                        }
                        rows[offset + column] = horizontal;
                    }
                    // Distinct taps are consecutive source rows, so their modulo slots
                    // cannot collide within one output row. Clamped taps share a slot.
                    cachedRows[slot] = sourceRow;
                }
                for (int column = 0; column < width; column++)
                {
                    if (!columnValid[column]) continue;
                    var vertical = Vector128<float>.Zero;
                    for (int j = 0; j < taps; j++)
                        vertical = Sse.Add(vertical, Sse.Multiply(Vector128.Create(rowWeights[rowBase + j]), rows[offsets[j] + column]));
                    var rounded = Sse41.RoundToNearestInteger(vertical);
                    var clamped = Sse.Max(Vector128<float>.Zero, Sse.Min(Vector128.Create(255f), rounded));
                    var integers = Sse2.ConvertToVector128Int32WithTruncation(clamped);
                    var shorts = Sse2.PackSignedSaturate(integers, Vector128<int>.Zero);
                    uint pixel = Sse2.PackUnsignedSaturate(shorts, Vector128<short>.Zero).AsUInt32().ToScalar();
                    BinaryPrimitives.WriteUInt32LittleEndian(destination.Pixels.AsSpan(targetRow + (x0 + column) * 4, 4), pixel);
                }
            }
        }
        finally { ArrayPool<Vector128<float>>.Shared.Return(rows); }
    }

    private static void DrawWeightedChannels(SourceWindow source, Surface destination, int x0, int y0, int x1, int y1, int taps,
        int[] columnIndices, float[] columnWeights, bool[] columnValid, int[] rowIndices, float[] rowWeights, bool[] rowValid)
    {
        for (int y = y0; y < y1; y++)
        {
            if (!rowValid[y - y0]) continue;
            int rowBase = (y - y0) * taps, targetRow = y * destination.Stride;
            for (int x = x0; x < x1; x++)
            {
                if (!columnValid[x - x0]) continue;
                int columnBase = (x - x0) * taps;
                var vertical = Vector128<float>.Zero;
                for (int j = 0; j < taps; j++)
                {
                    int sourceRow = rowIndices[rowBase + j] * source.Stride + source.Offset;
                    var horizontal = Vector128<float>.Zero;
                    for (int k = 0; k < taps; k++)
                    {
                        int index = sourceRow + columnIndices[columnBase + k] * 4;
                        // Read exactly one RGBA pixel, including at a cropped buffer's end.
                        uint packed = BinaryPrimitives.ReadUInt32LittleEndian(source.Pixels.AsSpan(index, 4));
                        var channels = Sse2.ConvertToVector128Single(Sse41.ConvertToVector128Int32(Vector128.CreateScalar(packed).AsByte()));
                        horizontal = Sse.Add(horizontal, Sse.Multiply(Vector128.Create(columnWeights[columnBase + k]), channels));
                    }
                    // Independent lanes keep each channel's scalar tap order; do not fuse
                    // multiply/add or rearrange the horizontal and vertical rounding.
                    vertical = Sse.Add(vertical, Sse.Multiply(Vector128.Create(rowWeights[rowBase + j]), horizontal));
                }
                var rounded = Sse41.RoundToNearestInteger(vertical);
                var clamped = Sse.Max(Vector128<float>.Zero, Sse.Min(Vector128.Create(255f), rounded));
                var integers = Sse2.ConvertToVector128Int32WithTruncation(clamped);
                var shorts = Sse2.PackSignedSaturate(integers, Vector128<int>.Zero);
                uint pixel = Sse2.PackUnsignedSaturate(shorts, Vector128<short>.Zero).AsUInt32().ToScalar();
                BinaryPrimitives.WriteUInt32LittleEndian(destination.Pixels.AsSpan(targetRow + x * 4, 4), pixel);
            }
        }
    }

    /// <summary>Tap indices (clamped) and filter weights for one output coordinate; the
    /// weights match the direct 2D convolution's kernel exactly.</summary>
    private static void WriteTaps(int[] indices, float[] weights, int offset, double u, int limit, int taps)
    {
        if (taps == 2)
        {
            int i0 = (int)Math.Floor(u);
            float t = (float)(u - i0);
            indices[offset] = Math.Clamp(i0, 0, limit - 1);
            indices[offset + 1] = Math.Clamp(i0 + 1, 0, limit - 1);
            weights[offset] = 1f - t;
            weights[offset + 1] = t;
            return;
        }
        int center = (int)Math.Floor(u);
        float fraction = (float)(u - center);
        for (int k = -1; k <= 2; k++)
        {
            indices[offset + k + 1] = Math.Clamp(center + k, 0, limit - 1);
            weights[offset + k + 1] = CatmullRom(k - fraction);
        }
    }

    // --- Rotated: general inverse mapping with a weight table ---

    private static void DrawRotated(SourceWindow source, LayerTransform transform, LayerSampling sampling, Surface destination, int x0, int y0, int x1, int y1)
    {
        var center = transform.Center;
        double radians = transform.Radians;
        double cos = Math.Cos(radians), sin = Math.Sin(radians);
        double width = transform.Size.Width, height = transform.Size.Height;
        double sourceWidth = source.Width, sourceHeight = source.Height;
        double flipScaleX = transform.FlipX ? -sourceWidth : sourceWidth;
        double flipScaleY = transform.FlipY ? -sourceHeight : sourceHeight;
        double flipOffsetX = transform.FlipX ? sourceWidth : 0;
        double flipOffsetY = transform.FlipY ? sourceHeight : 0;
        double unitScaleX = 1 / width, unitScaleY = 1 / height;

        for (int y = y0; y < y1; y++)
        {
            double localY = y + 0.5 - center.Y;
            double preY = localY * sin;
            double preY2 = localY * cos;
            int row = y * destination.Stride;
            for (int x = x0; x < x1; x++)
            {
                double localX = x + 0.5 - center.X;
                double boxX = localX * cos + preY;
                double boxY = -localX * sin + preY2;
                double unitX = boxX * unitScaleX + 0.5;
                double unitY = boxY * unitScaleY + 0.5;
                if (unitX is < -0.5 or >= 1.5 || unitY is < -0.5 or >= 1.5)
                {
                    continue;
                }
                double pixelX = unitX * flipScaleX + flipOffsetX;
                double pixelY = unitY * flipScaleY + flipOffsetY;
                SampleTo(source, pixelX, pixelY, sampling, destination.Pixels, row + x * 4);
            }
        }
    }

    internal static void SampleTo(SourceWindow source, double pixelX, double pixelY, LayerSampling quality, byte[] destination, int destinationIndex)
    {
        switch (quality)
        {
            case LayerSampling.Nearest:
                SampleNearest(source, pixelX, pixelY, destination, destinationIndex);
                break;
            case LayerSampling.Smooth:
                SampleBilinear(source, pixelX, pixelY, destination, destinationIndex);
                break;
            default:
                SampleBicubic(source, pixelX, pixelY, destination, destinationIndex);
                break;
        }
    }

    private static bool Inside(double pixelX, double pixelY, double sourceWidth, double sourceHeight) =>
        pixelX >= -0.5 && pixelX <= sourceWidth - 0.5 && pixelY >= -0.5 && pixelY <= sourceHeight - 0.5;

    private static void SampleNearest(SourceWindow source, double pixelX, double pixelY, byte[] destination, int destinationIndex)
    {
        int x = (int)Math.Floor(pixelX);
        int y = (int)Math.Floor(pixelY);
        if ((uint)x >= (uint)source.Width || (uint)y >= (uint)source.Height) return; // transparent
        int sourceIndex = y * source.Stride + x * 4 + source.Offset;
        destination[destinationIndex] = source.Pixels[sourceIndex];
        destination[destinationIndex + 1] = source.Pixels[sourceIndex + 1];
        destination[destinationIndex + 2] = source.Pixels[sourceIndex + 2];
        destination[destinationIndex + 3] = source.Pixels[sourceIndex + 3];
    }

    private static void SampleBilinear(SourceWindow source, double pixelX, double pixelY, byte[] destination, int destinationIndex)
    {
        if (!Inside(pixelX, pixelY, source.Width, source.Height)) return;
        double ux = pixelX - 0.5, uy = pixelY - 0.5;
        int x0 = (int)Math.Floor(ux), y0 = (int)Math.Floor(uy);
        float tx = (float)(ux - x0), ty = (float)(uy - y0);
        int x1 = x0 + 1, y1 = y0 + 1;
        x0 = Math.Clamp(x0, 0, source.Width - 1);
        x1 = Math.Clamp(x1, 0, source.Width - 1);
        y0 = Math.Clamp(y0, 0, source.Height - 1);
        y1 = Math.Clamp(y1, 0, source.Height - 1);

        ReadOnlySpan<byte> p00 = source.Pixels.AsSpan(y0 * source.Stride + x0 * 4 + source.Offset, 4);
        ReadOnlySpan<byte> p10 = source.Pixels.AsSpan(y0 * source.Stride + x1 * 4 + source.Offset, 4);
        ReadOnlySpan<byte> p01 = source.Pixels.AsSpan(y1 * source.Stride + x0 * 4 + source.Offset, 4);
        ReadOnlySpan<byte> p11 = source.Pixels.AsSpan(y1 * source.Stride + x1 * 4 + source.Offset, 4);
        for (int channel = 0; channel < 4; channel++)
        {
            float top = p00[channel] + (p10[channel] - p00[channel]) * tx;
            float bottom = p01[channel] + (p11[channel] - p01[channel]) * tx;
            destination[destinationIndex + channel] = (byte)Math.Clamp(MathF.Round(top + (bottom - top) * ty), 0, 255);
        }
    }

    private static void SampleBicubic(SourceWindow source, double pixelX, double pixelY, byte[] destination, int destinationIndex)
    {
        if (!Inside(pixelX, pixelY, source.Width, source.Height)) return;
        double ux = pixelX - 0.5, uy = pixelY - 0.5;
        int centerX = (int)Math.Floor(ux), centerY = (int)Math.Floor(uy);
        float tx = (float)(ux - centerX), ty = (float)(uy - centerY);

        // Indices and weights are shared by all four channels: compute them once.
        int x0 = Math.Clamp(centerX - 1, 0, source.Width - 1);
        int x1 = Math.Clamp(centerX, 0, source.Width - 1);
        int x2 = Math.Clamp(centerX + 1, 0, source.Width - 1);
        int x3 = Math.Clamp(centerX + 2, 0, source.Width - 1);
        int y0 = Math.Clamp(centerY - 1, 0, source.Height - 1);
        int y1 = Math.Clamp(centerY, 0, source.Height - 1);
        int y2 = Math.Clamp(centerY + 1, 0, source.Height - 1);
        int y3 = Math.Clamp(centerY + 2, 0, source.Height - 1);
        float wx0 = CatmullRom(-1 - tx), wx1 = CatmullRom(-tx), wx2 = CatmullRom(1 - tx), wx3 = CatmullRom(2 - tx);
        float wy0 = CatmullRom(-1 - ty), wy1 = CatmullRom(-ty), wy2 = CatmullRom(1 - ty), wy3 = CatmullRom(2 - ty);

        int r0 = y0 * source.Stride + source.Offset, r1 = y1 * source.Stride + source.Offset,
            r2 = y2 * source.Stride + source.Offset, r3 = y3 * source.Stride + source.Offset;
        int c0 = x0 * 4, c1 = x1 * 4, c2 = x2 * 4, c3 = x3 * 4;
        for (int channel = 0; channel < 4; channel++)
        {
            float row0 = wx0 * source.Pixels[r0 + c0 + channel] + wx1 * source.Pixels[r0 + c1 + channel]
                + wx2 * source.Pixels[r0 + c2 + channel] + wx3 * source.Pixels[r0 + c3 + channel];
            float row1 = wx0 * source.Pixels[r1 + c0 + channel] + wx1 * source.Pixels[r1 + c1 + channel]
                + wx2 * source.Pixels[r1 + c2 + channel] + wx3 * source.Pixels[r1 + c3 + channel];
            float row2 = wx0 * source.Pixels[r2 + c0 + channel] + wx1 * source.Pixels[r2 + c1 + channel]
                + wx2 * source.Pixels[r2 + c2 + channel] + wx3 * source.Pixels[r2 + c3 + channel];
            float row3 = wx0 * source.Pixels[r3 + c0 + channel] + wx1 * source.Pixels[r3 + c1 + channel]
                + wx2 * source.Pixels[r3 + c2 + channel] + wx3 * source.Pixels[r3 + c3 + channel];
            float value = wy0 * row0 + wy1 * row1 + wy2 * row2 + wy3 * row3;
            destination[destinationIndex + channel] = (byte)Math.Clamp(MathF.Round(value), 0, 255);
        }
    }

    // CatmullRom weights depend only on the absolute fractional distance (the kernel is
    // even); the 512-bucket table replaces the polynomial evaluations in the rotated path.
    private static readonly float[] CatmullRomTable = BuildCatmullRomTable();

    private static float[] BuildCatmullRomTable()
    {
        var table = new float[512];
        for (int bucket = 0; bucket < 512; bucket++)
        {
            table[bucket] = CatmullRomWeight(bucket / 512f * 2f); // distances 0…2
        }
        return table;
    }

    private static float CatmullRomWeight(float absolute)
    {
        if (absolute <= 1f)
        {
            return 1.5f * absolute * absolute * absolute - 2.5f * absolute * absolute + 1f;
        }
        if (absolute < 2f)
        {
            return -0.5f * absolute * absolute * absolute + 2.5f * absolute * absolute - 4f * absolute + 2f;
        }
        return 0f;
    }

    private static float CatmullRom(float t)
    {
        float absolute = MathF.Abs(t);
        if (absolute >= 2f) return 0f;
        return CatmullRomTable[Math.Min(511, (int)(absolute * 256f))];
    }
}
