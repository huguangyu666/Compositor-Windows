using System.Reflection;
using Xunit;

namespace Compositor.Core.Tests;

public class MaskCoveragePerformanceTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public void CoverageMatchesOriginalSamplerAndPremultipliedRounding(int geometry, bool blendBackdrop)
    {
        const int width = 521, height = 517, stride = width * 4 + 12;
        var random = new Random(61);
        var pixels = new byte[stride * height]; random.NextBytes(pixels);
        var maskBytes = new byte[width * height]; random.NextBytes(maskBytes);
        for (int i = 0; i < maskBytes.Length; i += 3) maskBytes[i] = 0;
        for (int i = 1; i < maskBytes.Length; i += 3) maskBytes[i] = 255;
        var mask = new MaskSurface(width, height, maskBytes);
        var target = new Surface(width, height, pixels, stride);
        var expected = target.Clone();
        var backdrop = blendBackdrop ? target.Clone() : null;
        if (backdrop is not null) random.NextBytes(backdrop.Pixels);
        var placement = new LayerTransform { Origin = geometry == 0 ? new(0, 0) : new(-7, 9), Size = new(width, height),
            FlipX = geometry == 1, FlipY = geometry == 1 };
        if (geometry == 2) placement.Origin = new(3.25, -9.5);
        if (geometry == 3) { placement.Rotation = 19; placement.Size = new(width / 1.3, height * 1.2); }
        const byte outside = 71;
        const int x0 = 2, y0 = 1, x1 = width - 2, y1 = height - 1;
        for (int y = y0; y < y1; y++)
        for (int x = x0; x < x1; x++)
        {
            var center = placement.Center;
            double localX = x + .5 - center.X, localY = y + .5 - center.Y;
            double cos = Math.Cos(placement.Radians), sin = Math.Sin(placement.Radians);
            double u = (localX * cos + localY * sin) / placement.Size.Width + .5;
            double v = (-localX * sin + localY * cos) / placement.Size.Height + .5;
            if (placement.FlipX) u = 1 - u;
            if (placement.FlipY) v = 1 - v;
            double mx = u * width, my = v * height;
            byte coverage = mx < 0 || my < 0 || mx >= width || my >= height ? outside : mask.Pixels[(int)my * width + (int)mx];
            int i = y * expected.Stride + x * 4;
            for (int c = 0; c < 4; c++)
                expected.Pixels[i + c] = backdrop is null ? (byte)(expected.Pixels[i + c] * coverage / 255)
                    : (byte)((expected.Pixels[i + c] * coverage + backdrop.Pixels[y * backdrop.Stride + x * 4 + c] * (255 - coverage) + 127) / 255);
        }
        var compositor = new CanvasCompositor(EditorDocument.Create(width, height).Snapshot);
        var apply = typeof(CanvasCompositor).GetMethod("MultiplyCoverage", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Action<MaskSurface, LayerTransform, Surface, int, int, int, int, byte, Surface?>>(compositor);
        apply(mask, placement, target, x0, y0, x1, y1, outside, backdrop);
        Assert.Equal(expected.Pixels, target.Pixels);
    }
}
