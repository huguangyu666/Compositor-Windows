using Compositor.Core;
using System.Text.Json.Nodes;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task<JsonObject> EditorAutomationAsync(string name, JsonObject args, EditorDocument owner)
    {
        RequireEditorIdle();
        var timeout = TimeSpan.FromSeconds(20);
#if DEBUG
        timeout = editorAutomationTimeoutProbe ?? timeout;
#endif
        using var deadline = new CancellationTokenSource(timeout);
        var cancellation = deadline.Token;
#if DEBUG
        var preparation = editorAutomationPreparationProbe;
#endif
        Selection? selection = null;
        MaskRefinement? maskRefinement = null;
        if (name == "editor_mask")
        {
            if (!Guid.TryParse(args["layerId"]?.GetValue<string>(), out var id)) throw new ArgumentException("Choose a layer UUID.");
            maskRefinement = MaskRefinement.Capture(owner, id);
            await Task.Run(async () =>
            {
#if DEBUG
                if (preparation is not null) await preparation;
#endif
                var imported = args["operation"]?.GetValue<string>() == "import"
                    ? await ReadEditorMaskAsync(EditorPath(args["path"]?.GetValue<string>(), ".png"), Limits.MaxSurfacePixels, cancellation) : null;
                maskRefinement.Prepare(args, imported, cancellation);
            }, cancellation).WaitAsync(cancellation);
        }
        if (name == "editor_select")
        {
            var manifest = ManifestJson.Deserialize(ManifestJson.Serialize(owner.Snapshot.Manifest));
            var current = owner.ActiveSelection;
            selection = await Task.Run(async () =>
            {
#if DEBUG
                if (preparation is not null) await preparation;
#endif
                MaskSurface? mask = null;
                if (args["mode"]?.GetValue<string>() == "mask")
                {
                    mask = await ReadEditorMaskAsync(EditorPath(args["path"]?.GetValue<string>(), ".png"), (long)manifest.Width * manifest.Height, cancellation);
                    if (mask.Width != manifest.Width || mask.Height != manifest.Height) throw new ArgumentException("The mask PNG must match the canvas dimensions.");
                }
                return EditorAutomation.PrepareSelection(manifest, current, args, mask, cancellation);
            }, cancellation).WaitAsync(cancellation);
        }
        cancellation.ThrowIfCancellationRequested();
        Dictionary<string, Guid>? aliases = null;
        EditorDocument.AutomationEdit? prepared = null;
        Guid? resultLayer = null;
        McpActivityPlan? plan = null;
        await QueueEditorActionAsync(owner, args, cancellation, () =>
        {
            if (name == "editor_select")
            {
                if (selection is { IsEmpty: false })
                {
                    var bounds = selection.Bounds;
                    plan = new(new() { Origin = new(bounds.X, bounds.Y), Size = new(bounds.Width, bounds.Height) }, "Mcp_Editing", Creating: true);
                }
            }
            else if (name == "editor_mask")
            {
                var layer = owner.Snapshot.Manifest.Layers.Single(l => l.Id == Guid.Parse(args["layerId"]!.GetValue<string>()));
                plan = new(CopyTransform(layer.Transform), "Mcp_Editing");
            }
            else
            {
                prepared = name == "editor_batch"
                    ? EditorAutomation.PrepareBatch(owner, args["commands"] as JsonArray ?? throw new ArgumentException("Missing commands."), out aliases)
                    : EditorAutomation.Prepare(owner, name, args, out resultLayer);
                if (prepared.HasChanges) plan = DescribeMcpActivity(owner.Snapshot, prepared.Snapshot, aliases is not null ? args["commands"]!.AsArray().Count : 1);
            }
        });
        var visual = await PreviewMcpActivityAsync(owner, plan, cancellation);
        bool applied = false;
        try
        {
            await QueueEditorActionAsync(owner, args, cancellation, () =>
            {
                if (name == "editor_mask")
                {
                    if (!maskRefinement!.Commit()) throw new InvalidOperationException("The mask source changed. Read editor_state before retrying.");
                }
                else if (name == "editor_select") owner.SetSelectionUndoable(selection);
                else
                {
                    if (!prepared!.Commit()) throw new InvalidOperationException("The edit source changed. Read editor_state before retrying.");
                    if (name == "editor_batch" || args["layerId"] is null) SelectEditorLayer(owner, name == "editor_batch" ? owner.Snapshot.Manifest.ActiveLayerId : resultLayer);
                }
            });
            applied = true;
        }
        finally { CompleteMcpActivity(visual, applied); }
        var state = EditorState();
        if (aliases is not null) state["batchAliases"] = System.Text.Json.JsonSerializer.SerializeToNode(aliases);
        return EditorResult(state);
    }

    private async Task QueueEditorActionAsync(EditorDocument owner, JsonObject args, CancellationToken cancellation, Action apply, bool allowTimeline = false)
    {
        cancellation.ThrowIfCancellationRequested();
        RequireEditorTarget(args); RequireEditorIdle(allowTimeline: allowTimeline);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool waiting = true;
        QueueCanvasInput(owner, () =>
        {
            if (!waiting || cancellation.IsCancellationRequested) return;
            try
            {
                RequireEditorTarget(args); RequireEditorIdle(allowTimeline: allowTimeline);
                apply();
                completion.TrySetResult();
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        });
        try { await completion.Task.WaitAsync(cancellation); }
        finally { waiting = false; }
    }

    private static async Task<MaskSurface> ReadEditorMaskAsync(string path, long maxPixels, CancellationToken cancellation = default)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > Limits.AssetMaxBytes) throw new ArgumentException("The mask PNG exceeds the asset limit.");
        var bytes = new byte[checked((int)file.Length)]; await file.ReadExactlyAsync(bytes, cancellation);
        var image = await WicImaging.DecodeSurfaceAsync(bytes, Limits.MaxSide, maxPixels, cancellation);
        var mask = new MaskSurface(image.Width, image.Height);
        for (int i = 0; i < mask.Pixels.Length; i++)
        {
            if (i % image.Width == 0) cancellation.ThrowIfCancellationRequested();
            int p = i * 4;
            if (image.Pixels[p + 3] != 255 || image.Pixels[p] != image.Pixels[p + 1] || image.Pixels[p] != image.Pixels[p + 2])
                throw new ArgumentException("Use an opaque grayscale mask PNG: black hides, white reveals.");
            mask.Pixels[i] = image.Pixels[p];
        }
        return mask;
    }
}
