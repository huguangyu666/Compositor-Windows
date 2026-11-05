using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace Compositor.App;

/// <summary>
/// Dragging rows of the layer panel, the counterpart of the Mac's NSTableView drop handling
/// (NativeLayerList.swift:374-470): a row lands above another row, inside the body of a folder, or at the
/// bottom of the stack, and every dragged row of a selection travels with the folder that contains it.
///
/// The Mac gets the band of the row from NSTableView's own `.on` / `.above` proposal; WinUI gives a drop
/// handler nothing but a point, so the point is hit-tested against the row grids the panel itself drew —
/// the same walk <see cref="ThumbnailDropTarget"/> already does for a mask dragged off a thumbnail. Only
/// the vertical position is read, as in the Mac: a row is chosen by the pointer's height, so a drop beside
/// a row still means that row.
///
/// Option-drag copies, as on the Mac: the row is duplicated and the copy lands on the row
/// (`DuplicateLayersAt`, the port of duplicateLayer(in:above:atBottom:), SelectionClipboard.swift:238).
/// Effect rows use the same layer hit test to copy one effect onto the target.
/// </summary>
public sealed partial class MainWindow
{
    private sealed record LayerPanelDrag(EditorDocument Owner, IReadOnlyList<Guid> Layers,
        (LayerEffectKind Kind, Guid Source)? Effect, IReadOnlyList<object>? NativeItems = null);
    private LayerPanelDrag? layerPanelDrag;
    private LayerPanelDrag? CurrentLayerPanelDrag => !windowClosed && layerPanelDrag is { } drag
        && ReferenceEquals(document, drag.Owner) ? drag : null;

    /// <summary>What the window can commit on demand while the layer-effects panel is open, and which layer
    /// that panel answers for — the Mac keeps the same pair as <code>effectsEditing</code> so a copy dropped
    /// onto that layer can close the live session before writing (LayerEffects.swift:333-336).</summary>
    private Guid? effectsPanelLayer;
    private Action? effectsPanelCommit;

    internal const string EffectDragProperty = "compositor/effect";

    /// <summary>What a drag over the layer panel should answer for an effect row: the row under the pointer
    /// takes the copy when that layer can accept it, and nothing is offered elsewhere.</summary>
    internal static DataPackageOperation EffectDropOperation(bool canEdit, bool layerUnder, bool targetAccepts)
        => canEdit && layerUnder && targetAccepts ? DataPackageOperation.Copy : DataPackageOperation.None;

    /// <summary>The layer whose row a point falls on, whatever band of it: the Mac's effect and mask drops
    /// both land "on whichever row is under the pointer" (NativeLayerList.swift:382-390), not on a gap.</summary>
    private Guid? LayerRowAt(Point point)
        => LayerDropTarget(point) is { } hit && hit.Index < layerItems.Count
            ? ((LayerRow)layerItems[hit.Index].Tag).Layer.Id
            : null;

    /// <summary>
    /// Moves the panel's selection to one layer without going through a click: the active layer, the extended
    /// set and the paint target all follow. Folding a folder that holds the working layer needs it (the Mac's
    /// toggleGroupExpansion selects the group), and so does a drop that lands an effect on a layer.
    /// </summary>
    private void SelectLayerRow(Guid id)
    {
        if (document is not { } owner) return;
        effectSelection = null;
        CommitInlineText(); // editing text on another layer is a different draft, not the same one
        selectedLayerId = id;
        selectedLayerIds.Clear();
        selectedLayerIds.Add(id);
        paintOnMask = false;
        PaintMaskCheck.IsChecked = false;
        selectingLayer = true;
        try { owner.SetActiveLayer(id); } finally { selectingLayer = false; }
    }

    /// <summary>Whether this layer sits inside that folder, at any depth — the same walk the panel makes to
    /// decide indentation, bounded the same way so a malformed tree cannot spin here.</summary>
    private bool IsInsideLayer(Guid id, Guid folder)
    {
        if (document is not { } owner) return false;
        var byId = owner.Snapshot.Manifest.Layers.ToDictionary(layer => layer.Id, layer => layer);
        if (!byId.TryGetValue(id, out var layer)) return false;
        Guid? parent = layer.ParentId;
        for (int depth = 0; depth < 64 && parent is { } ancestor; depth++)
        {
            if (ancestor == folder) return true;
            parent = byId.TryGetValue(ancestor, out var node) ? node.ParentId : null;
        }
        return false;
    }

    /// carried (Core's CopyEffect). If the effects panel is open on that same layer its live preview is
    /// committed first and the panel comes down, because a session that stayed open would roll the whole
    /// layer back on Cancel — past the copy it never saw. The Mac does not have this problem because its
    /// editing state is one effect at a time (LayerEffects.swift:333-336), while this panel previews the
    /// layer's whole set under one session key.
    /// </summary>
    private bool CopyEffectToRow(LayerEffectKind kind, Guid source, Guid target)
    {
        if (!CanEditLayerCommands || document is not { } owner) return false;
        if (effectsPanelLayer == target)
        {
            effectsPanelCommit?.Invoke();
            CloseFloatingPanel("effects", commit: true);
        }
        if (!owner.CopyEffect(kind, source, target)) return false;
        SelectLayerRow(target);
        effectSelection = (target, kind);
        RefreshLayerPanel();
        RenderCanvas();
        return true;
    }

    /// <summary>Where on a row a drop lands: above that row, into it when it is a folder, or below the last
    /// row of all, which is the bottom of the stack.</summary>
    private enum LayerDropZone
    {
        Above,
        Into,
        AtBottom,
    }

    private void OnLayerRowsDragItemsStarting(object sender, DragItemsStartingEventArgs args)
    {
        if (BeginLayerRowsDrag(args.Items) is null) args.Cancel = true;
    }

    private void OnLayerRowsDragOver(object sender, DragEventArgs args)
    {
        args.AcceptedOperation = LayerPanelDropOperation(args.GetPosition(RootGrid), KeyHeld(Windows.System.VirtualKey.Control));
        TrackLayerDropFeedback(args.GetPosition(RootGrid), args.AcceptedOperation);
        // Leave routing available to ListViewBase's native edge scrolling.
    }

    private void OnLayerRowsDragLeave(object sender, DragEventArgs args) => HideLayerDropFeedback();

    /// <summary>Draws what the already-tested zone decision means - the Mac's setDropRow (:406): an
    /// insertion line above the row a plain drop lands over, the stack's floor below the last row, and
    /// the row's own band highlighted when the drop goes INTO a folder. Only a plain layer drag gets
    /// the picture: the Mac's destination handler returns before setDropRow for effect and mask drags
    /// (draggedLayers is empty for them, :398-399), so those offer a drop but paint nothing here
    /// either. The line never eats input - the canvas is hit-test-transparent and the list keeps
    /// every gesture exactly as before.</summary>
    private void TrackLayerDropFeedback(Point rootPoint, DataPackageOperation accepted)
    {
        if (accepted == DataPackageOperation.None || CurrentLayerPanelDrag is not { Effect: null })
        { HideLayerDropFeedback(); return; }
        if (LayerDropTarget(rootPoint) is not { } target) { HideLayerDropFeedback(); return; }
        if (target.Zone == LayerDropZone.Into)
        {
            var row = layerItems[target.Index];
            Canvas.SetLeft(DropFolderBand, 0);
            Canvas.SetTop(DropFolderBand, row.TransformToVisual(LayerDropFeedback).TransformPoint(new Point(0, 0)).Y);
            DropFolderBand.Width = LayerDropFeedback.ActualWidth;
            DropFolderBand.Height = row.ActualHeight;
            DropFolderBand.Visibility = Visibility.Visible;
            DropInsertionLine.Visibility = Visibility.Collapsed;
            return;
        }
        var lineRow = target.Zone == LayerDropZone.AtBottom ? layerItems[^1] : layerItems[target.Index];
        double y = lineRow.TransformToVisual(LayerDropFeedback).TransformPoint(new Point(0, 0)).Y
            + (target.Zone == LayerDropZone.AtBottom ? lineRow.ActualHeight : 0);
        Canvas.SetLeft(DropInsertionLine, 0);
        Canvas.SetTop(DropInsertionLine, y);
        DropInsertionLine.Width = LayerDropFeedback.ActualWidth;
        DropInsertionLine.Visibility = Visibility.Visible;
        DropFolderBand.Visibility = Visibility.Collapsed;
    }

    private void HideLayerDropFeedback()
    {
        DropInsertionLine.Visibility = Visibility.Collapsed;
        DropFolderBand.Visibility = Visibility.Collapsed;
    }

    private LayerPanelDrag? BeginLayerRowsDrag(IList<object> items)
    {
        if (maskCopyGesture is not null || visibilitySwipe is not null || layerPanelDrag?.Effect is not null
            || !CanEditLayerCommands || document is not { } owner) return null;
        var ids = DraggedLayerRows(items);
        if (ids.Count == 0) return null;
        return layerPanelDrag = new(owner, ids, null, items.ToArray());
    }

    private LayerPanelDrag? BeginLayerEffectDrag(LayerEffectKind kind, Guid source)
    {
        if (!CanEditLayerCommands || document is not { } owner
            || LayerEffects.Parse(owner.Snapshot.Manifest.Layers.FirstOrDefault(layer => layer.Id == source)?.Effects)?.Contains(kind) != true) return null;
        return layerPanelDrag = new(owner, Array.Empty<Guid>(), (kind, source));
    }

    private void EndLayerPanelDrag(LayerPanelDrag? drag)
    {
        if (ReferenceEquals(drag, layerPanelDrag)) layerPanelDrag = null;
    }

    private void OnLayerRowsDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
        => CompleteLayerRowsDrag(args.Items);

    private void CompleteLayerRowsDrag(IReadOnlyList<object> items)
    {
        HideLayerDropFeedback();   // the session ended (dropped elsewhere or cancelled): no orphan line
        if (layerPanelDrag is { Effect: null, NativeItems: { } started } drag && started.SequenceEqual(items)) EndLayerPanelDrag(drag);
    }

    private DataPackageOperation LayerPanelDropOperation(Point point, bool copy)
    {
        if (!CanEditLayerCommands || CurrentLayerPanelDrag is not { } drag) return DataPackageOperation.None;
        var local = LayerList.TransformToVisual(RootGrid).Inverse.TransformPoint(point);
        if (local.X < 0 || local.Y < 0 || local.X >= LayerList.ActualWidth || local.Y >= LayerList.ActualHeight) return DataPackageOperation.None;
        if (drag.Effect is { } effect)
        {
            var under = LayerRowAt(point);
            return EffectDropOperation(true, under is not null,
                under is { } id && drag.Owner.CanCopyEffect(effect.Kind, effect.Source, id));
        }
        return LayerDropTarget(point) is { } target && LayerDropPlacement(target) is { } placement
            && drag.Layers.All(id => drag.Owner.CanPlaceLayer(id, placement.Parent))
            ? LayerDropOperation(placement, copy) : DataPackageOperation.None;
    }

    /// <summary>
    /// What a drop at this row would do, from what the row offers and the key the caller says is held: the
    /// Mac's Option-drag answers only <code>.copy</code> and a plain drag only <code>.move</code>
    /// (NativeLayerList.swift:398-404), which on Windows is Ctrl. A row that takes nothing offers nothing, so
    /// the pointer never promises a drop the document would refuse. Which key is down is read by the handler;
    /// this mapping is the part a probe can decide.
    /// </summary>
    internal static DataPackageOperation LayerDropOperation((Guid? Parent, Guid? Above, bool AtBottom)? placement, bool copyHeld)
        => placement is null ? DataPackageOperation.None
            : copyHeld ? DataPackageOperation.Copy : DataPackageOperation.Move;

    private void OnLayerRowsDrop(object sender, DragEventArgs args)
    {
        HideLayerDropFeedback();   // the picture ends where the decision becomes an action
        CompleteLayerPanelDrop(args.GetPosition(RootGrid), args.AcceptedOperation);
    }

    private bool CompleteLayerPanelDrop(Point point, DataPackageOperation accepted)
    {
        var drag = CurrentLayerPanelDrag;
        var operation = LayerPanelDropOperation(point, accepted == DataPackageOperation.Copy);
        EndLayerPanelDrag(drag);
        if (drag is null || operation == DataPackageOperation.None || (accepted & operation) == 0) return false;
        if (drag.Effect is { } effect)
        {
            return LayerRowAt(point) is { } under && CopyEffectToRow(effect.Kind, effect.Source, under);
        }
        return LayerDropTarget(point) is { } row && PlaceDroppedRows(drag.Layers, row.Index, row.Zone, operation == DataPackageOperation.Copy);
    }

    /// <summary>
    /// Every row the drag carries, in the order the panel shows them: the selected rows, leaving out
    /// anything inside a folder that is being dragged too, because the folder brings its own children
    /// along (the Mac's draggedLayers, NativeLayerList.swift:471). `starting` is the native selected set
    /// WinUI picked up; editor selection also covers rows awaiting a queued selection publication.
    /// </summary>
    private IReadOnlyList<Guid> DraggedLayerRows(IList<object> starting)
    {
        if (document is not { } owner) return Array.Empty<Guid>();
        var byId = owner.Snapshot.Manifest.Layers.ToDictionary(layer => layer.Id, layer => layer);
        var fromList = starting.OfType<ListViewItem>().Select(item => ((LayerRow)item.Tag).Layer.Id);
        var dragged = layerItems
            .Select(grid => ((LayerRow)grid.Tag).Layer.Id)
            .Where(id => id == selectedLayerId || selectedLayerIds.Contains(id))
            .Concat(fromList)
            .ToHashSet();
        var roots = layerItems
            .Select(grid => ((LayerRow)grid.Tag).Layer)
            .Where(layer => dragged.Contains(layer.Id) && !HasDraggedAncestor(layer, byId, dragged))
            .Select(layer => layer.Id)
            .ToHashSet();
        // The order the loop places them in is the caller's business, but the list order is this one's.
        return layerItems.Select(grid => ((LayerRow)grid.Tag).Layer.Id).Where(roots.Contains).ToArray();
    }

    private static bool HasDraggedAncestor(ProjectLayerRecord layer, IReadOnlyDictionary<Guid, ProjectLayerRecord> byId, HashSet<Guid> dragged)
    {
        Guid? parent = layer.ParentId;
        for (int depth = 0; depth < 64 && parent is { } id; depth++)
        {
            if (dragged.Contains(id)) return true;
            parent = byId.TryGetValue(id, out var node) ? node.ParentId : null;
        }
        return false;
    }

    /// <summary>The row a point falls on, and which part of it. The top band of any row means "land above
    /// this row"; the body of a folder means "land inside it"; the body of a plain row is above it too, as
    /// in the Mac where only a group row can answer `.on`. Past the last row is the bottom of the stack,
    /// and above the first row is no drop at all.</summary>
    private (int Index, LayerDropZone Zone)? LayerDropTarget(Point point)
    {
        for (int index = 0; index < layerItems.Count; index++)
        {
            var grid = layerItems[index];
            double y = grid.TransformToVisual(RootGrid).Inverse.TransformPoint(point).Y;
            if (y < 0 || y >= grid.ActualHeight) continue;
            var zone = y < grid.ActualHeight * 0.3 || !((LayerRow)grid.Tag).IsGroupView ? LayerDropZone.Above : LayerDropZone.Into;
            return (index, zone);
        }
        if (layerItems.Count > 0 && layerItems[^1].TransformToVisual(RootGrid).Inverse.TransformPoint(point).Y >= 0)
            return (layerItems.Count, LayerDropZone.AtBottom);
        return null;
    }

    /// <summary>Where a drop on this row puts a layer, in the document's own terms. Null when the row it
    /// names is not a place anything can go — a folder dropping into itself gets refused here, so the
    /// pointer never offers it either.</summary>
    private (Guid? Parent, Guid? Above, bool AtBottom)? LayerDropPlacement((int Index, LayerDropZone Zone) target)
    {
        if (document is not { } owner) return null;
        if (target.Zone == LayerDropZone.AtBottom) return (null, null, true);
        if (target.Index >= layerItems.Count) return null;
        var anchor = ((LayerRow)layerItems[target.Index].Tag).Layer;
        if (target.Zone == LayerDropZone.Into) return (anchor.Id, null, false);
        return (anchor.ParentId, anchor.Id, false);
    }

    /// <summary>
    /// The drop itself: the rows go in one after the other at the anchor as a single undo step, in the order
    /// the panel shows them when they land above a row (so the last ends up nearest the anchor) and
    /// bottom-up when they land in a folder (each lands on top), exactly as the Mac's place loop. <paramref
    /// name="copy"/> is the Option-drag: each row is duplicated and the copy lands on the row, which is the
    /// Mac's duplicateLayer(in:above:atBottom:) doing its two things in that order. Either way the rows that
    /// moved (or the copies made) stay selected so they can be dragged on again, and a folder the drop landed
    /// in opens to show what came in.
    /// </summary>
    private bool PlaceDroppedRows(IReadOnlyList<Guid> ids, int index, LayerDropZone zone, bool copy = false)
    {
        if (!CanEditLayerCommands || document is not { } owner) return false;
        if (LayerDropPlacement((index, zone)) is not { } placement) return false;
        var order = zone == LayerDropZone.Into ? ids.Reverse().ToArray() : ids.ToArray();
        IReadOnlyList<Guid> landed = ids;
        if (copy)
        {
            if (!owner.DuplicateLayersAt(order, placement.Parent, placement.Above, placement.AtBottom, out var copies)) return false;
            landed = copies;
        }
        else if (!owner.PlaceLayers(order, placement.Parent, placement.Above, placement.AtBottom)) return false;
        if (placement.Parent is { } folder && zone == LayerDropZone.Into) collapsedGroups.Remove(folder);
        selectedLayerIds.Clear();
        selectedLayerIds.UnionWith(landed);
        // A move keeps the topmost row it dragged as the primary; a copy answers with the layer the document
        // just made active, which is the copy of the last row placed — the two answers the Mac gives.
        selectedLayerId = copy ? owner.Snapshot.Manifest.ActiveLayerId : ids[0];
        paintOnMask = false;
        PaintMaskCheck.IsChecked = false;
        selectingLayer = true;
        try { if (selectedLayerId is { } primary) owner.SetActiveLayer(primary); } finally { selectingLayer = false; }
        RefreshLayerPanel();
        RenderCanvas();
        return true;
    }
}
