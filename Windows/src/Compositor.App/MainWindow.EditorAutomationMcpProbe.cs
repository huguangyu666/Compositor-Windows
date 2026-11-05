#if DEBUG
using Compositor.Core;
using System.Text.Json.Nodes;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task VerifyAutomationMcpAsync(Func<string, JsonObject?, bool, bool, Task<JsonObject>> call)
    {
        Task<JsonObject> Call(string name, JsonObject? args = null, bool error = false) => call(name, args, name is not ("editor_state" or "editor_preview"), error);
        var scene = EditorDocument.Create(128, 96); OpenDocument(scene); await WaitForFramesAsync(); await Call("editor_state");
        var first = await Call("editor_preview", new() { ["maxSide"] = 64 });
        var second = await Call("editor_preview", new() { ["maxSide"] = 64 });
        var previewInfo = JsonNode.Parse(second["content"]![0]!["text"]!.GetValue<string>())!;
        Require(previewInfo["cached"]!.GetValue<bool>() && first["content"]![1]!["data"]!.GetValue<string>() == second["content"]![1]!["data"]!.GetValue<string>(), "Repeated previews did not reuse exact PNG bytes.");
        await Call("editor_select", new() { ["mode"] = "polygon", ["points"] = new JsonArray(new JsonArray(8, 8), new JsonArray(100, 8), new JsonArray(8, 80)), ["feather"] = 2 });
        Require(scene.ActiveSelection is { Feather: 2 } && scene.ActiveSelection.CoverageAt(120, 90) == 0, "MCP polygon/feather changed protected coverage.");
        string root = Environment.GetEnvironmentVariable("COMPOSITOR_PREFERENCES_DIRECTORY")!;
        var mask = new Surface(128, 96);
        for (int i = 0; i < mask.Pixels.Length; i += 4) mask.Pixels[i + 3] = 255;
        for (int y = 20; y < 30; y++) for (int x = 20; x < 30; x++)
        { int i = y * mask.Stride + x * 4; mask.Pixels[i] = mask.Pixels[i + 1] = mask.Pixels[i + 2] = 255; }
        string path = Path.Combine(root, "selection-mask.png"); await File.WriteAllBytesAsync(path, await WicImaging.EncodePngAsync(mask));
        await Call("editor_select", new() { ["mode"] = "mask", ["path"] = path });
        Require(scene.ActiveSelection!.CoverageAt(25, 25) == 255 && scene.ActiveSelection.CoverageAt(10, 10) == 0, "Mask PNG coverage was not preserved.");
        await Call("editor_select", new() { ["mode"] = "ellipse", ["x"] = 22, ["y"] = 22, ["width"] = 6, ["height"] = 6, ["combine"] = "subtract" });
        Require(scene.ActiveSelection!.CoverageAt(25, 25) == 0 && scene.ActiveSelection.CoverageAt(20, 20) == 255, "Selection subtraction failed.");
        Guid id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        string before = ManifestJson.Serialize(scene.Snapshot.Manifest);
        var batch = await Call("editor_batch", new() { ["commands"] = new JsonArray(
            new JsonObject { ["tool"] = "editor_layer_duplicate", ["arguments"] = new JsonObject { ["layerId"] = id.ToString() }, ["as"] = "copy" },
            new JsonObject { ["tool"] = "editor_layer_transform", ["arguments"] = new JsonObject { ["layerId"] = "$copy", ["x"] = 5 } },
            new JsonObject { ["tool"] = "editor_layer_opacity", ["arguments"] = new JsonObject { ["layerId"] = "$copy", ["opacity"] = .4 } }) });
        var batchState = JsonNode.Parse(batch["content"]![0]!["text"]!.GetValue<string>())!;
        Require(batchState["batchAliases"]?["copy"] is not null && scene.LayerCount == 2, "Batch result aliases are missing.");
        await Call("editor_history", new() { ["action"] = "undo" });
        Require(ManifestJson.Serialize(scene.Snapshot.Manifest) == before && selectedLayerId == id, "Batch Undo lost content or native selection.");
        await Call("editor_batch", new() { ["commands"] = new JsonArray(
            new JsonObject { ["tool"] = "editor_layer_rename", ["arguments"] = new JsonObject { ["layerId"] = id.ToString(), ["name"] = "Temporary" } },
            new JsonObject { ["tool"] = "editor_layer_opacity", ["arguments"] = new JsonObject { ["layerId"] = id.ToString(), ["opacity"] = 2 } }) }, error: true);
        Require(ManifestJson.Serialize(scene.Snapshot.Manifest) == before && scene.CanRedo, "Failed batch changed content or erased redo.");
        await Call("editor_text", new() { ["content"] = "Hello AI", ["fontName"] = "Arial", ["fontSize"] = 18 });
        Guid textId = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        await Call("editor_text", new() { ["layerId"] = textId.ToString(), ["content"] = "你好", ["color"] = new JsonArray(1, 0, 0) });
        Require(scene.Snapshot.Manifest.Layers.Single(l => l.Id == textId).Text!.Content == "你好", "MCP text was not editable metadata.");
        await Call("editor_adjustment", new() { ["kind"] = "Exposure", ["settings"] = new JsonObject { ["exposureSettings"] = new JsonObject { ["exposure"] = -.5 } } });
        var adjustment = scene.Snapshot.Manifest.Layers.Last();
        Require(adjustment.Adjustment?.ExposureSettings?.Exposure == -.5, "MCP adjustment parameters were not applied.");
        await Call("editor_state");
        var after = await Call("editor_preview", new() { ["maxSide"] = 64 });
        Require(JsonNode.Parse(after["content"]![0]!["text"]!.GetValue<string>())!["cached"]!.GetValue<bool>() == false, "Document edits reused a stale preview.");
        await VerifyLayoutMcpAsync(call);
    }
}
#endif
