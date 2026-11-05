using System.Security.Cryptography;
using Xunit;

namespace Compositor.Core.Tests;

public class RgbaDistortTests
{
    // Captured from 7f6b6c7 before batching bilinear RGBA channels. Source rows
    // include padding, and all four channels have independent values.
    [Theory]
    [InlineData(1, 1, false, false, false, 0, "D1C6A35DB515AA41309348733C839CDDC050267642BEC031D63A25C54C8F909E")]
    [InlineData(1, 17, false, false, false, 0, "92B2394240E92BDD14242943C1423EB26A4EE76E52B08AC1BDC45D893FE82F2B")]
    [InlineData(19, 1, false, false, false, 0, "E49E118F4C777BFF9E86EAE50490241C7CE438EAD10D6D34ED634B2C0BFED3B5")]
    [InlineData(17, 13, false, false, false, 0, "0E1C4B401152E76DB104AC9282020FAD7ABE0911F2047BE3FAEB3B951A79B414")]
    [InlineData(17, 13, false, false, false, 7, "B6D72F68A5E002A27A977F7AAB0F4BA8BF3F847E42AC0946627BCA2FDCB8D8BC")]
    [InlineData(17, 13, false, true, false, 0, "E9CB04D474ADBC7BD5BD73779D22870EB977E9421F6FF4CB54DE3D86C7AE2913")]
    [InlineData(17, 13, false, true, false, 7, "DCA716FD2F15F07F353FBE95680FB59D695897F1B374F7A7883A51F616727C41")]
    [InlineData(17, 13, false, false, true, 0, "8969DFE3C3740990FA172506D7FFEE8788DD17FBF7937D8958C8C66616BBD393")]
    [InlineData(17, 13, false, false, true, 7, "DF91EBFA0035F2769F5414EB06582667D5BE391D77001C5ADD3D9ABB7BB213C8")]
    [InlineData(17, 13, false, true, true, 0, "C355F3545AB3BB760EF3D28E5D20E866612FB1363691D0FB1F143335E283388E")]
    [InlineData(17, 13, false, true, true, 7, "C9FC7927416209393090B00F1AE8916DBC2EF1E02500556A208928087B17C5B2")]
    [InlineData(17, 13, true, false, false, 0, "2061C3C0139188338CDB0B097F49015F2BC1B8EB1A29D0CF504753DD8B1E3665")]
    [InlineData(17, 13, true, false, false, 7, "81D2EEAC0E955819782E6CC615237BE4E2F353BF4C8E04F75BA8591FEC2B2C48")]
    [InlineData(17, 13, true, true, false, 0, "DAE06276490FA61EDF3FEF331DD4EF90C9BA58216860007838F11201BEF33753")]
    [InlineData(17, 13, true, true, false, 7, "03058F1EA8DF786FDE91EE3E96636F6B7676968094F441B5726523D78FB77449")]
    [InlineData(17, 13, true, false, true, 0, "A8B0FAB5A0EBEDBCE575C319799415F7266D1207894600E6A65383F9692F540C")]
    [InlineData(17, 13, true, false, true, 7, "E2F091F2B6A549D2E5A1D2D5FEDADB913F5A0D264149D63BF6B58A6628FF8A53")]
    [InlineData(17, 13, true, true, true, 0, "601F29B8C611E09167EEA4059FC3BAAC2F635348A4794D57B896E46D3B453108")]
    [InlineData(17, 13, true, true, true, 7, "FA1D71A9D7B0DD69AE7C806F218C4A58C71CA9D99D0969ECDCF4831E9D0D049B")]
    [InlineData(113, 127, false, false, false, 0, "AC0D21014E441191C84FD87183B14C41894BD656FFCCFDEBEF851A52732B8F1E")]
    public void WarpPreservesLegacyBytesForPerspectiveFoldFlipsAndPreview(int width, int height,
        bool fold, bool flipX, bool flipY, int cap, string hash)
    {
        var source = Pattern(width, height); var before = source.Pixels.ToArray();
        var transform = new LayerTransform { Origin = new(2, 3), Size = new(17, 13),
            FlipX = flipX, FlipY = flipY, Sampling = LayerSampling.HighQuality };
        PointD[] corners = [new(-1.25, 3.75), new(18.5, -.75), new(20.25, 12.25), new(2.5, 15.75)];
        if (fold) corners[0] = new(22.5, 17.25);
        var actual = DistortWarp.Warp(source, transform, corners, cap == 0 ? null : cap);
        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(actual.Image.Pixels)));
        Assert.Equal(before, source.Pixels);
        Assert.Equal(new PointD(-2 + (fold ? 4 : 0), -1), actual.Transform.Origin);
        Assert.Equal(LayerSampling.HighQuality, actual.Transform.Sampling);
    }

    [Fact]
    public void BilinearChannelsRoundHalfValuesToEvenIndependently()
    {
        var source = new Surface(2, 1, [0, 1, 2, 3, 1, 2, 3, 4], 8);
        var transform = new LayerTransform { Size = new(1, 1) };
        var result = DistortWarp.Warp(source, transform, DistortWarp.CornersOf(transform));
        Assert.Equal(new byte[] { 0, 2, 2, 4 }, result.Image.Pixels);
    }

    [Fact]
    public void LastPackedPixelNeedsOnlyFourReadableBytes()
    {
        var source = new Surface(1, 1, [31, 17, 3, 255], 4);
        var transform = new LayerTransform { Size = new(4, 3) };
        var result = DistortWarp.Warp(source, transform, DistortWarp.CornersOf(transform));
        for (int i = 0; i < result.Image.Pixels.Length; i += 4)
            Assert.Equal(source.Pixels, result.Image.Pixels.AsSpan(i, 4).ToArray());
    }

    [Fact]
    public void WarpAllocatesOneOutputWithoutPerPixelScratch()
    {
        var source = Pattern(512, 512); var transform = new LayerTransform { Size = new(512, 512) };
        var corners = DistortWarp.CornersOf(transform); DistortWarp.Warp(source, transform, corners);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var result = DistortWarp.Warp(source, transform, corners);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < result.Image.Pixels.Length + 65536, $"RGBA warp allocated {allocated} bytes.");
    }

    private static Surface Pattern(int width, int height)
    {
        int stride = width * 4 + 12; var bytes = new byte[stride * height];
        new Random(781 + width * height).NextBytes(bytes);
        return new(width, height, bytes, stride);
    }
}
