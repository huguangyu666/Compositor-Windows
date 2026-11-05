using Compositor.Core;
using Xunit;

namespace Compositor.Core.Tests;

public class SelectionContourTests
{
    [Fact]
    public void AllSmallMasksPreserveEveryBoundaryEdgeIncludingHoles()
    {
        for (int pattern = 0; pattern < 512; pattern++)
        {
            var mask = new MaskSurface(3, 3);
            var expected = new HashSet<(int X0, int Y0, int X1, int Y1)>();
            void Toggle((int, int, int, int) edge) { if (!expected.Add(edge)) expected.Remove(edge); }
            for (int i = 0; i < 9; i++)
            {
                if ((pattern & (1 << i)) == 0) continue;
                mask.Pixels[i] = 255;
                int x = i % 3, y = i / 3;
                Toggle((x, y, x + 1, y)); Toggle((x, y + 1, x + 1, y + 1));
                Toggle((x, y, x, y + 1)); Toggle((x + 1, y, x + 1, y + 1));
            }
            var actual = new HashSet<(int, int, int, int)>();
            foreach (var edge in SelectionContours.Build(Selection.FromCoverage(mask)))
            {
                for (int x = edge.X0; x < edge.X1; x++) Assert.True(actual.Add((x, edge.Y0, x + 1, edge.Y0)));
                for (int y = edge.Y0; y < edge.Y1; y++) Assert.True(actual.Add((edge.X0, y, edge.X0, y + 1)));
            }
            Assert.True(expected.SetEquals(actual), $"Mask {pattern} changed its boundary.");
        }
    }

    [Fact]
    public void CoverageMidpointAndFeatherKeepTheExistingOutline()
    {
        var mask = new MaskSurface(4, 1, [0, 127, 128, 255]);
        var selection = Selection.FromCoverage(mask);
        var edges = SelectionContours.Build(selection);
        Assert.Equal(SelectionContours.Build(Selection.Rectangle(4, 1, 2, 0, 2, 1)).OrderBy(edge => edge.ToString()),
            edges.OrderBy(edge => edge.ToString()));
        Assert.Equal(edges, SelectionContours.Build(selection.Feathered(20)));
    }

    [Fact]
    public void CancelledContourWorkStopsBeforeScanning()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => SelectionContours.Build(Selection.Rectangle(4, 4, 0, 0, 4, 4), cancellation.Token));
    }

    [Theory]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(97)]
    [InlineData(129)]
    public void WideUniformRunsKeepBoundaryOrderAtHolesThresholdsAndClippedBounds(int width)
    {
        var mask = new MaskSurface(width, 12); byte[] levels = [0, 127, 128, 255];
        var random = new Random(1729 + width);
        for (int y = 0; y < mask.Height; y++) for (int x = 0; x < width; x++)
            mask.Pixels[y * width + x] = y switch
            {
                0 or 1 or 2 or 7 or 8 => 255,
                3 or 4 => 127,
                5 => x == 32 || x == 64 ? (byte)255 : (byte)0,
                6 => levels[random.Next(levels.Length)],
                _ => 0,
            };
        foreach (int left in new[] { 0, 1, 3 })
        foreach (int top in new[] { 0, 1 })
        {
            var selection = new Selection(mask, new(left, top, width - left, mask.Height - top), 3).Translated(-9, 11);
            Assert.Equal(ScalarContourReference(selection), SelectionContours.Build(selection));
        }
    }

    // Retain the pre-batch scanner as an ordered output oracle: native path batching also
    // depends on where runs appear in the array, not just on the set of boundary pixels.
    private static SelectionEdge[] ScalarContourReference(Selection selection)
    {
        var mask = selection.OutlineCoverage; var bounds = selection.SourceBounds;
        int dx = (int)selection.OutlineOffset.X, dy = (int)selection.OutlineOffset.Y;
        int left = Math.Clamp(bounds.X, 0, mask.Width), top = Math.Clamp(bounds.Y, 0, mask.Height);
        int right = Math.Clamp(bounds.X + Math.Max(0, bounds.Width), left, mask.Width);
        int bottom = Math.Clamp(bounds.Y + Math.Max(0, bounds.Height), top, mask.Height);
        var edges = new List<SelectionEdge>();
        var starts = Enumerable.Repeat(-1, right - left + 1).ToArray();
        bool Selected(int x, int y) => x >= 0 && x < mask.Width && y >= 0 && y < mask.Height && mask.Pixels[y * mask.Width + x] >= 128;
        for (int y = top; y <= bottom; y++)
        {
            int horizontalStart = -1;
            for (int x = left; x <= right; x++)
            {
                bool inside = Selected(x, y);
                bool horizontal = x < right && inside != Selected(x, y - 1);
                if (horizontal && horizontalStart < 0) horizontalStart = x;
                if (!horizontal && horizontalStart >= 0)
                {
                    edges.Add(new(horizontalStart + dx, y + dy, x + dx, y + dy)); horizontalStart = -1;
                }
                bool vertical = y < bottom && inside != Selected(x - 1, y);
                int index = x - left, start = starts[index];
                if (vertical && start < 0) starts[index] = y;
                if (!vertical && start >= 0)
                {
                    edges.Add(new(x + dx, start + dy, x + dx, y + dy)); starts[index] = -1;
                }
            }
        }
        return edges.ToArray();
    }
}
