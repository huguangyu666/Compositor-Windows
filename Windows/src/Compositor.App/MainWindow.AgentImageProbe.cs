#if DEBUG
using Compositor.App.Ai;
using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json.Nodes;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task VerifyAgentImageMcpAsync(Func<string, JsonObject?, bool, bool, Task<JsonObject>> call, AiImageProbeHandler handler)
    {
        int requests = handler.Calls;
        new AiCredentialStore().Delete("api-key");
        var scene = EditorDocument.Create(8, 6); OpenDocument(scene); await WaitForFramesAsync();
        var asset = new Surface(8, 6); asset.Pixels[(2 * 8 + 3) * 4] = asset.Pixels[(2 * 8 + 3) * 4 + 3] = 255;
        string assetPath = Path.Combine(Environment.GetEnvironmentVariable("COMPOSITOR_PREFERENCES_DIRECTORY")!, "agent-asset.png");
        await File.WriteAllBytesAsync(assetPath, await WicImaging.EncodePngAsync(asset));
        await call("editor_state", null, false, false);
        await call("editor_layer_import", new() { ["path"] = assetPath, ["x"] = 10, ["y"] = 20, ["width"] = 80, ["height"] = 60 }, true, false);
        var placed = scene.Snapshot.Manifest.Layers[^1];
        Require(placed.Transform.Origin == new PointD(40, 40) && placed.Transform.Size == new SizeD(10, 10)
            && scene.Snapshot.Images[placed.Id].Width == 1 && scene.Snapshot.Images[placed.Id].Height == 1,
            "Agent asset placement did not preserve scaled artwork while trimming transparent margins.");
        await call("editor_history", new() { ["action"] = "undo" }, true, false);
        Require(scene.LayerCount == 1 && !scene.CanUndo, "Placed agent asset did not retain single-step Undo.");
        await call("editor_layer_import", new() { ["path"] = assetPath, ["x"] = 0, ["y"] = 0, ["width"] = 1, ["height"] = 1 }, true, false);
        placed = scene.Snapshot.Manifest.Layers[^1];
        Require(placed.Transform.Size == new SizeD(1, 1) && scene.Snapshot.Images[placed.Id].Width == 8,
            "A subpixel crop must retain the valid input grid instead of failing import.");
        await call("editor_history", new() { ["action"] = "undo" }, true, false);
        await call("editor_layer_import", new() { ["path"] = assetPath, ["x"] = 10, ["y"] = 20, ["width"] = 80, ["height"] = 60, ["trim"] = false }, true, false);
        placed = scene.Snapshot.Manifest.Layers[^1];
        Require(placed.Transform.Origin == new PointD(10, 20) && placed.Transform.Size == new SizeD(80, 60)
            && scene.Snapshot.Images[placed.Id].Width == 8, "trim=false did not preserve the source grid and bounds.");
        await call("editor_history", new() { ["action"] = "undo" }, true, false);
        scene = EditorDocument.Create(8, 6); OpenDocument(scene); await WaitForFramesAsync();
        var mask = new MaskSurface(8, 6);
        mask.Pixels[10] = 255; mask.Pixels[11] = 128;
        scene.SetSelection(Selection.FromCoverage(mask));
        await call("editor_state", null, false, false);
        await call("editor_ai_prepare", new() { ["operation"] = "replace" }, true, false);
        var state = EditorState(); var job = state["ai"]!;
        string requestId = job["requestId"]!.GetValue<string>();
        string inputPath = job["input"]!["imagePath"]!.GetValue<string>();
        string maskPath = job["input"]!["maskPath"]!.GetValue<string>();
        Require(job["source"]!.GetValue<string>() == "agent" && job["status"]!.GetValue<string>() == "waiting"
            && File.Exists(inputPath) && File.Exists(maskPath) && scene.LayerCount == 1 && !scene.CanUndo,
            "Agent input capture was not local, frozen and history-free.");
        var input = await WicImaging.DecodeSurfaceAsync(await File.ReadAllBytesAsync(inputPath));
        var inputMask = await WicImaging.DecodeSurfaceAsync(await File.ReadAllBytesAsync(maskPath));
        Require(input.Width == 8 && input.Height == 6 && input.Pixels.SequenceEqual(new CanvasCompositor(scene.Snapshot).Render().Pixels)
            && inputMask.Pixels[10 * 4 + 3] == 0 && inputMask.Pixels[11 * 4 + 3] == 127 && inputMask.Pixels[3] == 255,
            "Agent input or transparent-edit mask changed the captured pixels.");
        Require(AiWorkingHost.Visibility == Visibility.Visible && !FloatingPanelConfirmEnabled(AiPanelKey),
            "Waiting for an agent did not show activity or incorrectly enabled Accept.");
        var generated = new Surface(8, 6);
        for (int p = 0; p < generated.Pixels.Length; p += 4) { generated.Pixels[p] = 190; generated.Pixels[p + 3] = 255; }
        string resultPath = Path.Combine(Environment.GetEnvironmentVariable("COMPOSITOR_PREFERENCES_DIRECTORY")!, "agent-result.png");
        await File.WriteAllBytesAsync(resultPath, await WicImaging.EncodePngAsync(generated));
        await call("editor_ai_import", new() { ["requestId"] = "other-request", ["path"] = resultPath }, true, true);
        await call("editor_ai_import", new() { ["requestId"] = requestId, ["path"] = resultPath + ".missing.png" }, true, true);
        Require(scene.LayerCount == 1 && !scene.CanUndo && aiEditor?.Status == "waiting", "A rejected agent result damaged the draft.");
        await call("editor_ai_import", new() { ["requestId"] = requestId, ["path"] = resultPath }, true, false);
        Require(aiEditor?.Status == "ready" && aiEditor.Candidates.Count == 1 && AiPreviewHost.Visibility == Visibility.Visible
            && scene.LayerCount == 1 && !scene.CanUndo, "Agent import committed before review.");
        var after = AiPreviewImage.Source;
        var compare = floatingPanels[AiPanelKey].Body.Children.OfType<StackPanel>().Single().Children.OfType<ToggleSwitch>().Single();
        compare.IsOn = true;
        Require(!ReferenceEquals(after, AiPreviewImage.Source), "Agent original comparison did not change the preview.");
        compare.IsOn = false;
        await call("editor_preview", new() { ["view"] = "result" }, false, false);
        await call("editor_ai_accept", new() { ["requestId"] = requestId }, true, false);
        var composite = new CanvasCompositor(scene.Snapshot).Render();
        Require(scene.LayerCount == 2 && composite.Pixels[0] == 255 && composite.Pixels[10 * 4] == 190
            && composite.Pixels[11 * 4] is > 190 and < 255 && !File.Exists(inputPath) && !File.Exists(maskPath),
            "Agent accept lost selection protection, feather coverage or temporary-input cleanup.");
        var added = scene.Snapshot.Manifest.Layers[^1];
        Require(added.Transform.Origin == new PointD(2, 1) && scene.Snapshot.Images[added.Id].Width == 2,
            "Agent result retained transparent padding or changed placement.");
        await call("editor_history", new() { ["action"] = "undo" }, true, false);
        Require(scene.LayerCount == 1 && !scene.CanUndo && new CanvasCompositor(scene.Snapshot).Render().Pixels.SequenceEqual(input.Pixels),
            "Agent accept did not retain single-step Undo.");
        await call("editor_history", new() { ["action"] = "redo" }, true, false);
        string saved = Path.Combine(Environment.GetEnvironmentVariable("COMPOSITOR_PREFERENCES_DIRECTORY")!, "agent-accepted.comp");
        await call("editor_save", new() { ["path"] = saved }, true, false);
        Require(new CanvasCompositor(await ProjectStore.LoadAsync(saved)).Render().Pixels.SequenceEqual(composite.Pixels),
            "The accepted agent image did not survive save/reopen.");

        scene = EditorDocument.Create(8, 6); OpenDocument(scene); await WaitForFramesAsync();
        scene.SetSelection(Selection.Rectangle(8, 6, 2, 1, 3, 2));
        await call("editor_state", null, false, false);
        await call("editor_ai_prepare", new() { ["operation"] = "replace" }, true, false);
        requestId = EditorState()["ai"]!["requestId"]!.GetValue<string>();
        inputPath = EditorState()["ai"]!["input"]!["imagePath"]!.GetValue<string>();
        await call("editor_ai_cancel", new() { ["requestId"] = requestId }, true, false);
        await call("editor_ai_import", new() { ["requestId"] = requestId, ["path"] = resultPath }, true, true);
        await call("editor_ai_cancel", new() { ["requestId"] = requestId, ["discard"] = true }, true, false);
        Require(aiEditor is null && !File.Exists(inputPath) && !scene.CanUndo, "Agent cancellation left inputs or changed history.");
        await call("editor_ai_prepare", new() { ["operation"] = "replace" }, true, false);
        requestId = EditorState()["ai"]!["requestId"]!.GetValue<string>();
        inputPath = EditorState()["ai"]!["input"]!["imagePath"]!.GetValue<string>();
        scene.SetSelection(null);
        await call("editor_state", null, false, false);
        await call("editor_ai_import", new() { ["requestId"] = requestId, ["path"] = resultPath }, true, true);
        Require(aiEditor is null && !File.Exists(inputPath) && !scene.CanUndo, "A stale agent result survived selection replacement.");

        await call("editor_ai_prepare", new() { ["operation"] = "expand", ["left"] = 2, ["right"] = 3, ["top"] = 1 }, true, false);
        requestId = EditorState()["ai"]!["requestId"]!.GetValue<string>();
        inputPath = EditorState()["ai"]!["input"]!["imagePath"]!.GetValue<string>();
        input = await WicImaging.DecodeSurfaceAsync(await File.ReadAllBytesAsync(inputPath));
        Require(input.Width == 13 && input.Height == 7 && input.Pixels[3] == 0, "Agent expansion input has incorrect bounds.");
        await call("editor_ai_import", new() { ["requestId"] = requestId, ["path"] = resultPath }, true, false);
        await call("editor_ai_accept", new() { ["requestId"] = requestId }, true, false);
        Require(scene.Snapshot.Manifest.Width == 13 && scene.Snapshot.Manifest.Height == 7
            && new CanvasCompositor(scene.Snapshot).Render().Pixels[(1 * 13 + 2) * 4] == 255, "Agent expansion replaced protected original pixels.");
        await call("editor_history", new() { ["action"] = "undo" }, true, false);
        Require(scene.Snapshot.Manifest.Width == 8 && scene.Snapshot.Manifest.Height == 6 && !scene.CanUndo
            && handler.Calls == requests, "Agent expansion lost Undo or sent a cloud request.");
    }
}
#endif
