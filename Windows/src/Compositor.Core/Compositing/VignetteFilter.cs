namespace Compositor.Core;

/// <summary>
/// The standalone Vignette filter's kernel, ported from <c>adjust_colored_vignette</c>
/// (AdjustPixels.c:461-500) and its <c>vignette_mask_at</c> (:412-426). This is a different filter from the
/// Camera Raw Effects vignette (<c>effects_vignette</c>, :433): this one blends a chosen colour into the edges
/// instead of darkening or lifting them, and it has a second branch that paints into pixels with no coverage at
/// all — which is what <paramref name="fillsClear"/> selects on the Mac, where the falloff is measured against
/// the document's canvas rather than the layer's own rectangle.
/// </summary>
public static class VignetteFilter
{
    /// <summary>The sheet's defaults (Filters.swift:54-60). Note Amount starts at 35, not 0: opening the panel
    /// and pressing OK does apply a vignette. The identity rule that makes OK behave as Cancel is Amount == 0
    /// (Filters.swift:604).</summary>
    public const double DefaultAmount = 35, DefaultMidpoint = 50, DefaultRoundness = 100,
        DefaultFeather = 60, DefaultHighlights = 25;

    public const double UnitMin = 0, UnitMax = 100, RoundMin = -100, RoundMax = 100;

    /// <summary>The falloff at one point of the frame: 0 inside the protected middle, 1 past the edge.
    /// <paramref name="roundness"/> blends the circle (100) toward the square (-100 … 0), <paramref name="feather"/>
    /// sets how wide the ramp is, with a floor of 0.05 so a fully off feather still fades over five percent of the
    /// frame instead of cutting a hard edge (AdjustPixels.c:418-422).</summary>
    public static double MaskAt(double px, double py, double frameWidth, double frameHeight,
        double midpoint, double roundness, double feather)
    {
        double nx = px / frameWidth * 2.0 - 1.0;
        double ny = py / frameHeight * 2.0 - 1.0;
        double square = Math.Max(Math.Abs(nx), Math.Abs(ny));
        double circle = Math.Sqrt(nx * nx + ny * ny) / Math.Sqrt(2.0);
        double shape = (1.0 - roundness / 100.0) * 0.5;
        double dist = circle + (square - circle) * shape;
        double start = midpoint / 100.0 * 0.85;
        double soft = feather / 100.0;
        if (soft < 0.05) soft = 0.05;
        double t = CameraRawEngine.Clamp01((dist - start) / soft);
        return t * t * (3.0 - 2.0 * t);
    }

    /// <summary>The document's canvas rectangle in the layer's own pixel grid — the frame the falloff is
    /// measured against, which is why a layer bigger than the canvas is vignetted around the canvas and not
    /// around itself. The Mac gets this by applying the inverse layer mapping to the canvas rect
    /// (Filters.swift:228); here the corners go through <see cref="LayerTransform.PixelOf"/>, which is the same
    /// inverse including the baked flips, and the bounding box of those corners is the same box a transformed
    /// <c>CGRect</c> returns. Values are top-down like the pixel buffer: the Mac's <c>height - frame.maxY</c>
    /// only exists because CoreImage handed it a bottom-up rect.</summary>
    public static (double X, double Y, double Width, double Height) FrameOf(LayerTransform placement,
        double canvasWidth, double canvasHeight, int imageWidth, int imageHeight)
    {
        var source = new SizeD(imageWidth, imageHeight);
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var corner in new[]
        {
            new PointD(0, 0), new PointD(canvasWidth, 0), new PointD(0, canvasHeight), new PointD(canvasWidth, canvasHeight),
        })
        {
            var at = placement.PixelOf(corner, source);
            minX = Math.Min(minX, at.X);
            minY = Math.Min(minY, at.Y);
            maxX = Math.Max(maxX, at.X);
            maxY = Math.Max(maxY, at.Y);
        }
        return (minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>Blends <paramref name="red"/>/<paramref name="green"/>/<paramref name="blue"/> (0…1) into the
    /// edges of <paramref name="surface"/>, in place and premultiplied, exactly as the C does — including the
    /// guard that a zero or negative amount, or a frame with no extent, leaves every byte alone.</summary>
    public static void Apply(Surface surface, double frameX, double frameY, double frameWidth, double frameHeight,
        bool fillsClear, double amount, double midpoint, double roundness, double feather, double highlights,
        double red, double green, double blue)
    {
        int width = surface.Width, height = surface.Height;
        if (amount <= 0 || width == 0 || height == 0 || frameWidth <= 0 || frameHeight <= 0) return;
        double strength = CameraRawEngine.Clamp01(amount / 100.0);
        red = CameraRawEngine.Clamp01(red);
        green = CameraRawEngine.Clamp01(green);
        blue = CameraRawEngine.Clamp01(blue);
        for (int y = 0; y < height; y++)
        {
            int row = y * surface.Stride;
            for (int x = 0; x < width; x++)
            {
                int index = row + x * 4;
                double alpha = surface.Pixels[index + 3];
                if (alpha == 0 && !fillsClear) continue;
                double mask = MaskAt(x + 0.5 - frameX, y + 0.5 - frameY, frameWidth, frameHeight,
                    midpoint, roundness, feather);
                if (mask <= 0) continue;
                double r = 0, g = 0, b = 0, bright = 0;
                if (alpha > 0)
                {
                    r = Math.Min(1.0, surface.Pixels[index] / alpha);
                    g = Math.Min(1.0, surface.Pixels[index + 1] / alpha);
                    b = Math.Min(1.0, surface.Pixels[index + 2] / alpha);
                    bright = CameraRawEngine.Clamp01((CameraRawEngine.Rec709(r, g, b) - 0.45) / 0.55);
                }
                double effect = strength * mask * (1.0 - highlights / 100.0 * bright);
                if (!fillsClear)
                {
                    // Only the pixels that are there change colour; their coverage stays as it was.
                    CameraRawEngine.WritePremultiplied(surface, index,
                        r + (red - r) * effect, g + (green - g) * effect, b + (blue - b) * effect, alpha);
                    continue;
                }
                // The colour painted over the pixel at `effect`: an opaque pixel moves toward it, a clear one
                // takes it on — so alpha grows, which no other kernel in this port does.
                double outAlpha = alpha / 255.0 + effect * (1.0 - alpha / 255.0);
                if (outAlpha <= 0) continue;
                double coverage = alpha / 255.0;
                r = (red * effect + r * coverage * (1.0 - effect)) / outAlpha;
                g = (green * effect + g * coverage * (1.0 - effect)) / outAlpha;
                b = (blue * effect + b * coverage * (1.0 - effect)) / outAlpha;
                double written = Math.Min(255.0, Math.Round(outAlpha * 255.0, MidpointRounding.AwayFromZero));
                surface.Pixels[index + 3] = (byte)written;
                CameraRawEngine.WritePremultiplied(surface, index, r, g, b, written);
            }
        }
    }
}
