using System.Diagnostics;
using System.Numerics;
using Compositor.Core;
using Compositor.Renderer;
using Xunit;

namespace Compositor.Renderer.Tests;

[CollectionDefinition("Presentation performance", DisableParallelization = true)]
public sealed class PresentationPerformanceCollection { }

[Collection("Presentation performance")]
public class SurfacePresentationPerformanceTests
{
    [Fact]
    public void PackedPresentationKeepsScalarBytesWithLowerBatchCost()
    {
        const int side = 2048;
        var source = new Surface(side, side); new Random(7301).NextBytes(source.Pixels);
        var original = source.Pixels.ToArray(); var expected = new byte[source.Pixels.Length]; var actual = new byte[expected.Length];
        for (int i = 0; i < 4; i++) { Reference(); Draw(); }
        var referenceTimes = new List<double>(); var drawTimes = new List<double>();
        for (int i = 0; i < 7; i++)
        {
            if (i % 2 == 0) { referenceTimes.Add(Measure(Reference)); drawTimes.Add(Measure(Draw)); }
            else { drawTimes.Add(Measure(Draw)); referenceTimes.Add(Measure(Reference)); }
        }
        Assert.Equal(expected, actual); Assert.Equal(original, source.Pixels);
        double referenceMedian = referenceTimes.Order().ElementAt(3), drawMedian = drawTimes.Order().ElementAt(3);
        if (Vector.IsHardwareAccelerated && BitConverter.IsLittleEndian)
            Assert.True(drawMedian < referenceMedian * .4,
                $"BGRA median={drawMedian:F3}ms, scalar={referenceMedian:F3}ms; expected channel swizzle to be batched.");
        void Draw() => SurfacePresentation.FillBgra(source, actual, side, 0, 0, side, side);
        void Reference()
        {
            for (int row = 0; row < side; row++)
            {
                int input = row * source.Stride, output = row * side * 4;
                for (int column = 0; column < side; column++)
                {
                    expected[output] = source.Pixels[input + 2]; expected[output + 1] = source.Pixels[input + 1];
                    expected[output + 2] = source.Pixels[input]; expected[output + 3] = source.Pixels[input + 3];
                    input += 4; output += 4;
                }
            }
        }
        static double Measure(Action action) { var watch = Stopwatch.StartNew(); action(); watch.Stop(); return watch.Elapsed.TotalMilliseconds; }
    }
}
