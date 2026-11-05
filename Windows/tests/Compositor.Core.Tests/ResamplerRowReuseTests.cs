using System.Security.Cryptography;
using Xunit;

namespace Compositor.Core.Tests;

public class ResamplerRowReuseTests
{
    // Captured from 20cc708 before sharing horizontal rows between output rows.
    [Theory]
    [InlineData(LayerSampling.Smooth, false, false, "44CFF4D7C06A736E6487328053620F4625FC9FA623128519A463311C19B97962")]
    [InlineData(LayerSampling.Smooth, true, false, "67E17F71DAEC8258EED71C3AB2EC6E13CD369E7E84B7205A102BF7AACE372D5A")]
    [InlineData(LayerSampling.Smooth, false, true, "F302C4CB0D01383A293032D7E86BEE4574F66274C6B3A6C8095A983E9A887621")]
    [InlineData(LayerSampling.Smooth, true, true, "9108CB0FF6320407227349ABE35C0D3BB9A1F5C27525B9B2F4AAFED6C35EA8C8")]
    [InlineData(LayerSampling.HighQuality, false, false, "50890EB8C2CEA29229517EC7B268E45412F71C93633D7DF35986BD5205FAC50A")]
    [InlineData(LayerSampling.HighQuality, true, false, "93E040C09CD1145DA6AE4DD93938772E79E6C362686B7D0FC7B5A26100D84CF3")]
    [InlineData(LayerSampling.HighQuality, false, true, "4ED3C64E747E8BE406339C3F98E11BD6823824D81D907503CD72DEF4433CC8B8")]
    [InlineData(LayerSampling.HighQuality, true, true, "25A82BB400EF778A1F037DC4DE6D34F9692815ED2F724B953F3AAC3D5F4F999B")]
    public void CachedRowsKeepLegacyBytesForFlipsAndHalfTurn(LayerSampling sampling, bool flipX, bool flipY, string hash)
    {
        var source = Pattern(17, 13); var before = source.Pixels.ToArray(); var target = Pattern(151, 143);
        var transform = Placement(sampling, flipX, flipY); transform.Rotation = 180;
        Resampler.Draw(source, transform, target, 3, 2, 148, 141);
        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(target.Pixels)));
        Assert.Equal(before, source.Pixels);
    }

    [Theory]
    [InlineData(LayerSampling.Smooth, false)]
    [InlineData(LayerSampling.Smooth, true)]
    [InlineData(LayerSampling.HighQuality, false)]
    [InlineData(LayerSampling.HighQuality, true)]
    public void DirtyBandsKeepClampedRowsAndDestinationPadding(LayerSampling sampling, bool flipY)
    {
        var source = Pattern(17, 13); var transform = Placement(sampling, false, flipY);
        var expected = Pattern(151, 143); var actual = expected.Clone();
        Resampler.Draw(source, transform, expected, 3, 2, 148, 141);
        int[] boundaries = [2, 3, 7, 8, 31, 89, 127, 140, 141];
        for (int i = 0; i + 1 < boundaries.Length; i++)
            Resampler.Draw(source, transform, actual, 3, boundaries[i], 148, boundaries[i + 1]);
        Assert.Equal(expected.Pixels, actual.Pixels);
    }

    [Theory]
    [InlineData(LayerSampling.Smooth)]
    [InlineData(LayerSampling.HighQuality)]
    public void CroppedSourceUsesGlobalRowIndices(LayerSampling sampling)
    {
        var source = Pattern(113, 127); var transform = Placement(sampling, true, true);
        var band = new RectI(19, 11, 97, 103);
        var needed = Resampler.RequiredSourceRegion(source.Width, source.Height, transform, band);
        var cropped = source.Crop(needed.X, needed.Y, needed.Width, needed.Height);
        var expected = Pattern(151, 143); var actual = expected.Clone();
        Resampler.Draw(source, transform, expected, band.X, band.Y, band.X + band.Width, band.Y + band.Height);
        Resampler.Draw(new SourceWindow(cropped, source.Width, source.Height, needed.X, needed.Y), transform,
            actual, band.X, band.Y, band.X + band.Width, band.Y + band.Height);
        Assert.Equal(expected.Pixels, actual.Pixels);
    }

    [Theory]
    [InlineData(LayerSampling.Smooth, "8D9C3045DC1FE3E3F57279A251FA52A1322829665C3A2DCD9F90E496E905ABAA")]
    [InlineData(LayerSampling.HighQuality, "8828E58326D20A8934A0AE9C6EE0EF30D2C55295CBE2DD8BDAC9C8C7FBACFFE0")]
    public void AliasedBuffersKeepLegacyForwardSampling(LayerSampling sampling, string hash)
    {
        var image = Pattern(63, 47);
        var transform = new LayerTransform { Origin = new(-1.75, .125), Size = new(64.75, 48.375), Sampling = sampling, FlipY = true };
        Resampler.Draw(image, transform, image, 1, 2, 61, 45);
        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(image.Pixels)));
    }

    [Theory]
    [InlineData(LayerSampling.Smooth)]
    [InlineData(LayerSampling.HighQuality)]
    public async Task SeparateConcurrentDrawsDoNotShareLiveRows(LayerSampling sampling)
    {
        var inputs = Enumerable.Range(0, 4).Select(i => Pattern(31 + i, 27 + i)).ToArray();
        var expected = inputs.Select(source =>
        {
            var target = Pattern(151, 143); Resampler.Draw(source, Placement(sampling), target, 3, 2, 148, 141); return target.Pixels;
        }).ToArray();
        var actual = await Task.WhenAll(inputs.Select(source => Task.Run(() =>
        {
            var target = Pattern(151, 143); Resampler.Draw(source, Placement(sampling), target, 3, 2, 148, 141); return target.Pixels;
        })));
        for (int i = 0; i < inputs.Length; i++) Assert.Equal(expected[i], actual[i]);
    }

    [Fact]
    public void WarmDrawDoesNotAllocateAnIntermediateImage()
    {
        var source = Pattern(257, 251); var target = new Surface(512, 512);
        var transform = new LayerTransform { Size = new(513.25, 519.5), Sampling = LayerSampling.HighQuality };
        Resampler.Draw(source, transform, target, 0, 0, 512, 512);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Resampler.Draw(source, transform, target, 0, 0, 512, 512);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 131072, $"Warm weighted draw allocated {allocated} bytes.");
    }

    private static LayerTransform Placement(LayerSampling sampling, bool flipX = false, bool flipY = false) => new()
    {
        Origin = new(-2.375, 1.125), Size = new(141.75, 135.25), FlipX = flipX, FlipY = flipY, Sampling = sampling,
    };

    private static Surface Pattern(int width, int height)
    {
        int stride = width * 4 + 12; var pixels = new byte[stride * height];
        new Random(211 + width * height).NextBytes(pixels); return new(width, height, pixels, stride);
    }
}
