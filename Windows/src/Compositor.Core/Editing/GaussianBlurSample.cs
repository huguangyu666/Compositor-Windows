namespace Compositor.Core;

/// <summary>A frozen blur-stroke sample rendered only where stamps need it. Both passes
/// keep the configured edge behavior and byte rounding; retained window memory is bounded.</summary>
internal sealed class GaussianBlurSample
{
    private const long MaxRetainedBytes = 2 * 1024 * 1024;
    private const int MetadataBytes = 256;
    private readonly Surface source;
    private readonly float[] kernel;
    private readonly int radius;
    private readonly bool transparentEdges;
    private Surface? cached;
    private byte[]? horizontal;
    private RectI cachedRegion;

    internal GaussianBlurSample(Surface source, double sigma, bool transparentEdges = false)
    {
        this.source = source;
        this.transparentEdges = transparentEdges;
        kernel = CreateKernel(sigma, out radius);
    }

    internal static float[] CreateKernel(double sigma, out int radius)
    {
        sigma = Math.Max(.1, sigma);
        radius = Math.Max(1, (int)Math.Ceiling(sigma * 3));
        var kernel = new float[radius * 2 + 1];
        double sum = 0;
        for (int i = 0; i < kernel.Length; i++)
        {
            double x = i - radius;
            kernel[i] = (float)Math.Exp(-x * x / (2 * sigma * sigma));
            sum += kernel[i];
        }
        for (int i = 0; i < kernel.Length; i++) kernel[i] /= (float)sum;
        return kernel;
    }

    internal int Radius => radius;

    /// <summary>The rect a stamp of  samples with its full kernel: the stamp plus a
    /// radius margin on every side, the taps the filter reads when pixels feather outward.</summary>
    internal static RectI KernelSpan(RectI region, int radius) =>
        new(region.X - radius, region.Y - radius, region.Width + 2 * radius, region.Height + 2 * radius);

    internal long RetainedBytes => (cached?.Pixels.LongLength ?? 0) + (horizontal?.LongLength ?? 0) + kernel.LongLength * 4 + MetadataBytes;

    internal SourceWindow Render(RectI region)
    {
        if (cached is not null && cachedRegion.X <= region.X && cachedRegion.Y <= region.Y
            && cachedRegion.X + cachedRegion.Width >= region.X + region.Width
            && cachedRegion.Y + cachedRegion.Height >= region.Y + region.Height)
            return new(cached, source.Width, source.Height, cachedRegion.X, cachedRegion.Y);

        // A small margin lets nearby dabs reuse the same frozen sample.
        var expanded = RectI.Normalize(region.X - 16, region.Y - 16,
            region.X + region.Width + 16, region.Y + region.Height + 16, source.Width, source.Height);
        // A growing stroke's stamp reaches past the source: with the transparent-edge rule the
        // window may hold pixels the source has none of — those taps simply contribute zero.
        if (transparentEdges) expanded = RectI.Union(expanded, region);
        var output = RequiredBytes(expanded) <= MaxRetainedBytes ? expanded : region;
        int firstRow = Math.Max(0, output.Y - radius);
        int lastRow = Math.Min(source.Height, output.Y + output.Height + radius);
        int horizontalBytes = checked(output.Width * (lastRow - firstRow) * 4);
        bool retain = RequiredBytes(output) <= MaxRetainedBytes;
        Surface result;
        byte[] scratch;
        if (retain)
        {
            if (cached is null || cached.Width != output.Width || cached.Height != output.Height)
                cached = new(output.Width, output.Height);
            if (horizontal is null || horizontal.Length < horizontalBytes
                || horizontal.LongLength + cached.Pixels.LongLength + kernel.LongLength * 4 + MetadataBytes > MaxRetainedBytes)
                horizontal = new byte[horizontalBytes];
            result = cached; scratch = horizontal;
            cachedRegion = output;
        }
        else
        {
            // Large stamps may need a large temporary result, but do not pin it between dabs.
            cached = null; horizontal = null; cachedRegion = default;
            result = new(output.Width, output.Height); scratch = new byte[horizontalBytes];
        }

        for (int y = firstRow; y < lastRow; y++)
        for (int x = output.X; x < output.X + output.Width; x++)
        {
            int target = ((y - firstRow) * output.Width + x - output.X) * 4;
            int sourceRow = y * source.Stride;
            float red = 0, green = 0, blue = 0, alpha = 0;
            // The four channels share the same sample coordinates. Keep each channel's accumulation
            // order while calculating the clamp and byte address once per tap.
            for (int i = 0; i < kernel.Length; i++)
            {
                int sampleX = x + i - radius;
                if (transparentEdges && (uint)sampleX >= (uint)source.Width) continue;
                sampleX = Math.Clamp(sampleX, 0, source.Width - 1);
                int at = sourceRow + sampleX * 4;
                float weight = kernel[i];
                red += weight * source.Pixels[at];
                green += weight * source.Pixels[at + 1];
                blue += weight * source.Pixels[at + 2];
                alpha += weight * source.Pixels[at + 3];
            }
            scratch[target] = Round(red);
            scratch[target + 1] = Round(green);
            scratch[target + 2] = Round(blue);
            scratch[target + 3] = Round(alpha);
        }
        for (int y = output.Y; y < output.Y + output.Height; y++)
        for (int x = 0; x < output.Width; x++)
        {
            int target = (y - output.Y) * result.Stride + x * 4;
            float red = 0, green = 0, blue = 0, alpha = 0;
            for (int i = 0; i < kernel.Length; i++)
            {
                int sampleY = y + i - radius;
                if (transparentEdges && (uint)sampleY >= (uint)source.Height) continue;
                sampleY = Math.Clamp(sampleY, 0, source.Height - 1);
                int at = ((sampleY - firstRow) * output.Width + x) * 4;
                float weight = kernel[i];
                red += weight * scratch[at];
                green += weight * scratch[at + 1];
                blue += weight * scratch[at + 2];
                alpha += weight * scratch[at + 3];
            }
            result.Pixels[target] = Round(red);
            result.Pixels[target + 1] = Round(green);
            result.Pixels[target + 2] = Round(blue);
            result.Pixels[target + 3] = Round(alpha);
        }
        return new(result, source.Width, source.Height, output.X, output.Y);
    }

    private static byte Round(float value) => (byte)Math.Clamp(MathF.Round(value, MidpointRounding.AwayFromZero), 0, 255);

    private long RequiredBytes(RectI region)
    {
        int rows = Math.Min(source.Height, region.Y + region.Height + radius) - Math.Max(0, region.Y - radius);
        return (long)region.Width * (region.Height + rows) * 4 + kernel.LongLength * 4 + MetadataBytes;
    }
}
