using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Compositor.Core;

/// <summary>Local automation reuses editor operations on detached metadata, then publishes one history step.</summary>
public static partial class EditorAutomation
{
    public static Guid? Apply(EditorDocument owner, string tool, JsonObject args)
    {
        Guid? result = null;
        owner.ApplyAutomation(draft => result = ApplyCommand(draft, tool, args), tool);
        return result;
    }

    public static EditorDocument.AutomationEdit Prepare(EditorDocument owner, string tool, JsonObject args, out Guid? result)
    {
        Guid? id = null;
        var edit = owner.PrepareAutomation(draft => id = ApplyCommand(draft, tool, args), tool);
        result = id;
        return edit;
    }

    public static Dictionary<string, Guid> ApplyBatch(EditorDocument owner, JsonArray commands)
    {
        var prepared = PrepareBatch(owner, commands, out var names);
        prepared.Commit();
        return names;
    }

    public static EditorDocument.AutomationEdit PrepareBatch(EditorDocument owner, JsonArray commands, out Dictionary<string, Guid> aliases)
    {
        if (commands.Count is < 1 or > 32) throw new ArgumentException("A batch needs 1–32 local editing commands.");
        var names = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var prepared = owner.PrepareAutomation(draft =>
        {
            foreach (var item in commands)
            {
                if (item is not JsonObject command || command["arguments"] is not JsonObject supplied)
                    throw new ArgumentException("Each command needs tool and arguments.");
                var args = (JsonObject)supplied.DeepClone();
                foreach (string key in new[] { "layerId", "parentId", "aboveLayerId" })
                    if (args[key] is JsonValue value && value.TryGetValue<string>(out var text) && text.StartsWith('$'))
                        args[key] = names.TryGetValue(text[1..], out var id) ? id.ToString() : throw new ArgumentException("Unknown earlier batch alias.");
                if (args["layerIds"] is JsonArray ids)
                    for (int i = 0; i < ids.Count; i++)
                        if (ids[i] is JsonValue v && v.TryGetValue<string>(out var alias) && alias.StartsWith('$'))
                            ids[i] = names.TryGetValue(alias[1..], out var resolved) ? resolved.ToString() : throw new ArgumentException("Unknown earlier batch alias.");
                var result = ApplyCommand(draft, command["tool"]?.GetValue<string>() ?? "", args);
                if (command["as"] is { } aliasNode)
                {
                    string alias = aliasNode.GetValue<string>();
                    if (alias.Length is < 1 or > 64 || !alias.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')
                        || result is null || !names.TryAdd(alias, result.Value)) throw new ArgumentException("Batch aliases must be unique names for a resulting layer.");
                }
            }
        });
        aliases = names;
        return prepared;
    }

    private static Guid? ApplyCommand(EditorDocument owner, string tool, JsonObject args)
    {
        if (tool == "editor_text") return ApplyText(owner, args);
        if (tool == "editor_adjustment") return ApplyAdjustment(owner, args);
        if (tool == "editor_layout") return ApplyLayout(owner, args);
        if (tool == "editor_select")
        {
            owner.SetSelectionUndoable(PrepareSelection(owner.Snapshot.Manifest, owner.ActiveSelection, args));
            return owner.Snapshot.Manifest.ActiveLayerId;
        }
        var layer = Layer(owner, args); Guid id = layer.Id;
        switch (tool)
        {
            case "editor_layer_select": owner.SetActiveLayer(id); break;
            case "editor_layer_rename": owner.RenameLayer(id, Name(args["name"]?.GetValue<string>())); break;
            case "editor_layer_visibility": owner.SetVisible(id, args["visible"]?.GetValue<bool>() ?? throw new ArgumentException("Missing visible.")); break;
            case "editor_layer_opacity":
                double opacity = Number(args, "opacity", double.NaN);
                if (!double.IsFinite(opacity) || opacity is < 0 or > 1) throw new ArgumentException("opacity must be 0–1.");
                owner.SetOpacity(id, opacity); break;
            case "editor_layer_blend":
                if (layer.IsGroupOrFalse) throw new ArgumentException("Choose a non-folder layer.");
                owner.SetBlendMode(id, EnumValue<LayerBlendMode>(args["blendMode"]?.GetValue<string>())); break;
            case "editor_layer_transform":
                var edit = owner.BeginTransformDraft([id]) ?? throw new ArgumentException("Choose a visible drawable layer or folder.");
                var transform = edit.Value;
                transform.Origin = new(Number(args, "x", transform.Origin.X), Number(args, "y", transform.Origin.Y));
                transform.Size = new(Number(args, "width", transform.Size.Width), Number(args, "height", transform.Size.Height));
                transform.Rotation = Number(args, "rotation", transform.Rotation);
                if (!transform.IsValid) throw new ArgumentException("Invalid transform bounds.");
                if (!edit.Value.Equals(transform) && !owner.CommitTransform(edit.Preview(transform) ?? throw new ArgumentException("Invalid transform.")))
                    throw new InvalidOperationException("The transform could not be committed.");
                break;
            case "editor_layer_reorder":
                string? position = args["position"]?.GetValue<string>();
                Guid? parent = args.ContainsKey("parentId") ? args["parentId"] is { } p ? Guid.Parse(p.GetValue<string>()) : null : layer.ParentId;
                Guid? above = args["aboveLayerId"] is { } a ? Guid.Parse(a.GetValue<string>()) : null;
                if (position is not ("top" or "bottom" or "above") || position == "above" != above.HasValue || above == id
                    || !owner.CanPlaceLayer(id, parent) || !owner.PlaceLayer(id, parent, above, position == "bottom"))
                    throw new ArgumentException("Invalid layer placement.");
                owner.SetActiveLayer(id); break;
            case "editor_layer_duplicate":
                if (owner.DuplicateLayers([id], out var mapping).Count == 0 || !mapping.TryGetValue(id, out id))
                    throw new InvalidOperationException("The layer could not be duplicated.");
                owner.SetActiveLayer(id); break;
            case "editor_layer_delete":
                var deletion = owner.BeginLayerDeletion([id]) ?? throw new ArgumentException("Choose a layer to delete.");
                if (deletion.DependentLayerIds.Count > 0) throw new ArgumentException("Resolve surviving clipping/live-mask dependencies before deleting.");
                if (!owner.CommitLayerDeletion(deletion.Prepare(bake: false))) throw new InvalidOperationException("The deletion could not be committed.");
                return owner.Snapshot.Manifest.ActiveLayerId;
            default: throw new ArgumentException("This command is not supported in a local editing batch.");
        }
        return id;
    }

    public static Selection? PrepareSelection(ProjectManifest manifest, Selection? current, JsonObject args,
        MaskSurface? imported = null, CancellationToken cancellation = default)
    {
        int w = manifest.Width, h = manifest.Height;
        string? mode = args["mode"]?.GetValue<string>();
        if (mode == "clear") return null;
        if (mode == "feather") return (current ?? throw new ArgumentException("Select an area first.")).Feathered(Integer(args, "feather", 0, 250));
        Selection selection;
        if (mode == "all") selection = Selection.Rectangle(w, h, 0, 0, w, h);
        else if (mode is "rectangle" or "ellipse")
        {
            int x = Integer(args, "x", 0, w), y = Integer(args, "y", 0, h);
            int width = Integer(args, "width", 1, w), height = Integer(args, "height", 1, h);
            if ((long)x + width > w || (long)y + height > h) throw new ArgumentException("Selection bounds must lie inside the canvas.");
            selection = mode == "rectangle" ? Selection.Rectangle(w, h, x, y, width, height)
                : Selection.Ellipse(w, h, x + width / 2d, y + height / 2d, width / 2d, height / 2d, true);
        }
        else if (mode == "polygon")
        {
            if (args["points"] is not JsonArray points || points.Count is < 3 or > 4096) throw new ArgumentException("Supply 3–4096 [x,y] polygon points.");
            var vertices = points.Select(point => point is JsonArray { Count: 2 } pair
                ? new PointD(Real(pair[0]), Real(pair[1])) : throw new ArgumentException("Polygon points must be [x,y].")).ToArray();
            if (vertices.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y) || point.X < 0 || point.Y < 0 || point.X > w || point.Y > h))
                throw new ArgumentException("Polygon points must lie inside the canvas.");
            selection = LassoSelection.Rasterize(w, h, vertices, cancellation: cancellation);
        }
        else if (mode == "mask")
        {
            if (imported is null || imported.Width != w || imported.Height != h) throw new ArgumentException("The mask PNG must match the canvas dimensions.");
            selection = Selection.FromCoverage(imported, cancellation);
        }
        else throw new ArgumentException("Unknown selection mode.");
        if (args["feather"] is not null) selection = selection.Feathered(Integer(args, "feather", 0, 250));
        string combine = args["combine"]?.GetValue<string>() ?? "replace";
        if (combine == "replace") return selection;
        if (combine is not ("add" or "subtract")) throw new ArgumentException("combine must be replace, add or subtract.");
        // Combine visible coverage, including existing feather; the resulting mask already contains that fade.
        Selection Baked(Selection value)
        {
            if (value.Feather == 0) return value;
            var mask = new MaskSurface(w, h);
            for (int y = 0; y < h; y++) { cancellation.ThrowIfCancellationRequested(); for (int x = 0; x < w; x++) mask.Pixels[y * w + x] = value.CoverageAt(x, y); }
            return Selection.FromCoverage(mask, cancellation);
        }
        return LassoSelection.Combine(current is null ? null : Baked(current), Baked(selection),
            combine == "add" ? LassoSelectionMode.Add : LassoSelectionMode.Subtract);
    }

    private static Guid ApplyText(EditorDocument owner, JsonObject args)
    {
        var layer = args["layerId"] is null ? null : Layer(owner, args);
        if (layer is not null && layer.Text is null) throw new ArgumentException("Choose an editable text layer.");
        var style = layer?.Text is { } existing ? ManifestJson.Clone(existing) : new LayerTextStyle();
        if (args["content"] is { } content)
        {
            string value = content.GetValue<string>();
            var change = LayerTextStyle.MinimalEdit(style.Content, value); style.ReplaceCharacters(change.Location, change.Length, change.WithLength); style.Content = value;
        }
        else if (layer is null) throw new ArgumentException("New text requires content.");
        if (args["fontName"] is { } font) { style.FontName = font.GetValue<string>(); style.FontRuns = null; }
        style.FontSize = Number(args, "fontSize", style.FontSize); style.Tracking = Number(args, "tracking", style.Tracking); style.Leading = Number(args, "leading", style.Leading);
        if (args["alignment"] is { } alignment) style.Alignment = EnumValue<TextAlignment>(alignment.GetValue<string>());
        if (args["color"] is { } color)
        {
            if (color is not JsonArray { Count: 3 } channels) throw new ArgumentException("color needs three sRGB channels from 0 to 1.");
            style.Red = Real(channels[0]); style.Green = Real(channels[1]); style.Blue = Real(channels[2]); style.ColorRuns = null;
        }
        if (args.ContainsKey("boxWidth") || args.ContainsKey("boxHeight"))
            style.BoxSize = new(Number(args, "boxWidth", style.BoxSize?.Width ?? double.NaN), Number(args, "boxHeight", style.BoxSize?.Height ?? double.NaN));
        if (!style.IsValid || string.IsNullOrWhiteSpace(style.FontName) || style.FontName.Length > 200 || style.FontName.ContainsNewline()) throw new ArgumentException("Invalid text style.");
        if (layer is not null)
        {
            if (args.ContainsKey("x") || args.ContainsKey("y")) throw new ArgumentException("Use editor_layer_transform to move existing text.");
            if (!owner.UpdateTextLayer(layer.Id, style, layer.Transform)) throw new ArgumentException("The text could not be rendered within the document limits.");
            return layer.Id;
        }
        var origin = new PointD(Number(args, "x", 0), Number(args, "y", 0));
        if (Math.Abs(origin.X) > 1_000_000 || Math.Abs(origin.Y) > 1_000_000) throw new ArgumentException("Text origin exceeds the canvas coordinate limits.");
        return (owner.AddTextLayer(style, origin) ?? throw new ArgumentException("The text could not be rendered.")).Id;
    }

    private static Guid ApplyAdjustment(EditorDocument owner, JsonObject args)
    {
        var layer = args["layerId"] is null ? null : Layer(owner, args);
        if (layer is not null && layer.Adjustment is null) throw new ArgumentException("Choose an adjustment layer.");
        var kind = args["kind"] is { } raw ? EnumValue<AdjustmentKind>(raw.GetValue<string>())
            : layer?.Adjustment?.Kind ?? throw new ArgumentException("New adjustments require kind.");
        if (layer is not null && layer.Adjustment!.Kind != kind) throw new ArgumentException("An existing adjustment's kind cannot be changed.");
        var value = JsonSerializer.SerializeToNode(layer?.Adjustment ?? new LayerAdjustment { Kind = kind }, ManifestJson.Options)!.AsObject();
        if (args["settings"] is JsonObject patch)
        {
            if (patch.ContainsKey("kind")) throw new ArgumentException("Set kind outside settings.");
            Patch(value, patch, typeof(LayerAdjustment));
        }
        var updated = value.Deserialize<LayerAdjustment>(ManifestJson.Options)!;
        if (!updated.IsValid) throw new ArgumentException("Adjustment parameters are outside their supported ranges.");
        if (layer?.Adjustment?.Equals(updated) == true) return layer.Id;
        layer ??= owner.AddAdjustment(kind);
        if (!owner.MutateAdjustment(layer.Id, _ => layer.Adjustment = updated)) throw new InvalidOperationException("The adjustment could not be updated.");
        return layer.Id;
    }

    private static void Patch(JsonObject target, JsonObject patch, Type type)
    {
        foreach (var pair in patch)
        {
            var property = type.GetProperties().SingleOrDefault(p => p.CanWrite && p.GetCustomAttributes(typeof(JsonIgnoreAttribute), true).Length == 0
                && ManifestJson.CompositorNamingPolicy.Camel.ConvertName(p.Name) == pair.Key);
            if (property is null || pair.Value is null) throw new ArgumentException("Unknown or null adjustment parameter: " + pair.Key);
            if (pair.Value is JsonObject child && !property.PropertyType.IsGenericType)
            {
                var nested = target[pair.Key] as JsonObject ?? JsonSerializer.SerializeToNode(Activator.CreateInstance(property.PropertyType), property.PropertyType, ManifestJson.Options)!.AsObject();
                Patch(nested, child, property.PropertyType); target[pair.Key] = nested.DeepClone();
            }
            else target[pair.Key] = pair.Value.DeepClone();
        }
    }

    private static ProjectLayerRecord Layer(EditorDocument owner, JsonObject args) => Guid.TryParse(args["layerId"]?.GetValue<string>(), out var id)
        && owner.Snapshot.Manifest.Layers.FirstOrDefault(l => l.Id == id) is { } layer ? layer : throw new ArgumentException("Choose an existing layer UUID.");
    private static string Name(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 512 ? value : throw new ArgumentException("Layer names need 1–512 characters.");
    private static T EnumValue<T>(string? value) where T : struct, Enum => Enum.GetValues<T>().FirstOrDefault(v => RawEnumText.Resolve<T>(v.ToString()) == value) is var result
        && RawEnumText.Resolve<T>(result.ToString()) == value ? result : throw new ArgumentException("Choose an advertised " + typeof(T).Name + ".");
    private static double Number(JsonObject args, string key, double fallback) => args[key] is not { } node ? fallback : Real(node);
    private static double Real(JsonNode? node) => node?.GetValueKind() == JsonValueKind.Number && node.Deserialize<double>() is var number && double.IsFinite(number)
        ? number : throw new ArgumentException("Expected a finite number.");
    private static int Integer(JsonObject args, string key, int min, int max) => args[key] is JsonValue value && value.TryGetValue<int>(out int number) && number >= min && number <= max
        ? number : throw new ArgumentException(key + $" must be an integer from {min} to {max}.");
}

public sealed partial class EditorDocument
{
    internal void ApplyAutomation(Action<EditorDocument> edit, string operation = "Batch")
        => PrepareAutomation(edit, operation).Commit();

    internal AutomationEdit PrepareAutomation(Action<EditorDocument> edit, string operation = "Batch") => new(this, edit, operation);

    /// <summary>Validated local edits can show their exact placement before publishing one history step.</summary>
    public sealed class AutomationEdit
    {
        private readonly EditorDocument owner, fork;
        private readonly MementoState before;
        private readonly string operation;
        private readonly Dictionary<Guid, int> imageRevisions, maskRevisions;
        private bool committed;
        public ProjectSnapshot Snapshot => fork.Snapshot;
        public bool HasChanges { get; }

        internal AutomationEdit(EditorDocument owner, Action<EditorDocument> edit, string operation)
        {
            if (owner.ActiveTextDraft is not null) throw new InvalidOperationException("Finish the text draft first.");
            this.owner = owner; this.operation = operation; before = owner.CaptureState();
            imageRevisions = before.Images.ToDictionary(p => p.Key, p => p.Value.Revision);
            maskRevisions = before.Masks.ToDictionary(p => p.Key, p => p.Value.Revision);
            fork = new EditorDocument(new ProjectSnapshot
            {
                Manifest = ManifestJson.Deserialize(System.Text.Encoding.UTF8.GetString(before.Manifest)),
                Images = new Dictionary<Guid, Surface>(before.Images), Masks = new Dictionary<Guid, MaskSurface>(before.Masks),
            }) { ActiveSelection = before.Selection, suppressUndoHistory = true };
            foreach (var pair in owner.layerLocks) fork.layerLocks[pair.Key] = pair.Value;
            edit(fork);
            owner.ValidateLayerProtection(fork.Snapshot);
            ProjectValidator.Validate(fork.Snapshot.Manifest);
            if (fork.Snapshot.Images.Values.Sum(s => s.PixelCount) + fork.Snapshot.Masks.Values.Sum(s => (long)s.Width * s.Height) > Limits.DocumentPixelBudget)
                throw new ArgumentException("The batch exceeds the document pixel budget.");
            bool sameAssets = before.Images.Count == fork.Snapshot.Images.Count && before.Masks.Count == fork.Snapshot.Masks.Count
                && before.Images.All(pair => ReferenceEquals(pair.Value, fork.Snapshot.Images.GetValueOrDefault(pair.Key)))
                && before.Masks.All(pair => ReferenceEquals(pair.Value, fork.Snapshot.Masks.GetValueOrDefault(pair.Key)));
            HasChanges = !before.Manifest.AsSpan().SequenceEqual(MementoOf(fork.Snapshot)) || !ReferenceEquals(before.Selection, fork.ActiveSelection) || !sameAssets;
        }

        public bool Commit()
        {
            if (committed || owner.ActiveTextDraft is not null || owner.CurrentHistoryId != before.HistoryId
                || !ReferenceEquals(owner.ActiveSelection, before.Selection)
                || !before.Manifest.AsSpan().SequenceEqual(MementoOf(owner.Snapshot))
                || before.Images.Count != owner.Snapshot.Images.Count || before.Masks.Count != owner.Snapshot.Masks.Count
                || before.Images.Any(p => !ReferenceEquals(p.Value, owner.Snapshot.Images.GetValueOrDefault(p.Key)) || p.Value.Revision != imageRevisions[p.Key])
                || before.Masks.Any(p => !ReferenceEquals(p.Value, owner.Snapshot.Masks.GetValueOrDefault(p.Key)) || p.Value.Revision != maskRevisions[p.Key])) return false;
            owner.ValidateLayerProtection(fork.Snapshot);
            committed = true;
            if (!HasChanges) return true;
            bool selectionChanged = !ReferenceEquals(owner.ActiveSelection, fork.ActiveSelection);
            owner.BeginChange(owner.IsDirty == before.Dirty ? before : owner.CaptureState(), operation);
            owner.Snapshot = fork.Snapshot; owner.ActiveSelection = fork.ActiveSelection; owner.coalesceKey = null; owner.EndChange();
            if (selectionChanged) owner.SelectionChanged?.Invoke();
            return true;
        }
    }
}
