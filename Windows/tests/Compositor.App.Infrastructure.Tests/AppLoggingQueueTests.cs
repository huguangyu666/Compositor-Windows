using System.Diagnostics;
using System.Text;
using Compositor.App;
using Xunit;
using static Compositor.App.Tests.LoggingTestSupport;

namespace Compositor.App.Tests;

[Collection(AppLoggingCollection.Name)]
public sealed class AppLoggingQueueTests
{
    [Fact]
    public void StuckSinkLeavesTheCallingThreadFreeAndTheWorkerParked()
    {
        var sink = new RecordingSink { BlockFirstWrite = true };
        Restart(new AppLogging.Settings(), sink);
        var producerId = Thread.CurrentThread.ManagedThreadId;
        var producer = new Thread(() =>
        {
            AppLogging.Log("alpha", LogLevel.Diagnostic);
            AppLogging.Log("beta", LogLevel.Diagnostic);
        })
        {
            IsBackground = true,
        };

        // Had the producer written, it would still be parked in the sink and Join would time out.
        producer.Start();
        Assert.True(producer.Join(TimeSpan.FromSeconds(10)), "App.Log blocked the calling thread on the sink.");
        WaitFor(() => sink.Entered.IsSet, because: "the worker never reached the sink");
        Assert.NotEqual(producerId, sink.WriteThreadId);
        Assert.Empty(sink.Lines);
        Assert.Equal(0, AppLogging.WrittenCount);

        sink.Release.Set();
        WaitFor(() => AppLogging.WrittenCount >= 2, because: "the released records never arrived");
        var records = Records(sink);
        Assert.Equal(2, records.Count);
        Assert.EndsWith("alpha", records[0], StringComparison.Ordinal);
        Assert.EndsWith("beta", records[1], StringComparison.Ordinal);
        AppLogging.Shutdown(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void SingleProducerOrderSurvivesTheQueue()
    {
        var sink = new RecordingSink();
        Restart(new AppLogging.Settings { Capacity = 1024 }, sink);
        for (var index = 0; index < 500; index++) AppLogging.Log($"record {index}", LogLevel.Diagnostic);
        Assert.True(AppLogging.Shutdown(TimeSpan.FromSeconds(10)));

        Assert.Equal(Enumerable.Range(0, 500), Records(sink).Select(IndexAfter).ToArray());
    }

    [Fact]
    public void ManyProducersDeliverEveryRecordWhole()
    {
        var sink = new RecordingSink();
        Restart(new AppLogging.Settings { Capacity = 2048 }, sink);
        var producers = Enumerable.Range(0, 8).Select(worker => new Thread(() =>
        {
            for (var index = 0; index < 200; index++) AppLogging.Log($"p{worker}-{index}", LogLevel.Diagnostic);
        })
        {
            IsBackground = true,
        }).ToArray();
        foreach (var producer in producers) producer.Start();
        foreach (var producer in producers) Assert.True(producer.Join(TimeSpan.FromSeconds(10)));
        Assert.True(AppLogging.Shutdown(TimeSpan.FromSeconds(10)));

        var records = Records(sink);
        Assert.Equal(1600, records.Count);
        Assert.All(records, line => Assert.Matches(@"^\[\d\d:\d\d:\d\d\.\d{3}\] p\d-\d+$", line));
        Assert.Equal(1600, records.Select(line => line[(line.IndexOf(']') + 2)..]).Distinct().Count());
        Assert.Equal(0, AppLogging.DroppedDiagnosticCount);
    }

    [Fact]
    public void SaturatedQueueDropsDiagnosticsButKeepsEveryError()
    {
        var sink = new RecordingSink { BlockFirstWrite = true };
        Restart(new AppLogging.Settings { Capacity = 16, NoticeCooldownMilliseconds = 600_000 }, sink);
        for (var index = 0; index < 400; index++)
            AppLogging.Log(index % 40 == 0 ? $"render failed {index}" : $"slow frame band=8x8 render+upload={index}.0 ms",
                index % 40 == 0 ? LogLevel.Error : LogLevel.Diagnostic);
        WaitFor(() => sink.Entered.IsSet, because: "the worker never parked in the sink");

        // The backlog is capped and the calls returned anyway, so 400 records became far fewer writes.
        Assert.Equal(16, AppLogging.QueueHighWaterCount);
        Assert.Equal(0, AppLogging.DroppedErrorCount);
        Assert.True(AppLogging.DroppedDiagnosticCount > 350, $"only {AppLogging.DroppedDiagnosticCount} diagnostics were dropped");
        sink.Release.Set();
        WaitFor(() => Records(sink).Count(line => line.Contains("render failed")) == 10);
        Assert.Equal(10, Records(sink).Count(line => line.Contains("render failed")));
        AppLogging.Shutdown(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void AllErrorBacklogReportsTheErrorsItCannotKeep()
    {
        var sink = new RecordingSink { BlockFirstWrite = true };
        Restart(new AppLogging.Settings { Capacity = 4, NoticeCooldownMilliseconds = 600_000 }, sink);
        for (var index = 0; index < 40; index++) AppLogging.Log($"worker failed {index}", LogLevel.Error);
        WaitFor(() => sink.Entered.IsSet);

        // Nothing can keep 40 errors in 4 slots, and the point is that the loss is counted, not silent.
        Assert.True(AppLogging.DroppedErrorCount >= 30, $"an all-error backlog claimed it kept {AppLogging.EnqueuedCount} records");
        Assert.Equal(0, AppLogging.DroppedDiagnosticCount);
        sink.Release.Set();
        AppLogging.Shutdown(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void OversizedRecordTruncatesWithinItsByteCeiling()
    {
        var sink = new RecordingSink();
        Restart(new AppLogging.Settings { MaxRecordBytes = 64 }, sink);
        AppLogging.Log(new string('あ', 200) + string.Concat(Enumerable.Repeat("tail", 200)), LogLevel.Diagnostic);
        Assert.True(AppLogging.Shutdown(TimeSpan.FromSeconds(10)));

        var line = Assert.Single(Records(sink));
        Assert.EndsWith("\u2026[truncated]", line, StringComparison.Ordinal);
        Assert.True(Utf8.GetByteCount(line) <= 64, $"{Utf8.GetByteCount(line)} bytes exceeds the ceiling");
    }

    [Fact]
    public void TruncationNeverSplitsAnEmoji()
    {
        var sink = new RecordingSink();
        Restart(new AppLogging.Settings { MaxRecordBytes = 48 }, sink);
        AppLogging.Log(string.Concat(Enumerable.Repeat("🎨", 60)), LogLevel.Diagnostic);
        Assert.True(AppLogging.Shutdown(TimeSpan.FromSeconds(10)));

        // A strict decoder rejects a cut surrogate pair, and the editor's own '?' would hide one.
        var decoded = StrictUtf8.GetString(Utf8.GetBytes(Assert.Single(Records(sink))));
        Assert.DoesNotContain('?', decoded);
        Assert.Matches(@"^\[\d\d:\d\d:\d\d\.\d{3}\] (🎨)+\u2026\[truncated\]$", decoded);
    }

    [Fact]
    public void HugeMessageFormattingStaysBoundedOnTheCaller()
    {
        var sink = new RecordingSink();
        Restart(new AppLogging.Settings { MaxRecordBytes = 1024 }, sink);
        var message = string.Concat(Enumerable.Repeat('x', 4_000_000));
        var watch = Stopwatch.StartNew();
        AppLogging.Log(message, LogLevel.Diagnostic);
        watch.Stop();
        Assert.True(AppLogging.Shutdown(TimeSpan.FromSeconds(10)));

        var line = Assert.Single(Records(sink));
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(200), $"formatting a 4 MB message took {watch.ElapsedMilliseconds} ms");
        Assert.True(Utf8.GetByteCount(line) <= 1024, $"{Utf8.GetByteCount(line)} bytes exceeds the ceiling");
    }

    [Theory]
    [InlineData("slow frame band=3840x2160 render+upload=90.0 ms interactive=True", "Diagnostic")]
    [InlineData("startup: first draw 903 ms", "Diagnostic")]
    [InlineData("Frame publication failed: System.InvalidOperationException", "Error")]
    [InlineData("missing resource: S_DitherFailed (cannot find X)", "Error")]
    [InlineData("recent list unreadable: access denied", "Error")]
    [InlineData("recent list unwritable: access denied", "Error")]
    [InlineData("Unhandled: boom", "Error")]
    [InlineData("Fatal: System.Exception: crash", "Error")]
    [InlineData("Launch failed: System.IO.IOException", "Error")]
    [InlineData("Dither worker failed: x", "Error")]
    [InlineData("Rulers preference failed: x", "Error")]
    [InlineData("Transform continuation failed: x", "Error")]
    [InlineData("system clipboard write failed: x", "Error")]
    // LogLevel is internal to the app, so the theory carries the level by name.
    public void ClassificationMatchesTheEditorsRealMessageShapes(string message, string expectedLevel)
        => Assert.Equal(Enum.Parse<LogLevel>(expectedLevel), AppLogging.Classify(message));

    [Fact]
    public void ShutdownIsIdempotentAndLateCallsAreCounted()
    {
        var sink = new RecordingSink();
        Restart(new AppLogging.Settings(), sink);
        AppLogging.Log("before stop", LogLevel.Diagnostic);
        Assert.True(AppLogging.Shutdown(TimeSpan.FromSeconds(10)));
        Assert.True(AppLogging.Shutdown(TimeSpan.FromSeconds(10)));
        Assert.False(AppLogging.IsRunning);

        AppLogging.Log("after stop", LogLevel.Diagnostic);
        Assert.Equal(1, AppLogging.DroppedLateCount);
        Assert.Equal(0, AppLogging.ConsumerWriteFaultCount);
        Assert.Contains("before stop", Records(sink).Single());
    }

    [Fact]
    public void StopWithAStuckSinkReturnsInsideItsBound()
    {
        var sink = new RecordingSink { BlockFirstWrite = true };
        Restart(new AppLogging.Settings(), sink);
        AppLogging.Log("parked record", LogLevel.Diagnostic);
        WaitFor(() => sink.Entered.IsSet);
        var watch = Stopwatch.StartNew();
        var drained = AppLogging.Shutdown(TimeSpan.FromMilliseconds(300));
        watch.Stop();

        Assert.False(drained);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"the stop waited {watch.ElapsedMilliseconds} ms");
        sink.Release.Set();
    }

    [Fact]
    public void WriteFaultsAreCountedAndRetriedWithABackoff()
    {
        var sink = new RecordingSink { ThrowOnWriteAfter = 0 };
        Restart(new AppLogging.Settings { Capacity = 64, StartBackoffMilliseconds = 100 }, sink);
        AppLogging.Log("one", LogLevel.Diagnostic);
        AppLogging.Log("two", LogLevel.Diagnostic);
        AppLogging.Log("three", LogLevel.Diagnostic);
        Thread.Sleep(500);
        var calls = sink.WriteCalls;
        AppLogging.Shutdown(TimeSpan.FromSeconds(10));

        Assert.True(AppLogging.ConsumerWriteFaultCount > 0, "a sink that throws on every write went unreported");
        Assert.True(calls < 12, $"the consumer retried {calls} times inside 500 ms: it is spinning");
        Assert.True(AppLogging.RequeuedCount >= 2, $"requeued={AppLogging.RequeuedCount}");
        Assert.Equal(0, AppLogging.WrittenCount);
        Assert.Equal(3, AppLogging.EnqueuedCount - AppLogging.NoticeCount);
    }

    [Fact]
    public void FlushAndCloseFaultsAreCounted()
    {
        var sink = new RecordingSink { ThrowOnFlush = true, ThrowOnClose = true };
        Restart(new AppLogging.Settings { Capacity = 64 }, sink);
        AppLogging.Log("line one", LogLevel.Diagnostic);
        AppLogging.Log("line two", LogLevel.Diagnostic);
        WaitFor(() => AppLogging.ConsumerFlushFaultCount > 0, because: "a failing flush went unreported");

        Assert.True(AppLogging.DroppedFaultCount > 0, "records lost to a failing flush were not counted");
        Assert.Equal(0, AppLogging.WrittenCount);
        WaitFor(() => AppLogging.ConsumerCloseFaultCount > 0, because: "a failing close went unreported");
        AppLogging.Shutdown(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void UnwritableDirectoryKeepsCallersRunningAndReportsTheFault()
    {
        var blocker = Path.Combine(Path.GetTempPath(), $"compositor-logging-blocker-{Guid.NewGuid():N}");
        File.WriteAllText(blocker, "a file where a directory should be");
        try
        {
            Restart(new AppLogging.Settings
            {
                LogDirectoryOverride = Path.Combine(blocker, "logs"),
                Capacity = 16,
                StartBackoffMilliseconds = 50,
                NoticeCooldownMilliseconds = 600_000,
            });
            for (var index = 0; index < 60; index++) AppLogging.Log($"canvas job failed {index}", LogLevel.Diagnostic);
            WaitFor(() => AppLogging.FileCreateFaultCount > 0, because: "the log directory was never reported as unwritable");
            WaitFor(() => AppLogging.DroppedDiagnosticCount > 0, because: "the ring never saturated");

            // The consumer moves a batch out of the ring before its write fails, so the accepted count
            // can reach two ring-fulls; it never exceeds them and never grows per retry.
            Assert.True(AppLogging.QueueHighWaterCount <= 16, $"the ring held {AppLogging.QueueHighWaterCount} of 16 slots");
            Assert.True(AppLogging.EnqueuedCount <= 34, $"{AppLogging.EnqueuedCount} records were accepted for 16 slots");
            Assert.Equal(0, AppLogging.WrittenCount);
            Assert.Matches(@"createFaults=[1-9]\d*", AppLogging.StatusLine());
            AppLogging.Shutdown(TimeSpan.FromSeconds(10));
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Fact]
    public void RestartAfterAStopWritesAgain()
    {
        // The product's exit drain budget is 2 seconds and best effort; this test is not measuring
        // that budget, it is measuring that a stopped pipeline can start again and write. Under a
        // loaded machine the consumer thread may not be scheduled inside two seconds, so the wait
        // here is deliberately generous and the assertions are the sink contents.
        var first = new RecordingSink();
        Restart(new AppLogging.Settings(), first);
        AppLogging.Log("session one", LogLevel.Diagnostic);
        Assert.True(AppLogging.Shutdown(TimeSpan.FromSeconds(10)));

        var second = new RecordingSink();
        Restart(new AppLogging.Settings(), second);
        AppLogging.Log("session two", LogLevel.Diagnostic);
        Assert.True(AppLogging.Shutdown(TimeSpan.FromSeconds(10)));

        Assert.Contains("session one", Records(first).Single());
        Assert.Contains("session two", Records(second).Single());
    }

    [Fact]
    public void ShutdownWaitsForTheClosingSummaryAndNeverSkipsTheClose()
    {
        // "Drained" has to mean the sink is done, not that the queue is empty: the state goes to
        // stopped before the summary is written, so a shutdown that trusted the state would let the
        // process leave while the last write was still pending.
        var blocked = new RecordingSink { BlockAfterWrites = 1 };
        Restart(new AppLogging.Settings(), blocked);
        AppLogging.Log("before the summary", LogLevel.Diagnostic);
        WaitFor(() => blocked.WriteCalls >= 1);

        Assert.False(AppLogging.Shutdown(TimeSpan.FromMilliseconds(50)));
        Assert.Equal(0, blocked.CloseCalls);

        blocked.Release.Set();
        WaitFor(() => blocked.CloseCalls >= 1, because: "the consumer never finished closing the sink");
        Assert.True(AppLogging.Shutdown(TimeSpan.FromSeconds(10)));
        Assert.Contains("before the summary", Records(blocked).Single());
    }

    [Theory]
    [InlineData(0)]   // the closing summary write faults
    [InlineData(1)]   // the final flush faults
    [InlineData(2)]   // the close itself faults
    public void EachClosingStageReportsItsOwnFaultAndStillClosesTheSink(int stage)
    {
        // Whichever stage fails, the close is still attempted and the session announces itself
        // finished afterwards; each stage counts its own fault so the summary line stays truthful.
        var sink = new RecordingSink
        {
            ThrowOnWriteAfter = stage == 0 ? 1 : -1,
            ThrowOnFlush = stage == 1,
            ThrowOnClose = stage == 2,
        };
        Restart(new AppLogging.Settings(), sink);
        AppLogging.Log($"record before stage {stage}", LogLevel.Diagnostic);
        WaitFor(() => sink.WriteCalls >= 1);
        Assert.True(AppLogging.Shutdown(TimeSpan.FromSeconds(10)));

        // Whichever stage fails, only that stage's counter moves. The ordinary record's own flush
        // shares the injected fault, so the failing stage is asserted as "at least one".
        Assert.True(sink.CloseCalls >= 1, $"stage {stage} skipped the close after the fault");
        Assert.Equal(stage == 0 ? 1 : 0, AppLogging.ConsumerWriteFaultCount);
        Assert.True(stage == 1 ? AppLogging.ConsumerFlushFaultCount >= 1 : AppLogging.ConsumerFlushFaultCount == 0,
            $"flush faults were attributed wrongly for stage {stage}: {AppLogging.ConsumerFlushFaultCount}");
        Assert.Equal(stage == 2 ? 1 : 0, AppLogging.ConsumerCloseFaultCount);
        Assert.Contains($"record before stage {stage}", Records(sink).Single());
    }

    [Theory]
    // The label is not decoration: two bare surrogate literals serialize to the same replacement
    // characters, so xUnit gave those two cases the same ID and quietly dropped the second one.
    [InlineData("lone high", "\ud800")]
    [InlineData("lone low", "\udfff")]
    [InlineData("mixed", "中文\ud800abc\udfff尾")]
    public void UnpairedSurrogatesStayInsideTheRecordCeiling(string label, string malformed)
    {
        // The budget counts bytes and the encoder answers an unpaired surrogate with U+FFFD, three of
        // them. Estimating one byte let a malformed string blow through the ceiling.
        var message = string.Concat(Enumerable.Repeat(malformed, 600));
        var sink = new RecordingSink();
        Restart(new AppLogging.Settings { MaxRecordBytes = 64 }, sink);
        AppLogging.Log(message, LogLevel.Diagnostic);
        Assert.True(AppLogging.Shutdown(TimeSpan.FromSeconds(10)));

        var line = Assert.Single(Records(sink));
        Assert.True(Utf8.GetByteCount(line) <= 64, $"{label}: {Utf8.GetByteCount(line)} bytes exceeds the 64-byte ceiling");
        Assert.EndsWith("\u2026[truncated]", line, StringComparison.Ordinal);
    }

    private static int IndexAfter(string line) => int.Parse(line[(line.IndexOf("record ", StringComparison.Ordinal) + 7)..]);
}
