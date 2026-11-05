using System.Text.Json.Serialization;

namespace Compositor.Core;

/// <summary>A straight sRGB color stored with an adjustment, 0–1 per channel.</summary>
public sealed class AdjustmentColor : IEquatable<AdjustmentColor>
{
    [JsonRequired]
    public double Red { get; set; }

    [JsonRequired]
    public double Green { get; set; }

    [JsonRequired]
    public double Blue { get; set; }

    public AdjustmentColor() { }

    public AdjustmentColor(double red, double green, double blue)
    {
        Red = red;
        Green = green;
        Blue = blue;
    }

    [JsonIgnore]
    public bool IsValid => double.IsFinite(Red) && Red is >= 0 and <= 1
        && double.IsFinite(Green) && Green is >= 0 and <= 1
        && double.IsFinite(Blue) && Blue is >= 0 and <= 1;

    public bool Equals(AdjustmentColor? other) => other is not null
        && Red == other.Red && Green == other.Green && Blue == other.Blue;

    public override bool Equals(object? obj) => Equals(obj as AdjustmentColor);

    public override int GetHashCode() => HashCode.Combine(Red, Green, Blue);
}

/// <summary>Photoshop's Exposure: exposure in stops, an offset in linear light, then gamma.</summary>
public sealed class ExposureSettings : IEquatable<ExposureSettings>
{
    public const double ExposureMin = -20, ExposureMax = 20;
    public const double OffsetMin = -0.5, OffsetMax = 0.5;
    public const double GammaMin = 0.01, GammaMax = 9.99;

    [JsonRequired]
    public double Exposure { get; set; }

    [JsonRequired]
    public double Offset { get; set; }

    [JsonRequired]
    public double Gamma { get; set; } = 1;

    [JsonIgnore]
    public bool IsValid =>
        double.IsFinite(Exposure) && Exposure >= ExposureMin && Exposure <= ExposureMax
        && double.IsFinite(Offset) && Offset >= OffsetMin && Offset <= OffsetMax
        && double.IsFinite(Gamma) && Gamma >= GammaMin && Gamma <= GammaMax;

    public bool Equals(ExposureSettings? other) => other is not null
        && Exposure == other.Exposure && Offset == other.Offset && Gamma == other.Gamma;

    public override bool Equals(object? obj) => Equals(obj as ExposureSettings);

    public override int GetHashCode() => HashCode.Combine(Exposure, Offset, Gamma);
}

/// <summary>Gradient Map: each pixel's brightness picks a color between shadows and highlights.</summary>
public sealed class GradientMapSettings : IEquatable<GradientMapSettings>
{
    [JsonRequired]
    public AdjustmentColor Shadows { get; set; } = new(0, 0, 0);

    [JsonRequired]
    public AdjustmentColor Highlights { get; set; } = new(1, 1, 1);

    [JsonRequired]
    public bool Reversed { get; set; }

    [JsonIgnore]
    public bool IsValid => Shadows.IsValid && Highlights.IsValid;

    public bool Equals(GradientMapSettings? other) => other is not null
        && Shadows.Equals(other.Shadows) && Highlights.Equals(other.Highlights) && Reversed == other.Reversed;

    public override bool Equals(object? obj) => Equals(obj as GradientMapSettings);

    public override int GetHashCode() => HashCode.Combine(Shadows, Highlights, Reversed);
}

/// <summary>Photoshop's Black &amp; White: a brightness weight per color family, plus an optional tint.</summary>
public sealed class BlackWhiteSettings : IEquatable<BlackWhiteSettings>
{
    public const double Min = -200, Max = 300;

    [JsonRequired]
    public double Reds { get; set; } = 40;

    [JsonRequired]
    public double Yellows { get; set; } = 60;

    [JsonRequired]
    public double Greens { get; set; } = 40;

    [JsonRequired]
    public double Cyans { get; set; } = 60;

    [JsonRequired]
    public double Blues { get; set; } = 20;

    [JsonRequired]
    public double Magentas { get; set; } = 80;

    [JsonRequired]
    public bool Tint { get; set; }

    [JsonRequired]
    public double TintHue { get; set; }

    [JsonRequired]
    public double TintSaturation { get; set; }

    [JsonIgnore]
    public bool IsValid =>
        double.IsFinite(Reds) && Reds >= Min && Reds <= Max
        && double.IsFinite(Yellows) && Yellows >= Min && Yellows <= Max
        && double.IsFinite(Greens) && Greens >= Min && Greens <= Max
        && double.IsFinite(Cyans) && Cyans >= Min && Cyans <= Max
        && double.IsFinite(Blues) && Blues >= Min && Blues <= Max
        && double.IsFinite(Magentas) && Magentas >= Min && Magentas <= Max
        && double.IsFinite(TintHue) && TintHue is >= 0 and <= 360
        && double.IsFinite(TintSaturation) && TintSaturation is >= 0 and <= 100;

    public bool Equals(BlackWhiteSettings? other) => other is not null
        && Reds == other.Reds && Yellows == other.Yellows && Greens == other.Greens
        && Cyans == other.Cyans && Blues == other.Blues && Magentas == other.Magentas
        && Tint == other.Tint && TintHue == other.TintHue && TintSaturation == other.TintSaturation;

    public override bool Equals(object? obj) => Equals(obj as BlackWhiteSettings);

    public override int GetHashCode() => HashCode.Combine(Reds, Yellows, Greens, Cyans, Blues, Magentas, Tint, TintHue);
}

/// <summary>Color Balance: shifts toward one end of each opposing pair, per tonal range.</summary>
public sealed class ColorBalanceSettings : IEquatable<ColorBalanceSettings>
{
    public const double Min = -100, Max = 100;

    [JsonRequired]
    public double ShadowCyanRed { get; set; }

    [JsonRequired]
    public double ShadowMagentaGreen { get; set; }

    [JsonRequired]
    public double ShadowYellowBlue { get; set; }

    [JsonRequired]
    public double MidCyanRed { get; set; }

    [JsonRequired]
    public double MidMagentaGreen { get; set; }

    [JsonRequired]
    public double MidYellowBlue { get; set; }

    [JsonRequired]
    public double HighlightCyanRed { get; set; }

    [JsonRequired]
    public double HighlightMagentaGreen { get; set; }

    [JsonRequired]
    public double HighlightYellowBlue { get; set; }

    [JsonRequired]
    public bool PreserveLuminosity { get; set; } = true;

    [JsonIgnore]
    public bool IsValid =>
        InRange(ShadowCyanRed) && InRange(ShadowMagentaGreen) && InRange(ShadowYellowBlue)
        && InRange(MidCyanRed) && InRange(MidMagentaGreen) && InRange(MidYellowBlue)
        && InRange(HighlightCyanRed) && InRange(HighlightMagentaGreen) && InRange(HighlightYellowBlue);

    [JsonIgnore]
    public bool IsIdentity =>
        ShadowCyanRed == 0 && ShadowMagentaGreen == 0 && ShadowYellowBlue == 0
        && MidCyanRed == 0 && MidMagentaGreen == 0 && MidYellowBlue == 0
        && HighlightCyanRed == 0 && HighlightMagentaGreen == 0 && HighlightYellowBlue == 0;

    public bool Equals(ColorBalanceSettings? other) => other is not null
        && ShadowCyanRed == other.ShadowCyanRed && ShadowMagentaGreen == other.ShadowMagentaGreen
        && ShadowYellowBlue == other.ShadowYellowBlue && MidCyanRed == other.MidCyanRed
        && MidMagentaGreen == other.MidMagentaGreen && MidYellowBlue == other.MidYellowBlue
        && HighlightCyanRed == other.HighlightCyanRed && HighlightMagentaGreen == other.HighlightMagentaGreen
        && HighlightYellowBlue == other.HighlightYellowBlue && PreserveLuminosity == other.PreserveLuminosity;

    public override bool Equals(object? obj) => Equals(obj as ColorBalanceSettings);

    public override int GetHashCode() => HashCode.Combine(
        ShadowCyanRed, ShadowMagentaGreen, ShadowYellowBlue,
        MidCyanRed, MidMagentaGreen, MidYellowBlue,
        HighlightCyanRed, HighlightMagentaGreen);

    private static bool InRange(double value) => double.IsFinite(value) && value >= Min && value <= Max;
}

/// <summary>Film grain: brightness noise, strongest in the midtones, fixed by seed.</summary>
public sealed class GrainSettings : IEquatable<GrainSettings>
{
    public const double AmountMin = 0, AmountMax = 100;
    public const double SizeMin = 0.5, SizeMax = 20;
    public const double RoughnessMin = 0, RoughnessMax = 100;

    [JsonRequired]
    public double Amount { get; set; } = 25;

    [JsonRequired]
    public double Size { get; set; } = 1.5;

    [JsonRequired]
    public double Roughness { get; set; } = 50;

    [JsonRequired]
    public uint Seed { get; set; }

    [JsonIgnore]
    public bool IsValid =>
        double.IsFinite(Amount) && Amount >= AmountMin && Amount <= AmountMax
        && double.IsFinite(Size) && Size >= SizeMin && Size <= SizeMax
        && double.IsFinite(Roughness) && Roughness >= RoughnessMin && Roughness <= RoughnessMax;

    public bool Equals(GrainSettings? other) => other is not null
        && Amount == other.Amount && Size == other.Size && Roughness == other.Roughness && Seed == other.Seed;

    public override bool Equals(object? obj) => Equals(obj as GrainSettings);

    public override int GetHashCode() => HashCode.Combine(Amount, Size, Roughness, Seed);
}

public sealed class LevelRange : IEquatable<LevelRange>
{
    [JsonRequired]
    public double Black { get; set; }

    [JsonRequired]
    public double Gamma { get; set; } = 1;

    [JsonRequired]
    public double White { get; set; } = 255;

    [JsonRequired]
    public double OutputBlack { get; set; }

    [JsonRequired]
    public double OutputWhite { get; set; } = 255;

    /// <summary>The same values clamped into their allowed ranges.</summary>
    [JsonIgnore]
    public LevelRange Normalized
    {
        get
        {
            double black = Clamp(Black, 0, 254, 0);
            double white = Clamp(White, Math.Max(0, black) + 1, 255, 255);
            return new LevelRange
            {
                Black = black,
                White = white,
                Gamma = Clamp(Gamma, 0.1, 9.99, 1),
                OutputBlack = Clamp(OutputBlack, 0, 255, 0),
                OutputWhite = Clamp(OutputWhite, 0, 255, 255),
            };
        }
    }

    /// <summary>
    /// The macOS LevelRange.apply: value runs 0–1, the black/white points are read on the
    /// 0–255 scale, and the result comes back normalized to 0–1.
    /// </summary>
    public double Apply(double value)
    {
        var s = Normalized;
        double input = Math.Clamp((value * 255 - s.Black) / (s.White - s.Black), 0, 1);
        return (s.OutputBlack + Math.Pow(input, 1 / s.Gamma) * (s.OutputWhite - s.OutputBlack)) / 255;
    }

    public bool Equals(LevelRange? other) => other is not null
        && Black == other.Black && Gamma == other.Gamma && White == other.White
        && OutputBlack == other.OutputBlack && OutputWhite == other.OutputWhite;

    public override bool Equals(object? obj) => Equals(obj as LevelRange);

    public override int GetHashCode() => HashCode.Combine(Black, Gamma, White, OutputBlack, OutputWhite);

    private static double Clamp(double value, double lower, double upper, double fallback) =>
        double.IsFinite(value) ? Math.Min(upper, Math.Max(lower, value)) : fallback;
}

public sealed class LevelsSettings : IEquatable<LevelsSettings>
{
    [JsonRequired]
    [JsonConverter(typeof(RawStringEnumConverter<LevelsChannel>))]
    public LevelsChannel Channel { get; set; }

    [JsonRequired]
    public List<LevelRange> Ranges { get; set; } = new()
    {
        new LevelRange(), new LevelRange(), new LevelRange(), new LevelRange(),
    };

    /// <summary>The macOS isIdentity: every range equals its default.</summary>
    [JsonIgnore]
    public bool IsIdentity => Ranges.All(range => range.Normalized.Equals(new LevelRange()));

    [JsonIgnore]
    public bool IsValid => Ranges.Count == 4 && Ranges.All(range => range.Equals(range.Normalized));

    public bool Equals(LevelsSettings? other) => other is not null
        && Channel == other.Channel
        && Ranges.Count == other.Ranges.Count
        && Ranges.Zip(other.Ranges).All(pair => pair.First.Equals(pair.Second));

    public override bool Equals(object? obj) => Equals(obj as LevelsSettings);

    public override int GetHashCode() => HashCode.Combine(Channel, Ranges.Count);
}

public readonly record struct CurvePoint(double X, double Y);

public sealed class CurvesSettings : IEquatable<CurvesSettings>
{
    [JsonRequired]
    [JsonConverter(typeof(RawStringEnumConverter<LevelsChannel>))]
    public LevelsChannel Channel { get; set; }

    [JsonRequired]
    public List<List<CurvePoint>> Channels { get; set; } = new()
    {
        new List<CurvePoint> { new(0, 0), new(255, 255) },
        new List<CurvePoint> { new(0, 0), new(255, 255) },
        new List<CurvePoint> { new(0, 0), new(255, 255) },
        new List<CurvePoint> { new(0, 0), new(255, 255) },
    };

    /// <summary>
    /// The macOS CurvesSettings.value: shape-preserving cubic Hermite interpolation between
    /// the channel's points, clamped to 0–255.
    /// </summary>
    public double Value(double x, int channel)
    {
        var points = Channels[Math.Clamp(channel, 0, 3)];
        // Fewer than two points has no curve; the Mac's point() leaves the input unchanged.
        if (points.Count < 2) return x;
        int i = Math.Min(points.Count - 2, Math.Max(0, LastIndexAtOrBefore(points, x)));
        var slopes = new double[points.Count - 1];
        for (int k = 0; k < slopes.Length; k++) slopes[k] = (points[k + 1].Y - points[k].Y) / (points[k + 1].X - points[k].X);
        double Slope(int j) =>
            j == 0 ? slopes[0]
            : j == points.Count - 1 ? slopes[^1]
            : slopes[j - 1] * slopes[j] <= 0 ? 0
            : 2 / (1 / slopes[j - 1] + 1 / slopes[j]);
        double h = points[i + 1].X - points[i].X;
        double t = Math.Clamp((x - points[i].X) / h, 0, 1);
        double y = (2 * t * t * t - 3 * t * t + 1) * points[i].Y
            + (t * t * t - 2 * t * t + t) * h * Slope(i)
            + (-2 * t * t * t + 3 * t * t) * points[i + 1].Y
            + (t * t * t - t * t) * h * Slope(i + 1);
        return Math.Clamp(y, 0, 255);
    }

    private static int LastIndexAtOrBefore(List<CurvePoint> points, double x)
    {
        for (int k = points.Count - 1; k >= 0; k--)
        {
            if (points[k].X <= x) return k;
        }
        return 0;
    }

    [JsonIgnore]
    public bool IsValid => Channels.Count == 4 && Channels.All(points =>
        points.Count is >= 2 and <= 32
        && points[0].X == 0 && points[^1].X == 255
        && points.All(point => double.IsFinite(point.X) && double.IsFinite(point.Y) && point.X is >= 0 and <= 255 && point.Y is >= 0 and <= 255)
        && points.Zip(points.Skip(1)).All(pair => pair.First.X < pair.Second.X));

    public bool Equals(CurvesSettings? other) => other is not null
        && Channel == other.Channel
        && Channels.Count == other.Channels.Count
        && Channels.Zip(other.Channels).All(pair =>
            pair.First.Count == pair.Second.Count
            && pair.First.Zip(pair.Second).All(points => points.First == points.Second));

    public override bool Equals(object? obj) => Equals(obj as CurvesSettings);

    public override int GetHashCode() => HashCode.Combine(Channel, Channels.Count);
}

/// <summary>One color range's Hue/Saturation/Lightness shift.</summary>
public sealed class RangeAdjustment : IEquatable<RangeAdjustment>
{
    [JsonRequired]
    public double Hue { get; set; }

    [JsonRequired]
    public double Saturation { get; set; }

    [JsonRequired]
    public double Lightness { get; set; }

    [JsonIgnore]
    public bool IsValid =>
        double.IsFinite(Hue) && Math.Abs(Hue) <= 360
        && double.IsFinite(Saturation) && Math.Abs(Saturation) <= 100
        && double.IsFinite(Lightness) && Math.Abs(Lightness) <= 100;

    public bool Equals(RangeAdjustment? other) => other is not null
        && Hue == other.Hue && Saturation == other.Saturation && Lightness == other.Lightness;

    public override bool Equals(object? obj) => Equals(obj as RangeAdjustment);

    public override int GetHashCode() => HashCode.Combine(Hue, Saturation, Lightness);
}

/// <summary>The hue falloff of one color range: ramp in, plateau, ramp out, in degrees 0–360 with wraparound.</summary>
public sealed class HueBand : IEquatable<HueBand>
{
    [JsonRequired]
    public double FalloffStart { get; set; }

    [JsonRequired]
    public double RangeStart { get; set; }

    [JsonRequired]
    public double RangeEnd { get; set; }

    [JsonRequired]
    public double FalloffEnd { get; set; }

    [JsonIgnore]
    public bool IsValid =>
        double.IsFinite(FalloffStart) && double.IsFinite(RangeStart) && double.IsFinite(RangeEnd) && double.IsFinite(FalloffEnd);

    /// <summary>Degrees from `from` forward to `to`, always 0–360.</summary>
    public static double Forward(double from, double to)
    {
        double delta = (to - from) % 360;
        return delta < 0 ? delta + 360 : delta;
    }

    private static double Wrap(double value)
    {
        double remainder = value % 360;
        return remainder < 0 ? remainder + 360 : remainder;
    }

    /// <summary>The macOS normalize: all four values wrap into 0–360, and the band is capped
    /// just under a full circle so falloff shoulders keep their order.</summary>
    public HueBand Normalized()
    {
        var result = new HueBand
        {
            FalloffStart = Wrap(FalloffStart),
            RangeStart = Wrap(RangeStart),
            RangeEnd = Wrap(RangeEnd),
            FalloffEnd = Wrap(FalloffEnd),
        };
        if (Forward(result.FalloffStart, result.FalloffEnd) > 350)
        {
            result.FalloffEnd = Wrap(result.FalloffStart + 350);
        }
        return result;
    }

    /// <summary>The macOS centered(on:): a band of the same core and shoulder widths,
    /// centered on `hue` (wraparound preserved).</summary>
    public HueBand CenteredOn(double hue)
    {
        double core = Forward(RangeStart, RangeEnd);
        double leading = Forward(FalloffStart, RangeStart);
        double trailing = Forward(RangeEnd, FalloffEnd);
        double start = Wrap(hue - core / 2);
        return new HueBand
        {
            FalloffStart = Wrap(start - leading),
            RangeStart = start,
            RangeEnd = Wrap(start + core),
            FalloffEnd = Wrap(start + core + trailing),
        };
    }

    /// <summary>
    /// The macOS setHandle(_:to:): move one handle, keeping the four in order and the band
    /// under a full circle. Invalid geometries return the unchanged band.
    /// </summary>
    public HueBand WithHandle(int index, double degrees)
    {
        var updated = new HueBand
        {
            FalloffStart = FalloffStart,
            RangeStart = RangeStart,
            RangeEnd = RangeEnd,
            FalloffEnd = FalloffEnd,
        };
        double value = ((degrees % 360) + 360) % 360;
        switch (index)
        {
            case 0: updated.FalloffStart = value; break;
            case 1: updated.RangeStart = value; break;
            case 2: updated.RangeEnd = value; break;
            default: updated.FalloffEnd = value; break;
        }
        double span = Forward(updated.FalloffStart, updated.FalloffEnd);
        double toStart = Forward(updated.FalloffStart, updated.RangeStart);
        double toEnd = Forward(updated.FalloffStart, updated.RangeEnd);
        if (span <= 1 || span > 350 || toStart > toEnd || toEnd > span) return this;
        return updated;
    }

    /// <summary>
    /// The macOS include(_:): widens the band so this hue is fully inside it, moving
    /// whichever edge is nearer. Returns the updated band (HueBand is immutable here).
    /// </summary>
    public HueBand Including(double hue)
    {
        if (WeightOf(hue) >= 1) return this;
        double shoulderIn = Forward(FalloffStart, RangeStart);
        double shoulderOut = Forward(RangeEnd, FalloffEnd);
        double beforeStart = Forward(hue, RangeStart);
        double afterEnd = Forward(RangeEnd, hue);
        var updated = new HueBand
        {
            FalloffStart = FalloffStart,
            RangeStart = RangeStart,
            RangeEnd = RangeEnd,
            FalloffEnd = FalloffEnd,
        };
        if (beforeStart <= afterEnd)
        {
            updated.RangeStart = hue;
            updated.FalloffStart = hue - shoulderIn;
        }
        else
        {
            updated.RangeEnd = hue;
            updated.FalloffEnd = hue + shoulderOut;
        }
        return updated.Normalized();
    }

    /// <summary>
    /// How strongly this band claims a hue: 1 inside the range, ramping linearly through
    /// each falloff shoulder, 0 outside. Wraparound is handled by measuring forward.
    /// </summary>
    public double WeightOf(double hue)
    {
        double span = Forward(FalloffStart, FalloffEnd);
        if (span <= 0) return 1; // Master covers everything.
        double position = Forward(FalloffStart, hue);
        if (position > span) return 0;
        double rampIn = Forward(FalloffStart, RangeStart);
        double plateauEnd = Forward(FalloffStart, RangeEnd);
        if (position < rampIn) return rampIn > 0 ? position / rampIn : 1;
        if (position <= plateauEnd) return 1;
        double rampOut = span - plateauEnd;
        return rampOut > 0 ? (span - position) / rampOut : 1;
    }

    public bool Equals(HueBand? other) => other is not null
        && FalloffStart == other.FalloffStart && RangeStart == other.RangeStart
        && RangeEnd == other.RangeEnd && FalloffEnd == other.FalloffEnd;

    public override bool Equals(object? obj) => Equals(obj as HueBand);

    public override int GetHashCode() => HashCode.Combine(FalloffStart, RangeStart, RangeEnd, FalloffEnd);
}

/// <summary>Hue/Saturation with per-range adjustments and band handles.</summary>
public sealed class HueSaturationSettings : IEquatable<HueSaturationSettings>
{
    [JsonRequired]
    [JsonConverter(typeof(RawStringEnumConverter<ColorRange>))]
    public ColorRange Range { get; set; }

    [JsonRequired]
    public bool Colorize { get; set; }

    [JsonRequired]
    public bool InvertRange { get; set; }

    [JsonRequired]
    public Dictionary<string, RangeAdjustment> Adjustments { get; set; } = new();

    [JsonRequired]
    public Dictionary<string, HueBand> Bands { get; set; } = new();

    public HueSaturationSettings() { }

    /// <summary>The settings the adjustment's own scalar sliders describe, as the macOS app resolves them.</summary>
    public static HueSaturationSettings FromScalars(double hue, double saturation, double lightness, bool colorize) =>
        new()
        {
            Range = ColorRange.Master,
            Colorize = colorize,
            Adjustments = new Dictionary<string, RangeAdjustment>
            {
                [nameof(ColorRange.Master)] = new RangeAdjustment { Hue = hue, Saturation = saturation, Lightness = lightness },
            },
        };

    /// <summary>
    /// How much a range applies to one hue: Master everywhere, others through their band,
    /// inverted when the range asks for it (the macOS weight(of:hue:)).
    /// </summary>
    public double WeightOf(ColorRange colorRange, double hue)
    {
        if (colorRange == ColorRange.Master) return 1;
        string key = RawEnumText.Resolve(nameof(ColorRange), Enum.GetName(colorRange)!);
        var band = Bands.GetValueOrDefault(key) ?? ColorRangeDefaults.Band(colorRange);
        double weight = band.WeightOf(hue);
        return InvertRange && colorRange == Range ? 1 - weight : weight;
    }

    // The sliders read and write the selected range, exactly as the macOS computed properties do.

    [JsonIgnore]
    public double Hue
    {
        get => Adjustments.GetValueOrDefault(RangeKey)?.Hue ?? 0;
        set => Adjustments[RangeKey] = new RangeAdjustment { Hue = value, Saturation = Saturation, Lightness = Lightness };
    }

    [JsonIgnore]
    public double Saturation
    {
        get => Adjustments.GetValueOrDefault(RangeKey)?.Saturation ?? 0;
        set => Adjustments[RangeKey] = new RangeAdjustment { Hue = Hue, Saturation = value, Lightness = Lightness };
    }

    [JsonIgnore]
    public double Lightness
    {
        get => Adjustments.GetValueOrDefault(RangeKey)?.Lightness ?? 0;
        set => Adjustments[RangeKey] = new RangeAdjustment { Hue = Hue, Saturation = Saturation, Lightness = value };
    }

    private string RangeKey => RawEnumText.Resolve(nameof(ColorRange), Enum.GetName(Range)!);

    /// <summary>Whether every dictionary key is a color range the macOS app can decode.</summary>
    [JsonIgnore]
    public bool HasKnownKeys =>
        Adjustments.Keys.All(IsColorRange) && Bands.Keys.All(IsColorRange);

    public bool Equals(HueSaturationSettings? other)
    {
        if (other is null || Range != other.Range || Colorize != other.Colorize || InvertRange != other.InvertRange) return false;
        if (Adjustments.Count != other.Adjustments.Count || Bands.Count != other.Bands.Count) return false;
        foreach (var (key, value) in Adjustments)
        {
            if (!other.Adjustments.TryGetValue(key, out var theirs) || !value.Equals(theirs)) return false;
        }
        foreach (var (key, value) in Bands)
        {
            if (!other.Bands.TryGetValue(key, out var theirs) || !value.Equals(theirs)) return false;
        }
        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as HueSaturationSettings);

    public override int GetHashCode() => HashCode.Combine(Range, Colorize, InvertRange, Adjustments.Count, Bands.Count);

    private static bool IsColorRange(string key) =>
        Enum.GetNames<ColorRange>().Any(name => string.Equals(RawEnumText.Resolve(nameof(ColorRange), name), key, StringComparison.Ordinal));
}

/// <summary>
/// An adjustment layer's settings. The record always carries every kind's settings, each defaulting
/// to an identity adjustment; the optional blocks stay absent until that kind has been edited.
/// </summary>
public sealed class LayerAdjustment : IEquatable<LayerAdjustment>
{
    [JsonRequired]
    [JsonConverter(typeof(RawStringEnumConverter<AdjustmentKind>))]
    public AdjustmentKind Kind { get; set; }

    [JsonRequired]
    public double Hue { get; set; }

    [JsonRequired]
    public double Saturation { get; set; }

    [JsonRequired]
    public double Lightness { get; set; }

    [JsonRequired]
    public bool Colorize { get; set; }

    /// <summary>
    /// Bumped whenever the settings are edited in place. Render caches fold it into their
    /// validity fingerprints — the settings object reference never changes on edit.
    /// </summary>
    [JsonIgnore]
    public int Revision { get; private set; }

    public void MarkModified() => Revision++;

    public HueSaturationSettings? HsvSettings { get; set; }

    [JsonRequired]
    public LevelsSettings Levels { get; set; } = new();

    [JsonRequired]
    public CurvesSettings Curves { get; set; } = new();

    public ExposureSettings? ExposureSettings { get; set; }

    public GradientMapSettings? GradientMapSettings { get; set; }

    public GrainSettings? GrainSettings { get; set; }

    public BlackWhiteSettings? BlackWhiteSettings { get; set; }

    public ColorBalanceSettings? ColorBalanceSettings { get; set; }

    public double? BlurRadius { get; set; }

    public double? MotionAngle { get; set; }

    public double? MotionDistance { get; set; }

    public double? NoiseAmount { get; set; }

    public bool? NoiseGaussian { get; set; }

    public bool? NoiseMonochromatic { get; set; }

    public uint? NoiseSeed { get; set; }

    [JsonIgnore]
    public HueSaturationSettings ResolvedHsv =>
        HsvSettings ?? HueSaturationSettings.FromScalars(Hue, Saturation, Lightness, Colorize);

    [JsonIgnore]
    public double GaussianRadius => BlurRadius ?? 10;

    [JsonIgnore]
    public double ResolvedMotionAngle => MotionAngle ?? 0;

    [JsonIgnore]
    public double ResolvedMotionDistance => MotionDistance ?? 10;

    [JsonIgnore]
    public double ResolvedNoiseAmount => NoiseAmount ?? 10;

    /// <summary>The same check as the macOS LayerAdjustment.isValid.</summary>
    [JsonIgnore]
    public bool IsValid
    {
        get
        {
            if (!(double.IsFinite(Hue) && Math.Abs(Hue) <= 360
                && double.IsFinite(Saturation) && Math.Abs(Saturation) <= 100
                && double.IsFinite(Lightness) && Math.Abs(Lightness) <= 100)) return false;
            var hsv = ResolvedHsv;
            if (!hsv.HasKnownKeys) return false;
            if (!hsv.Adjustments.Values.All(value => value.IsValid)) return false;
            if (!hsv.Bands.Values.All(band => band.IsValid)) return false;
            if (!Levels.IsValid || !Curves.IsValid) return false;
            if (!(ExposureSettings ?? new ExposureSettings()).IsValid) return false;
            if (!(GradientMapSettings ?? new GradientMapSettings()).IsValid) return false;
            if (!(GrainSettings ?? new GrainSettings()).IsValid) return false;
            if (!(BlackWhiteSettings ?? new BlackWhiteSettings()).IsValid) return false;
            if (!(ColorBalanceSettings ?? new ColorBalanceSettings()).IsValid) return false;
            if (!(double.IsFinite(GaussianRadius) && GaussianRadius >= 0.1 && GaussianRadius <= 250)) return false;
            if (!(double.IsFinite(ResolvedMotionAngle) && ResolvedMotionAngle >= -90 && ResolvedMotionAngle <= 90)) return false;
            if (!(double.IsFinite(ResolvedMotionDistance) && ResolvedMotionDistance >= 1 && ResolvedMotionDistance <= 2000)) return false;
            if (!(double.IsFinite(ResolvedNoiseAmount) && ResolvedNoiseAmount >= 0.1 && ResolvedNoiseAmount <= 400)) return false;
            return true;
        }
    }

    public bool Equals(LayerAdjustment? other)
    {
        if (other is null || Kind != other.Kind || Hue != other.Hue || Saturation != other.Saturation
            || Lightness != other.Lightness || Colorize != other.Colorize) return false;
        if (!Same(HsvSettings, other.HsvSettings) || !Levels.Equals(other.Levels) || !Curves.Equals(other.Curves)) return false;
        if (!Same(ExposureSettings, other.ExposureSettings) || !Same(GradientMapSettings, other.GradientMapSettings)
            || !Same(GrainSettings, other.GrainSettings) || !Same(BlackWhiteSettings, other.BlackWhiteSettings)
            || !Same(ColorBalanceSettings, other.ColorBalanceSettings)) return false;
        return BlurRadius == other.BlurRadius && MotionAngle == other.MotionAngle
            && MotionDistance == other.MotionDistance && NoiseAmount == other.NoiseAmount
            && NoiseGaussian == other.NoiseGaussian && NoiseMonochromatic == other.NoiseMonochromatic
            && NoiseSeed == other.NoiseSeed;
    }

    private static bool Same<T>(T? left, T? right) where T : class, IEquatable<T> =>
        left is not null ? left.Equals(right) : right is null;

    public override bool Equals(object? obj) => Equals(obj as LayerAdjustment);

    public override int GetHashCode() => HashCode.Combine(Kind, Hue, Saturation, Lightness, Colorize);
}
