namespace Compositor.Core;

public sealed partial class EditorDocument
{
    private ImageStrokeGrid? imageStrokeGrid;
    private RectI imageStrokeBounds;

    private bool BeginGrowingImageStroke(object key, Guid id, BrushSettings brush, PointD point, bool erase)
    {
#if DEBUG
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        DebugStrokeCloneMs = DebugStrokeReplaceMs = DebugStrokeDepositMs = DebugStrokePaintMs = DebugStrokeNotifyMs = 0;
#endif
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) return false;
        var layer = Find(id)!;
        // The plane is published through ReplaceLayerImage, whose BeginChange captures the
        // source's own bytes as the undo basis: a refusal or an empty stroke never opens a step.
        var original = Snapshot.Images.GetValueOrDefault(id);
        if (KeepsAlpha(id) && original is null) return false;
        if (erase && original is null) return false; // erasing where there is nothing to erase
        var grid = ImageStrokeGrid.Create(Snapshot, layer, original, brush);
        if (grid is null || !grid.CanMaterialize(grid.CandidateBounds(grid.SourceBounds, point, point))) return false;
#if DEBUG
        DebugStrokeCloneMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        started = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        var coverage = new TileStroke(brush, grid.Placement, grid.Width, grid.Height);
        var touched = coverage.SweepSegment(point);
        if (touched.Width == 0 || touched.Height == 0) return false;
        // A blank layer has no body to carry: the plane starts as the touched rect alone.
        // Growth follows the pointer's own reach, not a snapped tile, so a stroke that
        // stays inside the source never moves the layer or discards the warm compositor.
        var bounds = KeepsAlpha(id) || !CanEditLayer(id, LayerLocks.Position) ? grid.SourceBounds
            : original is null ? touched : RectI.Union(grid.SourceBounds, touched);
#if DEBUG
        DebugStrokeDepositMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        started = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        var working = grid.Materialize(bounds);
#if DEBUG
        DebugStrokeCloneMs += System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        started = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        coverage.PaintImageGrid(working, grid, bounds, touched, ActiveSelection, erase, KeepsAlpha(id));
#if DEBUG
        DebugStrokePaintMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        started = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        strokeLayerId = id; strokeErase = erase; strokeBaseImage = original; strokeImage = working;
        stroke = coverage; imageStrokeGrid = grid; imageStrokeBounds = bounds; strokeLastPoint = point;
        strokeMask = strokeBaseMask = null;
        bool joins = key is not null && Equals(key, coalesceKey); coalesceKey = key;
        if (!joins) BeginChange(operation: erase ? "Erase" : "Brush"); // the memento holds the source's bytes and the old placement
        if (bounds != grid.SourceBounds) layer.Transform = grid.TransformFor(bounds); // the plane places, never squashes
        if (layer.ImageFile is null) layer.ImageFile = ProjectValidator.Uuid(id) + ".png";
        layer.Text = null; layer.Shape = null; // painting retires live metadata; Undo restores pixels, text and shape
        PublishGrowingImage(id, working);
        if (joins) Changed?.Invoke(); else EndChange();
#if DEBUG
        DebugStrokeReplaceMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
#endif
        return true;
    }

    private bool ContinueGrowingImageStroke(Guid id, PointD point)
    {
        if (imageStrokeGrid is not { } grid || stroke is not { } coverage || strokeImage is not { } working
            || !double.IsFinite(point.X) || !double.IsFinite(point.Y)
            || !grid.CanMaterialize(grid.CandidateBounds(imageStrokeBounds, strokeLastPoint ?? point, point))) return false;
#if DEBUG
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        coverage.RetireTail(); var touched = coverage.SweepSegment(point);
#if DEBUG
        DebugStrokeDepositMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        started = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        strokeLastPoint = point;
        var bounds = KeepsAlpha(id) || !CanEditLayer(id, LayerLocks.Position) ? imageStrokeBounds : RectI.Union(imageStrokeBounds, touched); // grows only when the tip leaves the plane
        bool grows = bounds != imageStrokeBounds;
        if (grows) working = grid.Materialize(bounds, working, imageStrokeBounds);
        coverage.PaintImageGrid(working, grid, bounds, touched, ActiveSelection, strokeErase, KeepsAlpha(id));
#if DEBUG
        DebugStrokePaintMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        started = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        if (grows)
        {
            strokeImage = working; imageStrokeBounds = bounds;
            if (bounds != grid.SourceBounds && Find(id) is { } moved) moved.Transform = grid.TransformFor(bounds);
            PublishGrowingImage(id, working); Changed?.Invoke();
        }
        else PixelsChanged?.Invoke();
#if DEBUG
        DebugStrokeNotifyMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
#endif
        return true;
    }

    private void PublishGrowingImage(Guid id, Surface working)
    {
        Snapshot = new() { Manifest = Snapshot.Manifest,
            Images = new Dictionary<Guid, Surface>(Snapshot.Images) { [id] = working }, Masks = Snapshot.Masks };
    }

    /// <summary>The commit at the end of the stroke, as `paintSnapshot` on the Mac: crop to
    /// the source's rectangle ∪ the painted alpha, keep the plane's world place, and re-edge
    /// an attached mask with white over the new area instead of stretching its pixels. A
    /// placed (independent) mask keeps its own pixels and place.</summary>
    private void CommitGrowingImageStroke(Guid id)
    {
        if (imageStrokeGrid is not { } grid || strokeImage is not { } working) return;
        imageStrokeGrid = null;
        CommitGrowingPlane(id, grid, imageStrokeBounds, working);
    }

    /// <summary>Settles a grown RGBA plane: crop, placement, and the attached mask's re-edge.
    /// Brush, clone and blur strokes all land here, so a stamp and a dab commit alike.</summary>
    private void CommitGrowingPlane(Guid id, ImageStrokeGrid grid, RectI bounds, Surface working)
    {
        var layer = Find(id);
        if (layer is null) return;
        var crop = grid.CommittedBounds(bounds, working);
        if (crop.Width == 0 || crop.Height == 0)
        {
            // Nothing painted anywhere and no source to keep (a fully erased blank layer):
            // commit the touched tiles themselves, exactly BrushCommit's fullBounds fallback.
            crop = bounds;
        }
        // The common case settles where it began: a canvas-sized layer's plane already is
        // the source, so skip a republish and the App's settle frame.
        if (crop == bounds && crop == grid.SourceBounds && ReferenceEquals(Snapshot.Images.GetValueOrDefault(id), working)) return;
        // An owned copy keeps the plane's larger buffer out of the undo history's hands.
        Surface final = crop == bounds ? working
            : working.Crop(crop.X - bounds.X, crop.Y - bounds.Y, crop.Width, crop.Height);
        if (crop != bounds) layer.Transform = grid.TransformFor(crop);
        ReedgeAttachedMask(layer, grid, crop);
        PublishGrowingImage(id, final);
        // Settling at the source's own rectangle and placement moves no pixels' geometry, so
        // it is a pixel change like the old in-place stroke: the warm compositor survives.
        // Anything else (a grown asset, a new placement, a re-edged mask) is structural.
        bool settled = crop == grid.SourceBounds && grid.Original is { } body
            && body.Width == final.Width && body.Height == final.Height;
        if (settled) PixelsChanged?.Invoke(); else Changed?.Invoke();
    }

    /// <summary>Opens the stroke's plane: one undo step whose memento still holds the source's
    /// bytes and the old placement, and the plane's own rectangle from the first dab.</summary>
    private void OpenGrowingPlane(ProjectLayerRecord layer, ImageStrokeGrid grid, RectI bounds, Surface working, object key)
    {
        bool joins = key is not null && Equals(key, coalesceKey); coalesceKey = key;
        if (!joins) BeginChange(operation: "Clone");
        if (bounds != grid.SourceBounds) layer.Transform = grid.TransformFor(bounds); // the plane places, never squashes
        if (layer.ImageFile is null) layer.ImageFile = ProjectValidator.Uuid(layer.Id) + ".png";
        layer.Text = null; layer.Shape = null; // painting retires live metadata; Undo restores pixels, text and shape
        PublishGrowingImage(layer.Id, working);
        if (joins) Changed?.Invoke(); else EndChange();
    }

    private void ReedgeAttachedMask(ProjectLayerRecord layer, ImageStrokeGrid grid, RectI crop)
    {
        if (layer.MaskPlacement is not null || Snapshot.Masks.GetValueOrDefault(layer.Id) is not { } mask) return;
        if (crop == grid.SourceBounds) return; // the plane still is the source: nothing to re-edge
        var output = new MaskSurface(crop.Width, crop.Height);
        // New canvas area has no pre-existing mask restriction: the old coverage stays
        // aligned at its grid place, and everything else takes the mask's edge tone.
        output.Pixels.AsSpan().Fill(MaskDisplay.Background(mask));
        bool direct = mask.Width == grid.SourceBounds.Width && mask.Height == grid.SourceBounds.Height;
        for (int y = grid.SourceBounds.Y; y < grid.SourceBounds.Y + grid.SourceBounds.Height; y++)
            for (int x = grid.SourceBounds.X; x < grid.SourceBounds.X + grid.SourceBounds.Width; x++)
            {
                if (x < crop.X || y < crop.Y || x >= crop.X + crop.Width || y >= crop.Y + crop.Height) continue;
                int sx = direct ? x - grid.SourceBounds.X : Math.Clamp((int)((x - grid.SourceBounds.X + .5) * mask.Width / grid.SourceBounds.Width), 0, mask.Width - 1);
                int sy = direct ? y - grid.SourceBounds.Y : Math.Clamp((int)((y - grid.SourceBounds.Y + .5) * mask.Height / grid.SourceBounds.Height), 0, mask.Height - 1);
                output.Pixels[(y - crop.Y) * output.Width + x - crop.X] = mask.Pixels[sy * mask.Width + sx];
            }
        Snapshot = new() { Manifest = Snapshot.Manifest, Images = Snapshot.Images,
            Masks = new Dictionary<Guid, MaskSurface>(Snapshot.Masks) { [layer.Id] = output } };
    }
}
