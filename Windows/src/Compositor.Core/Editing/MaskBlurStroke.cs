namespace Compositor.Core;

public sealed partial class EditorDocument
{
    private GaussianMaskBlurSample? maskBlurSample;

    public bool BeginMaskBlurStroke(Guid id, double blurRadius, BrushSettings brush, PointD point, object key)
    {
        if (WhyPaintRefused(id, onMask: true) is not null || !Snapshot.Masks.TryGetValue(id, out var mask)
            || mask.Width == 1 && mask.Height == 1 || !double.IsFinite(blurRadius)) return false;
        var layer = Find(id)!; var placed = layer.MaskPlacement ?? layer.Transform;
        if (!placed.IsValid) return false;
        double perPixel = Math.Max(1e-6, Math.Sqrt(Math.Abs(placed.Size.Width * placed.Size.Height / ((double)mask.Width * mask.Height))));
        double sigma = Math.Min(Math.Clamp(blurRadius, .5, 50) / perPixel, Math.Max(mask.Width, mask.Height) / 2d);
        var sample = new GaussianMaskBlurSample(mask, sigma, MaskDisplay.Background(mask));
        return BeginStrokeCore(key, id, brush, point, onMask: true, erase: false, sample);
    }
}
