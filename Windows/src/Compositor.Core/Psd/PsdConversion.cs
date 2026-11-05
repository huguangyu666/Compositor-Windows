namespace Compositor.Core.Psd;

/// <summary>
/// What the Photoshop reader made of a layer, mirroring the Mac's `PSDLayerKind`: the keys a
/// layer carries decide its kind before any record is parsed, and the conversion report reads
/// from that label, exactly as the Mac's builder does.
/// </summary>
public enum PsdLayerKind
{
    Raster,
    Group,
    Adjustment,
    Text,
    SmartObject,
    Effects,
    Vector,
}

/// <summary>
/// One line of the PSD conversion report: which layer lost what. The Mac collects these while
/// building the document and shows them in a confirm sheet before anything is applied; the port
/// collects them during the import and the App surfaces the same list.
/// </summary>
/// <param name="LayerName">The layer's name, as Photoshop wrote it.</param>
/// <param name="Message">What was converted, omitted or skipped.</param>
public sealed record PsdConversion(string LayerName, string Message)
{
    // The Mac's PSDConversion strings, byte-for-byte (its apostrophes are U+2019 except the
    // cropped line, and its blend lines quote the key with U+201C/U+201D). The per-feature notes
    // (warp, faux, first style, rasterize) live on PsdText/PsdVector next to the code deciding
    // them; these are the ones the collector adds.
    public const string CroppedToCanvas =
        "Cropped to the canvas so the file fits in memory. Pixels outside the canvas weren't imported.";
    public const string AdjustmentSkipped = "This adjustment type isn\u2019t supported and was skipped.";
    public const string AdjustmentApproximate = "Adjustment parameters may not match Photoshop exactly.";
    public const string SmartObjectRasterized = "The smart object was rasterized. Linked contents can\u2019t be edited.";
    public const string EffectsDiscarded = "Layer effects were discarded, so the appearance may differ.";
    public const string VectorRasterized = "Vector shape was rasterized to pixels.";

    /// <summary>A folder's unsupported key is reported as written; the Mac does not trim it.</summary>
    public static string FolderBlendUnsupported(string blendKey) =>
        "Folder blend mode \u201c" + blendKey + "\u201d isn\u2019t supported. The folder will be pass-through.";

    /// <summary>Photoshop pads short keys with a space; the Mac trims it before naming the mode.</summary>
    public static string BlendUnsupported(string blendKey) =>
        "Blend mode \u201c" + blendKey.Trim() + "\u201d isn\u2019t supported and will be applied as Normal.";
}

