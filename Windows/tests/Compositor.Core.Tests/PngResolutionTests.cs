using Compositor.Core;
using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// D04: the PNG export has to carry the document's resolution the way the original's <c>pngData</c> does
/// (<c>ImageExporter.swift:104-109</c>). Everything here is measured against the bytes WIC actually produces
/// — the encoder writes <c>pHYs 3779</c> on its own, so "no resolution" would have been a wrong premise and
/// "different resolution" is the real gap.
/// </summary>
public class PngResolutionTests
{
    private static Surface Ramp(int width = 9, int height = 5)
    {
        var surface = new Surface(width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int at = y * surface.Stride + x * 4;
                surface.Pixels[at] = (byte)(x * 27);
                surface.Pixels[at + 1] = (byte)(y * 51);
                surface.Pixels[at + 2] = 200;
                surface.Pixels[at + 3] = 255;
            }
        return surface;
    }

    [Theory]
    [InlineData(72, 2835)]
    [InlineData(96, 3780)]
    [InlineData(150, 5906)]
    [InlineData(300, 11811)]
    public void PixelsPerMetreRoundsAwayFromZero(double dpi, uint expected)
        => Assert.Equal(expected, PngMetadata.PixelsPerMetre(dpi));

    [Fact]
    public async Task ExportedPngReportsTheDocumentResolution()
    {
        byte[] png = await WicImaging.EncodePngForExportAsync(Ramp(), 300);
        Assert.True(PngMetadata.TryFindChunk(png, "pHYs", out int chunk, out int data), "no pHYs chunk in the export");
        Assert.Equal(11811u, ((uint)png[data] << 24) | ((uint)png[data + 1] << 16) | ((uint)png[data + 2] << 8) | png[data + 3]);
        Assert.Equal(11811u, ((uint)png[data + 4] << 24) | ((uint)png[data + 5] << 16) | ((uint)png[data + 6] << 8) | png[data + 7]);
        Assert.Equal(1, png[data + 8]);
        double? readBack = PngMetadata.ReadResolution(png);
        Assert.True(readBack.HasValue && Math.Abs(readBack.Value - 300) < 0.5, $"read back {readBack?.ToString() ?? "nothing"}");
        // The chunk's CRC has to cover its own type and data, or every reader on the far side rejects the file.
        uint stored = ((uint)png[chunk + 8 + 9] << 24) | ((uint)png[chunk + 9 + 9] << 16) | ((uint)png[chunk + 10 + 9] << 8) | png[chunk + 11 + 9];
        Assert.Equal(Crc(png.AsSpan(chunk + 4, 4 + 9)), stored);
    }

    [Fact]
    public async Task WithoutAResolutionTheExportBytesAreWhatTheProjectStores()
    {
        byte[] plain = await WicImaging.EncodePngAsync(Ramp());
        byte[] exported = await WicImaging.EncodePngForExportAsync(Ramp(), null);
        Assert.Equal(plain, exported);
        Assert.True(PngMetadata.TryFindChunk(plain, "pHYs", out _, out int data));
        Assert.Equal(3779u, BigUInt(plain.AsSpan(data)));
    }

    [Fact]
    public async Task ThePatchChangesNothingButTheResolutionChunk()
    {
        byte[] plain = await WicImaging.EncodePngAsync(Ramp());
        byte[] patched = await WicImaging.EncodePngForExportAsync(Ramp(), 150);
        Assert.Equal(plain.Length, patched.Length);
        var differing = new List<int>();
        for (int i = 0; i < plain.Length; i++) if (plain[i] != patched[i]) differing.Add(i);
        Assert.True(PngMetadata.TryFindChunk(plain, "pHYs", out int chunk, out int data));
        // Every byte that moved is inside the chunk's payload or its CRC, and the payload really did move.
        Assert.All(differing, index => Assert.InRange(index, data, chunk + 8 + 9 + 3));
        Assert.NotEmpty(differing.Where(index => index >= data && index < data + 9));
    }

    [Fact]
    public async Task DecodedPixelsSurviveThePatchUnchanged()
    {
        byte[] plain = await WicImaging.EncodePngAsync(Ramp());
        byte[] patched = await WicImaging.EncodePngForExportAsync(Ramp(), 300);
        var before = await WicImaging.DecodeSurfaceAsync(plain);
        var after = await WicImaging.DecodeSurfaceAsync(patched);
        Assert.Equal(before.Pixels, after.Pixels);
    }

    [Fact]
    public async Task ApplyingItTwiceIsTheSameAsOnce()
    {
        byte[] once = await WicImaging.EncodePngForExportAsync(Ramp(), 72);
        byte[] twice = await WicImaging.EncodePngForExportAsync(Ramp(), 72);
        Assert.Equal(once, twice);
        Assert.Equal(PngMetadata.ApplyResolution(once, 72), once);
    }

    [Fact]
    public void AnAbsentChunkIsInsertedAfterTheHeader()
    {
        byte[] stripped = StreamWithoutPhysicalResolution();
        Assert.False(PngMetadata.TryFindChunk(stripped, "pHYs", out _, out _));

        byte[] withChunk = PngMetadata.ApplyResolution(stripped, 300);
        Assert.True(PngMetadata.TryFindChunk(withChunk, "pHYs", out int chunk, out int data));
        Assert.Equal(11811u, BigUInt(withChunk.AsSpan(data)));
        Assert.Equal(11811u, BigUInt(withChunk.AsSpan(data + 4)));
        Assert.Equal(1, withChunk[data + 8]);
        Assert.Equal(stripped.Length + 12 + 9, withChunk.Length);   // one chunk: length, type, payload, CRC
        // It has to sit right after IHDR, where a reader that stops early still sees it.
        Assert.True(PngMetadata.TryFindChunk(withChunk, "IHDR", out int ihdrChunk, out _));
        Assert.Equal(ihdrChunk + 12 + 13, chunk);
        Assert.Equal(Crc(withChunk.AsSpan(chunk + 4, 4 + 9)), BigUInt(withChunk.AsSpan(chunk + 8 + 9)));
        // Everything that was already there is untouched: the tail after IHDR only moves by one chunk.
        int afterIhdr = 8 + 12 + 13;
        Assert.Equal(stripped.Skip(afterIhdr).ToArray(), withChunk.Skip(afterIhdr + 12 + 9).ToArray());
    }

    /// <summary>A one-row PNG stream with no pHYs at all, so the insertion path is what is under test.</summary>
    private static byte[] StreamWithoutPhysicalResolution()
    {
        var ihdr = new byte[13];
        ihdr[3] = 9; ihdr[7] = 5; ihdr[8] = 8; ihdr[9] = 6;      // 9×5, 8-bit RGBA, no interlace
        var stream = new List<byte> { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A };
        AddChunk(stream, "IHDR", ihdr);
        AddChunk(stream, "IDAT", [1, 2, 3]);
        AddChunk(stream, "IEND", []);
        return stream.ToArray();
    }

    private static void AddChunk(List<byte> stream, string type, byte[] payload)
    {
        int length = payload.Length;
        stream.AddRange(new[] { (byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length });
        int typeStart = stream.Count;
        stream.AddRange(System.Text.Encoding.ASCII.GetBytes(type));
        stream.AddRange(payload);
        uint crc = Crc(stream.GetRange(typeStart, 4 + length).Select(b => (byte)b).ToArray());
        stream.AddRange(new[] { (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void AnImpossibleResolutionIsRefused(double dpi)
        => Assert.Throws<ArgumentOutOfRangeException>(() => PngMetadata.ApplyResolution([0x89, (byte)'P', (byte)'N', (byte)'G'], dpi));

    private static uint BigUInt(ReadOnlySpan<byte> source) => ((uint)source[0] << 24) | ((uint)source[1] << 16) | ((uint)source[2] << 8) | source[3];

    /// <summary>Recomputed independently of the production helper, so a wrong CRC there cannot agree with itself.</summary>
    private static uint Crc(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte value in data) crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            table[i] = crc;
        }
        return table;
    }
}
