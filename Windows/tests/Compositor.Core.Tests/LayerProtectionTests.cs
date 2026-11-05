using System.Text.Json.Nodes;
using Xunit;

namespace Compositor.Core.Tests;

public class LayerProtectionTests
{
    private static void Lock(EditorDocument doc, Guid id, int flags)
    {
        Assert.True(doc.SetLayerLocks(id, (LayerLocks)flags));
    }

    [Fact]
    public void FullLockProtectsContentGeometryAndDeletionButAllowsSelectionAndVisibility()
    {
        var doc = EditorDocument.Create(16, 16); var id = doc.Snapshot.Manifest.ActiveLayerId!.Value;
        Lock(doc, id, 8);
        Assert.False(doc.MoveLayerBy(id, 1, 0));
        Assert.False(doc.ReplaceLayerImage(id, new Surface(16, 16)));
        Assert.False(doc.BeginStroke(new(), id, new(3, 1, 1, 255, 0, 0), new(4, 4)));
        Assert.False(doc.DeleteLayer(id));
        Assert.False(doc.CanUndo);
        Assert.True(doc.SetVisible(id, false));
        Assert.True(doc.Undo());
        Assert.False(doc.MoveLayerBy(id, 1, 0));
        Lock(doc, id, 0); Assert.True(doc.MoveLayerBy(id, 1, 0));
    }

    [Fact]
    public void PositionAndPixelLocksProtectDifferentOperations()
    {
        var doc = EditorDocument.Create(16, 16); var id = doc.Snapshot.Manifest.ActiveLayerId!.Value;
        Lock(doc, id, 1);
        Assert.False(doc.BeginTransform(new(), id));
        Assert.False(doc.MoveLayerBy(id, 2, 0));
        Assert.True(doc.FillPixels(id, 255, 0, 0));
        Lock(doc, id, 2);
        Assert.True(doc.MoveLayerBy(id, 2, 0));
        Assert.False(doc.FillPixels(id, 0, 255, 0));
        Assert.False(doc.ReplaceLayerImage(id, new Surface(16, 16)));
    }

    [Fact]
    public void TransparencyLockPreservesSoftAlphaPlacementAndUndo()
    {
        var doc = EditorDocument.Create(16, 16); var id = doc.Snapshot.Manifest.ActiveLayerId!.Value;
        var source = new Surface(8, 8);
        for (int i = 0; i < source.Pixels.Length; i += 4) { source.Pixels[i + 2] = source.Pixels[i + 3] = (byte)((i / 4 % 8) * 30); }
        doc.ReplaceLayerImage(id, source); doc = new(doc.Snapshot);
        string before = ManifestJson.Serialize(doc.Snapshot.Manifest); var alpha = source.Pixels.Where((_, i) => i % 4 == 3).ToArray();
        Lock(doc, id, 4); object key = new();
        Assert.True(doc.BeginStroke(key, id, new(20, .5, .7, 255, 0, 0), new(0, 0)));
        Assert.True(doc.ContinueStroke(key, new(16, 16))); doc.EndStroke(key);
        Assert.Equal(alpha, doc.Snapshot.Images[id].Pixels.Where((_, i) => i % 4 == 3));
        Assert.Equal(before, ManifestJson.Serialize(doc.Snapshot.Manifest));
        Assert.True(doc.Snapshot.Images[id].Pixels[4] > 0);
        Assert.True(doc.Undo()); Assert.Same(source, doc.Snapshot.Images[id]); Assert.False(doc.CanUndo);
        Assert.False(doc.BeginStroke(new(), id, new(4, 1, 1, 0, 0, 0), new(4, 4), erase: true));
        Assert.False(doc.CutSelectionMasked(id, Selection.Rectangle(16, 16, 0, 0, 16, 16)));
        Assert.True(doc.FillPixels(id, 0, 255, 0));
        Assert.Equal(alpha, doc.Snapshot.Images[id].Pixels.Where((_, i) => i % 4 == 3));
    }

    [Fact]
    public void FolderLockAndAutomationAreAtomic()
    {
        var doc = EditorDocument.Create(16, 16); var id = doc.Snapshot.Manifest.ActiveLayerId!.Value;
        var b = doc.ImportImage(new Surface(3, 3), "Second").Id;
        var folder = doc.GroupLayers([id])!; doc = new(doc.Snapshot); Lock(doc, folder.Id, 8);
        Assert.False(doc.MoveLayerBy(id, 2, 0)); Assert.False(doc.DeleteLayer(folder.Id));
        string before = ManifestJson.Serialize(doc.Snapshot.Manifest);
        Assert.ThrowsAny<Exception>(() => EditorAutomation.Apply(doc, "editor_layout", new JsonObject
        { ["operation"] = "align", ["alignment"] = "right", ["layerIds"] = new JsonArray(id.ToString(), b.ToString()) }));
        Assert.Equal(before, ManifestJson.Serialize(doc.Snapshot.Manifest)); Assert.False(doc.CanUndo);
    }

    [Fact]
    public void MultiTransformAndLateWorkerResultsRespectProtection()
    {
        var doc = EditorDocument.Create(16, 16); var a = doc.Snapshot.Manifest.ActiveLayerId!.Value;
        var b = doc.ImportImage(new Surface(8, 8), "B").Id; doc = new(doc.Snapshot);
        Lock(doc, b, 1); Assert.Null(doc.BeginTransformDraft([a, b])); Assert.False(doc.CanUndo);
        doc.SetSelection(Selection.Rectangle(16, 16, 0, 0, 10, 10));
        var clear = doc.BeginSelectionPlaneEdit(a, false, 0); Assert.NotNull(clear);
        var result = clear.Prepare(); Assert.NotNull(result);
        Lock(doc, a, 8); Assert.False(doc.CommitSelectionPlaneEdit(result)); Assert.False(doc.CanUndo);
        Assert.False(doc.AddHideAllMask(a));
    }

    [Fact]
    public void PositionLockedPaintingCannotGrowTheGrid()
    {
        var doc = EditorDocument.Create(16, 16); var id = doc.Snapshot.Manifest.ActiveLayerId!.Value;
        string original = ManifestJson.Serialize(doc.Snapshot.Manifest); Lock(doc, id, 1);
        object key = new(); Assert.True(doc.BeginStroke(key, id, new(8, 1, 1, 255, 0, 0), new(0, 0)));
        doc.ContinueStroke(key, new(30, 30)); doc.EndStroke(key);
        Assert.Equal(original, ManifestJson.Serialize(doc.Snapshot.Manifest));
        Assert.Equal(16, doc.Snapshot.Images[id].Width); Assert.True(doc.Undo()); Assert.False(doc.CanUndo);
    }

    [Fact]
    public void TransparencyLockedCloneAndTextCannotChangeAlpha()
    {
        var doc = EditorDocument.Create(16, 16); var id = doc.Snapshot.Manifest.ActiveLayerId!.Value;
        var image = doc.Snapshot.Images[id];
        for (int i = 0; i < image.Pixels.Length; i += 4) image.Pixels[i] = image.Pixels[i + 1] = image.Pixels[i + 2] = image.Pixels[i + 3] = (byte)(i / 4 % 16 * 16);
        byte[] alpha = image.Pixels.Where((_, i) => i % 4 == 3).ToArray(); Lock(doc, id, 4);
        object key = new(); Assert.True(doc.BeginCloneStroke(id, new(10, 5), new(0, 0), 8, 1, false, key));
        doc.CloneStampAppend(id, new(10, 5), new(14, 14), 8, 1); doc.EndCloneStroke();
        Assert.Equal(alpha, doc.Snapshot.Images[id].Pixels.Where((_, i) => i % 4 == 3));
        var text = doc.AddTextLayer(new() { Content = "Protected", FontSize = 20 }, new(2, 2))!;
        Lock(doc, text.Id, 4); Assert.False(doc.UpdateTextLayer(text.Id, new() { Content = "Different", FontSize = 30 }));
    }
}
