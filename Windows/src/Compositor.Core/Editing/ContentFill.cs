namespace Compositor.Core;

/// <summary>
/// Content-aware fill, ported from ContentFill.c: a coherent patch synthesis. Selected
/// pixels are filled from unselected opaque ones — a frontier queue fills boundary pixels
/// first, each choosing the donor whose neighborhood best matches what is already known
/// (neighbor-propagated offsets, then randomized refinement), and the donor's pixels are
/// copied in. Unselected transparent pixels are left alone. The randomized search makes the
/// result non-deterministic by design, as on the Mac (LCG seed 0x6d2b79f5).
/// </summary>
public static class ContentFill
{
    private const double Unknown = double.MaxValue;

    private static uint NextRandom(ref uint state)
    {
        state = state * 1664525u + 1013904223u;
        return state;
    }

    private static double Match(Surface pixels, bool[] known, int w, int h, int p, int q, int radius)
    {
        int px = p % w, py = p / w, qx = q % w, qy = q / w;
        int count = 0;
        double sum = 0;
        var data = pixels.Pixels;
        int stride = pixels.Stride;
        for (int dy = -radius; dy <= radius; dy++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                int x = px + dx, y = py + dy, sx = qx + dx, sy = qy + dy;
                if (x < 0 || y < 0 || x >= w || y >= h || sx < 0 || sy < 0 || sx >= w || sy >= h || !known[y * w + x]) continue;
                int a = y * stride + x * 4;
                int b = sy * stride + sx * 4;
                for (int c = 0; c < 4; c++)
                {
                    int d = data[a + c] - data[b + c];
                    sum += d * d;
                }
                count++;
            }
        }
        return count > 0 ? sum / count : Unknown;
    }

    /// <summary>
    /// Fills `mask`-covered pixels of `surface` in place from unselected opaque pixels.
    /// Returns false when there is nothing to fill from (no donors) — the caller reports
    /// "use a smaller selection with some surrounding image".
    /// </summary>
    public static bool Fill(Surface surface, MaskSurface mask)
    {
        int w = surface.Width, h = surface.Height;
        int n = w * h;
        var data = surface.Pixels;
        int stride = surface.Stride;
        var known = new bool[n];
        var target = new bool[n];
        var valid = new bool[n];
        var queued = new bool[n];
        var chosen = new int[n];
        Array.Fill(chosen, -1);
        var donors = new int[n];
        var queue = new int[n];
        int radius = w >= 5 && h >= 5 ? 2 : 0;
        int missing = 0, donorCount = 0, head = 0, tail = 0, scan = 0;

        // Selected pixels are filled. Unselected opaque pixels are the image to match and
        // copy from; unselected transparent ones are neither — left as they are.
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int p = y * w + x;
                target[p] = mask.Pixels[y * mask.Width + x] != 0;
                known[p] = !target[p] && data[y * stride + x * 4 + 3] == 255;
                chosen[p] = -1;
                if (target[p]) missing++;
            }
        }
        if (missing == 0) return true;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int p = y * w + x;
                if (!known[p]) continue;
                bool ok = true;
                for (int dy = -radius; dy <= radius && ok; dy++)
                {
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        int sx = x + dx, sy = y + dy;
                        if (sx < 0 || sy < 0 || sx >= w || sy >= h || !known[sy * w + sx]) { ok = false; break; }
                    }
                }
                if (ok) { valid[p] = true; donors[donorCount++] = p; }
            }
        }
        if (donorCount == 0) return false;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int p = y * w + x;
                if (target[p] && ((x > 0 && known[p - 1]) || (x + 1 < w && known[p + 1])
                    || (y > 0 && known[p - w]) || (y + 1 < h && known[p + w])))
                {
                    queue[tail++] = p;
                    queued[p] = true;
                }
            }
        }

        uint seed = 0x6d2b79f5;
        while (true)
        {
            while (head < tail)
            {
                int p = queue[head++];
                int x = p % w, y = p / w, best = -1;
                double score = Unknown;
                int neighborUpLeft = x > 0 ? p - 1 : -1;
                int neighborUpRight = x + 1 < w ? p + 1 : -1;
                int neighborUp = y > 0 ? p - w : -1;
                int neighborDown = y + 1 < h ? p + w : -1;
                Span<int> neighbors = [neighborUpLeft, neighborUpRight, neighborUp, neighborDown];
                // Propagate coherent source offsets, then refine with randomized patch search.
                for (int k = 0; k < 28; k++)
                {
                    int q;
                    if (k < 4)
                    {
                        int t = neighbors[k];
                        q = t >= 0 ? (chosen[t] >= 0 ? chosen[t] : t) + (p - t) : -1;
                    }
                    else
                    {
                        q = donors[NextRandom(ref seed) % donorCount];
                    }
                    if (q < 0 || q >= n || !valid[q]) continue;
                    double s = Match(surface, known, w, h, p, q, radius);
                    if (best < 0 || s < score) { score = s; best = q; }
                }
                if (best < 0) best = donors[0];
                for (int r = 64; r >= 1; r /= 2)
                {
                    int qx = best % w + (int)(NextRandom(ref seed) % (2 * r + 1)) - r;
                    int qy = best / w + (int)(NextRandom(ref seed) % (2 * r + 1)) - r;
                    if (qx < 0 || qy < 0 || qx >= w || qy >= h || !valid[qy * w + qx]) continue;
                    int q = qy * w + qx;
                    double s = Match(surface, known, w, h, p, q, radius);
                    if (s < score) { score = s; best = q; }
                }
                int sourceIndex = (best / w) * stride + (best % w) * 4;
                int targetIndex = (p / w) * stride + (p % w) * 4;
                for (int c = 0; c < 4; c++) data[targetIndex + c] = data[sourceIndex + c];
                known[p] = true;
                chosen[p] = best;
                for (int k = 0; k < 4; k++)
                {
                    int q = neighbors[k];
                    if (q >= 0 && target[q] && !known[q] && !queued[q])
                    {
                        queued[q] = true;
                        queue[tail++] = q;
                    }
                }
            }
            // A selected area that only transparency touches starts from the best random
            // donor, then spreads.
            while (scan < n && (!target[scan] || known[scan])) scan++;
            if (scan >= n) break;
            queue[tail++] = scan;
            queued[scan] = true;
        }
        return true;
    }
}
