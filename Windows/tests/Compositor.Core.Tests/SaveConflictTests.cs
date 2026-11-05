using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// D05a: the save side of "an outside program must not lose its work". Before this, a save after an
/// external write replaced the other program's manifest with no warning; now the store compares the
/// package against what the editor last agreed with, and refuses before touching a byte.
/// </summary>
public class SaveConflictTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "compositor-conflict-" + Guid.NewGuid().ToString("N")[..8]);

    public SaveConflictTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (IOException) { }
    }

    private async Task<(EditorDocument Document, string Path)> Saved(string name)
    {
        var document = EditorDocument.Create(8, 6);
        var path = Path.Combine(root, name);
        await ProjectStore.SaveAsync(document.Snapshot, path);
        document.MarkSaved(path);
        return (document, path);
    }

    private static Task ExternalWrite(string path, string layerName)
    {
        // The manifest is written with spaces after the colons, and the default layer is called
        // Background; a literal that assumes either would rewrite nothing and the test would pass by
        // doing nothing. Assert the rewrite landed instead.
        string manifest = Path.Combine(path, "manifest.json");
        var text = File.ReadAllText(manifest);
        var updated = System.Text.RegularExpressions.Regex.Replace(text, "\"name\"\\s*:\\s*\"[^\"]*\"", "\"name\": \"" + layerName + "\"");
        Assert.NotEqual(text, updated);
        return File.WriteAllTextAsync(manifest, updated);
    }

    [Fact]
    public async Task SaveIsRefusedAfterSomebodyElseChangedThePackage()
    {
        var (document, path) = await Saved("outside.comp");
        Assert.NotNull(document.SavedDiskDigest);
        await ExternalWrite(path, "TheirsWithLongerName");

        await Assert.ThrowsAsync<ProjectConflictException>(() =>
            ProjectStore.SaveAsync(document.Snapshot, path, document.UnchangedAssetNames(path), default, document.SavedDiskDigest));
        // The refusal happens before anything is written, so their manifest stands.
        Assert.Contains("TheirsWithLongerName", File.ReadAllText(Path.Combine(path, "manifest.json")));
    }

    [Fact]
    public async Task SaveGoesThroughWhileTheDiskStillMatchesWhatWeAcknowledged()
    {
        var (document, path) = await Saved("matching.comp");
        var before = document.SavedDiskDigest!;
        await ProjectStore.SaveAsync(document.Snapshot, path, document.UnchangedAssetNames(path), default, before);
        document.MarkSaved(path);
        Assert.True(document.SavedDiskDigest!.AsSpan().SequenceEqual(ProjectStore.ComputeDigest(path)));
    }

    [Fact]
    public async Task ReAcknowledgingAnExternalChangeUnblocksTheNextSave()
    {
        var (document, path) = await Saved("reack.comp");
        await ExternalWrite(path, "TheirsAgain");
        document.MarkSaved(path); // the user reloaded or took their version; now we agree with the disk

        await ProjectStore.SaveAsync(document.Snapshot, path, document.UnchangedAssetNames(path), default, document.SavedDiskDigest);
        Assert.DoesNotContain("TheirsAgain", File.ReadAllText(Path.Combine(path, "manifest.json")));
    }

    [Fact]
    public async Task ANewPackageWithNothingAcknowledgedSavesNormally()
    {
        var document = EditorDocument.Create(8, 6);
        string path = Path.Combine(root, "brand-new.comp");
        Assert.Null(document.SavedDiskDigest);
        await ProjectStore.SaveAsync(document.Snapshot, path, expectedDiskDigest: document.SavedDiskDigest);
        Assert.True(File.Exists(Path.Combine(path, "manifest.json")));
    }

    /// <summary>
    /// D05b: the "Keep Mine" answer to the external-change prompt. It has to keep claiming the work is
    /// unsaved (otherwise the window would close without asking) while making the outside version the
    /// baseline the next deliberate save may replace — and only for that one time.
    /// </summary>
    [Fact]
    public async Task KeepingMineAdoptsTheirVersionWithoutClaimingTheWorkIsSaved()
    {
        var (document, path) = await Saved("keepmine.comp");
        var layerId = document.Snapshot.Manifest.Layers[0].Id;
        Assert.True(document.RenameLayer(layerId, "MineUnsaved"));
        Assert.True(document.IsDirty);
        await ExternalWrite(path, "TheirsOnDisk");

        document.AdoptDiskState(path);
        Assert.True(document.IsDirty, "keeping mine claimed the unsaved work was saved");
        Assert.True(document.SavedDiskDigest!.AsSpan().SequenceEqual(ProjectStore.ComputeDigest(path)),
            "keeping mine did not take the package on disk as the new baseline");

        await ProjectStore.SaveAsync(document.Snapshot, path, document.UnchangedAssetNames(path), default, document.SavedDiskDigest);
        string manifest = File.ReadAllText(Path.Combine(path, "manifest.json"));
        Assert.Contains("MineUnsaved", manifest);
        Assert.DoesNotContain("TheirsOnDisk", manifest);

        document.MarkSaved(path); // SaveNow does this after a store that answered, so the two agree again
        await ExternalWrite(path, "TheirsAgainTwice");
        await Assert.ThrowsAsync<ProjectConflictException>(() =>
            ProjectStore.SaveAsync(document.Snapshot, path, document.UnchangedAssetNames(path), default, document.SavedDiskDigest));
    }

    /// <summary>
    /// D05b/#20: what the guard does when the package cannot be read. Two halves, both with a real held
    /// handle rather than a mock: comparing at save time throws outward instead of writing over the other
    /// program's manifest, while a baseline the editor could not read when it marked saved leaves every
    /// later save unguarded — today's semantics, pinned here so the hole stops being implicit.
    /// </summary>
    [Fact]
    public async Task AnUnreadableManifestThrowsOutwardRatherThanWritingOverTheirs()
    {
        var (document, path) = await Saved("unreadable-compare.comp");
        await ExternalWrite(path, "TheirsHeld");
        using (var holder = new FileStream(Path.Combine(path, "manifest.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() =>
                ProjectStore.SaveAsync(document.Snapshot, path, document.UnchangedAssetNames(path), default, document.SavedDiskDigest));
        }
        // Read after the hold is released: reading through the holder would raise the very IOException the
        // assertion is about, and the test would pass by tripping over its own file lock.
        Assert.Contains("TheirsHeld", File.ReadAllText(Path.Combine(path, "manifest.json")));
    }

    /// <summary>
    /// The same hold on the way *in*: MarkSaved records the package's shape, and when it cannot read it the
    /// baseline stays null, so the next save skips the comparison entirely and replaces the outside work.
    /// That is the fail-open half of the guard; it is asserted rather than assumed, because the save still
    /// succeeds and nothing else in the document says a baseline is missing.
    /// </summary>
    [Fact]
    public async Task AMissingBaselineSkipsTheComparisonAndReplacesTheOutsideWrite()
    {
        var (document, path) = await Saved("missing-baseline.comp");
        await ExternalWrite(path, "TheirsBeforeMark");
        using (var holder = new FileStream(Path.Combine(path, "manifest.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            document.MarkSaved(path); // Mac's rememberProjectDigest uses `try?` too: a failed read is not an error
            Assert.Null(document.SavedDiskDigest);
        }

        await ProjectStore.SaveAsync(document.Snapshot, path, document.UnchangedAssetNames(path), default, document.SavedDiskDigest);
        Assert.DoesNotContain("TheirsBeforeMark", File.ReadAllText(Path.Combine(path, "manifest.json")));
    }

    /// <summary>The store only compares against the disk when it was handed a baseline; the app needs to
    /// know whether a folder is already a package to say out loud that a save went in unguarded.</summary>
    [Fact]
    public async Task HasPackageAnswersOnlyForAFolderThatAlreadyHoldsAManifest()
    {
        string path = Path.Combine(root, "has-package.comp");
        Assert.False(ProjectStore.HasPackage(path));
        Assert.False(ProjectStore.HasPackage(Path.Combine(root, "nowhere-at-all")));

        var document = EditorDocument.Create(8, 6);
        await ProjectStore.SaveAsync(document.Snapshot, path);
        Assert.True(ProjectStore.HasPackage(path));
    }

    [Fact]
    public async Task TheWatcherHoldsAChangeUntilTheAppAnswersIt()
    {
        var (document, path) = await Saved("pending.comp");
        using var watcher = new ProjectWatcher(path) { Debounce = TimeSpan.FromMilliseconds(40) };
        var arrived = new ManualResetEventSlim(false);
        var notifications = 0;
        watcher.ProjectChanged += () => { Interlocked.Increment(ref notifications); arrived.Set(); };
        Assert.False(watcher.HasPendingChange);

        await ExternalWrite(path, "PendingOne");
        Assert.True(arrived.Wait(TimeSpan.FromSeconds(5)), "the watcher never reported the external change");
        Assert.True(watcher.HasPendingChange, "the change was retired without the app answering");
        Assert.True(watcher.RecheckPendingChange(), "a deferred change could not be re-presented");
        Assert.True(watcher.RecheckPendingChange());

        watcher.AcknowledgeExternalChange();
        Assert.False(watcher.HasPendingChange);
        Assert.False(watcher.RecheckPendingChange(), "an answered change was still re-presented");
    }

    [Fact]
    public async Task OurOwnSaveAnswersTheChangeItJustMade()
    {
        var (document, path) = await Saved("ownsaver.comp");
        using var watcher = new ProjectWatcher(path) { Debounce = TimeSpan.FromMilliseconds(40) };
        await ExternalWrite(path, "ShortLived");
        var arrived = new ManualResetEventSlim(false);
        watcher.ProjectChanged += () => arrived.Set();
        Assert.True(arrived.Wait(TimeSpan.FromSeconds(5)));

        watcher.MarkOwnSave();
        Assert.False(watcher.HasPendingChange);
        Assert.False(watcher.RecheckPendingChange());
    }
}
