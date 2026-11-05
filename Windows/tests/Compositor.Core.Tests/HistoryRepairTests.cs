using Xunit;

namespace Compositor.Core.Tests;

/// <summary>Regressions for the 2026-10 review fixes: cut undo, aborted-change history,
/// the undo byte budget, selection-vs-layer space mapping, and the stamp sessions.</summary>
public class HistoryRepairTests
{
    private static Surface Solid(int width, int height, byte r, byte g, byte b, byte a = 255)
    {
        var surface = new Surface(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = y * surface.Stride + x * 4;
                surface.Pixels[index] = r;
                surface.Pixels[index + 1] = g;
                surface.Pixels[index + 2] = b;
                surface.Pixels[index + 3] = a;
            }
        }
        return surface;
    }

    [Fact]
    public void CutThenUndoRestoresThePixels()
    {
        var document = EditorDocument.Create(16, 16);
        var background = document.Snapshot.Manifest.Layers[0];
        document.SetSelection(Selection.Rectangle(16, 16, 4, 4, 8, 8));
        var before = document.Snapshot.Images[background.Id].Pixels.ToArray();

        Assert.NotNull(document.CutSelection(background.Id));
        var cut = document.Snapshot.Images[background.Id];
        Assert.Equal(0, cut.Pixels[8 * cut.Stride + 8 * 4 + 3]); // inside: cleared

        // The cut must not mutate the surface the memento holds: undo brings the pixels back.
        Assert.True(document.Undo());
        Assert.True(before.SequenceEqual(document.Snapshot.Images[background.Id].Pixels));
    }

    [Fact]
    public void FailedImageSizeLeavesNoPhantomUndoStepAndKeepsRedo()
    {
        var document = EditorDocument.Create(100, 100);
        var background = document.Snapshot.Manifest.Layers[0];
        document.ImportImage(Solid(200, 200, 0, 0, 255), "Big");
        // Big alone: the background's own box (just under 200 MP) would resample
        // expensively before Big's ~800 MP box rejected the resize.
        document.DeleteLayer(background.Id);

        // Big's upscaled bounding box (~800 MP) blows the pixel budget on a 14142²
        // canvas immediately, so the resize is rolled back after its change step opened.
        Assert.False(document.ResizeImage(14142, 14142));
        Assert.True(document.Undo()); // undoes the layer deletion
        Assert.True(document.Undo()); // undoes the import
        Assert.False(document.Undo()); // no no-op step was left behind

        // An aborted change never invalidated the redo stack either.
        Assert.True(document.Redo()); // the import still redoes
        Assert.Equal(2, document.Snapshot.Manifest.Layers.Count);
    }

    [Fact]
    public void FailedImageSizeKeepsTheDocumentClean()
    {
        var document = EditorDocument.Create(100, 100);
        var background = document.Snapshot.Manifest.Layers[0];
        document.ImportImage(Solid(200, 200, 0, 0, 255), "Big");
        document.DeleteLayer(background.Id); // Big alone, so the budget rejects fast
        document.MarkSaved();

        Assert.False(document.ResizeImage(14142, 14142));
        Assert.False(document.IsDirty);
    }

    [Fact]
    public void UndoHistoryIsCappedByItsByteBudget()
    {
        var document = EditorDocument.Create(16, 16);
        var background = document.Snapshot.Manifest.Layers[0];
        var brush = new BrushSettings(4, 1, 1, 0, 0, 255);
        for (int i = 0; i < 6; i++)
        {
            var key = new object();
            Assert.True(document.BeginStroke(key, background.Id, brush, new PointD(8, 8)));
            Assert.True(document.EndStroke(key));
        }

        int CountUndo()
        {
            int steps = 0;
            while (document.Undo()) steps++;
            return steps;
        }
        Assert.Equal(6, CountUndo()); // the default budget keeps every step

        // A budget far below six pinned surfaces drops the oldest steps.
        var capped = EditorDocument.Create(16, 16);
        var cappedBackground = capped.Snapshot.Manifest.Layers[0];
        capped.UndoByteBudget = 3 * 1024;
        for (int i = 0; i < 6; i++)
        {
            var key = new object();
            Assert.True(capped.BeginStroke(key, cappedBackground.Id, brush, new PointD(8, 8)));
            Assert.True(capped.EndStroke(key));
        }
        int cappedSteps = 0;
        while (capped.Undo()) cappedSteps++;
        Assert.True(cappedSteps is >= 1 and < 6, $"expected a trimmed history, got {cappedSteps}");
    }

    [Fact]
    public void ContentFillMapsTheSelectionThroughTheLayerTransform()
    {
        var document = EditorDocument.Create(32, 32);
        // A 16×16 layer placed at (8,8): left half blue, right half red.
        var layer = new Surface(16, 16);
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                int index = y * layer.Stride + x * 4;
                byte r = x < 8 ? (byte)0 : (byte)255;
                layer.Pixels[index] = r;
                layer.Pixels[index + 2] = (byte)(255 - r);
                layer.Pixels[index + 3] = 255;
            }
        }
        document.ImportImage(layer, "TwoTone");
        var record = document.Snapshot.Manifest.Layers.Single(l => l.Name == "TwoTone");
        record.Transform.Origin = new PointD(8, 8);
        // The layer's right half in document space is x 16..24, y 8..24 (Rectangle takes
        // x, y, width, height) — layer columns 8..15 on every row.
        document.SetSelection(Selection.Rectangle(32, 32, 16, 8, 8, 16));

        Assert.True(document.ContentFillSelection(record.Id));
        var filled = document.Snapshot.Images[record.Id];
        for (int y = 0; y < 16; y++)
        {
            for (int x = 8; x < 16; x++)
            {
                int index = y * filled.Stride + x * 4;
                // The selected red right half was filled from the blue donors: now blue.
                Assert.Equal(0, filled.Pixels[index]);
                Assert.Equal(255, filled.Pixels[index + 2]);
            }
        }
        // (4,4) was an unselected blue donor: unchanged.
        Assert.Equal(255, filled.Pixels[4 * filled.Stride + 4 * 4 + 2]);
    }

    [Fact]
    public void InvertPixelsMapsTheSelectionThroughTheLayerTransform()
    {
        var document = EditorDocument.Create(32, 32);
        document.ImportImage(Solid(8, 8, 128, 128, 128), "Gray");
        var record = document.Snapshot.Manifest.Layers.Single(l => l.Name == "Gray");
        record.Transform.Origin = new PointD(8, 8);
        // The selection covers the layer's top-left quadrant in document space.
        document.SetSelection(Selection.Rectangle(32, 32, 8, 8, 4, 4));

        Assert.True(document.InvertPixels(record.Id));
        var inverted = document.Snapshot.Images[record.Id];
        // Inverted: alpha − color = 255 − 128 = 127 inside the mapped quadrant only.
        Assert.Equal(127, inverted.Pixels[1 * inverted.Stride + 1 * 4]);
        Assert.Equal(128, inverted.Pixels[6 * inverted.Stride + 6 * 4]);
    }

    [Fact]
    public void CloneStrokeSessionStampsAndUndoesAsOneStep()
    {
        var document = EditorDocument.Create(16, 16);
        document.ImportImage(Solid(8, 8, 0, 0, 255), "Blue");
        var blue = document.Snapshot.Manifest.Layers.Single(l => l.Name == "Blue");
        document.SetActiveLayer(blue.Id);
        var before = document.Snapshot.Images[blue.Id].Pixels.ToArray();

        var key = new object();
        Assert.True(document.BeginCloneStroke(blue.Id, new PointD(0, 0), new PointD(4, 4), 1, 1, sampleAllLayers: false, key));
        Assert.True(document.CloneStampAppend(blue.Id, new PointD(0, 0), new PointD(6, 4), 1, 1));
        document.EndCloneStroke();

        var after = document.Snapshot.Images[blue.Id];
        Assert.Equal(255, after.Pixels[4 * after.Stride + 6 * 4 + 3]); // second stamp landed

        Assert.True(document.Undo()); // the whole session is one step
        Assert.True(before.SequenceEqual(document.Snapshot.Images[blue.Id].Pixels));
    }

    [Fact]
    public void UnchangedAssetsTrackSavedSurfaces()
    {
        var document = EditorDocument.Create(16, 16);
        var background = document.Snapshot.Manifest.Layers[0];
        document.MarkSaved();
        Assert.Contains(background.ImageFile!, document.UnchangedAssetNames());

        document.InvertPixels(background.Id); // replaces the surface: no longer current
        Assert.Empty(document.UnchangedAssetNames());

        document.MarkSaved();
        Assert.Contains(background.ImageFile!, document.UnchangedAssetNames());
    }
}
