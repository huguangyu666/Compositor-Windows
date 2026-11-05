using System.IO.Compression;

namespace Compositor.Core;

/// <summary>
/// Writes 8-bit grayscale PNGs — the format layer masks must be saved in (white reveals, black
/// hides, no alpha). The WIC encoder refuses Gray8 input, and an RGBA mask would be rejected by
/// the macOS app, so this one format is written by hand; reading goes through WIC as usual.
/// </summary>
public static class GrayPng
{
    public static byte[] Encode(MaskSurface mask)
    {
        using var output = new MemoryStream();
        output.Write(PngHeader.Signature);

        Span<byte> length = stackalloc byte[4];
        // IHDR
        byte[] ihdr = new byte[13];
        WriteUInt32Big(ihdr, (uint)mask.Width);
        WriteUInt32Big(ihdr.AsSpan(4), (uint)mask.Height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = PngHeader.ColorTypeGray;
        WriteChunk(output, "IHDR", ihdr);
        // IDAT: filter 0 per row, zlib-compressed.
        var raw = new byte[(mask.Width + 1) * mask.Height];
        for (int row = 0; row < mask.Height; row++)
        {
            raw[row * (mask.Width + 1)] = 0;
            mask.Pixels.AsSpan(row * mask.Width, mask.Width).CopyTo(raw.AsSpan(row * (mask.Width + 1) + 1));
        }
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw);
        }
        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        WriteUInt32Big(length, (uint)data.Length);
        output.Write(length);
        var typeAndData = new byte[data.Length + 4];
        System.Text.Encoding.ASCII.GetBytes(type).AsSpan().CopyTo(typeAndData);
        data.CopyTo(typeAndData.AsSpan(4));
        output.Write(typeAndData);
        Span<byte> crc = stackalloc byte[4];
        WriteUInt32Big(crc, Crc32(typeAndData));
        output.Write(crc);
    }

    private static void WriteUInt32Big(Span<byte> target, uint value)
    {
        target[0] = (byte)(value >> 24);
        target[1] = (byte)(value >> 16);
        target[2] = (byte)(value >> 8);
        target[3] = (byte)value;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            table[i] = crc;
        }
        return table;
    }

    internal static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte value in data) crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }
}
