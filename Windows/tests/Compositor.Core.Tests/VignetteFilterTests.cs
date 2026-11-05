using Compositor.Core;
using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// Numbers for the standalone Vignette filter, ported from <c>adjust_colored_vignette</c>
/// (AdjustPixels.c:461-500) and <c>vignette_mask_at</c> (:412-426). Every expected byte comes from
/// Windows/tools/vignette-golden.py, a separate mirror of the same C — two derivations agreeing, not a run of
/// the Mac, which this machine cannot execute.
/// </summary>
public class VignetteFilterTests
{
    private const int Size = 21;

    private static Surface Field(int size, byte r, byte g, byte b, byte a)
    {
        var surface = new Surface(size, size);
        for (int i = 0; i < surface.Pixels.Length; i += 4)
        {
            surface.Pixels[i] = r;
            surface.Pixels[i + 1] = g;
            surface.Pixels[i + 2] = b;
            surface.Pixels[i + 3] = a;
        }
        return surface;
    }

    private static (byte R, byte G, byte B, byte A) Pixel(Surface surface, int x, int y)
    {
        int at = y * surface.Stride + x * 4;
        return (surface.Pixels[at], surface.Pixels[at + 1], surface.Pixels[at + 2], surface.Pixels[at + 3]);
    }

    private static Surface Run(Surface surface, bool fillsClear, double amount, double midpoint = 0,
        double roundness = 100, double feather = 100, double highlights = 0,
        double red = 0, double green = 0, double blue = 1,
        double frameX = 0, double frameY = 0, double frameWidth = Size, double frameHeight = Size)
    {
        VignetteFilter.Apply(surface, frameX, frameY, frameWidth, frameHeight, fillsClear,
            amount, midpoint, roundness, feather, highlights, red, green, blue);
        return surface;
    }

    [Fact]
    public void TheDefaultsAreTheSheetsOwn()
    {
        // Filters.swift:54-60. Amount starts at 35, so a panel opened and confirmed untouched does apply a vignette.
        Assert.Equal(35, VignetteFilter.DefaultAmount);
        Assert.Equal(50, VignetteFilter.DefaultMidpoint);
        Assert.Equal(100, VignetteFilter.DefaultRoundness);
        Assert.Equal(60, VignetteFilter.DefaultFeather);
        Assert.Equal(25, VignetteFilter.DefaultHighlights);
    }

    [Fact]
    public void AZeroMidpointDrivesTheCornerAllTheWayToTheChosenColour()
    {
        var surface = Run(Field(Size, 128, 128, 128, 255), fillsClear: false, amount: 100);
        // The middle of a 0-midpoint vignette is mask 0, and a pixel the mask skips is not rewritten at all —
        // so the grey field's centre is still exactly the grey it started as.
        Assert.Equal((128, 128, 128, 255), Pixel(surface, 10, 10));
        Assert.Equal((1, 1, 254, 255), Pixel(surface, 0, 0));
        Assert.Equal((32, 32, 223, 255), Pixel(surface, 0, 10));
        Assert.Equal((32, 32, 223, 255), Pixel(surface, 10, 0));
    }

    [Fact]
    public void HighlightsProtectTheBrightEdgesAndNothingElse()
    {
        var protectedBright = Run(Field(Size, 255, 255, 255, 255), fillsClear: false, amount: 100, highlights: 100);
        Assert.Equal((255, 255, 255, 255), Pixel(protectedBright, 0, 0));
        var unprotected = Run(Field(Size, 255, 255, 255, 255), fillsClear: false, amount: 100, highlights: 0);
        Assert.Equal((2, 2, 255, 255), Pixel(unprotected, 0, 0));
        // The protection reads the pixel's own brightness, so a grey edge still takes the colour.
        var grey = Run(Field(Size, 128, 128, 128, 255), fillsClear: false, amount: 100, highlights: 100);
        Assert.Equal((13, 13, 242, 255), Pixel(grey, 0, 0));
    }

    [Fact]
    public void FillsClearPaintsIntoTransparentPixelsAndLetsAlphaGrow()
    {
        var surface = Run(Field(Size, 0, 0, 0, 0), fillsClear: true, amount: 100);
        Assert.Equal((0, 0, 0, 0), Pixel(surface, 10, 10));
        Assert.Equal((0, 0, 253, 253), Pixel(surface, 0, 0));
        Assert.Equal((0, 0, 191, 191), Pixel(surface, 0, 10));
        var half = Run(Field(Size, 0, 0, 0, 0), fillsClear: true, amount: 50);
        Assert.Equal((0, 0, 127, 127), Pixel(half, 0, 0));
    }

    [Fact]
    public void WithoutFillsClearAClearPixelKeepsStayingClear()
    {
        // The other branch of the Mac's kernel: coverage never changes, so a transparent corner stays
        // transparent however strong the vignette is (AdjustPixels.c:472, :485-489).
        var surface = Run(Field(Size, 0, 0, 0, 0), fillsClear: false, amount: 100);
        Assert.Equal((0, 0, 0, 0), Pixel(surface, 0, 0));
    }

    [Fact]
    public void TheFalloffFollowsTheCanvasRatherThanTheLayer()
    {
        // A 41 px layer holding a 21 px canvas at (10,10): the canvas's own middle is untouched, its corner is
        // driven to the colour, and pixels outside it are past the ramp's end — full colour, not a falloff.
        var surface = Run(Field(41, 200, 180, 160, 255), fillsClear: false, amount: 100,
            frameX: 10, frameY: 10, frameWidth: 21, frameHeight: 21);
        Assert.Equal((200, 180, 160, 255), Pixel(surface, 20, 20));
        Assert.Equal((1, 1, 254, 255), Pixel(surface, 10, 10));
        Assert.Equal((0, 0, 255, 255), Pixel(surface, 0, 0));
    }

    [Fact]
    public void TheFrameIsTheCanvasMappedIntoTheLayersOwnPixels()
    {
        var identity = new LayerTransform { Origin = new(0, 0), Size = new(Size, Size) };
        Assert.Equal((0, 0, (double)Size, Size), VignetteFilter.FrameOf(identity, Size, Size, Size, Size));
        // A layer twice the canvas, centred on it: the canvas covers the middle 20 px of the 40 px layer.
        var grown = new LayerTransform { Origin = new(-10, -10), Size = new(40, 40) };
        var frame = VignetteFilter.FrameOf(grown, 20, 20, 40, 40);
        Assert.Equal(10, frame.X, precision: 9);
        Assert.Equal(10, frame.Y, precision: 9);
        Assert.Equal(20, frame.Width, precision: 9);
        Assert.Equal(20, frame.Height, precision: 9);
    }

    [Fact]
    public void RoundnessTradesTheCornerInForTheEdgeMidpoint()
    {
        // At a corner the circle and the square agree, so roundness cannot move it; at an edge midpoint the
        // square reaches further out than the circle, so -100 darkens it as much as the corner while 100 stays light.
        var circle = Run(Field(Size, 128, 128, 128, 255), fillsClear: false, amount: 100,
            midpoint: 50, roundness: 100);
        var square = Run(Field(Size, 128, 128, 128, 255), fillsClear: false, amount: 100,
            midpoint: 50, roundness: -100);
        Assert.Equal((108, 108, 148, 255), Pixel(circle, 0, 10));
        Assert.Equal((59, 59, 197, 255), Pixel(circle, 0, 0));
        Assert.Equal((59, 59, 197, 255), Pixel(square, 0, 10));
        Assert.Equal((59, 59, 197, 255), Pixel(square, 0, 0));
    }

    [Fact]
    public void AFeatherOfZeroStillFadesOverFivePercentOfTheFrame()
    {
        // The kernel's `if (soft < 0.05) soft = 0.05;` (AdjustPixels.c:422) is the difference between a ramp and
        // a cliff: one pixel inside the five percent, the feather-floor version blends to 89 while a version
        // without the floor divides by zero, clamps the ratio to 1 and lands on pure black.
        var wide = 201;
        var floor = Run(Field(wide, 128, 128, 128, 255), fillsClear: false, amount: 100, midpoint: 50,
            feather: 0, frameWidth: wide, frameHeight: wide);
        Assert.Equal((byte)89, Pixel(floor, 37, 100).R);
        var next = Pixel(floor, 38, 100).R;
        Assert.True(next > 89 && next < 128, $"the ramp should keep climbing past 89, it read {next}");
    }

    [Fact]
    public void AZeroAmountOrAnEmptyFrameLeavesEveryByteAlone()
    {
        var untouched = Run(Field(Size, 128, 128, 128, 255), fillsClear: false, amount: 0);
        Assert.Equal((128, 128, 128, 255), Pixel(untouched, 0, 0));
        // The Mac's guard reads `amount <= 0`, so a negative amount is not an inverted vignette — it is nothing.
        var negative = Run(Field(Size, 128, 128, 128, 255), fillsClear: false, amount: -50);
        Assert.Equal((128, 128, 128, 255), Pixel(negative, 0, 0));
        var noFrame = Run(Field(Size, 128, 128, 128, 255), fillsClear: true, amount: 100,
            frameWidth: 0, frameHeight: 0);
        Assert.Equal((128, 128, 128, 255), Pixel(noFrame, 0, 0));
    }
}
