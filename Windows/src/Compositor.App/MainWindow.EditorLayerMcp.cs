using Compositor.Core;
using System.Text.Json.Nodes;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task HandleEditorLayerToolAsync(string name, JsonObject args, EditorDocument owner)
    {
        RequireEditorIdle();
        var timeout = TimeSpan.FromSeconds(20);
#if DEBUG
        timeout = editorLayerImportTimeoutProbe ?? timeout;
#endif
        using var deadline = new CancellationTokenSource(timeout);
        var cancellation = deadline.Token;
        Surface? imported = null;
        LayerTransform? importPlacement = null;
        string? importName = null;
        if (name == "editor_layer_import")
        {
            string path = EditorPath(args["path"]?.GetValue<string>(), ".png");
            importName = EditorLayerName(args["name"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(path));
            long remaining = Limits.DocumentPixelBudget - owner.Snapshot.Images.Values.Sum(image => image.PixelCount)
                - owner.Snapshot.Masks.Values.Sum(mask => mask.PixelCount);
            if (remaining <= 0) throw new ArgumentException("The document has no remaining pixel budget.");
            if ((args["width"] is null) != (args["height"] is null)) throw new ArgumentException("Supply both width and height, or neither.");
            int canvasWidth = owner.Snapshot.Manifest.Width, canvasHeight = owner.Snapshot.Manifest.Height;
            bool trim = args["trim"]?.GetValue<bool>() ?? true;
#if DEBUG
            var preparation = editorLayerImportPreparationProbe;
#endif
            (imported, importPlacement) = await Task.Run(async () =>
            {
#if DEBUG
                if (preparation is not null) await preparation;
#endif
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
                if (file.Length > Limits.AssetMaxBytes) throw new ArgumentException("The PNG exceeds the encoded asset limit.");
                var bytes = new byte[checked((int)file.Length)];
                await file.ReadExactlyAsync(bytes, cancellation);
                var image = await WicImaging.DecodeSurfaceAsync(bytes, Limits.MaxSide, remaining, cancellation);
                double width = EditorLayerNumber(args, "width", image.Width), height = EditorLayerNumber(args, "height", image.Height);
                var placement = new LayerTransform { Origin = new(EditorLayerNumber(args, "x", Math.Floor((canvasWidth - width) / 2)),
                    EditorLayerNumber(args, "y", Math.Floor((canvasHeight - height) / 2))), Size = new(width, height) };
                if (!placement.IsValid) throw new ArgumentException("Invalid PNG placement: finite x/y within ±1000000 and width/height 1–300000 are required.");
                if (trim)
                {
                    try { return GrownLayerGrid.Trim(image, placement); }
                    // A crop can fall below the format's minimum size or exceed its origin limits.
                    catch (InvalidOperationException) { }
                }
                return (image, placement);
            }, cancellation).WaitAsync(cancellation);
        }
        else if (name == "editor_layer_import_svg")
        {
            string? source = args["svg"]?.GetValue<string>();
            string? path = args["path"]?.GetValue<string>();
            if ((source is null) == (path is null)) throw new ArgumentException("Supply exactly one of svg code or an absolute .svg path.");
            if (path is not null) path = EditorPath(path, ".svg");
            importName = EditorLayerName(args["name"]?.GetValue<string>() ?? (path is null ? "SVG" : Path.GetFileNameWithoutExtension(path)));
            int width = args["width"]?.GetValue<int>() ?? 0, height = args["height"]?.GetValue<int>() ?? 0;
            bool trim = args["trim"]?.GetValue<bool>() ?? true;
            var placement = new LayerTransform { Origin = new(EditorLayerNumber(args, "x", Math.Floor((owner.Snapshot.Manifest.Width - width) / 2d)),
                EditorLayerNumber(args, "y", Math.Floor((owner.Snapshot.Manifest.Height - height) / 2d))), Size = new(width, height) };
            if (!placement.IsValid) throw new ArgumentException("Invalid SVG bounds: finite x/y within ±1000000 and positive width/height are required.");
            long remaining = Limits.DocumentPixelBudget - owner.Snapshot.Images.Values.Sum(image => image.PixelCount)
                - owner.Snapshot.Masks.Values.Sum(mask => mask.PixelCount);
#if DEBUG
            var preparation = editorLayerImportPreparationProbe;
#endif
            (imported, importPlacement) = await Task.Run(async () =>
            {
#if DEBUG
                if (preparation is not null) await preparation;
#endif
                if (path is not null)
                {
                    using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
                    if (file.Length > SvgImport.MaxBytes) throw new ArgumentException("SVG files must not exceed 128 KiB.");
                    var bytes = new byte[checked((int)file.Length)];
                    await file.ReadExactlyAsync(bytes, cancellation);
                    try { source = new System.Text.UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
                    catch (System.Text.DecoderFallbackException ex) { throw new ArgumentException("The SVG file must use UTF-8 encoding.", ex); }
                }
                string xml = SvgImport.Prepare(source, width, height, remaining);
                return SvgRasterizer.Render(xml, width, height, placement, trim, cancellation);
            }, cancellation).WaitAsync(cancellation);
        }
        cancellation.ThrowIfCancellationRequested();
        RequireEditorTarget(args); RequireEditorIdle();
        McpActivityPlan? plan = null;
        if (imported is not null)
        {
            var manifest = owner.Snapshot.Manifest;
            plan = new(importPlacement ?? new() { Origin = new(Math.Floor((manifest.Width - imported.Width) / 2d), Math.Floor((manifest.Height - imported.Height) / 2d)),
                Size = new(imported.Width, imported.Height) }, "Mcp_Creating", Creating: true);
        }
        else if (Guid.TryParse(args["layerId"]?.GetValue<string>(), out var targetId)
            && owner.Snapshot.Manifest.Layers.FirstOrDefault(l => l.Id == targetId) is { } target)
        {
            if (name == "editor_layer_transform")
            {
                var edit = owner.BeginTransformDraft([targetId]) ?? throw new InvalidOperationException("Choose a visible layer or folder with drawable content.");
                var initial = edit.Value; var next = CopyTransform(initial);
                next.Origin = new(EditorLayerNumber(args, "x", initial.Origin.X), EditorLayerNumber(args, "y", initial.Origin.Y));
                next.Size = new(EditorLayerNumber(args, "width", initial.Size.Width), EditorLayerNumber(args, "height", initial.Size.Height));
                next.Rotation = EditorLayerNumber(args, "rotation", initial.Rotation);
                if (!next.IsValid) throw new ArgumentException("Invalid layer bounds: x/y ±1000000, width/height 1–300000, finite rotation.");
                if (!next.Equals(initial)) plan = new(next, "Mcp_Moving", Source: initial);
            }
            else if (!target.IsGroupOrFalse && name is "editor_layer_duplicate" or "editor_layer_delete" or "editor_layer_visibility" or "editor_layer_opacity" or "editor_layer_blend")
                plan = new(CopyTransform(target.Transform), name == "editor_layer_delete" ? "Mcp_Deleting" : name == "editor_layer_duplicate" ? "Mcp_Creating" : "Mcp_Editing",
                    Creating: name == "editor_layer_duplicate");
        }
        var visual = await PreviewMcpActivityAsync(owner, plan, cancellation);
        bool applied = false;
        try
        {
            await QueueEditorActionAsync(owner, args, cancellation, () =>
            {
                if (name is "editor_layer_import" or "editor_layer_import_svg")
                {
                    using var batch = owner.BeginFileImport();
                    var added = batch.AddImage(imported!, importName!, importPlacement);
                    SelectEditorLayer(owner, added.Id);
                }
                else
                {
                    if (!Guid.TryParse(args["layerId"]?.GetValue<string>(), out var id)
                        || owner.Snapshot.Manifest.Layers.FirstOrDefault(layer => layer.Id == id) is not { } layer)
                        throw new ArgumentException("Choose an existing layer UUID from editor_state.");
                    switch (name)
                    {
                        case "editor_layer_select": SelectEditorLayer(owner, id); break;
                        case "editor_layer_rename": owner.RenameLayer(id, EditorLayerName(args["name"]?.GetValue<string>())); break;
                        case "editor_layer_visibility":
                            owner.SetVisible(id, args["visible"]?.GetValue<bool>() ?? throw new ArgumentException("Missing visible.")); break;
                        case "editor_layer_opacity":
                            double opacity = EditorLayerNumber(args, "opacity", double.NaN);
                            if (!double.IsFinite(opacity) || opacity is < 0 or > 1) throw new ArgumentException("opacity must be 0–1.");
                            owner.SetOpacity(id, opacity); break;
                        case "editor_layer_blend":
                            string? text = args["blendMode"]?.GetValue<string>();
                            var modes = Enum.GetValues<LayerBlendMode>().Where(mode => RawEnumText.Resolve<LayerBlendMode>(mode.ToString()) == text).ToArray();
                            if (layer.IsGroupOrFalse || modes.Length != 1) throw new ArgumentException("Choose an advertised blendMode for a non-folder layer.");
                            owner.SetBlendMode(id, modes[0]); break;
                        case "editor_layer_transform":
                            var edit = owner.BeginTransformDraft([id]) ?? throw new InvalidOperationException("Choose a visible layer or folder with drawable content.");
                            var value = edit.Value;
                            value.Origin = new(EditorLayerNumber(args, "x", value.Origin.X), EditorLayerNumber(args, "y", value.Origin.Y));
                            value.Size = new(EditorLayerNumber(args, "width", value.Size.Width), EditorLayerNumber(args, "height", value.Size.Height));
                            value.Rotation = EditorLayerNumber(args, "rotation", value.Rotation);
                            if (!value.IsValid) throw new ArgumentException("Invalid layer bounds: x/y ±1000000, width/height 1–300000, finite rotation.");
                            if (!edit.Value.Equals(value))
                            {
                                var preview = edit.Preview(value) ?? throw new ArgumentException("The transform could not be prepared.");
                                if (!owner.CommitTransform(preview)) throw new InvalidOperationException("The transform source changed.");
                            }
                            break;
                        case "editor_layer_reorder":
                            string? position = args["position"]?.GetValue<string>();
                            if (position is not ("top" or "bottom" or "above")) throw new ArgumentException("position must be top, bottom or above.");
                            Guid? parent = args.ContainsKey("parentId") ? args["parentId"] is { } node ? Guid.Parse(node.GetValue<string>()) : null : layer.ParentId;
                            Guid? above = args["aboveLayerId"] is { } anchor ? Guid.Parse(anchor.GetValue<string>()) : null;
                            if (position == "above" != above.HasValue || above == id || !owner.CanPlaceLayer(id, parent))
                                throw new ArgumentException("Choose a valid folder and a sibling aboveLayerId for position=above only.");
                            if (!owner.PlaceLayer(id, parent, above, position == "bottom")) throw new ArgumentException("This placement would not form a valid layer tree.");
                            SelectEditorLayer(owner, id); break;
                        case "editor_layer_duplicate":
                            var copies = owner.DuplicateLayers([id], out var mapping);
                            if (copies.Count == 0 || !mapping.TryGetValue(id, out var copy)) throw new InvalidOperationException("The layer could not be duplicated.");
                            foreach (var pair in mapping) if (collapsedGroups.Contains(pair.Key)) collapsedGroups.Add(pair.Value);
                            SelectEditorLayer(owner, copy); break;
                        case "editor_layer_delete":
                            var deletion = owner.BeginLayerDeletion([id])!;
                            if (deletion.DependentLayerIds.Count > 0) throw new InvalidOperationException("Other layers use this layer as a clipping/live-mask source. Resolve those links before deleting it.");
                            if (!owner.CommitLayerDeletion(deletion.Prepare(bake: false))) throw new InvalidOperationException("The deletion source changed.");
                            SelectEditorLayer(owner, owner.Snapshot.Manifest.ActiveLayerId); break;
                        default: throw new ArgumentException("Unknown live layer tool.");
                    }
                }
            });
            applied = true;
        }
        finally { CompleteMcpActivity(visual, applied); }
    }

    private void SelectEditorLayer(EditorDocument owner, Guid? id)
    {
        layerSelectionRequest++; layerSelectionPointerTarget = null; effectSelection = null;
        bool solo = maskAloneLayerId is not null;
        selectedLayerId = id; timelineLayer = id; selectedLayerIds.Clear();
        if (id is { } selected)
        {
            selectedLayerIds.Add(selected);
            var byId = owner.Snapshot.Manifest.Layers.ToDictionary(layer => layer.Id);
            var parent = byId[selected].ParentId;
            while (parent is { } folder) { collapsedGroups.Remove(folder); parent = byId[folder].ParentId; }
        }
        paintOnMask = false; PaintMaskCheck.IsChecked = false; maskAloneLayerId = null;
        selectingLayer = true;
        try { owner.SetActiveLayer(id); } finally { selectingLayer = false; }
        RefreshLayerPanel();
        if (solo) RenderCanvas();
    }

    private static string EditorLayerName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 512
        ? name : throw new ArgumentException("A nonempty layer name of at most 512 characters is required.");

    private static double EditorLayerNumber(JsonObject args, string field, double fallback)
    {
        if (!args.TryGetPropertyValue(field, out var node)) return fallback;
        if (node is not JsonValue value || !value.TryGetValue<double>(out var number) || !double.IsFinite(number))
            throw new ArgumentException(field + " must be a finite number.");
        return number;
    }
}
