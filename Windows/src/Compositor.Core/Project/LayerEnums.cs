using System.Text.Json;
using System.Text.Json.Serialization;

namespace Compositor.Core;

public enum LayerSampling
{
    Nearest,
    Smooth,
    HighQuality,
}

public enum LayerBlendMode
{
    Normal,
    Darken,
    Multiply,
    ColorBurn,
    LinearBurn,
    Lighten,
    Screen,
    ColorDodge,
    LinearDodge,
    Overlay,
    SoftLight,
    HardLight,
    VividLight,
    LinearLight,
    PinLight,
    HardMix,
    Difference,
    Exclusion,
    Subtract,
    Divide,
    Hue,
    Saturation,
    Color,
    Luminosity,
}

public enum GuideAxis
{
    Horizontal,
    Vertical,
}

public enum TextAlignment
{
    Left,
    Center,
    Right,
}

public enum LevelsChannel
{
    Rgb,
    Red,
    Green,
    Blue,
}

public enum ColorRange
{
    Master,
    Reds,
    Yellows,
    Greens,
    Cyans,
    Blues,
    Magentas,
}

public enum AdjustmentKind
{
    HueSaturation,
    Levels,
    Curves,
    Exposure,
    GradientMap,
    Grain,
    AddNoise,
    GaussianBlur,
    MotionBlur,
    Invert,
    BlackWhite,
    ColorBalance,
}

/// <summary>
/// Maps manifest enums to the exact strings the macOS app writes and reads. Matching is
/// case-sensitive and integer values are refused. Only guides also accept the exact capitalized
/// strings saved by earlier Windows builds; writing always uses the macOS raw values.
/// </summary>
public sealed class RawStringEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    private static readonly (string Text, T Value)[] Cases = BuildCases();

    private static (string, T)[] BuildCases()
    {
        var names = Enum.GetNames<T>();
        var values = Enum.GetValues<T>();
        var result = new (string, T)[names.Length];
        for (int i = 0; i < names.Length; i++) result[i] = (RawEnumText.Resolve<T>(names[i]), values[i]);
        return result;
    }

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string text = reader.GetString() ?? throw new JsonException($"Expected a string for {typeof(T).Name}.");
        foreach (var (known, value) in Cases)
        {
            if (string.Equals(known, text, StringComparison.Ordinal)) return value;
        }
        // Earlier Windows builds wrote these two C# names instead of the Mac raw values.
        // Recover those projects without relaxing any other enum or accepting arbitrary casing.
        if (typeof(T) == typeof(GuideAxis))
        {
            if (text == "Horizontal") return (T)(object)GuideAxis.Horizontal;
            if (text == "Vertical") return (T)(object)GuideAxis.Vertical;
        }
        throw new JsonException($"'{text}' is not a {typeof(T).Name} value.");
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        foreach (var (known, candidate) in Cases)
        {
            if (EqualityComparer<T>.Default.Equals(candidate, value))
            {
                writer.WriteStringValue(known);
                return;
            }
        }
        throw new JsonException($"{value} is not a valid {typeof(T).Name}.");
    }
}
