using System.Buffers;
using System.Numerics;

namespace Compositor.Core;

public static partial class DistortWarp
{
    private static void WarpPerspectiveImage(Surface image, Surface result, Perspective map,
        int boundX, int boundY, double factor, CancellationToken token)
    {
        int width = result.Width;
        var scratch = ArrayPool<double>.Shared.Rent(width * 3);
        try
        {
            var m11 = scratch.AsSpan(0, width); var m12 = scratch.AsSpan(width, width); var rx = scratch.AsSpan(width * 2, width);
            int lanes = Vector<double>.Count;
            Span<double> samples = stackalloc double[lanes * 6];
            var unitsU = samples.Slice(0, lanes); var unitsV = samples.Slice(lanes, lanes);
            var ix = samples.Slice(lanes * 2, lanes); var iy = samples.Slice(lanes * 3, lanes);
            var fx = samples.Slice(lanes * 4, lanes); var fy = samples.Slice(lanes * 5, lanes);
            for (int x = 0; x < width; x++)
            {
                double px = boundX + (x + .5) / factor;
                m11[x] = map.A - px * map.G; m12[x] = map.B - px * map.H; rx[x] = px - map.C;
            }
            for (int y = 0; y < result.Height; y++)
            {
                token.ThrowIfCancellationRequested();
                double py = boundY + (y + .5) / factor;
                double m21 = map.D - py * map.G, m22 = map.E - py * map.H, ry = py - map.F;
                int output = y * result.Stride;
                int x = 0;
                if (Vector.IsHardwareAccelerated)
                {
                    var v21 = new Vector<double>(m21); var v22 = new Vector<double>(m22); var vry = new Vector<double>(ry);
                    var epsilon = new Vector<double>(1e-12); var outside = new Vector<double>(2);
                    var sourceWidth = new Vector<double>(image.Width); var sourceHeight = new Vector<double>(image.Height);
                    var half = new Vector<double>(.5);
                    for (; x <= width - lanes; x += lanes)
                    {
                        var v11 = new Vector<double>(m11.Slice(x)); var v12 = new Vector<double>(m12.Slice(x)); var vrx = new Vector<double>(rx.Slice(x));
                        var determinant = v11 * v22 - v12 * v21;
                        // Keep the scalar inverse's arithmetic order and singular test.
                        var u = Vector.ConditionalSelect(Vector.LessThan(Vector.Abs(determinant), epsilon), outside,
                            (v22 * vrx - v12 * vry) / determinant);
                        var v = (v11 * vry - v21 * vrx) / determinant;
                        u.CopyTo(unitsU); v.CopyTo(unitsV);
                        var sx = u * sourceWidth - half; var sy = v * sourceHeight - half;
                        var floorX = Vector.Floor(sx); var floorY = Vector.Floor(sy);
                        floorX.CopyTo(ix); floorY.CopyTo(iy); (sx - floorX).CopyTo(fx); (sy - floorY).CopyTo(fy);
                        for (int lane = 0; lane < lanes; lane++)
                        {
                            if (unitsU[lane] < 0 || unitsU[lane] > 1 || unitsV[lane] < 0 || unitsV[lane] > 1) continue;
                            SampleBilinear(image, (int)ix[lane], (int)iy[lane], fx[lane], fy[lane], result.Pixels, output + (x + lane) * 4);
                        }
                    }
                }
                for (; x < width; x++)
                {
                    double determinant = m11[x] * m22 - m12[x] * m21;
                    if (Math.Abs(determinant) < 1e-12) continue;
                    double u = (m22 * rx[x] - m12[x] * ry) / determinant;
                    double v = (m11[x] * ry - m21 * rx[x]) / determinant;
                    if (u < 0 || u > 1 || v < 0 || v > 1) continue;
                    double sx = u * image.Width - .5, sy = v * image.Height - .5;
                    int px = (int)Math.Floor(sx), pySource = (int)Math.Floor(sy);
                    SampleBilinear(image, px, pySource, sx - px, sy - pySource, result.Pixels, output + x * 4);
                }
            }
        }
        finally { ArrayPool<double>.Shared.Return(scratch); }
    }
}
