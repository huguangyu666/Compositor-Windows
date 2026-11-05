using System.Security.Cryptography;
using Xunit;

namespace Compositor.Core.Tests;

public class RgbaPerspectiveTests
{
    // Captured from 09db865's scalar inverse mapping before preparing RGBA columns.
    [Theory]
    [InlineData(1, 1, "4FD8032EC2FCE37BE75EE2B479CFB7B3D80A4715D5CBB1F83FC0A9E402832E40")]
    [InlineData(2, 3, "96C492892F4E1CAB849482606EE400865E185AB10FF29129612643860E6CDCE5")]
    [InlineData(3, 5, "2A71CDEEF1971226F5722303049B1BE27A585598600489C90AAF9B805711BB0F")]
    [InlineData(4, 7, "15F4D4875EFC2A07382801280EC17656F6346C6DD98F5E79C1B52593946AB579")]
    [InlineData(5, 9, "D46EF81627B56555F9B05C8EFB79B358CF7699A4CDC9472A0A974E0F0821FF9C")]
    [InlineData(7, 5, "08DD14F5AA4C8522526EFC29DC9868C4F459BB479E79AD7A09407853644A269A")]
    [InlineData(8, 7, "81BA254345ABF26351C71B49CEC89F7ECBD16185A44808A919AD6D922BC2ED24")]
    [InlineData(9, 9, "0560A707E64D2B29E77D1FAB83767009F379008B2A45666E3874027C2FA9D026")]
    [InlineData(31, 5, "71BE9174F4E6B90DF799D2E69E70F041CF7AA6B7BEB1D734161D300059DA2C68")]
    [InlineData(32, 7, "942E6EC1A38117D41DF80CF5D5BC6F929CEA0105C715FA6C6E841CD86E507250")]
    [InlineData(33, 9, "481D1D6DC7D0A3F04BD991899711BC538303FED081FABD74BB5A937BF1B582A8")]
    public void PreparedColumnsKeepLegacyPixelsAtVectorTails(int width, int height, string expected)
    {
        var source = Pattern(); var before = source.Pixels.ToArray();
        var value = new LayerTransform { Size = new(17, 13) };
        var result = DistortWarp.Warp(source, value, Shape(width, height));
        Assert.Equal(width, result.Image.Width); Assert.Equal(height, result.Image.Height);
        Assert.Equal(expected, Convert.ToHexString(SHA256.HashData(result.Image.Pixels)));
        Assert.Equal(before, source.Pixels);
    }

    [Fact]
    public void ConcurrentWarpsDoNotSharePreparedColumnsOrOutputPixels()
    {
        var source = Pattern(); var value = new LayerTransform { Size = new(17, 13) };
        var shapes = Enumerable.Range(0, 16).Select(i => Shape(31 + i, 19 + i)).ToArray();
        var expected = shapes.Select(shape => DistortWarp.Warp(source, value, shape).Image.Pixels).ToArray();
        Parallel.For(0, 64, i => Assert.Equal(expected[i % shapes.Length], DistortWarp.Warp(source, value, shapes[i % shapes.Length]).Image.Pixels));
    }

    [Fact]
    public void ReusedWideColumnsAllocateNoSecondImagePlane()
    {
        var source = Pattern(); var value = new LayerTransform { Size = new(17, 13) };
        var shape = Shape(8191, 3); DistortWarp.Warp(source, value, shape);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var result = DistortWarp.Warp(source, value, shape);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < result.Image.Pixels.Length + 65536, $"RGBA perspective allocated {allocated} bytes.");
    }

    private static Surface Pattern()
    {
        int stride = 17 * 4 + 12; var pixels = new byte[stride * 13]; new Random(781 + 17 * 13).NextBytes(pixels);
        return new(17, 13, pixels, stride);
    }

    private static PointD[] Shape(int width, int height) =>
        [new(.125, .25), new(width - .125, .125), new(width - .25, height - .125), new(.25, height - .25)];
}
