using Compositor.Core;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private sealed class SelectionMoveSession(EditorDocument owner, Selection original, PointD anchor, List<double> xs, List<double> ys)
    {
        public EditorDocument Owner { get; } = owner;
        public Selection Original { get; } = original;
        public PointD Anchor { get; } = anchor;
        public List<double> Xs { get; } = xs;
        public List<double> Ys { get; } = ys;
        public PointD Offset { get; set; }
    }

    private SelectionMoveSession? selectionMove;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? selectionAutoscroll;
    private PointD? selectionAutoscrollPoint;
    private VirtualKeyModifiers selectionAutoscrollModifiers;

    private static bool IsSelectionTool(CanvasTool value) => value is CanvasTool.RectSelect or CanvasTool.EllipseSelect or CanvasTool.Lasso or CanvasTool.Wand;

    private bool TryBeginSelectionMove(PointD point, VirtualKeyModifiers modifiers)
    {
        if (!IsSelectionTool(tool) || !CanEditSelection || selectionWorkResult is not null || lassoPoints is not null
            || modifiers.HasFlag(VirtualKeyModifiers.Control) || SelectionModeAtPress(modifiers) != LassoSelectionMode.Replace
            || document is not { ActiveSelection: { IsEmpty: false } original } owner || !original.Contains(point)) return false;
        CompleteMarqueePreview();
        var manifest = owner.Snapshot.Manifest;
        var xs = new List<double> { 0, manifest.Width }; var ys = new List<double> { 0, manifest.Height };
        var byId = manifest.Layers.ToDictionary(layer => layer.Id);
        // Guides.swift uses every visible raster's full geometry, including the active layer,
        // and only target edges. Effects, canvas clipping and mask placement do not change it.
        foreach (var id in DrawOrder.Resolve(manifest.Layers).Drawn)
        {
            if (!owner.Snapshot.Images.ContainsKey(id)) continue;
            var corners = TransformDrag.HandlePositions(byId[id].Transform);
            xs.Add(Math.Round(corners.Min(p => p.X), MidpointRounding.AwayFromZero));
            xs.Add(Math.Round(corners.Max(p => p.X), MidpointRounding.AwayFromZero));
            ys.Add(Math.Round(corners.Min(p => p.Y), MidpointRounding.AwayFromZero));
            ys.Add(Math.Round(corners.Max(p => p.Y), MidpointRounding.AwayFromZero));
        }
        if (manifest.Guides is { } guides) foreach (var guide in guides)
            (guide.Axis == GuideAxis.Vertical ? xs : ys).Add(guide.Position);
        selectionMove = new(owner, original, point, xs, ys);
        RefreshNavigationCursor(); return true;
    }

    private void MoveSelectionTo(PointD point, VirtualKeyModifiers modifiers)
    {
        if (selectionMove is not { } move || !ReferenceEquals(move.Owner, document)) return;
        double dx = point.X - move.Anchor.X, dy = point.Y - move.Anchor.Y;
        if (!double.IsFinite(dx) || !double.IsFinite(dy)) return;
        bool horizontal = true, vertical = true;
        if (modifiers.HasFlag(VirtualKeyModifiers.Shift))
        {
            if (Math.Abs(dx) >= Math.Abs(dy)) { dy = 0; vertical = false; }
            else { dx = 0; horizontal = false; }
        }
        dx = Math.Round(dx, MidpointRounding.AwayFromZero); dy = Math.Round(dy, MidpointRounding.AwayFromZero);
        double? lineX = null, lineY = null;
        var original = move.Original.OutlineBounds;
        if (!modifiers.HasFlag(VirtualKeyModifiers.Control))
        {
            var snap = TransformSnap.OffsetFor(new(original.X + dx, original.Y + dy, original.Width, original.Height),
                horizontal ? move.Xs : [], vertical ? move.Ys : [], 10 / Math.Max(viewport.PointsPerPixel, .0001));
            dx += snap.Offset.Width; dy += snap.Offset.Height; lineX = snap.X; lineY = snap.Y;
        }
        dx = Math.Round(dx, MidpointRounding.AwayFromZero); dy = Math.Round(dy, MidpointRounding.AwayFromZero);
        ShowSnapLines(new(original.X + dx, original.Y + dy, original.Width, original.Height), lineX, lineY,
            move.Owner.Snapshot.Manifest.Width, move.Owner.Snapshot.Manifest.Height);
        if (dx is < int.MinValue or > int.MaxValue || dy is < int.MinValue or > int.MaxValue || move.Offset == new PointD(dx, dy)) return;
        move.Offset = new(dx, dy);
        publishingSelection = true;
        try { move.Owner.SetSelection(move.Original.Translated((int)dx, (int)dy)); }
        finally { publishingSelection = false; }
    }

    private void EndSelectionMove(bool click = false)
    {
        if (selectionMove is not { } move) return;
        var result = move.Owner.ActiveSelection;
        ClearSelectionMove();
        if (!ReferenceEquals(move.Owner, document)) return;
        bool moved = move.Offset != new PointD(0, 0);
        if (moved)
        {
            publishingSelection = true;
            try { move.Owner.SetSelection(move.Original); move.Owner.SetSelectionUndoable(result); }
            finally { publishingSelection = false; }
            SetStatus(Loc.T("S_SelectionMoved", "Selection outline moved."));
        }
        else if (click)
        {
            if (tool == CanvasTool.Wand) SelectWithWand(move.Anchor, VirtualKeyModifiers.None);
            else QueueSelectionWork((_, _) => null, false, Loc.T("S_Deselected"));
        }
    }

    private void ClearSelectionMove()
    {
        selectionMove = null; StopSelectionAutoscroll();
        SnapLineX.Visibility = SnapLineY.Visibility = Visibility.Collapsed;
        CanvasStage.ReleasePointerCaptures(); RefreshNavigationCursor();
    }

    private bool NudgeSelection(VirtualKey key, VirtualKeyModifiers modifiers, bool textInput = false)
    {
        if (textInput || renamingLayer || textDialogOpen || !IsSelectionTool(tool) || !CanEditSelection || lassoPoints is not null
            || document?.ActiveSelection is not { IsEmpty: false } || modifiers.HasFlag(VirtualKeyModifiers.Control)
            || modifiers.HasFlag(VirtualKeyModifiers.Menu) || key is not (VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down)) return false;
        int step = modifiers.HasFlag(VirtualKeyModifiers.Shift) ? 10 : 1;
        int dx = key == VirtualKey.Left ? -step : key == VirtualKey.Right ? step : 0;
        int dy = key == VirtualKey.Up ? -step : key == VirtualKey.Down ? step : 0;
        QueueSelectionWork((previous, _) => previous?.Translated(dx, dy), true, Loc.T("S_SelectionMoved", "Selection outline moved.")); return true;
    }

    private bool HandleSelectionMoveKey(KeyRoutedEventArgs args)
    {
        VirtualKeyModifiers modifiers = SelectionKeyModifiers();
        if (args.KeyStatus.IsMenuKeyDown) modifiers |= VirtualKeyModifiers.Menu;
        var (nudged, lost) = CanvasTranslate(new ShortcutChord(args.Key, modifiers));
        if (lost && args.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down)
        { args.Handled = true; return true; }
        if (!NudgeSelection(nudged.Key, nudged.Modifiers, args.OriginalSource is TextBox or RichEditBox)) return false;
        args.Handled = true; return true;
    }

    private static VirtualKeyModifiers SelectionKeyModifiers() => (KeyHeld(VirtualKey.Shift) ? VirtualKeyModifiers.Shift : 0)
            | (KeyHeld(VirtualKey.Control) ? VirtualKeyModifiers.Control : 0)
            | (KeyHeld(VirtualKey.Menu) ? VirtualKeyModifiers.Menu : 0);

    private void TrackSelectionAutoscroll(PointD point, VirtualKeyModifiers modifiers)
    {
        if (selectionMove is null && (dragStart is null || tool is not (CanvasTool.RectSelect or CanvasTool.EllipseSelect))) return;
        selectionAutoscrollPoint = point; selectionAutoscrollModifiers = modifiers;
        if (SelectionAutoscrollDelta(point) == new PointD(0, 0)) { StopSelectionAutoscroll(); return; }
        if (selectionAutoscroll is null)
        {
            selectionAutoscroll = DispatcherQueue.CreateTimer(); selectionAutoscroll.Interval = TimeSpan.FromSeconds(1.0 / 60);
            selectionAutoscroll.Tick += (_, _) => StepSelectionAutoscroll(SelectionKeyModifiers());
        }
        if (!selectionAutoscroll.IsRunning) selectionAutoscroll.Start();
    }

    private PointD SelectionAutoscrollDelta(PointD point)
    {
        // EditorCanvas.swift's 12-point margin and capped linear speed, in view points.
        static double Speed(double past) => past <= 0 ? 0 : Math.Min(40, 2 + past * .4);
        return new(Speed(12 - point.X) - Speed(point.X - (viewport.Width - 12)),
            Speed(12 - point.Y) - Speed(point.Y - (viewport.Height - 12)));
    }

    private void StepSelectionAutoscroll(VirtualKeyModifiers? modifiers = null)
    {
        if (windowClosed || document is null || selectionAutoscrollPoint is not { } point
            || selectionMove is null && (dragStart is null || tool is not (CanvasTool.RectSelect or CanvasTool.EllipseSelect)))
        { StopSelectionAutoscroll(); return; }
        var delta = SelectionAutoscrollDelta(point);
        if (delta == new PointD(0, 0)) { StopSelectionAutoscroll(); return; }
        viewport.Pan(delta); QueueViewportFrame();
        var pixel = viewport.ToDocument(point);
        var flags = modifiers ?? selectionAutoscrollModifiers;
        if (selectionMove is not null) MoveSelectionTo(pixel, flags);
        else if (dragStart is { } anchor) ShowMarqueePreview(anchor, pixel, MarqueeSquare(flags));
    }

    private void StopSelectionAutoscroll()
    { selectionAutoscroll?.Stop(); selectionAutoscrollPoint = null; }
}
