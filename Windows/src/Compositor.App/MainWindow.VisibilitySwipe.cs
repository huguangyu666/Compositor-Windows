using Compositor.Core;
using Windows.Foundation;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private sealed record VisibilitySwipe(EditorDocument Owner, bool Visible, object Key);
    private VisibilitySwipe? visibilitySwipe;
    private bool applyingVisibilitySwipe;

    private bool BeginVisibilitySwipe(Guid id)
    {
        if (!CanEditLayerCommands || document is not { } owner
            || owner.Snapshot.Manifest.Layers.FirstOrDefault(layer => layer.Id == id) is not { } layer) return false;
        FinishVisibilitySwipe();
        CancelMaskCopyGesture();
        visibilitySwipe = new(owner, !layer.IsVisible, new object());
        ApplyVisibilityToRow(id);
        return true;
    }

    private void UpdateVisibilitySwipe(Point point)
    {
        if (visibilitySwipe is null) return;
        ApplyVisibilityAtPoint(point);
        TrackLayerAutoscroll(point);
    }

    private void ApplyVisibilityAtPoint(Point point)
    {
        if (ThumbnailDropTarget(point) is { } id) ApplyVisibilityToRow(id);
    }

    private void ApplyVisibilityToRow(Guid id)
    {
        if (visibilitySwipe is not { } swipe || !ReferenceEquals(document, swipe.Owner)) return;
        applyingVisibilitySwipe = true;
        try
        {
            if (!swipe.Owner.SetVisible(id, swipe.Visible, swipe.Key)) return;
            bool previous = suppressPanelEvents;
            suppressPanelEvents = true;
            try
            {
                if (layerRows.TryGetValue(id, out var grid))
                {
                    var row = (LayerRow)grid.Tag;
                    row.Update(row.Layer, row.Depth);
                    row.Eye.IsChecked = swipe.Visible;
                }
                if (SelectedLayer?.Id == id) VisibleCheck.IsChecked = swipe.Visible;
            }
            finally { suppressPanelEvents = previous; }
        }
        finally { applyingVisibilitySwipe = false; }
    }

    private void FinishVisibilitySwipe(bool closeHistory = true)
    {
        if (visibilitySwipe is not { } swipe) return;
        visibilitySwipe = null;
        StopLayerAutoscroll();
        // A foreign notification may follow Undo or another edit that already replaced history.
        // Only a normal gesture end can close this key's original step.
        if (closeHistory && ReferenceEquals(document, swipe.Owner)) swipe.Owner.EndTransform(swipe.Key);
        LayerList.ReleasePointerCaptures();
    }
}
