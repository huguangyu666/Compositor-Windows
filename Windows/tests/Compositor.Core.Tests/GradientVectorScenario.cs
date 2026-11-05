namespace Compositor.Core.Tests;

internal static class GradientVectorScenario
{
    public const int Count = 68;

    public static (EditorDocument Document, Guid Id, bool Mask, PointD Start, PointD End, GradientSettings Settings) Create(int scenario)
    {
        int width = new[] { 3, 4, 5, 7, 8, 17, 65, 257 }[scenario % 8], height = 5 + scenario % 3;
        if (scenario >= 64) { width = 129; height = 3; }
        var document = EditorDocument.Create(width, height); var layer = document.Snapshot.Manifest.Layers[0];
        int padding = scenario % 5 == 0 ? 12 : 0, stride = width * 4 + padding;
        var image = new Surface(width, height, new byte[stride * height + 17], stride);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int p = y * stride + x * 4; byte alpha = (byte)((x * 37 + y * 71) % 256);
            image.Pixels[p] = (byte)Math.Min(alpha, (x * 13 + y * 5) % 256);
            image.Pixels[p + 1] = (byte)Math.Min(alpha, (x * 3 + y * 7) % 256);
            image.Pixels[p + 2] = (byte)Math.Min(alpha, (x * 9 + y * 11) % 256); image.Pixels[p + 3] = alpha;
        }
        image.Pixels.AsSpan(stride * height).Fill(0xa7);
        ((Dictionary<Guid, Surface>)document.Snapshot.Images)[layer.Id] = image;
        layer.Transform.FlipX = scenario % 5 == 1; layer.Transform.FlipY = scenario % 7 == 1;
        bool mask = scenario % 3 == 0;
        if (mask)
        {
            layer.MaskFile = "mask.png"; var coverage = new MaskSurface(width, height);
            for (int p = 0; p < coverage.Pixels.Length; p++) coverage.Pixels[p] = (byte)((p * 19) % 256);
            ((Dictionary<Guid, MaskSurface>)document.Snapshot.Masks)[layer.Id] = coverage;
        }
        var start = new PointD(-2.12, 3.43); var end = new PointD(width + 3.62, height - 1.14);
        if (scenario % 2 == 0) { start = new(width / 3d + .12, height * .9); end = new(.62, -.14); }
        var settings = new GradientSettings((scenario / 2) % 2 == 0 ? GradientShape.Linear : GradientShape.Radial,
            (scenario / 4) % 2 == 0 ? GradientStyle.ForegroundToBackground : GradientStyle.ForegroundToTransparent,
            new(215, 37, 116), new(12, 210, 57), (scenario / 8) % 2 == 1,
            new[] { 1d, .73, .5, double.Epsilon }[(scenario / 16) % 4]);
        if (scenario is 64 or 65)
        {
            start = new(.5, .5); end = new(128.5, .5);
            settings = new(GradientShape.Linear, GradientStyle.ForegroundToBackground, new(0, 0, 0), new(255, 255, 255), false, scenario == 64 ? 1 : .5);
        }
        if (scenario == 66)
        {
            Array.Fill(image.Pixels, (byte)255);
            if (mask) Array.Fill(document.Snapshot.Masks[layer.Id].Pixels, (byte)255);
            settings = settings with { Foreground = new(255, 255, 255), Background = new(255, 255, 255), Style = GradientStyle.ForegroundToBackground };
        }
        if (scenario == 67)
        {
            start = new(1e154, 1e154); end = new(1e154 - 1e140, 1e154 - 1e140);
            settings = settings with { Shape = GradientShape.Radial, Opacity = .73 };
        }
        return (document, layer.Id, mask, start, end, settings);
    }
}
