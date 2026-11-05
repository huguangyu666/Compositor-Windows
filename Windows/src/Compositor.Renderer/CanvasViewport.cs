using Compositor.Core;

namespace Compositor.Renderer;

/// <summary>Ports CanvasViewport.swift: zoom is physical pixels per document pixel;
/// view coordinates are logical points, with unrestricted pan and anchored zoom.</summary>
public sealed class CanvasViewport(double documentWidth, double documentHeight)
{
    public double DocumentWidth { get; private set; } = documentWidth;
    public double DocumentHeight { get; private set; } = documentHeight;
    public double Width { get; private set; }
    public double Height { get; private set; }
    public double BackingScale { get; private set; } = 1;
    public double Zoom { get; private set; } = 1;
    public bool FollowsFit { get; private set; } = true;
    public double PanX { get; private set; }
    public double PanY { get; private set; }
    public double PointsPerPixel => Zoom / BackingScale;
    public PointD Center => new(Width / 2, Height / 2);
    public PointD Origin => new(Width / 2 - DocumentWidth * PointsPerPixel / 2 + PanX,
        Height / 2 - DocumentHeight * PointsPerPixel / 2 + PanY);
    private static readonly double[] keyboardLevels = [.125, 1.0 / 6, .25, 1.0 / 3, .5, 2.0 / 3, 1, 1.25, 1.5, 2, 3, 4, 5, 6, 8, 12, 16];

    public void SetDocument(double width, double height)
    {
        if (DocumentWidth == width && DocumentHeight == height) return;
        DocumentWidth = width; DocumentHeight = height;
        if (FollowsFit) Fit();
    }

    public void Resize(double width, double height, double backingScale)
    {
        double oldScale = PointsPerPixel;
        Width = width; Height = height; BackingScale = Math.Max(1, backingScale);
        if (FollowsFit) Fit();
        else { double ratio = PointsPerPixel / oldScale; PanX *= ratio; PanY *= ratio; }
    }

    public void Fit()
    {
        if (Width <= 0 || Height <= 0) { FollowsFit = true; return; }
        Zoom = Math.Clamp(Math.Min(Math.Max(1, Width - 96) / DocumentWidth,
            Math.Max(1, Height - 96) / DocumentHeight) * BackingScale, .001, 32);
        PanX = PanY = 0; FollowsFit = true;
    }

    public void SetZoom(double zoom, PointD anchor)
    {
        if (!double.IsFinite(zoom)) return;
        var pixel = ToDocument(anchor);
        Zoom = Math.Clamp(zoom, .001, 32);
        var moved = ToView(pixel);
        PanX += anchor.X - moved.X; PanY += anchor.Y - moved.Y; FollowsFit = false;
    }

    public PointD ToDocument(PointD point) => new((point.X - Origin.X) / PointsPerPixel, (point.Y - Origin.Y) / PointsPerPixel);
    public PointD ToView(PointD point) => new(Origin.X + point.X * PointsPerPixel, Origin.Y + point.Y * PointsPerPixel);
    public void Pan(PointD delta) { PanX += delta.X; PanY += delta.Y; FollowsFit = false; }
    public double KeyboardZoomTarget(int step)
    {
        double tolerance = Math.Max(1e-9, Math.Abs(Zoom) * 1e-9);
        if (step > 0) return keyboardLevels.FirstOrDefault(value => value > Zoom + tolerance, Zoom);
        if (step < 0) return keyboardLevels.LastOrDefault(value => value < Zoom - tolerance, Zoom);
        return Zoom;
    }
}
