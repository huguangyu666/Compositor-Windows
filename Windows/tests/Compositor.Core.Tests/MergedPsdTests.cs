using Compositor.Core.Psd;
using Xunit;

namespace Compositor.Core.Tests;

public class MergedPsdTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CompositeReadsEveryColorPlaneAndMarksFallback(bool psb, bool rle)
    {
        var parsed = PsdReader.Read(Composite(psb, rle)); var image = Assert.Single(parsed.Layers).Pixels!;
        Assert.Equal(ExpectedPixels(false), image.Pixels); Assert.True(parsed.IsCompositeFallback);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NegativeLayerCountUsesMergedTransparencyAndPremultiplies(bool rle)
    {
        var parsed = PsdReader.Read(Composite(false, rle, true));
        Assert.Equal(ExpectedPixels(true), Assert.Single(parsed.Layers).Pixels!.Pixels); Assert.True(parsed.IsCompositeFallback);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExtraAlphaWithoutTransparencyFlagDoesNotHideRgb(bool rle)
    {
        var parsed = PsdReader.Read(Composite(false, rle, extraAlpha: true));
        Assert.Equal(ExpectedPixels(false), Assert.Single(parsed.Layers).Pixels!.Pixels);
    }

    [Fact]
    public void MissingMergedPixelsAreRejectedInsteadOfAnEmptyImport()
    {
        var bytes = Composite(false, false);
        Assert.Throws<EndOfStreamException>(() => PsdReader.Read(bytes[..38]));
    }

    private static byte[] ExpectedPixels(bool alpha)
    {
        byte[][] planes = [[232, 16, 128, 64], [90, 200, 70, 40], [84, 11, 255, 33], [128, 255, 0, 64]];
        var result = new byte[16];
        for (int pixel = 0; pixel < 4; pixel++)
        {
            int coverage = alpha ? planes[3][pixel] : 255;
            for (int color = 0; color < 3; color++) result[pixel * 4 + color] = (byte)((planes[color][pixel] * coverage + 127) / 255);
            result[pixel * 4 + 3] = (byte)coverage;
        }
        return result;
    }

    private static byte[] Composite(bool psb, bool rle, bool transparent = false, bool extraAlpha = false)
    {
        byte[][] planes = [[232, 16, 128, 64], [90, 200, 70, 40], [84, 11, 255, 33], [128, 255, 0, 64]];
        int channels = transparent || extraAlpha ? 4 : 3;
        using var stream = new MemoryStream();
        stream.Write("8BPS"u8); U16(psb ? 2 : 1); stream.Write(new byte[6]); U16(channels);
        U32(2); U32(2); U16(8); U16(3); U32(0); U32(0);
        if (transparent)
        {
            // One empty folder, negative count: no usable layer pixels, but merged alpha is present.
            using var record = new MemoryStream();
            record.Write(new byte[18]); record.Write("8BIMnorm"u8); record.Write([255, 0, 0, 0]);
            record.Write([0, 0, 0, 28]); record.Write(new byte[8]); record.Write([1, (byte)'D', 0, 0]);
            record.Write("8BIMlsct"u8); record.Write([0, 0, 0, 4, 0, 0, 0, 1]);
            int infoLength = (int)record.Length + 2; Length(infoLength + (psb ? 8 : 4)); Length(infoLength);
            U16(65535); stream.Write(record.ToArray());
        }
        else Length(0);
        U16(rle ? 1 : 0);
        if (rle)
            for (int channel = 0; channel < channels; channel++)
                for (int row = 0; row < 2; row++) { if (psb) U32(3); else U16(3); }
        for (int channel = 0; channel < channels; channel++)
            for (int row = 0; row < 2; row++)
            { if (rle) stream.WriteByte(1); stream.Write(planes[channel].AsSpan(row * 2, 2)); }
        return stream.ToArray();

        void U16(int value) => stream.Write([(byte)(value >> 8), (byte)value]);
        void U32(int value) => stream.Write([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
        void Length(int value) { if (psb) U32(0); U32(value); }
    }
}
