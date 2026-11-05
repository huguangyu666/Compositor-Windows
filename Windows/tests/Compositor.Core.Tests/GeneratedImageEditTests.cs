using Xunit;

namespace Compositor.Core.Tests;

public class GeneratedImageEditTests
{
    [Fact]
    public void TransparentResultMarginsAreRemovedWithoutMovingOrChangingTheCanvas()
    {
        var document = EditorDocument.Create(8, 6);
        document.SetSelection(Selection.Rectangle(8, 6, 0, 0, 8, 6));
        var edit = GeneratedImageEdit.Capture(document);
        var generated = new Surface(8, 6);
        generated.Pixels[(2 * 8 + 3) * 4] = 200; generated.Pixels[(2 * 8 + 3) * 4 + 3] = 255;
        generated.Pixels[(3 * 8 + 4) * 4 + 3] = 1;
        var result = edit.PrepareResult(generated);
        var preview = edit.Preview(result, "Star");
        var layer = preview.Manifest.Layers[^1];
        Assert.Equal(new PointD(3, 2), layer.Transform.Origin);
        Assert.Equal(new SizeD(2, 2), layer.Transform.Size);
        Assert.Equal((2, 2), (preview.Images[layer.Id].Width, preview.Images[layer.Id].Height));
        Assert.Equal(1, preview.Images[layer.Id].Pixels[^1]);
        Assert.Equal((8, 6), (result.Width, result.Height));
        Assert.Equal((8, 6), (preview.Manifest.Width, preview.Manifest.Height));
        var expected = new CanvasCompositor(preview).Render().Pixels;
        Assert.True(document.AcceptGeneratedImage(edit, result, "Star"));
        Assert.Equal(expected, new CanvasCompositor(document.Snapshot).Render().Pixels);
        Assert.True(document.Undo()); Assert.Equal(1, document.LayerCount);
        Assert.True(document.Redo());
        Assert.Equal(expected, new CanvasCompositor(document.Snapshot).Render().Pixels);
    }

    [Fact]
    public void OneSidedExpansionStoresOnlyTheGeneratedStrip()
    {
        var document = EditorDocument.Create(2, 2);
        var edit = GeneratedImageEdit.Capture(document, right: 1);
        var preview = edit.Preview(edit.PrepareResult(Red(3, 2)), "Expand");
        var layer = preview.Manifest.Layers[^1];
        Assert.Equal(new PointD(2, 0), layer.Transform.Origin);
        Assert.Equal(new SizeD(1, 2), layer.Transform.Size);
        Assert.Equal((3, 2), (preview.Manifest.Width, preview.Manifest.Height));
    }

    private static Surface Red(int width, int height)
    {
        var image = new Surface(width, height);
        for (int i = 0; i < image.Pixels.Length; i += 4) { image.Pixels[i] = 200; image.Pixels[i + 3] = 255; }
        return image;
    }

    [Fact]
    public void SelectionProtectsOutsideAndAcceptIsOneUndo()
    {
        var document = EditorDocument.Create(4, 3);
        var mask = new MaskSurface(4, 3); mask.Pixels[5] = 255; mask.Pixels[6] = 128;
        document.SetSelection(Selection.FromCoverage(mask));
        var edit = GeneratedImageEdit.Capture(document);
        var input = edit.RenderInput();
        Assert.Equal(255, input.Mask.Pixels[3]);
        Assert.Equal(0, input.Mask.Pixels[5 * 4 + 3]);
        Assert.Equal(127, input.Mask.Pixels[6 * 4 + 3]);
        var result = edit.PrepareResult(Red(4, 3));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, result.Pixels[..4]);
        Assert.Equal(new byte[] { 200, 0, 0, 255 }, result.Pixels[20..24]);
        Assert.Equal(new byte[] { 100, 0, 0, 128 }, result.Pixels[24..28]);
        Assert.True(document.AcceptGeneratedImage(edit, result, "AI Replace"));
        Assert.Equal(2, document.LayerCount);
        Assert.Equal("AI Replace", document.Snapshot.Manifest.Layers[^1].Name);
        var composite = new CanvasCompositor(document.Snapshot).Render();
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, composite.Pixels[..4]);
        Assert.True(document.Undo()); Assert.Equal(1, document.LayerCount);
        Assert.False(document.CanUndo);
        Assert.True(document.Redo()); Assert.Equal(2, document.LayerCount);
    }

    [Fact]
    public void ExpansionPreservesOriginalAndMovesLayersInOneUndo()
    {
        var document = EditorDocument.Create(2, 2);
        var original = document.Snapshot.Manifest.Layers[0];
        var edit = GeneratedImageEdit.Capture(document, left: 1, right: 2, top: 1);
        var input = edit.RenderInput();
        Assert.Equal((5, 3), (input.Image.Width, input.Image.Height));
        Assert.Equal(0, input.Image.Pixels[3]);
        Assert.Equal(255, input.Image.Pixels[input.Image.Stride + 7]);
        Assert.Equal(0, input.Mask.Pixels[3]);
        Assert.Equal(255, input.Mask.Pixels[input.Mask.Stride + 7]);
        var result = edit.PrepareResult(Red(5, 3));
        Assert.Equal(0, result.Pixels[result.Stride + 7]);
        Assert.True(document.AcceptGeneratedImage(edit, result, "AI Expand"));
        Assert.Equal((5, 3), (document.Snapshot.Manifest.Width, document.Snapshot.Manifest.Height));
        Assert.Equal(new PointD(1, 1), document.Snapshot.Manifest.Layers[0].Transform.Origin);
        Assert.Equal(new PointD(0, 0), original.Transform.Origin);
        Assert.True(document.Undo());
        Assert.Equal((2, 2), (document.Snapshot.Manifest.Width, document.Snapshot.Manifest.Height));
        Assert.Equal(1, document.LayerCount); Assert.False(document.CanUndo);
    }

    [Fact]
    public void ChangedDocumentRejectsLateResultWithoutHistory()
    {
        var document = EditorDocument.Create(2, 2);
        var edit = GeneratedImageEdit.Capture(document, right: 1);
        var result = edit.PrepareResult(Red(3, 2));
        document.Snapshot.Manifest.Layers[0].Name = "Changed";
        Assert.False(document.AcceptGeneratedImage(edit, result, "AI"));
        Assert.False(document.CanUndo); Assert.Equal(1, document.LayerCount);
    }

    [Fact]
    public void EmptySelectionAndOversizedExpansionAreRejectedBeforeRendering()
    {
        var document = EditorDocument.Create(2, 2);
        Assert.Throws<InvalidOperationException>(() => GeneratedImageEdit.Capture(document));
        Assert.Throws<ArgumentOutOfRangeException>(() => GeneratedImageEdit.Capture(document, right: Limits.MaxSide));
        Assert.Throws<OperationCanceledException>(() => GeneratedImageEdit.Capture(document, right: 1)
            .RenderInput(new CancellationToken(true)));
        Assert.False(document.CanUndo);
    }

    [Fact]
    public void FeatherUsesTheExistingSelectionCoverage()
    {
        var document = EditorDocument.Create(9, 9);
        var coverage = new MaskSurface(9, 9); coverage.Pixels[40] = 255;
        var selection = new Selection(coverage, new RectI(4, 4, 1, 1), feather: 2);
        document.SetSelection(selection);
        var edit = GeneratedImageEdit.Capture(document);
        var result = edit.PrepareResult(Red(9, 9));
        for (int y = 0; y < 9; y++)
            for (int x = 0; x < 9; x++)
                Assert.Equal(selection.CoverageAt(x, y), result.Pixels[y * result.Stride + x * 4 + 3]);
    }

    [Fact]
    public async Task ImageDecodeChecksDimensionsBeforeAllocatingPixels()
    {
        var png = await WicImaging.EncodePngAsync(Red(3, 2));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => WicImaging.DecodeSurfaceAsync(png, 2, 6));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => WicImaging.DecodeSurfaceAsync(png, 3, 5));
        var decoded = await WicImaging.DecodeSurfaceAsync(png, 3, 6);
        Assert.Equal((3, 2), (decoded.Width, decoded.Height));
    }

    [Fact]
    public void ExpansionRefusesInvalidShiftBeforeSending()
    {
        var document = EditorDocument.Create(2, 2);
        document.Snapshot.Manifest.Layers[0].Transform.Origin = new PointD(1_000_000, 0);
        Assert.Throws<InvalidOperationException>(() => GeneratedImageEdit.Capture(document, left: 2));
        Assert.False(document.CanUndo);
    }

    [Fact]
    public void RevisedPixelsOrSelectionRejectTheResult()
    {
        var document = EditorDocument.Create(2, 2);
        var edit = GeneratedImageEdit.Capture(document, right: 1);
        var result = edit.PrepareResult(Red(3, 2));
        document.Snapshot.Images.Values.Single().MarkModified();
        Assert.False(document.AcceptGeneratedImage(edit, result, "AI"));
        edit = GeneratedImageEdit.Capture(document, right: 1);
        document.SetSelection(Selection.FromCoverage(new MaskSurface(2, 2)));
        Assert.False(document.AcceptGeneratedImage(edit, result, "AI"));
        Assert.False(document.CanUndo);
    }
}
