using System.Globalization;
using System.Text;

namespace Compositor.Core.Psd;

/// <summary>
/// Reads a Photoshop type layer (`TySh`) into the editor's text model, following the Mac's
/// `PSDText.parse`: the 2×3 transform, the text descriptor and the PostScript-like `EngineData`
/// dictionary supply the content, font, size, colour, tracking, leading and alignment. Anything
/// this model cannot represent stays a plain raster: vertical orientation, shear, uneven scale,
/// or a paragraph frame the style cannot store make the record unusable, exactly as there.
/// The decoded pixels stay the layer's asset; the style is the editable record the port redraws
/// from when the text is edited, the same shape a typed text layer takes.
/// </summary>
internal static class PsdText
{
    public const string RasterizedNote = "Editable Photoshop text becomes pixels and can’t be retyped.";
    public const string FirstStyleNote = "Only the first text style was kept.";
    public const string WarpNote = "The Photoshop text warp was omitted.";
    public const string FauxNote = "Faux bold or faux italic was omitted.";
    public const string JustifyNote = "Full justification was imported as left alignment.";

    /// <summary>The Mac's missingFontNote: it asks NSFont whether the face is installed; the port
    /// asks the same question of DirectWrite, with the same strict exact-name match.</summary>
    public static string? MissingFontNote(string? fontName)
    {
        if (string.IsNullOrEmpty(fontName) || TextRasterizer.FontInstalled(fontName)) return null;
        return "The font \u201c" + fontName + "\u201d isn\u2019t installed, so the text was drawn with the system font.";
    }

    /// <summary>A parsed text layer: the style, conversion notes and its document anchor.</summary>
    internal sealed record Parse(LayerTextStyle Style, List<string> Notes, double AnchorX, double AnchorY,
        double Rotation, bool FlipY, bool AnchorIsFrame);

    /// <summary>Whether the layer's extra-info loop should keep this key for the text path. The
    /// Mac's kind() also counts txt2; that label lives in PsdLayer.Kind, not here.</summary>
    internal static bool TracksKey(string key) => key is "TySh" or "tySh";

    internal static Parse? ReadLive(byte[]? data)
    {
        if (data is null || data.Length > 8_000_000) return null;
        var reader = new DescriptorReader(data);
        if (reader.U16() != 1) return null;
        var matrix = new double[6];
        for (int i = 0; i < 6; i++)
        {
            double? value = reader.F64();
            if (value is null || !double.IsFinite(value.Value)) return null;
            matrix[i] = value.Value;
        }
        if (reader.U16() != 50) return null;
        Descriptor? text = reader.Descriptor(true);
        if (text is null) return null;
        if (text.Enumeration("Ornt") == "Vrtc") return null;
        Placement? placed = PlacementOf(matrix);
        if (placed is null) return null;

        var notes = new List<string>();
        if (reader.Remaining >= 2 && reader.U16() == 1)
        {
            Descriptor? warp = reader.Descriptor(true);
            string? warpStyle = warp?.Enumeration("warpStyle");
            if (warpStyle is not null && warpStyle != "warpNone" && warpStyle != "none") notes.Add(WarpNote);
        }

        EngineData? engine = text.Data("EngineData") is { } raw ? EngineValue(raw) : null;
        string? content = Cleaned(text.String("Txt ") ?? text.String("Txt"));
        if (content is null && engine is not null) content = Cleaned(EnginePathString(engine, "EngineDict", "Editor", "Text"));
        if (string.IsNullOrEmpty(content) || content.Length > 100_000) return null;

        var style = new LayerTextStyle { Content = content };
        if (engine is not null) ApplyStyle(style, engine, placed.PixelScale, notes);
        else style.FontSize = Math.Clamp(12 * placed.PixelScale, 1, 2000);
        if (!double.IsFinite(style.FontSize) || style.FontSize <= 0) return null;

        double anchorX = placed.Tx, anchorY = placed.Ty;
        bool anchorIsFrame = false;
        if (text.Rect("bounds") is { } bounds && text.Rect("boundingBox") is { } glyphs
            && bounds.Width > glyphs.Width + 4 && bounds.Height > glyphs.Height + 4
            && bounds.Width > 1 && bounds.Height > 1)
        {
            double pad = LayerTextStyle.Padding;
            var boxed = new LayerTextStyle
            {
                Content = style.Content,
                FontName = style.FontName,
                FontSize = style.FontSize,
                Red = style.Red,
                Green = style.Green,
                Blue = style.Blue,
                Alignment = style.Alignment,
                Tracking = style.Tracking,
                Leading = style.Leading,
                ColorRuns = style.ColorRuns,
                FontRuns = style.FontRuns,
                BoxSize = new TextBoxSize
                {
                    Width = bounds.Width * placed.PixelScale + pad * 2,
                    Height = bounds.Height * placed.PixelScale + pad * 2,
                },
            };
            // A paragraph frame the model cannot store is dropped entirely: importing as point
            // text would lose the wrap without saying so. Vertical text falls back the same way.
            if (!boxed.IsValid) return null;
            style = boxed;
            (anchorX, anchorY) = placed.Map(bounds.X, bounds.Y);
            anchorIsFrame = true;
        }
        if (!style.IsValid) return null;
        return new Parse(style, notes, anchorX, anchorY, placed.Rotation, placed.FlipY, anchorIsFrame);
    }

    private sealed class Placement
    {
        public required double PixelScale { get; init; }
        public required double Rotation { get; init; }
        public required bool FlipY { get; init; }
        public required double Tx { get; init; }
        public required double Ty { get; init; }
        public required Func<double, double, (double X, double Y)> Map { get; init; }
    }

    /// <summary>
    /// Uniform scale, rotation and an optional vertical flip; shear and uneven scale return null,
    /// as the Mac's `placement` does. Engine sizes are already text-space units that the matrix
    /// maps into document pixels — document PPI must not multiply that product again.
    /// </summary>
    private static Placement? PlacementOf(double[] m)
    {
        double xx = m[0], xy = m[1], yx = m[2], yy = m[3], tx = m[4], ty = m[5];
        double scaleX = Math.Sqrt(xx * xx + yx * yx);
        if (scaleX <= 1e-6) return null;
        double cosR = xx / scaleX, sinR = yx / scaleX;
        double localX = cosR * xy + sinR * yy;
        double localY = -sinR * xy + cosR * yy;
        double scaleY = Math.Abs(localY);
        if (scaleY <= 1e-6) return null;
        double largest = Math.Max(scaleX, scaleY);
        if (Math.Abs(localX) > 0.02 * largest || Math.Abs(scaleX - scaleY) > 0.02 * largest) return null;
        if (!double.IsFinite(scaleX) || scaleX <= 0) return null;
        double ySign = localY < 0 ? -1.0 : 1.0;
        double exx = cosR * scaleX, eyx = sinR * scaleX, exy = -sinR * scaleX * ySign, eyy = cosR * scaleX * ySign;
        return new Placement
        {
            PixelScale = scaleX,
            Rotation = Math.Atan2(sinR, cosR) * 180 / Math.PI,
            FlipY = localY < 0,
            Tx = tx,
            Ty = ty,
            Map = (x, y) => (exx * x + exy * y + tx, eyx * x + eyy * y + ty),
        };
    }

    /// <summary>
    /// `PSDText.layerTransform`: flip, then clockwise rotation about the center, so the image's
    /// anchor point lands on the document anchor. Pure math, pinned by the placement test.
    /// </summary>
    internal static LayerTransform LayerTransformOf(int imageWidth, int imageHeight, double imageAnchorX, double imageAnchorY,
        double documentX, double documentY, double rotation, bool flipY)
    {
        double localX = imageAnchorX - imageWidth / 2.0;
        double localY = imageAnchorY - imageHeight / 2.0;
        if (flipY) localY = -localY;
        double radians = rotation * Math.PI / 180;
        double rotatedX = localX * Math.Cos(radians) - localY * Math.Sin(radians);
        double rotatedY = localX * Math.Sin(radians) + localY * Math.Cos(radians);
        double centerX = documentX - rotatedX, centerY = documentY - rotatedY;
        return new LayerTransform
        {
            Origin = new PointD(centerX - imageWidth / 2.0, centerY - imageHeight / 2.0),
            Size = new SizeD(imageWidth, imageHeight),
            Rotation = rotation,
            FlipY = flipY,
        };
    }

    /// <summary>
    /// Where the raster's anchor sits in the image: the frame's padding corner, or the first
    /// baseline at the alignment's horizontal offset — the Mac's `render` math, with the port's
    /// own <see cref="TextRasterizer.FirstBaseline"/> standing in for the layout query.
    /// </summary>
    internal static (double X, double Y) ImageAnchorOf(LayerTextStyle style, double imageWidth)
    {
        if (style.BoxSize is not null) return (LayerTextStyle.Padding, LayerTextStyle.Padding);
        double x = style.Alignment switch
        {
            TextAlignment.Right => imageWidth - LayerTextStyle.Padding,
            TextAlignment.Center => imageWidth / 2.0,
            _ => LayerTextStyle.Padding,
        };
        return (x, TextRasterizer.FirstBaseline(style));
    }

    private static void ApplyStyle(LayerTextStyle style, EngineData engine, double pixelScale, List<string> notes)
    {
        List<EngineData> runs = EngineArray(EngineWalk(engine, "EngineDict", "StyleRun", "RunArray"));
        EngineData first = runs.Count > 0 ? runs[0] : engine;
        EngineData? data = EngineWalk(first, "StyleSheet", "StyleSheetData") ?? EngineWalk(engine, "EngineDict", "StyleRun", "RunArray") ?? first;
        double points = EngineNumber(EngineWalk(data, "FontSize")) ?? 12; // the Mac defaults to twelve
        if (!double.IsFinite(points) || points <= 0) return;
        style.FontSize = Math.Clamp(points * pixelScale, 1, 2000);
        List<EngineData> fonts = EngineArray(EngineWalk(engine, "ResourceDict", "FontSet"));
        int index = (int)Math.Round(EngineNumber(EngineWalk(data, "Font")) ?? 0);
        if (index >= 0 && index < fonts.Count && EnginePathString(fonts[index], "Name") is { Length: > 0 } name)
            style.FontName = name;
        List<double> values = EngineArray(EngineWalk(data, "FillColor", "Values"))
            .Select(v => EngineNumber(v)).Where(v => v is not null).Select(v => v!.Value).ToList();
        if (values.Count > 0)
        {
            var (r, g, b) = ColorOf(values);
            style.Red = r; style.Green = g; style.Blue = b;
        }
        double tracking = EngineNumber(EngineWalk(data, "Tracking")) ?? 0;
        if (double.IsFinite(tracking)) style.Tracking = Math.Clamp(tracking * style.FontSize / 1000, -100, 1000);
        bool autoLeading = EngineBool(EngineWalk(data, "AutoLeading")) ?? true;
        if (!autoLeading && EngineNumber(EngineWalk(data, "Leading")) is { } leading && double.IsFinite(leading) && leading > 0)
            style.Leading = Math.Clamp(leading * pixelScale, 0, 5000);
        if (EngineBool(EngineWalk(data, "FauxBold")) == true || EngineBool(EngineWalk(data, "FauxItalic")) == true)
            notes.Add(FauxNote);
        if (runs.Count > 1 && runs.Skip(1).Any(run => !SignatureOf(run).Equals(SignatureOf(first))))
            notes.Add(FirstStyleNote);
        List<EngineData> paragraphs = EngineArray(EngineWalk(engine, "EngineDict", "ParagraphRun", "RunArray"));
        double? justification = EngineNumber(EngineWalk(paragraphs.Count > 0 ? paragraphs[0] : engine,
            "ParagraphSheet", "Properties", "Justification"));
        switch ((int)Math.Round(justification ?? 0))
        {
            case 1: style.Alignment = TextAlignment.Right; break;
            case 2: style.Alignment = TextAlignment.Center; break;
            case 0: style.Alignment = TextAlignment.Left; break;
            default: style.Alignment = TextAlignment.Left; notes.Add(JustifyNote); break;
        }
    }

    private readonly record struct Signature(double Font, double Size, double Tracking, double Leading, bool AutoLeading,
        double HorizontalScale, double VerticalScale, bool Bold, bool Italic, double Red, double Green, double Blue);

    private static Signature SignatureOf(EngineData run)
    {
        EngineData? data = EngineWalk(run, "StyleSheet", "StyleSheetData") ?? run;
        List<double> channels = EngineArray(EngineWalk(data, "FillColor", "Values"))
            .Select(v => EngineNumber(v)).Where(v => v is not null).Select(v => v!.Value).ToList();
        var (r, g, b) = ColorOf(channels);
        return new Signature(
            EngineNumber(EngineWalk(data, "Font")) ?? 0,
            EngineNumber(EngineWalk(data, "FontSize")) ?? 0,
            EngineNumber(EngineWalk(data, "Tracking")) ?? 0,
            EngineNumber(EngineWalk(data, "Leading")) ?? 0,
            EngineBool(EngineWalk(data, "AutoLeading")) ?? true,
            EngineNumber(EngineWalk(data, "HorizontalScale")) ?? 1,
            EngineNumber(EngineWalk(data, "VerticalScale")) ?? 1,
            EngineBool(EngineWalk(data, "FauxBold")) ?? false,
            EngineBool(EngineWalk(data, "FauxItalic")) ?? false,
            r, g, b);
    }

    private static (double R, double G, double B) ColorOf(IReadOnlyList<double> values)
    {
        static double Unit(double value) => value > 1 ? Math.Clamp(value, 0, 255) / 255 : Math.Clamp(value, 0, 1);
        if (values.Count >= 4) return (Unit(values[1]), Unit(values[2]), Unit(values[3]));
        if (values.Count == 3) return (Unit(values[0]), Unit(values[1]), Unit(values[2]));
        if (values.Count > 0) { double gray = Unit(values[0]); return (gray, gray, gray); }
        return (0, 0, 0);
    }

    private static string? Cleaned(string? text)
    {
        if (text is null) return null;
        int start = 0, end = text.Length;
        while (start < end && (text[start] == '\uFEFF' || text[start] == '\0')) start++;
        while (end > start && text[end - 1] == '\0') end--;
        var slice = text.AsSpan(start, end - start);
        if (slice.IndexOf('\r') >= 0)
        {
            var builder = new StringBuilder(slice.Length);
            for (int i = 0; i < slice.Length; i++)
            {
                if (slice[i] == '\r')
                {
                    builder.Append('\n');
                    if (i + 1 < slice.Length && slice[i + 1] == '\n') i++;
                }
                else builder.Append(slice[i]);
            }
            return builder.ToString();
        }
        return slice.ToString();
    }

    // ----- EngineData: a small PostScript-like subset (`<< >>`, arrays, names, numbers, strings). -----

    private abstract class EngineData { }

    private sealed class EngineNumberData(double value) : EngineData { public double Value = value; }

    private sealed class EngineBoolData(bool value) : EngineData { public bool Value = value; }

    private sealed class EngineStringData(string value) : EngineData { public string Value = value; }

    private sealed class EngineDictionaryData : EngineData
    {
        public Dictionary<string, EngineData> Items = new();

        /// <summary>A repeated name overwrites, as Swift's `items[key] = value` dictionary insert does.</summary>
        public void Add(string key, EngineData value) => Items[key] = value;
    }

    private sealed class EngineArrayData(List<EngineData> items) : EngineData { public List<EngineData> Items = items; }

    private static EngineData? EngineValue(byte[] data)
    {
        var cursor = new EngineCursor(data, 0);
        if (cursor.ParseValue() is EngineDictionaryData direct) return direct;
        // The engine block is usually embedded after binary junk: try the first `<<` past index 0.
        for (int i = 1; i + 1 < data.Length; i++)
        {
            if (data[i] != (byte)'<' || data[i + 1] != (byte)'<') continue;
            var at = new EngineCursor(data, i);
            return at.ParseValue() is EngineDictionaryData nested ? nested : null;
        }
        return null;
    }

    private static EngineData? EngineWalk(EngineData? value, params string[] keys)
    {
        EngineData? current = value;
        foreach (string key in keys)
        {
            if (current is not EngineDictionaryData dict || !dict.Items.TryGetValue(key, out EngineData? next)) return null;
            current = next;
        }
        return current;
    }

    private static double? EngineNumber(EngineData? value) => value is EngineNumberData number ? number.Value : null;

    private static bool? EngineBool(EngineData? value) => value is EngineBoolData flag ? flag.Value : null;

    private static string? EnginePathString(EngineData? value, params string[] keys)
    {
        if (keys.Length > 0) value = EngineWalk(value, keys);
        return value is EngineStringData text ? text.Value : null;
    }

    private static List<EngineData> EngineArray(EngineData? value) => value is EngineArrayData list ? list.Items : [];

    private struct EngineCursor
    {
        private readonly byte[] _data;
        private int _index;

        public EngineCursor(byte[] data, int index) { _data = data; _index = index; }

        public EngineData? ParseValue()
        {
            SkipWhitespace();
            byte? head = Peek();
            if (head is null) return null;
            switch (head.Value)
            {
                case (byte)'<':
                    return Ahead(1) == (byte)'<' ? ParseDictionary() : ParseHex();
                case (byte)'[':
                    return ParseArray();
                case (byte)'(':
                    return ParseString();
                case (byte)'/':
                    _index++;
                    return new EngineStringData(ReadToken());
            }
            byte b = head.Value;
            if (b == (byte)'-' || b == (byte)'+' || b == (byte)'.' || (b >= (byte)'0' && b <= (byte)'9'))
            {
                double? number = ParseNumber();
                return number is null ? null : new EngineNumberData(number.Value);
            }
            if (TakeWord("true")) return new EngineBoolData(true);
            if (TakeWord("false")) return new EngineBoolData(false);
            if (TakeWord("null")) return new EngineStringData("");
            return null;
        }

        private EngineData? ParseDictionary()
        {
            if (!Take("<<")) return null;
            var items = new EngineDictionaryData();
            while (true)
            {
                SkipWhitespace();
                byte? at = Peek();
                if (at is null || at.Value == (byte)'>') break;
                if (at.Value != (byte)'/') return null;
                _index++;
                string key = ReadToken();
                EngineData? value = ParseValue();
                if (value is null) return null;
                items.Add(key, value);
            }
            return Take(">>") ? items : null;
        }

        private EngineData? ParseArray()
        {
            if (!Take("[")) return null;
            var items = new List<EngineData>();
            while (true)
            {
                SkipWhitespace();
                byte? at = Peek();
                if (at is null || at.Value == (byte)']') break;
                EngineData? value = ParseValue();
                if (value is null) return null;
                items.Add(value);
            }
            return Take("]") ? new EngineArrayData(items) : null;
        }

        private double? ParseNumber()
        {
            int start = _index;
            byte? sign = Peek();
            if (sign == (byte)'+' || sign == (byte)'-') _index++;
            while (IsDigit(Peek())) _index++;
            if (Peek() == (byte)'.')
            {
                _index++;
                while (IsDigit(Peek())) _index++;
            }
            byte? expo = Peek();
            if (expo == (byte)'e' || expo == (byte)'E')
            {
                _index++;
                byte? esign = Peek();
                if (esign == (byte)'+' || esign == (byte)'-') _index++;
                while (IsDigit(Peek())) _index++;
            }
            if (_index <= start) return null;
            string text = Encoding.ASCII.GetString(_data, start, _index - start);
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : null;
        }

        private EngineData? ParseString()
        {
            if (!Take("(")) return null;
            var raw = new List<byte>();
            while (true)
            {
                byte? at = Peek();
                if (at is null) break;
                _index++;
                if (at.Value == (byte)')') break;
                if (at.Value != (byte)'\\') { raw.Add(at.Value); continue; }
                byte? escaped = Peek();
                if (escaped is null) return null;
                _index++;
                byte e = escaped.Value;
                if (e == (byte)'n') raw.Add((byte)'\n');
                else if (e == (byte)'r') raw.Add((byte)'\r');
                else if (e == (byte)'t') raw.Add((byte)'\t');
                else if (e >= (byte)'0' && e <= (byte)'7')
                {
                    int value = e - (byte)'0';
                    for (int i = 0; i < 2; i++)
                    {
                        byte? digit = Peek();
                        if (digit is null || digit.Value < (byte)'0' || digit.Value > (byte)'7') break;
                        _index++;
                        value = value * 8 + digit.Value - (byte)'0';
                    }
                    raw.Add((byte)(value & 0xFF));
                }
                else if (e != (byte)'\n' && e != (byte)'\r') raw.Add(e);
            }
            return new EngineStringData(DecodeEngine(raw));
        }

        private EngineData? ParseHex()
        {
            if (!Take("<")) return null;
            var nibbles = new List<byte>();
            while (true)
            {
                byte? at = Peek();
                if (at is null || at.Value == (byte)'>') break;
                _index++;
                byte? nibble = Hex(at.Value);
                if (nibble is not null) nibbles.Add(nibble.Value);
            }
            if (!Take(">")) return null;
            var raw = new List<byte>();
            for (int i = 0; i + 1 < nibbles.Count; i += 2) raw.Add((byte)((nibbles[i] << 4) | nibbles[i + 1]));
            return new EngineStringData(DecodeEngine(raw));
        }

        private static string DecodeEngine(List<byte> raw)
        {
            byte[] bytes = raw.ToArray();
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            return Encoding.Latin1.GetString(bytes);
        }

        private static byte? Hex(byte b) =>
            b >= (byte)'0' && b <= (byte)'9' ? (byte)(b - (byte)'0')
            : b >= (byte)'a' && b <= (byte)'f' ? (byte)(b - (byte)'a' + 10)
            : b >= (byte)'A' && b <= (byte)'F' ? (byte)(b - (byte)'A' + 10)
            : null;

        private static bool IsDigit(byte? b) => b is >= (byte)'0' and <= (byte)'9';

        private string ReadToken()
        {
            int start = _index;
            while (true)
            {
                byte? at = Peek();
                if (at is null || IsDelimiter(at.Value)) break;
                _index++;
            }
            return Encoding.ASCII.GetString(_data, start, _index - start);
        }

        private static bool IsDelimiter(byte b) => b <= 0x20
            || b == (byte)'/' || b == (byte)'<' || b == (byte)'>' || b == (byte)'[' || b == (byte)']' || b == (byte)'(' || b == (byte)')';

        private bool TakeWord(string word)
        {
            if (!Matches(word)) return false;
            int after = _index + word.Length;
            if (after < _data.Length && !IsDelimiter(_data[after])) return false;
            _index = after;
            return true;
        }

        private bool Take(string token)
        {
            if (!Matches(token)) return false;
            _index += token.Length;
            return true;
        }

        private bool Matches(string token)
        {
            if (_index + token.Length > _data.Length) return false;
            for (int i = 0; i < token.Length; i++) if (_data[_index + i] != (byte)token[i]) return false;
            return true;
        }

        private void SkipWhitespace()
        {
            while (true)
            {
                byte? at = Peek();
                if (at is null || (at.Value > 0x20 && at.Value != (byte)'%')) break;
                if (at.Value == (byte)'%')
                {
                    while (true)
                    {
                        byte? next = Peek();
                        if (next is null || next.Value == (byte)'\n' || next.Value == (byte)'\r') break;
                        _index++;
                    }
                }
                else _index++;
            }
        }

        private byte? Peek() => _index < _data.Length ? _data[_index] : null;

        private byte? Ahead(int count) => _index + count < _data.Length ? _data[_index + count] : null;
    }

    // ----- Descriptor reader: the same spec walker as `PSDText.Reader`. -----

    private abstract class DescriptorValue { }

    private sealed class DescString(string value) : DescriptorValue { public string Value = value; }

    private sealed class DescNumber(double value) : DescriptorValue { public double Value = value; }

    private sealed class DescEnumeration(string value) : DescriptorValue { public string Value = value; }

    private sealed class DescData(byte[] value) : DescriptorValue { public byte[] Value = value; }

    private sealed class DescList(List<DescriptorValue> items) : DescriptorValue { public List<DescriptorValue> Items = items; }

    private sealed class Descriptor : DescriptorValue
    {
        public Dictionary<string, DescriptorValue> Items = new();

        public string? String(string key) => Items.TryGetValue(key, out var value) && value is DescString text ? text.Value : null;

        public string? Enumeration(string key) => Items.TryGetValue(key, out var value) && value is DescEnumeration enumeration ? enumeration.Value : null;

        public byte[]? Data(string key) => Items.TryGetValue(key, out var value) && value is DescData data ? data.Value : null;

        /// <summary>Left/Top/Rght/Btom doubles, tolerating the un-padded keys some writers emit.</summary>
        public (double X, double Y, double Width, double Height)? Rect(string key)
        {
            if (!Items.TryGetValue(key, out var value) || value is not Descriptor nested) return null;
            double? Side(string name)
            {
                if (nested.Items.TryGetValue(name, out var direct) && direct is DescNumber number) return number.Value;
                if (nested.Items.TryGetValue(name.TrimEnd(), out var trimmed) && trimmed is DescNumber other) return other.Value;
                return null;
            }
            double? left = Side("Left"), top = Side("Top "), right = Side("Rght"), bottom = Side("Btom");
            if (left is null || top is null || right is null || bottom is null) return null;
            if (!double.IsFinite(left.Value) || !double.IsFinite(top.Value) || !double.IsFinite(right.Value) || !double.IsFinite(bottom.Value)) return null;
            return (left.Value, top.Value, right.Value - left.Value, bottom.Value - top.Value);
        }
    }

    private struct DescriptorReader
    {
        private readonly byte[] _data;
        private int _offset;

        public DescriptorReader(byte[] data) { _data = data; _offset = 0; }

        public readonly int Remaining => _data.Length - _offset;

        public Descriptor? Descriptor(bool versioned)
        {
            if (versioned && U32() != 16) return null;
            if (Unicode() is null || Identifier() is null) return null;
            uint? count = U32();
            if (count is null || count.Value > 10_000) return null;
            var items = new Descriptor();
            for (int i = 0; i < count.Value; i++)
            {
                string? key = Identifier();
                string? type = FourCC();
                if (key is null || type is null) return null;
                DescriptorValue? value = Value(type);
                if (value is null) return null;
                items.Items[key] = value;
            }
            return items;
        }

        private DescriptorValue? Value(string type)
        {
            switch (type)
            {
                case "doub":
                    return F64() is { } number ? new DescNumber(number) : null;
                case "UntF":
                    if (FourCC() is null) return null;
                    return F64() is { } unitValue ? new DescNumber(unitValue) : null;
                case "long":
                    return I32() is { } integer ? new DescNumber(integer) : null;
                case "comp":
                    if (Bytes(8) is not { } raw) return null;
                    ulong compBits = 0;
                    foreach (byte b in raw) compBits = (compBits << 8) | b;
                    return new DescNumber(unchecked((long)compBits));
                case "bool":
                    return U8() is null ? null : new DescNumber(0); // the Mac reads the flag but keeps 0
                case "TEXT":
                    return Unicode() is { } text ? new DescString(text) : null;
                case "enum":
                    if (Identifier() is null) return null;
                    return Identifier() is { } name ? new DescEnumeration(name) : null;
                case "tdta":
                    uint? length = U32();
                    if (length is null || length.Value > 8_000_000) return null;
                    return Bytes((int)length.Value) is { } data ? new DescData(data) : null;
                case "Objc" or "GlbO":
                    return Descriptor(versioned: false) is { } nested ? nested : null;
                case "VlLs":
                    uint? listCount = U32();
                    if (listCount is null || listCount.Value > 10_000) return null;
                    var items = new List<DescriptorValue>();
                    for (int i = 0; i < listCount.Value; i++)
                    {
                        string? itemType = FourCC();
                        if (itemType is null) return null;
                        DescriptorValue? item = Value(itemType);
                        if (item is null) return null;
                        items.Add(item);
                    }
                    return new DescList(items);
                case "alis":
                    uint? aliasLength = U32();
                    if (aliasLength is null || aliasLength.Value > 8_000_000) return null;
                    return Bytes((int)aliasLength.Value) is null ? null : new DescNumber(0);
                case "obj ":
                    return Reference() ? new DescNumber(0) : null;
                case "type" or "GlbC":
                    return Unicode() is not null && Identifier() is not null ? new DescNumber(0) : null;
                default:
                    return null;
            }
        }

        /// <summary>Skips a property reference so a later `EngineData` item can still be read.</summary>
        private bool Reference()
        {
            uint? count = U32();
            if (count is null || count.Value > 10_000) return false;
            for (int i = 0; i < count.Value; i++)
            {
                switch (FourCC())
                {
                    case "prop":
                        if (Unicode() is null || Identifier() is null || Identifier() is null) return false;
                        break;
                    case "Clss":
                        if (Unicode() is null || Identifier() is null) return false;
                        break;
                    case "Enmr":
                        if (Unicode() is null || Identifier() is null || Identifier() is null || Identifier() is null) return false;
                        break;
                    case "rele":
                        if (Unicode() is null || I32() is null) return false;
                        break;
                    case "Idnt" or "indx":
                        if (I32() is null) return false;
                        break;
                    case "name":
                        if (Unicode() is null) return false;
                        break;
                    default:
                        return false;
                }
            }
            return true;
        }

        private string? Unicode()
        {
            uint? count = U32();
            if (count is null || count.Value > 1_000_000) return null;
            byte[]? raw = Bytes((int)count.Value * 2);
            if (raw is null) return null;
            return raw.Length == 0 ? "" : Encoding.BigEndianUnicode.GetString(raw);
        }

        private string? Identifier()
        {
            uint? length = U32();
            if (length is null) return null;
            if (length.Value == 0) return FourCC();
            if (length.Value > 10_000) return null;
            byte[]? raw = Bytes((int)length.Value);
            return raw is null ? null : Encoding.ASCII.GetString(raw);
        }

        private string? FourCC() => Bytes(4) is { } raw ? Encoding.ASCII.GetString(raw) : null;

        private byte[]? Bytes(int count)
        {
            if (count < 0 || _offset + count > _data.Length) return null;
            var slice = new byte[count];
            Array.Copy(_data, _offset, slice, 0, count);
            _offset += count;
            return slice;
        }

        private byte? U8() => _offset >= _data.Length ? null : _data[_offset++];

        public ushort? U16() => Bytes(2) is { } raw ? (ushort)((raw[0] << 8) | raw[1]) : null;

        private uint? U32() => Bytes(4) is { } raw ? (uint)((raw[0] << 24) | (raw[1] << 16) | (raw[2] << 8) | raw[3]) : null;

        private int? I32() => U32() is { } value ? unchecked((int)value) : null;

        public double? F64()
        {
            if (Bytes(8) is not { } raw) return null;
            ulong bits = 0;
            foreach (byte b in raw) bits = (bits << 8) | b;
            return BitConverter.Int64BitsToDouble(unchecked((long)bits));
        }
    }
}
