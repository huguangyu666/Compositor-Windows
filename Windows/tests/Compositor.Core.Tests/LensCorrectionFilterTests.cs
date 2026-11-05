using Compositor.Core;
using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// Numbers for the Lens Correction filter's one slider, ported from Filters.swift:257-266 where the Mac hands
/// `lens_distort` `settings.distortion / 100 * lensStrength` with `lensStrength` = 0.35 (Filters.swift:178).
/// The expected centroids come from a second, independent mirror of the kernel written in Python
/// (Windows/tools/lens-correction-golden.py) — two implementations agreeing on a number, not a run of the Mac,
/// which this machine cannot execute.
/// </summary>
public class LensCorrectionFilterTests
{
    private const int Size = 41;
    private const int Line = 10;

    private static Surface LineFixture()
    {
        var surface = new Surface(Size, Size);
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                int at = y * surface.Stride + x * 4;
                surface.Pixels[at] = x == Line ? (byte)255 : (byte)0;
                surface.Pixels[at + 1] = 0;
                surface.Pixels[at + 2] = 0;
                surface.Pixels[at + 3] = 255;
            }
        return surface;
    }

    private static Surface Warp(Surface source, double k)
    {
        var into = new Surface(source.Width, source.Height);
        CameraRawOptics.Distort(source.Clone(), into, k);
        return into;
    }

    /// <summary>Where the line landed in one row: the red-weighted mean column, or −1 when the row holds nothing.
    /// The locks read a tenth of a pixel: the kernel rounds to bytes and the mirror in lens-correction-golden.py
    /// does not, and the two already disagree in the fourth decimal (6.8850 against 6.8855), which a two-decimal
    /// comparison would turn into a failure with nothing behind it.</summary>
    private static double Centroid(Surface surface, int row)
    {
        double sum = 0, weighted = 0;
        for (int x = 0; x < surface.Width; x++)
        {
            double value = surface.Pixels[row * surface.Stride + x * 4];
            sum += value;
            weighted += x * value;
        }
        return sum == 0 ? -1 : weighted / sum;
    }

    [Fact]
    public void AFullSliderReachesTheStrengthALayerGets()
    {
        Assert.Equal(0.35, CameraRawOptics.SliderK(100), precision: 10);
        Assert.Equal(-0.35, CameraRawOptics.SliderK(-100), precision: 10);
        Assert.Equal(0.175, CameraRawOptics.SliderK(50), precision: 10);
        Assert.Equal(0, CameraRawOptics.SliderK(0), precision: 10);
    }

    [Fact]
    public void TheFilterAndTheOpticsManualSliderAreTheSameNumber()
    {
        // Both are the same slider measured against the same constant (Filters.swift:264 and
        // CameraRawDetailOptics.swift:91-95), so one of them drifting is a bug in both.
        var settings = new CameraRawSettings { OpticsDistortion = 100 };
        Assert.Equal(CameraRawOptics.SliderK(100), CameraRawOptics.DistortionK(settings), precision: 10);
        Assert.Equal(0.35, CameraRawOptics.DistortionK(settings), precision: 10);
    }

    [Fact]
    public void AZeroWarpLeavesEveryByteWhereItWas()
    {
        var source = LineFixture();
        var warped = Warp(source, CameraRawOptics.SliderK(0));
        Assert.Equal(source.Pixels, warped.Pixels);
    }

    [Fact]
    public void APositiveAmountPushesTheLineOutwardAndANegativeOnePullsItBack()
    {
        var source = LineFixture();
        // The middle row is the clean one to read: the line is left of centre there, so "outward" is a smaller
        // column, and nothing has been pulled off the canvas yet.
        Assert.Equal(Line, Centroid(source, Size / 2), precision: 3);
        Assert.Equal(9.513, Centroid(Warp(source, CameraRawOptics.SliderK(100)), Size / 2), precision: 1);
        Assert.Equal(10.342, Centroid(Warp(source, CameraRawOptics.SliderK(-100)), Size / 2), precision: 1);
        Assert.True(Centroid(Warp(source, CameraRawOptics.SliderK(100)), Size / 2) < Line,
            "a positive amount moved the line inward; the Mac's positive straightens barrel bowing.");
        Assert.True(Centroid(Warp(source, CameraRawOptics.SliderK(-100)), Size / 2) > Line,
            "a negative amount moved the line outward; the two directions are the whole point of the slider.");
    }

    [Fact]
    public void TheBendGrowsWithDistanceFromTheCentre()
    {
        var source = LineFixture();
        var warped = Warp(source, CameraRawOptics.SliderK(100));
        double edge = Math.Abs(Centroid(warped, 0) - Line), middle = Math.Abs(Centroid(warped, Size / 2) - Line);
        // The kernel bends by the square of the normalized distance, and the top row sits far from the centre in
        // both axes, so it moves several times as far as the middle row of the same column.
        Assert.Equal(6.885, Centroid(warped, 0), precision: 1);
        Assert.True(edge > middle * 3, $"the edge row moved {edge:0.###} px and the centre row {middle:0.###} px — that is not radial.");
    }

    [Fact]
    public void PullingTheCornersInEjectsThemRatherThanRenormalizing()
    {
        var source = LineFixture();
        var warped = Warp(source, CameraRawOptics.SliderK(-100));
        // Neighbours that fall outside contribute nothing and the weights are not renormalized
        // (CameraRawOptics.Distort's own comment, LensPixels.c:4-37), so at the top row a strong negative amount
        // samples the whole line off the image and leaves black behind.
        Assert.Equal(-1, Centroid(warped, 0), precision: 3);
        Assert.True(Centroid(warped, Size / 2) > 0, "the middle row lost its line too, which is not what the kernel does.");
    }
}
