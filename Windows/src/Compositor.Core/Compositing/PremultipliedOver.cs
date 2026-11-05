using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Compositor.Core;

/// <summary>Normal blending at full opacity. Uniform alpha blocks avoid per-pixel math;
/// mixed blocks retain the compositor's established output bytes.</summary>
internal static class PremultipliedOver
{
#if DEBUG
    internal static bool DebugMixedVectors = true;
#endif
    private static readonly Vector<byte> AlphaMask = CreateAlphaMask();
    private static readonly Vector256<byte> AlphaShuffle = Vector256.Create(
        (byte)3, 3, 3, 3, 7, 7, 7, 7, 11, 11, 11, 11, 15, 15, 15, 15,
        3, 3, 3, 3, 7, 7, 7, 7, 11, 11, 11, 11, 15, 15, 15, 15);

    internal static void Apply(Surface source, Surface destination, RectI region, bool useVector = true)
    {
        int bytes = region.Width * 4;
        int blockBytes = useVector && Vector.IsHardwareAccelerated ? Vector<byte>.Count : 0;
        bool mixedVectors = blockBytes == 32 && Avx2.IsSupported
            && (!ReferenceEquals(source.Pixels, destination.Pixels) || source.Stride == destination.Stride);
#if DEBUG
        mixedVectors &= Volatile.Read(ref DebugMixedVectors);
#endif
        for (int y = region.Y; y < region.Y + region.Height; y++)
        {
            var input = source.Pixels.AsSpan(y * source.Stride + region.X * 4, bytes);
            var output = destination.Pixels.AsSpan(y * destination.Stride + region.X * 4, bytes);
            int offset = 0;
            if (blockBytes > 0)
            {
                for (; offset <= bytes - blockBytes; offset += blockBytes)
                {
                    var pixels = new Vector<byte>(input.Slice(offset, blockBytes));
                    var alpha = pixels & AlphaMask;
                    if (Vector.EqualsAll(alpha, AlphaMask))
                        pixels.CopyTo(output.Slice(offset, blockBytes));
                    else if (!Vector.EqualsAll(alpha, Vector<byte>.Zero))
                    {
                        if (mixedVectors) BlendMixedBlock(input.Slice(offset, blockBytes), output.Slice(offset, blockBytes));
                        else BlendPixels(input.Slice(offset, blockBytes), output.Slice(offset, blockBytes));
                    }
                }
            }
            BlendPixels(input[offset..], output[offset..]);
        }
    }

    private static void BlendMixedBlock(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        var input = Unsafe.ReadUnaligned<Vector256<byte>>(ref MemoryMarshal.GetReference(source));
        var backdrop = Unsafe.ReadUnaligned<Vector256<byte>>(ref MemoryMarshal.GetReference(destination));
        var alpha = Avx2.Shuffle(input, AlphaShuffle);
        var keep = Avx2.Subtract(Vector256.Create((byte)255), alpha);
        var lower = BlendChannels(input.GetLower(), backdrop.GetLower(), keep.GetLower());
        var upper = BlendChannels(input.GetUpper(), backdrop.GetUpper(), keep.GetUpper());
        var packed = Avx2.Permute4x64(Avx2.PackUnsignedSaturate(lower, upper).AsUInt64(), 0xd8).AsByte();
        // Transparent source pixels skip even dirty RGB, matching the scalar contract.
        packed = Avx2.BlendVariable(packed, backdrop, Avx2.CompareEqual(alpha, Vector256<byte>.Zero));
        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(destination), packed);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> BlendChannels(Vector128<byte> source, Vector128<byte> backdrop, Vector128<byte> keep)
    {
        var product = Avx2.MultiplyLow(Avx2.ConvertToVector256Int16(backdrop).AsUInt16(), Avx2.ConvertToVector256Int16(keep).AsUInt16());
        // Exact round(product/255), product <=65025. An odd denominator has no half ties;
        // the scalar float error cannot reach the nearest half boundary (1/510 away).
        var biased = Avx2.Add(product, Vector256.Create((ushort)128));
        var divided = Avx2.ShiftRightLogical(Avx2.Add(biased, Avx2.ShiftRightLogical(biased, 8)), 8);
        return Avx2.Add(Avx2.ConvertToVector256Int16(source).AsUInt16(), divided).AsInt16();
    }

    private static void BlendPixels(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        for (int i = 0; i < source.Length; i += 4)
        {
            byte alpha = source[i + 3];
            if (alpha == 0) continue;
            if (alpha == 255)
            {
                source.Slice(i, 4).CopyTo(destination.Slice(i, 4));
                continue;
            }
            float keep = 1f - alpha / 255f;
            float backdropAlpha = destination[i + 3] / 255f;
            for (int channel = 0; channel < 3; channel++)
                destination[i + channel] = (byte)Math.Clamp(MathF.Round(source[i + channel] + destination[i + channel] * keep), 0, 255);
            destination[i + 3] = (byte)Math.Clamp(MathF.Round(alpha + backdropAlpha * 255f * keep), 0, 255);
        }
    }

    private static Vector<byte> CreateAlphaMask()
    {
        Span<byte> mask = stackalloc byte[Vector<byte>.Count];
        mask.Clear();
        for (int i = 3; i < mask.Length; i += 4) mask[i] = 255;
        return new Vector<byte>(mask);
    }
}
