using System.Text.Json;
using System.Text.Json.Nodes;
using Compositor.Core;

namespace Compositor.Mcp;

/// <summary>
/// A Model Context Protocol server over stdio. It opens a .comp project and lets an agent
/// inspect it, add layers, replace layer pixels from PNG files, edit the manifest, and export
/// the flattened image. Package mutations validate and save through ProjectStore. Live tools
/// use the running editor's operations and undo history through its same-user pipe.
/// </summary>
public static partial class Program
{
    private static string? projectPath;

    /// <summary>The package digest this session last showed the client, or the one it just wrote.
    /// Every save is checked against it, so a write is never made over a package that moved on disk
    /// since the client looked at it (the same guard the app's save uses: EditorDocument.SavedDiskDigest).</summary>
    private static byte[]? acknowledgedDigest;

    public static async Task Main()
    {
        Console.InputEncoding = new System.Text.UTF8Encoding(false);
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        Console.Error.WriteLine("Compositor MCP server ready");
        while (Console.ReadLine() is { } line)
        {
            if (line.Trim().Length == 0) continue;
            JsonNode? request;
            try
            {
                request = JsonNode.Parse(line);
            }
            catch (JsonException ex)
            {
                // A line that is not JSON is a protocol error the client has to see; the reply
                // carries a null id because the request's id could not be read.
                Console.Error.WriteLine($"parse error: {ex.Message}");
                Console.WriteLine(ErrorWithoutId(-32700, "Parse error: " + ex.Message).ToJsonString(McpJsonOptions));
                continue;
            }
            if (request is null) continue;
            var response = await HandleAsync(request);
            if (response is not null)
            {
                Console.WriteLine(response.ToJsonString(McpJsonOptions));
            }
        }
    }

    private static readonly JsonSerializerOptions McpJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static async Task<JsonObject?> HandleAsync(JsonNode request)
    {
        // A line whose root is not an object — a batch array, a bare string or number — cannot carry a method or
        // an id at all, and reading ["method"] off it throws inside System.Text.Json. That is how one malformed
        // line used to end the whole session; JSON-RPC has a code for exactly this shape.
        if (request is not JsonObject) return ErrorWithoutId(-32600, "Invalid Request: the JSON value is not an object.");
        // A caller can put anything in "method", including the wrong JSON type; reading it must not
        // take the server down before it gets a chance to answer.
        string method = request["method"] is JsonValue name && name.TryGetValue(out string? text) ? text : "";
        JsonNode? id = request["id"];
        var parameters = request["params"] as JsonObject;

        // Notifications (no id) get no response.
        if (id is null) return null;

        if (method is not ("initialize" or "ping" or "tools/list" or "tools/call"))
            return Error(id, -32601, $"Method not found: '{method}'.");

        try
        {
            var result = method switch
            {
                "initialize" => Initialize(parameters),
                "ping" => new JsonObject(),
                "tools/list" => ToolsList(),
                _ => await CallToolAsync(parameters),
            };
            return new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id.DeepClone(),
                ["result"] = result,
            };
        }
        // Which side failed changes what the client should do: a bad parameter or tool name needs a
        // corrected call, an unreadable file or invalid project needs a state fix. Collapsing both
        // into -32603 told the client nothing.
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return Error(id, -32602, ex.Message);
        }
        catch (Exception ex) when (ex is ProjectFormatException or IOException or UnauthorizedAccessException)
        {
            // UnauthorizedAccessException is a SystemException, not an IOException, so a destination the
            // caller cannot write to used to land in the -32603 bucket as an unforeseen server fault.
            // Measured 2026-10-17: exporting onto a read-only file answered "Internal error: Access to the
            // path … is denied." That is the file's state, and the client can act on it.
            return Error(id, -32000, ex.Message);
        }
        catch (ProjectConflictException ex)
        {
            // The one refusal that is not the client's mistake: somebody else — the app, another session —
            // wrote the package after this client last looked. Nothing was written. The client fixes this by
            // reading again, so say that instead of only reporting the hash.
            return Error(id, -32000, $"{ex.Message} Something else wrote the project; call get_project_state "
                + "to see it and repeat the change against what you now have.");
        }
        catch (Exception ex)
        {
            // Anything unforeseen still has to be an answer with this request's id rather than a dead session:
            // JSON-RPC pairs a response to a request by id, and a reply without one (which is what an outer
            // guard in the read loop produced) leaves a client unable to tell which call failed. The message
            // stays on stderr, where diagnostics belong.
            Console.Error.WriteLine($"internal error: {ex.Message}");
            return Error(id, -32603, "Internal error: " + ex.Message);
        }
    }

    private static JsonObject Error(JsonNode id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    /// <summary>For the failures where the request could not be read far enough to learn an id.</summary>
    private static JsonObject ErrorWithoutId(int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = null,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    private static JsonObject Initialize(JsonObject? parameters)
    {
        return new JsonObject
        {
            ["protocolVersion"] = parameters?["protocolVersion"]?.GetValue<string>() == "2024-11-05" ? "2024-11-05" : "2025-06-18",
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject(),
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "compositor-windows",
                ["version"] = "1.1.0",
            },
        };
    }

    private static JsonObject TextResult(string text, bool isError = false)
    {
        var result = new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = text },
            },
        };
        if (isError) result["isError"] = true;
        return result;
    }

    private static JsonObject ToolsList()
    {
        static JsonObject Tool(string name, string description, JsonObject schema) => new()
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = schema,
        };

        var result = new JsonObject
        {
            ["tools"] = new JsonArray
            {
                Tool("open_project", "Open a Compositor .comp project folder. Required before any other tool.",
                    new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject { ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Path to the .comp folder" } },
                        ["required"] = new JsonArray { "path" },
                    }),
                Tool("get_project_state", "Describe the open project: canvas size and every layer with its id, name, visibility, opacity, blend mode and transform.",
                    new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }),
                Tool("add_layer", "Add a layer above the active one, from a PNG file, or a blank layer when no file is given. Saves the project.",
                    new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["name"] = new JsonObject { ["type"] = "string" },
                            ["pngPath"] = new JsonObject { ["type"] = "string", ["description"] = "A PNG file to place as the layer's pixels" },
                        },
                        ["required"] = new JsonArray { "name" },
                    }),
                Tool("replace_layer_image", "Replace a layer's pixels with a PNG file's contents, keeping the layer's id and transform. Saves the project.",
                    new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["layerId"] = new JsonObject { ["type"] = "string" },
                            ["pngPath"] = new JsonObject { ["type"] = "string" },
                        },
                        ["required"] = new JsonArray { "layerId", "pngPath" },
                    }),
                Tool("update_manifest", "Edit layer properties: name, visibility, opacity (0-1), blendMode, origin, size, rotation, parent, active layer. Saves the project.",
                    new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["activeLayerId"] = new JsonObject { ["type"] = "string" },
                            ["layers"] = new JsonObject
                            {
                                ["type"] = "array",
                                ["items"] = new JsonObject
                                {
                                    ["type"] = "object",
                                    ["properties"] = new JsonObject
                                    {
                                        ["id"] = new JsonObject { ["type"] = "string" },
                                        ["name"] = new JsonObject { ["type"] = "string" },
                                        ["isVisible"] = new JsonObject { ["type"] = "boolean" },
                                        ["opacity"] = new JsonObject { ["type"] = "number" },
                                        ["blendMode"] = new JsonObject { ["type"] = "string" },
                                        ["originX"] = new JsonObject { ["type"] = "number" },
                                        ["originY"] = new JsonObject { ["type"] = "number" },
                                        ["width"] = new JsonObject { ["type"] = "number" },
                                        ["height"] = new JsonObject { ["type"] = "number" },
                                        ["rotation"] = new JsonObject { ["type"] = "number" },
                                        ["parentId"] = new JsonObject { ["type"] = new JsonArray { "string", "null" } },
                                    },
                                    ["required"] = new JsonArray { "id" },
                                },
                            },
                        },
                    }),
                Tool("export_image", "Flatten the project and export it as PNG or JPEG.",
                    new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Destination file path" },
                            ["format"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "png", "jpeg" } },
                            ["quality"] = new JsonObject { ["type"] = "number", ["description"] = "JPEG quality 0-100, default 90" },
                        },
                        ["required"] = new JsonArray { "path", "format" },
                    }),
            },
        };
        foreach (var tool in EditorTools()) ((JsonArray)result["tools"]!).Add(tool);
        return result;
    }

    private static async Task<JsonObject> CallToolAsync(JsonObject? parameters)
    {
        string name = parameters?["name"]?.GetValue<string>() ?? throw new JsonException("Missing tool name.");
        var arguments = parameters["arguments"] as JsonObject ?? new JsonObject();
        if (name.StartsWith("editor_", StringComparison.Ordinal)) return await CallEditorAsync(name, arguments);
        string text = name switch
        {
            "open_project" => await OpenProjectAsync(arguments),
            "get_project_state" => await GetProjectStateAsync(),
            "add_layer" => await AddLayerAsync(arguments),
            "replace_layer_image" => await ReplaceLayerImageAsync(arguments),
            "update_manifest" => await UpdateManifestAsync(arguments),
            "export_image" => await ExportImageAsync(arguments),
            _ => throw new JsonException($"Unknown tool '{name}'."),
        };
        return TextResult(text);
    }

    private static async Task<ProjectSnapshot> LoadOpenProjectAsync()
    {
        if (projectPath is null)
            throw new ArgumentException("No project is open. Call open_project first.");
        var snapshot = await ProjectStore.LoadAsync(projectPath);
        return snapshot;
    }

    /// <summary>Take the package as it is on disk right now as the state the client has seen.</summary>
    private static void AcknowledgeDisk()
    {
        if (projectPath is null) { acknowledgedDigest = null; return; }
        try
        {
            acknowledgedDigest = ProjectStore.HasPackage(projectPath) ? ProjectStore.ComputeDigest(projectPath) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No baseline to compare against. The save then goes unguarded, exactly as the app's save does
            // when its own digest capture failed (MainWindow.xaml.cs:3752-3762).
            acknowledgedDigest = null;
        }
    }

    /// <param name="rewrittenAssets">Asset file names this call is replacing, which must be encoded even
    /// though the rest of the package is left alone. A replaced layer keeps its own file name, so without
    /// this the new pixels would be skipped and the manifest would describe the old picture.</param>
    private static async Task SaveAsync(ProjectSnapshot snapshot, IReadOnlySet<string>? rewrittenAssets = null)
    {
        if (projectPath is null) throw new ArgumentException("No project is open.");
        // The snapshot came off this folder a moment ago, so every asset it names is already on disk with
        // these bytes; only what this call actually changed needs re-encoding.
        var unchanged = new HashSet<string>();
        foreach (var layer in snapshot.Manifest.Layers)
        {
            if (layer.ImageFile is { } imageFile && rewrittenAssets?.Contains(imageFile) != true) unchanged.Add(imageFile);
            if (layer.MaskFile is { } maskFile && rewrittenAssets?.Contains(maskFile) != true) unchanged.Add(maskFile);
        }
        await ProjectStore.SaveAsync(snapshot, projectPath, unchanged, cancellation: default, expectedDiskDigest: acknowledgedDigest);
        // Our own write moved the package, and the client has just been told about it: that is the new baseline.
        AcknowledgeDisk();
    }

    private static async Task<string> OpenProjectAsync(JsonObject arguments)
    {
        string path = arguments["path"]?.GetValue<string>() ?? throw new JsonException("Missing 'path'.");
        if (!Directory.Exists(path)) throw new ArgumentException($"No folder at {path}.");
        if (!ProjectStore.IsProjectDirectory(path))
            throw new ArgumentException($"{path} is not a Compositor project (no manifest.json).");
        var snapshot = await ProjectStore.LoadAsync(path);
        projectPath = path;
        AcknowledgeDisk();
        return $"Opened {path}: {snapshot.Manifest.Width}x{snapshot.Manifest.Height}, {snapshot.Manifest.Layers.Count} layers, format version {snapshot.Manifest.Version}.";
    }

    private static async Task<string> GetProjectStateAsync()
    {
        var snapshot = await LoadOpenProjectAsync();
        var lines = new List<string>
        {
            $"Canvas: {snapshot.Manifest.Width}x{snapshot.Manifest.Height}, resolution {snapshot.Manifest.Resolution ?? 72} PPI, format version {snapshot.Manifest.Version}.",
            $"Document id: {ProjectValidator.Uuid(snapshot.Manifest.DocumentId)}.",
            "Layers run bottom to top; the last layer draws on top.",
        };
        foreach (var layer in snapshot.Manifest.Layers)
        {
            string description = layer.IsGroupOrFalse
                ? $"folder (opacity {layer.OpacityOrOne:0.##})"
                : layer.Adjustment is { } adjustment
                    ? $"adjustment {adjustment.Kind} (no image)"
                    : layer.ImageFile is null
                        ? "blank layer (no image)"
                        : $"{snapshot.Images[layer.Id].Width}x{snapshot.Images[layer.Id].Height}px image";
            string transform = $"at ({layer.Transform.Origin.X:0.#},{layer.Transform.Origin.Y:0.#}) size {layer.Transform.Size.Width:0.#}x{layer.Transform.Size.Height:0.#}";
            string parent = layer.ParentId is { } id ? $", parent {ProjectValidator.Uuid(id)}" : "";
            lines.Add($"  [{ProjectValidator.Uuid(layer.Id)}] \"{layer.Name}\" — {description}, {transform}, " +
                      $"visible {layer.IsVisible}, opacity {layer.OpacityOrOne:0.##}, blend {layer.BlendMode ?? LayerBlendMode.Normal}{parent}");
        }
        // The client has now been shown this exact package, so a later write in the session is allowed to
        // replace it. This is also how a refused save recovers: read again, then repeat the change.
        AcknowledgeDisk();
        return string.Join('\n', lines);
    }

    private static async Task<string> AddLayerAsync(JsonObject arguments)
    {
        string name = arguments["name"]?.GetValue<string>() ?? "Layer";
        string? pngPath = arguments["pngPath"]?.GetValue<string>();
        var snapshot = await LoadOpenProjectAsync();
        var manifest = snapshot.Manifest;

        var id = Guid.NewGuid();
        var layer = new ProjectLayerRecord
        {
            Id = id,
            Name = name,
            IsVisible = true,
            Opacity = 1,
            BlendMode = LayerBlendMode.Normal,
            IsGroup = false,
        };
        var images = new Dictionary<Guid, Surface>(snapshot.Images);
        if (pngPath is { } path)
        {
            var image = await DecodePngFileAsync(path);
            layer.ImageFile = ProjectValidator.Uuid(id) + ".png";
            layer.Transform = new LayerTransform { Origin = new PointD(0, 0), Size = new SizeD(image.Width, image.Height) };
            images[id] = image;
        }
        else
        {
            // A blank layer: no image asset, just a name and a canvas-sized box.
            layer.Transform = new LayerTransform { Origin = new PointD(0, 0), Size = new SizeD(manifest.Width, manifest.Height) };
        }
        int insertion = manifest.ActiveLayerId is { } active
            ? manifest.Layers.FindIndex(candidate => candidate.Id == active) + 1
            : manifest.Layers.Count;
        manifest.Layers.Insert(Math.Min(insertion, manifest.Layers.Count), layer);
        manifest.ActiveLayerId = id;

        await SaveAsync(new ProjectSnapshot { Manifest = manifest, Images = images, Masks = snapshot.Masks },
            layer.ImageFile is { } written ? new HashSet<string> { written } : null);
        return $"Added layer \"{name}\" [{ProjectValidator.Uuid(id)}]" + (pngPath is null ? " (blank)" : $" from {pngPath}") + " and saved.";
    }

    private static async Task<string> ReplaceLayerImageAsync(JsonObject arguments)
    {
        string layerIdText = arguments["layerId"]?.GetValue<string>() ?? throw new JsonException("Missing 'layerId'.");
        string pngPath = arguments["pngPath"]?.GetValue<string>() ?? throw new JsonException("Missing 'pngPath'.");
        if (!Guid.TryParse(layerIdText, out var id)) throw new ArgumentException($"'{layerIdText}' is not a layer id.");
        var snapshot = await LoadOpenProjectAsync();
        var manifest = snapshot.Manifest;
        var layer = manifest.Layers.FirstOrDefault(candidate => candidate.Id == id)
            ?? throw new ArgumentException($"No layer with id {layerIdText}.");

        var image = await DecodePngFileAsync(pngPath);
        var images = new Dictionary<Guid, Surface>(snapshot.Images) { [id] = image };
        layer.ImageFile = ProjectValidator.Uuid(id) + ".png";
        // The new pixels take their own size; the layer keeps its position and rotation.
        layer.Transform.Size = new SizeD(image.Width, image.Height);
        layer.Text = null; // Replaced pixels are no longer the rasterization of the saved text.

        await SaveAsync(new ProjectSnapshot { Manifest = manifest, Images = images, Masks = snapshot.Masks },
            new HashSet<string> { layer.ImageFile });
        return $"Replaced pixels of \"{layer.Name}\" with {image.Width}x{image.Height} from {pngPath} and saved.";
    }

    private static async Task<string> UpdateManifestAsync(JsonObject arguments)
    {
        var snapshot = await LoadOpenProjectAsync();
        var manifest = snapshot.Manifest;

        if (arguments["activeLayerId"] is { } activeNode)
        {
            string activeText = activeNode.GetValue<string>();
            if (!Guid.TryParse(activeText, out var activeId)) throw new ArgumentException($"'{activeText}' is not a UUID.");
            if (!manifest.Layers.Any(layer => layer.Id == activeId)) throw new ArgumentException($"No layer with id {activeText}.");
            manifest.ActiveLayerId = activeId;
        }

        var applied = new List<string>();
        if (arguments["layers"] is JsonArray updates)
        {
            foreach (var node in updates)
            {
                var update = node as JsonObject ?? throw new JsonException("Layer update must be an object.");
                string idText = update["id"]?.GetValue<string>() ?? throw new JsonException("Layer update needs 'id'.");
                if (!Guid.TryParse(idText, out var id)) throw new ArgumentException($"'{idText}' is not a UUID.");
                var layer = manifest.Layers.FirstOrDefault(candidate => candidate.Id == id)
                    ?? throw new ArgumentException($"No layer with id {idText}.");

                if (update["name"] is { } nameNode) { layer.Name = nameNode.GetValue<string>(); applied.Add($"{layer.Name}: name"); }
                if (update["isVisible"] is { } visibleNode) { layer.IsVisible = visibleNode.GetValue<bool>(); applied.Add($"{layer.Name}: visible={layer.IsVisible}"); }
                if (update["opacity"] is { } opacityNode) { layer.Opacity = Math.Clamp(opacityNode.GetValue<double>(), 0, 1); applied.Add($"{layer.Name}: opacity={layer.Opacity}"); }
                if (update["blendMode"] is { } blendNode)
                {
                    string blendText = blendNode.GetValue<string>();
                    if (!Enum.GetValues<LayerBlendMode>().Any(mode => RawEnumText.Resolve<LayerBlendMode>(Enum.GetName(mode)!) == blendText))
                        throw new ArgumentException($"'{blendText}' is not a blend mode.");
                    layer.BlendMode = Enum.GetValues<LayerBlendMode>().First(mode => RawEnumText.Resolve<LayerBlendMode>(Enum.GetName(mode)!) == blendText);
                    applied.Add($"{layer.Name}: blend={blendText}");
                }
                if (update["originX"] is { } originXNode || update["originY"] is { } originYNode)
                {
                    double x = update["originX"] is { } xNode ? xNode.GetValue<double>() : layer.Transform.Origin.X;
                    double y = update["originY"] is { } yNode ? yNode.GetValue<double>() : layer.Transform.Origin.Y;
                    layer.Transform.Origin = new PointD(x, y);
                    applied.Add($"{layer.Name}: origin=({x:0.#},{y:0.#})");
                }
                if (update["width"] is { } widthNode || update["height"] is { } heightNode)
                {
                    double width = update["width"] is { } widthNode2 ? widthNode2.GetValue<double>() : layer.Transform.Size.Width;
                    double height = update["height"] is { } heightNode2 ? heightNode2.GetValue<double>() : layer.Transform.Size.Height;
                    layer.Transform.Size = new SizeD(width, height);
                    applied.Add($"{layer.Name}: size={width:0.#}x{height:0.#}");
                }
                if (update["rotation"] is { } rotationNode) { layer.Transform.Rotation = rotationNode.GetValue<double>(); applied.Add($"{layer.Name}: rotation={layer.Transform.Rotation}"); }
                if (update["parentId"] is { } parentNode)
                {
                    if (parentNode.GetValueKind() == JsonValueKind.Null || parentNode.GetValue<string>()?.Length == 0)
                    {
                        layer.ParentId = null;
                        applied.Add($"{layer.Name}: parent=none");
                    }
                    else
                    {
                        string parentText = parentNode.GetValue<string>();
                        if (!Guid.TryParse(parentText, out var parentId)) throw new ArgumentException($"'{parentText}' is not a UUID.");
                        var parent = manifest.Layers.FirstOrDefault(candidate => candidate.Id == parentId)
                            ?? throw new ArgumentException($"No layer with id {parentText}.");
                        if (!parent.IsGroupOrFalse) throw new ArgumentException($"Layer {parentText} is not a folder.");
                        layer.ParentId = parentId;
                        applied.Add($"{layer.Name}: parent={parent.Name}");
                    }
                }
            }
        }

        ProjectValidator.Validate(manifest);
        await SaveAsync(new ProjectSnapshot { Manifest = manifest, Images = snapshot.Images, Masks = snapshot.Masks });
        return applied.Count == 0
            ? "Nothing to change; project saved."
            : "Updated and saved: " + string.Join("; ", applied) + ".";
    }

    /// <summary>
    /// A file the caller pointed at that WIC cannot read as a PNG is their argument's problem: the raw COM
    /// exception from the decoder carries no message a client could act on (measured 2026-10-17 as
    /// "Internal error: " with nothing after it). Unreadable files keep their own IOException answer.
    /// </summary>
    private static async Task<Surface> DecodePngFileAsync(string path)
    {
        // A folder handed to a parameter that wants a file reads as "access denied" on Windows, and that is the
        // caller's argument shape rather than the machine's state — say which.
        if (Directory.Exists(path)) throw new ArgumentException($"'{path}' is a folder, not a PNG file.");
        byte[] bytes = await File.ReadAllBytesAsync(path);
        try
        {
            return await WicImaging.DecodeSurfaceAsync(bytes);
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"Could not decode '{path}' as a PNG image: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task<string> ExportImageAsync(JsonObject arguments)
    {
        string path = arguments["path"]?.GetValue<string>() ?? throw new JsonException("Missing 'path'.");
        string format = arguments["format"]?.GetValue<string>() ?? "png";
        double quality = arguments["quality"]?.GetValue<double>() ?? 90;
        var snapshot = await LoadOpenProjectAsync();
        var surface = new CanvasCompositor(snapshot).Render();
        // The export path is the caller's, so the tool writes into it but never builds a tree the
        // caller did not create: a typo must fail with a message, not leave folders on disk.
        string? parent = Path.GetDirectoryName(Path.GetFullPath(path));
        if (parent is null || !Directory.Exists(parent))
            throw new ArgumentException($"No folder exists for the export path: {path}");
        // A .comp is a directory, so an export path pointing inside the package that is being served is one
        // typo away from overwriting the project itself. Measured 2026-10-17: exporting to
        // "<open>/manifest.json" answered "Exported 4x4 to …" and every call afterwards said the project was
        // not valid. The export is still free to overwrite any other file the caller names.
        if (projectPath is { } package)
        {
            string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(package));
            if (target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || string.Equals(target, root, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Refusing to export inside the open project package: {path}");
        }
        // Measured 2026-10-17: the agent's PNG export came out at the encoder's own 96 DPI (pHYs 3779 ppm) even
        // though the project declared 300 PPI, so the same "export" answered the user with one file and the
        // agent with another. D04a gave the editor's export the document resolution; this uses that same seam.
        byte[] bytes = format.ToLowerInvariant() switch
        {
            "jpeg" or "jpg" => await WicImaging.EncodeJpegAsync(surface, quality),
            "png" => await WicImaging.EncodePngForExportAsync(surface, snapshot.Manifest.Resolution),
            _ => throw new ArgumentException($"Unknown format '{format}'."),
        };
        string written = Path.GetFullPath(path);
        // The same shape as above: writing onto an existing folder answers "access denied", and the caller
        // named a folder where the tool wants a file.
        if (Directory.Exists(written)) throw new ArgumentException($"The export path is a folder, not a file: {path}");
        await File.WriteAllBytesAsync(written, bytes);
        return $"Exported {surface.Width}x{surface.Height} to {written} ({bytes.Length} bytes).";
    }
}
