#if DEBUG
using Compositor.Core;
using System.Text.Json.Nodes;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task VerifyLiveLayerMcpAsync(Func<string, JsonObject?, bool, bool, Task<JsonObject>> call)
    {
        Task<JsonObject> Call(string name, JsonObject? args = null, bool error = false) => call(name, args, name != "editor_state", error);
        var scene = EditorDocument.Create(32, 24); OpenDocument(scene); await WaitForFramesAsync();
        Guid background = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        string root = Environment.GetEnvironmentVariable("COMPOSITOR_PREFERENCES_DIRECTORY")!;
        string pngPath = Path.Combine(root, "MCP 图层.png");
        var pixels = new Surface(4, 6);
        for (int p = 0; p < pixels.Pixels.Length; p += 4)
        { pixels.Pixels[p] = 240; pixels.Pixels[p + 1] = 140; pixels.Pixels[p + 2] = 20; pixels.Pixels[p + 3] = 255; }
        await File.WriteAllBytesAsync(pngPath, await WicImaging.EncodePngAsync(pixels));
        await Call("editor_state");
        await Call("editor_layer_import", new() { ["path"] = pngPath, ["name"] = "Imported star" });
        Guid id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        Require(scene.LayerCount == 2 && id != background && selectedLayerId == id && selectedLayerIds.SetEquals([id])
            && scene.Snapshot.Images[id].Pixels.SequenceEqual(pixels.Pixels), "Live layer import did not select the decoded pixels.");
        await Call("editor_history", new() { ["action"] = "undo" });
        Require(scene.LayerCount == 1 && !scene.CanUndo, "Live import was not one Undo.");
        await Call("editor_history", new() { ["action"] = "redo" });
        paintOnMask = true; maskAloneLayerId = id;
        await Call("editor_layer_select", new() { ["layerId"] = background.ToString() });
        Require(selectedLayerId == background && selectedLayerIds.SetEquals([background]) && !paintOnMask && maskAloneLayerId is null,
            "Live layer selection left the previous mask or native rows active.");
        await Call("editor_layer_select", new() { ["layerId"] = id.ToString() });
        await Call("editor_history", new() { ["action"] = "undo" });
        Require(scene.LayerCount == 1 && !scene.CanUndo, "Selecting a layer created content history.");
        await Call("editor_history", new() { ["action"] = "redo" });

        ProjectLayerRecord Layer() => scene.Snapshot.Manifest.Layers.Single(layer => layer.Id == id);
        async Task OneUndo(string tool, JsonObject args, Func<bool> expected)
        {
            string before = ManifestJson.Serialize(scene.Snapshot.Manifest);
            await Call(tool, args); Require(expected(), tool + " did not apply the requested edit. " + ManifestJson.Serialize(scene.Snapshot.Manifest)
                + $" controls={TransformBox.Width}x{TransformBox.Height} selected={selectedLayerId}");
            string after = ManifestJson.Serialize(scene.Snapshot.Manifest);
            Require(before != after, tool + " did not change the document.");
            await Call("editor_history", new() { ["action"] = "undo" });
            Require(ManifestJson.Serialize(scene.Snapshot.Manifest) == before, tool + " was not one Undo.");
            Require(selectedLayerId == scene.Snapshot.Manifest.ActiveLayerId, tool + " Undo left the wrong layer selected.");
            await Call("editor_history", new() { ["action"] = "redo" });
            Require(ManifestJson.Serialize(scene.Snapshot.Manifest) == after, tool + " did not Redo exactly.");
            Require(selectedLayerId == scene.Snapshot.Manifest.ActiveLayerId, tool + " Redo left the wrong layer selected.");
        }
        JsonObject Target() => new() { ["layerId"] = id.ToString() };
        await OneUndo("editor_layer_rename", new() { ["layerId"] = id.ToString(), ["name"] = "金星" }, () => Layer().Name == "金星");
        await OneUndo("editor_layer_visibility", new() { ["layerId"] = id.ToString(), ["visible"] = false }, () => !Layer().IsVisible);
        await OneUndo("editor_layer_opacity", new() { ["layerId"] = id.ToString(), ["opacity"] = .4 }, () => Layer().Opacity == .4);
        await OneUndo("editor_layer_blend", new() { ["layerId"] = id.ToString(), ["blendMode"] = "Multiply" }, () => Layer().BlendMode == LayerBlendMode.Multiply);
        await Call("editor_layer_visibility", new() { ["layerId"] = id.ToString(), ["visible"] = true });
        var mask = new MaskSurface(4, 6); mask.Pixels.AsSpan().Fill(255); scene.AddRasterMask(id, mask);
        Layer().MaskPlacement = new() { Origin = new(Layer().Transform.Origin.X + 1, Layer().Transform.Origin.Y + 1), Size = new(4, 6) };
        await Call("editor_state");
        var originalPixels = scene.Snapshot.Images[id];
        await OneUndo("editor_layer_transform", new() { ["layerId"] = id.ToString(), ["x"] = 5, ["y"] = 6, ["width"] = 8, ["height"] = 12, ["rotation"] = 30 },
            () => Layer().Transform.Origin == new PointD(5, 6) && Layer().Transform.Size == new SizeD(8, 12)
                && Layer().Transform.Rotation == 30 && Math.Abs(Layer().MaskPlacement!.Size.Width - 8) < 1e-9
                && ReferenceEquals(originalPixels, scene.Snapshot.Images[id]) && TransformBox.Width == 8);
        await OneUndo("editor_layer_reorder", new() { ["layerId"] = id.ToString(), ["position"] = "bottom" },
            () => scene.Snapshot.Manifest.Layers[0].Id == id);
        await OneUndo("editor_layer_reorder", new() { ["layerId"] = id.ToString(), ["position"] = "above", ["aboveLayerId"] = background.ToString() },
            () => scene.Snapshot.Manifest.Layers[^1].Id == id);
        int count = scene.LayerCount;
        await OneUndo("editor_layer_duplicate", Target(), () => scene.LayerCount == count + 1 && scene.Snapshot.Manifest.ActiveLayerId != id
            && ReferenceEquals(scene.Snapshot.Images[scene.Snapshot.Manifest.ActiveLayerId!.Value], originalPixels));
        Guid copy = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        await OneUndo("editor_layer_delete", new() { ["layerId"] = copy.ToString() }, () => scene.LayerCount == count && !scene.Snapshot.Images.ContainsKey(copy));

        string stable = ManifestJson.Serialize(scene.Snapshot.Manifest);
        foreach (var invalid in new (string Tool, JsonObject Args)[]
        {
            ("editor_layer_transform", new() { ["layerId"] = id.ToString(), ["width"] = 0 }),
            ("editor_layer_transform", new() { ["layerId"] = id.ToString(), ["x"] = "bad" }),
            ("editor_layer_opacity", new() { ["layerId"] = id.ToString(), ["opacity"] = 2 }),
            ("editor_layer_blend", new() { ["layerId"] = id.ToString(), ["blendMode"] = "invalid" }),
            ("editor_layer_rename", new() { ["layerId"] = id.ToString(), ["name"] = " " }),
            ("editor_layer_select", new() { ["layerId"] = Guid.NewGuid().ToString() }),
            ("editor_layer_reorder", new() { ["layerId"] = id.ToString(), ["position"] = "above", ["aboveLayerId"] = id.ToString() }),
            ("editor_layer_import", new() { ["path"] = Path.Combine(root, "missing.png") }),
        })
        {
            await Call(invalid.Tool, invalid.Args, error: true);
            Require(ManifestJson.Serialize(scene.Snapshot.Manifest) == stable, "Invalid live layer arguments changed the document.");
        }
        await Call("editor_state");
        var stale = EditorState();
        await Call("editor_layer_opacity", new() { ["layerId"] = id.ToString(), ["opacity"] = .5 });
        await call("editor_layer_delete", new() { ["layerId"] = id.ToString(), ["documentToken"] = stale["documentToken"]!.DeepClone(), ["revision"] = stale["revision"]!.DeepClone() }, false, true);
        Require(scene.Snapshot.Images.ContainsKey(id), "A stale layer deletion reached the current document.");
        scene.SetSelection(Selection.Rectangle(32, 24, 0, 0, 4, 4));
        await Call("editor_state");
        OpenAiEditor(AiOperation.Remove);
        await Call("editor_layer_delete", Target(), error: true);
        RetireAiEdit();
        await Call("editor_state");
        string saved = Path.Combine(root, "live-layers.comp");
        await Call("editor_save", new() { ["path"] = saved });
        Require(new CanvasCompositor((await ProjectStore.LoadAsync(saved))).Render().Pixels
            .SequenceEqual(new CanvasCompositor(scene.Snapshot).Render().Pixels), "Live layer edits did not survive save/reload.");

        scene = EditorDocument.Create(32, 24); scene.AddFolder();
        Guid folder = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        OpenDocument(scene); await WaitForFramesAsync(); await Call("editor_state");
        await Call("editor_layer_import", new() { ["path"] = pngPath });
        Guid child = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        Require(scene.Snapshot.Manifest.Layers.Single(layer => layer.Id == child).ParentId == folder, "PNG import lost the active folder.");
        collapsedGroups.Add(folder);
        await Call("editor_layer_select", new() { ["layerId"] = child.ToString() });
        Require(!collapsedGroups.Contains(folder), "Selecting a nested layer did not reveal its row.");
        await OneUndo("editor_layer_reorder", new() { ["layerId"] = child.ToString(), ["parentId"] = null, ["position"] = "bottom" },
            () => scene.Snapshot.Manifest.Layers[0].Id == child && scene.Snapshot.Manifest.Layers[0].ParentId is null);
        await OneUndo("editor_layer_reorder", new() { ["layerId"] = child.ToString(), ["parentId"] = folder.ToString(), ["position"] = "top" },
            () => scene.Snapshot.Manifest.Layers.Single(layer => layer.Id == child).ParentId == folder);
        await OneUndo("editor_layer_transform", new() { ["layerId"] = folder.ToString(), ["x"] = 2, ["y"] = 3 },
            () => scene.Snapshot.Manifest.Layers.Single(layer => layer.Id == child).Transform.Origin == new PointD(2, 3));
        await Call("editor_layer_reorder", new() { ["layerId"] = folder.ToString(), ["parentId"] = folder.ToString(), ["position"] = "top" }, error: true);
        await OneUndo("editor_layer_duplicate", new() { ["layerId"] = folder.ToString() }, () => scene.LayerCount == 5
            && scene.Snapshot.Manifest.Layers.Any(layer => layer.ParentId == scene.Snapshot.Manifest.ActiveLayerId));
        Guid folderCopy = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        await OneUndo("editor_layer_delete", new() { ["layerId"] = folderCopy.ToString() }, () => scene.LayerCount == 3
            && scene.Snapshot.Manifest.Layers.All(layer => layer.ParentId != folderCopy));

        scene = EditorDocument.Create(8, 6); Guid source = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        var consumer = scene.ImportImage(pixels, "Clipped"); consumer.MaskSourceId = source;
        scene = new(scene.Snapshot); OpenDocument(scene); await WaitForFramesAsync(); await Call("editor_state");
        await Call("editor_layer_delete", new() { ["layerId"] = source.ToString() }, error: true);
        Require(scene.LayerCount == 2 && !scene.CanUndo && !scene.IsDirty
            && scene.Snapshot.Manifest.Layers.Single(layer => layer.Id == consumer.Id).MaskSourceId == source,
            "Deleting a live source silently removed a dependency or created history.");
    }
}
#endif
