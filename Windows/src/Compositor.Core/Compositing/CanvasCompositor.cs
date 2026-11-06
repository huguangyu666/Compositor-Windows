using System.IO;
using System.Runtime.CompilerServices;

namespace Compositor.Core;

/// <summary>
/// The CPU compositing engine. Renders a project's visible layers bottom to top onto a canvas:
/// each layer's pixels are placed by its transform, its alpha is multiplied by its raster mask,
/// the masks of every folder above it, and the live (clipping) masks it is linked to, then the
/// result blends on with the layer's opacity and blend mode. A layer with effects draws their
/// render instead — pixels with shadow, glow and stroke around them, the layer's own mask
/// already applied inside the render — placed through the transform grown by the render's
/// inset, as ImageExporter.swift composites them. Folders are pass-through; an adjustment
/// layer's own rendering arrives with the adjustment work.
/// </summary>
public sealed partial class CanvasCompositor
{
    private readonly Surface canvas;
    private Surface? layerBuffer;
    // Cached settled draws use their own placed buffer. Allocate the shared fallback only
    // when an interactive frame, adjustment, or layer beyond the cache limit needs it.
    private Surface SharedLayerBuffer => layerBuffer ??= new(canvas.Width, canvas.Height);
    private readonly int documentWidth, documentHeight;
    private readonly bool cacheLayers;
    private Dictionary<Guid, ProjectLayerRecord> byId = null!;
    private Dictionary<Guid, double> opacities = null!;
    private Dictionary<Guid, (int X0, int Y0, int X1, int Y1)> bounds = null!;
    private Dictionary<Guid, MaskSurface?> layerMasks = null!;
    private Dictionary<Guid, (LayerTransform Transform, MaskSurface? Mask)?> unlinkedPlacements = null!;
    private Dictionary<Guid, List<(Guid Id, Surface Image, LayerTransform Transform, double Opacity, MaskSurface? Mask)>> liveSources = null!;
    private List<Guid> drawnOrder = null!;
    private readonly Dictionary<Guid, LayerCache> caches = new();
    private readonly Dictionary<Guid, ContentCache> contentCaches = new();
    private readonly Dictionary<Guid, EffectsCache> effectsCaches = new();

    /// <summary>Caches beyond this many drawn layers fall back to the shared buffer, to bound memory.</summary>
    private const int MaxCaches = 24;

    /// <summary>One drawn layer's placed, masked content: invalidated by an exact fingerprint
    /// of everything that feeds it, and tracked by the regions recomputed since.</summary>
    private sealed class LayerCache
    {
        public Surface Buffer = null!;
        public ulong Fingerprint;
        public ulong BelowFingerprint;
        public List<(int X0, int Y0, int X1, int Y1)> Valid = new();
    }

    /// <summary>
    /// A layer's pixels resampled at this compositor's scale, independent of position. A drag
    /// changes only the transform's origin, so an interactive frame blits this instead of
    /// re-resampling the source every tick — the macOS Metal texture draw, but at full
    /// resolution: the settled frame (drag end) recomputes through the exact single-pass path.
    /// </summary>
    private sealed class ContentCache
    {
        public Surface Content = null!;
        public ulong Fingerprint;
    }

    /// <summary>
    /// One layer's effects: the typed settings parsed from the manifest's Effects JSON
    /// (re-parsed when its raw text changes, since the records mutate live under the
    /// compositor), and the render they last came from — keyed by the source pixels, the
    /// layer's own mask as the render takes it, and the settings themselves. The render is
    /// position-free, so moves and drags reuse it; only new pixels or settings redo it.
    /// </summary>
    private sealed class EffectsCache
    {
        public string Raw = "";
        public LayerEffects? Effects;
        public ulong Hash;
        public ulong SourceKey;
        public ulong MaskKey;
        public ulong MaskGeometryKey;
        private WeakReference<MaskSurface>? mask;
        public MaskSurface? Mask
        {
            get => mask is not null && mask.TryGetTarget(out var value) ? value : null;
            set
            {
                if (value is null) mask = null;
                else if (mask is null) mask = new(value);
                else mask.SetTarget(value);
            }
        }
        public int MaskRevision;
        public MaskSurface? GridMask;
        public Surface? Rendered;
        public SourceWindow? Window;
        public RectI WindowValid;
        public int Inset;
        private WeakReference<Surface>? source;
        public Surface? Source
        {
            get => source is not null && source.TryGetTarget(out var value) ? value : null;
            set
            {
                if (value is null) source = null;
                else if (source is null) source = new(value);
                else source.SetTarget(value);
            }
        }
        public int SourceRevision;
        public ulong RenderedHash;
        public ulong PlacementKey;
        public RectI LastBounds;
        public EffectScratch? Scratch;
    }

    /// <summary>
    /// Renders the document into a canvas of <paramref name="scale"/> × document size
    /// (0.5 = half resolution).
    /// </summary>
    /// <param name="scale">Render scale, 1 = document resolution.</param>
    public CanvasCompositor(ProjectSnapshot snapshot, double scale = 1.0, bool cacheLayers = true)
    {
        this.cacheLayers = cacheLayers;
        Snapshot = snapshot;
        documentWidth = snapshot.Manifest.Width; documentHeight = snapshot.Manifest.Height;
        Scale = scale;
        canvas = new Surface((int)Math.Ceiling(snapshot.Manifest.Width * scale), (int)Math.Ceiling(snapshot.Manifest.Height * scale));
        RebuildSnapshot(snapshot);
    }

    /// <summary>Switches same-sized draft state on the serial render worker. Canvas and layer
    /// buffers stay allocated; fingerprints invalidate only changed content. The caller must
    /// render the affected region (or the full canvas after structural edits).</summary>
    public void SetSnapshot(ProjectSnapshot snapshot)
    {
        if (snapshot.Manifest.Width != documentWidth || snapshot.Manifest.Height != documentHeight)
            throw new ArgumentException("A compositor cannot switch to a different canvas size.", nameof(snapshot));
        if (ReferenceEquals(snapshot, Snapshot)) return;
        RebuildSnapshot(snapshot);
        var eligible = drawnOrder.Take(MaxCaches).ToHashSet();
        foreach (var id in caches.Keys.Where(id => !eligible.Contains(id)).ToArray()) caches.Remove(id);
        foreach (var id in contentCaches.Keys.Where(id => !eligible.Contains(id)).ToArray()) contentCaches.Remove(id);
        foreach (var id in effectsCaches.Keys.Where(id => !byId.ContainsKey(id)).ToArray()) effectsCaches.Remove(id);
        foreach (var id in lastPrints.Keys.Where(id => !byId.ContainsKey(id)).ToArray()) lastPrints.Remove(id);
    }

    private void RebuildSnapshot(ProjectSnapshot snapshot)
    {
        Snapshot = snapshot;
        byId = snapshot.Manifest.Layers.ToDictionary(layer => layer.Id, layer => layer);
        opacities = EffectiveOpacity.All(snapshot.Manifest.Layers);
        Order = DrawOrder.Resolve(snapshot.Manifest.Layers);
        // Every lookup below was recomputed per layer per render; a region render walks the
        // layer list many times a second while painting, so they are built once here. Bounds
        // come from the placement transform — grown by the effects inset, so what the effects
        // draw around the layer's box is part of the layer's footprint.
        bounds = new Dictionary<Guid, (int, int, int, int)>(byId.Count);
        foreach (var layer in snapshot.Manifest.Layers)
        {
            bounds[layer.Id] = Resampler.BoundingRect(PlacementTransform(layer), canvas.Width, canvas.Height);
        }
        layerMasks = snapshot.Manifest.Layers.ToDictionary(
            layer => layer.Id,
            layer => layer.MaskFile is not null && layer.MaskEnabled != false && snapshot.Masks.TryGetValue(layer.Id, out var mask) ? mask : null);
        unlinkedPlacements = new Dictionary<Guid, (LayerTransform, MaskSurface?)?>(byId.Count);
        liveSources = new Dictionary<Guid, List<(Guid, Surface, LayerTransform, double, MaskSurface?)>>(byId.Count);
        foreach (var layer in snapshot.Manifest.Layers)
        {
            if (layer.MaskSourceId is null) continue;
            var chain = new List<(Guid, Surface, LayerTransform, double, MaskSurface?)>();
            Guid? sourceId = layer.MaskSourceId;
            int depth = 0;
            while (sourceId is { } id && depth < 256 && byId.TryGetValue(id, out var source))
            {
                double sourceOpacity = opacities.TryGetValue(source.Id, out double value) ? value : source.OpacityOrOne;
                if (sourceOpacity > 0 && source.ImageFile is not null && snapshot.Images.TryGetValue(source.Id, out var sourceImage))
                {
                    MaskSurface? sourceMask = source.MaskFile is not null && source.MaskEnabled != false && snapshot.Masks.TryGetValue(source.Id, out var m) ? m : null;
                    chain.Add((source.Id, sourceImage, source.Transform, sourceOpacity, sourceMask));
                }
                else
                {
                    chain.Clear(); // a blank source contributes no coverage at all
                    break;
                }
                sourceId = source.MaskSourceId;
                depth += 1;
            }
            liveSources[layer.Id] = chain;
        }
        drawnOrder = [.. Order.Drawn];
    }

    public ProjectSnapshot Snapshot { get; private set; }

    public DrawOrder.Result Order { get; private set; } = null!;

    /// <summary>The render scale this compositor works at (1 = document resolution).</summary>
    public double Scale { get; }

    public int Width => canvas.Width;

    public int Height => canvas.Height;

    /// <summary>A transform mapped into this compositor's scaled space.</summary>
    private LayerTransform Scaled(LayerTransform transform) => new LayerTransform
    {
        Origin = new PointD(transform.Origin.X * Scale, transform.Origin.Y * Scale),
        Size = new SizeD(transform.Size.Width * Scale, transform.Size.Height * Scale),
        Rotation = transform.Rotation,
        FlipX = transform.FlipX,
        FlipY = transform.FlipY,
        Sampling = transform.Sampling,
    };

    /// <summary>The composited document. The returned surface is reused across renders; clone it to keep it.</summary>
    public Surface Render() => Render(0, 0, canvas.Width, canvas.Height);

    /// <summary>
    /// Renders everything below (and including) the layers that precede `stopBeforeLayerId`
    /// in drawing order — what a Levels panel histograms: the content an adjustment layer
    /// acts on, before its own effect.
    /// </summary>
    public Surface RenderUntil(Guid stopBeforeLayerId, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var below = Order.Order.TakeWhile(id => id != stopBeforeLayerId).ToHashSet();
        Array.Clear(canvas.Pixels);
        var prints = ComputeFingerprints();
        for (int drawnIndex = 0; drawnIndex < Order.Drawn.Count; drawnIndex++)
        {
            var id = Order.Drawn[drawnIndex];
            if (!below.Contains(id)) continue;
            cancellation.ThrowIfCancellationRequested();
            if (!byId.TryGetValue(id, out var layer)) continue;
            DrawLayer(layer, 0, 0, canvas.Width, canvas.Height, drawnIndex, interactive: false, prints);
        }
        cancellation.ThrowIfCancellationRequested();
        return canvas;
    }

    /// <summary>
    /// Recomposites just a region of the canvas: the region is cleared and every layer is
    /// redrawn clipped to it. Brush strokes and drags update their rectangle only, which is
    /// what keeps painting responsive on large documents.
    /// </summary>
    /// <param name="interactive">A transient frame of a live placement drag (move or
    /// transform): layers place from the position-free content cache instead of re-resampling
    /// the source, and nothing is stored — the frame that settles recomputes exactly.</param>
    /// <param name="cacheAdjustments">Store recomputed adjustment output for reuse. Exact
    /// transform drafts can skip these copies without changing source sampling.</param>
    public Surface Render(int x0, int y0, int x1, int y1, bool interactive = false, bool cacheAdjustments = true)
    {
        maskDisplayId = null;
        x0 = Math.Clamp(x0, 0, canvas.Width);
        y0 = Math.Clamp(y0, 0, canvas.Height);
        x1 = Math.Clamp(x1, 0, canvas.Width);
        y1 = Math.Clamp(y1, 0, canvas.Height);
        if (x0 >= x1 || y0 >= y1) return canvas;
        for (int y = y0; y < y1; y++)
        {
            Array.Clear(canvas.Pixels, y * canvas.Stride + x0 * 4, (x1 - x0) * 4);
        }
        var prints = ComputeFingerprints();
        for (int index = 0; index < drawnOrder.Count; index++)
        {
            if (!byId.TryGetValue(drawnOrder[index], out var layer)) continue;
            DrawLayer(layer, x0, y0, x1, y1, index, interactive, prints, cacheAdjustments);
        }
        RememberRenderedMaskGeometry();
        return canvas;
    }

    /// <summary>
    /// Forgets one layer's placed and content caches, so the next render of its region
    /// recomputes from the source — the exact single-pass placement a settled drag lands on
    /// after its interactive frames placed from the cache.
    /// </summary>
    public void DropCachesFor(Guid layerId)
    {
        caches.Remove(layerId);
        contentCaches.Remove(layerId);
    }

    /// <summary>The lightweight per-layer fingerprint table, computed once per render pass.</summary>
    private ulong[] ComputeFingerprints()
    {
        var prints = new ulong[drawnOrder.Count];
        for (int i = 0; i < drawnOrder.Count; i++)
        {
            if (byId.TryGetValue(drawnOrder[i], out var layer)) prints[i] = Fingerprint(layer);
        }
        return prints;
    }

    private readonly Dictionary<Guid, ulong> lastPrints = new();

    private LayerCache? CacheFor(Guid id, int drawnIndex, ulong fingerprint)
    {
        // Bounds refresh is tied to the fingerprint, not to cache eligibility: layers beyond
        // MaxCaches have no cache but still need current bounds to be drawn at all. The
        // recomputation must use the Scaled transform — bounds live in this compositor's
        // (possibly scaled) space, same as the construction path.
        if (!lastPrints.TryGetValue(id, out var previous) || previous != fingerprint)
        {
            if (byId.TryGetValue(id, out var changed))
            {
                bounds[id] = Resampler.BoundingRect(PlacementTransform(changed), canvas.Width, canvas.Height);
            }
        }
        lastPrints[id] = fingerprint;

        if (!cacheLayers || drawnIndex >= MaxCaches) return null;
        if (!caches.TryGetValue(id, out var cache))
        {
            caches[id] = cache = new LayerCache { Buffer = new Surface(canvas.Width, canvas.Height) };
        }
        if (cache.Fingerprint != fingerprint)
        {
            cache.Fingerprint = fingerprint;
            cache.Valid.Clear();
        }
        return cache;
    }

    /// <summary>
    /// The layer's resampled content, free of position: hit when the transform only moved
    /// (rotation zero, mask linked), so a drag tick places it instead of re-resampling the
    /// source. The stored surface is sized by the same bounding math the placement uses, and
    /// resampled with the layer's own sampling so the placement matches the exact path. With
    /// effects on, `source` is their render and `transform` its grown placement — both
    /// position-free, so the cache holds them just the same.
    /// </summary>
    private ContentCache? ContentCacheFor(ProjectLayerRecord layer, Surface source, LayerTransform transform)
    {
        if (!cacheLayers || layer.Transform.Rotation != 0 || layer.MaskLinked == false) return null;
        if (contentCaches.Count >= MaxCaches && !contentCaches.ContainsKey(layer.Id)) return null;
        ulong fingerprint = Fingerprint(layer, includeOrigin: false);
        if (!contentCaches.TryGetValue(layer.Id, out var cache))
        {
            contentCaches[layer.Id] = cache = new ContentCache();
        }
        if (cache.Fingerprint != fingerprint)
        {
            var zeroed = Translated(transform, new PointD(0, 0));
            var (cx0, cy0, cx1, cy1) = Resampler.BoundingRect(zeroed, Limits.MaxSide, Limits.MaxSide);
            int width = cx1 - cx0, height = cy1 - cy0;
            if (width < 1 || height < 1 || (long)width * height > Limits.MaxSurfacePixels)
            {
                contentCaches.Remove(layer.Id);
                return null;
            }
            cache.Content = new Surface(width, height);
            Resampler.Draw(source, zeroed, cache.Content, 0, 0, width, height, transform.Sampling);
            cache.Fingerprint = fingerprint;
        }
        return cache;
    }

    /// <summary>The same transform with its origin replaced — content caches are position-free.</summary>
    private static LayerTransform Translated(LayerTransform transform, PointD origin) => new()
    {
        Origin = origin,
        Size = transform.Size,
        Rotation = transform.Rotation,
        FlipX = transform.FlipX,
        FlipY = transform.FlipY,
        Sampling = transform.Sampling,
    };

    private static bool Covered(List<(int X0, int Y0, int X1, int Y1)> valid, int x0, int y0, int x1, int y1)
    {
        foreach (var (vx0, vy0, vx1, vy1) in valid)
        {
            if (vx0 <= x0 && vy0 <= y0 && vx1 >= x1 && vy1 >= y1) return true;
        }
        return false;
    }

    private void DrawLayer(ProjectLayerRecord layer, int rx0, int ry0, int rx1, int ry1, int drawnIndex, bool interactive, ulong[] prints, bool cacheAdjustments = true)
    {
        ulong fingerprint = prints[drawnIndex];
        ulong belowFingerprint = BelowFingerprint(drawnIndex, prints);
        var cache = CacheFor(layer.Id, drawnIndex, fingerprint);
        // An adjustment layer changes the pixels below it within its own transform rect.
        if (layer.Adjustment is { } adjustment)
        {
            DrawAdjustment(layer, adjustment, rx0, ry0, rx1, ry1, cache, belowFingerprint, cacheAdjustments && !interactive);
            return;
        }

        var images = Snapshot.Images;
        if (layer.ImageFile is null || !images.TryGetValue(layer.Id, out var source)) return;

        var (bx0, by0, bx1, by1) = bounds[layer.Id];
        int x0 = Math.Max(bx0, rx0);
        int y0 = Math.Max(by0, ry0);
        int x1 = Math.Min(bx1, rx1);
        int y1 = Math.Min(by1, ry1);
        if (x0 >= x1 || y0 >= y1) return;

        // The layer's effects replace the plain draw: their render holds the pixels with
        // everything drawn around and over them, the layer's own mask applied inside, and
        // draws placed through the transform grown by the render's inset (ImageExporter.swift's
        // drawLayer: grown placement, mask nil, the layer's opacity and blend as they are).
        Surface target;
        if (cache is not null && Covered(cache.Valid, x0, y0, x1, y1))
        {
            target = cache.Buffer; // cache hit: blend the placed pixels, skip resample and masks
        }
        else
        {
            var effects = RenderedEffectsFor(layer, source, new(x0, y0, x1 - x0, y1 - y0));
            var drawSource = effects ?? new SourceWindow(source);
            var transform = PlacementTransform(layer);
            // A live placement frame: the position-free content cache turns the tick into one
            // placement blit when the transform only moved. Nothing is stored — the settled
            // frame recomputes through the exact path, so a drag never leaves the cached
            // (double-resampled) placement on the canvas.
            target = interactive ? SharedLayerBuffer : cache?.Buffer ?? SharedLayerBuffer;
            ClearBufferRegion(target, x0, y0, x1, y1);
            // A cropped effects window must retain its global grid, so place it directly.
            var content = interactive && !drawSource.IsWindow ? ContentCacheFor(layer, drawSource.Image, transform) : null;
            if (content is { } positioned)
            {
                Resampler.Draw(positioned.Content, transform, target, x0, y0, x1, y1, transform.Sampling);
            }
            else
            {
                Resampler.Draw(drawSource, transform, target, x0, y0, x1, y1);
            }
            // The layer's own mask went into the effects render; folder and live masks still
            // multiply the placed result, as the macOS composite applies them around the draw.
            if (effects is null) ApplyLayerMask(layer, target, x0, y0, x1, y1);
            ApplyFolderMasks(layer, target, x0, y0, x1, y1);
            ApplyLiveMasks(layer, target, x0, y0, x1, y1);
            if (!interactive) cache?.Valid.Add((x0, y0, x1, y1));
        }

        double opacity = LiveOpacity(layer);
        Composite(target, layer.BlendMode ?? LayerBlendMode.Normal, (float)opacity, x0, y0, x1, y1);
    }

    /// <summary>
    /// The layer's opacity multiplied by every ancestor folder's, computed live — caching it
    /// at construction left the opacity slider painting a stale value (SetOpacity only
    /// raises PixelsChanged, which does not rebuild the compositor).
    /// </summary>
    private double LiveOpacity(ProjectLayerRecord layer)
    {
        double opacity = layer.OpacityOrOne;
        Guid? parent = layer.ParentId;
        int depth = 0;
        while (parent is { } parentId && depth < 64 && byId.TryGetValue(parentId, out var node))
        {
            opacity *= node.OpacityOrOne;
            parent = node.ParentId;
            depth += 1;
        }
        return opacity;
    }

    /// <summary>Clears just the working rectangle of a buffer.</summary>
    private static void ClearBufferRegion(Surface target, int x0, int y0, int x1, int y1)
    {
        for (int y = y0; y < y1; y++)
        {
            Array.Clear(target.Pixels, y * target.Stride + x0 * 4, (x1 - x0) * 4);
        }
    }

    // --- Layer effects: the render the layer draws instead of its plain pixels ---

    /// <summary>
    /// The layer's typed effects, re-parsed whenever the manifest's Effects JSON changes under
    /// it (the records mutate live), with kinds the JSON marks disabled dropped. Layers with
    /// no Effects JSON stop at the null check — the pre-effects path, at zero cost.
    /// </summary>
    private EffectsCache EffectsFor(ProjectLayerRecord layer)
    {
        if (!effectsCaches.TryGetValue(layer.Id, out var cache))
        {
            effectsCaches[layer.Id] = cache = new EffectsCache();
        }
        if (layer.Effects is { } element)
        {
            string raw = element.GetRawText();
            if (raw != cache.Raw)
            {
                cache.Raw = raw;
                cache.Effects = LayerEffectsRenderer.Visible(element);
                cache.Hash = FnvOf(raw);
                cache.Rendered = null; // the settings changed; the render no longer matches
                cache.Window = null;
                cache.Scratch = null;
            }
        }
        else if (cache.Raw.Length != 0)
        {
            cache.Raw = "";
            cache.Effects = null;
            cache.Hash = 0;
            cache.Rendered = null;
            cache.Window = null;
            cache.Scratch = null;
        }
        return cache;
    }

    private static ulong FnvOf(string text)
    {
        ulong hash = FnvOffset;
        foreach (char c in text) hash = Mix(hash, c);
        return hash;
    }

    /// <summary>
    /// The inset the layer's effects render grows by, 0 when it has none or they cannot be
    /// made — the grown surface over the pixel budget, where the macOS app draws the layer
    /// as it is (LayerEffectsRenderer.cached's nil).
    /// </summary>
    private int EffectsInset(ProjectLayerRecord layer)
    {
        var cache = EffectsFor(layer);
        if (cache.Effects is not { } effects) return 0;
        if (layer.ImageFile is null || !Snapshot.Images.TryGetValue(layer.Id, out var source)) return 0;
        int inset = LayerEffectsRenderer.Margin(effects);
        long width = (long)source.Width + inset * 2, height = (long)source.Height + inset * 2;
        if (width > Limits.MaxSide || height > Limits.MaxSide || width * height > Limits.MaxSurfacePixels) return 0;
        return inset;
    }

    /// <summary>
    /// The transform the layer draws through: its own, or that grown by the effects inset —
    /// the same proportion LayerEffectsRenderer.placed grows by, around the same center, so
    /// the margin lands around the placed pixels (effects are laid out in layer pixels, so
    /// moving, scaling or rotating the layer moves their render with it).
    /// </summary>
    private LayerTransform PlacementTransform(ProjectLayerRecord layer)
    {
        var transform = layer.Transform;
        int inset = EffectsInset(layer);
        if (inset == 0 || !Snapshot.Images.TryGetValue(layer.Id, out var source)) return Scaled(transform);
        double width = source.Width + (double)inset * 2, height = source.Height + (double)inset * 2;
        var center = transform.Center;
        return Scaled(new LayerTransform
        {
            Size = new SizeD(transform.Size.Width * width / source.Width, transform.Size.Height * height / source.Height),
            Origin = new PointD(center.X - transform.Size.Width * width / source.Width / 2,
                                center.Y - transform.Size.Height * height / source.Height / 2),
            Rotation = transform.Rotation,
            FlipX = transform.FlipX,
            FlipY = transform.FlipY,
            Sampling = transform.Sampling,
        });
    }

    /// <summary>
    /// The layer's pixels with their effects rendered — shadow behind, stroke around, pixels
    /// on top, inside effects over them — at source resolution, with the layer's own mask
    /// applied inside: a linked mask rides the layer (stretched over the grid), one placed
    /// apart is resampled into it through its placement. Folder and live masks are not the
    /// render's business; they multiply the placed result as they do for any layer.
    /// </summary>
    private SourceWindow? RenderedEffectsFor(ProjectLayerRecord layer, Surface source, RectI requested, bool generalMapping = false)
    {
        var cache = EffectsFor(layer);
        if (cache.Effects is null)
        {
            if (cache.Source is not null)
            {
                cache.Source = source; cache.SourceRevision = source.Revision;
                cache.RenderedHash = cache.Hash; cache.MaskKey = EffectsMaskKey(layer);
                RememberMask(layer, cache);
                cache.PlacementKey = MixTransform(FnvOffset, layer.Transform);
                var (x0, y0, x1, y1) = Resampler.BoundingRect(Scaled(layer.Transform), canvas.Width, canvas.Height);
                cache.LastBounds = new(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
            }
            return null;
        }
        int inset = EffectsInset(layer);
        if (inset == 0) return null;

        int sourceRevision = source.Revision;
        ulong sourceKey = Mix(Mix(FnvOffset, (ulong)System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(source)), (ulong)sourceRevision);
        ulong maskKey = EffectsMaskKey(layer);
        bool apart = layer.MaskPlacement is { } placement && !SameGeometry(placement, layer.Transform);
        if (cache.Rendered is not null && cache.SourceKey == sourceKey && cache.MaskKey == maskKey)
        {
            RememberPlacement();
            return new SourceWindow(cache.Rendered);
        }

        MaskSurface? gridMask = null;
        bool knownChanges = TryGetEffectChanges(layer, source, cache, out var dirty);
        if (layerMasks.TryGetValue(layer.Id, out var ownedMask) && ownedMask is { } own)
        {
            if (apart)
            {
                gridMask = cache.GridMask is not null && (cache.MaskKey == maskKey || knownChanges)
                    && cache.GridMask.Width == source.Width && cache.GridMask.Height == source.Height
                    ? cache.GridMask
                    : RasterizeMaskIntoGrid(own, layer.MaskPlacement!, layer.Transform, source.Width, source.Height);
                if (ReferenceEquals(gridMask, cache.GridMask) && cache.MaskKey != maskKey && knownChanges)
                    RasterizeMaskIntoGridRegion(own, layer.MaskPlacement!, layer.Transform, gridMask, dirty);
            }
            else
            {
                gridMask = own;
            }
        }

        const long budget = 64 * 1024 * 1024;
        long gridBytes = apart && gridMask is not null ? gridMask.Pixels.LongLength : 0;
        cache.GridMask = gridBytes <= budget && apart ? gridMask : null;
        int fullWidth = source.Width + inset * 2, fullHeight = source.Height + inset * 2;
        if ((long)fullWidth * fullHeight * 4 + gridBytes > budget)
        {
            var output = Resampler.RequiredSourceRegion(fullWidth, fullHeight, PlacementTransform(layer), requested, generalMapping);
            if (cache.Window is { } previousWindow && cache.SourceKey == sourceKey && cache.MaskKey == maskKey
                && cache.RenderedHash == cache.Hash && Contains(cache.WindowValid, output))
            {
                RememberPlacement();
                return previousWindow;
            }
            cache.Window = null; cache.Rendered = null;
            var inputs = LayerEffectsRenderer.RegionWindow(source, cache.Effects, output);
            long workspaceBytes = EffectScratch.MaximumBytes(inputs.Width, inputs.Height);
            if (workspaceBytes <= 2 * 1024 * 1024 && workspaceBytes + gridBytes <= budget)
                cache.Scratch ??= new();
            else cache.Scratch = null;
            var (pixels, window) = LayerEffectsRenderer.RenderRegion(source, gridMask, cache.Effects, output, cache.Scratch);
            var view = new SourceWindow(pixels, fullWidth, fullHeight, window.X, window.Y);
            long retainedBytes = cache.Scratch is null ? (long)pixels.Stride * pixels.Height : workspaceBytes;
            if (retainedBytes + gridBytes <= budget)
            {
                cache.Window = view; cache.WindowValid = output;
            }
            RememberSource(inset); RememberPlacement();
            return view;
        }

        Surface rendered; int renderInset;
        if (cache.Rendered is { } previous && knownChanges)
        {
            // A small reusable workspace counts against the same entry limit as the output.
            // Large windows remain temporary and oversized outputs retain no workspace.
            if ((long)previous.Stride * previous.Height + gridBytes + 2 * 1024 * 1024 <= budget)
                cache.Scratch ??= new();
            else cache.Scratch = null;
#if DEBUG
            long updateBefore = GC.GetAllocatedBytesForCurrentThread();
            LayerEffectsRenderer.Update(previous, source, gridMask, cache.Effects, dirty, cache.Scratch);
            LayerEffectsRenderer.UpdateBytes += GC.GetAllocatedBytesForCurrentThread() - updateBefore;
#else
            LayerEffectsRenderer.Update(previous, source, gridMask, cache.Effects, dirty, cache.Scratch);
#endif
            rendered = previous; renderInset = cache.Inset;
        }
        else
        {
            cache.Scratch = null;
            (rendered, renderInset) = LayerEffectsRenderer.Render(source, gridMask, cache.Effects);
        }
        RememberSource(renderInset);
        RememberPlacement();
        // Kept the way the macOS render cache keeps its entries: within a memory budget, so
        // one huge layer's effects don't pin the document's worth of renders in memory.
        cache.Rendered = (long)rendered.Stride * rendered.Height + gridBytes <= budget ? rendered : null;
        cache.Window = null;
        if (cache.Rendered is null) cache.Scratch = null;
        return new SourceWindow(rendered);

        void RememberSource(int margin)
        {
            cache.SourceKey = sourceKey; cache.Source = source; cache.SourceRevision = sourceRevision;
            cache.RenderedHash = cache.Hash; cache.MaskKey = maskKey; cache.Inset = margin;
            RememberMask(layer, cache);
        }

        void RememberPlacement()
        {
            cache.PlacementKey = MixTransform(FnvOffset, layer.Transform);
            var (x0, y0, x1, y1) = Resampler.BoundingRect(PlacementTransform(layer), canvas.Width, canvas.Height);
            cache.LastBounds = new(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
        }
    }

    private static bool Contains(RectI outer, RectI inner) => outer.X <= inner.X && outer.Y <= inner.Y
        && outer.X + outer.Width >= inner.X + inner.Width && outer.Y + outer.Height >= inner.Y + inner.Height;

    private ulong EffectsMaskKey(ProjectLayerRecord layer)
    {
        ulong key = FnvOffset;
        if (layerMasks.TryGetValue(layer.Id, out var owned) && owned is not null)
        {
            key = Mix(Mix(FnvOffset, (ulong)RuntimeHelpers.GetHashCode(owned)), (ulong)owned.Revision);
            if (layer.MaskPlacement is { } placement && !SameGeometry(placement, layer.Transform))
                key = MixTransform(MixTransform(key, placement), layer.Transform);
        }
        return key;
    }

    /// <summary>
    /// A mask placed apart from its layer, resampled into the layer's source grid: each grid
    /// pixel's unit point goes through the layer's placement to the document and back through
    /// the mask's, sampled nearest. Coverage exists only inside the mask's own pixel area —
    /// anything outside hides, the convention this port's mask placement follows.
    /// </summary>
    private static MaskSurface RasterizeMaskIntoGrid(MaskSurface mask, LayerTransform placement, LayerTransform layer, int width, int height)
    {
        var grid = new MaskSurface(width, height);
        RasterizeMaskIntoGridRegion(mask, placement, layer, grid, new(0, 0, width, height));
        return grid;
    }

    private static void RasterizeMaskIntoGridRegion(MaskSurface mask, LayerTransform placement, LayerTransform layer, MaskSurface grid, RectI region)
    {
        int width = grid.Width, height = grid.Height;
        double cos = Math.Cos(placement.Radians), sin = Math.Sin(placement.Radians);
        var center = placement.Center;
        // This grid only exists for a mask placed apart from the layer, so the plate's edge tone
        // is what fills the layer pixels it does not cover (`LayerMask.placed` fills, then draws).
        byte outside = MaskDisplay.Background(mask);
        for (int y = region.Y; y < region.Y + region.Height; y++)
        {
            for (int x = region.X; x < region.X + region.Width; x++)
            {
                double u = (x + .5) / width, v = (y + .5) / height;
                var doc = layer.Point(new PointD(layer.FlipX ? 1 - u : u, layer.FlipY ? 1 - v : v));
                double localX = doc.X - center.X, localY = doc.Y - center.Y;
                double unitX = (localX * cos + localY * sin) / placement.Size.Width + 0.5;
                double unitY = (-localX * sin + localY * cos) / placement.Size.Height + 0.5;
                if (placement.FlipX) unitX = 1 - unitX;
                if (placement.FlipY) unitY = 1 - unitY;
                double pixelX = unitX * mask.Width, pixelY = unitY * mask.Height;
                grid.Pixels[y * width + x] = pixelX < 0 || pixelY < 0 || pixelX >= mask.Width || pixelY >= mask.Height
                    ? outside
                    : mask.Pixels[(int)pixelY * mask.Width + (int)pixelX];
            }
        }
    }

    /// <summary>Whether two transforms place the same rectangle (sampling aside).</summary>
    private static bool SameGeometry(LayerTransform left, LayerTransform right) =>
        left.Origin == right.Origin && left.Size == right.Size && left.Rotation == right.Rotation
        && left.FlipX == right.FlipX && left.FlipY == right.FlipY;

    // --- Fingerprints: the exact state a layer's cached content depends on ---
    // FNV-1a over the live inputs (transform fields, surface/mask/adjustment revisions,
    // reference identities, ancestor masks, the live chain with source opacity). No
    // serialization: a tick fingerprints every layer, so this stays allocation-free — the
    // one exception being a layer's Effects JSON, hashed from its raw text, which only
    // layers that carry effects pay for.

    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    private static ulong Mix(ulong hash, ulong value)
    {
        hash ^= value;
        return hash * FnvPrime;
    }

    private static ulong Mix(ulong hash, double value) => Mix(hash, (ulong)BitConverter.DoubleToUInt64Bits(value));

    private static ulong Mix(ulong hash, bool value) => Mix(hash, value ? 1UL : 0UL);

    private static ulong MixTransform(ulong hash, LayerTransform transform, bool includeOrigin = true)
    {
        if (includeOrigin)
        {
            hash = Mix(hash, transform.Origin.X);
            hash = Mix(hash, transform.Origin.Y);
        }
        hash = Mix(hash, transform.Size.Width);
        hash = Mix(hash, transform.Size.Height);
        hash = Mix(hash, transform.Rotation);
        hash = Mix(hash, transform.FlipX);
        hash = Mix(hash, transform.FlipY);
        return Mix(hash, (int)transform.Sampling);
    }

    private ulong Fingerprint(ProjectLayerRecord layer) => Fingerprint(layer, includeOrigin: true);

    private ulong Fingerprint(ProjectLayerRecord layer, bool includeOrigin)
    {
        ulong hash = FnvOffset;
        hash = Mix(hash, includeOrigin ? 1UL : 2UL);
        if (layer.Adjustment is { } adjustment)
        {
            hash = Mix(hash, (ulong)adjustment.Revision);
            hash = Mix(hash, (ulong)System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(adjustment));
        }
        else if (layer.ImageFile is not null && Snapshot.Images.TryGetValue(layer.Id, out var source))
        {
            hash = Mix(hash, (ulong)System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(source));
            hash = Mix(hash, (ulong)source.Revision);
        }
        // The effects replace the placed content wholesale, so their JSON takes part in the key.
        hash = Mix(hash, EffectsFor(layer).Hash);
        hash = MixTransform(hash, layer.Transform, includeOrigin);
        hash = MixMaskBlock(hash, layer, includeOrigin);
        // Ancestor folder masks multiply this layer's coverage.
        Guid? parent = layer.ParentId;
        int depth = 0;
        while (parent is { } parentId && depth < 64 && byId.TryGetValue(parentId, out var folder))
        {
            if (layerMasks.TryGetValue(folder.Id, out var mask) && mask is not null)
            {
                hash = Mix(hash, (ulong)System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(mask));
                hash = Mix(hash, (ulong)mask.Revision);
                hash = Mix(hash, folder.MaskLinked ?? true);
                hash = MixTransform(hash, folder.MaskPlacement ?? folder.Transform);
            }
            parent = folder.ParentId;
            depth += 1;
        }
        // The live-mask chain feeds this layer's coverage, including sources' opacity.
        Guid? sourceId = layer.MaskSourceId;
        int chainDepth = 0;
        while (sourceId is { } id && chainDepth < 256 && byId.TryGetValue(id, out var chainSource))
        {
            hash = Mix(hash, opacities.TryGetValue(id, out var sourceOpacity) ? sourceOpacity : chainSource.OpacityOrOne);
            if (chainSource.ImageFile is not null && Snapshot.Images.TryGetValue(chainSource.Id, out var chainImage))
            {
                hash = Mix(hash, (ulong)RuntimeHelpers.GetHashCode(chainImage));
                hash = Mix(hash, (ulong)chainImage.Revision);
                hash = MixTransform(hash, chainSource.Transform);
                // The source's effects render is its coverage; a settings change must reach
                // the clipped layer's cache, not just the source's own.
                hash = Mix(hash, EffectsFor(chainSource).Hash);
                if (layerMasks.TryGetValue(chainSource.Id, out var chainMask) && chainMask is not null)
                {
                    hash = MixMaskBlock(hash, chainSource);
                }
            }
            else
            {
                hash = Mix(hash, 0xDEADUL); // a blank source hides everything
            }
            sourceId = chainSource.MaskSourceId;
            chainDepth += 1;
        }
        return hash;
    }

    private ulong MixMaskBlock(ulong hash, ProjectLayerRecord layer, bool includeOrigin = true)
    {
        bool hasMask = layerMasks.TryGetValue(layer.Id, out var mask) && mask is not null;
        hash = Mix(hash, hasMask);
        if (hasMask)
        {
            hash = Mix(hash, (ulong)System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(mask!));
            hash = Mix(hash, (ulong)mask!.Revision);
            hash = Mix(hash, layer.MaskEnabled ?? true);
            hash = Mix(hash, layer.MaskLinked ?? true);
            // Linkage controls how edits move the mask, not where its pixels are sampled.
            hash = MixTransform(hash, layer.MaskPlacement ?? layer.Transform,
                includeOrigin || layer.MaskPlacement is not null || layer.MaskLinked == false);
        }
        return hash;
    }

    /// <summary>A rolling combination of the prints of every drawn layer before the
    /// adjustment — its cache depends on the composite below it.</summary>
    private static ulong BelowFingerprint(int drawnIndex, ulong[] prints)
    {
        ulong hash = FnvOffset;
        for (int i = 0; i < drawnIndex && i < prints.Length; i++)
        {
            hash = Mix(hash, prints[i]);
        }
        return hash;
    }

    /// <summary>
    /// Renders an adjustment layer: the composite below is adjusted over the region (expanded
    /// by the sampling halo blur kinds need) and blended back through the layer's masks,
    /// opacity and blend mode. The adjusted content is cached against the layer's settings
    /// and the below-fingerprint, so moving one layer re-runs the adjustment across the
    /// changed rectangle only. Transient frames may reuse a current cache without storing output.
    /// </summary>
    private void DrawAdjustment(ProjectLayerRecord layer, LayerAdjustment adjustment, int rx0, int ry0, int rx1, int ry1, LayerCache? cache, ulong belowFingerprint, bool cacheOutput)
    {
        var (bx0, by0, bx1, by1) = bounds[layer.Id];
        int x0 = Math.Max(bx0, rx0);
        int y0 = Math.Max(by0, ry0);
        int x1 = Math.Min(bx1, rx1);
        int y1 = Math.Min(by1, ry1);
        if (x0 >= x1 || y0 >= y1) return;

        double margin = AdjustmentEngine.SamplingMargin(adjustment) * Scale;
        int copyX0 = Math.Max(0, x0 - (int)Math.Ceiling(margin));
        int copyY0 = Math.Max(0, y0 - (int)Math.Ceiling(margin));
        int copyX1 = Math.Min(canvas.Width, x1 + (int)Math.Ceiling(margin));
        int copyY1 = Math.Min(canvas.Height, y1 + (int)Math.Ceiling(margin));
        if (copyX0 >= copyX1 || copyY0 >= copyY1) return;

        // The adjusted content depends on everything below it: a below-layer change (a moved
        // layer, a stroke) invalidates the cache before the coverage check. A transient frame
        // can leave the old cache intact, but must not hit one whose below content changed,
        // or the drag would paint the frozen pre-move composite.
        bool belowChanged = cache is not null && cache.BelowFingerprint != belowFingerprint;
        if (cacheOutput && cache is not null && belowChanged)
        {
            cache.BelowFingerprint = belowFingerprint;
            cache.Valid.Clear();
        }

        bool store = cache is not null && cacheOutput;
        var mode = layer.BlendMode ?? LayerBlendMode.Normal;
        double opacity = LiveOpacity(layer);
        Surface target;
        if (cache is not null && !belowChanged && Covered(cache.Valid, copyX0, copyY0, copyX1, copyY1))
        {
            target = cache.Buffer; // cache hit: the adjusted content is current
        }
        else
        {
            // A full-strength point adjustment with no coverage, or one aligned pixel mask,
            // can work on the canvas itself. Keep settled cache pixels without a round trip.
            if (mode == LayerBlendMode.Normal && opacity >= 1 && margin == 0 && TryAdjustInPlace(layer, adjustment, copyX0, copyY0, copyX1, copyY1))
            {
                if (store)
                {
                    for (int y = copyY0; y < copyY1; y++)
                        Array.Copy(canvas.Pixels, y * canvas.Stride + copyX0 * 4,
                            cache!.Buffer.Pixels, y * cache.Buffer.Stride + copyX0 * 4, (copyX1 - copyX0) * 4);
                    cache!.Valid.Add((copyX0, copyY0, copyX1, copyY1));
                }
                return;
            }
            target = store ? cache!.Buffer : SharedLayerBuffer;
            ClearBufferRegion(target, copyX0, copyY0, copyX1, copyY1);
            for (int y = copyY0; y < copyY1; y++)
            {
                int sourceRow = y * canvas.Stride + copyX0 * 4;
                int targetRow = y * target.Stride + copyX0 * 4;
                int length = (copyX1 - copyX0) * 4;
                Array.Copy(canvas.Pixels, sourceRow, target.Pixels, targetRow, length);
            }
            AdjustmentEngine.Apply(adjustment, target, copyX0, copyY0, copyX1, copyY1);
            // Normal adjustments replace their region. Masks interpolate the effect with
            // the unchanged backdrop, including alpha, rather than cutting a hole in it.
            Surface? backdrop = mode == LayerBlendMode.Normal ? canvas : null;
            ApplyLayerMask(layer, target, copyX0, copyY0, copyX1, copyY1, backdrop);
            ApplyFolderMasks(layer, target, copyX0, copyY0, copyX1, copyY1, backdrop);
            ApplyLiveMasks(layer, target, copyX0, copyY0, copyX1, copyY1, backdrop);
            if (store) cache!.Valid.Add((copyX0, copyY0, copyX1, copyY1));
        }

        if (mode == LayerBlendMode.Normal)
        {
            // The adjusted buffer IS the content below, reprocessed: a Normal adjustment
            // replaces its region, with the opacity choosing how much of the effect applies.
            // Source-over would composite the backdrop with itself.
            if (opacity >= 1)
            {
                // Full opacity replaces the region outright (Round(t·1 + c·0) = t): a row
                // copy, skipping the per-channel Round/Clamp that dominated drag ticks.
                for (int y = copyY0; y < copyY1; y++)
                {
                    Array.Copy(target.Pixels, y * target.Stride + copyX0 * 4,
                        canvas.Pixels, y * canvas.Stride + copyX0 * 4, (copyX1 - copyX0) * 4);
                }
            }
            else
            {
                float keep = (float)(1 - opacity);
                for (int y = copyY0; y < copyY1; y++)
                {
                    int row = y * canvas.Stride;
                    for (int x = copyX0; x < copyX1; x++)
                    {
                        int index = row + x * 4;
                        for (int channel = 0; channel < 4; channel++)
                        {
                            canvas.Pixels[index + channel] = (byte)Math.Clamp(
                                MathF.Round(target.Pixels[index + channel] * (float)opacity + canvas.Pixels[index + channel] * keep), 0, 255);
                        }
                    }
                }
            }
        }
        else
        {
            // A non-Normal blend mode blends the adjusted result against what is below, as
            // the PDF rule does — over a fully transparent backdrop the adjusted color wins.
            Composite(target, mode, (float)opacity, copyX0, copyY0, copyX1, copyY1);
        }
    }

    private bool TryAdjustInPlace(ProjectLayerRecord layer, LayerAdjustment adjustment, int x0, int y0, int x1, int y1)
    {
        if (layer.MaskSourceId is not null) return false;
        Guid? parent = layer.ParentId;
        int depth = 0;
        while (parent is { } id && depth++ < 64 && byId.TryGetValue(id, out var folder))
        {
            if (layerMasks.GetValueOrDefault(id) is not null) return false;
            parent = folder.ParentId;
        }
        if (layerMasks.GetValueOrDefault(layer.Id) is { } mask)
        {
            var placement = Scaled(layer.MaskPlacement ?? layer.Transform);
            if (mask.Width != canvas.Width || mask.Height != canvas.Height || placement.Origin != new PointD(0, 0)
                || placement.Size != new SizeD(canvas.Width, canvas.Height) || placement.Rotation != 0 || placement.FlipX || placement.FlipY)
                return false;
            return AdjustmentEngine.TryApplyMaskedPoint(adjustment, mask, canvas, x0, y0, x1, y1);
        }
        AdjustmentEngine.Apply(adjustment, canvas, x0, y0, x1, y1);
        return true;
    }

    /// <summary>
    /// The tone a mask shows beyond its own pixels, or 0 when it does not: only a mask placed
    /// apart from what it mounts is a plate with an outside. A linked mask (`placement` nil,
    /// or identical geometry) is stretched over its layer's grid, so nothing of that layer can
    /// lie outside it and the edge tone must not paint there.
    /// </summary>
    private static byte OutsideTone(MaskSurface mask, LayerTransform placement, LayerTransform covered)
        => SameGeometry(placement, covered) ? (byte)0 : MaskDisplay.Background(mask);

    private void ApplyLayerMask(ProjectLayerRecord layer, Surface target, int x0, int y0, int x1, int y1, Surface? backdrop = null)
    {
        if (!layerMasks.TryGetValue(layer.Id, out var mask) || mask is null) return;
        var placement = layer.MaskPlacement ?? layer.Transform;
        var transform = Scaled(placement);
        MultiplyCoverage(mask, transform, target, x0, y0, x1, y1, OutsideTone(mask, placement, layer.Transform), backdrop);
    }

    /// <summary>An enabled folder mask dims everything inside the folder, through the folder's own rect.</summary>
    private void ApplyFolderMasks(ProjectLayerRecord layer, Surface target, int x0, int y0, int x1, int y1, Surface? backdrop = null)
    {
        Guid? parent = layer.ParentId;
        int depth = 0;
        while (parent is { } parentId && depth < 64 && byId.TryGetValue(parentId, out var folder))
        {
            if (layerMasks.TryGetValue(folder.Id, out var mask) && mask is not null)
            {
                // A folder's mask clips its members through the folder's own box (`FolderMaskClip`),
                // so a folded-in mask leaves nothing outside that box. Only one explicitly placed
                // apart is a plate, and plates carry their edge tone.
                var placement = folder.MaskPlacement ?? folder.Transform;
                var transform = Scaled(placement);
                MultiplyCoverage(mask, transform, target, x0, y0, x1, y1, OutsideTone(mask, placement, folder.Transform), backdrop);
            }
            parent = folder.ParentId;
            depth += 1;
        }
    }

    /// <summary>
    /// A clipping mask multiplies the layer's coverage with the source layer's own coverage:
    /// its pixel alpha, its transform, its opacity, its raster mask, and its own upstream links.
    /// A source with effects clips by the shape it draws — LiveMaskRenderer.coverage extracts
    /// the alpha of the source drawn through the effects path, so shadow, stroke and glow
    /// spread the clip with it.
    /// </summary>
    private void ApplyLiveMasks(ProjectLayerRecord layer, Surface target, int x0, int y0, int x1, int y1, Surface? backdrop = null)
    {
        if (!liveSources.TryGetValue(layer.Id, out var chain))
        {
            // No clipping source; nothing to do. An empty chain means a blank source hid everything.
            if (byId.TryGetValue(layer.Id, out var record) && record.MaskSourceId is not null)
            {
                ZeroAlpha(target, x0, y0, x1, y1, backdrop);
            }
            return;
        }
        if (chain.Count == 0)
        {
            ZeroAlpha(target, x0, y0, x1, y1, backdrop);
            return;
        }
        foreach (var (sourceId, sourceImage, sourceTransform, sourceOpacity, sourceMask) in chain)
        {
            // The effects render is position-free and cached with the source's own draw, so a
            // tick reuses it at the cost of two key checks. It carries the source's raster mask
            // applied inside, so the separate multiply below would apply it twice; a source
            // whose effects cannot be made (over the pixel budget) falls back to its plain alpha.
            var coverage = new SourceWindow(sourceImage);
            var placement = Scaled(sourceTransform);
            bool maskInsideRender = false;
            if (byId.TryGetValue(sourceId, out var sourceRecord)
                && RenderedEffectsFor(sourceRecord, sourceImage, new(x0, y0, x1 - x0, y1 - y0), generalMapping: true) is { } rendered)
            {
                coverage = rendered;
                placement = PlacementTransform(sourceRecord);
                maskInsideRender = true;
            }
            MultiplyAlphaBySourceCoverage(coverage, placement, sourceOpacity, target, x0, y0, x1, y1, backdrop);
            if (sourceMask is not null && !maskInsideRender)
            {
                // The source's own mask, sampled where its plate sits: a mask placed apart from
                // the source keeps its edge tone out past its pixels, as it does on the source's
                // own draw. The source image's alpha keeps the zero-outside rule (covered above).
                LayerTransform maskTransform = placement;
                byte outside = 0;
                if (byId.TryGetValue(sourceId, out var maskedSource))
                {
                    var maskPlacement = maskedSource.MaskPlacement ?? maskedSource.Transform;
                    maskTransform = Scaled(maskPlacement);
                    outside = OutsideTone(sourceMask, maskPlacement, maskedSource.Transform);
                }
                MultiplyCoverage(sourceMask, maskTransform, target, x0, y0, x1, y1, outside, backdrop);
            }
        }
    }

    private void MultiplyCoverage(MaskSurface mask, LayerTransform transform, Surface target, int x0, int y0, int x1, int y1, byte outside = 0, Surface? backdrop = null)
    {
        var center = transform.Center;
        double radians = transform.Radians;
        double cos = Math.Cos(radians), sin = Math.Sin(radians);
        double width = transform.Size.Width, height = transform.Size.Height;
        // Document-sized masks on photo adjustments usually share the pixel grid. Their
        // centers map directly to integer indices; no inverse transform is needed per pixel.
        bool aligned = radians == 0 && width == mask.Width && height == mask.Height
            && transform.Origin.X == Math.Truncate(transform.Origin.X) && transform.Origin.Y == Math.Truncate(transform.Origin.Y)
            && Math.Abs(transform.Origin.X) <= int.MaxValue && Math.Abs(transform.Origin.Y) <= int.MaxValue;
        int originX = aligned ? (int)transform.Origin.X : 0, originY = aligned ? (int)transform.Origin.Y : 0;

        if ((long)(x1 - x0) * (y1 - y0) >= AdjustmentEngine.ParallelPointPixels)
            Parallel.For(y0, y1, AdjustmentEngine.ParallelPointOptions, ApplyRow);
        else
            for (int y = y0; y < y1; y++) ApplyRow(y);

        void ApplyRow(int y)
        {
            double localY = y + 0.5 - center.Y;
            for (int x = x0; x < x1; x++)
            {
                byte coverage;
                if (aligned)
                {
                    long mx = (long)x - originX, my = (long)y - originY;
                    if (transform.FlipX) mx = mask.Width - 1 - mx;
                    if (transform.FlipY) my = mask.Height - 1 - my;
                    coverage = mx < 0 || mx >= mask.Width || my < 0 || my >= mask.Height ? outside
                        : mask.Pixels[(int)my * mask.Width + (int)mx];
                }
                else
                {
                    double localX = x + 0.5 - center.X;
                    double boxX = localX * cos + localY * sin;
                    double boxY = -localX * sin + localY * cos;
                    double unitX = boxX / width + 0.5, unitY = boxY / height + 0.5;
                    if (transform.FlipX) unitX = 1 - unitX;
                    if (transform.FlipY) unitY = 1 - unitY;
                    coverage = SampleMask(mask, unitX * mask.Width, unitY * mask.Height, outside);
                }
                if (coverage == 255) continue;
                int index = y * target.Stride + x * 4;
                if (backdrop is not null)
                {
                    int backdropIndex = y * backdrop.Stride + x * 4;
                    if (coverage == 0)
                    {
                        Unsafe.WriteUnaligned(ref target.Pixels[index], Unsafe.ReadUnaligned<uint>(ref backdrop.Pixels[backdropIndex]));
                        continue;
                    }
                    for (int channel = 0; channel < 4; channel++)
                        target.Pixels[index + channel] = (byte)((target.Pixels[index + channel] * coverage
                            + backdrop.Pixels[backdropIndex + channel] * (255 - coverage) + 127) / 255);
                    continue;
                }
                if (coverage == 0)
                {
                    Array.Clear(target.Pixels, index, 4);
                    continue;
                }
                // Coverage scales the whole premultiplied pixel, color and alpha together.
                target.Pixels[index] = (byte)(target.Pixels[index] * coverage / 255);
                target.Pixels[index + 1] = (byte)(target.Pixels[index + 1] * coverage / 255);
                target.Pixels[index + 2] = (byte)(target.Pixels[index + 2] * coverage / 255);
                target.Pixels[index + 3] = (byte)(target.Pixels[index + 3] * coverage / 255);
            }
        }
    }

    private void MultiplyAlphaBySourceCoverage(SourceWindow sourceImage, LayerTransform transform, double opacity, Surface target, int x0, int y0, int x1, int y1, Surface? backdrop = null)
    {
        var center = transform.Center;
        double radians = transform.Radians;
        double cos = Math.Cos(radians), sin = Math.Sin(radians);
        double width = transform.Size.Width, height = transform.Size.Height;

        for (int y = y0; y < y1; y++)
        {
            double localY = y + 0.5 - center.Y;
            for (int x = x0; x < x1; x++)
            {
                int index = y * target.Stride + x * 4;
                byte alpha = target.Pixels[index + 3];
                if (alpha == 0 && backdrop is null) continue;
                double localX = x + 0.5 - center.X;
                double boxX = localX * cos + localY * sin;
                double boxY = -localX * sin + localY * cos;
                double unitX = boxX / width + 0.5, unitY = boxY / height + 0.5;
                double pixelX = transform.FlipX ? (1 - unitX) * sourceImage.Width : unitX * sourceImage.Width;
                double pixelY = transform.FlipY ? (1 - unitY) * sourceImage.Height : unitY * sourceImage.Height;
                byte coverage = SampleMaskAlpha(sourceImage, pixelX, pixelY);
                if (coverage == 255 && opacity >= 1) continue;
                float scale = coverage / 255f * (float)opacity;
                if (scale >= 1) continue;
                // The source's coverage scales the whole premultiplied pixel.
                for (int channel = 0; channel < 4; channel++)
                {
                    target.Pixels[index + channel] = (byte)MathF.Round(target.Pixels[index + channel] * scale
                        + (backdrop?.Pixels[index + channel] ?? 0) * (1 - scale));
                }
            }
        }
    }

    private static void ZeroAlpha(Surface target, int x0, int y0, int x1, int y1, Surface? backdrop = null)
    {
        if (backdrop is not null)
        {
            for (int y = y0; y < y1; y++)
                Array.Copy(backdrop.Pixels, y * backdrop.Stride + x0 * 4,
                    target.Pixels, y * target.Stride + x0 * 4, (x1 - x0) * 4);
            return;
        }
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                target.Pixels[y * target.Stride + x * 4 + 3] = 0;
            }
        }
    }

    // Mask coverage samples use the same center convention as the resampler: pixel i's
    // center at i + 0.5, nearest neighbor. A mask mounted on its layer covers exactly that
    // layer's pixel area, so anything outside hides. A mask placed apart is a plate the Mac
    // fills with its edge tone (`LayerMask.placed`): outside its own pixels the caller passes
    // the tone it read from `MaskDisplay.Background`, so a reveal-all plate keeps revealing
    // and a hide-all plate keeps hiding instead of ceding a hard black frame.
    private static byte SampleMask(MaskSurface mask, double pixelX, double pixelY, byte outside = 0)
    {
        if (pixelX < 0 || pixelY < 0 || pixelX >= mask.Width || pixelY >= mask.Height) return outside;
        int x = Math.Clamp((int)pixelX, 0, mask.Width - 1);
        int y = Math.Clamp((int)pixelY, 0, mask.Height - 1);
        return mask.Pixels[y * mask.Width + x];
    }

    private static byte SampleMaskAlpha(SourceWindow source, double pixelX, double pixelY)
    {
        if (pixelX < 0 || pixelY < 0 || pixelX >= source.Width || pixelY >= source.Height) return 0;
        int x = Math.Clamp((int)pixelX, 0, source.Width - 1);
        int y = Math.Clamp((int)pixelY, 0, source.Height - 1);
        return source.Pixels[y * source.Stride + x * 4 + source.Offset + 3];
    }

    /// <summary>
    /// Blends the layer buffer onto the canvas. Normal mode runs in premultiplied space, where
    /// "over" is an addition; every other mode converts to straight colors, applies the W3C
    /// blend, and writes the result back premultiplied.
    /// </summary>
    private void Composite(Surface source, LayerBlendMode mode, float opacity, int x0, int y0, int x1, int y1)
    {
        if (opacity <= 0) return;
        if (mode == LayerBlendMode.Normal && opacity == 1f)
        {
            PremultipliedOver.Apply(source, canvas, new(x0, y0, x1 - x0, y1 - y0));
            return;
        }
        Span<float> backdropColor = stackalloc float[3];
        Span<float> sourceColor = stackalloc float[3];
        Span<float> resultColor = stackalloc float[3];

        for (int y = y0; y < y1; y++)
        {
            int canvasRow = y * canvas.Stride;
            int sourceRow = y * source.Stride;
            for (int x = x0; x < x1; x++)
            {
                int canvasIndex = canvasRow + x * 4;
                int sourceIndex = sourceRow + x * 4;
                float sourceAlpha = source.Pixels[sourceIndex + 3] / 255f * opacity;
                if (sourceAlpha <= 0)
                {
                    continue;
                }
                float backdropAlpha = canvas.Pixels[canvasIndex + 3] / 255f;
                if (mode == LayerBlendMode.Normal)
                {
                    // Premultiplied over: out = src + dst * (1 - a_src).
                    // Fast path first: a fully covered pixel at full opacity replaces the
                    // backdrop outright (Round(src·1 + dst·0) = src), no float math at all —
                    // that is most pixels of a photo layer, and the per-channel Round/Clamp
                    // below dominated drag ticks on large documents.
                    if (sourceAlpha >= 1f)
                    {
                        canvas.Pixels[canvasIndex] = source.Pixels[sourceIndex];
                        canvas.Pixels[canvasIndex + 1] = source.Pixels[sourceIndex + 1];
                        canvas.Pixels[canvasIndex + 2] = source.Pixels[sourceIndex + 2];
                        canvas.Pixels[canvasIndex + 3] = 255;
                        continue;
                    }
                    float keep = 1f - sourceAlpha;
                    for (int channel = 0; channel < 3; channel++)
                    {
                        canvas.Pixels[canvasIndex + channel] = (byte)Math.Clamp(
                            MathF.Round(source.Pixels[sourceIndex + channel] * opacity + canvas.Pixels[canvasIndex + channel] * keep), 0, 255);
                    }
                    canvas.Pixels[canvasIndex + 3] = (byte)Math.Clamp(
                        MathF.Round(source.Pixels[sourceIndex + 3] * opacity + backdropAlpha * 255f * keep), 0, 255);
                    continue;
                }

                float backdropScale = backdropAlpha > 0 ? 1f / backdropAlpha : 0f;
                backdropColor[0] = canvas.Pixels[canvasIndex] / 255f * backdropScale;
                backdropColor[1] = canvas.Pixels[canvasIndex + 1] / 255f * backdropScale;
                backdropColor[2] = canvas.Pixels[canvasIndex + 2] / 255f * backdropScale;
                float sourceUnscale = source.Pixels[sourceIndex + 3] > 0 ? 255f / source.Pixels[sourceIndex + 3] : 0f;
                sourceColor[0] = source.Pixels[sourceIndex] / 255f * sourceUnscale;
                sourceColor[1] = source.Pixels[sourceIndex + 1] / 255f * sourceUnscale;
                sourceColor[2] = source.Pixels[sourceIndex + 2] / 255f * sourceUnscale;

                BlendMath.BlendColor(mode, (backdropColor[0], backdropColor[1], backdropColor[2]), (sourceColor[0], sourceColor[1], sourceColor[2]), resultColor);

                // Straight-space result, the PDF rule CoreGraphics follows: over a fully
                // transparent backdrop the blend is the source itself, over color it is the
                // blend, and partial backdrop alpha interpolates. Composed with the backdrop,
                // then premultiplied by the outgoing alpha.
                float outAlpha = sourceAlpha + backdropAlpha * (1f - sourceAlpha);
                float keepBackdrop = (1f - sourceAlpha) * backdropAlpha;
                for (int channel = 0; channel < 3; channel++)
                {
                    float color = sourceAlpha * (1f - backdropAlpha) * sourceColor[channel]
                        + sourceAlpha * backdropAlpha * resultColor[channel]
                        + keepBackdrop * backdropColor[channel];
                    canvas.Pixels[canvasIndex + channel] = (byte)Math.Clamp(MathF.Round(color * outAlpha * 255f), 0, 255);
                }
                canvas.Pixels[canvasIndex + 3] = (byte)Math.Clamp(MathF.Round(outAlpha * 255f), 0, 255);
            }
        }
    }
}
