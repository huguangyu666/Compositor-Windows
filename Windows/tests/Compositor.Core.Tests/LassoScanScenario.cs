namespace Compositor.Core.Tests;

internal static class LassoScanScenario
{
    public static IEnumerable<PointD[]> Fixtures()
    {
        var random = new Random(3127);
        for (int fixture = 0; fixture < 64; fixture++)
        {
            int count = 3 + random.Next(120);
            var points = Enumerable.Range(0, count).Select(_ => new PointD(random.NextDouble() * 100 - 20,
                random.NextDouble() * 90 - 15)).ToArray();
            if (fixture % 4 == 0) points = points.Select(p => new PointD(Math.Round(p.X), Math.Round(p.Y))).ToArray();
            yield return points;
        }
        yield return [new(1, .125), new(24, .125), new(24, 32.875), new(1, 32.875)];
        yield return [new(1, .375), new(24, .375), new(24, 32.625), new(1, 32.625)];
        yield return [new(-5, 12), new(30, 12), new(30, 12), new(20, 40), new(15, 12), new(10, 40)];
        yield return [new(-10, -10), new(74, 75), new(-10, 75), new(74, -10)];
        yield return [new(0, 0), new(64, 0), new(64, 65), new(0, 65), new(0, 0), new(64, 0), new(64, 65), new(0, 65)];
        yield return [new(.49, .49), new(.51, .49), new(.51, .51), new(.49, .51)];
        yield return [new(90, 90), new(100, 90), new(100, 100)];
        yield return [new(1, 1), new(1, 1), new(1, 1)];
    }
}
