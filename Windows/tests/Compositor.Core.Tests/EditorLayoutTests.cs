using System.Text.Json.Nodes;
using Xunit;

namespace Compositor.Core.Tests;

public sealed class EditorLayoutTests
{
    [Fact]
    public void GroupAliasesAndUngroupAreAtomicAndUndoable()
    {
        var doc = EditorDocument.Create(200, 100); var a = doc.Snapshot.Manifest.ActiveLayerId!.Value;
        string before = ManifestJson.Serialize(doc.Snapshot.Manifest);
        var names = EditorAutomation.ApplyBatch(doc, new JsonArray(
            new JsonObject { ["tool"] = "editor_layer_duplicate", ["arguments"] = new JsonObject { ["layerId"] = a.ToString() }, ["as"] = "copy" },
            new JsonObject { ["tool"] = "editor_layout", ["arguments"] = new JsonObject { ["operation"] = "group", ["layerIds"] = new JsonArray(a.ToString(), "$copy"), ["name"] = "Pair" }, ["as"] = "pair" }));
        var folder = doc.Snapshot.Manifest.Layers.Single(l => l.Id == names["pair"]);
        Assert.True(folder.IsGroupOrFalse); Assert.Equal("Pair", folder.Name);
        Assert.All(doc.Snapshot.Manifest.Layers.Where(l => l.Id != folder.Id), l => Assert.Equal(folder.Id, l.ParentId));
        Assert.True(doc.Undo()); Assert.False(doc.CanUndo); Assert.Equal(before, ManifestJson.Serialize(doc.Snapshot.Manifest));
        Assert.True(doc.Redo());
        EditorAutomation.Apply(doc, "editor_layout", new() { ["operation"] = "ungroup", ["layerIds"] = new JsonArray(folder.Id.ToString()) });
        Assert.Equal(2, doc.LayerCount); Assert.All(doc.Snapshot.Manifest.Layers, l => Assert.Null(l.ParentId));
        Assert.True(doc.Undo()); Assert.Equal(3, doc.LayerCount);
    }

    [Fact]
    public void AlignmentUsesRotatedBoundsAndPreservesIndependentMasks()
    {
        var doc = EditorDocument.Create(200, 100); var a = doc.Snapshot.Manifest.ActiveLayerId!.Value;
        var layer = doc.Snapshot.Manifest.Layers.Single();
        layer.Transform = new() { Origin = new(40, 20), Size = new(30, 10), Rotation = 90 };
        var image = doc.Snapshot.Images[a]; doc.AddRasterMask(a, new MaskSurface(200, 100));
        layer = doc.Snapshot.Manifest.Layers.Single(); layer.MaskLinked = false;
        var old = new LayerTransform { Origin = new(40, 20), Size = new(30, 10), Rotation = 90 };
        EditorAutomation.Apply(doc, "editor_layout", new() { ["operation"] = "align", ["layerIds"] = new JsonArray(a.ToString()), ["alignment"] = "left", ["relativeTo"] = "canvas" });
        layer = doc.Snapshot.Manifest.Layers.Single();
        Assert.Equal(0, Corners(layer.Transform).Min(p => p.X), 8); Assert.Equal(90, layer.Transform.Rotation);
        Assert.Equal(old, layer.MaskPlacement); Assert.Same(image, doc.Snapshot.Images[a]);
        Assert.True(doc.Undo()); Assert.Equal(old, doc.Snapshot.Manifest.Layers.Single().Transform);
    }

    [Fact]
    public void FolderAlignmentMovesHiddenDescendantsAndLinkedMasksExactlyOnce()
    {
        var doc = EditorDocument.Create(200, 100); var a = doc.Snapshot.Manifest.ActiveLayerId!.Value;
        doc.Snapshot.Manifest.Layers.Single().Transform = new() { Origin = new(20, 10), Size = new(20, 10) };
        var b = doc.ImportImage(new Surface(10, 10), "Hidden").Id;
        doc.Snapshot.Manifest.Layers.Single(l => l.Id == b).Transform.Origin = new(60, 20);
        doc.AddRasterMask(b, new MaskSurface(10, 10));
        var hidden = doc.Snapshot.Manifest.Layers.Single(l => l.Id == b); hidden.MaskPlacement = new() { Origin = new(61, 21), Size = new(10, 10) }; hidden.IsVisible = false;
        var group = doc.GroupLayers([a, b])!;
        EditorAutomation.Apply(doc, "editor_layout", new() { ["operation"] = "align", ["layerIds"] = new JsonArray(group.Id.ToString(), b.ToString()), ["alignment"] = "right", ["relativeTo"] = "canvas" });
        hidden = doc.Snapshot.Manifest.Layers.Single(l => l.Id == b);
        Assert.Equal(190, hidden.Transform.Origin.X); Assert.Equal(191, hidden.MaskPlacement!.Origin.X);
        Assert.Equal(150, doc.Snapshot.Manifest.Layers.Single(l => l.Id == a).Transform.Origin.X);
        Assert.True(doc.Undo()); Assert.Equal(60, doc.Snapshot.Manifest.Layers.Single(l => l.Id == b).Transform.Origin.X);
    }

    [Theory]
    [InlineData("centers", 42.5)]
    [InlineData("gaps", 40)]
    public void DistributionKeepsEndpointsAndHandlesUnequalWidths(string spacing, double middleX)
    {
        var doc = EditorDocument.Create(200, 100); var a = doc.Snapshot.Manifest.ActiveLayerId!.Value;
        doc.Snapshot.Manifest.Layers.Single().Transform = new() { Origin = new(10, 0), Size = new(20, 10) };
        var b = doc.ImportImage(new Surface(40, 10), "B").Id;
        var c = doc.ImportImage(new Surface(30, 10), "C").Id;
        doc.Snapshot.Manifest.Layers.Single(l => l.Id == b).Transform.Origin = new(35, 0);
        doc.Snapshot.Manifest.Layers.Single(l => l.Id == c).Transform.Origin = new(90, 0);
        // Centers: 20,62.5,105. Equal gaps: (110-20-40-30)/2 = 10.
        EditorAutomation.Apply(doc, "editor_layout", new() { ["operation"] = "distribute", ["layerIds"] = new JsonArray(c.ToString(), a.ToString(), b.ToString()), ["axis"] = "horizontal", ["spacing"] = spacing });
        Assert.Equal(middleX, doc.Snapshot.Manifest.Layers.Single(l => l.Id == b).Transform.Origin.X, 8);
        Assert.Equal(10, doc.Snapshot.Manifest.Layers.Single(l => l.Id == a).Transform.Origin.X);
        Assert.Equal(90, doc.Snapshot.Manifest.Layers.Single(l => l.Id == c).Transform.Origin.X);
    }

    [Fact]
    public void CropPreservesPixelsAndShiftsGuidesMasksAndSelectionInOneUndo()
    {
        var doc = EditorDocument.Create(100, 80); var a = doc.Snapshot.Manifest.ActiveLayerId!.Value;
        doc.AddRasterMask(a, new MaskSurface(100, 80));
        var layer = doc.Snapshot.Manifest.Layers.Single(); layer.MaskPlacement = new() { Origin = new(2, 3), Size = new(100, 80) };
        doc.Snapshot.Manifest.Guides = [new() { Id = Guid.NewGuid(), Axis = GuideAxis.Vertical, Position = 35 }];
        var selection = Selection.Rectangle(100, 80, 30, 25, 20, 20); doc.SetSelection(selection);
        var image = doc.Snapshot.Images[a]; string before = ManifestJson.Serialize(doc.Snapshot.Manifest);
        EditorAutomation.Apply(doc, "editor_layout", new() { ["operation"] = "crop", ["x"] = 20, ["y"] = 10, ["width"] = 50, ["height"] = 40 });
        Assert.Same(image, doc.Snapshot.Images[a]); Assert.Equal(50, doc.Snapshot.Manifest.Width); Assert.Equal(15, doc.Snapshot.Manifest.Guides.Single().Position);
        Assert.Equal(new PointD(-18, -7), doc.Snapshot.Manifest.Layers.Single().MaskPlacement!.Origin);
        Assert.Equal(255, doc.ActiveSelection!.CoverageAt(15, 20)); Assert.Equal(0, doc.ActiveSelection.CoverageAt(55, 20));
        Assert.Equal(50, doc.ActiveSelection.Coverage.Width); Assert.True(doc.Undo()); Assert.Same(selection, doc.ActiveSelection);
        Assert.Equal(before, ManifestJson.Serialize(doc.Snapshot.Manifest));
    }

    [Fact]
    public void InvalidLayoutRollsBackEarlierBatchCommandsAndKeepsRedo()
    {
        var doc = EditorDocument.Create(100, 80); var a = doc.Snapshot.Manifest.ActiveLayerId!.Value;
        doc.RenameLayer(a, "Redo"); doc.Undo(); string before = ManifestJson.Serialize(doc.Snapshot.Manifest);
        Assert.Throws<ArgumentException>(() => EditorAutomation.ApplyBatch(doc, new JsonArray(
            new JsonObject { ["tool"] = "editor_layer_rename", ["arguments"] = new JsonObject { ["layerId"] = a.ToString(), ["name"] = "Temporary" } },
            new JsonObject { ["tool"] = "editor_layout", ["arguments"] = new JsonObject { ["operation"] = "distribute", ["layerIds"] = new JsonArray(a.ToString()), ["axis"] = "horizontal" } })));
        Assert.Equal(before, ManifestJson.Serialize(doc.Snapshot.Manifest)); Assert.False(doc.CanUndo); Assert.True(doc.CanRedo);
    }

    private static PointD[] Corners(LayerTransform value) => [value.Point(new(0, 0)), value.Point(new(1, 0)), value.Point(new(1, 1)), value.Point(new(0, 1))];
}
