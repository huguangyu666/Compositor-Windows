namespace Compositor.Core;

public sealed class HealingResult
{
    internal HealingResult(HealingStroke stroke, ProjectSnapshot snapshot) { Stroke = stroke; Snapshot = snapshot; }
    internal HealingStroke Stroke { get; }
    public ProjectSnapshot Snapshot { get; }
}

/// <summary>Session-only healing coverage. The dark preview never becomes the source of repair.
/// Grid growth and the search margin follow BrushStroke.swift's init and heal.</summary>
public sealed class HealingStroke
{
    internal readonly ProjectSnapshot Source;
    internal readonly string Manifest;
    internal readonly Selection? Selection;
    private readonly Dictionary<Guid, (Surface Surface, int Revision)> images;
    private readonly Dictionary<Guid, (MaskSurface Surface, int Revision)> masks;
    private readonly Guid id;
    private readonly Surface original, preview;
    private readonly MaskSurface zeroCoverage;
    private readonly MaskSurface repairCoverage;
    private readonly TileStroke stroke;
    private readonly LayerTransform transform;
    private readonly RectI sourceRect;
    private readonly double opacity;
    private readonly SpotHealingMode mode;
    private bool sealedStroke;

    private HealingStroke(ProjectSnapshot source, Guid id, BrushSettings brush, SpotHealingMode mode,
        Selection? selection, Surface original, LayerTransform transform, RectI sourceRect)
    {
        Source = source; this.id = id; Selection = selection;
        Manifest = ManifestJson.Serialize(source.Manifest);
        images = source.Images.ToDictionary(pair => pair.Key, pair => (pair.Value, pair.Value.Revision));
        masks = source.Masks.ToDictionary(pair => pair.Key, pair => (pair.Value, pair.Value.Revision));
        this.original = original; this.transform = transform; this.sourceRect = sourceRect;
        opacity = brush.Opacity; this.mode = mode;
        preview = original.Clone();
        Coverage = new MaskSurface(original.Width, original.Height);
        zeroCoverage = new MaskSurface(original.Width, original.Height);
        repairCoverage = new MaskSurface(original.Width, original.Height);
        stroke = new TileStroke(brush with { Red = 255, Green = 255, Blue = 255, Opacity = 1 }, transform, original.Width, original.Height);
        PreviewSnapshot = MakeSnapshot(preview, new(0, 0, original.Width, original.Height), retireMetadata: false);
    }

    public Guid LayerId => id;
    public MaskSurface Coverage { get; }
    public ProjectSnapshot PreviewSnapshot { get; }

    internal bool SourcePixelsUnchanged() => images.Count == Source.Images.Count && masks.Count == Source.Masks.Count
        && images.All(pair => Source.Images.TryGetValue(pair.Key, out var image)
            && ReferenceEquals(image, pair.Value.Surface) && image.Revision == pair.Value.Revision)
        && masks.All(pair => Source.Masks.TryGetValue(pair.Key, out var mask)
            && ReferenceEquals(mask, pair.Value.Surface) && mask.Revision == pair.Value.Revision);

    internal static HealingStroke? Create(ProjectSnapshot source, Guid id, BrushSettings brush, SpotHealingMode mode, Selection? selection, bool onMask)
    {
        var layer = source.Manifest.Layers.FirstOrDefault(layer => layer.Id == id);
        if (onMask || layer is null || layer.IsGroupOrFalse || layer.Adjustment is not null || !layer.Transform.IsValid
            || !DrawOrder.Resolve(source.Manifest.Layers).Visible.Contains(id) || !source.Images.TryGetValue(id, out var image)
            || selection is { Bounds.Width: <= 0 } || selection is { Bounds.Height: <= 0 }
            || !double.IsFinite(brush.Radius) || brush.Radius is <= 0 or > 1050 || !double.IsFinite(brush.Hardness)
            || brush.Hardness is < 0 or > 1 || !double.IsFinite(brush.Opacity) || brush.Opacity is <= 0 or > 1 || !Enum.IsDefined(mode)) return null;
        var placed = layer.Transform;
        var corners = new[] { new PointD(0, 0), new PointD(source.Manifest.Width, 0),
            new PointD(0, source.Manifest.Height), new PointD(source.Manifest.Width, source.Manifest.Height) }
            .Select(p => BrushEngine.DocumentToPixel(placed, image.Width, image.Height, p)).ToArray();
        double left = Math.Floor(Math.Min(0, corners.Min(p => p.X)) + 1e-8);
        double top = Math.Floor(Math.Min(0, corners.Min(p => p.Y)) + 1e-8);
        double width = Math.Ceiling(Math.Max(image.Width, corners.Max(p => p.X)) - 1e-8) - left;
        double height = Math.Ceiling(Math.Max(image.Height, corners.Max(p => p.Y)) - 1e-8) - top;
        if (width is < 1 or > Limits.MaxSide || height is < 1 or > Limits.MaxSide || width * height > Limits.MaxSurfacePixels) return null;
        long used = source.Images.Sum(pair => pair.Key == id ? 0 : pair.Value.PixelCount) + source.Masks.Sum(pair => pair.Value.PixelCount);
        if (layer.MaskPlacement is null && source.Masks.TryGetValue(id, out var mask)) used += (long)(width * height) - mask.PixelCount;
        if (used + width * height > Limits.DocumentPixelBudget) return null;
        int w = (int)width, h = (int)height, x = -(int)left, y = -(int)top;
        var center = BrushEngine.PixelToDocument(placed, image.Width, image.Height, new(left + width / 2, top + height / 2));
        var size = new SizeD(w * placed.Size.Width / image.Width, h * placed.Size.Height / image.Height);
        var transform = new LayerTransform { Origin = new(center.X - size.Width / 2, center.Y - size.Height / 2), Size = size,
            Rotation = placed.Rotation, FlipX = placed.FlipX, FlipY = placed.FlipY, Sampling = placed.Sampling };
        if (!transform.IsValid) return null;
        var original = x == 0 && y == 0 && w == image.Width && h == image.Height ? image : new Surface(w, h);
        if (!ReferenceEquals(original, image))
            for (int row = 0; row < image.Height; row++)
                image.Pixels.AsSpan(row * image.Stride, image.Width * 4).CopyTo(original.Pixels.AsSpan((y + row) * original.Stride + x * 4));
        return new(source, id, brush, mode, selection, original, transform, new(x, y, image.Width, image.Height));
    }

    public RectI Append(PointD point)
    {
        if (sealedStroke) throw new InvalidOperationException("This healing stroke has already ended.");
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) throw new ArgumentOutOfRangeException(nameof(point));
        stroke.RetireTail();
        var touched = stroke.SweepSegment(point);
        int x1 = touched.X + touched.Width, y1 = touched.Y + touched.Height;
        // As in BrushStroke.swift, search with raw brush coverage. Selection clips
        // the preview and the repaired result, without changing the patch-search ring.
        stroke.PaintMaskRegion(repairCoverage, zeroCoverage, touched.X, touched.Y, x1, y1, null, false);
        stroke.PaintMaskRegion(Coverage, zeroCoverage, touched.X, touched.Y, x1, y1, Selection, false);
        for (int y = touched.Y; y < y1; y++)
        for (int x = touched.X; x < x1; x++)
        {
            var doc = BrushEngine.PixelToDocument(transform, original.Width, original.Height, new(x + .5, y + .5));
            int p = y * Coverage.Width + x;
            if (doc.X < 0 || doc.Y < 0 || doc.X >= PreviewSnapshot.Manifest.Width || doc.Y >= PreviewSnapshot.Manifest.Height) Coverage.Pixels[p] = 0;
            double alpha = Coverage.Pixels[p] / 255d * .45;
            int i = y * original.Stride + x * 4, target = y * preview.Stride + x * 4;
            for (int c = 0; c < 3; c++) preview.Pixels[target + c] = (byte)Math.Round(original.Pixels[i + c] * (1 - alpha) + 30.6 * alpha, MidpointRounding.AwayFromZero);
            preview.Pixels[target + 3] = (byte)Math.Round(original.Pixels[i + 3] * (1 - alpha) + 255 * alpha, MidpointRounding.AwayFromZero);
        }
        preview.MarkModified(touched);
        return touched;
    }

    public HealingResult? Render(uint seed, CancellationToken cancellationToken = default)
    {
        sealedStroke = true;
        cancellationToken.ThrowIfCancellationRequested();
        var painted = SpotHealingEngine.CoverageBounds(Coverage, cancellationToken);
        if (painted.Width == 0) return null;
        var raw = SpotHealingEngine.CoverageBounds(repairCoverage, cancellationToken);
        double reach = (Math.Max(raw.Width, raw.Height) + 32) * 3.2;
        int x0 = Math.Max(0, (int)Math.Floor(raw.X - reach)), y0 = Math.Max(0, (int)Math.Floor(raw.Y - reach));
        int x1 = Math.Min(original.Width, (int)Math.Ceiling(raw.X + raw.Width + reach));
        int y1 = Math.Min(original.Height, (int)Math.Ceiling(raw.Y + raw.Height + reach));
        var region = original.Crop(x0, y0, x1 - x0, y1 - y0);
        var coverage = new MaskSurface(region.Width, region.Height);
        for (int y = 0; y < region.Height; y++)
            repairCoverage.Pixels.AsSpan((y0 + y) * repairCoverage.Width + x0, region.Width).CopyTo(coverage.Pixels.AsSpan(y * region.Width));
        var healed = SpotHealingEngine.Heal(region, coverage, opacity, mode, seed, cancellationToken: cancellationToken);
        if (healed is null) return null;
        // Keep the original asset plus the touched area, rather than persisting a canvas-sized
        // empty border just because the working grid expanded to cover it.
        var crop = RectI.Union(sourceRect, painted);
        var result = original.Crop(crop.X, crop.Y, crop.Width, crop.Height);
        bool changed = false;
        for (int y = painted.Y; y < painted.Y + painted.Height; y++)
        for (int x = painted.X; x < painted.X + painted.Width; x++)
        {
            if (Coverage.Pixels[y * Coverage.Width + x] == 0) continue;
            var doc = BrushEngine.PixelToDocument(transform, original.Width, original.Height, new(x + .5, y + .5));
            double amount = Selection?.CoverageAt((int)doc.X, (int)doc.Y) / 255d ?? 1;
            int sourceIndex = y * original.Stride + x * 4;
            int healedIndex = (y - y0) * healed.Stride + (x - x0) * 4;
            int resultIndex = (y - crop.Y) * result.Stride + (x - crop.X) * 4;
            for (int c = 0; c < 4; c++)
            {
                byte value = (byte)Math.Round(original.Pixels[sourceIndex + c]
                    + (healed.Pixels[healedIndex + c] - original.Pixels[sourceIndex + c]) * amount, MidpointRounding.AwayFromZero);
                result.Pixels[resultIndex + c] = value;
                changed |= value != original.Pixels[sourceIndex + c];
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return changed ? new(this, MakeSnapshot(result, crop, retireMetadata: true)) : null;
    }

    private ProjectSnapshot MakeSnapshot(Surface image, RectI crop, bool retireMetadata)
    {
        var manifest = ManifestJson.Deserialize(Manifest);
        var layer = manifest.Layers.First(layer => layer.Id == id);
        var oldTransform = layer.Transform;
        var center = BrushEngine.PixelToDocument(transform, original.Width, original.Height, new(crop.X + crop.Width / 2d, crop.Y + crop.Height / 2d));
        var size = new SizeD(crop.Width * transform.Size.Width / original.Width, crop.Height * transform.Size.Height / original.Height);
        layer.Transform = new() { Origin = new(center.X - size.Width / 2, center.Y - size.Height / 2), Size = size,
            Rotation = transform.Rotation, FlipX = transform.FlipX, FlipY = transform.FlipY, Sampling = transform.Sampling };
        if (!layer.Transform.IsValid) throw new InvalidOperationException("The healed asset's placement is invalid.");
        if (retireMetadata) { layer.Text = null; layer.Shape = null; }
        var masks = new Dictionary<Guid, MaskSurface>(Source.Masks);
        if (layer.MaskPlacement is null && Source.Masks.TryGetValue(id, out var mask) && !layer.Transform.Equals(oldTransform))
        {
            var expanded = new MaskSurface(crop.Width, crop.Height);
            long edgeTotal = 0, edgeCount = 0;
            for (int y = 0; y < mask.Height; y++)
            for (int x = 0; x < mask.Width; x++)
                if (x == 0 || y == 0 || x == mask.Width - 1 || y == mask.Height - 1) { edgeTotal += mask.Pixels[y * mask.Width + x]; edgeCount++; }
            byte background = edgeTotal * 2 >= edgeCount * 255 ? (byte)255 : (byte)0;
            for (int y = 0; y < expanded.Height; y++)
            for (int x = 0; x < expanded.Width; x++)
            {
                int sx = x + crop.X - sourceRect.X, sy = y + crop.Y - sourceRect.Y;
                expanded.Pixels[y * expanded.Width + x] = sx < 0 || sy < 0 || sx >= sourceRect.Width || sy >= sourceRect.Height ? background
                    : mask.Pixels[sy * mask.Height / sourceRect.Height * mask.Width + sx * mask.Width / sourceRect.Width];
            }
            masks[id] = expanded;
        }
        return new() { Manifest = manifest, Images = new Dictionary<Guid, Surface>(Source.Images) { [id] = image }, Masks = masks };
    }
}

public sealed partial class EditorDocument
{
    public HealingStroke? BeginHealingStroke(Guid id, BrushSettings brush, SpotHealingMode mode, bool onMask = false) =>
        CanEditPixels(id, onMask) && (onMask || !KeepsAlpha(id)) ? HealingStroke.Create(Snapshot, id, brush, mode, ActiveSelection, onMask) : null;

    public bool CommitHealingStroke(HealingResult result)
    {
        var draft = result.Stroke;
        if (!ReferenceEquals(Snapshot, draft.Source) || !ReferenceEquals(ActiveSelection, draft.Selection)
            || ManifestJson.Serialize(Snapshot.Manifest) != draft.Manifest || !draft.SourcePixelsUnchanged()) return false;
        if (!LayerProtectionAllows(result.Snapshot)) return false;
        BeginChange(); coalesceKey = null;
        Snapshot = result.Snapshot;
        EndChange();
        return true;
    }
}
