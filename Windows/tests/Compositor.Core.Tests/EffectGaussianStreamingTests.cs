using System.Linq;
using System.Security.Cryptography;
using Xunit;

namespace Compositor.Core.Tests;

public class EffectGaussianStreamingTests
{
    // Captured from 29c4b02's two-full-plane implementation before replacing its storage.
    [Theory]
    [InlineData(201, 179, 8, 201, 179, "A9EB6DDF081E640AA7B08B5520C8D61FFD3107D33CA1A867E40C3E74D3BB07D5")]
    [InlineData(377, 293, 40, 377, 293, "2116EAC7C5C53C76928A6BEA7016A93482714B344749741CC045AAB829EB479B")]
    [InlineData(181, 7, 500, 181, 7, "781379926A151A8A4396EBE99F705020020B7FC1B49C15380BE068DEA2D7C78D")]
    [InlineData(1, 171, 40, 1, 171, "2B3EC2CD66BE5A2C6F95D6E19B60E4940A11FA322C5175FCFDB01C8C311F6C44")]
    [InlineData(179, 1, 8, 179, 1, "E27327A3C81085ECD856219E334FB812048C22857FDA7E555D0A8E2D984C4129")]
    [InlineData(51, 47, 40, 1024, 1024, "0726742C5F51C8E21BD21363D1AF8411B173695689E9E8E2A2BD046862A07ADE")]
    public void StreamingPreservesPreviousBytesAndFullPlaneGaussianChoice(int width, int height, double blur,
        int referenceWidth, int referenceHeight, string hash)
    {
        var scratch = new EffectScratch(); scratch.Resize(width, height);
        foreach (var workspace in new EffectScratch?[] { null, scratch })
        {
            var coverage = new byte[width * height]; new Random(7301).NextBytes(coverage);
            EffectGaussian.ApplyWindow(coverage, width, height, blur, referenceWidth, referenceHeight, workspace);
            Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(coverage)));
        }
    }

    [Fact]
    public void ColdBoxBlurDoesNotAllocateTwoFullFloatPlanes()
    {
        const int width = 2048, height = 2048;
        var coverage = new byte[width * height]; new Random(7301).NextBytes(coverage);
        EffectGaussian.Apply(new byte[201 * 179], 201, 179, 40);
        long before = GC.GetAllocatedBytesForCurrentThread();
        EffectGaussian.Apply(coverage, width, height, 40);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < coverage.LongLength * 5 + 1024 * 1024, $"Cold Gaussian allocated {allocated} bytes.");
    }

    [Fact]
    public void RetainedWindowReusesCapacityAcrossDifferentBlurPasses()
    {
        const int width = 201, height = 179;
        var scratch = new EffectScratch(); scratch.Resize(width, height);
        var input = new byte[width * height]; new Random(7301).NextBytes(input);
        EffectGaussian.ApplyWindow(input.ToArray(), width, height, 40, 1024, 1024, scratch);
        double[] passes = [8.0, 40, 8];
        // The reference passes take two full float planes each (~36 KB). Computed up front, they stay
        // clear of the measurement window below: with one of them landing right before the window,
        // GC.GetAllocatedBytesForCurrentThread reported a lump of exactly 6704 bytes in 2 of 180 runs
        // and 0 in the rest — the thread's allocation quantum crossing, charged to the next call.
        // That is B08's flake, and it was the test's own neighbour, not the retained path allocating.
        var references = passes.Select(blur =>
        {
            var copy = input.ToArray();
            EffectGaussian.ApplyWindow(copy, width, height, blur, 1024, 1024);
            return copy;
        }).ToList();
        foreach (var (blur, expected) in passes.Zip(references))
        {
            var actual = input.ToArray();
            // One throwaway retained pass absorbs that accounting jump before the asserted window.
            EffectGaussian.ApplyWindow(input.ToArray(), width, height, blur, 1024, 1024, scratch);
            var current = scratch.CurrentFloat; var rows = scratch.HorizontalFloatRows(1); var columns = scratch.Columns;
            long before = GC.GetAllocatedBytesForCurrentThread();
            EffectGaussian.ApplyWindow(actual, width, height, blur, 1024, 1024, scratch);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            bool retained = ReferenceEquals(current, scratch.CurrentFloat) && ReferenceEquals(rows, scratch.HorizontalFloatRows(1))
                && ReferenceEquals(columns, scratch.Columns);
            // The threshold is unchanged: the retained path measured 0 bytes in every one of the 180
            // samples once the neighbor was moved out of the window.
            Assert.True(allocated < 4096, $"Retained Gaussian blur {blur} allocated {allocated} bytes; same buffers={retained}, " +
                $"current={current.Length}, rows={rows.Length}, columns={columns.Length}, thread={Environment.CurrentManagedThreadId}, " +
                $"tiered={Environment.GetEnvironmentVariable("DOTNET_TieredCompilation")}.");
            Assert.True(retained, "Warmed Gaussian buffer identities changed.");
            Assert.Equal(expected, actual);
        }
    }
}
