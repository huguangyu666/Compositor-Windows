using Compositor.Core.Psd;
using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// The PSD conversion report, collected the way the Mac's builder assembles it: cropped first,
/// then the kind's own loss (type that cannot be edited, smart objects, discarded effects,
/// unplaced vectors), then the blend fallbacks, then the adjustment's approximate warning — with
/// the reader's skipped records riding both import routes. These tests run the batch route
/// (AddPhotoshop — the path the app's picker and drop actually take) against hand-built
/// descriptor bytes, so every line is under test control.
/// </summary>
public class PsdConversionReportTests
{
    private static byte[] U16(ushort value) => [(byte)(value >> 8), (byte)value];

    private static byte[] U32(uint value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static byte[] F64(double value)
    {
        var bits = (ulong)BitConverter.DoubleToInt64Bits(value);
        return [(byte)(bits >> 56), (byte)(bits >> 48), (byte)(bits >> 40), (byte)(bits >> 32),
                (byte)(bits >> 24), (byte)(bits >> 16), (byte)(bits >> 8), (byte)bits];
    }

    private static byte[] Bytes(string text) => System.Text.Encoding.ASCII.GetBytes(text);

    private static byte[] Unicode(string text) => [.. U32((uint)text.Length), .. System.Text.Encoding.BigEndianUnicode.GetBytes(text)];

    private static byte[] Identifier(string text) => text.Length == 4 ? [.. U32(0), .. Bytes(text)] : [.. U32((uint)text.Length), .. Bytes(text)];

    private static byte[] Descriptor(string classId, params (string Key, string Type, byte[] Payload)[] items)
    {
        var body = new List<byte>();
        body.AddRange(U32(16)); // the versioned form's leading count
        body.AddRange(Unicode(classId));
        body.AddRange(Identifier(classId));
        body.AddRange(U32((uint)items.Length));
        foreach (var (key, type, payload) in items)
            body.AddRange([.. Identifier(key), .. Bytes(type), .. payload]);
        return [.. body];
    }

    /// <summary>The TySh head: version, the matrix, text version 50, the text descriptor.</summary>
    private static byte[] TySh((string Key, string Type, byte[] Payload)[] items, double[]? matrix = null)
    {
        var m = matrix ?? [1d, 0d, 0d, 1d, 10d, 20d];
        var body = new List<byte>();
        body.AddRange(U16(1));
        foreach (var value in m) body.AddRange(F64(value));
        body.AddRange(U16(50));
        body.AddRange(Descriptor("textLayer", items));
        return [.. body];
    }

    private static (string, string, byte[]) Engine(string text)
    {
        var raw = System.Text.Encoding.ASCII.GetBytes(text);
        return ("EngineData", "tdta", [.. U32((uint)raw.Length), .. raw]);
    }

    /// <summary>A horizontal-orientation text record; extra items append, so a later Txt wins.</summary>
    private static byte[] TextTySh(params (string Key, string Type, byte[] Payload)[] extraItems)
    {
        var items = new List<(string, string, byte[])>
        {
            ("Txt ", "TEXT", Unicode("Hi")),
            ("Ornt", "enum", [.. Identifier("textOrientation"), .. Identifier("Hrzn")]),
        };
        items.AddRange(extraItems);
        return TySh([.. items]);
    }

    private static byte[] SolidColor(double r = 1, double g = 0, double b = 0) =>
        [.. Bytes("SbBl"), .. Bytes("Objc"), .. U32(1), .. Bytes("Objc"), .. Bytes("SoCo"),
        .. Bytes("Rd  "), .. Bytes("doub"), .. F64(r), .. Bytes("Grn "), .. Bytes("doub"), .. F64(g),
        .. Bytes("Bl  "), .. Bytes("doub"), .. F64(b)];

    private static byte[] Origination(int type, double left, double top, double right, double bottom)
    {
        var body = new List<byte>();
        body.AddRange(Bytes("keyOriginType")); body.AddRange(Bytes("long")); body.AddRange(U32((uint)type));
        body.AddRange(Bytes("keyOriginShapeBBox")); body.AddRange(Bytes("Objc")); body.AddRange(U32(4));
        body.AddRange(Bytes("Left")); body.AddRange(Bytes("UntF")); body.AddRange(Bytes("Pxl ")); body.AddRange(F64(left));
        body.AddRange(Bytes("Top ")); body.AddRange(Bytes("UntF")); body.AddRange(Bytes("Pxl ")); body.AddRange(F64(top));
        body.AddRange(Bytes("Rght")); body.AddRange(Bytes("UntF")); body.AddRange(Bytes("Pxl ")); body.AddRange(F64(right));
        body.AddRange(Bytes("Btom")); body.AddRange(Bytes("UntF")); body.AddRange(Bytes("Pxl ")); body.AddRange(F64(bottom));
        var header = new List<byte> { 0x00, 0x10 };
        header.AddRange("8BIM"u8); header.AddRange(Bytes("Vrsn")); header.AddRange(Bytes("long")); header.AddRange(U32(1));
        header.AddRange(U32(1)); header.AddRange(Bytes("Vrsn"));
        header.AddRange([0, 0, 0, 0, 0, 0, 0, 1]); // long 1
        header.AddRange(Bytes("Objc"));
        return [.. header, .. body];
    }

    /// <summary>A one-layer 64×64 PSD carrying the given extra-info blocks.</summary>
    private static byte[] BuildPsd((string Key, byte[] Payload)[] extras, int pixelSide = 8, string blendKey = "norm", string name = "Layer")
    {
        var outBytes = new List<byte>();
        outBytes.AddRange("8BPS"u8);
        outBytes.AddRange(U16(1));
        outBytes.AddRange(new byte[6]);
        outBytes.AddRange(U16(3));
        outBytes.AddRange(U32(64)); outBytes.AddRange(U32(64));
        outBytes.AddRange(U16(8));
        outBytes.AddRange(U16(3));
        outBytes.AddRange(U32(0));
        outBytes.AddRange(U32(0));

        var records = new List<byte>();
        var channelData = new List<byte>();
        int planes = pixelSide * pixelSide;
        records.AddRange(U32(0)); records.AddRange(U32(0));
        records.AddRange(U32((uint)pixelSide)); records.AddRange(U32((uint)pixelSide));
        records.AddRange(U16((ushort)(pixelSide > 0 ? 4 : 0)));
        if (pixelSide > 0)
        {
            foreach (var id in new[] { 0, 1, 2, -1 })
            {
                records.AddRange(U16(unchecked((ushort)(short)id)));
                records.AddRange(U32((uint)(planes + 2)));
                channelData.AddRange(U16(0));
                channelData.AddRange(Enumerable.Repeat((byte)(id == -1 ? 255 : 40), planes));
            }
        }
        records.AddRange("8BIM"u8);
        records.AddRange(Bytes(blendKey));
        records.Add(255); records.Add(0); records.Add(0); records.Add(0);
        var nameBytes = Bytes(name);
        int namePad = (4 - ((nameBytes.Length + 1) % 4)) % 4;
        int extraInner = 4 + 4 + 1 + nameBytes.Length + namePad;
        foreach (var (_, payload) in extras) extraInner += 12 + payload.Length + payload.Length % 2;
        records.AddRange(U32((uint)extraInner));
        records.AddRange(U32(0)); // mask
        records.AddRange(U32(0)); // ranges
        records.Add((byte)nameBytes.Length);
        records.AddRange(nameBytes);
        for (int pad = 0; pad < namePad; pad++) records.Add(0);
        foreach (var (key, payload) in extras)
        {
            records.AddRange("8BIM"u8);
            records.AddRange(Bytes(key));
            records.AddRange(U32((uint)payload.Length));
            records.AddRange(payload);
            if (payload.Length % 2 == 1) records.Add(0);
        }

        var layerInfo = new List<byte>();
        layerInfo.AddRange(U16(1));
        layerInfo.AddRange(records);
        layerInfo.AddRange(channelData);
        outBytes.AddRange(U32((uint)(4 + layerInfo.Count)));
        outBytes.AddRange(U32((uint)layerInfo.Count));
        outBytes.AddRange(layerInfo);
        // The global image section, so a file whose layers all skip still has the composite.
        outBytes.AddRange(U16(0));
        for (int plane = 0; plane < 3; plane++) outBytes.AddRange(Enumerable.Repeat((byte)255, 64 * 64));
        return [.. outBytes];
    }

    /// <summary>Imports through the batch route — the app's own path — returning report and scene.</summary>
    /// <summary>The imported layer inside the batch's wrapper group; the scene's own background
    /// layer would otherwise match a plain "non-group" search.</summary>
    private static ProjectLayerRecord Imported(EditorDocument scene, string name = "Layer") =>
        Assert.Single(scene.Snapshot.Manifest.Layers, layer => layer.Name == name);

    private static (List<PsdConversion> Conversions, EditorDocument Scene) ImportBatch(byte[] psd, string name = "Doc")
    {
        var scene = EditorDocument.Create(64, 64);
        var conversions = new List<PsdConversion>();
        using var batch = scene.BeginFileImport();
        batch.AddPhotoshop(PsdReader.Read(psd), name, conversions);
        return (conversions, scene);
    }

    [Fact]
    public void TheBatchRouteAttachesTheLiveShapeRecord()
    {
        // The app's picker and drop run AddPhotoshop; a fill record must become an editable shape
        // there too — the earlier chunks proved only the direct ImportPsd route.
        var (conversions, scene) = ImportBatch(BuildPsd([("SoCo", SolidColor()), ("vogk", Origination(1, 4, 6, 24, 16))]));
        var layer = Assert.Single(scene.Snapshot.Manifest.Layers, candidate => candidate.Shape is not null);
        var style = ShapeStyle.FromElement(layer.Shape)!;
        Assert.Equal(ShapeKind.Rectangle, style.Kind);
        var pixels = scene.Snapshot.Images[layer.Id];
        Assert.True(pixels.Pixels.SequenceEqual(ShapeRasterizer.Draw(style, pixels.Width, pixels.Height).Pixels));
        Assert.DoesNotContain(conversions, line => line.Message == PsdConversion.VectorRasterized);
    }

    [Fact]
    public void TheBatchRouteMakesAdjustmentRecordsLiveWithoutPixels()
    {
        var levl = new byte[292];
        U16(10).CopyTo(levl, 2); U16(200).CopyTo(levl, 4); U16(5).CopyTo(levl, 6); U16(250).CopyTo(levl, 8); U16(150).CopyTo(levl, 10);
        var (conversions, scene) = ImportBatch(BuildPsd([("levl", levl)]));
        var layer = Assert.Single(scene.Snapshot.Manifest.Layers, candidate => candidate.Adjustment is not null);
        Assert.Equal(AdjustmentKind.Levels, layer.Adjustment!.Kind);
        Assert.Null(layer.ImageFile); // the Mac's adjustment branch carries no asset
        Assert.DoesNotContain(layer.Id, scene.Snapshot.Images.Keys);
        Assert.Contains(conversions, line => line.Message == PsdConversion.AdjustmentApproximate && line.LayerName == "Layer");
    }

    [Fact]
    public void AnUnreadableAdjustmentRecordIsReportedEvenWhenTheLayerIsDropped()
    {
        // expA is an adjustment key nothing parses: the Mac skips the layer but keeps the report
        // line. Through the batch route a fully-skipped file still flattens to the composite.
        var (conversions, scene) = ImportBatch(BuildPsd([("expA", new byte[64])]));
        var line = Assert.Single(conversions, candidate => candidate.Message == PsdConversion.AdjustmentSkipped);
        Assert.Equal("Layer", line.LayerName);
        Assert.Contains(scene.Snapshot.Manifest.Layers, candidate => candidate.ImageFile is not null);
    }

    [Fact]
    public void SmartObjectAndEffectsKindsReportTheirLoss()
    {
        foreach (var (key, expectSmart) in new[] { ("SoLd", true), ("SoLE", true), ("lfx2", false), ("lrFX", false), ("lmfx", false) })
        {
            var (conversions, _) = ImportBatch(BuildPsd([(key, [0, 0, 0, 0])]));
            string expected = expectSmart ? PsdConversion.SmartObjectRasterized : PsdConversion.EffectsDiscarded;
            Assert.Contains(conversions, line => line.Message == expected && line.LayerName == "Layer");
        }
    }

    [Fact]
    public void VectorKeysThatCannotPlaceKeepPixelsAndSaySo()
    {
        // vmsk keys are a vector kind; with no placeable path the record keeps its raster and the
        // Mac reports the fallback.
        var (conversions, scene) = ImportBatch(BuildPsd([("vmsk", [0, 0, 0, 0])]));
        Assert.Contains(conversions, line => line.Message == PsdConversion.VectorRasterized && line.LayerName == "Layer");
        var layer = Imported(scene);
        Assert.NotNull(layer.ImageFile); // its own pixels, kept
    }

    [Fact]
    public void UnsupportedLayerBlendReportsItsFallback()
    {
        var (conversions, _) = ImportBatch(BuildPsd([], blendKey: "nope"));
        Assert.Contains(conversions, line => line.Message ==
            "Blend mode \u201cnope\u201d isn\u2019t supported and will be applied as Normal.");
    }

    [Fact]
    public void UnsupportedFolderBlendReportsTheRawKeyAndPassThrough()
    {
        // Groups take the pass-through line with the key unmodified, as the Mac writes it.
        var folder = new PsdLayer
        {
            Id = Guid.NewGuid(), Name = "Folder", IsGroup = true, Offset = new PointD(0, 0),
            BlendKey = "mul ", Kind = PsdLayerKind.Group,
        };
        var document = new PsdDocument { Width = 16, Height = 16, Layers = [folder] };
        var scene = EditorDocument.Create(16, 16);
        var conversions = new List<PsdConversion>();
        scene.ImportPsd(document, conversions);
        Assert.Contains(conversions, line => line.Message ==
            "Folder blend mode \u201cmul \u201d isn\u2019t supported. The folder will be pass-through." && line.LayerName == "Folder");
    }

    [Fact]
    public void EditableTextReportsItsOwnNotesWhenTheFontIsInstalled()
    {
        // A faux-bold style that does render says the bold was omitted. Segoe UI ships with
        // Windows, so no substitution line rides here.
        var engine = Engine("<< /EngineDict << /StyleRun << /RunArray [ << /StyleSheet << /StyleSheetData " +
            "<< /FontSize 10 /Font 0 /FauxBold true >> >> >> ] >> >> /ResourceDict << /FontSet [ << /Name (Segoe UI) >> ] >> >>");
        var (conversions, scene) = ImportBatch(BuildPsd([("TySh", TextTySh(engine))]));
        Assert.Contains(conversions, line => line.Message == PsdText.FauxNote);
        var layer = Assert.Single(scene.Snapshot.Manifest.Layers, candidate => candidate.Text is not null);
        Assert.NotNull(layer.Text);
    }

    [Fact]
    public void AFaceThatIsNotInstalledSaysTheSystemFontDrewTheText()
    {
        var engine = Engine("<< /EngineDict << /StyleRun << /RunArray [ << /StyleSheet << /StyleSheetData " +
            "<< /FontSize 10 /Font 0 >> >> >> ] >> >> /ResourceDict << /FontSet [ << /Name (NoSuchFaceXYZ) >> ] >> >>");
        var (conversions, _) = ImportBatch(BuildPsd([("TySh", TextTySh(engine))]));
        Assert.Contains(conversions, line => line.Message ==
            "The font \u201cNoSuchFaceXYZ\u201d isn\u2019t installed, so the text was drawn with the system font.");
    }

    [Fact]
    public void TextThatCannotRenderReportsItBecamePixels()
    {
        // A point run so wide the box cannot exist: the render throws, the style is dropped, and
        // the Mac's rasterize line covers it — the layer keeps its decoded pixels.
        var engine = Engine("<< /EngineDict << /StyleRun << /RunArray [ << /StyleSheet << /StyleSheetData " +
            "<< /FontSize 2000 /Font 0 >> >> >> ] >> >> /ResourceDict << /FontSet [ << /Name (Segoe UI) >> ] >> >>");
        var wide = ("Txt ", "TEXT", Unicode(string.Join("", Enumerable.Repeat("Wide", 2000))));
        var (conversions, scene) = ImportBatch(BuildPsd([("TySh", TextTySh(engine, wide))]));
        Assert.Contains(conversions, line => line.Message == PsdText.RasterizedNote);
        var layer = Imported(scene);
        Assert.Null(layer.Text); // the failed render left the pixels and dropped the style
        Assert.NotNull(layer.ImageFile);
    }

    [Fact]
    public void TheKindLinesComeBeforeTheBlendLineOfTheSameLayer()
    {
        // The Mac's notes pass appends kind lines first and the blend fallback after them; a
        // effects layer with an unsupported key must report in that order.
        var (conversions, _) = ImportBatch(BuildPsd([("lfx2", [0, 0, 0, 0])], blendKey: "nope"));
        int effectsIndex = conversions.FindIndex(line => line.Message == PsdConversion.EffectsDiscarded);
        int blendIndex = conversions.FindIndex(line => line.Message ==
            "Blend mode \u201cnope\u201d isn\u2019t supported and will be applied as Normal.");
        Assert.True(effectsIndex >= 0 && blendIndex > effectsIndex,
            $"report order wrong: {string.Join(" | ", conversions.Select(line => line.Message))}");
    }

    [Fact]
    public void VerticalTypeOverAFillRecordStillBecomesItsShapeWithoutTheRasterizeLine()
    {
        // The Mac relabels by what parsed after the chain: the failed type parse lets the vector
        // branch place the shape, and the final kind (vector) decides the notes — no rasterize line.
        var head = new List<byte>();
        head.AddRange(U16(1));
        foreach (var value in new double[] { 1, 0, 0, 1, 5, 5 }) head.AddRange(F64(value));
        head.AddRange(U16(50));
        head.AddRange(Descriptor("textLayer",
            ("Txt ", "TEXT", Unicode("V")),
            ("Ornt", "enum", [.. Identifier("textOrientation"), .. Identifier("Vrtc")])));
        var (conversions, scene) = ImportBatch(BuildPsd([
            ("TySh", [.. head]), ("SoCo", SolidColor()), ("vogk", Origination(1, 4, 6, 24, 16)),
        ]));
        var layer = Assert.Single(scene.Snapshot.Manifest.Layers, candidate => candidate.Shape is not null);
        Assert.Null(layer.Text);
        Assert.DoesNotContain(conversions, line => line.Message == PsdText.RasterizedNote);
        Assert.DoesNotContain(conversions, line => line.Message == PsdConversion.VectorRasterized);
    }
}
