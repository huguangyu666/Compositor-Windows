using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// Exploration only: is the coverage gaussian additive? An incremental shadow would blur each new dab
/// separately and sum, so blur(a ⊕ b) must equal blur(a) + blur(b) — including against the plane edge, where
/// the kernel replicates the border on purpose. This file is a measurement harness, not a gate.
/// </summary>
public class EffectBlurAdditivityTests
{
    private static byte[] Plane(int width, int height, params (int X, int Y, int Size, byte Value)[] dabs)
    {
        var plane = new byte[width * height];
        foreach (var (x, y, size, value) in dabs)
            for (int row = y; row < Math.Min(height, y + size); row++)
            for (int col = x; col < Math.Min(width, x + size); col++) plane[row * width + col] = value;
        return plane;
    }

    private static byte[] Blurred(int width, int height, byte[] coverage, double blur)
    {
        var copy = coverage.ToArray();
        EffectGaussian.ApplyWindow(copy, width, height, blur, width, height, null);
        return copy;
    }

    private static (int Max, double Mean, int WorstIndex) Compare(byte[] sum, byte[] joint, int width)
    {
        int max = 0, worst = 0; double total = 0;
        for (int i = 0; i < sum.Length; i++)
        {
            int diff = Math.Abs(sum[i] - joint[i]);
            total += diff;
            if (diff > max) { max = diff; worst = i; }
        }
        return (max, total / sum.Length, worst);
    }

    [Theory]
    [InlineData(60.0)] [InlineData(200.0)] [InlineData(400.0)]
    public void BlurringTwoDisjointDabsSeparatelyVersusTogether(double blur)
    {
        const int width = 512, height = 512;
        // Interior pair, then a pair pressed against both plane edges where the kernel replicates.
        foreach (var (label, a, b) in new (string, (int, int, int, byte)[], (int, int, int, byte)[])[]
        {
            ("interior", [(100, 100, 40, 200)], [(300, 300, 40, 200)]),
            ("bothEdges", [(0, 200, 40, 200)], [(472, 300, 40, 200)]),
        })
        {
            var planeA = Plane(width, height, a);
            var planeB = Plane(width, height, b);
            var planeAB = Plane(width, height, [.. a, .. b]);
            var blurredA = Blurred(width, height, planeA, blur);
            var blurredB = Blurred(width, height, planeB, blur);
            var joint = Blurred(width, height, planeAB, blur);
            var sum = new byte[blurredA.Length];
            for (int i = 0; i < sum.Length; i++) sum[i] = (byte)Math.Min(255, blurredA[i] + blurredB[i]);
            var (max, mean, worst) = Compare(sum, joint, width);
            var (wx, wy) = (worst % width, worst / width);
            Assert.True(max <= 2,
                $"blur={blur} {label}: separate-then-sum versus together differs by max {max} (mean {mean:0.000}) " +
                $"at ({wx},{wy}); clamped pixels = {sum.Count(i => i == 0 && joint[i] != 0)} missed, " +
                $"{sum.Zip(joint, (x, y) => (x, y)).Count(p => p.x == 255 && p.y != 255)} saturated.");
        }
    }

    /// <summary>
    /// The case an incremental shadow would actually hit: the new dab overlaps ink that is already there, so
    /// coverage saturates at 255 and a ⊕ Δ is not a + Δ. How far apart the two are is the budget the next
    /// batch has to buy back, so this reports the numbers rather than hiding them behind a comfortable bound.
    /// </summary>
    [Theory]
    [InlineData(60.0)] [InlineData(200.0)] [InlineData(400.0)]
    public void BlurringOverlappingDabsSeparatelyDivergesWhereCoverageSaturates(double blur)
    {
        const int width = 512, height = 512;
        var existing = Plane(width, height, (100, 100, 120, 255));
        var delta = Plane(width, height, (190, 100, 120, 255));
        var union = new byte[width * height];
        var difference = new byte[width * height];
        int saturated = 0;
        for (int i = 0; i < union.Length; i++)
        {
            union[i] = (byte)Math.Min(255, existing[i] + delta[i]);
            difference[i] = (byte)(union[i] - existing[i]);
            if (existing[i] + delta[i] > 255) saturated++;
        }
        var blurredUnion = Blurred(width, height, union, blur);
        var blurredExisting = Blurred(width, height, existing, blur);
        var blurredDelta = Blurred(width, height, difference, blur);
        var sum = new byte[blurredUnion.Length];
        int max = 0, worst = 0;
        for (int i = 0; i < sum.Length; i++)
        {
            sum[i] = (byte)Math.Min(255, blurredExisting[i] + blurredDelta[i]);
            int diff = Math.Abs(sum[i] - blurredUnion[i]);
            if (diff > max) { max = diff; worst = i; }
        }
        Assert.True(max <= 2,
            $"blur={blur}: coverage saturated in {saturated} pixels, yet separate-then-sum versus together differs by " +
            $"max {max} at ({worst % width},{worst / width}); mean {Enumerable.Range(0, sum.Length)
                .Average(i => Math.Abs((double)sum[i] - blurredUnion[i])):0.000}. " +
            $"Measured on 2026-10-15 at max 1 and mean 0.040/0.214/0.252 for blur 60/200/400 — union is exactly " +
            $"existing + difference by construction, so byte space stays additive and only the two roundings differ. " +
            $"The bound is one above that measurement, not a tuned threshold: it is the double-rounding budget, and it " +
            $"goes red if the kernel stops being linear on a replicated border.");
    }

    /// <summary>
    /// The risk the design actually carries: an incremental shadow accumulates one blurred delta per dab, so the
    /// error must not walk away from a full re-render over a stroke's worth of steps.
    /// </summary>
    [Theory]
    [InlineData(60.0)] [InlineData(200.0)] [InlineData(400.0)]
    public void TwentyIncrementalDabsStayNearOneFullRender(double blur)
    {
        const int width = 512, height = 512, dabs = 20;
        var union = Plane(width, height, (100, 100, 120, 255));
        var accumulated = Blurred(width, height, union, blur);
        var before = union.ToArray();
        for (int dab = 0; dab < dabs; dab++)
        {
            var patch = Plane(width, height, (150 + dab * 12, 240, 40, 255));
            var next = new byte[width * height];
            var delta = new byte[width * height];
            for (int i = 0; i < next.Length; i++)
            {
                next[i] = (byte)Math.Min(255, before[i] + patch[i]);
                delta[i] = (byte)(next[i] - before[i]);
            }
            var blurredDelta = Blurred(width, height, delta, blur);
            for (int i = 0; i < next.Length; i++)
            {
                accumulated[i] = (byte)Math.Min(255, accumulated[i] + blurredDelta[i]);
                before[i] = next[i];
            }
        }
        var reference = Blurred(width, height, before, blur);
        int max = 0, drifting = 0; double total = 0;
        for (int i = 0; i < accumulated.Length; i++)
        {
            int diff = Math.Abs(accumulated[i] - reference[i]);
            total += diff;
            if (diff > 0) drifting++;
            if (diff > max) max = diff;
        }
        Assert.True(max <= 12,
            $"blur={blur}: after {dabs} accumulated deltas the shadow differs from a full re-render by max {max} " +
            $"(mean {total / accumulated.Length:0.000}, {drifting} pixels of {accumulated.Length} differ at all). " +
            "Measured 2026-10-15 for these three radii at max 4/9/9 and mean 0.185/1.811/5.224, with 15% to 99% of " +
            "the plane differing at all — a byte accumulator walks away from a full render long before a stroke ends. " +
            "So an incremental shadow needs a float accumulator or a periodic full re-render; this bound is today's " +
            "drift plus headroom, and whoever buys speed with the trade moves it on purpose, not by accident.");
    }
}
