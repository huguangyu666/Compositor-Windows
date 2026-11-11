// Model coordinates and preprocessing follow MobileSAM / Segment Anything / SAM 2 (Apache-2.0),
// Chaoning Zhang et al. and Meta: https://github.com/ChaoningZhang/MobileSAM
// ONNX export: https://huggingface.co/Acly/MobileSAM (Acly). Adapted for premultiplied RGBA.
// Boundary antialiasing reference: Loop/Blinn, GPU Gems 3, chapter 25.5 (implicit-field gradients).
// https://developer.nvidia.com/gpugems/gpugems3/part-iv-image-effects/chapter-25-rendering-vector-art-gpu
// This adapter evaluates the bilinear mask field on the CPU; it does not copy the vector shader.
using Compositor.Core;

namespace Compositor.Segmentation;

public readonly record struct SelectionPoint(PointD Position, bool Include = true);

public sealed class SamGeometry
{
    public const int Side = 1024;
    public int OriginalWidth { get; }
    public int OriginalHeight { get; }
    public int Width { get; }
    public int Height { get; }

    public SamGeometry(int width, int height, bool stretch = false)
    {
        if (width < 1 || height < 1 || width > Limits.MaxSide || height > Limits.MaxSide) throw new ArgumentException("Invalid image dimensions.");
        OriginalWidth = width; OriginalHeight = height;
        double scale = Side / (double)Math.Max(width, height);
        Width = stretch ? Side : Math.Max(1, (int)(width * scale + .5));
        Height = stretch ? Side : Math.Max(1, (int)(height * scale + .5));
    }

    public PointD ToModel(PointD point)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || point.X < 0 || point.Y < 0 || point.X >= OriginalWidth || point.Y >= OriginalHeight)
            throw new ArgumentException("Selection points must be inside the canvas.");
        return new(point.X * Width / OriginalWidth, point.Y * Height / OriginalHeight);
    }

    public static float[] SampleRgb(Surface source, int width, int height, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var rgb = new float[checked(width * height * 3)];
        for (int y = 0; y < height; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            double sy = Math.Clamp((y + .5) * source.Height / height - .5, 0, source.Height - 1);
            int y0 = (int)sy, y1 = Math.Min(y0 + 1, source.Height - 1); double fy = sy - y0;
            for (int x = 0; x < width; x++)
            {
                double sx = Math.Clamp((x + .5) * source.Width / width - .5, 0, source.Width - 1);
                int x0 = (int)sx, x1 = Math.Min(x0 + 1, source.Width - 1); double fx = sx - x0;
                int a = y0 * source.Stride + x0 * 4, b = y0 * source.Stride + x1 * 4,
                    c = y1 * source.Stride + x0 * 4, d = y1 * source.Stride + x1 * 4;
                for (int channel = 0; channel < 3; channel++)
                {
                    double top = White(a) * (1 - fx) + White(b) * fx, bottom = White(c) * (1 - fx) + White(d) * fx;
                    rgb[(y * width + x) * 3 + channel] = (float)(top * (1 - fy) + bottom * fy);
                    int White(int p) => Math.Min(255, source.Pixels[p + channel] + 255 - source.Pixels[p + 3]);
                }
            }
        }
        return rgb;
    }

    public MaskSurface Restore(ReadOnlySpan<float> logits, int width, int height, bool soft, CancellationToken cancellation,
        IReadOnlyList<SelectionPoint>? points = null)
    {
        cancellation.ThrowIfCancellationRequested();
        if (logits.Length != width * height || width < 1 || height < 1) throw new ArgumentException("Invalid model mask.");
        if (points is not null) foreach (var point in points) ToModel(point.Position);
        var mask = new MaskSurface(OriginalWidth, OriginalHeight);
        double stepX = Width * (double)width / (OriginalWidth * Side), stepY = Height * (double)height / (OriginalHeight * Side);
        var columns = new (int Left, int Right, double Weight)[OriginalWidth];
        for (int x = 0; x < columns.Length; x++)
        {
            double sx = Math.Clamp((x + .5) * Width / OriginalWidth / Side * width - .5, 0, width - 1);
            int left = (int)sx; columns[x] = (left, Math.Min(left + 1, width - 1), sx - left);
        }
        for (int y = 0; y < OriginalHeight; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            double sy = Math.Clamp((y + .5) * Height / OriginalHeight / Side * height - .5, 0, height - 1);
            int y0 = (int)sy, y1 = Math.Min(y0 + 1, height - 1); double fy = sy - y0;
            for (int x = 0; x < OriginalWidth; x++)
            {
                var (x0, x1, fx) = columns[x];
                double a = logits[y0 * width + x0], b = logits[y0 * width + x1], c = logits[y1 * width + x0], d = logits[y1 * width + x1];
                double top = a * (1 - fx) + b * fx, bottom = c * (1 - fx) + d * fx;
                double value = top * (1 - fy) + bottom * fy;
                double coverage = value > 0 ? 1 : 0;
                if (soft && Math.Min(Math.Min(a, b), Math.Min(c, d)) <= 0 && Math.Max(Math.Max(a, b), Math.Max(c, d)) > 0)
                {
                    // Confidence is not opacity. Estimate the zero boundary's one-pixel
                    // footprint from the bilinear derivatives; interiors/exteriors stay solid.
                    double dx = ((b - a) * (1 - fy) + (d - c) * fy) * stepX, dy = (bottom - top) * stepY;
                    double footprint = Math.Abs(dx) + Math.Abs(dy);
                    if (footprint > 1e-12) coverage = Math.Clamp(.5 + value / footprint, 0, 1);
                }
                mask.Pixels[y * OriginalWidth + x] = (byte)Math.Round(255 * coverage);
            }
        }
        // A 4x4 decoder-cell area scales with document size; real openings and prompted
        // details survive. Clean the coverage itself so edits agree with the contour.
        double pixelArea = (OriginalWidth * (double)Side / (Width * width)) * (OriginalHeight * (double)Side / (Height * height));
        SamMaskCleanup.Apply(mask, Math.Max(1, (int)Math.Ceiling(16 * pixelArea)), points, cancellation);
        return mask;
    }
}
