using Xunit;

namespace Compositor.Core.Tests;

public class GrayDistortTests
{
    [Theory]
    [InlineData(false, false, false, 0, 0)]
    [InlineData(false, true, false, 255, 0)]
    [InlineData(false, false, true, 0, 7)]
    [InlineData(false, true, true, 255, 7)]
    [InlineData(true, false, false, 0, 0)]
    [InlineData(true, true, false, 255, 0)]
    [InlineData(true, false, true, 0, 7)]
    [InlineData(true, true, true, 255, 7)]
    public void GrayWarpMatchesRgbaSamplingForPerspectiveFoldFlipsAndCappedPreviews(bool fold, bool flipX, bool flipY, byte background, int cap)
    {
        var mask = new MaskSurface(17, 13); new Random(7301).NextBytes(mask.Pixels);
        var original = mask.Pixels.ToArray(); var rgba = new Surface(mask.Width, mask.Height);
        for (int i = 0; i < mask.Pixels.Length; i++)
        { rgba.Pixels[i * 4] = rgba.Pixels[i * 4 + 1] = rgba.Pixels[i * 4 + 2] = mask.Pixels[i]; rgba.Pixels[i * 4 + 3] = 255; }
        var value = new LayerTransform { Origin = new(2, 3), Size = new(17, 13), FlipX = flipX, FlipY = flipY, Sampling = LayerSampling.HighQuality };
        PointD[] corners = [new(-1.25, 3.75), new(18.5, -.75), new(20.25, 12.25), new(2.5, 15.75)];
        if (fold) corners[0] = new(22.5, 17.25);
        var reference = DistortWarp.Warp(rgba, value, corners, limit: cap == 0 ? null : cap);
        var actual = DistortWarp.WarpMask(mask, value, corners, background, limit: cap == 0 ? null : cap);
        Assert.Equal(reference.Transform, actual.Transform); Assert.Equal(reference.Image.Width, actual.Mask.Width); Assert.Equal(reference.Image.Height, actual.Mask.Height);
        for (int i = 0; i < actual.Mask.Pixels.Length; i++)
            Assert.Equal(reference.Image.Pixels[i * 4 + 3] == 0 ? background : reference.Image.Pixels[i * 4], actual.Mask.Pixels[i]);
        Assert.Equal(original, mask.Pixels);
    }

    [Fact]
    public void UniformGrayMaskKeepsItsAssetAndWholePixelBounds()
    {
        var mask = new MaskSurface(1, 1, [177]); var value = new LayerTransform { Size = new(4, 5), Sampling = LayerSampling.Nearest };
        PointD[] corners = [new(-.25, 1.125), new(4.5, 0), new(4, 5.25), new(0, 5)];
        var result = DistortWarp.WarpMask(mask, value, corners, background: 255, limit: 2);
        Assert.Same(mask, result.Mask); Assert.Equal(new(-1, 0), result.Transform.Origin); Assert.Equal(new SizeD(6, 6), result.Transform.Size);
        Assert.Equal(LayerSampling.Nearest, result.Transform.Sampling);
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)]
    public void InvalidPreviewCapRejectsBeforeAllocation(int cap)
    {
        var mask = new MaskSurface(1, 1, [177]); var value = new LayerTransform { Size = new(4, 5) };
        Assert.Throws<ArgumentOutOfRangeException>(() => DistortWarp.WarpMask(mask, value, DistortWarp.CornersOf(value), limit: cap));
    }

    [Fact]
    public void GrayWarpRejectsCollapsedAndOversizedShapesAndHonorsCancellation()
    {
        var mask = new MaskSurface(3, 2); var value = new LayerTransform { Size = new(3, 2) };
        Assert.Throws<ArgumentOutOfRangeException>(() => DistortWarp.WarpMask(mask, value, [new(1, 1), new(2, 2), new(3, 3), new(4, 4)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => DistortWarp.WarpMask(mask, value, [new(0, 0), new(30000, 0), new(30000, 30000), new(0, 30000)]));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => DistortWarp.WarpMask(mask, value, DistortWarp.CornersOf(value), cancellationToken: cancellation.Token));
    }

    [Fact]
    public void GrayMaskCommitAllocatesOneOutputPlaneWithoutTemporaryRgbaPlanes()
    {
        const int side = 1024;
        var doc = EditorDocument.Create(1, 1); doc.Snapshot.Manifest.Width = doc.Snapshot.Manifest.Height = side;
        var layer = doc.Snapshot.Manifest.Layers[0]; layer.Transform.Size = new(side, side);
        layer.MaskFile = ProjectValidator.Uuid(layer.Id) + ".mask.png"; layer.MaskLinked = false;
        var mask = new MaskSurface(side, side); mask.Pixels.AsSpan().Fill(255); mask.Pixels[0] = 17;
        ((Dictionary<Guid, MaskSurface>)doc.Snapshot.Masks)[layer.Id] = mask;
        var edit = doc.BeginTransformDraft([layer.Id], onMask: true)!;
        var corners = DistortWarp.CornersOf(edit.Value); edit.PrepareDistortCommit(edit.Value, corners);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var result = edit.PrepareDistortCommit(edit.Value, corners)!;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < mask.PixelCount * 2 + 65536, $"Gray warp allocated {allocated} bytes for {mask.PixelCount} pixels.");
        Assert.Equal(mask.Pixels, result.Snapshot.Masks[layer.Id].Pixels);
        Assert.False(doc.CanUndo);
    }
}
