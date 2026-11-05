namespace Compositor.Core;

/// <summary>The Blur tool's warp modes (SmudgeLiquify.swift's BlurToolMode, blur excluded —
/// it goes through the blur-sample path instead).</summary>
public enum WarpMode
{
    Smudge,
    Liquify,
}

/// <summary>
/// A Smudge or Liquify stroke's working copy, ported from SmudgeLiquify.swift's CPU path:
/// the active layer is rendered into a canvas-sized copy, dabs push or drag its pixels dab
/// by dab, and the result is written back into the layer's own pixels along the stroke path
/// when the stroke ends (one undo step). The Mac's GPU path (MetalWarp) is not ported.
/// </summary>
public sealed class WarpStroke
{
    private readonly Surface working;
    private readonly LayerTransform transform;
    private readonly int width;
    private readonly int height;
    private readonly WarpMode mode;
    private readonly double diameter;
    private readonly double hardness;
    private readonly double strength;
    private readonly List<PointD> points = new();
    private float[] carried = new float[0];
    private float[] scratch = new float[0];
    private PointD? last;

    public WarpStroke(Surface layerImage, LayerTransform transform, int canvasWidth, int canvasHeight,
        WarpMode mode, double diameter, double hardness, double strength)
    {
        working = new Surface(canvasWidth, canvasHeight);
        Resampler.Draw(layerImage, transform, working, 0, 0, canvasWidth, canvasHeight);
        this.transform = transform;
        width = canvasWidth;
        height = canvasHeight;
        this.mode = mode;
        this.diameter = Math.Max(2, diameter);
        this.hardness = Math.Min(0.98, Math.Max(0, hardness));
        this.strength = Math.Min(1, Math.Max(0.01, strength));
    }

    public IReadOnlyList<PointD> Points => points;

    private int Radius => (int)Math.Ceiling(diameter / 2);

    /// <summary>How much a dab moves pixels at a distance u (0 center, 1 rim): a smoothstep
    /// from full movement at the hardness edge to none at the rim.</summary>
    private float Weight(float u)
    {
        if (u >= 1) return 0;
        float h = (float)hardness;
        if (u <= h) return 1;
        float t = (1 - u) / (1 - h);
        return t * t * (3 - 2 * t);
    }

    /// <summary>Continues the stroke to point, dabbing along the way at the mode's spacing.</summary>
    public void Append(PointD point)
    {
        if (last is not { } from)
        {
            last = point;
            if (mode == WarpMode.Smudge) PickUp(point);
            points.Add(point);
            return;
        }
        double distance = Math.Sqrt(Math.Pow(point.X - from.X, 2) + Math.Pow(point.Y - from.Y, 2));
        // Smudge drags the pixels one dab's spacing at a time; liquify's wider steps run the
        // pushes together into one continuous warp.
        double spacing = Math.Max(1, diameter * (mode == WarpMode.Smudge ? 0.005 : 0.025));
        if (distance < spacing) return;
        int steps = (int)Math.Ceiling(distance / spacing);
        var previous = from;
        for (int step = 1; step <= steps; step++)
        {
            double t = (double)step / steps;
            var next = new PointD(from.X + (point.X - from.X) * t, from.Y + (point.Y - from.Y) * t);
            if (mode == WarpMode.Smudge) Smudge(next);
            else Push(previous, next);
            points.Add(next);
            previous = next;
        }
        last = point;
    }

    /// <summary>Smudge picks up the color under the first dab to carry along the stroke.</summary>
    private void PickUp(PointD center)
    {
        int r = Radius, side = 2 * r + 1;
        carried = new float[side * side * 4];
        int cx = (int)Math.Round(center.X), cy = (int)Math.Round(center.Y);
        for (int dy = -r; dy <= r; dy++)
        {
            int y = cy + dy;
            if (y < 0 || y >= height) continue;
            for (int dx = -r; dx <= r; dx++)
            {
                int x = cx + dx;
                if (x < 0 || x >= width) continue;
                int p = (y * width + x) * 4;
                int c = ((dy + r) * side + dx + r) * 4;
                for (int k = 0; k < 4; k++) carried[c + k] = working.Pixels[p + k];
            }
        }
    }

    /// <summary>Lays what the brush carries down at strength, then carries what it just left.</summary>
    private void Smudge(PointD center)
    {
        int r = Radius, side = 2 * r + 1;
        int cx = (int)Math.Round(center.X), cy = (int)Math.Round(center.Y);
        double keep = strength;
        double invR = 1 / (diameter / 2);
        for (int dy = -r; dy <= r; dy++)
        {
            int y = cy + dy;
            if (y < 0 || y >= height) continue;
            for (int dx = -r; dx <= r; dx++)
            {
                int x = cx + dx;
                if (x < 0 || x >= width) continue;
                float w = Weight((float)(Math.Sqrt(dx * dx + dy * dy) * invR));
                if (w <= 0) continue;
                int p = (y * width + x) * 4;
                int c = ((dy + r) * side + dx + r) * 4;
                for (int k = 0; k < 4; k++)
                {
                    double under = working.Pixels[p + k];
                    // What was under the brush at the last dab, laid down here at the smudge's
                    // strength; the brush then carries what it just left, and nothing older.
                    double painted = under + (carried[c + k] - under) * w * keep;
                    working.Pixels[p + k] = (byte)Math.Clamp((int)Math.Round(painted), 0, 255);
                    carried[c + k] = (float)painted;
                }
            }
        }
    }

    /// <summary>Forward warp: pixels under the brush move with it, most at its center.</summary>
    private void Push(PointD a, PointD b)
    {
        int r = Radius;
        double moveX = (b.X - a.X) * strength;
        double moveY = (b.Y - a.Y) * strength;
        int margin = (int)Math.Ceiling(Math.Max(Math.Abs(moveX), Math.Abs(moveY))) + 2;
        int cx = (int)Math.Round(b.X), cy = (int)Math.Round(b.Y);
        int x0 = Math.Max(0, cx - r - margin), x1 = Math.Min(width - 1, cx + r + margin);
        int y0 = Math.Max(0, cy - r - margin), y1 = Math.Min(height - 1, cy + r + margin);
        if (x0 > x1 || y0 > y1) return;
        int cw = x1 - x0 + 1, ch = y1 - y0 + 1;
        if (scratch.Length < cw * ch * 4) scratch = new float[cw * ch * 4];
        for (int y = 0; y < ch; y++)
        {
            for (int x = 0; x < cw; x++)
            {
                int p = ((y + y0) * width + x + x0) * 4;
                int s = (y * cw + x) * 4;
                for (int k = 0; k < 4; k++) scratch[s + k] = working.Pixels[p + k];
            }
        }
        double invR = 1 / (diameter / 2);
        for (int dy = -r; dy <= r; dy++)
        {
            int y = cy + dy;
            if (y < y0 || y > y1) continue;
            for (int dx = -r; dx <= r; dx++)
            {
                int x = cx + dx;
                if (x < x0 || x > x1) continue;
                float w = Weight((float)(Math.Sqrt(dx * dx + dy * dy) * invR));
                if (w <= 0) continue;
                // Bilinear sample of the old pixels, from behind the brush's travel.
                double sx = Math.Min(cw - 1, Math.Max(0, x - x0 - moveX * w));
                double sy = Math.Min(ch - 1, Math.Max(0, y - y0 - moveY * w));
                int ix = (int)Math.Min(cw - 2, sx);
                int iy = (int)Math.Min(ch - 2, sy);
                if (ix < 0 || iy < 0) continue;
                double fx = sx - ix, fy = sy - iy;
                int p = (y * width + x) * 4;
                int s00 = (iy * cw + ix) * 4, s10 = s00 + 4, s01 = s00 + cw * 4, s11 = s01 + 4;
                for (int k = 0; k < 4; k++)
                {
                    double top = scratch[s00 + k] + (scratch[s10 + k] - scratch[s00 + k]) * fx;
                    double bottom = scratch[s01 + k] + (scratch[s11 + k] - scratch[s01 + k]) * fx;
                    working.Pixels[p + k] = (byte)Math.Clamp((int)Math.Round(top + (bottom - top) * fy), 0, 255);
                }
            }
        }
    }

    /// <summary>
    /// Writes the working copy back into the layer's own pixels along the stroke path —
    /// a disc a little wider than the brush (hardness 1) at every fifth of the diameter —
    /// mapped through the layer transform. Returns the layer's new surface, or null when
    /// the stroke never dabbed.
    /// </summary>
    public Surface? FinishIntoLayer()
    {
        if (points.Count == 0) return null;
        int stampRadius = Radius + 2;
        var result = new Surface(width, height, (byte[])working.Pixels.Clone(), working.Stride);
        var painted = new bool[width * height];
        double spacing = Math.Max(1, diameter * 0.05);
        PointD? kept = null;
        foreach (var point in points)
        {
            // The Mac's skip test is circular (hypot < spacing), not per-axis — a square test
            // would drop corner stamps the Mac keeps (SmudgeLiquify.swift:239).
            if (kept is { } k && point != points[^1]
                && Math.Sqrt((point.X - k.X) * (point.X - k.X) + (point.Y - k.Y) * (point.Y - k.Y)) < spacing) continue;
            kept = point;
            int cx = (int)Math.Round(point.X), cy = (int)Math.Round(point.Y);
            for (int dy = -stampRadius; dy <= stampRadius; dy++)
            {
                int y = cy + dy;
                if (y < 0 || y >= height) continue;
                for (int dx = -stampRadius; dx <= stampRadius; dx++)
                {
                    int x = cx + dx;
                    if (x < 0 || x >= width || painted[y * width + x]) continue;
                    painted[y * width + x] = true;
                    var layerPixel = BrushEngine.DocumentToPixel(transform, width, height, new PointD(x + 0.5, y + 0.5));
                    int layerX = (int)Math.Floor(layerPixel.X);
                    int layerY = (int)Math.Floor(layerPixel.Y);
                    if ((uint)layerX >= (uint)working.Width || (uint)layerY >= (uint)working.Height) continue;
                    int canvasIndex = y * width + x;
                    var inImage = result;
                    int layerIndex = layerY * inImage.Stride + layerX * 4;
                    int canvasIndex4 = canvasIndex * 4;
                    for (int ch = 0; ch < 4; ch++) inImage.Pixels[layerIndex + ch] = working.Pixels[canvasIndex4 + ch];
                }
            }
        }
        return result;
    }
}
