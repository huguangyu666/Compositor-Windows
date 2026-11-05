using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Compositor.Core;

public static partial class MaskDisplay
{
    private readonly record struct CubicBatch(Vector128<int> Offsets, Vector256<double> W0, Vector256<double> W1,
        Vector256<double> W2, Vector256<double> W3, bool Interior)
    {
        public static CubicBatch Create(ReadOnlySpan<CubicColumn> columns)
        {
            bool interior = true;
            foreach (ref readonly var column in columns) interior &= column.I0 >= 0 && column.I1 == column.I0 + 1
                && column.I2 == column.I0 + 2 && column.I3 == column.I0 + 3;
            return new(Vector128.Create(columns[0].I0, columns[1].I0, columns[2].I0, columns[3].I0),
                Vector256.Create(columns[0].W0, columns[1].W0, columns[2].W0, columns[3].W0),
                Vector256.Create(columns[0].W1, columns[1].W1, columns[2].W1, columns[3].W1),
                Vector256.Create(columns[0].W2, columns[1].W2, columns[2].W2, columns[3].W2),
                Vector256.Create(columns[0].W3, columns[1].W3, columns[2].W3, columns[3].W3), interior);
        }
    }

    private static int DrawCubicBatches(byte[] pixels, CubicColumn[] columns, CubicBatch[] batches, Vector256<double>[] terms,
        Span<int> cachedRows, int sourceWidth, int count, Span<uint> output,
        int r0, int r1, int r2, int r3, double w0, double w1, double w2, double w3, bool exactY, uint backdrop)
    {
        var y0 = Vector256.Create(w0); var y1 = Vector256.Create(w1); var y2 = Vector256.Create(w2); var y3 = Vector256.Create(w3);
        int t0 = PrepareCubicTerms(pixels, batches, terms, cachedRows, sourceWidth, count, r0);
        int t1 = PrepareCubicTerms(pixels, batches, terms, cachedRows, sourceWidth, count, r1);
        int t2 = PrepareCubicTerms(pixels, batches, terms, cachedRows, sourceWidth, count, r2);
        int t3 = PrepareCubicTerms(pixels, batches, terms, cachedRows, sourceWidth, count, r3);
        for (int i = 0; i < count; i++)
        {
            ref readonly var batch = ref batches[i]; int x = i * 4;
            if (!batch.Interior)
            {
                for (int lane = 0; lane < 4; lane++)
                    output[x + lane] = CubicPixel(pixels, columns[x + lane], r0, r1, r2, r3, w0, w1, w2, w3, exactY, backdrop);
                continue;
            }
            var value = Vector256<double>.Zero;
            AddPreparedCubicRow(ref value, terms, t0 + i * 4, y0); AddPreparedCubicRow(ref value, terms, t1 + i * 4, y1);
            AddPreparedCubicRow(ref value, terms, t2 + i * 4, y2); AddPreparedCubicRow(ref value, terms, t3 + i * 4, y3);
            var rounded = Avx.Max(Vector256<double>.Zero, Avx.Min(Avx.RoundToNearestInteger(value), Vector256.Create(255d)));
            var gray = Avx.ConvertToVector128Int32WithTruncation(rounded).AsUInt32();
            Sse2.Or(Sse2.Or(gray, Sse2.ShiftLeftLogical(gray, 8)),
                Sse2.Or(Sse2.ShiftLeftLogical(gray, 16), Vector128.Create(0xff000000u))).CopyTo(output.Slice(x, 4));
            if (exactY)
                for (int lane = 0; lane < 4; lane++)
                    if (columns[x + lane].Exact) output[x + lane] = 0xff000000u | (uint)pixels[r1 + columns[x + lane].I1] * 0x010101u;
        }
        return count * 4;
    }

    private static unsafe int PrepareCubicTerms(byte[] pixels, CubicBatch[] batches, Vector256<double>[] terms,
        Span<int> cachedRows, int width, int count, int row)
    {
        // Distinct rows in the four clamped, consecutive taps have distinct modulo-four slots.
        int slot = (row / width) & 3, start = slot * count * 4;
        if (cachedRows[slot] == row) return start;
        fixed (byte* source = pixels)
        for (int i = 0; i < count; i++)
        {
            ref readonly var batch = ref batches[i]; if (!batch.Interior) continue;
            // Interior proves all four bytes lie within this clamped source row.
            // Cache pixel * horizontal separately; vertical multiplication and addition keep their order.
            var packed = Avx2.GatherVector128((uint*)(source + row), batch.Offsets, 1);
            var mask = Vector128.Create(255u);
            int index = start + i * 4;
            terms[index] = Avx.Multiply(Avx.ConvertToVector256Double(Sse2.And(packed, mask).AsInt32()), batch.W0);
            terms[index + 1] = Avx.Multiply(Avx.ConvertToVector256Double(Sse2.And(Sse2.ShiftRightLogical(packed, 8), mask).AsInt32()), batch.W1);
            terms[index + 2] = Avx.Multiply(Avx.ConvertToVector256Double(Sse2.And(Sse2.ShiftRightLogical(packed, 16), mask).AsInt32()), batch.W2);
            terms[index + 3] = Avx.Multiply(Avx.ConvertToVector256Double(Sse2.ShiftRightLogical(packed, 24).AsInt32()), batch.W3);
        }
        cachedRows[slot] = row;
        return start;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddPreparedCubicRow(ref Vector256<double> value, Vector256<double>[] terms, int start, Vector256<double> weight)
    {
        value = Avx.Add(value, Avx.Multiply(terms[start], weight));
        value = Avx.Add(value, Avx.Multiply(terms[start + 1], weight));
        value = Avx.Add(value, Avx.Multiply(terms[start + 2], weight));
        value = Avx.Add(value, Avx.Multiply(terms[start + 3], weight));
    }
}
