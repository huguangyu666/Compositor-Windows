namespace Compositor.Core;

public sealed partial class Selection
{
    private readonly MaskSurface sourceCoverage;
    private readonly int documentWidth, documentHeight, offsetX, offsetY;
    private MaskSurface? projectedCoverage;

    private Selection(MaskSurface source, RectI bounds, double feather, int x, int y, int width, int height)
    {
        sourceCoverage = source; MaskBounds = bounds; Feather = feather;
        offsetX = x; offsetY = y; documentWidth = width; documentHeight = height;
    }

    // Keep the unclipped outline, like the source CGPath, so an off-canvas drag can return intact.
    public MaskSurface OutlineCoverage => sourceCoverage;
    public RectI SourceBounds => MaskBounds;
    public RectI OutlineBounds => new(checked(MaskBounds.X + offsetX), checked(MaskBounds.Y + offsetY), MaskBounds.Width, MaskBounds.Height);
    public PointD OutlineOffset => new(offsetX, offsetY);

    public Selection Translated(int dx, int dy) => IsEmpty || dx == 0 && dy == 0 ? this
        : new(sourceCoverage, MaskBounds, Feather, checked(offsetX + dx), checked(offsetY + dy), documentWidth, documentHeight);

    internal Selection Reframed(int x, int y, int width, int height) => x == 0 && y == 0 && width == documentWidth && height == documentHeight ? this
        : new(sourceCoverage, MaskBounds, Feather, checked(offsetX - x), checked(offsetY - y), width, height);

    public bool Contains(PointD point) => double.IsFinite(point.X) && double.IsFinite(point.Y) && !IsEmpty
        && point.X >= OutlineBounds.X && point.Y >= OutlineBounds.Y
        && point.X < (long)OutlineBounds.X + OutlineBounds.Width && point.Y < (long)OutlineBounds.Y + OutlineBounds.Height
        && HardCoverageAt((int)Math.Floor(point.X), (int)Math.Floor(point.Y)) >= 128;

    private byte HardCoverageAt(int x, int y)
    {
        long sx = (long)x - offsetX, sy = (long)y - offsetY;
        return sx >= 0 && sy >= 0 && sx < sourceCoverage.Width && sy < sourceCoverage.Height
            ? sourceCoverage.Pixels[(int)sy * sourceCoverage.Width + (int)sx] : (byte)0;
    }

    private MaskSurface ProjectedCoverage
    {
        get
        {
            if (projectedCoverage is not null) return projectedCoverage;
            var result = new MaskSurface(documentWidth, documentHeight);
            int left = (int)Math.Clamp((long)offsetX, 0, documentWidth), top = (int)Math.Clamp((long)offsetY, 0, documentHeight);
            int right = (int)Math.Clamp((long)offsetX + sourceCoverage.Width, left, documentWidth);
            int bottom = (int)Math.Clamp((long)offsetY + sourceCoverage.Height, top, documentHeight);
            for (int y = top; right > left && y < bottom; y++) sourceCoverage.Pixels.AsSpan((y - offsetY) * sourceCoverage.Width + left - offsetX, right - left)
                .CopyTo(result.Pixels.AsSpan(y * documentWidth + left, right - left));
            return Interlocked.CompareExchange(ref projectedCoverage, result, null) ?? result;
        }
    }

    private static RectI ClipBounds(RectI bounds, int width, int height)
    {
        int left = (int)Math.Clamp((long)bounds.X, 0, width), top = (int)Math.Clamp((long)bounds.Y, 0, height);
        int right = (int)Math.Clamp((long)bounds.X + Math.Max(0, bounds.Width), left, width);
        int bottom = (int)Math.Clamp((long)bounds.Y + Math.Max(0, bounds.Height), top, height);
        return new(left, top, right - left, bottom - top);
    }

    internal bool SameOutlineAs(Selection other)
    {
        if (Feather != other.Feather || OutlineBounds != other.OutlineBounds || documentWidth != other.documentWidth || documentHeight != other.documentHeight) return false;
        if (ReferenceEquals(sourceCoverage, other.sourceCoverage) && offsetX == other.offsetX && offsetY == other.offsetY) return true;
        if (offsetX == other.offsetX && offsetY == other.offsetY && sourceCoverage.Width == other.sourceCoverage.Width && sourceCoverage.Height == other.sourceCoverage.Height)
            return sourceCoverage.Pixels.AsSpan().SequenceEqual(other.sourceCoverage.Pixels);
        var bounds = OutlineBounds;
        for (int y = 0; y < bounds.Height; y++) for (int x = 0; x < bounds.Width; x++)
            if (HardCoverageAt(bounds.X + x, bounds.Y + y) != other.HardCoverageAt(bounds.X + x, bounds.Y + y)) return false;
        return true;
    }

    internal IEnumerable<MaskSurface> RetainedCoverage
    {
        get
        {
            yield return sourceCoverage;
            if (projectedCoverage is { } projected && !ReferenceEquals(projected, sourceCoverage)) yield return projected;
            if (softenedCoverage is { } softened && !ReferenceEquals(softened, sourceCoverage) && !ReferenceEquals(softened, projectedCoverage)) yield return softened;
        }
    }
}
