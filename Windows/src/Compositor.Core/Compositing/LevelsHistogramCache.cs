using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Compositor.Core;

/// <summary>Frozen render metadata and revision-stamped input assets for one Levels panel.
/// Capture on the document owner; Compute on a worker. It never copies a document's pixels
/// on the UI thread. In-place pixel changes revoke the result before it can be cached.</summary>
public sealed class LevelsHistogramInput
{
    private sealed record Identity(long Value);
    private static readonly ConditionalWeakTable<object, Identity> identities = new();
    private static long nextIdentity;
    private static long AssetId(object asset) => identities.GetValue(asset, _ => new(Interlocked.Increment(ref nextIdentity))).Value;
    private readonly string metadata;
    private readonly Guid target;
    private readonly Dictionary<Guid, Surface> images;
    private readonly Dictionary<Guid, MaskSurface> masks;
    private readonly (Surface Image, int Revision)[] imageStamps;
    private readonly (MaskSurface Mask, int Revision)[] maskStamps;
    internal string Key { get; }
    public bool HasSameInput(LevelsHistogramInput other) => Key == other.Key;

    private LevelsHistogramInput(string metadata, string key, Guid target,
        Dictionary<Guid, Surface> images, Dictionary<Guid, MaskSurface> masks)
    {
        this.metadata = metadata; Key = key; this.target = target;
        this.images = images; this.masks = masks;
        imageStamps = images.Values.Select(image => (image, image.Revision)).ToArray();
        maskStamps = masks.Values.Select(mask => (mask, mask.Revision)).ToArray();
    }

    public static LevelsHistogramInput Capture(ProjectSnapshot snapshot, Guid layerId)
    {
        var layers = snapshot.Manifest.Layers;
        var byId = layers.ToDictionary(layer => layer.Id);
        if (!byId.TryGetValue(layerId, out var selected) || selected.Adjustment?.Kind != AdjustmentKind.Levels)
            throw new ArgumentException("The target must be a Levels adjustment.", nameof(layerId));
        var order = DrawOrder.Resolve(layers);
        var below = order.Order.TakeWhile(id => id != layerId).ToHashSet();
        if (!order.Order.Contains(layerId)) throw new ArgumentException("The target is outside the hierarchy.", nameof(layerId));
        var needed = new HashSet<Guid>(below);
        var pending = new Queue<Guid>(below);
        while (pending.TryDequeue(out var id))
        {
            var layer = byId[id];
            foreach (var dependency in new[] { layer.ParentId, layer.MaskSourceId })
                if (dependency is { } source && byId.ContainsKey(source) && needed.Add(source)) pending.Enqueue(source);
        }
        // A clipping source can sit above the target. Keep its metadata and assets, but
        // prevent it from drawing. Names, editor selection, guides and resolution do not
        // affect rendered pixels; rasterized text/shape content is covered by the asset key.
        needed.Remove(layerId);
        var records = layers.Where(layer => needed.Contains(layer.Id)).Select(layer => new ProjectLayerRecord
        {
            Id = layer.Id, Name = "", IsVisible = layer.IsVisible && (layer.IsGroupOrFalse || below.Contains(layer.Id)),
            Transform = layer.Transform, ImageFile = layer.ImageFile, ParentId = layer.ParentId, IsGroup = layer.IsGroup,
            Opacity = layer.Opacity, BlendMode = layer.BlendMode, MaskFile = layer.MaskFile, MaskEnabled = layer.MaskEnabled,
            MaskSourceId = layer.MaskSourceId, MaskPlacement = layer.MaskPlacement, MaskLinked = layer.MaskLinked,
            Adjustment = layer.Adjustment, Effects = layer.Effects,
        }).ToList();
        // Include a hidden stop marker so even a hidden target terminates RenderUntil.
        records.Add(new() { Id = layerId, Name = "", IsVisible = false, Transform = new(), ParentId = selected.ParentId });
        var frozen = new ProjectManifest { DocumentId = snapshot.Manifest.DocumentId,
            Width = snapshot.Manifest.Width, Height = snapshot.Manifest.Height, Layers = records };
        string metadata = JsonSerializer.Serialize(frozen, ManifestJson.CompactOptions);
        var images = new Dictionary<Guid, Surface>(); var masks = new Dictionary<Guid, MaskSurface>();
        var key = new StringBuilder(metadata);
        foreach (var layer in records)
        {
            if (layer.ImageFile is not null && snapshot.Images.TryGetValue(layer.Id, out var image))
            {
                images[layer.Id] = image;
                key.Append('|').Append(layer.Id).Append(":i:").Append(AssetId(image)).Append(':').Append(image.Revision);
            }
            if (layer.MaskFile is not null && snapshot.Masks.TryGetValue(layer.Id, out var mask))
            {
                masks[layer.Id] = mask;
                key.Append('|').Append(layer.Id).Append(":m:").Append(AssetId(mask)).Append(':').Append(mask.Revision);
            }
        }
        return new(metadata, key.ToString(), layerId, images, masks);
    }

    internal void CheckPixels(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (imageStamps.Any(stamp => stamp.Image.Revision != stamp.Revision)
            || maskStamps.Any(stamp => stamp.Mask.Revision != stamp.Revision))
            throw new OperationCanceledException("The histogram input changed.", cancellation);
    }

    public LevelsHistogramResult Compute(CancellationToken cancellation = default)
    {
        CheckPixels(cancellation);
        var snapshot = new ProjectSnapshot { Manifest = ManifestJson.Deserialize(metadata), Images = images, Masks = masks };
        double scale = Math.Min(1, 8000.0 / Math.Max(snapshot.Manifest.Width, snapshot.Manifest.Height));
        var surface = new CanvasCompositor(snapshot, scale, cacheLayers: false).RenderUntil(target, cancellation);
        var bins = LevelsHistogram.Sum(surface, null, cancellation);
        CheckPixels(cancellation);
        return new(Key, bins);
    }
}

/// <summary>Owns only small bins and a value key; no retained surfaces or mutable settings.</summary>
public sealed class LevelsHistogramResult
{
    internal string Key { get; }
    private readonly double[][] bins;
    internal LevelsHistogramResult(string key, double[][] bins) { Key = key; this.bins = bins; }
    public double[][] CopyBins() => bins.Select(row => (double[])row.Clone()).ToArray();
    public bool Matches(LevelsHistogramInput input) => Key == input.Key;
    public bool Matches(ProjectSnapshot snapshot, Guid layerId)
    {
        try { return Key == LevelsHistogramInput.Capture(snapshot, layerId).Key; }
        catch (ArgumentException) { return false; }
    }
}

/// <summary>One serial histogram worker and a small LRU. Hits return without scheduling a
/// render; obsolete requests check cancellation before and after each layer and histogram row.
/// Clear prevents work from an old document from populating the new document's cache.</summary>
public sealed class LevelsHistogramCache
{
    private const int Capacity = 8;
    private const long ByteBudget = 8 * 1024 * 1024;
    private readonly SemaphoreSlim worker = new(1);
    private readonly object gate = new();
    private readonly LinkedList<LevelsHistogramResult> entries = new();
    private readonly Func<LevelsHistogramInput, CancellationToken, LevelsHistogramResult> compute;
    private int generation;
    private long bytes;
    public LevelsHistogramCache() : this((input, cancellation) => input.Compute(cancellation)) { }
    internal LevelsHistogramCache(Func<LevelsHistogramInput, CancellationToken, LevelsHistogramResult> compute) => this.compute = compute;
    private static long Size(LevelsHistogramResult result) => result.Key.Length * 2L + 4 * 256 * sizeof(double);

    public void Clear()
    {
        lock (gate) { entries.Clear(); bytes = 0; generation++; }
    }

    private LevelsHistogramResult? Lookup(string key)
    {
        for (var node = entries.First; node is not null; node = node.Next)
            if (node.Value.Key == key) { entries.Remove(node); entries.AddFirst(node); return node.Value; }
        return null;
    }

    public Task<LevelsHistogramResult> GetAsync(LevelsHistogramInput input, CancellationToken cancellation = default)
    {
        input.CheckPixels(cancellation);
        int requestedGeneration;
        lock (gate)
        {
            if (Lookup(input.Key) is { } hit) return Task.FromResult(hit);
            requestedGeneration = generation;
        }
        return Task.Run(async () =>
        {
            await worker.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                input.CheckPixels(cancellation);
                lock (gate) { if (Lookup(input.Key) is { } hit) return hit; }
                var result = compute(input, cancellation);
                input.CheckPixels(cancellation);
                lock (gate)
                {
                    if (generation == requestedGeneration && Size(result) <= ByteBudget)
                    {
                        entries.AddFirst(result); bytes += Size(result);
                        while (entries.Count > Capacity || bytes > ByteBudget)
                        { bytes -= Size(entries.Last!.Value); entries.RemoveLast(); }
                    }
                }
                return result;
            }
            finally { worker.Release(); }
        }, cancellation);
    }
}
