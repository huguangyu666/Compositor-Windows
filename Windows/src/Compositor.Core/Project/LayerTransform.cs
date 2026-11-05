using System.Text.Json.Serialization;

namespace Compositor.Core;

/// <summary>
/// Unrotated bounds in document pixels; rotation is clockwise around their center.
/// JSON shape matches the macOS encoder: <c>origin</c> and <c>size</c> are arrays, and every
/// property is required (Swift's synthesized decoding has no defaults on read).
/// </summary>
public sealed class LayerTransform : IEquatable<LayerTransform>
{
    [JsonRequired]
    public PointD Origin { get; set; } = PointD.Zero;

    [JsonRequired]
    public SizeD Size { get; set; } = SizeD.Zero;

    [JsonRequired]
    public double Rotation { get; set; }

    [JsonRequired]
    public bool FlipX { get; set; }

    [JsonRequired]
    public bool FlipY { get; set; }

    [JsonRequired]
    [JsonConverter(typeof(RawStringEnumConverter<LayerSampling>))]
    public LayerSampling Sampling { get; set; } = LayerSampling.HighQuality;

    [JsonIgnore]
    public PointD Center => new(Origin.X + Size.Width / 2, Origin.Y + Size.Height / 2);

    [JsonIgnore]
    public double Radians => Rotation % 360 * Math.PI / 180;

    /// <summary>The same check as the macOS LayerTransform.isValid.</summary>
    [JsonIgnore]
    public bool IsValid =>
        IsFinite(Origin.X) && IsFinite(Origin.Y) && IsFinite(Size.Width) && IsFinite(Size.Height) && IsFinite(Rotation)
        && Size.Width is >= 1 and <= 300_000 && Size.Height is >= 1 and <= 300_000
        && Math.Abs(Origin.X) <= 1_000_000 && Math.Abs(Origin.Y) <= 1_000_000;

    /// <summary>Where this transform places the unit point (0–1, y down) on the document.</summary>
    public PointD Point(PointD unit)
    {
        double x = (unit.X - 0.5) * Size.Width, y = (unit.Y - 0.5) * Size.Height;
        double radians = Radians, cos = Math.Cos(radians), sin = Math.Sin(radians);
        var center = Center;
        return new PointD(center.X + x * cos - y * sin, center.Y + x * sin + y * cos);
    }

    /// <summary>
    /// The exact inverse of <see cref="Point"/>: which unit point of this placement a document point belongs
    /// to. The canvas can already answer "where does this corner land", but editing text in place needs the
    /// other direction — a click becomes a caret, a drag becomes a handle move — and that is this times the
    /// text's logical size. Values outside 0–1 are legitimate: the caret sits past the last glyph, a resize
    /// can start outside the box.
    /// </summary>
    public PointD InversePoint(PointD document)
    {
        double radians = Radians, cos = Math.Cos(radians), sin = Math.Sin(radians);
        double dx = document.X - Center.X, dy = document.Y - Center.Y;
        return new PointD((dx * cos + dy * sin) / Size.Width + .5, (-dx * sin + dy * cos) / Size.Height + .5);
    }

    /// <summary>
    /// Which point of the layer's own pixel grid a document point lands on. The flip is applied the way
    /// <c>BrushStroke.pixelToDocument</c> bakes it into a negative scale (Document/BrushStroke.swift:110-116),
    /// and the mask pass does the same arithmetic inline (CanvasCompositor.cs:1078-1095). Anything outside the
    /// grid is the caller's business: a click past the edge should do nothing rather than wrap around.
    /// </summary>
    public PointD PixelOf(PointD document, SizeD source)
    {
        var unit = InversePoint(document);
        return new PointD((FlipX ? 1 - unit.X : unit.X) * source.Width, (FlipY ? 1 - unit.Y : unit.Y) * source.Height);
    }

    /// <summary>The eight edge and corner handles in the macOS order (<c>LayerTransform.swift:96-98</c>):
    /// top left, top, top right, right, bottom right, bottom, bottom left, left.</summary>
    public static readonly IReadOnlyList<PointD> Handles = new PointD[]
    {
        new(0, 0), new(.5, 0), new(1, 0), new(1, .5), new(1, 1), new(.5, 1), new(0, 1), new(0, .5),
    };

    /// <summary>How far either side of an edge counts as that edge, in the box's own units
    /// (<c>InlineTextEditor.swift:385-387</c>): the handles are drawn 6 across while the Move tool's box grabs
    /// within 10, so the reach is 10/6 of one — capped at a third of the shorter side so a small box keeps a
    /// middle that is text rather than handle.</summary>
    public static double EdgeReach(double handleSize, SizeD box) =>
        Math.Min(handleSize * 10 / 6, Math.Min(box.Width, box.Height) / 3);

    /// <summary>Which handle's band a point in the box's own units lands in, or null for the text inside it
    /// (<c>InlineTextEditor.swift:391-408</c>). Corner bands win over edge bands in the same order the original
    /// lists them, and a point further than the reach outside the box is not a handle either.</summary>
    public static int? HandleAt(PointD point, SizeD box, double reach)
    {
        if (point.X < -reach || point.X > box.Width + reach || point.Y < -reach || point.Y > box.Height + reach) return null;
        bool left = point.X <= reach, right = point.X >= box.Width - reach,
            top = point.Y <= reach, bottom = point.Y >= box.Height - reach;
        if (left && top) return 0;
        if (right && top) return 2;
        if (right && bottom) return 4;
        if (left && bottom) return 6;
        if (top) return 1;
        if (right) return 3;
        if (bottom) return 5;
        if (left) return 7;
        return null;
    }

    /// <summary>
    /// A handle drag, as the macOS editor computes it (<c>InlineTextEditor.swift:460-489</c>): the document-space
    /// movement is turned into the box's own frame, only the two edges the handle owns move, the box never goes
    /// under the 16-pixel floor the rasterizer needs, and the corner the drag did not touch is put back exactly
    /// where it was. A box or placement that would be invalid returns null, which is the original's
    /// <c>guard … else return</c>: the draft simply does not move.
    /// </summary>
    /// <param name="handle">Index into <see cref="Handles"/>.</param>
    /// <param name="logical">The box's logical (text-view) size when the drag began — the new box is counted in
    /// those units, so a layer scaled away from its pixel size keeps its own numbers.</param>
    /// <param name="start">Where the drag began, in document pixels.</param>
    /// <param name="moved">Where the pointer is now, in document pixels.</param>
    public (SizeD Box, LayerTransform Placement)? ResizeBox(int handle, SizeD logical, PointD start, PointD moved)
    {
        if (handle < 0 || handle >= Handles.Count || logical.Width <= 0 || logical.Height <= 0
            || Size.Width <= 0 || Size.Height <= 0) return null;
        var unit = Handles[handle];
        double radians = Radians, dx = moved.X - start.X, dy = moved.Y - start.Y;
        double localX = dx * Math.Cos(radians) + dy * Math.Sin(radians);
        double localY = -dx * Math.Sin(radians) + dy * Math.Cos(radians);
        double left = 0, top = 0, right = Size.Width, bottom = Size.Height;
        double minW = 16 * Size.Width / logical.Width, minH = 16 * Size.Height / logical.Height;
        if (unit.X == 0) left = Math.Min(localX, right - minW);
        if (unit.X == 1) right = Math.Max(left + minW, right + localX);
        if (unit.Y == 0) top = Math.Min(localY, bottom - minH);
        if (unit.Y == 1) bottom = Math.Max(top + minH, bottom + localY);
        var box = new SizeD(Math.Round((right - left) * logical.Width / Size.Width, MidpointRounding.AwayFromZero),
            Math.Round((bottom - top) * logical.Height / Size.Height, MidpointRounding.AwayFromZero));
        if (!(new TextBoxSize { Width = box.Width, Height = box.Height }).IsValid) return null;
        var placement = new LayerTransform
        {
            Origin = Origin, Rotation = Rotation, FlipX = FlipX, FlipY = FlipY, Sampling = Sampling,
            Size = new SizeD(box.Width * Size.Width / logical.Width, box.Height * Size.Height / logical.Height),
        };
        // Growing about the centre would swing the kept corner away, so the anchor is put back by hand —
        // the same compensation UpdateTextLayer uses for point text.
        var anchor = Point(new PointD(left / Size.Width, top / Size.Height));
        var current = placement.Point(PointD.Zero);
        placement.Origin = new PointD(Origin.X + anchor.X - current.X, Origin.Y + anchor.Y - current.Y);
        return placement.IsValid ? (box, placement) : null;
    }

    /// <summary>
    /// This placement mirrored across a vertical line at <paramref name="axis"/> (or, for the
    /// vertical flip, a horizontal one): the picture flips, the angle turns the other way, and the
    /// middle crosses to the other side of the line. The same construction as the macOS
    /// <c>LayerTransform.mirrored(horizontally:across:)</c> (Document/LayerFlip.swift:6-17).
    /// </summary>
    public LayerTransform Mirrored(bool horizontally, double axis)
    {
        var center = Center;
        return new LayerTransform
        {
            Origin = horizontally
                ? new PointD(2 * axis - center.X - Size.Width / 2, Origin.Y)
                : new PointD(Origin.X, 2 * axis - center.Y - Size.Height / 2),
            Size = Size,
            Rotation = -Rotation,
            FlipX = horizontally ? !FlipX : FlipX,
            FlipY = horizontally ? FlipY : !FlipY,
            Sampling = Sampling,
        };
    }

    /// <summary>Whether the document point lands inside the placed rectangle.</summary>
    public bool Contains(PointD point)
    {
        var center = Center;
        double x = point.X - center.X, y = point.Y - center.Y;
        double radians = Radians, cos = Math.Cos(radians), sin = Math.Sin(radians);
        return Math.Abs(x * cos + y * sin) <= Size.Width / 2 && Math.Abs(-x * sin + y * cos) <= Size.Height / 2;
    }

    /// <summary>Carried along by a linked layer's edit; matches macOS following(from:to:).
    /// Uneven scaling can introduce shear, which the rectangle model drops.</summary>
    public LayerTransform Following(LayerTransform old, LayerTransform next)
    {
        var result = new LayerTransform { Origin = Origin, Size = Size, Rotation = Rotation, FlipX = FlipX, FlipY = FlipY, Sampling = Sampling };
        if (old.Size == next.Size && old.Rotation == next.Rotation && old.FlipX == next.FlipX && old.FlipY == next.FlipY)
        {
            result.Origin = new(Origin.X + next.Origin.X - old.Origin.X, Origin.Y + next.Origin.Y - old.Origin.Y);
            return result;
        }
        PointD Map(PointD unit)
        {
            var p = Point(new(FlipX ? 1 - unit.X : unit.X, FlipY ? 1 - unit.Y : unit.Y));
            double x = p.X - old.Center.X, y = p.Y - old.Center.Y;
            double c = Math.Cos(old.Radians), s = Math.Sin(old.Radians);
            double u = (x * c + y * s) / old.Size.Width + .5, v = (-x * s + y * c) / old.Size.Height + .5;
            if (old.FlipX) u = 1 - u;
            if (old.FlipY) v = 1 - v;
            return next.Point(new(next.FlipX ? 1 - u : u, next.FlipY ? 1 - v : v));
        }
        var p0 = Map(new(0, 0)); var px = Map(new(1, 0)); var py = Map(new(0, 1)); var middle = Map(new(.5, .5));
        double a = px.X - p0.X, b = px.Y - p0.Y, c2 = py.X - p0.X, d = py.Y - p0.Y;
        double sign = FlipX ? -1 : 1, angle = Math.Atan2(b * sign, a * sign);
        double along = -c2 * Math.Sin(angle) + d * Math.Cos(angle);
        result.Size = new(Math.Sqrt(a * a + b * b), Math.Abs(along));
        double degrees = angle * 180 / Math.PI;
        result.Rotation = degrees + Math.Round((Rotation - degrees) / 360, MidpointRounding.AwayFromZero) * 360;
        result.FlipY = along < 0;
        result.Origin = new(middle.X - result.Size.Width / 2, middle.Y - result.Size.Height / 2);
        return result;
    }

    public bool Equals(LayerTransform? other) => other is not null
        && Origin == other.Origin && Size == other.Size && Rotation == other.Rotation
        && FlipX == other.FlipX && FlipY == other.FlipY && Sampling == other.Sampling;

    public override bool Equals(object? obj) => Equals(obj as LayerTransform);

    public override int GetHashCode() => HashCode.Combine(Origin, Size, Rotation, FlipX, FlipY, Sampling);

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
