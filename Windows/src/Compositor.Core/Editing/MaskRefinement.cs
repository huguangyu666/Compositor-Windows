using System.Text.Json;
using System.Text.Json.Nodes;

namespace Compositor.Core;

/// <summary>A mask-only worker edit. Source image pixels remain immutable and commit rechecks the target.</summary>
public sealed class MaskRefinement
{
    private readonly EditorDocument owner;
    private readonly Guid id;
    private readonly ProjectSnapshot source;
    private readonly string manifest;
    private readonly Selection? selection;
    private readonly MaskSurface? originalMask;
    private readonly Surface? originalImage;
    private readonly int maskRevision, imageRevision;
    private EditorDocument? prepared;

    private MaskRefinement(EditorDocument owner, Guid id)
    {
        this.owner = owner; this.id = id;
        if (!owner.Snapshot.Manifest.Layers.Any(l => l.Id == id)) throw new ArgumentException("Choose an existing layer.");
        source = owner.CaptureSave().Snapshot; manifest = ManifestJson.Serialize(owner.Snapshot.Manifest); selection = owner.ActiveSelection;
        originalMask = owner.Snapshot.Masks.GetValueOrDefault(id); originalImage = owner.Snapshot.Images.GetValueOrDefault(id);
        maskRevision = originalMask?.Revision ?? 0; imageRevision = originalImage?.Revision ?? 0;
    }
    public static MaskRefinement Capture(EditorDocument owner, Guid layerId) => new(owner, layerId);

    public void Prepare(JsonObject args, MaskSurface? imported = null, CancellationToken cancellation = default)
    {
        var draft = new EditorDocument(new ProjectSnapshot { Manifest = ManifestJson.Deserialize(ManifestJson.Serialize(source.Manifest)),
            Images = new Dictionary<Guid, Surface>(source.Images), Masks = new Dictionary<Guid, MaskSurface>(source.Masks) });
        bool respectSelection = args["respectSelection"]?.GetValue<bool>() ?? true;
        if (respectSelection) draft.SetSelection(selection);
        var layer = draft.Snapshot.Manifest.Layers.Single(l => l.Id == id);
        string operation = args["operation"]?.GetValue<string>() ?? "";
        cancellation.ThrowIfCancellationRequested();
        if (operation == "remove") draft.RemoveMask(id);
        else if (operation is "enable" or "disable")
        {
            if (layer.MaskFile is null) throw new ArgumentException("The layer has no mask.");
            draft.SetMaskEnabled(id, operation == "enable");
        }
        else if (operation == "paint")
        {
            double radius = Number(args, "radius", 10), hardness = Number(args, "hardness", .8), opacity = Number(args, "opacity", 1), value = Number(args, "value", 255);
            if (radius is < .5 or > 250 || hardness is < 0 or > 1 || opacity is <= 0 or > 1 || value is < 0 or > 255 || value != Math.Floor(value))
                throw new ArgumentException("Mask brush: radius .5–250, hardness/opacity 0–1, value 0–255.");
            if (args["points"] is not JsonArray points || points.Count is < 1 or > 4096) throw new ArgumentException("Supply 1–4096 document-space brush points.");
            var path = points.Select(point => point is JsonArray { Count: 2 } pair ? new PointD(Real(pair[0]), Real(pair[1])) : throw new ArgumentException("Brush points must be [x,y].")).ToArray();
            double length = 0;
            for (int i = 0; i < path.Length; i++)
            {
                if (path[i].X < -radius || path[i].Y < -radius || path[i].X > source.Manifest.Width + radius || path[i].Y > source.Manifest.Height + radius)
                    throw new ArgumentException("Brush points must lie on or near the canvas.");
                if (i > 0) length += Math.Sqrt(Math.Pow(path[i].X - path[i - 1].X, 2) + Math.Pow(path[i].Y - path[i - 1].Y, 2));
            }
            if (length > 100_000) throw new ArgumentException("Split mask brush paths longer than 100000 pixels.");
            var key = new object(); byte tone = (byte)value;
            if (!draft.BeginStroke(key, id, new(radius, hardness, opacity, tone, tone, tone), path[0], onMask: true))
                throw new ArgumentException("The mask brush cannot paint this layer or disabled mask.");
            for (int i = 1; i < path.Length; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!draft.ContinueStroke(key, path[i])) throw new ArgumentException("The mask brush exceeds the document limits.");
            }
            draft.EndStroke(key);
        }
        else
        {
            var existing = draft.Snapshot.Masks.GetValueOrDefault(id);
            if (existing is null && operation != "import") throw new ArgumentException("Choose a layer with an existing mask.");
            int width = existing?.Width ?? originalImage?.Width ?? Math.Max(1, (int)layer.Transform.Size.Width);
            int height = existing?.Height ?? originalImage?.Height ?? Math.Max(1, (int)layer.Transform.Size.Height);
            if (width > Limits.MaxSide || height > Limits.MaxSide || (long)width * height > Limits.MaxSurfacePixels) throw new ArgumentException("Mask dimensions exceed the image limits.");
            existing ??= new MaskSurface(width, height); if (originalMask is null) existing.Pixels.AsSpan().Fill(255);
            MaskSurface result;
            if (operation == "import")
            {
                if (imported is null || imported.Width != width || imported.Height != height) throw new ArgumentException("The imported mask must match the existing mask grid, or the image grid for a new mask.");
                string combine = args["combine"]?.GetValue<string>() ?? "replace";
                if (combine is not ("replace" or "add" or "subtract")) throw new ArgumentException("combine must be replace, add or subtract.");
                result = new MaskSurface(width, height);
                for (int i = 0; i < result.Pixels.Length; i++) result.Pixels[i] = combine == "replace" ? imported.Pixels[i]
                    : combine == "add" ? Math.Max(existing.Pixels[i], imported.Pixels[i]) : (byte)((existing.Pixels[i] * (255 - imported.Pixels[i]) + 127) / 255);
            }
            else if (operation == "invert")
            {
                result = new MaskSurface(width, height);
                for (int i = 0; i < result.Pixels.Length; i++) result.Pixels[i] = (byte)(255 - existing.Pixels[i]);
            }
            else
            {
                int amount = checked((int)Number(args, "amount", 1));
                if (amount is < 1 or > 250 || Number(args, "amount", 1) != amount) throw new ArgumentException("amount must be an integer from 1 to 250 mask pixels.");
                if (operation is "expand" or "contract") result = Extrema(existing, amount, operation == "expand", cancellation);
                else if (operation == "feather")
                {
                    var softened = Selection.FromCoverage(existing, cancellation).Feathered(amount);
                    result = new MaskSurface(width, height);
                    for (int y = 0; y < height; y++) { cancellation.ThrowIfCancellationRequested(); for (int x = 0; x < width; x++) result.Pixels[y * width + x] = softened.CoverageAt(x, y); }
                }
                else throw new ArgumentException("Unknown mask refinement operation.");
            }
            if (respectSelection && draft.SelectionCoverageOnGrid(id, layer.MaskPlacement ?? layer.Transform, width, height) is { } coverage)
                for (int i = 0; i < result.Pixels.Length; i++) result.Pixels[i] = (byte)((result.Pixels[i] * coverage.Pixels[i] + existing.Pixels[i] * (255 - coverage.Pixels[i]) + 127) / 255);
            draft.ReplaceLayerMask(id, result);
        }
        cancellation.ThrowIfCancellationRequested(); prepared = draft;
    }

    public bool Commit()
    {
        if (prepared is null) throw new InvalidOperationException("Prepare the mask first.");
        if (manifest != ManifestJson.Serialize(owner.Snapshot.Manifest) || !ReferenceEquals(selection, owner.ActiveSelection)
            || !ReferenceEquals(originalMask, owner.Snapshot.Masks.GetValueOrDefault(id)) || (originalMask?.Revision ?? 0) != maskRevision
            || !ReferenceEquals(originalImage, owner.Snapshot.Images.GetValueOrDefault(id)) || (originalImage?.Revision ?? 0) != imageRevision) return false;
        var after = prepared.Snapshot.Manifest.Layers.Single(l => l.Id == id);
        var before = owner.Snapshot.Manifest.Layers.Single(l => l.Id == id);
        var next = prepared.Snapshot.Masks.GetValueOrDefault(id);
        bool samePixels = originalMask is null ? next is null : next is not null && originalMask.Width == next.Width && originalMask.Height == next.Height && originalMask.Pixels.AsSpan().SequenceEqual(next.Pixels);
        if (samePixels && before.MaskEnabled == after.MaskEnabled && Equals(before.MaskPlacement, after.MaskPlacement)) return true;
        owner.ApplyAutomation(draft =>
        {
            if (next is null) draft.RemoveMask(id);
            else
            {
                draft.ReplaceLayerMask(id, next);
                var layer = draft.Snapshot.Manifest.Layers.Single(l => l.Id == id);
                layer.MaskPlacement = after.MaskPlacement; layer.MaskEnabled = after.MaskEnabled;
            }
        });
        return true;
    }

    // Separable square max/min filters preserve every grayscale level, including soft hair coverage.
    private static MaskSurface Extrema(MaskSurface source, int radius, bool maximum, CancellationToken cancellation)
    {
        var scratch = new MaskSurface(source.Width, source.Height); var result = new MaskSurface(source.Width, source.Height);
        var deque = new int[Math.Max(source.Width, source.Height)];
        void Pass(MaskSurface input, MaskSurface output, bool vertical)
        {
            int lines = vertical ? input.Width : input.Height, count = vertical ? input.Height : input.Width;
            int Index(int line, int p) => vertical ? p * input.Width + line : line * input.Width + p;
            for (int line = 0; line < lines; line++)
            {
                cancellation.ThrowIfCancellationRequested(); int head = 0, tail = 0, next = 0;
                for (int p = 0; p < count; p++)
                {
                    int end = Math.Min(count - 1, p + radius);
                    for (; next <= end; next++)
                    {
                        byte value = input.Pixels[Index(line, next)];
                        while (tail > head && (maximum ? input.Pixels[Index(line, deque[tail - 1])] <= value : input.Pixels[Index(line, deque[tail - 1])] >= value)) tail--;
                        deque[tail++] = next;
                    }
                    while (head < tail && deque[head] < p - radius) head++;
                    output.Pixels[Index(line, p)] = input.Pixels[Index(line, deque[head])];
                }
            }
        }
        Pass(source, scratch, false); Pass(scratch, result, true); return result;
    }
    private static double Number(JsonObject args, string key, double fallback) => args[key] is { } node ? Real(node) : fallback;
    private static double Real(JsonNode? node) => node?.GetValueKind() == JsonValueKind.Number && node.Deserialize<double>() is var number && double.IsFinite(number)
        ? number : throw new ArgumentException("Expected a finite number.");
}
