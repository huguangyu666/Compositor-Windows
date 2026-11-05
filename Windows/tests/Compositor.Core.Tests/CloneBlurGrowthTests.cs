using Xunit;
using System.Linq;

namespace Compositor.Core.Tests;

/// <summary>
/// Clone Stamp and Blur follow the same growth grid as brush and eraser (§5): the stamp
/// samples from the source frozen at stroke start — the layer's own pixels or an
/// all-layers composite — in document space, so it reads past the old grid; the blur's
/// radius stays tied to the original grid and its sample reads transparent outside the
/// source, never drifting with the grown plane. One undo step, same as the brush.
/// </summary>
public class CloneBlurGrowthTests
{
    private static EditorDocument Scene()
    {
        var scene = EditorDocument.Create(64, 64);
        var id = scene.Snapshot.Manifest.Layers[0].Id;
        var source = new Surface(32, 32);
        for (int y = 0; y < 32; y++)
        {
            for (int x = 0; x < 32; x++)
            {
                int index = y * source.Stride + x * 4;
                source.Pixels[index] = 20;
                source.Pixels[index + 1] = (byte)(60 + x * 4); // varies so a copy is visible
                source.Pixels[index + 2] = 240;
                source.Pixels[index + 3] = 255;
            }
        }
        ((Dictionary<Guid, Surface>)scene.Snapshot.Images)[id] = source;
        scene.Snapshot.Manifest.Layers[0].Transform = new LayerTransform { Size = new SizeD(32, 32) };
        return scene;
    }

    private static EditorDocument SceneTwoLayers()
    {
        var scene = Scene();
        var id = scene.Snapshot.Manifest.Layers[0].Id;
        var top = scene.AddBlankLayer()!;
        // A red patch on the top layer at (36,4)…(44,12): outside the clone target's own pixels.
        var patch = new Surface(8, 8);
        for (int i = 0; i < patch.Pixels.Length; i += 4) { patch.Pixels[i] = 200; patch.Pixels[i + 3] = 255; }
        scene.ReplaceLayerImage(top.Id, patch);
        scene.Snapshot.Manifest.Layers.Last(l => l.Id == top.Id).Transform = new LayerTransform { Origin = new PointD(36, 4), Size = new SizeD(8, 8) };
        return new(scene.Snapshot);
    }

    private static byte Channel(Surface surface, int x, int y, int channel) =>
        x < 0 || y < 0 || x >= surface.Width || y >= surface.Height ? (byte)0 : surface.Pixels[y * surface.Stride + x * 4 + channel];

    private static (int X, int Y) GridOf(EditorDocument scene, Guid id, double docX, double docY)
    {
        var image = scene.Snapshot.Images[id]; var transform = scene.Snapshot.Manifest.Layers.Single(layer => layer.Id == id).Transform;
        var point = BrushEngine.DocumentToPixel(transform, image.Width, image.Height, new PointD(docX, docY));
        return ((int)point.X, (int)point.Y);
    }

    [Fact]
    public void CloneStampGrowsPastTheGridSamplingTheLayer()
    {
        var scene = Scene(); var id = scene.Snapshot.Manifest.Layers[0].Id;
        var original = scene.Snapshot.Images[id]; var placement = scene.Snapshot.Manifest.Layers[0].Transform;
        // Source (4.5, 4.5) inside the 32×32 layer; target (36.5, 4.5) outside it: the stamp
        // must grow the layer and copy from 32 document pixels to the left.
        Assert.True(scene.BeginCloneStroke(id, new(4.5, 4.5), new(36.5, 4.5), 2, 1, sampleAllLayers: false, new object()));
        scene.EndCloneStroke();
        var grown = scene.Snapshot.Images[id];
        Assert.True(grown.Width > 32 || grown.Height > 32, "the clone stamp clipped to the old grid");
        var (gx, gy) = GridOf(scene, id, 36.5, 4.5);
        Assert.Equal(20, Channel(grown, gx, gy, 0));
        Assert.Equal(240, Channel(grown, gx, gy, 2)); // the sampled source color, not the brush's
        Assert.Equal(255, Channel(grown, gx, gy, 3));
        Assert.True(scene.Undo());
        Assert.Same(original, scene.Snapshot.Images[id]); // one undo: bytes and placement
        Assert.Equal(placement, scene.Snapshot.Manifest.Layers[0].Transform);
    }

    [Fact]
    public void CloneStampSamplesTheFrozenCompositeForAllLayers()
    {
        var scene = SceneTwoLayers();
        var id = scene.Snapshot.Manifest.Layers.First(layer => layer.Name == "Background").Id;
        var before = scene.Snapshot.Images[id];
        // Copy the neighbor layer's red patch into the background's empty corner.
        Assert.True(scene.BeginCloneStroke(id, new(36.5, 4.5), new(10.5, 4.5), 2, 1, sampleAllLayers: true, new object()));
        scene.EndCloneStroke();
        var image = scene.Snapshot.Images[id];
        Assert.NotSame(before, image);
        Assert.Equal(200, Channel(image, 10, 4, 0)); // the top layer's red landed in the target
        Assert.True(scene.Undo()); Assert.Same(before, scene.Snapshot.Images[id]);
    }

    [Fact]
    public void CloneStampAcrossTilesKeepsOneUndoAndEarlierStamps()
    {
        var scene = Scene(); scene.Snapshot.Manifest.Width = 512; scene = new(scene.Snapshot);
        var id = scene.Snapshot.Manifest.Layers[0].Id;
        var original = scene.Snapshot.Images[id];
        var key = new object();
        Assert.True(scene.BeginCloneStroke(id, new(4.5, 4.5), new(36.5, 4.5), 2, 1, sampleAllLayers: false, key));
        Assert.True(scene.CloneStampAppend(id, new(4.5, 4.5), new(380.5, 4.5), 2, 1)); // beyond the first tile
        var mid = scene.Snapshot.Images[id]; // mid-stroke: the plane spans both tiles
        Assert.True(mid.Width > 256, $"the second tile did not grow the plane ({mid.Width}x{mid.Height})");
        var midLayer = scene.Snapshot.Manifest.Layers.Single(layer => layer.Id == id).Transform;
        var midFirst = BrushEngine.DocumentToPixel(midLayer, mid.Width, mid.Height, new(36.5, 4.5));
        Assert.Equal(240, Channel(mid, (int)midFirst.X, (int)midFirst.Y, 2)); // earlier stamp survived the growth
        scene.EndCloneStroke();
        // The commit trims the transparent far region: the fixed offset samples document
        // (348.5, 4.5), past the source, so nothing lands there.
        var image = scene.Snapshot.Images[id];
        var first = GridOf(scene, id, 36.5, 4.5);
        Assert.Equal(240, Channel(image, first.X, first.Y, 2)); // the visible stamp stayed
        Assert.True(scene.Undo()); Assert.False(scene.CanUndo); Assert.Same(original, scene.Snapshot.Images[id]);
    }

    [Fact]
    public void CloneOffsetSurvivesTheGridScaling()
    {
        // A 2× upscaled layer: the offset is in document pixels, so the copy distance in grid
        // pixels is half after growth changed the plane's size.
        var scene = EditorDocument.Create(64, 64); var id = scene.Snapshot.Manifest.Layers[0].Id;
        var source = new Surface(16, 16);
        for (int i = 0; i < source.Pixels.Length; i += 4) { source.Pixels[i] = 9; source.Pixels[i + 3] = 255; }
        ((Dictionary<Guid, Surface>)scene.Snapshot.Images)[id] = source;
        scene.Snapshot.Manifest.Layers[0].Transform = new() { Size = new(32, 32) };
        scene = new(scene.Snapshot);
        Assert.True(scene.BeginCloneStroke(id, new(8.5, 8.5), new(48.5, 8.5), 2, 1, sampleAllLayers: false, new object()));
        scene.EndCloneStroke();
        var (gx, gy) = GridOf(scene, id, 48.5, 8.5);
        Assert.Equal(9, Channel(scene.Snapshot.Images[id], gx, gy, 0));
    }

    [Fact]
    public void CloneOutsideTheSampleKeepsTheBasePixels()
    {
        var scene = Scene(); var id = scene.Snapshot.Manifest.Layers[0].Id;
        // The source point is itself outside the layer: nothing to copy, the grown area stays
        // transparent exactly where a brush would leave nothing.
        Assert.True(scene.BeginCloneStroke(id, new(50.5, 50.5), new(52.5, 52.5), 2, 1, sampleAllLayers: false, new object()));
        scene.EndCloneStroke();
        var (gx, gy) = GridOf(scene, id, 52.5, 52.5);
        Assert.Equal(0, Channel(scene.Snapshot.Images[id], gx, gy, 3));
    }

    [Fact]
    public void BlurGrowsWithTheGridAndItsRadiusStaysSourceScaled()
    {
        var scene = Scene(); var id = scene.Snapshot.Manifest.Layers[0].Id;
        var source = scene.Snapshot.Images[id];
        var key = new object();
        Assert.True(scene.BeginBlurStroke(id, 3, key));
        Assert.True(scene.BlurStampApply(id, new(33.5, 4.5), 3, 1));
        // A far tile materializes mid-stroke, then the edge is stamped again: the radius and
        // the feather must come out identical on the bigger plane — no drift with its size.
        Assert.True(scene.BlurStampApply(id, new(300.5, 4.5), 3, 1));
        Assert.True(scene.BlurStampApply(id, new(33.5, 4.5), 3, 1));
        scene.EndBlurStroke();
        var image = scene.Snapshot.Images[id];
        Assert.True(image.Width > 32, "the blur wrote no pixels past the source edge");
        // Over the opaque body the source-over blend keeps alpha 255 and feathers the color;
        // past the source the transparent-edge rule decays both — the grid decides where the
        // edge is, not the old asset bounds.
        Assert.Equal(255, Channel(image, 31, 4, 3)); // opaque over opaque stays opaque
        Assert.True(Channel(image, 31, 4, 1) < Channel(source, 31, 4, 1), "the edge kept the unblurred green");
        Assert.True(Channel(image, 33, 4, 3) > 0, "the blur did not feather past the source edge");
        Assert.True(Channel(image, 33, 4, 3) < 255, "the feathered edge kept full opacity");
        // The feather decays with distance, tied to the source-grid sigma: a few pixels
        // past the edge it is already faint, and it keeps fading outward.
        Assert.True(Channel(image, 35, 4, 3) < Channel(image, 33, 4, 3), "the feather did not fade outward");
        Assert.True(Channel(image, 37, 4, 3) < 16, $"the feather reached too far with sigma 3: {Channel(image, 37, 4, 3)}");
    }

    [Fact]
    public void BlurGrowthUndoesOnceAndRefusesOversizedBeforeHistory()
    {
        var scene = Scene(); var id = scene.Snapshot.Manifest.Layers[0].Id;
        var original = scene.Snapshot.Images[id]; var placement = scene.Snapshot.Manifest.Layers[0].Transform;
        var key = new object();
        Assert.True(scene.BeginBlurStroke(id, 3, key));
        Assert.True(scene.BlurStampApply(id, new(33.5, 4.5), 3, 1));
        scene.EndBlurStroke();
        Assert.NotSame(original, scene.Snapshot.Images[id]);
        Assert.True(scene.Undo()); Assert.False(scene.CanUndo); // one step for the whole stroke
        Assert.Same(original, scene.Snapshot.Images[id]);
        Assert.Equal(placement, scene.Snapshot.Manifest.Layers[0].Transform);
    }
}
