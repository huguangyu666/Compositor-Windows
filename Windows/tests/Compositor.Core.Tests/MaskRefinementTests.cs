using System.Text.Json.Nodes;
using Xunit;

namespace Compositor.Core.Tests;

public sealed class MaskRefinementTests
{
    [Fact]
    public void ExistingGrayMaskChangesInOneUndoWithoutTouchingImage()
    {
        var doc = EditorDocument.Create(16, 12); var id = doc.Snapshot.Manifest.ActiveLayerId!.Value;
        var image = doc.Snapshot.Images[id]; var mask = new MaskSurface(16, 12); mask.Pixels.AsSpan().Fill(80);
        doc.AddRasterMask(id, mask); var original = mask.Pixels.ToArray();
        var edit = MaskRefinement.Capture(doc, id);
        edit.Prepare(new JsonObject { ["operation"] = "invert" });
        Assert.Equal(original, doc.Snapshot.Masks[id].Pixels);
        Assert.True(edit.Commit()); Assert.Same(image, doc.Snapshot.Images[id]); Assert.Equal(175, doc.Snapshot.Masks[id].Pixels[0]);
        Assert.True(doc.Undo()); Assert.Same(mask, doc.Snapshot.Masks[id]);
        Assert.True(doc.Redo()); Assert.Equal(175, doc.Snapshot.Masks[id].Pixels[0]);
    }

    [Fact]
    public void MaskBrushRespectsSelectionAndLeavesSourceBytesImmutable()
    {
        var doc = EditorDocument.Create(32, 24); var id = doc.Snapshot.Manifest.ActiveLayerId!.Value;
        var mask = new MaskSurface(32, 24); mask.Pixels.AsSpan().Fill(255); doc.AddRasterMask(id, mask);
        doc.SetSelection(Selection.Rectangle(32, 24, 8, 8, 8, 8));
        var edit = MaskRefinement.Capture(doc, id);
        edit.Prepare(new JsonObject { ["operation"] = "paint", ["points"] = new JsonArray(new JsonArray(10, 10), new JsonArray(20, 10)), ["radius"] = 4, ["value"] = 0, ["hardness"] = 1 });
        Assert.True(edit.Commit()); Assert.All(mask.Pixels, value => Assert.Equal(255, value));
        var result = doc.Snapshot.Masks[id]; Assert.Equal(255, result.Pixels[10 * result.Width + 20]); Assert.True(result.Pixels[10 * result.Width + 11] < 255);
    }

    [Fact]
    public void ChangedTargetRefusesPreparedMaskAndGrayGrowthStaysGray()
    {
        var doc = EditorDocument.Create(9, 9); var id = doc.Snapshot.Manifest.ActiveLayerId!.Value;
        var mask = new MaskSurface(9, 9); mask.Pixels[40] = 96; doc.AddRasterMask(id, mask);
        var edit = MaskRefinement.Capture(doc, id); edit.Prepare(new JsonObject { ["operation"] = "expand", ["amount"] = 1 });
        doc.RenameLayer(id, "Changed"); Assert.False(edit.Commit()); Assert.Same(mask, doc.Snapshot.Masks[id]);
        edit = MaskRefinement.Capture(doc, id); edit.Prepare(new JsonObject { ["operation"] = "expand", ["amount"] = 1 }); Assert.True(edit.Commit());
        Assert.Equal(96, doc.Snapshot.Masks[id].Pixels[31]); Assert.DoesNotContain((byte)255, doc.Snapshot.Masks[id].Pixels);
    }
}
