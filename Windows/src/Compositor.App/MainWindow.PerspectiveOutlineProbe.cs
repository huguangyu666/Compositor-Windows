#if DEBUG
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task VerifyPerspectiveOutlineCostAsync(Dictionary<string, object> report, bool stageOnly = false,
        bool traceFrames = false, bool hideOutline = false)
    {
        var wasTool = tool; double wasZoom = viewport.Zoom; bool wasFit = viewport.FollowsFit;
        var scene = EditorDocument.Create(4096, 4096);
        var points = Enumerable.Range(0, 240).Select(step =>
        {
            double angle = step * Math.PI / 120, radius = step % 2 == 0 ? 1500 : 1200;
            return new PointD(2048 + radius * Math.Cos(angle), 2048 + radius * Math.Sin(angle));
        }).ToArray();
        var selection = await Task.Run(() => LassoSelection.Rasterize(4096, 4096, points));
        scene.SetSelection(selection); OpenDocument(scene); await WaitForSelectionReadyAsync(); await WaitForFramesAsync();
        OnFreeTransform(this, new()); await WaitForFramesAsync();
        Require(floatingTransform is not null, "Large perspective outline fixture did not float.");
        var original = CurrentTransformValue!;
        var observations = new List<object>(); report["perspectiveOutlineSamples"] = observations;
        report["perspectiveOutlineMode"] = stageOnly ? "outline-stage-only" : "complete-frame";
        var timelines = new List<object>();
        double maxPublicationQueueMs = 0;
        if (traceFrames) report["perspectiveFrameTimelines"] = timelines;
        if (traceFrames) report["perspectiveFrameOutlineHidden"] = hideOutline;
        for (int step = 0; step < 3; step++)
        {
            var corners = DistortWarp.CornersOf(original);
            corners[0] = new(corners[0].X - 20.25 - step * 10, corners[0].Y - 10.5 - step * 5);
            int priorPublications = floatingOutlinePublications;
            var total = Stopwatch.StartNew();
            var events = new System.Collections.Concurrent.ConcurrentQueue<(string Name, double At)>();
            if (traceFrames) frameTimelineProbe = name =>
            {
                events.Enqueue((name, total.Elapsed.TotalMilliseconds));
                if (name is "publication-queued" or "publication-entered")
                    App.Log($"frame latency trace step={step} event={name} at={total.Elapsed.TotalMilliseconds:F3}ms");
                if (hideOutline && name is "input-applied" or "draft-published") SelectionOutline.Visibility = Visibility.Collapsed;
            };
            if (stageOnly) { transformCorners = corners; UpdateFloatingSelectionOutline(); }
            else
            {
                QueueCanvasInput(scene, () => SetTransformCorners(corners));
                try { await WaitForFramesAsync(); }
                catch
                {
                    report["perspectiveOutlineFrameTimeoutState"] = new { builds = floatingOutlineBuilds,
                        publications = floatingOutlinePublications, processedRuns = floatingOutlineProcessedRuns,
                        workerBusy, HasPendingInteraction, HasPendingDirty };
                    throw;
                }
            }
            if (traceFrames) events.Enqueue(("frame-wait-observed", total.Elapsed.TotalMilliseconds));
            Require(await WaitUntilAsync(() => floatingOutlinePublications > priorPublications
                && floatingOutlinePublishedRevision == floatingOutlineRevision, 20000), "Large perspective outline did not publish.");
            double readyMs = total.Elapsed.TotalMilliseconds;
            if (traceFrames)
            {
                events.Enqueue(("outline-wait-observed", readyMs)); frameTimelineProbe = null;
                var queued = new Queue<double>();
                foreach (var item in events)
                {
                    if (item.Name == "publication-queued") queued.Enqueue(item.At);
                    else if (item.Name == "publication-entered")
                    {
                        Require(queued.TryDequeue(out double at), "Publication timeline lost a queued frame.");
                        maxPublicationQueueMs = Math.Max(maxPublicationQueueMs, item.At - at);
                    }
                }
                Require(queued.Count == 0, "Publication timeline ended before a queued frame arrived.");
                timelines.Add(new { step, events = events.Select(item => new { name = item.Name, atMs = item.At }).ToArray() });
            }
            double verificationMs = 0;
            if (!stageOnly) { var verification = Stopwatch.StartNew(); AssertTransformUpload(LayerDisplaySnapshot!); verificationMs = verification.Elapsed.TotalMilliseconds; }
            observations.Add(new { step, runs = floatingOutlineRuns, buildMs = floatingOutlineLastMs,
                prepareMs = floatingOutlinePrepareMs, uiMs = floatingOutlineUiMs,
                queueMs = floatingOutlineQueueMs, computeMs = floatingOutlineComputeMs,
                nativeActiveMs = floatingOutlineNativeActiveMs, gen2Collections = floatingOutlineGen2Collections,
                parseMs = floatingOutlineParseMs, addMs = floatingOutlineAddMs,
                readyMs, verificationMs, builds = floatingOutlineBuilds, publications = floatingOutlinePublications });
            if (traceFrames) report[$"outlineRetirementAfterFrame{step}"] = new { roots = retiredOutlineRoots.Count,
                figures = retiredOutlineFigures, yields = retiredOutlineYields, maxSliceMs = retiredOutlineMaxSliceMs };
        }
        if (traceFrames)
        {
            report["perspectiveFrameMaxPublicationQueueMs"] = maxPublicationQueueMs;
            Require(maxPublicationQueueMs < 200, $"Retired outlines blocked frame publication for {maxPublicationQueueMs:F3}ms.");
            report["perspectiveFramePublicationQueueRemainsResponsive"] = "passed";
        }
        Require(floatingOutlineRuns > 10_000, "Large perspective outline lost contour runs.");
        int before = floatingOutlineBuilds;
        for (int repeat = 0; repeat < 30; repeat++) UpdateFloatingSelectionOutline();
        report["perspectiveOutlineRepeatNotifications"] = new { calls = 30, buildsBefore = before, buildsAfter = floatingOutlineBuilds };
        Require(floatingOutlineBuilds == before, "Identical perspective notifications restarted outline construction.");
        var legacyClock = Stopwatch.StartNew();
        var legacy = await BuildLegacyProjectedOutlineAsync(selectionOutlineGeometry!, floatingTransform!.Edit.Value,
            transformInspectorValue!, selection.OutlineOffset, transformCorners!);
        double legacyMs = legacyClock.Elapsed.TotalMilliseconds;
        report["outlineRetirementBeforeCostAssertion"] = new { roots = retiredOutlineRoots.Count,
            figures = retiredOutlineFigures, maxSliceMs = retiredOutlineMaxSliceMs };
        report["outlineRetirementRootsBeforeCostAssertion"] = OutlineRetirementDiagnosticState();
        report["perspectiveOutlineLegacyControl"] = new { legacyMs, candidateMs = floatingOutlineLastMs,
            candidateOverLegacy = floatingOutlineLastMs / legacyMs };
        Require(floatingOutlineLastMs < legacyMs * .5, "Projected outline did not halve the retained legacy construction time.");
        RequireProjectedFiguresEqual(legacy, SelectionOutline.Data);
        RetireOutline(legacy);
        report["perspectiveOutlineCostAndNotificationReuse"] = "passed";
        QueueTransformCancel(); await WaitForFramesAsync();
        bool drained = await WaitUntilAsync(() => !retiringOutlines, OutlineDrainBudgetMs);
        report["outlineRetirementAtEnd"] = new { roots = retiredOutlineRoots.Count, figures = retiredOutlineFigures,
            yields = retiredOutlineYields, maxSliceMs = retiredOutlineMaxSliceMs,
            peakRoots = retiredOutlinePeakRoots, runsPerBatch = SelectionOutlineRunsPerBatch };
        report["outlineRetirementRootDetailsAtEnd"] = OutlineRetirementDiagnosticState();
        Require(drained, "Perspective outline retirement did not drain after cancellation.");
        ToolCombo.SelectedIndex = (int)wasTool;
        await WaitForTransformStateAsync(() => tool == wasTool && toolChangeApplied == toolChangeRevision);
        if (wasFit) FitToWindow(); else SetViewportZoom(wasZoom);
        FlushViewport();
    }

    private static async Task<PathGeometry> BuildLegacyProjectedOutlineAsync(Geometry source, LayerTransform original,
        LayerTransform value, PointD offset, PointD[] corners)
    {
        var carry = DistortWarp.DocumentMap(original, corners);
        Point Carry(Point p)
        {
            var unit = BrushEngine.DocumentToPixel(original, 1, 1, new(p.X + offset.X, p.Y + offset.Y));
            var mapped = carry(original.Point(new(value.FlipX ? 1 - unit.X : unit.X, value.FlipY ? 1 - unit.Y : unit.Y)));
            return new(mapped.X, mapped.Y);
        }
        var geometry = new PathGeometry(); var batch = Stopwatch.StartNew(); int count = 0;
        foreach (var figure in SelectionOutlineFigures(source))
        {
            var mapped = new PathFigure { StartPoint = Carry(figure.StartPoint), IsClosed = figure.IsClosed, IsFilled = figure.IsFilled };
            foreach (var segment in figure.Segments.OfType<LineSegment>()) mapped.Segments.Add(new LineSegment { Point = Carry(segment.Point) });
            geometry.Figures.Add(mapped);
            if ((++count & 255) == 0 && batch.Elapsed.TotalMilliseconds >= 2) { await Task.Yield(); batch.Restart(); }
        }
        return geometry;
    }

    private static void RequireProjectedFiguresEqual(Geometry expected, Geometry? actual)
    {
        using var wanted = SelectionOutlineFigures(expected).GetEnumerator();
        using var observed = SelectionOutlineFigures(actual).GetEnumerator();
        int count = 0;
        while (wanted.MoveNext())
        {
            Require(observed.MoveNext(), "Projected outline lost a run.");
            var first = wanted.Current; var second = observed.Current;
            Require(first.StartPoint == second.StartPoint && first.IsClosed == second.IsClosed && first.IsFilled == second.IsFilled
                && first.Segments.Count == 1 && second.Segments.Count == 1
                && first.Segments[0] is LineSegment a && second.Segments[0] is LineSegment b && a.Point == b.Point,
                $"Projected outline changed native figure {count} ({first.StartPoint}/{second.StartPoint}).");
            count++;
        }
        Require(!observed.MoveNext(), "Projected outline added a run.");
    }

    private async Task VerifyPerspectiveOutlineOwnershipAsync(Dictionary<string, object> report)
    {
        var wasTool = tool; double wasZoom = viewport.Zoom; bool wasFit = viewport.FollowsFit;
        var mask = new MaskSurface(64, 64); var random = new Random(211);
        for (int i = 0; i < mask.Pixels.Length; i++) mask.Pixels[i] = (byte)(random.Next(2) * 255);
        var selection = new Selection(mask, new(0, 0, 64, 64)).Translated(-3, 7);
        var scene = EditorDocument.Create(128, 128); scene.SetSelection(selection);
        OpenDocument(scene); await WaitForSelectionReadyAsync(); await WaitForFramesAsync();
        OnFreeTransform(this, new()); await WaitForFramesAsync();
        Require(floatingTransform is not null, "Projected outline ownership fixture did not float.");
        var original = floatingTransform!.Edit.Value; var source = selectionOutlineGeometry!;
        var comparisons = new List<object>(); report["perspectiveOutlinePixelControls"] = comparisons;
        foreach ((bool flipX, bool flipY) in new[] { (false, false), (true, false), (false, true), (true, true) })
        {
            var value = CopyTransform(original); value.FlipX = flipX; value.FlipY = flipY;
            var corners = DistortWarp.CornersOf(original); corners[0] = new(corners[0].X - 4.25, corners[0].Y - 2.5);
            transformInspectorValue = value; transformCorners = corners; UpdateFloatingSelectionOutline();
            await WaitForTransformStateAsync(() => !floatingOutlineBusy && floatingOutlinePublishedRevision == floatingOutlineRevision);
            var actual = SelectionOutline.Data!;
            var legacy = await BuildLegacyProjectedOutlineAsync(source, original, value, selection.OutlineOffset, corners);
            RequireProjectedFiguresEqual(legacy, actual);
            foreach (double zoom in new[] { 1.0, 2.0, .273 })
            {
                SetViewportZoom(zoom); FlushViewport();
                async Task<byte[]> Render(Geometry? data)
                {
                    SelectionOutline.Data = data; SelectionOutline.RenderTransform = null; RootGrid.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(CanvasStage);
                    return (await bitmap.GetPixelsAsync()).ToArray();
                }
                var blank = await Render(null); var expected = await Render(legacy); var pixels = await Render(actual);
                Require(expected.Length == pixels.Length && !expected.AsSpan().SequenceEqual(blank), "Projected outline pixel control was empty.");
                int different = expected.Zip(pixels).Count(pair => pair.First != pair.Second);
                comparisons.Add(new { flipX, flipY, zoom, differentBytes = different });
                Require(different == 0, $"Projected outline pixels changed for flips {flipX}/{flipY}, zoom {zoom}: {different} bytes.");
            }
            SelectionOutline.Data = actual;
        }
        report["perspectiveOutlineExactPointsFlipsOffsetAndPixels"] = "passed";

        int starts = floatingOutlineBuilds, publications = floatingOutlinePublications, abandoned = floatingOutlineAbandoned;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(false);
        bool sawCancellation = false;
        beforeFloatingOutlinePrepare = token =>
        {
            entered.TrySetResult();
            try { release.Wait(token); }
            finally { sawCancellation = token.IsCancellationRequested; }
        };
        try
        {
            var first = DistortWarp.CornersOf(original); first[0] = new(-8, -2);
            transformCorners = first; UpdateFloatingSelectionOutline(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Require(floatingOutlineWorkers == 1 && !workerBusy, "Outline preparation did not leave the UI and render worker available.");
            var visible = SelectionOutline.Data;
            for (int i = 0; i < 80; i++)
            {
                var next = first.ToArray(); next[0] = new(-9 - i * .125, -3 - i * .0625);
                transformCorners = next; UpdateFloatingSelectionOutline();
            }
            Require(floatingOutlineBuilds == starts + 1 && floatingOutlineWorkers == 1 && ReferenceEquals(visible, SelectionOutline.Data),
                "Superseded outline work flooded workers or changed the retained preview before publication.");
        }
        finally { beforeFloatingOutlinePrepare = null; release.Set(); }
        await WaitForTransformStateAsync(() => !floatingOutlineBusy && floatingOutlinePublishedRevision == floatingOutlineRevision);
        Require(sawCancellation && floatingOutlinePublications == publications + 1 && floatingOutlineAbandoned > abandoned && floatingOutlineMaxWorkers == 1,
            "Superseded outline published or ran overlapping preparations.");
        var finalLegacy = await BuildLegacyProjectedOutlineAsync(source, original, transformInspectorValue!, selection.OutlineOffset, transformCorners!);
        RequireProjectedFiguresEqual(finalLegacy, SelectionOutline.Data);

        bool replaced = false;
        beforeFloatingOutlineBatch = batch =>
        {
            if (batch != 1 || replaced) return;
            replaced = true; var next = transformCorners!.ToArray(); next[0] = new(-25.5, -7.25);
            transformCorners = next; UpdateFloatingSelectionOutline();
        };
        try
        {
            var next = transformCorners!.ToArray(); next[0] = new(-24.5, -6.25); transformCorners = next; UpdateFloatingSelectionOutline();
            await WaitForTransformStateAsync(() => replaced && !floatingOutlineBusy && floatingOutlinePublishedRevision == floatingOutlineRevision);
        }
        finally { beforeFloatingOutlineBatch = null; }
        foreach (bool fold in new[] { false, true })
        {
            publications = floatingOutlinePublications;
            var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var unblock = new ManualResetEventSlim(false);
            beforeFloatingOutlinePrepare = token => { blocked.TrySetResult(); unblock.Wait(token); };
            try
            {
                var next = DistortWarp.CornersOf(original); next[0] = new(-27.5, -9.25);
                transformCorners = next; UpdateFloatingSelectionOutline(); await blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (fold) { next[0] = new(next[2].X + 5, next[2].Y + 5); transformCorners = next; }
                else transformCorners = null;
                UpdateFloatingSelectionOutline();
            }
            finally { beforeFloatingOutlinePrepare = null; unblock.Set(); }
            await WaitForTransformStateAsync(() => !floatingOutlineBusy);
            Require(floatingOutlinePublications == publications
                && (fold ? SelectionOutline.Visibility == Visibility.Collapsed : ReferenceEquals(SelectionOutline.Data, source)),
                "Cancelled perspective work overwrote an affine or folded outline.");
        }
        Require(floatingOutlineFailures == 0, "Projected outline preparation logged a failure.");
        report["perspectiveOutlineLatestOnlySingleWorkerAndBatchCancel"] = "passed";

        var retired = EditorDocument.Create(32, 32); bool changedOwner = false;
        beforeFloatingOutlineBatch = batch => { if (batch == 1 && !changedOwner) { changedOwner = true; OpenDocument(retired); } };
        try
        {
            var next = transformCorners!.ToArray(); next[0] = new(-26.5, -8.25); transformCorners = next; UpdateFloatingSelectionOutline();
            await WaitForTransformStateAsync(() => changedOwner && !floatingOutlineBusy);
            Require(ReferenceEquals(document, retired) && SelectionOutline.Data is null && !retired.CanUndo,
                "Retired projected outline changed replacement document state.");
        }
        finally { beforeFloatingOutlineBatch = null; }
        report["perspectiveOutlineRetirementRejectsOldPublication"] = "passed";
        ToolCombo.SelectedIndex = (int)wasTool;
        await WaitForTransformStateAsync(() => tool == wasTool && toolChangeApplied == toolChangeRevision);
        if (wasFit) FitToWindow(); else SetViewportZoom(wasZoom); FlushViewport();
    }
}
#endif
