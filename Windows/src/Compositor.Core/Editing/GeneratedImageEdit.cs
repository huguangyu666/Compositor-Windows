namespace Compositor.Core;

/// <summary>A frozen AI edit. Network results become ordinary top-level raster layers.</summary>
public sealed class GeneratedImageEdit
{
    public const long MaxPixels = 16_777_216;
    private readonly EditorDocument owner;
    private readonly ProjectSnapshot source;
    private readonly string manifestJson;
    private readonly Selection? selection;
    private readonly Selection? ownerSelection;
    private readonly int[] imageRevisions, maskRevisions;
    public int Width { get; }
    public int Height { get; }
    public int Left { get; }
    public int Top { get; }
    public bool Expands { get; }

    private GeneratedImageEdit(EditorDocument document, int left, int right, int top, int bottom, Selection? recoveredSelection = null)
    {
        owner = document;
        var snapshot = document.Snapshot;
        long width = (long)snapshot.Manifest.Width + left + right, height = (long)snapshot.Manifest.Height + top + bottom;
        if (left < 0 || right < 0 || top < 0 || bottom < 0 || width > Limits.MaxSide || height > Limits.MaxSide || width * height > MaxPixels)
            throw new ArgumentOutOfRangeException(nameof(left), "AI edits support at most 16 megapixels within the canvas limits.");
        Expands = left + right + top + bottom > 0;
        ownerSelection = document.ActiveSelection;
        selection = recoveredSelection ?? ownerSelection;
        if (!Expands && selection is not { IsEmpty: false }) throw new InvalidOperationException("Select an area to edit first.");
        if (snapshot.Manifest.Layers.Count >= Limits.MaxLayers
            || snapshot.Images.Values.Sum(s => s.PixelCount) + snapshot.Masks.Values.Sum(s => (long)s.Width * s.Height) + width * height > Limits.DocumentPixelBudget)
            throw new InvalidOperationException("The generated layer would exceed the document budget.");
        Width = (int)width; Height = (int)height; Left = left; Top = top;
        if (snapshot.Manifest.Layers.Any(layer => !Shift(layer.Transform).IsValid || layer.MaskPlacement is { } mask && !Shift(mask).IsValid))
            throw new InvalidOperationException("The expansion would move a layer outside the supported coordinates.");
        manifestJson = ManifestJson.Serialize(snapshot.Manifest);
        source = new ProjectSnapshot
        {
            Manifest = ManifestJson.Deserialize(manifestJson),
            Images = new Dictionary<Guid, Surface>(snapshot.Images), Masks = new Dictionary<Guid, MaskSurface>(snapshot.Masks),
        };
        imageRevisions = source.Images.Values.Select(s => s.Revision).ToArray();
        maskRevisions = source.Masks.Values.Select(s => s.Revision).ToArray();
    }

    public static GeneratedImageEdit Capture(EditorDocument document, int left = 0, int right = 0, int top = 0, int bottom = 0) =>
        new(document, left, right, top, bottom);

    public static GeneratedImageEdit CaptureRecovered(EditorDocument document, Selection selection) => new(document, 0, 0, 0, 0, selection);

    public bool IsCurrent(EditorDocument document) => ReferenceEquals(owner, document)
        && ReferenceEquals(ownerSelection, document.ActiveSelection) && manifestJson == ManifestJson.Serialize(document.Snapshot.Manifest)
        && SameAssets(source.Images, document.Snapshot.Images, imageRevisions, s => s.Revision)
        && SameAssets(source.Masks, document.Snapshot.Masks, maskRevisions, s => s.Revision);

    private static bool SameAssets<T>(IReadOnlyDictionary<Guid, T> expected, IReadOnlyDictionary<Guid, T> actual, int[] revisions, Func<T, int> revision) where T : class
    {
        if (expected.Count != actual.Count) return false;
        int index = 0;
        foreach (var pair in expected)
            if (!actual.TryGetValue(pair.Key, out var value) || !ReferenceEquals(value, pair.Value) || revision(value) != revisions[index++]) return false;
        return true;
    }

    private byte CoverageAt(int x, int y) => Expands
        ? (byte)(x < Left || y < Top || x >= Left + source.Manifest.Width || y >= Top + source.Manifest.Height ? 255 : 0)
        : selection!.CoverageAt(x, y);

    public (Surface Image, Surface Mask) RenderInput(CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var original = new CanvasCompositor(source).Render();
        var image = new Surface(Width, Height);
        for (int y = 0; y < original.Height; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            original.Pixels.AsSpan(y * original.Stride, original.Width * 4).CopyTo(image.Pixels.AsSpan((y + Top) * image.Stride + Left * 4));
        }
        var mask = new Surface(Width, Height);
        for (int y = 0; y < Height; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int x = 0; x < Width; x++) mask.Pixels[y * mask.Stride + x * 4 + 3] = (byte)(255 - CoverageAt(x, y));
        }
        return (image, mask);
    }

    /// <summary>Reapply frozen coverage locally; a provider cannot change protected pixels.</summary>
    public Surface PrepareResult(Surface generated, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var sized = generated;
        if (generated.Width != Width || generated.Height != Height)
        {
            sized = new Surface(Width, Height);
            Resampler.Draw(generated, new LayerTransform { Size = new SizeD(Width, Height) }, sized, 0, 0, Width, Height);
        }
        var result = new Surface(Width, Height);
        for (int y = 0; y < Height; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int x = 0; x < Width; x++)
            {
                int from = y * sized.Stride + x * 4, to = y * result.Stride + x * 4;
                int coverage = CoverageAt(x, y);
                for (int channel = 0; channel < 4; channel++) result.Pixels[to + channel] = (byte)((sized.Pixels[from + channel] * coverage + 127) / 255);
            }
        }
        return result;
    }

    public ProjectSnapshot Preview(Surface result, string name)
    {
        if (result.Width != Width || result.Height != Height) throw new ArgumentException("Unexpected generated layer size.", nameof(result));
        var (image, placement) = GrownLayerGrid.Trim(result, new LayerTransform { Size = new SizeD(Width, Height) });
        var manifest = ManifestJson.Deserialize(manifestJson);
        manifest.Width = Width; manifest.Height = Height;
        foreach (var layer in manifest.Layers)
        {
            layer.Transform = Shift(layer.Transform);
            if (layer.MaskPlacement is { } mask) layer.MaskPlacement = Shift(mask);
        }
        if (manifest.Guides is { } guides)
            foreach (var guide in guides) guide.Position += guide.Axis == GuideAxis.Vertical ? Left : Top;
        var id = Guid.NewGuid();
        manifest.Layers.Add(new ProjectLayerRecord
        {
            Id = id, Name = name, IsVisible = true, IsGroup = false, Opacity = 1, BlendMode = LayerBlendMode.Normal,
            ImageFile = ProjectValidator.Uuid(id) + ".png", Transform = placement,
        });
        manifest.ActiveLayerId = id;
        return new ProjectSnapshot { Manifest = manifest, Images = new Dictionary<Guid, Surface>(source.Images) { [id] = image }, Masks = new Dictionary<Guid, MaskSurface>(source.Masks) };
    }

    private LayerTransform Shift(LayerTransform value) => new()
    {
        Origin = new PointD(value.Origin.X + Left, value.Origin.Y + Top), Size = value.Size,
        Rotation = value.Rotation, FlipX = value.FlipX, FlipY = value.FlipY, Sampling = value.Sampling,
    };
}

public sealed partial class EditorDocument
{
    public bool AcceptGeneratedImage(GeneratedImageEdit edit, Surface result, string name)
    {
        if (!edit.IsCurrent(this) || ActiveTextDraft is not null) return false;
        var preview = edit.Preview(result, UniqueName(name));
        BeginChange();
        Snapshot = preview;
        if (edit.Expands) ActiveSelection = null;
        EndChange();
        if (edit.Expands) SelectionChanged?.Invoke();
        return true;
    }
}
