using Compositor.Core;
using Compositor.Renderer;
using Xunit;

namespace Compositor.Renderer.Tests;

public class SurfacePresentationBatchTests
{
    [Theory]
    [InlineData(1)] [InlineData(3)] [InlineData(4)] [InlineData(7)] [InlineData(8)]
    [InlineData(9)] [InlineData(15)] [InlineData(16)] [InlineData(17)] [InlineData(33)]
    public void BatchAndTailsMatchScalarWithUnalignedRowsAndPackedPadding(int bandWidth)
    {
        int width = bandWidth + 6, stride = width * 4 + 3;
        var pixels = new byte[stride * 7 + 7]; new Random(7301).NextBytes(pixels);
        var surface = new Surface(width, 7, pixels, stride); var source = pixels.ToArray();
        int packedWidth = bandWidth + 2;
        var expected = Enumerable.Repeat((byte)173, packedWidth * 4 * 3 + 19).ToArray(); var actual = expected.ToArray();
        Reference(surface, expected, packedWidth, 3, 2, 3 + bandWidth, 5);
        SurfacePresentation.FillBgra(surface, actual, packedWidth, 3, 2, 3 + bandWidth, 5);
        Assert.Equal(expected, actual); Assert.Equal(source, pixels);
    }

    [Fact]
    public void EveryAlphaAndColorBytePassesThroughWithoutBakingCheckerboard()
    {
        var surface = new Surface(257, 3);
        for (int i = 0; i < surface.PixelCount; i++)
        {
            surface.Pixels[i * 4] = (byte)i; surface.Pixels[i * 4 + 1] = (byte)(i * 7);
            surface.Pixels[i * 4 + 2] = (byte)(255 - i); surface.Pixels[i * 4 + 3] = (byte)(i * 13);
        }
        var expected = new byte[surface.Pixels.Length]; var actual = new byte[expected.Length]; var source = surface.Pixels.ToArray();
        Reference(surface, expected, 257, 0, 0, 257, 3); SurfacePresentation.FillBgra(surface, actual, 257, 0, 0, 257, 3);
        Assert.Equal(expected, actual); Assert.Equal(source, surface.Pixels);
    }

    [Fact]
    public void SharedBuffersKeepTheExistingScalarWriteOrder()
    {
        var pixels = new byte[17 * 4 * 5]; new Random(7301).NextBytes(pixels);
        var expected = new Surface(17, 5, pixels.ToArray(), 17 * 4); var actual = new Surface(17, 5, pixels.ToArray(), 17 * 4);
        Reference(expected, expected.Pixels, 13, 2, 1, 15, 4);
        SurfacePresentation.FillBgra(actual, actual.Pixels, 13, 2, 1, 15, 4);
        Assert.Equal(expected.Pixels, actual.Pixels);
    }

    [Fact]
    public void EmptyBandsDoNotReadOrWriteOutsideBuffers()
    {
        var surface = new Surface(1, 1);
        SurfacePresentation.FillBgra(surface, [], 0, 100, 100, 100, 103);
        SurfacePresentation.FillBgra(surface, [], 0, 100, 100, 103, 100);
        SurfacePresentation.FillBgra(surface, [], 0, 103, 100, 100, 103);
    }

    private static void Reference(Surface surface, byte[] bgra, int packedWidth, int x0, int y0, int x1, int y1)
    {
        for (int row = y0; row < y1; row++)
        {
            int source = row * surface.Stride + x0 * 4, target = (row - y0) * packedWidth * 4;
            for (int column = x0; column < x1; column++)
            {
                bgra[target] = surface.Pixels[source + 2]; bgra[target + 1] = surface.Pixels[source + 1];
                bgra[target + 2] = surface.Pixels[source]; bgra[target + 3] = surface.Pixels[source + 3];
                source += 4; target += 4;
            }
        }
    }
}
