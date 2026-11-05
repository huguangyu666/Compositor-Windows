using System.Numerics;
using System.Runtime.InteropServices;

namespace Compositor.Core;

public enum LassoSelectionMode { Replace, Add, Subtract }

/// <summary>The Mac's winding-filled lasso outline, expressed as document-space coverage.
/// Four subpixel scanlines smooth its edges; a hard outline samples pixel centers.</summary>
public static class LassoSelection
{
    private readonly record struct ScanEdge(PointD A, PointD B, double MinY, double MaxY);

    public static Selection Rasterize(int width, int height, IReadOnlyList<PointD> points, bool antialiased = true,
        CancellationToken cancellation = default)
    {
        var mask = new MaskSurface(width, height);
        if (points.Count < 3 || points.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
            return new Selection(mask, new RectI(0, 0, 0, 0));
        int y0 = (int)Math.Clamp(Math.Floor(points.Min(point => point.Y)), 0, height);
        int y1 = (int)Math.Clamp(Math.Ceiling(points.Max(point => point.Y)), 0, height);
        int x0 = (int)Math.Clamp(Math.Floor(points.Min(point => point.X)), 0, width);
        int x1 = (int)Math.Clamp(Math.Ceiling(points.Max(point => point.X)), 0, width);
        if (x0 >= x1 || y0 >= y1) return new Selection(mask, new RectI(0, 0, 0, 0));

        int samples = antialiased ? 4 : 1;
        var edges = new List<ScanEdge>(points.Count);
        for (int i = 0; i < points.Count; i++)
        {
            if ((i & 255) == 0) cancellation.ThrowIfCancellationRequested();
            var a = points[i]; var b = points[(i + 1) % points.Count];
            if (a.Y != b.Y) edges.Add(new(a, b, Math.Min(a.Y, b.Y), Math.Max(a.Y, b.Y)));
        }
        edges.Sort((left, right) => left.MinY.CompareTo(right.MinY));
        var active = new List<ScanEdge>();
        int nextEdge = 0;
        var crossings = new List<(double X, int Delta)>(points.Count);
        var coverage = new double[x1 - x0];
        for (int y = y0; y < y1; y++)
        {
            // A complex lasso on a large canvas is scanned row by row, so a superseded release can
            // stop at the next row instead of filling the whole document.
            cancellation.ThrowIfCancellationRequested();
            Array.Clear(coverage);
            for (int sample = 0; sample < samples; sample++)
            {
                double scanY = y + (sample + 0.5) / samples;
                crossings.Clear();
                // Scanlines ascend: admit each edge once and retire it at the original
                // exclusive upper endpoint. Only edges crossing this sample need division.
                for (int i = active.Count - 1; i >= 0; i--)
                    if (scanY >= active[i].MaxY) active.RemoveAt(i);
                while (nextEdge < edges.Count && edges[nextEdge].MinY <= scanY)
                {
                    var edge = edges[nextEdge++];
                    if (scanY < edge.MaxY) active.Add(edge);
                }
                foreach (var edge in active)
                {
                    var a = edge.A; var b = edge.B;
                    double x = a.X + (scanY - a.Y) * (b.X - a.X) / (b.Y - a.Y);
                    crossings.Add((x, b.Y > a.Y ? 1 : -1));
                }
                crossings.Sort((left, right) => left.X.CompareTo(right.X));
                int winding = 0;
                double start = 0;
                foreach (var crossing in crossings)
                {
                    if (winding != 0)
                    {
                        double left = Math.Clamp(start, x0, x1), right = Math.Clamp(crossing.X, x0, x1);
                        int first = antialiased ? (int)Math.Floor(left) : (int)Math.Ceiling(left - 0.5);
                        int end = antialiased ? (int)Math.Ceiling(right) : (int)Math.Ceiling(right - 0.5);
                        first = Math.Max(x0, first); end = Math.Min(x1, end);
                        if (first < end)
                        {
                            if (!antialiased) AddCoverage(coverage.AsSpan(first - x0, end - first), 1);
                            else
                            {
                                // Only the two end pixels need fractional overlap. Interior
                                // pixels receive the same exact 1/4 contribution in each lane.
                                coverage[first - x0] += Math.Max(0, Math.Min(first + 1, right) - Math.Max(first, left)) / samples;
                                if (end - first > 1)
                                {
                                    int last = end - 1;
                                    coverage[last - x0] += Math.Max(0, Math.Min(last + 1, right) - Math.Max(last, left)) / samples;
                                    AddCoverage(coverage.AsSpan(first + 1 - x0, end - first - 2), 1d / samples);
                                }
                            }
                        }
                    }
                    winding += crossing.Delta;
                    start = crossing.X;
                }
            }
            for (int x = x0; x < x1; x++)
                mask.Pixels[y * width + x] = (byte)Math.Clamp(Math.Round(coverage[x - x0] * 255, MidpointRounding.AwayFromZero), 0, 255);
        }
        return Tightened(mask);
    }

    private static void AddCoverage(Span<double> coverage, double amount)
    {
        int x = 0;
        if (Vector.IsHardwareAccelerated && coverage.Length >= Vector<double>.Count)
        {
            var lanes = MemoryMarshal.Cast<double, Vector<double>>(coverage);
            var increment = new Vector<double>(amount);
            for (int i = 0; i < lanes.Length; i++) lanes[i] += increment;
            x = lanes.Length * Vector<double>.Count;
        }
        for (; x < coverage.Length; x++) coverage[x] += amount;
    }

    /// <summary>New outlines reset feather. Subtracting from no selection is a no-op;
    /// subtracting everything keeps an explicit empty mask, so painting touches nothing.</summary>
    public static Selection? Combine(Selection? current, Selection incoming, LassoSelectionMode mode)
    {
        if (mode == LassoSelectionMode.Replace) return incoming;
        if (current is null) return mode == LassoSelectionMode.Add ? incoming : null;
        var old = current.Coverage;
        var added = incoming.Coverage;
        if (old.Width != added.Width || old.Height != added.Height) throw new ArgumentException("Selection dimensions must match.");
        var mask = new MaskSurface(old.Width, old.Height);
        for (int i = 0; i < mask.Pixels.Length; i++)
            mask.Pixels[i] = mode == LassoSelectionMode.Add
                ? Math.Max(old.Pixels[i], added.Pixels[i])
                : (byte)((old.Pixels[i] * (255 - added.Pixels[i]) + 127) / 255);
        return Tightened(mask);
    }

    private static Selection Tightened(MaskSurface mask)
    {
        var selection = Selection.FromCoverage(mask);
        return selection.Bounds.Width <= 0 || selection.Bounds.Height <= 0
            ? new Selection(mask, new RectI(0, 0, 0, 0)) : selection;
    }
}
