using Compositor.Core.Psd;
using Xunit;

namespace Compositor.Core.Tests;

public class FileImportTests
{
    [Fact]
    public void RasterUsesNaturalSizeAndFlooredCanvasCenter()
    {
        var owner = EditorDocument.Create(21, 13);
        using var batch = owner.BeginFileImport();
        var image = new Surface(5, 4); var layer = batch.AddImage(image, "Photo");
        Assert.Equal(new PointD(8, 4), layer.Transform.Origin);
        Assert.Equal(new SizeD(5, 4), layer.Transform.Size);
        Assert.Same(image, owner.Snapshot.Images[layer.Id]);
        Assert.Equal(layer.Id, owner.Snapshot.Manifest.ActiveLayerId);
    }

    [Fact]
    public void RasterDropPreservesOffCanvasPointAndFractionRounding()
    {
        var owner = EditorDocument.Create(20, 12);
        using var batch = owner.BeginFileImport(new(12.75, -4.2));
        Assert.Equal(new PointD(10, -7), batch.AddImage(new Surface(5, 4), "Drop").Transform.Origin);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RasterAppendsAtTopAndUsesSelectedFolderOrItsParent(bool selectFolder)
    {
        var owner = FolderScene(selectFolder, out var folder);
        using var batch = owner.BeginFileImport();
        var added = batch.AddImage(new Surface(2, 2), "Photo");
        Assert.Equal(folder, added.ParentId); Assert.Same(added, owner.Snapshot.Manifest.Layers[^1]);
        ProjectValidator.Validate(owner.Snapshot.Manifest);
    }

    [Fact]
    public void BatchPublishesEachSuccessButHasOneExactUndoAndRedo()
    {
        var owner = EditorDocument.Create(20, 12); var id = owner.Snapshot.Manifest.ActiveLayerId!.Value;
        var original = owner.Snapshot.Images[id]; var selection = Selection.Rectangle(20, 12, 3, 2, 4, 4);
        owner.SetSelection(selection); int changes = 0; owner.Changed += () => changes++;
        using (var batch = owner.BeginFileImport())
        {
            batch.AddImage(new Surface(3, 3), "Photo"); Assert.Equal(2, owner.LayerCount);
            batch.AddImage(new Surface(4, 4), "Photo"); Assert.Equal(3, owner.LayerCount);
        }
        Assert.Equal(2, changes); Assert.Equal(new[] { "Background", "Photo", "Photo" }, owner.Snapshot.Manifest.Layers.Select(l => l.Name));
        var imported = owner.Snapshot.Images.Values.ToArray();
        Assert.True(owner.Undo()); Assert.False(owner.CanUndo); Assert.Single(owner.Snapshot.Manifest.Layers);
        Assert.False(owner.IsDirty); Assert.Same(original, owner.Snapshot.Images[id]); Assert.Same(selection, owner.ActiveSelection);
        Assert.True(owner.Redo()); Assert.Equal(3, owner.LayerCount); Assert.False(owner.CanRedo);
        foreach (var image in imported) Assert.Contains(owner.Snapshot.Images.Values, candidate => ReferenceEquals(image, candidate));
    }

    [Fact]
    public void SeparateRequestsHaveSeparateUndoSteps()
    {
        var owner = EditorDocument.Create(20, 12);
        using (var batch = owner.BeginFileImport()) batch.AddImage(new Surface(2, 2), "First");
        using (var batch = owner.BeginFileImport()) batch.AddImage(new Surface(3, 3), "Second");
        Assert.True(owner.Undo()); Assert.Equal(2, owner.LayerCount); Assert.True(owner.CanUndo);
        Assert.True(owner.Undo()); Assert.Single(owner.Snapshot.Manifest.Layers); Assert.False(owner.CanUndo);
    }

    [Fact]
    public void EmptyAndRejectedRequestPreserveRedoAndSnapshot()
    {
        var owner = EditorDocument.Create(20, 12); owner.ImportImage(new Surface(2, 2), "Earlier"); owner.Undo();
        var snapshot = owner.Snapshot;
        using (owner.BeginFileImport()) { }
        using (var batch = owner.BeginFileImport()) Assert.Throws<ProjectFormatException>(() => batch.AddImage(new Surface(2, 2), ""));
        Assert.Same(snapshot, owner.Snapshot); Assert.False(owner.CanUndo); Assert.True(owner.CanRedo); Assert.False(owner.IsDirty);
    }

    [Fact]
    public void InvalidSecondFileLeavesFirstSuccessAndOnlyOneUndo()
    {
        var owner = EditorDocument.Create(20, 12);
        using (var batch = owner.BeginFileImport())
        {
            var first = batch.AddImage(new Surface(2, 2), "First"); var snapshot = owner.Snapshot;
            var broken = new PsdDocument { Width = 10, Height = 10, Layers = [new PsdLayer {
                Id = Guid.NewGuid(), ParentId = Guid.NewGuid(), Name = "Broken", Offset = new(0, 0), Pixels = new Surface(1, 1) }] };
            Assert.Throws<ProjectFormatException>(() => batch.AddPhotoshop(broken, "File"));
            Assert.Same(snapshot, owner.Snapshot); Assert.Equal(first.Id, owner.Snapshot.Manifest.ActiveLayerId);
        }
        Assert.True(owner.Undo()); Assert.False(owner.CanUndo); Assert.Single(owner.Snapshot.Manifest.Layers);
    }

    [Fact]
    public void PhotoshopWrapsTreeInNamedSelectedFolderAndRetainsMasks()
    {
        var owner = FolderScene(true, out var parent); var psd = PhotoshopScene();
        using var batch = owner.BeginFileImport(); var wrapper = batch.AddPhotoshop(psd, "Photoshop File");
        Assert.True(wrapper.IsGroupOrFalse); Assert.Equal("Photoshop File", wrapper.Name); Assert.Equal(parent, wrapper.ParentId);
        Assert.Equal(wrapper.Id, owner.Snapshot.Manifest.ActiveLayerId);
        var incoming = owner.Snapshot.Manifest.Layers.Where(l => l.Id != wrapper.Id && l.Id != parent && l.Name != "Background").ToArray();
        var folder = incoming.Single(l => l.IsGroupOrFalse); var child = incoming.Single(l => l.Name == "Child");
        Assert.Equal(wrapper.Id, folder.ParentId); Assert.Equal(folder.Id, child.ParentId); Assert.Equal(new PointD(2, 3), child.Transform.Origin);
        Assert.Same(psd.Layers[0].Pixels, owner.Snapshot.Images[child.Id]); Assert.Same(psd.Layers[0].Mask, owner.Snapshot.Masks[child.Id]);
        Assert.False(child.MaskLinked); Assert.Equal(new SizeD(40, 30), child.MaskPlacement!.Size);
        var hidden = incoming.Single(l => l.Name == "Hidden"); Assert.False(hidden.IsVisible); Assert.Equal(.6, hidden.Opacity);
        ProjectValidator.Validate(owner.Snapshot.Manifest);
    }

    [Fact]
    public void PhotoshopDropCentersAllNonGroupBoundsAndMovesCanvasMaskWithPixels()
    {
        var owner = EditorDocument.Create(100, 80); var psd = PhotoshopScene();
        using var batch = owner.BeginFileImport(new(50, 40)); batch.AddPhotoshop(psd, "File");
        // Both visible and hidden pixel layers contribute: bounds (2,3)-(18,11), center (10,7).
        var child = owner.Snapshot.Manifest.Layers.Single(l => l.Name == "Child");
        Assert.Equal(new PointD(42, 36), child.Transform.Origin); Assert.Equal(new PointD(40, 33), child.MaskPlacement!.Origin);
        Assert.Equal(new PointD(52, 40), owner.Snapshot.Manifest.Layers.Single(l => l.Name == "Hidden").Transform.Origin);
    }

    [Fact]
    public void PhotoshopIdsAndParentsAreRemappedOnEveryImport()
    {
        var owner = EditorDocument.Create(100, 80); var psd = PhotoshopScene();
        using var batch = owner.BeginFileImport(); var first = batch.AddPhotoshop(psd, "File"); var second = batch.AddPhotoshop(psd, "File");
        Assert.NotEqual(first.Id, second.Id); Assert.Equal(first.Id, second.ParentId);
        var ids = owner.Snapshot.Manifest.Layers.Select(l => l.Id).ToArray(); Assert.Equal(ids.Length, ids.Distinct().Count());
        foreach (var source in psd.Layers) Assert.DoesNotContain(source.Id, ids);
        ProjectValidator.Validate(owner.Snapshot.Manifest);
    }

    [Fact]
    public void CompositeFallbackImportsNamedRasterWithoutWrappingFolder()
    {
        var owner = EditorDocument.Create(100, 80); var image = new Surface(40, 30);
        var psd = new PsdDocument { Width = 40, Height = 30, IsCompositeFallback = true,
            Layers = [new PsdLayer { Id = Guid.NewGuid(), Name = "Background", Offset = new(0, 0), Pixels = image }] };
        using var batch = owner.BeginFileImport(); var added = batch.AddPhotoshop(psd, "File");
        Assert.False(added.IsGroupOrFalse); Assert.Equal("File", added.Name); Assert.Equal(new PointD(30, 25), added.Transform.Origin);
        Assert.Equal(2, owner.LayerCount); Assert.Same(image, owner.Snapshot.Images[added.Id]);
    }

    [Theory]
    [InlineData("active")]
    [InlineData("selection")]
    [InlineData("pixels")]
    [InlineData("snapshot")]
    public void BatchRejectsAnInterleavedEditBeforeChangingHistory(string change)
    {
        var owner = FolderScene(true, out _); using var batch = owner.BeginFileImport();
        var original = owner.Snapshot.Manifest.Layers[0].Id;
        if (change == "active") owner.SetActiveLayer(original);
        else if (change == "selection") owner.SetSelection(Selection.Rectangle(20, 12, 1, 1, 3, 3));
        else if (change == "pixels") owner.Snapshot.Images[original].MarkModified();
        else owner.ImportImage(new Surface(1, 1), "Other operation");
        var snapshot = owner.Snapshot; bool undo = owner.CanUndo;
        Assert.False(batch.IsCurrent); Assert.Throws<InvalidOperationException>(() => batch.AddImage(new Surface(1, 1), "Late"));
        Assert.Same(snapshot, owner.Snapshot); Assert.Equal(undo, owner.CanUndo);
    }

    [Fact]
    public void DisposedAndOverlappingBatchesCannotPublish()
    {
        var owner = EditorDocument.Create(20, 12); var batch = owner.BeginFileImport();
        Assert.Throws<InvalidOperationException>(() => owner.BeginFileImport());
        batch.Dispose(); Assert.False(batch.IsCurrent);
        Assert.Throws<InvalidOperationException>(() => batch.AddImage(new Surface(1, 1), "Late"));
        using var next = owner.BeginFileImport(); Assert.True(next.IsCurrent);
    }

    [Fact]
    public void LegacyPhotoshopVersionGateRejectsWithoutLosingRedo()
    {
        var owner = EditorDocument.Create(20, 12); owner.Snapshot.Manifest.Version = 1;
        owner.ImportImage(new Surface(1, 1), "Earlier"); owner.Undo(); var snapshot = owner.Snapshot;
        using var batch = owner.BeginFileImport();
        Assert.Throws<ProjectFormatException>(() => batch.AddPhotoshop(PhotoshopScene(), "File"));
        Assert.Same(snapshot, owner.Snapshot); Assert.True(owner.CanRedo); Assert.False(owner.CanUndo);
    }

    [Fact]
    public void LayerLimitRejectsBeforePublishingOrOpeningHistory()
    {
        var owner = EditorDocument.Create(1, 1);
        while (owner.LayerCount < Limits.MaxLayers)
            owner.Snapshot.Manifest.Layers.Add(new ProjectLayerRecord { Id = Guid.NewGuid(), Name = "Folder", IsGroup = true,
                IsVisible = true, Transform = new() { Size = new(1, 1) } });
        var snapshot = owner.Snapshot;
        using var batch = owner.BeginFileImport();
        Assert.Throws<ProjectFormatException>(() => batch.AddImage(new Surface(1, 1), "Overflow"));
        Assert.Same(snapshot, owner.Snapshot); Assert.False(owner.CanUndo); Assert.False(owner.IsDirty);
    }

    [Fact]
    public async Task ImportedTreePlacementAndMaskRoundTripThroughProjectStore()
    {
        var owner = EditorDocument.Create(100, 80);
        using (var batch = owner.BeginFileImport(new(50, 40))) batch.AddPhotoshop(PhotoshopScene(), "File");
        var path = Path.Combine(Path.GetTempPath(), "compositor-file-import-tests-" + Guid.NewGuid().ToString("N"), "roundtrip.comp");
        try
        {
            await ProjectStore.SaveAsync(owner.Snapshot, path); var loaded = await ProjectStore.LoadAsync(path);
            Assert.Equal(ManifestJson.Serialize(owner.Snapshot.Manifest), ManifestJson.Serialize(loaded.Manifest));
            foreach (var pair in owner.Snapshot.Images) Assert.Equal(pair.Value.Pixels, loaded.Images[pair.Key].Pixels);
            foreach (var pair in owner.Snapshot.Masks) Assert.Equal(pair.Value.Pixels, loaded.Masks[pair.Key].Pixels);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    private static EditorDocument FolderScene(bool selectFolder, out Guid folder)
    {
        var owner = EditorDocument.Create(20, 12); folder = Guid.NewGuid();
        owner.Snapshot.Manifest.Layers[0].ParentId = folder;
        owner.Snapshot.Manifest.Layers.Add(new ProjectLayerRecord { Id = folder, Name = "Folder", IsGroup = true,
            IsVisible = true, Transform = new() { Size = new(20, 12) } });
        if (selectFolder) owner.Snapshot.Manifest.ActiveLayerId = folder;
        return owner;
    }

    private static PsdDocument PhotoshopScene()
    {
        var folder = Guid.NewGuid(); var mask = new MaskSurface(40, 30); Array.Fill(mask.Pixels, (byte)255);
        return new PsdDocument { Width = 40, Height = 30, Layers = [
            new PsdLayer { Id = Guid.NewGuid(), ParentId = folder, Name = "Child", Offset = new(2, 3), Pixels = new Surface(4, 6), Mask = mask },
            new PsdLayer { Id = folder, Name = "Nested", IsGroup = true, Offset = new(0, 0) },
            new PsdLayer { Id = Guid.NewGuid(), Name = "Hidden", Offset = new(12, 7), Pixels = new Surface(6, 4), Hidden = true, Opacity = .6 }] };
    }
}
