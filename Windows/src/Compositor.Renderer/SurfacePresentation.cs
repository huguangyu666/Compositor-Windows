using Compositor.Core;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Compositor.Renderer;

/// <summary>
/// Bridges compositor surfaces to the bitmaps WinUI displays. The canvas control needs BGRA8
/// premultiplied; the transparency checkerboard is NOT baked here — the draw pass lays it
/// under the bitmap on the GPU, so the same premultiplied pixels present over whatever
/// backdrop the canvas shows, and the fill is a straight swizzle.
/// </summary>
public static class SurfacePresentation
{
    /// <summary>
    /// Fills a tightly packed premultiplied BGRA staging buffer with the surface's region —
    /// row <c>y</c> of the output holds the region's pixels only, at <paramref name="packedWidth"/>
    /// bytes×4 per row, exactly what the texture's region upload expects. Alpha passes
    /// through: the GPU composites the frame over the checkerboard it draws beneath. The
    /// buffer is reused across frames, so a brush stroke or drag costs its rectangle, not
    /// the whole canvas.
    /// </summary>
    public static void FillBgra(Surface surface, byte[] bgra, int packedWidth, int x0, int y0, int x1, int y1)
    {
        if (x1 <= x0 || y1 <= y0) return;
        bool batch = Vector.IsHardwareAccelerated && BitConverter.IsLittleEndian
            && !ReferenceEquals(surface.Pixels, bgra) && x1 - x0 >= Vector<uint>.Count;
        for (int row = y0; row < y1; row++)
        {
            int source = row * surface.Stride + x0 * 4;
            int target = (row - y0) * packedWidth * 4;
            if (batch)
            {
                int bytes = (x1 - x0) * 4;
                SwapChannels(MemoryMarshal.Cast<byte, uint>(surface.Pixels.AsSpan(source, bytes)),
                    MemoryMarshal.Cast<byte, uint>(bgra.AsSpan(target, bytes)));
                continue;
            }
            for (int column = x0; column < x1; column++)
            {
                bgra[target] = surface.Pixels[source + 2];
                bgra[target + 1] = surface.Pixels[source + 1];
                bgra[target + 2] = surface.Pixels[source];
                bgra[target + 3] = surface.Pixels[source + 3];
                source += 4;
                target += 4;
            }
        }
    }

    private static void SwapChannels(ReadOnlySpan<uint> input, Span<uint> output)
    {
        var keep = new Vector<uint>(0xff00ff00u);
        var red = new Vector<uint>(0x000000ffu); var blue = new Vector<uint>(0x00ff0000u);
        ref uint source = ref MemoryMarshal.GetReference(input); ref uint target = ref MemoryMarshal.GetReference(output);
        int x = 0;
        // Both row spans have the same validated length; every vector stays inside them.
        for (; x <= input.Length - Vector<uint>.Count; x += Vector<uint>.Count)
        {
            var value = Vector.LoadUnsafe(ref source, (nuint)x);
            ((value & keep) | ((value & red) << 16) | ((value & blue) >> 16)).StoreUnsafe(ref target, (nuint)x);
        }
        for (; x < input.Length; x++)
        {
            uint value = input[x];
            output[x] = (value & 0xff00ff00u) | ((value & 0xffu) << 16) | ((value & 0xff0000u) >> 16);
        }
    }
}
