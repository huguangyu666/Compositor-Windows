using System.Diagnostics;
using System.Numerics;
using Compositor.Core;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;

namespace Compositor.App;

public sealed partial class MainWindow
{
    // Opt-in while its rendering differences are compared with the retained XAML path.
    private readonly bool directOutlineEnabled = Environment.GetEnvironmentVariable("COMPOSITOR_DIRECT_OUTLINE") == "1";
    private sealed class DirectOutlineResource(CanvasGeometry? geometry, SelectionEdge[] edges, Vector2[] points, Action disposed) : IDisposable
    {
        public readonly CanvasGeometry? Geometry = geometry;
        public readonly SelectionEdge[] Edges = edges;
        public readonly Vector2[] Points = points;
        private int isDisposed;
        public bool IsDisposed => Volatile.Read(ref isDisposed) != 0;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref isDisposed, 1) != 0) return;
            try { Geometry?.Dispose(); }
            finally { disposed(); }
        }
    }
    private sealed record DirectOutlineRequest(EditorDocument Owner, Selection Selection, DirectOutlineResource? Source,
        FloatingTransformSession? Session, LayerTransform? Original, LayerTransform? Value, PointD[]? Corners,
        PointD Offset, CancellationToken Token);
    private DirectOutlineResource? directSourceOutline, directProjectedOutline;
    private EditorDocument? directOutlineOwner;
    private DirectOutlineRequest? pendingDirectOutline, latestDirectProjection;
    private CancellationTokenSource? directProjectionCancellation;
    private CanvasStrokeStyle? directOutlineStroke;
    private Matrix3x2 directOutlinePlacement = Matrix3x2.Identity;
    private bool directOutlineVisible, directOutlineWorkerBusy;
    private int directOutlineCreated, directOutlineDisposed, directOutlinePeakResources;
    private int directOutlineBuilds, directOutlinePublications, directOutlineAbandoned, directOutlineFailures;
    private double directOutlineLastBuildMs;
#if DEBUG
    private Action<CancellationToken>? beforeDirectOutlinePrepare;
    private Func<Task>? beforeDirectOutlinePublication;
    private int directOutlineDraws;
#endif

    private void OnCanvasCreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args) => HandleCanvasResourceChange(args.Reason);

    private void HandleCanvasResourceChange(CanvasCreateResourcesReason reason)
    {
        if (windowClosed || reason != CanvasCreateResourcesReason.NewDevice) return;
        fullTarget?.Dispose(); fullTarget = null; presented = null;
        gpuPreviewRenderer?.Dispose(); gpuPreviewRenderer = null;
        gpuPreviewUnavailable = gpuFramePresented = false;
        checkerBrush?.Dispose(); checkerBrush = null; checkerTile?.Dispose(); checkerTile = null;
        frameRevision++; pendingFullRender = true; PumpRender();
        if (!directOutlineEnabled) return;
        // Retire both published geometry and any result prepared for the old device.
        // Re-enter the same owner-checked worker so recovery cannot publish stale work.
        outlineCancellation?.Cancel();
        DisposeDirectOutlines();
        RefreshDirectSelectionOutline(DisplayedSelection);
    }

    private void CancelDirectProjection()
    {
        directProjectionCancellation?.Cancel(); directProjectionCancellation?.Dispose(); directProjectionCancellation = null;
        latestDirectProjection = null;
        if (pendingDirectOutline?.Session is not null) pendingDirectOutline = null;
    }

    private void DisposeDirectOutlines()
    {
        CancelDirectProjection(); pendingDirectOutline = null;
        directProjectedOutline?.Dispose(); directProjectedOutline = null;
        directSourceOutline?.Dispose(); directSourceOutline = null;
        directOutlineStroke?.Dispose(); directOutlineStroke = null;
        directOutlineVisible = false; directOutlineOwner = null;
    }

    private void RefreshDirectSelectionOutline(Selection? selection)
    {
        SelectionOutline.Visibility = Visibility.Collapsed;
        bool sameShape = selection is not null && ReferenceEquals(directOutlineOwner, document) && HasOutlinedShape(selection);
        outlinedSelection = selection; directOutlineOwner = document;
        if (sameShape) { UpdateDirectOutlinePlacement(); return; }
        outlineCancellation?.Cancel(); outlineCancellation?.Dispose(); outlineCancellation = null;
        CancelDirectProjection(); pendingDirectOutline = null;
        directProjectedOutline?.Dispose(); directProjectedOutline = null;
        directSourceOutline?.Dispose(); directSourceOutline = null;
        selectionOutlineEdges = null; directOutlineVisible = false;
        if (!windowClosed) CanvasControl.Invalidate();
        if (selection is null || document is not { } owner || windowClosed) { CompleteMarqueePreview(); return; }
        outlineCancellation = new();
        pendingDirectOutline = new(owner, selection, null, null, null, null, null, selection.OutlineOffset, outlineCancellation.Token);
        if (!directOutlineWorkerBusy) _ = DrainDirectOutlinesAsync();
    }

    private void UpdateDirectOutlinePlacement()
    {
        if (!directOutlineEnabled || windowClosed) return;
        if (directSourceOutline is null || DisplayedSelection is not { } selection)
        {
            CancelDirectProjection(); directOutlineVisible = false; CanvasControl.Invalidate(); return;
        }
        var offset = selection.OutlineOffset;
        if (floatingTransform is { } session && transformInspectorValue is { } value)
        {
            if (transformCorners is { } corners)
            {
                if (!DistortWarp.IsConvex(corners))
                { CancelDirectProjection(); directOutlineVisible = false; CanvasControl.Invalidate(); return; }
                if (latestDirectProjection is { } latest && !latest.Token.IsCancellationRequested
                    && ReferenceEquals(latest.Session, session) && ReferenceEquals(latest.Source, directSourceOutline)
                    && latest.Offset == offset && latest.Value!.FlipX == value.FlipX && latest.Value.FlipY == value.FlipY
                    && latest.Corners!.AsSpan().SequenceEqual(corners)) return;
                CancelDirectProjection();
                directProjectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(session.Cancellation.Token, outlineCancellation!.Token);
                latestDirectProjection = pendingDirectOutline = new(session.Owner, selection, directSourceOutline, session,
                    CopyTransform(session.Edit.Value), CopyTransform(value), corners.ToArray(), offset, directProjectionCancellation.Token);
                if (!directOutlineWorkerBusy) _ = DrainDirectOutlinesAsync();
                return;
            }
            CancelDirectProjection(); directProjectedOutline?.Dispose(); directProjectedOutline = null;
            PointD Map(PointD p) => BrushEngine.PixelToDocument(value, 1, 1,
                BrushEngine.DocumentToPixel(session.Edit.Value, 1, 1, new(p.X + offset.X, p.Y + offset.Y)));
            var origin = Map(PointD.Zero); var x = Map(new(1, 0)); var y = Map(new(0, 1));
            directOutlinePlacement = new((float)(x.X - origin.X), (float)(x.Y - origin.Y),
                (float)(y.X - origin.X), (float)(y.Y - origin.Y), (float)origin.X, (float)origin.Y);
        }
        else
        {
            CancelDirectProjection(); directProjectedOutline?.Dispose(); directProjectedOutline = null;
            directOutlinePlacement = Matrix3x2.CreateTranslation((float)offset.X, (float)offset.Y);
        }
        directOutlineVisible = true; CanvasControl.Invalidate();
    }

    private bool IsCurrentDirectOutline(DirectOutlineRequest request) => !request.Token.IsCancellationRequested && !windowClosed
        && ReferenceEquals(document, request.Owner) && ReferenceEquals(directOutlineOwner, request.Owner)
        && HasOutlinedShape(request.Selection) && (request.Session is null
            || ReferenceEquals(latestDirectProjection, request) && ReferenceEquals(floatingTransform, request.Session)
                && ReferenceEquals(directSourceOutline, request.Source) && document.ActiveSelection?.OutlineOffset == request.Offset);

    private async Task DrainDirectOutlinesAsync()
    {
        directOutlineWorkerBusy = true;
        try
        {
            while (pendingDirectOutline is { } request)
            {
                pendingDirectOutline = null; DirectOutlineResource? unpublished = null;
                var clock = Stopwatch.StartNew(); directOutlineBuilds++;
#if DEBUG
                var beforePrepare = beforeDirectOutlinePrepare;
#endif
                try
                {
                    unpublished = await Task.Run(() =>
                    {
#if DEBUG
                        beforePrepare?.Invoke(request.Token);
#endif
                        return BuildDirectOutline(request);
                    }, request.Token);
#if DEBUG
                    if (beforeDirectOutlinePublication is { } beforePublication) await beforePublication();
#endif
                    if (!IsCurrentDirectOutline(request)) { directOutlineAbandoned++; continue; }
                    if (request.Session is null)
                    {
                        directSourceOutline?.Dispose(); directSourceOutline = unpublished;
                        selectionOutlineEdges = unpublished.Edges;
                    }
                    else
                    {
                        directProjectedOutline?.Dispose(); directProjectedOutline = unpublished;
                        directOutlinePlacement = Matrix3x2.Identity; directOutlineVisible = true;
                    }
                    unpublished = null; directOutlinePublications++; directOutlineLastBuildMs = clock.Elapsed.TotalMilliseconds;
                    if (request.Session is null) { UpdateDirectOutlinePlacement(); CompleteMarqueePreview(); }
                    CanvasControl.Invalidate();
                }
                catch (OperationCanceledException) { directOutlineAbandoned++; }
                catch (Exception ex)
                {
                    if (ReferenceEquals(latestDirectProjection, request)) latestDirectProjection = null;
                    directOutlineFailures++; App.Log($"Direct selection outline failed: {ex}");
                }
                finally { unpublished?.Dispose(); }
            }
        }
        finally { directOutlineWorkerBusy = false; }
    }

    private DirectOutlineResource BuildDirectOutline(DirectOutlineRequest request)
    {
        var token = request.Token; token.ThrowIfCancellationRequested();
        var edges = request.Source?.Edges ?? SelectionContours.Build(request.Selection.Translated(-(int)request.Offset.X, -(int)request.Offset.Y), token);
        var points = new Vector2[checked(edges.Length * 2)];
        Func<PointD, PointD>? carry = request.Corners is null ? null : DistortWarp.DocumentMap(request.Original!, request.Corners);
        Vector2 Map(int x, int y)
        {
            PointD point = new(x, y);
            if (carry is not null)
            {
                var original = request.Original!; var value = request.Value!;
                var unit = BrushEngine.DocumentToPixel(original, 1, 1, new(x + request.Offset.X, y + request.Offset.Y));
                point = carry(original.Point(new(value.FlipX ? 1 - unit.X : unit.X, value.FlipY ? 1 - unit.Y : unit.Y)));
            }
            var result = new Vector2((float)point.X, (float)point.Y);
            if (!float.IsFinite(result.X) || !float.IsFinite(result.Y)) throw new InvalidOperationException("Selection outline mapping produced a nonfinite point.");
            return result;
        }
        using var builder = new CanvasPathBuilder(CanvasDevice.GetSharedDevice());
        for (int i = 0; i < edges.Length; i++)
        {
            if ((i & 255) == 0) token.ThrowIfCancellationRequested();
            var edge = edges[i]; var start = Map(edge.X0, edge.Y0); var end = Map(edge.X1, edge.Y1);
            points[i * 2] = start; points[i * 2 + 1] = end;
            builder.BeginFigure(start); builder.AddLine(end); builder.EndFigure(CanvasFigureLoop.Open);
        }
        CanvasGeometry? geometry = edges.Length == 0 ? null : CanvasGeometry.CreatePath(builder);
        try
        {
            token.ThrowIfCancellationRequested();
            var result = new DirectOutlineResource(geometry, edges, points, () => Interlocked.Increment(ref directOutlineDisposed));
            geometry = null;
            int live = Interlocked.Increment(ref directOutlineCreated) - Volatile.Read(ref directOutlineDisposed);
            int previous = Volatile.Read(ref directOutlinePeakResources);
            while (live > previous)
            {
                int observed = Interlocked.CompareExchange(ref directOutlinePeakResources, live, previous);
                if (observed == previous) break; previous = observed;
            }
            return result;
        }
        finally { geometry?.Dispose(); }
    }

    private void DrawDirectSelectionOutline(CanvasDrawingSession drawing)
    {
        if (!directOutlineEnabled || !directOutlineVisible || windowClosed) return;
        var outline = directProjectedOutline ?? directSourceOutline;
        if (outline is null || outline.IsDisposed || outline.Geometry is not { } geometry) return;
        directOutlineStroke ??= new() { CustomDashStyle = [4, 3], StartCap = CanvasCapStyle.Flat,
            EndCap = CanvasCapStyle.Flat, DashCap = CanvasCapStyle.Flat };
        var previous = drawing.Transform; var origin = viewport.Origin;
        try
        {
            drawing.Transform = directOutlinePlacement * Matrix3x2.CreateScale((float)viewport.PointsPerPixel)
                * Matrix3x2.CreateTranslation((float)origin.X, (float)origin.Y);
            drawing.DrawGeometry(geometry, Microsoft.UI.ColorHelper.FromArgb(255, 76, 194, 255),
                (float)(1 / viewport.PointsPerPixel), directOutlineStroke);
#if DEBUG
            directOutlineDraws++;
#endif
        }
        finally { drawing.Transform = previous; }
    }
}
