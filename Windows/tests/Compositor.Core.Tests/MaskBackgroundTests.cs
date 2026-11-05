using Xunit;
using System.Linq;

namespace Compositor.Core.Tests;

/// <summary>
/// A mask placed apart from its layer shows its edge tone beyond its own pixels, as
/// `LayerMask.background` / `placed` / `clipImage` do on the Mac: a reveal-all mask keeps
/// revealing outside its rectangle, a hide-all mask keeps hiding. The tone is the majority of
/// the mask's own perimeter, quantized to white or black, and is fixed per stable input rather
/// than recomputed per pixel.
/// </summary>
public class MaskBackgroundTests
{
    private static ProjectSnapshot Snapshot(int width, int height, params ProjectLayerRecord[] layers) =>
        new()
        {
            Manifest = new ProjectManifest
            {
                DocumentId = Guid.NewGuid(),
                Width = width,
                Height = height,
                Layers = [.. layers],
            },
            Images = new Dictionary<Guid, Surface>(),
            Masks = new Dictionary<Guid, MaskSurface>(),
        };

    private static ProjectLayerRecord Layer(string name, int x = 0, int y = 0, int width = 4, int height = 4)
    {
        var id = Guid.NewGuid();
        return new ProjectLayerRecord
        {
            Id = id,
            Name = name,
            IsVisible = true,
            ImageFile = ProjectValidator.Uuid(id) + ".png",
            Transform = new LayerTransform { Origin = new PointD(x, y), Size = new SizeD(width, height) },
        };
    }

    private static Surface Solid(int width, int height, byte r, byte g, byte b, byte a = 255)
    {
        var surface = new Surface(width, height);
        for (int row = 0; row < height; row++)
        {
            for (int column = 0; column < width; column++)
            {
                int index = row * surface.Stride + column * 4;
                surface.Pixels[index] = r;
                surface.Pixels[index + 1] = g;
                surface.Pixels[index + 2] = b;
                surface.Pixels[index + 3] = a;
            }
        }
        return surface;
    }

    private static byte Alpha(Surface surface, int x, int y) => surface.Pixels[y * surface.Stride + x * 4 + 3];

    /// <summary>An 8×8 mask whose perimeter is all <paramref name="edge"/> and whose inside is <paramref name="inside"/>.</summary>
    private static MaskSurface Edged(byte edge, byte inside = 0)
    {
        var mask = new MaskSurface(8, 8);
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                mask.Pixels[y * 8 + x] = x == 0 || y == 0 || x == 7 || y == 7 ? edge : inside;
            }
        }
        return mask;
    }

    /// <summary>A white layer on a 20×20 document with an 8×8 mask placed over (6,6)–(13,13).</summary>
    private static (ProjectSnapshot Snapshot, ProjectLayerRecord Layer) PlacedApart(Surface image, MaskSurface mask, int layerSize = 20)
    {
        var layer = Layer("masked", 0, 0, layerSize, layerSize);
        layer.MaskFile = ProjectValidator.Uuid(layer.Id) + ".mask.png";
        layer.MaskLinked = false;
        layer.MaskPlacement = new LayerTransform { Origin = new PointD(6, 6), Size = new SizeD(8, 8) };
        var snapshot = Snapshot(20, 20, layer);
        ((Dictionary<Guid, Surface>)snapshot.Images)[layer.Id] = image;
        ((Dictionary<Guid, MaskSurface>)snapshot.Masks)[layer.Id] = mask;
        return (snapshot, layer);
    }

    [Fact]
    public void RevealAllMaskPlacedApartKeepsRevealingBeyondItsPixels()
    {
        // The Mac contract: what lies outside the mask's rectangle is the perimeter majority —
        // white here — so the layer stays visible well beyond the mask's own 8×8 footprint.
        var (snapshot, _) = PlacedApart(Solid(20, 20, 255, 255, 255), Edged(255, 0));

        var canvas = new CanvasCompositor(snapshot).Render();

        Assert.Equal(255, Alpha(canvas, 0, 0));   // far outside: the white background reveals
        Assert.Equal(255, Alpha(canvas, 19, 19));
        Assert.Equal(0, Alpha(canvas, 10, 10));   // inside the mask's own pixels: the tone does not apply
    }

    [Fact]
    public void RevealAllMaskKeepsItsOwnDarkInterior()
    {
        // The tone only fills the area *outside* the mask's pixels; the mask's own values stand.
        var (snapshot, _) = PlacedApart(Solid(20, 20, 255, 255, 255), Edged(255, 0));

        var canvas = new CanvasCompositor(snapshot).Render();

        Assert.Equal(0, Alpha(canvas, 9, 9));     // (3,3) of the mask: still the hollow middle
        Assert.Equal(255, Alpha(canvas, 6, 6));   // (0,0) of the mask: the white edge
    }

    [Fact]
    public void HideAllMaskPlacedApartKeepsHidingBeyondItsPixels()
    {
        // The mirror case: a black perimeter hides outside the rectangle too, so a hide-all mask
        // cannot leak the layer back in around its own edges.
        var (snapshot, _) = PlacedApart(Solid(20, 20, 255, 255, 255), Edged(0, 255));

        var canvas = new CanvasCompositor(snapshot).Render();

        Assert.Equal(0, Alpha(canvas, 0, 0));
        Assert.Equal(0, Alpha(canvas, 19, 19));
        Assert.Equal(255, Alpha(canvas, 9, 9)); // the mask's own hollow middle still reveals
    }

    [Fact]
    public void MidGrayPerimeterQuantizesToTheLighterTone()
    {
        // `background` answers white when the perimeter total is at least half of full white —
        // a 128 gray perimeter therefore reads as white.
        var (snapshot, _) = PlacedApart(Solid(20, 20, 255, 255, 255), Edged(127, 0));
        var dark = PlacedApart(Solid(20, 20, 255, 255, 255), Edged(128, 0));

        Assert.Equal(0, Alpha(new CanvasCompositor(snapshot).Render(), 0, 0));
        Assert.Equal(255, Alpha(new CanvasCompositor(dark.Snapshot).Render(), 0, 0));
    }

    [Fact]
    public void LinkedMaskIgnoresBackgroundBecauseItCoversTheLayer()
    {
        // A mask linked to its layer (`placement` nil) is the layer's grid: nothing of the layer
        // lies outside it, so the edge tone must not paint there.
        var layer = Layer("masked", 0, 0, 8, 8);
        layer.MaskFile = ProjectValidator.Uuid(layer.Id) + ".mask.png";
        var snapshot = Snapshot(20, 20, layer);
        ((Dictionary<Guid, Surface>)snapshot.Images)[layer.Id] = Solid(8, 8, 255, 255, 255);
        ((Dictionary<Guid, MaskSurface>)snapshot.Masks)[layer.Id] = Edged(255, 0);

        var canvas = new CanvasCompositor(snapshot).Render();

        Assert.Equal(0, Alpha(canvas, 3, 3));   // (3,3) of the mask: the hollow middle hides
        Assert.Equal(255, Alpha(canvas, 0, 0)); // (0,0) of the mask: its white edge reveals
        Assert.Equal(0, Alpha(canvas, 15, 15)); // the layer has no pixels out here at all
    }

    [Fact]
    public void ImageAlphaOutsideItsGridStaysHidden()
    {
        // The image's own alpha keeps the zero-outside rule: only masks extend an edge tone.
        var source = Layer("clip", 0, 0, 4, 4);
        var target = Layer("target", 0, 0, 20, 20);
        target.MaskSourceId = source.Id;
        var snapshot = Snapshot(20, 20, source, target);
        ((Dictionary<Guid, Surface>)snapshot.Images)[source.Id] = Solid(4, 4, 255, 0, 0);
        ((Dictionary<Guid, Surface>)snapshot.Images)[target.Id] = Solid(20, 20, 0, 0, 255);

        var canvas = new CanvasCompositor(snapshot).Render();

        Assert.Equal(255, Alpha(canvas, 1, 1)); // inside the clip source
        Assert.Equal(0, Alpha(canvas, 15, 15)); // outside it: nothing appears, no edge tone
    }

    [Fact]
    public void FolderMaskPlacedApartCarriesItsEdgeToneToEverythingInside()
    {
        var folder = Layer("folder", 0, 0, 8, 8);
        folder.IsGroup = true;
        folder.MaskFile = ProjectValidator.Uuid(folder.Id) + ".mask.png";
        folder.MaskLinked = false;
        folder.MaskPlacement = new LayerTransform { Origin = new PointD(6, 6), Size = new SizeD(8, 8) };
        var child = Layer("child", 0, 0, 20, 20);
        child.ParentId = folder.Id;
        var snapshot = Snapshot(20, 20, folder, child);
        ((Dictionary<Guid, Surface>)snapshot.Images)[child.Id] = Solid(20, 20, 255, 255, 255);
        ((Dictionary<Guid, MaskSurface>)snapshot.Masks)[folder.Id] = Edged(255, 0);

        var canvas = new CanvasCompositor(snapshot).Render();

        Assert.Equal(255, Alpha(canvas, 0, 0)); // outside the folder mask's rectangle: revealed
        Assert.Equal(0, Alpha(canvas, 10, 10)); // inside: the mask's hollow middle still hides the child
    }

    [Fact]
    public void PlacedMaskOutsideToneSurvivesRegionalAndCachedRenders()
    {
        // A warm cache and a regional frame must agree with a fresh full composite; a
        // background-driven change repaints the whole layer, not just the brush tip.
        var (snapshot, layer) = PlacedApart(Solid(20, 20, 255, 255, 255), Edged(255, 0));
        var warm = new CanvasCompositor(snapshot);
        var full = warm.Render();
        Assert.Equal(255, Alpha(full, 0, 0));

        warm.Render(8, 8, 12, 12);
        var region = warm.Render(0, 0, 20, 20);
        Assert.Equal(255, Alpha(region, 0, 0));

        // Flipping the mask's perimeter turns the outside dark; the cached compositor must follow.
        var mask = ((Dictionary<Guid, MaskSurface>)snapshot.Masks)[layer.Id];
        for (int i = 0; i < mask.Pixels.Length; i++) mask.Pixels[i] = (byte)(255 - mask.Pixels[i]);
        mask.MarkModified();
        var after = warm.Render(0, 0, 20, 20);
        Assert.Equal(0, Alpha(after, 0, 0));
        Assert.Equal(255, Alpha(new CanvasCompositor(snapshot).Render(), 9, 9));
    }

    [Fact]
    public void BackgroundRepaintsTheWholeLayerWhenTheEdgeToneChanges()
    {
        var (snapshot, layer) = PlacedApart(Solid(20, 20, 255, 255, 255), Edged(255, 0));
        var compositor = new CanvasCompositor(snapshot);
        Assert.Equal(255, Alpha(compositor.Render(), 0, 0));

        var mask = ((Dictionary<Guid, MaskSurface>)snapshot.Masks)[layer.Id];
        for (int i = 0; i < mask.Pixels.Length; i++) mask.Pixels[i] = (byte)(255 - mask.Pixels[i]);
        mask.MarkModified();

        // The dirty band for the edited mask region alone would leave the far corners stale, so
        // a consumer asking about the change must be told the layer's whole footprint moved.
        var bounds = CanvasCompositor.ChangedLayerBounds(snapshot, snapshot, layer.Id);
        Assert.Equal(20, bounds.Width);
        Assert.Equal(20, bounds.Height);
    }

    [Fact]
    public void RotatedAndFlippedPlacementsUseTheSameOuterTone()
    {
        foreach (var placement in new[]
        {
            new LayerTransform { Origin = new PointD(6, 6), Size = new SizeD(8, 8), Rotation = 30 },
            new LayerTransform { Origin = new PointD(6, 6), Size = new SizeD(8, 8), FlipX = true },
            new LayerTransform { Origin = new PointD(6, 6), Size = new SizeD(8, 8), FlipY = true },
            new LayerTransform { Origin = new PointD(6, 6), Size = new SizeD(8, 8), Rotation = 405, FlipX = true, FlipY = true },
        })
        {
            var layer = Layer("masked", 0, 0, 20, 20);
            layer.MaskFile = ProjectValidator.Uuid(layer.Id) + ".mask.png";
            layer.MaskLinked = false;
            layer.MaskPlacement = placement;
            var snapshot = Snapshot(20, 20, layer);
            ((Dictionary<Guid, Surface>)snapshot.Images)[layer.Id] = Solid(20, 20, 255, 255, 255);
            ((Dictionary<Guid, MaskSurface>)snapshot.Masks)[layer.Id] = Edged(255, 0);

            var canvas = new CanvasCompositor(snapshot).Render();

            Assert.Equal(255, Alpha(canvas, 0, 0));
            Assert.Equal(255, Alpha(canvas, 19, 19));
        }
    }

    [Fact]
    public void DisabledMaskLeavesTheLayerWhole()
    {
        var (snapshot, layer) = PlacedApart(Solid(20, 20, 255, 255, 255), Edged(0, 255));
        layer.MaskEnabled = false;

        var canvas = new CanvasCompositor(snapshot).Render();

        Assert.Equal(255, Alpha(canvas, 0, 0));
        Assert.Equal(255, Alpha(canvas, 10, 10));
    }
}
