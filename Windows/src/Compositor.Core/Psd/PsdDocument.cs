using Compositor.Core;

namespace Compositor.Core.Psd;

/// <summary>
/// One imported layer: premultiplied RGBA pixels plus its document offset. Group records
/// carry no pixels; their members point at them through <see cref="ParentId"/>. Ids pair a
/// section divider with its folder record, as on the Mac.
/// </summary>
public sealed class PsdLayer
{
    public required Guid Id { get; init; }

    public Guid? ParentId { get; init; }

    public bool IsGroup { get; init; }

    public required string Name { get; init; }

    public Surface? Pixels { get; init; }

    public required PointD Offset { get; init; }

    public bool Hidden { get; init; }

    /// <summary>The layer's opacity × fill, as the macOS assemble computes it.</summary>
    public double Opacity { get; init; } = 1;

    /// <summary>The Mac's PSDLayerKind label: which key family the layer carries, decided before
    /// any record is parsed, and what the conversion report reads.</summary>
    public PsdLayerKind Kind { get; init; } = PsdLayerKind.Raster;

    /// <summary>The unmodified four-byte Photoshop key, retained for conversion diagnostics.</summary>
    public string BlendKey { get; init; } = "norm";

    /// <summary>PSDTypes.swift's supported blend modes; folders remain pass-through.</summary>
    public LayerBlendMode BlendMode => IsGroup ? LayerBlendMode.Normal : BlendKey switch
    {
        "mul " => LayerBlendMode.Multiply, "scrn" => LayerBlendMode.Screen,
        "over" => LayerBlendMode.Overlay, "sLit" => LayerBlendMode.SoftLight,
        "dark" => LayerBlendMode.Darken, "lite" => LayerBlendMode.Lighten,
        "diff" => LayerBlendMode.Difference, "div " => LayerBlendMode.ColorDodge,
        "idiv" => LayerBlendMode.ColorBurn, "hue " => LayerBlendMode.Hue,
        "sat " => LayerBlendMode.Saturation, "colr" => LayerBlendMode.Color,
        "lum " => LayerBlendMode.Luminosity, "lbrn" => LayerBlendMode.LinearBurn,
        "lddg" => LayerBlendMode.LinearDodge, "hLit" => LayerBlendMode.HardLight,
        "vLit" => LayerBlendMode.VividLight, "lLit" => LayerBlendMode.LinearLight,
        "pLit" => LayerBlendMode.PinLight, "hMix" => LayerBlendMode.HardMix,
        "smud" => LayerBlendMode.Exclusion, "fsub" => LayerBlendMode.Subtract,
        "fdiv" => LayerBlendMode.Divide, _ => LayerBlendMode.Normal,
    };

    /// <summary>Unsupported modes use the Mac's Normal/pass-through fallback and must be reported.</summary>
    public bool HasUnsupportedBlendMode => IsGroup ? BlendKey is not ("norm" or "pass")
        : BlendKey is not ("norm" or "pass") && BlendMode == LayerBlendMode.Normal;

    /// <summary>The user mask on its placement grid: the decoded patch over the default.</summary>
    public MaskSurface? Mask { get; init; }

    /// <summary>Null preserves legacy callers supplying a canvas-sized mask.</summary>
    public LayerTransform? MaskPlacement { get; init; }

    public bool MaskLinked { get; init; }

    public bool CroppedToCanvas { get; init; }

    /// <summary>The live shape style read by PsdVector, when the layer is a fill record.</summary>
    public ShapeStyle? Shape { get; init; }

    /// <summary>A parsed adjustment record (`levl`/`curv`/`hue2`): live, and it carries no pixels.</summary>
    public LayerAdjustment? Adjustment { get; init; }

    /// <summary>What the shape conversion had to omit; the importer reports these as on the Mac.</summary>
    public IReadOnlyList<string>? ShapeNotes { get; init; }

    /// <summary>The editable text record a `TySh` layer parsed into; the importer re-renders it
    /// and keeps Photoshop's own pixels only when that render does not fit, as the Mac's builder does.</summary>
    public LayerTextStyle? Text { get; init; }

    /// <summary>What the text conversion had to omit, including the rasterize caveat.</summary>
    public IReadOnlyList<string>? TextNotes { get; init; }

    /// <summary>The record's document anchor and matrix, so the importer re-renders the style
    /// and places it where the descriptor said, exactly as the Mac's builder does.</summary>
    public PointD TextAnchor { get; init; }

    public double TextRotation { get; init; }

    public bool TextFlipY { get; init; }

    public bool MaskEnabled { get; init; } = true;
}

/// <summary>A parsed PSD document: canvas size plus its layers, bottom to top.</summary>
public sealed class PsdDocument
{
    public required int Width { get; init; }

    public required int Height { get; init; }

    public required IReadOnlyList<PsdLayer> Layers { get; init; }

    /// <summary>The reader used the merged image because there were no usable layer records.</summary>
    public bool IsCompositeFallback { get; init; }

    /// <summary>Conversion lines the reader itself had to record: an adjustment-keyed layer whose
    /// record does not parse is skipped here, and the Mac's builder still reports it. The rest of
    /// the report is collected by the import, which is where the render outcome is known.</summary>
    public List<PsdConversion>? ReaderConversions { get; set; }
}
