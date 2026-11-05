namespace Compositor.Core.Tests;

internal static class GradientBatchScenario
{
    public static readonly string[] Names = ["linear", "radial", "mask", "rotated-selection", "independent-mask",
        "clipped-flipped", "unchanged", "last-row", "small"];

    public static GradientSettings Settings(string name) => new(name == "radial" ? GradientShape.Radial : GradientShape.Linear,
        name is "radial" or "rotated-selection" ? GradientStyle.ForegroundToTransparent : GradientStyle.ForegroundToBackground,
        name == "unchanged" ? new(37, 68, 115) : new(215, 37, 116),
        name == "unchanged" ? new(37, 68, 115) : new(12, 210, 57), name is "radial" or "clipped-flipped", .73);

    public static (EditorDocument Document, Guid Id, bool OnMask) Create(string name, int width = 768, int height = 385)
    {
        if (name == "small") { width = 63; height = 41; }
        var document = EditorDocument.Create(width, height);
        var layer = document.Snapshot.Manifest.Layers[0];
        var image = document.Snapshot.Images[layer.Id];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int p = y * image.Stride + x * 4;
            image.Pixels[p] = (byte)(name == "unchanged" ? 37 : (x * 13 + y * 5) % 120);
            image.Pixels[p + 1] = (byte)(name == "unchanged" ? 68 : (x * 3 + y * 7) % 120);
            image.Pixels[p + 2] = (byte)(name == "unchanged" ? 115 : (x * 9 + y * 11) % 120);
            image.Pixels[p + 3] = (byte)(name == "unchanged" ? 255 : 120);
        }
        bool onMask = name is "mask" or "independent-mask";
        if (onMask || name == "rotated-selection")
        {
            layer.MaskFile = "mask.png";
            var mask = new MaskSurface(width, height);
            for (int p = 0; p < mask.Pixels.Length; p++) mask.Pixels[p] = (byte)((p * 7) % 256);
            ((Dictionary<Guid, MaskSurface>)document.Snapshot.Masks)[layer.Id] = mask;
        }
        if (name == "independent-mask")
            layer.MaskPlacement = new() { Origin = new(-3.2, 7.4), Size = new(width * 1.1, height * .93), Rotation = 19, FlipY = true };
        if (name == "rotated-selection")
        {
            layer.Transform.Rotation = 27;
            document.SetSelection(Selection.Ellipse(width, height, width * .47, height * .51, width * .39, height * .43).Feathered(5));
        }
        if (name == "clipped-flipped")
            layer.Transform = new() { Origin = new(-17.4, 9.2), Size = new(width * 1.2, height * .95), FlipX = true, FlipY = true };
        if (name == "last-row")
            document.SetSelection(Selection.Rectangle(width, height, 0, height - 1, width, 1));
        return (document, layer.Id, onMask);
    }
}
