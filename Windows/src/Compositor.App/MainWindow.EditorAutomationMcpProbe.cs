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
        await VerifyMcpActivityAsync(call);
    }

    private async Task VerifyMcpActivityAsync(Func<string, JsonObject?, bool, bool, Task<JsonObject>> call)
    {
        var scene = EditorDocument.Create(800, 600); OpenDocument(scene); await WaitForFramesAsync();
        mcpActivityAnimationsProbe = true;
        await call("editor_state", null, false, false);
        var creating = call("editor_text", new() { ["content"] = "AI preview", ["x"] = 40, ["y"] = 60,
            ["boxWidth"] = 400, ["boxHeight"] = 300 }, true, false);
        Require(await WaitUntilAsync(() => mcpActivity is not null, 3000), "MCP creation has no visual feedback.");
        Require(scene.LayerCount == 1 && !scene.CanUndo && !McpActivityHost.IsHitTestVisible,
            "MCP preview published content/history early or intercepted input.");
        Require(mcpActivity!.Plan.Target.Size == new SizeD(400, 300) && mcpActivity.Plan.Target.Origin == new PointD(40, 60),
            "MCP preview dimensions differ from the prepared element.");
        if (mcpActivity.Animated)
        {
            await Task.Delay(140);
            Require(McpActivityCursor.Visibility == Microsoft.UI.Xaml.Visibility.Visible && McpActivityFrame.Points.Count == 4
                && !creating.IsCompleted, "The AI cursor did not draw before publishing the element.");
            var tip = McpActivityFrame.Points[2];
            var origin = viewport.ToView(new(40, 60)); var end = viewport.ToView(new(440, 360));
            Require(tip.X > origin.X && tip.X < end.X && tip.Y > origin.Y && tip.Y < end.Y,
                "The AI cursor is not between the target corners.");
            viewport.SetZoom(viewport.Zoom * .8, viewport.Center); viewport.Pan(new(17, 9));
            OnMcpActivityFrame(null, EventArgs.Empty);
            var anchor = viewport.ToView(new(40, 60));
            Require(Math.Abs(McpActivityFrame.Points[0].X - anchor.X) < .001 && Math.Abs(McpActivityFrame.Points[0].Y - anchor.Y) < .001,
                "The creation frame drifted when the viewport changed.");
        }
        await creating; await WaitForFramesAsync();
        Require(scene.LayerCount == 2 && scene.Snapshot.Manifest.Layers.Last().Transform.Size == new SizeD(400, 300),
            "MCP creation did not publish its prepared element.");
        await call("editor_history", new() { ["action"] = "undo" }, true, false);
        Require(scene.LayerCount == 1 && !scene.CanUndo, "The visual feedback added an Undo step.");

        scene = EditorDocument.Create(800, 600); OpenDocument(scene); await WaitForFramesAsync();
        await call("editor_state", null, false, false);
        var stale = call("editor_text", new() { ["content"] = "Stale", ["boxWidth"] = 100, ["boxHeight"] = 100 }, true, true);
        Require(await WaitUntilAsync(() => mcpActivity is not null, 3000), "The stale-source test has no preview.");
        scene.RenameLayer(scene.Snapshot.Manifest.ActiveLayerId!.Value, "User edit during preview");
        await stale;
        Require(scene.LayerCount == 1 && McpActivityHost.Visibility == Microsoft.UI.Xaml.Visibility.Collapsed,
            "A stale animation published content or remained on the canvas.");
        await call("editor_state", null, false, false);

        var batch = call("editor_batch", new() { ["commands"] = new JsonArray(
            new JsonObject { ["tool"] = "editor_text", ["arguments"] = new JsonObject { ["content"] = "One", ["x"] = 40, ["y"] = 50 } },
            new JsonObject { ["tool"] = "editor_text", ["arguments"] = new JsonObject { ["content"] = "Two", ["x"] = 100, ["y"] = 100 } }) }, true, false);
        Require(await WaitUntilAsync(() => mcpActivity is { Compact: true }, 3000)
            && McpActivityCursor.Visibility == Microsoft.UI.Xaml.Visibility.Collapsed, "A batch did not use compact feedback.");
        await batch;
        await call("editor_history", new() { ["action"] = "undo" }, true, false);
        Require(scene.LayerCount == 1, "Compact batch feedback changed atomic Undo.");

        mcpActivityAnimationsProbe = false;
        await call("editor_text", new() { ["content"] = "Static feedback" }, true, false);
        Require(mcpActivity is { Animated: false } && McpActivityCursor.Visibility == Microsoft.UI.Xaml.Visibility.Collapsed,
            "Reduced-motion feedback animated a cursor or prevented the edit.");
        StopMcpActivity(); mcpActivityAnimationsProbe = true;
        await call("editor_history", new() { ["action"] = "undo" }, true, false);

        var retiring = call("editor_text", new() { ["content"] = "Retired document" }, true, true);
        Require(await WaitUntilAsync(() => mcpActivity is not null, 3000), "The document-retirement test has no preview.");
        var replacement = EditorDocument.Create(800, 600); OpenDocument(replacement);
        await retiring.WaitAsync(TimeSpan.FromSeconds(2));
        Require(replacement.LayerCount == 1 && !replacement.CanUndo && McpActivityHost.Visibility == Microsoft.UI.Xaml.Visibility.Collapsed,
            "A retired preview affected the replacement document.");
        scene = replacement; await call("editor_state", null, false, false);

        var moving = call("editor_layer_transform", new() { ["layerId"] = scene.Snapshot.Manifest.ActiveLayerId!.Value.ToString(), ["x"] = 20 }, true, true);
        Require(await WaitUntilAsync(() => mcpActivity is not null, 3000), "The retired-layer test has no preview.");
        replacement = EditorDocument.Create(800, 600); OpenDocument(replacement);
        await moving.WaitAsync(TimeSpan.FromSeconds(2));
        Require(!scene.CanUndo && !replacement.CanUndo && McpActivityHost.Visibility == Microsoft.UI.Xaml.Visibility.Collapsed,
            "A retired layer preview changed either document or remained visible.");
        scene = replacement; await call("editor_state", null, false, false);
        McpAnimationToggle.IsChecked = false;
        try
        {
            await call("editor_text", new() { ["content"] = "No animation" }, true, false);
            Require(mcpActivity is null && scene.LayerCount == 2, "Disabling MCP animations blocked editing or retained a visual.");
        }
        finally { McpAnimationToggle.IsChecked = true; mcpActivityAnimationsProbe = null; }
    }
}
#endif
