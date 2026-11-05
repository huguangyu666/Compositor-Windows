using Compositor.Core;
using Compositor.Renderer;
using System.Runtime.CompilerServices;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private readonly RenderInputQueue renderInputs = new();
    private PointerStroke? pointerStroke;
#if DEBUG
    /// <summary>Which member last discarded an open stroke. Several commands close the stroke on the
    /// far side of an await, so a stroke that opened while they were suspended can be eaten by a
    /// command the user issued before it; this names the eater without a debugger. Probe-only: the
    /// release binary never asks who closed a gesture, and a normal close is not an anomaly.</summary>
    private string pointerStrokeClosedBy = "never";
#endif
    private long frameRevision;
    private int renderFailureEpoch;
    private bool windowClosed;
    private bool placementFinishing;
    private bool startingRasterStroke;
    private Guid? transformLayerId;
    private ProjectSnapshot? renderSnapshot, renderSnapshotSource;

    private ProjectSnapshot CaptureRenderSnapshot(ProjectSnapshot source)
    {
        // Reuse metadata during pixel-only strokes. Controls may edit manifest records
        // while a worker renders, so its fingerprint and render must read one frozen view.
        if (renderSnapshot is not null && ReferenceEquals(renderSnapshotSource, source)) return renderSnapshot;
        renderSnapshotSource = source;
        return renderSnapshot = new()
        {
            Manifest = ManifestJson.Deserialize(ManifestJson.Serialize(source.Manifest)),
            Images = source.Images, Masks = source.Masks,
        };
    }

    private void FinishPlacementDrag()
    {
        if (floatingDuplicate is { } gesture) { gesture.Released = true; return; }
        if (document is not { } owner || placementFinishing) return;
        placementFinishing = true;
        QueueCanvasInput(owner, () =>
        {
            // Cache mutation and the final pending point use the same serial owner as paint.
            ApplyPendingInteraction();
            if (transformPointerDraftActive)
            {
                transformPointerDraftActive = placementDragActive = transformDragActive = false;
                transformDrag = null; pendingTransformPoint = null;
                SnapLineX.Visibility = SnapLineY.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                snapXs = snapYs = null; snapLockX = snapLockY = null;
                placementFinishing = false;
                if (!transformPersistent) CommitTransformNow();
                else { RefreshTransformInspector(); UpdateTransformHandles(); }
                return;
            }
            placementDragActive = false;
            snapXs = snapYs = null;
            foreach (var id in interactedLayers)
            {
                compositor?.DropCachesFor(id);
                if (owner.Snapshot.Manifest.Layers.FirstOrDefault(layer => layer.Id == id) is { } record)
                    MarkInteractionDirty(BoundsOf(record), grow: 2);
            }
            interactedLayers.Clear();
            if (transformDragActive && transformKey is { } key)
            {
                owner.EndTransform(key);
                transformDrag = null;
                transformKey = null;
                transformLayerId = null;
                transformDragActive = false;
                SnapLineX.Visibility = SnapLineY.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                UpdateTransformHandles();
                SetStatus(Loc.T("S_TransformApplied"));
            }
            dragLast = null;
            moveKey = null;
            moveRemainder = new(0, 0);
            snapLockX = snapLockY = null;
            placementFinishing = false;
            if (interactionDirty is not null) OnDocumentPixelsChanged();
        });
    }

    // Capture the target and settings at pointer-down. UI selection and controls can
    // change while this stroke is waiting for the render reader to release its pixels.
    private sealed class PointerStroke
    {
        public required EditorDocument Owner;
        public required Guid LayerId;
        public required CanvasTool Tool;
        public required BrushSettings Brush;
        public required BrushEndpoint Endpoint;
        public readonly object Key = new();
        public PointD Last, RawLast;
        public BrushPointerPath? Path;
        public PointD CloneOffset;
        public PointD CloneSource;
        public PointD? FixedCloneOffset;
        public bool OnMask, AllLayers, Started, RawShift, CloneAligned;
        public int BlurMode;
        public double BlurRadius, WarpStrength, Smoothing;
    }

    private void QueueCanvasInput(EditorDocument owner, Action apply)
    {
        renderInputs.Enqueue(owner, apply);
        PumpRender();
    }

    private void BeginPointerStroke(PointD point, bool shift = false)
    {
        if (IsFileImporting || IsGradientFinishing || IsDeletePending) return;
        if (pixelMove is not null || pixelNudgePending > 0) return;
        if (document is not { } owner || SelectedLayer is not { } layer)
        { SetStatus(Loc.T("S_SelectPaint")); return; }
        if (selectedLayerIds.Count > 1) { SetStatus(Loc.T("S_PaintMultipleLayers")); return; }
        if (!DrawOrder.Resolve(owner.Snapshot.Manifest.Layers).Visible.Contains(layer.Id))
        { SetStatus(Loc.F("S_PaintHiddenLayer", layer.Name)); return; }
        if (owner.ActiveSelection is { IsEmpty: true }) { SetStatus(Loc.T("S_EmptySelectionPaint")); return; }
        FinishPointerStroke();
        bool onMask = paintOnMask && layer.MaskFile is not null;
        var input = new PointerStroke
        {
            Owner = owner, LayerId = layer.Id, Tool = tool, Brush = CurrentBrush, Last = point, RawLast = point, RawShift = shift,
            Endpoint = new(owner, layer.Id, onMask, point, lastBrushEndpoint), OnMask = onMask,
            AllLayers = CloneAllLayersCheck.IsChecked == true,
            BlurMode = BlurModeCombo.SelectedIndex, BlurRadius = BlurRadiusBox?.Value ?? 10,
            WarpStrength = warpStrength, Smoothing = tool is CanvasTool.Brush or CanvasTool.Eraser ? brushSmoothing : 0,
        };
        if (input.OnMask && (input.Tool == CanvasTool.Clone || input.Tool == CanvasTool.Blur && input.BlurMode > 0))
        { SetStatus(Loc.T("S_PaintToolNeedsPixels")); return; }
        if (input.Tool == CanvasTool.Clone)
        {
            if (cloneSource is not { } source) { SetStatus(Loc.T("S_CloneNoSource")); return; }
            input.CloneSource = source; input.CloneAligned = CloneAlignedCheck.IsChecked == true;
            input.FixedCloneOffset = input.CloneAligned ? cloneAlignedOffset : null;
        }
        pointerStroke = input; lastBrushEndpoint = input.Endpoint; double zoom = viewport.Zoom;
        QueueCanvasInput(owner, () =>
        {
            var previous = LastValidBrushEndpoint(input.Endpoint.Previous);
            var start = shift && previous is not null && ReferenceEquals(previous.Owner, owner)
                && previous.LayerId == input.LayerId && previous.OnMask == input.OnMask ? previous.Point : point;
            input.Path = new(start, input.Smoothing); input.Last = start;
            if (input.Tool == CanvasTool.Clone)
            {
                bool sameSource = cloneSource == input.CloneSource;
                input.CloneOffset = (input.CloneAligned ? input.FixedCloneOffset ?? (sameSource ? cloneAlignedOffset : null) : null)
                    ?? new PointD(Math.Round(input.CloneSource.X - start.X, MidpointRounding.AwayFromZero),
                        Math.Round(input.CloneSource.Y - start.Y, MidpointRounding.AwayFromZero));
                if (input.CloneAligned && sameSource) cloneAlignedOffset ??= input.CloneOffset;
            }
            // Visibility can change while the first dab waits behind a render reader.
            bool visible = DrawOrder.Resolve(owner.Snapshot.Manifest.Layers).Visible.Contains(input.LayerId);
            if (visible) MarkStrokeSegment(input, start, start);
            startingRasterStroke = input.Tool is CanvasTool.Brush or CanvasTool.Eraser
                || input.Tool == CanvasTool.Blur && input.BlurMode == 0 || !input.OnMask && input.Tool == CanvasTool.Clone;
            try
            {
                input.Started = visible && (input.Tool switch
                {
                    CanvasTool.Brush or CanvasTool.Eraser => owner.BeginStroke(input.Key, input.LayerId, input.Brush,
                        start, input.OnMask, input.Tool == CanvasTool.Eraser),
                    CanvasTool.Clone => owner.BeginCloneStroke(input.LayerId, OffsetClone(input, start), start,
                        input.Brush.Radius, input.Brush.Hardness, input.AllLayers, input.Key, input.Brush.Opacity),
                    CanvasTool.Blur when input.BlurMode > 0 => owner.BeginWarpStroke(input.LayerId,
                        input.BlurMode == 2 ? WarpMode.Liquify : WarpMode.Smudge,
                        input.Brush.Radius * 2, input.Brush.Hardness, input.WarpStrength),
                    CanvasTool.Blur when input.OnMask => owner.BeginMaskBlurStroke(input.LayerId, input.BlurRadius, input.Brush, start, input.Key),
                    CanvasTool.Blur => owner.BeginBlurStroke(input.LayerId, input.BlurRadius, input.Key, input.Brush.Opacity),
                    _ => false,
                });
            }
            finally { startingRasterStroke = false; }
            if (input.Started && input.Tool == CanvasTool.Blur)
            {
                if (input.BlurMode > 0) owner.WarpStrokeAppend(start);
                else if (!input.OnMask) owner.BlurStampApply(input.LayerId, start, input.Brush.Radius, input.Brush.Hardness);
            }
            input.Endpoint.Valid = input.Started; input.Endpoint.Point = start;
            input.Endpoint.Previous = input.Started ? null : previous;
            if (input.Started && start != point && input.Path.Move(point, zoom) is { } connected) PaintPointerStrokePoint(input, connected);
            input.Path.ResetAxis(point, shift);
            if (!input.Started)
            {
                if (ReferenceEquals(pointerStroke, input))
                {
#if DEBUG
                    pointerStrokeClosedBy = $"first dab refused (tool={input.Tool}, onMask={input.OnMask})";
                    App.Log($"pointer stroke dropped by first dab refused (tool={input.Tool}, onMask={input.OnMask})");
#endif
                    pointerStroke = null;
                }
                SetStatus(PaintRefusalMessage(input));
            }
        });
    }

    private static PointD OffsetClone(PointerStroke input, PointD point) =>
        new(point.X + input.CloneOffset.X, point.Y + input.CloneOffset.Y);

    /// <summary>
    /// The brush's "why not" line, the counterpart of the macOS paintRefusal
    /// (EditorSession+Brush.swift:14). A folder and an adjustment layer now explain that the mask is
    /// the paintable target instead of answering with one generic message, which used to read as
    /// though the tool was broken.
    /// </summary>
    private string PaintRefusalMessage(PointerStroke input)
    {
        var reason = input.Owner.WhyPaintRefused(input.LayerId, input.OnMask);
        if (reason is null && !DrawOrder.Resolve(input.Owner.Snapshot.Manifest.Layers).Visible.Contains(input.LayerId))
            return Loc.F("S_PaintHiddenLayer", LayerName(input.LayerId));
        return reason switch
        {
            EditorDocument.PaintRefusal.FolderHasNoPixels => Loc.F("S_PaintFolderHasNoPixels", LayerName(input.LayerId)),
            EditorDocument.PaintRefusal.AdjustmentHasNoPixels => Loc.F("S_PaintAdjustmentHasNoPixels", LayerName(input.LayerId)),
            EditorDocument.PaintRefusal.MaskTurnedOff => Loc.T("S_PaintMaskTurnedOff"),
            EditorDocument.PaintRefusal.Locked => Loc.T("LP_Blocked"),
            EditorDocument.PaintRefusal.EmptySelection => Loc.T("S_PaintEmptySelection"),
            _ => Loc.T(input.Tool == CanvasTool.Clone ? "S_CloneNoSource" : "S_PaintNeedsLayer"),
        };
        string LayerName(Guid id) => input.Owner.Snapshot.Manifest.Layers.FirstOrDefault(layer => layer.Id == id)?.Name ?? string.Empty;
    }

    private void AppendPointerStroke(PointD point, bool shift = false)
    {
        if (pointerStroke is not { } input) return;
        if (point == input.RawLast && shift == input.RawShift) return;
        input.RawLast = point; input.RawShift = shift; double zoom = viewport.Zoom;
#if DEBUG
        // Measurement only: the wait a user feels starts at the point that was actually accepted, not at a
        // duplicate the input guard above dropped.
        if (input.Started) pointerPointStamp = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        QueueCanvasInput(input.Owner, () =>
        {
            if (input.Started && input.Path?.Move(point, zoom, shift) is { } painted) PaintPointerStrokePoint(input, painted);
        });
    }

    private void PaintPointerStrokePoint(PointerStroke input, PointD point, bool remember = true)
    {
        MarkStrokeSegment(input, input.Last, point); input.Last = point;
        if (remember) input.Endpoint.Point = point;
        switch (input.Tool)
        {
            case CanvasTool.Brush or CanvasTool.Eraser: input.Owner.ContinueStroke(input.Key, point); break;
            case CanvasTool.Clone:
                input.Owner.CloneStampAppend(input.LayerId, OffsetClone(input, point), point, input.Brush.Radius, input.Brush.Hardness); break;
            case CanvasTool.Blur when input.BlurMode > 0: input.Owner.WarpStrokeAppend(point); break;
            case CanvasTool.Blur when input.OnMask: input.Owner.ContinueStroke(input.Key, point); break;
            case CanvasTool.Blur: input.Owner.BlurStampApply(input.LayerId, point, input.Brush.Radius, input.Brush.Hardness); break;
        }
    }

    private void FinishPointerStroke(PointD? finalPoint = null, [CallerMemberName] string site = "", bool? shift = null)
    {
        if (pointerStroke is not { } input) return;
#if DEBUG
        pointerStrokeClosedBy = $"{site} (tool={input.Tool}, started={input.Started})";
        App.Log($"pointer stroke closed by {site} (tool={input.Tool}, started={input.Started})");
#endif
        if (finalPoint is { } point) AppendPointerStroke(point, shift ?? input.RawShift);
        pointerStroke = null;
        QueueCanvasInput(input.Owner, () =>
        {
            if (!input.Started) return;
            // The Mac flushes the string to the pointer on release without changing the remembered
            // filtered endpoint used by the next Shift-click (EditorSession+Brush.swift:137-143).
            if (input.Smoothing > 0 && input.Path is { } path && path.Pointer != input.Last)
                PaintPointerStrokePoint(input, path.Pointer, remember: false);
            switch (input.Tool)
            {
                case CanvasTool.Brush or CanvasTool.Eraser: input.Owner.EndStroke(input.Key); break;
                case CanvasTool.Clone: input.Owner.EndCloneStroke(); break;
                case CanvasTool.Blur when input.BlurMode > 0: input.Owner.EndWarpStroke(input.LayerId); break;
                case CanvasTool.Blur when input.OnMask: input.Owner.EndStroke(input.Key); UpdateTitle(); break;
                case CanvasTool.Blur: input.Owner.EndBlurStroke(); break;
            }
        });
    }

    private void MarkStrokeSegment(PointerStroke input, PointD previous, PointD point)
    {
        if (input.Tool == CanvasTool.Blur && input.BlurMode > 0) return;
        if (input.Owner.Snapshot.Manifest.Layers.FirstOrDefault(item => item.Id == input.LayerId) is not { } layer) return;
        var transform = input.OnMask ? layer.MaskPlacement ?? layer.Transform : layer.Transform;
        var image = input.Owner.Snapshot.Images.GetValueOrDefault(input.LayerId);
        var mask = input.OnMask ? input.Owner.Snapshot.Masks.GetValueOrDefault(input.LayerId) : null;
        int width = mask?.Width ?? image?.Width ?? (int)transform.Size.Width;
        int height = mask?.Height ?? image?.Height ?? (int)transform.Size.Height;
        MarkInteractionDirty(BrushEngine.StrokePresentationBounds(transform, width, height, previous, point, input.Brush.Radius), grow: 0);
    }
}
