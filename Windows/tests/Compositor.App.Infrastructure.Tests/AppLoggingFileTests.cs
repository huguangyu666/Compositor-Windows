using System.Text;
using Compositor.App;
using Xunit;
using static Compositor.App.Tests.LoggingTestSupport;

namespace Compositor.App.Tests;

[Collection(AppLoggingCollection.Name)]
public sealed class AppLoggingFileTests : IDisposable
{
    private readonly TempLogDirectory root = new();

    [Fact]
    public void NormalExitDrainsEveryAcceptedRecordIntoTheFile()
    {
        Restart(new AppLogging.Settings { LogDirectoryOverride = root.Path });
        for (var index = 0; index < 200; index++) AppLogging.Log($"drain record {index:000}", LogLevel.Diagnostic);
        Assert.True(AppLogging.Shutdown(TimeSpan.FromSeconds(10)));

        var lines = File.ReadAllLines(CurrentLogFileName(root.Path), Encoding.UTF8);
        Assert.Equal(201, lines.Length);
        Assert.Equal(Enumerable.Range(0, 200), lines[..200].Select(RecordIndex).ToArray());
        Assert.StartsWith("logging summary", lines[200], StringComparison.Ordinal);
        Assert.Contains("written=200", lines[200], StringComparison.Ordinal);
        Assert.Contains("droppedDiagnostic=0", lines[200], StringComparison.Ordinal);
    }

    [Fact]
    public void ToolsCanReadTheLogFileWhileTheEditorIsStillWriting()
    {
        Restart(new AppLogging.Settings { LogDirectoryOverride = root.Path });
        AppLogging.Log("first visible record", LogLevel.Diagnostic);
        WaitFor(() => File.Exists(CurrentLogFileName(root.Path)), because: "the consumer never opened the log file");

        // The consumer holds the file for writing, so a reader must coexist with it.
        var seen = ReadWhileOpen();
        AppLogging.Log("second visible record", LogLevel.Diagnostic);
        WaitFor(() => ReadWhileOpen().Contains("second visible record", StringComparison.Ordinal), because: "the running log stopped growing for readers");
        Assert.Contains("first visible record", seen, StringComparison.Ordinal);
        AppLogging.Shutdown(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void RotationKeepsFileCountAndByteCeilingsAndLeavesOtherFilesAlone()
    {
        EnsureRoot();
        File.WriteAllText(Path.Combine(root.Path, "notes.txt"), "not this implementation's file");
        File.WriteAllText(Path.Combine(root.Path, "compositor-app-plain.log"), "no process id, so not ours either");
        var sink = new AppLogging.FileLogSink(new AppLogging.Settings { LogDirectoryOverride = root.Path, MaxFileBytes = 512, MaxArchives = 3, MaxSessions = 8 });
        for (var index = 0; index < 200; index++) sink.WriteLine($"[00:00:00.000] rotation record {index:000}");
        sink.Flush();
        sink.Close();

        var ours = OwnFiles();
        Assert.Equal(4, ours.Count);
        Assert.All(ours, file => Assert.True(file.Length <= 512, $"{file.Name} holds {file.Length} bytes"));
        Assert.Contains("rotation record 199", File.ReadAllText(sink.CurrentFile, Encoding.UTF8), StringComparison.Ordinal);
        Assert.DoesNotContain("rotation record 000", Joined(ours), StringComparison.Ordinal);
        Assert.Equal("not this implementation's file", File.ReadAllText(Path.Combine(root.Path, "notes.txt"), Encoding.UTF8));
        Assert.Equal("no process id, so not ours either", File.ReadAllText(Path.Combine(root.Path, "compositor-app-plain.log"), Encoding.UTF8));
    }

    [Fact]
    public void RotateFaultIsReportedAndTheWrittenBytesStayIntact()
    {
        EnsureRoot();
        var sink = new AppLogging.FileLogSink(new AppLogging.Settings { LogDirectoryOverride = root.Path, MaxFileBytes = 40, MaxArchives = 3, MaxSessions = 8 });
        sink.WriteLine("[00:00:00.000] kept record");
        sink.Flush();
        Directory.CreateDirectory(sink.ArchiveFile(1));

        // An archive slot that cannot receive the move stops writing rather than truncating the file.
        var threw = Threw(() => sink.WriteLine("[00:00:00.000] blocked record"));
        sink.Flush();
        sink.Close();

        Assert.NotNull(threw);
        Assert.True(sink.RotateFaultCount > 0, "a blocked rotation went unreported");
        Assert.Contains("kept record", File.ReadAllText(sink.CurrentFile, Encoding.UTF8), StringComparison.Ordinal);
        Assert.DoesNotContain("blocked record", File.ReadAllText(sink.CurrentFile, Encoding.UTF8), StringComparison.Ordinal);
    }

    [Fact]
    public void CreateFaultIsReportedWithoutTakingTheCallerDown()
    {
        EnsureRoot();
        var blocked = Path.Combine(root.Path, "blocker");
        File.WriteAllText(blocked, "a file where a directory should be");
        var sink = new AppLogging.FileLogSink(new AppLogging.Settings { LogDirectoryOverride = Path.Combine(blocked, "logs") });

        Assert.NotNull(Threw(() => sink.WriteLine("[00:00:00.000] cannot land")));
        Assert.Equal(1, sink.CreateFaultCount);
        Assert.False(Directory.Exists(Path.Combine(blocked, "logs")));
    }

    [Fact]
    public void OccupiedLogFileIsReportedAndRecoversWhenTheHolderLeaves()
    {
        EnsureRoot();
        var sink = new AppLogging.FileLogSink(new AppLogging.Settings { LogDirectoryOverride = root.Path });
        File.WriteAllText(sink.CurrentFile, "held by another window");
        using (var holder = new FileStream(sink.CurrentFile, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            Assert.NotNull(Threw(() => sink.WriteLine("[00:00:00.000] while occupied")));
            Assert.Equal(1, sink.CreateFaultCount);
        }

        // Once the older window lets go, the same sink keeps writing without a restart.
        sink.WriteLine("[00:00:00.000] after release");
        sink.Flush();
        var content = ReadWhileOpen();
        sink.Close();
        Assert.Contains("after release", content, StringComparison.Ordinal);
        Assert.DoesNotContain("while occupied", content, StringComparison.Ordinal);
        Assert.Contains("held by another window", content, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadOnlyLogFileIsReportedAsAPermissionFault()
    {
        EnsureRoot();
        var sink = new AppLogging.FileLogSink(new AppLogging.Settings { LogDirectoryOverride = root.Path });
        File.WriteAllText(sink.CurrentFile, "read only");
        File.SetAttributes(sink.CurrentFile, FileAttributes.ReadOnly);
        try
        {
            Assert.NotNull(Threw(() => sink.WriteLine("[00:00:00.000] denied")));
            Assert.Equal(1, sink.CreateFaultCount);
        }
        finally
        {
            File.SetAttributes(sink.CurrentFile, FileAttributes.Normal);
        }
    }

    [Fact]
    public void PruningKeepsTheNewestSupersededSessionsOnly()
    {
        EnsureRoot();
        foreach (var (pid, hours) in new[] { (11111, 1), (22222, 2), (33333, 3), (44444, 4) })
        {
            var path = Path.Combine(root.Path, $"compositor-app-{pid}.log");
            File.WriteAllText(path, $"compositor-app-{pid} body");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-hours));
        }

        var archive = Path.Combine(root.Path, "compositor-app-11111.log.1");
        File.WriteAllText(archive, "archive body");
        File.SetLastWriteTimeUtc(archive, DateTime.UtcNow.AddHours(-1));
        File.WriteAllText(Path.Combine(root.Path, "notes.txt"), "keep me");
        // A user's own files can start with a session name. The pruning rule is the logger's naming
        // rule, so anything TrySession would not have accepted is not ours to remove either.
        var userBackup = Path.Combine(root.Path, "compositor-app-44444.log.backup-important.txt");
        var userNotes = Path.Combine(root.Path, "compositor-app-22222.log.bak");
        File.WriteAllText(userBackup, "mine");
        File.WriteAllText(userNotes, "also mine");

        var sink = new AppLogging.FileLogSink(new AppLogging.Settings { LogDirectoryOverride = root.Path, MaxSessions = 2 });
        sink.WriteLine("[00:00:00.000] session opened");
        sink.Flush();
        sink.Close();

        Assert.True(File.Exists(Path.Combine(root.Path, "compositor-app-11111.log")));
        Assert.True(File.Exists(archive), "the newest superseded session lost an archive file");
        Assert.False(File.Exists(Path.Combine(root.Path, "compositor-app-22222.log")));
        Assert.False(File.Exists(Path.Combine(root.Path, "compositor-app-44444.log")));
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(root.Path, "notes.txt"), Encoding.UTF8));
        Assert.Equal("mine", File.ReadAllText(userBackup, Encoding.UTF8));
        Assert.Equal("also mine", File.ReadAllText(userNotes, Encoding.UTF8));
    }

    [Fact]
    public void PruningLeavesARunningInstancesWholeSessionAlone()
    {
        EnsureRoot();
        var held = Path.Combine(root.Path, "compositor-app-55555.log");
        var heldArchive = Path.Combine(root.Path, "compositor-app-55555.log.1");
        File.WriteAllText(held, "another live process");
        File.WriteAllText(heldArchive, "its rotated history");
        File.SetLastWriteTimeUtc(held, DateTime.UtcNow.AddHours(-1));
        File.SetLastWriteTimeUtc(heldArchive, DateTime.UtcNow.AddHours(-1));
        using (new FileStream(held, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            var sink = new AppLogging.FileLogSink(new AppLogging.Settings { LogDirectoryOverride = root.Path, MaxSessions = 1 });
            sink.WriteLine("[00:00:00.000] session opened");
            sink.Flush();
            sink.Close();
            // The previous implementation protected a live session by failing to delete its current
            // file, then deleted the archives in the same loop. Nothing about that session may be
            // touched now, so there is no fault to report either.
            Assert.Equal(0, sink.PruneFaultCount);
        }

        Assert.Equal("another live process", File.ReadAllText(held, Encoding.UTF8));
        Assert.Equal("its rotated history", File.ReadAllText(heldArchive, Encoding.UTF8));
    }

    [Fact]
    public void DefaultPathResolvesToTheWritableUserFolderWithoutCreatingIt()
    {
        var previous = Environment.GetEnvironmentVariable(AppLogging.DirectoryOverrideVariable);
        Environment.SetEnvironmentVariable(AppLogging.DirectoryOverrideVariable, null);
        try
        {
            var existed = Directory.Exists(ExpectedDefaultDirectory());
            var resolved = AppLogging.ResolveLogDirectory(new AppLogging.Settings());
            Assert.Equal(ExpectedDefaultDirectory(), resolved);
            Assert.Equal(existed, Directory.Exists(resolved));
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppLogging.DirectoryOverrideVariable, previous);
        }
    }

    [Fact]
    public void ExplicitAndInheritedOverridesSelectTheChildDirectory()
    {
        var isolated = EnsureRoot();
        var previous = Environment.GetEnvironmentVariable(AppLogging.DirectoryOverrideVariable);
        Environment.SetEnvironmentVariable(AppLogging.DirectoryOverrideVariable, null);
        try
        {
            Assert.Equal(isolated, AppLogging.ResolveLogDirectory(new AppLogging.Settings { LogDirectoryOverride = isolated }));
            Assert.Equal(Path.GetFullPath("relative/logs"), AppLogging.ResolveLogDirectory(new AppLogging.Settings { LogDirectoryOverride = "relative/logs" }));
            Assert.Equal(ExpectedDefaultDirectory(), AppLogging.ResolveLogDirectory(new AppLogging.Settings { LogDirectoryOverride = "   " }));

            Environment.SetEnvironmentVariable(AppLogging.DirectoryOverrideVariable, isolated);
            Assert.Equal(isolated, AppLogging.ResolveLogDirectory(new AppLogging.Settings()));
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppLogging.DirectoryOverrideVariable, previous);
        }
    }

    [Theory]
    [InlineData("compositor-app-123.log", "compositor-app-123.log")]
    [InlineData("compositor-app-123.log.2", "compositor-app-123.log")]
    [InlineData("compositor-app-1234.log.10", "compositor-app-1234.log")]
    [InlineData("compositor-app-abc.log", null)]
    [InlineData("compositor-app-12.log.bak", null)]
    [InlineData("notes.log", null)]
    [InlineData("compositor-app-.log", null)]
    public void OnlyThisImplementationNamesAreSessionFiles(string fileName, string? expected)
    {
        var matched = AppLogging.FileLogSink.TrySession(fileName, out var baseName);
        Assert.Equal(expected is not null, matched);
        Assert.Equal(expected ?? string.Empty, baseName);
    }

    [Fact]
    public void SessionFileNamesCarryTheProcessId()
    {
        var sink = new AppLogging.FileLogSink(new AppLogging.Settings { LogDirectoryOverride = EnsureRoot() });
        Assert.Equal($"compositor-app-{Environment.ProcessId}.log", sink.BaseName);
        Assert.Equal(Path.Combine(root.Path, sink.BaseName), sink.CurrentFile);
        Assert.Equal(Path.Combine(root.Path, $"{sink.BaseName}.1"), sink.ArchiveFile(1));
    }

    public void Dispose()
    {
        AppLogging.Shutdown(TimeSpan.FromSeconds(10));
        root.Dispose();
    }

    private List<FileInfo> OwnFiles() => Directory.GetFiles(root.Path)
        .Where(path => Path.GetFileName(path).StartsWith($"compositor-app-{Environment.ProcessId}", StringComparison.Ordinal))
        .Select(path => new FileInfo(path))
        .ToList();

    private static string Joined(IEnumerable<FileInfo> files) => string.Concat(files.Select(file => File.ReadAllText(file.FullName, Encoding.UTF8)));

    private string EnsureRoot()
    {
        if (!Directory.Exists(root.Path)) Directory.CreateDirectory(root.Path);
        return root.Path;
    }

    private string ReadWhileOpen()
    {
        using var handle = new FileStream(CurrentLogFileName(root.Path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(handle, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static Exception? Threw(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static string ExpectedDefaultDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Compositor", AppLogging.FolderName);

    private static int RecordIndex(string line) => int.Parse(line[(line.IndexOf("record ", StringComparison.Ordinal) + 7)..]);
}
