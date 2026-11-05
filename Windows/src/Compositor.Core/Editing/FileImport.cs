using Compositor.Core.Psd;

namespace Compositor.Core;

public sealed partial class EditorDocument
{
    private FileImportBatch? fileImportBatch;

    /// <summary>One ordered file request: successful files publish individually and share one Undo.</summary>
    public FileImportBatch BeginFileImport(PointD? point = null)
    {
        if (fileImportBatch is not null) throw new InvalidOperationException("A file import is already active.");
        if (point is { } location && (!double.IsFinite(location.X) || !double.IsFinite(location.Y)))
            throw new ArgumentException("The import point must be finite.", nameof(point));
        return fileImportBatch = new(this, point);
    }

    public sealed class FileImportBatch : IDisposable
    {
        private readonly EditorDocument owner;
        private readonly PointD? point;
        private ProjectSnapshot expected;
        private string manifest = "";
        private Selection? selection;
        private Dictionary<Guid, (Surface Image, int Revision)> images = new();
        private Dictionary<Guid, (MaskSurface Mask, int Revision)> masks = new();
        private bool changed, disposed;

        internal FileImportBatch(EditorDocument owner, PointD? point)
        { this.owner = owner; this.point = point; expected = owner.Snapshot; Capture(); }

        public bool IsCurrent => !disposed && ReferenceEquals(owner.fileImportBatch, this)
            && ReferenceEquals(expected, owner.Snapshot) && ReferenceEquals(selection, owner.ActiveSelection)
            && manifest == ManifestJson.Serialize(expected.Manifest)
            && images.Count == expected.Images.Count && images.All(pair => expected.Images.TryGetValue(pair.Key, out var image)
                && ReferenceEquals(image, pair.Value.Image) && image.Revision == pair.Value.Revision)
            && masks.Count == expected.Masks.Count && masks.All(pair => expected.Masks.TryGetValue(pair.Key, out var mask)
                && ReferenceEquals(mask, pair.Value.Mask) && mask.Revision == pair.Value.Revision);

        public long RemainingPixels => Math.Max(0, Limits.DocumentPixelBudget
            - expected.Images.Sum(pair => pair.Value.PixelCount) - expected.Masks.Sum(pair => pair.Value.PixelCount));

        public ProjectLayerRecord AddImage(Surface image, string name)
        {
            RequireCurrent(); ArgumentNullException.ThrowIfNull(image);
            var next = DetachedSnapshot(); var center = point ?? new PointD(next.Manifest.Width / 2d, next.Manifest.Height / 2d);
            var layer = new ProjectLayerRecord { Id = Guid.NewGuid(), Name = name, IsVisible = true, IsGroup = false,
                Opacity = 1, BlendMode = LayerBlendMode.Normal, ParentId = ParentFor(next.Manifest),
                Transform = new() { Origin = new(Math.Floor(center.X - image.Width / 2d), Math.Floor(center.Y - image.Height / 2d)),
                    Size = new(image.Width, image.Height) } };
            layer.ImageFile = ProjectValidator.Uuid(layer.Id) + ".png";
            next.Manifest.Layers.Add(layer); next.Manifest.ActiveLayerId = layer.Id; next.Images.Add(layer.Id, image);
            Publish(next); return layer;
        }

        public ProjectLayerRecord AddPhotoshop(PsdDocument psd, string name, List<PsdConversion>? conversions = null)
        {
            RequireCurrent(); ArgumentNullException.ThrowIfNull(psd);
            // The Mac's builder reports the records it skips even when nothing else survives,
            // so the reader's lines ride both the layered and the flattened route.
            if (conversions is not null && psd.ReaderConversions is { } lines) conversions.AddRange(lines);
            if (psd.IsCompositeFallback)
            {
                if (psd.Layers.Count != 1 || psd.Layers[0].Pixels is not { } flattened)
                    throw new ProjectFormatException(ProjectErrorKind.Invalid);
                return AddImage(flattened, name);
            }
            if (psd.Layers.Count == 0) throw new ProjectFormatException(ProjectErrorKind.Invalid);
            var next = DetachedSnapshot(); var wrapper = new ProjectLayerRecord { Id = Guid.NewGuid(), Name = name,
                IsGroup = true, IsVisible = true, ParentId = ParentFor(next.Manifest),
                Transform = new() { Size = new(next.Manifest.Width, next.Manifest.Height) } };
            var mapping = psd.Layers.ToDictionary(layer => layer.Id, _ => Guid.NewGuid());
            double dx = 0, dy = 0;
            if (point is { } target)
            {
                var bounds = psd.Layers.Where(layer => !layer.IsGroup).Select(layer =>
                    (layer.Offset.X, layer.Offset.Y, Width: layer.Pixels?.Width ?? psd.Width, Height: layer.Pixels?.Height ?? psd.Height)).ToArray();
                if (bounds.Length > 0)
                {
                    dx = target.X - (bounds.Min(b => b.X) + bounds.Max(b => b.X + b.Width)) / 2;
                    dy = target.Y - (bounds.Min(b => b.Y) + bounds.Max(b => b.Y + b.Height)) / 2;
                }
            }
            next.Manifest.Layers.Add(wrapper);
            foreach (var source in psd.Layers)
            {
                if (source.ParentId is { } parent && !mapping.ContainsKey(parent)) throw new ProjectFormatException(ProjectErrorKind.Invalid);
                var layer = new ProjectLayerRecord { Id = mapping[source.Id], Name = source.Name, IsVisible = !source.Hidden,
                    Opacity = source.Opacity, BlendMode = source.BlendMode, IsGroup = source.IsGroup,
                    ParentId = source.ParentId is { } parentId ? mapping[parentId] : wrapper.Id,
                    Transform = new() { Origin = new(source.Offset.X + dx, source.Offset.Y + dy),
                        Size = new(source.Pixels?.Width ?? psd.Width, source.Pixels?.Height ?? psd.Height) } };
                // The same per-layer conversion the direct route runs: pixels, then the
                // adjustment/shape/text records, placement shifted by the drop, with the report lines.
                EditorDocument.AttachPsdExtras(source, layer, next.Images, psd.Width, psd.Height, dx, dy, conversions);
                if (source.Mask is { } mask)
                {
                    layer.MaskFile = ProjectValidator.Uuid(layer.Id) + ".mask.png"; layer.MaskEnabled = source.MaskEnabled;
                    var placement = source.MaskPlacement ?? new LayerTransform { Size = new(psd.Width, psd.Height) };
                    layer.MaskLinked = source.MaskLinked;
                    layer.MaskPlacement = new() { Origin = new(placement.Origin.X + dx, placement.Origin.Y + dy),
                        Size = placement.Size, Rotation = placement.Rotation, Sampling = placement.Sampling,
                        FlipX = placement.FlipX, FlipY = placement.FlipY };
                    next.Masks.Add(layer.Id, mask);
                }
                next.Manifest.Layers.Add(layer);
            }
            next.Manifest.ActiveLayerId = wrapper.Id; Publish(next); return wrapper;
        }

        public void Dispose()
        { disposed = true; if (ReferenceEquals(owner.fileImportBatch, this)) owner.fileImportBatch = null; }

        private void RequireCurrent()
        { if (!IsCurrent) throw new InvalidOperationException("The file import source changed or was retired."); }
        private (ProjectManifest Manifest, Dictionary<Guid, Surface> Images, Dictionary<Guid, MaskSurface> Masks) DetachedSnapshot()
            => (ManifestJson.Deserialize(manifest), new Dictionary<Guid, Surface>(expected.Images), new Dictionary<Guid, MaskSurface>(expected.Masks));
        private static Guid? ParentFor(ProjectManifest manifest)
        {
            var active = manifest.Layers.FirstOrDefault(layer => layer.Id == manifest.ActiveLayerId);
            return active?.IsGroupOrFalse == true ? active.Id : active?.ParentId;
        }
        private void Capture()
        {
            expected = owner.Snapshot; manifest = ManifestJson.Serialize(expected.Manifest); selection = owner.ActiveSelection;
            images = expected.Images.ToDictionary(pair => pair.Key, pair => (pair.Value, pair.Value.Revision));
            masks = expected.Masks.ToDictionary(pair => pair.Key, pair => (pair.Value, pair.Value.Revision));
        }
        private void Publish((ProjectManifest Manifest, Dictionary<Guid, Surface> Images, Dictionary<Guid, MaskSurface> Masks) next)
        {
            ProjectValidator.Validate(next.Manifest);
            if (next.Images.Values.Sum(image => image.PixelCount) + next.Masks.Values.Sum(mask => mask.PixelCount) > Limits.DocumentPixelBudget)
                throw new ProjectFormatException(ProjectErrorKind.TooLarge);
            if (!changed) { owner.BeginChange(); changed = true; }
            owner.coalesceKey = null;
            owner.Snapshot = new() { Manifest = next.Manifest, Images = next.Images, Masks = next.Masks };
            Capture(); owner.EndChange();
        }
    }
}
