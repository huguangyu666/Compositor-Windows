using System.Diagnostics;
using System.Runtime.Intrinsics.X86;
using Xunit;

namespace Compositor.Core.Tests;

[Collection("Mask display performance")]
public class MaskDisplayBatchPerformanceTests
{
    [Fact]
    public void HighQualityBatchPreservesScalarBytesWithinOneTenthTheCost()
    {
        var mask = new MaskSurface(513, 507); new Random(7301).NextBytes(mask.Pixels);
        var transform = new LayerTransform { Origin = new(-.375, -.875), Size = new(1026.25, 1035.125) };
        var expected = new Surface(1024, 1024); var actual = new Surface(1024, 1024); var band = new RectI(0, 0, 1024, 1024);
        for (int i = 0; i < 4; i++) { Reference(); Draw(); }
        var referenceTimes = new List<double>(); var drawTimes = new List<double>();
        for (int i = 0; i < 5; i++)
        {
            if (i % 2 == 0) { referenceTimes.Add(Measure(Reference)); drawTimes.Add(Measure(Draw)); }
            else { drawTimes.Add(Measure(Draw)); referenceTimes.Add(Measure(Reference)); }
        }
        Assert.Equal(expected.Pixels, actual.Pixels);
        double referenceMedian = referenceTimes.Order().ElementAt(2), drawMedian = drawTimes.Order().ElementAt(2);
        if (Avx2.IsSupported)
            Assert.True(drawMedian < referenceMedian * .1,
                $"Batch mask display median={drawMedian:F3}ms, scalar reference={referenceMedian:F3}ms; expected high-quality tap arithmetic to be batched.");
        void Reference() => MaskDisplay.DrawReference(mask, transform, expected, band);
        void Draw() => MaskDisplay.Draw(mask, transform, actual, band);
        static double Measure(Action action) { var watch = Stopwatch.StartNew(); action(); watch.Stop(); return watch.Elapsed.TotalMilliseconds; }
    }
}
