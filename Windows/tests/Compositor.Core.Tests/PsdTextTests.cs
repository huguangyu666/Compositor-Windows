using Compositor.Core.Psd;
using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// Photoshop type layers (`TySh`) become editable text: the descriptor and the PostScript-like
/// EngineData supply content, font, size, colour and alignment, and the 2×3 matrix decides where
/// the anchor lands. What the model cannot represent — vertical text, shear, uneven scale — falls
/// back to the layer's own pixels, as the Mac's reader does. The fixtures build descriptor bytes
/// by hand, so every field is under test control.
/// </summary>
public class PsdTextTests
{
    private static byte[] U16(ushort value) => [(byte)(value >> 8), (byte)value];

    private static byte[] U32(uint value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static byte[] F64(double value)
    {
        var bits = (ulong)BitConverter.DoubleToInt64Bits(value);
        return [(byte)(bits >> 56), (byte)(bits >> 48), (byte)(bits >> 40), (byte)(bits >> 32),
                (byte)(bits >> 24), (byte)(bits >> 16), (byte)(bits >> 8), (byte)bits];
    }

    private static byte[] Four(string text) => System.Text.Encoding.ASCII.GetBytes(text);

    private static byte[] Unicode(string text) => [.. U32((uint)text.Length), .. System.Text.Encoding.BigEndianUnicode.GetBytes(text)];

    private static byte[] Identifier(string text) => text.Length == 4 ? [.. U32(0), .. Four(text)] : [.. U32((uint)text.Length), .. Four(text)];

    private static byte[] Item(string key, string type, byte[] payload) => [.. Identifier(key), .. Four(type), .. payload];

    private static byte[] Descriptor(string classId, params (string Key, string Type, byte[] Payload)[] items)
        => Descriptor(classId, versioned: true, items);

    private static byte[] Descriptor(string classId, bool versioned, params (string Key, string Type, byte[] Payload)[] items)
    {
        var body = new List<byte>();
        if (versioned) body.AddRange(U32(16)); // only the top-level descriptor carries the count
        body.AddRange(Unicode(classId));
        body.AddRange(Identifier(classId));
        body.AddRange(U32((uint)items.Length));
        foreach (var (key, type, payload) in items) body.AddRange(Item(key, type, payload));
        return [.. body];
    }

    /// <summary>The TySh head: version, the transform matrix, text version 50, the text descriptor.</summary>
    private static byte[] TySh(double[]? matrix = null, byte[]? text = null, byte[]? warp = null)
    {
        var m = matrix ?? [1d, 0d, 0d, 1d, 10d, 20d];
        var body = new List<byte>();
        body.AddRange(U16(1));
        foreach (var value in m) body.AddRange(F64(value));
        body.AddRange(U16(50));
        body.AddRange(text ?? Descriptor("textLayer", ("Txt ", "TEXT", Unicode("Hello"))));
        if (warp is not null) { body.AddRange(U16(1)); body.AddRange(warp); }
        return [.. body];
    }

    /// <summary>An EngineData block wrapped in the tdta payload length.</summary>
    private static (string, string, byte[]) Engine(string text)
    {
        var raw = System.Text.Encoding.ASCII.GetBytes(text);
        return ("EngineData", "tdta", [.. U32((uint)raw.Length), .. raw]);
    }

    [Fact]
    public void PointTextParsesContentSizeAndAnchorFromTheMatrix()
    {
        var parse = PsdText.ReadLive(TySh());
        Assert.NotNull(parse);
        Assert.Equal("Hello", parse!.Style.Content);
        // No engine: the Mac's fallback is 12 points times the matrix's pixel scale.
        Assert.Equal(12, parse.Style.FontSize, 3);
        Assert.False(parse.AnchorIsFrame);
        Assert.Equal(10, parse.AnchorX); Assert.Equal(20, parse.AnchorY);
        Assert.Equal(0, parse.Rotation, 6); Assert.False(parse.FlipY);
        Assert.Empty(parse.Notes);
    }

    [Fact]
    public void VerticalOrientationIsNotEditable()
    {
        var text = Descriptor("textLayer", ("Txt ", "TEXT", Unicode("Vertical")), ("Ornt", "enum", [.. Identifier("textOrientation"), .. Identifier("Vrtc")]));
        Assert.Null(PsdText.ReadLive(TySh(text: text)));
    }

    [Fact]
    public void ShearedOrUnevenMatrixIsNotEditable()
    {
        Assert.Null(PsdText.ReadLive(TySh(matrix: [1, 0.5, 0, 1, 10, 20]))); // shear
        Assert.Null(PsdText.ReadLive(TySh(matrix: [1, 0, 0, 3, 10, 20]))); // uneven scale
    }

    [Fact]
    public void NegativeYYMatrixReadsAsFlippedText()
    {
        var parse = PsdText.ReadLive(TySh(matrix: [1, 0, 0, -1, 10, 20]));
        Assert.NotNull(parse);
        Assert.True(parse!.FlipY);
        Assert.Equal(0, parse.Rotation, 6);
    }

    [Fact]
    public void RotatedMatrixKeepsTheRotationForTheAnchorMath()
    {
        var radians = 30 * Math.PI / 180;
        // cosR reads xx/scale, sinR reads yx/scale: the counter-clockwise matrix form the spec stores.
        var parse = PsdText.ReadLive(TySh(matrix: [Math.Cos(radians), -Math.Sin(radians), Math.Sin(radians), Math.Cos(radians), 40, 50]));
        Assert.NotNull(parse);
        Assert.Equal(30, parse!.Rotation, 4);
        Assert.False(parse.FlipY);
    }

    [Fact]
    public void EmptyContentStaysAPlainRaster()
    {
        Assert.Null(PsdText.ReadLive(TySh(text: Descriptor("textLayer", ("Txt ", "TEXT", Unicode(""))))));
    }

    [Fact]
    public void WarpRecordIsNotedAndOmitted()
    {
        var warp = Descriptor("warpStyle", ("warpStyle", "enum", [.. Identifier("warpStyle"), .. Identifier("warpBulge")]));
        var parse = PsdText.ReadLive(TySh(warp: warp));
        Assert.NotNull(parse);
        Assert.Contains(PsdText.WarpNote, parse!.Notes);
    }

    [Fact]
    public void ParagraphFrameBecomesABoundedStyleAnchoredAtTheFrame()
    {
        // A bounds box wider and taller than the glyph box is a paragraph frame; the style carries it.
        static byte[] RectValue(double left, double top, double right, double bottom) =>
            Descriptor("rectBounds", versioned: false, ("Left", "doub", F64(left)), ("Top ", "doub", F64(top)), ("Rght", "doub", F64(right)), ("Btom", "doub", F64(bottom)));
        var text = Descriptor("textLayer",
            ("Txt ", "TEXT", Unicode("Wrap me")),
            ("bounds", "Objc", RectValue(0, 0, 120, 40)),
            ("boundingBox", "Objc", RectValue(0, 0, 60, 20)));
        var parse = PsdText.ReadLive(TySh(text: text));
        Assert.NotNull(parse);
        Assert.True(parse!.AnchorIsFrame);
        Assert.NotNull(parse.Style.BoxSize);
        Assert.Equal(120 + 2 * LayerTextStyle.Padding, parse.Style.BoxSize!.Width);
        Assert.Equal(40 + 2 * LayerTextStyle.Padding, parse.Style.BoxSize!.Height);
        Assert.Equal(10, parse.AnchorX); Assert.Equal(20, parse.AnchorY); // the frame origin through the identity matrix
    }

    [Fact]
    public void EngineDataSuppliesTheFirstStyleSheet()
    {
        var text = Descriptor("textLayer", Engine(
            "%+/-1.00 0 0\r<<\r/EngineDict <<\r/Editor << /Text (Two lines) >>\r" +
            "/StyleRun << /RunArray [ << /StyleSheet << /StyleSheetData <<\r" +
            "/FontSize 24 /Font 0 /Tracking 50 /FillColor << /Values [ 0 1 0 ] >> >> >> >> ] >>\r" +
            "/ParagraphRun << /RunArray [ << /ParagraphSheet << /Properties << /Justification 2 >> >> >> ] >>\r >>\r" +
            "/ResourceDict << /FontSet [ << /Name (Arial-BoldMT) >> ] >>\r>>"));
        var parse = PsdText.ReadLive(TySh(text: text));
        Assert.NotNull(parse);
        Assert.Equal("Two lines", parse!.Style.Content); // the Txt key was absent: the engine answers
        Assert.Equal(24, parse.Style.FontSize, 3);
        Assert.Equal("Arial-BoldMT", parse.Style.FontName);
        Assert.Equal(1.2, parse.Style.Tracking, 3); // 50 per mille of the 24 px size: 50*24/1000
        Assert.Equal(0, parse.Style.Red); Assert.Equal(1, parse.Style.Green);
        Assert.Equal(TextAlignment.Center, parse.Style.Alignment);
        Assert.Empty(parse.Notes);
    }

    [Fact]
    public void FauxBoldIsNotedAndOmitted()
    {
        var text = Descriptor("textLayer", ("Txt ", "TEXT", Unicode("Hello")),
            Engine("<< /EngineDict << /StyleRun << /RunArray [ << /StyleSheet << /StyleSheetData << /FontSize 16 /FauxBold true >> >> >> ] >> >> >>"));
        var parse = PsdText.ReadLive(TySh(text: text));
        Assert.NotNull(parse);
        Assert.Contains(PsdText.FauxNote, parse!.Notes);
    }

    [Fact]
    public void SecondRunWithAnotherStyleKeepsTheFirstAndNotesIt()
    {
        var text = Descriptor("textLayer", ("Txt ", "TEXT", Unicode("Hello")),
            Engine("<< /EngineDict << /StyleRun << /RunArray [ << /StyleSheet << /StyleSheetData << /FontSize 16 >> >> >> << /StyleSheet << /StyleSheetData << /FontSize 22 >> >> >> ] >> >> >>"));
        var parse = PsdText.ReadLive(TySh(text: text));
        Assert.NotNull(parse);
        Assert.Equal(16, parse!.Style.FontSize, 3);
        Assert.Contains(PsdText.FirstStyleNote, parse.Notes);
    }

    [Fact]
    public void FullJustificationReadsAsLeftAndSaysSo()
    {
        var text = Descriptor("textLayer", ("Txt ", "TEXT", Unicode("Hello")),
            Engine("<< /EngineDict << /ParagraphRun << /RunArray [ << /ParagraphSheet << /Properties << /Justification 4 >> >> >> ] >> >> >>"));
        var parse = PsdText.ReadLive(TySh(text: text));
        Assert.NotNull(parse);
        Assert.Equal(TextAlignment.Left, parse!.Style.Alignment);
        Assert.Contains(PsdText.JustifyNote, parse.Notes);
    }

    [Fact]
    public void TransformMathPutsTheAnchorOnTheDocumentPoint()
    {
        // The invariant the Mac's render relies on: the image anchor travels to the document
        // anchor through the flip and rotation, and the layer's box lands around it.
        const int width = 100, height = 40;
        var plain = PsdText.LayerTransformOf(width, height, 12, 32, 250, 300, 0, false);
        Assert.Equal(250 - (12 - width / 2.0) - width / 2.0, plain.Origin.X, 6); // 238
        Assert.Equal(300 - (32 - height / 2.0) - height / 2.0, plain.Origin.Y, 6); // 268
        var flipped = PsdText.LayerTransformOf(width, height, 12, 32, 250, 300, 0, true);
        Assert.Equal(plain.Origin.X, flipped.Origin.X, 6); // a vertical flip only moves Y
        Assert.Equal(300 + (32 - height / 2.0) - height / 2.0, flipped.Origin.Y, 6);
        Assert.True(flipped.FlipY);
        // Mapping the anchor back through the returned transform lands on the document point.
        var placed = BrushEngine.DocumentToPixel(plain, width, height, new PointD(250, 300));
        Assert.Equal(12, placed.X, 6); Assert.Equal(32, placed.Y, 6);
    }

    [Fact]
    public void ReaderKeepsTypeLayersAndTheImportRendersThemEditable()
    {
        // End to end: a TySh block inside a real layer record survives the reader; the import
        // re-renders the style and places it by the anchor, so the layer's pixels match its own
        // text (the live invariant editing depends on) instead of a mismatched Photoshop raster.
        var document = PsdReader.Read(BuildTextPsd());
        var layer = Assert.Single(document.Layers, candidate => !candidate.IsGroup);
        Assert.NotNull(layer.Text);
        Assert.Equal("Hello", layer.Text!.Content);
        Assert.NotNull(layer.Pixels); // the reader still carries the decoded raster
        Assert.True(layer.TextNotes is null or { Count: 0 }, "a plain point-text layer has no conversion notes");
        var scene = EditorDocument.Create(256, 256);
        var created = Assert.Single(scene.ImportPsd(document));
        Assert.NotNull(created.Text);
        Assert.Equal("Hello", created.Text!.Content);
        var pixels = scene.Snapshot.Images[created.Id];
        Assert.True(pixels.Pixels.SequenceEqual(TextRasterizer.Rasterize(created.Text!).Pixels),
            "the imported text layer's pixels do not match its own style, so editing would jump");
        Assert.True(created.Transform.Rotation == 0 && !created.Transform.FlipY); // the identity matrix
        Assert.NotEqual(8, pixels.Width); // re-rendered, not the 8x8 Photoshop fixture box
    }

    [Fact]
    public void ARotatedTypeLayerLandsWhereTheMatrixPlacedIt()
    {
        // The descriptor's 30° rotation reaches the manifest transform with the anchor honored:
        // mapping the image anchor through it lands on the record's document anchor.
        var document = PsdReader.Read(BuildTextPsd(matrix: Rotate(30, 60, 70)));
        var scene = EditorDocument.Create(256, 256);
        var created = Assert.Single(scene.ImportPsd(document));
        Assert.Equal(30, created.Transform.Rotation, 4);
        var pixels = scene.Snapshot.Images[created.Id];
        var anchor = PsdText.ImageAnchorOf(created.Text!, pixels.Width);
        // The invariant LayerTransformOf guarantees: mapping the record's document anchor through
        // the placed transform lands on the style's own anchor, in the raster's space — the same
        // point the re-render painted, so the text sits exactly where the descriptor put it.
        var landed = BrushEngine.DocumentToPixel(created.Transform, pixels.Width, pixels.Height, new PointD(60, 70));
        Assert.Equal(anchor.X, landed.X, 3);
        Assert.Equal(anchor.Y, landed.Y, 3);
    }

    /// <summary>One 8×8 raster layer carrying a `TySh` extra block over its own pixels.</summary>
    private static double[] Rotate(double degrees, double tx, double ty)
    {
        var r = degrees * Math.PI / 180;
        return [Math.Cos(r), -Math.Sin(r), Math.Sin(r), Math.Cos(r), tx, ty];
    }

    private static byte[] BuildTextPsd(double[]? matrix = null)
    {
        var outBytes = new List<byte>();
        outBytes.AddRange("8BPS"u8);
        outBytes.AddRange(U16(1));
        outBytes.AddRange(new byte[6]);
        outBytes.AddRange(U16(3));
        outBytes.AddRange(U32(64));
        outBytes.AddRange(U32(64));
        outBytes.AddRange(U16(8));
        outBytes.AddRange(U16(3));
        outBytes.AddRange(U32(0)); // color mode data
        outBytes.AddRange(U32(0)); // image resources

        const int side = 8;
        var records = new List<byte>();
        var channelData = new List<byte>();
        records.AddRange(U32(0)); records.AddRange(U32(0)); records.AddRange(U32(side)); records.AddRange(U32(side));
        records.AddRange(U16(4));
        foreach (var id in new[] { 0, 1, 2, -1 })
        {
            records.AddRange(U16(unchecked((ushort)(short)id)));
            records.AddRange(U32((uint)(side * side + 2)));
            channelData.AddRange(U16(0));
            channelData.AddRange(Enumerable.Repeat((byte)255, side * side));
        }
        records.AddRange("8BIM"u8);
        records.AddRange("norm"u8);
        records.Add(255); records.Add(0); records.Add(0); records.Add(0); // opacity, clipping, flags, filler
        var payload = TySh(matrix: matrix);
        int nameLength = "Title".Length;
        int namePad = (4 - ((nameLength + 1) % 4)) % 4;
        int extraInner = 4 + 4 + 1 + nameLength + namePad + 12 + payload.Length + payload.Length % 2;
        records.AddRange(U32((uint)extraInner));
        records.AddRange(U32(0)); // mask
        records.AddRange(U32(0)); // ranges
        records.Add((byte)nameLength);
        records.AddRange(System.Text.Encoding.ASCII.GetBytes("Title"));
        for (int pad = 0; pad < namePad; pad++) records.Add(0);
        records.AddRange("8BIM"u8);
        records.AddRange("TySh"u8);
        records.AddRange(U32((uint)payload.Length));
        records.AddRange(payload);
        if (payload.Length % 2 == 1) records.Add(0); // layer-extra blocks are even-padded

        var layerInfo = new List<byte>();
        layerInfo.AddRange(U16(1));
        layerInfo.AddRange(records);
        layerInfo.AddRange(channelData);
        outBytes.AddRange(U32((uint)(4 + layerInfo.Count)));
        outBytes.AddRange(U32((uint)layerInfo.Count));
        outBytes.AddRange(layerInfo);
        return [.. outBytes];
    }
}
