using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Compositor.Core;

/// <summary>
/// PNG and JPEG decode and encode through the Windows Imaging Component, the same role
/// ImageIO plays on the Mac. Layer images decode to premultiplied RGBA8; masks to gray8.
/// </summary>
public static class WicImaging
{
    public static async Task<Surface> DecodeSurfaceAsync(byte[] encoded, CancellationToken cancellation = default)
    {
        using var stream = await StreamForAsync(encoded);
        return await DecodeSurfaceAsync(stream, cancellation);
    }

    /// <summary>The same decode from a stream the caller already holds — a drag's in-memory image
    /// never needs a byte round-trip through here.</summary>
    public static async Task<Surface> DecodeSurfaceAsync(IRandomAccessStream stream, CancellationToken cancellation = default)
    {
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellation);
        return await DecodePixelsAsync(decoder, cancellation);
    }

    /// <summary>Checks untrusted image dimensions before WIC allocates the pixel buffer.</summary>
    public static async Task<Surface> DecodeSurfaceAsync(byte[] encoded, int maxSide, long maxPixels, CancellationToken cancellation = default)
    {
        using var stream = await StreamForAsync(encoded);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellation);
        if (decoder.OrientedPixelWidth < 1 || decoder.OrientedPixelHeight < 1
            || decoder.OrientedPixelWidth > Math.Min(maxSide, Limits.MaxSide) || decoder.OrientedPixelHeight > Math.Min(maxSide, Limits.MaxSide)
            || (long)decoder.OrientedPixelWidth * decoder.OrientedPixelHeight > Math.Min(maxPixels, Limits.MaxSurfacePixels))
            throw new ArgumentOutOfRangeException(nameof(encoded), "The decoded image exceeds the allowed dimensions.");
        return await DecodePixelsAsync(decoder, cancellation);
    }

    private static async Task<Surface> DecodePixelsAsync(BitmapDecoder decoder, CancellationToken cancellation)
    {
        var data = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Rgba8,
            BitmapAlphaMode.Premultiplied,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.ColorManageToSRgb).AsTask(cancellation);
        byte[] pixels = data.DetachPixelData();
        int stride = (int)decoder.OrientedPixelWidth * 4; // DetachPixelData is tightly packed
        return new Surface((int)decoder.OrientedPixelWidth, (int)decoder.OrientedPixelHeight, pixels, stride);
    }

    public static async Task<MaskSurface> DecodeMaskAsync(byte[] encoded, CancellationToken cancellation = default)
    {
        using var stream = await StreamForAsync(encoded);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellation);
        PixelDataProvider data;
        if (decoder.BitmapPixelFormat == BitmapPixelFormat.Gray8)
        {
            // The converting GetPixelDataAsync overload only takes RGBA/BGRA, so a native
            // 8-bit gray source (what validated masks always are) decodes directly.
            data = await decoder.GetPixelDataAsync().AsTask(cancellation);
            byte[] pixels = data.DetachPixelData();
            return new MaskSurface((int)decoder.OrientedPixelWidth, (int)decoder.OrientedPixelHeight, pixels);
        }
        // Anything else: decode to RGBA and take the red channel's luminance.
        data = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Rgba8,
            BitmapAlphaMode.Ignore,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage).AsTask(cancellation);
        byte[] rgba = data.DetachPixelData();
        int width = (int)decoder.OrientedPixelWidth, height = (int)decoder.OrientedPixelHeight;
        var gray = new byte[(long)width * height];
        for (int pixel = 0; pixel < gray.Length; pixel++) gray[pixel] = rgba[pixel * 4];
        return new MaskSurface(width, height, gray);
    }

    public static async Task<byte[]> EncodePngAsync(Surface surface, CancellationToken cancellation = default)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream).AsTask(cancellation);
        encoder.SetPixelData(
            BitmapPixelFormat.Rgba8,
            BitmapAlphaMode.Premultiplied,
            (uint)surface.Width,
            (uint)surface.Height,
            96, 96,
            surface.Pixels); // SetPixelData copies synchronously; no need to duplicate the buffer
        await encoder.FlushAsync().AsTask(cancellation);
        return await ReadAllAsync(stream);
    }

    /// <summary>Masks are true 8-bit grayscale PNGs; see <see cref="GrayPng"/>.</summary>
    public static Task<byte[]> EncodePngAsync(MaskSurface mask, CancellationToken cancellation = default) =>
        Task.FromResult(GrayPng.Encode(mask));

    /// <summary>
    /// A PNG for export: the document's own resolution in <c>pHYs</c>, as the original's
    /// <c>pngData</c> writes it, rather than the 3779 ppm ≈ 96 DPI WIC stamps from the pixel-data hint
    /// (measured, 2026-10-17: <c>IHDR sRGB gAMA pHYs[3779 3779 1] IDAT IEND</c>). When no resolution is set
    /// the bytes are exactly what <see cref="EncodePngAsync(Surface, CancellationToken)"/> returns — the
    /// project's stored layer images use that path and must not shift (see the D11 drift family).
    /// </summary>
    public static async Task<byte[]> EncodePngForExportAsync(Surface surface, double? dpi, CancellationToken cancellation = default)
    {
        byte[] png = await EncodePngAsync(surface, cancellation);
        return dpi is { } resolution ? PngMetadata.ApplyResolution(png, resolution) : png;
    }

    /// <summary>Encodes a composite over white, the way a JPEG export flattens transparency.</summary>
    public static async Task<byte[]> EncodeJpegAsync(Surface surface, double quality, byte matteRed = 255, byte matteGreen = 255, byte matteBlue = 255, CancellationToken cancellation = default)
    {
        using var stream = new InMemoryRandomAccessStream();
        var options = new List<KeyValuePair<string, BitmapTypedValue>>
        {
            new("ImageQuality", new BitmapTypedValue((float)Math.Clamp(quality, 0, 100) / 100f, Windows.Foundation.PropertyType.Single)),
        };
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream, options).AsTask(cancellation);
        byte[] flattened = FlattenOver(surface, matteRed, matteGreen, matteBlue);
        encoder.SetPixelData(
            BitmapPixelFormat.Rgba8,
            BitmapAlphaMode.Ignore,
            (uint)surface.Width,
            (uint)surface.Height,
            96, 96,
            flattened);
        await encoder.FlushAsync().AsTask(cancellation);
        return await ReadAllAsync(stream);
    }

    /// <summary>Straight RGBA over a matte color — JPEG has no alpha, so transparent areas
    /// take the background, as the macOS JPEGOptions matte does.</summary>
    private static byte[] FlattenOver(Surface surface, byte matteRed, byte matteGreen, byte matteBlue)
    {
        byte[] matte = [matteRed, matteGreen, matteBlue];
        var output = new byte[surface.Pixels.Length];
        for (int row = 0; row < surface.Height; row++)
        {
            int start = row * surface.Stride;
            for (int column = 0; column < surface.Width; column++)
            {
                int index = start + column * 4;
                byte alpha = surface.Pixels[index + 3];
                if (alpha == 255)
                {
                    surface.Pixels.AsSpan(index, 3).CopyTo(output.AsSpan(index));
                    output[index + 3] = 255;
                }
                else if (alpha == 0)
                {
                    output[index] = matteRed;
                    output[index + 1] = matteGreen;
                    output[index + 2] = matteBlue;
                    output[index + 3] = 255;
                }
                else
                {
                    for (int channel = 0; channel < 3; channel++)
                    {
                        // Premultiplied color over the matte: c + matte × (1 - a).
                        int value = surface.Pixels[index + channel] + (matte[channel] * (255 - alpha) + 127) / 255;
                        output[index + channel] = (byte)Math.Min(255, value);
                    }
                    output[index + 3] = 255;
                }
            }
        }
        return output;
    }

    private static async Task<IRandomAccessStream> StreamForAsync(byte[] bytes)
    {
        var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer());
        stream.Seek(0);
        return stream;
    }

    private static async Task<byte[]> ReadAllAsync(IRandomAccessStream stream)
    {
        ulong size = stream.Size;
        var bytes = new byte[size];
        stream.Seek(0);
        await stream.ReadAsync(bytes.AsBuffer(), (uint)size, InputStreamOptions.None);
        return bytes;
    }
}
