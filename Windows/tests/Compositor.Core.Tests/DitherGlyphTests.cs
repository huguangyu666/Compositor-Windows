using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// C08b: the ASCII style's marks come out of the glyph cells the caller supplies, not out of a tone
/// threshold (DitherPixels.c:280-312). The maps here are invented rather than drawn from a font, so the
/// expected bytes are hand-computed from the C: the cell's mean tone picks the glyph whose coverage sits
/// nearest to it, and then the glyph's own bytes say how much of the mark color each pixel gets.
/// </summary>
public class DitherGlyphTests
{
    private static Surface Bands(params byte[] values)
    {
        var surface = new Surface(values.Length, 1);
        for (int x = 0; x < values.Length; x++)
        {
            int at = x * 4;
            surface.Pixels[at] = surface.Pixels[at + 1] = surface.Pixels[at + 2] = values[x];
            surface.Pixels[at + 3] = 255;
        }
        return surface;
    }

    private static byte At(Surface surface, int x) => surface.Pixels[x * 4];

    /// <summary>Three cells of one pixel each: a blank glyph, a half-inked one, and a full one. The
    /// coverages are the ramp's three ends, least ink first, and each glyph's byte is its own ink level,
    /// so the result says which cell got which glyph.</summary>
    private static DitherParams Glyphs(bool lightOnDark) => new()
    {
        Style = DitherStyle.Ascii,
        LightOnDark = lightOnDark,
        GlyphWidth = 1,
        GlyphHeight = 1,
        GlyphCoverage = [0f, .5f, 1f],
        GlyphMaps = [0, 128, 255],
    };

    [Fact]
    public void TheCellTonePicksTheGlyphNearestItsOwnCoverage()
    {
        // Black, mid gray and white across three cells: the means are 0, 128/255 and 1, and 128/255 is
        // closer to 0.5 than to either end, so each cell gets the glyph it was aimed at.
        var surface = Bands(0, 128, 255);
        DitherEngine.Apply(surface, Glyphs(lightOnDark: true));
        Assert.Equal((byte)0, At(surface, 0));        // blank glyph → nothing but the dark screen
        Assert.Equal((byte)128, At(surface, 1));      // the half-inked glyph's own level, not a threshold
        Assert.Equal((byte)255, At(surface, 2));      // full glyph
    }

    [Fact]
    public void TheGlyphsOwnBytesLayTheMarkRatherThanTheTone()
    {
        // One glyph, two pixels wide and one tall, inked on the left only. Every cell is the same white,
        // so the only thing that can split the result down the middle is the map's own shape.
        var surface = Bands(255, 255);
        DitherEngine.Apply(surface, Glyphs(lightOnDark: true) with
        {
            GlyphWidth = 2, GlyphHeight = 1, GlyphCoverage = [1f], GlyphMaps = [255, 0],
        });
        Assert.Equal((byte)255, At(surface, 0));
        Assert.Equal((byte)0, At(surface, 1));
    }

    [Fact]
    public void LightOnDarkInvertsWhichEndOfTheRampTheToneMeans()
    {
        // On dark the mark stands for the light tones; on light it stands for the darkness, so the same
        // black and white swap which glyph they are aimed at (DitherPixels.c:292).
        var surface = Bands(0, 255);
        DitherEngine.Apply(surface, Glyphs(lightOnDark: false) with
        {
            GlyphCoverage = [0f, 1f], GlyphMaps = [0, 255],
        });
        Assert.Equal((byte)0, At(surface, 0));        // black wants the fullest mark, drawn in the dark ink
        Assert.Equal((byte)255, At(surface, 1));      // white wants the blank one, which is the light paper
    }

    [Fact]
    public void AGlyphSetThatDoesNotMatchItsCoverageFallsBackToTheScreenShapes()
    {
        // Two coverages but only one cell of map: indexing the second glyph would read past the buffer,
        // so the caller's broken set has to cost the marks and not the process.
        var surface = Bands(0, 128, 255);
        DitherEngine.Apply(surface, Glyphs(lightOnDark: true) with { GlyphMaps = [0] });
        Assert.Equal(255u, (uint)surface.Pixels[3]);
        Assert.True(surface.Pixels.All(pixel => pixel is 0 or 255),
            "a short glyph set leaked a partial mark instead of falling back to the screen shapes");
    }

    [Fact]
    public void TransparentPixelsStayOutOfTheCellsAndOutOfTheResult()
    {
        // The first pixel is fully transparent, so its cell holds no tone at all; the engine leaves the
        // pixel alone rather than marking it with whatever an empty mean would pick.
        var surface = new Surface(2, 1);
        surface.Pixels[4] = surface.Pixels[5] = surface.Pixels[6] = 255;
        surface.Pixels[7] = 255;
        DitherEngine.Apply(surface, Glyphs(lightOnDark: true) with { GlyphWidth = 1 });
        Assert.Equal(0u, surface.Pixels[3]);
        Assert.Equal((byte)255, At(surface, 1));
    }
}
