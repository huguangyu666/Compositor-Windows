using System.Text.Json;
using System.Text.Json.Serialization;

namespace Compositor.Core;

/// <summary>
/// One layer record in the manifest. Optional fields are null when the macOS app leaves them out,
/// and stay absent when writing back, so round trips preserve the file's own shape.
/// </summary>
public sealed class ProjectLayerRecord : IEquatable<ProjectLayerRecord>
{
    [JsonRequired]
    public Guid Id { get; set; }

    [JsonRequired]
    public string Name { get; set; } = "";

    [JsonRequired]
    public bool IsVisible { get; set; }

    [JsonRequired]
    public LayerTransform Transform { get; set; } = new();

    public string? ImageFile { get; set; }

    public Guid? ParentId { get; set; }

    public bool? IsGroup { get; set; }

    public double? Opacity { get; set; }

    [JsonConverter(typeof(RawStringEnumConverter<LayerBlendMode>))]
    public LayerBlendMode? BlendMode { get; set; }

    public string? MaskFile { get; set; }

    public bool? MaskEnabled { get; set; }

    public Guid? MaskSourceId { get; set; }

    public LayerAdjustment? Adjustment { get; set; }

    /// <summary>A mask moved apart from its layer: where it sits on the document.</summary>
    public LayerTransform? MaskPlacement { get; set; }

    /// <summary>Null (older projects) is linked.</summary>
    public bool? MaskLinked { get; set; }

    /// <summary>A shape layer's style, passed through untouched: the Windows app does not edit shapes yet.</summary>
    public JsonElement? Shape { get; set; }

    /// <summary>Stroke, shadows and glows, passed through untouched until they are rendered.</summary>
    public JsonElement? Effects { get; set; }

    public LayerTextStyle? Text { get; set; }

    [JsonIgnore]
    public double OpacityOrOne => Opacity ?? 1;

    [JsonIgnore]
    public bool IsGroupOrFalse => IsGroup ?? false;

    public bool Equals(ProjectLayerRecord? other) => other is not null
        && Id == other.Id && Name == other.Name && IsVisible == other.IsVisible
        && Transform.Equals(other.Transform) && ImageFile == other.ImageFile
        && ParentId == other.ParentId && IsGroup == other.IsGroup && Opacity == other.Opacity
        && BlendMode == other.BlendMode && MaskFile == other.MaskFile && MaskEnabled == other.MaskEnabled
        && MaskSourceId == other.MaskSourceId
        && (Adjustment?.Equals(other.Adjustment) ?? other.Adjustment is null)
        && (MaskPlacement?.Equals(other.MaskPlacement) ?? other.MaskPlacement is null)
        && MaskLinked == other.MaskLinked
        && JsonElementEquals(Shape, other.Shape) && JsonElementEquals(Effects, other.Effects)
        && (Text?.Equals(other.Text) ?? other.Text is null);

    private static bool JsonElementEquals(JsonElement? left, JsonElement? right)
    {
        if (!left.HasValue) return !right.HasValue;
        return right.HasValue && JsonElement.DeepEquals(left.Value, right.Value);
    }

    public override bool Equals(object? obj) => Equals(obj as ProjectLayerRecord);

    public override int GetHashCode() => Id.GetHashCode();
}

/// <summary>A user-placed alignment line. Horizontal guides sit at a document Y; vertical at a document X.</summary>
public sealed class CanvasGuide : IEquatable<CanvasGuide>
{
    [JsonRequired]
    public Guid Id { get; set; }

    [JsonRequired]
    [JsonConverter(typeof(RawStringEnumConverter<GuideAxis>))]
    public GuideAxis Axis { get; set; }

    [JsonRequired]
    public double Position { get; set; }

    public bool Equals(CanvasGuide? other) => other is not null
        && Id == other.Id && Axis == other.Axis && Position == other.Position;

    public override bool Equals(object? obj) => Equals(obj as CanvasGuide);

    public override int GetHashCode() => HashCode.Combine(Id, Axis, Position);
}

/// <summary>
/// The manifest.json of a .comp package. Versions 1 through <see cref="CurrentVersion"/> load; new
/// saves write <see cref="CurrentVersion"/>. Layers run bottom to top: the last layer draws on top.
/// </summary>
public sealed class ProjectManifest
{
    public const string FormatIdentifier = "com.compositor.project";

    /// <summary>The format version new saves write.</summary>
    public const int CurrentVersion = 11;

    [JsonRequired]
    public string Format { get; set; } = FormatIdentifier;

    [JsonRequired]
    public int Version { get; set; } = CurrentVersion;

    [JsonRequired]
    public string ColorSpace { get; set; } = "sRGB";

    /// <summary>Pixels per inch, 1–9600; older version-1 projects default to 72.</summary>
    public double? Resolution { get; set; }

    [JsonRequired]
    public Guid DocumentId { get; set; }

    [JsonRequired]
    public int Width { get; set; }

    [JsonRequired]
    public int Height { get; set; }

    public Guid? ActiveLayerId { get; set; }

    [JsonRequired]
    public List<ProjectLayerRecord> Layers { get; set; } = new();

    /// <summary>Alignment guides. Missing on versions 1–7.</summary>
    public List<CanvasGuide>? Guides { get; set; }
}
