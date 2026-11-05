#if DEBUG
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.System;

namespace Compositor.App;

public sealed partial class MainWindow
{
    // Keep the pre-batching construction as an independent performance and rendering control.
    private static async Task<PathGeometry> BuildLegacySelectionOutlineAsync(SelectionEdge[] edges)
    {
        var geometry = new PathGeometry(); var batch = Stopwatch.StartNew();
        for (int i = 0; i < edges.Length; i++)
        {
            var edge = edges[i];
            var figure = new PathFigure { StartPoint = new Point(edge.X0, edge.Y0) };
            figure.Segments.Add(new LineSegment { Point = new Point(edge.X1, edge.Y1) });
            geometry.Figures.Add(figure);
            if ((i & 255) == 255 && batch.Elapsed.TotalMilliseconds >= 2)
            { await Task.Yield(); batch.Restart(); }
        }
        return geometry;
    }

    private static void RequireOutlineRuns(Geometry? geometry, SelectionEdge[] edges)
    {
        int index = 0;
        foreach (var figure in SelectionOutlineFigures(geometry))
        {
            Require(index < edges.Length, "Outline added a contour run.");
            var edge = edges[index++];
            Require(figure.StartPoint == new Point(edge.X0, edge.Y0) && !figure.IsClosed && figure.IsFilled
                && figure.Segments.Count == 1 && figure.Segments[0] is LineSegment line
                && line.Point == new Point(edge.X1, edge.Y1), $"Outline changed run {index - 1}.");
        }
        Require(index == edges.Length, $"Outline lost contour runs ({index}/{edges.Length}).");
    }

    private async Task VerifySelectionOutlineBatchesAsync(Dictionary<string, object> report)
    {
        var previousTool = tool; double previousZoom = viewport.Zoom, previousPanX = viewport.PanX, previousPanY = viewport.PanY;
        bool followedFit = viewport.FollowsFit;
        var scene = EditorDocument.Create(128, 128); OpenDocument(scene); await WaitForFramesAsync();
        var ring = new MaskSurface(7, 7); ring.Pixels.AsSpan().Fill(255);
        for (int y = 2; y < 5; y++) for (int x = 2; x < 5; x++) ring.Pixels[y * 7 + x] = 0;
        var threshold = new MaskSurface(3, 3, [0, 127, 128, 255, 128, 127, 0, 255, 0]);
        var noise = new MaskSurface(64, 64); var random = new Random(871); byte[] coverage = [0, 127, 128, 255];
        for (int i = 0; i < noise.Pixels.Length; i++) noise.Pixels[i] = coverage[random.Next(4)];
        var noisy = new Selection(noise, new RectI(0, 0, 64, 64));
        foreach (var selection in new[] { new Selection(ring, new(0, 0, 7, 7)), new Selection(threshold, new(0, 0, 3, 3)), noisy })
        {
            scene.SetSelection(selection); await WaitForSelectionReadyAsync();
            var expected = SelectionContours.Build(selection);
            RequireOutlineRuns(selectionOutlineGeometry, expected);
            if (expected.Length > SelectionOutlineRunsPerBatch)
                Require(selectionOutlineGeometry is GeometryGroup group
                    && group.Children.Count == (expected.Length + SelectionOutlineRunsPerBatch - 1) / SelectionOutlineRunsPerBatch,
                    "Large outline did not retain bounded native batches.");
        }
        report["selectionOutlineBatchesExactThresholdHolesAndRuns"] = "passed";

        var geometry = selectionOutlineGeometry!;
        var translated = noisy.Translated(-7, 11); scene.SetSelection(translated);
        Require(ReferenceEquals(geometry, SelectionOutline.Data) && outlineTranslation.X == -7 && outlineTranslation.Y == 11,
            "Grouped outline translation rebuilt geometry or lost its offset.");
        scene.SetSelection(SelectionCommands.Modify(translated, SelectionModification.Feather, 3));
        Require(ReferenceEquals(geometry, SelectionOutline.Data), "Grouped outline feather rebuilt geometry.");

        var replacement = Selection.Rectangle(128, 128, 8, 8, 24, 24); bool replaced = false;
        ResetOutlineBuildProbe();
        beforeOutlineBatch = batch => { if (batch == 1 && !replaced) { replaced = true; scene.SetSelection(replacement); } };
        try
        {
            scene.SetSelection(new Selection(new MaskSurface(64, 64, noise.Pixels.ToArray()), new(0, 0, 64, 64)));
            await WaitForSelectionReadyAsync();
            Require(replaced && ReferenceEquals(scene.ActiveSelection, replacement)
                && OutlineBuildProbe.AbandonedAt == SelectionOutlineRunsPerBatch,
                "Superseded native outline did not stop at the next batch.");
            RequireOutlineRuns(selectionOutlineGeometry, SelectionContours.Build(replacement));
        }
        finally { beforeOutlineBatch = null; }
        bool retired = false; var nextScene = EditorDocument.Create(64, 64);
        beforeOutlineBatch = batch => { if (batch == 1 && !retired) { retired = true; OpenDocument(nextScene); } };
        try
        {
            scene.SetSelection(noisy); await WaitForSelectionReadyAsync(); await Task.Yield();
            Require(retired && ReferenceEquals(document, nextScene) && SelectionOutline.Data is null,
                "Retired native outline published into the replacement document.");
        }
        finally { beforeOutlineBatch = null; }
        report["selectionOutlineBatchesReuseCancelAndRetire"] = "passed";

        OpenDocument(scene); scene.SetSelection(noisy); await WaitForSelectionReadyAsync(); await WaitForFramesAsync();
        geometry = selectionOutlineGeometry!;
        var legacy = await BuildLegacySelectionOutlineAsync(SelectionContours.Build(noisy));
        async Task<byte[]> Render(Geometry? data)
        {
            SelectionOutline.Data = data; SelectionOutline.RenderTransform = null; SelectionOutline.Visibility = Visibility.Visible;
            RootGrid.UpdateLayout();
            var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(CanvasStage);
            return (await bitmap.GetPixelsAsync()).ToArray();
        }
        var observations = new List<object>();
        report["selectionOutlinePixelComparison"] = observations;
        foreach (double zoom in new[] { .5, 1.0, 2.0, .273 })
        {
            viewport.SetZoom(zoom, viewport.Center); FlushViewport();
            var origin = viewport.Origin;
            viewport.Pan(new(viewport.Center.X - 32 * viewport.PointsPerPixel - origin.X + .25,
                viewport.Center.Y - 32 * viewport.PointsPerPixel - origin.Y + .375)); FlushViewport();
            var blank = await Render(null); var before = await Render(legacy); var after = await Render(geometry);
            Require(before.Length > 0 && before.Length == after.Length && !before.AsSpan().SequenceEqual(blank),
                "Outline pixel comparison captured no visible outline.");
            int changed = 0, maxDelta = 0;
            for (int i = 0; i < before.Length; i++)
                if (before[i] != after[i]) { changed++; maxDelta = Math.Max(maxDelta, Math.Abs(before[i] - after[i])); }
            observations.Add(new { zoom, changedBytes = changed, maxDelta, totalBytes = before.Length });
            if (zoom != .273) Require(changed == 0, $"Outline rendering changed at zoom {zoom} ({changed} bytes).");
        }
        SelectionOutline.Data = geometry; SelectionOutline.RenderTransform = outlineTranslation;
        report["selectionOutlineBatchesPixelsAndFractionalObservation"] = "passed";

        SetViewportZoom(1); FlushViewport(); // Keep the rotation handle outside the corner's hit radius.
        var pixels = scene.Snapshot.Images[scene.Snapshot.Manifest.ActiveLayerId!.Value];
        OnFreeTransform(this, new()); await WaitForFramesAsync();
        Require(floatingTransform is not null && selectionOutlineGeometry is GeometryGroup, "Grouped floating outline did not start.");
        ChangeTransformNumber(TransformField.X, CurrentTransformValue!.Origin.X + 4); await WaitForFramesAsync();
        Require(ReferenceEquals(geometry, SelectionOutline.Data), "Grouped affine transform rebuilt its outline.");
        var start = CurrentTransformValue!.Point(TransformDrag.Handles[0]);
        BeginTransformPointer(start, VirtualKeyModifiers.Control); await WaitForFramesAsync();
        report["selectionOutlineBatchesCornerMode"] = transformDrag?.Mode.ToString() ?? "none";
        Require(transformDrag?.Mode == TransformDragMode.Distort && transformCorners is not null,
            "Grouped perspective fixture did not enter corner distortion.");
        MoveTransformPointer(new(start.X - 4.25, start.Y - 2.5), VirtualKeyModifiers.None);
        FinishPlacementDrag(); await WaitForFramesAsync();
        int figures = SelectionContours.Build(noisy).Length;
        await WaitForTransformStateAsync(() => floatingOutlinePublishedRevision == floatingOutlineRevision
            && SelectionOutline.Data is { } mapped && !ReferenceEquals(mapped, selectionOutlineGeometry)
            && SelectionOutlineFigures(mapped).Count() == figures);
        var first = SelectionOutlineFigures(geometry).First().StartPoint;
        var expectedFirst = DistortWarp.DocumentMap(floatingTransform!.Edit.Value, transformCorners!)(new(first.X, first.Y));
        var mappedFirst = SelectionOutlineFigures(SelectionOutline.Data).First().StartPoint;
        Near(expectedFirst.X, mappedFirst.X, "Grouped perspective outline X"); Near(expectedFirst.Y, mappedFirst.Y, "Grouped perspective outline Y");
        AssertTransformUpload(LayerDisplaySnapshot!); FinishTransformDraft(); await WaitForFramesAsync(); await WaitForSelectionReadyAsync();
        Require(scene.CanUndo, "Grouped floating merge did not create history.");
        OnUndo(this, new()); await WaitForFramesAsync(); await WaitForSelectionReadyAsync();
        Require(!scene.CanUndo && ReferenceEquals(noisy, scene.ActiveSelection)
            && ReferenceEquals(pixels, scene.Snapshot.Images[scene.Snapshot.Manifest.ActiveLayerId!.Value]),
            "Grouped floating merge Undo did not restore source pixels and selection.");
        report["selectionOutlineBatchesFloatingAffinePerspectiveAndUndo"] = "passed";
        ToolCombo.SelectedIndex = (int)previousTool;
        await WaitForTransformStateAsync(() => tool == previousTool && ToolCombo.SelectedIndex == (int)previousTool
            && toolChangeApplied == toolChangeRevision);
        if (followedFit) FitToWindow();
        else { SetViewportZoom(previousZoom); viewport.Pan(new(previousPanX - viewport.PanX, previousPanY - viewport.PanY)); }
        FlushViewport();
    }
}
#endif
