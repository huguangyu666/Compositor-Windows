using Compositor.Core;
using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// The Camera Raw panel's scope: three Levels rows plus a 64×64 hue/saturation grid. The laws are the macOS
/// <c>CameraRawScope.make</c> (CameraRaw.swift:350-391) read one line at a time — the channel rows come from
/// the Levels histogram (LevelsPixels.c:17-28, already covered by LevelsHistogramTests) and the grid is the
/// Swift loop. Cell coordinates here are worked out by hand from that loop: <c>0.5 ± 0.48</c> of the side,
/// truncated toward zero, so pure red lands at (62, 32) and pure green at (16, 58).
/// </summary>
public class CameraRawScopeTests
{
    private static Surface Solid(byte r, byte g, byte b, byte a = 255, int width = 1, int height = 1)
    {
        var surface = new Surface(width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int index = y * surface.Stride + x * 4;
                surface.Pixels[index] = r;
                surface.Pixels[index + 1] = g;
                surface.Pixels[index + 2] = b;
                surface.Pixels[index + 3] = a;
            }
        return surface;
    }

    private static double Cell(CameraRawScope scope, int column, int row) => scope.Vectorscope[row * 64 + column];

    [Fact]
    public void APureRedPixelPlotsDueEastFromTheCentre()
    {
        var scope = CameraRawScope.Make(Solid(255, 0, 0))!;
        Assert.Equal(1.0, Cell(scope, 62, 32), 6);          // (0.5 + 0.48) × 64, truncated
        Assert.Equal(4096, scope.Vectorscope.Length);
        Assert.Equal(1, scope.Vectorscope.Count(value => value > 0));
        Assert.Equal(1.0, scope.VectorPeak, 6);
    }

    [Fact]
    public void GreenAndBluePlotWhereTheMacsHueBranchesPutThem()
    {
        // Green's hue is 2/6 and blue's 4/6, so sin() sends green to the row below centre… which the panel
        // then draws above it, because the display maps row 0 to the bottom.
        Assert.Equal(1.0, Cell(CameraRawScope.Make(Solid(0, 255, 0))!, 16, 58), 6);
        Assert.Equal(1.0, Cell(CameraRawScope.Make(Solid(0, 0, 255))!, 16, 5), 6);
    }

    [Fact]
    public void AGreyPixelHasNoHueSoTheGridStaysEmptyButTheHistogramCountsIt()
    {
        var scope = CameraRawScope.Make(Solid(128, 128, 128))!;
        Assert.All(scope.Vectorscope, value => Assert.Equal(0.0, value));
        Assert.Equal(1.0, scope.Red[128], 6);
        Assert.Equal(1.0, scope.Green[128], 6);
        Assert.Equal(1.0, scope.Blue[128], 6);
    }

    [Fact]
    public void AHalfTransparentPixelWeighsItsAlpha()
    {
        // Premultiplied: 64 of 128 is a half-intensity red, so un-premultiplied it is still pure red.
        var scope = CameraRawScope.Make(Solid(64, 0, 0, 128))!;
        Assert.Equal(128.0 / 255.0, Cell(scope, 62, 32), 6);
        Assert.Equal(128.0 / 255.0, scope.Red[128], 6);   // round(64 × 255 / 128) = round(127.5)
        Assert.Equal(0.0, scope.Red[255], 6);
    }

    [Fact]
    public void FullyTransparentPixelsAreInNeitherCount()
    {
        var scope = CameraRawScope.Make(Solid(255, 0, 0, 0))!;
        Assert.Equal(0.0, scope.VectorPeak);
        Assert.Equal(0.0, scope.Red.Sum(), 6);
        // The scope still exists: make only refuses a picture with no pixels, and the panel keeps whatever
        // it last had because a nil answer is not stored (CameraRaw.swift:351, Filters.swift:564).
        Assert.NotNull(scope);
    }

    [Fact]
    public void TheThreeRibbonsShareOneVerticalScale()
    {
        // A flat pure-red picture fills one bin per row, so every row's own scale is the pixel count and the
        // shared peak is that same number — the ribbons cannot each pick their own height.
        var scope = CameraRawScope.Make(Solid(255, 0, 0, 255, 8, 4))!;
        Assert.Equal(32.0, scope.Peak, 6);
        Assert.Equal(32.0, scope.Red.Sum(), 6);
        Assert.Equal(32.0, scope.Blue.Sum(), 6);
    }

    [Fact]
    public void TheEndpointBinsDoNotSetTheSharedScale()
    {
        // DisplayScale throws away the first and last bin when it looks for the interior's 95th percentile,
        // so a frame that is all pure red (one bin at 255) falls back to the raw peak.
        var scope = CameraRawScope.Make(Solid(255, 0, 0, 255, 4, 4))!;
        Assert.Equal(16.0, scope.Peak, 6);
        Assert.Equal(16.0, LevelsHistogram.DisplayScale(scope.Red), 6);
        Assert.Equal(16.0, LevelsHistogram.DisplayScale(scope.Green), 6);
    }

    [Fact]
    public void TheChannelRowsAreTheLevelsPlanesNotTheMeanOne()
    {
        var surface = Solid(255, 128, 0);
        var scope = CameraRawScope.Make(surface)!;
        var bins = LevelsHistogram.Sum(surface, null);
        Assert.Equal(bins[1], scope.Red);
        Assert.Equal(bins[2], scope.Green);
        Assert.Equal(bins[3], scope.Blue);
        // The fourth Levels row is the mean of the three (each channel adds weight/3), and Camera Raw hands
        // that one to nobody.
        Assert.Equal(1.0 / 3, bins[0][255], 6);
        Assert.Equal(1.0 / 3, bins[0][128], 6);
        Assert.Equal(1.0 / 3, bins[0][0], 6);
    }

    [Fact]
    public void TwoIdenticalPixelsStackInTheSameCell()
    {
        var scope = CameraRawScope.Make(Solid(255, 0, 0, 255, 2, 1))!;
        Assert.Equal(2.0, Cell(scope, 62, 32), 6);
        Assert.Equal(2.0, scope.VectorPeak, 6);
    }
}
