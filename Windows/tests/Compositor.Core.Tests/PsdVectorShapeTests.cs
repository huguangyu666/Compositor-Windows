using Compositor.Core.Psd;
using System.IO;
using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// The Mac reads Photoshop's fill records (`SoCo` + `vogk`, or a sharp `vmsk`/`vsms` rectangle)
/// into a live shape layer: the style lands in the manifest, the pixels are the shape tool's own
/// raster, and the placement follows the record's box. Strokes are noted and omitted; a curved
/// bézier path needs the raster engine the port has not settled, so those layers fall through to
/// their bitmap — or away — exactly as the Mac's reader falls through.
/// </summary>
public class PsdVectorShapeTests
{
    private static byte[] U32(uint value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static byte[] U16(ushort value) => [(byte)(value >> 8), (byte)value];

    private static byte[] Doub(double value) => U32(unchecked((uint)(BitConverter.DoubleToInt64Bits(value) >> 32))).Concat(U32(unchecked((uint)BitConverter.DoubleToInt64Bits(value)))).ToArray();

    private static byte[] Bytes(string text) => System.Text.Encoding.ASCII.GetBytes(text);

    /// <summary>`Rd  `/`Grn `/`Bl  ` doubles, the SoCo descriptor's shape the Mac parses.</summary>
    private static byte[] SolidColor(double r, double g, double b) => [.. Bytes("SbBl"), .. Bytes("Objc"), .. U32(1), .. Bytes("Objc"), .. Bytes("SoCo"),
        .. Bytes("Rd  "), .. Bytes("doub"), .. Doub(r), .. Bytes("Grn "), .. Bytes("doub"), .. Doub(g), .. Bytes("Bl  "), .. Bytes("doub"), .. Doub(b)];

    /// <summary>`keyOriginType` + the shape box in `UntF` units, the `vogk` origination record.</summary>
    private static byte[] Origination(int type, double left, double top, double right, double bottom, double[]? radii = null)
    {
        var body = new List<byte>();
        body.AddRange(Bytes("keyOriginType")); body.AddRange(Bytes("long")); body.AddRange(U32((uint)type));
        body.AddRange(Bytes("keyOriginShapeBBox")); body.AddRange(Bytes("Objc")); body.AddRange(U32(4));
        body.AddRange(Bytes("Left")); body.AddRange(Bytes("UntF")); body.AddRange(Bytes("Pxl ")); body.AddRange(Doub(left));
        body.AddRange(Bytes("Top ")); body.AddRange(Bytes("UntF")); body.AddRange(Bytes("Pxl ")); body.AddRange(Doub(top));
        body.AddRange(Bytes("Rght")); body.AddRange(Bytes("UntF")); body.AddRange(Bytes("Pxl ")); body.AddRange(Doub(right));
        body.AddRange(Bytes("Btom")); body.AddRange(Bytes("UntF")); body.AddRange(Bytes("Pxl ")); body.AddRange(Doub(bottom));
        if (radii is { Length: 4 })
        {
            body.AddRange(Bytes("keyOriginRRectRadii"));
            foreach (var (key, value) in new[] { ("topLeft", radii[0]), ("topRight", radii[1]), ("bottomRight", radii[2]), ("bottomLeft", radii[3]) })
            {
                body.AddRange(Bytes(key)); body.AddRange(Bytes("UntF")); body.AddRange(Bytes("Pxl ")); body.AddRange(Doub(value));
            }
        }
        var header = new List<byte> { 0x00, 0x10 };
        header.AddRange(Bytes("8BIM")); header.AddRange(Bytes("Vrsn")); header.AddRange(U32(1));
        header.AddRange(Bytes("Vrsn")); header.AddRange(Bytes("long")); header.AddRange(U32(1));
        header.AddRange(Bytes("Objc")); // class follows, as in a real descriptor's count
        return [.. header, .. body];
    }

    /// <summary>`vstk` with the stroke flag the Mac reads before dropping the stroke.</summary>
    private static byte[] Stroke(bool fillEnabled, bool strokeEnabled)
    {
        var body = new List<byte>();
        body.AddRange(Bytes("fillEnabled")); body.AddRange(Bytes("bool")); body.Add((byte)(fillEnabled ? 1 : 0));
        body.AddRange(Bytes("strokeEnabled")); body.AddRange(Bytes("bool")); body.Add((byte)(strokeEnabled ? 1 : 0));
        return [0x00, 0x10, .. Bytes("8BIM"), .. body];
    }

    /// <summary>Four sharp anchors on a 0–1 normalized grid: the path the `vsms` rectangle holds.</summary>
    private static byte[] SharpMask(int canvasW, int canvasH)
    {
        var body = new List<byte>();
        body.AddRange(U32(0)); body.AddRange(U32(1)); // version + one subpath, the eight skipped bytes
        body.AddRange(U16(0)); body.AddRange(U16(4)); // a closed subpath of four anchors...
        body.AddRange(new byte[22]); // ...and its record is 26 bytes like every other
        static byte[] Fixed(double value) => U32(unchecked((uint)(value * 0x1000000)));
        void Anchor(double x, double y)
        {
            body.AddRange(U16(1)); // closed, linked anchor: incoming, anchor, outgoing [y][x] pairs
            body.AddRange(Fixed(y)); body.AddRange(Fixed(x)); // normalized 0–1; point() scales by the canvas
            body.AddRange(Fixed(y)); body.AddRange(Fixed(x));
            body.AddRange(Fixed(y)); body.AddRange(Fixed(x));
        }
        Anchor(0.25, 0.25); Anchor(0.75, 0.25); Anchor(0.75, 0.75); Anchor(0.25, 0.75);
        return [.. body];
    }

    /// <summary>A rectangle whose outgoing handle reaches away: a curved path, not a live shape.</summary>
    private static byte[] CurvedMask(int canvasW, int canvasH)
    {
        var body = new List<byte>();
        body.AddRange(U32(0)); body.AddRange(U32(1)); // version + one subpath
        body.AddRange(U16(0)); body.AddRange(U16(4)); // closed subpath of four anchors, 26-byte record
        body.AddRange(new byte[22]);
        static byte[] Fixed(double value) => U32(unchecked((uint)(value * 0x1000000)));
        void Anchor(double x, double y, double outX, double outY)
        {
            body.AddRange(U16(1));
            body.AddRange(Fixed(y)); body.AddRange(Fixed(x));
            body.AddRange(Fixed(y)); body.AddRange(Fixed(x));
            body.AddRange(Fixed(outY)); body.AddRange(Fixed(outX));
        }
        Anchor(0.25, 0.25, 0.4, 0.3); Anchor(0.75, 0.25, 0.75, 0.25); Anchor(0.75, 0.75, 0.75, 0.75); Anchor(0.25, 0.75, 0.25, 0.75);
        return [.. body];
    }

    /// <summary>A fill record: a layer whose channels and box are empty, carrying extra blocks.</summary>
    private static byte[] BuildShapePsd(string name, (string Key, byte[] Payload)[] extras,
        (int Top, int Left, int W, int H)? bitmap = null)
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

        var records = new List<byte>();
        var channelData = new List<byte>();
        int top = bitmap?.Top ?? 0, left = bitmap?.Left ?? 0, w = bitmap?.W ?? 0, h = bitmap?.H ?? 0;
        records.AddRange(U32((uint)top));
        records.AddRange(U32((uint)left));
        records.AddRange(U32((uint)(top + h)));
        records.AddRange(U32((uint)(left + w)));
        var planes = bitmap is null ? Array.Empty<(int Id, byte[] Data)>() : new[]
        {
            (0, Enumerable.Repeat((byte)200, w * h).ToArray()),
            (1, Enumerable.Repeat((byte)20, w * h).ToArray()),
            (2, Enumerable.Repeat((byte)30, w * h).ToArray()),
            (-1, Enumerable.Repeat((byte)255, w * h).ToArray()),
        };
        records.AddRange(U16((ushort)planes.Length));
        foreach (var (id, data) in planes)
        {
            records.AddRange(U16(unchecked((ushort)(short)id)));
            records.AddRange(U32((uint)(data.Length + 2)));
            channelData.AddRange(U16(0));
            channelData.AddRange(data);
        }
        records.AddRange("8BIM"u8);
        records.AddRange("norm"u8);
        records.Add(255); records.Add(0); records.Add(0); records.Add(0);
        var nameBytes = System.Text.Encoding.ASCII.GetBytes(name);
        int namePad = (4 - ((nameBytes.Length + 1) % 4)) % 4;
        int extraInner = 4 + 4 + 1 + nameBytes.Length + namePad;
        // Layer-extra blocks are padded to even byte counts, as the reader's odd-length skip expects.
        foreach (var (_, payload) in extras) extraInner += 12 + payload.Length + (payload.Length % 2);
        records.AddRange(U32((uint)extraInner));
        records.AddRange(U32(0)); // mask
        records.AddRange(U32(0)); // ranges
        records.Add((byte)nameBytes.Length);
        records.AddRange(nameBytes);
        for (int pad = 0; pad < namePad; pad++) records.Add(0);
        foreach (var (key, payload) in extras)
        {
            records.AddRange("8BIM"u8);
            records.AddRange(System.Text.Encoding.ASCII.GetBytes(key));
            records.AddRange(U32((uint)payload.Length));
            records.AddRange(payload);
            if (payload.Length % 2 == 1) records.Add(0); // even padding
        }

        var layerInfo = new List<byte>();
        layerInfo.AddRange(U16(1));
        layerInfo.AddRange(records);
        layerInfo.AddRange(channelData);
        outBytes.AddRange(U32((uint)(4 + layerInfo.Count)));
        outBytes.AddRange(U32((uint)layerInfo.Count));
        outBytes.AddRange(layerInfo);
        if (bitmap is null)
        {
            // A 64×64 flat white merged composite follows, so no case can fall to the fallback.
            outBytes.AddRange(U16(0));
            for (int plane = 0; plane < 3; plane++) outBytes.AddRange(Enumerable.Repeat((byte)255, 64 * 64));
        }
        return [.. outBytes];
    }

    private static PsdLayer Only(PsdDocument document) => Assert.Single(document.Layers, layer => !layer.IsGroup);

    [Fact]
    public void FillRecordBecomesALiveShapeWithTheToolsOwnPixels()
    {
        var document = PsdReader.Read(BuildShapePsd("Red Rect",
            [("SoCo", SolidColor(1, 0, 0)), ("vogk", Origination(1, 4, 6, 24, 16))]));
        var layer = Only(document);
        Assert.NotNull(layer.Shape);
        var style = layer.Shape!;
        Assert.Equal(ShapeKind.Rectangle, style.Kind);
        Assert.Equal(1, style.Red); Assert.Equal(0, style.Green);
        Assert.Equal(4, layer.Offset.X); Assert.Equal(6, layer.Offset.Y);
        Assert.Equal(20, layer.Pixels!.Width); Assert.Equal(10, layer.Pixels.Height);
        // The raster is the shape tool's own draw, not a resampled bitmap.
        var expected = ShapeRasterizer.Draw(style, 20, 10);
        Assert.True(layer.Pixels.Pixels.SequenceEqual(expected.Pixels));
        Assert.Null(layer.ShapeNotes); // notes stay absent unless the record adds something
    }

    [Fact]
    public void RoundedRectangleKeepsOneUniformRadius()
    {
        var document = PsdReader.Read(BuildShapePsd("Pill",
            [("SoCo", SolidColor(0, 0, 1)), ("vogk", Origination(2, 0, 0, 40, 20, radii: [8, 8, 8, 8]))]));
        var layer = Only(document);
        Assert.Equal(ShapeKind.Rectangle, layer.Shape!.Kind);
        Assert.Equal(8, layer.Shape!.CornerRadius);
    }

    [Fact]
    public void MixedRadiiFallBackToTheLayersBitmapInsteadOfGuessing()
    {
        // The Mac refuses a rounded rectangle whose four radii disagree; the bitmap stays.
        var document = PsdReader.Read(BuildShapePsd("Irregular",
            [("SoCo", SolidColor(1, 1, 0)), ("vogk", Origination(2, 0, 0, 8, 8, radii: [2, 6, 2, 2]))],
            bitmap: (Top: 2, Left: 2, W: 8, H: 8)));
        var layer = Only(document);
        Assert.Null(layer.Shape);
        Assert.Equal(8, layer.Pixels!.Width); // the decoded channels, not a shape raster
        Assert.Equal(2, layer.Offset.X);
    }

    [Fact]
    public void EllipseOriginationReadsAsALiveEllipse()
    {
        var document = PsdReader.Read(BuildShapePsd("Oval",
            [("SoCo", SolidColor(.25, .5, .75)), ("vogk", Origination(5, 2, 2, 18, 12))]));
        var layer = Only(document);
        Assert.Equal(ShapeKind.Ellipse, layer.Shape!.Kind);
        Assert.Equal(.25, layer.Shape!.Red, 3);
        Assert.Equal(16, layer.Pixels!.Width); Assert.Equal(10, layer.Pixels.Height);
    }

    [Fact]
    public void ReadLiveTurnsASharpMaskPathIntoARectangle()
    {
        // The mask path holds 26-byte records: one length record and four linked anchors,
        // normalized 0–1 fixed points on the canvas grid.
        var extras = new Dictionary<string, byte[]> { ["SoCo"] = SolidColor(1, 1, 1), ["vsms"] = SharpMask(64, 64) };
        long remaining = Limits.DocumentPixelBudget;
        var live = PsdVector.ReadLive(extras, 64, 64, ref remaining);
        Assert.NotNull(live);
        Assert.Equal(ShapeKind.Rectangle, live!.Style.Kind);
        Assert.Equal(16, live.X); Assert.Equal(16, live.Y);
        Assert.Equal(32, live.Width); Assert.Equal(32, live.Height);
    }

    [Fact]
    public void SharpVectorMaskWithoutOriginationReadsAsARectangle()
    {
        var document = PsdReader.Read(BuildShapePsd("Masked",
            [("SoCo", SolidColor(1, 1, 1)), ("vsms", SharpMask(64, 64))]));
        var layer = Only(document);
        Assert.Equal(ShapeKind.Rectangle, layer.Shape!.Kind);
        Assert.Equal(16, layer.Offset.X); Assert.Equal(16, layer.Offset.Y); // 0–1 of a 64² canvas
        Assert.Equal(32, layer.Pixels!.Width);
    }

    [Fact]
    public void CurvedVectorMaskKeepsTheDecodedBitmap()
    {
        // A bézier needs the raster path the port has not settled, so there is no live shape
        // record and the layer keeps the pixels it decoded — not dropped, not guessed.
        var document = PsdReader.Read(BuildShapePsd("Blob",
            [("SoCo", SolidColor(1, 0, 1)), ("vmsk", CurvedMask(64, 64))], bitmap: (Top: 0, Left: 0, W: 8, H: 8)));
        var layer = Only(document);
        Assert.Null(layer.Shape);
        Assert.Equal(8, layer.Pixels!.Width);
    }

    [Fact]
    public void StrokeFlagIsNotedAndOmitted()
    {
        var document = PsdReader.Read(BuildShapePsd("Outlined",
            [("SoCo", SolidColor(0, 1, 0)), ("vogk", Origination(1, 0, 0, 10, 10)), ("vstk", Stroke(fillEnabled: true, strokeEnabled: true))]));
        var layer = Only(document);
        Assert.NotNull(layer.Shape);
        Assert.NotNull(layer.ShapeNotes);
        Assert.Contains("The Photoshop stroke isn’t supported on shape layers and was omitted.", layer.ShapeNotes!);
    }

    [Fact]
    public void FillDisabledKeepsTheBitmapAndBuildsNoShape()
    {
        var document = PsdReader.Read(BuildShapePsd("NoFill",
            [("SoCo", SolidColor(1, 0, 0)), ("vogk", Origination(1, 0, 0, 8, 8)), ("vstk", Stroke(fillEnabled: false, strokeEnabled: false))],
            bitmap: (Top: 1, Left: 1, W: 8, H: 8)));
        var layer = Only(document);
        Assert.Null(layer.Shape);
        Assert.Equal(1, layer.Offset.X);
    }

    [Fact]
    public void ImportPsdCarriesTheLiveShapeIntoTheManifestAndUndoRestores()
    {
        var document = PsdReader.Read(BuildShapePsd("Red Rect",
            [("SoCo", SolidColor(1, 0, 0)), ("vogk", Origination(1, 4, 6, 24, 16))]));
        var scene = EditorDocument.Create(64, 64);
        var created = Assert.Single(scene.ImportPsd(document));
        Assert.NotNull(created.Shape);
        var style = ShapeStyle.FromElement(created.Shape)!;
        Assert.Equal(ShapeKind.Rectangle, style.Kind);
        var pixels = scene.Snapshot.Images[created.Id];
        Assert.True(pixels.Pixels.SequenceEqual(ShapeRasterizer.Draw(style, pixels.Width, pixels.Height).Pixels));
        Assert.True(scene.Undo());
        Assert.DoesNotContain(created.Id, scene.Snapshot.Images.Keys);
    }

    [Fact]
    public async Task APlacedShapeSurvivesTheProjectRoundTrip()
    {
        // The shape lives in the shared manifest contract: save, reload, redraw at a new size.
        var document = PsdReader.Read(BuildShapePsd("Red Rect",
            [("SoCo", SolidColor(1, 0, 0)), ("vogk", Origination(2, 0, 0, 30, 14, radii: [5, 5, 5, 5]))]));
        var scene = EditorDocument.Create(64, 64);
        var created = Assert.Single(scene.ImportPsd(document));
        string path = Path.Combine(Path.GetTempPath(), "compositor-shape-" + Guid.NewGuid().ToString("N") + ".comp");
        try
        {
            await ProjectStore.SaveAsync(scene.CaptureSave().Snapshot, path);
            var restored = await ProjectStore.LoadAsync(path);
            var layer = Assert.Single(restored.Manifest.Layers, candidate => candidate.Shape is not null);
            var style = ShapeStyle.FromElement(layer.Shape)!;
            Assert.Equal(ShapeKind.Rectangle, style.Kind);
            Assert.Equal(5, style.CornerRadius);
            var before = scene.Snapshot.Images[created.Id]; var after = restored.Images[layer.Id];
            Assert.Equal((int)layer.Transform.Size.Width, after.Width);
            // The style is the truth for a live shape: the reloaded record redraws the exact
            // pixels the import drew, which is what makes the placed shape editable after a
            // save. The stored PNG's fractional-alpha edge may drift by one on the premultiply
            // round-trip, so the bytes themselves only agree within that quantization.
            var pixels = ShapeRasterizer.Draw(style, after.Width, after.Height);
            Assert.True(pixels.Pixels.SequenceEqual(before.Pixels), "the reloaded style no longer draws the imported pixels");
            Assert.Equal(before.Pixels.Length, after.Pixels.Length);
            for (int i = 0; i < after.Pixels.Length; i++)
                Assert.True(Math.Abs(after.Pixels[i] - before.Pixels[i]) <= 1, $"stored bytes drifted at {i}: {before.Pixels[i]} -> {after.Pixels[i]}");
        }
        finally { if (File.Exists(path)) Directory.Delete(path, true); }
    }

    [Fact]
    public void AFillRecordWithAUserMaskKeepsItsMaskOnTheShapeGrid()
    {
        // A fill record has no decoded raster, so the mask grid follows the layer box rather
        // than dereferencing absent pixels — and the mask still clips the shape it rides on.
        var style = new ShapeStyle { Kind = ShapeKind.Rectangle, Red = 1, Green = 0, Blue = 0 };
        var mask = new MaskSurface(20, 10);
        for (int y = 0; y < 10; y++) mask.Pixels.AsSpan(y * 20, 20).Fill(255);
        for (int y = 0; y < 10 && y < 10; y++) for (int x = 0; x < 10; x++) mask.Pixels[y * 20 + x] = 0;
        var layer = new PsdLayer
        {
            Id = Guid.NewGuid(), Name = "Masked Shape", Offset = new PointD(4, 6),
            Pixels = ShapeRasterizer.Draw(style, 20, 10), Shape = style, Mask = mask,
            MaskPlacement = new LayerTransform { Origin = new PointD(4, 6), Size = new SizeD(20, 10) },
        };
        var scene = EditorDocument.Create(64, 64);
        var created = Assert.Single(scene.ImportPsd(new PsdDocument { Width = 64, Height = 64, Layers = [layer] }));
        Assert.NotNull(created.Shape);
        Assert.Equal(20 * 10, scene.Snapshot.Masks[created.Id].Pixels.Length);
        Assert.Equal(0, scene.Snapshot.Masks[created.Id].Pixels[0]);
        var composite = new CanvasCompositor(scene.Snapshot).Render();
        // The shape is pure red over the white background: the green channel tells them apart.
        int hole = 6 * composite.Stride + 4 * 4; // the mask's top-left 10×10 hides the shape
        Assert.Equal(255, composite.Pixels[hole + 1]); // it reveals the layer underneath, not a hole to nothing
        int body = 11 * composite.Stride + 19 * 4; // outside the hole the shape composites red
        Assert.Equal(0, composite.Pixels[body + 1]);
    }

    [Fact]
    public void AShapeOverTheRemainingBudgetRefusesTheWholeFile()
    {
        // The budget refuses before any pixels stand: the reader reports the file as unsupported
        // rather than importing a shape that cannot redraw.
        var data = BuildShapePsd("Big", [("SoCo", SolidColor(1, 0, 0)), ("vogk", Origination(1, 0, 0, 60, 60))]);
        Assert.Throws<PsdNotSupportedException>(() => PsdReader.Read(data, remainingPixels: 100));
        var document = PsdReader.Read(data, remainingPixels: Limits.DocumentPixelBudget);
        Assert.NotNull(Only(document).Shape);
    }
}
