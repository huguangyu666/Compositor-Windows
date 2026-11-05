#if DEBUG
using Compositor.Core;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task VerifySelectionMoveAsync(Dictionary<string, object> report)
    {
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        var scene = EditorDocument.Create(64, 48); OpenDocument(scene); await WaitForFramesAsync();
        ToolCombo.SelectedIndex = (int)CanvasTool.RectSelect; SetSelectionMode(LassoSelectionMode.Replace);
        var original = Selection.Rectangle(64, 48, 12, 10, 16, 12).Feathered(2);
        scene.SetSelection(original); await WaitForSelectionReadyAsync();
        var geometry = OutlineDisplayIdentity; var warm = compositor; int frames = presentedFrameCount;
        Require(TryBeginSelectionMove(new(16, 14), VirtualKeyModifiers.None), "New-mode press inside the selection did not begin outline movement.");
        Require(!CanEditSelection && HasEditingGesture && !scene.CanUndo, "An outline preview entered history or allowed another selection gesture.");
        MoveSelectionTo(new(80, -40), VirtualKeyModifiers.Control);
        Require(scene.ActiveSelection?.OutlineOffset == new PointD(64, -54) && scene.ActiveSelection.Feather == 2, "Outside move lost basis or feather.");
        Require(ReferenceEquals(geometry, OutlineDisplayIdentity) && OutlineDisplayOffset == new PointD(64, -54), "Outside move rebuilt geometry or lost its offset.");
        MoveSelectionTo(new(18.5, 17.5), VirtualKeyModifiers.Control);
        Require(scene.ActiveSelection?.OutlineOffset == new PointD(3, 4), "Movement was cumulative or failed source midpoint rounding.");
        Require(ReferenceEquals(original.OutlineCoverage, scene.ActiveSelection!.OutlineCoverage), "Outline preview copied the source mask.");
        EndSelectionMove(); EndSelectionMove();
        var moved = scene.ActiveSelection;
        Require(scene.CanUndo && selectionMove is null && !HasEditingGesture, "Movement did not close its gesture.");
        OnUndo(this, new()); await WaitForSelectionReadyAsync(); Require(ReferenceEquals(scene.ActiveSelection, original) && !scene.CanUndo, "Movement was not one Undo.");
        OnRedo(this, new()); await WaitForSelectionReadyAsync(); Require(ReferenceEquals(scene.ActiveSelection, moved), "Movement Redo lost the translated outline.");
        Require(ReferenceEquals(warm, compositor) && presentedFrameCount == frames && ReferenceEquals(geometry, OutlineDisplayIdentity), "Outline history recomposited pixels or rebuilt geometry.");
        report["selectionOutlineOutsideBasisHistoryAndGeometryReuse"] = "passed";

        OnUndo(this, new()); await WaitForSelectionReadyAsync();
        Require(scene.CanRedo && TryBeginSelectionMove(new(16, 14), VirtualKeyModifiers.None), "No-op fixture did not start.");
        MoveSelectionTo(new(19, 17), VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift);
        Require(scene.ActiveSelection?.OutlineOffset == new PointD(3, 0), "Shift tie did not constrain horizontally.");
        MoveSelectionTo(new(16, 14), VirtualKeyModifiers.Control); EndSelectionMove();
        Require(ReferenceEquals(original, scene.ActiveSelection) && scene.CanRedo && !scene.CanUndo, "Returning to the origin consumed Redo or inserted history.");
        report["selectionOutlineShiftAndNoOpPreservesRedo"] = "passed";

        Require(!TryBeginSelectionMove(new(16, 14), VirtualKeyModifiers.Shift), "Add began outline movement.");
        Require(!TryBeginSelectionMove(new(16, 14), VirtualKeyModifiers.Menu), "Subtract began outline movement.");
        Require(!TryBeginSelectionMove(new(16, 14), VirtualKeyModifiers.Control), "Temporary pixel movement was mistaken for outline movement.");
        Require(!TryBeginSelectionMove(new(11.5, 14), VirtualKeyModifiers.None), "Feather halo began outline movement.");
        ToolCombo.SelectedIndex = (int)CanvasTool.Lasso; LassoKindCombo.SelectedIndex = 1;
        BeginLasso(new(40, 30), VirtualKeyModifiers.None);
        Require(!TryBeginSelectionMove(new(16, 14), VirtualKeyModifiers.None), "A polygonal draft lost its next corner to movement.");
        CancelLasso(); LassoKindCombo.SelectedIndex = 0;
        var mask = new MaskSurface(64, 48); mask.Pixels[14 * 64 + 16] = 255; mask.Pixels[14 * 64 + 18] = 255;
        scene.SetSelection(Selection.FromCoverage(mask)); await WaitForSelectionReadyAsync();
        Require(!TryBeginSelectionMove(new(17.5, 14.5), VirtualKeyModifiers.None), "A mask hole began movement.");
        Require(TryBeginSelectionMove(new(16.5, 14.5), VirtualKeyModifiers.None), "Lasso tool could not move an existing outline."); EndSelectionMove();
        ToolCombo.SelectedIndex = (int)CanvasTool.Wand;
        Require(TryBeginSelectionMove(new(16.5, 14.5), VirtualKeyModifiers.None), "Wand could not move an existing outline."); EndSelectionMove();
        report["selectionOutlineModesHolesAndPolygonalPriority"] = "passed";

        scene = EditorDocument.Create(128, 96); OpenDocument(scene); await WaitForFramesAsync();
        ToolCombo.SelectedIndex = (int)CanvasTool.RectSelect; viewport.SetZoom(6, viewport.Center); FlushViewport();
        var layer = scene.Snapshot.Manifest.Layers[0]; layer.Transform.Origin = new(-21.5, 30.5); layer.Transform.Size = new(40, 20);
        scene.Snapshot.Manifest.Guides = [new() { Id = Guid.NewGuid(), Axis = GuideAxis.Vertical, Position = 50 }, new() { Id = Guid.NewGuid(), Axis = GuideAxis.Horizontal, Position = 22 }];
        original = Selection.Rectangle(128, 96, 20, 20, 10, 10); scene.SetSelection(original); await WaitForSelectionReadyAsync();
        Require(TryBeginSelectionMove(new(25, 25), VirtualKeyModifiers.None), "Snap fixture did not start.");
        Require(selectionMove!.Xs.Contains(-22) && selectionMove.Xs.Contains(19) && selectionMove.Xs.Contains(50)
            && selectionMove.Ys.Contains(31) && !selectionMove.Xs.Contains(-1), "Selection snap targets omitted active/off-canvas edges, guides or included target centers.");
        MoveSelectionTo(new(43, 25), VirtualKeyModifiers.None);
        Require(scene.ActiveSelection?.OutlineOffset.X == 20 && SnapLineX.Visibility == Microsoft.UI.Xaml.Visibility.Visible, "Outline edge did not snap to a guide at screen tolerance.");
        MoveSelectionTo(new(43, 25), VirtualKeyModifiers.Control);
        Require(scene.ActiveSelection?.OutlineOffset == new PointD(18, 0) && SnapLineX.Visibility == Microsoft.UI.Xaml.Visibility.Collapsed, "Control failed to bypass snapping.");
        MoveSelectionTo(new(43, 25), VirtualKeyModifiers.Shift);
        Require(scene.ActiveSelection?.OutlineOffset == new PointD(20, 0) && SnapLineY.Visibility == Microsoft.UI.Xaml.Visibility.Collapsed, "The Shift-locked axis snapped.");
        EndSelectionMove(); report["selectionOutlineSourceSnapTargetsAndAxisBypass"] = "passed";

        scene = EditorDocument.Create(64, 48); OpenDocument(scene); await WaitForFramesAsync();
        ToolCombo.SelectedIndex = (int)CanvasTool.RectSelect; original = Selection.Rectangle(64, 48, 12, 10, 16, 12);
        scene.SetSelection(original); await WaitForSelectionReadyAsync();
        Require(TryBeginSelectionMove(new(16, 14), VirtualKeyModifiers.None), "Click fixture did not start.");
        EndSelectionMove(click: true); await WaitForSelectionReadyAsync();
        Require(scene.ActiveSelection is null && scene.CanUndo, "Stationary marquee click did not deselect.");
        OnUndo(this, new()); await WaitForSelectionReadyAsync(); Require(ReferenceEquals(original, scene.ActiveSelection) && !scene.CanUndo, "Click deselection history.");
        ToolCombo.SelectedIndex = (int)CanvasTool.Wand; WandAllLayersCheck.IsChecked = true;
        Require(TryBeginSelectionMove(new(16, 14), VirtualKeyModifiers.None), "Wand click fixture did not start.");
        EndSelectionMove(click: true); await WaitForSelectionReadyAsync();
        Require(scene.ActiveSelection?.CoverageAt(0, 0) == 255 && scene.ActiveSelection.Bounds == new RectI(0, 0, 64, 48), "Stationary wand click did not select afresh.");
        OnUndo(this, new()); await WaitForSelectionReadyAsync(); Require(ReferenceEquals(original, scene.ActiveSelection) && !scene.CanUndo, "Wand click history count.");
        report["selectionOutlineStationaryMarqueeAndWandClicks"] = "passed";

        ToolCombo.SelectedIndex = (int)CanvasTool.EllipseSelect;
        Require(!NudgeSelection(VirtualKey.Right, VirtualKeyModifiers.Control) && !NudgeSelection(VirtualKey.Right, VirtualKeyModifiers.Menu)
            && !NudgeSelection(VirtualKey.Right, VirtualKeyModifiers.None, textInput: true), "Arrow guards intercepted text or pixel shortcuts.");
        for (int i = 0; i < 6; i++) Require(NudgeSelection(VirtualKey.Right, VirtualKeyModifiers.None), "Arrow repeat was refused.");
        Require(NudgeSelection(VirtualKey.Up, VirtualKeyModifiers.Shift), "Shift arrow was refused.");
        await WaitForSelectionReadyAsync(); Require(scene.ActiveSelection?.OutlineOffset == new PointD(6, -10), "Arrow repeat order or Shift step.");
        for (int i = 0; i < 7; i++) OnUndo(this, new()); await WaitForSelectionReadyAsync();
        Require(ReferenceEquals(original, scene.ActiveSelection) && !scene.CanUndo, "Arrow commands did not retain individual Undo steps.");
        report["selectionOutlineArrowRepeatModifiersAndHistory"] = "passed";

        Require(TryBeginSelectionMove(new(16, 14), VirtualKeyModifiers.None), "Interrupted move fixture did not start.");
        MoveSelectionTo(new(90, 14), VirtualKeyModifiers.Control); EndSelectionMove();
        Require(TryBeginSelectionMove(new(90, 14), VirtualKeyModifiers.None), "Off-canvas outline could not be grabbed again.");
        MoveSelectionTo(new(16, 14), VirtualKeyModifiers.Control); EndSelectionMove();
        Require(scene.ActiveSelection?.OutlineOffset == new PointD(0, 0) && scene.ActiveSelection.Coverage.Pixels.AsSpan().SequenceEqual(original.Coverage.Pixels), "Re-grabbing outside could not restore complete coverage.");
        Require(TryBeginSelectionMove(new(16, 14), VirtualKeyModifiers.None), "Replacement fixture did not start.");
        MoveSelectionTo(new(22, 14), VirtualKeyModifiers.Control);
        var replacement = EditorDocument.Create(64, 48); OpenDocument(replacement); EndSelectionMove();
        Require(selectionMove is null && replacement.ActiveSelection is null && !replacement.CanUndo, "Old movement leaked into a replacement document.");
        report["selectionOutlineOutsideRegrabAndOwnerGuards"] = "passed";

        scene = EditorDocument.Create(64, 48); OpenDocument(scene); await WaitForFramesAsync();
        ToolCombo.SelectedIndex = (int)CanvasTool.RectSelect; original = Selection.Rectangle(64, 48, 4, 4, 16, 12);
        scene.SetSelection(original); await WaitForSelectionReadyAsync();
        report["outlineFocusForegroundPrecondition"] = await RequireForegroundForFocusProbeAsync();
        Require(CanvasStage.Focus(Microsoft.UI.Xaml.FocusState.Programmatic), "Focus fixture could not focus the canvas.");
        Require(TryBeginSelectionMove(new(10, 10), VirtualKeyModifiers.None), "Focus fixture did not start.");
        MoveSelectionTo(new(30, 10), VirtualKeyModifiers.Control);
        RootGrid.UpdateLayout();
        var input = FindTextInput(OpacityBox);
        // The commit is synchronous (RootGrid.GotFocus → EndSelectionMove → SetSelectionUndoable); what is
        // not synchronous is WinUI delivering the focus change. A fixed 30 ms wait made that delivery a
        // timing assumption and went red 2 of 5 runs as soon as another group queued refresh work ahead of
        // it, so wait for the state instead of waiting for the clock and keep the same end claim.
        // The bounded wait itself timed out once inside a full suite (2026-10-17, mid workspace probe), and a
        // clock cannot tell that apart from the routed event never arriving at all. The observer says which:
        // zero events with focus already moved is #33's shape, one late event is a budget question. The count
        // is recorded on the green path too, so the rate is measurable instead of remembered.
        var routedFocus = new List<string>();
        void ObserveOutlineFocus(object sender, RoutedEventArgs args)
        {
            var onTheField = ReferenceEquals(args.OriginalSource, input) ? "=theField" : "";
            routedFocus.Add($"{args.OriginalSource?.GetType().Name}{onTheField}");
        }
        RootGrid.GotFocus += ObserveOutlineFocus;
        var focusWait = Stopwatch.StartNew();
        try
        {
            Require(input?.Focus(Microsoft.UI.Xaml.FocusState.Programmatic) == true, "Visible opacity text field could not focus.");
            var settled = await WaitUntilAsync(() => selectionMove is null && scene.CanUndo
                && scene.ActiveSelection?.OutlineOffset == new PointD(20, 0), 2000);
            var observed = string.Join(", ", routedFocus);
            Require(settled, $"Text focus did not finish outline movement: routed GotFocus count={routedFocus.Count} "
                + $"[{observed}], moveStillOpen={selectionMove is not null}, canUndo={scene.CanUndo}, "
                + $"outline={scene.ActiveSelection?.OutlineOffset}, "
                + $"focusNow={FocusManager.GetFocusedElement(RootGrid.XamlRoot)?.GetType().Name}, "
                + $"activations={windowActivationCount}/{lastActivationState}");
        }
        finally { RootGrid.GotFocus -= ObserveOutlineFocus; }
        // How long the delivery actually took, on the runs that pass. Without this the one timeout reads as
        // "slow", which a two second budget should never need; with it, a green distribution of tens of
        // milliseconds is what turns "it timed out" into evidence that the event did not arrive at all.
        report["outlineFocusRoutedEvents"] = $"{routedFocus.Count} [{string.Join(", ", routedFocus)}] "
            + $"activations={windowActivationCount}/{lastActivationState} "
            + $"waitMs={focusWait.Elapsed.TotalMilliseconds:0.#}";
        EndSelectionMove(); OnUndo(this, new()); await WaitForSelectionReadyAsync();
        Require(ReferenceEquals(original, scene.ActiveSelection) && !scene.CanUndo, "Focus loss ended movement more than once.");
        CanvasStage.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
        Require(TryBeginSelectionMove(new(10, 10), VirtualKeyModifiers.None), "Capture-loss fixture did not start.");
        MoveSelectionTo(new(30, 10), VirtualKeyModifiers.Control); OnHealingPointerCanceled(this, null!); OnHealingPointerCanceled(this, null!);
        moved = scene.ActiveSelection;
        OnUndo(this, new()); await WaitForSelectionReadyAsync(); Require(ReferenceEquals(original, scene.ActiveSelection) && !scene.CanUndo, "Capture loss did not commit one Undo.");
        OnRedo(this, new()); await WaitForSelectionReadyAsync(); Require(ReferenceEquals(moved, scene.ActiveSelection), "Capture loss Redo.");
        report["selectionOutlineTextFocusAndCaptureLossHistory"] = "passed";

        var paintId = scene.Snapshot.Manifest.ActiveLayerId!.Value; var unpainted = scene.Snapshot.Images[paintId];
        ToolCombo.SelectedIndex = (int)CanvasTool.Brush; BrushSizeSlider.Value = 12; BrushHardnessSlider.Value = 100;
        foregroundColor = Microsoft.UI.Colors.Red;
        BeginPointerStroke(new(32, 10)); FinishPointerStroke(); await WaitForFramesAsync(); AssertUploadedFrame(scene);
        var painted = scene.Snapshot.Images[paintId]; int interior = 10 * painted.Stride + 32 * 4;
        Require(painted.Pixels[interior + 1] < unpainted.Pixels[interior + 1], "Translated coverage did not admit the red stroke.");
        for (int y = 0; y < 48; y++) for (int x = 0; x < 64; x++)
            if (x < 24 || x >= 40 || y < 4 || y >= 16)
                Require(painted.Pixels.AsSpan(y * painted.Stride + x * 4, 4).SequenceEqual(unpainted.Pixels.AsSpan(y * unpainted.Stride + x * 4, 4)), "Translated selection painted outside the moved outline.");
        OnUndo(this, new()); await WaitForFramesAsync(); AssertUploadedFrame(scene);
        Require(ReferenceEquals(unpainted, scene.Snapshot.Images[paintId]) && ReferenceEquals(moved, scene.ActiveSelection), "Paint Undo lost translated coverage.");
        OnUndo(this, new()); await WaitForSelectionReadyAsync(); Require(ReferenceEquals(original, scene.ActiveSelection) && !scene.CanUndo, "Movement/paint Undo order.");
        OnRedo(this, new()); await WaitForSelectionReadyAsync(); OnRedo(this, new()); await WaitForFramesAsync(); AssertUploadedFrame(scene);
        Require(ReferenceEquals(painted, scene.Snapshot.Images[paintId]) && ReferenceEquals(moved, scene.ActiveSelection), "Movement/paint Redo order.");
        report["selectionOutlineTranslatedPaintGpuAndMixedHistory"] = "passed";

        scene = EditorDocument.Create(4096, 4096); OpenDocument(scene); await WaitForFramesAsync();
        ToolCombo.SelectedIndex = (int)CanvasTool.RectSelect;
        original = Selection.Rectangle(4096, 4096, 200, 200, 3600, 3600); scene.SetSelection(original); await WaitForSelectionReadyAsync();
        geometry = OutlineDisplayIdentity; warm = compositor; frames = presentedFrameCount;
        Require(TryBeginSelectionMove(new(300, 300), VirtualKeyModifiers.None), "4K movement did not start.");
        MoveSelectionTo(new(301, 302), VirtualKeyModifiers.Control); // Warm synchronous input/JIT separately.
        long allocated = GC.GetAllocatedBytesForCurrentThread(); var timer = Stopwatch.StartNew();
        for (int i = 0; i < 240; i++) MoveSelectionTo(new(300 + i * 3, 300 - i * 2), VirtualKeyModifiers.Control);
        double dispatchMs = timer.Elapsed.TotalMilliseconds; long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Require(ReferenceEquals(geometry, OutlineDisplayIdentity) && ReferenceEquals(original.OutlineCoverage, scene.ActiveSelection!.OutlineCoverage), "4K drag rebuilt contour geometry or copied its mask.");
        Require(presentedFrameCount == frames && ReferenceEquals(warm, compositor), "4K drag composited the document.");
        EndSelectionMove();
        report["selectionOutline4K240Moves"] = new { status = "passed", dispatchMs, totalDispatchAllocatedKiB = bytes / 1024.0, compositeFrames = presentedFrameCount - frames };

        // A fixed pointer follows the document as the viewport pans, at the source speed.
        var pixel = new PointD(1500, 1500); Require(TryBeginSelectionMove(pixel, VirtualKeyModifiers.None), "Autoscroll movement did not start.");
        var pointer = new PointD(viewport.Width + 8, viewport.Height / 2); var before = viewport.PanX;
        TrackSelectionAutoscroll(pointer, VirtualKeyModifiers.Control); selectionAutoscroll!.Stop(); StepSelectionAutoscroll();
        Require(Math.Abs(viewport.PanX - before + 10) < .0001, "Autoscroll speed differs from the source 12-point margin.");
        var underPointer = viewport.ToDocument(pointer);
        Require(scene.ActiveSelection?.OutlineOffset == new PointD(717 + Math.Round(underPointer.X - pixel.X, MidpointRounding.AwayFromZero),
            -478 + Math.Round(underPointer.Y - pixel.Y, MidpointRounding.AwayFromZero)), "Autoscroll did not follow a fixed view pointer.");
        EndSelectionMove(); Require(selectionAutoscrollPoint is null && !selectionAutoscroll.IsRunning, "Autoscroll survived release.");
        BeginMarqueeSelection(new(100, 100), VirtualKeyModifiers.None); before = viewport.PanX;
        TrackSelectionAutoscroll(pointer, VirtualKeyModifiers.Control); selectionAutoscroll.Stop(); StepSelectionAutoscroll();
        Require(Math.Abs(viewport.PanX - before + 10) < .0001 && DragPreview.Visibility == Microsoft.UI.Xaml.Visibility.Visible, "Marquee draft did not autoscroll.");
        dragStart = null; StopSelectionAutoscroll(); CancelDragPreview();
        Require(ReferenceEquals(geometry, OutlineDisplayIdentity) && ReferenceEquals(warm, compositor) && presentedFrameCount == frames, "Selection autoscroll rebuilt or composited content.");
        FlushViewport(); RootGrid.UpdateLayout(); await SaveWorkspaceCaptureAsync("selection-move-workspace.png");
        report["selectionOutlineAndMarqueeAutoscroll"] = "passed";

        // Movement while extraction is pending should retain that job and publish its
        // geometry at the latest offset, rather than starting 240 obsolete 4K jobs.
        original = Selection.Rectangle(4096, 4096, 201, 203, 3590, 3580); scene.SetSelection(original);
        Require(!OutlineIsReady, "Pending outline fixture was already synchronous.");
        var extracting = outlineCancellation;
        Require(TryBeginSelectionMove(new(300, 300), VirtualKeyModifiers.None), "Pending outline movement did not start.");
        MoveSelectionTo(new(337, 349), VirtualKeyModifiers.Control); MoveSelectionTo(new(341, 353), VirtualKeyModifiers.Control);
        Require(ReferenceEquals(extracting, outlineCancellation), "Movement cancelled the shared outline extraction.");
        await WaitForSelectionReadyAsync();
        Require(OutlineDisplayOffset == new PointD(41, 53) && OutlineIsReady, "Pending geometry published an old offset or applied it twice.");
        RequireSourceOutlineMinimumX(201);
        EndSelectionMove(); report["selectionOutlineMovesReusePendingExtraction"] = "passed";

        static Microsoft.UI.Xaml.Controls.TextBox? FindTextInput(Microsoft.UI.Xaml.DependencyObject root)
        {
            if (root is Microsoft.UI.Xaml.Controls.TextBox field) return field;
            for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i++)
                if (FindTextInput(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i)) is { } found) return found;
            return null;
        }
    }
}
#endif
