namespace Compositor.Core.Tests;

internal static class GradientScenario
{
    public static GradientSettings Settings(int scenario) => new(scenario % 2 == 0 ? GradientShape.Linear : GradientShape.Radial,
        scenario % 3 == 0 ? GradientStyle.ForegroundToTransparent : GradientStyle.ForegroundToBackground,
        new(215, 37, 116), new(12, 210, 57), scenario % 4 == 0, .73);

    public static (EditorDocument Document, Guid Id, bool OnMask) Create(int scenario)
    {
        var doc = EditorDocument.Create(41, 29);
        var layer = doc.Snapshot.Manifest.Layers[0];
        bool onMask = scenario >= 12;
        bool padded = scenario % 6 == 0;
        int width = scenario % 3 == 0 ? 41 : 23, height = scenario % 3 == 0 ? 29 : 17;
        int stride = width * 4 + (padded ? 12 : 0);
        var image = new Surface(width, height, new byte[stride * height], stride);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int p = y * stride + x * 4;
            image.Pixels[p] = (byte)((x * 13 + y * 5) % 120); image.Pixels[p + 1] = (byte)((x * 3 + y * 7) % 120);
            image.Pixels[p + 2] = (byte)((x * 9 + y * 11) % 120); image.Pixels[p + 3] = 120;
        }
        ((Dictionary<Guid, Surface>)doc.Snapshot.Images)[layer.Id] = image;
        layer.Transform = new() { Origin = new(scenario % 3 == 0 ? 0 : 7.2, scenario % 3 == 0 ? 0 : 3.4),
            Size = new(width, height), Rotation = scenario % 4 == 1 ? 37 : 0, FlipX = scenario % 4 == 2, FlipY = scenario % 4 == 3 };
        if (scenario % 5 == 0) doc.SetSelection(Selection.Rectangle(41, 29, 5, 4, 23, 17).Feathered(2));
        if (onMask || scenario % 5 == 1)
        {
            layer.MaskFile = "mask.png";
            var mask = new MaskSurface(7, 5); for (int p = 0; p < mask.Pixels.Length; p++) mask.Pixels[p] = (byte)((p * 7) % 256);
            ((Dictionary<Guid, MaskSurface>)doc.Snapshot.Masks)[layer.Id] = mask;
            if (onMask && scenario % 2 == 0) layer.MaskPlacement = new() { Origin = new(4, 3), Size = new(27, 21), Rotation = 19 };
        }
        return (doc, layer.Id, onMask);
    }
}
