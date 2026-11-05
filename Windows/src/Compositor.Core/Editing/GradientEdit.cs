namespace Compositor.Core;

public enum GradientShape { Linear, Radial }
public enum GradientStyle { ForegroundToBackground, ForegroundToTransparent }
public readonly record struct GradientColor(byte Red, byte Green, byte Blue);
public sealed record GradientSettings(GradientShape Shape, GradientStyle Style, GradientColor Foreground,
    GradientColor Background, bool Reversed = false, double Opacity = 1);

/// <summary>A read-only rendered draft. Discarding it leaves pixels and history untouched.</summary>
public sealed class GradientPreview
{
    internal GradientPreview(GradientEdit edit, ProjectSnapshot snapshot) { Edit = edit; Snapshot = snapshot; }
    internal GradientEdit Edit { get; }
    public ProjectSnapshot Snapshot { get; }
}

/// <summary>Paints every preview over the same immutable source, in the layer's own pixel grid.
/// Placement and canvas growth follow BrushStroke.pixelToDocument and Gradient.swift on macOS.</summary>
public sealed partial class GradientEdit
{
    private const int parallelPixels = 1 << 18;
    private const int rowsPerBatch = 16;
    internal readonly ProjectSnapshot Source;
    internal readonly string Manifest;
    internal readonly Selection? Selection;
    private readonly Guid id;
    private readonly bool onMask;
    private readonly LayerTransform placement;
    private readonly int sourceWidth, sourceHeight, x0, y0, width, height;
    private readonly int canvasWidth, canvasHeight;
    private readonly double cos, sin;
    private readonly double stepX, stepY, halfX, halfY, columnX, columnY;
    private readonly bool fullCoverage;
    private readonly byte maskBackground;
    private readonly Lazy<PreviewBasis> basis;
    private readonly ProjectSnapshot geometry;
    private readonly RectI dirtyBounds;
    private sealed record PreviewBasis(ProjectManifest Manifest, Surface? Image, MaskSurface? Mask, MaskSurface? AttachedMask);

    private GradientEdit(ProjectSnapshot source, Guid id, bool onMask, Selection? selection,
        LayerTransform placement, int sourceWidth, int sourceHeight, int x0, int y0, int width, int height)
    {
        Source = source;
        canvasWidth = source.Manifest.Width; canvasHeight = source.Manifest.Height;
        Manifest = ManifestJson.Serialize(source.Manifest);
        Selection = selection;
        this.id = id; this.onMask = onMask; this.placement = placement;
        this.sourceWidth = sourceWidth; this.sourceHeight = sourceHeight;
        this.x0 = x0; this.y0 = y0; this.width = width; this.height = height;
        cos = Math.Cos(placement.Radians); sin = Math.Sin(placement.Radians);
        stepX = placement.Size.Width / sourceWidth * (placement.FlipX ? -1 : 1);
        stepY = placement.Size.Height / sourceHeight * (placement.FlipY ? -1 : 1);
        columnX = stepX * cos; columnY = stepX * sin;
        halfX = (Math.Abs(columnX) + Math.Abs(stepY * sin)) / 2;
        halfY = (Math.Abs(columnY) + Math.Abs(stepY * cos)) / 2;
        fullCoverage = selection is null && x0 == 0 && y0 == 0 && width == sourceWidth && height == sourceHeight
            && placement.Rotation == 0 && placement.Origin.X >= 0 && placement.Origin.Y >= 0
            && placement.Origin.X + placement.Size.Width <= canvasWidth && placement.Origin.Y + placement.Size.Height <= canvasHeight;
        maskBackground = source.Masks.TryGetValue(id, out var mask) ? EdgeBackground(mask) : (byte)255;
        geometry = new() { Manifest = BuildPreviewManifest(), Images = source.Images, Masks = source.Masks };
        dirtyBounds = CanvasCompositor.ChangedLayerBounds(Source, geometry, id);
        basis = new(BuildBasis);
    }

    public Guid LayerId => id;
    public RectI DirtyBounds() => dirtyBounds;

    internal static GradientEdit? Create(ProjectSnapshot source, Guid id, bool onMask, Selection? selection)
    {
        var layer = source.Manifest.Layers.FirstOrDefault(layer => layer.Id == id);
        if (layer is null || !DrawOrder.Resolve(source.Manifest.Layers).Visible.Contains(id)
            || selection is { Bounds.Width: <= 0 } || selection is { Bounds.Height: <= 0 }) return null;
        if (onMask ? layer.MaskEnabled == false || !source.Masks.ContainsKey(id)
            : layer.IsGroupOrFalse || layer.Adjustment is not null) return null;
        var placed = onMask ? layer.MaskPlacement ?? layer.Transform : layer.Transform;
        if (!placed.IsValid) return null;
        // Clone the geometry: live edits must never change a draft's sampling grid.
        var placement = Copy(placed);
        source.Images.TryGetValue(id, out var image);
        source.Masks.TryGetValue(id, out var mask);
        bool independent = onMask && layer.MaskPlacement is not null;
        bool solid = independent && mask!.Width <= 2 && mask.Height <= 2;
        int sw = independent && !solid ? mask!.Width : solid || image is null ? (int)Math.Round(placed.Size.Width) : image.Width;
        int sh = independent && !solid ? mask!.Height : solid || image is null ? (int)Math.Round(placed.Size.Height) : image.Height;
        if (sw is < 1 or > Limits.MaxSide || sh is < 1 or > Limits.MaxSide) return null;
        double c = Math.Cos(placement.Radians), s = Math.Sin(placement.Radians);
        PointD Inverse(double x, double y)
        {
            x -= placement.Center.X; y -= placement.Center.Y;
            return new((x * c + y * s) * (placement.FlipX ? -1 : 1) * sw / placement.Size.Width + sw / 2d,
                (-x * s + y * c) * (placement.FlipY ? -1 : 1) * sh / placement.Size.Height + sh / 2d);
        }
        var corners = new[] { Inverse(0, 0), Inverse(source.Manifest.Width, 0),
            Inverse(0, source.Manifest.Height), Inverse(source.Manifest.Width, source.Manifest.Height) };
        // Roundoff at a quarter turn must not introduce a spare row or column.
        int x0 = (int)Math.Floor(Math.Min(0, corners.Min(p => p.X)) + 1e-8);
        int y0 = (int)Math.Floor(Math.Min(0, corners.Min(p => p.Y)) + 1e-8);
        double x1 = Math.Ceiling(Math.Max(sw, corners.Max(p => p.X)) - 1e-8);
        double y1 = Math.Ceiling(Math.Max(sh, corners.Max(p => p.Y)) - 1e-8);
        double w = x1 - x0, h = y1 - y0;
        if (w is < 1 or > Limits.MaxSide || h is < 1 or > Limits.MaxSide || w * h > Limits.MaxSurfacePixels) return null;
        long used = source.Images.Sum(pair => onMask || pair.Key != id ? pair.Value.PixelCount : 0)
            + source.Masks.Sum(pair => !onMask || pair.Key != id ? pair.Value.PixelCount : 0);
        if (!onMask && layer.MaskPlacement is null && mask is not null) used += (long)(w * h) - mask.PixelCount;
        if (used + w * h > Limits.DocumentPixelBudget) return null;
        return new(source, id, onMask, selection, placement, sw, sh, x0, y0, (int)w, (int)h);
    }

    public GradientPreview? Preview(PointD start, PointD end, GradientSettings settings) => RenderPreview(start, end, settings, null, null);

    private GradientPreview? RenderPreview(PointD start, PointD end, GradientSettings settings, PreviewBuffers? buffers, GradientPreview? retained)
    {
        double dx = end.X - start.X, dy = end.Y - start.Y, length2 = dx * dx + dy * dy;
        if (!double.IsFinite(start.X) || !double.IsFinite(start.Y) || !double.IsFinite(length2) || length2 < .25
            || !double.IsFinite(settings.Opacity) || settings.Opacity <= 0) return null;
        var prepared = basis.Value;
        var images = new Dictionary<Guid, Surface>(Source.Images);
        var masks = new Dictionary<Guid, MaskSurface>(Source.Masks);
        var output = prepared.Image is { } image ? buffers is null ? image.Clone()
            : buffers.Copy(image, retained?.Snapshot.Images.GetValueOrDefault(id)) : null;
        var maskOutput = prepared.Mask is { } mask ? buffers is null ? mask.Clone()
            : buffers.Copy(mask, retained?.Snapshot.Masks.GetValueOrDefault(id)) : null;
        double opacity = Math.Clamp(settings.Opacity, 0, 1);
        double inverseLength2 = 1 / length2;
        bool transparent = settings.Style == GradientStyle.ForegroundToTransparent;
        bool radial = settings.Shape == GradientShape.Radial;
        var a = settings.Foreground; var b = transparent ? a : settings.Background;
        double redDelta = b.Red - a.Red, greenDelta = b.Green - a.Green, blueDelta = b.Blue - a.Blue;
        var vectors = fullCoverage && width >= 4 && VectorGradient.Supports(start, end)
            ? new VectorGradient(start, dx, dy, inverseLength2, settings, opacity) : null;
        bool PaintRows(int first, int last)
        {
            bool changed = false;
            for (int y = first; y < last; y++)
            {
                var row = ToDocument(x0 + .5, y + y0 + .5);
                int firstScalar = 0;
                if (vectors is not null)
                {
                    firstScalar = vectors.PaintRow(onMask ? maskOutput!.Pixels : output!.Pixels,
                        y * width, width, row, columnX, columnY, onMask, out bool vectorChanged);
                    changed |= vectorChanged;
                }
                for (int x = firstScalar; x < width; x++)
                {
                    int offset = y * width + x;
                    var point = new PointD(row.X + x * columnX, row.Y + x * columnY);
                    double coverage = fullCoverage ? 1 : PaintCoverage(point);
                    if (coverage <= 0) continue;
                    double px = point.X - start.X, py = point.Y - start.Y;
                    double t = radial ? Math.Sqrt((px * px + py * py) * inverseLength2) : (px * dx + py * dy) * inverseLength2;
                    t = Math.Clamp(t, 0, 1);
                    if (settings.Reversed) t = 1 - t;
                    double alpha = opacity * coverage * (transparent ? 1 - t : 1);
                    if (alpha <= 0) continue;
                    double red = a.Red + redDelta * t;
                    double green = a.Green + greenDelta * t;
                    double blue = a.Blue + blueDelta * t;
                    if (onMask)
                    {
                        byte value = Byte(red * alpha + maskOutput!.Pixels[offset] * (1 - alpha));
                        changed |= value != maskOutput.Pixels[offset];
                        maskOutput.Pixels[offset] = value;
                    }
                    else
                    {
                        var pixels = output!.Pixels;
                        int i = offset * 4;
                        byte r = Byte(red * alpha + pixels[i] * (1 - alpha));
                        byte g = Byte(green * alpha + pixels[i + 1] * (1 - alpha));
                        byte b2 = Byte(blue * alpha + pixels[i + 2] * (1 - alpha));
                        byte a2 = Byte(255 * alpha + pixels[i + 3] * (1 - alpha));
                        changed |= r != pixels[i] || g != pixels[i + 1] || b2 != pixels[i + 2] || a2 != pixels[i + 3];
                        pixels[i] = r; pixels[i + 1] = g; pixels[i + 2] = b2; pixels[i + 3] = a2;
                    }
                }
            }
            return changed;
        }
        bool changed;
        if ((long)width * height < parallelPixels || height <= rowsPerBatch || Environment.ProcessorCount == 1)
            changed = PaintRows(0, height);
        else
        {
            // Each batch owns disjoint output rows; all batches read the same immutable basis.
            // Only the final changed flag is shared, once per batch rather than per pixel.
            int anyChanged = 0;
            Parallel.For(0, (height + rowsPerBatch - 1) / rowsPerBatch, batch =>
            {
                int first = batch * rowsPerBatch;
                if (PaintRows(first, Math.Min(height, first + rowsPerBatch))) Interlocked.Exchange(ref anyChanged, 1);
            });
            changed = anyChanged != 0;
        }
        if (!changed) return null;
        if (buffers is not null) { output?.MarkModified(); maskOutput?.MarkModified(); }
        if (onMask) masks[id] = maskOutput!;
        else
        {
            images[id] = output!;
            if (prepared.AttachedMask is not null) masks[id] = prepared.AttachedMask;
        }
        return new(this, new ProjectSnapshot { Manifest = prepared.Manifest, Images = images, Masks = masks });
    }

    private ProjectManifest BuildPreviewManifest()
    {
        // Geometry and metadata are fixed for the draft, independent of endpoints/colors.
        var manifest = ManifestJson.Deserialize(Manifest);
        var layer = manifest.Layers.First(layer => layer.Id == id);
        var center = ToDocument(x0 + width / 2d, y0 + height / 2d);
        var expanded = Copy(placement);
        expanded.Size = new(width * placement.Size.Width / sourceWidth, height * placement.Size.Height / sourceHeight);
        expanded.Origin = new(center.X - expanded.Size.Width / 2, center.Y - expanded.Size.Height / 2);
        if (!expanded.IsValid) throw new InvalidOperationException("The gradient grid placement is invalid.");
        if (onMask) layer.MaskPlacement = expanded;
        else { layer.Transform = expanded; layer.ImageFile ??= ProjectValidator.Uuid(id) + ".png"; layer.Text = null; layer.Shape = null; }
        return manifest;
    }

    private PreviewBasis BuildBasis()
    {
        // Original-grid preparation runs once on the worker. Lazy also serializes a racing
        // Apply request, without sharing mutable output pixels between preview frames.
        // Resolve feathering here once: its lazy coverage cache otherwise lets every row
        // worker allocate and calculate the same document-sized mask on the first preview.
        if ((long)width * height >= parallelPixels && height > rowsPerBatch && Selection is { Feather: > 0 })
            _ = Selection.CoverageAt(0, 0);
        var layer = geometry.Manifest.Layers.First(layer => layer.Id == id);
        Source.Images.TryGetValue(id, out var original);
        Source.Masks.TryGetValue(id, out var originalMask);
        MaskSurface PrepareMask()
        {
            if (x0 == 0 && y0 == 0 && width == sourceWidth && height == sourceHeight
                && originalMask!.Width == width && originalMask.Height == height) return originalMask;
            var prepared = new MaskSurface(width, height);
            Array.Fill(prepared.Pixels, maskBackground);
            for (int y = Math.Max(0, -y0); y < Math.Min(height, sourceHeight - y0); y++)
            for (int x = Math.Max(0, -x0); x < Math.Min(width, sourceWidth - x0); x++)
                prepared.Pixels[y * width + x] = originalMask!.Pixels[(y + y0) * originalMask.Height / sourceHeight * originalMask.Width
                    + (x + x0) * originalMask.Width / sourceWidth];
            return prepared;
        }
        Surface? image = null;
        MaskSurface? mask = null, attachedMask = null;
        if (onMask) mask = PrepareMask();
        else
        {
            if (x0 == 0 && y0 == 0 && original is not null && original.Width == width && original.Height == height && original.Stride == width * 4)
                image = original;
            else
            {
                image = new Surface(width, height);
                if (original is not null)
                    for (int y = 0; y < original.Height; y++)
                        original.Pixels.AsSpan(y * original.Stride, original.Width * 4)
                            .CopyTo(image.Pixels.AsSpan((y - y0) * image.Stride - x0 * 4, original.Width * 4));
            }
            if (layer.MaskPlacement is null && originalMask is not null) attachedMask = PrepareMask();
        }
        return new(geometry.Manifest, image, mask, attachedMask);
    }

    private PointD ToDocument(double x, double y)
    {
        x = (x / sourceWidth - .5) * placement.Size.Width * (placement.FlipX ? -1 : 1);
        y = (y / sourceHeight - .5) * placement.Size.Height * (placement.FlipY ? -1 : 1);
        return new(placement.Center.X + x * cos - y * sin, placement.Center.Y + x * sin + y * cos);
    }

    private double PaintCoverage(PointD center)
    {
        if (center.X + halfX <= 0 || center.Y + halfY <= 0 || center.X - halfX >= canvasWidth || center.Y - halfY >= canvasHeight) return 0;
        if (center.X - halfX >= -1e-8 && center.Y - halfY >= -1e-8
            && center.X + halfX <= canvasWidth + 1e-8 && center.Y + halfY <= canvasHeight + 1e-8)
            return Selection?.CoverageAt((int)center.X, (int)center.Y) / 255d ?? 1;
        // Only edge pixels need subpixel canvas clipping. Interior pixels retain exact
        // center samples; coverage is an approximation to Core Graphics' clip rasterizer.
        double total = 0;
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++)
        {
            double u = ((x + .5) / 4 - .5) * stepX, v = ((y + .5) / 4 - .5) * stepY;
            double px = center.X + u * cos - v * sin, py = center.Y + u * sin + v * cos;
            if (px >= 0 && py >= 0 && px < canvasWidth && py < canvasHeight)
                total += Selection?.CoverageAt((int)px, (int)py) / 255d ?? 1;
        }
        return total / 16;
    }
    private static byte Byte(double value) => (byte)Math.Clamp((int)Math.Floor(value + .5), 0, 255);
    private static LayerTransform Copy(LayerTransform t) => new() { Origin = t.Origin, Size = t.Size, Rotation = t.Rotation,
        FlipX = t.FlipX, FlipY = t.FlipY, Sampling = t.Sampling };
    private static byte EdgeBackground(MaskSurface mask)
    {
        long total = 0, count = 0;
        for (int y = 0; y < mask.Height; y++)
        for (int x = 0; x < mask.Width; x++)
            if (x == 0 || y == 0 || x == mask.Width - 1 || y == mask.Height - 1)
            { total += mask.Pixels[y * mask.Width + x]; count++; }
        return total * 2 >= count * 255 ? (byte)255 : (byte)0;
    }
}

public sealed partial class EditorDocument
{
    public GradientEdit? BeginGradient(Guid id, bool onMask = false) => CanEditPixels(id, onMask) && (onMask || !KeepsAlpha(id))
        ? GradientEdit.Create(Snapshot, id, onMask, ActiveSelection) : null;

    public bool CommitGradient(GradientPreview preview)
    {
        var edit = preview.Edit;
        if (!ReferenceEquals(Snapshot, edit.Source) || !ReferenceEquals(ActiveSelection, edit.Selection)
            || ManifestJson.Serialize(Snapshot.Manifest) != edit.Manifest) return false;
        // Draft frames share their fixed read-only manifest. Detach it once on commit,
        // so subsequent document edits cannot mutate retained preview metadata.
        var committed = new ProjectSnapshot { Manifest = ManifestJson.Deserialize(ManifestJson.Serialize(preview.Snapshot.Manifest)),
            Images = preview.Snapshot.Images, Masks = preview.Snapshot.Masks };
        if (!LayerProtectionAllows(committed)) return false;
        BeginChange();
        coalesceKey = null;
        Snapshot = committed;
        EndChange();
        return true;
    }
}
