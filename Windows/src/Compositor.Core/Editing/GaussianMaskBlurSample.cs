namespace Compositor.Core;

internal sealed record MaskBlurWindow(RectI Bounds, byte[] Pixels);

/// <summary>A local gray sample of the frozen mask, with its edge tone outside the source.</summary>
internal sealed class GaussianMaskBlurSample
{
    private const long MaxRetainedBytes = 2 * 1024 * 1024;
    private readonly MaskSurface source;
    private readonly byte background;
    private readonly float[] kernel;
    private readonly int radius;
    private MaskBlurWindow? cached;
    private byte[]? horizontal;

    internal GaussianMaskBlurSample(MaskSurface source, double sigma, byte background)
    {
        this.source = source; this.background = background;
        kernel = GaussianBlurSample.CreateKernel(sigma, out radius);
    }

    internal long RetainedBytes => (cached?.Pixels.LongLength ?? 0) + (horizontal?.LongLength ?? 0) + kernel.LongLength * 4 + 256;

    internal MaskBlurWindow Render(RectI region)
    {
        if (cached is { Bounds: var bounds } && bounds.X <= region.X && bounds.Y <= region.Y
            && bounds.X + bounds.Width >= region.X + region.Width && bounds.Y + bounds.Height >= region.Y + region.Height) return cached;
        var expanded = RectI.Normalize(region.X - 16, region.Y - 16, region.X + region.Width + 16,
            region.Y + region.Height + 16, source.Width, source.Height);
        var output = RequiredBytes(expanded) <= MaxRetainedBytes ? expanded : region;
        int firstRow = Math.Max(0, output.Y - radius), lastRow = Math.Min(source.Height, output.Y + output.Height + radius);
        int scratchBytes = checked(output.Width * (lastRow - firstRow));
        bool retain = RequiredBytes(output) <= MaxRetainedBytes;
        byte[] scratch, pixels;
        if (retain)
        {
            if (cached is null || cached.Pixels.Length != output.Width * output.Height)
                cached = new(output, new byte[checked(output.Width * output.Height)]);
            else cached = cached with { Bounds = output };
            if (horizontal is null || horizontal.Length < scratchBytes
                || horizontal.LongLength + cached.Pixels.LongLength + kernel.LongLength * 4 + 256 > MaxRetainedBytes)
                horizontal = new byte[scratchBytes];
            scratch = horizontal; pixels = cached.Pixels;
        }
        else
        {
            cached = null; horizontal = null;
            scratch = new byte[scratchBytes]; pixels = new byte[checked(output.Width * output.Height)];
        }
        for (int y = firstRow; y < lastRow; y++)
        for (int x = output.X; x < output.X + output.Width; x++)
        {
            float value = 0;
            for (int i = 0; i < kernel.Length; i++)
            {
                int sx = x + i - radius;
                value += kernel[i] * ((uint)sx < (uint)source.Width ? source.Pixels[y * source.Width + sx] : background);
            }
            scratch[(y - firstRow) * output.Width + x - output.X] = Round(value);
        }
        for (int y = output.Y; y < output.Y + output.Height; y++)
        for (int x = 0; x < output.Width; x++)
        {
            float value = 0;
            for (int i = 0; i < kernel.Length; i++)
            {
                int sy = y + i - radius;
                value += kernel[i] * ((uint)sy < (uint)source.Height ? scratch[(sy - firstRow) * output.Width + x] : background);
            }
            pixels[(y - output.Y) * output.Width + x] = Round(value);
        }
        return new(output, pixels);
    }

    private long RequiredBytes(RectI region) => (long)region.Width * (region.Height
        + Math.Min(source.Height, region.Y + region.Height + radius) - Math.Max(0, region.Y - radius)) + kernel.LongLength * 4 + 256;
    private static byte Round(float value) => (byte)Math.Clamp(MathF.Round(value, MidpointRounding.AwayFromZero), 0, 255);
}
