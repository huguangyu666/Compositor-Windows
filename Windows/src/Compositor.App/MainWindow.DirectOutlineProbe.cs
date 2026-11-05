#if DEBUG
using Compositor.Core;
using System.Diagnostics;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.System;

namespace Compositor.App;

public sealed partial class MainWindow
{
    internal async Task RunDirectOutlineProbeAsync()
    {
        var report = new Dictionary<string, object>();
        try
        {
            await Task.Delay(500);
            await VerifyDirectOutlineSuiteAsync(report);
            report["status"] = "passed";
        }
        catch (Exception ex) { report["status"] = "failed"; report["error"] = ex.ToString(); }
        finally { WriteProbeReport("direct-outline-probe.json", report); forceClose = true; Close(); }
    }

    private async Task VerifyDirectOutlineSuiteAsync(Dictionary<string, object> report)
    {
        var scene = EditorDocument.Create(128, 128);
        var selection = Selection.Rectangle(128, 128, 17, 19, 43, 37); scene.SetSelection(selection);
        OpenDocument(scene); await WaitForFramesAsync();
        Require(await WaitUntilAsync(() => selectionOutlineEdges is not null, 10000), "Outline extraction did not finish.");
        Require(SelectionOutline.Data is null && selectionOutlineGeometry is null,
            "Direct outline still created WinUI PathGeometry/PathFigure objects.");
        Require(ReferenceEquals(scene.ActiveSelection, selection) && !scene.CanUndo, "Outline rendering changed selection/history.");
        report["directOutlineKeepsWinuiGeometryEmpty"] = "passed";
        Require(await WaitUntilAsync(() => directOutlineDraws > 0, 5000), "The live canvas did not draw the direct outline.");
        await VerifyDirectOutlineRenderingAsync(scene, selection, report);
        await VerifyDirectOutlineTransitionsAsync(report);
        await VerifyDirectOutlineResourceResetAsync(report);
        await VerifyDirectOutlineLargeAsync(report);
        Require(directOutlineFailures == 0 && SelectionOutline.Data is null && selectionOutlineGeometry is null
            && retiredOutlineRoots.Count == 0, "Direct outlines failed or entered WinUI retirement.");
        report["directOutlineNoFailuresOrNativeRetirement"] = "passed";
        report["resources"] = new { created = directOutlineCreated, disposed = directOutlineDisposed, peak = directOutlinePeakResources };
    }

    private async Task WaitForDirectOutlineAsync()
    {
        Require(await WaitUntilAsync(() => !directOutlineWorkerBusy && pendingDirectOutline is null
            && (DisplayedSelection is null || directSourceOutline is not null), 20000), "Direct outline did not settle.");
    }

    private async Task VerifyDirectOutlineRenderingAsync(EditorDocument scene, Selection selection, Dictionary<string, object> report)
    {
        await WaitForDirectOutlineAsync();
        var observations = new List<object>(); report["directOutlineRenderingSamples"] = observations;
        using var referenceStyle = new CanvasStrokeStyle { CustomDashStyle = [4, 3], StartCap = CanvasCapStyle.Flat,
            EndCap = CanvasCapStyle.Flat, DashCap = CanvasCapStyle.Flat };
        var edges = SelectionContours.Build(selection);
        using var builder = new CanvasPathBuilder(CanvasDevice.GetSharedDevice());
        foreach (var edge in edges) { builder.BeginFigure(edge.X0, edge.Y0); builder.AddLine(edge.X1, edge.Y1); builder.EndFigure(CanvasFigureLoop.Open); }
        using var reference = CanvasGeometry.CreatePath(builder);
        foreach (double backing in new[] { 1.0, 1.5, 2.0 })
        foreach (double zoom in new[] { .273, 1.0, 2.0 })
        {
            viewport.Resize(160, 144, backing); viewport.SetZoom(zoom, viewport.Center);
            var origin = viewport.Origin;
            viewport.Pan(new(16.25 - origin.X, 13.375 - origin.Y));
            byte[] Render(bool expected)
            {
                using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 160, 144, (float)(96 * backing));
                using (var drawing = target.CreateDrawingSession())
                {
                    drawing.Clear(Microsoft.UI.Colors.Transparent);
                    if (expected)
                    {
                        drawing.Transform = Matrix3x2.CreateScale((float)viewport.PointsPerPixel)
                            * Matrix3x2.CreateTranslation((float)viewport.Origin.X, (float)viewport.Origin.Y);
                        drawing.DrawGeometry(reference, Microsoft.UI.ColorHelper.FromArgb(255, 76, 194, 255),
                            (float)(1 / viewport.PointsPerPixel), referenceStyle);
                    }
                    else DrawDirectSelectionOutline(drawing);
                }
                return target.GetPixelBytes();
            }
            var expected = Render(true); var actual = Render(false);
            Require(actual.Where((_, i) => i % 4 == 3).Any(alpha => alpha != 0), "Direct render reference was empty.");
            int different = expected.Zip(actual).Count(pair => pair.First != pair.Second);
            observations.Add(new { backing, zoom, differentBytes = different });
            Require(different == 0, $"Direct rectangle drawing differed from independent contour rendering at {backing}/{zoom}.");
        }
        ResizeViewport(); FitToWindow(); FlushViewport();
        report["directOutlineActualDrawingDpiZoomAndFractionalPan"] = "passed";

        var mask = new MaskSurface(64, 64); var random = new Random(7301);
        byte[] levels = [0, 127, 128, 255]; for (int i = 0; i < mask.Pixels.Length; i++) mask.Pixels[i] = levels[random.Next(4)];
        var noisy = new Selection(mask, new(0, 0, 64, 64)); scene.SetSelection(noisy); await WaitForDirectOutlineAsync();
        RequireDirectPoints(SelectionContours.Build(noisy), directSourceOutline!.Points);
        var source = directSourceOutline; int builds = directOutlineBuilds;
        scene.SetSelection(noisy.Translated(-7, 11)); await WaitForDirectOutlineAsync();
        Require(ReferenceEquals(source, directSourceOutline) && directOutlineBuilds == builds,
            "Translation rebuilt direct source geometry.");
        scene.SetSelection(SelectionCommands.Modify(scene.ActiveSelection!, SelectionModification.Feather, 3)); await WaitForDirectOutlineAsync();
        Require(ReferenceEquals(source, directSourceOutline) && directOutlineBuilds == builds
            && directOutlinePlacement.Translation == new Vector2(-7, 11), "Feather rebuilt direct contours or lost translation.");
        report["directOutlineThresholdHolesTranslationAndFeatherReuse"] = "passed";
        await VerifyDirectOutlineVisualDifferenceAsync(report);
    }

    private async Task VerifyDirectOutlineVisualDifferenceAsync(Dictionary<string, object> report)
    {
        var host = new Grid { Width = 320, Height = 240, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        Grid.SetRowSpan(host, 10); Grid.SetColumnSpan(host, 10); Canvas.SetZIndex(host, 1000); RootGrid.Children.Add(host);
        var observations = new List<object>(); report["directVersusXamlPixels"] = observations;
        var legacy = await BuildLegacySelectionOutlineAsync(directSourceOutline!.Edges);
        var path = new Microsoft.UI.Xaml.Shapes.Path { Data = legacy, Stroke = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 76, 194, 255)),
            StrokeDashArray = new DoubleCollection { 4, 3 } };
        try
        {
            foreach (double zoom in new[] { .273, 1.0, 2.0 })
            {
                host.Children.Clear(); RootGrid.UpdateLayout();
                var blankTarget = new RenderTargetBitmap(); await blankTarget.RenderAsync(host);
                var blank = (await blankTarget.GetPixelsAsync()).ToArray();
                int width = blankTarget.PixelWidth, height = blankTarget.PixelHeight;
                double backing = width / host.Width;
                viewport.Resize(host.Width, host.Height, backing); viewport.SetZoom(zoom, viewport.Center);
                viewport.Pan(new(27.25 - viewport.Origin.X, 31.375 - viewport.Origin.Y));
                double scale = viewport.PointsPerPixel;
                path.StrokeThickness = 1 / scale;
                path.RenderTransform = new MatrixTransform { Matrix = new(scale, 0, 0, scale,
                    viewport.Origin.X - 7 * scale, viewport.Origin.Y + 11 * scale) };
                host.Children.Add(path); RootGrid.UpdateLayout();
                var oldTarget = new RenderTargetBitmap(); await oldTarget.RenderAsync(host);
                var oldPixels = (await oldTarget.GetPixelsAsync()).ToArray();
                var oldRepeat = new RenderTargetBitmap(); await oldRepeat.RenderAsync(host);
                var repeated = (await oldRepeat.GetPixelsAsync()).ToArray();
                Require(!oldPixels.AsSpan().SequenceEqual(blank) && oldPixels.AsSpan().SequenceEqual(repeated),
                    "XAML difference control was empty or unstable.");
                using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), (float)host.Width, (float)host.Height, (float)(96 * backing));
                using (var draw = target.CreateDrawingSession()) { draw.Clear(Microsoft.UI.Colors.Transparent); DrawDirectSelectionOutline(draw); }
                var directPixels = target.GetPixelBytes();
                var bitmap = new WriteableBitmap(width, height); using (var stream = bitmap.PixelBuffer.AsStream()) stream.Write(directPixels);
                host.Children.Clear(); host.Children.Add(new Image { Source = bitmap, Width = host.Width, Height = host.Height, Stretch = Stretch.Fill });
                RootGrid.UpdateLayout(); var current = new RenderTargetBitmap(); await current.RenderAsync(host);
                var currentPixels = (await current.GetPixelsAsync()).ToArray();
                Require(!currentPixels.AsSpan().SequenceEqual(blank), "Direct difference capture was empty.");
                int different = oldPixels.Zip(currentPixels).Count(pair => pair.First != pair.Second);
                int maxDelta = oldPixels.Zip(currentPixels).Max(pair => Math.Abs(pair.First - pair.Second));
                observations.Add(new { zoom, backing, width, height, runs = directSourceOutline!.Edges.Length,
                    differentBytes = different, maxDelta, repeatDifferentBytes = 0 });
                if (zoom == 1)
                {
                    var directory = System.IO.Path.Combine(AppContext.BaseDirectory, "direct-outline-visual"); Directory.CreateDirectory(directory);
                    using var oldBitmap = CanvasBitmap.CreateFromBytes(CanvasDevice.GetSharedDevice(), oldPixels, width, height,
                        Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 96);
                    using var newBitmap = CanvasBitmap.CreateFromBytes(CanvasDevice.GetSharedDevice(), currentPixels, width, height,
                        Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 96);
                    await oldBitmap.SaveAsync(System.IO.Path.Combine(directory, "xaml.png"), CanvasBitmapFileFormat.Png);
                    await newBitmap.SaveAsync(System.IO.Path.Combine(directory, "direct.png"), CanvasBitmapFileFormat.Png);
                }
            }
        }
        finally { path.Data = null; RootGrid.Children.Remove(host); RetireOutline(legacy); ResizeViewport(); FitToWindow(); FlushViewport(); }
        // Observation only: the new backend is opt-in, and XAML's edge rasterization is retained as the control.
        report["directOutlineVisualDifferenceMeasuredWithNonemptyControls"] = "passed";
    }

    private async Task VerifyDirectOutlineTransitionsAsync(Dictionary<string, object> report)
    {
        var scene = EditorDocument.Create(128, 128); var pixels = scene.Snapshot.Images.Values.First();
        for (int i = 0; i < pixels.Pixels.Length; i += 4)
        { pixels.Pixels[i] = (byte)(i / 4 % 191); pixels.Pixels[i + 1] = 73; pixels.Pixels[i + 2] = 29; pixels.Pixels[i + 3] = 255; }
        var originalSelection = Selection.Rectangle(128, 128, 24, 27, 39, 33); scene.SetSelection(originalSelection);
        OpenDocument(scene); await WaitForFramesAsync(); await WaitForDirectOutlineAsync();
        var source = directSourceOutline!; var expectedSource = await BuildLegacySelectionOutlineAsync(source.Edges);
        OnFreeTransform(this, new()); await WaitForFramesAsync(); await WaitForDirectOutlineAsync();
        Require(floatingTransform is not null, "Direct floating fixture did not float.");
        var original = floatingTransform!.Edit.Value;
        ChangeTransformNumber(TransformField.X, original.Origin.X + 3.25); await WaitForFramesAsync(); await WaitForDirectOutlineAsync();
        Require(ReferenceEquals(source, directSourceOutline) && directProjectedOutline is null,
            "Affine direct preview rebuilt source geometry.");
        var comparisons = new List<object>(); report["directOutlineProjectionPointComparisons"] = comparisons;
        foreach ((bool flipX, bool flipY) in new[] { (false, false), (true, false), (false, true), (true, true) })
        {
            var value = CopyTransform(original); value.FlipX = flipX; value.FlipY = flipY;
            var corners = DistortWarp.CornersOf(original); corners[0] = new(corners[0].X - 7.25, corners[0].Y - 3.5);
            QueueCanvasInput(scene, () => { SetTransformDraft(value); SetTransformCorners(corners); });
            await WaitForFramesAsync(); await WaitForDirectOutlineAsync();
            var legacy = await BuildLegacyProjectedOutlineAsync(expectedSource, original, value, originalSelection.OutlineOffset, corners);
            var wanted = SelectionOutlineFigures(legacy).SelectMany(figure => new[] {
                new Vector2((float)figure.StartPoint.X, (float)figure.StartPoint.Y),
                new Vector2((float)((Microsoft.UI.Xaml.Media.LineSegment)figure.Segments[0]).Point.X,
                    (float)((Microsoft.UI.Xaml.Media.LineSegment)figure.Segments[0]).Point.Y) }).ToArray();
            Require(directProjectedOutline is not null && wanted.AsSpan().SequenceEqual(directProjectedOutline.Points),
                "Direct perspective changed retained legacy float endpoints.");
            comparisons.Add(new { flipX, flipY, runs = wanted.Length / 2, equal = true }); RetireOutline(legacy);
            AssertTransformUpload(LayerDisplaySnapshot!);
        }
        var folded = DistortWarp.CornersOf(original); folded[0] = new(folded[2].X + 5, folded[2].Y + 5);
        QueueCanvasInput(scene, () => SetTransformCorners(folded)); await WaitForFramesAsync(); await WaitForDirectOutlineAsync();
        Require(!directOutlineVisible, "Folded direct outline remained visible.");
        report["directOutlineAffinePerspectiveFlipsFoldAndGpu"] = "passed";

        var good = DistortWarp.CornersOf(original); good[0] = new(good[0].X - 5.25, good[0].Y - 2.5);
        QueueCanvasInput(scene, () => SetTransformCorners(good)); await WaitForFramesAsync(); await WaitForDirectOutlineAsync();
        int starts = directOutlineBuilds, publications = directOutlinePublications;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(false);
        beforeDirectOutlinePrepare = token => { entered.TrySetResult(); release.Wait(token); };
        try
        {
            var first = good.ToArray(); first[0] = new(first[0].X - .125, first[0].Y);
            transformCorners = first; UpdateDirectOutlinePlacement(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (int i = 1; i <= 80; i++)
            { var next = first.ToArray(); next[0] = new(first[0].X - i * .125, first[0].Y); transformCorners = next; UpdateDirectOutlinePlacement(); }
            Require(directOutlineBuilds == starts + 1, "Direct requests ran overlapping outline workers.");
        }
        finally { beforeDirectOutlinePrepare = null; release.Set(); }
        await WaitForDirectOutlineAsync();
        Require(directOutlineBuilds == starts + 2 && directOutlinePublications == publications + 1,
            "Superseded direct requests published or failed to coalesce.");
        report["directOutlineLatestOnlySingleWorkerCancellation"] = "passed";

        // A real commit uses the ordinary transform worker and history, not the point-only edits above.
        QueueCanvasInput(scene, () => SetTransformCorners(good)); await WaitForFramesAsync(); await WaitForDirectOutlineAsync();
        await FinishTransformBeforeOutputAsync(); await WaitForFramesAsync(); await WaitForDirectOutlineAsync();
        Require(scene.CanUndo && scene.LayerCount == 1, "Direct transform did not commit one floating merge.");
        AssertUploadedFrame(scene); OnUndo(this, new()); await WaitForFramesAsync(); await WaitForDirectOutlineAsync();
        Require(!scene.CanUndo && ReferenceEquals(scene.ActiveSelection, originalSelection)
            && ReferenceEquals(scene.Snapshot.Images.Values.First(), pixels), "Direct transform Undo lost exact source pixels/selection.");
        OnRedo(this, new()); await WaitForFramesAsync(); await WaitForDirectOutlineAsync(); AssertUploadedFrame(scene);
        RetireOutline(expectedSource);
        report["directOutlineCommitUndoRedoKeepsDocumentPixels"] = "passed";

        var ownerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var ownerRelease = new ManualResetEventSlim(false);
        beforeDirectOutlinePrepare = token => { ownerEntered.TrySetResult(); ownerRelease.Wait(token); };
        try
        {
            scene.SetSelection(Selection.Rectangle(128, 128, 1, 2, 11, 13)); await ownerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            OpenDocument(EditorDocument.Create(32, 32));
        }
        finally { beforeDirectOutlinePrepare = null; ownerRelease.Set(); }
        await WaitForDirectOutlineAsync(); await WaitForFramesAsync();
        Require(directSourceOutline is null && directProjectedOutline is null && directOutlineCreated == directOutlineDisposed,
            "Retired direct owner published or retained a geometry.");
        report["directOutlineOwnerRetirementDisposesUnpublishedWork"] = "passed";
    }

    private async Task VerifyDirectOutlineLargeAsync(Dictionary<string, object> report)
    {
        var points = Enumerable.Range(0, 240).Select(i => { double angle = i * Math.PI / 120, radius = i % 2 == 0 ? 1500 : 1200;
            return new PointD(2048 + radius * Math.Cos(angle), 2048 + radius * Math.Sin(angle)); }).ToArray();
        var selection = await Task.Run(() => LassoSelection.Rasterize(4096, 4096, points));
        var scene = EditorDocument.Create(4096, 4096); scene.SetSelection(selection);
        var watch = Stopwatch.StartNew(); OpenDocument(scene); await WaitForFramesAsync(); await WaitForDirectOutlineAsync();
        double initialMs = watch.Elapsed.TotalMilliseconds;
        double sourceCandidateMs = directOutlineLastBuildMs;
        Require(directSourceOutline!.Edges.Length > 10000, "Large direct fixture lost its contour runs.");
        RequireDirectPoints(SelectionContours.Build(selection), directSourceOutline.Points);
        OnFreeTransform(this, new()); await WaitForFramesAsync(); await WaitForDirectOutlineAsync();
        var original = floatingTransform!.Edit.Value; var frames = new List<object>(); double maxQueueMs = 0;
        for (int i = 0; i < 3; i++)
        {
            var corners = DistortWarp.CornersOf(original); corners[0] = new(corners[0].X - 20.25 - i * 10, corners[0].Y - 10.5 - i * 5);
            watch.Restart(); var events = new System.Collections.Concurrent.ConcurrentQueue<(string Name, double At)>();
            frameTimelineProbe = name => { if (name is "publication-queued" or "publication-entered") events.Enqueue((name, watch.Elapsed.TotalMilliseconds)); };
            try
            {
                QueueCanvasInput(scene, () => SetTransformCorners(corners));
                await WaitForFramesAsync(); await WaitForDirectOutlineAsync();
            }
            finally { frameTimelineProbe = null; }
            var queued = new Queue<double>();
            foreach (var item in events)
            {
                if (item.Name == "publication-queued") queued.Enqueue(item.At);
                else
                {
                    Require(queued.TryDequeue(out double at), "Direct frame timeline lost a queued publication.");
                    maxQueueMs = Math.Max(maxQueueMs, item.At - at);
                }
            }
            Require(queued.Count == 0, "Direct frame timeline ended before publication.");
            frames.Add(new { i, readyMs = watch.Elapsed.TotalMilliseconds, outlineMs = directOutlineLastBuildMs,
                runs = directProjectedOutline!.Edges.Length, live = directOutlineCreated - directOutlineDisposed });
            AssertTransformUpload(LayerDisplaySnapshot!);
        }
        report["directOutlineLargeSamples"] = new { initialMs, frames };
        report["directOutlineMaxPublicationQueueMs"] = maxQueueMs;
        Require(maxQueueMs < 200, $"Direct outline blocked frame publication for {maxQueueMs:F3}ms.");
        report["directOutlineFramePublicationQueueRemainsResponsive"] = "passed";
        double candidateMs = directOutlineLastBuildMs;
        var sourceClock = Stopwatch.StartNew();
        var legacySource = await BuildLegacySelectionOutlineAsync(directSourceOutline!.Edges);
        double sourceLegacyMs = sourceClock.Elapsed.TotalMilliseconds;
        report["directOutlineLegacySourceControl"] = new { candidateMs = sourceCandidateMs, legacyMs = sourceLegacyMs,
            ratio = sourceCandidateMs / sourceLegacyMs };
        Require(sourceCandidateMs < sourceLegacyMs * .5, "Direct source outline did not halve the retained legacy construction time.");
        var legacyClock = Stopwatch.StartNew();
        var legacyProjection = await BuildLegacyProjectedOutlineAsync(legacySource, original, transformInspectorValue!,
            selection.OutlineOffset, transformCorners!);
        double legacyMs = legacyClock.Elapsed.TotalMilliseconds;
        report["directOutlineLegacyProjectionControl"] = new { candidateMs, legacyMs, ratio = candidateMs / legacyMs };
        Require(candidateMs < legacyMs * .5, "Direct projection did not halve the retained legacy construction time.");
        RetireOutline(legacyProjection); RetireOutline(legacySource);
        // The independent XAML controls belong to the test, not the direct
        // renderer. Drain them before measuring the direct document transition.
        Require(await WaitUntilAsync(() => !retiringOutlines, OutlineDrainBudgetMs), "Independent XAML controls did not drain.");
        watch.Restart(); OpenDocument(EditorDocument.Create(32, 32)); await WaitForFramesAsync(); await WaitForDirectOutlineAsync();
        Require(await WaitUntilAsync(() => directOutlineCreated == directOutlineDisposed, 20000), "Direct large geometry did not drain in the retained deadline.");
        report["directOutlineLargeDisposal"] = new { readyMs = watch.Elapsed.TotalMilliseconds, peak = directOutlinePeakResources };
        Require(directOutlinePeakResources <= 3, "Direct outlines retained more than a source, published preview and worker result.");
        report["directOutlineLargeFrameAndBoundedDisposal"] = "passed";
    }

    private async Task VerifyDirectOutlineResourceResetAsync(Dictionary<string, object> report)
    {
        var scene = EditorDocument.Create(128, 128); var selection = Selection.Rectangle(128, 128, 17, 19, 43, 37);
        scene.SetSelection(selection); OpenDocument(scene); await WaitForFramesAsync(); await WaitForDirectOutlineAsync();
        var previous = directSourceOutline!; var points = previous.Points.ToArray();
        HandleCanvasResourceChange(Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesReason.DpiChanged);
        Require(ReferenceEquals(previous, directSourceOutline) && !previous.IsDisposed, "DPI-only change discarded device-independent contours.");
        HandleCanvasResourceChange(Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesReason.NewDevice);
        Require(previous.IsDisposed && directSourceOutline is null, "New device retained the previous direct geometry.");
        await WaitForFramesAsync(); await WaitForDirectOutlineAsync();
        Require(directSourceOutline is { IsDisposed: false } && !ReferenceEquals(previous, directSourceOutline)
            && points.AsSpan().SequenceEqual(directSourceOutline.Points) && ReferenceEquals(scene.ActiveSelection, selection) && !scene.CanUndo,
            "Device-resource reconstruction changed contour points or document history.");

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int publications = directOutlinePublications;
        beforeDirectOutlinePublication = () => { entered.TrySetResult(); return release.Task; };
        try
        {
            scene.SetSelection(Selection.Rectangle(128, 128, 21, 23, 47, 31));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Require(directOutlineCreated - directOutlineDisposed == 1, "Publication barrier did not hold a completed, unpublished geometry.");
            OpenDocument(EditorDocument.Create(32, 32));
        }
        finally { beforeDirectOutlinePublication = null; release.TrySetResult(); }
        await WaitForFramesAsync(); await WaitForDirectOutlineAsync();
        Require(directOutlinePublications == publications && directSourceOutline is null
            && directOutlineCreated == directOutlineDisposed, "Completed old-owner geometry published or was retained.");
        report["directOutlineResourceReconstructionAndCompletedOwnerRetirement"] = "passed";
    }

    private static void RequireDirectPoints(SelectionEdge[] expected, Vector2[] actual)
    {
        Require(actual.Length == expected.Length * 2, "Direct outline changed the number of contour endpoints.");
        for (int i = 0; i < expected.Length; i++)
        { var edge = expected[i]; Require(actual[i * 2] == new Vector2(edge.X0, edge.Y0)
            && actual[i * 2 + 1] == new Vector2(edge.X1, edge.Y1), $"Direct outline changed source run {i}."); }
    }
}
#endif
