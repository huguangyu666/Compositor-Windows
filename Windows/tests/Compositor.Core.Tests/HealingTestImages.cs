namespace Compositor.Core.Tests;

internal static class HealingTestImages
{
    public static readonly string[] Cases = ["pattern", "edge", "alpha", "coarse", "coarse-patch", "all", "padded"];

    public static (Surface Image, MaskSurface Coverage) Create(string name)
    {
        bool coarse = name is "coarse" or "coarse-patch";
        int size = name == "coarse-patch" ? 160 : name == "coarse" ? 72 : name == "all" ? 8 : name == "alpha" ? 24 : 32;
        int stride = size * 4 + (name == "padded" ? 12 : 0);
        var pixels = new byte[stride * size];
        Array.Fill(pixels, (byte)167);
        var coverage = new MaskSurface(size, size);
        int left = name == "edge" ? 0 : size / 2 - (coarse ? 18 : 2);
        int top = left, side = coarse ? 36 : name == "all" ? 8 : 5;
        if (name == "all") left = top = 0;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int i = y * stride + x * 4;
            byte alpha = name == "alpha" ? (byte)(160 + (x + y) % 80) : (byte)255;
            pixels[i] = (byte)((x * 17 + y * 7) % alpha);
            pixels[i + 1] = (byte)((x * 5 + y * 19) % alpha);
            pixels[i + 2] = (byte)((x * 13 + y * 3) % alpha);
            pixels[i + 3] = alpha;
            if (x >= left && y >= top && x < left + side && y < top + side)
            {
                coverage.Pixels[y * size + x] = name == "alpha" ? (byte)128 : (byte)255;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = 0;
            }
        }
        return (new Surface(size, size, pixels, stride), coverage);
    }
}
