using System.Text.Json;
using System.Text.Json.Serialization;

namespace Compositor.Core;

/// <summary>A point in document pixels. Encodes as the JSON array <c>[x, y]</c>, as Swift's CGPoint does.</summary>
public readonly record struct PointD(double X, double Y)
{
    public static readonly PointD Zero = default;
}

/// <summary>A size in document pixels. Encodes as the JSON array <c>[width, height]</c>, as Swift's CGSize does.</summary>
public readonly record struct SizeD(double Width, double Height)
{
    public static readonly SizeD Zero = default;
}

public sealed class PointDArrayConverter : JsonConverter<PointD>
{
    public override PointD Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Expected an [x, y] array.");
        if (!reader.Read() || reader.TokenType != JsonTokenType.Number) throw new JsonException("Expected a number for x.");
        double x = reader.GetDouble();
        if (!reader.Read() || reader.TokenType != JsonTokenType.Number) throw new JsonException("Expected a number for y.");
        double y = reader.GetDouble();
        if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray) throw new JsonException("Expected the array to end after two numbers.");
        return new PointD(x, y);
    }

    public override void Write(Utf8JsonWriter writer, PointD value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.X);
        writer.WriteNumberValue(value.Y);
        writer.WriteEndArray();
    }
}

public sealed class SizeDArrayConverter : JsonConverter<SizeD>
{
    public override SizeD Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Expected a [width, height] array.");
        if (!reader.Read() || reader.TokenType != JsonTokenType.Number) throw new JsonException("Expected a number for width.");
        double width = reader.GetDouble();
        if (!reader.Read() || reader.TokenType != JsonTokenType.Number) throw new JsonException("Expected a number for height.");
        double height = reader.GetDouble();
        if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray) throw new JsonException("Expected the array to end after two numbers.");
        return new SizeD(width, height);
    }

    public override void Write(Utf8JsonWriter writer, SizeD value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.Width);
        writer.WriteNumberValue(value.Height);
        writer.WriteEndArray();
    }
}

/// <summary>Writes GUIDs the way Swift's UUID does: uppercase and hyphenated.</summary>
public sealed class UuidConverter : JsonConverter<Guid>
{
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string text = reader.GetString() ?? throw new JsonException("Expected a UUID string.");
        if (!Guid.TryParse(text, out Guid id)) throw new JsonException($"'{text}' is not a UUID.");
        return id;
    }

    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString("D").ToUpperInvariant());
}
