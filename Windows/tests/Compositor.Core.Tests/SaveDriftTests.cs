using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// D11: what a second save is allowed to change. The existing coverage (ProjectStoreTests:297) only
/// compares manifest.json bytes, so PNG assets, transparency, gray masks and the watcher digest could
/// each drift between two saves of the same snapshot and nothing would notice. These do.
/// </summary>
public class SaveDriftTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "compositor-save-drift-" + Guid.NewGuid().ToString("N")[..8]);

    public SaveDriftTests() => Directory.CreateDirectory(root);

    private string ProjectPath(string name) => Path.Combine(root, name);

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (IOException) { }
    }

    private static Surface Solid(int width, int height, byte r, byte g, byte b, byte a = 255)
    {
        var surface = new Surface(width, height);
        for (int i = 0; i < surface.Pixels.Length; i += 4)
        {
            surface.Pixels[i] = r; surface.Pixels[i + 1] = g; surface.Pixels[i + 2] = b; surface.Pixels[i + 3] = a;
        }
        return surface;
    }

    private static ProjectSnapshot TwoLayerDocument(out Guid kept, out Guid edited, bool withMask = true)
    {
        kept = Guid.NewGuid(); edited = Guid.NewGuid();
        var manifest = new ProjectManifest
        {
            DocumentId = Guid.NewGuid(),
            Width = 8,
            Height = 8,
            ActiveLayerId = edited,
            Layers =
            [
                new ProjectLayerRecord
                {
                    Id = kept, Name = "Kept", IsVisible = true, ImageFile = ProjectValidator.Uuid(kept) + ".png",
                    MaskFile = withMask ? ProjectValidator.Uuid(kept) + ".mask.png" : null,
                    MaskEnabled = withMask ? true : null,
                    Transform = new LayerTransform { Origin = new PointD(0, 0), Size = new SizeD(8, 8) },
                },
                new ProjectLayerRecord
                {
                    Id = edited, Name = "Edited", IsVisible = true, ImageFile = ProjectValidator.Uuid(edited) + ".png",
                    Transform = new LayerTransform { Origin = new PointD(1, 1), Size = new SizeD(4, 4) },
                },
            ],
        };
        var images = new Dictionary<Guid, Surface>
        {
            // A half-transparent asset: a re-encode that dropped or flattened alpha would still be
            // valid PNG bytes, so equality has to be checked on the bytes, not the dimensions.
            [kept] = Solid(8, 8, 200, 100, 50, a: 128),
            [edited] = Solid(4, 4, 9, 9, 9),
        };
        var masks = new Dictionary<Guid, MaskSurface>();
        if (withMask)
        {
            var mask = new MaskSurface(8, 8);
            for (int i = 0; i < mask.Pixels.Length; i++) mask.Pixels[i] = (byte)(i % 251);
            masks[kept] = mask;
        }
        return new ProjectSnapshot { Manifest = manifest, Images = images, Masks = masks };
    }

    private static Dictionary<string, byte[]> AssetBytes(string project) =>
        Directory.EnumerateFiles(Path.Combine(project, ProjectStore.ImagesFolderName))
            .ToDictionary(file => Path.GetFileName(file), file => File.ReadAllBytes(file), StringComparer.Ordinal);

    [Fact]
    public async Task SavingTheSameSnapshotTwiceChangesNoAssetAndNoDigest()
    {
        string path = ProjectPath("same.comp");
        var snapshot = TwoLayerDocument(out _, out _);
        await ProjectStore.SaveAsync(snapshot, path);
        var firstManifest = await File.ReadAllBytesAsync(Path.Combine(path, "manifest.json"));
        var firstAssets = AssetBytes(path);
        var firstDigest = ProjectStore.ComputeDigest(path);

        await ProjectStore.SaveAsync(snapshot, path);
        Assert.Equal(firstManifest, await File.ReadAllBytesAsync(Path.Combine(path, "manifest.json")));
        var secondAssets = AssetBytes(path);
        Assert.Equal(firstAssets.Keys.OrderBy(name => name, StringComparer.Ordinal), secondAssets.Keys.OrderBy(name => name, StringComparer.Ordinal));
        foreach (var (name, bytes) in firstAssets)
        {
            Assert.True(secondAssets.ContainsKey(name), $"{name} vanished from the second save.");
            Assert.Equal(bytes, secondAssets[name]);
        }

        Assert.Equal(firstDigest, ProjectStore.ComputeDigest(path));
        var reloaded = await ProjectStore.LoadAsync(path);
        Assert.Equal(2, reloaded.Manifest.Layers.Count);
    }

    [Fact]
    public async Task GrayMaskAssetDoesNotDriftBetweenSaves()
    {
        string path = ProjectPath("mask.comp");
        var snapshot = TwoLayerDocument(out var kept, out _);
        await ProjectStore.SaveAsync(snapshot, path);
        var maskFile = Path.Combine(path, ProjectStore.ImagesFolderName, ProjectValidator.Uuid(kept) + ".mask.png");
        Assert.True(File.Exists(maskFile), "the mask asset is not where the store says it is.");
        var first = await File.ReadAllBytesAsync(maskFile);

        await ProjectStore.SaveAsync(snapshot, path);
        Assert.Equal(first, await File.ReadAllBytesAsync(maskFile));

        var reloaded = await ProjectStore.LoadAsync(path);
        var mask = reloaded.Masks[kept];
        Assert.Equal(8, mask.Width);
        Assert.Equal(snapshot.Masks[kept].Pixels, mask.Pixels);
    }

    [Fact]
    public async Task SavingAfterOneLayerChangedRewritesThatAssetAndNotTheOther()
    {
        string path = ProjectPath("edited.comp");
        var snapshot = TwoLayerDocument(out var kept, out var edited);
        await ProjectStore.SaveAsync(snapshot, path);
        string keptAsset = Path.Combine(path, ProjectStore.ImagesFolderName, ProjectValidator.Uuid(kept) + ".png");
        string editedAsset = Path.Combine(path, ProjectStore.ImagesFolderName, ProjectValidator.Uuid(edited) + ".png");
        var keptBefore = await File.ReadAllBytesAsync(keptAsset);
        var editedBefore = await File.ReadAllBytesAsync(editedAsset);

        var changedImages = new Dictionary<Guid, Surface>(snapshot.Images) { [edited] = Solid(4, 4, 250, 10, 10) };
        await ProjectStore.SaveAsync(new ProjectSnapshot
        {
            Manifest = snapshot.Manifest,
            Images = changedImages,
            Masks = snapshot.Masks,
        }, path);

        Assert.Equal(keptBefore, await File.ReadAllBytesAsync(keptAsset));
        Assert.False((await File.ReadAllBytesAsync(editedAsset)).AsSpan().SequenceEqual(editedBefore));
        var reloaded = await ProjectStore.LoadAsync(path);
        Assert.Equal(250, reloaded.Images[edited].Pixels[0]);
        // Two solid 4×4 PNGs compress to the same length, and the digest is manifest + asset lengths
        // (ProjectStore.ComputeDigest:206-213). So this rewrite is invisible to the digest by design:
        // the watcher trusts same-length asset replacement. That blind spot is pinned on purpose in
        // TheDigestSeesLengthChangesAndMissesSameLengthRewrites rather than papered over here.
    }

    [Fact]
    public async Task TheDigestSeesLengthChangesAndMissesSameLengthRewrites()
    {
        // Both directions of the documented rule, measured rather than asserted away: a touch with the
        // same bytes is not a change, an asset whose length changes is, and an asset rewritten at the
        // same length is NOT detected. The last one is the trust boundary D07's report already names.
        string path = ProjectPath("digest2.comp");
        var snapshot = TwoLayerDocument(out var kept, out _);
        await ProjectStore.SaveAsync(snapshot, path);
        var digest = ProjectStore.ComputeDigest(path);

        File.SetLastWriteTimeUtc(Path.Combine(path, "manifest.json"), DateTime.UtcNow.AddSeconds(-30));
        Assert.Equal(digest, ProjectStore.ComputeDigest(path));

        string asset = Path.Combine(path, ProjectStore.ImagesFolderName, ProjectValidator.Uuid(kept) + ".png");
        var bytes = await File.ReadAllBytesAsync(asset);
        bytes[^5] ^= 0xFF; // flip a byte inside the payload, keep the length
        await File.WriteAllBytesAsync(asset, bytes);
        Assert.Equal(digest, ProjectStore.ComputeDigest(path));

        await File.WriteAllBytesAsync(asset, [.. bytes, 0, 0, 0]);
        Assert.NotEqual(digest, ProjectStore.ComputeDigest(path));
    }
}
