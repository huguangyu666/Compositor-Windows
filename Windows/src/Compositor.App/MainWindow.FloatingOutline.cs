using System.Diagnostics;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Compositor.Core;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private sealed record FloatingOutlineRequest(FloatingTransformSession Session, Geometry Source, SelectionEdge[] Edges,
        LayerTransform Original, LayerTransform Value, PointD Offset, PointD[] Corners, int Revision, CancellationToken Token);
    private FloatingOutlineRequest? latestFloatingOutline, pendingFloatingOutline;
    private CancellationTokenSource? floatingOutlineCancellation;
    private bool floatingOutlineBusy;
#if DEBUG
    private double floatingOutlinePrepareMs, floatingOutlineUiMs;
    private double floatingOutlineQueueMs, floatingOutlineComputeMs, floatingOutlineNativeActiveMs;
    private double floatingOutlineParseMs, floatingOutlineAddMs;
    private int floatingOutlineGen2Collections;
    private int floatingOutlineWorkers, floatingOutlineMaxWorkers, floatingOutlineAbandoned, floatingOutlineFailures;
    private Action<CancellationToken>? beforeFloatingOutlinePrepare;
    private Action<int>? beforeFloatingOutlineBatch;
#endif

    private void CancelFloatingOutlineWork()
    {
        floatingOutlineCancellation?.Cancel(); floatingOutlineCancellation?.Dispose(); floatingOutlineCancellation = null;
        latestFloatingOutline = pendingFloatingOutline = null;
    }

    private void QueueFloatingOutline(FloatingTransformSession session, Geometry source, SelectionEdge[] edges,
        LayerTransform value, PointD offset, PointD[] corners)
    {
        // The render publication often repeats the pointer's mapping. Keep both completed and
        // in-flight results when only the notification changed.
        if (latestFloatingOutline is { } latest && !latest.Token.IsCancellationRequested
            && ReferenceEquals(latest.Session, session) && ReferenceEquals(latest.Source, source)
            && ReferenceEquals(latest.Edges, edges) && latest.Offset == offset
            && latest.Value.FlipX == value.FlipX && latest.Value.FlipY == value.FlipY
            && latest.Corners.AsSpan().SequenceEqual(corners)) return;
        CancelFloatingOutlineWork();
        floatingOutlineCancellation = CancellationTokenSource.CreateLinkedTokenSource(session.Cancellation.Token);
        latestFloatingOutline = pendingFloatingOutline = new(session, source, edges, session.Edit.Value,
            CopyTransform(value), offset, corners.ToArray(), ++floatingOutlineRevision, floatingOutlineCancellation.Token);
        if (!floatingOutlineBusy) _ = DrainFloatingOutlinesAsync();
    }

    private bool IsCurrentFloatingOutline(FloatingOutlineRequest request) => !request.Token.IsCancellationRequested
        && !windowClosed && request.Revision == floatingOutlineRevision && ReferenceEquals(floatingTransform, request.Session)
        && ReferenceEquals(document, request.Session.Owner) && ReferenceEquals(selectionOutlineGeometry, request.Source)
        && ReferenceEquals(selectionOutlineEdges, request.Edges) && document?.ActiveSelection?.OutlineOffset == request.Offset;

    private async Task DrainFloatingOutlinesAsync()
    {
        floatingOutlineBusy = true;
        try
        {
            // One preparation at a time; a drag replaces the pending request instead of queuing
            // more workers. Cancellation is checked between bounded blocks of contour runs.
            while (pendingFloatingOutline is { } request)
            {
                pendingFloatingOutline = null;
                try { await PrepareFloatingOutlineAsync(request); }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    if (ReferenceEquals(latestFloatingOutline, request)) latestFloatingOutline = null;
#if DEBUG
                    floatingOutlineFailures++;
#endif
                    App.Log($"Distorted selection outline failed: {ex}");
                }
            }
        }
        finally { floatingOutlineBusy = false; }
    }

    private async Task PrepareFloatingOutlineAsync(FloatingOutlineRequest request)
    {
        Geometry? unpublishedGeometry = null;
#if DEBUG
        floatingOutlineBuilds++;
        var total = Stopwatch.StartNew(); var prepare = Stopwatch.StartNew(); bool published = false;
        double queueMs = 0, computeMs = 0, nativeActiveMs = 0, parseMs = 0, addMs = 0; int gen2 = GC.CollectionCount(2);
#endif
        try
        {
            string[] paths;
#if DEBUG
            var beforePrepare = beforeFloatingOutlinePrepare;
            floatingOutlineWorkers++; floatingOutlineMaxWorkers = Math.Max(floatingOutlineMaxWorkers, floatingOutlineWorkers);
            try
#endif
            {
                paths = await Task.Run(() =>
                {
#if DEBUG
                    queueMs = prepare.Elapsed.TotalMilliseconds; var compute = Stopwatch.StartNew();
#endif
                    request.Token.ThrowIfCancellationRequested();
#if DEBUG
                    beforePrepare?.Invoke(request.Token);
#endif
                    var result = PrepareProjectedSelectionOutlinePaths(request.Edges, request.Original, request.Value,
                        request.Offset, request.Corners, request.Token);
#if DEBUG
                    computeMs = compute.Elapsed.TotalMilliseconds;
#endif
                    return result;
                }, request.Token);
            }
#if DEBUG
            finally { floatingOutlineWorkers--; }
            double prepareMs = prepare.Elapsed.TotalMilliseconds; var ui = Stopwatch.StartNew();
#endif
            Geometry? geometry = null; var group = paths.Length > 1 ? new GeometryGroup() : null;
            unpublishedGeometry = group;
            for (int i = 0; i < paths.Length; i++)
            {
#if DEBUG
                beforeFloatingOutlineBatch?.Invoke(i);
#endif
                if (!IsCurrentFloatingOutline(request)) return;
#if DEBUG
                var native = Stopwatch.StartNew();
#endif
                var child = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), paths[i]);
#if DEBUG
                parseMs += native.Elapsed.TotalMilliseconds; native.Restart();
#endif
                if (group is not null) group.Children.Add(child); else unpublishedGeometry = geometry = child;
#if DEBUG
                addMs += native.Elapsed.TotalMilliseconds;
                nativeActiveMs = parseMs + addMs;
                floatingOutlineProcessedRuns = Math.Min(request.Edges.Length, (i + 1) * SelectionOutlineRunsPerBatch);
#endif
                if (i + 1 < paths.Length) await Task.Yield();
            }
            if (!IsCurrentFloatingOutline(request)) return;
            ReplaceSelectionOutlineData(group is not null ? group : geometry ?? new PathGeometry());
            unpublishedGeometry = null;
            SelectionOutline.RenderTransform = null;
#if DEBUG
            published = true; floatingOutlinePublications++; floatingOutlinePublishedRevision = request.Revision;
            floatingOutlineRuns = request.Edges.Length; floatingOutlineLastMs = total.Elapsed.TotalMilliseconds;
            floatingOutlinePrepareMs = prepareMs; floatingOutlineUiMs = ui.Elapsed.TotalMilliseconds;
            floatingOutlineQueueMs = queueMs; floatingOutlineComputeMs = computeMs; floatingOutlineNativeActiveMs = nativeActiveMs;
            floatingOutlineGen2Collections = GC.CollectionCount(2) - gen2;
            floatingOutlineParseMs = parseMs; floatingOutlineAddMs = addMs;
#endif
        }
        finally
        {
            RetireOutline(unpublishedGeometry);
#if DEBUG
            if (!published) floatingOutlineAbandoned++;
#endif
        }
    }
}
