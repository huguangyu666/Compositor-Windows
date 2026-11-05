namespace Compositor.Core;

public sealed class FloatingSelectionPreview
{
    internal FloatingSelectionPreview(ProjectSnapshot snapshot, RectI dirtyBounds) { Snapshot = snapshot; DirtyBounds = dirtyBounds; }
    public ProjectSnapshot Snapshot { get; }
    public RectI DirtyBounds { get; }
}

public sealed class FloatingSelectionResult
{
    internal FloatingSelectionResult(FloatingSelectionEdit edit, ProjectSnapshot snapshot, Selection? selection)
    { Edit = edit; Snapshot = snapshot; Selection = selection; }
    internal FloatingSelectionEdit Edit { get; }
    public ProjectSnapshot Snapshot { get; }
    public Selection? Selection { get; }
}

/// <summary>FloatingSelection.swift: raw selected pixels float on a temporary sibling,
/// then merge into the cleared source grid. Preparation and merge run on a serial worker.</summary>
public sealed class FloatingSelectionEdit
{
    internal readonly ProjectSnapshot Source;
    internal readonly Selection Selection;
    internal readonly string Manifest;
    private readonly Dictionary<Guid, (Surface Image, int Revision)> images;
    private readonly Dictionary<Guid, (MaskSurface Mask, int Revision)> masks;
    private readonly LayerTransform initial;
    private readonly RectI region;
    private readonly int canvasWidth, canvasHeight;
    private ProjectSnapshot? prepared;
    private TransformEdit? inspector;
    private ProjectSnapshot? previousPreview;
    private readonly IReadOnlyList<Guid> floatingDirtyIds;
    private (LayerTransform Value, PointD[] Corners, Surface Image, LayerTransform Placement)? warpedPreview;

    private FloatingSelectionEdit(ProjectSnapshot source, Guid id, Selection selection)
    {
        Source = source; SourceLayerId = id; Selection = selection; Manifest = ManifestJson.Serialize(source.Manifest);
        images = source.Images.ToDictionary(p => p.Key, p => (p.Value, p.Value.Revision));
        masks = source.Masks.ToDictionary(p => p.Key, p => (p.Value, p.Value.Revision));
        region = selection.Bounds; canvasWidth = source.Manifest.Width; canvasHeight = source.Manifest.Height;
        initial = new() { Origin = new(region.X, region.Y), Size = new(region.Width, region.Height) };
        DirtyLayerIds = Array.AsReadOnly(new[] { id, FloatingLayerId });
        floatingDirtyIds = Array.AsReadOnly(new[] { FloatingLayerId });
    }

    public Guid SourceLayerId { get; }
    public Guid FloatingLayerId { get; } = Guid.NewGuid();
    public LayerTransform Value => TransformEdit.Copy(initial);
    public TransformEdit Inspector => inspector ?? throw new InvalidOperationException("Prepare the floating pixels on the worker first.");
    public IReadOnlyList<Guid> DirtyLayerIds { get; }

    internal static FloatingSelectionEdit? Create(ProjectSnapshot source, Guid id, Selection? selection, bool onMask)
    {
        var layer = source.Manifest.Layers.FirstOrDefault(l => l.Id == id);
        if (onMask || selection is null || selection.IsEmpty || selection.Bounds.Width == 0 || selection.Bounds.Height == 0
            || layer is null || layer.IsGroupOrFalse || layer.Adjustment is not null || !layer.Transform.IsValid
            || !source.Images.ContainsKey(id) || !DrawOrder.Resolve(source.Manifest.Layers).Visible.Contains(id)) return null;
        return new(source, id, selection);
    }

    public ProjectSnapshot Prepare(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (prepared is not null) return prepared;
        var manifest = ManifestJson.Deserialize(Manifest); var source = manifest.Layers.First(l => l.Id == SourceLayerId);
        var pixels = images[SourceLayerId].Image;
        if (images.Sum(p => p.Value.Image.PixelCount) + masks.Sum(p => p.Value.Mask.PixelCount) + (long)region.Width * region.Height > Limits.DocumentPixelBudget)
            throw new InvalidOperationException("The floating pixels exceed the document pixel budget.");
        var dirty = AffectedBounds(source.Transform, pixels); var allocated = AllocatedCutBounds(source.Transform, pixels, dirty);
        var lifted = new Surface(region.Width, region.Height); var placed = TransformEdit.Copy(source.Transform);
        placed.Origin = new(placed.Origin.X - region.X, placed.Origin.Y - region.Y);
        Resampler.Draw(pixels, placed, lifted, 0, 0, lifted.Width, lifted.Height);
        for (int y = 0; y < lifted.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < lifted.Width; x++)
            {
                int amount = Selection.CoverageAt(region.X + x, region.Y + y), index = y * lifted.Stride + x * 4;
                for (int c = 0; c < 4; c++) lifted.Pixels[index + c] = Multiply(lifted.Pixels[index + c], amount);
            }
        }
        var cleared = pixels.CloneForEditing(); ClearPixels(cleared, source.Transform, dirty, cancellationToken);
        cleared.MarkModified(dirty);
        var crop = AlphaBounds(cleared, cancellationToken, out bool empty);
        if (empty) crop = allocated;
        long cropPixels = (long)crop.Width * crop.Height;
        bool expandedMask = source.MaskPlacement is null && masks.ContainsKey(SourceLayerId) && crop != new RectI(0, 0, pixels.Width, pixels.Height);
        long preparedPixels = images.Where(p => p.Key != SourceLayerId).Sum(p => p.Value.Image.PixelCount) + cropPixels + lifted.PixelCount
            + masks.Where(p => p.Key != SourceLayerId || !expandedMask).Sum(p => p.Value.Mask.PixelCount) + (expandedMask ? cropPixels : 0);
        if (preparedPixels > Limits.DocumentPixelBudget) throw new InvalidOperationException("The cleared pixels and mask exceed the document pixel budget.");
        if (crop != new RectI(0, 0, pixels.Width, pixels.Height)) cleared = Crop(cleared, crop, cancellationToken);
        var resultMasks = masks.ToDictionary(p => p.Key, p => p.Value.Mask);
        source.Transform = Placement(source.Transform, pixels.Width, pixels.Height, crop); source.Text = null; source.Shape = null;
        AlignAttachedMask(source, resultMasks, pixels.Width, pixels.Height, crop, cancellationToken);
        var floating = new ProjectLayerRecord { Id = FloatingLayerId, Name = "Floating Selection", IsVisible = true,
            Transform = Value, ImageFile = ProjectValidator.Uuid(FloatingLayerId) + ".png", ParentId = source.ParentId,
            Opacity = source.Opacity, BlendMode = source.BlendMode };
        manifest.Layers.Insert(manifest.Layers.FindIndex(l => l.Id == SourceLayerId) + 1, floating); manifest.ActiveLayerId = FloatingLayerId;
        var resultImages = images.ToDictionary(p => p.Key, p => p.Value.Image); resultImages[SourceLayerId] = cleared; resultImages[FloatingLayerId] = lifted;
        var snapshot = new ProjectSnapshot { Manifest = manifest, Images = resultImages, Masks = resultMasks };
        ValidateBudget(snapshot);
        var transform = TransformEdit.Create(snapshot, [FloatingLayerId], false) ?? throw new InvalidOperationException("The floating placement is invalid.");
        cancellationToken.ThrowIfCancellationRequested();
        inspector = transform; prepared = snapshot; return snapshot;
    }

    public FloatingSelectionPreview Preview(LayerTransform value, PointD[]? corners = null, CancellationToken cancellationToken = default)
    {
        if (!value.IsValid) throw new ArgumentOutOfRangeException(nameof(value));
        Prepare(cancellationToken); var preview = Inspector.Preview(value) ?? throw new ArgumentOutOfRangeException(nameof(value));
        if (corners is not null)
        {
            if (!DistortWarp.IsUsable(corners)) throw new ArgumentOutOfRangeException(nameof(corners));
            var cached = warpedPreview;
            if (cached is null || !cached.Value.Value.Equals(value) || !cached.Value.Corners.AsSpan().SequenceEqual(corners))
            {
                ValidateWarpBudget(prepared!, corners, 2048);
                var warped = DistortWarp.Warp(prepared!.Images[FloatingLayerId], value, corners, limit: 2048, cancellationToken: cancellationToken);
                cached = (TransformEdit.Copy(value), corners.ToArray(), warped.Image, warped.Transform); warpedPreview = cached;
            }
            preview.Snapshot.Manifest.Layers.First(l => l.Id == FloatingLayerId).Transform = TransformEdit.Copy(cached.Value.Placement);
            ((Dictionary<Guid, Surface>)preview.Snapshot.Images)[FloatingLayerId] = cached.Value.Image;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var dirty = CanvasCompositor.ChangedLayersBounds(previousPreview ?? Source, preview.Snapshot, previousPreview is null ? DirtyLayerIds : floatingDirtyIds);
        previousPreview = preview.Snapshot; return new(preview.Snapshot, dirty);
    }

    public FloatingSelectionResult? PrepareCommit(LayerTransform value, PointD[]? corners = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!value.IsValid) throw new ArgumentOutOfRangeException(nameof(value));
        // Exact restoration avoids a cut/source-over seam on soft coverage and keeps Redo.
        if (initial.Equals(value) && corners is null) return null;
        var snapshot = Prepare(cancellationToken); var source = snapshot.Manifest.Layers.First(l => l.Id == SourceLayerId);
        var rest = snapshot.Images[SourceLayerId]; var lifted = snapshot.Images[FloatingLayerId];
        var draft = TransformEdit.Copy(value);
        if (corners is not null)
        {
            ValidateWarpBudget(snapshot, corners, null);
            var warped = DistortWarp.Warp(lifted, value, corners, cancellationToken: cancellationToken);
            var crop = AlphaBounds(warped.Image, cancellationToken); lifted = crop == new RectI(0, 0, warped.Image.Width, warped.Image.Height)
                ? warped.Image : Crop(warped.Image, crop, cancellationToken);
            value = Placement(warped.Transform, warped.Image.Width, warped.Image.Height, crop);
        }
        var documentCorners = DistortWarp.CornersOf(value);
        double left = documentCorners.Min(p => p.X), top = documentCorners.Min(p => p.Y), right = documentCorners.Max(p => p.X), bottom = documentCorners.Max(p => p.Y);
        // The source first takes the document bbox, then that rectangle's inverse bbox.
        var sourceCorners = new[] { new PointD(left, top), new(right, top), new(right, bottom), new(left, bottom) }
            .Select(p => BrushEngine.DocumentToPixel(source.Transform, rest.Width, rest.Height, p)).ToArray();
        var extent = Integral(Math.Min(0, sourceCorners.Min(p => p.X)), Math.Min(0, sourceCorners.Min(p => p.Y)),
            Math.Max(rest.Width, sourceCorners.Max(p => p.X)), Math.Max(rest.Height, sourceCorners.Max(p => p.Y)));
        var manifest = ManifestJson.Deserialize(ManifestJson.Serialize(snapshot.Manifest)); var merged = manifest.Layers.First(l => l.Id == SourceLayerId);
        var resultMasks = new Dictionary<Guid, MaskSurface>(snapshot.Masks);
        ValidateMergeBudget(snapshot, extent, merged.MaskPlacement is null && resultMasks.ContainsKey(SourceLayerId));
        var image = new Surface(extent.Width, extent.Height);
        for (int y = 0; y < rest.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rest.Pixels.AsSpan(y * rest.Stride, rest.Width * 4).CopyTo(image.Pixels.AsSpan((y - extent.Y) * image.Stride - extent.X * 4, rest.Width * 4));
        }
        PointD Map(PointD p) => BrushEngine.DocumentToPixel(value, lifted.Width, lifted.Height,
            BrushEngine.PixelToDocument(source.Transform, rest.Width, rest.Height, p));
        var origin = Map(new(extent.X, extent.Y)); var px = Map(new(extent.X + 1, extent.Y)); var py = Map(new(extent.X, extent.Y + 1));
        double xx = px.X - origin.X, xy = px.Y - origin.Y, yx = py.X - origin.X, yy = py.Y - origin.Y;
        var sample = new byte[4]; var window = new SourceWindow(lifted);
        int drawLeft = (int)Math.Clamp(Math.Floor(sourceCorners.Min(p => p.X) - extent.X + 1e-8), 0, image.Width);
        int drawTop = (int)Math.Clamp(Math.Floor(sourceCorners.Min(p => p.Y) - extent.Y + 1e-8), 0, image.Height);
        int drawRight = (int)Math.Clamp(Math.Ceiling(sourceCorners.Max(p => p.X) - extent.X - 1e-8), drawLeft, image.Width);
        int drawBottom = (int)Math.Clamp(Math.Ceiling(sourceCorners.Max(p => p.Y) - extent.Y - 1e-8), drawTop, image.Height);
        for (int y = drawTop; y < drawBottom; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double sx = origin.X + (drawLeft + .5) * xx + (y + .5) * yx, sy = origin.Y + (drawLeft + .5) * xy + (y + .5) * yy;
            for (int x = drawLeft; x < drawRight; x++, sx += xx, sy += xy)
            {
                if (sx < 0 || sy < 0 || sx >= lifted.Width || sy >= lifted.Height) continue;
                Array.Clear(sample);
                // Draw the whole image rectangle, extending the edge taps inside it.
                // The compositor's rotated sampler retains its existing edge convention.
                Resampler.SampleTo(window, Math.Clamp(sx, .5, lifted.Width - .5), Math.Clamp(sy, .5, lifted.Height - .5), value.Sampling, sample, 0);
                if (sample[3] == 0) continue;
                int index = y * image.Stride + x * 4;
                for (int c = 0; c < 4; c++) image.Pixels[index + c] = (byte)Math.Min(255, sample[c] + Multiply(image.Pixels[index + c], 255 - sample[3]));
            }
        }
        AlignAttachedMask(merged, resultMasks, rest.Width, rest.Height, extent, cancellationToken);
        merged.Transform = Placement(source.Transform, rest.Width, rest.Height, extent);
        if (!merged.Transform.IsValid) throw new InvalidOperationException("The merged placement is invalid.");
        // Original merge replaces ImageLayer without live text, shape, or effects metadata.
        merged.Text = null; merged.Shape = null; merged.Effects = null;
        manifest.Layers.RemoveAll(l => l.Id == FloatingLayerId); manifest.ActiveLayerId = SourceLayerId;
        var resultImages = snapshot.Images.Where(p => p.Key != FloatingLayerId).ToDictionary(); resultImages[SourceLayerId] = image;
        var selection = corners is null ? Selection.Transformed(initial, value, cancellationToken) : Selection.Warped(initial, draft, corners, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new(this, new() { Manifest = manifest, Images = resultImages, Masks = resultMasks }, selection);
    }

    internal bool IsCurrent(ProjectSnapshot current, Selection? selection) => ReferenceEquals(current, Source) && ReferenceEquals(selection, Selection)
        && ManifestJson.Serialize(current.Manifest) == Manifest && current.Images.Count == images.Count && current.Masks.Count == masks.Count
        && images.All(p => current.Images.TryGetValue(p.Key, out var image) && ReferenceEquals(image, p.Value.Image) && image.Revision == p.Value.Revision)
        && masks.All(p => current.Masks.TryGetValue(p.Key, out var mask) && ReferenceEquals(mask, p.Value.Mask) && mask.Revision == p.Value.Revision);

    private RectI AffectedBounds(LayerTransform placement, Surface pixels)
    {
        var corners = new[] { new PointD(region.X, region.Y), new(region.X + region.Width, region.Y),
            new(region.X + region.Width, region.Y + region.Height), new(region.X, region.Y + region.Height) }
            .Select(p => BrushEngine.DocumentToPixel(placement, pixels.Width, pixels.Height, p)).ToArray();
        int left = (int)Math.Clamp(Math.Floor(corners.Min(p => p.X) + 1e-8), 0, pixels.Width), top = (int)Math.Clamp(Math.Floor(corners.Min(p => p.Y) + 1e-8), 0, pixels.Height);
        int right = (int)Math.Clamp(Math.Ceiling(corners.Max(p => p.X) - 1e-8), left, pixels.Width), bottom = (int)Math.Clamp(Math.Ceiling(corners.Max(p => p.Y) - 1e-8), top, pixels.Height);
        return new(left, top, right - left, bottom - top);
    }
    private RectI AllocatedCutBounds(LayerTransform placement, Surface pixels, RectI dirty)
    {
        var original = new RectI(0, 0, pixels.Width, pixels.Height);
        if (dirty.Width == 0 || dirty.Height == 0) return original;
        var corners = new[] { PointD.Zero, new(canvasWidth, 0), new(canvasWidth, canvasHeight), new(0, canvasHeight) }
            .Select(p => BrushEngine.DocumentToPixel(placement, pixels.Width, pixels.Height, p)).ToArray();
        double gx = Math.Min(0, Math.Floor(corners.Min(p => p.X) + 1e-8)), gy = Math.Min(0, Math.Floor(corners.Min(p => p.Y) + 1e-8));
        double gr = Math.Max(pixels.Width, Math.Ceiling(corners.Max(p => p.X) - 1e-8)), gb = Math.Max(pixels.Height, Math.Ceiling(corners.Max(p => p.Y) - 1e-8));
        if (!double.IsFinite(gx + gy + gr + gb) || gr - gx is < 1 or > 1_000_000_000 || gb - gy is < 1 or > 1_000_000_000)
            throw new ArgumentOutOfRangeException(nameof(placement), "The source edit grid exceeds the supported limits.");
        double left = gx + Math.Floor((dirty.X - gx) / 256) * 256, top = gy + Math.Floor((dirty.Y - gy) / 256) * 256;
        double right = Math.Min(gr, gx + (Math.Floor((dirty.X + dirty.Width - 1 - gx) / 256) + 1) * 256);
        double bottom = Math.Min(gb, gy + (Math.Floor((dirty.Y + dirty.Height - 1 - gy) / 256) + 1) * 256);
        // BrushCommit keeps allocatedBounds when the cleared source has no visible alpha.
        return Integral(Math.Min(0, left), Math.Min(0, top), Math.Max(pixels.Width, right), Math.Max(pixels.Height, bottom));
    }
    private void ClearPixels(Surface pixels, LayerTransform placement, RectI dirty, CancellationToken cancellationToken)
    {
        var origin = BrushEngine.PixelToDocument(placement, pixels.Width, pixels.Height, PointD.Zero);
        var px = BrushEngine.PixelToDocument(placement, pixels.Width, pixels.Height, new(1, 0));
        var py = BrushEngine.PixelToDocument(placement, pixels.Width, pixels.Height, new(0, 1));
        double xx = px.X - origin.X, xy = px.Y - origin.Y, yx = py.X - origin.X, yy = py.Y - origin.Y;
        bool unitGrid = Math.Abs(origin.X - Math.Round(origin.X)) < 1e-8 && Math.Abs(origin.Y - Math.Round(origin.Y)) < 1e-8
            && ((Math.Abs(Math.Abs(xx) - 1) < 1e-8 && Math.Abs(xy) < 1e-8 && Math.Abs(yx) < 1e-8 && Math.Abs(Math.Abs(yy) - 1) < 1e-8)
                || (Math.Abs(xx) < 1e-8 && Math.Abs(Math.Abs(xy) - 1) < 1e-8 && Math.Abs(Math.Abs(yx) - 1) < 1e-8 && Math.Abs(yy) < 1e-8));
        for (int y = dirty.Y; y < dirty.Y + dirty.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = dirty.X; x < dirty.X + dirty.Width; x++)
            {
                int index = y * pixels.Stride + x * 4;
                if (pixels.Pixels[index + 3] == 0) continue;
                int amount = 0;
                if (unitGrid)
                {
                    double dx = origin.X + (x + .5) * xx + (y + .5) * yx, dy = origin.Y + (x + .5) * xy + (y + .5) * yy;
                    if (dx >= 0 && dy >= 0 && dx < canvasWidth && dy < canvasHeight) amount = Selection.CoverageAt((int)dx, (int)dy) * 16;
                }
                else for (int sy = 0; sy < 4; sy++) for (int sx = 0; sx < 4; sx++)
                {
                    double u = x + (sx + .5) / 4, v = y + (sy + .5) / 4;
                    double dx = origin.X + u * xx + v * yx, dy = origin.Y + u * xy + v * yy;
                    if (dx >= 0 && dy >= 0 && dx < canvasWidth && dy < canvasHeight) amount += Selection.CoverageAt((int)dx, (int)dy);
                }
                int removed = (amount + 8) / 16;
                if (removed == 0) continue;
                for (int c = 0; c < 4; c++) pixels.Pixels[index + c] = Multiply(pixels.Pixels[index + c], 255 - removed);
            }
        }
    }

    private void AlignAttachedMask(ProjectLayerRecord layer, Dictionary<Guid, MaskSurface> result, int width, int height, RectI crop, CancellationToken cancellationToken)
    {
        if (layer.MaskPlacement is not null || !result.TryGetValue(SourceLayerId, out var mask) || crop == new RectI(0, 0, width, height)) return;
        var aligned = new MaskSurface(crop.Width, crop.Height); Array.Fill(aligned.Pixels, (byte)255);
        int x0 = Math.Max(0, crop.X), y0 = Math.Max(0, crop.Y), x1 = Math.Min(width, crop.X + crop.Width), y1 = Math.Min(height, crop.Y + crop.Height);
        for (int y = y0; y < y1; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = x0; x < x1; x++) aligned.Pixels[(y - crop.Y) * aligned.Width + x - crop.X]
                = mask.Pixels[y * mask.Height / height * mask.Width + x * mask.Width / width];
        }
        result[SourceLayerId] = aligned;
    }
    private static LayerTransform Placement(LayerTransform source, int width, int height, RectI extent)
    {
        var center = BrushEngine.PixelToDocument(source, width, height, new(extent.X + extent.Width / 2d, extent.Y + extent.Height / 2d));
        var size = new SizeD(extent.Width * source.Size.Width / width, extent.Height * source.Size.Height / height);
        return new() { Origin = new(center.X - size.Width / 2, center.Y - size.Height / 2), Size = size,
            Rotation = source.Rotation, FlipX = source.FlipX, FlipY = source.FlipY, Sampling = source.Sampling };
    }
    private static RectI Integral(double x0, double y0, double x1, double y1)
    {
        double left = Math.Floor(x0 + 1e-8), top = Math.Floor(y0 + 1e-8), width = Math.Ceiling(x1 - 1e-8) - left, height = Math.Ceiling(y1 - 1e-8) - top;
        if (!double.IsFinite(left + top + width + height) || Math.Abs(left) > 1_000_000_000 || Math.Abs(top) > 1_000_000_000
            || width is < 1 or > Limits.MaxSide || height is < 1 or > Limits.MaxSide || width * height > Limits.MaxSurfacePixels)
            throw new ArgumentOutOfRangeException(nameof(x1), "The floating pixels exceed the raster limits.");
        return new((int)left, (int)top, (int)width, (int)height);
    }
    private static RectI AlphaBounds(Surface image, CancellationToken cancellationToken) => AlphaBounds(image, cancellationToken, out _);
    private static RectI AlphaBounds(Surface image, CancellationToken cancellationToken, out bool empty)
    {
        int left = image.Width, top = image.Height, right = 0, bottom = 0;
        for (int y = 0; y < image.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested(); int row = y * image.Stride, start = 0, end = image.Width;
            while (start < end && image.Pixels[row + start * 4 + 3] == 0) start++;
            if (start == end) continue;
            while (end > start && image.Pixels[row + (end - 1) * 4 + 3] == 0) end--;
            left = Math.Min(left, start); right = Math.Max(right, end); top = Math.Min(top, y); bottom = Math.Max(bottom, y + 1);
        }
        empty = right <= left; return empty ? new(0, 0, image.Width, image.Height) : new(left, top, right - left, bottom - top);
    }
    private static Surface Crop(Surface image, RectI bounds, CancellationToken cancellationToken)
    {
        var result = new Surface(bounds.Width, bounds.Height);
        int left = Math.Max(0, bounds.X), top = Math.Max(0, bounds.Y), right = Math.Min(image.Width, bounds.X + bounds.Width), bottom = Math.Min(image.Height, bounds.Y + bounds.Height);
        for (int y = top; y < bottom && right > left; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            image.Pixels.AsSpan(y * image.Stride + left * 4, (right - left) * 4).CopyTo(result.Pixels.AsSpan((y - bounds.Y) * result.Stride + (left - bounds.X) * 4, (right - left) * 4));
        }
        return result;
    }
    private static byte Multiply(byte value, int amount) => (byte)((value * amount + 127) / 255);
    private static void ValidateBudget(ProjectSnapshot snapshot)
    {
        if (snapshot.Images.Sum(p => p.Value.PixelCount) + snapshot.Masks.Sum(p => p.Value.PixelCount) > Limits.DocumentPixelBudget)
            throw new InvalidOperationException("The floating pixels exceed the document pixel budget.");
    }
    private void ValidateMergeBudget(ProjectSnapshot snapshot, RectI extent, bool attachedMask)
    {
        long area = (long)extent.Width * extent.Height;
        long remaining = snapshot.Images.Where(p => p.Key != SourceLayerId && p.Key != FloatingLayerId).Sum(p => p.Value.PixelCount)
            + snapshot.Masks.Where(p => p.Key != SourceLayerId || !attachedMask).Sum(p => p.Value.PixelCount);
        long maskPixels = attachedMask ? extent == new RectI(0, 0, snapshot.Images[SourceLayerId].Width, snapshot.Images[SourceLayerId].Height)
            ? snapshot.Masks[SourceLayerId].PixelCount : area : 0;
        if (remaining + area + maskPixels > Limits.DocumentPixelBudget)
            throw new InvalidOperationException("The merged pixels exceed the document pixel budget.");
    }
    private void ValidateWarpBudget(ProjectSnapshot snapshot, PointD[] corners, int? limit)
    {
        if (!DistortWarp.IsUsable(corners)) throw new ArgumentOutOfRangeException(nameof(corners));
        double width = Math.Ceiling(corners.Max(p => p.X)) - Math.Floor(corners.Min(p => p.X));
        double height = Math.Ceiling(corners.Max(p => p.Y)) - Math.Floor(corners.Min(p => p.Y));
        if (width is < 1 or > Limits.MaxSide || height is < 1 or > Limits.MaxSide || width * height > Limits.MaxSurfacePixels)
            throw new ArgumentOutOfRangeException(nameof(corners), "The distorted pixels exceed the raster limits.");
        double factor = limit is { } cap ? Math.Min(1, cap / Math.Max(width, height)) : 1;
        long area = (long)Math.Ceiling(width * factor) * (long)Math.Ceiling(height * factor);
        if (snapshot.Images.Where(p => p.Key != FloatingLayerId).Sum(p => p.Value.PixelCount) + snapshot.Masks.Sum(p => p.Value.PixelCount) + area > Limits.DocumentPixelBudget)
            throw new InvalidOperationException("The distorted pixels exceed the document pixel budget.");
    }
}

public sealed partial class EditorDocument
{
    public FloatingSelectionEdit? BeginFloatingSelection(Guid id, bool onMask = false) => CanEditPixels(id, onMask)
        && CanEditLayer(id, LayerLocks.Position) && (onMask || !KeepsAlpha(id)) ? FloatingSelectionEdit.Create(Snapshot, id, ActiveSelection, onMask) : null;
    public bool CommitFloatingSelection(FloatingSelectionResult result)
    {
        if (!result.Edit.IsCurrent(Snapshot, ActiveSelection)) return false;
        var committed = new ProjectSnapshot { Manifest = ManifestJson.Deserialize(ManifestJson.Serialize(result.Snapshot.Manifest)),
            Images = result.Snapshot.Images, Masks = result.Snapshot.Masks };
        if (!LayerProtectionAllows(committed)) return false;
        BeginChange(); coalesceKey = null; Snapshot = committed; ActiveSelection = result.Selection; EndChange(); return true;
    }
}
