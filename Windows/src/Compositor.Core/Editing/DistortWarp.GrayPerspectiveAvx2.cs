using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Compositor.Core;

public static partial class DistortWarp
{
    private static unsafe int SamplePerspectiveRowAvx2(byte[] pixels, int width, int height, ReadOnlySpan<double> unitsU,
        ReadOnlySpan<double> unitsV, Span<byte> output)
    {
        var sourceWidth = Vector256.Create((double)width); var sourceHeight = Vector256.Create((double)height);
        var half = Vector256.Create(.5); var zero = Vector256<double>.Zero; var one = Vector256.Create(1d);
        var maxX = Vector128.Create(width - 1); var maxY = Vector128.Create(height - 1);
        var rowWidth = Vector128.Create(width); var lastStart = Vector128.Create(pixels.Length - 4);
        Span<int> gray = stackalloc int[4];
        int x = 0;
        fixed (byte* source = pixels)
        for (; x <= output.Length - 4; x += 4)
        {
            var u = Vector256.Create(unitsU.Slice(x)); var v = Vector256.Create(unitsV.Slice(x));
            var sx = Avx.Subtract(Avx.Multiply(u, sourceWidth), half); var sy = Avx.Subtract(Avx.Multiply(v, sourceHeight), half);
            var floorX = Avx.Floor(sx); var floorY = Avx.Floor(sy);
            var ix = Avx.ConvertToVector128Int32WithTruncation(floorX); var iy = Avx.ConvertToVector128Int32WithTruncation(floorY);
            var x0 = Sse41.Min(Sse41.Max(ix, Vector128<int>.Zero), maxX);
            var x1 = Sse41.Min(Sse41.Max(Sse2.Add(ix, Vector128.Create(1)), Vector128<int>.Zero), maxX);
            var top = Sse41.MultiplyLow(Sse41.Min(Sse41.Max(iy, Vector128<int>.Zero), maxY), rowWidth);
            var bottom = Sse41.MultiplyLow(Sse41.Min(Sse41.Max(Sse2.Add(iy, Vector128.Create(1)), Vector128<int>.Zero), maxY), rowWidth);
            var a0 = GatherGray(source, Sse2.Add(top, x0), lastStart); var a1 = GatherGray(source, Sse2.Add(top, x1), lastStart);
            var b0 = GatherGray(source, Sse2.Add(bottom, x0), lastStart); var b1 = GatherGray(source, Sse2.Add(bottom, x1), lastStart);
            var fx = Avx.Subtract(sx, floorX); var fy = Avx.Subtract(sy, floorY);
            // Keep the original two horizontal lerps, vertical lerp and RoundToEven order.
            var upper = Avx.Add(a0, Avx.Multiply(Avx.Subtract(a1, a0), fx));
            var lower = Avx.Add(b0, Avx.Multiply(Avx.Subtract(b1, b0), fx));
            var value = Avx.Add(upper, Avx.Multiply(Avx.Subtract(lower, upper), fy));
            var rounded = Avx.Max(zero, Avx.Min(Avx.RoundToNearestInteger(value), Vector256.Create(255d)));
            Avx.ConvertToVector128Int32WithTruncation(rounded).CopyTo(gray);
            int outside = Avx.MoveMask(Avx.Or(Avx.Or(Avx.CompareLessThan(u, zero), Avx.CompareGreaterThan(u, one)),
                Avx.Or(Avx.CompareLessThan(v, zero), Avx.CompareGreaterThan(v, one))));
            for (int lane = 0; lane < 4; lane++) if ((outside & (1 << lane)) == 0) output[x + lane] = (byte)gray[lane];
        }
        return x;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe Vector256<double> GatherGray(byte* source, Vector128<int> offsets, Vector128<int> lastStart)
    {
        // Clamp the four-byte read, then shift to the requested byte. Even the final byte is in bounds.
        var starts = Sse41.Min(offsets, lastStart);
        var shifts = Sse2.ShiftLeftLogical(Sse2.Subtract(offsets, starts).AsUInt32(), 3);
        var packed = Avx2.GatherVector128((uint*)source, starts, 1);
        var gray = Sse2.And(Avx2.ShiftRightLogicalVariable(packed, shifts), Vector128.Create(255u));
        return Avx.ConvertToVector256Double(gray.AsInt32());
    }
}
