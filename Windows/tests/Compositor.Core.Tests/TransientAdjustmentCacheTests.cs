using Xunit;

namespace Compositor.Core.Tests;

public class TransientAdjustmentCacheTests
{
    [Theory]
    [InlineData(AdjustmentKind.Exposure, LayerBlendMode.Normal, 1, false)]
    [InlineData(AdjustmentKind.Exposure, LayerBlendMode.Normal, 1, true)]
    [InlineData(AdjustmentKind.ColorBalance, LayerBlendMode.Normal, .6, false)]
    [InlineData(AdjustmentKind.GaussianBlur, LayerBlendMode.Multiply, .8, true)]
    public void ExactDraftBandsAndSettledCachesSurviveCommitAndCancel(AdjustmentKind kind, LayerBlendMode mode, double opacity, bool masked)
    {
        const int width = 48, height = 34;
        var imageId = Guid.NewGuid(); var adjustmentId = Guid.NewGuid();
        var image = new Surface(17, 13); new Random(82).NextBytes(image.Pixels);
        for (int i = 0; i < image.Pixels.Length; i += 4)
            for (int c = 0; c < 3; c++) image.Pixels[i + c] = Math.Min(image.Pixels[i + c], image.Pixels[i + 3]);
        var mask = new MaskSurface(width, height); new Random(13).NextBytes(mask.Pixels);
        var original = new ProjectSnapshot {
            Manifest = new() { DocumentId = Guid.NewGuid(), Width = width, Height = height, Layers = [
                new() { Id = imageId, Name = "Photo", IsVisible = true, ImageFile = "photo.png",
                    Transform = new() { Origin = new(5.25, 8.5), Size = new(21.5, 18.25), Sampling = LayerSampling.HighQuality } },
                new() { Id = adjustmentId, Name = "Grade", IsVisible = true, BlendMode = mode, Opacity = opacity,
                    MaskFile = masked ? "mask.png" : null, Transform = new() { Size = new(width, height) },
                    Adjustment = new() { Kind = kind, ExposureSettings = new() { Exposure = .28 }, BlurRadius = 1.5,
                        ColorBalanceSettings = new() { MidCyanRed = 17, HighlightYellowBlue = -21 } } } ] },
            Images = new Dictionary<Guid, Surface> { [imageId] = image },
            Masks = masked ? new Dictionary<Guid, MaskSurface> { [adjustmentId] = mask } : new Dictionary<Guid, MaskSurface>() };
        var compositor = new CanvasCompositor(original); compositor.Render();
        ProjectSnapshot draft = original;
        foreach (var origin in new[] { new PointD(7.75, 4.125), new PointD(12.5, 7.875) })
        {
            draft = new() { Manifest = ManifestJson.Deserialize(ManifestJson.Serialize(original.Manifest)), Images = original.Images, Masks = original.Masks };
            draft.Manifest.Layers[0].Transform.Origin = origin;
            compositor.SetSnapshot(draft);
            // A blur needs its below stack over the halo too; the App expands that dirty
            // region before Render. Point adjustments can be replayed in separate bands.
            if (kind != AdjustmentKind.GaussianBlur) compositor.Render(0, 0, width, 11, cacheAdjustments: false);
            var actual = compositor.Render(0, kind == AdjustmentKind.GaussianBlur ? 0 : 11, width, height, cacheAdjustments: false);
            Assert.Equal(new CanvasCompositor(draft).Render().Pixels, actual.Pixels);
        }
        compositor.SetSnapshot(original);
        Assert.Equal(new CanvasCompositor(original).Render().Pixels, compositor.Render().Pixels);
        compositor.SetSnapshot(draft);
        compositor.Render(0, 0, width, height, cacheAdjustments: false);
        // Commit fills the current caches; undo must not reuse those at the old placement.
        Assert.Equal(new CanvasCompositor(draft).Render().Pixels, compositor.Render().Pixels);
        compositor.SetSnapshot(original);
        Assert.Equal(new CanvasCompositor(original).Render().Pixels, compositor.Render().Pixels);
        compositor.SetSnapshot(draft);
        Assert.Equal(new CanvasCompositor(draft).Render().Pixels, compositor.Render().Pixels);
    }
}
