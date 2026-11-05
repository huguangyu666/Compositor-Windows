namespace Compositor.Core;

public readonly record struct PngHeader(int Width, int Height, int BitDepth, int ColorType, int Interlace)
{
    public const int SignatureLength = 8;

    public static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public const int ColorTypeGray = 0;
    public const int ColorTypeRgb = 2;
    public const int ColorTypePalette = 3;
    public const int ColorTypeGrayAlpha = 4;
    public const int ColorTypeRgba = 6;

    /// <summary>
    /// Reads the signature and IHDR, the same checks the macOS loader makes before decoding:
    /// a single-image PNG whose bit depth is at most 8.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> bytes, out PngHeader header)
    {
        header = default;
        // Layout: signature (8), IHDR length (4), "IHDR" (4), data (13), CRC (4).
        if (bytes.Length < 33 || !bytes[..SignatureLength].SequenceEqual(Signature)) return false;
        if (ReadUInt32(bytes, 8) != 13) return false; // IHDR length
        if (ReadUInt32(bytes, 12) != 0x49484452) return false; // "IHDR"
        int width = (int)ReadUInt32(bytes, 16);
        int height = (int)ReadUInt32(bytes, 20);
        int bitDepth = bytes[24];
        int colorType = bytes[25];
        int compression = bytes[26];
        int filter = bytes[27];
        int interlace = bytes[28];
        if (width <= 0 || height <= 0) return false;
        if (bitDepth is not (1 or 2 or 4 or 8 or 16)) return false;
        if (colorType is not (ColorTypeGray or ColorTypeRgb or ColorTypePalette or ColorTypeGrayAlpha or ColorTypeRgba)) return false;
        if (compression != 0 || filter != 0 || interlace is not (0 or 1)) return false;
        header = new PngHeader(width, height, bitDepth, colorType, interlace);
        return true;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset) =>
        (uint)(bytes[offset] << 24 | bytes[offset + 1] << 16 | bytes[offset + 2] << 8 | bytes[offset + 3]);
}
