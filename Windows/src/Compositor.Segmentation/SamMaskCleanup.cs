// Small-region policy: Segment Anything by Meta Platforms, Inc. and affiliates (Apache-2.0).
// https://github.com/facebookresearch/segment-anything/blob/main/segment_anything/utils/amg.py
// Adapted from remove_small_regions: 8-connected holes/islands and a largest-island fallback.
// This .NET implementation preserves soft coverage, canvas openings and explicit user prompts,
// with a bit per visited pixel instead of OpenCV's full-size integer label image.
using Compositor.Core;

namespace Compositor.Segmentation;

internal static class SamMaskCleanup
{
    internal static void Apply(MaskSurface mask, int area, IReadOnlyList<SelectionPoint>? points, CancellationToken cancellation,
        bool fillHoles = true, bool retainLargeIslands = false)
    {
        cancellation.ThrowIfCancellationRequested();
        int width = mask.Width, height = mask.Height;
        var pixels = mask.Pixels;
        var visited = new ulong[(pixels.Length + 63) / 64];
        var protectedPixels = new HashSet<int>();
        bool hasIncludedRegion = false;
        if (points is not null)
            foreach (var point in points)
            {
                int index = (int)point.Position.Y * width + (int)point.Position.X;
                if ((pixels[index] >= 128) == point.Include)
                { protectedPixels.Add(index); hasIncludedRegion |= point.Include; }
            }
        var queue = new Queue<(int Row, int Left, int Right)>();
        var small = new List<int>();
        var componentRuns = new List<(int Start, int Length)>();
        (int Index, byte Coverage)[] largestRemoved = [];
        bool keptIsland = false;
        if (fillHoles)
        {
            Visit(selected: false); // Fill holes before counting foreground components.
            Array.Clear(visited);
        }
        Visit(selected: true);
        if (!keptIsland)
            foreach (var (index, coverage) in largestRemoved) pixels[index] = coverage;
        // Filled holes and removed components must not leave partly selected halos.
        // Keep antialiasing only where the final thresholded boundary still exists.
        for (int y = 0; y < height; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                int index = y * width + x;
                if (pixels[index] == 0 || pixels[index] == 255) continue;
                bool inside = false, outside = false;
                for (int row = Math.Max(0, y - 1); row <= Math.Min(height - 1, y + 1); row++)
                    for (int column = Math.Max(0, x - 1); column <= Math.Min(width - 1, x + 1); column++)
                        if (pixels[row * width + column] >= 128) inside = true; else outside = true;
                if (!inside) pixels[index] = 0;
                else if (!outside) pixels[index] = 255;
            }
        }

        void Visit(bool selected)
        {
            for (int y = 0; y < height; y++)
            {
                cancellation.ThrowIfCancellationRequested();
                for (int x = 0; x < width; x++)
                {
                    int seed = y * width + x;
                    if (Seen(seed) || (pixels[seed] >= 128) != selected) continue;
                    small.Clear(); componentRuns.Clear();
                    int count = 0;
                    bool keep = false;
                    AddRun(y, x);
                    while (queue.TryDequeue(out var run))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        for (int dy = -1; dy <= 1; dy += 2)
                        {
                            int adjacent = run.Row + dy;
                            if (adjacent < 0 || adjacent >= height) continue;
                            for (int column = Math.Max(0, run.Left - 1); column <= Math.Min(width - 1, run.Right + 1); column++)
                            {
                                int index = adjacent * width + column;
                                if (!Seen(index) && (pixels[index] >= 128) == selected) column = AddRun(adjacent, column);
                            }
                        }
                    }
                    if (selected && hasIncludedRegion && !keep && !retainLargeIslands)
                    { foreach (var run in componentRuns) Array.Fill(pixels, (byte)0, run.Start, run.Length); continue; }
                    if (keep || count > area) { if (selected) keptIsland = true; continue; }
                    if (selected && !keptIsland && count > largestRemoved.Length)
                        largestRemoved = small.Select(index => (index, pixels[index])).ToArray();
                    foreach (int index in small) pixels[index] = selected ? (byte)0 : (byte)255;

                    int AddRun(int row, int column)
                    {
                        int left = column, right = column, offset = row * width;
                        while (left > 0 && !Seen(offset + left - 1) && (pixels[offset + left - 1] >= 128) == selected) left--;
                        while (right + 1 < width && !Seen(offset + right + 1) && (pixels[offset + right + 1] >= 128) == selected) right++;
                        count += right - left + 1;
                        if (!selected && (row == 0 || row == height - 1 || left == 0 || right == width - 1)) keep = true;
                        foreach (int point in protectedPixels) if (point >= offset + left && point <= offset + right) keep = true;
                        if (keep) componentRuns.Clear();
                        else if (selected && hasIncludedRegion && !retainLargeIslands) componentRuns.Add((offset + left, right - left + 1));
                        for (int col = left; col <= right; col++)
                        {
                            int index = offset + col; visited[index >> 6] |= 1UL << (index & 63);
                            if (count <= area) small.Add(index);
                        }
                        if (count > area) small.Clear();
                        queue.Enqueue((row, left, right)); return right;
                    }
                }
            }
        }

        bool Seen(int index) => (visited[index >> 6] & (1UL << (index & 63))) != 0;
    }
}
