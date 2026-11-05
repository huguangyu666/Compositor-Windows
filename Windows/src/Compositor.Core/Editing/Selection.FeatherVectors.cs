using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Compositor.Core;

public sealed partial class Selection
{
    private readonly struct FeatherVectorDivider(ulong reciprocal)
    {
        private readonly Vector256<uint> low = Vector256.Create((uint)reciprocal);
        private readonly Vector256<uint> high = Vector256.Create((uint)(reciprocal >> 32));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector256<uint> Divide(Vector256<uint> numerator)
        {
            // Reconstruct the same 64-bit product as scalar DivideCoverage, separately
            // for even and odd uint lanes. The supported numerator keeps it below 2^56.
            var even = Avx2.Add(Avx2.Multiply(numerator, low), Avx2.ShiftLeftLogical(Avx2.Multiply(numerator, high), 32));
            var oddNumerator = Avx2.ShiftRightLogical(numerator.AsUInt64(), 32).AsUInt32();
            var odd = Avx2.Add(Avx2.Multiply(oddNumerator, low), Avx2.ShiftLeftLogical(Avx2.Multiply(oddNumerator, high), 32));
            return Avx2.Or(Avx2.ShiftRightLogical(even, 48), Avx2.ShiftLeftLogical(Avx2.ShiftRightLogical(odd, 48), 32)).AsUInt32();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> ReadFeatherBytes(ReadOnlySpan<byte> source, int x)
        => Avx2.ConvertToVector256Int32(Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref MemoryMarshal.GetReference(source), x))).AsByte());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreFeatherBytes(Span<byte> destination, int x, Vector256<uint> values)
    {
        var words = Sse2.PackSignedSaturate(values.GetLower().AsInt32(), values.GetUpper().AsInt32());
        var bytes = Sse2.PackUnsignedSaturate(words, Vector128<short>.Zero).AsUInt64().ToScalar();
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetReference(destination), x), bytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void UpdateFeatherSums(Span<int> sums, ReadOnlySpan<byte> row, bool subtract)
    {
        int x = 0;
        if (Avx2.IsSupported)
        {
            ref byte first = ref Unsafe.As<int, byte>(ref MemoryMarshal.GetReference(sums));
            for (; x + 8 <= sums.Length; x += 8)
            {
                var current = Unsafe.ReadUnaligned<Vector256<int>>(ref Unsafe.Add(ref first, x * sizeof(int)));
                var value = ReadFeatherBytes(row, x);
                var updated = subtract ? Avx2.Subtract(current, value) : Avx2.Add(current, value);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref first, x * sizeof(int)), updated);
            }
        }
        if (subtract)
            for (; x < sums.Length; x++) sums[x] -= row[x];
        else
            for (; x < sums.Length; x++) sums[x] += row[x];
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void WriteFeatherRow(ReadOnlySpan<int> sums, Span<byte> output, int count, ulong reciprocal)
    {
        int x = 0;
        if (Avx2.IsSupported)
        {
            var divider = new FeatherVectorDivider(reciprocal); var half = Vector256.Create(count / 2);
            ref byte first = ref Unsafe.As<int, byte>(ref MemoryMarshal.GetReference(sums));
            for (; x + 8 <= sums.Length; x += 8)
            {
                var current = Unsafe.ReadUnaligned<Vector256<int>>(ref Unsafe.Add(ref first, x * sizeof(int)));
                StoreFeatherBytes(output, x, divider.Divide(Avx2.Add(current, half).AsUInt32()));
            }
        }
        for (; x < sums.Length; x++) output[x] = DivideCoverage((uint)(sums[x] + count / 2), reciprocal);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int BlurFeatherInterior(ReadOnlySpan<byte> source, Span<byte> destination, int amount,
        int x, int last, uint count, ulong reciprocal, ref int sum)
    {
        if (!Avx2.IsSupported) return x;
        var divider = new FeatherVectorDivider(reciprocal);
        var previous = Vector256.Create(0, 0, 1, 2, 3, 4, 5, 6);
        // Exclude the final full-window pixel, whose update has no incoming source byte.
        for (; x + 8 < last; x += 8)
        {
            var delta = Avx2.Subtract(ReadFeatherBytes(source, x + amount + 1), ReadFeatherBytes(source, x - amount));
            var prefix = Avx2.Add(delta, Avx2.ShiftLeftLogical128BitLane(delta.AsByte(), 4).AsInt32());
            prefix = Avx2.Add(prefix, Avx2.ShiftLeftLogical128BitLane(prefix.AsByte(), 8).AsInt32());
            prefix = Avx2.Add(prefix, Vector256.Create(Vector128<int>.Zero, Sse2.Shuffle(prefix.GetLower(), 0xff)));
            var exclusive = Avx2.Blend(Avx2.PermuteVar8x32(prefix, previous), Vector256<int>.Zero, 1);
            var numerator = Avx2.Add(exclusive, Vector256.Create(sum + (int)(count / 2)));
            StoreFeatherBytes(destination, x, divider.Divide(numerator.AsUInt32()));
            sum += prefix.GetElement(7);
        }
        return x;
    }
}
