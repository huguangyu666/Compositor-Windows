namespace Compositor.Core.Tests;

// Exact blur and three-pass setup from c06d9c1, kept independent of the streaming implementation.
internal static class SelectionFeatherReference
{
    internal static MaskSurface Render(MaskSurface source, double feather)
    {
        double sigma = feather / 2;
        int radius = Math.Max(1, (int)Math.Round((Math.Sqrt(1 + 4 * sigma * sigma) - 1) / 2, MidpointRounding.AwayFromZero));
        var blurred = source;
        for (int pass = 0; pass < 3; pass++) blurred = Blurred(blurred, radius);
        return blurred;
    }

    private static MaskSurface Blurred(MaskSurface source, int amount)
    {
        int width = source.Width, height = source.Height;
        var horizontal = new int[width * height];
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            int sum = 0;
            for (int x = 0; x <= amount && x < width; x++) sum += source.Pixels[row + x];
            for (int x = 0; x < width; x++)
            {
                int low = Math.Max(0, x - amount);
                int high = Math.Min(width - 1, x + amount);
                int count = high - low + 1;
                horizontal[row + x] = (sum + count / 2) / count;
                int nextLow = Math.Max(0, x + 1 - amount);
                int nextHigh = Math.Min(width - 1, x + 1 + amount);
                if (nextLow > low) sum -= source.Pixels[row + low];
                if (nextHigh > high) sum += source.Pixels[row + nextHigh];
            }
        }
        var result = new MaskSurface(width, height);
        for (int x = 0; x < width; x++)
        {
            int sum = 0;
            for (int y = 0; y <= amount && y < height; y++) sum += horizontal[y * width + x];
            for (int y = 0; y < height; y++)
            {
                int low = Math.Max(0, y - amount);
                int high = Math.Min(height - 1, y + amount);
                int count = high - low + 1;
                result.Pixels[y * width + x] = (byte)((sum + count / 2) / count);
                int nextLow = Math.Max(0, y + 1 - amount);
                int nextHigh = Math.Min(height - 1, y + 1 + amount);
                if (nextLow > low) sum -= horizontal[low * width + x];
                if (nextHigh > high) sum += horizontal[nextHigh * width + x];
            }
        }
        return result;
    }

}
