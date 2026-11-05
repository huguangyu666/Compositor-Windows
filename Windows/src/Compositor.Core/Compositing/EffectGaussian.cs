using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Compositor.Core;

/// <summary>Effect coverage blur. The Mac CPU renderer uses sigma = blur/2 on a
/// clamped-to-extent plane. Small kernels remain exact; three sliding boxes approximate
/// larger kernels with matched variance and bounded work per pixel.</summary>
internal static class EffectGaussian
{
    internal static void Apply(byte[] coverage, int width, int height, double blur)
        => ApplyWindow(coverage, width, height, blur, width, height);

    private static bool UsesExact(double sigma, int width, int height) => sigma <= 2
        || Math.Max(width, height) <= 128 && sigma * 3 > Math.Min(width, height);

    internal static bool UsesFloatWorkspace(double blur, int width, int height) =>
        blur > 0 && !UsesExact(Math.Max(.1, blur / 2), width, height);

    internal static int Reach(double blur, int width, int height)
    {
        if (blur <= 0) return 0;
        double sigma = Math.Max(.1, blur / 2);
        if (UsesExact(sigma, width, height)) return Math.Max(1, (int)Math.Ceiling(sigma * 3));
        var (lowerWidth, lowerCount) = Boxes(sigma);
        return 3 * ((lowerWidth - 1) / 2) + 3 - lowerCount;
    }

    private static (int Width, int Count) Boxes(double sigma)
    {
        int lowerWidth = (int)Math.Floor(Math.Sqrt(4 * sigma * sigma + 1));
        if (lowerWidth % 2 == 0) lowerWidth--;
        int lowerCount = Math.Clamp((int)Math.Round((12 * sigma * sigma - 3 * lowerWidth * lowerWidth
            - 12 * lowerWidth - 9) / (-4.0 * lowerWidth - 4), MidpointRounding.AwayFromZero), 0, 3);
        return (lowerWidth, lowerCount);
    }

    internal static void ApplyWindow(byte[] coverage, int width, int height, double blur, int referenceWidth, int referenceHeight,
        EffectScratch? scratch = null, byte[]? outputWorkspace = null)
    {
        if (blur <= 0) return;
        double sigma = Math.Max(.1, blur / 2);
        // Re-clamping between box passes changes a fade across a very small extent.
        // An exact finite-plane convolution is cheap here, even for a wide kernel.
        if (UsesExact(sigma, referenceWidth, referenceHeight))
        { Exact(coverage, width, height, sigma, scratch); return; }

        var (lowerWidth, lowerCount) = Boxes(sigma);
        // A caller may lend its still-blank RGBA output until this first outside
        // blur finishes. It owns restoration before any color fill or publication.
        Span<float> current = outputWorkspace is null ? scratch?.CurrentFloat ?? new float[coverage.Length]
            : MemoryMarshal.Cast<byte, float>(outputWorkspace.AsSpan(0, checked(coverage.Length * sizeof(float))));
        // Vertical output needs only the horizontal rows in its box. Produce those before
        // overwriting the corresponding input rows. One extra slot keeps add/remove distinct;
        // four-row batches reserve three more slots for their bounded lookahead.
        bool batchRows = Avx.IsSupported;
        int rowCount = Math.Min(height, lowerWidth + (batchRows ? 6 : 3));
        var horizontal = scratch?.HorizontalFloatRows(rowCount) ?? new float[width * rowCount];
        var columns = scratch?.Columns ?? new double[width];
        for (int i = 0; i < coverage.Length; i++) current[i] = coverage[i];
        for (int pass = 0; pass < 3; pass++)
        {
            int radius = (lowerWidth - 1) / 2 + (pass < lowerCount ? 0 : 1);
            double factor = 1.0 / (radius * 2 + 1);
            int bottom = Math.Min(radius, height - 1);
            int generated = -1;
            generated = GenerateThrough(current, horizontal, width, height, rowCount, generated, bottom, radius, factor, batchRows);
            for (int x = 0; x < width; x++)
            {
                double sum = horizontal[x] * (radius + 1.0);
                for (int y = 1; y <= bottom; y++) sum += horizontal[y % rowCount * width + x];
                if (radius > bottom) sum += (radius - bottom) * horizontal[(height - 1) % rowCount * width + x];
                columns[x] = sum;
            }
            // Keep the original double sum and float conversion order, including clamped
            // rows at both edges. Full-plane and halo-window renders must agree byte for byte.
            for (int y = 0; y < height; y++)
            {
                int next = Math.Min(height - 1, y + radius + 1);
                if (next > generated)
                    generated = GenerateThrough(current, horizontal, width, height, rowCount, generated, next, radius, factor, batchRows);
                int row = y * width, add = next % rowCount * width, remove = Math.Max(0, y - radius) % rowCount * width;
                int x = 0;
                if (Vector.IsHardwareAccelerated)
                {
                    int lanes = Vector<double>.Count;
                    var scale = new Vector<double>(factor);
                    for (; x <= width - Vector<float>.Count; x += Vector<float>.Count)
                    {
                        var lower = new Vector<double>(columns, x); var upper = new Vector<double>(columns, x + lanes);
                        Vector.Narrow(lower * scale, upper * scale).CopyTo(current.Slice(row + x));
                        Vector.Widen(new Vector<float>(horizontal, add + x), out var addLower, out var addUpper);
                        Vector.Widen(new Vector<float>(horizontal, remove + x), out var removeLower, out var removeUpper);
                        // Each lane keeps the original double difference and accumulation order.
                        (lower + (addLower - removeLower)).CopyTo(columns, x);
                        (upper + (addUpper - removeUpper)).CopyTo(columns, x + lanes);
                    }
                }
                for (; x < width; x++)
                {
                    current[row + x] = (float)(columns[x] * factor);
                    columns[x] += (double)horizontal[add + x] - horizontal[remove + x];
                }
            }
        }
        int index = 0;
        if (Avx.IsSupported)
        {
            var half = Vector256.Create(.5); var maximum = Vector256.Create(255d);
            for (; index <= coverage.Length - 4; index += 4)
            {
                var values = Avx.ConvertToVector256Double(Vector128.Create(current[index], current[index + 1], current[index + 2], current[index + 3]));
                // Float sums are promoted before adding .5, so values immediately below
                // a half cannot round upward during a float addition. Clamp commutes with
                // away-from-zero byte rounding on these finite coverage values.
                var rounded = Avx.RoundToZero(Avx.Add(Avx.Max(Vector256<double>.Zero, Avx.Min(maximum, values)), half));
                var integers = Avx.ConvertToVector128Int32WithTruncation(rounded);
                var shorts = Sse2.PackSignedSaturate(integers, Vector128<int>.Zero);
                uint packed = Sse2.PackUnsignedSaturate(shorts, Vector128<short>.Zero).AsUInt32().ToScalar();
                BinaryPrimitives.WriteUInt32LittleEndian(coverage.AsSpan(index, 4), packed);
            }
        }
        for (int i = index; i < coverage.Length; i++)
            coverage[i] = (byte)Math.Clamp(Math.Round(current[i], MidpointRounding.AwayFromZero), 0, 255);
    }

    private static int GenerateThrough(ReadOnlySpan<float> current, float[] horizontal, int width, int height, int rowCount,
        int generated, int last, int radius, double factor, bool batchRows)
    {
        while (generated < last)
        {
            int first = generated + 1;
            if (batchRows && first + 3 < height)
            {
                HorizontalFourRows(current, horizontal, width, rowCount, first, radius, factor);
                generated += 4;
            }
            else { HorizontalRow(current, horizontal, width, rowCount, first, radius, factor); generated++; }
        }
        return generated;
    }

    private static Vector128<float> ReadFourRows(ReadOnlySpan<float> current, int row, int width, int x) =>
        Vector128.Create(current[row + x], current[row + width + x], current[row + width * 2 + x], current[row + width * 3 + x]);

    private static void HorizontalFourRows(ReadOnlySpan<float> current, float[] horizontal, int width, int rowCount,
        int first, int radius, double factor)
    {
        int row = first * width;
        int slot0 = first % rowCount * width, slot1 = (first + 1) % rowCount * width;
        int slot2 = (first + 2) % rowCount * width, slot3 = (first + 3) % rowCount * width;
        var sum = Avx.Multiply(Avx.ConvertToVector256Double(ReadFourRows(current, row, width, 0)), Vector256.Create(radius + 1.0));
        int end = Math.Min(radius, width - 1);
        for (int x = 1; x <= end; x++) sum = Avx.Add(sum, Avx.ConvertToVector256Double(ReadFourRows(current, row, width, x)));
        if (radius > end)
            sum = Avx.Add(sum, Avx.ConvertToVector256Double(Sse.Multiply(Vector128.Create((float)(radius - end)), ReadFourRows(current, row, width, width - 1))));
        var scale = Vector256.Create(factor);
        for (int x = 0; x < width; x++)
        {
            var output = Avx.ConvertToVector128Single(Avx.Multiply(sum, scale));
            horizontal[slot0 + x] = output.GetElement(0); horizontal[slot1 + x] = output.GetElement(1);
            horizontal[slot2 + x] = output.GetElement(2); horizontal[slot3 + x] = output.GetElement(3);
            var add = Avx.ConvertToVector256Double(ReadFourRows(current, row, width, Math.Min(width - 1, x + radius + 1)));
            var remove = Avx.ConvertToVector256Double(ReadFourRows(current, row, width, Math.Max(0, x - radius)));
            sum = Avx.Add(sum, Avx.Subtract(add, remove));
        }
    }

    private static void HorizontalRow(ReadOnlySpan<float> current, float[] horizontal, int width, int rowCount, int y, int radius, double factor)
    {
        var source = current.Slice(y * width, width);
        var output = horizontal.AsSpan(y % rowCount * width, width);
        double sum = source[0] * (radius + 1.0);
        int end = Math.Min(radius, width - 1);
        for (int i = 1; i <= end; i++) sum += source[i];
        if (radius > end) sum += (radius - end) * source[^1];
        // Split at the two clamp boundaries; the same samples enter and leave the
        // double sum in the same order, including when a wide box spans both edges.
        int leftEnd = Math.Min(radius, width), addEnd = Math.Clamp(width - radius - 1, 0, width);
        int firstEnd = Math.Min(leftEnd, addEnd), x = 0;
        for (; x < firstEnd; x++)
        {
            output[x] = (float)(sum * factor);
            sum += (double)source[x + radius + 1] - source[0];
        }
        if (leftEnd < addEnd)
        {
            for (; x < addEnd; x++)
            {
                output[x] = (float)(sum * factor);
                sum += (double)source[x + radius + 1] - source[x - radius];
            }
        }
        else
        {
            for (; x < leftEnd; x++)
            {
                output[x] = (float)(sum * factor);
                sum += (double)source[^1] - source[0];
            }
        }
        for (; x < width; x++)
        {
            output[x] = (float)(sum * factor);
            sum += (double)source[^1] - source[x - radius];
        }
    }

    private static void Exact(byte[] coverage, int width, int height, double sigma, EffectScratch? scratch)
    {
        int radius = Math.Max(1, (int)Math.Ceiling(sigma * 3));
        var kernel = new double[radius * 2 + 1]; double sum = 0;
        for (int k = -radius; k <= radius; k++)
        { double value = Math.Exp(-k * (double)k / (2 * sigma * sigma)); kernel[k + radius] = value; sum += value; }
        for (int k = 0; k < kernel.Length; k++) kernel[k] /= sum;
        var prefix = new double[kernel.Length + 1];
        for (int k = 0; k < kernel.Length; k++) prefix[k + 1] = prefix[k] + kernel[k];
        var horizontal = scratch?.Horizontal ?? new byte[coverage.Length];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            double value = 0;
            if (sigma <= 2)
                for (int k = -radius; k <= radius; k++) value += coverage[y * width + Math.Clamp(x + k, 0, width - 1)] * kernel[k + radius];
            else
            {
                for (int sx = Math.Max(0, x - radius); sx <= Math.Min(width - 1, x + radius); sx++)
                    value += coverage[y * width + sx] * kernel[sx - x + radius];
                if (x < radius) value += coverage[y * width] * prefix[radius - x];
                if (x + radius >= width) value += coverage[y * width + width - 1] * (prefix[^1] - prefix[radius + width - x]);
            }
            horizontal[y * width + x] = (byte)Math.Round(value, MidpointRounding.AwayFromZero);
        }
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            double value = 0;
            if (sigma <= 2)
                for (int k = -radius; k <= radius; k++) value += horizontal[Math.Clamp(y + k, 0, height - 1) * width + x] * kernel[k + radius];
            else
            {
                for (int sy = Math.Max(0, y - radius); sy <= Math.Min(height - 1, y + radius); sy++)
                    value += horizontal[sy * width + x] * kernel[sy - y + radius];
                if (y < radius) value += horizontal[x] * prefix[radius - y];
                if (y + radius >= height) value += horizontal[(height - 1) * width + x] * (prefix[^1] - prefix[radius + height - y]);
            }
            coverage[y * width + x] = (byte)Math.Round(value, MidpointRounding.AwayFromZero);
        }
    }
}
