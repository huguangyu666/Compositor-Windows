#if DEBUG
using Compositor.Core;
using System.Text.Json.Nodes;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private Task? editorAutomationPreparationProbe;
    private TimeSpan? editorAutomationTimeoutProbe;

    private async Task VerifyLayoutMcpAsync(Func<string, JsonObject?, bool, bool, Task<JsonObject>> call)
    {
        Task<JsonObject> Call(string name, JsonObject? args = null, bool error = false) => call(name, args, name != "editor_state", error);
        var scene = EditorDocument.Create(160, 100); OpenDocument(scene); await WaitForFramesAsync(); await Call("editor_state");
        var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        string before = ManifestJson.Serialize(scene.Snapshot.Manifest);
        await Call("editor_batch", new() { ["commands"] = new JsonArray(
            new JsonObject { ["tool"] = "editor_layer_duplicate", ["arguments"] = new JsonObject { ["layerId"] = id.ToString() }, ["as"] = "copy" },
            new JsonObject { ["tool"] = "editor_layout", ["arguments"] = new JsonObject { ["operation"] = "group", ["layerIds"] = new JsonArray(id.ToString(), "$copy"), ["name"] = "Pair" }, ["as"] = "pair" }) });
        Guid folder = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        Require(scene.LayerCount == 3 && selectedLayerId == folder, "Grouping did not synchronize the native selection.");
        await Call("editor_history", new() { ["action"] = "undo" });
        Require(ManifestJson.Serialize(scene.Snapshot.Manifest) == before && selectedLayerId == id, "Layout batch did not undo atomically.");
        await Call("editor_history", new() { ["action"] = "redo" });
        await Call("editor_layout", new() { ["operation"] = "ungroup", ["layerIds"] = new JsonArray(folder.ToString()) });
        Require(scene.LayerCount == 2 && scene.Snapshot.Manifest.Layers.All(l => l.ParentId is null) && selectedLayerId == scene.Snapshot.Manifest.ActiveLayerId, "Ungroup left stale folder selection.");
        await Call("editor_layout", new() { ["operation"] = "align", ["layerIds"] = new JsonArray(id.ToString()), ["alignment"] = "right", ["relativeTo"] = "canvas" });
        string valid = ManifestJson.Serialize(scene.Snapshot.Manifest);
        await Call("editor_layout", new() { ["operation"] = "distribute", ["layerIds"] = new JsonArray(id.ToString()), ["axis"] = "horizontal" }, error: true);
        Require(ManifestJson.Serialize(scene.Snapshot.Manifest) == valid, "Invalid layout changed the document.");
        await Call("editor_layout", new() { ["operation"] = "crop", ["x"] = 10, ["y"] = 5, ["width"] = 120, ["height"] = 80 });
        Require(scene.Snapshot.Manifest.Width == 120 && scene.Snapshot.Manifest.Height == 80, "MCP crop did not update canvas dimensions.");
        await Call("editor_history", new() { ["action"] = "undo" });
        Require(ManifestJson.Serialize(scene.Snapshot.Manifest) == valid, "Crop Undo did not restore placement and canvas.");

        // Hold worker preparation past the request deadline, then release it after timeout.
        foreach (string name in new[] { "editor_select", "editor_mask" })
        {
            await Call("editor_state");
            var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            editorAutomationPreparationProbe = barrier.Task; editorAutomationTimeoutProbe = TimeSpan.FromMilliseconds(40);
            var args = new JsonObject { ["documentToken"] = editorDocumentToken, ["revision"] = editorRevision,
                ["mode"] = "all", ["operation"] = "paint", ["points"] = new JsonArray(new JsonArray(20, 20)), ["value"] = 0, ["layerId"] = id.ToString() };
            string original = ManifestJson.Serialize(scene.Snapshot.Manifest);
            var originalSelection = scene.ActiveSelection; bool hadMask = scene.Snapshot.Masks.ContainsKey(id);
            Task<JsonObject> pending = EditorAutomationAsync(name, args, scene);
            bool expired;
            try { await Task.Delay(200); expired = pending.IsCompleted; }
            finally { barrier.TrySetResult(); editorAutomationPreparationProbe = null; editorAutomationTimeoutProbe = null; }
            bool canceled = false;
            try { await pending; } catch (OperationCanceledException) { canceled = true; }
            await Task.Delay(50); await WaitForFramesAsync();
            Require(expired && canceled, "Worker preparation outlived the automation deadline.");
            Require(original == ManifestJson.Serialize(scene.Snapshot.Manifest) && ReferenceEquals(originalSelection, scene.ActiveSelection)
                && hadMask == scene.Snapshot.Masks.ContainsKey(id), "Timed-out preparation committed a late edit.");
        }
        await VerifyImportDeadlineMcpAsync();
    }
}
#endif
