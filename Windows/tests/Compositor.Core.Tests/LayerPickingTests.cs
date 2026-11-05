using Xunit;

namespace Compositor.Core.Tests;

public class LayerPickingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TransparentForegroundDoesNotBlockTheLayerBelow(bool foregroundActive)
    {
        var (source, background, foreground) = Scene();
        Array.Clear(source.Images[foreground.Id].Pixels);
        var active = foregroundActive ? foreground : background;
        Assert.Equal(background.Id, Pick(source, active, new(50, 50))!.LayerId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnabledMaskHolesPassThroughButDisabledMasksDoNot(bool enabled)
    {
        var (source, background, foreground) = Scene();
        foreground.MaskEnabled = enabled;
        AddMask(source, foreground, 0);
        Assert.Equal(enabled ? background.Id : foreground.Id, Pick(source, foreground, new(50, 50))!.LayerId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HiddenOrZeroOpacityForegroundDoesNotCatchClicks(bool hidden)
    {
        var (source, background, foreground) = Scene();
        foreground.IsVisible = !hidden;
        foreground.Opacity = hidden ? 1 : 0;
        Assert.Equal(background.Id, Pick(source, background, new(50, 50))!.LayerId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FolderCoverageAppliesToItsChildren(bool masked)
    {
        var (source, background, foreground) = Scene();
        var folder = new ProjectLayerRecord { Id = Guid.NewGuid(), Name = "Folder", IsGroup = true,
            IsVisible = true, Opacity = masked ? 1 : 0, Transform = new() { Size = new(120, 100) } };
        source.Manifest.Layers.Add(folder);
        foreground.ParentId = folder.Id;
        if (masked) AddMask(source, folder, 0);
        Assert.Equal(background.Id, Pick(source, background, new(50, 50))!.LayerId);
    }

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(37, true, false)]
    [InlineData(90, false, true)]
    public void PixelPickingFollowsScaleRotationAndFlips(double rotation, bool flipX, bool flipY)
    {
        var (source, background, foreground) = Scene();
        var image = source.Images[foreground.Id];
        Array.Clear(image.Pixels);
        image.Pixels[2 * image.Stride + 1 * 4 + 3] = 255;
        foreground.Transform.Rotation = rotation;
        foreground.Transform.FlipX = flipX; foreground.Transform.FlipY = flipY;
        var unit = new PointD(1.5 / image.Width, 2.5 / image.Height);
        var visible = foreground.Transform.Point(new(flipX ? 1 - unit.X : unit.X, flipY ? 1 - unit.Y : unit.Y));
        Assert.Equal(foreground.Id, Pick(source, background, visible)!.LayerId);
        Assert.Equal(background.Id, Pick(source, foreground, foreground.Transform.Point(new(.75, .75)))!.LayerId);
    }

    [Fact]
    public void ClippingSourceTransparencyLetsClicksThrough()
    {
        var (source, background, foreground) = Scene();
        var clippingSource = new ProjectLayerRecord { Id = Guid.NewGuid(), Name = "Clip", IsVisible = false,
            ImageFile = "clip.png", Transform = foreground.Transform };
        source.Manifest.Layers.Insert(1, clippingSource);
        ((Dictionary<Guid, Surface>)source.Images)[clippingSource.Id] = new(8, 8);
        foreground.MaskSourceId = clippingSource.Id;
        Assert.Equal(background.Id, Pick(source, background, new(50, 50))!.LayerId);
    }

    [Fact]
    public void AnIndependentWhiteMaskKeepsItsOutsideCoverage()
    {
        var (source, _, foreground) = Scene();
        AddMask(source, foreground, 255);
        foreground.MaskPlacement = new() { Origin = new(20, 20), Size = new(10, 10) };
        Assert.Equal(foreground.Id, Pick(source, foreground, new(50, 50))!.LayerId);
    }

    [Fact]
    public void DisabledAutoSelectStillKeepsTheActiveLayer()
    {
        var (source, _, foreground) = Scene();
        Array.Clear(source.Images[foreground.Id].Pixels);
        Assert.Equal(new TransformPressResult(foreground.Id, false), TransformPress.Pick(source, new(50, 50),
            foreground.Id, foreground.Transform, false, false, false));
    }

    private static TransformPressResult? Pick(ProjectSnapshot source, ProjectLayerRecord active, PointD point) =>
        TransformPress.Pick(source, point, active.Id, active.Transform, false, false, true);

    private static void AddMask(ProjectSnapshot source, ProjectLayerRecord layer, byte tone)
    {
        layer.MaskFile = "mask.png";
        var mask = new MaskSurface(8, 8); Array.Fill(mask.Pixels, tone);
        ((Dictionary<Guid, MaskSurface>)source.Masks)[layer.Id] = mask;
    }

    private static (ProjectSnapshot Source, ProjectLayerRecord Background, ProjectLayerRecord Foreground) Scene()
    {
        var source = EditorDocument.Create(120, 100).Snapshot;
        var foreground = new ProjectLayerRecord { Id = Guid.NewGuid(), Name = "Foreground", IsVisible = true,
            ImageFile = "foreground.png", Transform = new() { Origin = new(20, 20), Size = new(80, 60) } };
        var image = new Surface(8, 8);
        for (int i = 3; i < image.Pixels.Length; i += 4) image.Pixels[i] = 255;
        source.Manifest.Layers.Add(foreground);
        ((Dictionary<Guid, Surface>)source.Images)[foreground.Id] = image;
        return (source, source.Manifest.Layers[0], foreground);
    }
}
