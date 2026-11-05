using Xunit;
using System.Linq;

namespace Compositor.Core.Tests;

/// <summary>
/// C02a: which target a brush may actually paint. The macOS session is explicit — "A folder has no
/// pixels of its own, so only its mask can be painted" (EditorSession+Brush.swift:5-11) — and the
/// port used to refuse folders and adjustment layers on both planes, so a folder mask existed,
/// rendered, and could not be edited.
/// </summary>
public class MaskPaintTargetTests
{
    private static EditorDocument DocumentWithLayer(out Guid layerId, int size = 16)
    {
        var document = EditorDocument.Create(size, size);
        var surface = new Surface(size, size);
        for (int i = 0; i < surface.Pixels.Length; i += 4)
        {
            surface.Pixels[i] = 200; surface.Pixels[i + 1] = 20; surface.Pixels[i + 2] = 20; surface.Pixels[i + 3] = 255;
        }
        var record = document.ImportImage(surface, "Block");
        layerId = record.Id;
        return document;
    }

    private static MaskSurface White(int size)
    {
        var mask = new MaskSurface(size, size);
        Array.Fill(mask.Pixels, (byte)255);
        return mask;
    }

    private static bool PaintAt(EditorDocument document, Guid layerId, bool onMask, double x, double y, byte value = 0)
    {
        var key = new object();
        if (!document.BeginStroke(key, layerId, new BrushSettings(3, 1, 1, value, value, value), new PointD(x, y), onMask)) return false;
        document.ContinueStroke(key, new PointD(x + 0.5, y + 0.5));
        document.EndStroke(key);
        return true;
    }

    [Fact]
    public void AFolderMaskTakesPaintAndChangesNothingElse()
    {
        var document = DocumentWithLayer(out var child);
        var folder = document.GroupLayers([child])!;
        Assert.True(folder.IsGroupOrFalse);
        Assert.True(document.AddRasterMask(folder.Id, White(16)));

        Assert.True(document.WhyPaintRefused(folder.Id, onMask: true) is null);
        Assert.True(PaintAt(document, folder.Id, onMask: true, x: 8.5, y: 8.5));

        var mask = document.Snapshot.Masks[folder.Id];
        Assert.Contains(mask.Pixels, level => level < 255);
        Assert.All(document.Snapshot.Images[child].Pixels.Where((_, index) => index % 4 == 3), level => Assert.Equal(255, level));
        Assert.True(document.Undo());
        Assert.All(document.Snapshot.Masks[folder.Id].Pixels, level => Assert.Equal(255, level));
    }

    [Fact]
    public void AFolderStillRefusesImagePaintAndSaysWhy()
    {
        var document = DocumentWithLayer(out var child);
        var folder = document.GroupLayers([child])!;

        Assert.Equal(EditorDocument.PaintRefusal.FolderHasNoPixels, document.WhyPaintRefused(folder.Id, onMask: false));
        Assert.False(PaintAt(document, folder.Id, onMask: false, x: 8.5, y: 8.5));
        // One Undo is still expected to revert the grouping itself: had the refused stroke opened a
        // history step, that step is what Undo would take back and the folder would survive.
        Assert.NotNull(document.Snapshot.Manifest.Layers.FirstOrDefault(layer => layer.Id == folder.Id));
        Assert.True(document.Undo());
        Assert.Null(document.Snapshot.Manifest.Layers.FirstOrDefault(layer => layer.Id == folder.Id));
    }

    [Fact]
    public void AnAdjustmentLayerTakesMaskPaintButNotImagePaint()
    {
        var document = DocumentWithLayer(out _);
        var adjustment = document.AddAdjustment(AdjustmentKind.Levels);

        Assert.Equal(EditorDocument.PaintRefusal.AdjustmentHasNoPixels, document.WhyPaintRefused(adjustment.Id, onMask: false));
        Assert.True(document.AddRasterMask(adjustment.Id, White(16)));
        Assert.True(document.WhyPaintRefused(adjustment.Id, onMask: true) is null);
        Assert.True(PaintAt(document, adjustment.Id, onMask: true, x: 8.5, y: 8.5));
        Assert.Contains(document.Snapshot.Masks[adjustment.Id].Pixels, level => level < 255);
    }

    [Fact]
    public void ATurnedOffMaskRefusesPaintUntilItIsTurnedBackOn()
    {
        var document = DocumentWithLayer(out var layerId);
        Assert.True(document.AddRasterMask(layerId, White(16)));

        Assert.True(PaintAt(document, layerId, onMask: true, x: 4.5, y: 4.5));
        Assert.True(document.SetMaskEnabled(layerId, false));
        Assert.Equal(EditorDocument.PaintRefusal.MaskTurnedOff, document.WhyPaintRefused(layerId, onMask: true));
        Assert.False(PaintAt(document, layerId, onMask: true, x: 12.5, y: 12.5));
        Assert.Equal(255, document.Snapshot.Masks[layerId].Pixels[12 * 16 + 12]);

        Assert.True(document.SetMaskEnabled(layerId, true));
        Assert.True(PaintAt(document, layerId, onMask: true, x: 12.5, y: 12.5));
        Assert.Equal(0, document.Snapshot.Masks[layerId].Pixels[12 * 16 + 12]);
    }

    [Fact]
    public void AnEmptySelectionRefusesPaintAndNamesTheWayOut()
    {
        var document = DocumentWithLayer(out var layerId);
        document.SetSelection(Selection.FromCoverage(new MaskSurface(16, 16)));

        Assert.Equal(EditorDocument.PaintRefusal.EmptySelection, document.WhyPaintRefused(layerId, onMask: false));
        Assert.False(PaintAt(document, layerId, onMask: false, x: 8.5, y: 8.5));

        document.SetSelection(Selection.Rectangle(16, 16, 6, 6, 4, 4));
        Assert.True(document.WhyPaintRefused(layerId, onMask: false) is null);
    }

    [Fact]
    public void AMissingLayerIsRefusedRatherThanCrashingTheGate()
    {
        var document = DocumentWithLayer(out _);
        Assert.Equal(EditorDocument.PaintRefusal.MissingLayer, document.WhyPaintRefused(Guid.NewGuid(), onMask: true));
    }

    /// <summary>
    /// C02b: asking for a mask on a layer that already has one keeps the mask that was painted. The store
    /// used to replace it outright — a stray menu click turned a painted mask into a blank white one, and
    /// it pushed an undo step for the damage. The Mac disables the affordance (LayerMaskMenu.swift:16) and
    /// refuses in the model (LayerMask.swift:243), so a refusal must also leave the document otherwise
    /// untouched.
    /// </summary>
    [Fact]
    public void AddingAMaskToAMaskedLayerKeepsThePaintedOneAndPushesNoUndoStep()
    {
        var document = EditorDocument.Create(6, 4);
        var layer = document.Snapshot.Manifest.Layers[0];
        var painted = new MaskSurface(6, 4, Enumerable.Range(1, 24).Select(value => (byte)value).ToArray());
        Assert.True(document.AddRasterMask(layer.Id, painted));
        document.MarkSaved();
        var held = document.Snapshot.Masks[layer.Id];

        Assert.False(document.AddRasterMask(layer.Id, MaskSurface.Solid(revealing: true)));

        Assert.Same(held, document.Snapshot.Masks[layer.Id]);
        Assert.Equal(painted.Pixels, document.Snapshot.Masks[layer.Id].Pixels);
        Assert.False(document.IsDirty, "a refused add marked the document unsaved.");
        Assert.NotNull(document.Snapshot.Manifest.Layers[0].MaskFile);
        Assert.NotEqual(false, document.Snapshot.Manifest.Layers[0].MaskEnabled);
    }
}
