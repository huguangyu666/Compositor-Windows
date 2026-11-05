using System.Diagnostics;
using System.Numerics;
using System.Runtime.Intrinsics.X86;
using Xunit;

namespace Compositor.Core.Tests;

[CollectionDefinition("Gray warp performance", DisableParallelization = true)]
public sealed class GrayPerspectivePerformanceCollection { }

[Collection("Gray warp performance")]
public class GrayPerspectivePerformanceTests
{
    [Fact]
    public void ConvexGrayWarpAvoidsRepeatedInverseSetup()
    {
        const int side = 1024;
        var mask = new MaskSurface(side, side); new Random(7301).NextBytes(mask.Pixels);
        var value = new LayerTransform { Size = new(side, side) };
        PointD[] corners = [new(.25, .125), new(side * .95, 1.125), new(side - .75, side * .91), new(side * .06, side - .5)];
        var inverse = DistortWarp.Inverse(corners);
        MaskSurface expected = null!, actual = null!;
        for (int i = 0; i < 2; i++) { Reference(); Draw(); }
        var referenceTimes = new List<double>(); var drawTimes = new List<double>();
        for (int i = 0; i < 5; i++)
        {
            if (i % 2 == 0) { referenceTimes.Add(Measure(Reference)); drawTimes.Add(Measure(Draw)); }
            else { drawTimes.Add(Measure(Draw)); referenceTimes.Add(Measure(Reference)); }
        }
        Assert.Equal(expected.Pixels, actual.Pixels);
        double referenceMedian = referenceTimes.Order().ElementAt(2), drawMedian = drawTimes.Order().ElementAt(2);
        if (Vector.IsHardwareAccelerated)
            Assert.True(drawMedian < referenceMedian * .5,
                $"Convex gray warp median={drawMedian:F3}ms, scalar inverse reference={referenceMedian:F3}ms; expected inverse setup to be reused. " +
                $"Vector={Vector.IsHardwareAccelerated}/{Vector<double>.Count}, AVX2={Avx2.IsSupported}, tiered={Environment.GetEnvironmentVariable("DOTNET_TieredCompilation")}, " +
                $"draw=[{string.Join(",", drawTimes.Select(v => v.ToString("F3")))}], reference=[{string.Join(",", referenceTimes.Select(v => v.ToString("F3")))}].");
        void Draw() => actual = DistortWarp.WarpMask(mask, value, corners).Mask;
        void Reference()
        {
            expected = new MaskSurface(side, side);
            for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
            {
                var unit = inverse(new(x + .5, y + .5));
                if (unit is not { } uv || uv.X < 0 || uv.X > 1 || uv.Y < 0 || uv.Y > 1) continue;
                double sx = uv.X * side - .5, sy = uv.Y * side - .5;
                int ix = (int)Math.Floor(sx), iy = (int)Math.Floor(sy);
                double fx = sx - ix, fy = sy - iy;
                int x0 = Math.Clamp(ix, 0, side - 1), x1 = Math.Clamp(ix + 1, 0, side - 1);
                int top = Math.Clamp(iy, 0, side - 1) * side, bottom = Math.Clamp(iy + 1, 0, side - 1) * side;
                double a = mask.Pixels[top + x0] + (mask.Pixels[top + x1] - mask.Pixels[top + x0]) * fx;
                double b = mask.Pixels[bottom + x0] + (mask.Pixels[bottom + x1] - mask.Pixels[bottom + x0]) * fx;
                expected.Pixels[y * side + x] = (byte)Math.Clamp((int)Math.Round(a + (b - a) * fy), 0, 255);
            }
        }
        static double Measure(Action action) { var watch = Stopwatch.StartNew(); action(); watch.Stop(); return watch.Elapsed.TotalMilliseconds; }
    }
}
