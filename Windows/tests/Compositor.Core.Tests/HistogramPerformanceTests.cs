using System.Text.Json;
using Xunit;

namespace Compositor.Core.Tests;

public class HistogramPerformanceTests
{
    private static (EditorDocument Doc, Guid Target) Scene()
    {
        var doc = EditorDocument.Create(8, 8);
        var image = doc.Snapshot.Images[doc.Snapshot.Manifest.Layers[0].Id];
        for (int i = 0; i < image.Pixels.Length; i += 4)
        {
            byte value = (byte)(i % 8 == 0 ? 60 : 90);
            image.Pixels[i] = image.Pixels[i + 1] = image.Pixels[i + 2] = value;
            image.Pixels[i + 3] = 255;
        }
        image.MarkModified();
        return (doc, doc.AddAdjustment(AdjustmentKind.Levels).Id);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HistogramAndAutoUseInputBeforeTargetEvenWhenHidden(bool visible)
    {
        var (doc, target) = Scene();
        doc.MutateLevels(target, levels => levels.Ranges[0].Black = 70);
        doc.SetVisible(target, visible);
        doc.AddAdjustment(AdjustmentKind.Invert);
        var bins = LevelsHistogramInput.Capture(doc.Snapshot, target).Compute().CopyBins();
        Assert.Equal(32, bins[1][60]); Assert.Equal(32, bins[3][90]);
        Assert.True(doc.AutoLevels(target, LevelsAutoMode.Contrast));
        var range = doc.Snapshot.Manifest.Layers.Single(layer => layer.Id == target).Adjustment!.Levels.Ranges[0];
        Assert.Equal(60, range.Black); Assert.Equal(90, range.White);
        doc.Undo();
        Assert.Equal(70, doc.Snapshot.Manifest.Layers.Single(layer => layer.Id == target).Adjustment!.Levels.Ranges[0].Black);
    }

    [Fact]
    public void StraightColorRoundingMatchesCAtHalfwayValues()
    {
        var surface = new Surface(1, 1, [1, 1, 1, 6], 4); // 42.5, C round -> 43
        var bins = LevelsHistogram.Sum(surface, null);
        Assert.Equal(6 / 255.0, bins[3][43]); Assert.Equal(0, bins[3][42]);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(1000000, 8)]
    public void DisplayScaleLimitsEndpointSpikes(double endpoint, double expected)
    {
        var bins = new double[256]; bins[0] = endpoint;
        if (endpoint > 1) { bins[1] = 1; bins[2] = 2; bins[3] = 2; }
        Assert.Equal(expected, LevelsHistogram.DisplayScale(bins));
        bins[255] = double.PositiveInfinity; bins[50] = double.NaN;
        Assert.Equal(expected, LevelsHistogram.DisplayScale(bins));
    }

    [Theory]
    [InlineData("rename", false)]
    [InlineData("active", false)]
    [InlineData("target", false)]
    [InlineData("above", false)]
    [InlineData("pixels", true)]
    [InlineData("replacement", true)]
    [InlineData("transform", true)]
    [InlineData("opacity", true)]
    [InlineData("visibility", true)]
    [InlineData("blend", true)]
    [InlineData("mask", true)]
    [InlineData("mask-revision", true)]
    [InlineData("mask-placement", true)]
    [InlineData("mask-enabled", true)]
    [InlineData("effects", true)]
    [InlineData("prior-adjustment", true)]
    [InlineData("order", true)]
    public async Task CacheTracksRenderingInputs(string change, bool invalidates)
    {
        var (doc, target) = Scene();
        var prior = doc.AddAdjustment(AdjustmentKind.Exposure);
        doc.Snapshot.Manifest.Layers.Remove(prior); doc.Snapshot.Manifest.Layers.Insert(1, prior);
        var above = doc.AddAdjustment(AdjustmentKind.Invert);
        doc.Snapshot.Manifest.Layers.Remove(above); doc.Snapshot.Manifest.Layers.Add(above);
        var bottom = doc.Snapshot.Manifest.Layers[0];
        bottom.MaskFile = "mask.png";
        ((Dictionary<Guid, MaskSurface>)doc.Snapshot.Masks)[bottom.Id] = MaskSurface.Solid(true);
        var cache = new LevelsHistogramCache();
        var first = await cache.GetAsync(LevelsHistogramInput.Capture(doc.Snapshot, target));
        switch (change)
        {
            case "rename": bottom.Name = "Renamed"; break;
            case "active": doc.Snapshot.Manifest.ActiveLayerId = above.Id; break;
            case "target": doc.MutateLevels(target, levels => levels.Ranges[0].Gamma = 2); break;
            case "above": above.Adjustment!.Kind = AdjustmentKind.Exposure; break;
            case "pixels": doc.Snapshot.Images[bottom.Id].MarkModified(); break;
            case "replacement": ((Dictionary<Guid, Surface>)doc.Snapshot.Images)[bottom.Id] = doc.Snapshot.Images[bottom.Id].Clone(); break;
            case "transform": bottom.Transform.Origin = new(1, 0); break;
            case "opacity": bottom.Opacity = .5; break;
            case "visibility": bottom.IsVisible = false; break;
            case "blend": bottom.BlendMode = LayerBlendMode.Multiply; break;
            case "mask": ((Dictionary<Guid, MaskSurface>)doc.Snapshot.Masks)[bottom.Id] = MaskSurface.Solid(false); break;
            case "mask-revision": doc.Snapshot.Masks[bottom.Id].MarkModified(); break;
            case "mask-placement": bottom.MaskLinked = false; bottom.MaskPlacement = new() { Origin = new(1, 1), Size = new(2, 2) }; break;
            case "mask-enabled": bottom.MaskEnabled = false; break;
            case "effects": bottom.Effects = JsonSerializer.SerializeToElement(new LayerEffects { Shadow = new() { Distance = 2 } }, ManifestJson.CompactOptions); break;
            case "prior-adjustment": prior.Adjustment!.ExposureSettings = new() { Exposure = 1 }; break;
            case "order": doc.Snapshot.Manifest.Layers.Remove(prior); doc.Snapshot.Manifest.Layers.Add(prior); break;
        }
        var input = LevelsHistogramInput.Capture(doc.Snapshot, target);
        Assert.Equal(!invalidates, first.Matches(doc.Snapshot, target));
        var next = await cache.GetAsync(input);
        Assert.Equal(!invalidates, ReferenceEquals(first, next));
        Assert.Equal(input.Compute().CopyBins().SelectMany(row => row), next.CopyBins().SelectMany(row => row));
    }

    [Fact]
    public async Task LiveSourceAboveTargetAndFolderMasksInvalidateInput()
    {
        var (doc, target) = Scene();
        var bottom = doc.Snapshot.Manifest.Layers[0];
        var source = new ProjectLayerRecord { Id = Guid.NewGuid(), Name = "Source", ImageFile = "source.png", IsVisible = false,
            Transform = new() { Size = new(8, 8) } };
        doc.Snapshot.Manifest.Layers.Add(source);
        var sourceImage = new Surface(8, 8); Array.Fill(sourceImage.Pixels, (byte)255);
        ((Dictionary<Guid, Surface>)doc.Snapshot.Images)[source.Id] = sourceImage;
        bottom.MaskSourceId = source.Id;
        var folder = new ProjectLayerRecord { Id = Guid.NewGuid(), Name = "Folder", IsGroup = true, IsVisible = true, Opacity = .5,
            MaskFile = "folder.png", Transform = new() { Size = new(8, 8) } };
        doc.Snapshot.Manifest.Layers.Add(folder); bottom.ParentId = folder.Id;
        // Place target in the same folder, after the child in hierarchy order.
        doc.Snapshot.Manifest.Layers.Single(layer => layer.Id == target).ParentId = folder.Id;
        ((Dictionary<Guid, MaskSurface>)doc.Snapshot.Masks)[folder.Id] = MaskSurface.Solid(true);
        var cache = new LevelsHistogramCache();
        var result = await cache.GetAsync(LevelsHistogramInput.Capture(doc.Snapshot, target));
        Assert.Equal(LevelsHistogram.Sum(new CanvasCompositor(doc.Snapshot).RenderUntil(target), null).SelectMany(row => row), result.CopyBins().SelectMany(row => row));
        sourceImage.Pixels[3] = 0; sourceImage.MarkModified();
        Assert.False(result.Matches(doc.Snapshot, target));
        result = await cache.GetAsync(LevelsHistogramInput.Capture(doc.Snapshot, target));
        doc.Snapshot.Masks[folder.Id].MarkModified();
        Assert.False(result.Matches(doc.Snapshot, target));
    }

    [Fact]
    public async Task MetadataIsFrozenAndChangedPixelsCannotPublish()
    {
        var (doc, target) = Scene();
        var input = LevelsHistogramInput.Capture(doc.Snapshot, target);
        doc.Snapshot.Manifest.Layers[0].Opacity = 0;
        Assert.Equal(32, input.Compute().CopyBins()[1][60]);
        Assert.False(input.Compute().Matches(doc.Snapshot, target));
        var cache = new LevelsHistogramCache();
        doc.Snapshot.Images[doc.Snapshot.Manifest.Layers[0].Id].MarkModified();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetAsync(input));
        Assert.ThrowsAny<OperationCanceledException>(() => LevelsHistogram.Sum(new Surface(1, 1), null, new CancellationToken(true)));
    }

    [Fact]
    public async Task CachedAutoRejectsStaleDataWithoutUndoOrRedoChange()
    {
        var (doc, target) = Scene();
        var result = await new LevelsHistogramCache().GetAsync(LevelsHistogramInput.Capture(doc.Snapshot, target));
        var copy = result.CopyBins(); copy[1][60] = 0;
        Assert.Equal(32, result.CopyBins()[1][60]);
        doc.RenameLayer(target, "Temp"); doc.Undo();
        Assert.True(doc.CanRedo);
        doc.Snapshot.Images[doc.Snapshot.Manifest.Layers[0].Id].MarkModified();
        Assert.False(doc.AutoLevels(target, LevelsAutoMode.Contrast, result));
        Assert.True(doc.CanRedo);
    }

    [Fact]
    public void OnePassCompositorAvoidsFullCanvasCachePerLayer()
    {
        var (doc, target) = Scene();
        doc.Snapshot.Manifest.Layers[0].Opacity = .6;
        doc.Snapshot.Manifest.Layers[0].Effects = JsonSerializer.SerializeToElement(new LayerEffects { Shadow = new() { Blur = 1 } }, ManifestJson.CompactOptions);
        var expected = new CanvasCompositor(doc.Snapshot).RenderUntil(target);
        Assert.Equal(expected.Pixels, new CanvasCompositor(doc.Snapshot, cacheLayers: false).RenderUntil(target).Pixels);
        Assert.ThrowsAny<OperationCanceledException>(() => new CanvasCompositor(doc.Snapshot).RenderUntil(target, new CancellationToken(true)));
    }

    [Fact]
    public async Task WorkerReturnsImmediatelyAndCanceledWorkDoesNotEnterCache()
    {
        var (doc, target) = Scene();
        var input = LevelsHistogramInput.Capture(doc.Snapshot, target);
        int ownerThread = Environment.CurrentManagedThreadId, calls = 0;
        var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var cache = new LevelsHistogramCache((request, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            { started.SetResult(Environment.CurrentManagedThreadId); release.Wait(token); }
            return request.Compute(token);
        });
        var first = cache.GetAsync(input, cancellation.Token);
        try
        {
            Assert.NotEqual(ownerThread, await started.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.False(first.IsCompleted);
            var queued = cache.GetAsync(input);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            var result = await queued.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Same(result, await cache.GetAsync(input));
            Assert.Equal(2, calls);
        }
        finally { cancellation.Cancel(); release.Set(); }
    }

    [Fact]
    public async Task ClearingCacheDuringWorkerPreventsOldDocumentInsertion()
    {
        var (doc, target) = Scene(); var input = LevelsHistogramInput.Capture(doc.Snapshot, target);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(); int calls = 0;
        var cache = new LevelsHistogramCache((request, token) =>
        { if (Interlocked.Increment(ref calls) == 1) { started.SetResult(); release.Wait(token); } return request.Compute(token); });
        var first = cache.GetAsync(input);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cache.Clear(); release.Set();
            var old = await first;
            Assert.NotSame(old, await cache.GetAsync(input)); Assert.Equal(2, calls);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task CacheEvictsLeastRecentlyUsedResult()
    {
        var (doc, target) = Scene(); var cache = new LevelsHistogramCache();
        var input = LevelsHistogramInput.Capture(doc.Snapshot, target);
        var first = await cache.GetAsync(input);
        for (int i = 1; i <= 8; i++)
        { doc.Snapshot.Manifest.Layers[0].Opacity = i / 10.0; await cache.GetAsync(LevelsHistogramInput.Capture(doc.Snapshot, target)); }
        Assert.NotSame(first, await cache.GetAsync(input));
    }
}
