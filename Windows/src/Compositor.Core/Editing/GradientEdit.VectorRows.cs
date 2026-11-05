using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Compositor.Core;

public sealed partial class GradientEdit
{
    private sealed class VectorGradient
    {
        private readonly Vector256<double> startX, startY, dx, dy, inverseLength2, opacity;
        private readonly Vector256<double> red, green, blue, redDelta, greenDelta, blueDelta;
        private readonly bool radial, reversed, transparent, opaque;

        public static bool Supports(PointD start, PointD end) => Avx.IsSupported && Sse41.IsSupported
            // Keep squared distances finite, including radial gradients with distant endpoints.
            && Math.Abs(start.X) < 1e150 && Math.Abs(start.Y) < 1e150
            && Math.Abs(end.X) < 1e150 && Math.Abs(end.Y) < 1e150;

        public VectorGradient(PointD start, double dx, double dy, double inverseLength2, GradientSettings settings, double opacity)
        {
            startX = Vector256.Create(start.X); startY = Vector256.Create(start.Y);
            this.dx = Vector256.Create(dx); this.dy = Vector256.Create(dy);
            this.inverseLength2 = Vector256.Create(inverseLength2); this.opacity = Vector256.Create(opacity);
            radial = settings.Shape == GradientShape.Radial; reversed = settings.Reversed;
            transparent = settings.Style == GradientStyle.ForegroundToTransparent;
            opaque = !transparent && opacity == 1;
            var a = settings.Foreground; var b = transparent ? a : settings.Background;
            red = Vector256.Create((double)a.Red); green = Vector256.Create((double)a.Green); blue = Vector256.Create((double)a.Blue);
            redDelta = Vector256.Create((double)b.Red - a.Red); greenDelta = Vector256.Create((double)b.Green - a.Green);
            blueDelta = Vector256.Create((double)b.Blue - a.Blue);
        }

        public int PaintRow(byte[] pixels, int offset, int width, PointD row, double columnX, double columnY, bool mask, out bool changed)
        {
            var indices = Vector256.Create(0d, 1d, 2d, 3d); var four = Vector256.Create(4d);
            var rowX = Vector256.Create(row.X); var rowY = Vector256.Create(row.Y);
            var stepX = Vector256.Create(columnX); var stepY = Vector256.Create(columnY);
            var one = Vector256.Create(1d); var byteMask = Vector128.Create(255u);
            int last = width & ~3; changed = false;
            for (int x = 0; x < last; x += 4, indices = Avx.Add(indices, four))
            {
                // Match scalar row + x * column, then subtract start. Avoid reassociation/FMA.
                var px = Avx.Subtract(Avx.Add(rowX, Avx.Multiply(indices, stepX)), startX);
                var py = Avx.Subtract(Avx.Add(rowY, Avx.Multiply(indices, stepY)), startY);
                var t = radial
                    ? Avx.Sqrt(Avx.Multiply(Avx.Add(Avx.Multiply(px, px), Avx.Multiply(py, py)), inverseLength2))
                    : Avx.Multiply(Avx.Add(Avx.Multiply(px, dx), Avx.Multiply(py, dy)), inverseLength2);
                t = Avx.Min(Avx.Max(t, Vector256<double>.Zero), one);
                if (reversed) t = Avx.Subtract(one, t);
                var alpha = transparent ? Avx.Multiply(opacity, Avx.Subtract(one, t)) : opacity;
                var inverseAlpha = Avx.Subtract(one, alpha);
                var r = Avx.Add(red, Avx.Multiply(redDelta, t));
                if (mask)
                {
                    uint original = Unsafe.ReadUnaligned<uint>(ref pixels[offset + x]);
                    var source = Sse41.ConvertToVector128Int32(Vector128.CreateScalar(original).AsByte());
                    var value = opaque ? Round(r) : Blend(r, source, alpha, inverseAlpha);
                    uint packed = Sse2.PackUnsignedSaturate(Sse2.PackSignedSaturate(value, value), Vector128<short>.Zero).AsUInt32().ToScalar();
                    changed |= packed != original;
                    Unsafe.WriteUnaligned(ref pixels[offset + x], packed);
                }
                else
                {
                    int i = (offset + x) * 4;
                    var original = Unsafe.ReadUnaligned<Vector128<uint>>(ref pixels[i]);
                    var g = Avx.Add(green, Avx.Multiply(greenDelta, t));
                    var b = Avx.Add(blue, Avx.Multiply(blueDelta, t));
                    Vector128<uint> rp, gp, bp, ap;
                    if (opaque)
                    {
                        rp = Round(r).AsUInt32(); gp = Round(g).AsUInt32(); bp = Round(b).AsUInt32(); ap = byteMask;
                    }
                    else
                    {
                        rp = Blend(r, Sse2.And(original, byteMask).AsInt32(), alpha, inverseAlpha).AsUInt32();
                        gp = Blend(g, Sse2.And(Sse2.ShiftRightLogical(original, 8), byteMask).AsInt32(), alpha, inverseAlpha).AsUInt32();
                        bp = Blend(b, Sse2.And(Sse2.ShiftRightLogical(original, 16), byteMask).AsInt32(), alpha, inverseAlpha).AsUInt32();
                        ap = Blend(Vector256.Create(255d), Sse2.ShiftRightLogical(original, 24).AsInt32(), alpha, inverseAlpha).AsUInt32();
                    }
                    var packed = Sse2.Or(Sse2.Or(rp, Sse2.ShiftLeftLogical(gp, 8)),
                        Sse2.Or(Sse2.ShiftLeftLogical(bp, 16), Sse2.ShiftLeftLogical(ap, 24)));
                    changed |= Sse2.MoveMask(Sse2.CompareEqual(packed, original).AsByte()) != 0xffff;
                    Unsafe.WriteUnaligned(ref pixels[i], packed);
                }
            }
            return last;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<int> Blend(Vector256<double> color, Vector128<int> source, Vector256<double> alpha, Vector256<double> inverseAlpha)
            => Round(Avx.Add(Avx.Multiply(color, alpha), Avx.Multiply(Avx.ConvertToVector256Double(source), inverseAlpha)));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<int> Round(Vector256<double> value)
            // All lanes are finite, nonnegative and <=255; truncation implements Floor(value + .5).
            => Avx.ConvertToVector128Int32WithTruncation(Avx.Add(value, Vector256.Create(.5)));
    }
}
