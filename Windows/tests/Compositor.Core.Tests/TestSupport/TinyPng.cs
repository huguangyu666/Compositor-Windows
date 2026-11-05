using System.IO.Compression;
using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// A tiny independent PNG codec for tests: writes and reads PNG bytes without going through
/// WIC, so decode tests cross-check the production codec against a second implementation.
/// Supports 8-bit gray, RGB and RGBA, filter 0 on write and all five filters on read.
/// </summary>
public static class TinyPng
{
    public static byte[] EncodeRgba(int width, int height, ReadOnlySpan<byte> rgbaStraight)
    {
        int stride = width * 4;
        var raw = new byte[(stride + 1) * height];
        for (int row = 0; row < height; row++)
        {
            raw[row * (stride + 1)] = 0; // filter: none
            rgbaStraight.Slice(row * stride, stride).CopyTo(raw.AsSpan(row * (stride + 1) + 1));
        }
        return Encode(width, height, 8, 6, raw);
    }

    public static byte[] EncodeGray(int width, int height, ReadOnlySpan<byte> gray)
    {
        var raw = new byte[(width + 1) * height];
        for (int row = 0; row < height; row++)
        {
            raw[row * (width + 1)] = 0;
            gray.Slice(row * width, width).CopyTo(raw.AsSpan(row * (width + 1) + 1));
        }
        return Encode(width, height, 8, 0, raw);
    }

    private static byte[] Encode(int width, int height, int bitDepth, int colorType, byte[] filtered)
    {
        using var payload = new MemoryStream();
        payload.WriteByte(0x78);
        payload.WriteByte(0x01);
        byte[] compressed = CompressStored(filtered);
        payload.Write(compressed);
        WriteUInt32Big(payload, Adler32(filtered));

        using var output = new MemoryStream();
        void Chunk(string type, ReadOnlySpan<byte> data)
        {
            Span<byte> length = stackalloc byte[4];
            WriteUInt32Big(length, (uint)data.Length);
            output.Write(length);
            var typeAndData = new byte[data.Length + 4];
            System.Text.Encoding.ASCII.GetBytes(type).AsSpan().CopyTo(typeAndData);
            data.CopyTo(typeAndData.AsSpan(4));
            output.Write(typeAndData);
            WriteUInt32Big(output, Crc32(typeAndData));
        }

        output.Write(PngHeader.Signature);
        var header = new byte[13];
        WriteUInt32Big(header, (uint)width);
        WriteUInt32Big(header.AsSpan(4), (uint)height);
        header[8] = (byte)bitDepth;
        header[9] = (byte)colorType;
        // compression 0, filter 0, interlace 0 stay zero
        Chunk("IHDR", header);
        payload.Position = 0;
        Chunk("IDAT", payload.ToArray());
        Chunk("IEND", ReadOnlySpan<byte>.Empty);
        return output.ToArray();
    }

    private static byte[] CompressStored(byte[] data)
    {
        using var output = new MemoryStream();
        int offset = 0;
        while (true)
        {
            int block = Math.Min(65_535, data.Length - offset);
            bool final = offset + block >= data.Length;
            output.WriteByte(final ? (byte)1 : (byte)0);
            output.WriteByte((byte)(block & 0xFF));
            output.WriteByte((byte)(block >> 8));
            output.WriteByte((byte)(~block & 0xFF));
            output.WriteByte((byte)((~block >> 8) & 0xFF));
            output.Write(data, offset, block);
            offset += block;
            if (final) break;
        }
        return output.ToArray();
    }

    public sealed class DecodedImage
    {
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required int BitDepth { get; init; }
        public required int ColorType { get; init; }
        /// <summary>RGBA, straight alpha, one byte per channel per pixel.</summary>
        public required byte[] Rgba { get; init; }
        /// <summary>Gray, one byte per pixel, for color type 0.</summary>
        public required byte[] Gray { get; init; }
    }

    public static DecodedImage Decode(byte[] png)
    {
        Assert.True(png.AsSpan(0, 8).ToArray().SequenceEqual(PngHeader.Signature));
        if (!PngHeader.TryRead(png, out var header)) throw new Xunit.Sdk.XunitException("TinyPng.Decode: bad IHDR");
        var idat = new MemoryStream();
        int offset = 8;
        while (offset < png.Length)
        {
            uint length = ReadUInt32(png, offset);
            string type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            if (type == "IDAT") idat.Write(png, offset + 8, (int)length);
            offset += 12 + (int)length;
        }
        idat.Position = 0;
        using var zlib = new ZLibStream(idat, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zlib.CopyTo(raw);
        byte[] bytes = raw.ToArray();

        int width = header.Width, height = header.Height;
        int bitsPerPixel = header.BitDepth * ChannelsFor(header.ColorType);
        int stride = (width * bitsPerPixel + 7) / 8;
        var result = new byte[stride * height];
        for (int row = 0; row < height; row++)
        {
            int filter = bytes[row * (stride + 1)];
            Span<byte> line = bytes.AsSpan(row * (stride + 1) + 1, stride);
            Span<byte> previous = row == 0 ? default : bytes.AsSpan((row - 1) * (stride + 1) + 1, stride);
            Unfilter(line, previous, filter, bitsPerPixel / 8);
            line.CopyTo(result.AsSpan(row * stride));
        }
        return new DecodedImage
        {
            Width = width,
            Height = height,
            BitDepth = header.BitDepth,
            ColorType = header.ColorType,
            Rgba = ToRgba(result, width, height, header),
            Gray = header.ColorType == PngHeader.ColorTypeGray ? result : Array.Empty<byte>(),
        };
    }

    private static int ChannelsFor(int colorType) => colorType switch
    {
        PngHeader.ColorTypeGray => 1,
        PngHeader.ColorTypeRgb => 3,
        PngHeader.ColorTypePalette => 1,
        PngHeader.ColorTypeGrayAlpha => 2,
        PngHeader.ColorTypeRgba => 4,
        _ => throw new Xunit.Sdk.XunitException("Unsupported color type"),
    };

    private static byte[] ToRgba(byte[] filtered, int width, int height, PngHeader header)
    {
        var rgba = new byte[width * height * 4];
        int stride = (width * header.BitDepth * ChannelsFor(header.ColorType) + 7) / 8;
        if (header.BitDepth != 8) throw new Xunit.Sdk.XunitException("TinyPng.ToRgba only supports 8-bit fixtures");
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int source = y * stride + x * ChannelsFor(header.ColorType);
                int target = (y * width + x) * 4;
                switch (header.ColorType)
                {
                    case PngHeader.ColorTypeGray:
                        rgba[target] = rgba[target + 1] = rgba[target + 2] = filtered[source];
                        rgba[target + 3] = 255;
                        break;
                    case PngHeader.ColorTypeRgb:
                        filtered.AsSpan(source, 3).CopyTo(rgba.AsSpan(target));
                        rgba[target + 3] = 255;
                        break;
                    case PngHeader.ColorTypeRgba:
                        filtered.AsSpan(source, 4).CopyTo(rgba.AsSpan(target));
                        break;
                    default:
                        throw new Xunit.Sdk.XunitException("TinyPng.ToRgba does not support this color type");
                }
            }
        }
        return rgba;
    }

    private static void Unfilter(Span<byte> line, ReadOnlySpan<byte> previous, int filter, int bpp)
    {
        switch (filter)
        {
            case 0: break;
            case 1:
                for (int i = bpp; i < line.Length; i++) line[i] = (byte)(line[i] + line[i - bpp]);
                break;
            case 2:
                for (int i = 0; i < line.Length; i++) line[i] = (byte)(line[i] + (previous.IsEmpty ? 0 : previous[i]));
                break;
            case 3:
                for (int i = 0; i < line.Length; i++)
                {
                    int left = i >= bpp ? line[i - bpp] : 0;
                    int up = previous.IsEmpty ? 0 : previous[i];
                    line[i] = (byte)(line[i] + (left + up) / 2);
                }
                break;
            case 4:
                for (int i = 0; i < line.Length; i++)
                {
                    int a = i >= bpp ? line[i - bpp] : 0;
                    int b = previous.IsEmpty ? 0 : previous[i];
                    int c = i >= bpp && !previous.IsEmpty ? previous[i - bpp] : 0;
                    int p = a + b - c;
                    int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                    int predictor = pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
                    line[i] = (byte)(line[i] + predictor);
                }
                break;
            default:
                throw new Xunit.Sdk.XunitException($"Unknown PNG filter {filter}");
        }
    }

    private static void WriteUInt32Big(Span<byte> target, uint value)
    {
        target[0] = (byte)(value >> 24);
        target[1] = (byte)(value >> 16);
        target[2] = (byte)(value >> 8);
        target[3] = (byte)value;
    }

    private static void WriteUInt32Big(Stream target, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        WriteUInt32Big(buffer, value);
        target.Write(buffer);
    }

    private static uint ReadUInt32(byte[] bytes, int offset) =>
        (uint)(bytes[offset] << 24 | bytes[offset + 1] << 16 | bytes[offset + 2] << 8 | bytes[offset + 3]);

    private static uint Adler32(ReadOnlySpan<byte> data)
    {
        uint a = 1, b = 0;
        foreach (byte value in data)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }
        return b << 16 | a;
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

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte value in data) crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }
}
