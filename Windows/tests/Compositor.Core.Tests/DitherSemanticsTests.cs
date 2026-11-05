using Xunit;
using System.Linq;

namespace Compositor.Core.Tests;

/// <summary>
/// C08a: the dither styles' size semantics and the selection clip. Both come straight from the macOS
/// rules (Document/Dither.swift:34 for which styles shrink, :170 for the scanline spacing), and the
/// expected bytes below are written out by hand rather than read back from the engine.
/// </summary>
public class DitherSemanticsTests
{
    private static Surface Gray(int size, byte value = 200)
    {
        var surface = new Surface(size, size);
        for (int i = 0; i < surface.Pixels.Length; i += 4)
        {
            surface.Pixels[i] = value; surface.Pixels[i + 1] = value; surface.Pixels[i + 2] = value; surface.Pixels[i + 3] = 255;
        }
        return surface;
    }

    [Fact]
    public void AsciiAndScanlinesAreTheTwoStylesThatKeepFullResolution()
    {
        Assert.False(DitherStyle.Ascii.UsesPixelSize());
        Assert.False(DitherStyle.Scanlines.UsesPixelSize());
        foreach (var style in Enum.GetValues<DitherStyle>().Where(style => style is not (DitherStyle.Ascii or DitherStyle.Scanlines)))
        {
            Assert.True(style.UsesPixelSize(), $"{style} lost the chunky-pixel block it is drawn with.");
        }
    }

    [Fact]
    public void ScanlinesHonorLineSpacingRatherThanTheHalftoneCell()
    {
        // Bands of four: rows 0-3 white, 4-7 black, 8-11 white, 12-15 black.
        var source = Gray(16, 0);
        for (int y = 0; y < 16; y++)
        {
            byte value = y % 8 < 4 ? (byte)255 : (byte)0;
            for (int x = 0; x < 16; x++)
            {
                int at = y * source.Stride + x * 4;
                source.Pixels[at] = value; source.Pixels[at + 1] = value; source.Pixels[at + 2] = value;
            }
        }

        // Rows 2 and 5 are the same distance from the middle of an 8-row band (1.5 either way), so
        // with the halftone cell they must read identically; with a 4-row spacing they fall in
        // different bands — the all-white one and the all-black one — and cannot. The beam darkens
        // a row by how far it sits from its band's middle, which is why the comparison has to be
        // between rows at equal offset rather than row 0 and row 4.
        var spaced = source.Clone();
        DitherEngine.Apply(spaced, new DitherParams { Style = DitherStyle.Scanlines, Cell = 8, LineSpacing = 4 });
        int row2 = spaced.Pixels[2 * spaced.Stride + 4];
        int row6 = spaced.Pixels[6 * spaced.Stride + 4];
        Assert.True(row2 > 100, $"the white band's row should stay bright, got {row2}.");
        Assert.True(row6 < 20, $"the black band's row should stay dark, got {row6}.");

        var cellOnly = source.Clone();
        DitherEngine.Apply(cellOnly, new DitherParams { Style = DitherStyle.Scanlines, Cell = 8 });
        Assert.Equal(cellOnly.Pixels[2 * cellOnly.Stride + 4], cellOnly.Pixels[5 * cellOnly.Stride + 4]);
        Assert.True(cellOnly.Pixels[2 * cellOnly.Stride + 4] is > 60 and < 200,
            $"an 8-row band averaging half white should land mid-tone, got {cellOnly.Pixels[2 * cellOnly.Stride + 4]}.");
    }

    [Fact]
    public void ClipToCoverageKeepsTheSourceOutsideTheSelection()
    {
        var source = Gray(4, 100);
        var processed = new Surface(4, 4);
        for (int i = 0; i < processed.Pixels.Length; i += 4)
        {
            processed.Pixels[i] = 255; processed.Pixels[i + 3] = 255; // opaque red
        }
        var coverage = new MaskSurface(4, 4);
        for (int y = 0; y < 4; y++) coverage.Pixels[y * 4 + 1] = 255; // column 1 selected

        var result = DitherEngine.ClipToCoverage(source, processed, coverage);
        Assert.Equal(255, result.Pixels[4 + 0]); // inside: the processed pixel
        Assert.Equal(0, result.Pixels[4 + 1]);
        Assert.Equal(255, result.Pixels[4 + 3]);
        Assert.Equal(100, result.Pixels[0]); // outside: the source, byte for byte
        Assert.Equal(100, result.Pixels[3 * 4 * 4 + 3 * 4 + 2]);
        Assert.False(ReferenceEquals(result, source));
        Assert.Equal(100, source.Pixels[0]); // the source was not written through
    }

    [Fact]
    public void ClipToCoverageBlendsHalfCoverageAsSourceOver()
    {
        var source = Gray(2, 100);
        var processed = new Surface(2, 2);
        for (int i = 0; i < processed.Pixels.Length; i += 4) { processed.Pixels[i] = 255; processed.Pixels[i + 3] = 255; }
        var coverage = new MaskSurface(2, 2, [51, 51, 51, 51]);

        var result = DitherEngine.ClipToCoverage(source, processed, coverage);
        // 255*51/255 + 100*204/255 = 51 + 80 = 131, exactly; alpha 255*0.2 + 255*0.8 = 255.
        Assert.Equal(131, result.Pixels[0]);
        Assert.Equal(255, result.Pixels[3]);
    }

    [Fact]
    public void ClipToCoverageRejectsADifferentlySizedProcessedCopy()
    {
        var source = Gray(4);
        var processed = Gray(2);
        Assert.Throws<ArgumentException>(() => DitherEngine.ClipToCoverage(source, processed, new MaskSurface(4, 4)));
    }

    /// <summary>
    /// The four predicates the panel uses to decide which rows exist (Dither.swift:27-32, read against its
    /// groups at :19-24). Which styles fall in which group is written out here from the macOS source, not
    /// inferred from the C# names: diffusion is two styles, ordered adds the three Bayers, halftone is
    /// exactly the three screens, and marks are everything that is neither a tone style nor a CRT.
    /// </summary>
    [Fact]
    public void TheStyleGroupsDecideWhichControlsExist()
    {
        var diffusing = new[] { DitherStyle.Atkinson, DitherStyle.FloydSteinberg };
        var ordered = new[] { DitherStyle.Bayer2, DitherStyle.Bayer4, DitherStyle.Bayer8 };
        var halftone = new[] { DitherStyle.HalftoneDots, DitherStyle.HalftoneLines, DitherStyle.HalftoneDiamonds };

        foreach (var style in Enum.GetValues<DitherStyle>())
        {
            Assert.Equal(diffusing.Contains(style), style.Diffuses());
            Assert.Equal(diffusing.Contains(style) || ordered.Contains(style), style.HasTones());
            Assert.Equal(halftone.Contains(style), style.IsHalftone());
            Assert.Equal(style is DitherStyle.MacPatterns or DitherStyle.Ascii || halftone.Contains(style), style.DrawsMarks());
        }
        // The CRT is the one style that draws no marks yet has no tones either, so it must not offer
        // "Light on Dark": there is nothing on top of anything for it to flip.
        Assert.False(DitherStyle.Scanlines.DrawsMarks());
        Assert.False(DitherStyle.Scanlines.HasTones());
    }

    [Fact]
    public void DotShapedPixelsCarveTheScreenOutOfEachBlock()
    {
        // A 4×4 block with a 1.68 radius: the pixel at the block's corner sits sqrt(1.5²+1.5²)=2.12
        // from the middle, past the radius, so the gap shows through; the one at (1,1) is 0.71 away,
        // inside it, so the dot is whole there.
        var surface = Gray(4);
        DitherEngine.Dots(surface, 4, (0, 0, 0));
        Assert.True(surface.Pixels[0] < 40, $"the corner of a dot-matrix pixel should be the screen, was {surface.Pixels[0]}.");
        Assert.Equal(200, surface.Pixels[4 * 4 + 1 * 4]);   // (1,1) of the same block, whole
        Assert.Equal(200, surface.Pixels[4 * 4 + 2 * 4]);  // (2,1)
    }

    [Fact]
    public void ABlockTooSmallToRoundIsLeftAlone()
    {
        var surface = Gray(4);
        DitherEngine.Dots(surface, 1, (0, 0, 0));
        Assert.All(Enumerable.Range(0, 16).SelectMany(pixel => new[] { surface.Pixels[pixel * 4] }), value => Assert.Equal(200, value));
    }
}
