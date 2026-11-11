using Compositor.Core;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private List<PointD>? lassoPoints;
    private PointD? lassoCursor;
    private bool lassoPolygonal;
    private bool lassoDrawing;
    private LassoSelectionMode lassoMode;
    private Selection? outlinedSelection;
    private Geometry? selectionOutlineGeometry;
    private SelectionEdge[]? selectionOutlineEdges;
#if DEBUG
    // Probe-only observation of what one outline build actually costs: how many contour runs the
    // extraction returned, worker preparation and native parsing time, dispatcher yields, and
    // how far a superseded build got before it stopped.
    private int outlineBuildsAccepted;
    private int outlineObservationEpoch;
    private int outlineRunCount;
    private int outlineYieldCount;
    private double outlineExtractMs;
    private double outlineUiBuildMs;
    private double outlinePrepareMs;
    private int outlineNativeBatches;
    private double outlineMaxBatchMs;
    private Action<int>? beforeOutlineBatch;
    private Func<Selection, Task>? afterOutlineExtractionProbe;
    private Action<Selection>? outlineBuildCompletedProbe;
    private double outlineTotalMs;
    private int outlineAbandonedAt;
    private double outlineAbandonedMs;
    internal readonly record struct OutlineBuild(int Runs, int Yields, double ExtractMs, double UiBuildMs, double TotalMs,
        int Builds, int AbandonedAt, double AbandonedMs, double PrepareMs, int NativeBatches, double MaxBatchMs);
    internal OutlineBuild OutlineBuildProbe => new(outlineRunCount, outlineYieldCount, outlineExtractMs, outlineUiBuildMs,
        outlineTotalMs, outlineBuildsAccepted, outlineAbandonedAt, outlineAbandonedMs,
        outlinePrepareMs, outlineNativeBatches, outlineMaxBatchMs);
    internal void ResetOutlineBuildProbe()
    {
        outlineObservationEpoch++;
        outlineBuildsAccepted = 0; outlineRunCount = 0; outlineYieldCount = 0; outlineExtractMs = 0;
        outlineUiBuildMs = 0; outlineTotalMs = 0; outlineAbandonedAt = 0; outlineAbandonedMs = 0;
        outlinePrepareMs = 0; outlineNativeBatches = 0; outlineMaxBatchMs = 0;
    }
#endif
    private readonly TranslateTransform outlineTranslation = new();
    private bool lassoPreviewPending;
    private bool lassoPreviewQueued;

    private void OnLassoOptionsChanged(object sender, SelectionChangedEventArgs e) => CancelLasso();

    private void BeginLasso(PointD point, VirtualKeyModifiers modifiers)
    {
        if (!CanEditSelection || !double.IsFinite(point.X) || !double.IsFinite(point.Y)) return;
        CompleteMarqueePreview();
        if (lassoPoints is { Count: > 0 } points && lassoPolygonal)
        {
            double dx = point.X - points[0].X, dy = point.Y - points[0].Y;
            if (points.Count >= 3 && Math.Sqrt(dx * dx + dy * dy) <= 8 / viewport.PointsPerPixel) FinishLasso();
            else { AppendLassoPoint(point); lassoCursor = point; ShowLassoDraft(); }
            return;
        }
        lassoPolygonal = LassoKindCombo.SelectedIndex == 1;
        lassoMode = SelectionModeAtPress(modifiers);
        lassoPoints = [point];
        lassoCursor = null;
        lassoDrawing = true;
        ShowLassoDraft();
    }

    private void AppendLassoPoint(PointD point)
    {
        if (lassoPoints is not { Count: > 0 } points || !double.IsFinite(point.X) || !double.IsFinite(point.Y)) return;
        var last = points[^1];
        double dx = point.X - last.X, dy = point.Y - last.Y;
        if (Math.Sqrt(dx * dx + dy * dy) >= 0.25) points.Add(point);
    }

    private void MoveLasso(PointD point)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) return;
        if (lassoPolygonal) lassoCursor = point;
        else if (lassoDrawing) AppendLassoPoint(point);
        if (!lassoPreviewQueued)
        {
            lassoPreviewQueued = true;
            CompositionTarget.Rendering += OnLassoPreviewFrame;
        }
    }

    private void OnLassoPreviewFrame(object? sender, object args)
    {
        CompositionTarget.Rendering -= OnLassoPreviewFrame;
        lassoPreviewQueued = false;
        ShowLassoDraft();
    }

    private void ShowLassoDraft()
    {
        if (lassoPoints is null) return;
        var displayed = LassoPreview.Points;
        while (displayed.Count > lassoPoints.Count) displayed.RemoveAt(displayed.Count - 1);
        while (displayed.Count < lassoPoints.Count)
        {
            var point = lassoPoints[displayed.Count];
            displayed.Add(new Point(point.X, point.Y));
        }
        if (lassoPoints.Count > 0)
        {
            var last = lassoPoints[^1];
            displayed[lassoPoints.Count - 1] = new Point(last.X, last.Y);
        }
        if (lassoPolygonal && lassoCursor is { } cursor) displayed.Add(new Point(cursor.X, cursor.Y));
        LassoPreview.Visibility = Visibility.Visible;
    }

    private void FinishLasso()
    {
        if (!CanEditSelection || document is null || lassoPoints is null) return;
        var points = lassoPoints;
        var mode = lassoMode;
        bool hasArea = points.Count >= 3 && points.Max(point => point.X) > points.Min(point => point.X)
            && points.Max(point => point.Y) > points.Min(point => point.Y);
        ShowLassoDraft(); // Flush the final pointer samples before retaining the closed preview.
        CancelLasso(keepPreview: hasArea);
        if (!hasArea)
        {
            if (mode == LassoSelectionMode.Replace) QueueSelectionWork((_, _) => null, false, Loc.T("S_Deselected"));
            return;
        }
        if (mode == LassoSelectionMode.Subtract && document.ActiveSelection is null && selectionWorkResult is null)
        { CompleteMarqueePreview(); return; }
        var manifest = document.Snapshot.Manifest;
        int width = manifest.Width, height = manifest.Height;
        bool antialias = LassoAntialiasCheck.IsChecked == true;
        QueueSelectionWork((previous, token) => LassoSelection.Combine(previous,
            LassoSelection.Rasterize(width, height, points, antialias, token), mode), mode != LassoSelectionMode.Replace,
            Loc.T("S_LassoApplied"));
    }

    private void CancelLasso(bool keepPreview = false)
    {
        if (lassoPreviewQueued) CompositionTarget.Rendering -= OnLassoPreviewFrame;
        lassoPreviewQueued = false;
        lassoPoints = null;
        lassoCursor = null;
        lassoDrawing = false;
        lassoPreviewPending = keepPreview;
        if (LassoPreview is not null)
        {
            if (keepPreview && LassoPreview.Points.Count > 0) LassoPreview.Points.Add(LassoPreview.Points[0]);
            else
            {
                LassoPreview.Points.Clear();
                LassoPreview.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void OnCanvasDoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (tool != CanvasTool.Lasso || !lassoPolygonal || lassoPoints is null) return;
        FinishLasso();
        args.Handled = true;
    }

    private bool HandleLassoKey(KeyRoutedEventArgs args)
    {
        if (renamingLayer || textDialogOpen || args.OriginalSource is TextBox or RichEditBox) return false;
        // The L that picks this tool lives in the canvas key table (HandleWorkspaceToolKey), not here.
        if (tool != CanvasTool.Lasso) return false;
        if (args.Key == VirtualKey.Tab) LassoKindCombo.SelectedIndex = LassoKindCombo.SelectedIndex == 0 ? 1 : 0;
        else if (lassoPoints is null) return false;
        else if (IsCancelPress(args.Key)) CancelLasso();
        else if (IsApplyPress(args.Key)) FinishLasso();
        else if (IsDeletePress(args.Key))
        {
            RemoveLastLassoPoint();
        }
        else return false;
        args.Handled = true;
        return true;
    }

    private void RemoveLastLassoPoint()
    {
        if (lassoPoints is null) return;
        lassoPoints.RemoveAt(lassoPoints.Count - 1);
        if (lassoPoints.Count == 0) CancelLasso();
        else ShowLassoDraft();
    }

    /// <summary>Coverage contours instead of a bounding box. The geometry is built only when
    /// the selection changes; painting through it reuses the same overlay.</summary>
    private async void RefreshMaskSelectionOutline(Selection? selection)
    {
        if (directOutlineEnabled) { RefreshDirectSelectionOutline(selection); return; }
        if (selection is null) CompleteMarqueePreview();
        SelectionOutline.Visibility = selection is null ? Visibility.Collapsed : Visibility.Visible;
        if (ReferenceEquals(selection, outlinedSelection)) return;
        bool sameShape = selection is not null && HasOutlinedShape(selection);
        outlinedSelection = selection;
        SelectionOutline.RenderTransform = outlineTranslation;
        outlineTranslation.X = selection?.OutlineOffset.X ?? 0; outlineTranslation.Y = selection?.OutlineOffset.Y ?? 0;
        if (sameShape) return; // Reuse even an in-flight shape build; movement changes only its transform.
        outlineCancellation?.Cancel();
        outlineCancellation?.Dispose();
        outlineCancellation = null;
        var previousRoot = selectionOutlineGeometry;
        selectionOutlineGeometry = null;
        selectionOutlineEdges = null;
        ReplaceSelectionOutlineData(null);
        RetireOutline(previousRoot);
        if (selection is null) return;
        var cancellation = new CancellationTokenSource(); outlineCancellation = cancellation;
        var token = cancellation.Token;
        Geometry? unpublishedGeometry = null;
#if DEBUG
        var buildStarted = Stopwatch.StartNew();
        int observationEpoch = outlineObservationEpoch, nativeBatches = 0, yields = 0;
        double maxBatchMs = 0;
        outlineBuildsAccepted++;
#endif
        try
        {
            var offset = selection.OutlineOffset;
            var source = selection.Translated(-(int)offset.X, -(int)offset.Y);
            var edges = await Task.Run(() => SelectionContours.Build(source, token));
#if DEBUG
            if (afterOutlineExtractionProbe is { } afterExtraction) await afterExtraction(selection);
            double extractMs = buildStarted.Elapsed.TotalMilliseconds;
            var prepareStarted = Stopwatch.StartNew();
#endif
            var paths = await Task.Run(() => PrepareSelectionOutlinePaths(edges, token));
#if DEBUG
            double prepareMs = prepareStarted.Elapsed.TotalMilliseconds;
            var uiStarted = Stopwatch.StartNew();
#endif
            Geometry? geometry = null;
            var group = paths.Length > 1 ? CreateSelectionOutlineGroup() : null;
            unpublishedGeometry = group;
            var batch = Stopwatch.StartNew();
            for (int i = 0; i < paths.Length; i++)
            {
#if DEBUG
                beforeOutlineBatch?.Invoke(i);
#endif
                if (token.IsCancellationRequested || windowClosed || !HasOutlinedShape(selection))
                {
#if DEBUG
                    if (observationEpoch == outlineObservationEpoch)
                    {
                        outlineAbandonedAt = i * SelectionOutlineRunsPerBatch;
                        outlineAbandonedMs = buildStarted.Elapsed.TotalMilliseconds;
                    }
#endif
                    return;
                }
#if DEBUG
                var nativeStarted = Stopwatch.StartNew();
#endif
                var child = ParseSelectionOutlineBatch(paths[i], group);
                if (group is null) unpublishedGeometry = geometry = child;
#if DEBUG
                nativeBatches++;
                maxBatchMs = Math.Max(maxBatchMs, nativeStarted.Elapsed.TotalMilliseconds);
#endif
                // Parse bounded native batches instead of crossing WinRT for every figure
                // and segment. Keep runs independent so dash phase and caps are retained.
                if (i + 1 < paths.Length && batch.Elapsed.TotalMilliseconds >= 2)
                {
                    await Task.Yield();
                    batch.Restart();
#if DEBUG
                    yields++;
#endif
                }
            }
            if (!token.IsCancellationRequested && !windowClosed && HasOutlinedShape(selection))
            {
                selectionOutlineEdges = edges;
                selectionOutlineGeometry = group is not null ? group : geometry ?? new PathGeometry();
                ReplaceSelectionOutlineData(selectionOutlineGeometry);
                unpublishedGeometry = null;
                UpdateFloatingSelectionOutline();
                CompleteMarqueePreview();
#if DEBUG
                if (observationEpoch == outlineObservationEpoch)
                {
                    outlineRunCount = edges.Length; outlineExtractMs = extractMs; outlinePrepareMs = prepareMs;
                    outlineYieldCount = yields; outlineNativeBatches = nativeBatches; outlineMaxBatchMs = maxBatchMs;
                    outlineUiBuildMs = uiStarted.Elapsed.TotalMilliseconds;
                    outlineTotalMs = buildStarted.Elapsed.TotalMilliseconds;
                }
#endif
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.Log($"Selection outline failed: {ex}"); }
        finally
        {
            RetireOutline(unpublishedGeometry);
#if DEBUG
            outlineBuildCompletedProbe?.Invoke(selection);
#endif
        }
    }

    private bool HasOutlinedShape(Selection selection) => ReferenceEquals(selection.OutlineCoverage, outlinedSelection?.OutlineCoverage)
        && selection.SourceBounds == outlinedSelection?.SourceBounds;
}
