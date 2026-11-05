using Xunit;

namespace Compositor.Core.Tests;

public class LayerGroupTests
{
    private static Surface Solid(int width, int height, byte r, byte g, byte b)
    {
        var surface = new Surface(width, height);
        for (int row = 0; row < height; row++)
        {
            for (int column = 0; column < width; column++)
            {
                int index = row * surface.Stride + column * 4;
                surface.Pixels[index] = r;
                surface.Pixels[index + 1] = g;
                surface.Pixels[index + 2] = b;
                surface.Pixels[index + 3] = 255;
            }
        }
        return surface;
    }

    private static (Guid Background, Guid Red, Guid Blue) ThreeLayers(EditorDocument document)
    {
        var manifest = document.Snapshot.Manifest;
        var background = manifest.Layers[0].Id;
        document.ImportImage(Solid(8, 8, 255, 0, 0), "Red");
        document.ImportImage(Solid(8, 8, 0, 0, 255), "Blue");
        manifest = document.Snapshot.Manifest;
        return (background,
            manifest.Layers.Single(layer => layer.Name == "Red").Id,
            manifest.Layers.Single(layer => layer.Name == "Blue").Id);
    }

    [Fact]
    public void GroupLayersWrapsTheLayerAtItsBranchPoint()
    {
        var document = EditorDocument.Create(16, 16);
        var (background, red, blue) = ThreeLayers(document);

        var folder = document.GroupLayers(red);

        Assert.NotNull(folder);
        var manifest = document.Snapshot.Manifest;
        Assert.True(folder!.IsGroupOrFalse);
        Assert.Equal("Folder 1", folder.Name);
        Assert.Null(folder.ParentId);
        Assert.Equal(folder.Id, manifest.ActiveLayerId);
        Assert.Equal(folder.Id, manifest.Layers.Single(layer => layer.Id == red).ParentId);

        // Tree order: background, folder (then its child Red), then Blue on top.
        Assert.Equal(new[] { background, folder.Id, red, blue }, DrawOrder.Resolve(manifest.Layers).Order.ToList());
        Assert.Equal(new[] { background, red, blue }, DrawOrder.Resolve(manifest.Layers).Drawn.ToList());

        Assert.True(document.Undo());
        var restored = document.Snapshot.Manifest;
        Assert.Null(restored.Layers.Single(layer => layer.Id == red).ParentId);
        Assert.DoesNotContain(restored.Layers, layer => layer.IsGroupOrFalse);
        Assert.Equal(new[] { background, red, blue }, DrawOrder.Resolve(restored.Layers).Order.ToList());

        Assert.True(document.Redo());
        Assert.Equal(folder.Id, document.Snapshot.Manifest.Layers.Single(layer => layer.Id == red).ParentId);
    }

    [Fact]
    public void UngroupLiftsChildrenIntoTheFolderPlace()
    {
        var document = EditorDocument.Create(16, 16);
        var (background, red, blue) = ThreeLayers(document);
        var folder = document.GroupLayers(red)!;

        Assert.True(document.Ungroup(folder.Id));

        var manifest = document.Snapshot.Manifest;
        Assert.DoesNotContain(manifest.Layers, layer => layer.Id == folder.Id);
        Assert.Null(manifest.Layers.Single(layer => layer.Id == red).ParentId);
        Assert.Equal(red, manifest.ActiveLayerId);
        // The flat list is exactly what it was before grouping.
        Assert.Equal(new[] { background, red, blue }, manifest.Layers.Select(layer => layer.Id).ToList());

        Assert.True(document.Undo());
        Assert.Contains(document.Snapshot.Manifest.Layers, layer => layer.Id == folder.Id);
    }

    [Fact]
    public void UngroupOnAPixelLayerDoesNothing()
    {
        var document = EditorDocument.Create(16, 16);
        var (_, red, _) = ThreeLayers(document);

        Assert.False(document.Ungroup(red));
        Assert.False(document.Ungroup(Guid.NewGuid()));
    }

    [Fact]
    public void GroupLayersWithTwoLayersKeepsTheirDrawOrder()
    {
        var document = EditorDocument.Create(16, 16);
        var (background, red, blue) = ThreeLayers(document);

        var folder = document.GroupLayers(new[] { red, blue });

        Assert.NotNull(folder);
        var manifest = document.Snapshot.Manifest;
        Assert.Null(folder!.ParentId); // both live at the root: the folder does too
        Assert.Equal(folder.Id, manifest.Layers.Single(layer => layer.Id == red).ParentId);
        Assert.Equal(folder.Id, manifest.Layers.Single(layer => layer.Id == blue).ParentId);
        Assert.Equal(new[] { background, folder.Id, red, blue }, DrawOrder.Resolve(manifest.Layers).Order.ToList());
        Assert.Equal(new[] { background, red, blue }, DrawOrder.Resolve(manifest.Layers).Drawn.ToList());

        Assert.True(document.Undo());
        var restored = document.Snapshot.Manifest;
        Assert.Equal(new[] { background, red, blue }, restored.Layers.Select(layer => layer.Id).ToList());
        Assert.True(restored.Layers.All(layer => layer.ParentId is null));
    }

    [Fact]
    public void GroupingASelectedFolderCarriesItsSubtree()
    {
        var document = EditorDocument.Create(16, 16);
        var (background, red, blue) = ThreeLayers(document);
        var folder = document.GroupLayers(red)!; // Folder 1 holds Red

        // Selecting the folder and its child: only the folder is a root, the child rides along.
        var outer = document.GroupLayers(new[] { folder.Id, red });

        Assert.NotNull(outer);
        var manifest = document.Snapshot.Manifest;
        Assert.Equal("Folder 2", outer!.Name);
        Assert.Equal(outer.Id, manifest.Layers.Single(layer => layer.Id == folder.Id).ParentId);
        // The child keeps pointing at its folder, which itself moved inside.
        Assert.Equal(folder.Id, manifest.Layers.Single(layer => layer.Id == red).ParentId);
        Assert.Equal(new[] { background, outer.Id, folder.Id, red, blue }, DrawOrder.Resolve(manifest.Layers).Order.ToList());
    }

    [Fact]
    public void FolderNamesSkipTakenNumbers()
    {
        var document = EditorDocument.Create(16, 16);
        var (_, red, _) = ThreeLayers(document);

        Assert.True(document.AddFolder());
        Assert.Equal("Folder 1", document.Snapshot.Manifest.Layers.Single(layer => layer.IsGroupOrFalse).Name);

        var second = document.GroupLayers(red);
        Assert.NotNull(second);
        Assert.Equal("Folder 2", second!.Name);
    }

    [Fact]
    public void GroupedLayerRendersWithTheFoldersOpacity()
    {
        var document = EditorDocument.Create(8, 8);
        var (_, red, _) = ThreeLayers(document);
        var folder = document.GroupLayers(red)!;
        Assert.True(document.SetOpacity(folder.Id, 0.5));

        var byId = document.Snapshot.Manifest.Layers.ToDictionary(layer => layer.Id, layer => layer);
        // The folder at 50% multiplies into everything inside: Red draws at 50%.
        Assert.Equal(0.5, EffectiveOpacity.Resolve(red, byId), 9);
        // The background stays at 100%.
        Assert.Equal(1, EffectiveOpacity.Resolve(document.Snapshot.Manifest.Layers[0].Id, byId), 9);
    }

    [Fact]
    public void PlaceLayerPutsItAtTheRowADropWouldUse()
    {
        var document = EditorDocument.Create(16, 16);
        var (background, red, blue) = ThreeLayers(document);

        // The manifest is bottom-to-top, so dropping the background "above Blue" is the top of the stack.
        Assert.True(document.PlaceLayer(background, parent: null, above: blue));
        var manifest = document.Snapshot.Manifest;
        Assert.Equal(new[] { red, blue, background }, manifest.Layers.Select(layer => layer.Id));
        Assert.Equal(background, manifest.ActiveLayerId);

        Assert.True(document.Undo());
        Assert.Equal(new[] { background, red, blue }, document.Snapshot.Manifest.Layers.Select(layer => layer.Id));
        Assert.True(document.Redo());
        Assert.Equal(new[] { red, blue, background }, document.Snapshot.Manifest.Layers.Select(layer => layer.Id));
    }

    [Fact]
    public void PlaceLayerIntoAFolderReparentsItAndPlacesItAboveTheSibling()
    {
        var document = EditorDocument.Create(16, 16);
        var (background, red, blue) = ThreeLayers(document);
        var folder = document.GroupLayers(red)!;

        Assert.True(document.CanPlaceLayer(blue, folder.Id));
        Assert.True(document.PlaceLayer(blue, folder.Id, above: red));

        var manifest = document.Snapshot.Manifest;
        Assert.Equal(folder.Id, manifest.Layers.Single(layer => layer.Id == blue).ParentId);
        Assert.Equal(new[] { background, folder.Id, red, blue }, DrawOrder.Resolve(manifest.Layers).Order.ToList());
    }

    [Fact]
    public void PlaceLayerAtTheBottomStartsTheStackUnderEverything()
    {
        var document = EditorDocument.Create(16, 16);
        var (background, red, blue) = ThreeLayers(document);
        var folder = document.GroupLayers(red)!;

        Assert.True(document.PlaceLayer(blue, folder.Id, atBottom: true));

        var manifest = document.Snapshot.Manifest;
        Assert.Equal(folder.Id, manifest.Layers.Single(layer => layer.Id == blue).ParentId);
        // Among the folder's children the dropped layer is the lowest, so Red still draws on top of it.
        Assert.Equal(new[] { background, folder.Id, blue, red }, DrawOrder.Resolve(manifest.Layers).Order.ToList());
    }

    [Fact]
    public void PlaceLayerRefusesAFolderInsideWhatItContainsWithoutLeavingAStep()
    {
        var document = EditorDocument.Create(16, 16);
        var (background, red, _) = ThreeLayers(document);
        var folder = document.GroupLayers(red)!;

        Assert.False(document.CanPlaceLayer(folder.Id, red));   // its own child
        Assert.False(document.CanPlaceLayer(folder.Id, folder.Id)); // itself
        Assert.False(document.CanPlaceLayer(background, red)); // a layer, not a folder
        Assert.False(document.PlaceLayer(folder.Id, red));
        Assert.False(document.PlaceLayer(folder.Id, folder.Id));

        // The refusals pushed no memento: one Undo is still the grouping itself, not a no-op step.
        Assert.True(document.Undo());
        Assert.DoesNotContain(document.Snapshot.Manifest.Layers, layer => layer.IsGroupOrFalse);
    }

    [Fact]
    public void PlaceLayerRefusesARowThatIsNotASiblingOfTheTargetParent()
    {
        var document = EditorDocument.Create(16, 16);
        var (background, red, blue) = ThreeLayers(document);
        var folder = document.GroupLayers(red)!;

        // Red is inside the folder, so it is not a row the top level can be dropped above.
        Assert.False(document.PlaceLayer(blue, parent: null, above: red));
        Assert.False(document.PlaceLayer(background, parent: null, above: background));

        var manifest = document.Snapshot.Manifest;
        Assert.Equal(new[] { background, folder.Id, red, blue }, DrawOrder.Resolve(manifest.Layers).Order.ToList());
        Assert.True(document.Undo());
        Assert.DoesNotContain(document.Snapshot.Manifest.Layers, layer => layer.IsGroupOrFalse);
    }

    [Fact]
    public void PlaceLayersMovesTheBatchAsOneStep()
    {
        var document = EditorDocument.Create(16, 16);
        var (background, red, blue) = ThreeLayers(document);

        // The two go in one after the other above Blue, so the first ends up the topmost of the pair.
        Assert.True(document.PlaceLayers(new[] { background, red }, parent: null, above: blue));
        var manifest = document.Snapshot.Manifest;
        Assert.Equal(new[] { blue, red, background }, manifest.Layers.Select(layer => layer.Id));
        Assert.Equal(red, manifest.ActiveLayerId);

        // One step, as the Mac's beginEdit around the whole drop loop: one Undo puts both back.
        Assert.True(document.Undo());
        Assert.Equal(new[] { background, red, blue }, document.Snapshot.Manifest.Layers.Select(layer => layer.Id));
        Assert.True(document.Redo());
        Assert.Equal(new[] { blue, red, background }, document.Snapshot.Manifest.Layers.Select(layer => layer.Id));
    }

    [Fact]
    public void ACopyDropPutsTheCopyAtTheRowAndKeepsTheOriginal()
    {
        var document = EditorDocument.Create(16, 16);
        var (background, red, blue) = ThreeLayers(document);

        Assert.True(document.DuplicateLayersAt([background], parent: null, above: blue, atBottom: false, out var copies));
        var manifest = document.Snapshot.Manifest;
        Assert.Equal(4, manifest.Layers.Count);
        Assert.Equal(["Background copy"], manifest.Layers.Where(layer => copies.Contains(layer.Id)).Select(layer => layer.Name));
        // The original is where it was: bottom of the stack, with the copy dropped on top of Blue.
        Assert.Equal(new[] { background, red, blue }, manifest.Layers.Take(3).Select(layer => layer.Id));
        Assert.Equal(copies[0], manifest.ActiveLayerId);

        // Duplicating and moving the copy are one step, as they are on the Mac.
        Assert.True(document.Undo());
        Assert.Equal(3, document.Snapshot.Manifest.Layers.Count);
    }

    [Fact]
    public void ACopyDropOfAFolderCarriesItsChildrenIntoTheCopy()
    {
        var document = EditorDocument.Create(16, 16);
        var (background, red, blue) = ThreeLayers(document);
        var folder = document.GroupLayers(red)!;

        Assert.True(document.DuplicateLayersAt([folder.Id], parent: null, above: folder.Id, atBottom: false, out var copies));
        var manifest = document.Snapshot.Manifest;
        var copy = Assert.Single(manifest.Layers, layer => copies.Contains(layer.Id) && layer.IsGroupOrFalse);
        Assert.Equal("Folder 1 copy", copy.Name);
        // Only the root gains " copy"; what is inside it comes along with its own name and its new parent.
        var carried = Assert.Single(manifest.Layers, layer => layer.ParentId == copy.Id);
        Assert.Equal("Red", carried.Name);
        Assert.Equal(folder.Id, manifest.Layers.Single(layer => layer.Id == red).ParentId);
        // Dropped above the original folder: in the top level's own order the copy sits right after it.
        Assert.Equal(new[] { background, folder.Id, copy.Id, blue },
            manifest.Layers.Where(layer => layer.ParentId is null).Select(layer => layer.Id));
    }

    [Fact]
    public void ARefusedCopyDropLeavesNoStepBehindIt()
    {
        var document = EditorDocument.Create(16, 16);
        var (_, red, _) = ThreeLayers(document);
        var folder = document.GroupLayers(red)!;

        // A folder cannot be copied into itself, so nothing is duplicated either.
        Assert.False(document.DuplicateLayersAt([folder.Id], folder.Id, above: null, atBottom: false, out var copies));
        Assert.Empty(copies);
        // Three layers plus the folder, and no copy of either.
        Assert.Equal(4, document.Snapshot.Manifest.Layers.Count);
        Assert.True(document.Undo());
        Assert.DoesNotContain(document.Snapshot.Manifest.Layers, layer => layer.IsGroupOrFalse);
    }

    [Fact]
    public void PlacingALayerIntoTheMiddleOfAClippingGroupMakesItClipToo()
    {
        var document = EditorDocument.Create(16, 16);
        var (background, red, blue) = ThreeLayers(document);
        Assert.True(document.ToggleClippingMask(blue));
        Assert.Equal(red, document.Snapshot.Manifest.Layers.Single(layer => layer.Id == blue).MaskSourceId);

        // Dropped between the base and what clips to it, the background joins the group.
        Assert.True(document.PlaceLayer(background, parent: null, above: red));

        var manifest = document.Snapshot.Manifest;
        Assert.Equal(new[] { red, background, blue }, manifest.Layers.Select(layer => layer.Id));
        Assert.Equal(red, manifest.Layers.Single(layer => layer.Id == background).MaskSourceId);
    }

    [Fact]
    public void MovingTheClipBaseIntoAFolderReleasesWhatClippedToIt()
    {
        var document = EditorDocument.Create(16, 16);
        var (background, red, blue) = ThreeLayers(document);
        Assert.True(document.ToggleClippingMask(blue));
        var folder = document.GroupLayers(background)!;

        // Carry the base into the folder: the stack Blue sits in no longer holds it, so Blue stops clipping.
        Assert.True(document.PlaceLayer(red, folder.Id, atBottom: true));

        var manifest = document.Snapshot.Manifest;
        Assert.Equal(folder.Id, manifest.Layers.Single(layer => layer.Id == red).ParentId);
        Assert.Null(manifest.Layers.Single(layer => layer.Id == blue).MaskSourceId);
        Assert.True(document.Undo());
        Assert.Equal(red, document.Snapshot.Manifest.Layers.Single(layer => layer.Id == blue).MaskSourceId);
    }
}
