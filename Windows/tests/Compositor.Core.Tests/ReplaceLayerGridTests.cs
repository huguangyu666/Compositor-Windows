using Compositor.Core;
using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// The commit seam the spreading filters need (Filters.swift:616-649): a blurred layer gets a bigger
/// grid and the placement that puts it back on the same document rectangle, in one undo step — and the
/// attached mask rides onto the new grid with the old edge's own tone outside it, disabled or not.
/// </summary>
public class ReplaceLayerGridTests
{
    private static (EditorDocument Doc, Guid Id, Surface Pixels) Scene(
        int width = 8, int height = 6, MaskSurface? mask = null, bool maskEnabled = true)
    {
        var id = Guid.NewGuid();
        var pixels = new Surface(width, height);
        Array.Fill(pixels.Pixels, (byte)200);
        for (int i = 3; i < pixels.Pixels.Length; i += 4) pixels.Pixels[i] = 255;
        var transform = new LayerTransform { Origin = new PointD(10, 20), Size = new SizeD(width, height) };
        var doc = new EditorDocument(new ProjectSnapshot
        {
            Manifest = new() { DocumentId = Guid.NewGuid(), Width = 32, Height = 24, ActiveLayerId = id,
                Layers = [new() { Id = id, Name = "Layer", IsVisible = true, Transform = transform,
                    ImageFile = ProjectValidator.Uuid(id) + ".png",
                    MaskFile = mask is null ? null : ProjectValidator.Uuid(id) + ".mask.png",
                    MaskEnabled = mask is null ? null : maskEnabled }] },
            Images = new Dictionary<Guid, Surface> { [id] = pixels },
            Masks = mask is null ? new() : new Dictionary<Guid, MaskSurface> { [id] = mask },
        });
        return (doc, id, pixels);
    }

    private static LayerTransform Placed(double x, double y, double w, double h)
        => new() { Origin = new PointD(x, y), Size = new SizeD(w, h) };

    [Fact]
    public void TheGridAndItsPlacementLandTogetherOnOneStep()
    {
        var (doc, id, _) = Scene();
        var before = doc.Snapshot.Manifest.Layers[0].Transform;
        var grown = new Surface(12, 10);
        Array.Fill(grown.Pixels, (byte)120);
        for (int i = 3; i < grown.Pixels.Length; i += 4) grown.Pixels[i] = 255;
        var placement = Placed(8, 18, 12, 10);

        Assert.True(doc.ReplaceLayerGrid(id, grown, placement));
        Assert.Same(grown, doc.Snapshot.Images[id]);
        var after = doc.Snapshot.Manifest.Layers[0].Transform;
        Assert.Equal(placement.Origin, after.Origin);
        Assert.Equal(placement.Size, after.Size);

        // One step: the step that Undo consumes goes back to BOTH the old pixels and the old placement.
        Assert.True(doc.Undo());
        Assert.NotSame(grown, doc.Snapshot.Images[id]);
        Assert.Equal(before.Origin, doc.Snapshot.Manifest.Layers[0].Transform.Origin);
        Assert.Equal(before.Size, doc.Snapshot.Manifest.Layers[0].Transform.Size);
        Assert.True(doc.Redo());
        Assert.Same(grown, doc.Snapshot.Images[id]);
    }

    [Fact]
    public void ACoalescedGridSwapKeepsOneUndoStepAndMovesPixelsOnly()
    {
        var (doc, id, _) = Scene();
        var key = new object();
        var first = new Surface(10, 8); var second = new Surface(12, 10);
        Assert.True(doc.ReplaceLayerGrid(id, first, Placed(9, 19, 10, 8), key));
        Assert.True(doc.ReplaceLayerGrid(id, second, Placed(8, 18, 12, 10), key));

        // The preview ticks of one session coalesce; the first commit opened the step and the second
        // joined it, so one Undo leaves the session exactly as it started.
        doc.EndTransform(key);
        Assert.True(doc.Undo());
        Assert.Equal(8, doc.Snapshot.Images[id]!.Width);
    }

    [Fact]
    public void APlacementTheManifestWouldRefuseLeavesNothingBehind()
    {
        var (doc, id, _) = Scene();
        var snapshot = doc.Snapshot;
        // Size beyond the manifest's own IsValid bound: the refusal runs before BeginChange, like the
        // Mac's grow throwing too large (Filters.swift:405) — no step, no pixel, no placement touched.
        var enormous = new LayerTransform { Origin = new PointD(0, 0), Size = new SizeD(1e9, 1e9) };
        Assert.False(doc.ReplaceLayerGrid(id, doc.Snapshot.Images[id]!, enormous));
        Assert.Same(snapshot, doc.Snapshot);
        Assert.False(doc.CanUndo);
    }

    [Fact]
    public void TheCarryRidesTheMaskOntoTheGrownGridWithTheEdgesOwnTone()
    {
        // An attached mask that reveals the middle and hides the border: the border's vote is OFF, so
        // the padding a blur grew into must come in OFF, and the mask's own picture rides through the
        // two placements' document mapping unchanged where it still exists.
        var mask = new MaskSurface(8, 6);
        for (int y = 0; y < 6; y++)
            for (int x = 0; x < 8; x++)
                mask.Pixels[y * 8 + x] = x is >= 2 and <= 5 && y is >= 1 and <= 4 ? (byte)255 : (byte)0;
        var (doc, id, _) = Scene(mask: mask);
        var oldPlacement = doc.Snapshot.Manifest.Layers[0].Transform;

        // Grow by two on every side, same center: origin −2, size +4 (the Mac's expand ratio).
        Assert.True(doc.CarryAttachedMask(id, oldPlacement, 8, 6, Placed(8, 18, 12, 10), 12, 10));
        var carried = doc.Snapshot.Masks[id]!;
        Assert.Equal(12, carried.Width);
        Assert.Equal(10, carried.Height);
        Assert.Equal(0, carried.Pixels[0]);                                     // padding took the edge's off
        Assert.Equal(0, carried.Pixels[(9 * 12 + 9)]);                          // bottom-right padding, off
        Assert.Equal(255, carried.Pixels[(4 * 12 + 5)]);                        // the old reveal, moved with the grid
        Assert.Equal(255, carried.Pixels[(5 * 12 + 6)]);
        Assert.True(doc.Undo());                                                // one step, back to the old mask
        Assert.Same(mask, doc.Snapshot.Masks[id]);
    }

    [Fact]
    public void ADisabledMaskTravelsAndStillSleeps()
    {
        var mask = new MaskSurface(8, 6); Array.Fill(mask.Pixels, (byte)0);   // hides everything, turned off
        var (doc, id, _) = Scene(mask: mask, maskEnabled: false);
        var oldPlacement = doc.Snapshot.Manifest.Layers[0].Transform;
        Assert.True(doc.CarryAttachedMask(id, oldPlacement, 8, 6, Placed(8, 18, 12, 10), 12, 10));
        Assert.False(doc.Snapshot.Manifest.Layers[0].MaskEnabled != false);
        // The padding of an all-off mask is off, and waking the mask was never the carry's job.
        Assert.Equal(0, doc.Snapshot.Masks[id]!.Pixels[0]);
    }

    [Fact]
    public void SelectionCoverageFollowsTheGrownPlacementItIsSampledThrough()
    {
        // The layer box is doc x 10–18; the selection covers its right half, doc x 14–22. Sampled
        // through the LAYER placement a grown-grid pixel maps docX = px + 10 — the selection begins at
        // px 4. Sampled through a placement grown by two on every side (doc x 8–20, grown 12×10) the
        // same document edge arrives at grown px 6. The clip the sheet blends through has to say where
        // the selection sits ON THE GROWN GRID, which is what the grown placement is for.
        var (doc, id, _) = Scene();
        doc.SetSelection(Selection.Rectangle(32, 24, 14, 20, 8, 6));
        var own = doc.Snapshot.Manifest.Layers[0].Transform;
        var layerCoverage = doc.SelectionCoverageOnGrid(id, own, 8, 6)!;
        var grown = Placed(8, 18, 12, 10);
        var grownCoverage = doc.SelectionCoverageOnGrid(id, grown, 12, 10)!;
        Assert.Equal(0, layerCoverage.Pixels[3 * 8 + 3]);
        Assert.Equal(255, layerCoverage.Pixels[3 * 8 + 5]);
        Assert.Equal(0, grownCoverage.Pixels[4 * 12 + 5]);
        Assert.Equal(255, grownCoverage.Pixels[4 * 12 + 7]);
    }

    [Fact]
    public void ASeparatelyPlacedOrUniformOrAbsentMaskIsLeftAlone()
    {
        var (doc, id, _) = Scene();
        Assert.False(doc.CarryAttachedMask(id, doc.Snapshot.Manifest.Layers[0].Transform, 8, 6, Placed(8, 18, 12, 10), 12, 10));

        var uniform = new MaskSurface(1, 1); uniform.Pixels[0] = 255;
        var (doc2, id2, _) = Scene(mask: uniform);
        // A 1×1 uniform mask does not travel grid-to-grid — its size is not the layer's grid, and the
        // carry's own guard (mask.Width != oldWidth) leaves the layer's mask exactly as it stands,
        // covering any grid it is drawn over.
        Assert.False(doc2.CarryAttachedMask(id2, doc2.Snapshot.Manifest.Layers[0].Transform, 8, 6, Placed(8, 18, 12, 10), 12, 10));
        Assert.Equal(1, doc2.Snapshot.Masks[id2]!.Width);
        Assert.False(doc2.CanUndo);
    }
}
