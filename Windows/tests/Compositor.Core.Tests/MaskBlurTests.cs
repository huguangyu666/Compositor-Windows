using Xunit;

namespace Compositor.Core.Tests;

public class MaskBlurTests
{
    private static MaskSurface Pattern(int width, int height)
    {
        var mask = new MaskSurface(width, height);
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            mask.Pixels[y * width + x] = (byte)((x * 37 + y * 53) % 256);
        return mask;
    }

    [Theory]
    [InlineData(1, 19, .5)]
    [InlineData(31, 1, 2)]
    [InlineData(79, 61, 3)]
    [InlineData(179, 103, 10)]
    public void GrayWindowsMatchPaddedRgbaKernelAtEdges(int width, int height, double sigma)
    {
        var mask = Pattern(width, height); var bytes = mask.Pixels.ToArray(); byte background = MaskDisplay.Background(mask);
        int margin = (int)Math.Ceiling(sigma * 3); var full = new Surface(width + 2 * margin, height + 2 * margin);
        for (int y = 0; y < full.Height; y++) for (int x = 0; x < full.Width; x++)
        {
            byte value = x >= margin && x < width + margin && y >= margin && y < height + margin
                ? mask.Pixels[(y - margin) * width + x - margin] : background;
            int at = y * full.Stride + x * 4;
            full.Pixels[at] = full.Pixels[at + 1] = full.Pixels[at + 2] = value; full.Pixels[at + 3] = 255;
        }
        AdjustmentEngine.ApplyGaussianBlur(sigma, full, 0, 0, full.Width, full.Height);
        var sample = new GaussianMaskBlurSample(mask, sigma, background);
        foreach (var point in new[] { new PointD(0, 0), new(width - 1, height - 1), new(width / 2, height / 2) })
        {
            var region = RectI.Normalize((int)point.X - 3, (int)point.Y - 4, (int)point.X + 4, (int)point.Y + 5, width, height);
            var window = sample.Render(region);
            for (int y = region.Y; y < region.Y + region.Height; y++) for (int x = region.X; x < region.X + region.Width; x++)
                Assert.Equal(full.Pixels[(y + margin) * full.Stride + (x + margin) * 4], window.Pixels[(y - window.Bounds.Y) * window.Bounds.Width + x - window.Bounds.X]);
            Assert.InRange(sample.RetainedBytes, 0, 2 * 1024 * 1024);
        }
        Assert.Equal(bytes, mask.Pixels);
    }

    [Theory]
    [InlineData("image")]
    [InlineData("folder")]
    [InlineData("adjustment")]
    public void BlurEditsOnlyTheMaskAndUndoesOnce(string kind)
    {
        var scene = EditorDocument.Create(64, 64); var id = scene.Snapshot.Manifest.Layers[0].Id;
        if (kind == "folder") id = scene.GroupLayers([id])!.Id;
        if (kind == "adjustment") id = scene.AddAdjustment(AdjustmentKind.Levels).Id;
        var mask = Pattern(32, 32); scene.AddRasterMask(id, mask);
        var layer = scene.Snapshot.Manifest.Layers.Single(layer => layer.Id == id);
        layer.MaskPlacement = new() { Origin = new(10, 12), Size = new(16, 16), Rotation = 17, FlipX = true };
        scene = new(scene.Snapshot); var images = scene.Snapshot.Images;
        var key = new object(); var point = BrushEngine.PixelToDocument(layer.MaskPlacement, 32, 32, new(16.5, 16.5));
        Assert.True(scene.BeginMaskBlurStroke(id, 1, new(3, 1, .25, 255, 0, 0), point, key));
        scene.EndStroke(key); var edited = scene.Snapshot.Masks[id];
        var sample = new GaussianMaskBlurSample(mask, 2, MaskDisplay.Background(mask)).Render(new(16, 16, 1, 1));
        byte softened = sample.Pixels[(16 - sample.Bounds.Y) * sample.Bounds.Width + 16 - sample.Bounds.X];
        Assert.Equal((byte)MathF.Round(mask.Pixels[16 * 32 + 16] * .75f + softened * .25f), edited.Pixels[16 * 32 + 16]);
        Assert.False(mask.Pixels.AsSpan().SequenceEqual(edited.Pixels)); Assert.Same(images, scene.Snapshot.Images);
        Assert.True(scene.Undo()); Assert.False(scene.CanUndo); Assert.Same(mask, scene.Snapshot.Masks[id]);
        Assert.True(scene.Redo()); Assert.Same(edited, scene.Snapshot.Masks[id]);
    }

    [Fact]
    public void SelectedBlurPreservesOutsideAndSharedSource()
    {
        var scene = EditorDocument.Create(32, 32); var id = scene.Snapshot.Manifest.Layers[0].Id;
        var other = scene.ImportImage(new Surface(32, 32), "Other").Id; var mask = Pattern(32, 32);
        scene.AddRasterMask(id, mask); scene.AddRasterMask(other, mask); scene = new(scene.Snapshot);
        scene.SetSelection(Selection.Rectangle(32, 32, 16, 0, 16, 32));
        var key = new object(); Assert.True(scene.BeginMaskBlurStroke(id, 2, new(6, 1, 1, 0, 0, 0), new(16, 16), key));
        scene.ContinueStroke(key, new(20, 16)); scene.EndStroke(key);
        Assert.Same(mask, scene.Snapshot.Masks[other]); Assert.NotSame(mask, scene.Snapshot.Masks[id]);
        var edited = scene.Snapshot.Masks[id];
        for (int y = 0; y < 32; y++) for (int x = 0; x < 16; x++) Assert.Equal(mask.Pixels[y * 32 + x], edited.Pixels[y * 32 + x]);
    }

    [Fact]
    public void DisabledUniformOrEmptySelectionDoesNotOpenHistory()
    {
        var scene = EditorDocument.Create(16, 16); var id = scene.Snapshot.Manifest.Layers[0].Id;
        scene.AddRasterMask(id, MaskSurface.Solid(true)); scene = new(scene.Snapshot);
        Assert.False(scene.BeginMaskBlurStroke(id, 2, new(3, 1, 1, 0, 0, 0), new(8, 8), new object())); Assert.False(scene.CanUndo);
        scene.RemoveMask(id); scene.AddRasterMask(id, Pattern(16, 16)); scene.SetMaskEnabled(id, false); scene = new(scene.Snapshot);
        Assert.False(scene.BeginMaskBlurStroke(id, 2, new(3, 1, 1, 0, 0, 0), new(8, 8), new object())); Assert.False(scene.CanUndo);
        scene.SetMaskEnabled(id, true); scene = new(scene.Snapshot); scene.SetSelection(new(new MaskSurface(16, 16), new(0, 0, 0, 0)));
        Assert.False(scene.BeginMaskBlurStroke(id, 2, new(3, 1, 1, 0, 0, 0), new(8, 8), new object())); Assert.False(scene.CanUndo);
    }

    [Fact]
    public void UnchangedBlurDoesNotLeaveAnUndoStepOrDirtyFlag()
    {
        var scene = EditorDocument.Create(16, 16); var id = scene.Snapshot.Manifest.Layers[0].Id;
        var mask = new MaskSurface(16, 16); mask.Pixels.AsSpan().Fill(255); scene.AddRasterMask(id, mask); scene = new(scene.Snapshot);
        var key = new object(); Assert.True(scene.BeginMaskBlurStroke(id, 2, new(3, 1, 1, 0, 0, 0), new(8, 8), key));
        scene.EndStroke(key); Assert.False(scene.CanUndo); Assert.False(scene.IsDirty);
    }
}
