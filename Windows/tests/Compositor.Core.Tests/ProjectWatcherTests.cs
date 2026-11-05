using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// The watcher owns the "an outside editor changed this package" signal, so a package that is only
/// briefly readable must not cost the user the next change. These runs use the real file system:
/// notification timing is external, so every wait is bounded and the assertions are about whether a
/// change eventually arrived rather than how fast.
/// </summary>
public sealed class ProjectWatcherTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private readonly string root = Path.Combine(Path.GetTempPath(), "compositor-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task BurstOfExternalWritesRaisesExactlyOneChangeAfterTheQuietWindow()
    {
        var path = await SaveProjectAsync("burst");
        using var watcher = new ProjectWatcher(path) { Debounce = TimeSpan.FromMilliseconds(40) };
        var changes = 0;
        var arrived = new ManualResetEventSlim(false);
        watcher.ProjectChanged += () => { Interlocked.Increment(ref changes); arrived.Set(); };

        await WriteManifestAsync(path, "Outer");
        await WriteManifestAsync(path, "Inner");
        Assert.True(arrived.Wait(Wait), "the watcher never reported the external change");
        var digest = ProjectStore.ComputeDigest(path);
        var seen = Volatile.Read(ref changes);
        await WriteManifestAsync(path, "Inner"); // the same bytes: the digest decides, not the event
        await Task.Delay(300);
        Assert.True(digest.SequenceEqual(ProjectStore.ComputeDigest(path)), "rewriting identical bytes changed the digest");
        Assert.Equal(seen, Volatile.Read(ref changes));
    }

    [Fact]
    public async Task UnreadableManifestDoesNotSwallowTheNextChange()
    {
        var path = await SaveProjectAsync("locked");
        using var watcher = new ProjectWatcher(path) { Debounce = TimeSpan.FromMilliseconds(40) };
        var changes = 0;
        var arrived = new ManualResetEventSlim(false);
        watcher.ProjectChanged += () => { Interlocked.Increment(ref changes); arrived.Set(); };

        var manifestPath = Path.Combine(path, "manifest.json");
        using (var holder = new FileStream(manifestPath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            // Something touches the package while the manifest is claimed: the digest read throws,
            // the burst is dropped, and no half-informed reload is started.
            File.WriteAllBytes(Path.Combine(path, "images", "extra.png"), [1, 2, 3, 4]);
            await Task.Delay(400);
            Assert.Equal(0, Volatile.Read(ref changes));
        }

        await WriteManifestAsync(path, "AfterRelease");
        Assert.True(arrived.Wait(Wait), "the watcher stayed deaf after one unreadable read");
        Assert.Contains("AfterRelease", File.ReadAllText(manifestPath));
    }

    [Fact]
    public async Task MissingImagesFolderIsReportedAsAChangeNotAThrow()
    {
        var path = await SaveProjectAsync("noimages");
        using var watcher = new ProjectWatcher(path) { Debounce = TimeSpan.FromMilliseconds(40) };
        var arrived = new ManualResetEventSlim(false);
        watcher.ProjectChanged += () => arrived.Set();

        Directory.Delete(Path.Combine(path, "images"), recursive: true);
        Assert.True(arrived.Wait(Wait), "losing images/ produced no change signal");
    }

    [Fact]
    public async Task DisposedWatcherIgnoresLaterWrites()
    {
        var path = await SaveProjectAsync("retired");
        var watcher = new ProjectWatcher(path) { Debounce = TimeSpan.FromMilliseconds(40) };
        var changes = 0;
        watcher.ProjectChanged += () => Interlocked.Increment(ref changes);
        watcher.Dispose();

        await WriteManifestAsync(path, "TooLate");
        await Task.Delay(300);
        Assert.Equal(0, Volatile.Read(ref changes));
    }

    [Fact]
    public async Task OwnSaveDoesNotReloadTheDocument()
    {
        var path = await SaveProjectAsync("ownsave");
        // A wider window here: MarkOwnSave has to land inside the debounce the way SaveNow does.
        using var watcher = new ProjectWatcher(path) { Debounce = TimeSpan.FromMilliseconds(300) };
        var arrived = new ManualResetEventSlim(false);
        watcher.ProjectChanged += () => arrived.Set();

        await WriteManifestAsync(path, "AppWroteThis");
        watcher.MarkOwnSave();
        await Task.Delay(600);
        Assert.False(arrived.IsSet);

        await WriteManifestAsync(path, "SomeoneElse");
        Assert.True(arrived.Wait(Wait), "the watcher stayed muted after the app's own save");
    }

    [Fact]
    public async Task AFailedDigestReadRetriesABoundedNumberOfTimesAndSaysSo()
    {
        var path = await SaveProjectAsync("retry-budget");
        using var watcher = new ProjectWatcher(path)
        {
            Debounce = TimeSpan.FromMilliseconds(40),
            RetrySchedule = _ => TimeSpan.FromMilliseconds(5),
        };
        var changes = 0;
        watcher.ProjectChanged += () => Interlocked.Increment(ref changes);
        var exhausted = new ManualResetEventSlim(false);
        watcher.RetriesExhausted += () => exhausted.Set();

        // A package with no manifest is readable-failure terrain for the digest, and no further
        // filesystem event will ever arrive to "let the next burst decide".
        File.Delete(Path.Combine(path, "manifest.json"));
        Assert.True(exhausted.Wait(Wait), $"the retry budget was never exhausted; {watcher.Status()}");
        Assert.InRange(watcher.RetryCount, ProjectWatcher.RetryLimit, ProjectWatcher.RetryLimit * 2);
        Assert.True(watcher.GivenUpBursts >= 1, watcher.Status());
        Assert.Equal(0, Volatile.Read(ref changes));
        Assert.Contains($"retries={watcher.RetryCount}", watcher.Status());
    }

    [Fact]
    public async Task GivingUpOnOneBurstDoesNotStopTheWatcherFromReportingTheNextChange()
    {
        var path = await SaveProjectAsync("retry-recovers");
        using var watcher = new ProjectWatcher(path)
        {
            Debounce = TimeSpan.FromMilliseconds(40),
            RetrySchedule = _ => TimeSpan.FromMilliseconds(5),
        };
        var arrived = new ManualResetEventSlim(false);
        var exhausted = new ManualResetEventSlim(false);
        watcher.ProjectChanged += () => arrived.Set();
        watcher.RetriesExhausted += () => exhausted.Set();

        File.Delete(Path.Combine(path, "manifest.json"));
        Assert.True(exhausted.Wait(Wait), $"the retry budget was never exhausted; {watcher.Status()}");
        var retriesAfterGiveUp = watcher.RetryCount;

        await RestoreProjectAsync(path, "Restored");
        Assert.True(arrived.Wait(Wait), "the watcher stayed deaf after a burst gave up");
        Assert.True(watcher.RetryCount - retriesAfterGiveUp < ProjectWatcher.RetryLimit,
            $"a healthy burst consumed a whole retry budget: {watcher.Status()}");
    }

    [Fact]
    public async Task AWatcherErrorReArmsTheBufferAndIsCounted()
    {
        var path = await SaveProjectAsync("error-rearms");
        using var watcher = new ProjectWatcher(path) { Debounce = TimeSpan.FromMilliseconds(40) };
        var arrived = new ManualResetEventSlim(false);
        watcher.ProjectChanged += () => arrived.Set();

        watcher.RaiseWatcherErrorForTests();
        Assert.Equal(1, watcher.WatcherErrorCount);
        Assert.Equal(1, watcher.BufferRecoveries);
        Assert.Equal(0, watcher.BufferRecoveryFailures);

        await WriteManifestAsync(path, "After Recovery");
        Assert.True(arrived.Wait(Wait), "re-arming the buffer left the watcher unable to report the next change");
    }

    [Fact]
    public async Task AWatcherErrorThatCannotReArmIsReportedSeparately()
    {
        var path = await SaveProjectAsync("error-fails");
        using var watcher = new ProjectWatcher(path) { Debounce = TimeSpan.FromMilliseconds(40) };
        var dead = new FileSystemWatcher(root);
        dead.Dispose();

        watcher.RaiseWatcherErrorForTests(dead);
        Assert.Equal(1, watcher.WatcherErrorCount);
        Assert.Equal(0, watcher.BufferRecoveries);
        Assert.Equal(1, watcher.BufferRecoveryFailures);
        Assert.Contains("bufferRecoveryFailures=1", watcher.Status());
    }

    /// <summary>Rebuilds the package through the real store with a renamed layer, after a test deleted
    /// the manifest to force digest failures. Hand-writing manifest.json would test my memory of the
    /// schema instead of the watcher.</summary>
    private async Task RestoreProjectAsync(string path, string layerName)
    {
        var document = EditorDocument.Create(8, 6);
        document.RenameLayer(document.Snapshot.Manifest.Layers[0].Id, layerName);
        await ProjectStore.SaveAsync(document.Snapshot, path);
    }

    private async Task<string> SaveProjectAsync(string name)    {
        var path = Path.Combine(root, name);
        var document = EditorDocument.Create(8, 6);
        await ProjectStore.SaveAsync(document.Snapshot, path);
        return path;
    }

    private static async Task WriteManifestAsync(string path, string layerName)
    {
        var manifestPath = Path.Combine(path, "manifest.json");
        var text = await File.ReadAllTextAsync(manifestPath);
        var updated = System.Text.RegularExpressions.Regex.Replace(text, "\"name\"\\s*:\\s*\"[^\"]*\"", "\"name\": \"" + layerName + "\"");
        // Proves the fixture rewrite happened; the same name twice is the deliberate no-change case.
        Assert.Contains("\"name\": \"" + layerName + "\"", updated);
        await File.WriteAllTextAsync(manifestPath, updated);
    }
}
