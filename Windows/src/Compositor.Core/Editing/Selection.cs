using System.Runtime.CompilerServices;

namespace Compositor.Core;

/// <summary>
/// A session-only selection: a document-sized coverage mask (255 = selected) plus a
/// persistent feather scalar. Selections constrain brush strokes and pixel edits; they
/// are not saved with the project, like the macOS app's. Rectangular and elliptical
/// marquees build them. The feather is kept apart from the mask, as the Mac's
/// DocumentSelection keeps it beside the path (Document/Selection.swift:11), and is
/// applied when coverage is sampled: a soft edge fades either side of the outline.
/// </summary>
public sealed partial class Selection
{
    public Selection(MaskSurface coverage, RectI bounds, double feather = 0)
    {
        sourceCoverage = coverage;
        documentWidth = coverage.Width; documentHeight = coverage.Height;
        MaskBounds = bounds;
        Feather = feather;
    }

    public MaskSurface Coverage => offsetX == 0 && offsetY == 0 && sourceCoverage.Width == documentWidth && sourceCoverage.Height == documentHeight
        ? sourceCoverage : ProjectedCoverage;

    /// <summary>How far the edge fades, in document pixels. 0 is a hard edge. Kept across
    /// the reshape operations and combined root-sum-of-squares by Feather, as on the Mac;
    /// new selections from the tools start at 0.</summary>
    public double Feather { get; }

    public bool IsEmpty => MaskBounds.Width <= 0 || MaskBounds.Height <= 0;

    /// <summary>The selected area's bounding rectangle in document pixels. A feathered
    /// edge reaches past the hard mask, so the bounds grow by the feather's visible
    /// falloff — four standard deviations, as the Mac's coverageBounds does
    /// (Document/Selection.swift:33-36).</summary>
    public RectI Bounds
    {
        get
        {
            if (IsEmpty) return new(0, 0, 0, 0);
            var outline = OutlineBounds;
            if (Feather <= 0) return ClipBounds(outline, documentWidth, documentHeight);
            int spread = (int)Math.Ceiling(Feather * 2);
            return ClipBounds(new(outline.X - spread, outline.Y - spread, outline.Width + spread * 2, outline.Height + spread * 2), documentWidth, documentHeight);
        }
    }

    /// <summary>The bounding rectangle of the hard mask, before the feather's falloff.</summary>
    private RectI MaskBounds { get; }

    /// <summary>
    /// The selection mirrored across the canvas's own middle, for Flip Canvas. The macOS original
    /// transforms the vector path (Document/LayerFlip.swift:69-76); the Windows selection is a
    /// coverage field, so the mirror is the same column/row reversal, and the feather — a distance,
    /// not a direction — carries over unchanged.
    /// </summary>
    public Selection Mirrored(bool horizontally, CancellationToken cancellation = default)
    {
        var source = Coverage;
        var pixels = new byte[documentWidth * documentHeight];
        for (int y = 0; y < documentHeight; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            int sourceY = horizontally ? y : documentHeight - 1 - y;
            if (sourceY >= source.Height) continue;
            int targetRow = y * documentWidth;
            int sourceRow = sourceY * source.Width;
            for (int x = 0; x < documentWidth; x++)
            {
                int sourceX = horizontally ? documentWidth - 1 - x : x;
                if (sourceX < source.Width) pixels[targetRow + x] = source.Pixels[sourceRow + sourceX];
            }
        }
        var bounds = MaskBounds;
        var mirroredBounds = horizontally
            ? new RectI(documentWidth - bounds.X - bounds.Width, bounds.Y, bounds.Width, bounds.Height)
            : new RectI(bounds.X, documentHeight - bounds.Y - bounds.Height, bounds.Width, bounds.Height);
        return new Selection(new MaskSurface(documentWidth, documentHeight, pixels), mirroredBounds, Feather);
    }

    /// <summary>
    /// The complement of this selection over the whole document: every unselected pixel
    /// becomes selected and vice versa. The bounds span the full document, as the inverse
    /// of a partial selection usually reaches every edge. The feather survives, as on the
    /// Mac's Inverse (Document/Selection.swift:358).
    /// </summary>
    public Selection Inverted(int documentWidth, int documentHeight, CancellationToken cancellation = default)
    {
        var coverage = new MaskSurface(documentWidth, documentHeight);
        for (int y = 0; y < documentHeight; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            int row = y * documentWidth;
            int sourceRow = y * Coverage.Width;
            for (int x = 0; x < documentWidth; x++)
            {
                byte selected = x < Coverage.Width && y < Coverage.Height
                    ? Coverage.Pixels[sourceRow + x]
                    : (byte)0;
                coverage.Pixels[row + x] = (byte)(255 - selected);
            }
        }
        return new Selection(coverage, new RectI(0, 0, documentWidth, documentHeight), Feather);
    }

    /// <summary>
    /// Grows the outline by `amount` pixels with a round edge, clipped to the document —
    /// Expand Selection (the Mac strokes the path with a round band of twice the amount).
    /// The feather carries over unchanged, as the Mac's resizeSelection does
    /// (Document/Selection.swift:341).
    /// </summary>
    public Selection Expanded(int amount, CancellationToken cancellation = default)
    {
        if (offsetX != 0 || offsetY != 0)
        {
            var padded = new MaskSurface(sourceCoverage.Width + amount * 2, sourceCoverage.Height + amount * 2);
            for (int y = 0; y < sourceCoverage.Height; y++) sourceCoverage.Pixels.AsSpan(y * sourceCoverage.Width, sourceCoverage.Width)
                .CopyTo(padded.Pixels.AsSpan((y + amount) * padded.Width + amount, sourceCoverage.Width));
            var expanded = FromCoverage(Dilated(padded, amount, includeEdges: false, cancellation));
            var translated = new Selection(expanded.Coverage, expanded.MaskBounds, Feather, offsetX - amount, offsetY - amount, documentWidth, documentHeight);
            var clipped = FromCoverage(translated.Coverage, cancellation);
            return new(clipped.Coverage, clipped.MaskBounds, Feather);
        }
        var grown = FromCoverage(Dilated(Coverage, amount, includeEdges: false, cancellation), cancellation);
        return new Selection(grown.Coverage, grown.Bounds, Feather);
    }

    /// <summary>
    /// Shrinks the outline by `amount` pixels, including away from the canvas edges.
    /// Contracting past the middle keeps an explicit empty selection, which clips every
    /// later pixel edit away. The feather carries over unchanged, as Expand's does.
    /// </summary>
    public Selection Contracted(int amount, CancellationToken cancellation = default)
    {
        // Erode = dilate the complement: a pixel survives only if the nearest unselected
        // pixel (or the canvas edge) lies farther away than the amount.
        var background = new MaskSurface(sourceCoverage.Width, sourceCoverage.Height);
        for (int i = 0; i < background.Pixels.Length; i++)
        {
            if ((i & 0xFFFF) == 0) cancellation.ThrowIfCancellationRequested();
            background.Pixels[i] = (byte)(255 - sourceCoverage.Pixels[i]);
        }
        var grown = Dilated(background, amount, includeEdges: true, cancellation);
        var coverage = new MaskSurface(grown.Width, grown.Height);
        for (int i = 0; i < coverage.Pixels.Length; i++)
        {
            if ((i & 0xFFFF) == 0) cancellation.ThrowIfCancellationRequested();
            coverage.Pixels[i] = (byte)(255 - grown.Pixels[i]);
        }
        var shrunk = FromCoverage(coverage, cancellation);
        return new Selection(shrunk.Coverage, shrunk.MaskBounds, Feather, offsetX, offsetY, documentWidth, documentHeight);
    }

    /// <summary>
    /// Softens the selection's edge by `amount` pixels, stacking with any feather already
    /// present — Select → Modify → Feather. The Mac keeps feather as a scalar on the
    /// outline rather than editing the geometry (Document/Selection.swift:325-331): two
    /// soft edges together spread a little less than their sum, as blurs do, and the
    /// combined fade caps at 250. The coverage mask is left untouched here; sampling
    /// (CoverageAt) applies the fade.
    /// </summary>
    public Selection Feathered(int amount)
    {
        double softened = Math.Min(250, Math.Sqrt(Feather * Feather + (double)amount * amount));
        return new Selection(sourceCoverage, MaskBounds, softened, offsetX, offsetY, documentWidth, documentHeight);
    }

    /// <summary>A selection over a finished coverage mask, bounds tightened to its nonzero
    /// pixels — the shared entry for the wand and the reshape operations.</summary>
    public static Selection FromCoverage(MaskSurface coverage, CancellationToken cancellation = default)
    {
        int minX = coverage.Width, minY = coverage.Height, maxX = 0, maxY = 0;
        for (int y = 0; y < coverage.Height; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            int row = y * coverage.Width;
            for (int x = 0; x < coverage.Width; x++)
            {
                if (coverage.Pixels[row + x] > 0)
                {
                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x + 1);
                    maxY = Math.Max(maxY, y + 1);
                }
            }
        }
        return new Selection(coverage, maxX <= minX || maxY <= minY
            ? new RectI(0, 0, 0, 0) : new RectI(minX, minY, maxX - minX, maxY - minY));
    }

    /// <summary>
    /// Sets every pixel within `amount` of the source, as a two-pass 3-4 chamfer distance
    /// transform: round edges at O(pixels) cost. With includeEdges the canvas border acts
    /// as source too, so contracting pulls away from the edges.
    /// </summary>
    private static MaskSurface Dilated(MaskSurface source, int amount, bool includeEdges, CancellationToken cancellation)
    {
        int width = source.Width, height = source.Height;
        const int unknown = int.MaxValue / 4;
        var distance = new int[width * height];
        for (int i = 0; i < distance.Length; i++)
        {
            // The two sweeps below are the expensive part of a large expand/contract, so the
            // cancel check rides the row boundary rather than every pixel.
            if ((i & 0xFFFF) == 0) cancellation.ThrowIfCancellationRequested();
            distance[i] = source.Pixels[i] > 0 ? 0 : unknown;
        }
        if (includeEdges)
        {
            for (int x = 0; x < width; x++)
            {
                distance[x] = 0;
                distance[(height - 1) * width + x] = 0;
            }
            for (int y = 0; y < height; y++)
            {
                distance[y * width] = 0;
                distance[y * width + width - 1] = 0;
            }
        }
        void Sweep(int fromY, int toY, int stepY, int fromX, int toX, int stepX)
        {
            for (int y = fromY; y != toY; y += stepY)
            {
                cancellation.ThrowIfCancellationRequested();
                int row = y * width;
                for (int x = fromX; x != toX; x += stepX)
                {
                    int d = distance[row + x];
                    if (x - stepX >= 0 && x - stepX < width) d = Math.Min(d, distance[row + x - stepX] + 3);
                    if (y - stepY >= 0 && y - stepY < height)
                    {
                        d = Math.Min(d, distance[row - stepY * width + x] + 3);
                        if (x - stepX >= 0 && x - stepX < width) d = Math.Min(d, distance[row - stepY * width + x - stepX] + 4);
                        if (x + stepX >= 0 && x + stepX < width) d = Math.Min(d, distance[row - stepY * width + x + stepX] + 4);
                    }
                    distance[row + x] = d;
                }
            }
        }
        Sweep(0, height, 1, 0, width, 1);
        Sweep(height - 1, -1, -1, width - 1, -1, -1);
        int threshold = amount * 3;
        var result = new MaskSurface(width, height);
        for (int i = 0; i < distance.Length; i++)
        {
            if ((i & 0xFFFF) == 0) cancellation.ThrowIfCancellationRequested();
            if (distance[i] <= threshold) result.Pixels[i] = 255;
        }
        return result;
    }

    /// <summary>Three separable box passes, with clipped edges and integer half-up rounding.</summary>
    // A first sample can process the full document before call-count tiering warms this kernel.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static MaskSurface FeatherCoverage(MaskSurface source, int amount)
    {
        int width = source.Width, height = source.Height;
        var result = new MaskSurface(width, height);
        int rowCount = (int)Math.Min(height, 2L * amount + 1);
        var rows = new byte[width * rowCount];
        var sums = new int[width];
        for (int pass = 0; pass < 3; pass++)
        {
            Array.Clear(sums);
            int firstHigh = Math.Min(height - 1, amount);
            for (int y = 0; y <= firstHigh; y++)
            {
                var row = rows.AsSpan((y % rowCount) * width, width);
                BlurRow(source.Pixels.AsSpan(y * width, width), row, amount);
                UpdateFeatherSums(sums, row, subtract: false);
            }
            for (int y = 0; y < height; y++)
            {
                int low = Math.Max(0, y - amount);
                int high = Math.Min(height - 1, y + amount);
                int count = high - low + 1;
                ulong reciprocal = ((1UL << 48) + (uint)count - 1) / (uint)count;
                var output = result.Pixels.AsSpan(y * width, width);
                WriteFeatherRow(sums, output, count, reciprocal);
                int nextLow = Math.Max(0, y + 1 - amount);
                int nextHigh = Math.Min(height - 1, y + 1 + amount);
                if (nextLow > low)
                {
                    var row = rows.AsSpan((low % rowCount) * width, width);
                    UpdateFeatherSums(sums, row, subtract: true);
                }
                if (nextHigh > high)
                {
                    // Retire the old row before reusing its slot. In passes two and three,
                    // future source rows are filtered before the in-place output reaches them.
                    var row = rows.AsSpan((nextHigh % rowCount) * width, width);
                    BlurRow(source.Pixels.AsSpan(nextHigh * width, width), row, amount);
                    UpdateFeatherSums(sums, row, subtract: false);
                }
            }
            source = result;
        }
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void BlurRow(ReadOnlySpan<byte> source, Span<byte> destination, int amount)
    {
        int width = source.Length, sum = 0;
        uint fullCount = (uint)Math.Min(width, 2L * amount + 1);
        ulong reciprocal = ((1UL << 48) + fullCount - 1) / fullCount;
        for (int x = 0; x <= amount && x < width; x++) sum += source[x];
        int first = Math.Max(0, Math.Min(amount, width - amount - 1));
        int last = Math.Min(width, Math.Max(amount + 1, width - amount));
        int xIndex = 0;
        for (; xIndex < first; xIndex++)
        {
            int count = xIndex + amount + 1;
            destination[xIndex] = (byte)((sum + count / 2) / count);
            sum += source[xIndex + amount + 1];
        }
        if (fullCount == width)
        {
            // A wide box covers the complete row throughout this middle interval.
            destination.Slice(first, last - first).Fill(DivideCoverage((uint)(sum + fullCount / 2), reciprocal));
            xIndex = last;
            if (last < width) sum -= source[0];
        }
        else
        {
            xIndex = BlurFeatherInterior(source, destination, amount, xIndex, last, fullCount, reciprocal, ref sum);
            for (; xIndex + 1 < last; xIndex++)
            {
                destination[xIndex] = DivideCoverage((uint)(sum + fullCount / 2), reciprocal);
                sum -= source[xIndex - amount];
                sum += source[xIndex + amount + 1];
            }
            destination[xIndex] = DivideCoverage((uint)(sum + fullCount / 2), reciprocal);
            sum -= source[xIndex - amount];
            xIndex++;
        }
        for (; xIndex < width; xIndex++)
        {
            int count = width - xIndex + amount;
            destination[xIndex] = (byte)((sum + count / 2) / count);
            sum -= source[xIndex - amount];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte DivideCoverage(uint value, ulong reciprocal)
    {
        // Counts are at most MaxSide (30000), and the numerator is at most
        // 255*count + count/2. At 48 bits the upward reciprocal error cannot
        // cross the next integer, so this remains exact integer division.
        return (byte)(value * reciprocal >> 48);
    }

    public byte CoverageAt(int documentX, int documentY)
    {
        if (IsEmpty || documentX < 0 || documentY < 0 || documentX >= documentWidth || documentY >= documentHeight) return 0;
        return Feather > 0 ? SoftenedCoverage.Pixels[documentY * documentWidth + documentX] : HardCoverageAt(documentX, documentY);
    }

    /// <summary>The coverage cache softened by the feather, built on first sample. Selections
    /// are immutable, so the race here is benign: every writer stores the same value.</summary>
    private MaskSurface? softenedCoverage;
#if DEBUG
    internal double DebugFeatherBuildMs;
    internal int DebugFeatherBuilds;
#endif

    /// <summary>
    /// The coverage with the feather's fade applied, as the Mac's coverage() renders it: the
    /// filled outline blurred by a Gaussian of sigma feather/2 (Document/Selection.swift:27).
    /// Three box-blur passes approximate that Gaussian at O(1) per pixel whatever the radius;
    /// a single pass's variance is r(r+1)/3, so three of radius r spread like a Gaussian with
    /// sigma = sqrt(r(r+1)), which is what the radius below solves for.
    /// </summary>
    private MaskSurface SoftenedCoverage
    {
        get
        {
            if (softenedCoverage is null)
            {
#if DEBUG
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
                double sigma = Feather / 2;
                int radius = Math.Max(1, (int)Math.Round((Math.Sqrt(1 + 4 * sigma * sigma) - 1) / 2, MidpointRounding.AwayFromZero));
                softenedCoverage = FeatherCoverage(Coverage, radius);
#if DEBUG
                DebugFeatherBuildMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                Interlocked.Increment(ref DebugFeatherBuilds);
#endif
            }
            return softenedCoverage;
        }
    }

    public static Selection Rectangle(int documentWidth, int documentHeight, int x, int y, int width, int height)
    {
        var coverage = new MaskSurface(documentWidth, documentHeight);
        int x0 = Math.Clamp(x, 0, documentWidth), y0 = Math.Clamp(y, 0, documentHeight);
        int x1 = Math.Clamp(x + width, 0, documentWidth), y1 = Math.Clamp(y + height, 0, documentHeight);
        for (int row = y0; row < y1; row++)
        {
            Array.Fill(coverage.Pixels, (byte)255, row * documentWidth + x0, Math.Max(0, x1 - x0));
        }
        return new Selection(coverage, new RectI(x0, y0, x1 - x0, y1 - y0));
    }

    public static Selection Ellipse(int documentWidth, int documentHeight, double centerX, double centerY, double radiusX, double radiusY, bool antialiased = false)
    {
        var coverage = new MaskSurface(documentWidth, documentHeight);
        if (radiusX <= 0 || radiusY <= 0 || !double.IsFinite(centerX) || !double.IsFinite(centerY)
            || !double.IsFinite(radiusX) || !double.IsFinite(radiusY)) return new(coverage, new(0, 0, 0, 0));
        if (antialiased) return AntialiasedEllipse(coverage, centerX, centerY, radiusX, radiusY);
        if (radiusX <= 0 || radiusY <= 0) return new Selection(coverage, new RectI(0, 0, 0, 0));
        double left = Math.Max(0, centerX - radiusX - 1), right = Math.Min(documentWidth, centerX + radiusX + 1);
        double top = Math.Max(0, centerY - radiusY - 1), bottom = Math.Min(documentHeight, centerY + radiusY + 1);
        int minX = documentWidth, minY = documentHeight, maxX = 0, maxY = 0;
        for (int row = (int)top; row < (int)bottom; row++)
        {
            for (int column = (int)left; column < (int)right; column++)
            {
                double dx = (column + 0.5 - centerX) / radiusX;
                double dy = (row + 0.5 - centerY) / radiusY;
                if (dx * dx + dy * dy <= 1)
                {
                    coverage.Pixels[row * documentWidth + column] = 255;
                    minX = Math.Min(minX, column);
                    minY = Math.Min(minY, row);
                    maxX = Math.Max(maxX, column + 1);
                    maxY = Math.Max(maxY, row + 1);
                }
            }
        }
        return new Selection(coverage, maxX <= minX || maxY <= minY ? new(0, 0, 0, 0) : new(minX, minY, maxX - minX, maxY - minY));
    }

    // Four subpixel scanlines integrate horizontal coverage; only the row endpoints need
    // fractional work. This keeps ellipse AA linear without sixteen inverse samples per pixel.
    private static Selection AntialiasedEllipse(MaskSurface coverage, double cx, double cy, double rx, double ry)
    {
        int x0 = (int)Math.Clamp(Math.Floor(cx - rx), 0, coverage.Width), x1 = (int)Math.Clamp(Math.Ceiling(cx + rx), 0, coverage.Width);
        int y0 = (int)Math.Clamp(Math.Floor(cy - ry), 0, coverage.Height), y1 = (int)Math.Clamp(Math.Ceiling(cy + ry), 0, coverage.Height);
        var row = new double[Math.Max(0, x1 - x0)];
        int minX = coverage.Width, minY = coverage.Height, maxX = 0, maxY = 0;
        double inverseY = 1 / ry;
        for (int y = y0; y < y1; y++)
        {
            row.AsSpan().Clear();
            for (int sy = 0; sy < 4; sy++)
            {
                double dy = (y + (sy + .5) / 4 - cy) * inverseY;
                if (Math.Abs(dy) >= 1) continue;
                double half = rx * Math.Sqrt(1 - dy * dy), left = Math.Clamp(cx - half, 0, coverage.Width), right = Math.Clamp(cx + half, 0, coverage.Width);
                if (left >= right) continue;
                int first = (int)Math.Floor(left), last = (int)Math.Ceiling(right) - 1;
                row[first - x0] += Math.Min(first + 1, right) - left;
                for (int x = first + 1; x < last; x++) row[x - x0] += 1;
                if (last > first) row[last - x0] += right - last;
            }
            for (int x = x0; x < x1; x++)
            {
                byte value = (byte)Math.Clamp(Math.Round(row[x - x0] * 255 / 4, MidpointRounding.AwayFromZero), 0, 255);
                if (value == 0) continue;
                coverage.Pixels[y * coverage.Width + x] = value;
                minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x + 1); maxY = Math.Max(maxY, y + 1);
            }
        }
        return new(coverage, maxX <= minX || maxY <= minY ? new(0, 0, 0, 0) : new(minX, minY, maxX - minX, maxY - minY));
    }
}

public readonly record struct RectI(int X, int Y, int Width, int Height)
{
    public static RectI Normalize(int x0, int y0, int x1, int y1, int boundWidth, int boundHeight)
    {
        int left = Math.Clamp(Math.Min(x0, x1), 0, boundWidth);
        int top = Math.Clamp(Math.Min(y0, y1), 0, boundHeight);
        int right = Math.Clamp(Math.Max(x0, x1), 0, boundWidth);
        int bottom = Math.Clamp(Math.Max(y0, y1), 0, boundHeight);
        return new RectI(left, top, right - left, bottom - top);
    }

    public static RectI Union(RectI first, RectI second)
    {
        int left = Math.Min(first.X, second.X);
        int top = Math.Min(first.Y, second.Y);
        int right = Math.Max(first.X + first.Width, second.X + second.Width);
        int bottom = Math.Max(first.Y + first.Height, second.Y + second.Height);
        return new RectI(left, top, right - left, bottom - top);
    }
}
