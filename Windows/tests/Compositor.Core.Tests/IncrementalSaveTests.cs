using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// `ProjectStore.SaveAsync` has always accepted the set of assets whose bytes on disk are already
/// what the document holds, but nothing passed it, so every save re-encoded the whole project.
/// Skipping is only sound while the file really is the one that was written, which is what the
/// fingerprint recorded by `MarkSaved(directory)` is for. The asset that cannot be verified is
/// re-encoded, which is exactly the behaviour the wiring used to have.
/// </summary>
public sealed class IncrementalSaveTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "compositor-incremental", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    private string ProjectPath(string name) => Path.Combine(root, name);

    private static ProjectLayerRecord Row(Guid id, string name, int side) => new()
    {
        Id = id,
        Name = name,
        IsVisible = true,
        ImageFile = ProjectValidator.Uuid(id) + ".png",
        Transform = new LayerTransform { Origin = new PointD(0, 0), Size = new SizeD(side, side) },
    };

    private static Surface Solid(int side, byte tone)
    {
        var surface = new Surface(side, side);
        for (int i = 0; i < surface.Pixels.Length; i += 4)
        {
            surface.Pixels[i] = tone;
            surface.Pixels[i + 1] = tone;
            surface.Pixels[i + 2] = tone;
            surface.Pixels[i + 3] = 255;
        }
        return surface;
    }

    private static EditorDocument TwoLayerDocument(int side, out string backFile, out string frontFile)
    {
        var back = Guid.NewGuid();
        var front = Guid.NewGuid();
        backFile = ProjectValidator.Uuid(back) + ".png";
        frontFile = ProjectValidator.Uuid(front) + ".png";
        var manifest = new ProjectManifest
        {
            DocumentId = Guid.NewGuid(),
            Width = side,
            Height = side,
            ActiveLayerId = front,
            Layers = [Row(back, "Back", side), Row(front, "Front", side)],
        };
        return new EditorDocument(new ProjectSnapshot
        {
            Manifest = manifest,
            Images = new Dictionary<Guid, Surface> { [back] = Solid(side, 8), [front] = Solid(side, 200) },
            Masks = new Dictionary<Guid, MaskSurface>(),
        });
    }

    private static byte[] SameLengthPayload(int length, byte value)
    {
        var payload = new byte[length];
        Array.Fill(payload, value);
        payload[1] = (byte)(value + 1);
        return payload;
    }

    [Fact]
    public async Task VerifiedAssetsAreSkippedAndAnOutsideRewriteIsReEncoded()
    {
        string path = ProjectPath("skip.comp");
        var document = TwoLayerDocument(64, out string backName, out string frontName);
        await ProjectStore.SaveAsync(document.Snapshot, path);
        document.MarkSaved(path);
        string back = Path.Combine(path, "images", backName);
        string front = Path.Combine(path, "images", frontName);
        byte[] writtenBack = await File.ReadAllBytesAsync(back);
        byte[] writtenFront = await File.ReadAllBytesAsync(front);
        Assert.Contains(backName, document.UnchangedAssetNames(path));

        // Another program replaces the back PNG with a payload of exactly the recorded length and
        // write time: the fingerprint cannot tell, so the save skips it and those bytes survive.
        // That is the trust boundary, and the next block is what keeps ordinary writes honest.
        var stamp = new FileInfo(back).LastWriteTimeUtc;
        await File.WriteAllBytesAsync(back, SameLengthPayload(writtenBack.Length, 0xAB));
        File.SetLastWriteTimeUtc(back, stamp);
        await ProjectStore.SaveAsync(document.Snapshot, path, document.UnchangedAssetNames(path));
        byte[] spoofed = await File.ReadAllBytesAsync(back);
        Assert.Equal(0xAB, spoofed[0]);
        Assert.False(writtenBack.AsSpan().SequenceEqual(spoofed), "the skipped asset was rewritten anyway");
        byte[] stillFront = await File.ReadAllBytesAsync(front);
        Assert.True(writtenFront.AsSpan().SequenceEqual(stillFront), "the untouched front asset was re-encoded");

        // A rewrite the fingerprint can see — here a newer write time — is re-encoded rather than
        // left under our manifest, which is what saving without the set always did.
        File.SetLastWriteTimeUtc(back, stamp.AddSeconds(1));
        Assert.DoesNotContain(backName, document.UnchangedAssetNames(path));
        await ProjectStore.SaveAsync(document.Snapshot, path, document.UnchangedAssetNames(path));
        Assert.True((await File.ReadAllBytesAsync(back)).AsSpan().SequenceEqual(writtenBack), "the touched asset was not restored to our pixels");
        Assert.Contains(frontName, document.UnchangedAssetNames(path));
    }

    [Fact]
    public async Task ADocumentSavedWithoutADirectoryVerifiesNothingAndSkipsNothing()
    {
        // The parameterless MarkSaved keeps the identity rule the other tests rely on, but a save
        // that asks for verification must not trust an unrecorded fingerprint.
        string path = ProjectPath("untracked.comp");
        var document = TwoLayerDocument(8, out string backName, out _);
        await ProjectStore.SaveAsync(document.Snapshot, path);
        document.MarkSaved();
        Assert.Contains(backName, document.UnchangedAssetNames());
        Assert.DoesNotContain(backName, document.UnchangedAssetNames(path));
    }

    [Fact]
    public async Task ALoadedProjectSkipsEveryAssetTheEditDidNotTouch()
    {
        string path = ProjectPath("reload.comp");
        var document = TwoLayerDocument(2048, out _, out _);
        await ProjectStore.SaveAsync(document.Snapshot, path);
        document.MarkSaved(path);
        var loaded = new EditorDocument(await ProjectStore.LoadAsync(path));
        loaded.MarkSaved(path);
        Assert.Equal(2, loaded.UnchangedAssetNames(path).Count);

        var full = new List<long>();
        var skipping = new List<long>();
        for (int round = 0; round < 5; round++)
        {
            // Every save is followed by MarkSaved, as the editor does: re-writing an asset changes
            // its stamp, and the next save must record the new one before anything can be skipped.
            var timer = System.Diagnostics.Stopwatch.StartNew();
            await ProjectStore.SaveAsync(loaded.Snapshot, path, new HashSet<string>());
            timer.Stop();
            loaded.MarkSaved(path);
            full.Add(timer.ElapsedMilliseconds);
            timer.Restart();
            await ProjectStore.SaveAsync(loaded.Snapshot, path, loaded.UnchangedAssetNames(path));
            timer.Stop();
            loaded.MarkSaved(path);
            skipping.Add(timer.ElapsedMilliseconds);
        }
        long Middle(List<long> samples) => samples.OrderBy(sample => sample).ElementAt(samples.Count / 2);
        // Measured on this machine in five alternating rounds: 2 × 2048² layers save in 125–127 ms
        // when every asset is re-encoded and 1–2 ms when both are skipped; 2 × 64² goes 4–5 ms → 1 ms.
        Assert.True(Middle(skipping) * 4 < Middle(full), $"skipping did not pay for itself: skipping {string.Join('/', skipping)} vs full {string.Join('/', full)} ms");
        Assert.Equal(2, loaded.UnchangedAssetNames(path).Count);
    }

    [Fact]
    public async Task AnEditedLayerIsTheOnlyOneReEncoded()
    {
        // Re-encoding a PNG whose surface did not change produces the same bytes, so skipping is
        // proven by a marker the save would have to overwrite: the back layer carries one and must
        // still carry it, while the edited front layer goes back to our pixels.
        string path = ProjectPath("edit.comp");
        var document = TwoLayerDocument(64, out string backName, out string frontName);
        await ProjectStore.SaveAsync(document.Snapshot, path);
        document.MarkSaved(path);
        string backFile = Path.Combine(path, "images", backName);
        int backLength = (int)new FileInfo(backFile).Length;
        var backStamp = new FileInfo(backFile).LastWriteTimeUtc;
        await File.WriteAllBytesAsync(backFile, SameLengthPayload(backLength, 0xCD));
        File.SetLastWriteTimeUtc(backFile, backStamp);
        var front = document.Snapshot.Manifest.Layers[1].Id;
        document.ReplaceLayerImage(front, Solid(64, 40));

        var skipped = document.UnchangedAssetNames(path);
        Assert.Contains(backName, skipped);
        Assert.DoesNotContain(frontName, skipped);
        await ProjectStore.SaveAsync(document.Snapshot, path, skipped);
        Assert.Equal(0xCD, (await File.ReadAllBytesAsync(backFile))[0]);
        document.MarkSaved(path);
        Assert.Equal(2, document.UnchangedAssetNames(path).Count);
    }
}
