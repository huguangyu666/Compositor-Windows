using System.Buffers.Binary;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Compositor.Core;

/// <summary>
/// Free distortion, ported from DistortWarp.swift: the layer's four corners move
/// independently. A convex shape is warped in perspective (inverse-homography sampling);
/// a folded one (a corner pulled past its neighbours) is warped as two triangles meeting
/// along the diagonal, each with its own affine map. Output pixels are inverse-mapped into
/// the source and bilinearly sampled, which is what Core Image's perspective filter does.
/// </summary>
public static partial class DistortWarp
{
    /// <summary>The transform's corners in handle order: TL, TR, BR, BL.</summary>
    public static PointD[] CornersOf(LayerTransform transform)
    {
        (double, double)[] units = [(0, 0), (1, 0), (1, 1), (0, 1)];
        return units.Select(unit => transform.Point(new PointD(unit.Item1, unit.Item2))).ToArray();
    }

    private static double Area(PointD a, PointD b, PointD c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    private readonly record struct Perspective(double A, double B, double C, double D, double E, double F, double G, double H)
    {
        public PointD Map(PointD p)
        { double w = G * p.X + H * p.Y + 1; return new((A * p.X + B * p.Y + C) / w, (D * p.X + E * p.Y + F) / w); }
        public PointD? Inverse(PointD p)
        {
            double m11 = A - p.X * G, m12 = B - p.X * H, m21 = D - p.Y * G, m22 = E - p.Y * H;
            double determinant = m11 * m22 - m12 * m21;
            return Math.Abs(determinant) < 1e-12 ? null : new((m22 * (p.X - C) - m12 * (p.Y - F)) / determinant,
                (m11 * (p.Y - F) - m21 * (p.X - C)) / determinant);
        }
    }
    private static Perspective Mapping(PointD[] c)
    {
        double sx = c[0].X - c[1].X + c[2].X - c[3].X, sy = c[0].Y - c[1].Y + c[2].Y - c[3].Y;
        double g = 0, h = 0;
        if (Math.Abs(sx) > 1e-9 || Math.Abs(sy) > 1e-9)
        {
            double dx1 = c[1].X - c[2].X, dx2 = c[3].X - c[2].X, dy1 = c[1].Y - c[2].Y, dy2 = c[3].Y - c[2].Y;
            double denominator = dx1 * dy2 - dx2 * dy1;
            if (Math.Abs(denominator) > 1e-12) { g = (sx * dy2 - dx2 * sy) / denominator; h = (dx1 * sy - sx * dy1) / denominator; }
        }
        return new(c[1].X - c[0].X + g * c[1].X, c[3].X - c[0].X + h * c[3].X, c[0].X,
            c[1].Y - c[0].Y + g * c[1].Y, c[3].Y - c[0].Y + h * c[3].Y, c[0].Y, g, h);
    }
    internal static Func<PointD, PointD> Forward(PointD[] corners) => Mapping(corners).Map;

    public static Func<PointD, PointD> DocumentMap(LayerTransform transform, PointD[] corners)
    {
        var map = Forward(corners); var placement = TransformEdit.Copy(transform); placement.FlipX = placement.FlipY = false;
        return point => map(BrushEngine.DocumentToPixel(placement, 1, 1, point));
    }

    public static PointD[] Carried(LayerTransform placement, LayerTransform transform, PointD[] corners) =>
        CornersOf(placement).Select(DocumentMap(transform, corners)).ToArray();
    internal static Func<PointD, PointD?> Inverse(PointD[] corners)
    {
        if (IsConvex(corners)) return Mapping(corners).Inverse;
        var first = Affine((0, 0), (1, 0), (1, 1), (corners[0].X, corners[0].Y), (corners[1].X, corners[1].Y), (corners[2].X, corners[2].Y));
        var second = Affine((0, 0), (1, 1), (0, 1), (corners[0].X, corners[0].Y), (corners[2].X, corners[2].Y), (corners[3].X, corners[3].Y));
        // BrushRaster.draw uses Copy: the second drawn half owns overlapping pixels.
        return p => InverseTriangle(second, p, false) ?? InverseTriangle(first, p, true);
    }

    /// <summary>Four finite corners with area to both diagonal halves.</summary>
    public static bool IsUsable(PointD[] corners)
    {
        if (corners.Length != 4) return false;
        if (!corners.All(c => double.IsFinite(c.X) && double.IsFinite(c.Y) && Math.Abs(c.X) <= 1_000_000 && Math.Abs(c.Y) <= 1_000_000)) return false;
        return Math.Abs(Area(corners[0], corners[1], corners[2])) > 0.01 && Math.Abs(Area(corners[0], corners[2], corners[3])) > 0.01;
    }

    /// <summary>Convex, wound consistently either way (a mirrored shape counts).</summary>
    public static bool IsConvex(PointD[] corners)
    {
        if (!IsUsable(corners)) return false;
        int sign = 0;
        for (int index = 0; index < 4; index++)
        {
            var a = corners[index];
            var b = corners[(index + 1) % 4];
            var c = corners[(index + 2) % 4];
            double cross = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
            if (Math.Abs(cross) <= 0.01) return false;
            if (sign == 0) sign = cross < 0 ? -1 : 1;
            else if ((cross < 0) != (sign < 0)) return false;
        }
        return true;
    }

    /// <summary>The affine map taking three source points to three destination points.</summary>
    private static (double A, double B, double C, double D, double Tx, double Ty)? Affine(
        (double X, double Y) s0, (double X, double Y) s1, (double X, double Y) s2,
        (double X, double Y) t0, (double X, double Y) t1, (double X, double Y) t2)
    {
        double uX = s1.X - s0.X, uY = s1.Y - s0.Y, vX = s2.X - s0.X, vY = s2.Y - s0.Y;
        double uuX = t1.X - t0.X, uuY = t1.Y - t0.Y, vvX = t2.X - t0.X, vvY = t2.Y - t0.Y;
        double det = uX * vY - vX * uY;
        if (Math.Abs(det) <= 1e-9) return null;
        double a = (uuX * vY - vvX * uY) / det;
        double c = (vvX * uX - uuX * vX) / det;
        double b = (uuY * vY - vvY * uY) / det;
        double d = (vvY * uX - uuY * vX) / det;
        double tx = t0.X - (a * s0.X + c * s0.Y);
        double ty = t0.Y - (b * s0.X + d * s0.Y);
        return (a, b, c, d, tx, ty);
    }

    /// <summary>
    /// Inverse maps the folded shape through its two affine triangle halves. Null outside it.
    /// </summary>
    private static PointD? InverseTriangle((double A, double B, double C, double D, double Tx, double Ty)? map, PointD point, bool below)
    {
        if (map is not { } m) return null;
        double determinant = m.A * m.D - m.B * m.C;
        if (Math.Abs(determinant) < 1e-12) return null;
        double rx = point.X - m.Tx, ry = point.Y - m.Ty;
        double u = (m.D * rx - m.C * ry) / determinant, v = (m.A * ry - m.B * rx) / determinant;
        if (u < 0 || v < 0 || u > 1 || v > 1 || below && u < v || !below && u > v) return null;
        return new(u, v);
    }

    /// <summary>
    /// Warps `image` (the layer's pixels at their own size) so its corners land on
    /// `corners` — in document coordinates. Returns the warped pixels over the shape's
    /// whole-pixel bounds and the axis-aligned transform for them.
    /// </summary>
    public static (Surface Image, LayerTransform Transform) Warp(
        Surface image, LayerTransform transform, PointD[] corners, int? limit = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (placed, width, height, factor) = Geometry(transform, corners, limit);
        int boundX = (int)placed.Origin.X, boundY = (int)placed.Origin.Y;
        var imageCorners = ImageCorners(transform, corners); var result = new Surface(width, height);
        if (IsConvex(imageCorners))
        {
            WarpPerspectiveImage(image, result, Mapping(imageCorners), boundX, boundY, factor, cancellationToken);
            return (result, placed);
        }
        var inverse = Inverse(imageCorners);
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                // Sample at the output pixel's center, in shape coordinates.
                var unit = inverse(new(boundX + (x + .5) / factor, boundY + (y + .5) / factor));
                if (unit is not { } uv) continue;
                // Outside the unit square the shape doesn't cover this pixel — leave it empty
                // (SampleBilinear clamps, so an unclamped range check is required here).
                if (uv.X < 0 || uv.X > 1 || uv.Y < 0 || uv.Y > 1) continue;
                double sx = uv.X * image.Width - 0.5;
                double sy = uv.Y * image.Height - 0.5;
                int ix = (int)Math.Floor(sx), iy = (int)Math.Floor(sy);
                double fx = sx - ix, fy = sy - iy;
                int outIndex = y * result.Stride + x * 4;
                SampleBilinear(image, ix, iy, fx, fy, result.Pixels, outIndex);
            }
        }
        return (result, placed);
    }

    /// <summary>Distort.swift's gray mask warp: one output plane, with its background outside the shape.</summary>
    public static (MaskSurface Mask, LayerTransform Transform) WarpMask(MaskSurface mask, LayerTransform transform,
        PointD[] corners, byte background = 0, int? limit = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (placed, width, height, factor) = Geometry(transform, corners, limit);
        // A uniform mask already covers the shape, including its outside background.
        if (mask.Width == 1 && mask.Height == 1) return (mask, placed);
        var imageCorners = ImageCorners(transform, corners); var result = new MaskSurface(width, height);
        if (background != 0) result.Pixels.AsSpan().Fill(background);
        int boundX = (int)placed.Origin.X, boundY = (int)placed.Origin.Y;
        if (IsConvex(imageCorners))
        {
            WarpPerspectiveMask(mask, result, Mapping(imageCorners), boundX, boundY, factor, cancellationToken);
            return (result, placed);
        }
        var inverse = Inverse(imageCorners);
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                var unit = inverse(new(boundX + (x + .5) / factor, boundY + (y + .5) / factor));
                if (unit is not { } uv || uv.X < 0 || uv.X > 1 || uv.Y < 0 || uv.Y > 1) continue;
                double sx = uv.X * mask.Width - .5, sy = uv.Y * mask.Height - .5;
                int ix = (int)Math.Floor(sx), iy = (int)Math.Floor(sy);
                double fx = sx - ix, fy = sy - iy;
                int x0 = Math.Clamp(ix, 0, mask.Width - 1), x1 = Math.Clamp(ix + 1, 0, mask.Width - 1);
                int top = Math.Clamp(iy, 0, mask.Height - 1) * mask.Width;
                int bottom = Math.Clamp(iy + 1, 0, mask.Height - 1) * mask.Width;
                double a = mask.Pixels[top + x0] + (mask.Pixels[top + x1] - mask.Pixels[top + x0]) * fx;
                double b = mask.Pixels[bottom + x0] + (mask.Pixels[bottom + x1] - mask.Pixels[bottom + x0]) * fx;
                result.Pixels[y * width + x] = (byte)Math.Clamp((int)Math.Round(a + (b - a) * fy), 0, 255);
            }
        }
        return (result, placed);
    }

    private static (LayerTransform Placement, int Width, int Height, double Factor) Geometry(LayerTransform transform, PointD[] corners, int? limit)
    {
        if (limit is <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        if (!IsUsable(corners)) throw new ArgumentOutOfRangeException(nameof(corners), "Distorted shape is unusable.");
        double minX = corners.Min(c => c.X), minY = corners.Min(c => c.Y);
        int boundX = (int)Math.Floor(minX);
        int boundY = (int)Math.Floor(minY);
        int boundW = Math.Max(1, (int)Math.Ceiling(corners.Max(c => c.X)) - boundX);
        int boundH = Math.Max(1, (int)Math.Ceiling(corners.Max(c => c.Y)) - boundY);
        if (boundW > Limits.MaxSide || boundH > Limits.MaxSide || (long)boundW * boundH > Limits.MaxSurfacePixels)
            throw new ArgumentOutOfRangeException(nameof(corners), "Distorted shape exceeds the supported limits.");
        var placed = new LayerTransform
        {
            Origin = new PointD(boundX, boundY),
            Size = new SizeD(boundW, boundH),
            Sampling = transform.Sampling,
        };
        double factor = limit is { } cap ? Math.Min(1, (double)cap / Math.Max(boundW, boundH)) : 1;
        int width = Math.Max(1, (int)Math.Ceiling(boundW * factor)), height = Math.Max(1, (int)Math.Ceiling(boundH * factor));
        return (placed, width, height, factor);
    }

    private static PointD[] ImageCorners(LayerTransform transform, PointD[] corners)
    {
        PointD Corner(int x, int y)
        {
            int index = (transform.FlipY ? 1 - y : y) * 2 + (transform.FlipX ? 1 - x : x);
            return corners[index switch { 2 => 3, 3 => 2, _ => index }];
        }
        // Flips move the image corners before the source splits its TL-BR diagonal.
        return [Corner(0, 0), Corner(1, 0), Corner(1, 1), Corner(0, 1)];
    }

    private static void SampleBilinear(Surface image, int ix, int iy, double fx, double fy, byte[] output, int index)
    {
        int x0 = Math.Clamp(ix, 0, image.Width - 1) * 4, x1 = Math.Clamp(ix + 1, 0, image.Width - 1) * 4;
        int top = Math.Clamp(iy, 0, image.Height - 1) * image.Stride;
        int bottom = Math.Clamp(iy + 1, 0, image.Height - 1) * image.Stride;
        var pixels = image.Pixels;
        if (Avx.IsSupported && Sse41.IsSupported)
        {
            var a = ReadChannels(pixels, top + x0); var b = ReadChannels(pixels, bottom + x0);
            var weightX = Vector256.Create(fx); var weightY = Vector256.Create(fy);
            // Independent double lanes retain the scalar lerp order and rounding.
            // Keep multiply/add separate rather than fusing either interpolation.
            var upper = Avx.Add(a, Avx.Multiply(Avx.Subtract(ReadChannels(pixels, top + x1), a), weightX));
            var lower = Avx.Add(b, Avx.Multiply(Avx.Subtract(ReadChannels(pixels, bottom + x1), b), weightX));
            var value = Avx.Add(upper, Avx.Multiply(Avx.Subtract(lower, upper), weightY));
            var rounded = Avx.RoundToNearestInteger(value);
            var clamped = Avx.Max(Vector256<double>.Zero, Avx.Min(Vector256.Create(255d), rounded));
            var integers = Avx.ConvertToVector128Int32WithTruncation(clamped);
            var shorts = Sse2.PackSignedSaturate(integers, Vector128<int>.Zero);
            uint packed = Sse2.PackUnsignedSaturate(shorts, Vector128<short>.Zero).AsUInt32().ToScalar();
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(index, 4), packed);
            return;
        }
        for (int channel = 0; channel < 4; channel++)
        {
            double a = pixels[top + x0 + channel], b = pixels[bottom + x0 + channel];
            double upper = a + (pixels[top + x1 + channel] - a) * fx;
            double lower = b + (pixels[bottom + x1 + channel] - b) * fx;
            output[index + channel] = (byte)Math.Clamp((int)Math.Round(upper + (lower - upper) * fy), 0, 255);
        }
    }

    private static Vector256<double> ReadChannels(byte[] pixels, int index)
    {
        // Read only this packed pixel, including when it is the last one in a row.
        uint packed = BinaryPrimitives.ReadUInt32LittleEndian(pixels.AsSpan(index, 4));
        return Avx.ConvertToVector256Double(Sse41.ConvertToVector128Int32(Vector128.CreateScalar(packed).AsByte()));
    }
}
