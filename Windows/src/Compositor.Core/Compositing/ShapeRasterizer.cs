using System.Text.Json;
using System.Text.Json.Serialization;

namespace Compositor.Core;

/// <summary>The shape-tool primitives, named as the Mac's raw values for the manifest.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ShapeKind
{
    Rectangle,
    Ellipse,
    Line,
}

/// <summary>
/// What a shape layer draws, kept in the manifest so the shape can be redrawn at a new size.
/// Field names and shapes follow the Mac's <c>LayerShapeStyle</c> Codable exactly: camelCase
/// keys, the kind spelled "Rectangle"/"Ellipse"/"Line", points as {"x","y"}, and the optional
/// line fields omitted for the other shapes. Red/green/blue are 0–1; corner radius and line
/// width are document pixels.
/// </summary>
public sealed class ShapeStyle
{
    public ShapeKind Kind { get; set; }
    public double Red { get; set; }
    public double Green { get; set; }
    public double Blue { get; set; }
    public double CornerRadius { get; set; }
    public double? LineWidth { get; set; }
    public PointD? Start { get; set; }
    public PointD? End { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public JsonElement ToElement() => JsonSerializer.SerializeToElement(this, JsonOptions);

    public static ShapeStyle? FromElement(JsonElement? element) =>
        element is { } value ? JsonSerializer.Deserialize<ShapeStyle>(value.GetRawText(), JsonOptions) : null;
}

/// <summary>
/// Rasterizes the shape-tool primitives into a premultiplied RGBA Surface, following the
/// macOS ShapeTool.shapeImage: rectangles fill with the corner radius capped at half the
/// shorter side (a large radius makes a pill), ellipses ignore the radius, and a line is a
/// round-capped stroke between its two points, which are fractions of the layer box so the
/// line lands where it was dragged at any size. Edges antialias analytically through a
/// signed distance sampled at pixel centers — one pixel of coverage falloff, like the
/// CoreGraphics fill on the Mac.
/// </summary>
public static class ShapeRasterizer
{
    public static Surface Draw(ShapeStyle style, int width, int height)
    {
        var surface = new Surface(width, height);
        byte red = Channel(style.Red), green = Channel(style.Green), blue = Channel(style.Blue);
        double radius = Math.Min(Math.Min(Math.Max(0, style.CornerRadius), width / 2.0), height / 2.0);
        double thickness = Math.Max(1, style.LineWidth ?? 0);
        // Without stored ends (older lines) the stroke runs inset corner to corner.
        double insetX = Math.Min(thickness, width) / 2.0, insetY = Math.Min(thickness, height) / 2.0;
        PointD from = style.Start is { } start
            ? new PointD(start.X * width, start.Y * height)
            : new PointD(insetX, insetY);
        PointD to = style.End is { } end
            ? new PointD(end.X * width, end.Y * height)
            : new PointD(width - insetX, height - insetY);

        for (int y = 0; y < height; y++)
        {
            int row = y * surface.Stride;
            for (int x = 0; x < width; x++)
            {
                double px = x + 0.5, py = y + 0.5;
                double distance = style.Kind switch
                {
                    ShapeKind.Rectangle => RectangleDistance(px, py, width, height, radius),
                    ShapeKind.Ellipse => EllipseDistance(px, py, width, height),
                    _ => SegmentDistance(px, py, from, to) - thickness / 2.0,
                };
                double coverage = Math.Clamp(0.5 - distance, 0, 1);
                if (coverage <= 0) continue;
                byte alpha = (byte)Math.Round(coverage * 255.0);
                int index = row + x * 4;
                surface.Pixels[index] = (byte)Math.Round(red * alpha / 255.0);
                surface.Pixels[index + 1] = (byte)Math.Round(green * alpha / 255.0);
                surface.Pixels[index + 2] = (byte)Math.Round(blue * alpha / 255.0);
                surface.Pixels[index + 3] = alpha;
            }
        }
        return surface;
    }

    /// <summary>Signed distance to the box (0, 0, width, height) with its corners rounded by radius.</summary>
    private static double RectangleDistance(double px, double py, int width, int height, double radius)
    {
        double qx = Math.Abs(px - width / 2.0) - (width / 2.0 - radius);
        double qy = Math.Abs(py - height / 2.0) - (height / 2.0 - radius);
        double outsideX = Math.Max(qx, 0), outsideY = Math.Max(qy, 0);
        return Math.Sqrt(outsideX * outsideX + outsideY * outsideY) + Math.Min(Math.Max(qx, qy), 0) - radius;
    }

    /// <summary>Approximate signed distance to the ellipse inscribed in (0, 0, width, height).</summary>
    private static double EllipseDistance(double px, double py, int width, int height)
    {
        double ex = (px - width / 2.0) / (width / 2.0);
        double ey = (py - height / 2.0) / (height / 2.0);
        double k0 = Math.Sqrt(ex * ex + ey * ey);
        double k1 = Math.Sqrt(ex * ex / (width / 2.0 * (width / 2.0)) + ey * ey / (height / 2.0 * (height / 2.0)));
        return k0 * (k0 - 1.0) / Math.Max(k1, 1e-9);
    }

    private static double SegmentDistance(double px, double py, PointD from, PointD to)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y;
        double lengthSquared = dx * dx + dy * dy;
        double t = lengthSquared <= 0 ? 0 : Math.Clamp(((px - from.X) * dx + (py - from.Y) * dy) / lengthSquared, 0, 1);
        double nearestX = from.X + t * dx, nearestY = from.Y + t * dy;
        return Math.Sqrt((px - nearestX) * (px - nearestX) + (py - nearestY) * (py - nearestY));
    }

    private static byte Channel(double value) => (byte)Math.Clamp(Math.Round(value * 255.0), 0, 255);
}
