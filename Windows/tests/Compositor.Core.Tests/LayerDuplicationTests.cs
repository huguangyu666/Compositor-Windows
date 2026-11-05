using Xunit;

namespace Compositor.Core.Tests;

public class LayerDuplicationTests
{
    [Fact]
    public void SelectedFolderPrunesSelectedDescendantsAndRemapsItsLiveMasks()
    {
        var folder = Layer("Folder", group: true); var basis = Layer("Base", folder.Id); var clipped = Layer("Clipped", folder.Id);
        clipped.MaskSourceId = basis.Id;
        var doc = Scene(folder, basis, clipped); doc.Snapshot.Manifest.ActiveLayerId = folder.Id;
        var copies = doc.DuplicateLayers([folder.Id, clipped.Id]);
        Assert.Equal(3, copies.Count);
        var root = copies.Single(l => l.IsGroupOrFalse);
        Assert.Equal("Folder copy", root.Name);
        Assert.Equal(new[] { "Base", "Clipped" }, copies.Where(l => !l.IsGroupOrFalse).Select(l => l.Name));
        Assert.Equal(copies.Single(l => l.Name == "Base").Id, copies.Single(l => l.Name == "Clipped").MaskSourceId);
        Assert.All(copies.Where(l => !l.IsGroupOrFalse), l => Assert.Equal(root.Id, l.ParentId));
        Assert.Equal(root.Id, doc.Snapshot.Manifest.ActiveLayerId); ProjectValidator.Validate(doc.Snapshot.Manifest);
    }

    [Fact]
    public void SeveralCopiesStackTogetherInPanelOrderAboveHighestOriginal()
    {
        var low = Layer("Low"); var middle = Layer("Middle"); var high = Layer("High");
        var doc = Scene(low, middle, high); doc.Snapshot.Manifest.ActiveLayerId = low.Id;
        var copies = doc.DuplicateLayers([high.Id, low.Id]);
        Assert.Equal(new[] { "Low", "Middle", "High", "Low copy", "High copy" }, doc.Snapshot.Manifest.Layers.Select(l => l.Name));
        Assert.Equal(copies.Single(l => l.Name == "Low copy").Id, doc.Snapshot.Manifest.ActiveLayerId);
        Assert.True(doc.Undo()); Assert.Equal(3, doc.LayerCount); Assert.False(doc.CanUndo);
        Assert.True(doc.Redo()); Assert.Equal(5, doc.LayerCount);
    }

    [Fact]
    public void CrossFolderCopiesUseHighestOriginalParentAndKeepDescendantsInsideCopy()
    {
        // Top-first visits a folder BEFORE its children; reversing the draw order would choose the wrong parent.
        var low = Layer("Low"); var topFolder = Layer("Top folder", group: true); var child = Layer("Child", topFolder.Id);
        var doc = Scene(low, topFolder, child);
        var copies = doc.DuplicateLayers([low.Id, child.Id]);
        Assert.All(copies, l => Assert.Equal(topFolder.Id, l.ParentId));
        Assert.Equal(new[] { "Child", "Low copy", "Child copy" }, doc.Snapshot.Manifest.Layers.Where(l => l.ParentId == topFolder.Id).Select(l => l.Name));
        ProjectValidator.Validate(doc.Snapshot.Manifest);
    }

    [Fact]
    public void SingleCopyKeepsAnExternalLiveMaskSource()
    {
        var source = Layer("Source"); var target = Layer("Target"); target.MaskSourceId = source.Id;
        var doc = Scene(source, target); var copy = Assert.Single(doc.DuplicateLayers([target.Id]));
        Assert.Equal(source.Id, copy.MaskSourceId); Assert.Equal("Target copy", copy.Name);
    }

    [Fact]
    public void MovingSeveralCopiesReleasesDetachedClippingAsInTheSource()
    {
        var source = Layer("Source"); var target = Layer("Target"); target.MaskSourceId = source.Id;
        var high = Layer("High"); var doc = Scene(source, target, high); var copies = doc.DuplicateLayers([target.Id, high.Id]);
        Assert.Null(copies.Single(l => l.Name == "Target copy").MaskSourceId);
        Assert.Equal(source.Id, doc.Snapshot.Manifest.Layers.Single(l => l.Id == target.Id).MaskSourceId);
    }

    [Fact]
    public void CopiesInsertedInsideAClippingStackAdoptTheExistingBase()
    {
        var source = Layer("Source"); var target = Layer("Target"); target.MaskSourceId = source.Id;
        var doc = Scene(source, target); var copies = doc.DuplicateLayers([source.Id, target.Id]);
        Assert.All(copies, l => Assert.Equal(source.Id, l.MaskSourceId));
    }

    [Fact]
    public void RejectedCopyPreservesLiveMetadataActiveLayerAndRedo()
    {
        var doc = EditorDocument.Create(16, 16); var id = doc.Snapshot.Manifest.Layers[0].Id;
        doc.RenameLayer(id, "Renamed"); Assert.True(doc.Undo());
        doc.Snapshot.Manifest.Layers[0].Transform.Size = new(0, 1);
        var before = ManifestJson.Serialize(doc.Snapshot.Manifest); var snapshot = doc.Snapshot;
        Assert.Empty(doc.DuplicateLayers([id]));
        Assert.Same(snapshot, doc.Snapshot); Assert.Equal(before, ManifestJson.Serialize(doc.Snapshot.Manifest));
        Assert.True(doc.CanRedo); Assert.False(doc.CanUndo); Assert.False(doc.IsDirty);
    }

    [Fact]
    public void LayerLimitSkipsAnOversizedFolderAndStillCopiesTheRemainingRoot()
    {
        var folder = Layer("Folder", group: true); var child = Layer("Child", folder.Id); var leaf = Layer("Leaf");
        var doc = Scene(new[] { folder, child, leaf }.Concat(Enumerable.Range(0, Limits.MaxLayers - 4).Select(i => Layer("Empty"))).ToArray());
        var copy = Assert.Single(doc.DuplicateLayers([folder.Id, leaf.Id]));
        Assert.Equal("Leaf copy", copy.Name); Assert.Equal(Limits.MaxLayers, doc.LayerCount);
    }

    private static ProjectLayerRecord Layer(string name, Guid? parent = null, bool group = false) => new()
    { Id = Guid.NewGuid(), Name = name, ParentId = parent, IsGroup = group, IsVisible = true, Transform = new() { Size = new(16, 16) } };
    [Fact]
    public void LayerViaCopyWithoutSelectionIncludesFolders()
    {
        var folder = Layer("Folder", group: true); var child = Layer("Child", folder.Id); var doc = Scene(folder, child);
        var copy = Assert.IsType<ProjectLayerRecord>(doc.LayerViaCopy(folder.Id));
        Assert.Equal("Folder copy", copy.Name); Assert.True(copy.IsGroupOrFalse); Assert.Equal(4, doc.LayerCount);
        Assert.True(doc.Undo()); Assert.False(doc.CanUndo);
    }
    private static EditorDocument Scene(params ProjectLayerRecord[] layers)
    {
        var manifest = EditorDocument.Create(16, 16).Snapshot.Manifest; manifest.Layers = layers.ToList(); manifest.ActiveLayerId = layers[0].Id;
        return new(new() { Manifest = manifest, Images = new Dictionary<Guid, Surface>(), Masks = new Dictionary<Guid, MaskSurface>() });
    }

    [Fact]
    public void DuplicateKeepsEditableTextWithoutSharingItsMutableStyle()
    {
        var document = EditorDocument.Create(16, 16);
        var layer = document.Snapshot.Manifest.Layers[0];
        layer.Text = new() { Content = "Editable", FontName = "Segoe UI", FontSize = 12, Red = .7, Green = .2, Blue = .1 };
        var originalPixels = document.Snapshot.Images[layer.Id];
        var copy = Assert.Single(document.DuplicateLayers([layer.Id]));
        Assert.Equal(layer.Text, copy.Text); Assert.NotSame(layer.Text, copy.Text);
        copy.Text!.Content = "Changed";
        Assert.Equal("Editable", layer.Text.Content); Assert.Same(originalPixels, document.Snapshot.Images[copy.Id]);
    }

    [Fact]
    public void DuplicateRetainsShapeEffectsAndTheSameRenderedPixels()
    {
        var document = EditorDocument.Create(32, 32); var layer = document.Snapshot.Manifest.Layers[0];
        layer.Shape = new ShapeStyle { Kind = ShapeKind.Rectangle, Red = 1, CornerRadius = 4 }.ToElement();
        layer.Effects = new LayerEffects { Stroke = new() { Size = 3, Red = 1, Opacity = 1 } }.Serialize();
        var before = new CanvasCompositor(document.Snapshot).Render().Pixels.ToArray();
        var copy = Assert.Single(document.DuplicateLayers([layer.Id]));
        Assert.True(System.Text.Json.JsonElement.DeepEquals(layer.Shape!.Value, copy.Shape!.Value));
        Assert.True(System.Text.Json.JsonElement.DeepEquals(layer.Effects!.Value, copy.Effects!.Value));
        document.Snapshot.Manifest.Layers.Single(l => l.Id == layer.Id).IsVisible = false;
        Assert.Equal(before, new CanvasCompositor(document.Snapshot).Render().Pixels);
        ProjectValidator.Validate(document.Snapshot.Manifest);
    }

    [Fact]
    public void DuplicateDetachesMaskPlacementAndSharesMaskPixels()
    {
        var document = EditorDocument.Create(16, 16); var layer = document.Snapshot.Manifest.Layers[0];
        document.AddRasterMask(layer.Id, new MaskSurface(16, 16));
        layer.MaskPlacement = new() { Origin = new(4, 5), Size = new(12, 13), Rotation = 17 };
        var copy = Assert.Single(document.DuplicateLayers([layer.Id]));
        Assert.Equal(layer.MaskPlacement, copy.MaskPlacement); Assert.NotSame(layer.MaskPlacement, copy.MaskPlacement);
        Assert.Same(document.Snapshot.Masks[layer.Id], document.Snapshot.Masks[copy.Id]);
        copy.MaskPlacement!.Origin = new(9, 10); Assert.Equal(new PointD(4, 5), layer.MaskPlacement.Origin);
    }
}
