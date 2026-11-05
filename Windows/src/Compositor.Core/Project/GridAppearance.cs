namespace Compositor.Core;

/// <summary>
/// How the layout grid is drawn, after the Mac's GridAppearance (Guides.swift:66-119): majors in the chosen
/// color at the chosen opacity, subdivisions dotted and at a little over half that. It is a view setting, so
/// it lives beside <see cref="LayoutGrid"/> and never inside a project manifest.
/// </summary>
public sealed class GridAppearance : IEquatable<GridAppearance>
{
    /// The picker's swatches; Custom is the document's own chosen color (Guides.swift:67-86).
    public enum Preset
    {
        LightGray, LightBlue, LightRed, Green, MediumBlue, Yellow, Magenta, Cyan, Black, Custom
    }

    /// The major lines' pattern; subdivisions stay dotted whatever this says (Guides.swift:91-102).
    public enum LineStyle
    {
        Lines, DashedLines, Dots
    }

    public const int OpacityMin = 1, OpacityMax = 100;

    /// Light Gray, solid, 45% — what the Mac starts with (Guides.swift:107-112).
    public static GridAppearance Default { get; } = new();

    public Preset ColorPreset { get; }
    public AdjustmentColor CustomColor { get; }

    /// Used only while <see cref="ColorPreset"/> is Custom, and kept when another preset is chosen, so
    /// switching back finds the color that was picked (Guides.swift:108-109).
    public LineStyle Style { get; }

    public int Opacity { get; }

    public GridAppearance(Preset colorPreset = Preset.LightGray, AdjustmentColor? customColor = null,
        LineStyle style = LineStyle.Lines, int opacity = 45)
    {
        ColorPreset = colorPreset;
        CustomColor = customColor is { } given && given.IsValid
            ? new AdjustmentColor(given.Red, given.Green, given.Blue)
            : new AdjustmentColor(.7, .7, .7);
        Style = style;
        Opacity = Math.Min(Math.Max(opacity, OpacityMin), OpacityMax);
    }

    /// The color actually in use.
    public AdjustmentColor Color => ColorPreset == Preset.Custom
        ? CustomColor
        : PresetColor(ColorPreset);

    /// The majors' alpha, 0–1 (Guides.swift:115-117).
    public double MajorAlpha => Opacity / 100.0;

    /// Subdivisions at a little over half the majors: 28% beside the default 45% — and it scales with the
    /// opacity rather than being a constant (Guides.swift:118-119).
    public double SubdivisionAlpha => MajorAlpha * 28 / 45;

    /// On and off lengths in screen points, empty for a solid line (Guides.swift:96-101).
    public static double[] DashesFor(LineStyle style) => style switch
    {
        LineStyle.Lines => [],
        LineStyle.DashedLines => [4, 3],
        LineStyle.Dots => [1, 2],
        _ => [],
    };

    public static AdjustmentColor PresetColor(Preset preset) => preset switch
    {
        Preset.LightGray => new AdjustmentColor(.7, .7, .7),
        Preset.LightBlue => new AdjustmentColor(.29, .78, 1),
        Preset.LightRed => new AdjustmentColor(1, .4, .4),
        Preset.Green => new AdjustmentColor(.25, .8, .25),
        Preset.MediumBlue => new AdjustmentColor(.2, .4, 1),
        Preset.Yellow => new AdjustmentColor(1, 1, 0),
        Preset.Magenta => new AdjustmentColor(1, 0, 1),
        Preset.Cyan => new AdjustmentColor(0, 1, 1),
        Preset.Black => new AdjustmentColor(0, 0, 0),
        Preset.Custom => new AdjustmentColor(.7, .7, .7),
        _ => new AdjustmentColor(.7, .7, .7),
    };

    /// The names the Mac stores and shows (Guides.swift:67-70, :92). Round-trips with <see cref="ParsePreset"/>.
    public static string RawName(Preset preset) => preset switch
    {
        Preset.LightGray => "Light Gray", Preset.LightBlue => "Light Blue", Preset.LightRed => "Light Red",
        Preset.Green => "Green", Preset.MediumBlue => "Medium Blue", Preset.Yellow => "Yellow",
        Preset.Magenta => "Magenta", Preset.Cyan => "Cyan", Preset.Black => "Black", Preset.Custom => "Custom",
        _ => "Light Gray",
    };

    public static string RawName(LineStyle style) => style switch
    {
        LineStyle.Lines => "Lines", LineStyle.DashedLines => "Dashed Lines", LineStyle.Dots => "Dots",
        _ => "Lines",
    };

    public static Preset ParsePreset(string? text) => text switch
    {
        "Light Gray" => Preset.LightGray, "Light Blue" => Preset.LightBlue, "Light Red" => Preset.LightRed,
        "Green" => Preset.Green, "Medium Blue" => Preset.MediumBlue, "Yellow" => Preset.Yellow,
        "Magenta" => Preset.Magenta, "Cyan" => Preset.Cyan, "Black" => Preset.Black, "Custom" => Preset.Custom,
        _ => Preset.LightGray,
    };

    public static LineStyle ParseStyle(string? text) => text switch
    {
        "Dashed Lines" => LineStyle.DashedLines, "Dots" => LineStyle.Dots, _ => LineStyle.Lines,
    };

    public bool Equals(GridAppearance? other) => other is not null && ColorPreset == other.ColorPreset
        && CustomColor.Equals(other.CustomColor) && Style == other.Style && Opacity == other.Opacity;

    public override bool Equals(object? obj) => Equals(obj as GridAppearance);
    public override int GetHashCode() => HashCode.Combine(ColorPreset, CustomColor, Style, Opacity);
}
