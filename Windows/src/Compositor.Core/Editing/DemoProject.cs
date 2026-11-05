using System.Text;

namespace Compositor.Core;

/// <summary>
/// Builds a demo .comp that exercises the editor's features: a gradient sky, a soft sun,
/// color bars for the transform handles, a masked vignette layer, a Curves adjustment, and
/// rule-of-thirds guides. No production caller — keep internal so it doesn't ship in the
/// public API (a CLI/verification helper at most).
/// </summary>
internal static class DemoProject
{
    internal static void Write(string path, int width = 1280, int height = 800)
    {
        var backdrop = Guid.NewGuid();
        var sun = Guid.NewGuid();
        var bars = Guid.NewGuid();
        var vignette = Guid.NewGuid();
        var curves = Guid.NewGuid();

        var manifest = new ProjectManifest
        {
            DocumentId = Guid.NewGuid(),
            Width = width,
            Height = height,
            Resolution = 72,
            ActiveLayerId = bars,
            Guides =
            [
                new CanvasGuide { Id = Guid.NewGuid(), Axis = GuideAxis.Vertical, Position = width / 3.0 },
                new CanvasGuide { Id = Guid.NewGuid(), Axis = GuideAxis.Vertical, Position = width * 2.0 / 3.0 },
                new CanvasGuide { Id = Guid.NewGuid(), Axis = GuideAxis.Horizontal, Position = height / 3.0 },
                new CanvasGuide { Id = Guid.NewGuid(), Axis = GuideAxis.Horizontal, Position = height * 2.0 / 3.0 },
            ],
            Layers =
            [
                new ProjectLayerRecord
                {
                    Id = backdrop,
                    Name = "Sky",
                    IsVisible = true,
                    ImageFile = ProjectValidator.Uuid(backdrop) + ".png",
                    Opacity = 1,
                    BlendMode = LayerBlendMode.Normal,
                    Transform = new LayerTransform { Origin = new PointD(0, 0), Size = new SizeD(width, height) },
                },
                new ProjectLayerRecord
                {
                    Id = sun,
                    Name = "Sun",
                    IsVisible = true,
                    ImageFile = ProjectValidator.Uuid(sun) + ".png",
                    Opacity = 1,
                    BlendMode = LayerBlendMode.Screen,
                    Transform = new LayerTransform { Origin = new PointD(width * 0.68, height * 0.10), Size = new SizeD(360, 360) },
                },
                new ProjectLayerRecord
                {
                    Id = bars,
                    Name = "Bars",
                    IsVisible = true,
                    ImageFile = ProjectValidator.Uuid(bars) + ".png",
                    Opacity = 1,
                    BlendMode = LayerBlendMode.Normal,
                    Transform = new LayerTransform { Origin = new PointD(width / 2.0 - 220, height * 0.52), Size = new SizeD(440, 260), Rotation = -6 },
                },
                new ProjectLayerRecord
                {
                    Id = vignette,
                    Name = "Vignette",
                    IsVisible = true,
                    ImageFile = ProjectValidator.Uuid(vignette) + ".png",
                    MaskFile = ProjectValidator.Uuid(vignette) + ".mask.png",
                    MaskEnabled = true,
                    Opacity = 0.85,
                    BlendMode = LayerBlendMode.Multiply,
                    Transform = new LayerTransform { Origin = new PointD(0, 0), Size = new SizeD(width, height) },
                },
                new ProjectLayerRecord
                {
                    Id = curves,
                    Name = "Warm Grade",
                    IsVisible = true,
                    Opacity = 1,
                    BlendMode = LayerBlendMode.Normal,
                    Adjustment = new LayerAdjustment
                    {
                        Kind = AdjustmentKind.Curves,
                        Curves = new CurvesSettings
                        {
                            Channel = LevelsChannel.Rgb,
                            Channels =
                            [
                                new() { new CurvePoint(0, 8), new CurvePoint(128, 138), new CurvePoint(255, 250) },
                                new() { new CurvePoint(0, 0), new CurvePoint(255, 255) },
                                new() { new CurvePoint(0, 0), new CurvePoint(120, 132), new CurvePoint(255, 255) },
                                new() { new CurvePoint(0, 0), new CurvePoint(110, 98), new CurvePoint(255, 244) },
                            ],
                        },
                    },
                    Transform = new LayerTransform { Origin = new PointD(0, 0), Size = new SizeD(width, height) },
                },
            ],
        };

        var images = new Dictionary<Guid, Surface>
        {
            [backdrop] = Sky(width, height),
            [sun] = SoftDisc(512, 512),
            [bars] = Bars(440, 260),
            [vignette] = Solid(width, height, 60, 60, 80),
        };
        var masks = new Dictionary<Guid, MaskSurface> { [vignette] = VignetteMask(width, height) };

        Directory.CreateDirectory(path);
        Directory.CreateDirectory(System.IO.Path.Combine(path, "images"));
        foreach (var (id, surface) in images)
        {
            File.WriteAllBytes(
                System.IO.Path.Combine(path, "images", ProjectValidator.Uuid(id) + ".png"),
                WicImaging.EncodePngAsync(surface).GetAwaiter().GetResult());
        }
        foreach (var (id, mask) in masks)
        {
            File.WriteAllBytes(
                System.IO.Path.Combine(path, "images", ProjectValidator.Uuid(id) + ".mask.png"),
                GrayPng.Encode(mask));
        }
        File.WriteAllText(System.IO.Path.Combine(path, "manifest.json"), ManifestJson.Serialize(manifest));
    }

    private static Surface Sky(int width, int height)
    {
        var surface = new Surface(width, height);
        for (int y = 0; y < height; y++)
        {
            double t = y / (double)height;
            byte r = (byte)(24 + 90 * t * t);
            byte g = (byte)(32 + 80 * t * t);
            byte b = (byte)(70 + 90 * t);
            for (int x = 0; x < width; x++)
            {
                int index = y * surface.Stride + x * 4;
                surface.Pixels[index] = r;
                surface.Pixels[index + 1] = g;
                surface.Pixels[index + 2] = b;
                surface.Pixels[index + 3] = 255;
            }
        }
        return surface;
    }

    private static Surface SoftDisc(int size, int size2)
    {
        var surface = new Surface(size, size2);
        double centerX = size / 2.0, centerY = size2 / 2.0, radius = size / 2.0;
        for (int y = 0; y < size2; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double dx = x + 0.5 - centerX, dy = y + 0.5 - centerY;
                double d = Math.Sqrt(dx * dx + dy * dy) / radius;
                byte alpha = (byte)(d >= 1 ? 0 : Math.Clamp(1.4 - d, 0, 1) * 255);
                int index = y * surface.Stride + x * 4;
                surface.Pixels[index] = 255;
                surface.Pixels[index + 1] = 214;
                surface.Pixels[index + 2] = 130;
                surface.Pixels[index + 3] = alpha;
            }
        }
        return surface;
    }

    private static Surface Bars(int width, int height)
    {
        var surface = new Surface(width, height);
        (byte R, byte G, byte B)[] colors =
        {
            (232, 90, 84), (244, 180, 76), (98, 200, 130),
            (72, 150, 220), (150, 110, 220), (228, 110, 180),
        };
        int barWidth = width / colors.Length;
        for (int x = 0; x < width; x++)
        {
            var (r, g, b) = colors[Math.Min(colors.Length - 1, x / barWidth)];
            for (int y = 0; y < height; y++)
            {
                int index = y * surface.Stride + x * 4;
                surface.Pixels[index] = r;
                surface.Pixels[index + 1] = g;
                surface.Pixels[index + 2] = b;
                surface.Pixels[index + 3] = 255;
            }
        }
        return surface;
    }

    private static Surface Solid(int width, int height, byte r, byte g, byte b)
    {
        var surface = new Surface(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = y * surface.Stride + x * 4;
                surface.Pixels[index] = r;
                surface.Pixels[index + 1] = g;
                surface.Pixels[index + 2] = b;
                surface.Pixels[index + 3] = 255;
            }
        }
        return surface;
    }

    private static MaskSurface VignetteMask(int width, int height)
    {
        var mask = new MaskSurface(width, height);
        double centerX = width / 2.0, centerY = height / 2.0;
        double max = Math.Sqrt(centerX * centerX + centerY * centerY);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double dx = x + 0.5 - centerX, dy = y + 0.5 - centerY;
                double d = Math.Sqrt(dx * dx + dy * dy) / max;
                double reveal = Math.Clamp(1.6 - d * 1.9, 0, 1);
                mask.Pixels[y * width + x] = (byte)(reveal * 255);
            }
        }
        return mask;
    }
}
