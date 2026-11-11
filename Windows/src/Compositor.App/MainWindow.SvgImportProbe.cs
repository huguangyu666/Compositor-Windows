#if DEBUG
using Compositor.Core;
using System.Text.Json.Nodes;

namespace Compositor.App;

public sealed partial class MainWindow
{
    internal async Task RunSvgImportProbeAsync()
    {
        var report = new Dictionary<string, object>();
        try
        {
            string root = Environment.GetEnvironmentVariable("COMPOSITOR_PREFERENCES_DIRECTORY")
                ?? throw new InvalidOperationException("Use isolated preferences for the SVG probe.");
            Directory.CreateDirectory(root); await WaitForFramesAsync();
            mcpActivityAnimationsProbe = true;
            const string svg = "<svg viewBox='0 0 100 100'><rect x='20' y='30' width='40' height='20' fill='#ff8000'/></svg>";
            string alphaXml = SvgImport.Prepare("<svg viewBox='0 0 32 24'><rect x='8' y='6' width='16' height='12' fill='red' opacity='.5'/></svg>", 32, 24, 768);
            var placement = new LayerTransform { Size = new(32, 24) };
            var hardware = await Task.Run(() => SvgRasterizer.Render(alphaXml, 32, 24, placement, false, default));
            var software = await Task.Run(() => SvgRasterizer.Render(alphaXml, 32, 24, placement, false, default, true));
            int p = (8 * 32 + 10) * 4;
            report["alphaMeasurement"] = new { hardware = hardware.Image.Pixels.AsSpan(p, 4).ToArray().Select(v => (int)v).ToArray(),
                software = software.Image.Pixels.AsSpan(p, 4).ToArray().Select(v => (int)v).ToArray(), outsideAlpha = software.Image.Pixels[3],
                differingBytes = hardware.Image.Pixels.Zip(software.Image.Pixels).Count(pair => pair.First != pair.Second) };
            // D2D hardware quantizes this .5-opacity fill to 127; WARP quantizes it to 128.
            Require(hardware.Image.Pixels.Zip(software.Image.Pixels).All(pair => Math.Abs(pair.First - pair.Second) <= 1)
                && hardware.Image.Pixels[p] == hardware.Image.Pixels[p + 3] && hardware.Image.Pixels[p + 3] is 127 or 128
                && software.Image.Pixels[3] == 0 && software.Image.Pixels[p] == 128 && software.Image.Pixels[p + 1] == 0
                && software.Image.Pixels[p + 2] == 0 && software.Image.Pixels[p + 3] == 128,
                "SVG alpha, RGBA order or software rendering changed.");
            report["nativeAndSoftwareAlpha"] = "passed";

            var scene = EditorDocument.Create(160, 120); OpenDocument(scene); await WaitForFramesAsync();
            RootGrid.UpdateLayout(); ResizeViewport();
            var creating = Call("editor_layer_import_svg", Input());
            await WaitUntilAsync(() => mcpActivity is not null || creating.IsCompleted, 3000);
            if (creating.IsFaulted) await creating;
            report["feedbackMeasurement"] = new { enabled = McpAnimationToggle.IsChecked, stageWidth = CanvasStage.ActualWidth,
                stageHeight = CanvasStage.ActualHeight, layers = scene.LayerCount, taskStatus = creating.Status.ToString(),
                viewportWidth = viewport.Width, viewportHeight = viewport.Height, zoom = viewport.Zoom,
                originX = viewport.Origin.X, originY = viewport.Origin.Y, windowClosed,
                importedX = scene.Snapshot.Manifest.Layers.Last().Transform.Origin.X, importedY = scene.Snapshot.Manifest.Layers.Last().Transform.Origin.Y };
            Require(mcpActivity is not null, "SVG import has no creation feedback.");
            Require(mcpActivity!.Plan.Target.Origin == new PointD(30.25, 50.5) && mcpActivity.Plan.Target.Size == new SizeD(40, 20)
                && scene.LayerCount == 1 && !scene.CanUndo, "SVG creation bounds differ from the cropped artwork or publish early.");
            await creating; await WaitForFramesAsync(); mcpActivityAnimationsProbe = false;
            Guid id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
            var layer = scene.Snapshot.Manifest.Layers.Single(l => l.Id == id);
            Require(selectedLayerId == id && scene.Snapshot.Images[id].Width == 40 && scene.Snapshot.Images[id].Height == 20
                && layer.Transform.Origin == new PointD(30.25, 50.5) && layer.Transform.Size == new SizeD(40, 20),
                "SVG import did not select/trim/place its result.");
            byte[] original = scene.Snapshot.Images[id].Pixels.ToArray();
            await Call("editor_history", new() { ["action"] = "undo" }); await WaitForFramesAsync();
            Require(scene.LayerCount == 1 && !scene.CanUndo && scene.CanRedo && !scene.IsDirty, "SVG import is not one Undo.");
            await Call("editor_history", new() { ["action"] = "redo" }); await WaitForFramesAsync();
            Require(scene.Snapshot.Images[id].Pixels.SequenceEqual(original), "Redo changed SVG pixels.");
            report["trimPlacementCreationFeedbackAndHistory"] = "passed";

            var full = Input(); full["trim"] = false; full["x"] = 3d; full["y"] = 4d;
            await Call("editor_layer_import_svg", full); await WaitForFramesAsync();
            var fullLayer = scene.Snapshot.Manifest.Layers.Single(l => l.Id == scene.Snapshot.Manifest.ActiveLayerId);
            Require(fullLayer.Transform.Origin == new PointD(3, 4) && fullLayer.Transform.Size == new SizeD(100, 100)
                && scene.Snapshot.Images[fullLayer.Id].Pixels[3] == 0, "Keeping the SVG viewport lost transparent pixels or bounds.");
            report["untrimmedViewport"] = "passed";

            scene = EditorDocument.Create(160, 120); scene.AddFolder();
            Guid folder = scene.Snapshot.Manifest.ActiveLayerId!.Value;
            OpenDocument(scene); await WaitForFramesAsync();
            string path = Path.Combine(root, "图标.svg"); await File.WriteAllTextAsync(path, svg);
            await Call("editor_layer_import_svg", new() { ["path"] = path, ["width"] = 100, ["height"] = 100 });
            id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
            Require(scene.Snapshot.Manifest.Layers.Single(l => l.Id == id).ParentId == folder, "SVG import lost the active folder.");
            string saved = Path.Combine(root, "svg-import.comp");
            await Call("editor_save", new() { ["path"] = saved });
            var reopened = await ProjectStore.LoadAsync(saved);
            Require(reopened.Images[id].Pixels.SequenceEqual(scene.Snapshot.Images[id].Pixels)
                && reopened.Manifest.Layers.Single(l => l.Id == id).Transform.Equals(scene.Snapshot.Manifest.Layers.Single(l => l.Id == id).Transform),
                "SVG layer pixels/placement did not survive save/reopen.");
            report["fileFolderAndSaveReopen"] = "passed";

            var history = scene.CurrentHistoryId; int count = scene.LayerCount;
            foreach (string invalid in new[] { "<svg viewBox='0 0 1 1'><text>unsupported</text></svg>",
                "<svg viewBox='0 0 1 1'><path d='M0 0' fill='url(https://example.test/a)'/></svg>", "<svg viewBox='0 0 1 1'/>" })
            {
                var args = Input(); args["svg"] = invalid;
                await Refused(Call("editor_layer_import_svg", args));
            }
            var both = Input(); both["path"] = path; await Refused(Call("editor_layer_import_svg", both));
            Require(scene.LayerCount == count && scene.CurrentHistoryId == history, "An invalid SVG published history.");
            report["invalidAndInvisibleInputAtomicity"] = "passed";

            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            editorLayerImportPreparationProbe = gate.Task;
            var pending = Call("editor_layer_import_svg", Input());
            OpenDocument(EditorDocument.Create(80, 60)); await WaitForFramesAsync();
            gate.SetResult(); await Refused(pending); editorLayerImportPreparationProbe = null;
            Require(document!.LayerCount == 1 && !document.CanUndo && scene.LayerCount == count, "A stale SVG reached a retired/new document.");
            report["retiredOwnerRefused"] = "passed";

            var current = document;
            gate = new(TaskCreationOptions.RunContinuationsAsynchronously); editorLayerImportPreparationProbe = gate.Task;
            editorLayerImportTimeoutProbe = TimeSpan.FromMilliseconds(25);
            await Refused(Call("editor_layer_import_svg", Input()));
            gate.SetResult(); editorLayerImportPreparationProbe = null; editorLayerImportTimeoutProbe = null;
            await Task.Delay(100); await WaitForFramesAsync();
            Require(current.LayerCount == 1 && !current.CanUndo && mcpActivity is null, "A timed-out SVG published late content or feedback.");
            report["timeoutNoLatePublication"] = "passed";
            report["status"] = "passed";

            JsonObject Input() => new() { ["svg"] = svg, ["width"] = 100, ["height"] = 100, ["x"] = 10.25, ["y"] = 20.5, ["name"] = "SVG tile" };
            async Task<JsonObject> Call(string tool, JsonObject args)
            {
                var state = EditorState(); args["documentToken"] = state["documentToken"]!.DeepClone(); args["revision"] = state["revision"]!.DeepClone();
                var response = await HandleEditorToolAsync(new() { ["tool"] = tool, ["arguments"] = args });
                await WaitForFramesAsync(); return response;
            }
            static async Task Refused(Task<JsonObject> task)
            {
                try { await task; }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OperationCanceledException) { return; }
                throw new InvalidOperationException("Expected SVG rejection.");
            }
        }
        catch (Exception ex) { report["status"] = "failed"; report["error"] = ex.ToString(); }
        finally
        {
            editorLayerImportPreparationProbe = null; editorLayerImportTimeoutProbe = null; mcpActivityAnimationsProbe = null;
            WriteProbeReport("svg-import-probe.json", report); forceClose = true; Close();
        }
    }
}
#endif
