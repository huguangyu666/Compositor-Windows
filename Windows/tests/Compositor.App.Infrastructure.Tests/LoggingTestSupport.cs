using System.Text;
using Compositor.App;
using Xunit;

namespace Compositor.App.Tests;

[CollectionDefinition(Name)]
public sealed class AppLoggingCollection
{
    // The logger holds process-wide queue and worker state, so these tests never overlap.
    public const string Name = "app-logging-serial";
}

// Shared seams: the logger is process-wide, so every test that starts a session belongs to the
// serial collection and must stop it again.
internal static class LoggingTestSupport
{
    internal static readonly UTF8Encoding Utf8 = new(false);
    internal static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    internal static void Restart(AppLogging.Settings settings, AppLogging.ILogSink? sink = null)
    {
        // A session whose sink never answered stays parked forever, so tests take a new one.
        AppLogging.Shutdown(TimeSpan.FromSeconds(2));
        AppLogging.Start(settings, sink, force: true);
    }

    // Bounded polling for "eventually visible" checks only; the blocking and ordering evidence below
    // uses explicit events instead.
    internal static void WaitFor(Func<bool> until, int timeoutMilliseconds = 2000, string because = "the logger did not reach the expected state")
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (Environment.TickCount64 < deadline)
            if (until()) return;
            else Thread.Sleep(5);

        Assert.True(until(), $"{because} (after {timeoutMilliseconds} ms).");
    }

    internal static IReadOnlyList<string> Records(RecordingSink sink) => sink.Lines.Where(line => !line.StartsWith("logging summary", StringComparison.Ordinal)).ToArray();

    internal static string CurrentLogFileName(string directory) => Path.Combine(directory, $"compositor-app-{Environment.ProcessId}.log");
}

internal sealed class RecordingSink : AppLogging.ILogSink
{
    private readonly object sync = new();
    private readonly List<string> lines = [];

    internal readonly ManualResetEventSlim Entered = new(false);
    internal readonly ManualResetEventSlim Release = new(false);

    internal int WriteThreadId = -1;
    internal int WriteCalls;
    internal int FlushCalls;
    internal int CloseCalls;
    internal bool BlockFirstWrite;
    internal int ThrowOnWriteAfter = -1;
    // Blocks every write after the Nth one, which is how the shutdown tests park the consumer on the
    // closing summary instead of on an ordinary record.
    internal int BlockAfterWrites = -1;
    internal bool ThrowOnFlush;
    internal bool ThrowOnClose;

    internal IReadOnlyList<string> Lines
    {
        get
        {
            lock (sync) return lines.ToArray();
        }
    }

    public void WriteLine(string line)
    {
        WriteThreadId = Environment.CurrentManagedThreadId;
        var calls = Interlocked.Increment(ref WriteCalls);
        Entered.Set();
        if (BlockFirstWrite) Release.Wait(TimeSpan.FromSeconds(20));
        if (BlockAfterWrites >= 0 && calls > BlockAfterWrites) Release.Wait(TimeSpan.FromSeconds(20));
        if (ThrowOnWriteAfter >= 0 && calls > ThrowOnWriteAfter) throw new IOException("injected write fault");
        lock (sync) lines.Add(line);
    }

    public void Flush()
    {
        Interlocked.Increment(ref FlushCalls);
        if (ThrowOnFlush) throw new IOException("injected flush fault");
    }

    public void Close()
    {
        Interlocked.Increment(ref CloseCalls);
        if (ThrowOnClose) throw new IOException("injected close fault");
    }
}

internal sealed class TempLogDirectory : IDisposable
{
    internal TempLogDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "compositor-logging-" + Guid.NewGuid().ToString("N"));
    }

    internal string Path { get; }

    internal string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        var root = System.IO.Path.GetFullPath(Path);
        var temp = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
        if (!root.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !System.IO.Path.GetFileName(root).StartsWith("compositor-logging-", StringComparison.Ordinal))
            throw new IOException($"Refusing to clean unexpected path {root}.");
        if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, true);
    }
}
