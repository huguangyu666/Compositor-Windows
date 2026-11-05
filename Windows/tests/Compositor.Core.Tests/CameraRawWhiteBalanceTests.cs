using Compositor.Core;
using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// The white-balance solve, ported from the macOS CameraRaw statics (Document/CameraRaw.swift:265-323).
/// Two kinds of lock: the semantic one — the numbers it returns must bring the three channels to the same
/// level under the very gains the light pass multiplies — and the exact bytes the Mac's own arithmetic gives,
/// from Windows/tools/white-balance-golden.py.
/// </summary>
public class CameraRawWhiteBalanceTests
{
    private static Surface Pixels(params (byte R, byte G, byte B, byte A)[] rows)
    {
        var width = Math.Max(1, rows.Length);
        var surface = new Surface(width, 1);
        for (int column = 0; column < rows.Length; column++)
        {
            int index = column * 4;
            surface.Pixels[index] = rows[column].R;
            surface.Pixels[index + 1] = rows[column].G;
            surface.Pixels[index + 2] = rows[column].B;
            surface.Pixels[index + 3] = rows[column].A;
        }
        return surface;
    }

    /// <summary>The Mac's own neutral test: solve, then multiply the linear channels by the resulting gains.
    /// Whatever the solve returns, these three have to agree — the eyedropper inverts the kernel's arithmetic
    /// and nothing else.</summary>
    [Theory]
    [InlineData(0.8, 0.6, 0.5)]
    [InlineData(0.4, 0.5, 0.8)]
    [InlineData(0.5, 0.8, 0.5)]
    [InlineData(0.8, 0.5, 0.8)]
    [InlineData(1.0, 0.25, 0.1)]
    public void SolvedGainsBringEveryChannelToTheSameLevel(double red, double green, double blue)
    {
        var solved = CameraRawWhiteBalance.NeutralizeStraight(red, green, blue);
        Assert.NotNull(solved);

        var settings = new CameraRawSettings
            { Temperature = solved!.Value.Temperature, Tint = solved.Value.Tint };
        var (redGain, greenGain, blueGain) = settings.Gains;
        double linearRed = Math.Pow((red + 0.055) / 1.055, 2.4);
        double linearGreen = Math.Pow((green + 0.055) / 1.055, 2.4);
        double linearBlue = Math.Pow((blue + 0.055) / 1.055, 2.4);

        Assert.Equal(linearGreen * greenGain, linearRed * redGain, 9);
        Assert.Equal(linearGreen * greenGain, linearBlue * blueGain, 9);
    }

    [Fact]
    public void SolvedValuesMatchTheMacsArithmetic()
    {
        // Windows/tools/white-balance-golden.py, mirroring CameraRaw.swift:265-299.
        Assert.Equal((-136.524579, 1.745959), Round(CameraRawWhiteBalance.NeutralizeStraight(0.8, 0.6, 0.5)));
        Assert.Equal((181.587502, -3.889201), Round(CameraRawWhiteBalance.NeutralizeStraight(0.4, 0.5, 0.8)));
        Assert.Equal((0.0, 182.779932), Round(CameraRawWhiteBalance.NeutralizeStraight(0.5, 0.8, 0.5)));
        Assert.Equal((0.0, -251.821924), Round(CameraRawWhiteBalance.NeutralizeStraight(0.8, 0.5, 0.8)));
        Assert.Equal((0.0, 0.0), Round(CameraRawWhiteBalance.NeutralizeStraight(0.6, 0.6, 0.6)));
    }

    [Fact]
    public void AChannelThatIsGoneHasNoSolution()
    {
        // A pure red has no green and no blue to pull up: the Mac answers nil and leaves the sliders alone
        // rather than inventing a gain that divides by nothing. The determinant guard behind it is a safety net
        // — with three positive channels the system is never singular, because every term in the determinant
        // carries the same sign.
        Assert.Null(CameraRawWhiteBalance.NeutralizeStraight(1.0, 0.0, 0.0));
        Assert.Null(CameraRawWhiteBalance.NeutralizeStraight(0.00005, 0.00005, 0.00005));
        Assert.Null(CameraRawWhiteBalance.Neutralize(0.5, 0.5, 1e-9));
    }

    [Fact]
    public void ANeutralPixelNeedsNothing()
    {
        var solved = CameraRawWhiteBalance.NeutralizeStraight(0.6, 0.6, 0.6);
        Assert.NotNull(solved);
        Assert.Equal(0, solved!.Value.Temperature, 9);
        Assert.Equal(0, solved.Value.Tint, 9);
    }

    [Fact]
    public void AutoBalanceAveragesTheOpaquePixelsInTheLinearDomain()
    {
        // Two warm pixels, one cool, one fully transparent: the transparent one must not drag the average,
        // and the average is taken after the sRGB decode, not on the bytes.
        var surface = Pixels((200, 120, 90, 255), (200, 120, 90, 255), (60, 90, 200, 255), (0, 0, 0, 0));

        var solved = CameraRawWhiteBalance.AutoBalance(surface);

        Assert.NotNull(solved);
        Assert.Equal((-45.420155, -164.383413), Round(solved));
    }

    [Fact]
    public void AutoBalanceOnFlatGrayNeedsNothingAndOnNothingIsNoAnswer()
    {
        Assert.Equal((0.0, 0.0), Round(CameraRawWhiteBalance.AutoBalance(Pixels((180, 180, 180, 255)))));
        Assert.Null(CameraRawWhiteBalance.AutoBalance(Pixels((10, 10, 10, 0))));
    }

    [Fact]
    public void AutoBalanceDividesPremultipliedBytesByTheirOwnAlpha()
    {
        var straight = Pixels((200, 120, 90, 255));
        // The same straight colour at half coverage: 100, 60, 45 over alpha 128 divides back to about the
        // 200, 120, 90 that the opaque row carries, so both must solve to the same answer.
        var premultiplied = Pixels((100, 60, 45, 128));

        var fromStraight = CameraRawWhiteBalance.AutoBalance(straight);
        var fromPremultiplied = CameraRawWhiteBalance.AutoBalance(premultiplied);

        Assert.NotNull(fromStraight);
        Assert.NotNull(fromPremultiplied);
        Assert.Equal(fromStraight!.Value.Temperature, fromPremultiplied!.Value.Temperature, 0);
        Assert.Equal(fromStraight.Value.Tint, fromPremultiplied.Value.Tint, 0);
    }

    [Fact]
    public void TheWhiteBalanceModeMovesNoPixelByItself()
    {
        // Mac's adjustsColor counts temperature and tint only: picking Auto in the picker before the scan
        // returns must not grade anything, and must survive a clone.
        var settings = new CameraRawSettings { WhiteBalance = CameraRawSettings.WhiteBalanceAuto };

        Assert.True(settings.IsIdentity);
        Assert.False(settings.AdjustsLightColor);
        Assert.Equal(CameraRawSettings.WhiteBalanceAuto, settings.Clone().WhiteBalance);
        Assert.Equal(["Custom", "Auto"], CameraRawSettings.WhiteBalanceNames);
    }

    [Fact]
    public void PixelOfWalksBackThroughTheRotationAndTheFlip()
    {
        var placement = new LayerTransform
        {
            Origin = new PointD(10, 20),
            Size = new SizeD(200, 100),
            Rotation = 90,
            FlipX = true,
            Sampling = LayerSampling.Smooth,
        };

        // The centre of the placement is its own middle, whatever the angle: 110, 70 document → 100, 50 pixels.
        Assert.Equal(100, placement.PixelOf(new PointD(110, 70), new SizeD(200, 100)).X, 6);
        Assert.Equal(50, placement.PixelOf(new PointD(110, 70), new SizeD(200, 100)).Y, 6);
        // Turned a quarter of the box's own height: at 90° a document-x offset arrives as the box's y axis, so
        // this lands on the pixel row, not the column — and the row is not mirrored, because FlipY is off.
        Assert.Equal(100, placement.PixelOf(new PointD(110 - 25, 70), new SizeD(200, 100)).X, 6);
        Assert.Equal(75, placement.PixelOf(new PointD(110 - 25, 70), new SizeD(200, 100)).Y, 6);
        // And the mapping is the inverse of where the compositor puts a pixel: unit point out, pixel back.
        var document = placement.Point(new PointD(0.25, 0.6));
        var back = placement.PixelOf(document, new SizeD(40, 20));
        Assert.Equal(30, back.X, 6);   // (1 - 0.25) * 40, because this placement is flipped
        Assert.Equal(12, back.Y, 6);
    }

    private static (double, double) Round((double Temperature, double Tint)? solved) =>
        (Math.Round(solved!.Value.Temperature, 6), Math.Round(solved.Value.Tint, 6));
}
