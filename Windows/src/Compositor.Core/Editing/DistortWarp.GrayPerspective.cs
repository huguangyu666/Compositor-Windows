using System.Buffers;
using System.Numerics;
using System.Runtime.Intrinsics.X86;

namespace Compositor.Core;

public static partial class DistortWarp
{
    private static void WarpPerspectiveMask(MaskSurface mask, MaskSurface result, Perspective map,
        int boundX, int boundY, double factor, CancellationToken token)
    {
        int width = result.Width;
        int sourceWidth = mask.Width, sourceHeight = mask.Height;
        var pixels = mask.Pixels;
        var scratch = ArrayPool<double>.Shared.Rent(width * 5);
        try
        {
            var m11 = scratch.AsSpan(0, width); var m12 = scratch.AsSpan(width, width); var rx = scratch.AsSpan(width * 2, width);
            var unitsU = scratch.AsSpan(width * 3, width); var unitsV = scratch.AsSpan(width * 4, width);
            for (int x = 0; x < width; x++)
            {
                double px = boundX + (x + .5) / factor;
                m11[x] = map.A - px * map.G; m12[x] = map.B - px * map.H; rx[x] = px - map.C;
            }
            for (int y = 0; y < result.Height; y++)
            {
                token.ThrowIfCancellationRequested();
                var output = result.Pixels.AsSpan(y * width, width);
                double py = boundY + (y + .5) / factor;
                double m21 = map.D - py * map.G, m22 = map.E - py * map.H, ry = py - map.F;
                int x = 0;
                if (Vector.IsHardwareAccelerated)
                {
                    var v21 = new Vector<double>(m21); var v22 = new Vector<double>(m22); var vry = new Vector<double>(ry);
                    var epsilon = new Vector<double>(1e-12); var outside = new Vector<double>(2);
                    for (; x <= width - Vector<double>.Count; x += Vector<double>.Count)
                    {
                        var v11 = new Vector<double>(m11.Slice(x)); var v12 = new Vector<double>(m12.Slice(x)); var vrx = new Vector<double>(rx.Slice(x));
                        var determinant = v11 * v22 - v12 * v21;
                        // Preserve the scalar inverse's operations, including its singular test.
                        var u = (v22 * vrx - v12 * vry) / determinant;
                        Vector.ConditionalSelect(Vector.LessThan(Vector.Abs(determinant), epsilon), outside, u).CopyTo(unitsU.Slice(x));
                        ((v11 * vry - v21 * vrx) / determinant).CopyTo(unitsV.Slice(x));
                    }
                }
                for (; x < width; x++)
                {
                    double determinant = m11[x] * m22 - m12[x] * m21;
                    unitsU[x] = Math.Abs(determinant) < 1e-12 ? 2 : (m22 * rx[x] - m12[x] * ry) / determinant;
                    unitsV[x] = (m11[x] * ry - m21 * rx[x]) / determinant;
                }
                x = Avx2.IsSupported && sourceWidth > 0 && sourceHeight > 0 && pixels.Length >= 4
                    ? SamplePerspectiveRowAvx2(pixels, sourceWidth, sourceHeight, unitsU, unitsV, output)
                    : Vector.IsHardwareAccelerated ? SamplePerspectiveRow(pixels, sourceWidth, sourceHeight, unitsU, unitsV, output) : 0;
                for (; x < width; x++)
                {
                    double u = unitsU[x], v = unitsV[x];
                    if (u < 0 || u > 1 || v < 0 || v > 1) continue;
                    double sx = u * sourceWidth - .5, sy = v * sourceHeight - .5;
                    int ix = (int)Math.Floor(sx), iy = (int)Math.Floor(sy);
                    double fx = sx - ix, fy = sy - iy;
                    int x0 = Math.Clamp(ix, 0, sourceWidth - 1), x1 = Math.Clamp(ix + 1, 0, sourceWidth - 1);
                    int top = Math.Clamp(iy, 0, sourceHeight - 1) * sourceWidth, bottom = Math.Clamp(iy + 1, 0, sourceHeight - 1) * sourceWidth;
                    double a = pixels[top + x0] + (pixels[top + x1] - pixels[top + x0]) * fx;
                    double b = pixels[bottom + x0] + (pixels[bottom + x1] - pixels[bottom + x0]) * fx;
                    output[x] = (byte)Math.Clamp((int)Math.Round(a + (b - a) * fy), 0, 255);
                }
            }
        }
        finally { ArrayPool<double>.Shared.Return(scratch); }
    }

    private static int SamplePerspectiveRow(byte[] pixels, int width, int height, ReadOnlySpan<double> unitsU,
        ReadOnlySpan<double> unitsV, Span<byte> output)
    {
        int lanes = Vector<double>.Count, x = 0;
        Span<double> taps = stackalloc double[lanes * 4];
        var a0 = taps.Slice(0, lanes); var da = taps.Slice(lanes, lanes);
        var b0 = taps.Slice(lanes * 2, lanes); var db = taps.Slice(lanes * 3, lanes);
        var sourceWidth = new Vector<double>(width); var sourceHeight = new Vector<double>(height); var half = new Vector<double>(.5);
        for (; x <= output.Length - lanes; x += lanes)
        {
            var u = new Vector<double>(unitsU.Slice(x)); var v = new Vector<double>(unitsV.Slice(x));
            var sx = u * sourceWidth - half; var sy = v * sourceHeight - half;
            var ix = Vector.Floor(sx); var iy = Vector.Floor(sy); var fx = sx - ix; var fy = sy - iy;
            for (int lane = 0; lane < lanes; lane++)
            {
                if (u[lane] < 0 || u[lane] > 1 || v[lane] < 0 || v[lane] > 1)
                { a0[lane] = da[lane] = b0[lane] = db[lane] = 0; continue; }
                int px = (int)ix[lane], py = (int)iy[lane];
                int x0 = Math.Clamp(px, 0, width - 1), x1 = Math.Clamp(px + 1, 0, width - 1);
                int top = Math.Clamp(py, 0, height - 1) * width, bottom = Math.Clamp(py + 1, 0, height - 1) * width;
                int a = pixels[top + x0], b = pixels[bottom + x0];
                a0[lane] = a; da[lane] = pixels[top + x1] - a; b0[lane] = b; db[lane] = pixels[bottom + x1] - b;
            }
            var upper = new Vector<double>(a0) + new Vector<double>(da) * fx;
            var lower = new Vector<double>(b0) + new Vector<double>(db) * fx;
            var value = upper + (lower - upper) * fy;
            for (int lane = 0; lane < lanes; lane++)
                if (!(u[lane] < 0 || u[lane] > 1 || v[lane] < 0 || v[lane] > 1))
                    output[x + lane] = (byte)Math.Clamp((int)Math.Round(value[lane]), 0, 255);
        }
        return x;
    }
}
