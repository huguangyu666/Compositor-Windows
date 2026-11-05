using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Compositor.Core;

public static partial class MaskDisplay
{
    private sealed class EdgeTone { public int Revision = -1; public byte Value; }
    private static readonly ConditionalWeakTable<MaskSurface, EdgeTone> edgeTones = new();

    public static byte Background(MaskSurface mask)
    {
        var cached = edgeTones.GetOrCreateValue(mask);
        lock (cached)
        {
            if (cached.Revision == mask.Revision) return cached.Value;
            // LayerMask.background reads the perimeter of a thumbnail capped at 96 pixels.
            double factor = Math.Min(1, 96d / Math.Max(mask.Width, mask.Height));
            int width = Math.Max(1, (int)(mask.Width * factor)), height = Math.Max(1, (int)(mask.Height * factor));
            int total = 0, count = 0;
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                if (x != 0 && x != width - 1 && y != 0 && y != height - 1) continue;
                total += Sample(mask, (x + .5) * mask.Width / width, (y + .5) * mask.Height / height, LayerSampling.HighQuality); count++;
            }
            cached.Revision = mask.Revision;
            return cached.Value = (byte)(total * 2 >= count * 255 ? 255 : 0);
        }
    }

    public static void Draw(MaskSurface mask, LayerTransform transform, Surface destination, RectI region, double scale = 1)
    {
        byte background = Background(mask);
        var band = RectI.Normalize(region.X, region.Y, region.X + region.Width, region.Y + region.Height, destination.Width, destination.Height);
        if (DrawRows(mask, transform, destination, band, scale, background)) return;
        if (DrawAxisAligned(mask, transform, destination, band, scale, background)) return;
        DrawResampled(mask, transform, destination, band, scale, background);
    }

    internal static void DrawReference(MaskSurface mask, LayerTransform transform, Surface destination, RectI region, double scale = 1)
    {
        var band = RectI.Normalize(region.X, region.Y, region.X + region.Width, region.Y + region.Height, destination.Width, destination.Height);
        DrawResampled(mask, transform, destination, band, scale, Background(mask));
    }

    private static void DrawResampled(MaskSurface mask, LayerTransform transform, Surface destination, RectI band, double scale, byte background)
    {
        var mapping = new RasterMapping(transform, mask.Width, mask.Height, scale);
        for (int y = band.Y; y < band.Y + band.Height; y++)
        {
            var (startU, startV) = mapping.At(band.X + .5, y + .5);
            for (int x = band.X; x < band.X + band.Width; x++)
            {
                double u = startU + (x - band.X) * mapping.StepU, v = startV + (x - band.X) * mapping.StepV;
                byte gray = u >= 0 && v >= 0 && u < mask.Width && v < mask.Height ? Sample(mask, u, v, transform.Sampling) : background;
                int i = y * destination.Stride + x * 4;
                destination.Pixels[i] = destination.Pixels[i + 1] = destination.Pixels[i + 2] = gray; destination.Pixels[i + 3] = 255;
            }
        }
    }

    // Exact 1:1 placements need gray-to-RGBA expansion only, including flipped rows.
    private static bool DrawRows(MaskSurface mask, LayerTransform transform, Surface destination, RectI band, double scale, byte background)
    {
        double left = transform.Origin.X * scale, top = transform.Origin.Y * scale;
        if (transform.Rotation % 360 != 0 || transform.Size.Width * scale != mask.Width || transform.Size.Height * scale != mask.Height
            || left != Math.Floor(left) || top != Math.Floor(top)) return false;
        int x0 = (int)Math.Clamp(left, band.X, band.X + band.Width), x1 = (int)Math.Clamp(left + mask.Width, band.X, band.X + band.Width);
        uint backdrop = 0xff000000u | (uint)background * 0x010101u;
        for (int y = band.Y; y < band.Y + band.Height; y++)
        {
            var output = MemoryMarshal.Cast<byte, uint>(destination.Pixels.AsSpan(y * destination.Stride + band.X * 4, band.Width * 4));
            if (x0 >= x1 || y < top || y >= top + mask.Height) { output.Fill(backdrop); continue; }
            int py = (int)(y - top);
            if (x0 > band.X || x1 < band.X + band.Width) output.Fill(backdrop);
            if (transform.FlipY) py = mask.Height - 1 - py;
            int sourceRow = py * mask.Width;
            for (int x = x0; x < x1; x++)
            {
                int px = (int)(x - left); if (transform.FlipX) px = mask.Width - 1 - px;
                output[x - band.X] = 0xff000000u | (uint)mask.Pixels[sourceRow + px] * 0x010101u;
            }
        }
        return true;
    }

    internal static byte Sample(MaskSurface mask, double u, double v, LayerSampling quality)
    {
        if (quality == LayerSampling.Nearest) return mask.Pixels[Math.Clamp((int)v, 0, mask.Height - 1) * mask.Width + Math.Clamp((int)u, 0, mask.Width - 1)];
        double px = u - .5, py = v - .5; int x0 = (int)Math.Floor(px), y0 = (int)Math.Floor(py);
        double tx = px - x0, ty = py - y0;
        byte Read(int x, int y) => mask.Pixels[Math.Clamp(y, 0, mask.Height - 1) * mask.Width + Math.Clamp(x, 0, mask.Width - 1)];
        if (tx == 0 && ty == 0) return Read(x0, y0);
        double result;
        if (quality == LayerSampling.Smooth)
        {
            double top = Read(x0, y0) * (1 - tx) + Read(x0 + 1, y0) * tx;
            double bottom = Read(x0, y0 + 1) * (1 - tx) + Read(x0 + 1, y0 + 1) * tx;
            result = top * (1 - ty) + bottom * ty;
        }
        else
        {
            result = 0;
            for (int y = -1; y <= 2; y++)
            for (int x = -1; x <= 2; x++) result += Read(x0 + x, y0 + y) * Cubic(x - tx) * Cubic(y - ty);
        }
        return (byte)Math.Clamp(Math.Round(result), 0, 255);
    }

    private static double Cubic(double x)
    {
        x = Math.Abs(x);
        return x < 1 ? (1.5 * x - 2.5) * x * x + 1 : x < 2 ? ((-.5 * x + 2.5) * x - 4) * x + 2 : 0;
    }
}

internal readonly struct RasterMapping
{
    private readonly double stepUY, stepVY, offsetU, offsetV;
    public readonly double StepU, StepV;
    public bool IsAxisAligned => stepUY == 0 && StepV == 0;
    public RasterMapping(LayerTransform transform, int width, int height, double scale = 1)
    {
        double cos = Math.Cos(transform.Radians), sin = Math.Sin(transform.Radians);
        double signX = transform.FlipX ? -1 : 1, signY = transform.FlipY ? -1 : 1;
        StepU = signX * cos * width / (transform.Size.Width * scale); stepUY = signX * sin * width / (transform.Size.Width * scale);
        StepV = -signY * sin * height / (transform.Size.Height * scale); stepVY = signY * cos * height / (transform.Size.Height * scale);
        offsetU = width * .5 - (StepU * transform.Center.X + stepUY * transform.Center.Y) * scale;
        offsetV = height * .5 - (StepV * transform.Center.X + stepVY * transform.Center.Y) * scale;
    }
    public (double U, double V) At(double x, double y) => (StepU * x + stepUY * y + offsetU, StepV * x + stepVY * y + offsetV);
}
