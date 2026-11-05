using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// Dithering is the edit that can hold a worker thread for seconds on a large layer, and its output
/// replaces a layer's whole image, so its sweeps check the retirement token at row boundaries.
/// A pre-cancelled token proves the check sits in front of the pixel writes rather than beside them;
/// a live but uncancelled token run against the same input proves the checks cost the normal path
/// nothing. Stopping a run that is already halfway through a 4K document is measured by the App
/// transform probe, which retires the operation through the real document-switch path.
/// </summary>
public sealed class DitherCancellationTests
{
    private static Surface Gray96(int width = 64, int height = 48)
    {
        var surface = new Surface(width, height);
        for (int i = 0; i < surface.Pixels.Length; i += 4)
        {
            surface.Pixels[i] = 96;
            surface.Pixels[i + 1] = 96;
            surface.Pixels[i + 2] = 96;
            surface.Pixels[i + 3] = 255;
        }
        return surface;
    }

    private static DitherParams Parameters(DitherStyle style, bool originalColors = false) => new()
    {
        Style = style,
        Levels = 4,
        Diffusion = 0.75f,
        Cell = 4,
        Dots = 0.5f,
        Wobble = 2f,
        OriginalColors = originalColors,
        GlyphWidth = 1,
        GlyphHeight = 1,
        GlyphCoverage = [0f, 1f],
        GlyphMaps = [0, 255],
    };

    public static TheoryData<DitherStyle> Styles => new()
    {
        DitherStyle.Atkinson,
        DitherStyle.FloydSteinberg,
        DitherStyle.Bayer2,
        DitherStyle.Bayer4,
        DitherStyle.Bayer8,
        DitherStyle.HalftoneDots,
        DitherStyle.HalftoneLines,
        DitherStyle.HalftoneDiamonds,
        DitherStyle.MacPatterns,
        DitherStyle.Ascii,
        DitherStyle.Scanlines,
    };

    [Theory]
    [MemberData(nameof(Styles))]
    public void CancelledTokenStopsBeforeAnyPixelChanges(DitherStyle style)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var surface = Gray96();
        var before = (byte[])surface.Pixels.Clone();

        Assert.Throws<OperationCanceledException>(() => DitherEngine.Apply(surface, Parameters(style), cancellation.Token));
        Assert.True(before.AsSpan().SequenceEqual(surface.Pixels), $"{style} wrote pixels after its token was cancelled");
    }

    [Fact]
    public void CancelledTokenStopsThePerPlaneSweepsOfOriginalColors()
    {
        // Original colors build three tone planes, and each style's plane loops is checked apart
        // from the single-plane ones, so both shapes have to honour the token.
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var surface = Gray96();
        var before = (byte[])surface.Pixels.Clone();

        Assert.Throws<OperationCanceledException>(
            () => DitherEngine.Apply(surface, Parameters(DitherStyle.Bayer4, originalColors: true), cancellation.Token));
        Assert.True(before.AsSpan().SequenceEqual(surface.Pixels), "the three-plane run wrote pixels after cancellation");
    }

    [Theory]
    [MemberData(nameof(Styles))]
    public void LiveTokenRunsTheSameDitherAsNoToken(DitherStyle style)
    {
        using var cancellation = new CancellationTokenSource();
        var withoutToken = Gray96();
        DitherEngine.Apply(withoutToken, Parameters(style));
        var withToken = Gray96();
        DitherEngine.Apply(withToken, Parameters(style), cancellation.Token);

        Assert.True(withoutToken.Pixels.AsSpan().SequenceEqual(withToken.Pixels), $"{style} changed its result for a token");
    }
}
