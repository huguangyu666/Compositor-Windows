namespace Compositor.Core;

public enum SpotHealingMode { ContentAware, CreateTexture, ProximityMatch }

/// <summary>Managed port of Compositor/Rendering/HealPixels.c. Reads premultiplied RGBA,
/// searches a nearby patch by its surrounding ring, and solves the edge difference membrane.</summary>
public static class SpotHealingEngine
{
    public const int MaxWorkPixels = 8_000_000;
    private const byte Outside = 0, Ring = 1, Hole = 2;

    public static Surface? Heal(Surface source, MaskSurface coverage, double opacity, SpotHealingMode mode, uint seed,
        int workPixelBudget = MaxWorkPixels, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (source.Width < 1 || source.Height < 1 || source.Width > Limits.MaxSide || source.Height > Limits.MaxSide
            || source.PixelCount > Limits.MaxSurfacePixels || source.Stride % 4 != 0
            || source.Stride < (long)source.Width * 4 || source.Pixels.LongLength < (long)source.Stride * source.Height
            || coverage.Width != source.Width || coverage.Height != source.Height || coverage.Pixels.LongLength < source.PixelCount)
            throw new ArgumentException("The image and coverage buffers do not describe the same valid pixel grid.");
        if (!double.IsFinite(opacity) || opacity < 0 || opacity > 1 || !Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(opacity));
        if (workPixelBudget < 1) throw new ArgumentOutOfRangeException(nameof(workPixelBudget));
        if (opacity == 0) return null;
        var box = CoverageBounds(coverage, cancellationToken);
        if (box.Width == 0) return null;
        int ring = Math.Clamp(Math.Max(box.Width, box.Height) / 8, 2, 16);
        int x0 = Math.Max(0, box.X - ring), y0 = Math.Max(0, box.Y - ring);
        int w = Math.Min(source.Width, box.X + box.Width + ring) - x0;
        int h = Math.Min(source.Height, box.Y + box.Height + ring) - y0;
        if ((long)w * h > Math.Min(workPixelBudget, MaxWorkPixels))
            throw new InvalidOperationException("The spot healing work region exceeds its memory budget.");
        int n = checked(w * h);
        var role = new byte[n]; var near = new byte[n];
        var prefix = new int[Math.Max(w, h) + 1];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            role[y * w + x] = coverage.Pixels[(y0 + y) * source.Width + x0 + x] > 0 ? Hole : Outside;
        for (int y = 0; y < h; y++)
        {
            prefix[0] = 0;
            for (int x = 0; x < w; x++) prefix[x + 1] = prefix[x] + (role[y * w + x] == Hole ? 1 : 0);
            for (int x = 0; x < w; x++) near[y * w + x] = prefix[Math.Min(w, x + ring + 1)] - prefix[Math.Max(0, x - ring)] > 0 ? (byte)1 : (byte)0;
        }
        int ringCount = 0;
        for (int x = 0; x < w; x++)
        {
            prefix[0] = 0;
            for (int y = 0; y < h; y++) prefix[y + 1] = prefix[y] + near[y * w + x];
            for (int y = 0; y < h; y++)
                if (role[y * w + x] == Outside && prefix[Math.Min(h, y + ring + 1)] - prefix[Math.Max(0, y - ring)] > 0)
                    role[y * w + x] = Ring;
        }
        foreach (byte value in role) if (value == Ring) ringCount++;
        if (ringCount == 0) return null;

        double Score(int dx, int dy)
        {
            if (Math.Abs(dx) < w && Math.Abs(dy) < h || x0 + dx < 0 || y0 + dy < 0
                || x0 + w + dx > source.Width || y0 + h + dy > source.Height) return double.PositiveInfinity;
            double sum = 0; int count = 0;
            for (int y = 0; y < h; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int x = 0; x < w; x++)
                {
                    if (role[y * w + x] != Ring) continue;
                    int target = (y0 + y) * source.Stride + (x0 + x) * 4;
                    int sample = (y0 + y + dy) * source.Stride + (x0 + x + dx) * 4;
                    for (int c = 0; c < 4; c++) { double d = source.Pixels[target + c] - source.Pixels[sample + c]; sum += d * d; }
                    count++;
                }
            }
            return count == 0 ? double.PositiveInfinity : sum / count;
        }
        int ox = 0, oy = 0; bool haveSource = false;
        if (mode != SpotHealingMode.CreateTexture)
        {
            double[] factors = [1.05, 1.35, 1.75, 2.25, 2.8];
            int count = mode == SpotHealingMode.ProximityMatch ? 2 : 5;
            double best = double.PositiveInfinity;
            for (int f = 0; f < count; f++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int a = 0; a < 24; a++)
                {
                    double angle = a * Math.PI / 12;
                    int dx = Round(Math.Cos(angle) * factors[f] * w), dy = Round(Math.Sin(angle) * factors[f] * h);
                    double score = Score(dx, dy) * (1 + (mode == SpotHealingMode.ProximityMatch ? .6 : .1) * f);
                    if (score < best) { best = score; ox = dx; oy = dy; }
                }
            }
            if (double.IsFinite(best))
            {
                int cx = ox, cy = oy; double refined = Score(cx, cy);
                for (int j = -3; j <= 3; j++)
                for (int i = -3; i <= 3; i++)
                {
                    double score = Score(cx + i, cy + j);
                    if (score < refined) { refined = score; ox = cx + i; oy = cy + j; }
                }
                haveSource = true;
            }
        }
        var values = new float[checked(n * 4)];
        double[] mean = new double[4], detail = new double[3];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int p = y * w + x;
            if (role[p] != Ring) continue;
            int ix = x0 + x, iy = y0 + y, target = iy * source.Stride + ix * 4;
            int sample = haveSource ? (iy + oy) * source.Stride + (ix + ox) * 4 : 0;
            for (int c = 0; c < 4; c++)
            {
                values[p * 4 + c] = source.Pixels[target + c] - (haveSource ? source.Pixels[sample + c] : 0);
                mean[c] += values[p * 4 + c];
            }
            if (haveSource) continue;
            for (int c = 0; c < 3; c++)
            {
                double around = 0; int count = 0;
                void Neighbor(int nx, int ny)
                { if (nx >= 0 && ny >= 0 && nx < source.Width && ny < source.Height) { around += source.Pixels[ny * source.Stride + nx * 4 + c]; count++; } }
                Neighbor(ix - 1, iy); Neighbor(ix + 1, iy); Neighbor(ix, iy - 1); Neighbor(ix, iy + 1);
                if (count > 0) { double d = source.Pixels[target + c] - around / count; detail[c] += d * d; }
            }
        }
        for (int c = 0; c < 4; c++) mean[c] /= ringCount;
        for (int p = 0; p < n; p++) if (role[p] == Hole)
            for (int c = 0; c < 4; c++) values[p * 4 + c] = (float)mean[c];
        Solve(values, role, w, h, 0, cancellationToken);
        for (int c = 0; c < 3; c++) detail[c] = Math.Sqrt(detail[c] / ringCount) * .9;
        var result = source.Clone(); bool changed = false;
        float cap = (float)opacity; // The C entry point receives float opacity.
        for (int y = 0; y < h; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < w; x++)
            {
                int p = y * w + x;
                if (role[p] != Hole) continue;
                int ix = x0 + x, iy = y0 + y, target = iy * source.Stride + ix * 4;
                int sample = haveSource ? (iy + oy) * source.Stride + (ix + ox) * 4 : 0;
                double amount = coverage.Pixels[iy * source.Width + ix] / 255d * cap;
                double grain = 0;
                if (!haveSource)
                {
                    uint key = Hash(seed ^ Hash((uint)(iy * source.Width + ix)));
                    grain = Math.Sqrt(-2 * Math.Log(1 - Unit(key))) * Math.Cos(2 * Math.PI * Unit(key ^ 0x68e31da4u));
                }
                double Value(int c)
                {
                    double healed = (haveSource ? source.Pixels[sample + c] : 0) + values[p * 4 + c] + (c < 3 ? grain * detail[c] : 0);
                    return source.Pixels[target + c] + (healed - source.Pixels[target + c]) * amount;
                }
                byte alpha = (byte)Round(Math.Clamp(Value(3), 0, 255));
                result.Pixels[target + 3] = alpha;
                changed |= alpha != source.Pixels[target + 3];
                for (int c = 0; c < 3; c++)
                {
                    byte value = (byte)Round(Math.Clamp(Value(c), 0, alpha));
                    result.Pixels[target + c] = value;
                    changed |= value != source.Pixels[target + c];
                }
            }
        }
        return changed ? result : null;
    }

    internal static RectI CoverageBounds(MaskSurface mask, CancellationToken token = default)
    {
        int x0 = mask.Width, y0 = mask.Height, x1 = 0, y1 = 0;
        for (int y = 0; y < mask.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < mask.Width; x++)
                if (mask.Pixels[y * mask.Width + x] != 0)
                { x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x + 1); y1 = Math.Max(y1, y + 1); }
        }
        return x1 <= x0 ? new(0, 0, 0, 0) : new(x0, y0, x1 - x0, y1 - y0);
    }

    private static void Solve(float[] values, byte[] role, int w, int h, int depth, CancellationToken token)
    {
        int iterations = 300;
        if (w > 32 && h > 32 && depth < 16)
        {
            int cw = (w + 1) / 2, ch = (h + 1) / 2;
            var coarse = new float[cw * ch * 4]; var coarseRole = new byte[cw * ch];
            Span<float> knownSum = stackalloc float[4]; Span<float> holeSum = stackalloc float[4];
            for (int y = 0; y < ch; y++)
            for (int x = 0; x < cw; x++)
            {
                int known = 0, hole = 0;
                knownSum.Clear(); holeSum.Clear();
                for (int j = 0; j < 2; j++)
                for (int i = 0; i < 2; i++)
                {
                    int fx = x * 2 + i, fy = y * 2 + j;
                    if (fx >= w || fy >= h) continue;
                    int p = fy * w + fx;
                    if (role[p] == Ring) { known++; for (int c = 0; c < 4; c++) knownSum[c] += values[p * 4 + c]; }
                    else if (role[p] == Hole) { hole++; for (int c = 0; c < 4; c++) holeSum[c] += values[p * 4 + c]; }
                }
                int q = y * cw + x;
                if (known > 0) { coarseRole[q] = Ring; for (int c = 0; c < 4; c++) coarse[q * 4 + c] = knownSum[c] / known; }
                else if (hole > 0) { coarseRole[q] = Hole; for (int c = 0; c < 4; c++) coarse[q * 4 + c] = holeSum[c] / hole; }
            }
            Solve(coarse, coarseRole, cw, ch, depth + 1, token);
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int p = y * w + x, q = y / 2 * cw + x / 2;
                if (role[p] == Hole && coarseRole[q] == Hole) Array.Copy(coarse, q * 4, values, p * 4, 4);
            }
            iterations = 40;
        }
        Span<float> sum = stackalloc float[4];
        Span<int> neighbors = stackalloc int[4];
        for (int it = 0; it < iterations; it++)
        {
            token.ThrowIfCancellationRequested();
            for (int y = 0; y < h; y++)
            {
                token.ThrowIfCancellationRequested();
                for (int x = 0; x < w; x++)
                {
                    int p = y * w + x;
                    if (role[p] != Hole) continue;
                    sum.Clear(); int count = 0;
                    int left = x > 0 ? p - 1 : -1, right = x + 1 < w ? p + 1 : -1;
                    int up = y > 0 ? p - w : -1, down = y + 1 < h ? p + w : -1;
                    neighbors[0] = left; neighbors[1] = right; neighbors[2] = up; neighbors[3] = down;
                    foreach (int q in neighbors)
                    {
                        if (q < 0 || role[q] == Outside) continue;
                        for (int c = 0; c < 4; c++) sum[c] += values[q * 4 + c];
                        count++;
                    }
                    if (count == 0) continue;
                    for (int c = 0; c < 4; c++) values[p * 4 + c] += 1.8f * (sum[c] / count - values[p * 4 + c]);
                }
            }
        }
    }
    private static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);
    private static uint Hash(uint x) { unchecked { x ^= x >> 16; x *= 0x7feb352du; x ^= x >> 15; x *= 0x846ca68bu; return x ^ (x >> 16); } }
    private static double Unit(uint key) => (Hash(key) >> 8) / 16777216d;
}
