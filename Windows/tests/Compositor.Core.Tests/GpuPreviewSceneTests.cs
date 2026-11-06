using Xunit;

namespace Compositor.Core.Tests;

public class GpuPreviewSceneTests
{
    [Theory]
    [InlineData("missingMask")]
    public void UnsupportedVisibleWorkRejectsTheEntireStack(string change)
    {
        var snapshot = Scene(); var layer = snapshot.Manifest.Layers[0]; var adjustment = snapshot.Manifest.Layers[1];
        switch (change)
        {
            case "blend": layer.BlendMode = LayerBlendMode.Multiply; break;
            case "opacity": layer.Opacity = .9; break;
            case "rotation": layer.Transform.Rotation = 19; break;
            case "mask": layer.MaskFile = "mask.png"; break;
            case "missingMask": layer.MaskFile = "missing.png"; break;
            case "liveMask": layer.MaskSourceId = Guid.NewGuid(); break;
            case "effect": layer.Effects = System.Text.Json.JsonDocument.Parse("{}").RootElement; break;
            case "blur": adjustment.Adjustment!.Kind = AdjustmentKind.GaussianBlur; break;
            case "offsetAdjustment": adjustment.Transform.Origin = new(1, 0); break;
        }
        Assert.Null(GpuPreviewScene.TryCreate(snapshot));
        layer.IsVisible = false; adjustment.IsVisible = false;
        Assert.Null(GpuPreviewScene.TryCreate(snapshot));
    }

    [Fact]
    public void AlignedMaskedPointStackRetainsTheOriginalAssets()
    {
        var scene = Scene(); var adjustment = scene.Manifest.Layers[1];
        adjustment.MaskFile = "mask.png";
        ((Dictionary<Guid, MaskSurface>)scene.Masks)[adjustment.Id] = new(32, 24);
        var plan = GpuPreviewScene.TryCreate(scene);
        Assert.NotNull(plan); Assert.Same(scene, plan.Snapshot);
        Assert.Equal(scene.Manifest.Layers.Select(layer => layer.Id), plan.Layers.Select(layer => layer.Id));
    }

    [Theory]
    [InlineData(LayerBlendMode.Normal)]
    [InlineData(LayerBlendMode.Multiply)]
    [InlineData(LayerBlendMode.Hue)]
    [InlineData(LayerBlendMode.SoftLight)]
    public void RasterBlendOpacityAndMaskRemainGpuWork(LayerBlendMode mode)
    {
        var snapshot = Scene(); var layer = snapshot.Manifest.Layers[0];
        layer.BlendMode = mode; layer.Opacity = .37; layer.MaskFile = "mask.png";
        ((Dictionary<Guid, MaskSurface>)snapshot.Masks)[layer.Id] = new(32, 24);
        Assert.NotNull(GpuPreviewScene.TryCreate(snapshot));
    }

    [Theory]
    [InlineData(17.25)]
    [InlineData(90)]
    [InlineData(180)]
    public void RotatedRasterUsesGpuSampling(double angle)
    {
        var snapshot = Scene(); snapshot.Manifest.Layers[0].Transform.Rotation = angle;
        Assert.NotNull(GpuPreviewScene.TryCreate(snapshot));
    }

    [Theory]
    [InlineData(AdjustmentKind.GradientMap)]
    [InlineData(AdjustmentKind.HueSaturation)]
    [InlineData(AdjustmentKind.BlackWhite)]
    [InlineData(AdjustmentKind.Grain)]
    [InlineData(AdjustmentKind.AddNoise)]
    [InlineData(AdjustmentKind.GaussianBlur)]
    [InlineData(AdjustmentKind.MotionBlur)]
    public void EveryAdjustmentCanUseTheGpu(AdjustmentKind kind)
    {
        var snapshot = Scene(); snapshot.Manifest.Layers[1].Adjustment!.Kind = kind;
        Assert.NotNull(GpuPreviewScene.TryCreate(snapshot));
    }

    [Fact]
    public void LayerCountDoesNotDisableTheGpu()
    {
        var snapshot = Scene();
        for (int i = 0; i < 30; i++)
        {
            var id = Guid.NewGuid();
            snapshot.Manifest.Layers.Add(new() { Id = id, Name = "Tile", ImageFile = "tile.png", IsVisible = true,
                Transform = new() { Size = new(1, 1) } });
            ((Dictionary<Guid, Surface>)snapshot.Images)[id] = new(1, 1);
        }
        Assert.Equal(32, GpuPreviewScene.TryCreate(snapshot)?.Layers.Count);
    }

    [Theory]
    [InlineData("rotatedMask")]
    [InlineData("offsetAdjustment")]
    [InlineData("clipping")]
    public void CoverageRemainsGpuWork(string change)
    {
        var snapshot = Scene(); var layer = snapshot.Manifest.Layers[0];
        if (change == "rotatedMask")
        {
            layer.MaskFile = "mask.png"; layer.MaskPlacement = new() { Size = new(31, 23), Rotation = 17 };
            ((Dictionary<Guid, MaskSurface>)snapshot.Masks)[layer.Id] = new(17, 13);
        }
        else if (change == "offsetAdjustment") snapshot.Manifest.Layers[1].Transform.Origin = new(1, 0);
        else snapshot.Manifest.Layers[1].MaskSourceId = layer.Id;
        Assert.NotNull(GpuPreviewScene.TryCreate(snapshot));
    }

    [Fact]
    public void VisibleLayerEffectsRemainGpuWork()
    {
        var snapshot = Scene();
        snapshot.Manifest.Layers[0].Effects = new LayerEffects { Stroke = new() }.Serialize();
        Assert.NotNull(GpuPreviewScene.TryCreate(snapshot));
    }

    [Fact]
    public void GaussianPreparationReusesResidentSeedsAndChecksBudgetBeforeAllocation()
    {
        var scene = Scene();
        scene.Manifest.Layers[1].Adjustment = new() { Kind = AdjustmentKind.AddNoise, NoiseGaussian = true, NoiseSeed = 127 };
        var plan = GpuPreviewScene.TryCreate(scene)!;
        Assert.Empty(plan.PrepareGaussianNoise(new HashSet<uint> { 127 }, 0));
        Assert.Throws<NotSupportedException>(() => plan.PrepareGaussianNoise(new HashSet<uint>(), 1));
        var prepared = plan.PrepareGaussianNoise(new HashSet<uint>(), 32 * 24 * 16);
        Assert.Equal(AdjustmentEngine.GaussianNoiseBasis(32, 24, 127), prepared[127]);
    }

    private static ProjectSnapshot Scene()
    {
        var id = Guid.NewGuid();
        return new() { Manifest = new() { Width = 32, Height = 24, Layers = [
            new() { Id = id, Name = "Photo", IsVisible = true, ImageFile = "photo.png", Transform = new() { Size = new(31, 23) } },
            new() { Id = Guid.NewGuid(), Name = "Exposure", IsVisible = true, Transform = new() { Size = new(32, 24) },
                Adjustment = new() { Kind = AdjustmentKind.Exposure, ExposureSettings = new() { Exposure = .28 } } } ] },
            Images = new Dictionary<Guid, Surface> { [id] = new(31, 23) }, Masks = new Dictionary<Guid, MaskSurface>() };
    }
}
