using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;

namespace Compositor.Core.Tests;

public sealed class EditorBatchAllocationTests(ITestOutputHelper output)
{
    [Fact]
    public void MetadataBatchAvoidsPerCommandFullDocumentHistory()
    {
        (long Bytes, double Milliseconds, EditorDocument Document) Run(int count)
        {
            var doc = ManyLayers();
            var commands = new JsonArray(doc.Snapshot.Manifest.Layers.Take(count).Select(layer => (JsonNode)new JsonObject
            {
                ["tool"] = "editor_layer_opacity", ["arguments"] = new JsonObject { ["layerId"] = layer.Id.ToString(), ["opacity"] = .5 },
            }).ToArray());
            long before = GC.GetAllocatedBytesForCurrentThread(); var watch = Stopwatch.StartNew();
            EditorAutomation.ApplyBatch(doc, commands);
            return (GC.GetAllocatedBytesForCurrentThread() - before, watch.Elapsed.TotalMilliseconds, doc);
        }
        Run(1); Run(32); // Warm serializers and editing paths before measuring allocations.
        var single = Run(1); var batch = Run(32);
        output.WriteLine($"256 layers: 1 command {single.Bytes} B / {single.Milliseconds:F3} ms; 32 commands {batch.Bytes} B / {batch.Milliseconds:F3} ms.");
        Assert.True(batch.Bytes < single.Bytes * 3, $"Batch allocated {batch.Bytes} bytes versus {single.Bytes} for one command; intermediate undo captures must not scale with command count.");
        Assert.All(batch.Document.Snapshot.Manifest.Layers.Take(32), l => Assert.Equal(.5, l.Opacity));
        Assert.True(batch.Document.Undo()); Assert.False(batch.Document.CanUndo); Assert.False(batch.Document.IsDirty);
        Assert.All(batch.Document.Snapshot.Manifest.Layers, l => Assert.Equal(1, l.Opacity));
        Assert.True(batch.Document.Redo()); Assert.All(batch.Document.Snapshot.Manifest.Layers.Take(32), l => Assert.Equal(.5, l.Opacity));
    }

    [Fact]
    public void NetZeroBatchKeepsRedoAndPublishesNoChange()
    {
        var doc = ManyLayers(); var id = doc.Snapshot.Manifest.ActiveLayerId!.Value;
        doc.RenameLayer(id, "Redo"); doc.Undo();
        var snapshot = doc.Snapshot; int changes = 0; doc.Changed += () => changes++;
        string name = doc.Snapshot.Manifest.Layers.Single(l => l.Id == id).Name;
        EditorAutomation.ApplyBatch(doc, new JsonArray(
            new JsonObject { ["tool"] = "editor_layer_rename", ["arguments"] = new JsonObject { ["layerId"] = id.ToString(), ["name"] = "Temporary" } },
            new JsonObject { ["tool"] = "editor_layer_rename", ["arguments"] = new JsonObject { ["layerId"] = id.ToString(), ["name"] = name } }));
        Assert.Same(snapshot, doc.Snapshot); Assert.Equal(0, changes); Assert.False(doc.IsDirty); Assert.False(doc.CanUndo); Assert.True(doc.CanRedo);
    }

    private static EditorDocument ManyLayers()
    {
        var doc = EditorDocument.Create(64, 64); var manifest = doc.Snapshot.Manifest;
        var images = new Dictionary<Guid, Surface>(doc.Snapshot.Images);
        var image = new Surface(1, 1);
        for (int i = 1; i < 256; i++)
        {
            var id = Guid.NewGuid();
            manifest.Layers.Add(new() { Id = id, Name = "Layer " + i, IsVisible = true, Opacity = 1,
                ImageFile = ProjectValidator.Uuid(id) + ".png", Transform = new() { Origin = new(i % 64, i / 64), Size = new(1, 1) } });
            images.Add(id, image);
        }
        return new(new() { Manifest = manifest, Images = images, Masks = new Dictionary<Guid, MaskSurface>() });
    }
}
