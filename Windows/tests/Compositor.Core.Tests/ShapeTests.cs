using Xunit;

namespace Compositor.Core.Tests;

public class ShapeTests
{
    private static ShapeStyle Solid(ShapeKind kind, double cornerRadius = 0, double lineWidth = 0,
        PointD? start = null, PointD? end = null) => new()
    {
        Kind = kind,
        Red = 1,
        Green = 0,
        Blue = 0,
        CornerRadius = cornerRadius,
        LineWidth = lineWidth > 0 ? lineWidth : null,
        Start = start,
        End = end,
    };

    private static byte AlphaAt(Surface surface, int x, int y) => surface.Pixels[y * surface.Stride + x * 4 + 3];

    [Fact]
    public void EllipseFillsItsCenterAndKeepsCornersEmpty()
    {
        var surface = ShapeRasterizer.Draw(Solid(ShapeKind.Ellipse), 100, 100);

        // Center is solid red; the four corners are outside the ellipse.
        Assert.Equal(255, AlphaAt(surface, 50, 50));
        Assert.Equal(0, AlphaAt(surface, 0, 0));
        Assert.Equal(0, AlphaAt(surface, 99, 0));
        Assert.Equal(0, AlphaAt(surface, 0, 99));
        Assert.Equal(0, AlphaAt(surface, 99, 99));

        // The extreme axes are covered: pixel centers there are strictly inside.
        Assert.True(AlphaAt(surface, 50, 2) > 200);
        Assert.True(AlphaAt(surface, 97, 50) > 200);
    }

    [Fact]
    public void RectangleCornerRadiusCapsAtHalfTheShorterSide()
    {
        // A 60×40 rectangle with a huge radius becomes a pill (radius 20), not a 255-radius arc.
        var pill = ShapeRasterizer.Draw(Solid(ShapeKind.Rectangle, cornerRadius: 255), 60, 40);

        // Near the sharp corner the pill's arc keeps pixels empty; the cap center is solid.
        Assert.Equal(255, AlphaAt(pill, 10, 10));
        Assert.Equal(0, AlphaAt(pill, 58, 2));
        Assert.True(AlphaAt(pill, 10, 1) < 128); // inside the 20px corner arc's cut

        // Without a radius the same corner is filled.
        var sharp = ShapeRasterizer.Draw(Solid(ShapeKind.Rectangle), 60, 40);
        Assert.Equal(255, AlphaAt(sharp, 0, 0));
    }

    [Fact]
    public void LineEndPointsSurviveRescaling()
    {
        // A line from (10, 10) to (50, 30) in an 80×40 box; the ends ride as box fractions.
        var style = Solid(ShapeKind.Line, lineWidth: 8, start: new PointD(0.125, 0.25), end: new PointD(0.625, 0.75));
        var small = ShapeRasterizer.Draw(style, 80, 40);

        Assert.True(AlphaAt(small, 10, 10) > 200);
        Assert.True(AlphaAt(small, 50, 30) > 200);
        Assert.True(AlphaAt(small, 78, 2) < 10);

        // Redrawn at twice the size from the same fractions, the line passes through the
        // same proportional points. The stored width stays 8 document pixels, as on the Mac.
        var large = ShapeRasterizer.Draw(style, 160, 80);
        Assert.True(AlphaAt(large, 20, 20) > 200);
        Assert.True(AlphaAt(large, 60, 40) > 200);
        Assert.True(AlphaAt(large, 100, 60) > 200);
        Assert.True(AlphaAt(large, 150, 10) < 10);
    }

    [Fact]
    public void AddShapeLayerInsertsAboveActiveAndUndoes()
    {
        var document = EditorDocument.Create(16, 16);
        document.ImportImage(Solid(8, 8, 0, 255, 0), "Photo");
        var style = Solid(ShapeKind.Ellipse);
        var layer = document.AddShapeLayer(style, 8, 8, new PointD(2, 3));

        var manifest = document.Snapshot.Manifest;
        Assert.Equal(3, document.LayerCount);
        Assert.Equal(2, manifest.Layers.IndexOf(manifest.Layers.Single(candidate => candidate.Id == layer.Id)));
        Assert.Equal(layer.Id, manifest.ActiveLayerId);
        Assert.Equal("Ellipse 1", layer.Name);
        Assert.Equal(new PointD(2, 3), layer.Transform.Origin);
        Assert.Equal(new SizeD(8, 8), layer.Transform.Size);
        Assert.Equal("Ellipse", layer.Shape!.Value.GetProperty("kind").GetString());
        Assert.Equal(1, layer.Shape.Value.GetProperty("red").GetDouble());

        var image = document.Snapshot.Images[layer.Id];
        Assert.Equal(255, AlphaAt(image, 4, 4));

        Assert.True(document.Undo());
        Assert.Equal(2, document.LayerCount);

        Assert.True(document.Redo());
        Assert.Equal("Ellipse", document.Snapshot.Manifest.Layers[2].Shape!.Value.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task ShapeRoundTripsThroughTheStore()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"shape-roundtrip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var document = EditorDocument.Create(16, 16);
            var style = Solid(ShapeKind.Line, cornerRadius: 0, lineWidth: 6,
                start: new PointD(0.2, 0.3), end: new PointD(0.8, 0.7));
            document.AddShapeLayer(style, 20, 10, new PointD(1, 1));
            await ProjectStore.SaveAsync(document.Snapshot, directory);

            var loaded = await ProjectStore.LoadAsync(directory);
            var layer = loaded.Manifest.Layers.Single(candidate => candidate.Shape is not null);
            var restored = ShapeStyle.FromElement(layer.Shape)!;
            Assert.Equal(ShapeKind.Line, restored.Kind);
            Assert.Equal(6, restored.LineWidth);
            Assert.Equal(0.2, restored.Start!.Value.X);
            Assert.Equal(0.7, restored.End!.Value.Y);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ShapeNamesSkipTakenNumbers()
    {
        var document = EditorDocument.Create(16, 16);
        var first = document.AddShapeLayer(Solid(ShapeKind.Rectangle), 4, 4, new PointD(0, 0));
        var second = document.AddShapeLayer(Solid(ShapeKind.Rectangle), 4, 4, new PointD(8, 8));

        Assert.Equal("Rectangle 1", first.Name);
        Assert.Equal("Rectangle 2", second.Name);
    }

    private static Surface Solid(int width, int height, byte r, byte g, byte b)
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
                surface.Pixels[index + 3] = 255;
            }
        }
        return surface;
    }
}
