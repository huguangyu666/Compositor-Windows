namespace Compositor.Core.Psd;

/// <summary>
/// Reads Photoshop's adjustment records — `levl`, `curv` and `hue2`/`hue ` — into live
/// adjustment layers, following the Mac's `PSDAdjustments.parse`: Levels stores its five
/// hundredths-scale values per channel (RGB, then red, green, blue) and Curves stores
/// output-before-input pairs that the port then sorts and caps at the 0/255 ends, exactly as
/// there. Hue/Saturation reads the Colorize switch and its own hue/saturation/lightness, or the
/// Master shift plus the six color ranges with their band edges in degrees. A record this model
/// cannot answer for stays what it was before: the layer is skipped, as the Mac skips it.
/// </summary>
internal static class PsdAdjustment
{
    /// <summary>Whether the layer's extra-info loop should keep this key for the adjustment path.</summary>
    internal static bool TracksKey(string key) => key is "levl" or "curv" or "hue2" or "hue ";

    /// <summary>PSDReader.adjustmentKeys: every key that makes the Mac label a layer an adjustment,
    /// whether or not a port parser reads its parameters.</summary>
    public static readonly HashSet<string> AdjustmentKeys = new(["levl", "curv", "hue2", "hue ",
        "expA", "grdm", "brit", "blnc", "nvrt", "thrs", "post", "mixr", "selc", "blwh", "phfl", "vibA"]);

    /// <summary>`PSDAdjustments.parse`: the first record that answers wins, in the Mac's order.</summary>
    internal static LayerAdjustment? ReadLive(Dictionary<string, byte[]>? extra)
    {
        if (extra is null) return null;
        if (extra.TryGetValue("levl", out var levels))
        {
            var settings = Levels(levels);
            if (settings is not null) return new LayerAdjustment { Kind = AdjustmentKind.Levels, Levels = settings };
        }
        if (extra.TryGetValue("curv", out var curves))
        {
            var settings = Curves(curves);
            if (settings is not null) return new LayerAdjustment { Kind = AdjustmentKind.Curves, Curves = settings };
        }
        if (extra.TryGetValue("hue2", out var hue) || extra.TryGetValue("hue ", out hue))
        {
            var settings = Hue(hue);
            if (settings is not null) return new LayerAdjustment { Kind = AdjustmentKind.HueSaturation, HsvSettings = settings, Colorize = settings.Colorize };
        }
        return null;
    }

    /// <summary>
    /// Photoshop's `levl`: a version, then records of input black, input white, output black,
    /// output white and gamma in hundredths (100 is 1.00), for RGB, then red, green and blue.
    /// </summary>
    private static LevelsSettings? Levels(byte[] data)
    {
        // Photoshop writes the four channel records inside a 292-byte block; the Mac refuses a
        // shorter one, and so a partial or truncated record is skipped rather than guessed.
        if (data.Length < 292) return null;
        var ranges = new List<LevelRange>(4);
        for (int channel = 0; channel < 4; channel++)
        {
            int baseAt = 2 + channel * 10;
            ranges.Add(new LevelRange
            {
                Black = U16(data, baseAt),
                White = U16(data, baseAt + 2),
                OutputBlack = U16(data, baseAt + 4),
                OutputWhite = U16(data, baseAt + 6),
                Gamma = U16(data, baseAt + 8) / 100.0,
            }.Normalized);
        }
        return new LevelsSettings { Ranges = ranges };
    }

    /// <summary>
    /// Photoshop's `curv`: an optional leading flag byte, a version (1 or 4), and up to four
    /// channels of output-before-input point pairs. Two or more points become the channel's
    /// curve, sorted and extended to the 0/255 ends; fewer points leave the channel identity.
    /// </summary>
    private static CurvesSettings? Curves(byte[] data)
    {
        int offset = data.Length > 0 && data[0] == 0 ? 1 : 0;
        if (offset + 4 > data.Length) return null;
        int version = U16(data, offset);
        offset += 2;
        if (version is not (1 or 4)) return null;
        int count = U16(data, offset);
        offset += 2;
        var channels = new List<List<CurvePoint>> { IdentityCurve(), IdentityCurve(), IdentityCurve(), IdentityCurve() };
        for (int channel = 0; channel < Math.Min(4, count); channel++)
        {
            if (offset + 2 > data.Length) break;
            int points = U16(data, offset);
            offset += 2;
            var curve = new List<CurvePoint>(points);
            for (int index = 0; index < points; index++)
            {
                if (offset + 4 > data.Length) break;
                double output = U16(data, offset);
                double input = U16(data, offset + 2);
                offset += 4;
                curve.Add(new CurvePoint(Math.Clamp(input, 0, 255), Math.Clamp(output, 0, 255)));
            }
            if (curve.Count < 2) continue;
            curve.Sort((left, right) => left.X.CompareTo(right.X));
            if (curve[0].X != 0) curve.Insert(0, new CurvePoint(0, curve[0].Y));
            if (curve[^1].X != 255) curve.Add(new CurvePoint(255, curve[^1].Y));
            channels[channel] = curve;
        }
        var settings = new CurvesSettings { Channels = channels };
        return settings.IsValid ? settings : null;
    }

    private static List<CurvePoint> IdentityCurve() => [new CurvePoint(0, 0), new CurvePoint(255, 255)];

    /// <summary>
    /// Photoshop's `hue2`: the Colorize switch, its own hue/saturation/lightness, the Master's,
    /// then for Reds through Magentas the band edges (falloff and range, in degrees) and the
    /// range's shift. Colorize keeps only its values, the way the Mac returns early.
    /// </summary>
    private static HueSaturationSettings? Hue(byte[] data)
    {
        if (data.Length < 16) return null;
        bool colorize = data[2] != 0;
        var settings = new HueSaturationSettings { Colorize = colorize };
        // Colorize carries its own values, at the offset the Master would otherwise use.
        var shift = Range(data, colorize ? 4 : 10);
        if (shift is null) return null;
        settings.Adjustments[nameof(ColorRange.Master)] = shift;
        if (colorize) return settings;
        var ranges = new[] { nameof(ColorRange.Reds), nameof(ColorRange.Yellows), nameof(ColorRange.Greens),
            nameof(ColorRange.Cyans), nameof(ColorRange.Blues), nameof(ColorRange.Magentas) };
        int offset = 16;
        foreach (var range in ranges)
        {
            if (offset + 14 > data.Length) break;
            settings.Bands[range] = new HueBand
            {
                FalloffStart = Degrees(I16(data, offset)),
                RangeStart = Degrees(I16(data, offset + 2)),
                RangeEnd = Degrees(I16(data, offset + 4)),
                FalloffEnd = Degrees(I16(data, offset + 6)),
            };
            var values = Range(data, offset + 8);
            if (values is null) return null;
            settings.Adjustments[range] = values;
            offset += 14;
        }
        return settings;
    }

    private static RangeAdjustment? Range(byte[] data, int offset)
    {
        if (offset + 6 > data.Length) return null;
        // Photoshop's own sliders never leave these ranges, so the clamp is a no-op for real
        // files; it keeps a hand-edited record inside what the model (and the validator) accepts.
        return new RangeAdjustment
        {
            Hue = Math.Clamp((int)I16(data, offset), -360, 360),
            Saturation = Math.Clamp((int)I16(data, offset + 2), -100, 100),
            Lightness = Math.Clamp((int)I16(data, offset + 4), -100, 100),
        };
    }

    /// <summary>Wrap an angle into 0–360, as the Mac normalizes band edges.</summary>
    private static double Degrees(double value)
    {
        double wrapped = value % 360;
        return wrapped < 0 ? wrapped + 360 : wrapped;
    }

    private static ushort U16(byte[] data, int at) => (ushort)((data[at] << 8) | data[at + 1]);

    private static short I16(byte[] data, int at) => unchecked((short)U16(data, at));
}
