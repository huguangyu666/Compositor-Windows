using System.Text;

namespace Compositor.Core;

/// <summary>
/// Every rule a manifest must satisfy before it can load or save, mirroring the macOS
/// ProjectStore: version gates, per-layer appearance gates, the folder hierarchy and the
/// live (clipping) mask graph, guides, and the naming of image assets.
/// </summary>
public static class ProjectValidator
{
    public static void Validate(ProjectManifest manifest)
    {
        if (manifest.Format != ProjectManifest.FormatIdentifier) throw new ProjectFormatException(ProjectErrorKind.Invalid);
        if (manifest.Version is < 1 or > ProjectManifest.CurrentVersion)
            throw new ProjectFormatException(ProjectErrorKind.Version, manifest.Version);
        if (manifest.ColorSpace != "sRGB") throw new ProjectFormatException(ProjectErrorKind.Invalid);
        if (manifest.Resolution is { } resolution
            && (!double.IsFinite(resolution) || resolution is < 1 or > 9600))
            throw new ProjectFormatException(ProjectErrorKind.Invalid);
        if (manifest.Width is < 1 or > Limits.MaxSide || manifest.Height is < 1 or > Limits.MaxSide
            || manifest.Layers.Count > Limits.MaxLayers)
            throw new ProjectFormatException(ProjectErrorKind.TooLarge);

        foreach (var layer in manifest.Layers)
        {
            if (layer.Text is { } text)
            {
                // Per-letter colors arrived in version 10, per-letter faces in version 11.
                if (!text.IsValid
                    || (layer.Text.ColorRuns != null && manifest.Version < 10)
                    || (layer.Text.FontRuns != null && manifest.Version < 11)
                    || layer.ImageFile is null || layer.IsGroupOrFalse || layer.Adjustment is not null)
                    throw new ProjectFormatException(ProjectErrorKind.Invalid);
            }
            if (layer.Adjustment is { } adjustment)
            {
                if (manifest.Version < 7 || layer.IsGroupOrFalse || layer.ImageFile is not null || !adjustment.IsValid)
                    throw new ProjectFormatException(ProjectErrorKind.Invalid);
                if (adjustment.Kind is AdjustmentKind.GaussianBlur or AdjustmentKind.MotionBlur or AdjustmentKind.AddNoise
                    && manifest.Version < 9)
                    throw new ProjectFormatException(ProjectErrorKind.Invalid);
            }
            // Layer masks arrived in version 4, folder masks in version 6.
            if (layer.MaskFile is { } maskFile)
            {
                int maskedVersion = layer.IsGroupOrFalse ? 6 : 4;
                if (manifest.Version < maskedVersion || maskFile != $"{Uuid(layer.Id)}.mask.png")
                    throw new ProjectFormatException(ProjectErrorKind.Invalid);
            }
            if (layer.MaskEnabled is not null && layer.MaskFile is null)
                throw new ProjectFormatException(ProjectErrorKind.Invalid);
            if (layer.MaskPlacement is { } placement && (!placement.IsValid || layer.MaskFile is null))
                throw new ProjectFormatException(ProjectErrorKind.Invalid);

            double opacity = layer.OpacityOrOne;
            var blend = layer.BlendMode ?? LayerBlendMode.Normal;
            // Folders took an opacity of their own in version 8, which multiplies into what is inside
            // them; their blend mode is still pass-through, so it stays Normal.
            if (!double.IsFinite(opacity) || opacity is < 0 or > 1) throw new ProjectFormatException(ProjectErrorKind.Invalid);
            if (manifest.Version < 3 && !(opacity == 1 && blend == LayerBlendMode.Normal))
                throw new ProjectFormatException(ProjectErrorKind.Invalid);
            if (layer.IsGroupOrFalse && !(blend == LayerBlendMode.Normal && (manifest.Version >= 8 || opacity == 1)))
                throw new ProjectFormatException(ProjectErrorKind.Invalid);
        }

        ValidateHierarchy(manifest.Layers);
        ValidateLiveMaskGraph(manifest.Layers);
        if (manifest.Version < 5 && manifest.Layers.Any(layer => layer.MaskSourceId is not null))
            throw new ProjectFormatException(ProjectErrorKind.Invalid);
        if (manifest.Version == 1 && manifest.Layers.Any(layer => layer.ParentId is not null || layer.IsGroup == true))
            throw new ProjectFormatException(ProjectErrorKind.Invalid);

        var ids = new HashSet<Guid>();
        foreach (var layer in manifest.Layers)
        {
            if (!ids.Add(layer.Id)) throw new ProjectFormatException(ProjectErrorKind.Invalid);
            if (!layer.Transform.IsValid) throw new ProjectFormatException(ProjectErrorKind.Invalid);
            if (layer.Name.Trim().Length == 0) throw new ProjectFormatException(ProjectErrorKind.Invalid);
            if (Encoding.UTF8.GetByteCount(layer.Name) > 16_384) throw new ProjectFormatException(ProjectErrorKind.Invalid);
            if (layer.ImageFile is { } imageFile && imageFile != $"{Uuid(layer.Id)}.png")
                throw new ProjectFormatException(ProjectErrorKind.Invalid);
        }
        if (manifest.ActiveLayerId is { } active && !ids.Contains(active))
            throw new ProjectFormatException(ProjectErrorKind.Invalid);

        ValidateGuides(manifest);
        if (manifest.Timeline is { } timeline && (manifest.Version < 12 || !timeline.IsValidFor(manifest.Layers)))
            throw new ProjectFormatException(ProjectErrorKind.Invalid);
    }

    private static void ValidateGuides(ProjectManifest manifest)
    {
        if (manifest.Guides is not { } guides) return;
        if (manifest.Version < 8)
        {
            // An empty guides array on an old manifest is tolerated; entries are not.
            if (guides.Count != 0) throw new ProjectFormatException(ProjectErrorKind.Invalid);
            return;
        }
        if (guides.Count > 1_000) throw new ProjectFormatException(ProjectErrorKind.TooLarge);
        var ids = new HashSet<Guid>();
        foreach (var guide in guides)
        {
            if (!ids.Add(guide.Id) || !double.IsFinite(guide.Position) || Math.Abs(guide.Position) > 1_000_000)
                throw new ProjectFormatException(ProjectErrorKind.Invalid);
        }
    }

    /// <summary>The macOS LayerHierarchy.validate: unique ids, imageless folders, acyclic group parents.</summary>
    private static void ValidateHierarchy(List<ProjectLayerRecord> layers)
    {
        var byId = new Dictionary<Guid, ProjectLayerRecord>();
        foreach (var layer in layers)
        {
            if (!byId.TryAdd(layer.Id, layer)) throw new ProjectFormatException(ProjectErrorKind.Invalid);
            if (layer.IsGroupOrFalse && layer.ImageFile is not null) throw new ProjectFormatException(ProjectErrorKind.Invalid);
        }
        foreach (var layer in layers)
        {
            var seen = new HashSet<Guid> { layer.Id };
            Guid? parent = layer.ParentId;
            while (parent is { } id)
            {
                if (seen.Count > 64 || !seen.Add(id) || !byId.TryGetValue(id, out var node) || !node.IsGroupOrFalse)
                    throw new ProjectFormatException(ProjectErrorKind.Invalid);
                parent = node.ParentId;
            }
            if (layer.IsGroupOrFalse && seen.Count > 64) throw new ProjectFormatException(ProjectErrorKind.Invalid);
        }
    }

    /// <summary>
    /// The macOS LiveMaskGraph.validate: every clipping source exists, is a plain pixel layer
    /// (no groups, no adjustments), and the chains neither repeat nor run past 256 nodes.
    /// </summary>
    private static void ValidateLiveMaskGraph(List<ProjectLayerRecord> layers)
    {
        var records = new Dictionary<Guid, ProjectLayerRecord>();
        foreach (var layer in layers)
        {
            if (!records.TryAdd(layer.Id, layer)) throw new ProjectFormatException(ProjectErrorKind.Invalid);
        }
        foreach (var layer in layers)
        {
            var path = new HashSet<Guid>();
            Guid? current = layer.Id;
            while (current is { } id)
            {
                if (path.Count >= 256 || !path.Add(id) || !records.TryGetValue(id, out var record))
                    throw new ProjectFormatException(ProjectErrorKind.Invalid);
                if (record.MaskSourceId is { } source)
                {
                    if (record.IsGroupOrFalse || !records.ContainsKey(source)
                        || records[source].IsGroupOrFalse || records[source].Adjustment is not null)
                        throw new ProjectFormatException(ProjectErrorKind.Invalid);
                }
                current = record.MaskSourceId;
            }
        }
    }

    /// <summary>The macOS UUID string: uppercase, hyphenated — the form image files are named after.</summary>
    public static string Uuid(Guid id) => id.ToString("D").ToUpperInvariant();

    /// <summary>Whether the layers form a valid live-mask graph (no cycles, raster sources) —
    /// the gate a clipping-mask link shares with the validator.</summary>
    public static bool LiveMaskGraphIsValid(List<ProjectLayerRecord> layers)
    {
        try
        {
            ValidateLiveMaskGraph(layers);
            return true;
        }
        catch (ProjectFormatException)
        {
            return false;
        }
    }

}
