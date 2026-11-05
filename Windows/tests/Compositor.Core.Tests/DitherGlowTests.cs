using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// C08d: a CRT's bloom. The Mac blurs the finished scanlines and adds that light back over them
/// (Document/Dither.swift:146-163, DitherPixels.c:360-374), and the two halves of that are the two
/// methods tested here — <c>Bloom</c>, which is a resample-plus-Gaussian over a clamped plane, and
/// <c>Glow</c>, which is the byte arithmetic on top. The expected numbers are hand-computed from that
/// arithmetic; the blur's are chosen where the picture is a wide plateau, because an 8-bit plane cannot
/// hold a point source smeared over fifteen pixels (it would round to nothing).
/// </summary>
public class DitherGlowTests
{
    /// <summary>64×64: the left half opaque white, the right half opaque black, split at x = 32.</summary>
    private static Surface HalfLight(int side = 64)
    {
        var surface = new Surface(side, side);
        for (int y = 0; y < side; y++)
        {
            for (int x = 0; x < side; x++)
            {
                int at = y * surface.Stride + x * 4;
                byte value = x < side / 2 ? (byte)255 : (byte)0;
                surface.Pixels[at] = surface.Pixels[at + 1] = surface.Pixels[at + 2] = value;
                surface.Pixels[at + 3] = 255;
            }
        }
        return surface;
    }

    private static byte Red(Surface surface, int x, int y) => surface.Pixels[y * surface.Stride + x * 4];

    [Fact]
    public void BloomLeavesThePictureItCameFromAlone()
    {
        var source = HalfLight();
        var before = (byte[])source.Pixels.Clone();
        var bloom = DitherEngine.Bloom(source, 4);
        Assert.True(source.Pixels.AsSpan().SequenceEqual(before), "the bloom wrote over the lines it was given.");
        Assert.False(ReferenceEquals(bloom, source), "the bloom came back as the same surface.");
        Assert.Equal(source.Width, bloom.Width);
        Assert.Equal(source.Height, bloom.Height);
    }

    [Fact]
    public void ABloomingPlaneIsClampedAtItsOwnEdgesRatherThanDarkenedByThem()
    {
        // Core Image clamps the extent before the blur, so the middle of a wide white field has nothing
        // darker to average in: a plane padded with black would read about 127 at the first column.
        var bloom = DitherEngine.Bloom(HalfLight(), 4);
        Assert.True(Red(bloom, 1, 32) >= 250, $"the light's own edge went dark: {Red(bloom, 1, 32)}.");
        Assert.True(Red(bloom, 1, 32) >= Red(bloom, 20, 32), "the plateau fell away from its edge inwards.");
    }

    [Fact]
    public void TheGlowSpillsPastTheLightAndThinsAsItGoes()
    {
        var bloom = DitherEngine.Bloom(HalfLight(), 4);
        int near = Red(bloom, 36, 32), far = Red(bloom, 52, 32);
        Assert.True(near > 0, "no light crossed the edge of the field at all.");
        Assert.True(near > far, $"the smear got denser with distance ({near} then {far}).");
        Assert.True(Red(bloom, 30, 32) >= near, "the smear was darkest right where the light ended.");
    }

    [Fact]
    public void TheLineSpacingIsWhatSetsHowFarTheBloomReaches()
    {
        // The sheet's spacing feeds the radius directly (Dither.swift:150), so the same column 16 pixels
        // past the light is faint at two-pixel spacing and bright at sixteen — and the two must not be
        // the same smear, which is what happens if the glow reads the halftone cell instead.
        int tight = Red(DitherEngine.Bloom(HalfLight(), 2), 48, 32);
        int loose = Red(DitherEngine.Bloom(HalfLight(), 16), 48, 32);
        Assert.True(loose > tight + 20, $"spacing 16 reached {loose} and spacing 2 reached {tight}.");
    }

    [Fact]
    public void AFlatFieldBloomsIntoItself()
    {
        // Uniform white has nothing to smear into: whatever the shrink and the kernel do, every sample
        // is 255, so the copy has to come back 255. This also walks the shrink == 1 branch, where the
        // plane is blurred at full resolution (spacing 1 → radius 6 → shrink 1).
        var white = HalfLight(33);
        for (int i = 0; i < white.Pixels.Length; i += 4) white.Pixels[i] = white.Pixels[i + 1] = white.Pixels[i + 2] = 255;
        var bloom = DitherEngine.Bloom(white, 1);
        Assert.True(Enumerable.Range(0, bloom.Pixels.Length / 4)
            .All(pixel => bloom.Pixels[pixel * 4] == 255 && bloom.Pixels[pixel * 4 + 3] == 255),
            "a flat field came back mottled.");
    }

    [Fact]
    public void GlowAddsTheLightItIsGivenAndNeverMoreThanThePixelCanCarry()
    {
        // (10,20,30) at alpha 200, over a white bloom: v = rgb + light · amount · alpha/255, capped at the
        // alpha, because these are premultiplied bytes. Half beam: 10 + 255·0.5·200/255 = 110.
        var lines = new Surface(2, 1);
        var light = new Surface(2, 1);
        for (int x = 0; x < 2; x++)
        {
            int at = x * 4;
            lines.Pixels[at] = 10; lines.Pixels[at + 1] = 20; lines.Pixels[at + 2] = 30; lines.Pixels[at + 3] = 200;
            light.Pixels[at] = light.Pixels[at + 1] = light.Pixels[at + 2] = 255; light.Pixels[at + 3] = 255;
        }
        lines.Pixels[4] = lines.Pixels[5] = lines.Pixels[6] = 40;      // the second pixel's own copy below
        lines.Pixels[4] = 0; lines.Pixels[5] = 0; lines.Pixels[6] = 0; lines.Pixels[7] = 0;
        DitherEngine.Glow(lines, light, .5f);
        Assert.Equal(110u, lines.Pixels[0]);
        Assert.Equal(120u, lines.Pixels[1]);
        Assert.Equal(130u, lines.Pixels[2]);
        Assert.Equal(200u, lines.Pixels[3]);
        Assert.Equal(0u, lines.Pixels[7]);                             // alpha is never touched
        Assert.Equal(0u, lines.Pixels[4]);                             // and nothing lights a transparent pixel
    }

    [Fact]
    public void AFullBeamSaturatesAtThePixelsOwnAlpha()
    {
        var lines = new Surface(1, 1);
        var light = new Surface(1, 1);
        lines.Pixels[0] = 10; lines.Pixels[1] = 20; lines.Pixels[2] = 30; lines.Pixels[3] = 200;
        light.Pixels[0] = light.Pixels[1] = light.Pixels[2] = 255; light.Pixels[3] = 255;
        DitherEngine.Glow(lines, light, 1f);
        // 10 + 255·1·200/255 = 265, and a premultiplied pixel cannot exceed its own alpha.
        Assert.Equal(200u, lines.Pixels[0]);
        Assert.Equal(200u, lines.Pixels[1]);
        Assert.Equal(200u, lines.Pixels[2]);
        Assert.Equal(200u, lines.Pixels[3]);
    }

    [Fact]
    public void AverageDownKeepsThePremultipliedMeanIncludingTransparency()
    {
        // A 2×2 block: two opaque white pixels and two fully transparent ones. The mean of the
        // premultiplied bytes is (255+255+0+0)/4 = 127 and of the alpha (255+255+0+0)/4 = 127, which is
        // the same picture at half coverage — not white on a quarter-transparent ground.
        var source = new Surface(2, 2);
        source.Pixels[0] = source.Pixels[1] = source.Pixels[2] = source.Pixels[3] = 255;
        source.Pixels[8] = source.Pixels[9] = source.Pixels[10] = source.Pixels[11] = 255;
        var small = DitherEngine.AverageDown(source, 2);
        Assert.Equal(1, small.Width);
        Assert.Equal(1, small.Height);
        Assert.Equal(127u, small.Pixels[0]);
        Assert.Equal(127u, small.Pixels[3]);
    }

    [Fact]
    public void ASquareThatDoesNotDivideTheWidthAveragesOnlyWhatItHolds()
    {
        // Three pixels across with a block of two: the last cell is one pixel wide, so its own value is
        // the average rather than that value halved by an absent neighbor.
        var source = new Surface(3, 1);
        for (int x = 0; x < 3; x++)
        {
            source.Pixels[x * 4] = source.Pixels[x * 4 + 1] = source.Pixels[x * 4 + 2] = source.Pixels[x * 4 + 3] = 200;
        }
        var small = DitherEngine.AverageDown(source, 2);
        Assert.Equal(2, small.Width);
        Assert.Equal(200u, small.Pixels[0]);
        Assert.Equal(200u, small.Pixels[4]);
    }
}
