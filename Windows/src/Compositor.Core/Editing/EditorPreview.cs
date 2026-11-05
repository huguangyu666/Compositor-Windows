namespace Compositor.Core;

public static class EditorPreview
{
    public static string ContentFingerprint(ProjectSnapshot source)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        var manifest = ManifestJson.Deserialize(ManifestJson.Serialize(source.Manifest));
        manifest.ActiveLayerId = null;
        hash.AppendData(ManifestJson.SerializeToUtf8(manifest));
        foreach (var pair in source.Images.OrderBy(pair => pair.Key))
        {
            hash.AppendData(pair.Key.ToByteArray());
            hash.AppendData(BitConverter.GetBytes(pair.Value.Width)); hash.AppendData(BitConverter.GetBytes(pair.Value.Height));
            for (int y = 0; y < pair.Value.Height; y++) hash.AppendData(pair.Value.Pixels.AsSpan(y * pair.Value.Stride, pair.Value.Width * 4));
        }
        foreach (var pair in source.Masks.OrderBy(pair => pair.Key))
        {
            hash.AppendData(pair.Key.ToByteArray()); hash.AppendData(BitConverter.GetBytes(pair.Value.Width)); hash.AppendData(BitConverter.GetBytes(pair.Value.Height));
            hash.AppendData(pair.Value.Pixels);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static ProjectSnapshot Capture(EditorDocument document, Guid? layerId = null)
    {
        var capture = document.CaptureSave().Snapshot;
        if (layerId is not { } id) return capture;
        if (!capture.Images.TryGetValue(id, out var image)) throw new ArgumentException("Choose a raster layer to preview.");
        var layer = capture.Manifest.Layers.Single(l => l.Id == id);
        layer.ParentId = null;
        capture.Manifest.Layers.Clear(); capture.Manifest.Layers.Add(layer);
        return new ProjectSnapshot { Manifest = capture.Manifest, Images = new Dictionary<Guid, Surface> { [id] = image },
            Masks = capture.Masks.TryGetValue(id, out var mask) ? new Dictionary<Guid, MaskSurface> { [id] = mask } : new Dictionary<Guid, MaskSurface>() };
    }

    public static Surface Render(ProjectSnapshot snapshot, int maxSide)
    {
        if (maxSide is < 64 or > 2048) throw new ArgumentOutOfRangeException(nameof(maxSide));
        double scale = Math.Min(1, (double)maxSide / Math.Max(snapshot.Manifest.Width, snapshot.Manifest.Height));
        return new CanvasCompositor(snapshot, scale, cacheLayers: false).Render();
    }
}
