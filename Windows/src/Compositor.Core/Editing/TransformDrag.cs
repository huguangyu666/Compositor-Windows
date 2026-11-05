namespace Compositor.Core;

/// <summary>Drag modes for a layer transform, mirroring the macOS TransformDrag.Mode.</summary>
public enum TransformDragMode
{
    Move,
    Resize,
    Rotate,
    Distort,
}

/// <summary>Where a drag began: the layer's original transform, the start point, and the mode.</summary>
public sealed record TransformDragState(
    LayerTransform Original,
    PointD Start,
    TransformDragMode Mode,
    int ResizeIndex = 0,
    PointD[]? OriginalCorners = null);

/// <summary>
/// Drag→transform semantics, ported from the macOS TransformDrag.updated (LayerTransform.swift):
///
/// - Move: the origin follows the pointer; Shift locks to the dominant axis.
/// - Resize: the handle opposite the grabbed one is the anchor (Option anchors at the center).
///   Distances are measured in the layer's rotated frame, so edges stay edge-on. Dragging past
///   the opposite side flips the layer on that axis (the size stays positive and the flip flag
///   toggles), as a negative scale would. LockRatio projects onto the original diagonal for
///   proportional scaling; Shift inverts that meaning, as in Photoshop.
/// - Rotate: the angle from the center to the pointer; Shift snaps to 15° steps.
///
/// Invalid results (NaN, out-of-range sizes) return the original, as on the Mac.
/// </summary>
public static class TransformDrag
{
    /// <summary>The eight handles' unit coordinates (LayerTransform.handles).</summary>
    public static readonly PointD[] Handles =
    {
        new(0, 0), new(0.5, 0), new(1, 0),
        new(1, 0.5), new(1, 1), new(0.5, 1),
        new(0, 1), new(0, 0.5),
    };

    /// <summary>The handles' document positions for a transform, in handle order.</summary>
    public static PointD[] HandlePositions(LayerTransform transform) =>
        Handles.Select(transform.Point).ToArray();

    public static PointD[] HandlePositions(PointD[] corners) => Enumerable.Range(0, 8).Select(i => i % 2 == 0
        ? corners[i / 2] : new PointD((corners[i / 2].X + corners[(i / 2 + 1) % 4].X) / 2,
            (corners[i / 2].Y + corners[(i / 2 + 1) % 4].Y) / 2)).ToArray();

    /// <summary>LayerTransform.swift: free corners, paired edge corners, or the entire shape; Shift locks the dominant axis.</summary>
    public static PointD[]? UpdatedCorners(TransformDragState drag, PointD point, bool shift = false)
    {
        if (drag.OriginalCorners is not { Length: 4 } original || drag.Mode is not (TransformDragMode.Move or TransformDragMode.Distort)) return null;
        double dx = point.X - drag.Start.X, dy = point.Y - drag.Start.Y;
        if (shift) { if (Math.Abs(dx) >= Math.Abs(dy)) dy = 0; else dx = 0; }
        var result = original.ToArray();
        int index = drag.ResizeIndex;
        if (drag.Mode == TransformDragMode.Distort && index is < 0 or > 7) return null;
        for (int i = 0; i < 4; i++)
            if (drag.Mode == TransformDragMode.Move || i == index / 2 || index % 2 == 1 && i == (index / 2 + 1) % 4)
                result[i] = new(result[i].X + dx, result[i].Y + dy);
        return result;
    }

    /// <summary>
    /// The rotation handle: above the top-middle handle, along the box's own up direction,
    /// 28 document pixels out (the macOS overlay's offset).
    /// </summary>
    public static PointD RotationHandle(LayerTransform transform)
    {
        var top = transform.Point(Handles[1]);
        double radians = transform.Radians;
        return new PointD(top.X + Math.Sin(radians) * 28, top.Y - Math.Cos(radians) * 28);
    }

    /// <summary>
    /// Which drag mode a document point hits: the rotation handle first, then the eight
    /// handles, then the four edges (10 px reach, the macOS overlay's distances).
    /// </summary>
    public static (TransformDragMode Mode, int Index)? HitTest(LayerTransform transform, PointD point, double reach, bool rotationVisible = true)
    {
        var handles = HandlePositions(transform);
        if (rotationVisible)
        {
            var rotation = RotationHandle(transform);
            if (Distance(point, rotation) <= reach) return (TransformDragMode.Rotate, 0);
        }
        return HitHandles(handles, point, reach);
    }

    public static (TransformDragMode Mode, int Index)? HitTest(PointD[] corners, PointD point, double reach) =>
        HitHandles(HandlePositions(corners), point, reach);

    private static (TransformDragMode Mode, int Index)? HitHandles(PointD[] handles, PointD point, double reach)
    {
        for (int i = 0; i < handles.Length; i++)
        {
            if (Distance(point, handles[i]) <= reach) return (TransformDragMode.Resize, i);
        }
        for (int i = 0; i < handles.Length; i += 2)
        {
            var a = handles[i];
            var b = handles[(i + 2) % handles.Length];
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double lengthSquared = dx * dx + dy * dy;
            if (lengthSquared <= 0) continue;
            double t = ((point.X - a.X) * dx + (point.Y - a.Y) * dy) / lengthSquared;
            if (t is >= 0 and <= 1)
            {
                double px = a.X + t * dx, py = a.Y + t * dy;
                if (Distance(point, new PointD(px, py)) <= reach) return (TransformDragMode.Resize, i + 1);
            }
        }
        return null;
    }

    private static double Distance(PointD a, PointD b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>The transform after dragging to <paramref name="point"/>.</summary>
    public static LayerTransform Updated(TransformDragState drag, PointD point, bool lockRatio, bool shift, bool option)
    {
        var original = drag.Original;
        var result = new LayerTransform
        {
            Origin = original.Origin,
            Size = original.Size,
            Rotation = original.Rotation,
            FlipX = original.FlipX,
            FlipY = original.FlipY,
            Sampling = original.Sampling,
        };
        switch (drag.Mode)
        {
            case TransformDragMode.Move:
            {
                double dx = point.X - drag.Start.X, dy = point.Y - drag.Start.Y;
                if (shift)
                {
                    if (Math.Abs(dx) >= Math.Abs(dy)) dy = 0;
                    else dx = 0;
                }
                result.Origin = new PointD(original.Origin.X + dx, original.Origin.Y + dy);
                break;
            }
            case TransformDragMode.Rotate:
            {
                var center = original.Center;
                double delta = Math.Atan2(point.Y - center.Y, point.X - center.X)
                    - Math.Atan2(drag.Start.Y - center.Y, drag.Start.X - center.X);
                result.Rotation = original.Rotation + delta * 180 / Math.PI;
                if (shift) result.Rotation = Math.Round(result.Rotation / 15, MidpointRounding.AwayFromZero) * 15;
                break;
            }
            case TransformDragMode.Resize:
            {
                int index = drag.ResizeIndex;
                var handle = Handles[index % Handles.Length];
                var anchorUnit = option ? new PointD(0.5, 0.5) : new PointD(1 - handle.X, 1 - handle.Y);
                var anchor = original.Point(anchorUnit);
                // Use the initial handle plus the pointer delta to avoid a jump on grab.
                var initialHandle = original.Point(handle);
                double dx = initialHandle.X + point.X - drag.Start.X - anchor.X;
                double dy = initialHandle.Y + point.Y - drag.Start.Y - anchor.Y;
                // Center-to-handle distances cover half the size on each axis.
                double span = option ? 2 : 1;
                double radians = original.Radians;
                double cos = Math.Cos(radians), sin = Math.Sin(radians);
                double localX = (dx * cos + dy * sin) * span;
                double localY = (-dx * sin + dy * cos) * span;
                double sx = handle.X * 2 - 1, sy = handle.Y * 2 - 1;
                // Dragging a handle past the opposite side turns the layer over rather than
                // stopping at nothing: the size stays positive and the axis flips.
                double rawWidth = sx == 0 ? original.Size.Width : localX * sx;
                double rawHeight = sy == 0 ? original.Size.Height : localY * sy;
                bool mirroredX = rawWidth < 0, mirroredY = rawHeight < 0;
                double width = Math.Max(1, Math.Abs(rawWidth));
                double height = Math.Max(1, Math.Abs(rawHeight));
                if (lockRatio != shift)
                {
                    double factor;
                    if (sx == 0) factor = height / original.Size.Height;
                    else if (sy == 0) factor = width / original.Size.Width;
                    else
                    {
                        // Project onto the original diagonal for proportional scaling.
                        factor = Math.Max(1 / Math.Min(original.Size.Width, original.Size.Height),
                            (localX * sx * original.Size.Width + localY * sy * original.Size.Height)
                            / (original.Size.Width * original.Size.Width + original.Size.Height * original.Size.Height));
                    }
                    width = original.Size.Width * factor;
                    height = original.Size.Height * factor;
                }
                result.Size = new SizeD(width, height);
                if (mirroredX) result.FlipX = !result.FlipX;
                if (mirroredY) result.FlipY = !result.FlipY;
                // Turned over, the box lies on the other side of the anchor.
                double offsetX = (0.5 - anchorUnit.X) * width * (mirroredX ? -1 : 1);
                double offsetY = (0.5 - anchorUnit.Y) * height * (mirroredY ? -1 : 1);
                var center = new PointD(
                    anchor.X + offsetX * cos - offsetY * sin,
                    anchor.Y + offsetX * sin + offsetY * cos);
                result.Origin = new PointD(center.X - width / 2, center.Y - height / 2);
                break;
            }
        }
        return result.IsValid ? result : original;
    }
}

/// <summary>A double-precision rectangle, for snap targets in document pixels.</summary>
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double MinX => X;
    public double MidX => X + Width / 2;
    public double MaxX => X + Width;
    public double MinY => Y;
    public double MidY => Y + Height / 2;
    public double MaxY => Y + Height;
}

/// <summary>
/// Moving a layer snaps its edges and center to the canvas and to the other layers. The pull
/// is a fixed distance on screen, so it feels the same at any zoom, and small enough to slide
/// past without a fight (the macOS TransformSnap).
/// </summary>
public static class TransformSnap
{
    /// <summary>Crop.swift snappedResizePoint: snap the dragged upright edges before rounding the draft.</summary>
    public static (PointD Point, double? X, double? Y) ResizePoint(
        TransformDragState drag, PointD point, IReadOnlyList<double> xs, IReadOnlyList<double> ys,
        double tolerance, bool lockRatio, bool shift, bool option)
    {
        if (drag.Mode != TransformDragMode.Resize || drag.Original.Radians != 0) return (point, null, null);
        var handle = TransformDrag.Handles[drag.ResizeIndex];
        var grab = drag.Original.Point(handle);
        var at = new PointD(grab.X + point.X - drag.Start.X, grab.Y + point.Y - drag.Start.Y);
        var draft = Update(point);
        var snaps = new List<(bool Horizontal, double Target)>(2);
        if (handle.X != .5 && Nearest(Edge(draft, true), xs) is { } x) snaps.Add((true, x));
        if (handle.Y != .5 && Nearest(Edge(draft, false), ys) is { } y) snaps.Add((false, y));
        if (lockRatio != shift && snaps.Count == 2)
        {
            var horizontal = snaps[0]; var vertical = snaps[1];
            snaps = [Math.Abs(horizontal.Target - Edge(draft, true)) <= Math.Abs(vertical.Target - Edge(draft, false)) ? horizontal : vertical];
        }
        var result = point;
        foreach (var snap in snaps)
        {
            double before = Edge(Update(result), snap.Horizontal);
            var nudged = snap.Horizontal ? new PointD(result.X + 1, result.Y) : new PointD(result.X, result.Y + 1);
            double perPixel = Edge(Update(nudged), snap.Horizontal) - before;
            if (Math.Abs(perPixel) <= .01) continue;
            double offset = (snap.Target - before) / perPixel;
            result = snap.Horizontal ? new(result.X + offset, result.Y) : new(result.X, result.Y + offset);
        }
        return (result, snaps.Where(s => s.Horizontal).Select(s => (double?)s.Target).FirstOrDefault(),
            snaps.Where(s => !s.Horizontal).Select(s => (double?)s.Target).FirstOrDefault());

        LayerTransform Update(PointD value) => TransformDrag.Updated(drag, value, lockRatio, shift, option);
        double Edge(LayerTransform transform, bool horizontal)
        {
            double min = horizontal ? transform.Origin.X : transform.Origin.Y;
            double max = min + (horizontal ? transform.Size.Width : transform.Size.Height);
            double pointer = horizontal ? at.X : at.Y;
            return Math.Abs(min - pointer) <= Math.Abs(max - pointer) ? min : max;
        }
        double? Nearest(double value, IReadOnlyList<double> lines)
        {
            double? nearest = null;
            foreach (double line in lines)
                if (Math.Abs(line - value) <= tolerance && (nearest is null || Math.Abs(line - value) < Math.Abs(nearest.Value - value))) nearest = line;
            return nearest;
        }
    }

    /// <summary>
    /// <paramref name="box"/> moved so that whichever of its left, center or right lands
    /// nearest an <paramref name="xs"/> target does, and the same vertically — each axis on
    /// its own, and only within <paramref name="tolerance"/> document pixels. The targets it
    /// landed on come back too, to draw a line along.
    /// </summary>
    public static (SizeD Offset, double? X, double? Y) OffsetFor(
        RectD box, IReadOnlyList<double> xs, IReadOnlyList<double> ys, double tolerance)
    {
        var horizontal = Shift([box.MinX, box.MidX, box.MaxX], xs, tolerance);
        var vertical = Shift([box.MinY, box.MidY, box.MaxY], ys, tolerance);
        return (new SizeD(horizontal.Move, vertical.Move), horizontal.Target, vertical.Target);
    }

    /// <summary>The smallest move that puts one of <paramref name="guides"/> on one of the targets.</summary>
    private static (double Move, double? Target) Shift(double[] guides, IReadOnlyList<double> targets, double tolerance)
    {
        (double Move, double? Target)? best = null;
        foreach (double guideValue in guides)
        {
            foreach (double target in targets)
            {
                double move = target - guideValue;
                if (Math.Abs(move) > tolerance) continue;
                if (best is { } current && Math.Abs(current.Move) <= Math.Abs(move)) continue;
                best = (move, target);
            }
        }
        return (best?.Move ?? 0, best?.Target);
    }
}
