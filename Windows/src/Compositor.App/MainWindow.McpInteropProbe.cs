#if DEBUG
using System.Diagnostics;
using System.Text.Json.Nodes;
using Compositor.Core;
using Microsoft.UI.Xaml.Controls;

namespace Compositor.App;

public sealed partial class MainWindow
{
    /// <summary>
    /// DEBUG-only end-to-end check of the automation path against the live editor: a real
    /// Compositor.Mcp.exe process writes the package this window has open. The watcher probe already
    /// proves the editor reacts to a foreign write; nothing proves the write an agent actually makes
    /// is a package the editor accepts, or that the two writers refuse each other the way the editor
    /// refuses itself.
    /// </summary>
    internal async Task RunMcpInteropProbeAsync()
    {
        var report = new Dictionary<string, object>();
        var root = Path.Combine(Path.GetTempPath(), $"compositor-mcp-interop-{Guid.NewGuid():N}");
        Process? server = null;
        StreamWriter? toServer = null;
        StreamReader? fromServer = null;
        try
        {
            var serverPath = Environment.GetEnvironmentVariable("COMPOSITOR_MCP_EXECUTABLE");
            if (string.IsNullOrWhiteSpace(serverPath) || !File.Exists(serverPath))
            {
                throw new InvalidOperationException(
                    $"COMPOSITOR_MCP_EXECUTABLE must name a built Compositor.Mcp.exe for this probe; got '{serverPath}'.");
            }
            Directory.CreateDirectory(Path.Combine(root, "images"));
            var scene = EditorDocument.Create(8, 6);
            await ProjectStore.SaveAsync(scene.Snapshot, root);
            var asset = scene.Snapshot.Manifest.Layers[0].ImageFile
                ?? throw new InvalidOperationException("The fixture has no asset.");
            var agentPng = Path.Combine(root, "agent.png");
            await File.WriteAllBytesAsync(agentPng, await File.ReadAllBytesAsync(Path.Combine(root, "images", asset)));
            var layerId = scene.Snapshot.Manifest.Layers[0].Id;

            OpenDocument(new EditorDocument(await ProjectStore.LoadAsync(root)), root);
            await WaitForFramesAsync();
            Require(document is not null && !document.IsDirty, "the interop fixture opened a dirty document, so the clean reload path would not be the one under test.");

            var start = new ProcessStartInfo(serverPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = root,
            };
            server = Process.Start(start) ?? throw new InvalidOperationException("The MCP server did not start.");
            toServer = server.StandardInput;
            fromServer = server.StandardOutput;
            int nextId = 1;

            async Task<JsonObject> Send(string method, JsonObject? parameters)
            {
                var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = nextId, ["method"] = method };
                if (parameters is not null) request["params"] = parameters;
                toServer!.WriteLine(request.ToJsonString());
                toServer!.Flush();
                var read = fromServer!.ReadLineAsync();
                if (await Task.WhenAny(read, Task.Delay(20_000)) != read) throw new TimeoutException($"the MCP server did not answer id {nextId} within 20 s.");
                var line = read.Result ?? throw new InvalidOperationException($"the MCP server closed stdout on id {nextId}.");
                nextId++;
                return JsonNode.Parse(line)!.AsObject();
            }

            // DeepClone: a JsonNode sits under exactly one parent, and the probe hands the same argument
            // object to the retry after a refusal.
            async Task<JsonObject> Call(string tool, JsonObject arguments)
                => await Send("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments.DeepClone() });

            string TextOf(JsonObject reply) => reply["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? string.Empty;
            bool Refused(JsonObject reply, int code) => reply["error"]?["code"]?.GetValue<int>() == code;

            var openArguments = new JsonObject { ["path"] = root };
            var opened = await Call("open_project", openArguments);
            Require(!Refused(opened, -32000) && !string.IsNullOrEmpty(TextOf(opened)), $"the server could not open the package the editor has open: {opened.ToJsonString()}");

            // 1. An agent adds a layer: the editor has to be able to load what the server wrote, and a clean
            //    document takes it without being asked. A package the editor cannot read shows up as the
            //    failure status, never as S_Reloaded.
            var addArguments = new JsonObject { ["name"] = "Added by agent", ["pngPath"] = agentPng };
            var added = await Call("add_layer", addArguments);
            Require(!Refused(added, -32602) && !Refused(added, -32000), $"add_layer refused in a session that had just opened the package: {added.ToJsonString()}");
            var reloaded = await WaitUntilAsync(() => StatusText.Text == Loc.T("S_Reloaded"), 10_000);
            report["reloadStatus"] = StatusText.Text;
            var seenByEditor = document?.Snapshot.Manifest.Layers.Any(record => record.Name == "Added by agent") == true;
            Require(reloaded, $"the editor never reloaded what the MCP server wrote (status: {StatusText.Text}).");
            Require(seenByEditor, "the reload landed but the layer the agent added is not in the editor's document.");
            Require(document is not null && !document.IsDirty, "taking the agent's write left the editor holding an unsaved mark it did not earn.");

            // 2. The other direction, with the editor's own save as the outside write: the server must refuse
            //    rather than replace it, and reading again must be the way back.
            document!.RenameLayer(layerId, "Renamed in the editor");
            Require(await SaveNow(), "the editor's own save before the interop refusal test did not go through.");
            var manifestBeforeRefusal = await File.ReadAllBytesAsync(Path.Combine(root, "manifest.json"));
            var refused = await Call("add_layer", addArguments);
            Require(Refused(refused, -32000), $"the server saved over the editor's just-saved package instead of refusing: {refused.ToJsonString()}");
            Require((await File.ReadAllBytesAsync(Path.Combine(root, "manifest.json"))).AsSpan().SequenceEqual(manifestBeforeRefusal),
                "the refused save still rewrote the manifest.");
            var state = await Call("get_project_state", new JsonObject());
            Require(TextOf(state).Contains("Renamed in the editor", StringComparison.Ordinal),
                "get_project_state did not show the editor's rename, so the client has nothing to re-aim at.");
            var retryArguments = new JsonObject { ["name"] = "Added by agent again", ["pngPath"] = agentPng };
            var retried = await Call("add_layer", retryArguments);
            Require(!Refused(retried, -32000), $"reading again was not the way back out of the refusal: {retried.ToJsonString()}");
            var bothOnDisk = await ProjectStore.LoadAsync(root);
            Require(bothOnDisk.Manifest.Layers.Any(record => record.Name == "Renamed in the editor")
                  && bothOnDisk.Manifest.Layers.Any(record => record.Name == "Added by agent again"),
                $"the agent's retry lost the editor's work: [{string.Join(", ", bothOnDisk.Manifest.Layers.Select(record => record.Name))}]");

            // 3. Unsaved editor work is not replaced silently by an agent. The ask is the editor's own promise
            //    (ProjectController+ExternalChanges.swift); here it has to fire for a write the agent really made.
            var asked = new List<string>();
            projectDialogProbe = dialog =>
            {
                asked.Add($"{dialog.Title}|{(dialog.Content as TextBlock)?.Text}|{dialog.PrimaryButtonText}|{dialog.SecondaryButtonText}");
                return Task.FromResult(ContentDialogResult.Secondary);
            };
            var tookTheRetry = await WaitUntilAsync(() => document?.Snapshot.Manifest.Layers.Any(record => record.Name == "Added by agent again") == true, 10_000);
            Require(tookTheRetry, "the editor never took the agent's retried write into its document.");
            document!.RenameLayer(layerId, "Mine, unsaved");
            Require(document.IsDirty, "the dirty fixture left nothing to defend.");
            // A preview folder planted while the document is dirty: the only writer that can run now is the agent,
            // so where this folder ends up says what the server's save does with one it did not make.
            Directory.CreateDirectory(Path.Combine(root, "QuickLook"));
            await File.WriteAllBytesAsync(Path.Combine(root, "QuickLook", "preview.jpg"), [(byte)0xFF, (byte)0xD8]);
            report["quickLookBeforeAgentSave"] = "planted";
            var secondAdd = new JsonObject { ["name"] = "Added while dirty", ["pngPath"] = agentPng };
            var duringDirty = await Call("add_layer", secondAdd);
            Require(!Refused(duringDirty, -32000), $"the agent had just read the package, so its next write should be allowed: {duringDirty.ToJsonString()}");
            var askedOnce = await WaitUntilAsync(() => asked.Count == 1, 10_000);
            report["askedShape"] = asked.Count == 1 ? asked[0] : $"{asked.Count} asks";
            Require(askedOnce, $"a dirty editor met by an agent write asked {asked.Count} times instead of once.");
            Require(asked[0].Contains(Loc.T("Dlg_Revert"), StringComparison.Ordinal)
                  && asked[0].Contains(Loc.T("Dlg_KeepMine"), StringComparison.Ordinal),
                "the agent-write conflict prompt did not offer both answers.");
            Require(document.Snapshot.Manifest.Layers.Any(record => record.Name == "Mine, unsaved"),
                "answering Keep Mine threw the editor's unsaved work away.");
            var agentLayerOnDisk = await File.ReadAllTextAsync(Path.Combine(root, "manifest.json"));
            Require(agentLayerOnDisk.Contains("Added while dirty", StringComparison.Ordinal),
                "Keep Mine undid the agent's write instead of adopting it as the baseline.");
            projectDialogProbe = null;

            var afterAgent = Directory.Exists(Path.Combine(root, "QuickLook"));
            report["quickLookAfterAgentSaves"] = afterAgent ? "kept" : "deleted by the server's save";
            Require(!afterAgent, "the agent's save kept a QuickLook folder it never made, which is not what "
                + "ProjectStore.SaveAsync does; if a preview generator was added, this is the line to update "
                + "together with the editor's half below.");
            // #62's premise, measured instead of carried over from the Mac: on the Mac the editor writes the
            // Space-bar preview inside the same save (ImageExporter.quickLookImages, ProjectStore.swift:84 and :118
            // put `contents["QuickLook"]` down), so an agent save looked like it destroyed something the editor
            // would restore. The port has no such writer at all — `ProjectStore.cs:152` only ever deletes the
            // folder — so the two paths have to agree by deleting. This is the editor's half of that check.
            Directory.CreateDirectory(Path.Combine(root, "QuickLook"));
            await File.WriteAllBytesAsync(Path.Combine(root, "QuickLook", "preview.jpg"), [(byte)0xFF, (byte)0xD8]);
            Require(await SaveNow(), "the editor's own save did not go through, so the preview symmetry check measured nothing.");
            var afterEditor = Directory.Exists(Path.Combine(root, "QuickLook"));
            report["quickLookAfterEditorSave"] = afterEditor ? "kept" : "deleted by the editor's save";
            Require(afterEditor == afterAgent,
                $"the two writers disagree about the preview folder: agent {afterAgent}, editor {afterEditor}. "
                + "Either both generate a preview or neither does; one of the two is currently a surprise.");
            report["watcherStatus"] = watcher?.Status() ?? "no watcher";
            report["status"] = "passed";
        }
        catch (Exception ex)
        {
            report["status"] = "failed";
            report["error"] = ex.ToString();
            App.Log($"MCP interop probe failed: {ex}", LogLevel.Error);
        }
        finally
        {
            projectDialogProbe = null;
            try { toServer?.Close(); } catch (IOException) { }
            if (server is not null)
            {
                try { if (!server.WaitForExit(5000)) server.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                server.Dispose();
            }

            WriteProbeReport("mcp-interop-probe.json", report);
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
            catch (IOException)
            {
                // A scratch package the server still holds open is noise, not a failed behavior.
            }

            forceClose = true;
            Close();
        }
    }
}
#endif
