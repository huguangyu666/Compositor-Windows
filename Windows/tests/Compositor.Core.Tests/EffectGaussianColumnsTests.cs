using System.Security.Cryptography;
using Xunit;

namespace Compositor.Core.Tests;

public class EffectGaussianColumnsTests
{
    // Captured from da1866c before batching vertical column updates. Reference extent
    // stays large so these narrow windows exercise the streaming box path.
    [Theory]
    [InlineData(7, 13, 8, "A4A6BD75437DFBAD86461AE1502FD42408DE7004AFE89031A3196796DBE7686B")]
    [InlineData(7, 13, 500, "2DA426885CE1F49547B166E7E6585379D1BDF9B1E34C35EB147C0E7CB54CAE3A")]
    [InlineData(8, 15, 8, "0B9DF31C2D4AFB498082640614D86BE4D40598B595611C9C4B0F4151CD5B0778")]
    [InlineData(8, 15, 500, "6E18F27B6E91C5B16CDC9DA280D12CF60EDC4D9B30136C6A935727CFED616B52")]
    [InlineData(9, 17, 8, "206B21DCECEDC455EA71D02A818B24E278CDDB000A9BD9EEB265A8AD9FCE3413")]
    [InlineData(9, 17, 500, "62AA086CB79BD14E83FE132EF82746974B9AA1A0AB20755A93C206BD84C39C3A")]
    [InlineData(31, 7, 8, "E108E6615884999D44EA752B551EF1AAA2C1A24636C635CB33904F92A5E709AC")]
    [InlineData(31, 7, 500, "0066416DCBF2DFB903FD454E1B2689257C7D694BDFB7D74C0BABAF6BAE0FF434")]
    [InlineData(32, 9, 8, "B2B089BDBA399AA315B2DA6A65CBAAF5C8550366BD56EA136F3FCF51CCB30EA5")]
    [InlineData(32, 9, 500, "2BB7EA02718F8431AD9439F43D20ECFDBCAB53561FD1601CB07018BCF4960DDC")]
    [InlineData(33, 11, 8, "6ADF2FBB20C3216CD3005A0A0DAEFA3067CDDE653831C5DC86C32F2C3A3EFF73")]
    [InlineData(33, 11, 500, "AB25500E1C4E556D40226A3A9EAFFE07A74EC082344BACE86381BA76C1371222")]
    public void ColumnBatchesPreserveLegacyClampRoundingAndVectorTails(int width, int height, double blur, string hash)
    {
        var scratch = new EffectScratch(); scratch.Resize(width, height);
        foreach (var workspace in new EffectScratch?[] { null, scratch })
        {
            var data = Pattern(width * height);
            EffectGaussian.ApplyWindow(data, width, height, blur, 4096, 4096, workspace);
            Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(data)));
        }
    }

    [Fact]
    public void IndependentConcurrentWindowsDoNotShareColumnState()
    {
        var input = Pattern(173 * 181);
        var expected = new[] { 8d, 40, 500 }.Select(blur =>
        {
            var copy = input.ToArray(); EffectGaussian.Apply(copy, 173, 181, blur); return copy;
        }).ToArray();
        Parallel.For(0, 24, i =>
        {
            var copy = input.ToArray(); EffectGaussian.Apply(copy, 173, 181, new[] { 8d, 40, 500 }[i % 3]);
            Assert.Equal(expected[i % 3], copy);
        });
    }

    private static byte[] Pattern(int count) { var bytes = new byte[count]; new Random(7301).NextBytes(bytes); return bytes; }
}
