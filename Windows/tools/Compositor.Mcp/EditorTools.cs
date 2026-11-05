using Compositor.Core;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace Compositor.Mcp;

public static partial class Program
{
    private static IEnumerable<JsonObject> EditorTools()
    {
        static JsonObject Field(string type, string description) => new() { ["type"] = type, ["description"] = description };
        static JsonObject Choice(params string[] values) => new() { ["type"] = "string", ["enum"] = new JsonArray(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()) };
        static JsonObject Tool(string name, string description, JsonObject fields, string[] required, bool mutates = false, bool network = false)
        {
            fields["appPid"] = Field("integer", "Editor PID from editor_list. May be omitted when exactly one editor is running.");
            if (mutates)
            {
                fields["documentToken"] = Field("string", "Exact token from the latest editor_state; protects the currently shown document.");
                fields["revision"] = Field("integer", "Exact revision from editor_state; stale edits are refused. Read state again after every mutation.");
                required = [.. required, "documentToken", "revision"];
            }
            return new()
            {
                ["name"] = name, ["description"] = description,
                ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = fields,
                    ["required"] = new JsonArray(required.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()), ["additionalProperties"] = false },
                ["annotations"] = new JsonObject { ["readOnlyHint"] = !mutates && !network, ["destructiveHint"] = mutates,
                    ["idempotentHint"] = !mutates && !network, ["openWorldHint"] = network },
            };
        }
        yield return Tool("editor_list", "List running Compositor editor processes. Live editor tools operate on unsaved in-memory documents and preserve normal undo.", new(), []);
        yield return Tool("editor_state", "Read the current live document, selection, layers, undo availability and AI job status. No upload. Poll this after editor_ai_start.", new(), []);
        yield return Tool("editor_preview", "Return a bounded PNG as an MCP image of the live canvas, AI result, or original input. No cloud request or disk write.",
            new() { ["view"] = Choice("canvas", "result", "original", "layer", "mask"), ["layerId"] = Field("string", "Layer UUID for view=layer or mask"), ["maxSide"] = Field("integer", "Maximum preview side, 64–2048; default 1024."),
                ["region"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["x"] = Field("integer", "Left pixel"), ["y"] = Field("integer", "Top pixel"), ["width"] = Field("integer", "Region width"), ["height"] = Field("integer", "Region height") },
                    ["required"] = new JsonArray("x", "y", "width", "height"), ["additionalProperties"] = false, ["description"] = "Optional detail crop in document pixels; view=mask uses mask-grid pixels instead." } }, []);
        yield return Tool("editor_mask", "Refine a layer's editable raster mask in one Undo without changing source image pixels. Worker preparation rechecks the target before publication. Paint uses the native mask brush in document coordinates; filters preserve grayscale and mask placement. Honors the active selection unless respectSelection=false. A disabled mask must be enabled before painting. Use preview view=mask/layer and optional region to inspect details.",
            new() { ["layerId"] = Field("string", "Layer UUID"), ["operation"] = Choice("paint", "feather", "expand", "contract", "invert", "import", "enable", "disable", "remove"),
                ["points"] = new JsonObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 4096, ["items"] = new JsonObject { ["type"] = "array", ["minItems"] = 2, ["maxItems"] = 2, ["items"] = new JsonObject { ["type"] = "number" } } },
                ["radius"] = Field("number", "Brush radius in document pixels, .5–250, default 10"), ["hardness"] = Field("number", "Brush hardness 0–1, default .8"),
                ["opacity"] = Field("number", "Brush opacity, greater than 0 through 1, default 1"), ["value"] = Field("integer", "Gray tone: 0 hides, 255 reveals; default 255"),
                ["amount"] = Field("integer", "Feather or square grow/shrink radius, 1–250 mask pixels"), ["path"] = Field("string", "Absolute opaque grayscale PNG matching the current mask grid, or layer image grid for a new mask"),
                ["combine"] = Choice("replace", "add", "subtract"), ["respectSelection"] = Field("boolean", "Constrain pixel changes to the active selection, default true") }, ["layerId", "operation"], true);
        yield return Tool("editor_mask_color", "Reveal only matching colors on a raster layer using the editor's Color Range algorithm and a new editable mask. Preserves source pixels, refuses an existing mask, and adds one undo step.",
            new() { ["layerId"] = Field("string", "Raster layer UUID"), ["colors"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 255 }, ["minItems"] = 3, ["maxItems"] = 48, ["description"] = "Flat RGB triplets to reveal, such as [230,180,50]" },
                ["fuzziness"] = Field("integer", "Per-channel color tolerance, 0–200; default 40") }, ["layerId", "colors"], true);
        yield return Tool("editor_trim_layer", "Crop transparent margins from a raster layer and its attached mask, preserving its position and scale in one undo step. Refuses editable text/shapes, effects and independent masks. Does not save or generate.",
            new() { ["layerId"] = Field("string", "Raster layer UUID") }, ["layerId"], true);
        yield return Tool("editor_layer_select", "Select one live layer, reveal its row and target its content. Clears mask/effect targeting; creates no content undo step.",
            new() { ["layerId"] = Field("string", "Layer UUID") }, ["layerId"], true);
        yield return Tool("editor_layer_import", "Import a local PNG as a selected layer, centered on the canvas, in one undo step. Uses the current folder and document pixel budget. Does not save.",
            new() { ["path"] = Field("string", "Absolute existing PNG path"), ["name"] = Field("string", "Optional layer name; defaults to the filename") }, ["path"], true);
        yield return Tool("editor_layer_transform", "Move, resize or rotate a visible layer or folder in one undo step using the editor's normal transform operation. Omitted fields retain their current values. Coordinates are unrotated bounds in document pixels; clockwise rotation is around their center. Linked masks follow.",
            new() { ["layerId"] = Field("string", "Layer UUID"), ["x"] = Field("number", "Left pixel, -1000000 to 1000000"),
                ["y"] = Field("number", "Top pixel, -1000000 to 1000000"), ["width"] = Field("number", "Width, 1 to 300000"),
                ["height"] = Field("number", "Height, 1 to 300000"), ["rotation"] = Field("number", "Clockwise angle in degrees") }, ["layerId"], true);
        yield return Tool("editor_layer_reorder", "Place a layer or folder at the top/bottom of a folder, or immediately above a sibling, in one undo step. Omitted parentId keeps the current parent; null means the root. Refuses cycles.",
            new() { ["layerId"] = Field("string", "Layer UUID"), ["position"] = Choice("top", "bottom", "above"),
                ["parentId"] = new JsonObject { ["type"] = new JsonArray("string", "null"), ["description"] = "Destination folder UUID, or null for root; omit to keep current parent" },
                ["aboveLayerId"] = Field("string", "Required sibling UUID for position=above only") }, ["layerId", "position"], true);
        yield return Tool("editor_layer_duplicate", "Duplicate one layer, including a folder's descendants, and select the copy in one undo step. Preserves editable content and masks. Does not save.",
            new() { ["layerId"] = Field("string", "Layer UUID") }, ["layerId"], true);
        yield return Tool("editor_layer_delete", "Delete a layer and its descendants in one undo step. Refuses surviving clipping/live-mask dependents rather than silently changing their appearance. Does not save.",
            new() { ["layerId"] = Field("string", "Layer UUID") }, ["layerId"], true);
        yield return Tool("editor_layer_rename", "Rename one live layer in one undo step.",
            new() { ["layerId"] = Field("string", "Layer UUID"), ["name"] = Field("string", "Nonempty layer name, at most 512 characters") }, ["layerId", "name"], true);
        yield return Tool("editor_layer_visibility", "Show or hide one live layer in one undo step.",
            new() { ["layerId"] = Field("string", "Layer UUID"), ["visible"] = Field("boolean", "Whether to show the layer") }, ["layerId", "visible"], true);
        yield return Tool("editor_layer_opacity", "Set live layer opacity from 0 to 1 in one undo step.",
            new() { ["layerId"] = Field("string", "Layer UUID"), ["opacity"] = Field("number", "Opacity, 0 to 1") }, ["layerId", "opacity"], true);
        yield return Tool("editor_layer_blend", "Set the blend mode of a live non-folder layer in one undo step.",
            new() { ["layerId"] = Field("string", "Layer UUID"), ["blendMode"] = Choice(Enum.GetValues<LayerBlendMode>().Select(mode => RawEnumText.Resolve<LayerBlendMode>(mode.ToString())).ToArray()) }, ["layerId", "blendMode"], true);
        yield return Tool("editor_select", "Set or combine an undoable rectangle, ellipse, polygon or opaque grayscale PNG mask selection. Black protects; white selects. Modes all/clear replace the selection; feather adds 0–250 pixels of feather to the current selection. Worker preparation preserves the native outline renderer.",
            new() { ["mode"] = Choice("rectangle", "ellipse", "polygon", "mask", "feather", "all", "clear"), ["x"] = Field("integer", "Left pixel"), ["y"] = Field("integer", "Top pixel"),
                ["width"] = Field("integer", "Positive width"), ["height"] = Field("integer", "Positive height"), ["combine"] = Choice("replace", "add", "subtract"),
                ["feather"] = Field("integer", "Feather radius, 0–250"), ["path"] = Field("string", "Absolute canvas-sized opaque grayscale PNG for mode=mask"),
                ["points"] = new JsonObject { ["type"] = "array", ["minItems"] = 3, ["maxItems"] = 4096, ["items"] = new JsonObject { ["type"] = "array", ["minItems"] = 2, ["maxItems"] = 2, ["items"] = new JsonObject { ["type"] = "number" } } } }, ["mode"], true);
        yield return Tool("editor_text", "Create editable text, or update an existing text layer, as one undo step. Creation requires content; layerId targets existing text. Colors are sRGB 0–1. Existing text placement is preserved; use layer_transform to move it.",
            new() { ["layerId"] = Field("string", "Existing text layer UUID; omit to create"), ["content"] = Field("string", "Text, up to 100000 UTF-16 characters"),
                ["fontName"] = Field("string", "Installed font family, up to 200 characters"), ["fontSize"] = Field("number", "Size, 1–2000 pixels"),
                ["x"] = Field("number", "New text origin X"), ["y"] = Field("number", "New text origin Y"), ["alignment"] = Choice("Left", "Center", "Right"),
                ["tracking"] = Field("number", "Tracking, -100–1000"), ["leading"] = Field("number", "Line height, 0 auto or up to 5000"),
                ["boxWidth"] = Field("number", "Fixed text box width, 16–30000"), ["boxHeight"] = Field("number", "Fixed text box height, 16–30000"),
                ["color"] = new JsonObject { ["type"] = "array", ["minItems"] = 3, ["maxItems"] = 3, ["items"] = new JsonObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1 } } }, [], true);
        yield return Tool("editor_adjustment", "Create or edit an adjustment layer in one undo step. Omit layerId and supply kind to create. settings is a partial object using the exact adjustment field names returned by editor_state; nested fields preserve omitted values. All parameters are validated before publication.",
            new() { ["layerId"] = Field("string", "Existing adjustment layer UUID; omit to create"),
                ["kind"] = Choice(Enum.GetValues<AdjustmentKind>().Select(kind => RawEnumText.Resolve<AdjustmentKind>(kind.ToString())).ToArray()),
                ["settings"] = new JsonObject { ["type"] = "object", ["description"] = "Partial LayerAdjustment fields, for example {exposureSettings:{exposure:1,gamma:1}}. kind is supplied separately." } }, [], true);
        yield return Tool("editor_layout", "Group, ungroup, align, distribute or crop as one Undo. layerIds are 1–256 distinct UUIDs; selected folder descendants are not counted twice. Align/distribute use rotated placement bounds without effects, including hidden folder content. Linked masks move; independent masks stay fixed. Ungroup discards the folder's own opacity/blend/mask, like the native command. Crop retains source pixels and shifts guides, masks and selection.",
            new() { ["operation"] = Choice("group", "ungroup", "align", "distribute", "crop"),
                ["layerIds"] = new JsonObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 256, ["uniqueItems"] = true, ["items"] = Field("string", "Layer UUID or earlier batch $alias") },
                ["name"] = Field("string", "Optional new group name, 1–512 characters"),
                ["alignment"] = Choice("left", "center", "right", "top", "middle", "bottom"), ["relativeTo"] = Choice("layers", "canvas"),
                ["axis"] = Choice("horizontal", "vertical"), ["spacing"] = Choice("gaps", "centers"),
                ["x"] = Field("integer", "Crop left, inside the canvas"), ["y"] = Field("integer", "Crop top, inside the canvas"),
                ["width"] = Field("integer", "Positive crop width, inside the canvas"), ["height"] = Field("integer", "Positive crop height, inside the canvas") }, ["operation"], true);
        yield return Tool("editor_batch", "Apply 1–32 local layer/text/adjustment/selection/layout commands atomically as ONE undo step. Failure publishes nothing and preserves redo. Supports the editor_layer tools except import, plus editor_text, editor_adjustment, editor_layout and geometric editor_select. No cloud, save or nested batches. Use as to name an earlier result, then layerId/parentId/aboveLayerId or layerIds entries='$name'. Returns batchAliases.",
            new() { ["commands"] = new JsonObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 32, ["items"] = new JsonObject { ["type"] = "object",
                ["properties"] = new JsonObject { ["tool"] = Field("string", "Local editing tool name"), ["arguments"] = new JsonObject { ["type"] = "object" }, ["as"] = Field("string", "Optional unique result alias") },
                ["required"] = new JsonArray("tool", "arguments"), ["additionalProperties"] = false } } }, ["commands"], true);
        yield return Tool("editor_ai_start", "Start one image-edit request using the editor's saved custom API connection or explicitly selected account. Uploads the canvas, selection and prompt; may incur provider charges. Returns a requestId immediately. Poll editor_state; preview before accepting. Never retries or applies automatically.",
            new() { ["operation"] = Choice("remove", "replace", "expand"), ["prompt"] = Field("string", "Requested image edit; required for replace"),
                ["model"] = Choice("gpt-image-2.5-sunburst", "gpt-image-2.5-flare"), ["connection"] = Choice("api", "account"),
                ["resolution"] = Choice("auto"),
                ["preserve"] = Field("string", "Features the model should retain, up to 1000 characters. A semantic request, not a guaranteed identity lock."),
                ["references"] = new JsonObject { ["type"] = "array", ["maxItems"] = 4, ["items"] = new JsonObject { ["type"] = "object",
                    ["properties"] = new JsonObject { ["path"] = Field("string", "Absolute local image path, up to 14 MiB and 16 megapixels; resized to at most 1600 pixels before upload"),
                        ["role"] = Choice("general", "identity", "clothing", "style", "composition") }, ["required"] = new JsonArray("path"), ["additionalProperties"] = false } },
                ["left"] = Field("integer", "Expansion pixels, 0–8192"), ["right"] = Field("integer", "Expansion pixels, 0–8192"),
                ["top"] = Field("integer", "Expansion pixels, 0–8192"), ["bottom"] = Field("integer", "Expansion pixels, 0–8192") }, ["operation", "model"], true, true);
        yield return Tool("editor_ai_accept", "Accept the selected AI candidate as one ordinary layer and one undo step. Requires the exact ready requestId; does not generate or save.",
            new() { ["requestId"] = Field("string", "Ready AI request ID"), ["candidateIndex"] = Field("integer", "Zero-based candidate; default latest") }, ["requestId"], true);
        yield return Tool("editor_ai_cancel", "Cancel local waiting for the matching AI job, or discard its preview. Does not claim to cancel server billing.",
            new() { ["requestId"] = Field("string", "AI request ID"), ["discard"] = Field("boolean", "Also close the preview; default false") }, ["requestId"], true);
        yield return Tool("editor_history", "Undo or redo exactly one operation in the live document. Refuses active drafts and requests.", new() { ["action"] = Choice("undo", "redo") }, ["action"], true);
        yield return Tool("editor_save", "Save the live document using the editor's captured, conflict-checked save flow. Existing projects need no path. A first save requires an absolute new .comp directory path. Does not open a folder picker.",
            new() { ["path"] = Field("string", "Optional new .comp directory; must not overwrite a different existing project") }, [], true);
        yield return Tool("editor_open", "Open a saved .comp in the editor using normal project ownership. Refuses an unsaved current document or active draft; save it first.", new() { ["path"] = Field("string", "Absolute .comp directory") }, ["path"], true);
        yield return Tool("editor_export", "Export the live committed canvas, or one raster layer on transparency, to a PNG without saving the project. The destination folder must exist; refuses an existing destination.", new() { ["path"] = Field("string", "Absolute new .png file"), ["layerId"] = Field("string", "Optional raster layer UUID to isolate") }, ["path"], true);
    }

    private static async Task<JsonObject> CallEditorAsync(string name, JsonObject arguments)
    {
        if (!EditorTools().Any(tool => tool["name"]!.GetValue<string>() == name)) throw new ArgumentException($"Unknown editor tool '{name}'.");
        if (name == "editor_list")
        {
            var editors = new JsonArray();
            foreach (var process in Process.GetProcessesByName("Compositor.App"))
            {
                using (process) editors.Add(new JsonObject { ["appPid"] = process.Id, ["title"] = process.MainWindowTitle });
            }
            return TextResult(new JsonObject { ["editors"] = editors }.ToJsonString());
        }
        int pid;
        if (arguments["appPid"] is { } target) pid = target.GetValue<int>();
        else
        {
            var editors = Process.GetProcessesByName("Compositor.App");
            try
            {
                if (editors.Length != 1) return TextResult("Call editor_list and supply appPid: exactly one running editor is required for automatic selection.", true);
                pid = editors[0].Id;
            }
            finally { foreach (var editor in editors) editor.Dispose(); }
        }
        if (pid <= 0) throw new ArgumentException("appPid must be positive.");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var pipe = new NamedPipeClientStream(".", $"Compositor.Editor.{pid}", PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(2000, timeout.Token);
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(new JsonObject { ["tool"] = name, ["arguments"] = arguments.DeepClone() }.ToJsonString().AsMemory(), timeout.Token);
            string response = await reader.ReadLineAsync(timeout.Token) ?? throw new IOException("Editor closed the connection.");
            return JsonNode.Parse(response) as JsonObject ?? throw new IOException("Editor returned an invalid result.");
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
        {
            return TextResult("The editor connection failed or timed out. Start the current Compositor build and call editor_state to check the outcome before retrying a mutation. " + ex.GetType().Name, true);
        }
    }
}
