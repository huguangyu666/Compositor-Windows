namespace Compositor.Core;

/// <summary>
/// Watches a .comp package for external changes the way the macOS app does: writes land as
/// debounced bursts, each burst's digest is compared against the last one, and only a digest
/// change counts — a package that was merely touched reloads nothing.
/// </summary>
public sealed class ProjectWatcher : IDisposable
{
    /// <summary>How many times one burst's digest read is retried before the burst is given up on.</summary>
    public const int RetryLimit = 4;

    private readonly FileSystemWatcher manifestWatcher;
    private readonly FileSystemWatcher imagesWatcher;
    private readonly SynchronizationContext? synchronization;
    private readonly Timer timer;
    private byte[]? lastDigest;
    private byte[]? pendingDigest;
    private bool disposed;
    private int eventsPending;
    private int retriesInBurst;
    private int retryCount;
    private int gaveUpBursts;
    private int watcherErrorCount;
    private int bufferRecoveries;
    private int bufferRecoveryFailures;
    private int ownSaveDigestFailures;

    /// <summary>Raised when the package's digest changed after the debounce window.</summary>
    public event Action? ProjectChanged;

    /// <summary>Raised when a burst exhausts its retry budget and is given up on. The next real
    /// filesystem event starts a fresh burst, so this is a report, not a dead end.</summary>
    public event Action? RetriesExhausted;

    public ProjectWatcher(string projectPath)
    {
        ProjectPath = projectPath;
        synchronization = SynchronizationContext.Current;
        timer = new Timer(_ => OnDebounced(), null, Timeout.Infinite, Timeout.Infinite);

        manifestWatcher = new FileSystemWatcher(projectPath, "manifest.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
        };
        manifestWatcher.Changed += OnEvent;
        manifestWatcher.Created += OnEvent;
        manifestWatcher.Deleted += OnEvent;
        manifestWatcher.Renamed += OnEvent;
        manifestWatcher.Error += OnWatcherError;
        manifestWatcher.EnableRaisingEvents = true;

        string imagesPath = Path.Combine(projectPath, "images");
        Directory.CreateDirectory(imagesPath); // a project without images still watches for them
        imagesWatcher = new FileSystemWatcher(imagesPath, "*.png")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            IncludeSubdirectories = false,
        };
        imagesWatcher.Changed += OnEvent;
        imagesWatcher.Created += OnEvent;
        imagesWatcher.Deleted += OnEvent;
        imagesWatcher.Renamed += OnEvent;
        imagesWatcher.Error += OnWatcherError;
        imagesWatcher.EnableRaisingEvents = true;

        try
        {
            lastDigest = ProjectStore.ComputeDigest(projectPath);
        }
        catch (IOException)
        {
            lastDigest = null;
        }
    }

    public string ProjectPath { get; }

    /// <summary>How long writes must stay quiet before a burst is treated as complete.</summary>
    public TimeSpan Debounce { get; set; } = TimeSpan.FromMilliseconds(350);

    /// <summary>How long to wait before each retry of a failed digest read. Attempt is 0-based, so the
    /// default backs off 250 ms, 500 ms, 1 s, 2 s — bounded, and never a busy loop.</summary>
    public Func<int, TimeSpan> RetrySchedule { get; set; } =
        attempt => TimeSpan.FromMilliseconds(250L * (1L << Math.Min(attempt, 3)));

    /// <summary>Retries this watcher has scheduled in its lifetime (across all bursts).</summary>
    public int RetryCount => Volatile.Read(ref retryCount);

    /// <summary>Bursts that gave up after <see cref="RetryLimit"/> failed reads without reporting a change.</summary>
    public int GivenUpBursts => Volatile.Read(ref gaveUpBursts);

    /// <summary>Watcher-level errors seen (a full internal buffer being the usual one).</summary>
    public int WatcherErrorCount => Volatile.Read(ref watcherErrorCount);

    /// <summary>Times a buffer was re-armed after an error, and times re-arming itself failed.</summary>
    public int BufferRecoveries => Volatile.Read(ref bufferRecoveries);

    public int BufferRecoveryFailures => Volatile.Read(ref bufferRecoveryFailures);

    /// <summary>Times the app's own save could not be digested, so a later burst has to decide.</summary>
    public int OwnSaveDigestFailures => Volatile.Read(ref ownSaveDigestFailures);

    /// <summary>One line for the log and the watcher probe, so a recovery that only lives in counters
    /// is still visible to whoever is reading the session.</summary>
    public string Status() => $"events pending={eventsPending} retries={RetryCount} givenUp={GivenUpBursts} " +
        $"watcherErrors={WatcherErrorCount} bufferRecoveries={BufferRecoveries} bufferRecoveryFailures={BufferRecoveryFailures} " +
        $"ownSaveDigestFailures={OwnSaveDigestFailures}";

    private void OnEvent(object sender, FileSystemEventArgs e)
    {
        // Every write resets the window: a burst of writes fires once, after quiet. A fresh event also
        // means a fresh burst, so an earlier burst that gave up cannot eat this one's retry budget.
        Interlocked.Exchange(ref eventsPending, 1);
        Volatile.Write(ref retriesInBurst, 0);
        timer.Change(Debounce, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// The read failed (a package caught mid-write, a lock, a directory being swapped). Retry the same
    /// burst a bounded number of times instead of waiting for an event that may never come, and say so
    /// once the budget runs out. Timer-driven, so there is no busy loop on a persistently bad path.
    /// </summary>
    private void ScheduleBurstRetry()
    {
        if (disposed) return;
        if (Volatile.Read(ref retriesInBurst) >= RetryLimit)
        {
            Volatile.Write(ref retriesInBurst, 0);
            Interlocked.Increment(ref gaveUpBursts);
            RetriesExhausted?.Invoke();
            return;
        }
        int attempt = Interlocked.Increment(ref retriesInBurst) - 1;
        Interlocked.Increment(ref retryCount);
        timer.Change(RetrySchedule(attempt), Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// A full internal buffer drops the queued events and the watcher goes quiet, which would hide
    /// every later change. Recreating the buffer and re-arming the window is the only recovery, and
    /// it has to be silent: this runs on the watcher's own thread.
    /// </summary>
    private void OnWatcherError(object? sender, ErrorEventArgs e)
    {
        if (disposed) return;
        Interlocked.Increment(ref watcherErrorCount);
        if (sender is FileSystemWatcher watcher)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.EnableRaisingEvents = true;
                Interlocked.Increment(ref bufferRecoveries);
            }
            catch (Exception)
            {
                // Whatever the re-arm rejects (locked, disposed, a path that no longer exists), this
                // runs on the watcher's own thread and an escaping exception would take the process
                // down. It is counted so the session shows a watcher that stopped recovering.
                Interlocked.Increment(ref bufferRecoveryFailures);
            }
        }

        Interlocked.Exchange(ref eventsPending, 1);
        Volatile.Write(ref retriesInBurst, 0);
        timer.Change(RetrySchedule(0), Timeout.InfiniteTimeSpan);
    }

    private void OnDebounced()
    {
        Interlocked.Exchange(ref eventsPending, 0);
        if (disposed) return;
        byte[] digest;
        try
        {
            digest = ProjectStore.ComputeDigest(ProjectPath);
        }
        catch (IOException)
        {
            ScheduleBurstRetry(); // caught mid-write; this burst is not finished yet
            return;
        }
        catch (UnauthorizedAccessException)
        {
            ScheduleBurstRetry();
            return;
        }
        catch (Exception)
        {
            // This runs on a timer thread: anything escaping it would take the process down, so an
            // unexpected package shape is treated like a transient read failure and retried the same
            // bounded way. The digest itself only reads bytes and directory entries.
            ScheduleBurstRetry();
            return;
        }
        var known = lastDigest;
        if (known is not null && digest.AsSpan().SequenceEqual(known))
        {
            // Back to what the document holds, so a change the app had not handled yet is dropped.
            if (pendingDigest is not null) pendingDigest = null;
            return;
        }
        if (pendingDigest is not null && digest.AsSpan().SequenceEqual(pendingDigest)) return; // already asked
        pendingDigest = digest;
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        if (synchronization is { } context) context.Post(_ => ProjectChanged?.Invoke(), null);
        else ProjectChanged?.Invoke();
    }

    /// <summary>Whether an external change has been seen but the app has not finished handling it.</summary>
    public bool HasPendingChange => Volatile.Read(ref pendingDigest) is not null;

    /// <summary>
    /// Called once the app has taken the package as it stands (it reloaded, or it saved over it and
    /// the digest was checked). This is the only thing that retires a pending change.
    /// </summary>
    public void AcknowledgeExternalChange()
    {
        Volatile.Write(ref pendingDigest, null);
        try
        {
            lastDigest = ProjectStore.ComputeDigest(ProjectPath);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Re-presents a pending change to the app. Used when a prompt was deferred because the editor was
    /// mid-stroke or had unsaved work: without this, "ask again later" would never ask again.
    /// </summary>
    public bool RecheckPendingChange()
    {
        if (disposed || Volatile.Read(ref pendingDigest) is null) return false;
        RaiseChanged();
        return true;
    }

    /// <summary>Call after the app itself saves, so its own write does not reload the document.</summary>
    public void MarkOwnSave()
    {
        // Our own save is the answer to a pending change, whatever the app does with it next.
        Volatile.Write(ref pendingDigest, null);
        try
        {
            lastDigest = ProjectStore.ComputeDigest(ProjectPath);
        }
        catch (IOException)
        {
            // The digest is refreshed on the next burst; a save must not fail because of this.
            Interlocked.Increment(ref ownSaveDigestFailures);
        }
        catch (UnauthorizedAccessException)
        {
            Interlocked.Increment(ref ownSaveDigestFailures);
        }
    }

    /// <summary>
    /// Test seam: run exactly the path <c>FileSystemWatcher.Error</c> takes, on a chosen watcher.
    /// The real event has no public way to be raised, and the recovery it triggers is the part this
    /// class must not get wrong.
    /// </summary>
    internal void RaiseWatcherErrorForTests(FileSystemWatcher? watcher = null) =>
        OnWatcherError(watcher ?? manifestWatcher, new ErrorEventArgs(new IOException("injected buffer overflow")));

    public void Dispose()
    {
        disposed = true;
        manifestWatcher.Dispose();
        imagesWatcher.Dispose();
        timer.Dispose();
    }
}
