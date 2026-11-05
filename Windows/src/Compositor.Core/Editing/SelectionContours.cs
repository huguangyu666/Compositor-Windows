using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Compositor.Core;

public readonly record struct SelectionEdge(int X0, int Y0, int X1, int Y1);

/// <summary>Pixel-exact outline runs at the coverage midpoint, independent of UI objects.</summary>
public static class SelectionContours
{
    public static SelectionEdge[] Build(Selection selection, CancellationToken cancellationToken = default)
    {
        var mask = selection.OutlineCoverage;
        var bounds = selection.SourceBounds;
        int offsetX = (int)selection.OutlineOffset.X, offsetY = (int)selection.OutlineOffset.Y;
        int left = Math.Clamp(bounds.X, 0, mask.Width), top = Math.Clamp(bounds.Y, 0, mask.Height);
        int right = Math.Clamp(bounds.X + Math.Max(0, bounds.Width), left, mask.Width);
        int bottom = Math.Clamp(bounds.Y + Math.Max(0, bounds.Height), top, mask.Height);
        if (left == right || top == bottom) return [];
        var edges = new List<SelectionEdge>();
        var verticalStarts = new int[right - left + 1]; Array.Fill(verticalStarts, -1);
        bool Selected(int x, int y) => x >= 0 && x < mask.Width && y >= 0 && y < mask.Height
            && mask.Pixels[y * mask.Width + x] >= 128;

        // Both orientations walk rows, keeping coverage access contiguous instead of
        // scanning the full mask again by columns. Vertical starts carry between rows.
        for (int y = top; y <= bottom; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int horizontalStart = -1;
            for (int x = left; x <= right;)
            {
                if (Avx2.IsSupported && x <= right - 32 && y < bottom && horizontalStart < 0)
                {
                    int row = y * mask.Width + x;
                    int bits = Avx2.MoveMask(Vector256.LoadUnsafe(ref mask.Pixels[row]).AsSByte());
                    if ((bits == 0 || bits == -1)
                        && bits == (y > 0 ? Avx2.MoveMask(Vector256.LoadUnsafe(ref mask.Pixels[row - mask.Width]).AsSByte()) : 0)
                        && Selected(x - 1, y) == (bits == -1)
                        && (y == top || Selected(x - 1, y - 1) == (bits == -1)))
                    {
                        // Matching uniform rows and their left neighbors contain no edge,
                        // nor a vertical run still open from the preceding row. The high
                        // bits are exactly the coverage >= 128 test, including AA pixels.
                        x += 32;
                        continue;
                    }
                }
                int end = Avx2.IsSupported ? Math.Min(right + 1, x + 32) : right + 1;
                for (; x < end; x++)
                {
                    bool inside = Selected(x, y);
                    bool horizontal = x < right && inside != Selected(x, y - 1);
                    if (horizontal && horizontalStart < 0) horizontalStart = x;
                    if (!horizontal && horizontalStart >= 0)
                    {
                        edges.Add(new(horizontalStart + offsetX, y + offsetY, x + offsetX, y + offsetY)); horizontalStart = -1;
                    }
                    bool vertical = y < bottom && inside != Selected(x - 1, y);
                    int index = x - left, start = verticalStarts[index];
                    if (vertical && start < 0) verticalStarts[index] = y;
                    if (!vertical && start >= 0)
                    {
                        edges.Add(new(x + offsetX, start + offsetY, x + offsetX, y + offsetY)); verticalStarts[index] = -1;
                    }
                }
            }
        }
        return edges.ToArray();
    }
}
