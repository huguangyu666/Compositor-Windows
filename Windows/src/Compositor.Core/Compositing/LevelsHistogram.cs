namespace Compositor.Core;

/// <summary>
/// Levels panel histograms, ported from the macOS levels_histogram (LevelsPixels.c): each
/// pixel's weight is its alpha times the selection coverage, per-channel bins count straight
/// colors, and the RGB row is the mean of the three channel histograms — not a luminance one.
/// </summary>
public static class LevelsHistogram
{
    /// <summary>Four rows of 256 bins: RGB mean, then red, green, blue.</summary>
    public static double[][] Sum(Surface surface, MaskSurface? coverage, CancellationToken cancellation = default)
    {
        var bins = new double[4][];
        for (int row = 0; row < 4; row++) bins[row] = new double[256];
        for (int y = 0; y < surface.Height; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            int row = y * surface.Stride;
            for (int x = 0; x < surface.Width; x++)
            {
                int index = row + x * 4;
                int alpha = surface.Pixels[index + 3];
                if (alpha == 0) continue;
                double weight = alpha / 255.0;
                if (coverage is not null)
                {
                    if (x >= coverage.Width || y >= coverage.Height) continue;
                    weight *= coverage.Pixels[y * coverage.Width + x] / 255.0;
                }
                for (int channel = 0; channel < 3; channel++)
                {
                    int value = Math.Min(255, (int)Math.Round(surface.Pixels[index + channel] * 255.0 / alpha, MidpointRounding.AwayFromZero));
                    bins[channel + 1][value] += weight;
                    bins[0][value] += weight / 3.0;
                }
            }
        }
        return bins;
    }

    /// <summary>LevelsHistogramDisplay.scale: preserve linear ratios while limiting isolated
    /// endpoint spikes to four times the interior's 95th percentile.</summary>
    public static double DisplayScale(double[] bins)
    {
        double peak = bins.Where(value => double.IsFinite(value) && value > 0).DefaultIfEmpty(0).Max();
        if (peak == 0) return 0;
        var interior = bins.Skip(1).Take(Math.Max(0, bins.Length - 2))
            .Where(value => double.IsFinite(value) && value > 0).Order().ToArray();
        return interior.Length == 0 ? peak : Math.Min(peak, interior[(int)((interior.Length - 1) * .95)] * 4);
    }
}
