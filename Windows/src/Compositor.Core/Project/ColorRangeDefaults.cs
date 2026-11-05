namespace Compositor.Core;

/// <summary>The starting hue band for each color range, as Photoshop lays them out.</summary>
public static class ColorRangeDefaults
{
    /// <summary>Human-readable names for the band picker (macOS panel labels).</summary>
    public static string DisplayName(ColorRange range) => range switch
    {
        ColorRange.Master => "Master",
        ColorRange.Reds => "Reds",
        ColorRange.Yellows => "Yellows",
        ColorRange.Greens => "Greens",
        ColorRange.Cyans => "Cyans",
        ColorRange.Blues => "Blues",
        ColorRange.Magentas => "Magentas",
        _ => range.ToString(),
    };

    public static HueBand Band(ColorRange range) => range switch
    {
        ColorRange.Reds => new HueBand { FalloffStart = 315, RangeStart = 345, RangeEnd = 15, FalloffEnd = 45 },
        ColorRange.Yellows => new HueBand { FalloffStart = 15, RangeStart = 45, RangeEnd = 75, FalloffEnd = 105 },
        ColorRange.Greens => new HueBand { FalloffStart = 75, RangeStart = 105, RangeEnd = 135, FalloffEnd = 165 },
        ColorRange.Cyans => new HueBand { FalloffStart = 135, RangeStart = 165, RangeEnd = 195, FalloffEnd = 225 },
        ColorRange.Blues => new HueBand { FalloffStart = 195, RangeStart = 225, RangeEnd = 255, FalloffEnd = 285 },
        ColorRange.Magentas => new HueBand { FalloffStart = 255, RangeStart = 285, RangeEnd = 315, FalloffEnd = 345 },
        _ => new HueBand { FalloffStart = 0, RangeStart = 0, RangeEnd = 360, FalloffEnd = 360 },
    };
}
