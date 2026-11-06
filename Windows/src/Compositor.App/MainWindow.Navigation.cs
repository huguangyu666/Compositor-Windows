using System.Globalization;
using System.Numerics;
using Compositor.Core;
using Compositor.Renderer;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace Compositor.App;

public sealed class ViewportSurface : ContentControl
{
    private InputSystemCursorShape? cursorShape;
    public ViewportSurface()
    {
        Template = (ControlTemplate)XamlReader.Load("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ContentControl'><ContentPresenter Content='{TemplateBinding Content}' HorizontalContentAlignment='Stretch' VerticalContentAlignment='Stretch'/></ControlTemplate>");
    }
    internal void SetCursor(InputSystemCursorShape shape)
    {
        if (cursorShape == shape) return;
        cursorShape = shape; ProtectedCursor = InputSystemCursor.Create(shape);
    }
}

public sealed partial class MainWindow
{
    private CanvasViewport viewport = new(1, 1);
    private readonly MatrixTransform viewportTransform = new();
    private bool viewportFrameQueued;
    private bool spaceHeld;
    private PointD? panLast;
    private (PointD Start, double Zoom, bool Moved)? zoomDrag;
    private bool samplingPalette;
    private bool syncingZoom;
    private bool creatingRulerGuide;
    private int viewportFrameCount;
    private Windows.UI.Color sampleOriginal;
    private PointD? rulerPointer;
    private GuideAxis? rulerGuideAxis;
    private EditorDocument? rulerGuideOwner;
    private double rulerGuidePosition;
    private static string RulersPreferencePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Compositor", "show-rulers.txt");
    private bool HasEditingGesture => pointerStroke is not null || placementDragActive || cropDragging || dragStart is not null
        || rulerGuideOwner is not null || draggingGuideId is not null || healingPointerDown || gradientDragHandle >= 0 || lassoDrawing || selectionMove is not null || pixelMove is not null;

    private void InitializeNavigation()
    {
        CanvasHost.RenderTransform = viewportTransform;
        CanvasScroll.SizeChanged += (_, _) => ResizeViewport();
        CanvasStage.PointerPressed += OnViewportPressed;
        CanvasStage.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnViewportMoved), true);
        CanvasStage.PointerReleased += OnViewportReleased;
        CanvasStage.PointerCanceled += (_, _) => { CancelFloatingPointerForFocus(); CancelPixelMove(focusLoss: true); EndSelectionMove(); StopSelectionAutoscroll(); CancelNavigation(); };
        CanvasStage.PointerCaptureLost += (_, _) => { CancelFloatingPointerForFocus(); CancelPixelMove(focusLoss: true); EndSelectionMove(); StopSelectionAutoscroll(); CancelNavigation(); };
        CanvasStage.PointerExited += (_, _) => { rulerPointer = null; InvalidateRulers(); };
        CanvasStage.PointerWheelChanged += OnViewportWheel;
        CanvasStage.ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY | ManipulationModes.Scale;
        CanvasStage.ManipulationDelta += (_, args) =>
        {
            if (HasEditingGesture) return;
            ApplyViewportManipulation(args.Delta.Scale, new(args.Delta.Translation.X, args.Delta.Translation.Y), new(args.Position.X, args.Position.Y));
            args.Handled = true;
        };
        RootGrid.Loaded += (_, _) => { RootGrid.XamlRoot.Changed += (_, _) => ResizeViewport(); ResizeViewport(); };
        Content.KeyUp += (_, args) => ReleaseTemporaryPan(args.Key);
        RootGrid.GotFocus += (_, args) => { if (args.OriginalSource is TextBox or RichEditBox) { spaceHeld = false; CancelFloatingPointerForFocus(); CancelPixelMove(focusLoss: true); EndSelectionMove(); StopSelectionAutoscroll(); CancelNavigation(); } };
        Activated += (_, args) =>
        {
#if DEBUG
            // #33's open question is whether the silent focus events correlate with the window being in the
            // foreground, so the activation stream is counted rather than remembered: a probe run that never
            // activates is itself a data point, and one that does says when.
            windowActivationCount++;
            lastActivationState = args.WindowActivationState.ToString();
            if (args.WindowActivationState == WindowActivationState.Deactivated) windowDeactivationCount++;
#endif
            if (args.WindowActivationState == WindowActivationState.Deactivated)
            {
                EndOpacityEdit();
                spaceHeld = false; CancelFloatingPointerForFocus(); CancelPixelMove(focusLoss: true); EndSelectionMove(); StopSelectionAutoscroll(); CancelNavigation();
                return;
            }
            // Coming back to the foreground is where a change that arrived while the window was away gets
            // its answer, as on the Mac (ProjectController+ExternalChanges.swift:29-31).
            ResumeDeferredExternalChange();
        };
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            try { RulersToggle.IsChecked = File.Exists(RulersPreferencePath) && File.ReadAllText(RulersPreferencePath).Trim() == "true"; RefreshRulersVisibility(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { App.Log($"Rulers preference failed: {ex.Message}"); }
        });
    }

    private void ResizeViewport()
    {
        double width = CanvasScroll.ActualWidth, height = CanvasScroll.ActualHeight;
        if (width <= 0 || height <= 0) return;
        CanvasStage.Width = width; CanvasStage.Height = height;
        CanvasStage.Clip = new RectangleGeometry { Rect = new(0, 0, width, height) };
        viewport.Resize(width, height, RootGrid.XamlRoot?.RasterizationScale ?? 1);
        QueueViewportFrame();
    }

    private void QueueViewportFrame()
    {
        if (viewportFrameQueued || windowClosed) return;
        viewportFrameQueued = true; CompositionTarget.Rendering += OnViewportFrame;
    }
    private void OnViewportFrame(object? sender, object args) => FlushViewport();
    private void FlushViewport()
    {
        if (viewportFrameQueued) CompositionTarget.Rendering -= OnViewportFrame;
        viewportFrameQueued = false;
        viewportFrameCount++;
        var origin = viewport.Origin;
        RefreshAiActions();
        viewportTransform.Matrix = new(viewport.PointsPerPixel, 0, 0, viewport.PointsPerPixel, origin.X, origin.Y);
        ZoomLabel.Text = $"{viewport.Zoom * 100:0.#}%";
        syncingZoom = true; try { NavigationZoomBox.Value = viewport.Zoom * 100; } finally { syncingZoom = false; }
        UpdateSelectionStrokes(); UpdateTransformHandles(); RefreshGuides();
        if (gradientEdit is not null) ShowGradientHandles();
        CanvasControl.Invalidate();
        InvalidateRulers(); RefreshNavigationCursor();
    }

    private void SetViewportZoom(double value, PointD? anchor = null)
    { viewport.SetZoom(value, anchor ?? viewport.Center); QueueViewportFrame(); }
    private void OnNavigationZoomChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    { if (!syncingZoom && double.IsFinite(args.NewValue) && args.NewValue > 0) SetViewportZoom(args.NewValue / 100); }
    private void OnNavigationZoomKey(object sender, KeyRoutedEventArgs args)
    { if (args.Key is VirtualKey.Enter or VirtualKey.Escape) { CanvasStage.Focus(FocusState.Programmatic); args.Handled = true; } }

    private bool HandleNavigationKey(KeyRoutedEventArgs args)
    {
        if (renamingLayer || textDialogOpen || args.OriginalSource is TextBox or RichEditBox || args.KeyStatus.IsMenuKeyDown
            || InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return false;
        if (IsCancelPress(args.Key) && (panLast is not null || zoomDrag is not null || samplingPalette || rulerGuideOwner is not null))
        { CancelNavigation(); args.Handled = true; return true; }
        if (!BeginTemporaryPan(args.Key)) return false;
        args.Handled = true; return true;
    }
    private void RefreshNavigationCursor() => CanvasStage.SetCursor(panLast is not null || selectionMove is not null ? InputSystemCursorShape.SizeAll
        : spaceHeld || tool == CanvasTool.Hand ? InputSystemCursorShape.Hand
        : tool is CanvasTool.Zoom or CanvasTool.Eyedropper ? InputSystemCursorShape.Cross : InputSystemCursorShape.Arrow);

    private static PointD StagePoint(PointerRoutedEventArgs args, UIElement stage)
    { var point = args.GetCurrentPoint(stage).Position; return new(point.X, point.Y); }
    private bool TryBeginNavigation(PointerRoutedEventArgs args)
    {
        if (document is null) return false;
        // Fingers navigate; mouse and pen retain the document tools. The recognizer owns pinch/translation.
        if (args.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch) return true;
        if (HasEditingGesture) return false;
        var properties = args.GetCurrentPoint(CanvasStage).Properties;
        var point = StagePoint(args, CanvasStage);
        if ((!properties.IsMiddleButtonPressed && !properties.IsLeftButtonPressed)
            || !BeginNavigationAt(point, properties.IsMiddleButtonPressed, args.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Menu))) return false;
        CanvasStage.Focus(FocusState.Pointer); CanvasStage.CapturePointer(args.Pointer); args.Handled = true; RefreshNavigationCursor(); return true;
    }
    private bool BeginNavigationAt(PointD point, bool middle, bool alt)
    {
        if (document is null || HasEditingGesture) return false;
        if (middle || spaceHeld || tool == CanvasTool.Hand) panLast = point;
        else if (tool == CanvasTool.Zoom) zoomDrag = (point, viewport.Zoom, false);
        else if (tool == CanvasTool.Eyedropper || (alt && tool is CanvasTool.Brush or CanvasTool.Eraser or CanvasTool.Healing or CanvasTool.Gradient))
        {
            samplingPalette = true; sampleOriginal = tool == CanvasTool.Eyedropper && EyedropperTargetCombo.SelectedIndex == 1 ? backgroundColor : foregroundColor;
            SamplePalette(point, tool == CanvasTool.Eyedropper && EyedropperTargetCombo.SelectedIndex == 1);
        }
        else return false;
        RefreshNavigationCursor(); return true;
    }
    private void OnViewportPressed(object sender, PointerRoutedEventArgs args)
    {
        if (TryBeginNavigation(args) || args.Pointer.PointerDeviceType == PointerDeviceType.Touch) return;
        if (args.GetCurrentPoint(CanvasStage).Properties.IsLeftButtonPressed && HandlePixelMovePress(viewport.ToDocument(StagePoint(args, CanvasStage)), args.KeyModifiers))
        { CanvasStage.Focus(FocusState.Pointer); CanvasStage.CapturePointer(args.Pointer); args.Handled = true; return; }
        if (args.GetCurrentPoint(CanvasStage).Properties.IsLeftButtonPressed && TryBeginSelectionMove(viewport.ToDocument(StagePoint(args, CanvasStage)), args.KeyModifiers))
        { CanvasStage.Focus(FocusState.Pointer); CanvasStage.CapturePointer(args.Pointer); args.Handled = true; }
    }
    private void OnViewportMoved(object sender, PointerRoutedEventArgs args)
    {
        var point = StagePoint(args, CanvasStage); rulerPointer = point; InvalidateRulers();
        if (!args.Handled && pixelMove is not null) { MovePixelsTo(viewport.ToDocument(point), args.KeyModifiers); args.Handled = true; return; }
        if (!args.Handled && selectionMove is not null)
        { MoveSelectionTo(viewport.ToDocument(point), args.KeyModifiers); TrackSelectionAutoscroll(point, args.KeyModifiers); args.Handled = true; return; }
        if (MoveNavigationTo(point)) args.Handled = true;
    }
    private bool MoveNavigationTo(PointD point)
    {
        if (panLast is { } last)
        { viewport.Pan(new(point.X - last.X, point.Y - last.Y)); panLast = point; QueueViewportFrame(); }
        else if (zoomDrag is { } drag)
        {
            double delta = point.X - drag.Start.X;
            bool moved = drag.Moved || Math.Abs(delta) >= 3;
            zoomDrag = (drag.Start, drag.Zoom, moved);
            if (moved) SetViewportZoom(drag.Zoom * Math.Pow(2, delta / 100), drag.Start);
        }
        else if (samplingPalette)
        { SamplePalette(point, tool == CanvasTool.Eyedropper && EyedropperTargetCombo.SelectedIndex == 1); }
        else return false;
        return true;
    }
    private void OnViewportReleased(object sender, PointerRoutedEventArgs args)
    {
        if (pixelMove is not null) { _ = FinishPixelMoveAsync(); CanvasStage.ReleasePointerCapture(args.Pointer); args.Handled = true; return; }
        if (selectionMove is not null)
        { EndSelectionMove(click: true); args.Handled = true; return; }
        if (!EndNavigationAt(StagePoint(args, CanvasStage), args.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Menu))) return;
        CanvasStage.ReleasePointerCapture(args.Pointer); args.Handled = true;
    }
    private bool EndNavigationAt(PointD point, bool alt)
    {
        if (panLast is null && zoomDrag is null && !samplingPalette) return false;
        MoveNavigationTo(point);
        if (zoomDrag is { Moved: false } drag) SetViewportZoom(viewport.Zoom * (alt ? .5 : 2), drag.Start);
        CancelNavigation(); FlushViewport(); return true;
    }
    private void CancelNavigation()
    {
        bool guideWasActive = rulerGuideAxis is not null || draggingGuideId is not null;
        bool navigationWasActive = panLast is not null || zoomDrag is not null || samplingPalette;
        panLast = null; zoomDrag = null; samplingPalette = false;
        if (SampleRingCanvas is not null) SampleRingCanvas.Visibility = Visibility.Collapsed;
        rulerGuideAxis = null; rulerGuideOwner = null; draggingGuideId = null;
        if (RulerGuidePreview is not null) RulerGuidePreview.Visibility = Visibility.Collapsed;
        if (guideWasActive) RefreshGuides();
        // Clear state before releasing capture: capture-lost routes back through cancellation.
        if (navigationWasActive) CanvasStage.ReleasePointerCaptures();
        if (guideWasActive) { CanvasHost.ReleasePointerCaptures(); HorizontalRuler.ReleasePointerCaptures(); VerticalRuler.ReleasePointerCaptures(); }
        RefreshNavigationCursor();
    }
    private void OnViewportWheel(object sender, PointerRoutedEventArgs args)
    {
        if (HasEditingGesture) return;
        var properties = args.GetCurrentPoint(CanvasStage).Properties;
        double delta = properties.MouseWheelDelta;
        ApplyViewportWheel(delta, properties.IsHorizontalMouseWheel || args.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift),
            args.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control) || args.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Menu), StagePoint(args, CanvasStage));
        args.Handled = true;
    }
    private void ApplyViewportWheel(double delta, bool horizontal, bool zoom, PointD point)
    {
        if (HasEditingGesture) return;
        if (zoom) SetViewportZoom(viewport.Zoom * Math.Exp(delta * .0015), point);
        else
        {
            viewport.Pan(horizontal ? new(delta / 3, 0) : new(0, delta / 3)); QueueViewportFrame();
        }
    }
    private void ApplyViewportManipulation(double scale, PointD translation, PointD anchor)
    {
        if (HasEditingGesture) return;
        viewport.SetZoom(viewport.Zoom * scale, anchor); viewport.Pan(translation); QueueViewportFrame();
    }

    private bool SamplePalette(PointD viewPoint, bool background)
    {
        var pixel = viewport.ToDocument(viewPoint);
        if (presented is null || document is null || HasPendingInteraction || HasPendingDirty || workerBusy
            || pixel.X < 0 || pixel.Y < 0 || pixel.X >= document.Snapshot.Manifest.Width || pixel.Y >= document.Snapshot.Manifest.Height) return false;
        var bytes = presented.GetPixelBytes((int)Math.Floor(pixel.X), (int)Math.Floor(pixel.Y), 1, 1);
        if (CompositeColor.FromBgra(bytes) is not { } color) return false;
        var sampled = Microsoft.UI.ColorHelper.FromArgb(255, color.Red, color.Green, color.Blue);
        if (background) backgroundColor = sampled; else foregroundColor = sampled;
        RefreshPaletteChips();
        Canvas.SetLeft(SampleRingCanvas, viewPoint.X - 58); Canvas.SetTop(SampleRingCanvas, viewPoint.Y - 58);
        SampleRingCanvas.Visibility = SampleRingCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SampleRingCanvas.Invalidate(); return true;
    }
    private void OnSampleRingDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var drawing = args.DrawingSession;
        drawing.DrawCircle(58, 58, 46, sampleOriginal, 16);
        using (drawing.CreateLayer(1, new Rect(58, 0, 58, 116)))
            drawing.DrawCircle(58, 58, 46, EyedropperTargetCombo.SelectedIndex == 1 && tool == CanvasTool.Eyedropper ? backgroundColor : foregroundColor, 16);
        drawing.DrawCircle(58, 58, 54, Microsoft.UI.Colors.White, 1);
        drawing.DrawCircle(58, 58, 38, Microsoft.UI.Colors.White, 1);
    }

    private void OnShowRulers(object sender, RoutedEventArgs args)
    {
        RefreshRulersVisibility();
        try { Directory.CreateDirectory(Path.GetDirectoryName(RulersPreferencePath)!); File.WriteAllText(RulersPreferencePath, RulersToggle.IsChecked ? "true" : "false"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { App.Log($"Rulers preference failed: {ex.Message}"); }
    }
    private void RefreshRulersVisibility()
    {
        bool visible = RulersToggle.IsChecked;
        RulerRow.Height = new(visible ? CanvasRuler.Thickness : 0); RulerColumn.Width = new(visible ? CanvasRuler.Thickness : 0);
        HorizontalRuler.Visibility = VerticalRuler.Visibility = RulerCorner.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        InvalidateRulers();
    }
    private void InvalidateRulers()
    { if (RulersToggle.IsChecked) { HorizontalRuler.Invalidate(); VerticalRuler.Invalidate(); } }
    private void OnRulerDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        bool horizontal = ReferenceEquals(sender, HorizontalRuler);
        double length = horizontal ? sender.ActualWidth : sender.ActualHeight;
        DrawRulerContent(args.DrawingSession, horizontal, length);
    }
    private void DrawRulerContent(CanvasDrawingSession drawing, bool horizontal, double length)
    {
        double origin = horizontal ? viewport.Origin.X : viewport.Origin.Y;
        float hairline = (float)(1 / viewport.BackingScale);
        var tickColor = Microsoft.UI.ColorHelper.FromArgb(255, 158, 158, 158);
        using var format = new CanvasTextFormat { FontFamily = "Segoe UI", FontSize = 8 };
        foreach (var tick in CanvasRuler.Ticks(origin, length, viewport.PointsPerPixel))
        {
            float position = (float)tick.Position;
            if (horizontal) drawing.FillRectangle(position - hairline / 2, 18 - tick.Length, hairline, tick.Length, tickColor);
            else drawing.FillRectangle(18 - tick.Length, position - hairline / 2, tick.Length, hairline, tickColor);
            if (tick.Length != 8) continue;
            string label = Math.Round(tick.Value).ToString("0", CultureInfo.InvariantCulture);
            if (horizontal) drawing.DrawText(label, position + 2, 0, Microsoft.UI.Colors.LightGray, format);
            else
            {
                drawing.Transform = Matrix3x2.CreateRotation(MathF.PI / 2) * Matrix3x2.CreateTranslation(11, position + 2);
                drawing.DrawText(label, 0, 0, Microsoft.UI.Colors.LightGray, format); drawing.Transform = Matrix3x2.Identity;
            }
        }
        if (rulerPointer is { } pointer)
        {
            float position = (float)(horizontal ? pointer.X : pointer.Y);
            if (horizontal) drawing.DrawLine(position, 0, position, 18, Microsoft.UI.Colors.DeepSkyBlue, hairline);
            else drawing.DrawLine(0, position, 18, position, Microsoft.UI.Colors.DeepSkyBlue, hairline);
        }
        var edgeColor = Microsoft.UI.ColorHelper.FromArgb(255, 20, 20, 20);
        if (horizontal) drawing.FillRectangle(0, 18 - hairline, (float)length, hairline, edgeColor);
        else drawing.FillRectangle(18 - hairline, 0, hairline, (float)length, edgeColor);
    }
    private void OnRulerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (document is null || HasEditingGesture || !args.GetCurrentPoint((UIElement)sender).Properties.IsLeftButtonPressed) return;
        BeginRulerGuideAt(ReferenceEquals(sender, HorizontalRuler) ? GuideAxis.Horizontal : GuideAxis.Vertical, StagePoint(args, CanvasStage));
        ((UIElement)sender).CapturePointer(args.Pointer); args.Handled = true;
    }
    /// <summary>
    /// The Mac's hitGuide (Guides.swift:156-172): of the guides that are currently shown and editable,
    /// the one nearest the pointer within reach. Hidden or locked guides return nothing, so the pointer
    /// reaches the tool underneath instead of nudging a line the user cannot see or is not allowed to move.
    /// </summary>
    private CanvasGuide? HitGuide(PointD documentPoint)
    {
        if (document is not { } owner || !showsGuides || locksGuides) return null;
        if (owner.Snapshot.Manifest.Guides is not { Count: > 0 } guides) return null;
        double reach = 6 / viewport.PointsPerPixel;
        CanvasGuide? nearest = null;
        double nearestDistance = double.MaxValue;
        foreach (var guide in guides)
        {
            double distance = Math.Abs((guide.Axis == GuideAxis.Vertical ? documentPoint.X : documentPoint.Y) - guide.Position);
            if (distance > reach || distance >= nearestDistance) continue;
            nearest = guide;
            nearestDistance = distance;
        }
        return nearest;
    }

    /// <summary>
    /// The Mac's snappedGuidePosition (Guides.swift:288-305): a guide being dragged from a ruler lands on an
    /// alignment line when one is within 10 view points of it, the closest winning. Its target list is its
    /// own — the layout grid, bounds, same-axis guides and every layer that has pixels — not the move-drag
    /// list, because it must also be able to sit on the layer the pointer is moving and on guides of this
    /// axis only.
    /// </summary>
    private double SnapGuidePosition(double position, GuideAxis axis)
    {
        if (!snapEnabled || document is not { } owner) return position;
        var manifest = owner.Snapshot.Manifest;
        double length = axis == GuideAxis.Vertical ? manifest.Width : manifest.Height;
        var targets = new List<double>();
        // The grid branch the move list has too: shown and switched on, or it is not a target (Guides.swift:292).
        if (snapToGrid && showsGrid) targets.AddRange(LayoutGrid.Lines(length));
        if (snapToGuides && showsGuides && manifest.Guides is { } guides)
            targets.AddRange(guides.Where(guide => guide.Axis == axis && guide.Id != draggingGuideId).Select(guide => guide.Position));
        if (snapToBounds) targets.AddRange([0, length / 2, length]);
        if (snapToLayers)
        {
            var snapshot = LayerDisplaySnapshot!;
            foreach (var record in snapshot.Manifest.Layers.Where(record => snapshot.Images.ContainsKey(record.Id)))
            {
                var corners = TransformDrag.HandlePositions(record.Transform);
                double[] values = [.. corners.Select(point => axis == GuideAxis.Vertical ? point.X : point.Y)];
                double min = values.Min(), max = values.Max();
                targets.AddRange([Math.Round(min, MidpointRounding.AwayFromZero),
                    Math.Round((min + max) / 2, MidpointRounding.AwayFromZero),
                    Math.Round(max, MidpointRounding.AwayFromZero)]);
            }
        }
        double tolerance = 10 / Math.Max(viewport.PointsPerPixel, .0001);
        double best = position;
        bool found = false;
        foreach (double target in targets)
        {
            if (Math.Abs(target - position) > tolerance) continue;
            if (found && Math.Abs(best - position) <= Math.Abs(target - position)) continue;
            best = target;
            found = true;
        }
        return best;
    }

    private void BeginRulerGuideAt(GuideAxis axis, PointD point)
    {
        // The Mac creates a guide through the same canEditGuides guard as moving one (Guides.swift:139,
        // :174-178), so a locked document cannot pull a new line out of a ruler either — and a created guide
        // switches View ▸ Guides back on, since a guide the user cannot see is not a guide.
        if (IsFileImporting || locksGuides) return;
        if (!showsGuides) SetGuidesVisible(true);
        rulerGuideAxis = axis; rulerGuideOwner = document; MoveRulerGuideTo(point);
    }
    private void BeginExistingGuideAt(CanvasGuide guide, PointD point)
    {
        if (IsFileImporting) return;
        // View ▸ Lock Guides (⌘⌥; on the Mac, CompositorApp.swift:150-151 → Guides.swift:139): the guides stay
        // where they are, and a click on one does nothing rather than nudging it a pixel on the way to a miss.
        if (locksGuides) return;
        draggingGuideId = guide.Id; rulerGuideAxis = guide.Axis; rulerGuideOwner = document;
        RefreshGuides(); MoveRulerGuideTo(point);
    }
    private void OnRulerMoved(object sender, PointerRoutedEventArgs args)
    {
        MoveRulerGuideTo(StagePoint(args, CanvasStage)); args.Handled = true;
    }
    private void MoveRulerGuideTo(PointD point)
    {
        if (rulerGuideAxis is not { } axis) return;
        var pixel = viewport.ToDocument(point);
        // Guides.swift:288-305: what a guide lands on is the snapped position, and the preview has to sit on
        // the line it will commit to rather than under the pointer — otherwise the user aims at one thing
        // and gets the other.
        rulerGuidePosition = SnapGuidePosition(axis == GuideAxis.Horizontal ? pixel.Y : pixel.X, axis);
        var view = viewport.ToView(axis == GuideAxis.Horizontal
            ? new PointD(pixel.X, rulerGuidePosition)
            : new PointD(rulerGuidePosition, pixel.Y));
        RulerGuidePreview.Visibility = Visibility.Visible;
        RulerGuidePreview.X1 = axis == GuideAxis.Vertical ? view.X : 0;
        RulerGuidePreview.X2 = axis == GuideAxis.Vertical ? view.X : viewport.Width;
        RulerGuidePreview.Y1 = axis == GuideAxis.Horizontal ? view.Y : 0;
        RulerGuidePreview.Y2 = axis == GuideAxis.Horizontal ? view.Y : viewport.Height;
    }
    private void OnRulerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (rulerGuideAxis is null) return;
        EndRulerGuideAt(StagePoint(args, CanvasStage));
        ((UIElement)sender).ReleasePointerCapture(args.Pointer); args.Handled = true;
    }
    private void EndRulerGuideAt(PointD point)
    {
        if (rulerGuideAxis is not { } axis) return;
        MoveRulerGuideTo(point);
        bool overRuler = RulersToggle.IsChecked && (point.X < 0 || point.Y < 0);
        if (double.IsFinite(rulerGuidePosition) && ReferenceEquals(document, rulerGuideOwner))
        {
            creatingRulerGuide = true;
            try
            {
                if (draggingGuideId is { } id)
                {
                    if (overRuler) document!.RemoveGuide(id);
                    else document!.MoveGuide(id, rulerGuidePosition);
                }
                else if (!overRuler) document!.AddGuide(axis, rulerGuidePosition);
            }
            finally { creatingRulerGuide = false; }
            RefreshGuides();
        }
        CancelNavigation();
    }
    private void OnRulerCancelled(object sender, PointerRoutedEventArgs args) => CancelNavigation();
}
