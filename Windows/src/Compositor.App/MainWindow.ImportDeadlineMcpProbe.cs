#if DEBUG
using Compositor.Core;
using System.Text.Json.Nodes;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private Task? editorLayerImportPreparationProbe;
    private TimeSpan? editorLayerImportTimeoutProbe;

    private async Task VerifyImportDeadlineMcpAsync()
    {
        var scene = EditorDocument.Create(64, 64); OpenDocument(scene); await WaitForFramesAsync();
        Guid id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        scene.RenameLayer(id, "Redo survives timeout"); scene.Undo();
        EditorState();
        string path = Path.Combine(Environment.GetEnvironmentVariable("COMPOSITOR_PREFERENCES_DIRECTORY")!, "deadline-import.png");
        await File.WriteAllBytesAsync(path, await WicImaging.EncodePngAsync(new Surface(2, 2)));
        var args = new JsonObject { ["documentToken"] = editorDocumentToken, ["revision"] = editorRevision, ["path"] = path };
        string before = ManifestJson.Serialize(scene.Snapshot.Manifest);
        var source = scene.Snapshot.Images[id];
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        editorLayerImportPreparationProbe = barrier.Task; editorLayerImportTimeoutProbe = TimeSpan.FromMilliseconds(40);
        Task pending = HandleEditorLayerToolAsync("editor_layer_import", args, scene);
        bool expired;
        try { await Task.Delay(200); expired = pending.IsCompleted; }
        finally { barrier.TrySetResult(); editorLayerImportPreparationProbe = null; editorLayerImportTimeoutProbe = null; }
        bool canceled = false;
        try { await pending; } catch (OperationCanceledException) { canceled = true; }
        await Task.Delay(50); await WaitForFramesAsync();
        Require(expired && canceled, "PNG import preparation outlived its request deadline.");
        Require(before == ManifestJson.Serialize(scene.Snapshot.Manifest) && ReferenceEquals(source, scene.Snapshot.Images[id])
            && !scene.IsDirty && !scene.CanUndo && scene.CanRedo && selectedLayerId == id, "Timed-out import changed pixels, selection or undo/redo.");

        // A valid decode is also refused if the document was edited while it waited.
        barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        editorLayerImportPreparationProbe = barrier.Task;
        args["revision"] = editorRevision;
        pending = HandleEditorLayerToolAsync("editor_layer_import", args, scene);
        scene.RenameLayer(id, "New user edit"); before = ManifestJson.Serialize(scene.Snapshot.Manifest);
        barrier.TrySetResult(); editorLayerImportPreparationProbe = null;
        bool stale = false;
        try { await pending; } catch (InvalidOperationException) { stale = true; }
        Require(stale && before == ManifestJson.Serialize(scene.Snapshot.Manifest) && scene.LayerCount == 1, "Import overwrote a newer document revision.");
    }
}
#endif
