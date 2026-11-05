using System.Text.Json.Nodes;
using Xunit;

namespace Compositor.Core.Tests;

public sealed class EditorAutomationTests
{
    [Fact]
    public void BatchDuplicateTransformAndOpacityIsOneUndo()
    {
        var doc = EditorDocument.Create(24, 16);
        string before = ManifestJson.Serialize(doc.Snapshot.Manifest);
        var id = doc.Snapshot.Manifest.ActiveLayerId!.Value;
        var result = EditorAutomation.ApplyBatch(doc, new JsonArray(
            new JsonObject { ["tool"] = "editor_layer_duplicate", ["arguments"] = new JsonObject { ["layerId"] = id.ToString() }, ["as"] = "copy" },
            new JsonObject { ["tool"] = "editor_layer_transform", ["arguments"] = new JsonObject { ["layerId"] = "$copy", ["x"] = 4, ["y"] = 3 } },
            new JsonObject { ["tool"] = "editor_layer_opacity", ["arguments"] = new JsonObject { ["layerId"] = "$copy", ["opacity"] = .5 } }));
        Assert.Equal(2, doc.LayerCount);
        Assert.Equal(.5, doc.Snapshot.Manifest.Layers.Single(l => l.Id == result["copy"]).Opacity);
        string after = ManifestJson.Serialize(doc.Snapshot.Manifest);
        Assert.True(doc.Undo()); Assert.False(doc.CanUndo); Assert.Equal(before, ManifestJson.Serialize(doc.Snapshot.Manifest));
        Assert.True(doc.Redo()); Assert.Equal(after, ManifestJson.Serialize(doc.Snapshot.Manifest));
    }

    [Fact]
    public void FailedBatchPreservesDocumentHistoryAndRedo()
    {
        var doc = EditorDocument.Create(8, 6); var id = doc.Snapshot.Manifest.ActiveLayerId!.Value;
        doc.RenameLayer(id, "Redo name"); doc.Undo();
        string before = ManifestJson.Serialize(doc.Snapshot.Manifest); int changes = 0; doc.Changed += () => changes++;
        Assert.Throws<ArgumentException>(() => EditorAutomation.ApplyBatch(doc, new JsonArray(
            new JsonObject { ["tool"] = "editor_layer_rename", ["arguments"] = new JsonObject { ["layerId"] = id.ToString(), ["name"] = "Temporary" } },
            new JsonObject { ["tool"] = "editor_layer_opacity", ["arguments"] = new JsonObject { ["layerId"] = id.ToString(), ["opacity"] = 2 } })));
        Assert.Equal(before, ManifestJson.Serialize(doc.Snapshot.Manifest)); Assert.Equal(0, changes);
        Assert.False(doc.IsDirty); Assert.False(doc.CanUndo); Assert.True(doc.Redo());
        Assert.Equal("Redo name", doc.Snapshot.Manifest.Layers.Single().Name);
    }

    [Fact]
    public void PolygonCombinesWithSelectionAndKeepsFeatherCoverage()
    {
        var doc = EditorDocument.Create(20, 20);
        var polygon = new JsonObject { ["mode"] = "polygon", ["points"] = new JsonArray(new JsonArray(2, 2), new JsonArray(15, 2), new JsonArray(2, 15)), ["feather"] = 2 };
        var selected = EditorAutomation.PrepareSelection(doc.Snapshot.Manifest, null, polygon);
        Assert.NotNull(selected); Assert.Equal(2, selected.Feather); Assert.Equal(0, selected.CoverageAt(18, 18));
        var combined = EditorAutomation.PrepareSelection(doc.Snapshot.Manifest, selected,
            new JsonObject { ["mode"] = "rectangle", ["x"] = 0, ["y"] = 0, ["width"] = 5, ["height"] = 5, ["combine"] = "subtract" });
        Assert.NotNull(combined); Assert.Equal(0, combined.CoverageAt(3, 3)); Assert.True(combined.CoverageAt(7, 4) > 0);
    }

    [Fact]
    public void PreviewSharesSettledPixelsAndRendersAtBoundedSize()
    {
        var doc = EditorDocument.Create(2048, 1536); var id = doc.Snapshot.Manifest.ActiveLayerId!.Value;
        var capture = EditorPreview.Capture(doc, id);
        Assert.Same(doc.Snapshot.Images[id], capture.Images[id]);
        var output = EditorPreview.Render(capture, 128);
        Assert.Equal(128, output.Width); Assert.Equal(96, output.Height);
        Assert.All(output.Pixels, value => Assert.Equal(255, value));
    }

    [Fact]
    public void TextAndAdjustmentUseExistingMetadataAndSingleUndo()
    {
        var doc = EditorDocument.Create(400, 300);
        var text = EditorAutomation.Apply(doc, "editor_text", new JsonObject { ["content"] = "Hello", ["fontName"] = "Arial", ["fontSize"] = 24, ["x"] = 10, ["y"] = 20 });
        Assert.NotNull(text); Assert.Equal("Hello", doc.Snapshot.Manifest.Layers.Single(l => l.Id == text).Text!.Content);
        EditorAutomation.Apply(doc, "editor_text", new JsonObject { ["layerId"] = text.ToString(), ["content"] = "你好", ["fontSize"] = 30 });
        Assert.Equal("你好", doc.Snapshot.Manifest.Layers.Single(l => l.Id == text).Text!.Content);
        Assert.True(doc.Undo()); Assert.Equal("Hello", doc.Snapshot.Manifest.Layers.Single(l => l.Id == text).Text!.Content);
        var adjustment = EditorAutomation.Apply(doc, "editor_adjustment", new JsonObject { ["kind"] = "Exposure", ["settings"] = new JsonObject { ["exposureSettings"] = new JsonObject { ["exposure"] = 1 } } });
        Assert.Equal(1, doc.Snapshot.Manifest.Layers.Single(l => l.Id == adjustment).Adjustment!.ExposureSettings!.Exposure);
        string before = ManifestJson.Serialize(doc.Snapshot.Manifest);
        Assert.Throws<ArgumentException>(() => EditorAutomation.Apply(doc, "editor_adjustment", new JsonObject { ["layerId"] = adjustment.ToString(), ["settings"] = new JsonObject { ["exposureSettings"] = new JsonObject { ["gamma"] = -1 } } }));
        Assert.Equal(before, ManifestJson.Serialize(doc.Snapshot.Manifest));
    }
}
