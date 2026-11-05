namespace Compositor.Renderer;

public readonly record struct RulerTick(double Value, double Position, int Length);
public static class CanvasRuler
{
    public const int Thickness = 18;
    private static readonly double[] nice = [1, 2, 5, 10, 20, 25, 50, 100, 200, 250, 500, 1000, 2000, 2500, 5000, 10000, 20000, 25000];
    // CanvasRulers.swift chooses numbered ticks about 70 logical points apart.
    public static double MajorStep(double pointsPerPixel) => nice.FirstOrDefault(value => value >= 70 / Math.Max(pointsPerPixel, .0001), 50000);
    public static RulerTick[] Ticks(double origin, double length, double pointsPerPixel)
    {
        if (!double.IsFinite(origin) || !double.IsFinite(length) || !double.IsFinite(pointsPerPixel) || pointsPerPixel <= 0 || length <= 0) return [];
        double step = MajorStep(pointsPerPixel), minor = step / 10;
        long first = (long)Math.Floor(-origin / pointsPerPixel / minor);
        long last = (long)Math.Ceiling((length - origin) / pointsPerPixel / minor);
        var ticks = new RulerTick[last - first + 1];
        for (long i = first; i <= last; i++)
        {
            double value = i * minor;
            ticks[i - first] = new(value, origin + value * pointsPerPixel, i % 10 == 0 ? 8 : i % 5 == 0 ? 5 : 3);
        }
        return ticks;
    }
}
