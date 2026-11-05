using System.Security.Cryptography;
using Xunit;

namespace Compositor.Core.Tests;

public class PremultipliedOverTests
{
    [Fact]
    public void MixedBlocksMatchFloatReferenceForEveryColorDestinationAndAlphaByte()
    {
        var source = new Surface(256, 256); var initial = new Surface(256, 256);
        var expected = new Surface(256, 256); var actual = new Surface(256, 256);
        for (int color = 0; color < 256; color++)
        {
            for (int destination = 0; destination < 256; destination++)
            for (int alpha = 0; alpha < 256; alpha++)
            {
                int i = (destination * 256 + alpha) * 4;
                source.Pixels[i] = source.Pixels[i + 1] = source.Pixels[i + 2] = (byte)color;
                source.Pixels[i + 3] = (byte)alpha;
                initial.Pixels.AsSpan(i, 4).Fill((byte)destination);
            }
            initial.Pixels.CopyTo(expected.Pixels, 0); initial.Pixels.CopyTo(actual.Pixels, 0);
            PremultipliedOver.Apply(source, expected, new(0, 0, 256, 256), useVector: false);
            PremultipliedOver.Apply(source, actual, new(0, 0, 256, 256));
            Assert.Equal(expected.Pixels, actual.Pixels);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedSharedStorageKeepsForwardOrder(bool sameStride)
    {
        const int width = 65, height = 7, sourceStride = width * 4 + 12;
        int destinationStride = sameStride ? sourceStride : width * 4 + 20;
        var original = new byte[destinationStride * height]; Array.Fill(original, (byte)128);
        var expected = original.ToArray(); var actual = original.ToArray();
        PremultipliedOver.Apply(new(width, height, expected, sourceStride), new(width, height, expected, destinationStride), new(2, 1, 58, 4), false);
        PremultipliedOver.Apply(new(width, height, actual, sourceStride), new(width, height, actual, destinationStride), new(2, 1, 58, 4));
        Assert.Equal(expected, actual);
    }

    // Captured from 4c9582f's Composite before adding the bulk path.
    [Theory]
    [InlineData("opaque", "D8A75077615698B2F7DA71BFF14DE5692E11F48E110B2583DBB9CDD2AA72156C")]
    [InlineData("transparent", "983D58A094D5CAD6453AFBE89BC204D15929A607AFA410692F4D3555C159C300")]
    [InlineData("mixed", "A6ECA8A98B89A0C9511818632C9E0196821BC84BE732AD18CE9ED2EC3E858203")]
    [InlineData("partial", "FD304307790ABC54A616676A310423A14B73F3F95BC3D4EC41F19CED969F9FE2")]
    [InlineData("dirty-transparent", "983D58A094D5CAD6453AFBE89BC204D15929A607AFA410692F4D3555C159C300")]
    public void BulkAndScalarMatchPreviousBytesWithStrideClippingAndTail(string kind, string hash)
    {
        foreach (bool vector in new[] { false, true })
        {
            var source = Pattern(65, 17, kind, 91, 12); var input = source.Pixels.ToArray();
            var canvas = Pattern(65, 17, "partial", 73, 20);
            PremultipliedOver.Apply(source, canvas, new(3, 2, 58, 12), vector);
            Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(canvas.Pixels)));
            Assert.Equal(input, source.Pixels);
        }
    }

    internal static Surface Pattern(int width, int height, string kind, int seed, int padding)
    {
        int stride = width * 4 + padding; var image = new Surface(width, height, new byte[stride * height], stride);
        Array.Fill(image.Pixels, (byte)173); var random = new Random(seed);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int alpha = kind switch { "opaque" => 255, "transparent" or "dirty-transparent" => 0,
                "mixed" when x % 48 < 16 => 255, "mixed" when x % 48 < 32 => 0, _ => random.Next(1, 255) };
            int index = y * stride + x * 4; image.Pixels[index + 3] = (byte)alpha;
            for (int c = 0; c < 3; c++) image.Pixels[index + c] = (byte)random.Next(kind == "dirty-transparent" ? 256 : alpha + 1);
        }
        return image;
    }
}
