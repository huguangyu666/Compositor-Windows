using System.Text;

namespace Compositor.App;

internal enum LogLevel
{
    Diagnostic,
    Error,
}

// Logging for the editor. App.Log formats one record, parks it in a bounded ring and returns; one
// background thread owns every disk operation, so a slow or broken volume cannot stall a canvas
// frame or the close sequence. Records keep the order their producers logged them in.
internal static class AppLogging
{
    internal interface ILogSink
    {
        void WriteLine(string line);

        void Flush();

        void Close();
    }

    internal sealed record Settings
    {
        internal int Capacity { get; init; } = 1024;

        internal int MaxRecordBytes { get; init; } = 16 * 1024;

        internal long MaxFileBytes { get; init; } = 2 * 1024 * 1024;

        internal int MaxArchives { get; init; } = 3;

        // Sessions this implementation keeps files for, including the running one, so a long-lived
        // log folder cannot fill a disk.
        internal int MaxSessions { get; init; } = 4;

        internal int IdleWaitMilliseconds { get; init; } = 200;

        internal int StartBackoffMilliseconds { get; init; } = 100;

        internal int MaxBackoffMilliseconds { get; init; } = 10_000;

        internal int NoticeCooldownMilliseconds { get; init; } = 10_000;

        internal string? LogDirectoryOverride { get; init; }

        internal static Settings Default { get; } = new();
    }

    internal const string DirectoryOverrideVariable = "COMPOSITOR_LOG_DIRECTORY";

    internal const string FilePrefix = "compositor-app-";

    internal const string FileSuffix = ".log";

    internal const string FolderName = "Logs";

    private const int IdleState = 0, RunningState = 1, StoppingState = 2, StoppedState = 3;

    // Text that marks a record as worth keeping once the ring is saturated. Most failure logs are
    // written at call sites outside this file, so their wording is the only signal available here;
    // slow frames and startup timings stay ordinary diagnostics.
    private static readonly string[] errorMarkers =
        ["failed", "Failed", "Fatal", "Unhandled", "unhandled", "missing resource", "unreadable", "unwritable"];

    private static readonly object gate = new();
    private static readonly UTF8Encoding utf8 = new(false);

    private static Session? current;
    private static int startFaults;

    // Configures and starts the consumer. Nothing here touches the disk: the file sink only holds
    // resolved paths until the worker opens them. `force` retires a consumer that never answered,
    // which production does not need but a test that parked its sink does.
    internal static void Start(Settings? overrides = null, ILogSink? replacementSink = null, bool force = false)
    {
        lock (gate)
        {
            var previous = current;
            if (previous is not null && !force && previous.IsAccepting) return;
            var session = new Session(overrides ?? Settings.Default, replacementSink ?? new FileLogSink(overrides ?? Settings.Default));
            current = session;
            // A superseded worker may be parked in its retry pause; wake it so it can notice and leave
            // instead of holding a thread for up to the maximum backoff.
            previous?.Abandon();
            session.Start();
        }
    }

    internal static LogLevel Classify(string message)
    {
        foreach (var marker in errorMarkers)
            if (message.Contains(marker, StringComparison.Ordinal)) return LogLevel.Error;
        return LogLevel.Diagnostic;
    }

    internal static void Log(string message) => Log(message, Classify(message));

    internal static void Log(string message, LogLevel level)
    {
        try
        {
            var session = Volatile.Read(ref current);
            if (session is null)
            {
                Start();
                session = Volatile.Read(ref current)!;
            }

            session.Log(message, level);
        }
        catch
        {
            // Logging never fails the caller, including when the consumer could not start.
            Interlocked.Increment(ref startFaults);
        }
    }

    // Asks the consumer to write what the ring accepted and finish. Returns within the bound even
    // when the disk never answers, and an abandoned consumer is a background thread, so it cannot
    // hold the process open. Safe to call twice; a cancelled close simply never calls it.
    internal static bool Shutdown(TimeSpan bound)
    {
        var session = Volatile.Read(ref current);
        return session is null || session.Shutdown(bound);
    }

    internal static void RequestShutdown() => Volatile.Read(ref current)?.RequestShutdown();

    internal static Settings ActiveSettings => Volatile.Read(ref current)?.Options ?? Settings.Default;

    internal static bool IsRunning => Volatile.Read(ref current)?.IsAccepting ?? false;

    internal static int CapacitySetting => ActiveSettings.Capacity;

    internal static string LogDirectory => Volatile.Read(ref current) is { Sink: FileLogSink file }
        ? file.Directory
        : ResolveLogDirectory(Settings.Default);

    internal static int EnqueuedCount => Volatile.Read(ref current)?.Enqueued ?? 0;

    internal static int WrittenCount => Volatile.Read(ref current)?.Written ?? 0;

    internal static int DroppedDiagnosticCount => Volatile.Read(ref current)?.DroppedDiagnostic ?? 0;

    internal static int DroppedErrorCount => Volatile.Read(ref current)?.DroppedError ?? 0;

    internal static int DroppedLateCount => Volatile.Read(ref current)?.DroppedLate ?? 0;

    internal static int QueueHighWaterCount => Volatile.Read(ref current)?.HighWater ?? 0;

    internal static int DroppedFaultCount => Volatile.Read(ref current)?.DroppedFault ?? 0;

    internal static int RequeuedCount => Volatile.Read(ref current)?.Requeued ?? 0;

    internal static int NoticeCount => Volatile.Read(ref current)?.Notices ?? 0;

    internal static int ConsumerWriteFaultCount => Volatile.Read(ref current)?.WriteFaults ?? 0;

    internal static int ConsumerFlushFaultCount => Volatile.Read(ref current)?.FlushFaults ?? 0;

    internal static int ConsumerCloseFaultCount => Volatile.Read(ref current)?.CloseFaults ?? 0;

    internal static int FileCreateFaultCount => Volatile.Read(ref current)?.Sink is FileLogSink file ? file.CreateFaultCount : 0;

    internal static int FileRotateFaultCount => Volatile.Read(ref current)?.Sink is FileLogSink file ? file.RotateFaultCount : 0;

    internal static int FilePruneFaultCount => Volatile.Read(ref current)?.Sink is FileLogSink file ? file.PruneFaultCount : 0;

    internal static string StatusLine() => Volatile.Read(ref current)?.Status() ?? $"state=idle startFaults={Volatile.Read(ref startFaults)} directory={LogDirectory}";

    // The log directory and file names resolve without touching the disk; the consumer creates them.
    internal static string ResolveLogDirectory(Settings value)
    {
        var configured = value.LogDirectoryOverride ?? Environment.GetEnvironmentVariable(DirectoryOverrideVariable);
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root)) root = Path.Combine(Path.GetTempPath(), "Compositor");
        return Path.Combine(root, "Compositor", FolderName);
    }

    // One session owns one ring, one sink and one worker, so a consumer that never answered an
    // earlier shutdown cannot take records away from the session that replaced it.
    internal sealed class Session(Settings options, ILogSink sink)
    {
        private readonly object queueGate = new();
        private readonly ManualResetEventSlim door = new(false);
        private readonly ManualResetEventSlim drainRequested = new(false);
        private readonly ManualResetEventSlim stopped = new(false);
        private readonly string?[] queue = new string?[RingSize(options)];
        private readonly bool[] keep = new bool[RingSize(options)];
        private readonly string[] batch = new string[RingSize(options)];
        private readonly bool[] batchKeep = new bool[RingSize(options)];
        private int head, tail, count, state = RunningState;

        private static int RingSize(Settings value) => Math.Max(1, value.Capacity);
        private long noticeDueAt;
        private int enqueued, written, droppedDiagnostic, droppedError, droppedLate, droppedFault, requeued, notices;
        private int writeFaults, flushFaults, closeFaults, highWater;

        internal Settings Options => options;

        internal ILogSink Sink => sink;

        internal bool IsAccepting => Volatile.Read(ref state) is RunningState or StoppingState;

        internal int Enqueued => Volatile.Read(ref enqueued);

        internal int Written => Volatile.Read(ref written);

        internal int DroppedDiagnostic => Volatile.Read(ref droppedDiagnostic);

        internal int DroppedError => Volatile.Read(ref droppedError);

        internal int DroppedLate => Volatile.Read(ref droppedLate);

        internal int DroppedFault => Volatile.Read(ref droppedFault);

        internal int Requeued => Volatile.Read(ref requeued);

        internal int Notices => Volatile.Read(ref notices);

        internal int WriteFaults => Volatile.Read(ref writeFaults);

        internal int FlushFaults => Volatile.Read(ref flushFaults);

        internal int CloseFaults => Volatile.Read(ref closeFaults);

        internal int HighWater => Volatile.Read(ref highWater);

        internal void Start() => new Thread(Run) { IsBackground = true, Name = "Compositor.Logging" }.Start();

        // Wakes a worker that is parked in its retry pause and lets it finish its own records, so a
        // superseded session drains and exits instead of holding a thread and its file handle.
        internal void Abandon() => RequestShutdown();

        internal void Log(string message, LogLevel level)
        {
            var line = Format(message, level);
            lock (queueGate)
            {
                if (state != RunningState)
                {
                    droppedLate++;
                    return;
                }

                EnqueueLocked(line, level == LogLevel.Error);
            }

            ReportDrops();
        }

        internal bool Shutdown(TimeSpan bound)
        {
            RequestShutdown();
            // A session that never started has nothing to drain. Any other session answers "done"
            // only through the completion event: `state` goes to Stopped before the closing write,
            // flush and close have finished, so reading the state here would let the process walk
            // away mid-summary.
            if (Volatile.Read(ref state) == IdleState) return true;
            return stopped.Wait(bound);
        }

        internal void RequestShutdown()
        {
            lock (queueGate)
            {
                if (state != RunningState) return;
                state = StoppingState;
            }

            door.Set();
            drainRequested.Set();
        }

        internal string Status()
        {
            var name = Volatile.Read(ref state) switch
            {
                RunningState => "running",
                StoppingState => "stopping",
                StoppedState => "stopped",
                _ => "idle",
            };
            return $"state={name} enqueued={Volatile.Read(ref enqueued)} written={Volatile.Read(ref written)}"
                + $" queue={Volatile.Read(ref count)} highWater={Volatile.Read(ref highWater)}"
                + $" droppedDiagnostic={Volatile.Read(ref droppedDiagnostic)} droppedError={Volatile.Read(ref droppedError)}"
                + $" droppedLate={Volatile.Read(ref droppedLate)} droppedFault={Volatile.Read(ref droppedFault)}"
                + $" requeued={Volatile.Read(ref requeued)} notices={Volatile.Read(ref notices)}"
                + $" consumerWriteFaults={Volatile.Read(ref writeFaults)} consumerFlushFaults={Volatile.Read(ref flushFaults)}"
                + $" consumerCloseFaults={Volatile.Read(ref closeFaults)}"
                + (sink is FileLogSink file ? file.StatusLine() : $" directory={LogDirectory}");
        }

        private string Format(string message, LogLevel level)
        {
            var header = $"[{DateTime.Now:HH:mm:ss.fff}]";
            if (level == LogLevel.Error) header += " ERROR";
            // One byte for the space after the header and one for the line feed the sink appends.
            var budget = options.MaxRecordBytes - utf8.GetByteCount(header) - 2;
            if (budget < 0) budget = 0;
            if (message.Length > budget || ExactUtf8Length(message, budget) > budget)
            {
                const string marker = "\u2026[truncated]";
                budget = Math.Max(0, budget - utf8.GetByteCount(marker));
                return $"{header} {TruncateToUtf8(message, budget)}{marker}";
            }

            return $"{header} {message}";
        }

        // Stops once the total passes `stopAfter`, so a caller with a huge buffer never charges its
        // whole message to the calling thread.
        private static int ExactUtf8Length(string value, int stopAfter)
        {
            var total = 0;
            for (var index = 0; index < value.Length; index++)
            {
                var size = Utf8Size(value, index);
                total += size;
                if (size == 4) index++;
                if (total > stopAfter) break;
            }

            return total;
        }

        private static string TruncateToUtf8(string value, int maxBytes)
        {
            if (maxBytes <= 0) return string.Empty;
            var total = 0;
            var index = 0;
            while (index < value.Length)
            {
                var size = Utf8Size(value, index);
                if (total + size > maxBytes) break;
                total += size;
                index += size == 4 ? 2 : 1;
            }

            return value[..index];
        }

        private static int Utf8Size(string value, int index)
        {
            var current = value[index];
            // An unpaired surrogate cannot be encoded, and the encoder answers it with U+FFFD: three
            // bytes. Counting one was what let a malformed string blow through the record ceiling.
            if (char.IsHighSurrogate(current))
                return index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]) ? 4 : 3;
            if (char.IsLowSurrogate(current)) return 3;
            return current switch { < '\u0080' => 1, < '\u0800' => 2, _ => 3 };
        }

        // Runs under `queueGate`. An incoming error evicts the oldest diagnostic; only an all-error
        // ring loses an error. Nothing here waits or touches the disk.
        private void EnqueueLocked(string line, bool isError)
        {
            if (count >= queue.Length)
            {
                var slot = -1;
                if (isError)
                {
                    for (var offset = 0; offset < count; offset++)
                    {
                        var candidate = (head + offset) % queue.Length;
                        if (!keep[candidate])
                        {
                            slot = candidate;
                            break;
                        }
                    }
                }

                if (slot < 0)
                {
                    if (isError) droppedError++;
                    else droppedDiagnostic++;
                    return;
                }

                for (var shift = slot; shift != head;)
                {
                    var previous = (shift - 1 + queue.Length) % queue.Length;
                    queue[shift] = queue[previous];
                    keep[shift] = keep[previous];
                    shift = previous;
                }

                queue[head] = null;
                head = (head + 1) % queue.Length;
                count--;
                droppedDiagnostic++;
            }

            queue[tail] = line;
            keep[tail] = isError;
            tail = (tail + 1) % queue.Length;
            count++;
            enqueued++;
            if (count > highWater) highWater = count;
            door.Set();
        }

        // Drops are visible in the file as well as in the counters, so a script that greps the log
        // for unexpected exceptions cannot read a saturated run as a clean one.
        private void ReportDrops()
        {
            var now = Environment.TickCount64;
            if (now < Volatile.Read(ref noticeDueAt)) return;
            var pending = Volatile.Read(ref droppedDiagnostic) + Volatile.Read(ref droppedError) + Volatile.Read(ref droppedLate);
            if (pending == 0) return;
            Interlocked.Exchange(ref noticeDueAt, now + options.NoticeCooldownMilliseconds);
            lock (queueGate)
            {
                if (state != RunningState) return;
                notices++;
                EnqueueLocked(Format($"dropped {pending} records since the last notice (queue full)", LogLevel.Error), true);
            }
        }

        private void Run()
        {
            var backoff = options.StartBackoffMilliseconds;
            var retryDueAt = 0L;
            while (true)
            {
                // A failed batch gets a real pause: the door is set again as soon as a producer or a
                // retry parks a record, so waiting on it alone would turn a dead volume into a spin.
                // A shutdown skips the pause, since the drain must finish inside the caller's bound.
                var remaining = Volatile.Read(ref state) == RunningState ? retryDueAt - Environment.TickCount64 : 0;
                if (remaining > 0)
                {
                    drainRequested.Wait((int)remaining);
                    continue;
                }

                var stopping = Volatile.Read(ref state) != RunningState;
                var taken = Take();
                if (taken > 0 && !Commit(taken))
                {
                    backoff = Math.Min(backoff * 2, options.MaxBackoffMilliseconds);
                    retryDueAt = Environment.TickCount64 + backoff;
                }
                else backoff = options.StartBackoffMilliseconds;

                if (stopping && taken == 0)
                {
                    Finish();
                    return;
                }

                // The door stays set while records remain, so a busy producer costs no wait here.
                door.Wait(taken > 0 && backoff == options.StartBackoffMilliseconds ? options.IdleWaitMilliseconds : backoff);
            }
        }

        private int Take()
        {
            lock (queueGate)
            {
                var taken = Math.Min(count, batch.Length);
                for (var index = 0; index < taken; index++)
                {
                    var slot = (head + index) % queue.Length;
                    batch[index] = queue[slot]!;
                    batchKeep[index] = keep[slot];
                    queue[slot] = null;
                }

                head = (head + taken) % queue.Length;
                count -= taken;
                if (count == 0) door.Reset();
                return taken;
            }
        }

        // True when the batch reached the sink. A record whose write threw is put back where it was
        // taken from: nothing was committed for it, so the retry cannot duplicate a line. The sink
        // closes and the caller backs off, which is what keeps a dead volume from spinning this thread.
        private bool Commit(int taken)
        {
            var index = 0;
            try
            {
                for (; index < taken; index++) sink.WriteLine(batch[index]);
                sink.Flush();
                Interlocked.Add(ref written, taken);
                return true;
            }
            catch
            {
                if (index < taken)
                {
                    Interlocked.Increment(ref writeFaults);
                    Interlocked.Add(ref requeued, taken - index);
                    Interlocked.Add(ref droppedFault, taken - index - RequeueHead(index, taken));
                }
                else
                {
                    Interlocked.Increment(ref flushFaults);
                    Interlocked.Add(ref droppedFault, taken);
                }

                try { sink.Close(); }
                catch { Interlocked.Increment(ref closeFaults); }

                return false;
            }
        }

        // Returns how many retried records could not be parked again because producers had filled the
        // ring in the meantime; those count as fault drops rather than sitting somewhere unbounded.
        private int RequeueHead(int from, int to)
        {
            var parked = 0;
            lock (queueGate)
            {
                for (var index = to - 1; index >= from; index--)
                {
                    if (state != RunningState || count >= queue.Length) break;
                    head = (head - 1 + queue.Length) % queue.Length;
                    queue[head] = batch[index];
                    keep[head] = batchKeep[index];
                    count++;
                    parked++;
                    door.Set();
                }
            }

            return parked;
        }

        private void Finish()
        {
            lock (queueGate)
            {
                if (state == StoppedState) return;
                state = StoppedState;
            }

            // Closing is the part that owns the file, so it always runs: an earlier summary or flush
            // fault must not skip the Close and leave the sink holding the log while the session
            // announces itself finished. Each stage counts its own fault, because the summary line
            // reports which one the user has to believe.
            try
            {
                try { sink.WriteLine($"logging summary {Status()}"); }
                catch { Interlocked.Increment(ref writeFaults); }

                try { sink.Flush(); }
                catch { Interlocked.Increment(ref flushFaults); }
            }
            finally
            {
                try { sink.Close(); }
                catch { Interlocked.Increment(ref closeFaults); }
                stopped.Set();
            }
        }
    }

    // One file per process: two editors can share a log directory without truncating or rotating each
    // other's output, and no cross-process lock enters the logging path.
    internal class FileLogSink : ILogSink
    {
        private static readonly byte[] lineFeed = [(byte)'\n'];

        private readonly Settings settings;
        private FileStream? stream;
        private long bytes;
        private bool pruned;
        private int createFaults, writeFaults, flushFaults, rotateFaults, pruneFaults;

        internal FileLogSink(Settings settings)
        {
            this.settings = settings;
            Directory = ResolveLogDirectory(settings);
            BaseName = $"{FilePrefix}{Environment.ProcessId}{FileSuffix}";
        }

        internal string Directory { get; }

        internal string BaseName { get; }

        internal string CurrentFile => Path.Combine(Directory, BaseName);

        internal string ArchiveFile(int index) => Path.Combine(Directory, $"{BaseName}.{index}");

        public void WriteLine(string line)
        {
            var payload = utf8.GetBytes(line);
            Open();
            // Rotate before writing so every file stays under the byte ceiling. The current file
            // counts toward the retained set: it becomes archive 1 once it is full.
            if (bytes > 0 && bytes + payload.LongLength + 1 > settings.MaxFileBytes)
            {
                Rotate();
                Open();
            }

            if (stream is null) throw new IOException($"The log file {CurrentFile} is not open.");
            try
            {
                stream.Write(payload, 0, payload.Length);
                stream.Write(lineFeed);
            }
            catch
            {
                Interlocked.Increment(ref writeFaults);
                throw;
            }

            bytes += payload.LongLength + 1;
        }

        public void Flush()
        {
            if (stream is null) return;
            try { stream.Flush(); }
            catch
            {
                Interlocked.Increment(ref flushFaults);
                throw;
            }
        }

        public void Close()
        {
            if (stream is null) return;
            var opening = stream;
            stream = null;
            opening.Dispose();
        }

        internal int CreateFaultCount => Volatile.Read(ref createFaults);

        internal int RotateFaultCount => Volatile.Read(ref rotateFaults);

        internal int PruneFaultCount => Volatile.Read(ref pruneFaults);

        internal string StatusLine() => $" directory={Directory} current={BaseName} createFaults={CreateFaultCount}"
            + $" fileWriteFaults={Volatile.Read(ref writeFaults)} fileFlushFaults={Volatile.Read(ref flushFaults)}"
            + $" rotateFaults={RotateFaultCount} pruneFaults={PruneFaultCount}";

        private void Open()
        {
            if (stream is not null) return;
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                stream = new FileStream(CurrentFile, FileMode.Append, FileAccess.Write, FileShare.Read);
                bytes = stream.Length;
            }
            catch
            {
                Interlocked.Increment(ref createFaults);
                throw;
            }

            if (pruned) return;
            pruned = true;
            try { PruneOldSessions(); }
            catch { Interlocked.Increment(ref pruneFaults); }
        }

        private void Rotate()
        {
            try
            {
                Flush();
                Close();
                TryDelete(ArchiveFile(settings.MaxArchives));
                for (var index = settings.MaxArchives - 1; index >= 1; index--) Move(ArchiveFile(index), ArchiveFile(index + 1));
                Move(CurrentFile, ArchiveFile(1));
                bytes = 0;
            }
            catch
            {
                Interlocked.Increment(ref rotateFaults);
                throw;
            }
        }

        private static void Move(string source, string destination)
        {
            if (File.Exists(source)) File.Move(source, destination, true);
        }

        private static void TryDelete(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }

        // A file this process still holds is left alone even when its session looks superseded, and
        // only names this implementation produces are candidates.
        private void PruneOldSessions()
        {
            if (!System.IO.Directory.Exists(Directory)) return;
            const int activeGraceSeconds = 120;
            var sessions = new Dictionary<string, DateTime>(StringComparer.Ordinal);
            foreach (var path in System.IO.Directory.GetFiles(Directory))
            {
                if (!TrySession(Path.GetFileName(path), out var baseName)) continue;
                if (string.Equals(baseName, BaseName, StringComparison.Ordinal)) continue;
                var writtenAt = File.GetLastWriteTimeUtc(path);
                if (!sessions.TryGetValue(baseName, out var latest) || writtenAt > latest) sessions[baseName] = writtenAt;
            }

            foreach (var stale in sessions.OrderByDescending(pair => pair.Value).Skip(Math.Max(0, settings.MaxSessions - 1)))
            {
                if (stale.Value > DateTime.UtcNow.AddSeconds(-activeGraceSeconds)) continue;
                // Time since the last write does not prove the process is gone: an idle editor keeps
                // its own log open, and Windows then refuses only the current file while the wildcard
                // delete happily removed that same session's archives. Ask the current file first and
                // leave a held session's files — all of them — alone.
                if (SessionIsHeld(stale.Key)) continue;
                foreach (var path in System.IO.Directory.GetFiles(Directory))
                {
                    // Only files this logger's own naming rule accepts for that exact session. A user
                    // who saved "compositor-app-44444.log.backup-important.txt" beside the logs owns
                    // that file, and a prefix match would have deleted it.
                    if (!TrySession(Path.GetFileName(path), out var owner) || !string.Equals(owner, stale.Key, StringComparison.Ordinal)) continue;
                    try { File.Delete(path); }
                    catch { Interlocked.Increment(ref pruneFaults); }
                }
            }
        }

        // True when some process still has the session's current log open for writing. The sink opens
        // with FileShare.Read, so asking for write access is what reveals the holder; `sessionName`
        // is what TrySession reports, which already carries the ".log" ending.
        private bool SessionIsHeld(string sessionName)
        {
            try
            {
                using var probe = new FileStream(Path.Combine(Directory, sessionName), FileMode.Open, FileAccess.Write, FileShare.Read);
                return false;
            }
            catch (FileNotFoundException) { return false; }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }

        internal static bool TrySession(string fileName, out string baseName)
        {
            baseName = string.Empty;
            if (!fileName.StartsWith(FilePrefix, StringComparison.Ordinal)) return false;
            var name = fileName;
            var dot = name.LastIndexOf('.');
            if (dot > 0 && IsAllDigits(name, dot + 1, name.Length)) name = name[..dot];
            if (!name.EndsWith(FileSuffix, StringComparison.Ordinal)) return false;
            var start = FilePrefix.Length;
            var length = name.Length - start - FileSuffix.Length;
            if (length <= 0 || !IsAllDigits(name, start, start + length)) return false;
            baseName = name;
            return true;
        }

        private static bool IsAllDigits(string value, int start, int end)
        {
            for (var index = start; index < end; index++)
                if (!char.IsAsciiDigit(value[index])) return false;
            return start < end;
        }
    }
}
