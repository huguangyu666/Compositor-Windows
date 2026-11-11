using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace Compositor.App.Automation;

/// <summary>Same-user local transport. All document work is dispatched by the window.</summary>
internal sealed class EditorPipeServer(Func<JsonObject, Task<JsonObject>> dispatch) : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    public Task RunAsync() => RunCoreAsync(lifetime.Token);

    private async Task RunCoreAsync(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream($"Compositor.Editor.{Environment.ProcessId}", PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellation).ConfigureAwait(false);
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                var line = await reader.ReadLineAsync(requestTimeout.Token).ConfigureAwait(false);
                JsonObject response;
                try
                {
                    if (line is null || line.Length > 1024 * 1024 || JsonNode.Parse(line) is not JsonObject request)
                        throw new ArgumentException("Invalid editor request.");
                    response = await dispatch(request).WaitAsync(requestTimeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException)
                {
                    response = new() { ["isError"] = true, ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = ex.Message }) };
                }
                await writer.WriteLineAsync(response.ToJsonString().AsMemory(), requestTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        }
    }

    public void Dispose() => lifetime.Cancel();
}
