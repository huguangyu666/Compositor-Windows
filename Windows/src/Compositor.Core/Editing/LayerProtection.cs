using System.Text.Json;

namespace Compositor.Core;

[Flags]
public enum LayerLocks { None = 0, Position = 1, Pixels = 2, Transparency = 4, All = 8 }

public sealed partial class EditorDocument
{
    // Editor protection is local workspace state, like selection, rather than .comp metadata.
    private readonly Dictionary<Guid, LayerLocks> layerLocks = [];
    private ProjectManifest? protectionManifest;
    private Dictionary<Guid, ProjectLayerRecord> protectionLayers = [];
    public event Action? LayerLocksChanged;
    public IReadOnlyDictionary<Guid, LayerLocks> LayerLockSettings => layerLocks;

    public LayerLocks GetLayerLocks(Guid id, bool inherited = true)
    {
        var flags = layerLocks.GetValueOrDefault(id);
        if (!inherited || layerLocks.Count == 0) return flags;
        var parent = ProtectionLayer(id)?.ParentId;
        for (int depth = 0; parent is { } p && depth < 64; depth++)
        { flags |= layerLocks.GetValueOrDefault(p); parent = ProtectionLayer(p)?.ParentId; }
        return flags;
    }

    private ProjectLayerRecord? ProtectionLayer(Guid id)
    {
        if (!ReferenceEquals(protectionManifest, Snapshot.Manifest) || protectionLayers.Count != Snapshot.Manifest.Layers.Count)
        { protectionManifest = Snapshot.Manifest; protectionLayers = Snapshot.Manifest.Layers.ToDictionary(l => l.Id); }
        return protectionLayers.GetValueOrDefault(id);
    }

    public bool SetLayerLocks(Guid id, LayerLocks flags)
    {
        if (Find(id) is null || (flags & ~(LayerLocks.Position | LayerLocks.Pixels | LayerLocks.Transparency | LayerLocks.All)) != 0) return false;
        if (flags == GetLayerLocks(id, inherited: false)) return true;
        if (flags == LayerLocks.None) layerLocks.Remove(id); else layerLocks[id] = flags;
        LayerLocksChanged?.Invoke(); return true;
    }

    public bool CanEditLayer(Guid id, LayerLocks blocked, bool branch = false)
    {
        if (layerLocks.Count == 0) return true;
        if ((GetLayerLocks(id) & (blocked | LayerLocks.All)) != 0) return false;
        if (!branch) return true;
        foreach (var layer in Snapshot.Manifest.Layers)
        {
            if ((GetLayerLocks(layer.Id) & (blocked | LayerLocks.All)) == 0) continue;
            var parent = layer.ParentId;
            for (int depth = 0; parent is { } p && depth < 64; depth++)
            { if (p == id) return false; parent = ProtectionLayer(p)?.ParentId; }
        }
        return true;
    }

    private bool CanEditPixels(Guid id, bool onMask = false) => CanEditLayer(id, onMask ? LayerLocks.None : LayerLocks.Pixels);
    private bool KeepsAlpha(Guid id) => (GetLayerLocks(id) & LayerLocks.Transparency) != 0;

    private bool LayerProtectionAllows(ProjectSnapshot changed)
    {
        try { ValidateLayerProtection(changed); return true; }
        catch (ArgumentException) { return false; }
    }

    private void ProtectGridAlpha(Guid id, Surface working, ImageStrokeGrid grid, RectI bounds, RectI touched)
    {
        if (!KeepsAlpha(id) || grid.Original is not { } source) return;
        int left = Math.Max(bounds.X, touched.X), top = Math.Max(bounds.Y, touched.Y);
        int right = Math.Min(bounds.X + bounds.Width, touched.X + touched.Width), bottom = Math.Min(bounds.Y + bounds.Height, touched.Y + touched.Height);
        for (int y = top; y < bottom; y++) for (int x = left; x < right; x++)
        {
            var old = grid.BaseAt(x, y); int at = (y - bounds.Y) * working.Stride + (x - bounds.X) * 4;
            int from = old.X < 0 ? -1 : (int)old.Y * source.Stride + (int)old.X * 4;
            int alpha = from < 0 ? 0 : source.Pixels[from + 3], produced = working.Pixels[at + 3];
            for (int c = 0; c < 3; c++) working.Pixels[at + c] = produced == 0 ? from < 0 ? (byte)0 : source.Pixels[from + c]
                : (byte)Math.Min(alpha, (working.Pixels[at + c] * alpha + produced / 2) / produced);
            working.Pixels[at + 3] = (byte)alpha;
        }
    }

    private Surface? ProtectAlpha(Guid id, Surface image)
    {
        if (!KeepsAlpha(id)) return image;
        if (!Snapshot.Images.TryGetValue(id, out var original) || original.Width != image.Width || original.Height != image.Height) return null;
        var result = image.Clone();
        for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++)
        {
            int at = y * result.Stride + x * 4, old = y * original.Stride + x * 4;
            int alpha = original.Pixels[old + 3], produced = image.Pixels[y * image.Stride + x * 4 + 3];
            for (int c = 0; c < 3; c++) result.Pixels[at + c] = produced == 0 ? original.Pixels[old + c]
                : (byte)Math.Min(alpha, (result.Pixels[at + c] * alpha + produced / 2) / produced);
            result.Pixels[at + 3] = (byte)alpha;
        }
        return result;
    }

    private void ValidateLayerProtection(ProjectSnapshot changed)
    {
        if (layerLocks.Count == 0) return;
        var after = changed.Manifest.Layers.ToDictionary(l => l.Id);
        foreach (var layer in Snapshot.Manifest.Layers)
        {
            var flags = GetLayerLocks(layer.Id); if (flags == LayerLocks.None) continue;
            if (!after.TryGetValue(layer.Id, out var next))
            { if ((flags & LayerLocks.All) != 0) throw new ArgumentException("Unlock the layer before deleting it."); continue; }
            bool all = (flags & LayerLocks.All) != 0;
            bool pixels = !ReferenceEquals(Snapshot.Images.GetValueOrDefault(layer.Id), changed.Images.GetValueOrDefault(layer.Id));
            if ((all || (flags & LayerLocks.Position) != 0) && (!layer.Transform.Equals(next.Transform)
                || !(layer.MaskPlacement?.Equals(next.MaskPlacement) ?? next.MaskPlacement is null))
                || (all || (flags & LayerLocks.Pixels) != 0) && pixels)
                throw new ArgumentException("Unlock the layer before editing it.");
            if (all)
            {
                var allowed = JsonSerializer.Deserialize<ProjectLayerRecord>(JsonSerializer.Serialize(next, ManifestJson.Options), ManifestJson.Options)!;
                allowed.Name = layer.Name; allowed.IsVisible = layer.IsVisible;
                if (!layer.Equals(allowed) || !ReferenceEquals(Snapshot.Masks.GetValueOrDefault(layer.Id), changed.Masks.GetValueOrDefault(layer.Id)))
                    throw new ArgumentException("Unlock the layer before editing it.");
            }
            if ((flags & LayerLocks.Transparency) != 0 && pixels)
            {
                var a = Snapshot.Images.GetValueOrDefault(layer.Id); var b = changed.Images.GetValueOrDefault(layer.Id);
                if (a is null || b is null || a.Width != b.Width || a.Height != b.Height) throw new ArgumentException("Unlock transparency before changing the pixel grid.");
                for (int y = 0; y < a.Height; y++) for (int x = 0; x < a.Width; x++)
                    if (a.Pixels[y * a.Stride + x * 4 + 3] != b.Pixels[y * b.Stride + x * 4 + 3]) throw new ArgumentException("Unlock transparency before changing alpha.");
            }
        }
    }
}
