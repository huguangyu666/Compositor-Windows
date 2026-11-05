namespace Compositor.App;

public sealed partial class MainWindow
{
    // EditorSession.canEditLayers rejects content commands for the entire draft,
    // including the worker preparation and the merge-to-copy continuation.
    private bool CanEditLayerCommands => CanEditSelection && cropRectangle is null;
}
