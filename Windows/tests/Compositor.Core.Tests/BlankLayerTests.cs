using Xunit;

namespace Compositor.Core.Tests;

public class BlankLayerTests
{
    [Fact]
    public void NewBlankLayerSkipsTakenNamesAndEntersTopOfSelectedFolderWithoutPixels()
    {
        var scene = EditorDocument.Create(16, 16); scene.Snapshot.Manifest.Layers[0].Name = "Layer 1";
        scene.AddFolder(); var folder = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        var child = scene.ImportImage(new Surface(4, 4), "Child"); child.ParentId = folder;
        scene.AddFolder(); var nested = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        var grandchild = scene.ImportImage(new Surface(4, 4), "Grandchild"); grandchild.ParentId = nested;
        var outside = scene.ImportImage(new Surface(4, 4), "Outside");
        scene.SetActiveLayer(folder); scene = new(scene.Snapshot);
        var images = scene.Snapshot.Images; var layer = scene.AddBlankLayer()!;
        Assert.Equal("Layer 2", layer.Name); Assert.Equal(folder, layer.ParentId);
        Assert.Equal(new SizeD(16, 16), layer.Transform.Size); Assert.Equal(PointD.Zero, layer.Transform.Origin);
        Assert.Null(layer.ImageFile); Assert.Same(images, scene.Snapshot.Images);
        var order = scene.Snapshot.Manifest.Layers.Select(item => item.Id).ToList();
        Assert.Equal(order.IndexOf(grandchild.Id) + 1, order.IndexOf(layer.Id));
        Assert.Equal(order.IndexOf(layer.Id) + 1, order.IndexOf(outside.Id));
        Assert.Equal(layer.Id, scene.Snapshot.Manifest.ActiveLayerId); ProjectValidator.Validate(scene.Snapshot.Manifest);
        Assert.True(scene.Undo()); Assert.False(scene.CanUndo); Assert.Equal(folder, scene.Snapshot.Manifest.ActiveLayerId);
        Assert.True(scene.Redo()); Assert.Equal(layer.Id, scene.Snapshot.Manifest.ActiveLayerId);
    }

    private static EditorDocument BlankScene()
    {
        var scene = EditorDocument.Create(16, 16);
        var layer = new ProjectLayerRecord { Id = Guid.NewGuid(), Name = "Layer 1", IsVisible = true,
            Transform = new() { Size = new(16, 16) } };
        scene.Snapshot.Manifest.Layers.Add(layer); scene.Snapshot.Manifest.ActiveLayerId = layer.Id;
        return new(scene.Snapshot);
    }

    [Fact]
    public async Task FirstBrushOnBlankCreatesSavedPixelsAndOneUndoRestoresNoAsset()
    {
        var scene = BlankScene(); var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        var key = new object(); var selection = Selection.Rectangle(16, 16, 4, 4, 8, 8); scene.SetSelection(selection);
        Assert.True(scene.BeginStroke(key, id, new(3, 1, 1, 255, 0, 0), new(8, 8)));
        scene.ContinueStroke(key, new(10, 8)); scene.EndStroke(key);
        Assert.True(scene.IsDirty); Assert.Same(selection, scene.ActiveSelection);
        Assert.Equal(ProjectValidator.Uuid(id) + ".png", scene.Snapshot.Manifest.Layers.Single(layer => layer.Id == id).ImageFile);
        var pixels = scene.Snapshot.Images[id].Pixels.ToArray(); Assert.Contains(pixels, value => value != 0);
        string path = Path.Combine(Path.GetTempPath(), "compositor-blank-" + Guid.NewGuid().ToString("N") + ".comp");
        try
        {
            await ProjectStore.SaveAsync(scene.Snapshot, path);
            var restored = await ProjectStore.LoadAsync(path); Assert.Equal(pixels, restored.Images[id].Pixels);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
        Assert.True(scene.Undo()); Assert.False(scene.CanUndo); Assert.False(scene.Snapshot.Images.ContainsKey(id));
        Assert.Null(scene.Snapshot.Manifest.Layers.Single(layer => layer.Id == id).ImageFile);
        Assert.True(scene.Redo()); Assert.Equal(pixels, scene.Snapshot.Images[id].Pixels);
    }

    [Theory]
    [InlineData("fill")]
    [InlineData("clone")]
    [InlineData("gradient")]
    public void FirstColorEditOnBlankCreatesAnUndoableRaster(string operation)
    {
        var scene = BlankScene(); var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        scene.SetSelection(Selection.Rectangle(16, 16, 4, 4, 8, 8));
        switch (operation)
        {
            case "fill": Assert.True(scene.FillPixels(id, 255, 0, 0)); break;
            case "clone":
                Assert.True(scene.BeginCloneStroke(id, new(8, 8), new(8, 8), 3, 1, true, new object()));
                scene.EndCloneStroke(); break;
            case "gradient":
                var draft = scene.BeginGradient(id)!;
                var frame = draft.Preview(new(0, 0), new(16, 0), new(GradientShape.Linear,
                    GradientStyle.ForegroundToBackground, new(255, 0, 0), new(0, 0, 255)))!;
                Assert.False(scene.Snapshot.Images.ContainsKey(id)); Assert.False(scene.CanUndo);
                Assert.True(scene.CommitGradient(frame)); break;
        }
        var image = scene.Snapshot.Images[id]; Assert.Equal(0, image.Pixels[3]);
        Assert.Contains(image.Pixels, value => value != 0);
        Assert.NotNull(scene.Snapshot.Manifest.Layers.Single(layer => layer.Id == id).ImageFile);
        Assert.True(scene.IsDirty); Assert.True(scene.Undo()); Assert.False(scene.CanUndo);
        Assert.False(scene.Snapshot.Images.ContainsKey(id));
        Assert.Null(scene.Snapshot.Manifest.Layers.Single(layer => layer.Id == id).ImageFile);
    }

    [Fact]
    public async Task EmptyBlankSavesWithoutAnAssetAndEraserOrEmptySelectionLeavesItEmpty()
    {
        var scene = BlankScene(); var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        Assert.False(scene.BeginStroke(new object(), id, new(3, 1, 1, 255, 0, 0), new(8, 8), erase: true));
        scene.SetSelection(new(new MaskSurface(16, 16), new(0, 0, 0, 0)));
        Assert.False(scene.FillPixels(id, 255, 0, 0)); Assert.False(scene.CanUndo); Assert.False(scene.IsDirty);
        string path = Path.Combine(Path.GetTempPath(), "compositor-blank-" + Guid.NewGuid().ToString("N") + ".comp");
        try
        {
            await ProjectStore.SaveAsync(scene.Snapshot, path); var loaded = await ProjectStore.LoadAsync(path);
            Assert.False(loaded.Images.ContainsKey(id));
            Assert.Null(loaded.Manifest.Layers.Single(layer => layer.Id == id).ImageFile);
            Assert.False(File.Exists(Path.Combine(path, "images", ProjectValidator.Uuid(id) + ".png")));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Fact]
    public void OversizedBlankCannotAllocateItsFirstRaster()
    {
        var scene = BlankScene(); var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        scene.Snapshot.Manifest.Layers.Single(layer => layer.Id == id).Transform.Size = new(30_001, 30_001);
        Assert.False(scene.BeginStroke(new object(), id, new(3, 1, 1, 255, 0, 0), new(8, 8)));
        Assert.False(scene.CanUndo); Assert.False(scene.IsDirty); Assert.False(scene.Snapshot.Images.ContainsKey(id));
    }
}
