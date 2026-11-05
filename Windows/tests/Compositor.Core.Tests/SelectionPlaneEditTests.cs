using Xunit;

namespace Compositor.Core.Tests;

public class SelectionPlaneEditTests
{
    private static EditorDocument Scene(out Guid id)
    {
        var scene = EditorDocument.Create(16, 16);
        var image = new Surface(4, 2); image.Pixels.AsSpan().Fill(255);
        var layer = scene.ImportImage(image, "Target"); id = layer.Id;
        layer.Transform = new() { Size = new(4, 2), Sampling = LayerSampling.Nearest };
        return scene;
    }

    [Fact]
    public void UniformMaskFillExpandsOnlyTheEditedLayerAndClipsToSelection()
    {
        var scene = Scene(out var id); var shared = MaskSurface.Solid(revealing: true);
        scene.AddRasterMask(id, shared);
        var other = scene.Snapshot.Manifest.Layers[0].Id; scene.AddRasterMask(other, shared);
        scene = new EditorDocument(scene.Snapshot); scene.SetSelection(Selection.Rectangle(16, 16, 1, 0, 1, 1));
        Assert.True(scene.FillMask(id, 0));
        var edited = scene.Snapshot.Masks[id];
        Assert.Equal(4, edited.Width); Assert.Equal(2, edited.Height);
        Assert.Equal(new byte[] { 255, 0, 255, 255, 255, 255, 255, 255 }, edited.Pixels);
        Assert.Same(shared, scene.Snapshot.Masks[other]); Assert.Equal(255, shared.Pixels[0]);
        Assert.True(scene.Undo()); Assert.False(scene.CanUndo); Assert.Same(shared, scene.Snapshot.Masks[id]);
        Assert.True(scene.Redo()); Assert.Equal(0, scene.Snapshot.Masks[id].Pixels[1]);
    }

    [Fact]
    public void MaskFillUsesItsIndependentPlacementAndFlips()
    {
        var scene = Scene(out var id); var mask = new MaskSurface(4, 2); Array.Fill(mask.Pixels, (byte)255);
        scene.AddRasterMask(id, mask);
        var layer = scene.Snapshot.Manifest.Layers.Single(layer => layer.Id == id);
        layer.MaskLinked = false; layer.MaskPlacement = new() { Origin = new(8, 4), Size = new(4, 2), FlipX = true };
        scene = new EditorDocument(scene.Snapshot); scene.SetSelection(Selection.Rectangle(16, 16, 8, 4, 1, 1));
        Assert.True(scene.FillMask(id, 0));
        Assert.Equal(new byte[] { 255, 255, 255, 0, 255, 255, 255, 255 }, scene.Snapshot.Masks[id].Pixels);
        Assert.Equal(255, mask.Pixels[3]); Assert.True(scene.Undo()); Assert.False(scene.CanUndo);
    }

    [Fact]
    public void ClearingPixelsMapsTheSelectionThroughLayerFlips()
    {
        var scene = Scene(out var id);
        scene.Snapshot.Manifest.Layers.Single(layer => layer.Id == id).Transform.FlipX = true;
        scene = new EditorDocument(scene.Snapshot); var original = scene.Snapshot.Images[id];
        var selection = Selection.Rectangle(16, 16, 0, 0, 1, 1);
        Assert.True(scene.CutSelectionMasked(id, selection));
        var pixels = scene.Snapshot.Images[id].Pixels;
        Assert.All(pixels.Skip(12).Take(4), value => Assert.Equal(0, value));
        Assert.All(pixels.Take(12).Concat(pixels.Skip(16)), value => Assert.Equal(255, value));
        Assert.True(scene.Undo()); Assert.False(scene.CanUndo); Assert.Same(original, scene.Snapshot.Images[id]);
    }

    [Fact]
    public void EmptySelectionDoesNotFillAnEntireUniformMaskOrCreateHistory()
    {
        var scene = Scene(out var id); var mask = MaskSurface.Solid(revealing: true); scene.AddRasterMask(id, mask);
        scene = new EditorDocument(scene.Snapshot); var empty = new Selection(new(16, 16), new(0, 0, 0, 0));
        scene.SetSelection(empty);
        Assert.False(scene.FillMask(id, 0)); Assert.False(scene.CutSelectionMasked(id, empty));
        Assert.False(scene.CanUndo); Assert.Same(mask, scene.Snapshot.Masks[id]);
    }
}
