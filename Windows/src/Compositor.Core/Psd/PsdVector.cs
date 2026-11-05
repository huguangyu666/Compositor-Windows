using System.Buffers.Binary;

namespace Compositor.Core.Psd;

/// <summary>
/// Maps Photoshop's vector-fill records (`SoCo`, `vogk`, the `vstk` enable flags, and a sharp
/// `vmsk`/`vsms` rectangle) onto a live shape layer, following the Mac's `PSDVector.live`: the
/// fill colour, kind, box and corner radius become a `ShapeStyle`, and the pixels come from the
/// same rasterizer the shape tool draws with, so a placed PSD rectangle or ellipse stays
/// editable and resamples cleanly at any size. Strokes are noted and omitted, as there, because
/// the shape model has no stroke field. The general bézier path (`PSDVector.raster`) stays
/// unported: it needs curve flattening on top of the polygon scanline engine, and its
/// antialias parity target is the bézier-AA tradeoff the user has not settled.
/// </summary>
internal static class PsdVector
{
    /// <summary>A live fill: style, document rectangle, rasterized pixels and user notes.</summary>
    internal sealed record Live(ShapeStyle Style, int X, int Y, int Width, int Height, Surface Image, List<string>? Notes);

    /// <summary>Descriptor payloads are kilobytes; anything larger is not a shape origination.</summary>
    internal const int MaxDescriptorBytes = 256 * 1024;

    /// <summary>Whether the layer's extra-info loop should keep this key for the shape path.</summary>
    internal static bool TracksKey(string key) => key is "SoCo" or "vstk" or "vogk" or "vmsk" or "vsms";

    /// <summary>
    /// `PSDVector.live(extra:canvas:remainingPixels:)`. Null means the layer is not a live
    /// fill: the caller keeps its bitmap or drops it, exactly as the Mac's reader falls
    /// through. A shape that fits the side limit but not the pixel budget throws, too.
    /// </summary>
    internal static Live? ReadLive(Dictionary<string, byte[]>? extra, int canvasWidth, int canvasHeight, ref long remainingPixels)
    {
        if (extra is null || canvasWidth <= 0 || canvasHeight <= 0 || !extra.TryGetValue("SoCo", out var fillData)) return null;
        extra.TryGetValue("vstk", out var strokeData);
        // stroke.flatMap { bool("fillEnabled") } ?? (SoCo != nil): SoCo is here, so a vstk
        // without the key still means filled. strokeEnabled has no fallback but false.
        if (strokeData is not null && (Bool(strokeData, "fillEnabled") ?? true) == false) return null;
        bool strokeEnabled = strokeData is not null && (Bool(strokeData, "strokeEnabled") ?? false);
        if (Rgb(fillData) is not { } fill) return null;
        var origin = Origination(extra.GetValueOrDefault("vogk")) ?? SharpRect(extra.GetValueOrDefault("vmsk") ?? extra.GetValueOrDefault("vsms"), canvasWidth, canvasHeight);
        if (origin is null) return null;
        // CGRect.integral: floor the origin, ceil the far edge; then the pixelSize guards.
        if (!double.IsFinite(origin.Left) || !double.IsFinite(origin.Top)) return null;
        int x = (int)Math.Floor(origin.Left), y = (int)Math.Floor(origin.Top);
        if ((int)Math.Ceiling(origin.Right) - x < 1 || (int)Math.Ceiling(origin.Bottom) - y < 1) return null;
        int width = Math.Max(1, (int)Math.Ceiling(origin.Right) - x), height = Math.Max(1, (int)Math.Ceiling(origin.Bottom) - y);
        if (width > Limits.MaxSide || height > Limits.MaxSide) throw new PsdNotSupportedException("Shape exceeds the supported limits.");
        long budget = Math.Min(Limits.MaxSurfacePixels, Math.Max(0, remainingPixels));
        if ((long)width * height > budget) throw new PsdNotSupportedException("Shape exceeds the remaining pixel budget.");
        var style = new ShapeStyle { Kind = origin.Kind, Red = fill.r, Green = fill.g, Blue = fill.b, CornerRadius = origin.CornerRadius };
        var notes = new List<string>(origin.Notes);
        if (strokeEnabled) notes.Add("The Photoshop stroke isn’t supported on shape layers and was omitted.");
        remainingPixels = Math.Max(0, remainingPixels - (long)width * height);
        return new Live(style, x, y, width, height, ShapeRasterizer.Draw(style, width, height), notes.Count > 0 ? notes : null);
    }

    private sealed record Origin(ShapeKind Kind, double Left, double Top, double Right, double Bottom, double CornerRadius, List<string> Notes);

    /// <summary>
    /// `PSDVector.origination`: 1/2 = rectangle (2 is rounded), 5 = ellipse, with the shape's
    /// pixel box and — for a rounded rectangle whose four radii agree — one uniform radius.
    /// </summary>
    private static Origin? Origination(byte[]? data)
    {
        if (data is null) return null;
        var type = Int32(data, "keyOriginType");
        if (type is not (1 or 2 or 5)) return null;
        var kind = type == 5 ? ShapeKind.Ellipse : ShapeKind.Rectangle;
        int from = Math.Max(0, IndexOf(data, "keyOriginShapeBBox"u8));
        if (Unit(data, "Left"u8, from) is not { } left || Unit(data, "Top "u8, from) is not { } top
            || Unit(data, "Rght"u8, from) is not { } right || Unit(data, "Btom"u8, from) is not { } bottom) return null;
        if (right - left < 1 || bottom - top < 1 || !double.IsFinite(left) || !double.IsFinite(top)
            || !double.IsFinite(right) || !double.IsFinite(bottom)) return null;
        double radius = 0;
        int radiiAt = IndexOf(data, "keyOriginRRectRadii"u8);
        if (kind == ShapeKind.Rectangle && radiiAt >= 0)
        {
            var radii = new List<double>(4);
            foreach (string key in new[] { "topLeft", "topRight", "bottomRight", "bottomLeft" })
                if (Unit(data, System.Text.Encoding.ASCII.GetBytes(key), radiiAt) is { } value) radii.Add(value);
            if (radii.Count == 4)
            {
                if (radii.Max() - radii.Min() > 0.5) return null;
                radius = radii.Max();
            }
        }
        return new Origin(kind, left, top, right, bottom, radius, []);
    }

    /// <summary>
    /// `PSDVector.sharpRect`: a vector mask whose only four anchors sit where their own handles
    /// point is a rectangle — its bounding box, kind rectangle, no radius. Curvier paths are not
    /// live shapes and need the unported raster path.
    /// </summary>
    private static Origin? SharpRect(byte[]? data, int canvasWidth, int canvasHeight)
    {
        if (data is null || data.Length < 8) return null;
        int offset = 8, remaining = 0;
        var anchors = new List<(double X, double Y)>();
        bool sharp = true;
        while (offset + 26 <= data.Length)
        {
            int type = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(offset));
            var body = data.AsSpan(offset + 2, 24);
            offset += 26;
            switch (type)
            {
                case 0 or 3:
                    if (anchors.Count > 0) return null;
                    remaining = BinaryPrimitives.ReadInt16BigEndian(body[..2]);
                    break;
                case 1 or 2 or 4 or 5:
                    if (remaining <= 0) break;
                    remaining -= 1;
                    var incoming = Point(body, 0, canvasWidth, canvasHeight);
                    var anchor = Point(body, 8, canvasWidth, canvasHeight);
                    var outgoing = Point(body, 16, canvasWidth, canvasHeight);
                    if (Math.Sqrt(Math.Pow(incoming.X - anchor.X, 2) + Math.Pow(incoming.Y - anchor.Y, 2)) > .5
                        || Math.Sqrt(Math.Pow(outgoing.X - anchor.X, 2) + Math.Pow(outgoing.Y - anchor.Y, 2)) > .5) sharp = false;
                    anchors.Add(anchor);
                    break;
            }
        }
        if (!sharp || anchors.Count != 4) return null;
        double minX = anchors.Min(a => a.X), maxX = anchors.Max(a => a.X);
        double minY = anchors.Min(a => a.Y), maxY = anchors.Max(a => a.Y);
        if (maxX - minX < 1 || maxY - minY < 1) return null;
        return new Origin(ShapeKind.Rectangle, minX, minY, maxX, maxY, 0, []);
    }

    /// <summary>The 16.16 anchor pair, normalized to the canvas, like the Mac's `point`.</summary>
    private static (double X, double Y) Point(ReadOnlySpan<byte> body, int at, int canvasWidth, int canvasHeight)
    {
        // The 16.16 pair is a real fraction first: integer division would snap every anchor to zero.
        double y = BinaryPrimitives.ReadInt32BigEndian(body.Slice(at, 4)) / (double)0x1000000 * canvasHeight;
        double x = BinaryPrimitives.ReadInt32BigEndian(body.Slice(at + 4, 4)) / (double)0x1000000 * canvasWidth;
        return (x, y);
    }

    /// <summary>The descriptor's RGB: doubles above 1 read as 0–255, anything else as 0–1.</summary>
    private static (double r, double g, double b)? Rgb(byte[] data)
    {
        if (Double(data, "Rd  "u8) is not { } red || Double(data, "Grn "u8) is not { } green || Double(data, "Bl  "u8) is not { } blue) return null;
        static double Channel(double value) => value > 1 ? Math.Clamp(value, 0, 255) / 255 : Math.Clamp(value, 0, 1);
        return (Channel(red), Channel(green), Channel(blue));
    }

    private static bool? Bool(byte[] data, string key)
    {
        int start = IndexOf(data, System.Text.Encoding.ASCII.GetBytes(key));
        if (start < 0) return null;
        int at = start + key.Length;
        if (at + 5 > data.Length || System.Text.Encoding.ASCII.GetString(data, at, 4) != "bool") return null;
        return data[at + 4] != 0;
    }

    /// <summary>The double eight bytes past the key's `UntF` tag, as the Mac's `unit` reads it.</summary>
    private static double? Unit(byte[] data, ReadOnlySpan<byte> key, int from)
    {
        int keyAt = IndexOf(data, key, from);
        if (keyAt < 0) return null;
        int unit = IndexOf(data, "UntF"u8, keyAt);
        if (unit < 0 || unit + 16 > data.Length) return null;
        return BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(unit + 8, 8)));
    }

    private static int? Int32(byte[] data, string key)
    {
        int start = IndexOf(data, System.Text.Encoding.ASCII.GetBytes(key));
        if (start < 0) return null;
        int at = start + key.Length;
        if (at + 8 > data.Length || System.Text.Encoding.ASCII.GetString(data, at, 4) != "long") return null;
        return BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(at + 4, 4));
    }

    private static double? Double(byte[] data, ReadOnlySpan<byte> key)
    {
        int start = IndexOf(data, key);
        if (start < 0) return null;
        int at = start + key.Length;
        if (at + 12 > data.Length || System.Text.Encoding.ASCII.GetString(data, at, 4) != "doub") return null;
        return BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(at + 4, 8)));
    }

    private static int IndexOf(byte[] data, ReadOnlySpan<byte> needle, int from = 0) =>
        from >= data.Length ? -1 : from + data.AsSpan(from).IndexOf(needle);
}
