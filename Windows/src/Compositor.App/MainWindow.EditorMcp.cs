using Compositor.App.Ai;
using Compositor.App.Automation;
using Compositor.Core;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private EditorPipeServer? editorPipe;
    private EditorDocument? editorTrackedDocument;
    private long editorRevision;
    private string editorDocumentToken = "";

    private void StartEditorMcp()
    {
        editorPipe = new(request =>
        {
            var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!DispatcherQueue.TryEnqueue(async () =>
            {
                try { completion.TrySetResult(await HandleEditorToolAsync(request)); }
                catch (Exception ex)
                {
                    string message = ex is ArgumentException or InvalidOperationException or JsonException or IOException
                        ? ex.Message : "The editor operation failed. Read editor_state before retrying.";
                    completion.TrySetResult(EditorResult(new { error = message }, true));
                }
            })) completion.TrySetResult(EditorResult(new { error = "The editor is closing." }, true));
            return completion.Task;
        });
        _ = editorPipe.RunAsync();
    }

    private void EditorDocumentChanged() => editorRevision++;
    private void TrackEditorDocument(EditorDocument? current)
    {
        if (ReferenceEquals(editorTrackedDocument, current)) return;
        StopMcpActivity(); lastMcpActivity = 0;
        if (editorTrackedDocument is { } old)
        { old.Changed -= EditorDocumentChanged; old.PixelsChanged -= EditorDocumentChanged; old.SelectionChanged -= EditorDocumentChanged; old.LayerLocksChanged -= EditorDocumentChanged; }
        editorTrackedDocument = current;
        editorDocumentToken = Guid.NewGuid().ToString("N"); editorRevision++;
        if (current is not null)
        { current.Changed += EditorDocumentChanged; current.PixelsChanged += EditorDocumentChanged; current.SelectionChanged += EditorDocumentChanged; current.LayerLocksChanged += EditorDocumentChanged; }
    }

    private JsonObject EditorState()
    {
        TrackEditorDocument(document);
        var owner = document!;
        var manifest = owner.Snapshot.Manifest;
        return (JsonObject)JsonSerializer.SerializeToNode(new
        {
            appPid = Environment.ProcessId, documentToken = editorDocumentToken, revision = editorRevision,
            documentId = manifest.DocumentId, path = projectPath, width = manifest.Width, height = manifest.Height,
            formatVersion = manifest.Version, activeLayerId = manifest.ActiveLayerId,
            selectedLayerIds = manifest.Layers.Where(layer => selectedLayerIds.Contains(layer.Id)).Select(layer => layer.Id),
            dirty = owner.IsDirty, canUndo = owner.CanUndo, canRedo = owner.CanRedo, busy = ProjectEditsInFlight,
            timeline = manifest.Timeline is null ? null : new { durationFrames = manifest.Timeline.DurationFrames,
                framesPerSecond = manifest.Timeline.FramesPerSecond, loop = manifest.Timeline.Loop,
                currentFrame = timelineFrame, preview = TimelinePreviewActive, playing = timelinePlaying,
                tracks = JsonSerializer.SerializeToNode(manifest.Timeline.Tracks, ManifestJson.Options) },
            selection = owner.ActiveSelection is { } selection ? new { x = selection.Bounds.X, y = selection.Bounds.Y, width = selection.Bounds.Width, height = selection.Bounds.Height } : null,
            layers = manifest.Layers.Select(layer => new { id = layer.Id, name = layer.Name, visible = layer.IsVisible, opacity = layer.OpacityOrOne,
                parentId = layer.ParentId, blendMode = layer.BlendMode ?? LayerBlendMode.Normal,
                blendModeName = RawEnumText.Resolve<LayerBlendMode>((layer.BlendMode ?? LayerBlendMode.Normal).ToString()),
                kind = layer.IsGroupOrFalse ? "folder" : layer.Adjustment is not null ? "adjustment" : layer.Text is not null ? "text" : layer.Shape is not null ? "shape" : "raster",
                hasMask = owner.Snapshot.Masks.ContainsKey(layer.Id), locks = owner.GetLayerLocks(layer.Id).ToString(), ownLocks = owner.GetLayerLocks(layer.Id, inherited: false).ToString(),
                mask = owner.Snapshot.Masks.TryGetValue(layer.Id, out var rasterMask) ? new { width = rasterMask.Width, height = rasterMask.Height,
                    enabled = layer.MaskEnabled != false, linked = layer.MaskLinked != false,
                    placement = JsonSerializer.SerializeToNode(layer.MaskPlacement ?? layer.Transform, ManifestJson.Options) } : null,
                text = layer.Text is null ? null : JsonSerializer.SerializeToNode(layer.Text, ManifestJson.Options),
                adjustment = layer.Adjustment is null ? null : JsonSerializer.SerializeToNode(layer.Adjustment, ManifestJson.Options),
                x = layer.Transform.Origin.X, y = layer.Transform.Origin.Y, width = layer.Transform.Size.Width, height = layer.Transform.Size.Height, rotation = layer.Transform.Rotation }),
            ai = new { status = aiEditor?.Status ?? "idle", stage = aiEditor?.Stage ?? "idle", requestId = aiEditor?.RequestId, candidates = aiEditor?.Candidates.Count ?? 0,
                source = aiEditor is null ? null : aiEditor.External ? "agent" : "editor",
                input = aiEditor is { External: true, Before: not null, InputDirectory: { } inputDirectory, Edit: { } agentEdit }
                    ? new { imagePath = Path.Combine(inputDirectory, "input.png"), maskPath = Path.Combine(inputDirectory, "mask.png"),
                        width = agentEdit.Width, height = agentEdit.Height, left = agentEdit.Left, top = agentEdit.Top,
                        maskConvention = "transparent=edit, opaque=protect", resultLayout = "fullCanvas" } : null },
            objectSelection = new { installed = ObjectSelectionModels.IsInstalled, backend = objectSegmenter?.Backend ?? "unloaded",
                preview = objectSelectionDraft is not null, working = objectSelectionDraft?.Busy == true || objectModelInstalling },
        })!;
    }

    private static JsonObject EditorResult(object value, bool error = false)
    {
        var node = value as JsonNode ?? JsonSerializer.SerializeToNode(value)!;
        return new() { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = node.ToJsonString() }), ["isError"] = error };
    }

    private void RequireEditorTarget(JsonObject args)
    {
        TrackEditorDocument(document);
        if (args["documentToken"]?.GetValue<string>() != editorDocumentToken || args["revision"]?.GetValue<long>() != editorRevision)
            throw new InvalidOperationException("The document or revision changed. Read editor_state and review the current canvas before editing.");
    }

    private void RequireEditorIdle(bool allowAi = false, bool allowTimeline = false)
    {
        bool ready = CanEditSelection || allowTimeline && TimelinePreviewActive && CanEditSelectionBaseState && !renderInputs.HasPending && !timelinePlaying;
        if (windowClosed || document is null || ProjectEditsInFlight || !ready || lassoPoints is not null || dragStart is not null
            || cropRectangle is not null || !allowAi && aiEditor is not null)
            throw new InvalidOperationException("The editor has an active edit, dialog or AI preview. Finish or cancel it before this operation.");
    }

    private async Task<JsonObject> HandleEditorToolAsync(JsonObject request)
    {
        string name = request["tool"]?.GetValue<string>() ?? throw new ArgumentException("Missing tool.");
        var args = request["arguments"] as JsonObject ?? new();
        if (windowClosed || document is null) throw new InvalidOperationException("The editor is closing or has no document.");
        if (name == "editor_state") return EditorResult(EditorState());
        if (name == "editor_preview") return await EditorPreviewAsync(args);
        RequireEditorTarget(args);
        var owner = document;
        if (name == "editor_timeline") return await EditorTimelineAsync(args, owner);
        if (name is "editor_select" or "editor_batch" or "editor_text" or "editor_adjustment" or "editor_mask" or "editor_layout")
            return await EditorAutomationAsync(name, args, owner);
        if (name.StartsWith("editor_layer_", StringComparison.Ordinal))
        {
            await HandleEditorLayerToolAsync(name, args, owner);
            SetStatus("MCP · " + name);
            return EditorResult(EditorState());
        }
        switch (name)
        {
            case "editor_ai_prepare":
                await PrepareAgentImageAsync(args, owner);
                break;
            case "editor_ai_import":
                await ImportAgentImageAsync(args, owner);
                break;
            case "editor_ai_start":
                RequireEditorIdle();
                string operation = args["operation"]?.GetValue<string>() ?? "";
                AiOperation action = operation switch { "remove" => AiOperation.Remove, "replace" => AiOperation.Replace, "expand" => AiOperation.Expand, _ => throw new ArgumentException("Unknown AI operation.") };
                string model = args["model"]?.GetValue<string>() ?? "";
                string? prompt = args["prompt"]?.GetValue<string>();
                if (prompt?.Length > 4000 || prompt is not null && string.IsNullOrWhiteSpace(prompt) || action == AiOperation.Replace && prompt is null)
                    throw new ArgumentException("A nonempty prompt of at most 4000 characters is required.");
                string connection = args["connection"]?.GetValue<string>() ?? "api";
                if (connection is not ("api" or "account")) throw new ArgumentException("connection must be api or account.");
                string resolution = args["resolution"]?.GetValue<string>() ?? "auto";
                if (model is not (ImageModels.Flare or ImageModels.Sunburst) || resolution != "auto")
                    throw new ArgumentException("Choose an advertised image model and automatic resolution.");
                int left = EditorEdge(args, "left"), right = EditorEdge(args, "right"), top = EditorEdge(args, "top"), bottom = EditorEdge(args, "bottom");
                if (action == AiOperation.Expand && left + right + top + bottom == 0) throw new ArgumentException("Set at least one expansion edge.");
                if (action != AiOperation.Expand && left + right + top + bottom != 0) throw new ArgumentException("Only expand accepts expansion edges.");
                if (action != AiOperation.Expand && owner.ActiveSelection is not { IsEmpty: false }) throw new InvalidOperationException("Select an area before an AI selection edit.");
                if (args["references"] is not null and not JsonArray) throw new ArgumentException("references must be an array.");
                var referenceFiles = args["references"] is JsonArray referenceArray ? referenceArray.Select(item => item is JsonObject reference
                    ? new AiReferenceFile(reference["path"]?.GetValue<string>() ?? "", reference["role"]?.GetValue<string>() ?? "general")
                    : throw new ArgumentException("Each reference needs path and optional role.")).ToArray() : Array.Empty<AiReferenceFile>();
                string preserve = args["preserve"]?.GetValue<string>() ?? "";
                if (referenceFiles.Length > AiReferenceInputs.MaxReferences || referenceFiles.Any(reference => !Path.IsPathFullyQualified(reference.Path) || !AiReferenceInputs.Roles.Contains(reference.Role)) || preserve.Length > 1000)
                    throw new ArgumentException("Use up to four local image references with supported roles and up to 1000 preservation characters.");
                OpenAiEditor(action);
                aiEditor!.Configure!(prompt, model, connection, left, right, top, bottom);
                aiEditor.ConfigureReferences!(referenceFiles, preserve);
                _ = aiEditor.Generate!();
                break;
            case "editor_mask_color":
                RequireEditorIdle();
                Guid maskLayerId = Guid.Parse(args["layerId"]?.GetValue<string>() ?? "");
                if (!owner.Snapshot.Images.TryGetValue(maskLayerId, out var sourceImage) || owner.Snapshot.Masks.ContainsKey(maskLayerId))
                    throw new ArgumentException("Choose a raster layer without an existing mask.");
                if (args["colors"] is not JsonArray colors || colors.Count is < 3 or > 48 || colors.Count % 3 != 0)
                    throw new ArgumentException("colors must contain 1–16 RGB triplets.");
                byte[] include = colors.Select(value => value!.GetValue<int>() is >= 0 and <= 255 and var channel ? (byte)channel : throw new ArgumentException("RGB channels must be 0–255.")).ToArray();
                int fuzziness = args["fuzziness"]?.GetValue<int>() ?? 40;
                if (fuzziness is < 0 or > 200) throw new ArgumentException("fuzziness must be 0–200.");
                var frozenImage = sourceImage.Clone();
                var colorMask = await Task.Run(() =>
                {
                    var mask = new MaskSurface(frozenImage.Width, frozenImage.Height);
                    if (WandPixels.ColorRangeMask(frozenImage, include, [], fuzziness, false, mask) == 0)
                        throw new ArgumentException("No matching color pixels were found; the layer was left unchanged.");
                    return mask;
                });
                RequireEditorTarget(args);
                RequireEditorIdle();
                if (!owner.AddRasterMask(maskLayerId, colorMask)) throw new InvalidOperationException("The layer mask could not be added.");
                break;
            case "editor_trim_layer":
                RequireEditorIdle();
                Guid trimLayerId = Guid.Parse(args["layerId"]?.GetValue<string>() ?? "");
                if (!owner.TrimLayerTransparency(trimLayerId))
                    throw new InvalidOperationException("No transparent margins to trim, or the layer uses editable content, effects or an independent mask.");
                break;
            case "editor_ai_accept":
                RequireEditorIdle(allowAi: true);
                var accepting = RequireEditorAi(args);
                if (accepting.Work is not null || accepting.Status != "ready" || accepting.Edit?.IsCurrent(owner) != true)
                    throw new InvalidOperationException("The AI result is not ready for this document.");
                int index = args["candidateIndex"]?.GetValue<int>() ?? accepting.Candidates.Count - 1;
                if (index < 0 || index >= accepting.Candidates.Count) throw new ArgumentException("candidateIndex is out of range.");
                accepting.SelectCandidate!(index);
                int count = owner.LayerCount;
                CloseFloatingPanel(AiPanelKey, commit: true);
                if (owner.LayerCount != count + 1) throw new InvalidOperationException("The AI result was not accepted.");
                break;
            case "editor_ai_cancel":
                var cancelling = RequireEditorAi(args);
                cancelling.Work?.Cancel();
                if (cancelling.External && cancelling.Status is "preparing" or "running" or "waiting")
                {
                    cancelling.Status = cancelling.Stage = "cancelled";
                    StopAiWorkingEffect();
                    cancelling.RefreshExternal?.Invoke();
                }
                if (args["discard"]?.GetValue<bool>() == true) RetireAiEdit();
                break;
            case "editor_history":
                StopTimelinePlayback(); RequireEditorIdle(allowTimeline: true);
                bool changed = args["action"]?.GetValue<string>() switch { "undo" => ApplyHistory(false), "redo" => ApplyHistory(true), _ => throw new ArgumentException("action must be undo or redo.") };
                if (!changed) throw new InvalidOperationException("No history step is available.");
                break;
            case "editor_save":
                StopTimelinePlayback(); RequireEditorIdle(allowTimeline: true);
                string? destination = args["path"]?.GetValue<string>();
                if (destination is not null)
                {
                    destination = EditorPath(destination, ".comp");
                    if (!string.Equals(destination, projectPath, StringComparison.OrdinalIgnoreCase) && (Directory.Exists(destination) || File.Exists(destination)))
                        throw new ArgumentException("A different existing destination will not be overwritten.");
                    if (!Directory.Exists(Path.GetDirectoryName(destination))) throw new ArgumentException("The destination parent folder must exist.");
                }
                if (destination is null && projectPath is null) throw new ArgumentException("The first save needs an absolute new .comp path.");
                if (!await SaveProjectAsync(false, destination)) throw new InvalidOperationException("The current document was not saved. Read editor_state before retrying.");
                break;
            case "editor_open":
                StopTimelinePlayback(); RequireEditorIdle(allowTimeline: true);
                if (owner.IsDirty) throw new InvalidOperationException("Save the unsaved current document before opening a project.");
                string openPath = EditorPath(args["path"]?.GetValue<string>(), ".comp");
                if (!ProjectStore.IsProjectDirectory(openPath)) throw new ArgumentException("The path is not a .comp project.");
                await OpenProjectAsync(openPath);
                if (!string.Equals(projectPath, openPath, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The project did not open.");
                break;
            case "editor_export":
                StopTimelinePlayback(); RequireEditorIdle(allowTimeline: true);
                string exportPath = EditorPath(args["path"]?.GetValue<string>(), ".png");
                if (File.Exists(exportPath) || !Directory.Exists(Path.GetDirectoryName(exportPath))) throw new ArgumentException("Choose a new PNG in an existing folder.");
                var snapshot = CaptureEditorSnapshot(owner.Snapshot);
                if (TimelinePreviewActive) snapshot = AnimationEvaluator.Evaluate(snapshot, timelineFrame);
                if (args["layerId"] is { } exportLayer) IsolateEditorLayer(snapshot, Guid.Parse(exportLayer.GetValue<string>()));
                await Task.Run(async () =>
                {
                    byte[] png = await WicImaging.EncodePngAsync(new CanvasCompositor(snapshot).Render());
                    using var file = new FileStream(exportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    await file.WriteAsync(png);
                });
                break;
            default: throw new ArgumentException("Unknown live editor tool.");
        }
        SetStatus("MCP · " + name);
        return EditorResult(EditorState());
    }

    private AiEditorSession RequireEditorAi(JsonObject args)
    {
        if (aiEditor is not { } session || string.IsNullOrEmpty(session.RequestId) || args["requestId"]?.GetValue<string>() != session.RequestId)
            throw new InvalidOperationException("The AI request changed or was retired. Read editor_state.");
        return session;
    }

    private static int EditorInt(JsonObject args, string key) => args[key]?.GetValue<int>() ?? throw new ArgumentException("Missing " + key + ".");
    private static int EditorEdge(JsonObject args, string key)
    {
        int value = args[key]?.GetValue<int>() ?? 0;
        return value is >= 0 and <= 8192 ? value : throw new ArgumentException("Expansion edges must be 0–8192.");
    }
    private static string EditorPath(string? value, string extension)
    {
        if (string.IsNullOrEmpty(value) || !Path.IsPathFullyQualified(value) || !value.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("An absolute " + extension + " path is required.");
        return Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar);
    }

    private static ProjectSnapshot CaptureEditorSnapshot(ProjectSnapshot source) => new()
    {
        Manifest = ManifestJson.Deserialize(ManifestJson.Serialize(source.Manifest)),
        Images = new Dictionary<Guid, Surface>(source.Images),
        Masks = new Dictionary<Guid, MaskSurface>(source.Masks),
    };

    private static void IsolateEditorLayer(ProjectSnapshot snapshot, Guid id)
    {
        if (!snapshot.Images.ContainsKey(id)) throw new ArgumentException("Choose a raster layer to isolate.");
        var layer = snapshot.Manifest.Layers.Single(record => record.Id == id);
        layer.ParentId = null;
        snapshot.Manifest.Layers.Clear(); snapshot.Manifest.Layers.Add(layer);
    }

    private readonly List<(string Key, byte[] Png)> editorPreviewCache = [];
    private async Task<JsonObject> EditorPreviewAsync(JsonObject args)
    {
        string view = args["view"]?.GetValue<string>() ?? "canvas";
        int maxSide = args["maxSide"]?.GetValue<int>() ?? 1024;
        if (maxSide is < 64 or > 2048) throw new ArgumentException("maxSide must be 64–2048.");
        if (view is not ("canvas" or "result" or "original" or "layer" or "mask")) throw new ArgumentException("Unknown preview view.");
        if (ProjectEditsInFlight) throw new InvalidOperationException("Finish the active document edit before reading a preview.");
        var state = EditorState();
        string cacheKey = $"{editorDocumentToken}:{editorRevision}:{view}:{args["layerId"]}:{args["region"]}:{maxSide}:{aiEditor?.RequestId}:{aiEditor?.Status}:{aiEditor?.Candidates.Count}:{(TimelinePreviewActive ? timelineFrame : -1)}";
        byte[]? cached = editorPreviewCache.FirstOrDefault(entry => entry.Key == cacheKey).Png;
        if (cached is not null) return EditorPreviewResult(state, view, maxSide, cached, true);
        ProjectSnapshot? snapshot = null;
        MaskSurface? previewMask = null;
        GeneratedImageEdit? edit = null;
        if (view == "mask")
        {
            Guid id = Guid.Parse(args["layerId"]?.GetValue<string>() ?? "");
            if (!document!.CaptureSave().Snapshot.Masks.TryGetValue(id, out previewMask)) throw new ArgumentException("The layer has no raster mask.");
        }
        else if (view is "canvas" or "layer")
        {
            snapshot = EditorPreview.Capture(document!);
            if (TimelinePreviewActive) snapshot = AnimationEvaluator.Evaluate(snapshot, timelineFrame);
            if (view == "layer") IsolateEditorLayer(snapshot, Guid.Parse(args["layerId"]?.GetValue<string>() ?? ""));
        }
        else
        {
            var session = aiEditor ?? throw new InvalidOperationException("No AI preview is open.");
            edit = session.Edit ?? throw new InvalidOperationException("No AI input is captured.");
            if (!edit.IsCurrent(document!)) throw new InvalidOperationException("The AI preview is stale.");
            if (view == "result")
            {
                if (session.Status != "ready" || session.Candidates.Count == 0) throw new InvalidOperationException("No AI result is ready.");
                snapshot = CaptureEditorSnapshot(edit.Preview(session.Candidates[^1].Layer, "AI Preview"));
            }
            else snapshot = EditorPreview.Capture(document!);
        }
        var bytes = await Task.Run(async () =>
        {
            if (previewMask is not null)
            {
                var region = EditorPreviewRegion(args, previewMask.Width, previewMask.Height);
                double scale = Math.Min(1, (double)maxSide / Math.Max(region.Width, region.Height));
                var display = new Surface(Math.Max(1, (int)(region.Width * scale)), Math.Max(1, (int)(region.Height * scale)));
                for (int y = 0; y < display.Height; y++) for (int x = 0; x < display.Width; x++)
                {
                    byte gray = previewMask.Pixels[(region.Y + Math.Min(region.Height - 1, (int)(y / scale))) * previewMask.Width + region.X + Math.Min(region.Width - 1, (int)(x / scale))];
                    int p = y * display.Stride + x * 4; display.Pixels[p] = display.Pixels[p + 1] = display.Pixels[p + 2] = gray; display.Pixels[p + 3] = 255;
                }
                return await WicImaging.EncodePngAsync(display);
            }
            if (view == "original" && edit!.Expands)
            {
                snapshot!.Manifest.Width = edit.Width; snapshot.Manifest.Height = edit.Height;
                foreach (var layer in snapshot.Manifest.Layers)
                {
                    layer.Transform.Origin = new(layer.Transform.Origin.X + edit.Left, layer.Transform.Origin.Y + edit.Top);
                    if (layer.MaskPlacement is { } mask) mask.Origin = new(mask.Origin.X + edit.Left, mask.Origin.Y + edit.Top);
                }
            }
            if (args["region"] is not null)
            {
                var region = EditorPreviewRegion(args, snapshot!.Manifest.Width, snapshot.Manifest.Height);
                snapshot.Manifest.Width = region.Width; snapshot.Manifest.Height = region.Height;
                foreach (var layer in snapshot.Manifest.Layers)
                {
                    layer.Transform.Origin = new(layer.Transform.Origin.X - region.X, layer.Transform.Origin.Y - region.Y);
                    if (layer.MaskPlacement is { } mask) mask.Origin = new(mask.Origin.X - region.X, mask.Origin.Y - region.Y);
                }
            }
            var image = EditorPreview.Render(snapshot!, maxSide);
            return await WicImaging.EncodePngAsync(image);
        });
        editorPreviewCache.Add((cacheKey, bytes));
        while (editorPreviewCache.Count > 4 || editorPreviewCache.Sum(entry => entry.Png.LongLength) > 16 * 1024 * 1024)
            editorPreviewCache.RemoveAt(0);
        return EditorPreviewResult(state, view, maxSide, bytes, false);
    }

    private static RectI EditorPreviewRegion(JsonObject args, int width, int height)
    {
        if (args["region"] is null) return new(0, 0, width, height);
        if (args["region"] is not JsonObject region) throw new ArgumentException("region must be x/y/width/height.");
        int x = EditorInt(region, "x"), y = EditorInt(region, "y"), w = EditorInt(region, "width"), h = EditorInt(region, "height");
        if (x < 0 || y < 0 || w < 1 || h < 1 || (long)x + w > width || (long)y + h > height) throw new ArgumentException("Preview region must lie inside the canvas or mask grid.");
        return new(x, y, w, h);
    }

    private static JsonObject EditorPreviewResult(JsonObject state, string view, int maxSide, byte[] bytes, bool cached)
    {
        var result = EditorResult(new { state, view, maxSide, cached });
        ((JsonArray)result["content"]!).Add(new JsonObject { ["type"] = "image", ["mimeType"] = "image/png", ["data"] = Convert.ToBase64String(bytes) });
        return result;
    }
}
