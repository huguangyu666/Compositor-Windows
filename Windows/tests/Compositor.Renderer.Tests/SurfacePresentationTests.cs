using Compositor.Core;
using Compositor.Renderer;
using Xunit;

namespace Compositor.Renderer.Tests;

/// <summary>
/// The canvas presentation fill: a premultiplied RGBA surface region swizzles into the
/// tightly packed BGRA staging buffer the texture's region upload takes, alpha and all —
/// the checkerboard is drawn by the GPU beneath the frame, never baked here.
/// </summary>
public class SurfacePresentationTests
{
    private static Surface Solid(int width, int height, byte r, byte g, byte b, byte a)
    {
        var surface = new Surface(width, height);
        for (int row = 0; row < height; row++)
        {
            for (int column = 0; column < width; column++)
            {
                int index = row * surface.Stride + column * 4;
                surface.Pixels[index] = r;
                surface.Pixels[index + 1] = g;
                surface.Pixels[index + 2] = b;
                surface.Pixels[index + 3] = a;
            }
        }
        return surface;
    }

    [Fact]
    public void OpaquePixelsSwapToBgra()
    {
        var surface = Solid(2, 2, 200, 100, 50, 255);
        var bgra = new byte[2 * 2 * 4];

        SurfacePresentation.FillBgra(surface, bgra, 2, 0, 0, 2, 2);

        Assert.Equal(50, bgra[0]); // blue first
        Assert.Equal(100, bgra[1]);
        Assert.Equal(200, bgra[2]); // red last
        Assert.Equal(255, bgra[3]);
    }

    [Fact]
    public void AlphaPassesThroughUnbaked()
    {
        // Half-covered blue: the premultiplied color and its real alpha both pass through —
        // what the GPU composites over the checker is up to the draw pass.
        var surface = Solid(1, 1, 0, 0, 100, 128);
        var bgra = new byte[4];

        SurfacePresentation.FillBgra(surface, bgra, 1, 0, 0, 1, 1);

        Assert.Equal(100, bgra[0]);
        Assert.Equal(0, bgra[1]);
        Assert.Equal(0, bgra[2]);
        Assert.Equal(128, bgra[3]);
    }

    [Fact]
    public void BandFillPacksRowsAndLeavesTheRestAlone()
    {
        // A 2×2 band out of a 6×4 surface lands packed at the staging buffer's origin;
        // bytes beyond the band's 2×2 stay untouched.
        var surface = Solid(6, 4, 10, 20, 30, 255);
        var bgra = new byte[2 * 2 * 4 + 8];
        for (int i = 0; i < bgra.Length; i++) bgra[i] = 0xAA;

        SurfacePresentation.FillBgra(surface, bgra, 2, 2, 1, 4, 3);

        Assert.Equal(30, bgra[0]);
        Assert.Equal(255, bgra[3]);
        Assert.Equal(30, bgra[2 * 4]); // second row
        for (int i = 2 * 2 * 4; i < bgra.Length; i++)
        {
            Assert.Equal(0xAA, bgra[i]); // past the band: untouched
        }
    }
}
