using Compositor.Core;

namespace Compositor.Renderer;

/// <summary>CanvasThumbnail.swift: a canvas-shaped thumbnail with independently placed image or mask pixels.</summary>
public static class CanvasThumbnail
{
    public const int BackingScale = 2;

    public static (int Width, int Height) FittedSize(int canvasWidth, int canvasHeight, int box)
    {
        if (canvasWidth <= 0 || canvasHeight <= 0) return (box, box);
        double factor = (double)box / Math.Max(canvasWidth, canvasHeight);
        return (Math.Max(1, (int)Math.Round(canvasWidth * factor, MidpointRounding.AwayFromZero)),
            Math.Max(1, (int)Math.Round(canvasHeight * factor, MidpointRounding.AwayFromZero)));
    }

    public static Surface Draw(int canvasWidth, int canvasHeight, int box, LayerTransform placement, Surface? image = null, MaskSurface? mask = null)
    {
        var size = FittedSize(canvasWidth, canvasHeight, box);
        var output = new Surface(size.Width * BackingScale, size.Height * BackingScale);
        double scale = canvasWidth > 0 ? (double)output.Width / canvasWidth : 1;
        if (mask is not null)
        {
            MaskDisplay.Draw(mask, placement, output, new(0, 0, output.Width, output.Height), scale);
            return output;
        }
        if (image is not null)
        {
            var placed = new LayerTransform
            {
                Origin = new(placement.Origin.X * scale, placement.Origin.Y * scale),
                Size = new(placement.Size.Width * scale, placement.Size.Height * scale),
                Rotation = placement.Rotation, FlipX = placement.FlipX, FlipY = placement.FlipY, Sampling = placement.Sampling,
            };
            Resampler.Draw(image, placed, output, 0, 0, output.Width, output.Height);
        }
        // Six-point checker cells and 0.22/0.32 gray, as in the native thumbnail.
        for (int y = 0; y < output.Height; y++)
        for (int x = 0; x < output.Width; x++)
        {
            int i = y * output.Stride + x * 4;
            int checker = ((x / (6 * BackingScale) + y / (6 * BackingScale)) & 1) == 0 ? 82 : 56;
            int background = checker * (255 - output.Pixels[i + 3]) / 255;
            output.Pixels[i] = (byte)Math.Min(255, output.Pixels[i] + background);
            output.Pixels[i + 1] = (byte)Math.Min(255, output.Pixels[i + 1] + background);
            output.Pixels[i + 2] = (byte)Math.Min(255, output.Pixels[i + 2] + background);
            output.Pixels[i + 3] = 255;
        }
        return output;
    }
}
