namespace Compositor.Core;

/// <summary>
/// Reading and writing the PNG <c>pHYs</c> chunk, which carries the image's physical resolution
/// (pixels per unit; unit 1 is the metre). Export uses it so a PNG leaves the app with the document's
/// own resolution the way the original does (<c>ImageExporter.swift:104-109</c>), instead of the
/// 3779 ppm ≈ 96 DPI that WIC stamps from the pixel-data hint.
/// </summary>
public static class PngMetadata
{
    private const byte MetreUnit = 1;
    private const double PixelsPerInchPerMetre = 39.37007874015748;

    /// <summary>Pixels per metre for a DPI value, rounded away from zero as the PNG spec's reader expects an
    /// integer; 72 → 2835, 300 → 11811.</summary>
    public static uint PixelsPerMetre(double dpi) => (uint)Math.Round(dpi * PixelsPerInchPerMetre, MidpointRounding.AwayFromZero);

    public static double DpiFromPixelsPerMetre(uint ppm) => ppm / PixelsPerInchPerMetre;

    /// <summary>The DPI in the image's <c>pHYs</c> chunk, or null when it has none or the unit is unspecified.</summary>
    public static double? ReadResolution(ReadOnlySpan<byte> png)
    {
        if (!TryFindChunk(png, "pHYs", out _, out int data)) return null;
        if (png[data + 8] != MetreUnit) return null;
        uint x = BigUInt32(png[data..]), y = BigUInt32(png[(data + 4)..]);
        return x == 0 || y == 0 ? null : DpiFromPixelsPerMetre(x);
    }

    /// <summary>
    /// Rewrites <c>pHYs</c> with this DPI, inserting the chunk after <c>IHDR</c> when the encoder left it out.
    /// Only that chunk's bytes and its CRC change; the rest of the stream is copied untouched, so the decoded
    /// pixels are identical before and after.
    /// </summary>
    public static byte[] ApplyResolution(byte[] png, double dpi)
    {
        if (dpi is <= 0 or > 100_000 || double.IsFinite(dpi) == false)
            throw new ArgumentOutOfRangeException(nameof(dpi), "A PNG resolution has to be a finite DPI above zero.");
        uint ppm = PixelsPerMetre(dpi);
        byte[] body = [
            (byte)(ppm >> 24), (byte)(ppm >> 16), (byte)(ppm >> 8), (byte)ppm,
            (byte)(ppm >> 24), (byte)(ppm >> 16), (byte)(ppm >> 8), (byte)ppm,
            MetreUnit,
        ];
        if (TryFindChunk(png, "pHYs", out int chunk, out int data))
        {
            for (int i = 0; i < body.Length; i++) png[data + i] = body[i];
            WriteBigUInt32(png.AsSpan(chunk + 8 + body.Length, 4), Crc32(png.AsSpan(chunk + 4, 4 + body.Length)));
            return png;
        }
        int ihdrEnd = 8 + 12 + 13; // signature, then IHDR's length/type/data/CRC with its 13-byte payload
        byte[] grown = new byte[png.Length + 4 + 4 + body.Length + 4];
        Array.Copy(png, 0, grown, 0, ihdrEnd);
        WriteBigUInt32(grown.AsSpan(ihdrEnd, 4), (uint)body.Length);
        grown[ihdrEnd + 4] = (byte)'p'; grown[ihdrEnd + 5] = (byte)'H'; grown[ihdrEnd + 6] = (byte)'Y'; grown[ihdrEnd + 7] = (byte)'s';
        Array.Copy(body, 0, grown, ihdrEnd + 8, body.Length);
        WriteBigUInt32(grown.AsSpan(ihdrEnd + 8 + body.Length, 4),
            Crc32(grown.AsSpan(ihdrEnd + 4, 4 + body.Length)));
        Array.Copy(png, ihdrEnd, grown, ihdrEnd + 4 + 4 + body.Length + 4, png.Length - ihdrEnd);
        return grown;
    }

    /// <summary>Walks the chunk stream looking for a four-character type, reporting the chunk start and its data.</summary>
    public static bool TryFindChunk(ReadOnlySpan<byte> png, string type, out int chunk, out int data)
    {
        chunk = data = 0;
        if (png.Length < 8 || png[0] != 0x89 || png[1] != 'P' || png[2] != 'N' || png[3] != 'G') return false;
        for (int at = 8; at + 12 <= png.Length;)
        {
            uint length = BigUInt32(png[at..]);
            if (length > png.Length - (at + 12u)) return false;
            if (TypeOf(png, at + 4) == type) { chunk = at; data = at + 8; return true; }
            at += 12 + (int)length;
        }
        return false;
    }

    private static string TypeOf(ReadOnlySpan<byte> png, int at) => $"{(char)png[at]}{(char)png[at + 1]}{(char)png[at + 2]}{(char)png[at + 3]}";

    private static uint BigUInt32(ReadOnlySpan<byte> source) => ((uint)source[0] << 24) | ((uint)source[1] << 16) | ((uint)source[2] << 8) | source[3];

    private static void WriteBigUInt32(Span<byte> target, uint value)
    {
        target[0] = (byte)(value >> 24);
        target[1] = (byte)(value >> 16);
        target[2] = (byte)(value >> 8);
        target[3] = (byte)value;
    }

    private static uint Crc32(ReadOnlySpan<byte> data) => GrayPng.Crc32(data);
}
