using Compositor.Core.Psd;
using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// Photoshop's adjustment records (`levl`, `curv`, `hue2`/`hue `) become live adjustment layers,
/// the Mac's `PSDAdjustments.parse` rules reproduced exactly: Levels in hundredths normalized
/// into the model's ranges, Curves sorted and capped at the 0/255 ends, Hue/Saturation with the
/// Colorize early return and band degrees wrapped into 0–360. A record the model cannot read
/// drops the layer, as the Mac's builder skips `kind == .adjustment` with nothing parsed, and a
/// readable record wins over text and vector keys on the same layer.
/// </summary>
public class PsdAdjustmentTests
{
    private static byte[] U16(int value) => [(byte)(value >> 8), (byte)value];

    private static byte[] U32(int value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static byte[] F64(double value)
    {
        var bits = (ulong)BitConverter.DoubleToInt64Bits(value);
        return [(byte)(bits >> 56), (byte)(bits >> 48), (byte)(bits >> 40), (byte)(bits >> 32),
                (byte)(bits >> 24), (byte)(bits >> 16), (byte)(bits >> 8), (byte)bits];
    }

    private static byte[] Levl(int rgbBlack = 0, int rgbWhite = 255, int outputBlack = 0, int outputWhite = 255, int gammaHundredths = 100)
    {
        // Two leading u16s, then four channel records of five u16s inside the 292-byte block.
        var data = new byte[292];
        void Put(int at, int value) { U16(value).CopyTo(data.AsSpan(at)); }
        Put(2, rgbBlack); Put(4, rgbWhite); Put(6, outputBlack); Put(8, outputWhite); Put(10, gammaHundredths);
        for (int channel = 1; channel < 4; channel++)
        {
            Put(12 + channel * 10, 0); Put(14 + channel * 10, 255);
            Put(16 + channel * 10, 0); Put(18 + channel * 10, 255); Put(20 + channel * 10, 100);
        }
        return data;
    }

    private static byte[] Curv(params (int Channel, (int Output, int Input)[] Points)[] channels)
    {
        var body = new List<byte>();
        body.Add(0); // the leading flag byte the Mac's parser skips
        body.AddRange(U16(1)); // version
        body.AddRange(U16(channels.Length));
        foreach (var (_, points) in channels)
        {
            body.AddRange(U16(points.Length));
            foreach (var (output, input) in points) { body.AddRange(U16(output)); body.AddRange(U16(input)); }
        }
        return [.. body];
    }

    private static byte[] Hue(short masterHue = 0, short masterSat = 0, short masterLight = 0,
        bool colorize = false, (short[] Band, short[] Values)[]? ranges = null)
    {
        var data = new byte[16 + 14 * (ranges?.Length ?? 0)];
        data[2] = (byte)(colorize ? 1 : 0);
        void Put(int offset, short[] triplet)
        {
            U16(triplet[0]).CopyTo(data.AsSpan(offset));
            U16(triplet[1]).CopyTo(data.AsSpan(offset + 2));
            U16(triplet[2]).CopyTo(data.AsSpan(offset + 4));
        }
        // Colorize stores its own values at offset 4; the Master otherwise sits at 10.
        Put(colorize ? 4 : 10, [masterHue, masterSat, masterLight]);
        int offset = 16;
        foreach (var (band, values) in ranges ?? [])
        {
            for (int i = 0; i < 4; i++) U16(band[i]).CopyTo(data.AsSpan(offset + i * 2));
            Put(offset + 8, values);
            offset += 14;
        }
        return data;
    }

    /// <summary>A one-layer PSD whose layer carries the given extra-info blocks.</summary>
    private static byte[] BuildExtraPsd((string Key, byte[] Payload)[] extras, int pixelSide = 0)
    {
        var outBytes = new List<byte>();
        outBytes.AddRange("8BPS"u8);
        outBytes.AddRange(U16(1));
        outBytes.AddRange(new byte[6]);
        outBytes.AddRange(U16(3));
        outBytes.AddRange(U32(64)); outBytes.AddRange(U32(64)); // canvas 64x64
        outBytes.AddRange(U16(8));
        outBytes.AddRange(U16(3));
        outBytes.AddRange(U32(0)); // color mode data
        outBytes.AddRange(U32(0)); // image resources

        var records = new List<byte>();
        var channelData = new List<byte>();
        int planes = pixelSide * pixelSide;
        records.AddRange(U32(0)); // top
        records.AddRange(U32(0)); // left
        records.AddRange(U32(pixelSide)); // bottom
        records.AddRange(U32(pixelSide)); // right
        records.AddRange(U16(pixelSide > 0 ? 4 : 0));
        if (pixelSide > 0)
        {
            foreach (var id in new[] { 0, 1, 2, -1 })
            {
                records.AddRange(U16(id));
                records.AddRange(U32(planes + 2));
                channelData.AddRange(U16(0));
                channelData.AddRange(Enumerable.Repeat((byte)(id == -1 ? 255 : 40), planes));
            }
        }
        records.AddRange("8BIM"u8);
        records.AddRange("norm"u8);
        records.Add(255); records.Add(0); records.Add(0); records.Add(0); // opacity, clipping, flags, filler
        var nameBytes = System.Text.Encoding.ASCII.GetBytes("Adjust");
        int namePad = (4 - ((nameBytes.Length + 1) % 4)) % 4;
        int extraInner = 4 + 4 + 1 + nameBytes.Length + namePad;
        foreach (var (_, payload) in extras) extraInner += 12 + payload.Length + payload.Length % 2;
        records.AddRange(U32(extraInner));
        records.AddRange(U32(0)); // mask data length
        records.AddRange(U32(0)); // blending ranges
        records.Add((byte)nameBytes.Length);
        records.AddRange(nameBytes);
        for (int pad = 0; pad < namePad; pad++) records.Add(0);
        foreach (var (key, payload) in extras)
        {
            records.AddRange("8BIM"u8);
            records.AddRange(System.Text.Encoding.ASCII.GetBytes(key));
            records.AddRange(U32(payload.Length));
            records.AddRange(payload);
            if (payload.Length % 2 == 1) records.Add(0); // even padding, as Photoshop writes
        }

        var layerInfo = new List<byte>();
        layerInfo.AddRange(U16(1));
        layerInfo.AddRange(records);
        layerInfo.AddRange(channelData);
        outBytes.AddRange(U32(4 + layerInfo.Count));
        outBytes.AddRange(U32(layerInfo.Count));
        outBytes.AddRange(layerInfo);
        // The global image section: a 64x64 white composite, which the fallback reads when every
        // layer record has been dropped.
        outBytes.AddRange(U16(0));
        for (int plane = 0; plane < 3; plane++) outBytes.AddRange(Enumerable.Repeat((byte)255, 64 * 64));
        return [.. outBytes];
    }

    [Fact]
    public void LevelsReadFromHundredthsAndNormalizeIntoModelRanges()
    {
        // Input white 300 clamps to 255, gamma 120 becomes 1.2, the other channels stay identity.
        var adjustment = PsdAdjustment.ReadLive(new Dictionary<string, byte[]> { ["levl"] = Levl(10, 300, 20, 235, 120) });
        Assert.NotNull(adjustment);
        Assert.Equal(AdjustmentKind.Levels, adjustment!.Kind);
        var rgb = adjustment.Levels.Ranges[0];
        Assert.Equal(10, rgb.Black); Assert.Equal(255, rgb.White);
        Assert.Equal(20, rgb.OutputBlack); Assert.Equal(235, rgb.OutputWhite);
        Assert.Equal(1.2, rgb.Gamma, 3);
        Assert.Equal(new LevelRange(), adjustment.Levels.Ranges[3]);
        Assert.True(adjustment.IsValid);
    }

    [Fact]
    public void LevelsShorterThanTheRecordTheMacDemandsAreRefused()
    {
        // The Mac's guard is the full 292-byte block; a shorter record is not Levels at all.
        Assert.Null(PsdAdjustment.ReadLive(new Dictionary<string, byte[]> { ["levl"] = new byte[120] }));
    }

    [Fact]
    public void CurvesSortAndCapTheChannelEnds()
    {
        // Output-before-input pairs, given out of order: the curve sorts by x and extends to 0/255.
        var adjustment = PsdAdjustment.ReadLive(new Dictionary<string, byte[]>
        {
            ["curv"] = Curv((0, [(200, 128), (0, 0)])),
        });
        Assert.NotNull(adjustment);
        Assert.Equal(AdjustmentKind.Curves, adjustment!.Kind);
        var curve = adjustment.Curves.Channels[0];
        Assert.Equal(new[] { 0d, 128d, 255d }, curve.Select(point => point.X));
        Assert.Equal(new[] { 0d, 200d, 200d }, curve.Select(point => point.Y));
        Assert.Equal(new List<CurvePoint> { new(0, 0), new(255, 255) }, adjustment.Curves.Channels[3]);
        Assert.True(adjustment.IsValid);
    }

    [Fact]
    public void CurvesOfAnUnknownVersionBuildNoAdjustment()
    {
        // The Mac accepts only v1 and v4; anything else is not a curve we can place.
        byte[] data = [.. U16(2), .. U16(1), .. U16(2), .. U16(0), .. U16(0), .. U16(255), .. U16(255)];
        Assert.Null(PsdAdjustment.ReadLive(new Dictionary<string, byte[]> { ["curv"] = data }));
    }

    [Fact]
    public void HueMasterAndBandsWrapDegreesIntoTheModel()
    {
        var adjustment = PsdAdjustment.ReadLive(new Dictionary<string, byte[]>
        {
            ["hue2"] = Hue(15, -20, 5, ranges: [([-30, 0, 30, 60], [(short)10, (short)20, (short)0])]),
        });
        Assert.NotNull(adjustment);
        Assert.Equal(AdjustmentKind.HueSaturation, adjustment!.Kind);
        var hsv = adjustment.HsvSettings!;
        Assert.Equal(15, hsv.Adjustments[nameof(ColorRange.Master)].Hue);
        Assert.Equal(-20, hsv.Adjustments[nameof(ColorRange.Master)].Saturation);
        var band = hsv.Bands[nameof(ColorRange.Reds)];
        Assert.Equal(330, band.FalloffStart); // -30 wraps to 330
        Assert.Equal(0, band.RangeStart);
        Assert.Equal(30, band.RangeEnd);
        Assert.Equal(60, band.FalloffEnd);
        Assert.Equal(10, hsv.Adjustments[nameof(ColorRange.Reds)].Hue);
        Assert.True(adjustment.IsValid);
    }

    [Fact]
    public void ColorizeKeepsOnlyItsOwnValues()
    {
        // The Mac returns early for colorize: its triple sits at offset 4, and there are no bands.
        var adjustment = PsdAdjustment.ReadLive(new Dictionary<string, byte[]>
        {
            ["hue2"] = Hue(colorize: true, masterHue: 120, masterSat: 40, masterLight: -10),
        });
        Assert.NotNull(adjustment);
        var hsv = adjustment!.HsvSettings!;
        Assert.True(adjustment.Colorize);
        Assert.True(hsv.Colorize);
        Assert.Equal(120, hsv.Adjustments[nameof(ColorRange.Master)].Hue);
        Assert.Equal(40, hsv.Adjustments[nameof(ColorRange.Master)].Saturation);
        Assert.Empty(hsv.Bands);
    }

    [Fact]
    public void AReaderSeesAdjustmentRecordsAsLiveLayersWithoutPixels()
    {
        var document = PsdReader.Read(BuildExtraPsd([("levl", Levl())]));
        var layer = Assert.Single(document.Layers, candidate => !candidate.IsGroup);
        Assert.NotNull(layer.Adjustment);
        Assert.Equal(AdjustmentKind.Levels, layer.Adjustment!.Kind);
        Assert.Null(layer.Pixels); // the Mac's adjustment branch carries no asset
        var scene = EditorDocument.Create(64, 64);
        var created = Assert.Single(scene.ImportPsd(document));
        Assert.Equal(AdjustmentKind.Levels, created.Adjustment!.Kind);
        Assert.Null(created.ImageFile);
        Assert.Equal(64, created.Transform.Size.Width); // canvas-sized, like AddAdjustment
        Assert.True(scene.Undo()); Assert.True(scene.Redo()); // one step, and it restores

        // Live, not inert: the parsed values pass through the same renderer the panel edits drive.
        var gray = new Surface(64, 64);
        for (int i = 0; i < gray.Pixels.Length; i += 4) { gray.Pixels[i] = 128; gray.Pixels[i + 1] = 128; gray.Pixels[i + 2] = 128; gray.Pixels[i + 3] = 255; }
        var darkened = EditorDocument.Create(64, 64);
        darkened.ImportImage(gray, "Base");
        var importLevels = Assert.Single(darkened.ImportPsd(PsdReader.Read(BuildExtraPsd([("levl", Levl(128, 255))]))));
        int at = (32 * 64 + 32) * 4;
        Assert.Equal(0, new CanvasCompositor(darkened.Snapshot).Render().Pixels[at]); // black point 128 crushes the gray

        // An unreadable record drops the layer even when it carries decoded pixels — kind reads
        // as adjustment and nothing parsed, so the Mac skips it; only the merged fallback remains.
        var corrupt = PsdReader.Read(BuildExtraPsd([("levl", new byte[64])], pixelSide: 8));
        var survivor = Assert.Single(corrupt.Layers, candidate => !candidate.IsGroup && candidate.Adjustment is null);
        Assert.NotEqual(8, survivor.Pixels?.Width); // our 8x8 layer was dropped; this is the composite
    }

    [Fact]
    public async Task APlacedAdjustmentSurvivesTheProjectRoundTrip()
    {
        // The adjustment lives in the shared manifest contract: save validates it (no pixels,
        // valid settings, version 7+), reload brings the parsed values back intact.
        var document = PsdReader.Read(BuildExtraPsd([("levl", Levl(10, 200, 5, 250, 150))]));
        var scene = EditorDocument.Create(64, 64);
        var created = Assert.Single(scene.ImportPsd(document));
        string path = Path.Combine(Path.GetTempPath(), "compositor-adjust-" + Guid.NewGuid().ToString("N") + ".comp");
        try
        {
            await ProjectStore.SaveAsync(scene.CaptureSave().Snapshot, path);
            var restored = await ProjectStore.LoadAsync(path);
            var layer = Assert.Single(restored.Manifest.Layers, candidate => candidate.Adjustment is not null);
            Assert.Equal(AdjustmentKind.Levels, layer.Adjustment!.Kind);
            var rgb = layer.Adjustment.Levels.Ranges[0];
            Assert.Equal(10, rgb.Black); Assert.Equal(200, rgb.White);
            Assert.Equal(5, rgb.OutputBlack); Assert.Equal(250, rgb.OutputWhite);
            Assert.Equal(1.5, rgb.Gamma, 3);
            Assert.Null(layer.ImageFile); // an adjustment never carries pixels in the manifest
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    [Fact]
    public void AReadableAdjustmentBeatsTypeAndShapeKeysOnTheSameLayer()
    {
        // kind() may label the layer text or vector, but the builder's adjustment branch sits above
        // both, so a parsed record ignores the type and the decoded pixels alike.
        var document = PsdReader.Read(BuildExtraPsd([("TySh", VerticalTypeBytes()), ("levl", Levl())]));
        var layer = Assert.Single(document.Layers, candidate => !candidate.IsGroup);
        Assert.NotNull(layer.Adjustment);
        Assert.Null(layer.Text); // never parsed: the text branch sits under the adjustment one
    }

    [Fact]
    public void AFailedTextParseStillRunsTheVectorBranch()
    {
        // The Mac's chain is an : kind() labels the layer text, but when the TySh parse
        // fails (here: vertical type) the vector branch still runs, so the fill record lives.
        var document = PsdReader.Read(BuildExtraPsd([("TySh", VerticalTypeBytes()), ("SoCo", Solid()), ("vogk", Rect())]));
        var layer = Assert.Single(document.Layers, candidate => !candidate.IsGroup);
        Assert.NotNull(layer.Shape);
        Assert.Null(layer.Text);
    }

    /// <summary>The SoCo descriptor: Rd/Grn/Bl doubles, the shape PsdVector reads.</summary>
    private static byte[] Solid()
    {
        static byte[] Doub(string key, double value) => [.. System.Text.Encoding.ASCII.GetBytes(key), .. System.Text.Encoding.ASCII.GetBytes("doub"), .. F64(value)];
        return [.. System.Text.Encoding.ASCII.GetBytes("Objc"), .. U32(0), .. Doub("Rd  ", 1), .. Doub("Grn ", 0), .. Doub("Bl  ", 0)];
    }

    private static byte[] Rect()
    {
        static byte[] Unit(string key, double value) => [.. System.Text.Encoding.ASCII.GetBytes(key), .. System.Text.Encoding.ASCII.GetBytes("UntF"),
            .. System.Text.Encoding.ASCII.GetBytes("Pxl "), .. F64(value)];
        return [.. System.Text.Encoding.ASCII.GetBytes("keyOriginType"), .. System.Text.Encoding.ASCII.GetBytes("long"), .. U32(1),
            .. System.Text.Encoding.ASCII.GetBytes("keyOriginShapeBBox"),
            .. Unit("Left", 4), .. Unit("Top ", 6), .. Unit("Rght", 24), .. Unit("Btom", 16)];
    }

    private static byte[] VerticalTypeBytes()
    {
        // A minimal TySh: version 1, identity matrix, text version 50, one `Ornt = Vrtc` item.
        var body = new List<byte>();
        body.AddRange(U16(1));
        foreach (var value in new double[] { 1, 0, 0, 1, 5, 5 }) body.AddRange(F64(value));
        body.AddRange(U16(50));
        body.AddRange(U32(16)); // the versioned form's leading count
        body.AddRange(U32(1)); body.AddRange([(byte)0, (byte)'t']); // class, one UTF-16 unit
        body.AddRange(U32(0)); body.AddRange("text"u8); // class id
        body.AddRange(U32(1)); // one item
        body.AddRange(U32(0)); body.AddRange("Ornt"u8); // key
        body.AddRange("enum"u8); // type
        body.AddRange(U32(0)); body.AddRange("text"u8); // enum type
        body.AddRange(U32(0)); body.AddRange("Vrtc"u8); // enum value: vertical
        return [.. body];
    }
}
