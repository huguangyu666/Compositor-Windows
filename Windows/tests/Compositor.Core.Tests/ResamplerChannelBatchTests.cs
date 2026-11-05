using System.Security.Cryptography;
using Xunit;

namespace Compositor.Core.Tests;

public class ResamplerChannelBatchTests
{
    // Captured from db1be61 before changing the weighted sampling loop. The hash
    // includes nonzero destination padding and pixels outside the requested band.
    [Theory]
    [InlineData(LayerSampling.Smooth, 17, 13, false, false, "67E17F71DAEC8258EED71C3AB2EC6E13CD369E7E84B7205A102BF7AACE372D5A")]
    [InlineData(LayerSampling.Smooth, 17, 13, true, false, "44CFF4D7C06A736E6487328053620F4625FC9FA623128519A463311C19B97962")]
    [InlineData(LayerSampling.Smooth, 17, 13, false, true, "9108CB0FF6320407227349ABE35C0D3BB9A1F5C27525B9B2F4AAFED6C35EA8C8")]
    [InlineData(LayerSampling.Smooth, 17, 13, true, true, "F302C4CB0D01383A293032D7E86BEE4574F66274C6B3A6C8095A983E9A887621")]
    [InlineData(LayerSampling.HighQuality, 17, 13, false, false, "93E040C09CD1145DA6AE4DD93938772E79E6C362686B7D0FC7B5A26100D84CF3")]
    [InlineData(LayerSampling.HighQuality, 17, 13, true, false, "50890EB8C2CEA29229517EC7B268E45412F71C93633D7DF35986BD5205FAC50A")]
    [InlineData(LayerSampling.HighQuality, 17, 13, false, true, "25A82BB400EF778A1F037DC4DE6D34F9692815ED2F724B953F3AAC3D5F4F999B")]
    [InlineData(LayerSampling.HighQuality, 17, 13, true, true, "4ED3C64E747E8BE406339C3F98E11BD6823824D81D907503CD72DEF4433CC8B8")]
    [InlineData(LayerSampling.HighQuality, 1, 1, false, false, "89DE111BCE26EABEF4AE866F695363FE48167C5E12C573B6574FDE1D0B0DE30C")]
    [InlineData(LayerSampling.HighQuality, 1, 17, false, false, "EEBC7BCCA5677E4E2BCC894F0EB3094791E07D8A7BC3FD79517B170380ED0D64")]
    [InlineData(LayerSampling.HighQuality, 19, 1, false, false, "E52DB2626909EB2747814246CE403C4709E5DDCE1455F9B798F7BA45E36C1D2A")]
    [InlineData(LayerSampling.HighQuality, 113, 127, false, false, "A3A0E9245B21C5DB606BF1C6C910F353465F3CBA3ABB1C0443E89DCEE246DC3A")]
    public void WeightedChannelsPreserveLegacyBytesAndPadding(LayerSampling sampling, int width, int height,
        bool flipX, bool flipY, string hash)
    {
        var source = Pattern(width, height); var before = source.Pixels.ToArray();
        var target = Pattern(151, 143);
        var transform = new LayerTransform { Origin = new(-2.375, 1.125), Size = new(141.75, 135.25),
            FlipX = flipX, FlipY = flipY, Sampling = sampling };
        Resampler.Draw(source, transform, target, 3, 2, 148, 141);
        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(target.Pixels)));
        Assert.Equal(before, source.Pixels);
    }

    [Theory]
    [InlineData(LayerSampling.Smooth)]
    [InlineData(LayerSampling.HighQuality)]
    public void CroppedMemoryUsesTheSameGlobalTaps(LayerSampling sampling)
    {
        var whole = Pattern(113, 127);
        var transform = new LayerTransform { Origin = new(-2.375, 1.125), Size = new(141.75, 135.25), Sampling = sampling };
        var band = new RectI(43, 31, 29, 23);
        var needed = Resampler.RequiredSourceRegion(whole.Width, whole.Height, transform, band);
        var cropped = whole.Crop(needed.X, needed.Y, needed.Width, needed.Height);
        var expected = Pattern(151, 143); var actual = expected.Clone();
        Resampler.Draw(whole, transform, expected, band.X, band.Y, band.X + band.Width, band.Y + band.Height);
        Resampler.Draw(new SourceWindow(cropped, whole.Width, whole.Height, needed.X, needed.Y), transform,
            actual, band.X, band.Y, band.X + band.Width, band.Y + band.Height);
        Assert.Equal(expected.Pixels, actual.Pixels);
    }

    [Theory]
    [InlineData(LayerSampling.Smooth)]
    [InlineData(LayerSampling.HighQuality)]
    public void SinglePackedPixelNeedsNoBytesPastTheLastPixel(LayerSampling sampling)
    {
        var source = new Surface(1, 1, new byte[] { 31, 17, 3, 255 }, 4);
        var target = new Surface(9, 7);
        var transform = new LayerTransform { Size = new(9.5, 7.5), Sampling = sampling };
        Resampler.Draw(source, transform, target, 0, 0, 9, 7);
        for (int y = 0; y < 7; y++) for (int x = 0; x < 9; x++)
        {
            // Retain the existing half-pixel validity boundary, including for a
            // one-pixel source. The final valid tap has exactly four readable bytes.
            var expected = x < 5 && y < 4 ? source.Pixels : new byte[4];
            Assert.Equal(expected, target.Pixels.AsSpan(y * target.Stride + x * 4, 4).ToArray());
        }
    }

    private static Surface Pattern(int width, int height)
    {
        int stride = width * 4 + 12; var bytes = new byte[stride * height];
        new Random(211 + width * height).NextBytes(bytes);
        return new(width, height, bytes, stride);
    }
}
