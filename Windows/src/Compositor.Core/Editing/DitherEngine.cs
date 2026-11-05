namespace Compositor.Core;

/// <summary>The Filter > Dither styles, in the panel's menu order (DitherPixels.h).</summary>
public enum DitherStyle
{
    Atkinson,
    FloydSteinberg,
    Bayer2,
    Bayer4,
    Bayer8,
    HalftoneDots,
    HalftoneLines,
    HalftoneDiamonds,
    MacPatterns,
    Ascii,
    Scanlines,
}

/// <summary>
/// How a chunky pixel is drawn: a solid square, or a round dot with the dark color showing around
/// it, like the lit pixels of a dot-matrix or LED screen (Dither.swift:39-42).
/// </summary>
public enum DitherPixelShape
{
    Square,
    Dot,
}

/// <summary>
/// Which tones the result is built from (Dither.swift:44-48). Black & White is the fixed pair, Two
/// Colors is the caller's own dark and light, and Original keeps the image's colors — the macOS
/// sheet's picker, with the Windows parameters spelling it out as OriginalColors plus Dark/Light.
/// </summary>
public enum DitherPalette
{
    BlackWhite,
    TwoColors,
    Original,
}

/// <summary>
/// Which styles honor the caller's chunky-pixel block. ASCII's characters and a CRT's lines are
/// drawn at full resolution — averaging the source down first blurs the glyphs and breaks the
/// lines up, which is why the macOS styles carry the same rule (Document/Dither.swift:34).
/// The other three predicates are the same file's :26-32, and together they decide which controls
/// Filter ▸ Dither has any business showing (FilterSheet.swift:192-260).
/// </summary>
public static class DitherStyleExtensions
{
    public static bool UsesPixelSize(this DitherStyle style) => style is not (DitherStyle.Ascii or DitherStyle.Scanlines);

    /// Error diffusion: each pixel's rounding error is passed to its neighbors (Dither.swift:27).
    public static bool Diffuses(this DitherStyle style) => style is DitherStyle.Atkinson or DitherStyle.FloydSteinberg;

    /// Diffusion and ordered styles quantize to a number of tones; the rest draw marks in two (Dither.swift:29).
    public static bool HasTones(this DitherStyle style) => style.Diffuses() || style is DitherStyle.Bayer2 or DitherStyle.Bayer4 or DitherStyle.Bayer8;

    /// The three halftone screens, the only styles with a cell and a screen angle (Dither.swift:30).
    public static bool IsHalftone(this DitherStyle style) => style is DitherStyle.HalftoneDots or DitherStyle.HalftoneLines or DitherStyle.HalftoneDiamonds;

    /// Halftone shapes, patterns and characters mark one tone on the other, so which one is the mark
    /// matters — that is what "Light on Dark" decides (Dither.swift:31-32).
    public static bool DrawsMarks(this DitherStyle style) => !style.HasTones() && style != DitherStyle.Scanlines;
}

/// <summary>The dither's parameters — DitherPixels.h's DitherParams, in straight floats.</summary>
public sealed record DitherParams
{
    public DitherStyle Style { get; init; }
    /// <summary>Tones per channel for diffusion and ordered styles, 2–16.</summary>
    public int Levels { get; init; } = 2;
    /// <summary>How much of each pixel's error diffusion passes on, 0–1.</summary>
    public float Diffusion { get; init; } = 1;
    /// <summary>−1…1: darker (more ink) or lighter, before dithering.</summary>
    public float Density { get; init; }
    /// <summary>−1…1: flatter or punchier, pivoting on mid gray.</summary>
    public float Contrast { get; init; }
    /// <summary>Halftone/scanline cells, in pixels.</summary>
    public int Cell { get; init; } = 8;
    /// <summary>Scanlines: the line spacing in pixels. The macOS sheet feeds this in place of the
    /// halftone cell for that style (Dither.swift:170), so 0 means "use <see cref="Cell"/>".</summary>
    public int LineSpacing { get; init; }
    /// <summary>Halftone screen angle, radians.</summary>
    public float Angle { get; init; }
    public bool LightOnDark { get; init; } = true;
    /// <summary>True keeps the image's own colors; false builds the result from Dark/Light.</summary>
    public bool OriginalColors { get; init; }
    public (byte R, byte G, byte B) Dark { get; init; } = (0, 0, 0);
    public (byte R, byte G, byte B) Light { get; init; } = (255, 255, 255);
    /// <summary>ASCII: coverage maps of GlyphWidth × GlyphHeight bytes (255 = fully inked), least inked first,
    /// with each map's mean coverage. Provided by the caller — the App renders the font.</summary>
    public byte[] GlyphMaps { get; init; } = [];
    public float[] GlyphCoverage { get; init; } = [];
    public int GlyphWidth { get; init; } = 1;
    public int GlyphHeight { get; init; } = 1;
    /// <summary>Scanlines: how far each line breaks into dots (0–1) and its sideways wobble in pixels.</summary>
    public float Dots { get; init; }
    public float Wobble { get; init; }
    /// <summary>Scanlines: how far the lines' light blooms back over them, 0–1 — the macOS sheet's Glow
    /// slider, 35% by default (Dither.swift:70 and 97). Nothing in <see cref="Apply"/> reads it: the
    /// bloom is a second pass over the finished picture, so the caller takes it up
    /// (<see cref="Bloom"/> and <see cref="Glow"/>), as Dither.swift:131 does.</summary>
    public float Glow { get; init; }
}

/// <summary>
/// The dither engine, ported from DitherPixels.c: error diffusion (Atkinson's six-eighths,
/// Floyd–Steinberg), ordered Bayer thresholds, CRT scanlines with beam glow, and mark
/// styles (halftone screens, old Mac patterns, ASCII glyphs). Single-threaded — the C runs
/// bands across cores, which the port can adopt later without changing results.
/// </summary>
public static class DitherEngine
{
    private static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;

    private static float AdjustTone(float v, float gamma, float contrast)
    {
        v = MathF.Pow(Clamp01(v), gamma);
        return Clamp01((v - 0.5f) * contrast + 0.5f);
    }

    private static readonly (int Dx, int Dy, int Weight)[] AtkinsonTaps = [(1, 0, 1), (2, 0, 1), (-1, 1, 1), (0, 1, 1), (1, 1, 1), (0, 2, 1)];
    private static readonly (int Dx, int Dy, int Weight)[] FloydTaps = [(1, 0, 7), (-1, 1, 3), (0, 1, 5), (1, 1, 1)];

    // Atkinson passes on only six eighths of the error, which gives the Mac's crisp look.
    private static ((int Dx, int Dy, int Weight)[] Taps, int Count, float Divisor) KernelFor(DitherStyle style) =>
        style == DitherStyle.Atkinson ? (AtkinsonTaps, 6, 8) : (FloydTaps, 4, 16);

    private static float Quantize(float v, int levels)
    {
        float steps = levels - 1;
        return MathF.Round(Clamp01(v) * steps) / steps;
    }

    private static void Diffuse(Span<float> plane, byte[] alpha, int width, int height, DitherParams p,
        CancellationToken cancellation)
    {
        var (taps, count, divisor) = KernelFor(p.Style);
        for (int y = 0; y < height; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            bool reverse = (y & 1) != 0;
            for (int i = 0; i < width; i++)
            {
                int x = reverse ? width - 1 - i : i;
                int at = y * width + x;
                if (alpha[at] == 0) continue;
                float old = plane[at], q = Quantize(old, p.Levels);
                plane[at] = q;
                float error = (old - q) * p.Diffusion / divisor;
                for (int t = 0; t < count; t++)
                {
                    int nx = x + (reverse ? -taps[t].Dx : taps[t].Dx);
                    int ny = y + taps[t].Dy;
                    if (nx < 0 || nx >= width || ny >= height) continue;
                    plane[ny * width + nx] += error * taps[t].Weight;
                }
            }
        }
    }

    private static readonly byte[] Bayer8 =
    [
        0, 32, 8, 40, 2, 34, 10, 42, 48, 16, 56, 24, 50, 18, 58, 26,
        12, 44, 4, 36, 14, 46, 6, 38, 60, 28, 52, 20, 62, 30, 54, 22,
        3, 35, 11, 43, 1, 33, 9, 41, 51, 19, 59, 27, 49, 17, 57, 25,
        15, 47, 7, 39, 13, 45, 5, 37, 63, 31, 55, 23, 61, 29, 53, 21,
    ];

    // Smaller Bayer matrices are the top-left corners of the 8×8 one, rescaled.
    private static float OrderedThreshold(DitherStyle style, int x, int y)
    {
        switch (style)
        {
            case DitherStyle.Bayer2:
            {
                ReadOnlySpan<byte> m = [0, 2, 3, 1];
                return (m[(y & 1) * 2 + (x & 1)] + 0.5f) / 4;
            }
            case DitherStyle.Bayer4:
            {
                ReadOnlySpan<byte> m = [0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5];
                return (m[(y & 3) * 4 + (x & 3)] + 0.5f) / 16;
            }
            default:
                return (Bayer8[(y & 7) * 8 + (x & 7)] + 0.5f) / 64;
        }
    }

    private static float Ordered(float v, float threshold, int levels)
    {
        float steps = levels - 1;
        float q = MathF.Floor(Clamp01(v) * steps + threshold);
        return (q > steps ? steps : q) / steps;
    }

    // How much of a halftone cell a point must cover before it's marked, per screen shape.
    private static float Spot(DitherStyle style, float u, float v)
    {
        float au = MathF.Abs(u), av = MathF.Abs(v);
        return style switch
        {
            DitherStyle.HalftoneDots => MathF.PI * (u * u + v * v),
            DitherStyle.HalftoneLines => av * 2,
            _ => au + av,
        };
    }

    // Old Mac fill patterns, 8×8, leftmost pixel in the top bit, sparsest to fullest.
    private static readonly byte[][] Patterns =
    [
        [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
        [0x80, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00],
        [0x88, 0x00, 0x22, 0x00, 0x88, 0x00, 0x22, 0x00],
        [0x80, 0x40, 0x20, 0x10, 0x08, 0x04, 0x02, 0x01],
        [0x88, 0x22, 0x88, 0x22, 0x88, 0x22, 0x88, 0x22],
        [0x00, 0xFF, 0x00, 0x00, 0x00, 0xFF, 0x00, 0x00],
        [0x11, 0x22, 0x44, 0x88, 0x11, 0x22, 0x44, 0x88],
        [0xAA, 0x00, 0xAA, 0x00, 0xAA, 0x00, 0xAA, 0x00],
        [0x88, 0x55, 0x22, 0x55, 0x88, 0x55, 0x22, 0x55],
        [0xFF, 0x80, 0x80, 0x80, 0xFF, 0x08, 0x08, 0x08],
        [0xAA, 0x55, 0xAA, 0x55, 0xAA, 0x55, 0xAA, 0x55],
        [0x81, 0x42, 0x24, 0x18, 0x18, 0x24, 0x42, 0x81],
        [0x77, 0xAA, 0xDD, 0xAA, 0x77, 0xAA, 0xDD, 0xAA],
        [0xEE, 0xDD, 0xBB, 0x77, 0xEE, 0xDD, 0xBB, 0x77],
        [0x77, 0xFF, 0xDD, 0xFF, 0x77, 0xFF, 0xDD, 0xFF],
        [0x7F, 0xFF, 0xFF, 0xFF, 0xF7, 0xFF, 0xFF, 0xFF],
        [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF],
    ];

    private static void WritePixel(byte[] px, int index, float r, float g, float b)
    {
        float a = px[index + 3] / 255f;
        px[index] = (byte)MathF.Round(Clamp01(r) * a * 255f);
        px[index + 1] = (byte)MathF.Round(Clamp01(g) * a * 255f);
        px[index + 2] = (byte)MathF.Round(Clamp01(b) * a * 255f);
    }

    /// <summary>
    /// Keeps the processed copy only where the coverage says so, as a filter restricted to a
    /// selection: both surfaces are premultiplied, so a linear mix by coverage is source-over with
    /// the coverage as the alpha. A soft edge therefore stays soft instead of popping. The source is
    /// never written to; a fresh surface comes back.
    /// </summary>
    public static Surface ClipToCoverage(Surface source, Surface processed, MaskSurface coverage, CancellationToken cancellation = default)
    {
        if (source.Width != processed.Width || source.Height != processed.Height)
            throw new ArgumentException("The processed copy must match the source size.");
        var result = new Surface(source.Width, source.Height);
        for (int y = 0; y < source.Height; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            int row = y * source.Stride;
            for (int x = 0; x < source.Width; x++)
            {
                int index = row + x * 4;
                byte strength = x < coverage.Width && y < coverage.Height ? coverage.Pixels[y * coverage.Width + x] : (byte)0;
                if (strength == 0)
                {
                    for (int channel = 0; channel < 4; channel++) result.Pixels[index + channel] = source.Pixels[index + channel];
                    continue;
                }
                double over = strength / 255.0, keep = 1 - over;
                for (int channel = 0; channel < 4; channel++)
                {
                    result.Pixels[index + channel] = (byte)Math.Round(
                        processed.Pixels[index + channel] * over + source.Pixels[index + channel] * keep, MidpointRounding.AwayFromZero);
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Dithers premultiplied RGBA pixels in place; alpha is kept, transparent pixels left alone.
    /// Cancellation is checked row by row, so a cancelled call leaves the surface half-dithered
    /// and the caller has to discard it rather than publish it.
    /// </summary>
    public static void Apply(Surface surface, DitherParams p, CancellationToken cancellation = default)
    {
        int width = surface.Width, height = surface.Height;
        int count = width * height;
        if (count == 0) return;
        var rgba = surface.Pixels;
        int stride = surface.Stride;
        int planes = p.OriginalColors ? 3 : 1;
        var tone = new float[count * planes];
        var alpha = new byte[count];
        var source = p.OriginalColors ? new float[count * 3] : null;

        float gamma = MathF.Pow(2, p.Density * 1.5f);
        float contrast = p.Contrast >= 0 ? 1f / (1f - 0.95f * p.Contrast) : 1f + p.Contrast;
        for (int y = 0; y < height; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                int px = row + x * 4;
                int at = y * width + x;
                alpha[at] = rgba[px + 3];
                float r = 0, g = 0, b = 0;
                if (rgba[px + 3] != 0)
                {
                    float scale = 1f / rgba[px + 3];
                    r = rgba[px] * scale; g = rgba[px + 1] * scale; b = rgba[px + 2] * scale;
                }
                if (p.OriginalColors)
                {
                    tone[at] = AdjustTone(r, gamma, contrast);
                    tone[count + at] = AdjustTone(g, gamma, contrast);
                    tone[2 * count + at] = AdjustTone(b, gamma, contrast);
                    source![at * 3] = r; source[at * 3 + 1] = g; source[at * 3 + 2] = b;
                }
                else
                {
                    tone[at] = AdjustTone(0.2126f * r + 0.7152f * g + 0.0722f * b, gamma, contrast);
                }
            }
        }

        float dark0 = p.Dark.R / 255f, dark1 = p.Dark.G / 255f, dark2 = p.Dark.B / 255f;
        float light0 = p.Light.R / 255f, light1 = p.Light.G / 255f, light2 = p.Light.B / 255f;
        var style = p.Style;
        int levels = p.Levels < 2 ? 2 : p.Levels > 16 ? 16 : p.Levels;

        if (style is DitherStyle.Atkinson or DitherStyle.FloydSteinberg or DitherStyle.Bayer2 or DitherStyle.Bayer4 or DitherStyle.Bayer8)
        {
            if (style is DitherStyle.Atkinson or DitherStyle.FloydSteinberg)
            {
                for (int c = 0; c < planes; c++)
                    Diffuse(tone.AsSpan(c * count, count), alpha, width, height, p with { Levels = levels }, cancellation);
            }
            else
            {
                for (int c = 0; c < planes; c++)
                {
                    var plane = tone.AsSpan(c * count, count);
                    for (int y = 0; y < height; y++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        for (int x = 0; x < width; x++)
                        {
                            int at = y * width + x;
                            if (alpha[at] != 0) plane[at] = Ordered(plane[at], OrderedThreshold(style, x, y), levels);
                        }
                    }
                }
            }
            for (int y = 0; y < height; y++)
            {
                cancellation.ThrowIfCancellationRequested();
                int row = y * stride;
                for (int x = 0; x < width; x++)
                {
                    int at = y * width + x;
                    if (alpha[at] == 0) continue;
                    if (p.OriginalColors)
                    {
                        WritePixel(rgba, row + x * 4, tone[at], tone[count + at], tone[2 * count + at]);
                    }
                    else
                    {
                        float t = tone[at];
                        WritePixel(rgba, row + x * 4, dark0 + (light0 - dark0) * t, dark1 + (light1 - dark1) * t,
                            dark2 + (light2 - dark2) * t);
                    }
                }
            }
        }
        else if (style == DitherStyle.Scanlines)
        {
            // A CRT: each line scans the image; the beam glows where the picture is light and
            // the screen between lines stays dark. The spacing is the scanline cell, which the
            // macOS sheet keeps separate from the halftone cell size.
            int requested = ScanlineSpacing(p);
            int spacing = requested < 2 ? 2 : requested;
            float middle = spacing / 2f, dots = Clamp01(p.Dots);
            float[] screen = [dark0, dark1, dark2];
            float[] phosphor = [light0, light1, light2];
            var scan = new float[width * planes];
            for (int line = 0; line * spacing < height; line++)
            {
                cancellation.ThrowIfCancellationRequested();
                int top = line * spacing;
                int bottom = Math.Min(top + spacing, height);
                // Wobble: a slow wave down the screen with a quicker one over it.
                float wave = MathF.Sin(line * 0.45f) * 0.7f + MathF.Sin(line * 1.7f + 1.3f) * 0.3f;
                int shift = (int)MathF.Round(p.Wobble * wave);
                for (int x = 0; x < width; x++)
                {
                    float s0 = 0, s1 = 0, s2 = 0;
                    int n = 0;
                    int sx = x - shift;
                    if (sx >= 0 && sx < width)
                    {
                        for (int y = top; y < bottom; y++)
                        {
                            int at = y * width + sx;
                            if (alpha[at] == 0) continue;
                            s0 += tone[at];
                            if (planes > 1) s1 += tone[count + at];
                            if (planes > 2) s2 += tone[2 * count + at];
                            n++;
                        }
                    }
                    scan[x] = n > 0 ? s0 / n : 0;
                    if (planes > 1) scan[width + x] = n > 0 ? s1 / n : 0;
                    if (planes > 2) scan[2 * width + x] = n > 0 ? s2 / n : 0;
                }
                for (int y = top; y < bottom; y++)
                {
                    int row = y * stride;
                    float offset = MathF.Abs(y - top + 0.5f - middle);
                    for (int x = 0; x < width; x++)
                    {
                        if (alpha[y * width + x] == 0) continue;
                        float along = (x + 0.5f) % spacing - middle;
                        int centered = (int)MathF.Round(x - along * dots);
                        int at = Math.Clamp(centered, 0, width - 1);
                        float r, g, b, t;
                        if (p.OriginalColors)
                        {
                            r = scan[at]; g = scan[width + at]; b = scan[2 * width + at];
                            t = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                        }
                        else
                        {
                            t = scan[at];
                            r = screen[0] + (phosphor[0] - screen[0]) * t;
                            g = screen[1] + (phosphor[1] - screen[1]) * t;
                            b = screen[2] + (phosphor[2] - screen[2]) * t;
                        }
                        // The beam is driven brighter than the picture, making up for the dark screen.
                        r *= 1.35f; g *= 1.35f; b *= 1.35f;
                        float beam = middle * (0.2f + 0.5f * MathF.Sqrt(Clamp01(t)));
                        float across = along * dots, distance = MathF.Sqrt(offset * offset + across * across);
                        float cover = Clamp01(beam - distance + 0.5f);
                        float br = p.OriginalColors ? 0 : screen[0], bg = p.OriginalColors ? 0 : screen[1];
                        float bb = p.OriginalColors ? 0 : screen[2];
                        WritePixel(rgba, row + x * 4, br + (r - br) * cover, bg + (g - bg) * cover, bb + (b - bb) * cover);
                    }
                }
            }
        }
        else
        {
            // Marks (halftone shapes, patterns, glyphs) cover as much of each spot as the
            // tone calls for. On light they stand for darkness; light on dark, the reverse.
            var marks = p.OriginalColors ? new float[count] : tone;
            if (p.OriginalColors)
            {
                for (int i = 0; i < count; i++)
                {
                    if ((i & 0xFFFF) == 0) cancellation.ThrowIfCancellationRequested();
                    marks[i] = 0.2126f * tone[i] + 0.7152f * tone[count + i] + 0.0722f * tone[2 * count + i];
                }
            }
            int cell = p.Cell < 2 ? 2 : p.Cell;
            float cosA = MathF.Cos(p.Angle), sinA = MathF.Sin(p.Angle);
            var ink = p.LightOnDark ? (light0, light1, light2) : (dark0, dark1, dark2);
            var paper = p.LightOnDark ? (dark0, dark1, dark2) : (light0, light1, light2);
            int gw = Math.Max(1, p.GlyphWidth), gh = Math.Max(1, p.GlyphHeight);
            int columns = (width + gw - 1) / gw, cellRows = (height + gh - 1) / gh;
            int[]? picked = null;
            // Glyph data comes from the caller: an incomplete GlyphMaps (shorter than
            // coverage × glyph size) would index out of bounds below, so a mismatched set
            // falls back to the non-Ascii mark shapes instead of throwing.
            bool asciiUsable = style == DitherStyle.Ascii && p.GlyphCoverage.Length > 0
                && p.GlyphMaps.Length >= p.GlyphCoverage.Length * gw * gh;
            if (asciiUsable)
            {
                picked = new int[columns * cellRows];
                for (int cellRow = 0; cellRow < cellRows; cellRow++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    for (int column = 0; column < columns; column++)
                    {
                        float sum = 0;
                        int n = 0;
                        for (int yy = cellRow * gh; yy < (cellRow + 1) * gh && yy < height; yy++)
                        {
                            for (int xx = column * gw; xx < (column + 1) * gw && xx < width; xx++)
                            {
                                int i = yy * width + xx;
                                if (alpha[i] != 0) { sum += marks[i]; n++; }
                            }
                        }
                        float t = n > 0 ? sum / n : 1;
                        float wanted = (p.LightOnDark ? t : 1 - t) * p.GlyphCoverage[p.GlyphCoverage.Length - 1];
                        int best = 0;
                        float bestDistance = 2;
                        for (int g = 0; g < p.GlyphCoverage.Length; g++)
                        {
                            float d = MathF.Abs(p.GlyphCoverage[g] - wanted);
                            if (d < bestDistance) { bestDistance = d; best = g; }
                        }
                        picked[cellRow * columns + column] = best;
                    }
                }
            }
            float paperOriginal = p.LightOnDark ? 0f : 1f;
            for (int y = 0; y < height; y++)
            {
                cancellation.ThrowIfCancellationRequested();
                int row = y * stride;
                for (int x = 0; x < width; x++)
                {
                    int at = y * width + x;
                    if (alpha[at] == 0) continue;
                    float amount;
                    if (picked is not null)
                    {
                        int glyph = picked[(y / gh) * columns + x / gw];
                        amount = p.GlyphMaps[glyph * gw * gh + y % gh * gw + x % gw] / 255f;
                    }
                    else if (style == DitherStyle.MacPatterns)
                    {
                        float t = marks[at];
                        float coverage = p.LightOnDark ? t : 1 - t;
                        int index = (int)MathF.Round(coverage * (Patterns.Length - 1));
                        amount = (Patterns[index][y & 7] >> (7 - (x & 7))) & 1;
                    }
                    else
                    {
                        float fx = x + 0.5f, fy = y + 0.5f;
                        float u = (fx * cosA + fy * sinA) / cell, v = (-fx * sinA + fy * cosA) / cell;
                        u -= MathF.Floor(u) + 0.5f; v -= MathF.Floor(v) + 0.5f;
                        float t = marks[at];
                        amount = (p.LightOnDark ? t : 1 - t) > Spot(style, u, v) ? 1 : 0;
                    }
                    if (p.OriginalColors)
                    {
                        int s = at * 3;
                        WritePixel(rgba, row + x * 4, paperOriginal + (source![s] - paperOriginal) * amount,
                            paperOriginal + (source[s + 1] - paperOriginal) * amount,
                            paperOriginal + (source[s + 2] - paperOriginal) * amount);
                    }
                    else
                    {
                        WritePixel(rgba, row + x * 4, paper.Item1 + (ink.Item1 - paper.Item1) * amount,
                            paper.Item2 + (ink.Item2 - paper.Item2) * amount,
                            paper.Item3 + (ink.Item3 - paper.Item3) * amount);
                    }
                }
            }
        }
    }

    /// <summary>Turns each block×block square into a round dot in its color on `gap` (dither_dots).</summary>
    public static void Dots(Surface surface, int block, (byte R, byte G, byte B) gap)
    {
        if (block < 2) return;
        var rgba = surface.Pixels;
        int stride = surface.Stride;
        float radius = block * 0.42f, middle = block / 2f;
        for (int y = 0; y < surface.Height; y++)
        {
            int row = y * stride;
            float dy = y % block + 0.5f - middle;
            for (int x = 0; x < surface.Width; x++)
            {
                int px = row + x * 4;
                if (rgba[px + 3] == 0) continue;
                float dx = x % block + 0.5f - middle;
                float cover = Clamp01(radius - MathF.Sqrt(dx * dx + dy * dy) + 0.5f);
                if (cover >= 1) continue;
                float source = rgba[px + 3] / 255f * (1 - cover);
                rgba[px] = (byte)MathF.Round(rgba[px] * cover + gap.R * source);
                rgba[px + 1] = (byte)MathF.Round(rgba[px + 1] * cover + gap.G * source);
                rgba[px + 2] = (byte)MathF.Round(rgba[px + 2] * cover + gap.B * source);
            }
        }
    }

    /// <summary>Adds `glow` over the pixels at `amount`, never past their own alpha (dither_glow).</summary>
    public static void Glow(Surface surface, Surface glow, float amount)
    {
        var rgba = surface.Pixels;
        var light = glow.Pixels;
        for (int y = 0; y < surface.Height; y++)
        {
            int row = y * surface.Stride;
            for (int x = 0; x < surface.Width * 4; x += 4)
            {
                float a = rgba[row + x + 3];
                for (int c = 0; c < 3; c++)
                {
                    float v = rgba[row + x + c] + light[row + x + c] * amount * a / 255f;
                    rgba[row + x + c] = (byte)MathF.Round(v > a ? a : v);
                }
            }
        }
    }

    /// <summary>The row period the scanlines were drawn at: the sheet's line spacing, or the halftone
    /// cell when the caller left it unset (Dither.swift:170). The bloom reaches by this same number,
    /// which is why the two passes must not each pick their own (<see cref="Bloom"/>).</summary>
    public static int ScanlineSpacing(DitherParams p) => p.LineSpacing >= 2 ? p.LineSpacing : p.Cell;

    /// <summary>
    /// The lines' light spread wide and soft, ready to be added back over them as a CRT's phosphor bloom
    /// (Dither.swift:146-163). The reach is three line spacings plus a little; because that is far wider
    /// than a pixel, the copy is averaged down first, blurred at one kernel over the whole plane — which
    /// is what Core Image's clamped-to-extent plane does at the edges, so the glow does not darken them —
    /// and blown back up with smooth sampling. The input is never written to.
    /// </summary>
    public static Surface Bloom(Surface dithered, int lineSpacing, CancellationToken cancellation = default)
    {
        double sigma = Math.Max(1, lineSpacing) * 3 + 3;
        int shrink = Math.Max(1, (int)Math.Floor(sigma / 4));
        var small = AverageDown(dithered, shrink, cancellation);
        AdjustmentEngine.ApplyGaussianBlur(sigma / shrink, small, 0, 0, small.Width, small.Height);
        if (shrink == 1) return small;
        var full = new Surface(dithered.Width, dithered.Height);
        Resampler.Draw(small, new LayerTransform { Origin = PointD.Zero, Size = new SizeD(full.Width, full.Height) },
            full, 0, 0, full.Width, full.Height, LayerSampling.Smooth);
        return full;
    }

    /// <summary>
    /// Averages every `block`×`block` square down to one pixel, keeping the premultiplied mean of color
    /// and alpha alike, and a short final row or column averages only what it holds. This is the chunky
    /// pixel pass' shrink and <see cref="Bloom"/>'s shrink in one place, so the two cannot drift apart.
    /// </summary>
    public static Surface AverageDown(Surface source, int block, CancellationToken cancellation = default)
    {
        int width = (source.Width + block - 1) / block, height = (source.Height + block - 1) / block;
        var small = new Surface(width, height);
        for (int y = 0; y < height; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                long r = 0, g = 0, b = 0, a = 0, n = 0;
                for (int dy = 0; dy < block && y * block + dy < source.Height; dy++)
                    for (int dx = 0; dx < block && x * block + dx < source.Width; dx++)
                    {
                        int index = (y * block + dy) * source.Stride + (x * block + dx) * 4;
                        r += source.Pixels[index]; g += source.Pixels[index + 1]; b += source.Pixels[index + 2]; a += source.Pixels[index + 3]; n++;
                    }
                int at = y * small.Stride + x * 4;
                small.Pixels[at] = (byte)(r / n); small.Pixels[at + 1] = (byte)(g / n);
                small.Pixels[at + 2] = (byte)(b / n); small.Pixels[at + 3] = (byte)(a / n);
            }
        }
        return small;
    }
}
