using Compositor.Core;
using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// The import gate's TIFF half: WinRT's decoder (the one <see cref="WicImaging"/> hands every non-Photoshop
/// file to) ships a TIFF codec, so a baseline uncompressed RGB TIFF reads through the same entry the app uses
/// for a dropped file. The bytes below are a hand-built TIFF rather than a fixture on disk, so the test cannot
/// quietly start depending on a file that drifts.
/// </summary>
public class TiffDecodeTests
{
    private static byte[] Tiff(int width, int height, byte[] rgb)
    {
        var stream = new MemoryStream();
        void U16(ushort value) { stream.Write(BitConverter.GetBytes(value)); }
        void U32(uint value) { stream.Write(BitConverter.GetBytes(value)); }
        const int bitsOffset = 122, pixelOffset = 128;

        stream.Write("II"u8); U16(42); U32(8);          // little-endian, magic, first IFD at 8
        U16(9);                                        // nine directory entries
        U16(256); U16(4); U32(1); U32((uint)width);    // ImageWidth
        U16(257); U16(4); U32(1); U32((uint)height);   // ImageLength
        U16(258); U16(3); U32(3); U32(bitsOffset);     // BitsPerSample, too long to inline: 8/8/8
        U16(259); U16(3); U32(1); U16(1); U16(0);      // Compression: no codec
        U16(262); U16(3); U32(1); U16(2); U16(0);      // Photometric: RGB
        U16(273); U16(4); U32(1); U32(pixelOffset);    // StripOffsets
        U16(277); U16(3); U32(1); U16(3); U16(0);      // SamplesPerPixel
        U16(278); U16(4); U32(1); U32((uint)height);   // RowsPerStrip
        U16(279); U16(4); U32(1); U32((uint)rgb.Length); // StripByteCounts
        U32(0);                                        // no next directory
        U16(8); U16(8); U16(8);                        // BitsPerSample: the array ends where the pixels start
        stream.Write(rgb);
        return stream.ToArray();
    }

    [Fact]
    public async Task AnUncompressedRgbTiffDecodesThroughTheImportPath()
    {
        const int width = 4, height = 2;
        var rgb = new byte[width * height * 3];
        for (int pixel = 0; pixel < width * height; pixel++)
        {
            rgb[pixel * 3] = (byte)(10 + pixel);
            rgb[pixel * 3 + 1] = (byte)(200 - pixel);
            rgb[pixel * 3 + 2] = 90;
        }

        var surface = await WicImaging.DecodeSurfaceAsync(Tiff(width, height, rgb));

        Assert.Equal(width, surface.Width);
        Assert.Equal(height, surface.Height);
        for (int pixel = 0; pixel < width * height; pixel++)
        {
            int index = pixel * 4;
            // The import path asks WIC for RGBA, so a decoded surface is in that order; the red/blue swap
            // belongs to presentation, not here.
            Assert.True(surface.Pixels[index] == (byte)(10 + pixel), $"the red of pixel {pixel} was {surface.Pixels[index]}.");
            Assert.True(surface.Pixels[index + 1] == (byte)(200 - pixel), $"the green of pixel {pixel} was {surface.Pixels[index + 1]}.");
            Assert.True(surface.Pixels[index + 2] == 90, $"the blue of pixel {pixel} was {surface.Pixels[index + 2]}.");
            Assert.True(surface.Pixels[index + 3] == 255, $"the alpha of pixel {pixel} was {surface.Pixels[index + 3]}.");
        }
    }
}
