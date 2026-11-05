namespace Compositor.Core.Psd;

public static partial class PsdReader
{
    private static long Extent(int end, int start) => Math.Max(0, (long)end - start);

    private static bool FitsPlane(long width, long height, long remaining)
        => width == 0 || height == 0 || width <= MaxSide && height <= MaxSide
            && width * height <= Math.Min(MaxSurfacePixels, remaining);

    private static bool FitsBudget(List<RawLayer> layers, long remaining)
    {
        long used = 0;
        foreach (var layer in layers)
        {
            long width = Extent(layer.Right, layer.Left), height = Extent(layer.Bottom, layer.Top);
            long maskWidth = Extent(layer.MaskRight, layer.MaskLeft), maskHeight = Extent(layer.MaskBottom, layer.MaskTop);
            if (!FitsPlane(width, height, remaining - used)
                || layer.HasMask && !FitsPlane(maskWidth, maskHeight, remaining - used)) return false;
            long pixels = width * height;
            used += pixels;
            if (layer.HasMask && !layer.MaskFromRender && maskWidth > 0 && maskHeight > 0
                && layer.Channels.Any(channel => channel.Id == -2 && channel.Length >= 2)) used += pixels;
            if (used > remaining) return false;
        }
        return true;
    }

    // Keep source rectangles for channel strides; only the target windows and placement change.
    private static void CropToCanvas(RawLayer layer, int width, int height)
    {
        var image = Crop(layer.Left, layer.Top, layer.Right, layer.Bottom, width, height);
        if (image.X != 0 || image.Y != 0 || image.Width != Extent(layer.Right, layer.Left) || image.Height != Extent(layer.Bottom, layer.Top))
        {
            layer.Left = (int)((long)layer.Left + image.X); layer.Top = (int)((long)layer.Top + image.Y);
            layer.Right = layer.Left + image.Width; layer.Bottom = layer.Top + image.Height;
            layer.ImageCrop = image; layer.Cropped = true;
        }
        if (!layer.HasMask) return;
        var mask = Crop(layer.MaskLeft, layer.MaskTop, layer.MaskRight, layer.MaskBottom, width, height);
        if (mask.X != 0 || mask.Y != 0 || mask.Width != Extent(layer.MaskRight, layer.MaskLeft) || mask.Height != Extent(layer.MaskBottom, layer.MaskTop))
        {
            layer.MaskLeft = (int)((long)layer.MaskLeft + mask.X); layer.MaskTop = (int)((long)layer.MaskTop + mask.Y);
            layer.MaskRight = layer.MaskLeft + mask.Width; layer.MaskBottom = layer.MaskTop + mask.Height;
            layer.MaskCrop = mask; layer.Cropped = true;
        }
    }

    private static PsdCrop Crop(int left, int top, int right, int bottom, int width, int height)
    {
        int x0 = Math.Clamp(left, 0, width), y0 = Math.Clamp(top, 0, height);
        int x1 = Math.Max(x0, Math.Min(width, right)), y1 = Math.Max(y0, Math.Min(height, bottom));
        return new((long)x0 - left, (long)y0 - top, x1 - x0, y1 - y0);
    }
}
