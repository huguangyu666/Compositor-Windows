namespace Compositor.Core;

/// <summary>EditorCanvas's latched Shift axis followed by EditorSession's string smoothing.
/// Coordinates are document pixels; smoothing length is divided by the current zoom.</summary>
public sealed class BrushPointerPath
{
    private readonly double smoothing;
    private PointD? axisAnchor;
    private bool? horizontal;
    public PointD Pointer { get; private set; }
    public PointD LastPainted { get; private set; }

    public BrushPointerPath(PointD start, double smoothing = 0)
    {
        Pointer = LastPainted = start;
        this.smoothing = double.IsFinite(smoothing) ? Math.Clamp(smoothing, 0, 100) : 0;
    }

    public void ResetAxis(PointD pointer, bool shift)
    {
        Pointer = pointer; axisAnchor = shift ? pointer : null; horizontal = null;
    }

    public PointD? Move(PointD point, double zoom, bool shift = false)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) return null;
        if (shift)
        {
            var anchor = axisAnchor ?? Pointer;
            if (axisAnchor is null) { axisAnchor = anchor; horizontal = null; }
            double dx = point.X - anchor.X, dy = point.Y - anchor.Y;
            if (horizontal is null && dx * dx + dy * dy >= 9) horizontal = Math.Abs(dx) >= Math.Abs(dy);
            point = horizontal is { } alongX ? alongX ? new(point.X, anchor.Y) : new(anchor.X, point.Y) : anchor;
        }
        else { axisAnchor = null; horizontal = null; }
        Pointer = point;
        if (smoothing > 0)
        {
            double radius = smoothing / (double.IsFinite(zoom) ? Math.Max(.01, zoom) : 1);
            double dx = point.X - LastPainted.X, dy = point.Y - LastPainted.Y;
            double distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance <= radius) return null;
            double step = (distance - radius) / distance;
            point = new(LastPainted.X + dx * step, LastPainted.Y + dy * step);
        }
        if (point == LastPainted) return null;
        return LastPainted = point;
    }
}
