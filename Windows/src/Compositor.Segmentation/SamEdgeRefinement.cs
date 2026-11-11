/*M///////////////////////////////////////////////////////////////////////////////////////
//
//  IMPORTANT: READ BEFORE DOWNLOADING, COPYING, INSTALLING OR USING.
//
//  By downloading, copying, installing or using the software you agree to this license.
//  If you do not agree to this license, do not download, install,
//  copy or use the software.
//
//
//                        Intel License Agreement
//                For Open Source Computer Vision Library
//
// Copyright (C) 2000, Intel Corporation, all rights reserved.
// Third party copyrights are property of their respective owners.
//
// Redistribution and use in source and binary forms, with or without modification,
// are permitted provided that the following conditions are met:
//
//   * Redistribution's of source code must retain the above copyright notice,
//     this list of conditions and the following disclaimer.
//
//   * Redistribution's in binary form must reproduce the above copyright notice,
//     this list of conditions and the following disclaimer in the documentation
//     and/or other materials provided with the distribution.
//
//   * The name of Intel Corporation may not be used to endorse or promote products
//     derived from this software without specific prior written permission.
//
// This software is provided by the copyright holders and contributors "as is" and
// any express or implied warranties, including, but not limited to, the implied
// warranties of merchantability and fitness for a particular purpose are disclaimed.
// In no event shall the Intel Corporation or contributors be liable for any direct,
// indirect, incidental, special, exemplary, or consequential damages
// (including, but not limited to, procurement of substitute goods or services;
// loss of use, data, or profits; or business interruption) however caused
// and on any theory of liability, whether in contract, strict liability,
// or tort (including negligence or otherwise) arising in any way out of
// the use of this software, even if advised of the possibility of such damage.
//
//M*/
// Original-resolution narrow-band GrabCut adaptation. Color mixtures and contrast weights follow
// OpenCV's grabcut.cpp (Intel Open Source Computer Vision license, copyright 2000 Intel).
// https://github.com/opencv/opencv/blob/4.x/modules/imgproc/src/grabcut.cpp
// The image-graph solver is adapted from OpenCV's detail/gcgraph.hpp under the same Intel license.
// https://github.com/opencv/opencv/blob/4.x/modules/imgproc/include/opencv2/imgproc/detail/gcgraph.hpp
// Adapted to bounded managed arrays, deterministic sampled color fitting, fixed semantic interiors,
// cancellation, and local RGB alpha estimation. No OpenCV binary or extra model is required.
using Compositor.Core;
using System.Numerics;
using System.Diagnostics;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Compositor.Segmentation.Tests")]

namespace Compositor.Segmentation;

public static class SamEdgeRefinement
{
    private const int MaximumNodes = 300000;

    public static MaskSurface Refine(Surface image, MaskSurface mask, bool soft,
        IReadOnlyList<SelectionPoint>? points, CancellationToken cancellation, Action<string, double>? timing = null)
    {
        long started = Stopwatch.GetTimestamp();
        void Measure(string stage) { timing?.Invoke(stage, Stopwatch.GetElapsedTime(started).TotalMilliseconds); started = Stopwatch.GetTimestamp(); }
        cancellation.ThrowIfCancellationRequested();
        if (image.Width != mask.Width || image.Height != mask.Height) throw new ArgumentException("Image and mask dimensions must match.");
        int width = mask.Width, height = mask.Height, length = mask.Pixels.Length;
        var constraints = new Dictionary<int, bool>();
        if (points is not null)
            foreach (var point in points)
            {
                if (!double.IsFinite(point.Position.X) || !double.IsFinite(point.Position.Y)
                    || point.Position.X < 0 || point.Position.Y < 0 || point.Position.X >= width || point.Position.Y >= height)
                    throw new ArgumentException("Selection points must be inside the canvas.");
                constraints[(int)point.Position.Y * width + (int)point.Position.X] = point.Include;
            }
        int radius = Math.Clamp((int)Math.Ceiling(Math.Max(width, height) / 128.0), 4, 24);
        var distance = new byte[length]; Array.Fill(distance, (byte)(radius + 1));
        // Two distance passes locate only the uncertain border; the canvas edge is not background.
        for (int y = 0; y < height; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x, d = distance[i]; bool inside = Inside(i);
                if (x > 0) d = Math.Min(d, Inside(i - 1) != inside ? 1 : distance[i - 1] + 1);
                if (y > 0) d = Math.Min(d, Inside(i - width) != inside ? 1 : distance[i - width] + 1);
                distance[i] = (byte)d;
            }
        }
        int count = 0;
        for (int y = height - 1; y >= 0; y--)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int x = width - 1; x >= 0; x--)
            {
                int i = y * width + x, d = distance[i]; bool inside = Inside(i);
                if (x + 1 < width) d = Math.Min(d, Inside(i + 1) != inside ? 1 : distance[i + 1] + 1);
                if (y + 1 < height) d = Math.Min(d, Inside(i + width) != inside ? 1 : distance[i + width] + 1);
                distance[i] = (byte)d;
                if (d <= radius) count++;
            }
        }
        if (count == 0 || count > MaximumNodes) return mask.Clone();
        Measure("distance");

        var foreground = new List<Vector3>(); var background = new List<Vector3>();
        double contrast = 0; int contrastCount = 0, step = Math.Max(1, length / 16384);
        for (int i = 0; i < length; i += step)
        {
            if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
            var color = Color(image, i); (Inside(i) ? foreground : background).Add(color);
            if (i % width + 1 < width) { contrast += Vector3.DistanceSquared(color, Color(image, i + 1)); contrastCount++; }
            if (i + width < length) { contrast += Vector3.DistanceSquared(color, Color(image, i + width)); contrastCount++; }
        }
        if (foreground.Count == 0 || background.Count == 0) return mask.Clone();
        var fg = ColorMixture.Fit(foreground, cancellation); var bg = ColorMixture.Fit(background, cancellation);
        Measure("mixtures");
        // A sampled grid can miss an aligned color edge. Zero sampled contrast uses
        // constant neighbor weights, as in GrabCut; it must not skip color fitting.
        double beta = contrast > 1e-5 ? contrastCount / (2 * contrast) : 0;
        var ids = new int[length]; Array.Fill(ids, -1);
        var pixels = new int[count]; int node = 0;
        for (int i = 0; i < length; i++) if (distance[i] <= radius) { ids[i] = node; pixels[node++] = i; }
        int source = count, sink = count + 1;
        var graph = new CutGraph(count + 2, count * 12 + 8);
        for (node = 0; node < count; node++)
        {
            if ((node & 1023) == 0) cancellation.ThrowIfCancellationRequested();
            int i = pixels[node], x = i % width, y = i / width;
            var color = Color(image, i);
            double delta = Math.Clamp(fg.Cost(color) - bg.Cost(color), -24, 24) + (Inside(i) ? -.5 : .5);
            double fromSource = Math.Max(0, -delta), toSink = Math.Max(0, delta);
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0 || x + dx < 0 || x + dx >= width || y + dy < 0 || y + dy >= height) continue;
                    int j = i + dy * width + dx, other = ids[j];
                    if (other >= 0 && other <= node) continue;
                    double weight = 25 * Math.Exp(-beta * Vector3.DistanceSquared(color, Color(image, j)));
                    if (dx != 0 && dy != 0) weight *= .7071067811865476;
                    if (other >= 0) { if (other > node) graph.Add(node, other, (float)weight, (float)weight); }
                    else if (Inside(j)) fromSource += weight; else toSink += weight;
                }
            if (constraints.TryGetValue(i, out bool include)) { if (include) fromSource += 100000; else toSink += 100000; }
            // Remove the constant terminal contribution before constructing the graph.
            double terminal = fromSource - toSink;
            if (terminal > 0) graph.Add(source, node, (float)terminal, 0);
            else if (terminal < 0) graph.Add(node, sink, (float)-terminal, 0);
        }
        Measure("graph");
        var selected = graph.Solve(source, sink, cancellation);
        Measure("solve");
        var result = mask.Clone();
        for (node = 0; node < count; node++) result.Pixels[pixels[node]] = selected[node] ? (byte)255 : (byte)0;
        // The cut can detach small background-colored fragments from the coarse contour.
        // Keep explicit details and large components; retain the newly fitted openings.
        SamMaskCleanup.Apply(result, radius * radius, points, cancellation, fillHoles: false, retainLargeIslands: true);
        if (soft) EstimateAlpha(image, result, pixels, cancellation);
        Measure("alpha");
        foreach (var (index, include) in constraints) result.Pixels[index] = include ? (byte)255 : (byte)0;
        return result;

        bool Inside(int i) => mask.Pixels[i] >= 128;
    }

    private static Vector3 Color(Surface image, int i)
    {
        int p = i / image.Width * image.Stride + i % image.Width * 4, white = 255 - image.Pixels[p + 3];
        return new(Math.Min(255, image.Pixels[p] + white), Math.Min(255, image.Pixels[p + 1] + white), Math.Min(255, image.Pixels[p + 2] + white));
    }

    private static void EstimateAlpha(Surface image, MaskSurface result, int[] pixels, CancellationToken cancellation)
    {
        int width = result.Width, height = result.Height;
        // Read an immutable hard result while publishing fractional coverage to its copy.
        var hard = result.Pixels.ToArray();
        bool Inside(int x, int y) => hard[y * width + x] >= 128;
        bool Solid(int x, int y) => (x == 0 || Inside(x - 1, y) == Inside(x, y))
            && (x + 1 == width || Inside(x + 1, y) == Inside(x, y))
            && (y == 0 || Inside(x, y - 1) == Inside(x, y)) && (y + 1 == height || Inside(x, y + 1) == Inside(x, y));
        for (int n = 0; n < pixels.Length; n++)
        {
            if ((n & 1023) == 0) cancellation.ThrowIfCancellationRequested();
            int i = pixels[n], x = i % width, y = i / width;
            if (Solid(x, y)) continue;
            Vector3 foreground = default, background = default; int fg = 0, bg = 0;
            for (int row = Math.Max(0, y - 3); row <= Math.Min(height - 1, y + 3); row++)
                for (int column = Math.Max(0, x - 3); column <= Math.Min(width - 1, x + 3); column++)
                {
                    if (!Solid(column, row)) continue;
                    var color = Color(image, row * width + column);
                    if (Inside(column, row)) { foreground += color; fg++; } else { background += color; bg++; }
                }
            if (fg == 0 || bg == 0) continue;
            foreground /= fg; background /= bg; var difference = foreground - background;
            float denominator = difference.LengthSquared();
            if (denominator < 25) continue;
            float alpha = Math.Clamp(Vector3.Dot(Color(image, i) - background, difference) / denominator, 0, 1);
            result.Pixels[i] = (byte)MathF.Round(alpha * 255);
        }
    }

    private sealed class ColorMixture
    {
        private readonly Gaussian[] components;
        private ColorMixture(Gaussian[] components) => this.components = components;
        private readonly record struct Gaussian(Vector3 Mean, double RR, double RG, double RB, double GG, double GB, double BB, double Scale)
        {
            public double Probability(Vector3 value)
            {
                var d = value - Mean;
                double energy = d.X * d.X * RR + d.Y * d.Y * GG + d.Z * d.Z * BB
                    + 2 * (d.X * d.Y * RG + d.X * d.Z * RB + d.Y * d.Z * GB);
                return Scale * Math.Exp(-.5 * Math.Max(0, energy));
            }
        }

        public double Cost(Vector3 value)
        {
            double probability = 0;
            foreach (var component in components) probability += component.Probability(value);
            return -Math.Log(Math.Max(1e-30, probability));
        }

        public static ColorMixture Fit(List<Vector3> samples, CancellationToken cancellation)
        {
            int count = Math.Min(5, samples.Count); var means = new Vector3[count]; means[0] = samples[0];
            var nearest = Enumerable.Repeat(float.PositiveInfinity, samples.Count).ToArray();
            for (int k = 1; k < count; k++)
            {
                int farthest = 0;
                for (int i = 0; i < samples.Count; i++)
                {
                    nearest[i] = Math.Min(nearest[i], Vector3.DistanceSquared(samples[i], means[k - 1]));
                    if (nearest[i] > nearest[farthest]) farthest = i;
                }
                means[k] = samples[farthest];
            }
            var labels = new int[samples.Count]; Array.Fill(labels, -1);
            var sizes = new int[count]; var sums = new Vector3[count];
            for (int iteration = 0; iteration < 10; iteration++)
            {
                cancellation.ThrowIfCancellationRequested(); Array.Clear(sizes); Array.Clear(sums); bool changed = false;
                for (int i = 0; i < samples.Count; i++)
                {
                    int best = 0; float distance = float.PositiveInfinity;
                    for (int k = 0; k < count; k++)
                    {
                        float d = Vector3.DistanceSquared(samples[i], means[k]);
                        if (d < distance) { distance = d; best = k; }
                    }
                    changed |= labels[i] != best; labels[i] = best; sums[best] += samples[i]; sizes[best]++;
                }
                for (int k = 0; k < count; k++) if (sizes[k] > 0) means[k] = sums[k] / sizes[k];
                if (!changed) break;
            }
            var components = new List<Gaussian>();
            for (int k = 0; k < count; k++)
            {
                if (sizes[k] == 0) continue;
                double rr = 0, rg = 0, rb = 0, gg = 0, gb = 0, bb = 0;
                for (int i = 0; i < samples.Count; i++)
                {
                    if (labels[i] != k) continue;
                    var d = samples[i] - means[k];
                    rr += d.X * d.X; rg += d.X * d.Y; rb += d.X * d.Z;
                    gg += d.Y * d.Y; gb += d.Y * d.Z; bb += d.Z * d.Z;
                }
                rr = rr / sizes[k] + 1; rg /= sizes[k]; rb /= sizes[k];
                gg = gg / sizes[k] + 1; gb /= sizes[k]; bb = bb / sizes[k] + 1;
                double ir = gg * bb - gb * gb, ig = rb * gb - rg * bb, ib = rg * gb - rb * gg;
                double determinant = Math.Max(1e-12, rr * ir + rg * ig + rb * ib);
                components.Add(new(means[k], ir / determinant, ig / determinant, ib / determinant,
                    (rr * bb - rb * rb) / determinant, (rg * rb - rr * gb) / determinant,
                    (rr * gg - rg * rg) / determinant, sizes[k] / (double)samples.Count / Math.Sqrt(determinant)));
            }
            return new(components.ToArray());
        }
    }

    internal sealed class CutGraph(int size, int capacity)
    {
        private readonly int[] head = new int[size];
        private readonly int[] target = new int[capacity], next = new int[capacity];
        private readonly float[] residual = new float[capacity];
        private readonly float[] terminal = new float[size];
        private int edgeCount = 2;

        public void Add(int from, int to, float forward, float backward)
        {
            if (from == size - 2) { terminal[to] += forward; return; }
            if (to == size - 1) { terminal[from] -= forward; return; }
            target[edgeCount] = to; residual[edgeCount] = forward; next[edgeCount] = head[from]; head[from] = edgeCount++;
            target[edgeCount] = from; residual[edgeCount] = backward; next[edgeCount] = head[to]; head[to] = edgeCount++;
        }

        public bool[] Solve(int source, int sink, CancellationToken cancellation)
        {
            const int root = -1, orphan = -2;
            int count = size - 2, timestamp = 0, operations = 0;
            var parent = new int[count]; var stamp = new int[count]; var distance = new int[count];
            var sinkTree = new bool[size]; sinkTree[sink] = true;
            var queued = new bool[count]; var active = new Queue<int>(); var orphans = new Stack<int>();
            for (int i = 0; i < count; i++)
                if (terminal[i] != 0) { parent[i] = root; distance[i] = 1; sinkTree[i] = terminal[i] < 0; Activate(i); }
            while (true)
            {
                if ((operations++ & 1023) == 0) cancellation.ThrowIfCancellationRequested();
                int bridge = 0;
                // Retain the current active vertex when a bridge is found, as in GCGraph.
                while (active.TryPeek(out int v))
                {
                    if ((operations++ & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                    if (parent[v] != 0)
                    {
                        int tree = sinkTree[v] ? 1 : 0;
                        for (int e = head[v]; e != 0; e = next[e])
                        {
                            if (residual[e ^ tree] <= 1e-6f) continue;
                            int u = target[e];
                            if (parent[u] == 0)
                            {
                                sinkTree[u] = sinkTree[v]; parent[u] = e ^ 1; stamp[u] = stamp[v]; distance[u] = distance[v] + 1;
                                Activate(u); continue;
                            }
                            if (sinkTree[u] != sinkTree[v]) { bridge = e ^ tree; break; }
                            if (distance[u] > distance[v] + 1 && stamp[u] <= stamp[v])
                            { parent[u] = e ^ 1; stamp[u] = stamp[v]; distance[u] = distance[v] + 1; }
                        }
                        if (bridge != 0) break;
                    }
                    active.Dequeue(); queued[v] = false;
                }
                if (bridge == 0)
                {
                    // A saturated terminal can leave an unowned zero-cost component. Use
                    // residual reachability so its old tree labels cannot split a free tie.
                    var selected = new bool[size]; selected[source] = true;
                    var pending = new Queue<int>();
                    for (int i = 0; i < count; i++)
                        if (terminal[i] > 1e-6f) { selected[i] = true; pending.Enqueue(i); }
                    while (pending.TryDequeue(out int i))
                        for (int e = head[i]; e != 0; e = next[e])
                            if (residual[e] > 1e-6f && !selected[target[e]])
                            { selected[target[e]] = true; pending.Enqueue(target[e]); }
                    return selected;
                }

                float amount = residual[bridge];
                for (int tree = 1; tree >= 0; tree--)
                {
                    int v = target[bridge ^ tree];
                    while (parent[v] > 0)
                    { int e = parent[v]; amount = Math.Min(amount, residual[e ^ tree]); v = target[e]; }
                    amount = Math.Min(amount, Math.Abs(terminal[v]));
                }
                residual[bridge] -= amount; residual[bridge ^ 1] += amount;
                for (int tree = 1; tree >= 0; tree--)
                {
                    int v = target[bridge ^ tree];
                    while (parent[v] > 0)
                    {
                        int e = parent[v]; residual[e ^ (tree ^ 1)] += amount; residual[e ^ tree] -= amount;
                        if (residual[e ^ tree] <= 1e-6f) { residual[e ^ tree] = 0; parent[v] = orphan; orphans.Push(v); }
                        v = target[e];
                    }
                    terminal[v] += amount * (1 - tree * 2);
                    if (Math.Abs(terminal[v]) <= 1e-6f) { terminal[v] = 0; parent[v] = orphan; orphans.Push(v); }
                }
                timestamp++;
                while (orphans.TryPop(out int v))
                {
                    if ((operations++ & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                    int best = 0, shortest = int.MaxValue, tree = sinkTree[v] ? 1 : 0;
                    for (int e = head[v]; e != 0; e = next[e])
                    {
                        if (residual[e ^ (tree ^ 1)] <= 1e-6f) continue;
                        int u = target[e]; if (sinkTree[u] != sinkTree[v] || parent[u] == 0) continue;
                        int d = 0;
                        while (true)
                        {
                            if (stamp[u] == timestamp) { d += distance[u]; break; }
                            int p = parent[u]; d++;
                            if (p < 0)
                            {
                                if (p == orphan) d = int.MaxValue - 1;
                                else { stamp[u] = timestamp; distance[u] = 1; }
                                break;
                            }
                            u = target[p];
                        }
                        if (++d >= int.MaxValue) continue;
                        if (d < shortest) { shortest = d; best = e; }
                        u = target[e];
                        while (stamp[u] != timestamp)
                        { stamp[u] = timestamp; distance[u] = --d; u = target[parent[u]]; }
                    }
                    parent[v] = best;
                    if (best != 0) { stamp[v] = timestamp; distance[v] = shortest; continue; }
                    stamp[v] = 0;
                    for (int e = head[v]; e != 0; e = next[e])
                    {
                        int u = target[e], p = parent[u];
                        if (sinkTree[u] != sinkTree[v] || p == 0) continue;
                        if (residual[e ^ (tree ^ 1)] > 1e-6f) Activate(u);
                        if (p > 0 && target[p] == v) { parent[u] = orphan; orphans.Push(u); }
                    }
                }
            }

            void Activate(int vertex)
            {
                if (!queued[vertex]) { active.Enqueue(vertex); queued[vertex] = true; }
            }
        }
    }
}
