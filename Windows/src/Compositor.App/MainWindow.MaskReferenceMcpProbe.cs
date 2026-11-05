#if DEBUG
using Compositor.App.Ai;
using Compositor.Core;
using System.Text.Json.Nodes;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task VerifyMaskReferenceMcpAsync(Func<string, JsonObject?, bool, bool, Task<JsonObject>> call, AiImageProbeHandler handler)
    {
        handler.Waiting = null; handler.Pending = false;
        Task<JsonObject> Call(string name, JsonObject? args = null, bool error = false) => call(name, args, name is not ("editor_state" or "editor_preview"), error);
        var scene = EditorDocument.Create(32, 24); Guid id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        var image = scene.Snapshot.Images[id]; var mask = new MaskSurface(32, 24); mask.Pixels.AsSpan().Fill(80); scene.AddRasterMask(id, mask);
        OpenDocument(scene); await WaitForFramesAsync(); await Call("editor_state");
        await Call("editor_mask", new() { ["layerId"] = id.ToString(), ["operation"] = "invert" });
        Require(ReferenceEquals(image, scene.Snapshot.Images[id]) && scene.Snapshot.Masks[id].Pixels[0] == 175 && mask.Pixels[0] == 80, "Mask refinement changed image pixels or the source mask.");
        var preview = await Call("editor_preview", new() { ["view"] = "mask", ["layerId"] = id.ToString(), ["region"] = new JsonObject { ["x"] = 3, ["y"] = 4, ["width"] = 5, ["height"] = 6 } });
        var png = preview["content"]!.AsArray().Single(item => item?["type"]?.GetValue<string>() == "image")!;
        var decoded = await WicImaging.DecodeSurfaceAsync(Convert.FromBase64String(png["data"]!.GetValue<string>()), 256, 65536);
        Require(decoded.Width == 5 && decoded.Height == 6 && decoded.Pixels[0] == 175 && decoded.Pixels[3] == 255, "Mask detail preview lost its gray values or crop.");
        await Call("editor_preview", new() { ["view"] = "layer", ["layerId"] = id.ToString(), ["region"] = new JsonObject { ["x"] = 3, ["y"] = 4, ["width"] = 5, ["height"] = 6 } });
        await Call("editor_history", new() { ["action"] = "undo" });
        Require(ReferenceEquals(mask, scene.Snapshot.Masks[id]), "Mask refinement was not one Undo.");
        string root = Environment.GetEnvironmentVariable("COMPOSITOR_PREFERENCES_DIRECTORY")!;
        string referencePath = Path.Combine(root, "reference-image.png"); await File.WriteAllBytesAsync(referencePath, await WicImaging.EncodePngAsync(image));
        await Call("editor_select", new() { ["mode"] = "rectangle", ["x"] = 8, ["y"] = 8, ["width"] = 8, ["height"] = 8 });
        await Call("editor_mask", new() { ["layerId"] = id.ToString(), ["operation"] = "paint", ["points"] = new JsonArray(new JsonArray(10, 10), new JsonArray(20, 10)), ["radius"] = 4, ["value"] = 0 });
        var painted = scene.Snapshot.Masks[id];
        Require(painted.Pixels[10 * painted.Width + 11] < 80 && painted.Pixels[10 * painted.Width + 20] == 80, "MCP mask brush ignored its selection.");
        await Call("editor_history", new() { ["action"] = "undo" });
        await Call("editor_ai_start", new() { ["operation"] = "replace", ["prompt"] = "change the scene", ["model"] = ImageModels.Flare,
            ["preserve"] = "same face", ["references"] = new JsonArray(new JsonObject { ["path"] = referencePath, ["role"] = "identity" }, new JsonObject { ["path"] = referencePath, ["role"] = "clothing" }) });
        Require(await WaitUntilAsync(() => aiEditor?.Work is null, 10000), "Reference image edit did not finish.");
        Require(aiEditor?.Status == "ready" && handler.InputImageCount == 3 && handler.LastPrompt?.Contains("Image 2: identity") == true
            && handler.LastPrompt.Contains("Image 3: clothing") && handler.LastPrompt.Contains("same face"), "MCP lost reference pixels, image order, roles or preservation instructions.");
        RetireAiEdit(); await Call("editor_state");
        string saved = Path.Combine(root, "refined-mask.comp"); await Call("editor_save", new() { ["path"] = saved });
        Require(new CanvasCompositor(await ProjectStore.LoadAsync(saved)).Render().Pixels.SequenceEqual(new CanvasCompositor(scene.Snapshot).Render().Pixels), "Refined mask did not survive save/reload.");
    }
}
#endif
