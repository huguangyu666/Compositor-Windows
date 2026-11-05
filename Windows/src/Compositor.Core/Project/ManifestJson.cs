using System.Text.Json;
using System.Text.Json.Serialization;

namespace Compositor.Core;

/// <summary>
/// JSON settings matching the macOS encoder: exact property names, nulls omitted (Swift's
/// encodeIfPresent), pretty-printed with two-space indent, GUIDs uppercase hyphenated, and
/// strict enum strings.
/// </summary>
public static class ManifestJson
{
    public static JsonSerializerOptions Options { get; } = Create(writeIndented: true);

    public static JsonSerializerOptions CompactOptions { get; } = Create(writeIndented: false);

    private static JsonSerializerOptions Create(bool writeIndented)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = writeIndented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = CompositorNamingPolicy.Camel,
            PropertyNameCaseInsensitive = false,
            NumberHandling = JsonNumberHandling.Strict,
            ReadCommentHandling = JsonCommentHandling.Disallow,
        };
        options.Converters.Add(new PointDArrayConverter());
        options.Converters.Add(new SizeDArrayConverter());
        options.Converters.Add(new UuidConverter());
        options.Converters.Add(new RawStringEnumConverter<LayerSampling>());
        options.Converters.Add(new RawStringEnumConverter<LayerBlendMode>());
        options.Converters.Add(new RawStringEnumConverter<GuideAxis>());
        options.Converters.Add(new RawStringEnumConverter<TextAlignment>());
        options.Converters.Add(new RawStringEnumConverter<LevelsChannel>());
        options.Converters.Add(new RawStringEnumConverter<ColorRange>());
        options.Converters.Add(new RawStringEnumConverter<AdjustmentKind>());
        return options;
    }

    /// <summary>
    /// The macOS encoder's camelCase, with one correction: it keeps "ID" uppercase
    /// (documentID, activeLayerID, maskSourceID), as Swift's property names do.
    /// </summary>
    public sealed class CompositorNamingPolicy : JsonNamingPolicy
    {
        public static readonly CompositorNamingPolicy Camel = new();

        public override string ConvertName(string name)
        {
            if (name.Length == 0) return name;
            if (name == "Id") return "id";
            string camel = char.ToLowerInvariant(name[0]) + name[1..];
            return camel.EndsWith("Id", StringComparison.Ordinal) ? camel[..^2] + "ID" : camel;
        }
    }

    /// <summary>A detached copy of a text style, through the same JSON shape the manifest saves it in, so a
    /// property added to the model later cannot be missed by whoever copies a style to change one word of it.</summary>
    public static LayerTextStyle Clone(LayerTextStyle style) =>
        JsonSerializer.Deserialize<LayerTextStyle>(JsonSerializer.Serialize(style, Options), Options)!;

    public static string Serialize(ProjectManifest manifest) =>
        JsonSerializer.Serialize(manifest, Options);

    public static byte[] SerializeToUtf8(ProjectManifest manifest) =>
        JsonSerializer.SerializeToUtf8Bytes(manifest, Options);

    public static ProjectManifest Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ProjectManifest>(json, Options)
                ?? throw new ProjectFormatException(ProjectErrorKind.Invalid);
        }
        catch (JsonException ex)
        {
            throw new ProjectFormatException(ProjectErrorKind.Invalid, innerException: ex);
        }
    }
}
