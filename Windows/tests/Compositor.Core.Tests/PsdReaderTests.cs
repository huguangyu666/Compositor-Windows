using Compositor.Core.Psd;
using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// PSD reading tests over hand-built fixtures: the bytes are constructed by the test
/// helpers below, so every field is under test control (no external files).
/// </summary>
public class PsdReaderTests
{
    private const int CanvasW = 32;
    private const int CanvasH = 32;

    public static IEnumerable<object[]> LayersFixture =>
    [
        // Two layers: red 12×12 at (10,20), blue 8×8 at (0,0).
        [new object[] { "Red Layer", 10, 20, 12, 12, (byte)232, (byte)90, (byte)84 },
         new object[] { "Blue Layer", 0, 0, 8, 8, (byte)72, (byte)150, (byte)220 }],
    ];

    private static byte[] U32(uint value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static byte[] U16(ushort value) => [(byte)(value >> 8), (byte)value];

    private static byte[] PlaneRaw(byte value, int pixels) => Enumerable.Repeat(value, pixels).ToArray();

    private static byte[] PlaneRaw(byte[] values) => values.ToArray();

    private static byte[][] SolidRows(byte value, int w, int h)
    {
        var rows = new byte[h][];
        for (int y = 0; y < h; y++)
        {
            rows[y] = Enumerable.Repeat(value, w).ToArray();
        }
        return rows;
    }

    private static byte[] Flat(byte[][] rows) => rows.SelectMany(row => row).ToArray();

    /// <summary>Per-row PackBits with a u16 length per row: the PSD channel RLE layout.</summary>
    private static byte[] PlaneRle(byte[][] rows)
    {
        var table = new List<byte>();
        var packed = new List<byte>();
        foreach (var row in rows)
        {
            var chunks = new List<byte>();
            int offset = 0;
            while (offset < row.Length)
            {
                int count = Math.Min(127, row.Length - offset);
                chunks.Add((byte)(count - 1));
                chunks.AddRange(row.Skip(offset).Take(count));
                offset += count;
            }
            table.AddRange(U16((ushort)chunks.Count));
            packed.AddRange(chunks);
        }
        return [.. table, .. packed];
    }

    /// <summary>Builds a minimal RGB 8-bit PSD with the given layers (raw compression).</summary>
    private static byte[] BuildLayeredPsd(
        int canvasW, int canvasH,
        (string Name, int Left, int Top, int W, int H, byte R, byte G, byte B)[] layers,
        bool rle = false)
    {
        var outBytes = new List<byte>();
        outBytes.AddRange("8BPS"u8);
        outBytes.AddRange(U16(1));
        outBytes.AddRange(new byte[6]);
        outBytes.AddRange(U16(3));
        outBytes.AddRange(U32((uint)canvasH));
        outBytes.AddRange(U32((uint)canvasW));
        outBytes.AddRange(U16(8));
        outBytes.AddRange(U16(3));
        outBytes.AddRange(U32(0)); // color mode data
        outBytes.AddRange(U32(0)); // image resources

        // Layer section payload: [u16 count][records...][channel data...] — every record
        // first, then every layer's channel data, exactly as the reader consumes them.
        var records = new List<byte>();
        var channelData = new List<byte>();
        foreach (var (name, left, top, w, h, r, g, b) in layers)
        {
            int pixels = w * h;
            records.AddRange(U32((uint)top));
            records.AddRange(U32((uint)left));
            records.AddRange(U32((uint)(top + h)));
            records.AddRange(U32((uint)(left + w)));
            records.AddRange(U16(4));
            byte[][] redRows = SolidRows(r, w, h);
            byte[][] greenRows = SolidRows(g, w, h);
            byte[][] blueRows = SolidRows(b, w, h);
            byte[][] alphaRows = SolidRows(255, w, h);
            var planeSpecs = new (int Id, byte[] Data)[]
            {
                (0, rle ? PlaneRle(redRows) : Flat(redRows)),
                (1, rle ? PlaneRle(greenRows) : Flat(greenRows)),
                (2, rle ? PlaneRle(blueRows) : Flat(blueRows)),
                (-1, rle ? PlaneRle(alphaRows) : Flat(alphaRows)),
            };
            foreach (var (id, data) in planeSpecs)
            {
                records.AddRange(U16(unchecked((ushort)(short)id)));
                records.AddRange(U32((uint)data.Length + 2));
                channelData.AddRange(U16((ushort)(rle ? 1 : 0)));
                channelData.AddRange(data);
            }
            records.AddRange("8BIM"u8);
            records.AddRange("norm"u8);
            records.Add(255); // opacity
            records.Add(0);   // clipping
            records.Add(0);   // flags: visible
            records.Add(0);   // filler
            var nameBytes = System.Text.Encoding.ASCII.GetBytes(name);
            int extraInner = 4 + 4 + 1 + nameBytes.Length;
            int namePad = (4 - ((nameBytes.Length + 1) % 4)) % 4;
            extraInner += namePad;
            records.AddRange(U32((uint)extraInner));
            records.AddRange(U32(0)); // mask
            records.AddRange(U32(0)); // ranges
            records.Add((byte)nameBytes.Length);
            records.AddRange(nameBytes);
            for (int pad = 0; pad < namePad; pad++) records.Add(0);
        }

        var layerInfo = new List<byte>();
        layerInfo.AddRange(U16((ushort)layers.Length));
        layerInfo.AddRange(records);
        layerInfo.AddRange(channelData);

        // Layer section: its own length field precedes the layer info length, and both
        // counts include what follows them.
        outBytes.AddRange(U32((uint)(4 + layerInfo.Count)));
        outBytes.AddRange(U32((uint)layerInfo.Count));
        outBytes.AddRange(layerInfo);

        // No merged composite tail (the layer path must not depend on it).
        return [.. outBytes];
    }

    private static byte[] BuildGrayscalePsd()
    {
        var outBytes = new List<byte>();
        outBytes.AddRange("8BPS"u8);
        outBytes.AddRange(U16(1));
        outBytes.AddRange(new byte[6]);
        outBytes.AddRange(U16(1));   // channels
        outBytes.AddRange(U32(8));
        outBytes.AddRange(U32(8));
        outBytes.AddRange(U16(8));
        outBytes.AddRange(U16(0));   // grayscale — unsupported
        outBytes.AddRange(U32(0));
        outBytes.AddRange(U32(0));
        outBytes.AddRange(U32(0));
        return [.. outBytes];
    }

    [Fact]
    public void ReadsLayersWithNamesOffsetsAndPixels()
    {
        var red = (Name: "Red Layer", Left: 10, Top: 20, W: 12, H: 12, R: (byte)232, G: (byte)90, B: (byte)84);
        var blue = (Name: "Blue Layer", Left: 0, Top: 0, W: 8, H: 8, R: (byte)72, G: (byte)150, B: (byte)220);

        var document = PsdReader.Read(BuildLayeredPsd(32, 32, [red, blue]));

        Assert.Equal(32, document.Width);
        Assert.Equal(32, document.Height);
        Assert.Equal(2, document.Layers.Count);

        var first = document.Layers[0];
        Assert.Equal("Red Layer", first.Name);
        Assert.Equal(10, first.Offset.X);
        Assert.Equal(20, first.Offset.Y);
        Assert.NotNull(first.Pixels);
        Assert.Equal(12, first.Pixels!.Width);
        Assert.Equal(12, first.Pixels.Height);
        // First bar color at (1,1) of the red layer.
        Assert.Equal(232, first.Pixels.Pixels[1 * first.Pixels.Stride + 1 * 4]);
        Assert.Equal(90, first.Pixels.Pixels[1 * first.Pixels.Stride + 1 * 4 + 1]);

        var second = document.Layers[1];
        Assert.Equal("Blue Layer", second.Name);
        Assert.Equal(0, second.Offset.X);
        Assert.Equal(0, second.Offset.Y);
        Assert.Equal(72, second.Pixels!.Pixels[0]);
    }

    [Fact]
    public void ReadsRleCompressedChannels()
    {
        var red = (Name: "Red Layer", Left: 0, Top: 0, W: 12, H: 12, R: (byte)232, G: (byte)90, B: (byte)84);

        var document = PsdReader.Read(BuildLayeredPsd(32, 32, [red], rle: true));

        var layer = document.Layers[0];
        Assert.NotNull(layer.Pixels);
        Assert.Equal(12, layer.Pixels!.Width);
        Assert.Equal(232, layer.Pixels.Pixels[1 * layer.Pixels.Stride + 1 * 4]);
        Assert.Equal(90, layer.Pixels.Pixels[1 * layer.Pixels.Stride + 1 * 4 + 1]);
    }

    [Fact]
    public void GrayscaleModeIsRejected()
    {
        Assert.Throws<PsdNotSupportedException>(() => PsdReader.Read(BuildGrayscalePsd()));
    }

    [Fact]
    public void ParallelDecodeIsBitIdenticalToSerial()
    {
        // Enough RLE layers of real size that the parallel pass actually spreads across cores.
        var layers = Enumerable.Range(0, 8).Select(i => (
            Name: $"Layer {i}", Left: i, Top: i * 2, W: 96, H: 64,
            R: (byte)(40 + i * 20), G: (byte)(200 - i * 10), B: (byte)(60 + i * 15))).ToArray();
        var data = BuildLayeredPsd(160, 160, layers, rle: true);

        var serial = PsdReader.Read(data, parallel: false);
        var parallel = PsdReader.Read(data, parallel: true);

        Assert.Equal(serial.Width, parallel.Width);
        Assert.Equal(serial.Height, parallel.Height);
        Assert.Equal(serial.Layers.Count, parallel.Layers.Count);
        for (int i = 0; i < serial.Layers.Count; i++)
        {
            var a = serial.Layers[i];
            var b = parallel.Layers[i];
            Assert.Equal(a.Name, b.Name);
            Assert.Equal(a.Offset, b.Offset);
            Assert.Equal(a.Opacity, b.Opacity);
            Assert.Equal(a.Hidden, b.Hidden);
            Assert.Equal(a.MaskEnabled, b.MaskEnabled);
            Assert.Equal(a.Pixels is null, b.Pixels is null);
            if (a.Pixels is { } pixels)
            {
                Assert.True(pixels.Pixels.SequenceEqual(b.Pixels!.Pixels), $"layer {i} pixels differ");
            }
        }
    }

    [Fact]
    public void NonPsdBytesAreRejected()
    {
        Assert.Throws<PsdNotSupportedException>(() => PsdReader.Read(new byte[] { 1, 2, 3, 4 }));
    }

    [Fact]
    public void UnpackRleDecodesHugePlanesInParallelBands()
    {
        // 4.16 M pixels: above UnpackRle's row-band threshold, so this exercises the
        // parallel path — the output must still match the packed rows byte for byte.
        int width = 2600, height = 1600;
        var rows = new byte[height][];
        for (int y = 0; y < height; y++)
        {
            rows[y] = new byte[width];
            for (int x = 0; x < width; x++) rows[y][x] = (byte)((x * 7 + y * 13) & 0xFF);
        }
        var payload = PlaneRle(rows);

        var plane = PsdBinaryReader.UnpackRle(payload, 0, width, height, largeDocument: false);

        for (int y = 0; y < height; y++)
        {
            Assert.True(rows[y].AsSpan().SequenceEqual(plane.AsSpan(y * width, width)), $"row {y} differs");
        }
    }

    [Fact]
    public void UnpackRleTruncationStillThrowsEndOfStream()
    {
        int width = 2600, height = 1600;
        var rows = new byte[height][];
        for (int y = 0; y < height; y++) rows[y] = new byte[width];
        var payload = PlaneRle(rows)[..^10]; // cut into the last row's runs

        Assert.Throws<EndOfStreamException>(() => PsdBinaryReader.UnpackRle(payload, 0, width, height, largeDocument: false));
    }

    [Fact]
    public void DepthSixteenIsRejectedLikeOnTheMac()
    {
        var bytes = BuildLayeredPsd(32, 32, []).ToList();
        // depth sits at byte 22: signature(4) + version(2) + reserved(6) + channels(2) + height(4) + width(4).
        bytes[22] = 0;
        bytes[23] = 16;
        Assert.Throws<PsdNotSupportedException>(() => PsdReader.Read([.. bytes]));
    }

    // --- Masks, groups and opacity (second scope) ---

    /// <summary>One layer record for BuildPsd. Zero W/H with a Section makes a divider/folder.</summary>
    private sealed class PsdRecordSpec
    {
        public string Name = "Layer";
        public int Left, Top, W = 8, H = 8;
        public byte R, G = 0, B = 0;
        public int? Section;
        public byte Opacity = 255;
        public byte[]? PlaneValues; // W×H bytes overriding the flat red channel
        public (int Left, int Top, int W, int H, byte Default, byte[] Plane, bool Disabled, bool FromRender)? Mask;
    }

    private static byte[] BuildPsd(int canvasW, int canvasH, params PsdRecordSpec[] specs)
    {
        var outBytes = new List<byte>();
        outBytes.AddRange("8BPS"u8);
        outBytes.AddRange(U16(1));
        outBytes.AddRange(new byte[6]);
        outBytes.AddRange(U16(3));
        outBytes.AddRange(U32((uint)canvasH));
        outBytes.AddRange(U32((uint)canvasW));
        outBytes.AddRange(U16(8));
        outBytes.AddRange(U16(3));
        outBytes.AddRange(U32(0));
        outBytes.AddRange(U32(0));

        var records = new List<byte>();
        var channelData = new List<byte>();
        foreach (var spec in specs)
        {
            records.AddRange(U32((uint)spec.Top));
            records.AddRange(U32((uint)spec.Left));
            records.AddRange(U32((uint)(spec.Top + spec.H)));
            records.AddRange(U32((uint)(spec.Left + spec.W)));

            var planes = new List<(int Id, byte[] Data)>();
            if (spec.W > 0 && spec.H > 0)
            {
                int pixels = spec.W * spec.H;
                var red = spec.PlaneValues ?? Enumerable.Repeat(spec.R, pixels).ToArray();
                planes.Add((0, red));
                planes.Add((1, Flat(SolidRows(spec.G, spec.W, spec.H))));
                planes.Add((2, Flat(SolidRows(spec.B, spec.W, spec.H))));
                planes.Add((-1, Flat(SolidRows(255, spec.W, spec.H))));
            }
            if (spec.Mask is { } mask)
            {
                planes.Add((-2, mask.Plane));
            }
            records.AddRange(U16((ushort)planes.Count));
            foreach (var (id, data) in planes)
            {
                records.AddRange(U16(unchecked((ushort)(short)id)));
                records.AddRange(U32((uint)data.Length + 2));
                channelData.AddRange(U16(0));
                channelData.AddRange(data);
            }

            records.AddRange("8BIM"u8);
            records.AddRange("norm"u8);
            records.Add(spec.Opacity);
            records.Add(0);   // clipping
            records.Add(0);   // flags: visible
            records.Add(0);   // filler

            var nameBytes = System.Text.Encoding.ASCII.GetBytes(spec.Name);
            int namePad = (4 - ((nameBytes.Length + 1) % 4)) % 4;
            var extras = new List<byte>();
            if (spec.Section is { } section)
            {
                extras.AddRange("8BIM"u8);
                extras.AddRange("lsct"u8);
                extras.AddRange(U32(4));
                extras.AddRange(U32((uint)section));
            }
            int maskBlock = 0;
            if (spec.Mask is { } maskSpec)
            {
                maskBlock = 20;
            }
            int extraInner = 4 + maskBlock + 4 + 1 + nameBytes.Length + namePad + extras.Count;
            records.AddRange(U32((uint)extraInner));
            if (spec.Mask is { } theMask)
            {
                records.AddRange(U32(20));
                records.AddRange(U32((uint)theMask.Top));
                records.AddRange(U32((uint)theMask.Left));
                records.AddRange(U32((uint)(theMask.Top + theMask.H)));
                records.AddRange(U32((uint)(theMask.Left + theMask.W)));
                records.Add(theMask.Default);
                records.Add((byte)((theMask.Disabled ? 2 : 0) | (theMask.FromRender ? 8 : 0)));
                records.Add(0);
                records.Add(0);
            }
            else
            {
                records.AddRange(U32(0));
            }
            records.AddRange(U32(0)); // ranges
            records.Add((byte)nameBytes.Length);
            records.AddRange(nameBytes);
            for (int pad = 0; pad < namePad; pad++) records.Add(0);
            records.AddRange(extras);
        }

        var layerInfo = new List<byte>();
        layerInfo.AddRange(U16((ushort)specs.Length));
        layerInfo.AddRange(records);
        layerInfo.AddRange(channelData);
        outBytes.AddRange(U32((uint)(4 + layerInfo.Count)));
        outBytes.AddRange(U32((uint)layerInfo.Count));
        outBytes.AddRange(layerInfo);
        return [.. outBytes];
    }

    [Fact]
    public void ReadsLayerMaskWithItsDefault()
    {
        var plane = Enumerable.Repeat((byte)200, 8 * 8).ToArray();
        var spec = new PsdRecordSpec
        {
            Name = "Masked", Left = 8, Top = 8, W = 16, H = 16, R = 255,
            Mask = (4, 4, 8, 8, 10, plane, Disabled: false, FromRender: false),
        };

        var document = PsdReader.Read(BuildPsd(32, 32, spec));

        var layer = document.Layers.Single();
        Assert.NotNull(layer.Mask);
        Assert.Equal(16, layer.Mask!.Width);
        Assert.Equal(16, layer.Mask.Height);
        Assert.True(layer.MaskEnabled);
        Assert.Equal(new PointD(8, 8), layer.MaskPlacement!.Origin);
        // Document (8,8)-(12,12) overlaps the layer's top-left four pixels; the rest is default.
        Assert.Equal(200, layer.Mask.Pixels[0]);
        Assert.Equal(200, layer.Mask.Pixels[3 * 16 + 3]);
        Assert.Equal(10, layer.Mask.Pixels[4 * 16 + 4]);
        Assert.Equal(10, layer.Mask.Pixels[15 * 16 + 15]);
    }

    [Fact]
    public void DisabledAndRenderMasksBehaveLikeOnTheMac()
    {
        var plane = Enumerable.Repeat((byte)200, 8 * 8).ToArray();
        var disabled = PsdReader.Read(BuildPsd(32, 32, new PsdRecordSpec
        {
            Mask = (4, 4, 8, 8, 10, plane, Disabled: true, FromRender: false),
        })).Layers.Single();
        Assert.False(disabled.MaskEnabled);
        Assert.NotNull(disabled.Mask);

        var fromRender = PsdReader.Read(BuildPsd(32, 32, new PsdRecordSpec
        {
            Mask = (4, 4, 8, 8, 10, plane, Disabled: false, FromRender: true),
        })).Layers.Single();
        Assert.Null(fromRender.Mask);
    }

    [Fact]
    public void RebuildsTheGroupTreeFromSectionDividers()
    {
        var document = PsdReader.Read(BuildPsd(
            32, 32,
            new PsdRecordSpec { Name = "Group A", Section = 3, W = 0, H = 0 },
            new PsdRecordSpec { Name = "Child", R = 255 },
            new PsdRecordSpec { Name = "Group A", Section = 1, W = 0, H = 0 }));

        var child = document.Layers.Single(layer => layer.Name == "Child");
        var folder = document.Layers.Single(layer => layer.IsGroup);
        Assert.Equal(folder.Id, child.ParentId);
        Assert.Null(folder.ParentId);
        Assert.Null(folder.Pixels);
        Assert.True(document.Layers.ToList().IndexOf(child) < document.Layers.ToList().IndexOf(folder));
    }

    [Fact]
    public void UnbalancedGroupsAreRejected()
    {
        Assert.Throws<PsdNotSupportedException>(() => PsdReader.Read(BuildPsd(
            32, 32,
            new PsdRecordSpec { Name = "Group A", Section = 3, W = 0, H = 0 },
            new PsdRecordSpec { Name = "Child", R = 255 })));
    }

    [Fact]
    public void OpacityCombinesTheFillFactor()
    {
        var plain = PsdReader.Read(BuildPsd(32, 32, new PsdRecordSpec { Opacity = 128 })).Layers.Single();
        Assert.Equal(128 / 255.0, plain.Opacity, 6);

        var filled = PsdReader.Read(BuildPsd(32, 32, new PsdRecordSpec
        {
            Opacity = 128,
            PlaneValues = Enumerable.Repeat((byte)255, 64).ToArray(),
        })).Layers.Single();
        Assert.Equal(128 / 255.0, filled.Opacity, 6);
    }

    [Fact]
    public void ImportPsdRebuildsTreeMasksAndPassesValidation()
    {
        var document = EditorDocument.Create(32, 32);
        var parsed = PsdReader.Read(BuildPsd(
            32, 32,
            new PsdRecordSpec { Name = "Group A", Section = 3, W = 0, H = 0 },
            new PsdRecordSpec
            {
                Name = "Masked", R = 255, Opacity = 128,
                Mask = (4, 4, 8, 8, 10, Enumerable.Repeat((byte)200, 64).ToArray(), false, false),
            },
            new PsdRecordSpec { Name = "Group A", Section = 1, W = 0, H = 0 }));

        document.ImportPsd(parsed);

        var manifest = document.Snapshot.Manifest;
        Assert.Equal(3, manifest.Layers.Count); // background, child, folder
        var child = manifest.Layers.Single(layer => layer.Name == "Masked");
        var folder = manifest.Layers.Single(layer => layer.IsGroupOrFalse);
        Assert.Equal(folder.Id, child.ParentId);
        Assert.Equal(128 / 255.0, child.Opacity!.Value, 6);
        Assert.True(child.MaskLinked);
        Assert.Equal(0, child.MaskPlacement!.Origin.X);

        var mask = document.Snapshot.Masks[child.Id];
        Assert.Equal(8, mask.Width); Assert.Equal(8, mask.Height);
        Assert.Equal(200, mask.Pixels[5 * 8 + 5]);
        Assert.Equal(10, mask.Pixels[0]);

        ProjectValidator.Validate(manifest); // must not throw

        Assert.True(document.Undo());
        Assert.Equal(1, document.LayerCount);
    }
}
