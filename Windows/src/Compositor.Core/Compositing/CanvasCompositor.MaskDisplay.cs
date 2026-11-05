namespace Compositor.Core;

public sealed partial class CanvasCompositor
{
    private Guid? maskDisplayId;
    private byte maskDisplayBackground;
    private LayerTransform? maskDisplayPlacement;
    private RectI maskDisplayBounds;

    public Surface RenderMaskAlone(Guid id, ref RectI band)
    {
        if (!byId.TryGetValue(id, out var layer) || !Snapshot.Masks.TryGetValue(id, out var mask))
            throw new ArgumentException("The layer has no raster mask.", nameof(id));
        byte background = MaskDisplay.Background(mask);
        var placement = layer.MaskPlacement ?? layer.Transform;
        bool changedPlacement = !placement.Equals(maskDisplayPlacement);
        var nextBounds = maskDisplayBounds;
        if (changedPlacement)
        {
            var (x0, y0, x1, y1) = Resampler.BoundingRect(Scaled(placement), canvas.Width, canvas.Height);
            nextBounds = x1 > x0 && y1 > y0
                ? RectI.Normalize(x0 - 1, y0 - 1, x1 + 1, y1 + 1, canvas.Width, canvas.Height) : new(0, 0, 0, 0);
        }
        if (maskDisplayId != id || maskDisplayBackground != background) band = new(0, 0, canvas.Width, canvas.Height);
        else if (changedPlacement)
        {
            // Composite dirty bounds follow the pixel layer, which can be far from an
            // independent mask. Clear its previous placement and draw its new one too.
            band = Include(Include(band, maskDisplayBounds), nextBounds);
        }
        band = RectI.Normalize(band.X, band.Y, band.X + band.Width, band.Y + band.Height, canvas.Width, canvas.Height);
        MaskDisplay.Draw(mask, placement, canvas, band, Scale);
        if (changedPlacement) maskDisplayPlacement = TransformEdit.Copy(placement);
        maskDisplayBounds = nextBounds;
        maskDisplayId = id; maskDisplayBackground = background;
        RememberRenderedMaskGeometry();
        return canvas;

        static RectI Include(RectI current, RectI footprint)
            => footprint.Width == 0 || footprint.Height == 0 ? current
                : current.Width == 0 || current.Height == 0 ? footprint : RectI.Union(current, footprint);
    }
}
