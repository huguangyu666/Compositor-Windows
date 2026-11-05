namespace Compositor.Core;

public sealed class PixelMoveResult
{
    internal PixelMoveResult(PixelMoveEdit edit, ProjectSnapshot snapshot, Selection selection)
    { Edit = edit; Snapshot = snapshot; Selection = selection; }
    internal PixelMoveEdit Edit { get; }
    public ProjectSnapshot Snapshot { get; }
    public Selection Selection { get; }
}

public sealed class PixelMovePreview
{
    internal PixelMovePreview(ProjectSnapshot snapshot, RectI dirtyBounds) { Snapshot = snapshot; DirtyBounds = dirtyBounds; }
    public ProjectSnapshot Snapshot { get; }
    public RectI DirtyBounds { get; }
}

public sealed class PixelMoveEdit
{
    private const int TileSize = 256;
    private sealed record Tile(RectI Rect, Surface Basis, Surface Pixels, Surface Sample);
    internal readonly ProjectSnapshot Source;
    internal readonly string Manifest;
    internal readonly Selection Selection;
    private readonly Guid id;
    private readonly bool duplicate;
    private readonly Surface original;
    private readonly LayerTransform placement;
    private readonly RectI liftedBounds;
    private readonly long otherPixels;
    private readonly bool attachedMask;
    private readonly Dictionary<Guid, (Surface Image, int Revision)> images;
    private readonly Dictionary<Guid, (MaskSurface Mask, int Revision)> masks;
    private readonly Dictionary<(int X, int Y), Tile> tiles = new();
    private HashSet<(int X, int Y)> moveTiles = new();
    private Surface? lifted;
    private MaskSurface? clip;
    private PointD? appliedOffset;
    private ProjectSnapshot? preview;
    private RectI previewBounds;
    public IReadOnlyList<Guid> DirtyLayerIds { get; }

    private PixelMoveEdit(ProjectSnapshot source, Guid id, Selection selection, bool duplicate, RectI grid, RectI region)
    {
        Source = source; Manifest = ManifestJson.Serialize(source.Manifest); Selection = selection;
        this.id = id; this.duplicate = duplicate; original = source.Images[id];
        DirtyLayerIds = Array.AsReadOnly(new[] { id });
        placement = TransformEdit.Copy(source.Manifest.Layers.First(layer => layer.Id == id).Transform);
        GridBounds = grid; liftedBounds = region; PixelBounds = new(0, 0, original.Width, original.Height);
        attachedMask = source.Masks.ContainsKey(id) && source.Manifest.Layers.First(layer => layer.Id == id).MaskPlacement is null;
        otherPixels = source.Images.Where(pair => pair.Key != id).Sum(pair => pair.Value.PixelCount)
            + source.Masks.Where(pair => pair.Key != id || !attachedMask).Sum(pair => (long)pair.Value.Width * pair.Value.Height);
        images = source.Images.ToDictionary(pair => pair.Key, pair => (pair.Value, pair.Value.Revision));
        masks = source.Masks.ToDictionary(pair => pair.Key, pair => (pair.Value, pair.Value.Revision));
    }

    internal static PixelMoveEdit? Create(ProjectSnapshot source, Guid id, Selection? selection, bool duplicate, bool onMask)
    {
        var layer = source.Manifest.Layers.FirstOrDefault(layer => layer.Id == id);
        if (onMask || selection is null || selection.IsEmpty || layer is null || layer.IsGroupOrFalse || layer.Adjustment is not null
            || !layer.Transform.IsValid || !source.Images.TryGetValue(id, out var image) || !DrawOrder.Resolve(source.Manifest.Layers).Visible.Contains(id)) return null;
        var bounds = selection.Bounds;
        if (bounds.Width == 0 || bounds.Height == 0) return null;
        var region = MappedBounds(layer.Transform, image, Math.Max(0, bounds.X - 1), Math.Max(0, bounds.Y - 1),
            Math.Min(source.Manifest.Width, bounds.X + bounds.Width + 1), Math.Min(source.Manifest.Height, bounds.Y + bounds.Height + 1));
        region = Intersect(region, new(0, 0, image.Width, image.Height));
        if (region.Width == 0 || region.Height == 0) return null;
        var canvas = MappedBounds(layer.Transform, image, 0, 0, source.Manifest.Width, source.Manifest.Height);
        var grid = RectI.Union(canvas, new(0, 0, image.Width, image.Height));
        return grid.Width is > 0 and <= 1_000_000_000 && grid.Height is > 0 and <= 1_000_000_000
            ? new(source, id, selection, duplicate, grid, region) : null;
    }

    public PointD Offset { get; private set; }
    public RectI PixelBounds { get; private set; }
    public RectI GridBounds { get; }
    public long AllocatedPixels => tiles.Values.Sum(tile => tile.Pixels.PixelCount);
    public Selection DisplayedSelection => Selection.Translated((int)Offset.X, (int)Offset.Y);

    /// <summary>The serial render worker borrows a reusable raster. Retained frames must not read it concurrently.</summary>
    public PixelMovePreview Preview(PointD offset, CancellationToken cancellationToken = default)
    {
        var dirty = ApplyOffset(offset, cancellationToken);
        if (preview is null || previewBounds != PixelBounds)
        {
            Surface image;
            if (PixelBounds == new RectI(0, 0, original.Width, original.Height))
            {
                image = original.CloneForEditing();
                foreach (var tile in tiles.Values) CopyWindow(tile.Pixels, tile.Rect, image, PixelBounds);
                image.MarkModified(dirty);
            }
            else { image = new(PixelBounds.Width, PixelBounds.Height); DrawPixels(image, PixelBounds); }
            preview = MakeSnapshot(image, PixelBounds, cancellationToken); previewBounds = PixelBounds;
        }
        else if (dirty.Width > 0 && dirty.Height > 0)
        {
            var image = preview.Images[id]; var window = Intersect(dirty, previewBounds);
            // Rebuild only old/new source windows. The rest of the 4K raster stays warm.
            for (int y = window.Y; y < window.Y + window.Height; y++)
                image.Pixels.AsSpan((y - previewBounds.Y) * image.Stride + (window.X - previewBounds.X) * 4, window.Width * 4).Clear();
            CopyWindow(original, new(0, 0, original.Width, original.Height), image, previewBounds, window);
            foreach (var tile in tiles.Values) CopyWindow(tile.Pixels, tile.Rect, image, previewBounds, window);
            image.MarkModified(new(window.X - previewBounds.X, window.Y - previewBounds.Y, window.Width, window.Height));
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (dirty.Width == 0 || dirty.Height == 0) return new(preview, default);
        var region = Resampler.BoundingRect(PlacementFor(dirty), Source.Manifest.Width, Source.Manifest.Height);
        return new(preview, RectI.Normalize(region.X0 - 2, region.Y0 - 2, region.X1 + 2, region.Y1 + 2, Source.Manifest.Width, Source.Manifest.Height));
    }

    /// <summary>BrushStroke.moveLifted: a serial worker rebuilds touched tiles from the fixed basis, never from the previous move.</summary>
    public RectI ApplyOffset(PointD offset, CancellationToken cancellationToken = default)
    {
        offset = Rounded(offset); cancellationToken.ThrowIfCancellationRequested();
        _ = Selection.Translated((int)offset.X, (int)offset.Y);
        if (appliedOffset == offset) return default;
        var zero = BrushEngine.DocumentToPixel(placement, original.Width, original.Height, PointD.Zero);
        var point = BrushEngine.DocumentToPixel(placement, original.Width, original.Height, offset);
        double dx = point.X - zero.X, dy = point.Y - zero.Y;
        double tx = liftedBounds.X + dx, ty = liftedBounds.Y + dy;
        var needed = IntegralIntersection(Math.Min(liftedBounds.X, tx), Math.Min(liftedBounds.Y, ty),
            Math.Max(liftedBounds.X + liftedBounds.Width, tx + liftedBounds.Width),
            Math.Max(liftedBounds.Y + liftedBounds.Height, ty + liftedBounds.Height), GridBounds);
        var keys = new HashSet<(int X, int Y)>();
        int left = (needed.X - GridBounds.X) / TileSize, top = (needed.Y - GridBounds.Y) / TileSize;
        int right = (needed.X + needed.Width - 1 - GridBounds.X) / TileSize, bottom = (needed.Y + needed.Height - 1 - GridBounds.Y) / TileSize;
        // Validate the whole next allocation before creating or changing any tile.
        var nextBounds = RectI.Union(PixelBounds, RectI.Union(TileRect(left, top), TileRect(right, bottom)));
        ValidateBounds(nextBounds);
        for (int y = top; y <= bottom; y++) for (int x = left; x <= right; x++) keys.Add((x, y));
        Lift(cancellationToken);
        var oldBounds = TargetBounds(Offset);
        var dirty = RectI.Union(liftedBounds, RectI.Union(oldBounds, needed));
        moveTiles.UnionWith(keys); appliedOffset = null;
        foreach (var key in moveTiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!tiles.TryGetValue(key, out var tile))
            {
                var rect = TileRect(key.X, key.Y); var basis = new Surface(rect.Width, rect.Height);
                CopyOriginal(basis, rect);
                if (!duplicate)
                {
                    var overlap = Intersect(rect, liftedBounds);
                    for (int y = overlap.Y; y < overlap.Y + overlap.Height; y++) for (int x = overlap.X; x < overlap.X + overlap.Width; x++)
                    {
                        int i = (y - rect.Y) * basis.Stride + (x - rect.X) * 4;
                        byte amount = clip!.Pixels[(y - liftedBounds.Y) * clip.Width + x - liftedBounds.X];
                        for (int c = 0; c < 4; c++) basis.Pixels[i + c] = Multiply(basis.Pixels[i + c], 255 - amount);
                    }
                }
                tile = new(rect, basis, new(rect.Width, rect.Height), new(rect.Width, rect.Height)); tiles.Add(key, tile);
            }
            tile.Basis.Pixels.CopyTo(tile.Pixels.Pixels, 0); Array.Clear(tile.Sample.Pixels);
            var target = new LayerTransform { Origin = new(tx - tile.Rect.X, ty - tile.Rect.Y), Size = new(lifted!.Width, lifted.Height) };
            bool wholePixels = Math.Abs(tx - Math.Round(tx)) < 1e-8 && Math.Abs(ty - Math.Round(ty)) < 1e-8;
            Resampler.Draw(lifted, target, tile.Sample, 0, 0, tile.Rect.Width, tile.Rect.Height,
                wholePixels ? LayerSampling.Nearest : LayerSampling.HighQuality);
            for (int i = 0; i < tile.Pixels.Pixels.Length; i += 4)
            {
                int alpha = tile.Sample.Pixels[i + 3];
                if (alpha == 0) continue;
                for (int c = 0; c < 4; c++) tile.Pixels.Pixels[i + c] = (byte)Math.Min(255,
                    tile.Sample.Pixels[i + c] + Multiply(tile.Pixels.Pixels[i + c], 255 - alpha));
            }
            tile.Pixels.MarkModified();
        }
        PixelBounds = nextBounds; moveTiles = keys; Offset = offset; appliedOffset = offset;
        return dirty;
    }

    /// <summary>Copies a bounded source-grid window into caller-owned memory. No document-sized preview allocation.</summary>
    public void DrawPixels(Surface destination, RectI pixelBounds)
    {
        if (destination.Width != pixelBounds.Width || destination.Height != pixelBounds.Height)
            throw new ArgumentException("The pixel window and destination dimensions must match.", nameof(destination));
        Array.Clear(destination.Pixels); CopyOriginal(destination, pixelBounds);
        foreach (var tile in tiles.Values) CopyWindow(tile.Pixels, tile.Rect, destination, pixelBounds);
    }

    public PixelMoveResult? PrepareCommit(PointD offset, CancellationToken cancellationToken = default)
    {
        offset = Rounded(offset); cancellationToken.ThrowIfCancellationRequested();
        if (offset == PointD.Zero) return null;
        ApplyOffset(offset, cancellationToken);
        // BrushCommit.render trims every nonzero-alpha pixel once, off the pointer path.
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        for (int y = 0; y < original.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int x = 0;
            while (x < original.Width)
            {
                int tileX = (x - GridBounds.X) / TileSize;
                int end = Math.Min(original.Width, GridBounds.X + (tileX + 1) * TileSize);
                if (!tiles.ContainsKey((tileX, (y - GridBounds.Y) / TileSize))) IncludeRow(original, y, x, end, 0, 0);
                x = end;
            }
        }
        foreach (var tile in tiles.Values)
        for (int y = 0; y < tile.Rect.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IncludeRow(tile.Pixels, y, 0, tile.Rect.Width, tile.Rect.X, tile.Rect.Y);
        }
        var crop = left == int.MaxValue ? PixelBounds : new RectI(left, top, right - left, bottom - top);
        ValidateBounds(crop, committing: true); var image = new Surface(crop.Width, crop.Height); DrawPixels(image, crop);
        cancellationToken.ThrowIfCancellationRequested();
        return new(this, MakeSnapshot(image, crop, cancellationToken), DisplayedSelection);

        void Include(int x, int y) { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x + 1); bottom = Math.Max(bottom, y + 1); }
        void IncludeRow(Surface pixels, int y, int start, int end, int originX, int originY)
        {
            int row = y * pixels.Stride;
            while (start < end && pixels.Pixels[row + start * 4 + 3] == 0) start++;
            if (start == end) return;
            while (end > start && pixels.Pixels[row + (end - 1) * 4 + 3] == 0) end--;
            Include(start + originX, y + originY); Include(end - 1 + originX, y + originY);
        }
    }

    private ProjectSnapshot MakeSnapshot(Surface image, RectI crop, CancellationToken cancellationToken)
    {
        var manifest = ManifestJson.Deserialize(Manifest); var layer = manifest.Layers.First(layer => layer.Id == id);
        layer.Transform = PlacementFor(crop);
        if (!layer.Transform.IsValid) throw new InvalidOperationException("The moved asset's placement is invalid.");
        layer.Text = null; layer.Shape = null;
        var resultMasks = new Dictionary<Guid, MaskSurface>(Source.Masks);
        if (attachedMask && crop != new RectI(0, 0, original.Width, original.Height))
        {
            var mask = Source.Masks[id]; var expanded = new MaskSurface(crop.Width, crop.Height); Array.Fill(expanded.Pixels, (byte)255);
            var overlap = Intersect(crop, new(0, 0, original.Width, original.Height));
            for (int y = overlap.Y; y < overlap.Y + overlap.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int x = overlap.X; x < overlap.X + overlap.Width; x++) expanded.Pixels[(y - crop.Y) * crop.Width + x - crop.X]
                    = mask.Pixels[y * mask.Height / original.Height * mask.Width + x * mask.Width / original.Width];
            }
            resultMasks[id] = expanded;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new() { Manifest = manifest, Images = new Dictionary<Guid, Surface>(Source.Images) { [id] = image }, Masks = resultMasks };
    }

    internal bool SourcePixelsUnchanged() => Source.Images.Count == images.Count && Source.Masks.Count == masks.Count
        && images.All(pair => Source.Images.TryGetValue(pair.Key, out var image) && ReferenceEquals(image, pair.Value.Image) && image.Revision == pair.Value.Revision)
        && masks.All(pair => Source.Masks.TryGetValue(pair.Key, out var mask) && ReferenceEquals(mask, pair.Value.Mask) && mask.Revision == pair.Value.Revision);

    private void Lift(CancellationToken cancellationToken)
    {
        if (lifted is not null) return;
        var pixels = new Surface(liftedBounds.Width, liftedBounds.Height); var coverage = new MaskSurface(liftedBounds.Width, liftedBounds.Height);
        var origin = BrushEngine.PixelToDocument(placement, original.Width, original.Height, new(liftedBounds.X, liftedBounds.Y));
        var px = BrushEngine.PixelToDocument(placement, original.Width, original.Height, new(liftedBounds.X + 1, liftedBounds.Y));
        var py = BrushEngine.PixelToDocument(placement, original.Width, original.Height, new(liftedBounds.X, liftedBounds.Y + 1));
        double xx = px.X - origin.X, xy = px.Y - origin.Y, yx = py.X - origin.X, yy = py.Y - origin.Y;
        bool unitGrid = Math.Abs(origin.X - Math.Round(origin.X)) < 1e-8 && Math.Abs(origin.Y - Math.Round(origin.Y)) < 1e-8
            && ((Math.Abs(Math.Abs(xx) - 1) < 1e-8 && Math.Abs(xy) < 1e-8 && Math.Abs(yx) < 1e-8 && Math.Abs(Math.Abs(yy) - 1) < 1e-8)
                || (Math.Abs(xx) < 1e-8 && Math.Abs(Math.Abs(xy) - 1) < 1e-8 && Math.Abs(Math.Abs(yx) - 1) < 1e-8 && Math.Abs(yy) < 1e-8));
        for (int y = 0; y < pixels.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < pixels.Width; x++)
            {
                double amount = 0;
                // Windows mask sampling approximates Core Graphics' document-space clip.
                if (unitGrid)
                {
                    double dx = origin.X + (x + .5) * xx + (y + .5) * yx, dy = origin.Y + (x + .5) * xy + (y + .5) * yy;
                    if (dx >= 0 && dy >= 0 && dx < Source.Manifest.Width && dy < Source.Manifest.Height) amount = Selection.CoverageAt((int)dx, (int)dy) * 16;
                }
                else for (int sy = 0; sy < 4; sy++) for (int sx = 0; sx < 4; sx++)
                {
                    double u = x + (sx + .5) / 4, v = y + (sy + .5) / 4;
                    double dx = origin.X + u * xx + v * yx, dy = origin.Y + u * xy + v * yy;
                    if (dx >= 0 && dy >= 0 && dx < Source.Manifest.Width && dy < Source.Manifest.Height)
                        amount += Selection.CoverageAt((int)dx, (int)dy);
                }
                byte value = (byte)Math.Round(amount / 16, MidpointRounding.AwayFromZero); coverage.Pixels[y * coverage.Width + x] = value;
                int target = y * pixels.Stride + x * 4, source = (y + liftedBounds.Y) * original.Stride + (x + liftedBounds.X) * 4;
                for (int c = 0; c < 4; c++) pixels.Pixels[target + c] = Multiply(original.Pixels[source + c], value);
            }
        }
        clip = coverage; lifted = pixels;
    }

    private void ValidateBounds(RectI bounds, bool committing = false)
    {
        long area = (long)bounds.Width * bounds.Height;
        long maskPixels = attachedMask ? (long)Source.Masks[id].Width * Source.Masks[id].Height : 0;
        if (attachedMask && committing && bounds != new RectI(0, 0, original.Width, original.Height)) maskPixels = area;
        if (bounds.Width is < 1 or > Limits.MaxSide || bounds.Height is < 1 or > Limits.MaxSide || area > Limits.MaxSurfacePixels
            || area + maskPixels + otherPixels > Limits.DocumentPixelBudget)
            throw new InvalidOperationException("The moved pixels exceed the raster or document pixel budget.");
    }

    private RectI TargetBounds(PointD offset)
    {
        var zero = BrushEngine.DocumentToPixel(placement, original.Width, original.Height, PointD.Zero);
        var point = BrushEngine.DocumentToPixel(placement, original.Width, original.Height, offset);
        double x = liftedBounds.X + point.X - zero.X, y = liftedBounds.Y + point.Y - zero.Y;
        return IntegralIntersection(x, y, x + liftedBounds.Width, y + liftedBounds.Height, GridBounds);
    }

    private RectI TileRect(int x, int y) => new(GridBounds.X + x * TileSize, GridBounds.Y + y * TileSize,
        Math.Min(TileSize, GridBounds.Width - x * TileSize), Math.Min(TileSize, GridBounds.Height - y * TileSize));
    private void CopyOriginal(Surface destination, RectI window) => CopyWindow(original, new(0, 0, original.Width, original.Height), destination, window);
    private LayerTransform PlacementFor(RectI crop)
    {
        var center = BrushEngine.PixelToDocument(placement, original.Width, original.Height, new(crop.X + crop.Width / 2d, crop.Y + crop.Height / 2d));
        var size = new SizeD(crop.Width * placement.Size.Width / original.Width, crop.Height * placement.Size.Height / original.Height);
        return new() { Origin = new(center.X - size.Width / 2, center.Y - size.Height / 2), Size = size,
            Rotation = placement.Rotation, FlipX = placement.FlipX, FlipY = placement.FlipY, Sampling = placement.Sampling };
    }
    private static void CopyWindow(Surface source, RectI sourceBounds, Surface destination, RectI destinationBounds, RectI? clip = null)
    {
        var overlap = Intersect(sourceBounds, destinationBounds);
        if (clip is { } window) overlap = Intersect(overlap, window);
        if (overlap.Width == 0 || overlap.Height == 0) return;
        for (int y = overlap.Y; y < overlap.Y + overlap.Height; y++)
            source.Pixels.AsSpan((y - sourceBounds.Y) * source.Stride + (overlap.X - sourceBounds.X) * 4, overlap.Width * 4)
                .CopyTo(destination.Pixels.AsSpan((y - destinationBounds.Y) * destination.Stride + (overlap.X - destinationBounds.X) * 4, overlap.Width * 4));
    }

    private static byte Multiply(byte value, int amount) => (byte)((value * amount + 127) / 255);
    private static PointD Rounded(PointD offset)
    {
        if (!double.IsFinite(offset.X) || !double.IsFinite(offset.Y) || Math.Abs(offset.X) > int.MaxValue - 1d || Math.Abs(offset.Y) > int.MaxValue - 1d)
            throw new ArgumentOutOfRangeException(nameof(offset));
        return new(Math.Round(offset.X, MidpointRounding.AwayFromZero), Math.Round(offset.Y, MidpointRounding.AwayFromZero));
    }

    private static RectI MappedBounds(LayerTransform placement, Surface image, double x0, double y0, double x1, double y1)
    {
        var points = new[] { new PointD(x0, y0), new(x1, y0), new(x0, y1), new(x1, y1) }
            .Select(point => BrushEngine.DocumentToPixel(placement, image.Width, image.Height, point)).ToArray();
        return IntegralIntersection(points.Min(point => point.X), points.Min(point => point.Y), points.Max(point => point.X), points.Max(point => point.Y),
            new(-1_000_000_000, -1_000_000_000, 2_000_000_000, 2_000_000_000));
    }
    private static RectI IntegralIntersection(double x0, double y0, double x1, double y1, RectI bounds)
    {
        int left = (int)Math.Floor(Math.Clamp(x0 + 1e-8, bounds.X, bounds.X + bounds.Width));
        int top = (int)Math.Floor(Math.Clamp(y0 + 1e-8, bounds.Y, bounds.Y + bounds.Height));
        int right = (int)Math.Ceiling(Math.Clamp(x1 - 1e-8, left, bounds.X + bounds.Width));
        int bottom = (int)Math.Ceiling(Math.Clamp(y1 - 1e-8, top, bounds.Y + bounds.Height));
        return new(left, top, right - left, bottom - top);
    }
    private static RectI Intersect(RectI left, RectI right)
    {
        int x = Math.Max(left.X, right.X), y = Math.Max(left.Y, right.Y);
        return new(x, y, Math.Max(0, Math.Min(left.X + left.Width, right.X + right.Width) - x),
            Math.Max(0, Math.Min(left.Y + left.Height, right.Y + right.Height) - y));
    }
}

public sealed partial class EditorDocument
{
    public PixelMoveEdit? BeginPixelMove(Guid id, bool duplicate = false, bool onMask = false) => CanEditPixels(id, onMask)
        && CanEditLayer(id, LayerLocks.Position) && (onMask || !KeepsAlpha(id)) ? PixelMoveEdit.Create(Snapshot, id, ActiveSelection, duplicate, onMask) : null;
    public bool CommitPixelMove(PixelMoveResult result)
    {
        var edit = result.Edit;
        if (!ReferenceEquals(Snapshot, edit.Source) || !ReferenceEquals(ActiveSelection, edit.Selection)
            || ManifestJson.Serialize(Snapshot.Manifest) != edit.Manifest || !edit.SourcePixelsUnchanged()) return false;
        var committed = new ProjectSnapshot { Manifest = ManifestJson.Deserialize(ManifestJson.Serialize(result.Snapshot.Manifest)),
            Images = result.Snapshot.Images, Masks = result.Snapshot.Masks };
        if (!LayerProtectionAllows(committed)) return false;
        BeginChange(); coalesceKey = null; Snapshot = committed; ActiveSelection = result.Selection; EndChange();
        return true;
    }
}
