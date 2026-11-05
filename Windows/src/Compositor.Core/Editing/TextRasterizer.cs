using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Compositor.Core;

/// <summary>
/// Rasterizes a text layer's style into a premultiplied RGBA Surface, the Windows counterpart of the
/// macOS EditorSession.textImage in TypeTool.swift: what CoreText and NSLayoutManager lay out and
/// paint there, GDI+ does here — glyphs shaped onto a GraphicsPath, measured with a typographic
/// StringFormat. The Mac's rules are kept:
/// - every line box is LineHeight tall (Auto leading is 120% of the font size) and its baseline sits
///   one descent above the box's bottom, so extra leading gathers above the letters, as Photoshop's does;
/// - the paragraph is inset by LayerTextStyle.Padding on all sides and laid out within that box, as
///   NSTextContainer bounds it: lines that do not fit the box are dropped, the way NSLayoutManager
///   lays out only the glyphs that fit, while ink that overhangs the box (a fixed box's long word,
///   the ascenders of tightly led lines) is still painted, as AppKit's fragments are;
/// - point text (no BoxSize) is as wide as what it measures plus the padding and a caret's width,
///   and as tall as its lines; box text wraps by words into the box's inner width;
/// - alignment and the per-letter color and face runs (UTF-16 offsets into Content, as saved in the
///   manifest) are honored.
/// Differences from the Mac this port does not promise to match pixel for pixel: GDI+ shapes text
/// without kerning pairs, so Tracking is laid out as a per-cluster advance instead of CoreText's
/// kern; a face that resolves on the Mac but not on Windows (Helvetica) falls back to the generic
/// sans-serif rather than the Mac's system font; and antialiasing is path-fill coverage rather than
/// CoreGraphics' gamma-corrected gray coverage.
/// </summary>
public static class TextRasterizer
{
    /// <summary>
    /// How big the text is: its fixed box, or what a point-text style measures plus its padding — the
    /// same shape as the Mac's textBoxSize, which sizes the layer the caret is typed into.
    /// </summary>
    public static SizeD TextBoxSize(LayerTextStyle style)
    {
        if (!style.IsValid) throw new ProjectFormatException(ProjectErrorKind.Invalid);
        if (style.BoxSize is { } box) return new SizeD(box.Width, box.Height);
        using var paragraph = new Paragraph(style);
        var lines = paragraph.Layout(double.PositiveInfinity);
        double widest = 0;
        foreach (var line in lines) widest = Math.Max(widest, paragraph.VisibleWidth(line.Start, line.Start + line.Length));
        // A caret's worth of width, as the Mac adds fontSize * 0.1, so an empty line has somewhere to type.
        double width = Math.Max(16, Math.Ceiling(widest + 2 * LayerTextStyle.Padding + style.FontSize * 0.1));
        double height = Math.Max(16, Math.Ceiling(Math.Max(lines.Count, 1) * style.LineHeight + 2 * LayerTextStyle.Padding));
        return new SizeD(width, height);
    }

    /// <summary>
    /// How far the style's first baseline sits below its layer's top edge: the padding plus one
    /// line box, minus the base face's descent — the Mac's `beginText(at:)` origin math, so a new
    /// point-text layer can be placed with its first baseline on the pointer.
    /// </summary>
    public static double FirstBaseline(LayerTextStyle style)
    {
        if (!style.IsValid) throw new ProjectFormatException(ProjectErrorKind.Invalid);
        using var paragraph = new Paragraph(style);
        return LayerTextStyle.Padding + style.LineHeight - paragraph.BaseDescent;
    }

    /// <summary>
    /// How many of the laid-out lines a fixed box can actually draw — the rule <see cref="Rasterize"/> paints by,
    /// kept here so that asking "does this overflow?" cannot answer differently from what gets drawn. Whole line
    /// fragments only, as NSLayoutManager lays them out: a line whose box does not fit is never laid.
    /// </summary>
    private static int FittingLines(int height, int laidOut, double lineHeight)
    {
        float inner = Math.Max(1f, height - 2 * (float)LayerTextStyle.Padding);
        int fitting = 0;
        while (fitting < laidOut && (fitting + 1) * (float)lineHeight <= inner + 0.01f) fitting++;
        return fitting;
    }

    /// <summary>
    /// Whether the style's own paragraph box cannot hold what it says. The macOS editor marks that case with a
    /// plus drawn in the bottom-right handle (<c>InlineTextEditor.swift:420-433</c>: the glyph range the layout
    /// manager covered stops short of the glyphs there are), and point text never has it — a point-text layer is
    /// as tall as its lines, so there is nothing to overflow. Throws for a style or box the document would refuse.
    /// </summary>
    public static bool TextOverflows(LayerTextStyle style)
    {
        if (!style.IsValid) throw new ProjectFormatException(ProjectErrorKind.Invalid);
        if (style.BoxSize is null) return false;
        var box = TextBoxSize(style);
        int width = (int)Math.Ceiling(box.Width), height = (int)Math.Ceiling(box.Height);
        if (width < 1 || height < 1 || width > Limits.MaxSide || height > Limits.MaxSide
            || (long)width * height > Limits.MaxSurfacePixels) throw new ProjectFormatException(ProjectErrorKind.TooLarge);
        using var paragraph = new Paragraph(style);
        int laidOut = paragraph.Layout(Math.Max(1f, width - 2 * (float)LayerTextStyle.Padding)).Count;
        return FittingLines(height, laidOut, style.LineHeight) < laidOut;
    }

    /// <summary>Whether DirectWrite knows this exact face name — the question the Mac's
    /// missingFontNote asks NSFont. The face-suffix fallback does not count: the note is about
    /// the named font being absent, not about what the rasterizer can substitute for it.</summary>
    public static bool FontInstalled(string faceName)
    {
        try { using var _ = new FontFamily(faceName.Trim()); return true; }
        catch (ArgumentException) { return false; } // not installed
    }

    /// <summary>The layer's pixels: the style laid out and painted, premultiplied. The Mac's textImage.</summary>
    public static Surface Rasterize(LayerTextStyle style)
    {
        if (!style.IsValid) throw new ProjectFormatException(ProjectErrorKind.Invalid);
        var box = TextBoxSize(style);
        int width = (int)Math.Ceiling(box.Width), height = (int)Math.Ceiling(box.Height);
        if (width < 1 || height < 1 || width > Limits.MaxSide || height > Limits.MaxSide
            || (long)width * height > Limits.MaxSurfacePixels)
            throw new ProjectFormatException(ProjectErrorKind.TooLarge);
        var surface = new Surface(width, height);
        float padding = (float)LayerTextStyle.Padding;
        float innerWidth = Math.Max(1f, width - 2 * padding);
        using var paragraph = new Paragraph(style);
        var lines = paragraph.Layout(style.BoxSize is null ? double.PositiveInfinity : innerWidth);
        // NSLayoutManager lays out whole line fragments only, so lines whose box does not fit the
        // NSTextContainer are never laid out, never drawn — the floor of glyphRange(for:). Ink that
        // overhangs a fitting line is another matter and stays painted, unclipped: a fixed box's long
        // word, the ascenders of tightly led lines, all draw past the padding as AppKit's fragments do.
        int fitting = FittingLines(height, lines.Count, style.LineHeight);
        if (fitting < lines.Count) lines.RemoveRange(fitting, lines.Count - fitting);
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.PageUnit = GraphicsUnit.Pixel;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            paragraph.Paint(graphics, lines, padding, innerWidth);
        }
        CopyPremultiplied(bitmap, surface);
        return surface;
    }

    /// <summary>
    /// GDI+ hands back straight-alpha BGRA, top row first; the port's currency is premultiplied RGBA,
    /// so each row is reordered and its color channels scaled by the coverage alpha.
    /// </summary>
    private static void CopyPremultiplied(Bitmap bitmap, Surface surface)
    {
        var bounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var bits = bitmap.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[bitmap.Width * 4];
            for (int y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(bits.Scan0, y * bits.Stride), row, 0, row.Length);
                int target = y * surface.Stride;
                for (int x = 0; x < bitmap.Width; x++)
                {
                    byte alpha = row[x * 4 + 3];
                    surface.Pixels[target + x * 4] = Premultiply(row[x * 4 + 2], alpha);
                    surface.Pixels[target + x * 4 + 1] = Premultiply(row[x * 4 + 1], alpha);
                    surface.Pixels[target + x * 4 + 2] = Premultiply(row[x * 4], alpha);
                    surface.Pixels[target + x * 4 + 3] = alpha;
                }
            }
        }
        finally { bitmap.UnlockBits(bits); }
    }

    /// <summary>Rounded to nearest, the same conversion ShapeRasterizer paints its coverage with.</summary>
    private static byte Premultiply(byte channel, byte alpha) => (byte)Math.Round(channel * alpha / 255.0);

    private readonly record struct Segment(int Start, int Length, Face Face, Color Color);

    private readonly record struct VisualLine(int Start, int Length);

    /// <summary>A face the style names, resolved to a family GDI+ can set text in, with its cell metrics.</summary>
    private sealed class Face : IDisposable
    {
        public required FontFamily Family { get; init; }
        /// <summary>The FontStyle as an int, which is what GraphicsPath.AddString takes.</summary>
        public required int Style { get; init; }
        public required Font Font { get; init; }
        /// <summary>Cell ascent in pixels — AddString's origin is the cell's top, its baseline one ascent down.</summary>
        public required float Ascent { get; init; }
        public required float Descent { get; init; }
        public required bool OwnsFamily { get; init; }

        public void Dispose()
        {
            Font.Dispose();
            if (OwnsFamily) Family.Dispose();
        }
    }

    /// <summary>
    /// One style prepared for measuring and painting: its content folded to plain \n lines (runs stay
    /// addressable through the saved source offsets), a scratch Graphics to measure on, and the faces
    /// its letters need, resolved once.
    /// </summary>
    private sealed class Paragraph : IDisposable
    {
        private readonly LayerTextStyle _style;
        private readonly Bitmap _scratch;
        private readonly Graphics _measure;
        private readonly StringFormat _format = CreateFormat();
        private readonly Dictionary<string, Face> _faces = new();
        private readonly Dictionary<Font, float> _sentinels = new();
        private readonly float _size;
        private readonly double _tracking;
        private readonly string _text;
        private readonly int[] _source;
        private readonly Face[]? _unitFaces;
        private readonly Color[] _unitColors;
        private readonly Face _base;

        /// <summary>The style's own face's descent, the baseline placement uses.</summary>
        public float BaseDescent => _base.Descent;

        public Paragraph(LayerTextStyle style)
        {
            _style = style;
            _size = (float)style.FontSize;
            _tracking = style.Tracking;
            (_text, _source) = Normalize(style.Content);
            _base = ResolveFace(style.FontName);
            _unitFaces = BuildUnitFaces();
            _unitColors = BuildUnitColors();
            _scratch = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
            _measure = Graphics.FromImage(_scratch);
            _measure.PageUnit = GraphicsUnit.Pixel;
        }

        public void Dispose()
        {
            _measure.Dispose();
            _scratch.Dispose();
            _format.Dispose();
            foreach (var face in _faces.Values) face.Dispose();
        }

        /// <summary>The typographic layout GDI+ has: no cell padding, no trimming, overhangs free to
        /// poke outside the black box — the equivalent of the Mac's lineFragmentPadding of 0. The flags
        /// match StringFormat.GenericTypographic's own.</summary>
        private static StringFormat CreateFormat() => new()
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Near,
            Trimming = StringTrimming.None,
            FormatFlags = StringFormatFlags.NoClip | StringFormatFlags.FitBlackBox | StringFormatFlags.LineLimit,
        };

        /// <summary>Every separator Cocoa's typesetter ends a line at; each becomes the one break (a
        /// plain \n) the layout works in — the same set LayerTextFontRun forbids in a face's name.</summary>
        private static bool IsLineBreak(char c) =>
            c == '\r' || c == '\n' || c == '\u2028' || c == '\u2029' || c == '\u0085' || c == '\u000B' || c == '\u000C';

        /// <summary>The whitespace a line may break after: .NET's whitespace minus the non-breaking
        /// spaces, which glue their word to the next one as they do on the Mac.</summary>
        private static bool IsWrapSpace(char c) =>
            char.IsWhiteSpace(c) && c is not '\u00A0' and not '\u2007' and not '\u202F';

        /// <summary>Line separators — CRLF as one, and the Unicode breaks Cocoa breaks at — become the
        /// one line break the layout works in, keeping the original UTF-16 offset of every surviving
        /// unit so runs stay where the manifest put them.</summary>
        private static (string Text, int[] Source) Normalize(string content)
        {
            var text = new StringBuilder(content.Length);
            var source = new List<int>(content.Length);
            for (int i = 0; i < content.Length; i++)
            {
                char c = content[i];
                text.Append(IsLineBreak(c) ? '\n' : c);
                source.Add(i);
                // The LF of a CRLF pair is folded into the break its CR already added.
                if (c == '\r' && i + 1 < content.Length && content[i + 1] == '\n') i++;
            }
            return (text.ToString(), source.ToArray());
        }

        private Face ResolveFace(string name)
        {
            if (_faces.TryGetValue(name, out var cached)) return cached;
            var (family, style, owned) = ResolveFamily(name);
            var face = new Face
            {
                Family = family,
                Style = style,
                Font = new Font(family, _size, (FontStyle)style, GraphicsUnit.Pixel),
                Ascent = family.GetCellAscent((FontStyle)style) * _size / family.GetEmHeight((FontStyle)style),
                Descent = family.GetCellDescent((FontStyle)style) * _size / family.GetEmHeight((FontStyle)style),
                OwnsFamily = owned,
            };
            _faces[name] = face;
            return face;
        }

        /// <summary>
        /// A whole face name first, as font menus save it; when the whole name is not installed, the
        /// style suffixes PostScript-style names carry ("Arial-Bold") are peeled into GDI+'s FontStyle.
        /// Whatever still doesn't resolve — a Mac face this machine lacks, or plain garbage — draws in
        /// the generic sans-serif instead of throwing.
        /// </summary>
        private static (FontFamily Family, int Style, bool Owned) ResolveFamily(string faceName)
        {
            string name = faceName.Trim();
            if (TryFamily(name, out var family))
                return (family, BestStyle(family, FontStyle.Regular), true);
            foreach (var (suffix, style) in FaceSuffixes)
            {
                if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                string stripped = name[..^suffix.Length].TrimEnd('-', ' ');
                if (stripped.Length > 0 && TryFamily(stripped, out family))
                    return (family, BestStyle(family, style), true);
            }
            return (FontFamily.GenericSansSerif, (int)FontStyle.Regular, false);
        }

        private static readonly (string Suffix, FontStyle Style)[] FaceSuffixes =
        [
            ("-BoldItalic", FontStyle.Bold | FontStyle.Italic),
            ("-Bold Italic", FontStyle.Bold | FontStyle.Italic),
            (" Bold Italic", FontStyle.Bold | FontStyle.Italic),
            (" BoldItalic", FontStyle.Bold | FontStyle.Italic),
            ("-BoldOblique", FontStyle.Bold | FontStyle.Italic),
            ("-Bold", FontStyle.Bold),
            (" Bold", FontStyle.Bold),
            ("-Italic", FontStyle.Italic),
            (" Italic", FontStyle.Italic),
            ("-Oblique", FontStyle.Italic),
            (" Oblique", FontStyle.Italic),
            ("-Regular", FontStyle.Regular),
            (" Regular", FontStyle.Regular),
        ];

        private static bool TryFamily(string name, out FontFamily family)
        {
            try { family = new FontFamily(name); }
            catch (ArgumentException) { family = null!; return false; } // not installed — the caller falls back
            return true;
        }

        private static int BestStyle(FontFamily family, FontStyle wanted)
        {
            // A family without the wanted face is set in what it has, as GDI+ would simulate it anyway.
            for (var style = wanted; style != 0; style = (FontStyle)((int)style >> 1))
                if (family.IsStyleAvailable(style)) return (int)style;
            return (int)FontStyle.Regular;
        }

        /// <summary>One face per unit, the style's own except where a font run covers it, as the Mac's
        /// unitFonts does. Null when there are no runs, the common case.</summary>
        private Face[]? BuildUnitFaces()
        {
            if (_style.FontRuns is not { Count: > 0 } runs) return null;
            var faces = new Face[_text.Length];
            int run = 0;
            for (int unit = 0; unit < faces.Length; unit++)
            {
                int source = _source[unit];
                while (run < runs.Count && runs[run].Location + runs[run].Length <= source) run++;
                faces[unit] = run < runs.Count && runs[run].Location <= source
                    ? ResolveFace(runs[run].FontName) : _base;
            }
            return faces;
        }

        /// <summary>One color per unit, the style's own except where a color run covers it.</summary>
        private Color[] BuildUnitColors()
        {
            var colors = new Color[_text.Length];
            Color whole = Color.FromArgb(255, Channel(_style.Red), Channel(_style.Green), Channel(_style.Blue));
            for (int unit = 0; unit < colors.Length; unit++) colors[unit] = whole;
            if (_style.ColorRuns is { Count: > 0 } runs)
            {
                int run = 0;
                for (int unit = 0; unit < colors.Length; unit++)
                {
                    int source = _source[unit];
                    while (run < runs.Count && runs[run].Location + runs[run].Length <= source) run++;
                    if (run < runs.Count && runs[run].Location <= source)
                        colors[unit] = Color.FromArgb(255, Channel(runs[run].Red), Channel(runs[run].Green), Channel(runs[run].Blue));
                }
            }
            return colors;
        }

        private static byte Channel(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);

        /// <summary>The visual lines: hard lines, each wrapped to maxWidth unless it is infinite (point
        /// text never wraps). A word wider than the line is left to overflow into the clip rather than
        /// be split, as byWordWrapping leaves it.</summary>
        public List<VisualLine> Layout(double maxWidth)
        {
            var lines = new List<VisualLine>();
            int index = 0;
            while (index <= _text.Length)
            {
                int lineBreak = _text.IndexOf('\n', index);
                int hardEnd = lineBreak < 0 ? _text.Length : lineBreak;
                lines.AddRange(WrapLine(index, hardEnd, maxWidth));
                if (lineBreak < 0) break;
                index = lineBreak + 1;
            }
            return lines;
        }

        private List<VisualLine> WrapLine(int start, int end, double maxWidth)
        {
            if (start == end) return [new VisualLine(start, 0)];
            if (double.IsPositiveInfinity(maxWidth)) return [new VisualLine(start, end - start)];
            var lines = new List<VisualLine>();
            int lineStart = start, lineEnd = start;
            double linePen = 0, lineTail = 0;
            int index = start;
            while (index < end)
            {
                // A word and the whitespace after it: the whitespace rides with the word, and wrapping
                // consumes it, since a line is measured without its trailing spaces anyway.
                int wordStart = index;
                while (index < end && !IsWrapSpace(_text[index])) index++;
                while (index < end && IsWrapSpace(_text[index])) index++;
                double tokenPen = PenAdvance(wordStart, index);
                double tokenTail = tokenPen - VisibleWidth(wordStart, index);
                // The fit is judged on what the eye sees — the line without its trailing whitespace,
                // the token without the whitespace it brings — as TextKit's breaker measures a line.
                if (linePen > 0 && linePen - lineTail + tokenPen - tokenTail > maxWidth)
                {
                    lines.Add(new VisualLine(lineStart, lineEnd - lineStart));
                    lineStart = wordStart;
                    linePen = tokenPen;
                }
                else linePen += tokenPen;
                lineTail = tokenTail;
                lineEnd = index;
            }
            lines.Add(new VisualLine(lineStart, lineEnd - lineStart));
            return lines;
        }

        /// <summary>What the range advances the pen across, tracking included, minus what the eye
        /// doesn't see: the trailing whitespace and the kern after the last cluster.</summary>
        public double VisibleWidth(int start, int end) => PenAdvance(start, end) - TrailingInvisible(start, end);

        private double PenAdvance(int start, int end)
        {
            double total = 0;
            foreach (var segment in BuildSegments(start, end)) total += PenAdvance(segment);
            return total;
        }

        private double TrailingInvisible(int start, int end)
        {
            var segments = BuildSegments(start, end);
            return segments.Length == 0 ? 0 : TrailingInvisible(segments[^1]);
        }

        /// <summary>What the segment advances the pen across: its measured width, plus tracking after
        /// every cluster when the style tracks, since GDI+ has no kern to add it to.</summary>
        private double PenAdvance(Segment segment)
        {
            string text = _text.Substring(segment.Start, segment.Length);
            if (text.Length == 0) return 0;
            if (_tracking == 0) return Advance(text, segment.Face.Font);
            double total = 0;
            var clusters = StringInfo.GetTextElementEnumerator(text);
            while (clusters.MoveNext())
                total += Advance((string)clusters.Current, segment.Face.Font) + _tracking;
            return total;
        }

        private double TrailingInvisible(Segment segment)
        {
            string text = _text.Substring(segment.Start, segment.Length);
            if (text.Length == 0) return 0;
            int end = text.Length;
            while (end > 0 && char.IsWhiteSpace(text[end - 1])) end--;
            double hidden = 0;
            if (end < text.Length)
            {
                string tail = text[end..];
                if (_tracking == 0) hidden = Advance(tail, segment.Face.Font);
                else
                {
                    var clusters = StringInfo.GetTextElementEnumerator(tail);
                    while (clusters.MoveNext())
                        hidden += Advance((string)clusters.Current, segment.Face.Font) + _tracking;
                }
            }
            return hidden + _tracking;
        }

        /// <summary>GDI+'s typographic measure skips trailing spaces, so a harmless letter is appended
        /// and its own width subtracted to get the whole advance — what moves the pen to the next run.</summary>
        private double Advance(string text, Font font)
        {
            const string sentinel = "n";
            if (!_sentinels.TryGetValue(font, out float own))
            {
                own = _measure.MeasureString(sentinel, font, PointF.Empty, _format).Width;
                _sentinels[font] = own;
            }
            return _measure.MeasureString(text + sentinel, font, PointF.Empty, _format).Width - own;
        }

        private Segment[] BuildSegments(int start, int end)
        {
            var segments = new List<Segment>();
            int index = start;
            while (index < end)
            {
                var face = _unitFaces is { } faces ? faces[index] : _base;
                var color = _unitColors[index];
                int next = index + 1;
                while (next < end && (_unitFaces is { } f ? f[next] : _base) == face && _unitColors[next] == color) next++;
                segments.Add(new Segment(index, next - index, face, color));
                index = next;
            }
            return segments.ToArray();
        }

        public void Paint(Graphics target, List<VisualLine> lines, float padding, float innerWidth)
        {
            float lineHeight = (float)_style.LineHeight;
            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (line.Length == 0) continue;
                var segments = BuildSegments(line.Start, line.Start + line.Length);
                double width = 0;
                for (int s = 0; s < segments.Length; s++)
                    width += PenAdvance(segments[s]) - (s == segments.Length - 1 ? TrailingInvisible(segments[s]) : 0);
                // A whitespace-only line can measure below zero once its trailing tracking leaves it;
                // such a line is set from its padding, not from a negative width.
                width = Math.Max(0, width);
                double startX = _style.Alignment switch
                {
                    TextAlignment.Center => padding + (innerWidth - width) / 2,
                    TextAlignment.Right => padding + innerWidth - width,
                    _ => padding,
                };
                // The baseline sits one descent above the line box's bottom, so extra leading gathers
                // above the letters. Every run in the line shares that baseline, each AddString origin
                // one of its own face's ascent above it, as the layout manager sets a line on one baseline.
                float baseline = padding + (i + 1) * lineHeight - _base.Descent;
                double pen = startX;
                int index = 0;
                while (index < segments.Length)
                {
                    // One path per run of one color keeps antialiased edges from doubling at segment seams.
                    int end = index + 1;
                    Color color = segments[index].Color;
                    while (end < segments.Length && segments[end].Color == color) end++;
                    using var path = new GraphicsPath();
                    for (int s = index; s < end; s++) PaintSegment(path, segments[s], ref pen, baseline);
                    using var brush = new SolidBrush(color);
                    target.FillPath(brush, path);
                    index = end;
                }
            }
        }

        private void PaintSegment(GraphicsPath path, Segment segment, ref double pen, float baseline)
        {
            string text = _text.Substring(segment.Start, segment.Length);
            if (text.Length == 0) return;
            var face = segment.Face;
            if (_tracking == 0)
            {
                path.AddString(text, face.Family, face.Style, _size,
                    new PointF((float)pen, baseline - face.Ascent), _format);
                pen += Advance(text, face.Font);
                return;
            }
            // Tracked text is set cluster by cluster at the running pen, since there is no kern to bend.
            var clusters = StringInfo.GetTextElementEnumerator(text);
            while (clusters.MoveNext())
            {
                string cluster = (string)clusters.Current;
                path.AddString(cluster, face.Family, face.Style, _size,
                    new PointF((float)pen, baseline - face.Ascent), _format);
                pen += Advance(cluster, face.Font) + _tracking;
            }
        }
    }
}
