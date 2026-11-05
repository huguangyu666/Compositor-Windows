namespace Compositor.Core;

public sealed class SelectionSampleInput
{
    private readonly ProjectSnapshot snapshot;
    private SelectionSampleInput(ProjectSnapshot snapshot) => this.snapshot = snapshot;

    // Freeze metadata and dictionaries on the owner thread; immutable pixel assets are shared.
    public static SelectionSampleInput Capture(ProjectSnapshot source) => new(new()
    {
        Manifest = ManifestJson.Deserialize(ManifestJson.Serialize(source.Manifest)),
        Images = new Dictionary<Guid, Surface>(source.Images), Masks = new Dictionary<Guid, MaskSurface>(source.Masks),
    });

    public Surface Render(bool allLayers, Guid? activeLayerId)
    {
        if (allLayers) return new CanvasCompositor(snapshot).Render();
        var canvas = new Surface(snapshot.Manifest.Width, snapshot.Manifest.Height);
        var layer = snapshot.Manifest.Layers.FirstOrDefault(l => l.Id == activeLayerId);
        if (layer is not null && !layer.IsGroupOrFalse && snapshot.Images.TryGetValue(layer.Id, out var image))
            Resampler.Draw(image, layer.Transform, canvas, 0, 0, canvas.Width, canvas.Height);
        return canvas;
    }
}

public sealed record ColorRangeResult(Selection? Selection, Surface? Preview);

public static class SelectionSampling
{
    public static Selection? Wand(Surface sample, PointD point, WandSettings settings, CancellationToken cancellation = default)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || point.X < 0 || point.Y < 0 || point.X >= sample.Width || point.Y >= sample.Height) return null;
        var mask = new MaskSurface(sample.Width, sample.Height);
        long count = WandPixels.WandMask(sample, (int)Math.Floor(point.X), (int)Math.Floor(point.Y), settings.SampleRadius,
            Math.Clamp(settings.Tolerance, 0, 255), settings.Contiguous, mask, cancellation);
        return count > 0 ? Selection.FromCoverage(mask, cancellation) : null;
    }
    public static ColorRangeResult ColorRange(Surface sample, byte[] include, byte[] exclude, int fuzziness, bool invert,
        int maxWidth = 292, int maxHeight = 200, CancellationToken cancellation = default)
    {
        if (include.Length < 3) return new(null, null);
        var mask = new MaskSurface(sample.Width, sample.Height);
        long count = WandPixels.ColorRangeMask(sample, include, exclude, Math.Clamp(fuzziness, 0, 200), invert, mask, cancellation);
        double scale = Math.Min(1, Math.Min((double)maxWidth / sample.Width, (double)maxHeight / sample.Height));
        var preview = new Surface(Math.Max(1, (int)(sample.Width * scale)), Math.Max(1, (int)(sample.Height * scale)));
        for (int y = 0; y < preview.Height; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int x = 0; x < preview.Width; x++)
            {
                byte gray = mask.Pixels[Math.Min(mask.Height - 1, (int)(y / scale)) * mask.Width + Math.Min(mask.Width - 1, (int)(x / scale))];
                int i = y * preview.Stride + x * 4;
                preview.Pixels[i] = preview.Pixels[i + 1] = preview.Pixels[i + 2] = gray; preview.Pixels[i + 3] = 255;
            }
        }
        return new(count > 0 ? Selection.FromCoverage(mask, cancellation) : null, preview);
    }
}
