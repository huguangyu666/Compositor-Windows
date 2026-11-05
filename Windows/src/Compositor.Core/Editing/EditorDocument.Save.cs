using System.Text;

namespace Compositor.Core;

public sealed partial class EditorDocument
{
    /// <summary>A save's frozen metadata, immutable assets and source revision stamps.</summary>
    public sealed class SaveCapture
    {
        internal SaveCapture() { }
        internal EditorDocument Owner { get; init; } = null!;
        internal byte[] Manifest { get; init; } = null!;
        internal Dictionary<Guid, (Surface Surface, int Revision)> Images { get; init; } = null!;
        internal Dictionary<Guid, (MaskSurface Surface, int Revision)> Masks { get; init; } = null!;
        public ProjectSnapshot Snapshot { get; internal init; } = null!;
    }

    /// <summary>Capture on the editor owner thread. Only live, mutable stroke buffers need
    /// copies; settled assets remain shared, as they are in undo mementos.</summary>
    public SaveCapture CaptureSave()
    {
        var manifest = MementoOf(Snapshot);
        var images = Snapshot.Images.ToDictionary(pair => pair.Key, pair => (pair.Value, pair.Value.Revision));
        var masks = Snapshot.Masks.ToDictionary(pair => pair.Key, pair => (pair.Value, pair.Value.Revision));
        return new SaveCapture
        {
            Owner = this, Manifest = manifest, Images = images, Masks = masks,
            Snapshot = new ProjectSnapshot
            {
                Manifest = ManifestJson.Deserialize(Encoding.UTF8.GetString(manifest)),
                Images = Snapshot.Images.ToDictionary(pair => pair.Key, pair =>
                    ReferenceEquals(pair.Value, strokeImage) || ReferenceEquals(pair.Value, cloneWorking) || ReferenceEquals(pair.Value, blurWorking)
                        ? pair.Value.Clone() : pair.Value),
                Masks = Snapshot.Masks.ToDictionary(pair => pair.Key, pair => ReferenceEquals(pair.Value, strokeMask) ? pair.Value.Clone() : pair.Value),
            },
        };
    }

    /// <summary>Remember what reached disk, without claiming edits made after capture were
    /// saved. Returns true only when the current document still matches that capture.</summary>
    public bool CompleteSave(SaveCapture capture, string projectDirectory)
    {
        if (!ReferenceEquals(capture.Owner, this)) throw new ArgumentException("Save capture belongs to another document.", nameof(capture));
        bool current = capture.Manifest.AsSpan().SequenceEqual(MementoOf(Snapshot))
            && capture.Images.Count == Snapshot.Images.Count && capture.Masks.Count == Snapshot.Masks.Count
            && capture.Images.All(pair => Snapshot.Images.TryGetValue(pair.Key, out var image)
                && ReferenceEquals(image, pair.Value.Surface) && image.Revision == pair.Value.Revision)
            && capture.Masks.All(pair => Snapshot.Masks.TryGetValue(pair.Key, out var mask)
                && ReferenceEquals(mask, pair.Value.Surface) && mask.Revision == pair.Value.Revision);
        savedImages.Clear(); savedMasks.Clear();
        foreach (var layer in capture.Snapshot.Manifest.Layers)
        {
            if (layer.ImageFile is { } imageFile && capture.Images.TryGetValue(layer.Id, out var image))
            {
                var (bytes, written) = AssetStamp(projectDirectory, imageFile);
                savedImages[layer.Id] = (image.Surface, image.Revision, imageFile, bytes, written);
            }
            if (layer.MaskFile is { } maskFile && capture.Masks.TryGetValue(layer.Id, out var mask))
            {
                var (bytes, written) = AssetStamp(projectDirectory, maskFile);
                savedMasks[layer.Id] = (mask.Surface, mask.Revision, maskFile, bytes, written);
            }
        }
        IsDirty = !current;
        savedDiskDigest = DigestOf(projectDirectory);
        // This acknowledges disk state, not a content edit. The caller refreshes its
        // dirty indicator; content notifications would tear down newer UI drafts.
        return current;
    }
}
