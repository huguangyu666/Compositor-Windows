using System.Text.Json;

namespace Compositor.Core;

public sealed partial class EditorDocument
{
    /// <summary>
    /// The Mac's <c>copyLayers(_:into:at:)</c> (ProjectWorkspace.swift:213-243) seen from the receiving
    /// document: bring a selection of layers — folders with all they hold — into THIS project as one
    /// undoable step. Copies keep their names (no " copy"; this is a move between projects, not a
    /// duplicate in place), their relative positions, and everything the record carries (masks,
    /// adjustments, shapes, text, effects).
    /// Placement follows the original's anchor math: one layer lands with its own center at the drop
    /// point (the canvas center when there is none); several land with the CENTER OF THEIR UNION there,
    /// so the group keeps its shape. A separately placed mask rides the same shift (the Mac moves
    /// <c>mask.placement.origin</c> with the layer); an attached one follows its layer by definition.
    /// A clipped layer whose BASE was not part of the copy comes in as it was SEEN: adjustments just
    /// drop the link (the Mac nils <c>maskSourceID</c> and keeps the record), raster layers are baked
    /// through the source's own clip chain first — <see cref="CanvasCompositor.BakeDeletionMasks"/> is
    /// this port's <c>LiveMaskBaker</c> — so the copy stands alone exactly as it looked.
    /// Budget comes before anything changes (the Mac's megapixel guard, :226-228): this document counts
    /// pixels the way every other write here does, images AND masks together. The whole copy is one
    /// refusal when it does not fit, when the ids are unknown, when the source IS this document (the
    /// Mac's destination == sourceTab early return, :217), or when the resulting hierarchy would not
    /// validate. Returns the old-to-new id map, empty-or-null meaning nothing happened.
    /// </summary>
    public IReadOnlyDictionary<Guid, Guid>? ImportLayersFrom(EditorDocument source, IReadOnlyList<Guid> ids,
        PointD? dropCenter = null)
    {
        if (ReferenceEquals(source, this)) return null;
        // The Mac's guard reads ids.first (ProjectWorkspace.swift:214): a selection whose head the
        // source document does not hold refuses the whole copy, not a partial one without it.
        if (ids.Count == 0 || source.Find(ids[0]) is null) return null;
        var wanted = ids.Distinct().Where(id => source.Find(id) is not null).ToArray();

        // Included = the roots plus everything under each of them (the Mac's descendantIDs union, :224).
        var included = new HashSet<Guid>(wanted);
        foreach (var id in wanted) included.UnionWith(source.Descendants(id));

        // The source's own order, so folders precede their children in the appended block the way
        // they do in the source array (the panel order is a reverse of it; the Mac appends in
        // layers-array order too).
        var copied = source.Snapshot.Manifest.Layers.Where(layer => included.Contains(layer.Id)).ToList();

        // Anchor and shift (Mac :225-227): pictured means non-group layers' own rects; a lone layer
        // (or nothing pictured at all — a folder tree of groups) lands by the first wanted layer's
        // center, several by the union of those rects.
        var bySourceId = copied.ToDictionary(layer => layer.Id);
        var pictured = copied.Where(layer => !layer.IsGroupOrFalse)
            .Select(layer => (Origin: layer.Transform.Origin, Size: layer.Transform.Size)).ToList();
        PointD anchor = ids.Count == 1 || pictured.Count == 0   // the Mac reads ids.count (:226), the requested set
            ? (bySourceId.TryGetValue(wanted[0], out var lone) ? lone.Transform.Center : PointD.Zero)
            : UnionCenter(pictured);
        var center = dropCenter ?? new PointD(Snapshot.Manifest.Width / 2.0, Snapshot.Manifest.Height / 2.0);
        var shift = new PointD(center.X - anchor.X, center.Y - anchor.Y);

        // Budget before any change (Mac :226-228). Bakes keep the same grid sizes, so counting the
        // source surfaces here counts what lands.
        long used = Snapshot.Images.Sum(pair => (long)pair.Value.PixelCount)
            + Snapshot.Masks.Sum(pair => (long)pair.Value.PixelCount);
        long added = copied.Sum(layer => (source.Snapshot.Images.GetValueOrDefault(layer.Id)?.PixelCount ?? 0)
            + (source.Snapshot.Masks.GetValueOrDefault(layer.Id)?.PixelCount ?? 0));
        if (used + added > Limits.DocumentPixelBudget) return null;

        // Clip bases left behind get baked from the SOURCE snapshot (the Mac bakes each such layer
        // through LiveMaskBaker before the record is written, :238-242).
        var needsBake = copied.Where(layer => layer.MaskSourceId is { } baseId && !included.Contains(baseId)
            && layer.Adjustment is null && source.Snapshot.Images.ContainsKey(layer.Id))
            .Select(layer => layer.Id).ToList();
        var baked = needsBake.Count == 0
            ? new Dictionary<Guid, Surface>()
            : CanvasCompositor.BakeDeletionMasks(source.Snapshot, needsBake, CancellationToken.None);

        // Build the candidate against a deserialized copy of THIS manifest (the PrepareDuplication
        // shape): nothing touches the live snapshot until the candidate validates.
        var manifest = ManifestJson.Deserialize(ManifestJson.Serialize(Snapshot.Manifest));
        var images = new Dictionary<Guid, Surface>(Snapshot.Images);
        var masks = new Dictionary<Guid, MaskSurface>(Snapshot.Masks);
        var local = copied.ToDictionary(layer => layer.Id, _ => Guid.NewGuid());

        foreach (var layer in copied)
        {
            var copy = JsonSerializer.Deserialize<ProjectLayerRecord>(
                JsonSerializer.Serialize(layer, MementoOptions), MementoOptions)!;
            copy.Id = local[layer.Id];
            if (copy.ImageFile is not null) copy.ImageFile = ProjectValidator.Uuid(copy.Id) + ".png";
            if (copy.MaskFile is not null) copy.MaskFile = ProjectValidator.Uuid(copy.Id) + ".mask.png";
            if (copy.ParentId is { } parent) copy.ParentId = local.TryGetValue(parent, out var parentCopy) ? parentCopy : null;
            if (copy.MaskSourceId is { } maskSource)
            {
                // The base came along → remap onto the copy's id. It stayed behind → the link is a
                // reference into a document the copy is leaving: adjustments lose it (the Mac nils
                // maskSourceID, :239) and rasters arrive BAKED — their pixels already carry the clip —
                // so both end unlinked with the appearance they were seen in.
                copy.MaskSourceId = local.TryGetValue(maskSource, out var maskCopy) ? maskCopy : null;
            }
            copy.Transform.Origin = new PointD(copy.Transform.Origin.X + shift.X, copy.Transform.Origin.Y + shift.Y);
            if (copy.MaskPlacement is { } placement)
                copy.MaskPlacement = new LayerTransform
                {
                    Origin = new PointD(placement.Origin.X + shift.X, placement.Origin.Y + shift.Y),
                    Size = placement.Size, Rotation = placement.Rotation,
                    FlipX = placement.FlipX, FlipY = placement.FlipY, Sampling = placement.Sampling,
                };
            var image = baked.TryGetValue(layer.Id, out var seen) ? seen
                : source.Snapshot.Images.GetValueOrDefault(layer.Id);
            if (layer.ImageFile is not null && image is { } pixels) images[copy.Id] = pixels;
            if (layer.MaskFile is not null && source.Snapshot.Masks.TryGetValue(layer.Id, out var mask))
                masks[copy.Id] = mask;
            manifest.Layers.Add(copy);   // appended: the new block sits on top, as the Mac's append
        }
        manifest.ActiveLayerId = local[wanted[0]];   // the Mac's activeLayerID = mapping[id] (:235)
        try { ProjectValidator.Validate(manifest); }
        catch (ProjectFormatException) { return null; }

        BeginChange();
        Snapshot = new ProjectSnapshot { Manifest = manifest, Images = images, Masks = masks };
        EndChange();
        return new System.Collections.ObjectModel.ReadOnlyDictionary<Guid, Guid>(local);

        static PointD UnionCenter(List<(PointD Origin, SizeD Size)> rects)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var (origin, size) in rects)
            {
                minX = Math.Min(minX, origin.X); minY = Math.Min(minY, origin.Y);
                maxX = Math.Max(maxX, origin.X + size.Width); maxY = Math.Max(maxY, origin.Y + size.Height);
            }
            return new PointD((minX + maxX) / 2, (minY + maxY) / 2);
        }
    }
}
