#if DEBUG
using System.Text.Json;

namespace Compositor.App;

public sealed partial class MainWindow
{
    /// <summary>
    /// Writes a probe report where the driver expects to find it. Two editors started from the same
    /// build directory share that name, so a collision falls back to a PID-suffixed file rather than
    /// throwing: every caller writes this inside the `finally` that then closes the window, and an
    /// exception escaping from there leaves the window open until the driver kills it — which is what
    /// one dual-instance run of `--logging-probe` did before this existed.
    /// </summary>
    private void WriteProbeReport(string fileName, Dictionary<string, object> report, bool includeFields = false)
    {
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, IncludeFields = includeFields });
        try
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, fileName), json);
            return;
        }
        catch (IOException held)
        {
            App.Log($"probe report held by another instance: {held.Message}", LogLevel.Diagnostic);
        }
        try
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory,
                $"{Path.GetFileNameWithoutExtension(fileName)}-{Environment.ProcessId}{Path.GetExtension(fileName)}"), json);
        }
        catch (Exception ex) { App.Log($"probe report write failed: {ex}", LogLevel.Error); }
    }
}
#endif
