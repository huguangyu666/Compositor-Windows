namespace Compositor.Core;

/// <summary>The Levels panel's auto buttons (LevelsAutomatic.swift).</summary>
public enum LevelsAutoMode
{
    Contrast,
    Color,
    Neutral,
}

/// <summary>
/// Auto Levels, ported from LevelsAutomatic.swift: endpoints at the 0.1% histogram tails —
/// shared across the channels for Contrast (preserving their relationships), per-channel for
/// Color, and Color plus a neutral-gray gamma for Neutral (clamped to 0.1–9.99, the same
/// range LevelRange.Normalized clamps to).
/// </summary>
public static class LevelsAutomatic
{
    /// <summary>Builds Levels settings from four 256-bin rows: the RGB mean then red, green, blue.</summary>
    public static LevelsSettings Settings(double[][] histogram, LevelsAutoMode mode)
    {
        var result = new LevelsSettings();
        (double, double)? Endpoints(double[] bins)
        {
            double total = bins.Sum();
            if (total <= 0) return null;
            double sum = 0;
            int low = 255;
            for (int i = 0; i < 256; i++)
            {
                sum += bins[i];
                if (sum > total * 0.001) { low = i; break; }
            }
            sum = 0;
            int high = 0;
            for (int i = 255; i >= 0; i--)
            {
                sum += bins[i];
                if (sum > total * 0.001) { high = i; break; }
            }
            return low < high ? ((double)low, (double)high) : null;
        }

        if (mode == LevelsAutoMode.Contrast)
        {
            // A shared interval preserves channel relationships.
            var limits = histogram.Skip(1).Select(Endpoints).Where(limit => limit is not null).Select(limit => limit!.Value).ToList();
            if (limits.Count > 0)
            {
                double low = limits.Min(limit => limit.Item1);
                double high = limits.Max(limit => limit.Item2);
                if (low < high) result.Ranges[0] = new LevelRange { Black = low, White = high };
            }
        }
        else
        {
            for (int channel = 1; channel <= 3; channel++)
            {
                if (Endpoints(histogram[channel]) is not (double low, double high)) continue;
                var range = new LevelRange { Black = low, White = high };
                if (mode == LevelsAutoMode.Neutral)
                {
                    double total = histogram[channel].Sum();
                    double mean = 0;
                    for (int i = 0; i < 256; i++) mean += range.Apply(i / 255.0) * histogram[channel][i];
                    mean /= total;
                    if (mean > 0 && mean < 1) range.Gamma = Math.Min(9.99, Math.Max(0.1, Math.Log(mean) / Math.Log(0.5)));
                }
                result.Ranges[channel] = range.Normalized;
            }
        }
        return result;
    }
}
