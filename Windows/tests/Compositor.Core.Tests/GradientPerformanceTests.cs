using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace Compositor.Core.Tests;

public class GradientPerformanceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SwappingSnapshotsMatchesFreshCompositeAndKeepsCanvas(bool interactive)
    {
        var doc = EditorDocument.Create(32, 24);
        var id = doc.Snapshot.Manifest.Layers[0].Id;
        var source = doc.Snapshot;
        var edit = doc.BeginGradient(id)!;
        var compositor = new CanvasCompositor(source);
        var canvas = compositor.Render();
        if (interactive) compositor.Render(0, 0, 32, 24, true);
        for (int frame = 0; frame < 3; frame++)
        {
            var preview = edit.Preview(new(.1, .2), new(28 + frame, 22), GradientScenario.Settings(frame))!;
            compositor.SetSnapshot(preview.Snapshot);
            Assert.Same(canvas, compositor.Render(0, 0, 32, 24, interactive));
            Assert.Equal(new CanvasCompositor(preview.Snapshot).Render().Pixels, canvas.Pixels);
        }
        compositor.SetSnapshot(source);
        Assert.Equal(new CanvasCompositor(source).Render().Pixels, compositor.Render().Pixels);
        Assert.Throws<ArgumentException>(() => compositor.SetSnapshot(EditorDocument.Create(33, 24).Snapshot));
        Assert.Same(source, compositor.Snapshot);
    }

    [Fact]
    public void SnapshotSwapKeepsWarmLayerBuffersWithinSmallAllocationBudget()
    {
        var doc = EditorDocument.Create(512, 512);
        var id = doc.Snapshot.Manifest.Layers[0].Id;
        var source = doc.Snapshot;
        var compositor = new CanvasCompositor(source);
        var canvas = compositor.Render();
        var preview = doc.BeginGradient(id)!.Preview(new(0, 0), new(500, 500), GradientScenario.Settings(0))!;
        compositor.SetSnapshot(preview.Snapshot); compositor.Render();
        compositor.SetSnapshot(source); compositor.Render();
        long before = GC.GetAllocatedBytesForCurrentThread();
        compositor.SetSnapshot(preview.Snapshot);
        var rendered = compositor.Render();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Same(canvas, rendered);
        Assert.True(allocated < 128_000, $"Warm snapshot swap allocated {allocated} bytes.");
        Assert.Equal(new CanvasCompositor(preview.Snapshot).Render().Pixels, rendered.Pixels);
    }

    [Theory]
    [InlineData("image")]
    [InlineData("mask")]
    [InlineData("hierarchy")]
    [InlineData("effects")]
    [InlineData("adjustment")]
    public void DerivedReferencesAndDependentCachesRefresh(string change)
    {
        var doc = EditorDocument.Create(32, 24);
        var id = doc.Snapshot.Manifest.Layers[0].Id;
        var top = new ProjectLayerRecord { Id = Guid.NewGuid(), Name = "Top", IsVisible = true,
            Transform = new() { Origin = new(6, 4), Size = new(20, 14) }, ImageFile = "top.png", MaskSourceId = id };
        doc.Snapshot.Manifest.Layers.Add(top);
        ((Dictionary<Guid, Surface>)doc.Snapshot.Images)[top.Id] = new Surface(20, 14);
        Array.Fill(doc.Snapshot.Images[top.Id].Pixels, (byte)180);
        var compositor = new CanvasCompositor(doc.Snapshot);
        compositor.Render(); compositor.Render(0, 0, 32, 24, true);
        var next = new ProjectSnapshot { Manifest = ManifestJson.Deserialize(ManifestJson.Serialize(doc.Snapshot.Manifest)),
            Images = new Dictionary<Guid, Surface>(doc.Snapshot.Images), Masks = new Dictionary<Guid, MaskSurface>() };
        var layer = next.Manifest.Layers[0];
        if (change == "image") ((Dictionary<Guid, Surface>)next.Images)[id] = new Surface(32, 24);
        if (change == "mask")
        { layer.MaskFile = "mask.png"; ((Dictionary<Guid, MaskSurface>)next.Masks)[id] = MaskSurface.Solid(false); }
        if (change == "hierarchy")
        {
            var folder = new ProjectLayerRecord { Id = Guid.NewGuid(), Name = "Folder", IsGroup = true, IsVisible = true, Opacity = .2 };
            next.Manifest.Layers.Add(folder); layer.ParentId = folder.Id;
        }
        if (change == "effects") layer.Effects = JsonSerializer.SerializeToElement(new LayerEffects {
            Shadow = new() { Blur = 2, Opacity = .8, Distance = 3 } }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        if (change == "adjustment")
            next.Manifest.Layers.Add(new ProjectLayerRecord { Id = Guid.NewGuid(), Name = "Invert", IsVisible = true,
                Transform = new() { Size = new(32, 24) }, Adjustment = new() { Kind = AdjustmentKind.Invert } });
        compositor.SetSnapshot(next);
        Assert.Equal(new CanvasCompositor(next).Render().Pixels, compositor.Render().Pixels);
    }

    [Fact]
    public void ChangedLayerBoundsContainEffectsDependentsAndBlurHalo()
    {
        var doc = EditorDocument.Create(128, 96);
        var id = doc.Snapshot.Manifest.Layers[0].Id;
        var layer = doc.Snapshot.Manifest.Layers[0];
        layer.Transform = new() { Origin = new(40, 30), Size = new(16, 12) };
        ((Dictionary<Guid, Surface>)doc.Snapshot.Images)[id] = new Surface(16, 12);
        Array.Fill(doc.Snapshot.Images[id].Pixels, (byte)200);
        layer.Effects = JsonSerializer.SerializeToElement(new LayerEffects {
            Shadow = new() { Blur = 2, Opacity = .8, Distance = 3 } }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var next = new ProjectSnapshot { Manifest = ManifestJson.Deserialize(ManifestJson.Serialize(doc.Snapshot.Manifest)),
            Images = new Dictionary<Guid, Surface>(doc.Snapshot.Images), Masks = doc.Snapshot.Masks };
        ((Dictionary<Guid, Surface>)next.Images)[id] = new Surface(16, 12);
        var before = new CanvasCompositor(doc.Snapshot).Render().Clone();
        var after = new CanvasCompositor(next).Render();
        var dirty = CanvasCompositor.ChangedLayerBounds(doc.Snapshot, next, id);
        Assert.True(dirty.Width < 128 && dirty.Height < 96);
        Assert.True(dirty.X < 40 && dirty.Y < 30);
        var warm = new CanvasCompositor(doc.Snapshot); warm.Render(); warm.SetSnapshot(next);
        Assert.Equal(after.Pixels, warm.Render(dirty.X, dirty.Y, dirty.X + dirty.Width, dirty.Y + dirty.Height).Pixels);
        for (int y = 0; y < 96; y++)
        for (int x = 0; x < 128; x++)
            if (!before.Pixels.AsSpan(y * before.Stride + x * 4, 4).SequenceEqual(after.Pixels.AsSpan(y * after.Stride + x * 4, 4)))
                Assert.True(x >= dirty.X && x < dirty.X + dirty.Width && y >= dirty.Y && y < dirty.Y + dirty.Height);
        var top = new ProjectLayerRecord { Id = Guid.NewGuid(), Name = "Clipped", IsVisible = true,
            Transform = new() { Origin = new(70, 10), Size = new(10, 10) }, MaskSourceId = id, ImageFile = "top.png" };
        next.Manifest.Layers.Add(top);
        next.Manifest.Layers.Add(new ProjectLayerRecord { Id = Guid.NewGuid(), Name = "Blur", IsVisible = true,
            Transform = new() { Size = new(128, 96) }, Adjustment = new() { Kind = AdjustmentKind.GaussianBlur, BlurRadius = 2 } });
        var dependencies = CanvasCompositor.ChangedLayerBounds(doc.Snapshot, next, id);
        Assert.True(dependencies.X <= dirty.X && dependencies.Y <= 10);
        Assert.True(dependencies.X + dependencies.Width > 80);
    }

    [Fact]
    public void RetainedDraftPixelsAndManifestSurviveLaterFramesAndCommitEdits()
    {
        var doc = EditorDocument.Create(32, 24);
        var id = doc.Snapshot.Manifest.Layers[0].Id;
        var edit = doc.BeginGradient(id)!;
        var first = edit.Preview(new(0, 0), new(31, 23), GradientScenario.Settings(0))!;
        byte[] bytes = (byte[])first.Snapshot.Images[id].Pixels.Clone();
        string manifest = ManifestJson.Serialize(first.Snapshot.Manifest);
        var second = edit.Preview(new(0, 0), new(25, 20), GradientScenario.Settings(1))!;
        Assert.Equal(bytes, first.Snapshot.Images[id].Pixels);
        Assert.True(doc.CommitGradient(second));
        doc.RenameLayer(id, "After commit");
        Assert.Equal(manifest, ManifestJson.Serialize(first.Snapshot.Manifest));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RealMaskDraftRendersOnlyItsConsumersAndRestoresOnCancel(bool folder)
    {
        var doc = EditorDocument.Create(128, 96);
        var layer = doc.Snapshot.Manifest.Layers[0];
        layer.Transform = new() { Origin = new(40, 30), Size = new(16, 12) };
        ((Dictionary<Guid, Surface>)doc.Snapshot.Images)[layer.Id] = new Surface(16, 12);
        Array.Fill(doc.Snapshot.Images[layer.Id].Pixels, (byte)200);
        var target = layer;
        if (folder)
        {
            target = new() { Id = Guid.NewGuid(), Name = "Folder", IsVisible = true, IsGroup = true, Transform = new() { Size = new(128, 96) } };
            doc.Snapshot.Manifest.Layers.Add(target); layer.ParentId = target.Id;
        }
        target.MaskFile = "mask.png";
        ((Dictionary<Guid, MaskSurface>)doc.Snapshot.Masks)[target.Id] = MaskSurface.Solid(true);
        doc.Snapshot.Manifest.Layers.Add(new ProjectLayerRecord { Id = Guid.NewGuid(), Name = "Blur", IsVisible = true,
            Transform = new() { Size = new(128, 96) }, Adjustment = new() { Kind = AdjustmentKind.GaussianBlur, BlurRadius = 2 } });
        var edit = doc.BeginGradient(target.Id, true)!;
        var dirty = edit.DirtyBounds();
        Assert.True(dirty.Width < 128 && dirty.Height < 96);
        var compositor = new CanvasCompositor(doc.Snapshot);
        var source = compositor.Render().Clone();
        for (int frame = 0; frame < 2; frame++)
        {
            var preview = edit.Preview(new(40, 30), new(56 - frame, 42), GradientScenario.Settings(frame))!;
            compositor.SetSnapshot(preview.Snapshot);
            Assert.Equal(new CanvasCompositor(preview.Snapshot).Render().Pixels,
                compositor.Render(dirty.X, dirty.Y, dirty.X + dirty.Width, dirty.Y + dirty.Height).Pixels);
        }
        compositor.SetSnapshot(doc.Snapshot);
        Assert.Equal(source.Pixels, compositor.Render(dirty.X, dirty.Y, dirty.X + dirty.Width, dirty.Y + dirty.Height).Pixels);
    }

    [Fact]
    public void InvalidDraftMetadataCannotConsumeRedoBeforeCommit()
    {
        var doc = EditorDocument.Create(32, 24);
        var id = doc.Snapshot.Manifest.Layers[0].Id;
        doc.RenameLayer(id, "Changed"); doc.Undo();
        var preview = doc.BeginGradient(id)!.Preview(new(0, 0), new(31, 23), GradientScenario.Settings(0))!;
        preview.Snapshot.Manifest.Layers[0].Transform.Origin = new(double.NaN, 0);
        Assert.Throws<ArgumentException>(() => doc.CommitGradient(preview));
        Assert.True(doc.CanRedo);
        Assert.False(doc.CanUndo);
        Assert.False(doc.IsDirty);
    }

    public static IEnumerable<object[]> LegacyFrames()
    {
        using var stream = typeof(GradientPerformanceTests).Assembly.GetManifestResourceStream("Compositor.Core.Tests.Fixtures.gradient-p78-reference.json")!;
        using var json = JsonDocument.Parse(stream);
        foreach (var row in json.RootElement.EnumerateArray())
            yield return [row.GetProperty("scenario").GetInt32(), row.GetProperty("hash").GetString()!];
    }

    [Theory]
    [MemberData(nameof(LegacyFrames))]
    public void OptimizedPreviewMatchesP78Pixels(int scenario, string hash)
    {
        var (doc, id, onMask) = GradientScenario.Create(scenario);
        var preview = doc.BeginGradient(id, onMask)!.Preview(new(3.12, 2.43), new(37.62, 24.14), GradientScenario.Settings(scenario))!;
        var bytes = onMask ? preview.Snapshot.Masks[id].Pixels : preview.Snapshot.Images[id].Pixels;
        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(bytes)));
    }
}
