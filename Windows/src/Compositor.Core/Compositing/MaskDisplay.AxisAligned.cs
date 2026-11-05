using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Compositor.Core;

public static partial class MaskDisplay
{
    private readonly record struct CubicColumn(int I0, int I1, int I2, int I3, double W0, double W1, double W2, double W3, bool Exact);

    private static bool DrawAxisAligned(MaskSurface mask, LayerTransform transform, Surface destination, RectI band, double scale, byte background)
    {
        if (transform.Sampling != LayerSampling.HighQuality) return false;
        var mapping = new RasterMapping(transform, mask.Width, mask.Height, scale);
        if (!mapping.IsAxisAligned) return false;
        if (band.Width == 0 || band.Height == 0) return true;
        var columns = ArrayPool<CubicColumn>.Shared.Rent(band.Width);
        CubicBatch[]? batches = null;
        Vector256<double>[]? terms = null;
        try
        {
            // Keep the scalar mapping's exact expression and accumulation order.
            // Only its invariant clamps and cubic weights move out of the pixel loop.
            double startU = mapping.At(band.X + .5, band.Y + .5).U;
            for (int x = 0; x < band.Width; x++)
            {
                double u = startU + x * mapping.StepU;
                if (!(u >= 0 && u < mask.Width)) { columns[x] = new(-1, 0, 0, 0, 0, 0, 0, 0, false); continue; }
                double px = u - .5; int ix = (int)Math.Floor(px); double tx = px - ix;
                columns[x] = new(Math.Clamp(ix - 1, 0, mask.Width - 1), Math.Clamp(ix, 0, mask.Width - 1),
                    Math.Clamp(ix + 1, 0, mask.Width - 1), Math.Clamp(ix + 2, 0, mask.Width - 1),
                    Cubic(-1 - tx), Cubic(-tx), Cubic(1 - tx), Cubic(2 - tx), tx == 0);
            }
            if (Avx2.IsSupported && mask.Width >= 4 && band.Width >= 4)
            {
                batches = ArrayPool<CubicBatch>.Shared.Rent(band.Width / 4);
                for (int i = 0; i < band.Width / 4; i++) batches[i] = CubicBatch.Create(columns.AsSpan(i * 4, 4));
                terms = ArrayPool<Vector256<double>>.Shared.Rent(band.Width / 4 * 16);
            }
            Span<int> cachedRows = stackalloc int[4]; cachedRows.Fill(-1);
            uint backdrop = 0xff000000u | (uint)background * 0x010101u;
            var pixels = mask.Pixels;
            for (int y = band.Y; y < band.Y + band.Height; y++)
            {
                var output = MemoryMarshal.Cast<byte, uint>(destination.Pixels.AsSpan(y * destination.Stride + band.X * 4, band.Width * 4));
                double v = mapping.At(band.X + .5, y + .5).V;
                if (!(v >= 0 && v < mask.Height)) { output.Fill(backdrop); continue; }
                double py = v - .5; int iy = (int)Math.Floor(py); double ty = py - iy;
                int r0 = Math.Clamp(iy - 1, 0, mask.Height - 1) * mask.Width, r1 = Math.Clamp(iy, 0, mask.Height - 1) * mask.Width;
                int r2 = Math.Clamp(iy + 1, 0, mask.Height - 1) * mask.Width, r3 = Math.Clamp(iy + 2, 0, mask.Height - 1) * mask.Width;
                double w0 = Cubic(-1 - ty), w1 = Cubic(-ty), w2 = Cubic(1 - ty), w3 = Cubic(2 - ty);
                int x = batches is null ? 0 : DrawCubicBatches(pixels, columns, batches, terms!, cachedRows, mask.Width, band.Width / 4, output,
                    r0, r1, r2, r3, w0, w1, w2, w3, ty == 0, backdrop);
                for (; x < band.Width; x++) output[x] = CubicPixel(pixels, columns[x], r0, r1, r2, r3, w0, w1, w2, w3, ty == 0, backdrop);
            }
            return true;
        }
        finally
        {
            ArrayPool<CubicColumn>.Shared.Return(columns);
            if (batches is not null) ArrayPool<CubicBatch>.Shared.Return(batches);
            if (terms is not null) ArrayPool<Vector256<double>>.Shared.Return(terms);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint CubicPixel(byte[] pixels, in CubicColumn column, int r0, int r1, int r2, int r3,
        double w0, double w1, double w2, double w3, bool exactY, uint backdrop)
    {
        if (column.I0 < 0) return backdrop;
        byte gray;
        if (column.Exact && exactY) gray = pixels[r1 + column.I1];
        else
        {
            double value = 0;
            AddCubicRow(ref value, pixels, r0, column, w0); AddCubicRow(ref value, pixels, r1, column, w1);
            AddCubicRow(ref value, pixels, r2, column, w2); AddCubicRow(ref value, pixels, r3, column, w3);
            gray = (byte)Math.Clamp(Math.Round(value), 0, 255);
        }
        return 0xff000000u | (uint)gray * 0x010101u;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddCubicRow(ref double value, byte[] pixels, int row, in CubicColumn column, double weight)
    {
        value += pixels[row + column.I0] * column.W0 * weight;
        value += pixels[row + column.I1] * column.W1 * weight;
        value += pixels[row + column.I2] * column.W2 * weight;
        value += pixels[row + column.I3] * column.W3 * weight;
    }
}
