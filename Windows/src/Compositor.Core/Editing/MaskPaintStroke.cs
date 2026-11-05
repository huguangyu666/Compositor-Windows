namespace Compositor.Core;

public sealed partial class EditorDocument
{
    private MaskStrokeGrid? maskStrokeGrid;
    private RectI maskStrokeBounds;

    private bool BeginGrowingMaskStroke(object key, Guid id, BrushSettings brush, PointD point, bool erase)
    {
#if DEBUG
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        DebugStrokeCloneMs = DebugStrokeReplaceMs = DebugStrokeDepositMs = DebugStrokePaintMs = DebugStrokeNotifyMs = 0;
#endif
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) return false;
        var layer = Find(id)!;
        var original = Snapshot.Masks.GetValueOrDefault(id) ?? MaskSurface.Solid(true);
        var grid = MaskStrokeGrid.Create(Snapshot, layer, original, brush);
        if (grid is null || !grid.CanMaterialize(grid.CandidateBounds(grid.SourceBounds, point, point))) return false;
#if DEBUG
        DebugStrokeCloneMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        started = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        var coverage = new TileStroke(brush, grid.Placement, grid.Width, grid.Height);
        var touched = coverage.SweepSegment(point); var painted = coverage.Bounds();
        if (painted.Width == 0 || painted.Height == 0) return false;
        var bounds = RectI.Union(grid.SourceBounds, painted);
#if DEBUG
        DebugStrokeDepositMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        started = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        var working = grid.Materialize(bounds);
#if DEBUG
        DebugStrokeCloneMs += System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        started = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        coverage.PaintMaskGrid(working, grid, bounds, touched, ActiveSelection, erase);
#if DEBUG
        DebugStrokePaintMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        started = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        strokeLayerId = id; strokeErase = erase; strokeBaseMask = original; strokeMask = working;
        stroke = coverage; maskStrokeGrid = grid; maskStrokeBounds = bounds; strokeLastPoint = point;
        strokeImage = strokeBaseImage = null;
        bool joins = key is not null && Equals(key, coalesceKey); coalesceKey = key;
        if (!joins) BeginChange(operation: erase ? "Erase" : "Brush");
        PublishGrowingMask(layer, working, bounds, grid);
        if (joins) Changed?.Invoke(); else EndChange();
#if DEBUG
        DebugStrokeReplaceMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
#endif
        return true;
    }

    private bool ContinueGrowingMaskStroke(Guid id, PointD point)
    {
        if (maskStrokeGrid is not { } grid || stroke is not { } coverage || strokeMask is not { } working
            || !double.IsFinite(point.X) || !double.IsFinite(point.Y)
            || !grid.CanMaterialize(grid.CandidateBounds(maskStrokeBounds, strokeLastPoint ?? point, point))) return false;
#if DEBUG
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        coverage.RetireTail(); var touched = coverage.SweepSegment(point);
#if DEBUG
        DebugStrokeDepositMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        started = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        strokeLastPoint = point; var painted = coverage.TailBounds();
        var bounds = painted.Width == 0 || painted.Height == 0 ? maskStrokeBounds : RectI.Union(maskStrokeBounds, painted);
        bool grows = bounds != maskStrokeBounds;
        if (grows) working = grid.Materialize(bounds, working, maskStrokeBounds);
        coverage.PaintMaskGrid(working, grid, bounds, touched, ActiveSelection, strokeErase);
#if DEBUG
        DebugStrokePaintMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        started = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        if (grows)
        {
            strokeMask = working; maskStrokeBounds = bounds;
            PublishGrowingMask(Find(id)!, working, bounds, grid); Changed?.Invoke();
        }
        else PixelsChanged?.Invoke();
#if DEBUG
        DebugStrokeNotifyMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
#endif
        return true;
    }

    private void PublishGrowingMask(ProjectLayerRecord layer, MaskSurface working, RectI bounds, MaskStrokeGrid grid)
    {
        if (layer.MaskFile is null) { layer.MaskFile = ProjectValidator.Uuid(layer.Id) + ".mask.png"; layer.MaskEnabled = true; }
        if (bounds != grid.SourceBounds || layer.MaskPlacement is not null) layer.MaskPlacement = grid.TransformFor(bounds);
        Snapshot = new() { Manifest = Snapshot.Manifest, Images = Snapshot.Images,
            Masks = new Dictionary<Guid, MaskSurface>(Snapshot.Masks) { [layer.Id] = working } };
    }
}
