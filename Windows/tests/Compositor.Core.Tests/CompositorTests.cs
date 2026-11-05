using Xunit;
using System.Linq;

namespace Compositor.Core.Tests;

/// <summary>
/// Golden tests for the CPU compositor: small images with hand-computed expected bytes,
/// covering transforms, sampling, opacity, blend modes, masks, folders and clipping.
/// </summary>
public class CompositorTests
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

    private static ProjectLayerRecord Layer(string name, int x = 0, int y = 0, int? width = null, int? height = null)
    {
        var id = Guid.NewGuid();
        return new ProjectLayerRecord
        {
            Id = id,
            Name = name,
            IsVisible = true,
            ImageFile = ProjectValidator.Uuid(id) + ".png",
            Transform = new LayerTransform
            {
                Origin = new PointD(x, y),
                Size = new SizeD(width ?? 4, height ?? 4),
            },
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

    private static (ProjectSnapshot Snapshot, Guid LayerId) WithImage(ProjectSnapshot snapshot, ProjectLayerRecord layer, Surface image)
    {
        ((Dictionary<Guid, Surface>)snapshot.Images)[layer.Id] = image;
        return (snapshot, layer.Id);
    }

    private static byte Pixel(Surface surface, int x, int y, int channel) =>
        surface.Pixels[y * surface.Stride + x * 4 + channel];

    [Fact]
    public void IdentityTransformCopiesPixelsExactly()
    {
        var layer = Layer("A");
        var snapshot = Snapshot(4, 4, layer);
        WithImage(snapshot, layer, Solid(4, 4, 255, 128, 10));

        var canvas = new CanvasCompositor(snapshot).Render();

        for (int i = 0; i < 16; i++)
        {
            Assert.Equal(255, canvas.Pixels[i * 4]);
            Assert.Equal(128, canvas.Pixels[i * 4 + 1]);
            Assert.Equal(10, canvas.Pixels[i * 4 + 2]);
            Assert.Equal(255, canvas.Pixels[i * 4 + 3]);
        }
    }

    [Fact]
    public void HiddenLayersDoNotDraw()
    {
        var layer = Layer("A");
        layer.IsVisible = false;
        var snapshot = Snapshot(4, 4, layer);
        WithImage(snapshot, layer, Solid(4, 4, 255, 0, 0));

        var canvas = new CanvasCompositor(snapshot).Render();

        for (int i = 0; i < 16; i++) Assert.Equal(0, canvas.Pixels[i * 4 + 3]);
    }

    [Fact]
    public void LayersDrawBottomToTop()
    {
        var bottom = Layer("bottom");
        var top = Layer("top", width: 2, height: 4);
        var snapshot = Snapshot(4, 4, bottom, top);
        WithImage(snapshot, bottom, Solid(4, 4, 255, 0, 0));
        WithImage(snapshot, top, Solid(2, 4, 0, 0, 255));

        var canvas = new CanvasCompositor(snapshot).Render();

        Assert.Equal(0, Pixel(canvas, 0, 0, 0)); // blue on top of red
        Assert.Equal(255, Pixel(canvas, 0, 0, 2));
        Assert.Equal(255, Pixel(canvas, 3, 0, 0)); // red where nothing covers it
        Assert.Equal(0, Pixel(canvas, 3, 0, 2));
    }

    [Fact]
    public void PartialOpacityCompositesOver()
    {
        // 75% red over opaque grey 100: r = 255×0.75 + 100×0.25 = 216.25 → 216, g = b = 25.
        var backdrop = Layer("backdrop");
        var layer = Layer("red");
        layer.Opacity = 0.75;
        var snapshot = Snapshot(1, 1, backdrop, layer);
        WithImage(snapshot, backdrop, Solid(1, 1, 100, 100, 100));
        WithImage(snapshot, layer, Solid(1, 1, 255, 0, 0));

        var canvas = new CanvasCompositor(snapshot).Render();

        Assert.Equal(216, Pixel(canvas, 0, 0, 0));
        Assert.Equal(25, Pixel(canvas, 0, 0, 1));
        Assert.Equal(25, Pixel(canvas, 0, 0, 2));
        Assert.Equal(255, Pixel(canvas, 0, 0, 3));
    }

    [Fact]
    public void MultiplyBlendsOverColorAndShowsSourceOverTransparent()
    {
        // Over opaque 153 (0.6): multiply with 128 (0.50196) = 0.30118 → 76.8 → 77.
        var backdrop = Layer("backdrop");
        var layer = Layer("multiply");
        layer.BlendMode = LayerBlendMode.Multiply;
        var snapshot = Snapshot(2, 1, backdrop, layer);
        layer.Transform.Size = new SizeD(1, 1); // only over the left pixel
        WithImage(snapshot, backdrop, Solid(2, 1, 153, 153, 153));
        WithImage(snapshot, layer, Solid(1, 1, 128, 128, 128));

        var canvas = new CanvasCompositor(snapshot).Render();

        Assert.Equal(77, Pixel(canvas, 0, 0, 0)); // multiplied
        Assert.Equal(153, Pixel(canvas, 1, 0, 0)); // untouched backdrop
        Assert.Equal(153, Pixel(canvas, 1, 0, 1));

        // The same layer over transparency keeps its own color (the PDF rule CG follows).
        var overTransparent = Snapshot(1, 1, layer);
        WithImage(overTransparent, layer, Solid(1, 1, 128, 128, 128));
        var transparent = new CanvasCompositor(overTransparent).Render();
        Assert.Equal(128, Pixel(transparent, 0, 0, 0));
        Assert.Equal(255, Pixel(transparent, 0, 0, 3));
    }

    [Fact]
    public void ScreenLightens()
    {
        // Screen of 153 and 128: 0.6 + 0.50196 − 0.6×0.50196 = 0.80078 → 204.2 → 204.
        var backdrop = Layer("backdrop");
        var layer = Layer("screen");
        layer.BlendMode = LayerBlendMode.Screen;
        var snapshot = Snapshot(1, 1, backdrop, layer);
        WithImage(snapshot, backdrop, Solid(1, 1, 153, 153, 153));
        WithImage(snapshot, layer, Solid(1, 1, 128, 128, 128));

        var canvas = new CanvasCompositor(snapshot).Render();

        Assert.Equal(204, Pixel(canvas, 0, 0, 0));
        Assert.Equal(204, Pixel(canvas, 0, 0, 1));
    }

    [Fact]
    public void DifferenceSubtractsChannels()
    {
        // |200 − 100| = 100 on the red channel, |50 − 150| = 100 on green.
        var backdrop = Layer("backdrop");
        var layer = Layer("difference");
        layer.BlendMode = LayerBlendMode.Difference;
        var snapshot = Snapshot(1, 1, backdrop, layer);
        WithImage(snapshot, backdrop, Solid(1, 1, 200, 50, 0));
        WithImage(snapshot, layer, Solid(1, 1, 100, 150, 0));

        var canvas = new CanvasCompositor(snapshot).Render();

        Assert.Equal(100, Pixel(canvas, 0, 0, 0));
        Assert.Equal(100, Pixel(canvas, 0, 0, 1));
    }

    [Fact]
    public void LuminosityKeepsBackdropColor()
    {
        // A red source's luminance applied to a grey backdrop stays grey.
        var backdrop = Layer("backdrop");
        var layer = Layer("lum");
        layer.BlendMode = LayerBlendMode.Luminosity;
        var snapshot = Snapshot(1, 1, backdrop, layer);
        WithImage(snapshot, backdrop, Solid(1, 1, 100, 100, 100));
        WithImage(snapshot, layer, Solid(1, 1, 255, 0, 0));

        var canvas = new CanvasCompositor(snapshot).Render();

        Assert.Equal(77, Pixel(canvas, 0, 0, 0)); // 0.3×255 = 76.5, clipped back to grey
        Assert.Equal(77, Pixel(canvas, 0, 0, 1));
        Assert.Equal(77, Pixel(canvas, 0, 0, 2));
    }

    [Fact]
    public void RotationNinetyDegreesIsExact()
    {
        // A 2×2 image at 90° clockwise: [[A,B],[C,D]] lands as [[C,A],[D,B]].
        var layer = Layer("rot");
        layer.Transform.Size = new SizeD(2, 2);
        layer.Transform.Rotation = 90;
        var snapshot = Snapshot(2, 2, layer);
        var image = new Surface(2, 2);
        byte[,] source = { { 10, 20 }, { 30, 40 } };
        for (int y = 0; y < 2; y++)
        for (int x = 0; x < 2; x++)
        {
            image.Pixels[y * image.Stride + x * 4] = source[y, x];
            image.Pixels[y * image.Stride + x * 4 + 3] = 255;
        }
        WithImage(snapshot, layer, image);

        var canvas = new CanvasCompositor(snapshot).Render();

        Assert.Equal(30, Pixel(canvas, 0, 0, 0));
        Assert.Equal(10, Pixel(canvas, 1, 0, 0));
        Assert.Equal(40, Pixel(canvas, 0, 1, 0));
        Assert.Equal(20, Pixel(canvas, 1, 1, 0));
        for (int i = 0; i < 4; i++) Assert.Equal(255, canvas.Pixels[i * 4 + 3]);
    }

    [Fact]
    public void FlipsMirrorExactly()
    {
        var flipX = Layer("flipX");
        flipX.Transform.Size = new SizeD(2, 1);
        flipX.Transform.FlipX = true;
        var snapshot = Snapshot(2, 1, flipX);
        var image = Solid(2, 1, 0, 0, 0);
        image.Pixels[0] = 200; // left pixel red
        image.Pixels[4] = 20; // right pixel dim red
        WithImage(snapshot, flipX, image);

        var canvas = new CanvasCompositor(snapshot).Render();

        Assert.Equal(20, Pixel(canvas, 0, 0, 0));
        Assert.Equal(200, Pixel(canvas, 1, 0, 0));
    }

    [Fact]
    public void NearestSamplingDoublesPixels()
    {
        var layer = Layer("scaled");
        layer.Transform.Size = new SizeD(4, 1);
        layer.Transform.Sampling = LayerSampling.Nearest;
        var snapshot = Snapshot(4, 1, layer);
        var image = Solid(2, 1, 0, 0, 0);
        image.Pixels[0] = 100; // left half of the scaled layer
        image.Pixels[4] = 200;
        WithImage(snapshot, layer, image);

        var canvas = new CanvasCompositor(snapshot).Render();

        Assert.Equal(100, Pixel(canvas, 0, 0, 0));
        Assert.Equal(100, Pixel(canvas, 1, 0, 0));
        Assert.Equal(200, Pixel(canvas, 2, 0, 0));
        Assert.Equal(200, Pixel(canvas, 3, 0, 0));
    }

    [Fact]
    public void HalfOpacityFolderDoublesWithLayerOpacity()
    {
        // A layer at 50% in a folder at 50% shows at 25%: 255×0.25 + 100×0.75 = 138.75 → 139.
        var folder = Layer("folder");
        folder.IsGroup = true;
        folder.Opacity = 0.5;
        var child = Layer("child");
        child.ParentId = folder.Id;
        child.Opacity = 0.5;
        var backdrop = Layer("backdrop");
        // Bottom-to-top: backdrop, folder, child inside it.
        var snapshot = Snapshot(1, 1, backdrop, folder, child);
        WithImage(snapshot, backdrop, Solid(1, 1, 100, 100, 100));
        WithImage(snapshot, child, Solid(1, 1, 255, 0, 0));

        var canvas = new CanvasCompositor(snapshot).Render();

        Assert.Equal(139, Pixel(canvas, 0, 0, 0));
        Assert.Equal(75, Pixel(canvas, 0, 0, 1)); // 100×0.75
        Assert.Equal(255, Pixel(canvas, 0, 0, 3));
    }

    [Fact]
    public void HiddenFolderHidesItsChildren()
    {
        var folder = Layer("folder");
        folder.IsGroup = true;
        folder.IsVisible = false;
        var child = Layer("child");
        child.ParentId = folder.Id;
        var snapshot = Snapshot(1, 1, child);
        WithImage(snapshot, child, Solid(1, 1, 255, 0, 0));

        var order = new CanvasCompositor(snapshot).Order;

        Assert.Empty(order.Drawn);
    }

    [Fact]
    public void RasterMaskCoversTheLayer()
    {
        // A mask half white half black: the left column shows, the right hides.
        var backdrop = Layer("backdrop");
        var layer = Layer("masked");
        layer.Transform.Size = new SizeD(2, 1);
        layer.MaskFile = ProjectValidator.Uuid(layer.Id) + ".mask.png";
        var snapshot = Snapshot(2, 1, backdrop, layer);
        WithImage(snapshot, backdrop, Solid(2, 1, 10, 10, 10));
        WithImage(snapshot, layer, Solid(2, 1, 255, 255, 255));
        ((Dictionary<Guid, MaskSurface>)snapshot.Masks)[layer.Id] = new MaskSurface(2, 1, [255, 0]);

        var canvas = new CanvasCompositor(snapshot).Render();

        Assert.Equal(255, Pixel(canvas, 0, 0, 0)); // white mask: layer shows
        Assert.Equal(10, Pixel(canvas, 1, 0, 0)); // black mask: backdrop shows
    }

    [Fact]
    public void GrayMaskBlendsSoftly()
    {
        // Mask value 128 over an opaque grey-10 backdrop: layer at 128/255 coverage
        // gives r = 255×(128/255) + 10×(127/255) ≈ 133.
        var backdrop = Layer("backdrop");
        var layer = Layer("masked");
        layer.Transform.Size = new SizeD(1, 1);
        layer.MaskFile = ProjectValidator.Uuid(layer.Id) + ".mask.png";
        var snapshot = Snapshot(1, 1, backdrop, layer);
        WithImage(snapshot, backdrop, Solid(1, 1, 10, 10, 10));
        WithImage(snapshot, layer, Solid(1, 1, 255, 255, 255));
        ((Dictionary<Guid, MaskSurface>)snapshot.Masks)[layer.Id] = new MaskSurface(1, 1, [128]);

        var canvas = new CanvasCompositor(snapshot).Render();

        Assert.Equal(133, Pixel(canvas, 0, 0, 0));
        Assert.Equal(255, Pixel(canvas, 0, 0, 3));
    }

    [Fact]
    public void DisabledMaskChangesNothing()
    {
        var backdrop = Layer("backdrop");
        var layer = Layer("masked");
        layer.MaskFile = ProjectValidator.Uuid(layer.Id) + ".mask.png";
        layer.MaskEnabled = false;
        var snapshot = Snapshot(1, 1, backdrop, layer);
        WithImage(snapshot, backdrop, Solid(1, 1, 10, 10, 10));
        WithImage(snapshot, layer, Solid(1, 1, 255, 255, 255));
        ((Dictionary<Guid, MaskSurface>)snapshot.Masks)[layer.Id] = new MaskSurface(1, 1, [0]); // would hide everything

        var canvas = new CanvasCompositor(snapshot).Render();

        Assert.Equal(255, Pixel(canvas, 0, 0, 0));
    }

    [Fact]
    public void UnlinkedMaskKeepsItsOwnPlacement()
    {
        // A mask moved one pixel to the right: its all-white edge tone keeps the layer's left
        // pixel revealed, and the right pixel shows through the mask's own pixels.
        var backdrop = Layer("backdrop");
        var layer = Layer("masked");
        layer.Transform.Size = new SizeD(2, 1);
        layer.MaskFile = ProjectValidator.Uuid(layer.Id) + ".mask.png";
        layer.MaskLinked = false;
        layer.MaskPlacement = new LayerTransform
        {
            Origin = new PointD(1, 0),
            Size = new SizeD(2, 1),
        };
        var snapshot = Snapshot(3, 1, layer);
        WithImage(snapshot, layer, Solid(2, 1, 255, 255, 255));
        ((Dictionary<Guid, MaskSurface>)snapshot.Masks)[layer.Id] = new MaskSurface(2, 1, [255, 255]);

        var canvas = new CanvasCompositor(snapshot).Render();

        // `LayerMask.placed` fills the mask's edge tone beyond its pixels, so a reveal-all mask
        // placed apart keeps revealing; the outside-always-hidden convention this pinned was wrong.
        Assert.Equal(255, Pixel(canvas, 0, 0, 3)); // outside the mask on the left: the white tone reveals
        Assert.Equal(255, Pixel(canvas, 1, 0, 3)); // mask covers the layer's right pixel
        Assert.Equal(0, Pixel(canvas, 2, 0, 3)); // mask hangs off the layer: no pixels of the layer out here
    }

    [Fact]
    public void FolderMaskCoversEverythingInside()
    {
        // The folder's mask hides its right half, for every layer inside it.
        var folder = Layer("folder");
        folder.IsGroup = true;
        folder.Transform.Size = new SizeD(2, 1);
        folder.MaskFile = ProjectValidator.Uuid(folder.Id) + ".mask.png";
        var child = Layer("child");
        child.Transform.Size = new SizeD(2, 1);
        child.ParentId = folder.Id;
        var snapshot = Snapshot(2, 1, folder, child);
        WithImage(snapshot, child, Solid(2, 1, 255, 255, 255));
        ((Dictionary<Guid, MaskSurface>)snapshot.Masks)[folder.Id] = new MaskSurface(2, 1, [255, 0]);

        var canvas = new CanvasCompositor(snapshot).Render();

        Assert.Equal(255, Pixel(canvas, 0, 0, 3));
        Assert.Equal(0, Pixel(canvas, 1, 0, 3));
    }

    [Fact]
    public void ClippingMaskLimitsCoverageToItsSource()
    {
        // The target (blue, full canvas) is clipped by the red source (left half only):
        // blue shows only where the source has pixels; the source itself still draws.
        var source = Layer("source", width: 2, height: 4);
        var target = Layer("target");
        target.MaskSourceId = source.Id;
        var snapshot = Snapshot(4, 4, source, target);
        WithImage(snapshot, source, Solid(2, 4, 255, 0, 0));
        WithImage(snapshot, target, Solid(4, 4, 0, 0, 255));

        var canvas = new CanvasCompositor(snapshot).Render();

        Assert.Equal(0, Pixel(canvas, 0, 0, 0)); // blue over red inside the clip
        Assert.Equal(255, Pixel(canvas, 0, 0, 2));
        Assert.Equal(0, Pixel(canvas, 3, 0, 3)); // nothing outside it
    }

    [Fact]
    public void ClippedLayerInheritsSourceOpacity()
    {
        // A source at 50% opacity halves the clipped layer's coverage as well.
        var source = Layer("source", width: 1, height: 1);
        source.Opacity = 0.5;
        var target = Layer("target");
        target.MaskSourceId = source.Id;
        var snapshot = Snapshot(1, 1, source, target);
        WithImage(snapshot, source, Solid(1, 1, 255, 0, 0));
        WithImage(snapshot, target, Solid(1, 1, 0, 0, 255));

        var canvas = new CanvasCompositor(snapshot).Render();

        // Blue at half coverage over red at half coverage: r = 128×(1−0.502) ≈ 64, b = 128.
        Assert.Equal(64, Pixel(canvas, 0, 0, 0));
        Assert.Equal(128, Pixel(canvas, 0, 0, 2));
    }

    [Fact]
    public void OffCanvasLayersAreClippedToTheCanvas()
    {
        var layer = Layer("off");
        layer.Transform.Origin = new PointD(-2, -2);
        var snapshot = Snapshot(4, 4, layer);
        WithImage(snapshot, layer, Solid(4, 4, 255, 0, 0, 255));

        var canvas = new CanvasCompositor(snapshot).Render();

        // Only the bottom-right quadrant of the layer lands on the canvas.
        Assert.Equal(255, Pixel(canvas, 0, 0, 0));
        Assert.Equal(0, Pixel(canvas, 3, 3, 3));
    }

    [Fact]
    public void LargerThanCanvasLayersDrawClipped()
    {
        var layer = Layer("big");
        layer.Transform.Size = new SizeD(8, 8);
        var snapshot = Snapshot(4, 4, layer);
        WithImage(snapshot, layer, Solid(8, 8, 7, 8, 9));

        var canvas = new CanvasCompositor(snapshot).Render();

        for (int i = 0; i < 16; i++)
        {
            Assert.Equal(7, canvas.Pixels[i * 4]);
            Assert.Equal(255, canvas.Pixels[i * 4 + 3]);
        }
    }


public class CompositorCacheTests
{
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

    private static MaskSurface TopStripe(int width, int height, int rows)
    {
        var mask = new MaskSurface(width, height);
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < rows; y++) mask.Pixels[y * width + x] = 255;
        }
        return mask;
    }

    private static ProjectSnapshot ThreeLayerDocument()
    {
        var backdrop = Guid.NewGuid();
        var mover = Guid.NewGuid();
        var vignette = Guid.NewGuid();
        var curves = Guid.NewGuid();
        return new ProjectSnapshot
        {
            Manifest = new ProjectManifest
            {
                DocumentId = Guid.NewGuid(),
                Width = 16,
                Height = 16,
                Layers =
                [
                    new ProjectLayerRecord
                    {
                        Id = backdrop, Name = "Backdrop", IsVisible = true,
                        ImageFile = ProjectValidator.Uuid(backdrop) + ".png",
                        Transform = new LayerTransform { Origin = new PointD(0, 0), Size = new SizeD(16, 16) },
                    },
                    new ProjectLayerRecord
                    {
                        Id = mover, Name = "Mover", IsVisible = true,
                        ImageFile = ProjectValidator.Uuid(mover) + ".png",
                        Transform = new LayerTransform { Origin = new PointD(2, 2), Size = new SizeD(6, 6) },
                    },
                    new ProjectLayerRecord
                    {
                        Id = vignette, Name = "Vignette", IsVisible = true,
                        ImageFile = ProjectValidator.Uuid(vignette) + ".png",
                        MaskFile = ProjectValidator.Uuid(vignette) + ".mask.png",
                        BlendMode = LayerBlendMode.Multiply,
                        Transform = new LayerTransform { Origin = new PointD(0, 0), Size = new SizeD(16, 16) },
                    },
                    new ProjectLayerRecord
                    {
                        Id = curves, Name = "Grade", IsVisible = true,
                        Adjustment = new LayerAdjustment
                        {
                            Kind = AdjustmentKind.Curves,
                            Curves = new CurvesSettings
                            {
                                Channels =
                                [
                                    new() { new CurvePoint(0, 0), new CurvePoint(128, 180), new CurvePoint(255, 255) },
                                    new() { new CurvePoint(0, 0), new CurvePoint(255, 255) },
                                    new() { new CurvePoint(0, 0), new CurvePoint(255, 255) },
                                    new() { new CurvePoint(0, 0), new CurvePoint(255, 255) },
                                ],
                            },
                        },
                        Transform = new LayerTransform { Origin = new PointD(0, 0), Size = new SizeD(16, 16) },
                    },
                ],
            },
            Images = new Dictionary<Guid, Surface>
            {
                [backdrop] = Solid(16, 16, 90, 90, 90),
                [mover] = Solid(6, 6, 220, 40, 40),
                [vignette] = Solid(16, 16, 120, 120, 120),
            },
            Masks = new Dictionary<Guid, MaskSurface> { [vignette] = TopStripe(16, 16, 5) },
        };
    }

    [Fact]
    public void RegionRenderAfterAMoveMatchesAFullRender()
    {
        var snapshot = ThreeLayerDocument();
        var compositor = new CanvasCompositor(snapshot);
        _ = compositor.Render(); // warm the caches

        snapshot.Manifest.Layers[1].Transform.Origin = new PointD(8, 9);
        // The dirty band spans the old and the new placement, as the app tracks it.
        var viaRegion = compositor.Render(2, 2, 16, 16);

        var viaFull = new CanvasCompositor(snapshot).Render();

        for (int i = 0; i < 16 * 16; i++)
        {
            Assert.True(
                viaRegion.Pixels.AsSpan(i * 4, 4).ToArray().SequenceEqual(viaFull.Pixels.AsSpan(i * 4, 4).ToArray()),
                $"pixel ({i % 16}, {i / 16}) differs between region and full render");
        }
    }

    [Fact]
    public void MoveAndMoveBackReproducesTheOriginal()
    {
        var snapshot = ThreeLayerDocument();
        var compositor = new CanvasCompositor(snapshot);
        var original = compositor.Render().Clone();

        snapshot.Manifest.Layers[1].Transform.Origin = new PointD(9, 9);
        _ = compositor.Render(8, 8, 16, 16);
        snapshot.Manifest.Layers[1].Transform.Origin = new PointD(2, 2);
        var back = compositor.Render(0, 0, 16, 16);

        Assert.Equal(original.Pixels, back.Pixels);
    }

    [Fact]
    public void RegionRenderOfAMaskChangeMatchesAFullRender()
    {
        var snapshot = ThreeLayerDocument();
        var compositor = new CanvasCompositor(snapshot);
        _ = compositor.Render();

        var mask = (MaskSurface)snapshot.Masks.Values.First();
        for (int x = 0; x < mask.Width; x++) mask.Pixels[(mask.Height - 2) * mask.Width + x] = 255;
        mask.MarkModified(); // in-place edits bump the revision, as brush strokes do
        var viaRegion = compositor.Render(0, 13, 16, 16);

        var viaFull = new CanvasCompositor(snapshot).Render();
        for (int i = 0; i < 16 * 16; i++)
        {
            Assert.True(
                viaRegion.Pixels.AsSpan(i * 4, 4).ToArray().SequenceEqual(viaFull.Pixels.AsSpan(i * 4, 4).ToArray()),
                $"pixel ({i % 16}, {i / 16}) differs between region and full render");
        }
    }
}

    // --- Interactive content cache: moves must place cached content, not re-resample ---

    [Fact]
    public void InteractiveMovePlacesTheCachedLayerAtTheNewOrigin()
    {
        var layer = Layer("A", x: 0, y: 0, width: 8, height: 8);
        var snapshot = Snapshot(16, 16, layer);
        WithImage(snapshot, layer, Solid(8, 8, 255, 0, 0));

        // Interactive scale 0.5: the compositor canvas is 8×8, the placement (0..4, 0..4).
        var interactive = new CanvasCompositor(snapshot, scale: 0.5);
        interactive.Render(0, 0, interactive.Width, interactive.Height, interactive: true); // warms the content cache

        layer.Transform.Origin = new PointD(8, 8); // the drag tick's move → placement (4..8)
        var canvas = interactive.Render(0, 0, interactive.Width, interactive.Height, interactive: true);

        // The new placement shows the red layer…
        Assert.Equal(255, Pixel(canvas, 5, 5, 0));
        Assert.Equal(255, Pixel(canvas, 5, 5, 3));
        // …and the old placement is gone: the move cleared it from the canvas.
        Assert.Equal(0, Pixel(canvas, 1, 1, 3));
        Assert.Equal(0, Pixel(canvas, 2, 2, 3));
    }

    [Fact]
    public void InteractiveContentCacheInvalidatesOnPixelReplacement()
    {
        var layer = Layer("A");
        var snapshot = Snapshot(4, 4, layer);
        WithImage(snapshot, layer, Solid(4, 4, 255, 0, 0));

        var interactive = new CanvasCompositor(snapshot, scale: 0.5);
        interactive.Render(0, 0, 2, 2, interactive: true);

        // A brush stroke ends by replacing the layer's surface: new reference, new color.
        ((Dictionary<Guid, Surface>)snapshot.Images)[layer.Id] = Solid(4, 4, 0, 0, 255);
        var band = interactive.Render(0, 0, 2, 2, interactive: true);

        Assert.Equal(255, Pixel(band, 1, 1, 2));
        Assert.Equal(0, Pixel(band, 1, 1, 0));
    }

    [Fact]
    public void WarmInteractiveBandMatchesWarmInteractiveFull()
    {
        var first = Layer("A", x: 2, y: 2, width: 16, height: 16);
        var second = Layer("B", x: 8, y: 8, width: 20, height: 20);
        var snapshot = Snapshot(32, 32, first, second);
        WithImage(snapshot, first, Solid(16, 16, 255, 0, 0));
        WithImage(snapshot, second, Solid(20, 20, 0, 0, 255, a: 128));

        // Interactive scale 0.5 → a 16×16 canvas; warm both layers' content caches on bands first.
        var interactive = new CanvasCompositor(snapshot, scale: 0.5);
        interactive.Render(0, 0, 8, 8, interactive: true);
        interactive.Render(8, 8, 16, 16, interactive: true);

        var whole = interactive.Render(0, 0, interactive.Width, interactive.Height, interactive: true);
        var saved = whole.Pixels.ToArray(); // both renders return the same canvas surface
        interactive.Render(4, 4, 12, 12, interactive: true);
        // The band recomposition must agree pixel for pixel with the full recomposition.
        for (int y = 4; y < 12; y++)
        {
            for (int x = 4; x < 12; x++)
            {
                int index = y * whole.Stride + x * 4;
                Assert.Equal(saved[index..(index + 4)], whole.Pixels.AsSpan(index, 4).ToArray());
            }
        }
    }
}

/// <summary>
/// Full-resolution interactive placement: the drag frames must place from the content cache
/// at document scale (no draft downscale), and the settled frame — after the app drops the
/// caches — must match a fresh, exact, single-pass render pixel for pixel.
/// </summary>
public class InteractivePlacementTests
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

    private static ProjectLayerRecord Layer(string name, int x, int y, int width, int height)
    {
        var id = Guid.NewGuid();
        return new ProjectLayerRecord
        {
            Id = id,
            Name = name,
            IsVisible = true,
            ImageFile = ProjectValidator.Uuid(id) + ".png",
            Transform = new LayerTransform
            {
                Origin = new PointD(x, y),
                Size = new SizeD(width, height),
            },
        };
    }

    private static Surface Gradient(int width, int height)
    {
        var surface = new Surface(width, height);
        for (int row = 0; row < height; row++)
        {
            for (int column = 0; column < width; column++)
            {
                int index = row * surface.Stride + column * 4;
                surface.Pixels[index] = (byte)(column * 255 / Math.Max(1, width - 1));
                surface.Pixels[index + 1] = (byte)(row * 255 / Math.Max(1, height - 1));
                surface.Pixels[index + 2] = (byte)((column + row) % 256);
                surface.Pixels[index + 3] = (byte)(192 + (column * 63 / Math.Max(1, width - 1)));
            }
        }
        return surface;
    }

    private static void WithImage(ProjectSnapshot snapshot, ProjectLayerRecord layer, Surface image) =>
        ((Dictionary<Guid, Surface>)snapshot.Images)[layer.Id] = image;

    [Fact]
    public void InteractiveFullScaleMoveMatchesFreshFullRender()
    {
        // Two overlapping layers at document scale: the drag's interactive band must agree
        // with a compositor that renders the moved stack fresh — bit for bit.
        var under = Layer("Under", 2, 2, 24, 24);
        var over = Layer("Over", 4, 4, 16, 16);
        var snapshot = Snapshot(32, 32, under, over);
        WithImage(snapshot, under, Gradient(24, 24));
        WithImage(snapshot, over, Gradient(16, 16));

        var compositor = new CanvasCompositor(snapshot);
        _ = compositor.Render(); // warm the caches the way an app session would

        over.Transform.Origin = new PointD(13, 9); // the drag tick's move, integer document pixels
        var viaBand = compositor.Render(4, 4, 32, 32, interactive: true).Pixels.ToArray();

        var viaFull = new CanvasCompositor(snapshot).Render();
        Assert.Equal(viaFull.Pixels.ToArray(), viaBand);
    }

    [Fact]
    public void SettledRenderAfterDroppingCachesMatchesFreshFullRender()
    {
        // The app settles a drag by dropping the dragged layer's caches and re-rendering its
        // footprint exactly; that frame must equal a fresh single-pass composite.
        var under = Layer("Under", 2, 2, 24, 24);
        var over = Layer("Over", 4, 4, 16, 16);
        var snapshot = Snapshot(32, 32, under, over);
        WithImage(snapshot, under, Gradient(24, 24));
        WithImage(snapshot, over, Gradient(16, 16));

        var compositor = new CanvasCompositor(snapshot);
        _ = compositor.Render();
        over.Transform.Origin = new PointD(13, 9);
        _ = compositor.Render(4, 4, 32, 32, interactive: true); // the drag's cached placement

        compositor.DropCachesFor(over.Id); // the settle
        var settled = compositor.Render(13, 9, 29, 25).Pixels.ToArray(); // exact pass over the footprint

        var viaFull = new CanvasCompositor(snapshot).Render();
        Assert.Equal(viaFull.Pixels.ToArray(), settled);
    }

    [Fact]
    public void InteractiveMoveUnderAnAdjustmentStaysCurrent()
    {
        // A layer moves beneath an adjustment: the interactive frame must re-run the
        // adjustment over the changed rectangle, not show a frozen pre-move composite.
        var under = Layer("Under", 2, 2, 24, 24);
        var over = Layer("Over", 4, 4, 16, 16);
        var adjustment = new ProjectLayerRecord
        {
            Id = Guid.NewGuid(),
            Name = "Grade",
            IsVisible = true,
            Adjustment = new LayerAdjustment { Kind = AdjustmentKind.Invert },
            Transform = new LayerTransform { Origin = new PointD(0, 0), Size = new SizeD(32, 32) },
        };
        var snapshot = Snapshot(32, 32, under, over, adjustment);
        WithImage(snapshot, under, Gradient(24, 24));
        WithImage(snapshot, over, Gradient(16, 16));

        var compositor = new CanvasCompositor(snapshot);
        _ = compositor.Render();

        over.Transform.Origin = new PointD(13, 9);
        var viaBand = compositor.Render(4, 4, 32, 32, interactive: true).Pixels.ToArray();

        var viaFull = new CanvasCompositor(snapshot).Render();
        Assert.Equal(viaFull.Pixels.ToArray(), viaBand);
    }

    // --- Merge Layers: composite-as-seen into one trimmed pixel layer ---

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

    private static byte Pixel(Surface surface, int x, int y, int channel) =>
        surface.Pixels[y * surface.Stride + x * 4 + channel];

    [Fact]
    public void MergeLayersCompositesAndTrimsToContent()
    {
        var document = EditorDocument.Create(16, 16);
        document.ImportImage(Solid(8, 8, 255, 0, 0), "Red");
        var manifest = document.Snapshot.Manifest;
        var red = manifest.Layers.Single(l => l.Name == "Red");
        red.Transform.Origin = new PointD(4, 4);

        var merged = document.MergeLayers(new[] { red.Id });

        Assert.NotNull(merged);
        Assert.Equal(2, document.LayerCount); // background + merged
        Assert.Equal("Red", merged!.Name);
        Assert.Equal(new PointD(4, 4), merged.Transform.Origin);
        Assert.Equal(new SizeD(8, 8), merged.Transform.Size);

        // The merged pixels are the composited ones, byte for byte.
        var image = document.Snapshot.Images[merged.Id];
        // The expected pixels: the red layer exactly as it stood before the merge.
        var expectedLayer = new ProjectLayerRecord
        {
            Id = red.Id,
            Name = "Red",
            IsVisible = true,
            ImageFile = red.ImageFile,
            Transform = new LayerTransform { Origin = new PointD(4, 4), Size = new SizeD(8, 8) },
        };
        var expected = new CanvasCompositor(new ProjectSnapshot
        {
            Manifest = new ProjectManifest
            {
                DocumentId = manifest.DocumentId,
                Width = 16,
                Height = 16,
                Layers = [expectedLayer],
            },
            Images = new Dictionary<Guid, Surface> { [red.Id] = Solid(8, 8, 255, 0, 0) },
            Masks = new Dictionary<Guid, MaskSurface>(),
        }).Render(4, 4, 12, 12);
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                int index = y * image.Stride + x * 4;
                int expectedIndex = (y + 4) * expected.Stride + (x + 4) * 4;
                Assert.Equal(expected.Pixels.AsSpan(expectedIndex, 4).ToArray(),
                             image.Pixels.AsSpan(index, 4).ToArray());
            }
        }

        Assert.True(document.Undo());
        Assert.Equal(2, document.LayerCount);
        Assert.True(document.Redo());
        Assert.Equal(2, document.LayerCount);
    }

    [Fact]
    public void MergeLayersBakesAdjustmentsAndRemovesMergedLayers()
    {
        var document = EditorDocument.Create(16, 16);
        document.ImportImage(Solid(8, 8, 255, 0, 0), "Red");
        document.AddAdjustment(AdjustmentKind.Invert); // sits above Red, affects it
        var manifest = document.Snapshot.Manifest;
        var red = manifest.Layers.Single(l => l.Name == "Red");
        var invert = manifest.Layers.Single(l => l.Adjustment is not null);

        var merged = document.MergeLayers(new[] { red.Id, invert.Id });

        Assert.NotNull(merged);
        Assert.Equal(2, document.LayerCount); // background + merged (adjustment folded in)
        var image = document.Snapshot.Images[merged.Id];
        // Red 255 inverted → cyan-ish: red channel gone, blue/green full.
        Assert.Equal(0, Pixel(image, 4, 4, 0));
        Assert.Equal(255, Pixel(image, 4, 4, 1));
        Assert.Equal(255, Pixel(image, 4, 4, 2));
        Assert.DoesNotContain(document.Snapshot.Manifest.Layers, l => l.Adjustment is not null);
    }

    [Fact]
    public void MergedLayerReceivesClippingLayers()
    {
        var document = EditorDocument.Create(16, 16);
        document.ImportImage(Solid(8, 8, 255, 0, 0), "Red");
        document.ImportImage(Solid(8, 8, 0, 0, 255), "Blue");
        var manifest = document.Snapshot.Manifest;
        var red = manifest.Layers.Single(l => l.Name == "Red");
        var blue = manifest.Layers.Single(l => l.Name == "Blue");
        blue.MaskSourceId = red.Id; // Blue clips onto Red

        var merged = document.MergeLayers(new[] { red.Id });

        Assert.NotNull(merged);
        var clipping = document.Snapshot.Manifest.Layers.Single(l => l.Name == "Blue");
        Assert.Equal(merged!.Id, clipping.MaskSourceId);
    }

    // --- Merge Down / Merge Group (the Mac's other two ⌘E modes) ---

    [Fact]
    public void MergeDownTakesTheLowerLayerNameAndSlot()
    {
        var document = EditorDocument.Create(16, 16);
        document.ImportImage(Solid(8, 8, 255, 0, 0), "Red");
        document.ImportImage(Solid(8, 8, 0, 0, 255), "Blue");
        var manifest = document.Snapshot.Manifest;
        var red = manifest.Layers.Single(l => l.Name == "Red");
        var blue = manifest.Layers.Single(l => l.Name == "Blue");
        blue.Transform.Origin = new PointD(6, 6);

        var merged = document.MergeDown(blue.Id);

        Assert.NotNull(merged);
        Assert.Equal("Red", merged!.Name); // the lower layer's name wins
        var layers = document.Snapshot.Manifest.Layers;
        Assert.Equal(2, layers.Count); // background + merged
        Assert.DoesNotContain(layers, l => l.Id == blue.Id);
        Assert.DoesNotContain(layers, l => l.Id == red.Id);
        // The slot: the merged layer sits where Red sat (index 1, above Background).
        Assert.Equal(1, layers.IndexOf(layers.Single(l => l.Id == merged.Id)));

        // Pixels: both layers composited across the union.
        var image = document.Snapshot.Images[merged.Id];
        Assert.Equal(255, Pixel(image, 1, 1, 0)); // red-only region
        Assert.Equal(0, Pixel(image, 7, 7, 0));   // blue-overlap region
        Assert.Equal(255, Pixel(image, 7, 7, 2));

        Assert.True(document.Undo());
        Assert.Equal(3, document.LayerCount);
        Assert.True(document.Redo());
        Assert.Equal(2, document.LayerCount);
    }

    [Fact]
    public void MergeDownWithoutALowerLayerReturnsNull()
    {
        var document = EditorDocument.Create(16, 16);
        var background = document.Snapshot.Manifest.Layers[0];
        Assert.Null(document.MergeDown(background.Id));
    }

    [Fact]
    public void MergeGroupFoldsTheSubtreeAndRemovesTheFolder()
    {
        var document = EditorDocument.Create(16, 16);
        document.AddFolder();
        var folder = document.Snapshot.Manifest.Layers.Single(l => l.IsGroupOrFalse);
        var insideParent = folder.ParentId;
        document.ImportImage(Solid(8, 8, 255, 0, 0), "Inside");
        // Import lands above the active folder as a root layer; adopt it into the folder.
        var inside = document.Snapshot.Manifest.Layers.Single(l => l.Name == "Inside");
        document.Snapshot.Manifest.Layers.Single(l => l.Id == folder.Id);
        inside.ParentId = folder.Id;

        var merged = document.MergeGroup(folder.Id);

        Assert.NotNull(merged);
        var layers = document.Snapshot.Manifest.Layers;
        Assert.DoesNotContain(layers, l => l.Id == folder.Id);
        Assert.DoesNotContain(layers, l => l.Id == inside.Id);
        Assert.Equal(folder.Name, merged!.Name);
        Assert.True(merged.IsGroupOrFalse == false);
        Assert.Equal(insideParent, merged.ParentId);
        var image = document.Snapshot.Images[merged.Id];
        Assert.Equal(255, Pixel(image, 2, 2, 0));
    }

    [Fact]
    public void MergeGroupOnAPixelLayerReturnsNull()
    {
        var document = EditorDocument.Create(16, 16);
        document.ImportImage(Solid(8, 8, 255, 0, 0), "Red");
        var red = document.Snapshot.Manifest.Layers.Single(l => l.Name == "Red");

        Assert.Null(document.MergeGroup(red.Id));
    }

    // --- Image Size: canvas resample with layers and guides following ---

    [Fact]
    public void ResizeImageScalesLayersAndGuides()
    {
        var document = EditorDocument.Create(16, 16);
        document.ImportImage(Solid(8, 8, 255, 0, 0), "Red");
        var manifest = document.Snapshot.Manifest;
        var red = manifest.Layers.Single(l => l.Name == "Red");
        red.Transform.Origin = new PointD(4, 4);
        document.AddGuide(GuideAxis.Vertical, 8);

        Assert.True(document.ResizeImage(32, 32));

        var after = document.Snapshot.Manifest;
        Assert.Equal(32, after.Width);
        Assert.Equal(32, after.Height);
        var scaledRed = after.Layers.Single(l => l.Name == "Red");
        Assert.Equal(new PointD(8, 8), scaledRed.Transform.Origin);
        Assert.Equal(new SizeD(16, 16), scaledRed.Transform.Size);
        Assert.Equal(16, after.Guides!.Single().Position);

        // The resampled layer's pixels: doc (8..24, 8..24) is red, elsewhere background.
        var image = document.Snapshot.Images[scaledRed.Id];
        Assert.Equal(255, Pixel(image, 0, 0, 0));
        Assert.Equal(0, Pixel(image, 0, 0, 1));

        Assert.True(document.Undo());
        var restored = document.Snapshot.Manifest;
        Assert.Equal(16, restored.Width);
        Assert.Equal(8, restored.Layers.Single(l => l.Name == "Red").Transform.Size.Width);
        Assert.Equal(8, restored.Guides!.Single().Position);
    }

    [Fact]
    public void ResizeImageRejectsOverBudget()
    {
        var document = EditorDocument.Create(16, 16);
        int side = 30000; // beyond MaxSurfacePixels at this aspect
        Assert.False(document.ResizeImage(side, side));
        Assert.Equal(16, document.Snapshot.Manifest.Width);
    }

    [Fact]
    public void ResizeImageSameSizeIsANoOpThatKeepsDirtyUnchanged()
    {
        var document = EditorDocument.Create(16, 16);
        Assert.True(document.ResizeImage(16, 16));
        Assert.Equal(16, document.Snapshot.Manifest.Width);
    }

    // --- Select All / Inverse Selection ---

    [Fact]
    public void InvertedSelectionIsTheComplement()
    {
        var selection = Selection.Rectangle(8, 8, 2, 2, 4, 4);
        var inverted = selection.Inverted(8, 8);

        // Inside the original selection: now empty. Corners: now selected.
        Assert.Equal(0, inverted.CoverageAt(3, 3));
        Assert.Equal(255, inverted.CoverageAt(0, 0));
        Assert.Equal(255, inverted.CoverageAt(7, 7));
        Assert.Equal(255, inverted.CoverageAt(0, 7));
        Assert.Equal(255, inverted.CoverageAt(7, 0));
        // Bounds span the full document.
        Assert.Equal(new RectI(0, 0, 8, 8), inverted.Bounds);
    }

    [Fact]
    public void InvertingTwiceRestoresTheOriginal()
    {
        var selection = Selection.Rectangle(8, 8, 2, 2, 4, 4);
        var inverted = selection.Inverted(8, 8).Inverted(8, 8);
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                Assert.Equal(selection.CoverageAt(x, y), inverted.CoverageAt(x, y));
            }
        }
    }

    // --- Duplicate Layers / Move Out of Folder ---

    [Fact]
    public void DuplicateLayersClonesAboveOriginalAndSharesPixels()
    {
        var document = EditorDocument.Create(16, 16);
        document.ImportImage(Solid(8, 8, 255, 0, 0), "Red");
        var manifest = document.Snapshot.Manifest;
        var red = manifest.Layers.Single(l => l.Name == "Red");
        red.Transform.Origin = new PointD(4, 4);
        var originalImage = document.Snapshot.Images[red.Id];

        var copies = document.DuplicateLayers(new[] { red.Id });

        var copy = Assert.Single(copies);
        var layers = document.Snapshot.Manifest.Layers;
        Assert.Equal(3, layers.Count); // background, red, copy
        Assert.Equal(red.Id, layers[1].Id);
        Assert.Equal(copy.Id, layers[2].Id); // directly above the original
        Assert.Equal("Red copy", copy.Name);
        Assert.Equal(new PointD(4, 4), copy.Transform.Origin);
        // Pixels are shared by reference (immutable), so the copy renders identically.
        Assert.Same(originalImage, document.Snapshot.Images[copy.Id]);

        Assert.True(document.Undo());
        Assert.Equal(2, document.LayerCount);
    }

    [Fact]
    public void DuplicateFolderCarriesTheSubtree()
    {
        var document = EditorDocument.Create(16, 16);
        document.ImportImage(Solid(8, 8, 255, 0, 0), "Red");
        document.AddFolder();
        var manifest = document.Snapshot.Manifest;
        var folder = manifest.Layers.Single(l => l.IsGroupOrFalse);
        var red = manifest.Layers.Single(l => l.Name == "Red");
        red.ParentId = folder.Id;

        var copies = document.DuplicateLayers(new[] { folder.Id });

        // Duplicating a folder duplicates everything it holds: both copies come back.
        Assert.Equal(2, copies.Count);
        var copyFolder = copies.Single(l => l.IsGroupOrFalse);
        var copyChild = copies.Single(l => !l.IsGroupOrFalse);
        Assert.Equal(copyFolder.Id, copyChild.ParentId);
    }

    [Fact]
    public void MoveOutOfFolderLiftsTheLayer()
    {
        var document = EditorDocument.Create(16, 16);
        document.ImportImage(Solid(8, 8, 255, 0, 0), "Red");
        document.AddFolder();
        var manifest = document.Snapshot.Manifest;
        var folder = manifest.Layers.Single(l => l.IsGroupOrFalse);
        var red = manifest.Layers.Single(l => l.Name == "Red");
        red.ParentId = folder.Id;

        Assert.True(document.MoveOutOfFolder(red.Id));

        var moved = document.Snapshot.Manifest.Layers.Single(l => l.Name == "Red");
        Assert.Null(moved.ParentId);
        Assert.Contains(folder.Id, document.Snapshot.Manifest.Layers.Select(l => l.Id)); // folder stays

        Assert.True(document.Undo());
        Assert.Equal(folder.Id, document.Snapshot.Manifest.Layers.Single(l => l.Name == "Red").ParentId);
    }

    [Fact]
    public void MoveOutOfFolderOnARootLayerReturnsFalse()
    {
        var document = EditorDocument.Create(16, 16);
        var background = document.Snapshot.Manifest.Layers[0];

        Assert.False(document.MoveOutOfFolder(background.Id));
    }

    // --- Selection clipboard: copy / cut / paste ---

    [Fact]
    public void CopySelectionReturnsTheSelectedPixelsAtTheirOrigin()
    {
        var document = EditorDocument.Create(8, 8);
        document.ImportImage(Solid(6, 6, 255, 0, 0), "Red");
        var red = document.Snapshot.Manifest.Layers.Single(l => l.Name == "Red");
        red.Transform.Origin = new PointD(1, 1);
        document.SetActiveLayer(red.Id);
        document.SetSelection(Selection.Rectangle(8, 8, 2, 2, 4, 4));

        var copied = document.CopySelection(red.Id);

        Assert.NotNull(copied);
        Assert.Equal(4, copied!.Pixels.Width);
        Assert.Equal(new PointD(2, 2), copied.Origin);
        // Region pixel (0,0) is doc (2,2) = layer (1,1): red.
        Assert.Equal(255, Pixel(copied.Pixels, 0, 0, 0));
    }

    [Fact]
    public void CutSelectionClearsTheSelectedAreaOnly()
    {
        var document = EditorDocument.Create(8, 8);
        document.ImportImage(Solid(6, 6, 255, 0, 0), "Red");
        var red = document.Snapshot.Manifest.Layers.Single(l => l.Name == "Red");
        red.Transform.Origin = new PointD(1, 1);
        document.SetActiveLayer(red.Id);
        document.SetSelection(Selection.Rectangle(8, 8, 2, 2, 4, 4));

        Assert.NotNull(document.CutSelection(red.Id));

        var image = document.Snapshot.Images[red.Id];
        // Inside the selection: cleared. Outside: kept.
        Assert.Equal(0, Pixel(image, 2, 2, 3));
        Assert.Equal(0, Pixel(image, 4, 4, 3));
        Assert.Equal(255, Pixel(image, 0, 0, 3));
        Assert.Equal(255, Pixel(image, 5, 5, 3)); // doc (6,6), outside the selection
    }

    [Fact]
    public void PasteClipboardCreatesALayerAtTheCopiedOrigin()
    {
        var document = EditorDocument.Create(8, 8);
        document.ImportImage(Solid(6, 6, 255, 0, 0), "Red");
        var red = document.Snapshot.Manifest.Layers.Single(l => l.Name == "Red");
        red.Transform.Origin = new PointD(1, 1);
        document.SetActiveLayer(red.Id);
        document.SetSelection(Selection.Rectangle(8, 8, 2, 2, 4, 4));
        var copied = document.CopySelection(red.Id);
        Assert.NotNull(copied);
        var clipboardBytes = copied!.Pixels.Pixels.ToArray();
        Assert.NotNull(document.CutSelection(red.Id));

        var pasted = document.PasteClipboard();

        Assert.NotNull(pasted);
        Assert.Equal(new PointD(2, 2), pasted!.Transform.Origin);
        Assert.Equal(new SizeD(4, 4), pasted.Transform.Size);
        var pastedImage = document.Snapshot.Images[pasted.Id];
        Assert.True(clipboardBytes.SequenceEqual(pastedImage.Pixels),
            "clipboard: [" + string.Join(", ", clipboardBytes.Take(48)) + "] pasted: [" + string.Join(", ", pastedImage.Pixels.Take(48)) + "]");
    }

    [Fact]
    public void EachCopyGetsAFreshIdAndPasteExternalCenters()
    {
        var document = EditorDocument.Create(16, 16);

        var first = document.CopySelection(document.Snapshot.Manifest.Layers[0].Id);
        var second = document.CopySelection(document.Snapshot.Manifest.Layers[0].Id);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(Guid.Empty, first!.Id);
        Assert.NotEqual(first.Id, second!.Id);

        var pasted = document.PasteExternal(Solid(4, 4, 255, 0, 0));
        Assert.NotNull(pasted);
        Assert.Equal(new PointD(6, 6), pasted!.Transform.Origin); // (16 − 4) / 2
    }

    // --- Layer via Copy: the selection's pixels become a new layer in place ---

    [Fact]
    public void LayerViaCopyMakesALayerFromTheSelectionWhereItSits()
    {
        var document = EditorDocument.Create(16, 16);
        document.ImportImage(Solid(8, 8, 255, 0, 0), "Red");
        var red = document.Snapshot.Manifest.Layers.Single(l => l.Name == "Red");
        red.Transform.Origin = new PointD(4, 4);
        document.SetActiveLayer(red.Id);
        document.SetSelection(Selection.Rectangle(16, 16, 6, 6, 4, 4));

        // The pixels come from the same render Copy uses, without touching the pasteboard.
        var viaCopy = document.LayerViaCopy(red.Id);

        Assert.NotNull(viaCopy);
        Assert.Equal(new PointD(6, 6), viaCopy!.Transform.Origin);
        Assert.Equal(new SizeD(4, 4), viaCopy.Transform.Size);
        Assert.Equal(255, document.Snapshot.Images[viaCopy.Id].Pixels[0]); // red, premultiplied

        // The source layer is untouched, and one undo removes only the new layer.
        Assert.Equal(8, document.Snapshot.Images[red.Id].Width);
        Assert.True(document.Undo());
        Assert.Equal(2, document.LayerCount);
    }

    [Fact]
    public void LayerViaCopyWithoutASelectionDuplicatesTheLayer()
    {
        var document = EditorDocument.Create(16, 16);
        document.ImportImage(Solid(8, 8, 255, 0, 0), "Red");
        var red = document.Snapshot.Manifest.Layers.Single(l => l.Name == "Red");

        var copy = document.LayerViaCopy(red.Id);

        Assert.NotNull(copy);
        Assert.Equal("Red copy", copy!.Name);
        Assert.Equal(8, document.Snapshot.Images[copy.Id].Width);
        Assert.Equal(3, document.LayerCount); // Background + Red + the duplicate

        Assert.True(document.Undo());
        Assert.Equal(2, document.LayerCount);
    }

    [Fact]
    public void LayerViaCopyWithSelectionIsRefusedOnGroupsAndWithoutLayers()
    {
        var document = EditorDocument.Create(16, 16);
        document.ImportImage(Solid(4, 4, 255, 0, 0), "Red");
        var red = document.Snapshot.Manifest.Layers.Single(l => l.Name == "Red");
        document.GroupLayers([red.Id]);
        var group = document.Snapshot.Manifest.Layers.Single(l => l.IsGroupOrFalse);

        document.SetSelection(Selection.Rectangle(16, 16, 2, 2, 4, 4));
        Assert.Null(document.LayerViaCopy(group.Id));
        Assert.Null(document.LayerViaCopy(Guid.NewGuid()));
    }

    // --- Clipping mask: link to the lower sibling's base ---

    [Fact]
    public void ToggleClippingMaskLinksToTheLowerSiblingAndReleases()
    {
        var document = EditorDocument.Create(16, 16);
        document.ImportImage(Solid(8, 8, 255, 0, 0), "A");
        document.ImportImage(Solid(8, 8, 0, 0, 255), "B");
        var manifest = document.Snapshot.Manifest;
        var a = manifest.Layers.Single(l => l.Name == "A");
        var b = manifest.Layers.Single(l => l.Name == "B");

        Assert.True(document.CanToggleClippingMask(b.Id));
        Assert.True(document.ToggleClippingMask(b.Id));
        Assert.Equal(a.Id, document.Snapshot.Manifest.Layers.Single(l => l.Name == "B").MaskSourceId);

        // One undo removes the link; toggling again releases it the same way.
        Assert.True(document.Undo());
        Assert.Null(document.Snapshot.Manifest.Layers.Single(l => l.Name == "B").MaskSourceId);
        Assert.True(document.ToggleClippingMask(b.Id));
        Assert.Equal(a.Id, document.Snapshot.Manifest.Layers.Single(l => l.Name == "B").MaskSourceId);
        Assert.True(document.ToggleClippingMask(b.Id));
        Assert.Null(document.Snapshot.Manifest.Layers.Single(l => l.Name == "B").MaskSourceId);
    }

    [Fact]
    public void ClippingMaskFollowsTheLowerSiblingsOwnBase()
    {
        var document = EditorDocument.Create(16, 16);
        document.ImportImage(Solid(8, 8, 255, 0, 0), "A");
        document.ImportImage(Solid(8, 8, 0, 255, 0), "B");
        document.ImportImage(Solid(8, 8, 0, 0, 255), "C");
        var a = document.Snapshot.Manifest.Layers.Single(l => l.Name == "A");
        var b = document.Snapshot.Manifest.Layers.Single(l => l.Name == "B");
        var c = document.Snapshot.Manifest.Layers.Single(l => l.Name == "C");

        // B clips to A; C above it then clips to A too — B's own base, not B.
        Assert.True(document.ToggleClippingMask(b.Id));
        document.ToggleClippingMask(c.Id);

        Assert.Equal(a.Id, document.Snapshot.Manifest.Layers.Single(l => l.Name == "B").MaskSourceId);
        Assert.Equal(a.Id, document.Snapshot.Manifest.Layers.Single(l => l.Name == "C").MaskSourceId);
    }

    [Fact]
    public void ReleasingAMidChainTakesTheContiguousClippedSiblingsAbove()
    {
        var document = EditorDocument.Create(16, 16);
        document.ImportImage(Solid(8, 8, 255, 0, 0), "A");
        document.ImportImage(Solid(8, 8, 0, 255, 0), "B");
        document.ImportImage(Solid(8, 8, 0, 0, 255), "C");
        var b = document.Snapshot.Manifest.Layers.Single(l => l.Name == "B");
        var c = document.Snapshot.Manifest.Layers.Single(l => l.Name == "C");
        document.ToggleClippingMask(b.Id);
        document.ToggleClippingMask(c.Id);
        var source = document.Snapshot.Manifest.Layers.Single(l => l.Name == "B").MaskSourceId;

        // Releasing B releases C too — same source, contiguous above.
        Assert.True(document.ToggleClippingMask(b.Id));
        Assert.Null(document.Snapshot.Manifest.Layers.Single(l => l.Name == "B").MaskSourceId);
        Assert.Null(document.Snapshot.Manifest.Layers.Single(l => l.Name == "C").MaskSourceId);

        Assert.True(document.Undo());
        Assert.Equal(source, document.Snapshot.Manifest.Layers.Single(l => l.Name == "B").MaskSourceId);
        Assert.Equal(source, document.Snapshot.Manifest.Layers.Single(l => l.Name == "C").MaskSourceId);
    }

    [Fact]
    public void ClippingMaskIsRefusedAtTheBottomAndUnderGroups()
    {
        var document = EditorDocument.Create(16, 16);
        document.ImportImage(Solid(8, 8, 255, 0, 0), "A");
        var background = document.Snapshot.Manifest.Layers[0];

        // The bottom layer has nothing below to clip to.
        Assert.False(document.CanToggleClippingMask(background.Id));
        Assert.False(document.ToggleClippingMask(background.Id));

        // A group below cannot be a base.
        document.ImportImage(Solid(4, 4, 0, 0, 255), "Red");
        var red = document.Snapshot.Manifest.Layers.Single(l => l.Name == "Red");
        document.GroupLayers([red.Id]);
        var group = document.Snapshot.Manifest.Layers.Single(l => l.IsGroupOrFalse);
        document.ImportImage(Solid(4, 4, 255, 255, 0), "Top");
        var top = document.Snapshot.Manifest.Layers.Single(l => l.Name == "Top");

        Assert.False(document.CanToggleClippingMask(top.Id));
        Assert.False(document.ToggleClippingMask(top.Id));
    }

    // --- Magic Wand: flood-match a sampled color ---

    [Fact]
    public void MagicWandSelectsTheContiguousMatchingRun()
    {
        var document = EditorDocument.Create(16, 16);
        document.ImportImage(Solid(8, 8, 0, 0, 255), "Blue");
        var blue = document.Snapshot.Manifest.Layers.Single(l => l.Name == "Blue");
        document.SetActiveLayer(blue.Id);

        var selection = document.MagicWandSelect(new PointD(4, 4), new WandSettings());

        Assert.NotNull(selection);
        Assert.Equal(255, selection!.CoverageAt(4, 4));
        Assert.Equal(255, selection.CoverageAt(7, 7)); // the whole 8×8 block
        Assert.Equal(0, selection.CoverageAt(8, 8)); // the white canvas around it
    }

    [Fact]
    public void MagicWandToleranceAndContiguityBoundTheSelection()
    {
        var document = EditorDocument.Create(16, 16);
        // A blue layer whose right half is slightly off: within tolerance 8, outside 2.
        var image = Solid(8, 8, 0, 0, 250);
        for (int y = 0; y < 8; y++)
        {
            for (int x = 4; x < 8; x++)
            {
                int index = y * image.Stride + x * 4 + 2;
                image.Pixels[index] = 240;
            }
        }
        document.ImportImage(image, "Blue");
        var blue = document.Snapshot.Manifest.Layers.Single(l => l.Name == "Blue");
        document.SetActiveLayer(blue.Id);

        var tight = document.MagicWandSelect(new PointD(2, 2), new WandSettings { Tolerance = 2 });
        Assert.NotNull(tight);
        Assert.Equal(255, tight!.CoverageAt(2, 2));
        Assert.Equal(0, tight.CoverageAt(5, 2)); // 250 vs 240 differs by 10 > 2

        var loose = document.MagicWandSelect(new PointD(2, 2), new WandSettings { Tolerance = 10 });
        Assert.NotNull(loose);
        Assert.Equal(255, loose!.CoverageAt(5, 2)); // within 10

        // Non-contiguous mode selects the off-color half even though it is not reachable
        // through matching neighbors from the seed.
        var split = document.MagicWandSelect(new PointD(0, 0), new WandSettings { Tolerance = 10, Contiguous = false });
        Assert.NotNull(split);
        Assert.Equal(255, split!.CoverageAt(7, 7));
    }

    [Fact]
    public void MagicWandSamplesTheBoxAroundTheClick()
    {
        var document = EditorDocument.Create(16, 16);
        // Left half blue, right half red: a 3×3 sample centered one pixel left of the
        // boundary averages blue and red into purple.
        var image = new Surface(8, 8);
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                int index = y * image.Stride + x * 4;
                bool red = x >= 4;
                image.Pixels[index] = red ? (byte)200 : (byte)0;
                image.Pixels[index + 2] = red ? (byte)0 : (byte)200;
                image.Pixels[index + 3] = 255;
            }
        }
        document.ImportImage(image, "Split");
        var split = document.Snapshot.Manifest.Layers.Single(l => l.Name == "Split");
        document.SetActiveLayer(split.Id);

        // Point sample on pure blue: tolerance 60 keeps only the blue half.
        var blue = document.MagicWandSelect(new PointD(1, 1), new WandSettings { Tolerance = 60 });
        Assert.NotNull(blue);
        Assert.Equal(255, blue!.CoverageAt(1, 1));
        Assert.Equal(0, blue.CoverageAt(6, 1));

        // 3×3 sample straddling the boundary averages to purple — 67 off blue, 133 off red.
        // Tolerance 140 takes both halves but not the white canvas (255 off in green).
        var purple = document.MagicWandSelect(new PointD(3, 1), new WandSettings { Tolerance = 140, SampleRadius = 1 });
        Assert.NotNull(purple);
        Assert.Equal(255, purple!.CoverageAt(1, 1));
        Assert.Equal(255, purple.CoverageAt(6, 1));
        Assert.Equal(0, purple.CoverageAt(15, 15));
    }

    [Fact]
    public void MagicWandOnTheCompositeAndEmptyMatches()
    {
        var document = EditorDocument.Create(16, 16);
        document.ImportImage(Solid(8, 8, 0, 0, 255), "Blue");
        var blue = document.Snapshot.Manifest.Layers.Single(l => l.Name == "Blue");
        document.SetActiveLayer(blue.Id);

        // Sampling all layers reads the composite: the blue over white canvas.
        var composite = document.MagicWandSelect(new PointD(4, 4), new WandSettings { SampleAllLayers = true, Tolerance = 40 });
        Assert.NotNull(composite);
        Assert.Equal(255, composite!.CoverageAt(4, 4));
        Assert.Equal(0, composite.CoverageAt(12, 12)); // pure white differs from blue-on-white

        // Nothing matches: null, so a replace-mode caller clears the selection.
        var none = document.MagicWandSelect(new PointD(-1, 4), new WandSettings());
        Assert.Null(none);
    }

    // --- Color Range: every pixel near the picked colors, over the composite ---

    [Fact]
    public void ColorRangeSelectsNearColorsOverTheComposite()
    {
        var document = EditorDocument.Create(16, 16);
        document.ImportImage(Solid(8, 8, 200, 30, 30), "Red");

        // Composite: red block over white canvas. Include red at fuzziness 40.
        var selection = document.ColorRangeSelect([(byte)195, (byte)35, (byte)35], [], 40, invert: false);

        Assert.NotNull(selection);
        Assert.Equal(255, selection!.CoverageAt(4, 4)); // red block
        Assert.Equal(0, selection.CoverageAt(12, 12)); // white canvas
    }

    [Fact]
    public void ColorRangeExcludeAndInvertFlipTheMask()
    {
        var document = EditorDocument.Create(16, 16);
        document.ImportImage(Solid(8, 8, 200, 30, 30), "Red");
        document.ImportImage(Solid(4, 4, 30, 30, 200), "Blue");
        var blue = document.Snapshot.Manifest.Layers.Single(l => l.Name == "Blue");
        blue.Transform.Origin = new PointD(6, 6);

        // Red included, blue excluded: the blue block sitting on the red one drops out.
        var selection = document.ColorRangeSelect([(byte)195, (byte)35, (byte)35], [(byte)30, (byte)30, (byte)195], 40, invert: false);
        Assert.NotNull(selection);
        Assert.Equal(255, selection!.CoverageAt(2, 2));
        Assert.Equal(0, selection.CoverageAt(8, 8)); // excluded blue

        // Inverted: everything else, blue block included, red block out.
        var inverted = document.ColorRangeSelect([(byte)195, (byte)35, (byte)35], [], 40, invert: true);
        Assert.NotNull(inverted);
        Assert.Equal(0, inverted!.CoverageAt(4, 4));
        Assert.Equal(255, inverted.CoverageAt(12, 12));
    }

    [Fact]
    public void ColorRangeUnpremultipliesBeforeMatching()
    {
        var document = EditorDocument.Create(16, 16);
        document.ImportImage(Solid(8, 8, 100, 15, 15, a: 128), "Dim");
        var dim = document.Snapshot.Manifest.Layers.Single(l => l.Name == "Dim");
        document.SetActiveLayer(dim.Id);
        // Drop the white background so the composite keeps the layer's half alpha.
        Assert.True(document.DeleteLayer(document.Snapshot.Manifest.Layers[0].Id));

        var selection = document.ColorRangeSelect([(byte)199, (byte)30, (byte)30], [], 20, invert: false);

        Assert.NotNull(selection);
        Assert.Equal(255, selection!.CoverageAt(4, 4));
    }

    [Fact]
    public void ColorRangeWithoutColorsIsNull()
    {
        var document = EditorDocument.Create(16, 16);
        Assert.Null(document.ColorRangeSelect([], [], 40, invert: false));
    }

    [Fact]
    public void ColorRangePreviewFitsTheBoxAndKeepsTheMask()
    {
        // 300×200 source: red block on the left, blue on the right, fuzziness picks both.
        var image = new Surface(300, 200);
        for (int y = 0; y < 200; y++)
        {
            for (int x = 0; x < 300; x++)
            {
                int index = y * image.Stride + x * 4;
                bool red = x < 150;
                image.Pixels[index] = red ? (byte)200 : (byte)30;
                image.Pixels[index + 1] = (byte)30;
                image.Pixels[index + 2] = red ? (byte)30 : (byte)200;
                image.Pixels[index + 3] = 255;
            }
        }

        var preview = WandPixels.ColorRangePreview(image, [(byte)195, (byte)35, (byte)35], [], 40, invert: false, 292, 200);

        Assert.True(preview.Width <= 292 && preview.Height <= 200);
        Assert.Equal(292, preview.Width);
        // Nearest neighbor keeps the red/blue split: only the red side matches the include color.
        Assert.Equal(255, preview.Pixels[100 * preview.Width + 100]);
        Assert.Equal(0, preview.Pixels[100 * preview.Width + preview.Width - 100]);
    }

    [Fact]
    public void ColorRangeSampleReadsCompositeColorsAtPoints()
    {
        var document = EditorDocument.Create(16, 16);
        document.ImportImage(Solid(8, 8, 0, 0, 255), "Blue");
        var sample = document.ColorRangeSample();

        // The blue block over the white canvas: both colors read back where they sit.
        Assert.Equal(((byte)0, (byte)0, (byte)255, (byte)255), EditorDocument.ColorAt(sample, new PointD(4, 4)));
        Assert.Equal(((byte)255, (byte)255, (byte)255, (byte)255), EditorDocument.ColorAt(sample, new PointD(12, 12)));
        Assert.Null(EditorDocument.ColorAt(sample, new PointD(-1, 4)));
        Assert.Null(EditorDocument.ColorAt(sample, new PointD(16, 16)));
    }

    // --- Image > Trim: cut the canvas down to its content ---

    [Fact]
    public void TrimCanvasCutsToTheTransparentBounds()
    {
        var document = EditorDocument.Create(16, 16);
        document.ImportImage(Solid(8, 8, 0, 0, 255), "Blue");
        var blue = document.Snapshot.Manifest.Layers.Single(l => l.Name == "Blue");
        blue.Transform.Origin = new PointD(4, 4);
        // The white Background is opaque: drop it so the composite has real transparency.
        Assert.True(document.DeleteLayer(document.Snapshot.Manifest.Layers[0].Id));

        Assert.True(document.TrimCanvas(new EditorDocument.TrimOptions()));

        Assert.Equal(8, document.Snapshot.Manifest.Width);
        Assert.Equal(8, document.Snapshot.Manifest.Height);
        // The blue layer shifted with the canvas: (4,4) → (0,0).
        Assert.Equal(new PointD(0, 0), document.Snapshot.Manifest.Layers.Single(l => l.Name == "Blue").Transform.Origin);

        Assert.True(document.Undo());
        Assert.Equal(16, document.Snapshot.Manifest.Width);
    }

    [Fact]
    public void TrimCanvasKeepsTheEdgesThatAreSwitchedOff()
    {
        var document = EditorDocument.Create(16, 16);
        document.ImportImage(Solid(8, 8, 0, 0, 255), "Blue");
        var blue = document.Snapshot.Manifest.Layers.Single(l => l.Name == "Blue");
        blue.Transform.Origin = new PointD(4, 4);
        Assert.True(document.DeleteLayer(document.Snapshot.Manifest.Layers[0].Id));

        // Left and top stay: the rectangle only shrinks from the right and bottom.
        Assert.True(document.TrimCanvas(new EditorDocument.TrimOptions { Left = false, Top = false }));

        Assert.Equal(12, document.Snapshot.Manifest.Width);
        Assert.Equal(12, document.Snapshot.Manifest.Height);
        Assert.Equal(new PointD(4, 4), document.Snapshot.Manifest.Layers.Single(l => l.Name == "Blue").Transform.Origin);
    }

    [Fact]
    public void TrimCanvasMatchesTheCornerColorWithinTolerance()
    {
        // A white canvas with a near-white border (250) around a colored center.
        var document = EditorDocument.Create(16, 16);
        var image = new Surface(16, 16);
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                int index = y * image.Stride + x * 4;
                bool border = x is < 3 or >= 13 || y is < 3 or >= 13;
                image.Pixels[index] = border ? (byte)250 : (byte)40;
                image.Pixels[index + 1] = border ? (byte)250 : (byte)120;
                image.Pixels[index + 2] = border ? (byte)250 : (byte)200;
                image.Pixels[index + 3] = 255;
            }
        }
        document.ImportImage(image, "Dim");

        Assert.True(document.TrimCanvas(new EditorDocument.TrimOptions { BasedOn = EditorDocument.TrimBasedOn.TopLeftColor, Tolerance = 6 }));

        Assert.Equal(10, document.Snapshot.Manifest.Width);
        Assert.Equal(10, document.Snapshot.Manifest.Height);
    }

    [Fact]
    public void TrimCanvasRefusesWhenEverythingMatches()
    {
        var document = EditorDocument.Create(16, 16); // plain white canvas

        // The corner color is the whole canvas: nothing to keep.
        Assert.False(document.TrimCanvas(new EditorDocument.TrimOptions { BasedOn = EditorDocument.TrimBasedOn.TopLeftColor }));
        Assert.Equal(16, document.Snapshot.Manifest.Width);
    }

    // --- Auto Levels: endpoints from the 0.1% histogram tails ---

    private static double[][] HistogramOf(params (int Low, int High)[] channels)
    {
        var histogram = new double[4][];
        histogram[0] = new double[256];
        for (int c = 0; c < channels.Length; c++)
        {
            histogram[c + 1] = new double[256];
            histogram[c + 1][channels[c].Low] = 1;
            histogram[c + 1][channels[c].High] = 1;
            for (int i = 0; i < 256; i++) histogram[0][i] += histogram[c + 1][i] / 3.0;
        }
        return histogram;
    }

    [Fact]
    public void AutoLevelsContrastSharesOneIntervalAcrossChannels()
    {
        var histogram = HistogramOf((60, 180), (30, 220), (50, 200));

        var settings = LevelsAutomatic.Settings(histogram, LevelsAutoMode.Contrast);

        Assert.Equal(30, settings.Ranges[0].Black);
        Assert.Equal(220, settings.Ranges[0].White); // the widest span wins
        Assert.Equal(1, settings.Ranges[0].Gamma);
        Assert.Equal(new LevelRange(), settings.Ranges[1]); // per-channel ranges untouched
    }

    [Fact]
    public void AutoLevelsColorSetsPerChannelEndpoints()
    {
        var histogram = HistogramOf((60, 180), (30, 220), (50, 200));

        var settings = LevelsAutomatic.Settings(histogram, LevelsAutoMode.Color);

        Assert.Equal(60, settings.Ranges[1].Black);
        Assert.Equal(180, settings.Ranges[1].White);
        Assert.Equal(30, settings.Ranges[2].Black);
        Assert.Equal(220, settings.Ranges[2].White);
        Assert.Equal(50, settings.Ranges[3].Black);
        Assert.Equal(200, settings.Ranges[3].White);
    }

    [Fact]
    public void AutoLevelsNeutralKeepsAGrayImageNeutral()
    {
        // A full gray ramp centered at mid: mean = 0.5 -> gamma = 1, the image stays neutral.
        var histogram = new double[4][];
        for (int row = 0; row < 4; row++)
        {
            histogram[row] = new double[256];
            for (int i = 0; i < 256; i++) histogram[row][i] = 1;
        }

        var settings = LevelsAutomatic.Settings(histogram, LevelsAutoMode.Neutral);

        Assert.Equal(0, settings.Ranges[1].Black);
        Assert.Equal(255, settings.Ranges[1].White);
        Assert.InRange(settings.Ranges[1].Gamma, 0.9, 1.1);
    }

    [Fact]
    public void AutoLevelsAppliesToALevelsAdjustmentAndUndoRestores()
    {
        var document = EditorDocument.Create(16, 16);
        var image = new Surface(8, 8);
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                int index = y * image.Stride + x * 4;
                byte gray = x < 4 ? (byte)60 : (byte)90;
                image.Pixels[index] = gray;
                image.Pixels[index + 1] = gray;
                image.Pixels[index + 2] = gray;
                image.Pixels[index + 3] = 255;
            }
        }
        document.ImportImage(image, "Dim");
        var dim = document.Snapshot.Manifest.Layers.Single(l => l.Name == "Dim");
        document.SetActiveLayer(dim.Id);
        document.DeleteLayer(document.Snapshot.Manifest.Layers[0].Id); // the opaque white canvas hides the endpoints
        var adjustment = document.AddAdjustment(AdjustmentKind.Levels);

        Assert.True(document.AutoLevels(adjustment.Id, LevelsAutoMode.Contrast));
        var ranges = document.Snapshot.Manifest.Layers.Single(l => l.Id == adjustment.Id).Adjustment!.Levels.Ranges;
        Assert.InRange(ranges[0].Black, 59, 61); // the two-tone gray's own endpoints
        Assert.InRange(ranges[0].White, 89, 91);

        Assert.True(document.Undo());
        Assert.Equal(255, document.Snapshot.Manifest.Layers.Single(l => l.Id == adjustment.Id).Adjustment!.Levels.Ranges[0].White);
    }

    [Fact]
    public void AutoLevelsIsRefusedOnNonLevelsAdjustments()
    {
        var document = EditorDocument.Create(16, 16);
        var adjustment = document.AddAdjustment(AdjustmentKind.Exposure);

        Assert.False(document.AutoLevels(adjustment.Id, LevelsAutoMode.Contrast));
    }
    // --- Blur tool: blurred-sample stamps through the brush disc ---

    [Fact]
    public void BlurStampBlendsTowardTheBlurredSample()
    {
        var document = EditorDocument.Create(16, 16);
        // Left half bright 200, right half dark 40: the boundary blurs when stamped.
        var image = new Surface(16, 16);
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                int index = y * image.Stride + x * 4;
                byte v = x < 8 ? (byte)200 : (byte)40;
                image.Pixels[index] = v;
                image.Pixels[index + 1] = v;
                image.Pixels[index + 2] = v;
                image.Pixels[index + 3] = 255;
            }
        }
        document.ImportImage(image, "Split");
        var layer = document.Snapshot.Manifest.Layers.Single(l => l.Name == "Split");
        document.SetActiveLayer(layer.Id);

        Assert.True(document.BeginBlurStroke(layer.Id, 6, new object()));
        // Stamp centered on the boundary: both sides inside the disc blend toward the mean.
        Assert.True(document.BlurStampApply(layer.Id, new PointD(8, 8), 4, 1));
        var after = document.Snapshot.Images[layer.Id];
        // (6,8) was 200; the blurred sample at the boundary pulls it toward 120.
        Assert.InRange(after.Pixels[8 * after.Stride + 6 * 4], 121, 199);
        // (1,1) far from the boundary keeps its value (blurred sample = 200 there).
        Assert.Equal(200, after.Pixels[1 * after.Stride + 1 * 4]);

        // One undo reverts the whole stroke.
        Assert.True(document.Undo());
        Assert.Equal(200, document.Snapshot.Images[layer.Id].Pixels[8 * after.Stride + 6 * 4]);
        document.EndBlurStroke();
    }

    [Fact]
    public void BlurStampWithoutBeginIsRefused()
    {
        var document = EditorDocument.Create(16, 16);
        Assert.False(document.BlurStampApply(document.Snapshot.Manifest.Layers[0].Id, new PointD(8, 8), 4, 1));
    }

    // --- Smudge / Liquify: warp-stroke working copy ---

    [Fact]
    public void LiquifyPushMovesPixelsWithTheStroke()
    {
        var document = EditorDocument.Create(16, 16);
        // Left half bright 220, right half dark 30: a rightward push drags the boundary.
        var image = new Surface(16, 16);
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                int index = y * image.Stride + x * 4;
                byte v = x < 8 ? (byte)220 : (byte)30;
                image.Pixels[index] = v;
                image.Pixels[index + 1] = v;
                image.Pixels[index + 2] = v;
                image.Pixels[index + 3] = 255;
            }
        }
        document.ImportImage(image, "Split");
        var layer = document.Snapshot.Manifest.Layers.Single(l => l.Name == "Split");
        document.SetActiveLayer(layer.Id);

        Assert.True(document.BeginWarpStroke(layer.Id, WarpMode.Liquify, diameter: 6, hardness: 0.5, strength: 1));
        document.WarpStrokeAppend(new PointD(6, 8));
        document.WarpStrokeAppend(new PointD(10, 8));
        var result = document.EndWarpStroke(layer.Id);
        Assert.True(result);
        var after = document.Snapshot.Images[layer.Id];

        // The push dragged the bright side toward the dark: pixel (9,8), past the old
        // boundary at x=8, is now bright (it was 30 before).
        Assert.Equal(220, after.Pixels[8 * after.Stride + 9 * 4]);
        // Undo restores the untouched split.
        Assert.True(document.Undo());
        Assert.Equal(30, document.Snapshot.Images[layer.Id].Pixels[8 * after.Stride + 9 * 4]);
    }

    [Fact]
    public void SmudgeCarriesThePickedColorAlongTheStroke()
    {
        var document = EditorDocument.Create(16, 16);
        document.ImportImage(Solid(8, 8, 200, 40, 40), "Red");
        var red = document.Snapshot.Manifest.Layers.Single(l => l.Name == "Red");
        document.SetActiveLayer(red.Id);
        // Delete the white background so the smudged trail reads against transparency.
        document.DeleteLayer(document.Snapshot.Manifest.Layers[0].Id);

        Assert.True(document.BeginWarpStroke(red.Id, WarpMode.Smudge, diameter: 6, hardness: 0.8, strength: 1));
        document.WarpStrokeAppend(new PointD(2, 4));
        document.WarpStrokeAppend(new PointD(4, 4));
        document.EndWarpStroke(red.Id);
        var after = document.Snapshot.Images[red.Id];

        // The dab disc (radius 3) around (4,4) reaches x=6: full-carry strength lays the
        // picked red over the transparency there.
        Assert.Equal(200, after.Pixels[4 * after.Stride + 6 * 4]);
        Assert.Equal(40, after.Pixels[4 * after.Stride + 6 * 4 + 1]);
    }

    // --- Content-aware fill: coherent patch synthesis from the surroundings ---

    [Fact]
    public void ContentFillFillsTheSelectionFromTheSurroundings()
    {
        var document = EditorDocument.Create(16, 16);
        // Solid green with a 4x4 hole of zeros (filled from the green).
        var image = new Surface(16, 16);
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                int index = y * image.Stride + x * 4;
                bool hole = x is >= 6 and < 10 && y is >= 6 and < 10;
                image.Pixels[index] = hole ? (byte)0 : (byte)40;
                image.Pixels[index + 1] = hole ? (byte)0 : (byte)160;
                image.Pixels[index + 2] = hole ? (byte)0 : (byte)60;
                image.Pixels[index + 3] = 255;
            }
        }
        document.ImportImage(image, "Green");
        var layer = document.Snapshot.Manifest.Layers.Single(l => l.Name == "Green");
        document.SetActiveLayer(layer.Id);
        document.SetSelection(Selection.Rectangle(16, 16, 6, 6, 4, 4));

        Assert.True(document.ContentFillSelection(layer.Id));
        var filled = document.Snapshot.Images[layer.Id];
        // Every hole pixel is now exactly the surrounding green (the only donors).
        for (int y = 6; y < 10; y++)
        {
            for (int x = 6; x < 10; x++)
            {
                int index = y * filled.Stride + x * 4;
                Assert.Equal(40, filled.Pixels[index]);
                Assert.Equal(160, filled.Pixels[index + 1]);
                Assert.Equal(60, filled.Pixels[index + 2]);
            }
        }
        // One undo reverts the fill.
        Assert.True(document.Undo());
        Assert.Equal(0, document.Snapshot.Images[layer.Id].Pixels[7 * filled.Stride + 7 * 4]);
    }

    [Fact]
    public void ContentFillReportsWhenNothingCanDonate()
    {
        var document = EditorDocument.Create(16, 16);
        var layer = document.Snapshot.Manifest.Layers[0];
        document.SetActiveLayer(layer.Id);
        // Select everything: no unselected opaque pixels remain to donate.
        document.SetSelection(Selection.Rectangle(16, 16, 0, 0, 16, 16));

        Assert.False(document.ContentFillSelection(layer.Id));
    }

    // --- Dither: diffusion, ordered, halftone, scanlines ---

    [Fact]
    public void AtkinsonDitherKeepsLargeUniformAreasUnchanged()
    {
        var document = EditorDocument.Create(16, 16);
        document.ImportImage(Solid(8, 8, 200, 200, 200), "Gray");
        var layer = document.Snapshot.Manifest.Layers.Single(l => l.Name == "Gray");
        var image = document.Snapshot.Images[layer.Id];

        // A uniform gray dithered in black/white: every pixel is quantized to black or white.
        DitherEngine.Apply(image, new DitherParams
        {
            Style = DitherStyle.Atkinson,
            Levels = 2,
            OriginalColors = false,
        });
        // Alpha untouched everywhere; colors are pure black or white.
        for (int i = 0; i < image.Pixels.Length; i += 4)
        {
            Assert.Equal(255, image.Pixels[i + 3]);
            Assert.True(image.Pixels[i] is 0 or 255, $"channel R = {image.Pixels[i]} at {i / 4}");
        }
    }

    [Fact]
    public void Bayer4OrderedDitherDarkensLowTonesSelectively()
    {
        var surface = new Surface(4, 4);
        for (int i = 0; i < 16; i++)
        {
            surface.Pixels[i * 4] = 128;
            surface.Pixels[i * 4 + 1] = 128;
            surface.Pixels[i * 4 + 2] = 128;
            surface.Pixels[i * 4 + 3] = 255;
        }
        // Mid gray at 2 tones: roughly half the pixels go white, ordered by the Bayer matrix.
        DitherEngine.Apply(surface, new DitherParams { Style = DitherStyle.Bayer4, Levels = 2 });
        int whites = 0;
        for (int i = 0; i < 16; i++) whites += surface.Pixels[i * 4] == 255 ? 1 : 0;
        Assert.InRange(whites, 6, 10); // ~8 of 16, ordered not random
        // The Bayer threshold at (0,1) is 0.53 — above mid → that pixel goes white;
        // (0,0) has the smallest threshold (0.03) → stays black.
        Assert.Equal(0, surface.Pixels[0]);
        Assert.Equal(255, surface.Pixels[4]);
    }

    [Fact]
    public void HalftoneDotsMarkLightOnDarkByTone()
    {
        var surface = new Surface(8, 8);
        for (int i = 0; i < 64; i++)
        {
            surface.Pixels[i * 4] = 255;
            surface.Pixels[i * 4 + 1] = 255;
            surface.Pixels[i * 4 + 2] = 255;
            surface.Pixels[i * 4 + 3] = 255;
        }
        // Pure white with lightOnDark: marks stand for light tones, so a full-white image
        // is fully marked (drawn in the light color = white → unchanged).
        DitherEngine.Apply(surface, new DitherParams
        {
            Style = DitherStyle.HalftoneDots,
            Cell = 4,
            LightOnDark = true,
            OriginalColors = false,
        });
        for (int i = 0; i < 64; i++) Assert.Equal(255, surface.Pixels[i * 4]);
    }

    // --- Free distortion: perspective / two-triangle corner warp ---

    [Fact]
    public void DistortLayerWarpsPixelsToTheCorners()
    {
        var document = EditorDocument.Create(16, 16);
        document.ImportImage(Solid(8, 8, 0, 0, 255), "Blue");
        var blue = document.Snapshot.Manifest.Layers.Single(l => l.Name == "Blue");
        document.SetActiveLayer(blue.Id);
        var before = document.Snapshot.Images[blue.Id].Pixels.ToArray();

        // Perspective: pull the top edge inward (trapezoid, still convex).
        var corners = new PointD[] { new(6, 3), new(10, 3), new(12, 11), new(4, 11) };
        Assert.True(document.DistortLayer(blue.Id, corners));
        var layer = document.Snapshot.Manifest.Layers.Single(l => l.Name == "Blue");
        // The layer is now axis-aligned over the shape's bounds.
        Assert.Equal(4, layer.Transform.Origin.X);
        Assert.Equal(3, layer.Transform.Origin.Y);
        Assert.Equal(8, layer.Transform.Size.Width);
        Assert.Equal(8, layer.Transform.Size.Height);
        var after = document.Snapshot.Images[blue.Id];
        // The warped image is layer-local: shape center (8,7) is layer pixel (4,4) — still blue.
        Assert.Equal(255, after.Pixels[4 * after.Stride + 4 * 4 + 2]);
        // Canvas (4.5, 3.5) is inside the bounds but outside the trapezoid: layer pixel (0,0) empty.
        Assert.Equal(0, after.Pixels[0 * after.Stride + 0 * 4 + 2]);

        Assert.True(document.Undo());
        Assert.True(before.SequenceEqual(document.Snapshot.Images[blue.Id].Pixels));
    }

    [Fact]
    public void DistortLayerRefusesUnusableShapes()
    {
        var document = EditorDocument.Create(16, 16);
        var layer = document.Snapshot.Manifest.Layers[0];

        // A degenerate sliver (both diagonal halves have no area).
        Assert.False(document.DistortLayer(layer.Id, [new(0, 0), new(8, 0), new(8, 0), new(0, 0)]));
    }

    // --- Layer effects: shadow + stroke rendering on a known shape ---

    [Fact]
    public void LayerEffectsShadowShiftsAndTints()
    {
        var document = EditorDocument.Create(16, 16);
        document.ImportImage(Solid(8, 8, 0, 0, 255), "Blue");
        var blue = document.Snapshot.Manifest.Layers.Single(l => l.Name == "Blue");
        // A manifest-shaped effects JSON (shadow straight down 4, blur 0): parse path verified.
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            shadow = new { angle = 90.0, distance = 4.0, blur = 0.0, red = 1.0, green = 0.0, blue = 0.0, opacity = 1.0 },
        });
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var effects = LayerEffects.Parse(doc.RootElement);
        Assert.NotNull(effects);
        Assert.False(effects!.IsEmpty);

        var (result, inset) = LayerEffectsRenderer.Render(Solid(8, 8, 0, 0, 255), null, effects);
        Assert.True(inset >= 4);
        // Under the layer, untouched (the layer is opaque and covers the shape).
        int under = (inset + 4) * result.Stride + (inset + 4) * 4;
        Assert.Equal(0, result.Pixels[under]);       // R: blue kept
        Assert.Equal(255, result.Pixels[under + 2]); // B: blue kept
        // Below the layer's bottom edge (4 px down): the pure shadow color red 255.
        int shadow = (inset + 8 + 3) * result.Stride + (inset + 4) * 4;
        Assert.Equal(255, result.Pixels[shadow]);    // R: shadow red
        Assert.Equal(0, result.Pixels[shadow + 2]);  // B: shadow has no blue
    }

    [Fact]
    public void LayerEffectsEmptyEffectsLeaveTheLayerAlone()
    {
        var document = EditorDocument.Create(16, 16);
        document.ImportImage(Solid(8, 8, 0, 0, 255), "Blue");
        // Parse of an empty JSON object returns null → the compositor draws the layer as-is.
        using var doc = System.Text.Json.JsonDocument.Parse("{}");
        Assert.Null(LayerEffects.Parse(doc.RootElement));
    }

    // --- Clone Stamp: copy a falloff disc from a source offset ---

    [Fact]
    public void CloneStampCopiesAHardDiscPixelForPixel()
    {
        var document = EditorDocument.Create(16, 16);
        document.ImportImage(Solid(8, 8, 0, 0, 255), "Blue");
        var blue = document.Snapshot.Manifest.Layers.Single(l => l.Name == "Blue");
        document.SetActiveLayer(blue.Id);
        var before = document.Snapshot.Images[blue.Id].Pixels.ToArray();

        Assert.True(document.CloneStampApply(blue.Id, new PointD(2, 2), new PointD(4, 4), 2, 1, sampleAllLayers: false));

        var image = document.Snapshot.Images[blue.Id];
        for (int y = 2; y < 7; y++)
        {
            for (int x = 2; x < 7; x++)
            {
                double dx = x + 0.5 - 4.0;
                double dy = y + 0.5 - 4.0;
                if (dx * dx + dy * dy > 4) continue;
                int index = y * image.Stride + x * 4;
                int sourceIndex = (y - 2) * image.Stride + (x - 2) * 4;
                Assert.True(image.Pixels[index + 3] == before[sourceIndex + 3],
                    $"({x},{y}) alpha {image.Pixels[index + 3]} != source {before[sourceIndex + 3]}");
            }
        }

        Assert.True(document.Undo());
        Assert.True(before.SequenceEqual(document.Snapshot.Images[blue.Id].Pixels));
    }

    [Fact]
    public void CloneStampSoftEdgeBlendsAndSamplingFollowsTheFlag()
    {
        var document = EditorDocument.Create(16, 16);
        document.ImportImage(Solid(8, 8, 0, 0, 255), "Blue");
        var blue = document.Snapshot.Manifest.Layers.Single(l => l.Name == "Blue");
        document.SetActiveLayer(blue.Id);

        // Sampling all layers reads the composite (white canvas): soft edge (hardness 0.5)
        // blends — the pixel at 0.75r gets half white over the blue, the center goes white.
        Assert.True(document.CloneStampApply(blue.Id, new PointD(12, 12), new PointD(4, 4), 2, 0.5, sampleAllLayers: true));
        var image = document.Snapshot.Images[blue.Id];
        Assert.InRange(image.Pixels[4 * image.Stride + 5 * 4], 1, 254); // 0.75r: half-blended
        Assert.Equal(255, image.Pixels[4 * image.Stride + 4 * 4]); // center fully copied
    }

    [Fact]
    public void CloneStampSkipsSourcesOutsideTheSample()
    {
        var document = EditorDocument.Create(16, 16);
        document.ImportImage(Solid(8, 8, 0, 0, 255), "Blue");
        var blue = document.Snapshot.Manifest.Layers.Single(l => l.Name == "Blue");
        document.SetActiveLayer(blue.Id);
        var before = document.Snapshot.Images[blue.Id].Pixels.ToArray();

        Assert.True(document.CloneStampApply(blue.Id, new PointD(0, 0), new PointD(1, 1), 2, 1, sampleAllLayers: false));
        var image = document.Snapshot.Images[blue.Id];
        Assert.Equal(255, image.Pixels[1 * image.Stride + 1 * 4 + 2]);

        Assert.True(document.Undo());
        Assert.True(before.SequenceEqual(document.Snapshot.Images[blue.Id].Pixels));
    }


    // --- Image > Invert: colors become alpha - color, alpha kept ---

    [Fact]
    public void InvertPixelsFlipsColorsKeepingAlpha()
    {
        var document = EditorDocument.Create(16, 16);
        document.ImportImage(Solid(8, 8, 100, 60, 250), "Layer");
        var layer = document.Snapshot.Manifest.Layers.Single(l => l.Name == "Layer");
        document.SetActiveLayer(layer.Id);

        Assert.True(document.InvertPixels(layer.Id));
        var image = document.Snapshot.Images[layer.Id];
        Assert.Equal(155, image.Pixels[4]); // 255 - 100
        Assert.Equal(195, image.Pixels[5]); // 255 - 60
        Assert.Equal(5, image.Pixels[6]); // 255 - 250
        Assert.Equal(255, image.Pixels[7]); // alpha kept

        Assert.True(document.Undo());
        Assert.Equal(100, document.Snapshot.Images[layer.Id].Pixels[4]);
    }

    [Fact]
    public void InvertPixelsOnPremultipliedHalfAlpha()
    {
        var document = EditorDocument.Create(16, 16);
        document.ImportImage(Solid(8, 8, 100, 100, 100, a: 128), "Dim");
        var dim = document.Snapshot.Manifest.Layers.Single(l => l.Name == "Dim");

        Assert.True(document.InvertPixels(dim.Id));
        var image = document.Snapshot.Images[dim.Id];
        // Premultiplied: new = alpha - old = 128 - 100 = 28.
        Assert.Equal(28, image.Pixels[4]);
        Assert.Equal(128, image.Pixels[7]);
    }

    [Fact]
    public void InvertPixelsRespectsTheSelection()
    {
        var document = EditorDocument.Create(16, 16);
        document.ImportImage(Solid(8, 8, 100, 100, 100), "Layer");
        var layer = document.Snapshot.Manifest.Layers.Single(l => l.Name == "Layer");
        document.SetActiveLayer(layer.Id);
        document.SetSelection(Selection.Rectangle(16, 16, 4, 4, 4, 4));

        Assert.True(document.InvertPixels(layer.Id));
        var image = document.Snapshot.Images[layer.Id];
        Assert.Equal(155, image.Pixels[5 * image.Stride + 5 * 4]); // inside: inverted
        Assert.Equal(100, image.Pixels[4]); // outside: untouched

        // Groups have no pixels of their own.
        document.Undo();
        document.GroupLayers([layer.Id]);
        var group = document.Snapshot.Manifest.Layers.Single(l => l.IsGroupOrFalse);
        Assert.False(document.InvertPixels(group.Id));
    }

    // --- Fill with the palette color: coverage-weighted source-over ---

    [Fact]
    public void FillPixelsInsideTheSelectionReplacesAndLeavesTheRestAlone()
    {
        var document = EditorDocument.Create(16, 16);
        document.ImportImage(Solid(16, 16, 100, 100, 100), "Layer");
        var layer = document.Snapshot.Manifest.Layers.Single(l => l.Name == "Layer");
        document.SetActiveLayer(layer.Id);
        document.SetSelection(Selection.Rectangle(16, 16, 4, 4, 4, 4));

        Assert.True(document.FillPixels(layer.Id, 255, 0, 0));
        var image = document.Snapshot.Images[layer.Id];
        Assert.Equal(255, image.Pixels[5 * image.Stride + 5 * 4]);
        Assert.Equal(0, image.Pixels[5 * image.Stride + 5 * 4 + 1]);
        Assert.Equal(0, image.Pixels[5 * image.Stride + 5 * 4 + 2]);
        Assert.Equal(255, image.Pixels[5 * image.Stride + 5 * 4 + 3]);
        Assert.Equal(100, image.Pixels[1 * image.Stride + 1 * 4]); // outside the selection
        Assert.Equal(255, image.Pixels[1 * image.Stride + 1 * 4 + 3]);

        Assert.True(document.Undo());
        Assert.Equal(100, document.Snapshot.Images[layer.Id].Pixels[5 * image.Stride + 5 * 4]);

        // A group has no pixels to fill.
        document.GroupLayers([layer.Id]);
        var group = document.Snapshot.Manifest.Layers.Single(l => l.IsGroupOrFalse);
        Assert.False(document.FillPixels(group.Id, 0, 0, 0));
    }

    [Fact]
    public void FillPixelsWithoutASelectionFillsTheWholeLayerOpaque()
    {
        var document = EditorDocument.Create(8, 8);
        document.ImportImage(Solid(8, 8, 100, 100, 100, a: 128), "Dim");
        var dim = document.Snapshot.Manifest.Layers.Single(l => l.Name == "Dim");

        Assert.True(document.FillPixels(dim.Id, 255, 0, 0));
        var image = document.Snapshot.Images[dim.Id];
        // Full coverage: the fill replaces the color, and its own opaque alpha dominates the stack.
        Assert.Equal(255, image.Pixels[0]);
        Assert.Equal(0, image.Pixels[1]);
        Assert.Equal(0, image.Pixels[2]);
        Assert.Equal(255, image.Pixels[3]);
    }

    [Fact]
    public void FillPixelsAtHalfCoverageCompositesOverThePixelUnderneath()
    {
        var document = EditorDocument.Create(8, 8);
        document.ImportImage(Solid(8, 8, 100, 100, 100), "Layer");
        var layer = document.Snapshot.Manifest.Layers.Single(l => l.Name == "Layer");
        var coverage = new MaskSurface(8, 8);
        for (int i = 0; i < coverage.Pixels.Length; i++) coverage.Pixels[i] = 128;
        document.SetSelection(Selection.FromCoverage(coverage));
        Assert.Equal(128, document.ActiveSelection!.CoverageAt(4, 4)); // the fixture really is half

        Assert.True(document.FillPixels(layer.Id, 255, 0, 0));
        var image = document.Snapshot.Images[layer.Id];
        // over = 128/255: red 255*0.50196 + 100*0.49804 = 178, green and blue keep 100*0.49804 = 50;
        // alpha 128 + 255*0.49804 = 255.
        Assert.Equal(178, image.Pixels[4]);
        Assert.Equal(50, image.Pixels[5]);
        Assert.Equal(50, image.Pixels[6]);
        Assert.Equal(255, image.Pixels[7]);
    }

    [Fact]
    public void FillPixelsRoundsTheCompositeAwayFromZero()
    {
        var document = EditorDocument.Create(8, 8);
        document.ImportImage(Solid(8, 8, 101, 101, 101), "Layer");
        var layer = document.Snapshot.Manifest.Layers.Single(l => l.Name == "Layer");
        var mid = new MaskSurface(8, 8);
        for (int i = 0; i < mid.Pixels.Length; i++) mid.Pixels[i] = 51;
        document.SetSelection(Selection.FromCoverage(mid));
        Assert.Equal(51, document.ActiveSelection!.CoverageAt(4, 4));
        Assert.True(document.FillPixels(layer.Id, 0, 255, 0));
        // Green: 255*51/255 + 101*204/255 = 51 + 80.8 = 131.8, so this only lands on 132 if the
        // composite rounds; a truncating blend would report 131.
        Assert.Equal(132, document.Snapshot.Images[layer.Id].Pixels[5]);
    }

    [Fact]
    public void FillMaskWritesACopyAndNeverRewritesTheStoredSurface()
    {
        var document = EditorDocument.Create(16, 16);
        document.ImportImage(Solid(16, 16, 0, 120, 240), "Blue");
        var blue = document.Snapshot.Manifest.Layers.Single(l => l.Name == "Blue");
        document.SetActiveLayer(blue.Id);
        document.SetSelection(Selection.Rectangle(16, 16, 2, 2, 2, 2));
        Assert.True(document.AddSelectionMask(blue.Id, revealing: true));
        document.SetSelection(null);

        var stored = document.Snapshot.Masks[blue.Id];
        Assert.Equal(255, stored.Pixels[3 * 16 + 3]); // revealed inside the 2×2 selection
        Assert.Equal(0, stored.Pixels[0]);

        Assert.True(document.FillMask(blue.Id, 0));
        var written = document.Snapshot.Masks[blue.Id];
        Assert.False(ReferenceEquals(stored, written), "the fill rewrote the stored mask in place");
        Assert.Equal(255, stored.Pixels[3 * 16 + 3]); // the surface the snapshot held before stays as it was
        Assert.Equal(0, written.Pixels[3 * 16 + 3]); // no selection left, so the whole mask went black
        Assert.True(document.Undo());
        Assert.Equal(255, document.Snapshot.Masks[blue.Id].Pixels[3 * 16 + 3]);
    }

    // --- AI segmentation seam: reserved, no model wired ---

    [Fact]
    public void SelectSubjectReportsMissingSegmenterAndUsesItWhenWired()
    {
        var document = EditorDocument.Create(16, 16);
        // Not wired: null, the caller reports AI as unavailable.
        Assert.Null(document.SelectSubject());
        Assert.Empty(document.SelectObject(new PointD(8, 8)));

        // A test segmenter wired in: subject mask becomes the selection.
        AiSegmentation.Registered = new StubSegmenter();
        try
        {
            var subject = document.SelectSubject();
            Assert.NotNull(subject);
            Assert.Equal(255, subject!.CoverageAt(3, 3));
            var objects = document.SelectObject(new PointD(3, 3));
            Assert.Equal(2, objects.Count);
        }
        finally
        {
            AiSegmentation.Registered = null; // never leak the stub into other tests
        }
    }

    private sealed class StubSegmenter : IAiSegmenter
    {
        public MaskSurface? SegmentSubject(Surface composite)
        {
            var mask = new MaskSurface(composite.Width, composite.Height);
            for (int y = 0; y < 6; y++)
                for (int x = 0; x < 6; x++)
                    mask.Pixels[y * mask.Width + x] = 255;
            return mask;
        }

        public IReadOnlyList<MaskSurface> SegmentObjects(Surface composite, PointD point)
        {
            var first = new MaskSurface(composite.Width, composite.Height);
            first.Pixels[3 * first.Width + 3] = 255;
            var second = new MaskSurface(composite.Width, composite.Height);
            second.Pixels[4 * second.Width + 4] = 255;
            return [first, second];
        }
    }

    [Fact]
    public void InvertPixelsCanInvertTheMaskPlaneInstead()
    {
        var document = EditorDocument.Create(16, 16);
        document.ImportImage(Solid(8, 8, 0, 0, 255), "Blue");
        var blue = document.Snapshot.Manifest.Layers.Single(l => l.Name == "Blue");
        document.AddRasterMask(blue.Id, MaskSurface.Solid(revealing: true));

        Assert.True(document.InvertPixels(blue.Id, invertMask: true));
        // The mask is now all black (hiding), the layer pixels untouched.
        Assert.Equal(0, document.Snapshot.Masks[blue.Id].Pixels[0]);
        Assert.Equal(255, document.Snapshot.Images[blue.Id].Pixels[6]);

        Assert.True(document.InvertPixels(blue.Id, invertMask: true)); // inverting again flips back
        Assert.Equal(255, document.Snapshot.Masks[blue.Id].Pixels[0]);
        // The layer's pixels were never touched: the blue channel stays 255.
        Assert.Equal(255, document.Snapshot.Images[blue.Id].Pixels[6]);
    }

    // --- Copy Merged: the composite as-seen, clipped to the selection ---

    [Fact]
    public void CopyMergedSelectionMatchesTheComposite()
    {
        var document = EditorDocument.Create(8, 8);
        document.ImportImage(Solid(6, 6, 255, 0, 0), "Red");
        document.ImportImage(Solid(4, 4, 0, 0, 255, a: 128), "Blue");
        var manifest = document.Snapshot.Manifest;
        var blue = manifest.Layers.Single(l => l.Name == "Blue");
        blue.Transform.Origin = new PointD(2, 2);
        document.SetSelection(Selection.Rectangle(8, 8, 2, 2, 4, 4));

        var merged = document.CopyMergedSelection();

        Assert.NotNull(merged);
        Assert.Equal(4, merged!.Pixels.Width);
        // Byte-for-byte against the full compositor over the same region.
        var full = new CanvasCompositor(document.Snapshot).Render(2, 2, 6, 6);
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                int mergedIndex = y * merged.Pixels.Stride + x * 4;
                int fullIndex = (y + 2) * full.Stride + (x + 2) * 4;
                for (int c = 0; c < 4; c++)
                {
                    Assert.Equal(full.Pixels[fullIndex + c], merged.Pixels.Pixels[mergedIndex + c]);
                }
            }
        }
    }
    // --- Clone Stamp: one key joins a stroke's stamps into a single undo step ---

    [Fact]
    public void CloneStampCoalescesAStrokeIntoOneUndo()
    {
        var document = EditorDocument.Create(16, 16);
        document.ImportImage(Solid(8, 8, 0, 0, 255), "Blue");
        var blue = document.Snapshot.Manifest.Layers.Single(l => l.Name == "Blue");
        document.SetActiveLayer(blue.Id);
        var key = new object();

        // The stroke opens one undo step at begin; both dabs land in the working copy, so
        // one undo reverts both. The source is the white composite, so the dabs read on the
        // RED channel — a blue pixel's own blue channel would read 255 either way.
        Assert.True(document.BeginCloneStroke(blue.Id, new PointD(12, 12), new PointD(4, 4), 1, 1, sampleAllLayers: true, key));
        document.CloneStampAppend(blue.Id, new PointD(12, 12), new PointD(6, 4), 1, 1);
        document.EndCloneStroke();
        var image = document.Snapshot.Images[blue.Id];
        Assert.Equal(255, image.Pixels[4 * image.Stride + 4 * 4]);
        Assert.Equal(255, image.Pixels[4 * image.Stride + 6 * 4]);

        Assert.True(document.Undo());
        image = document.Snapshot.Images[blue.Id];
        Assert.Equal(0, image.Pixels[4 * image.Stride + 4 * 4]);
        Assert.Equal(0, image.Pixels[4 * image.Stride + 6 * 4]);
    }

}
