#if DEBUG
using System.Text.Json;
using System.Diagnostics;
using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Compositor.App;

public sealed partial class MainWindow
{
    internal async Task RunWorkspaceProbeAsync()
    {
        var report = new Dictionary<string, object>();
        try
        {
            await Task.Delay(500);
            if (Environment.GetEnvironmentVariable("COMPOSITOR_PROBE_FOREGROUND") == "1")
                report["startupForegroundPrecondition"] = await RequireForegroundForFocusProbeAsync(60000);
            RootGrid.UpdateLayout();
            if (Environment.GetEnvironmentVariable("COMPOSITOR_HISTORY_ONLY") == "1")
            {
                report["scope"] = "history-panel";
                await VerifyHistoryPanelAsync(report);
                report["status"] = "passed";
                return;
            }
            if (Environment.GetEnvironmentVariable("COMPOSITOR_LAYER_PROTECTION_ONLY") == "1")
            {
                report["scope"] = "layer-protection";
                await VerifyLayerProtectionAsync(report);
                report["status"] = "passed";
                return;
            }
            if (Environment.GetEnvironmentVariable("COMPOSITOR_QUICK_TOOLS_ONLY") == "1")
            {
                report["scope"] = "quick-tools";
                await VerifyQuickToolsAsync(report);
                report["status"] = "passed";
                return;
            }
            if (Environment.GetEnvironmentVariable("COMPOSITOR_SELECTION_MODE_ONLY") == "1")
            {
                report["scope"] = "selection-mode-buttons";
                await VerifySelectionModeButtonsAsync(report);
                report["status"] = "passed";
                return;
            }
            if (Environment.GetEnvironmentVariable("COMPOSITOR_SOURCE_OPEN_ONLY") == "1")
            {
                report["scope"] = "source-open";
                await VerifySourceOpenAsync(report);
                report["status"] = "passed";
                return;
            }
            if (Environment.GetEnvironmentVariable("COMPOSITOR_TOOLBAR_ONLY") == "1")
            {
                report["scope"] = "toolbar";
                await VerifyToolbarAsync(report);
                report["status"] = "passed";
                return;
            }
            if (Environment.GetEnvironmentVariable("COMPOSITOR_MASK_STROKE_GRID_ONLY") == "1")
            {
                report["scope"] = "mask-stroke-grid";
                await VerifyMaskStrokeGridAsync(report);
                report["status"] = "passed";
                return;
            }
            if (Environment.GetEnvironmentVariable("COMPOSITOR_PSD_REPORT_ONLY") == "1")
            {
                report["scope"] = "psd-conversion-sheet";
                await VerifyPsdConversionSheetAsync(report);
                report["status"] = "passed";
                return;
            }
            // The file/PSD import groups normally ride --transform-probe, where this machine's focus
            // precondition stops them; none of them needs focus, so this scope runs exactly those two
            // functions — including the budget and blend groups that now stub the conversion sheet.
            if (Environment.GetEnvironmentVariable("COMPOSITOR_PSD_IMPORT_ONLY") == "1")
            {
                report["scope"] = "psd-file-import";
                await VerifyFileImportsAsync(report);
                await VerifyPsdBlendImportsAsync(report);
                report["status"] = "passed";
                return;
            }
            // Isolates the fill/clear command group: the full run fails Clear's selected-row gate
            // around group 40, and this answers whether the long sequence or the group itself is
            // at fault.
            if (Environment.GetEnvironmentVariable("COMPOSITOR_FILL_CLEAR_ONLY") == "1")
            {
                report["scope"] = "fill-clear-selection";
                await VerifyFillAndClearCommandsAsync(report);
                report["status"] = "passed";
                return;
            }
            if (Environment.GetEnvironmentVariable("COMPOSITOR_ADD_NOISE_ONLY") == "1")
            {
                report["scope"] = "add-noise-filter";
                await VerifyAddNoiseFilterAsync(report);
                report["status"] = "passed";
                return;
            }
            if (Environment.GetEnvironmentVariable("COMPOSITOR_GAUSSIAN_FILTER_ONLY") == "1")
            {
                report["scope"] = "gaussian-blur-filter";
                await VerifyGaussianBlurFilterAsync(report);
                report["status"] = "passed";
                return;
            }
            if (Environment.GetEnvironmentVariable("COMPOSITOR_MOTION_FILTER_ONLY") == "1")
            {
                report["scope"] = "motion-blur-filter";
                await VerifyMotionBlurFilterAsync(report);
                report["status"] = "passed";
                return;
            }
            if (Environment.GetEnvironmentVariable("COMPOSITOR_BLOOM_FILTER_ONLY") == "1")
            {
                report["scope"] = "bloom-filter";
                await VerifyBloomFilterAsync(report);
                report["status"] = "passed";
                return;
            }
            if (Environment.GetEnvironmentVariable("COMPOSITOR_FILTER_FINISH_ONLY") == "1")
            {
                report["scope"] = "filter-finish-retirement";
                await VerifyFilterFinishFeedbackAsync(report);
                report["status"] = "passed";
                return;
            }
            if (Environment.GetEnvironmentVariable("COMPOSITOR_TAB_SWITCH_ONLY") == "1")
            {
                report["scope"] = "project-tab-switch";
                await VerifyProjectTabSwitchingAsync(report);
                report["status"] = "passed";
                return;
            }
            if (Environment.GetEnvironmentVariable("COMPOSITOR_BRUSH_PATH_ONLY") == "1")
            {
                report["scope"] = "brush-path";
                await VerifyBrushPathAsync(report);
                report["status"] = "passed";
                return;
            }
            if (Environment.GetEnvironmentVariable("COMPOSITOR_IMAGE_BLUR_ONLY") == "1")
            {
                report["scope"] = "image-blur";
                await VerifyImageBlurAsync(report);
                report["status"] = "passed";
                return;
            }
            if (Environment.GetEnvironmentVariable("COMPOSITOR_CLONE_COVERAGE_ONLY") == "1")
            {
                report["scope"] = "clone-coverage";
                await VerifyCloneCoverageAsync(report);
                report["status"] = "passed";
                return;
            }
            if (Environment.GetEnvironmentVariable("COMPOSITOR_MASK_BLUR_ONLY") == "1")
            {
                report["scope"] = "mask-blur-workflow";
                await VerifyMaskBlurAsync(report);
                report["status"] = "passed";
                return;
            }
            if (Environment.GetEnvironmentVariable("COMPOSITOR_PAINT_GUARDS_ONLY") == "1")
            {
                report["scope"] = "paint-target-guards";
                await VerifyPaintTargetGuardsAsync(report);
                report["status"] = "passed";
                return;
            }
            if (Environment.GetEnvironmentVariable("COMPOSITOR_OPACITY_KEYS_ONLY") == "1")
            {
                report["scope"] = "opacity-digit-workflow";
                await VerifyOpacityKeysAsync(report);
                report["status"] = "passed";
                return;
            }
            if (Environment.GetEnvironmentVariable("COMPOSITOR_BLEND_PREVIEW_ONLY") == "1")
            {
                report["scope"] = "blend-mode-preview";
                await VerifyBlendPreviewAsync(report);
                report["status"] = "passed";
                return;
            }
            if (Environment.GetEnvironmentVariable("COMPOSITOR_OPACITY_ONLY") == "1")
            {
                report["scope"] = "layer-opacity-workflow";
                await VerifyOpacityWorkflowAsync(report);
                report["status"] = "passed";
                return;
            }
            if (Environment.GetEnvironmentVariable("COMPOSITOR_BLANK_LAYER_ONLY") == "1")
            {
                report["scope"] = "blank-layer-workflow";
                await VerifyBlankLayerWorkflowAsync(report);
                await VerifyMaskWorkflowAsync(report);
                report["status"] = "passed";
                return;
            }
            if (Environment.GetEnvironmentVariable("COMPOSITOR_CONTOUR_SCAN_ONLY") == "1")
            {
                report["scope"] = "selection-contour-scan";
                await VerifySelectionOutlineBatchesAsync(report);
                await VerifyOutlineMetricsOwnershipAsync(report);
                report["status"] = "passed";
                return;
            }
            if (Environment.GetEnvironmentVariable("COMPOSITOR_MASK_CREATE_WORKER_ONLY") == "1")
            {
                report["scope"] = "selection-mask-creation-worker";
                await VerifySelectionMaskWorkerAsync(report);
                report["status"] = "passed";
                return;
            }
            if (Environment.GetEnvironmentVariable("COMPOSITOR_MASK_CREATE_PERF_ONLY") == "1")
            {
                report["scope"] = "selection-mask-creation-performance";
                await MeasureSelectionMaskCreationAsync(report);
                report["status"] = "passed";
                return;
            }
            if (Environment.GetEnvironmentVariable("COMPOSITOR_MASK_WORKFLOW_ONLY") == "1")
            {
                report["scope"] = "mask-workflow";
                await VerifyMaskWorkflowAsync(report);
                report["status"] = "passed";
                return;
            }
            if (Environment.GetEnvironmentVariable("COMPOSITOR_LAYER_CONTEXT_ONLY") == "1")
            {
                report["scope"] = "layer-context-menu";
                await VerifyLayerContextAsync(report);
                report["status"] = "passed";
                return;
            }
            if (Environment.GetEnvironmentVariable("COMPOSITOR_DRAG_LIFETIME_ONLY") == "1")
            {
                report["scope"] = "layer-panel-drag-lifetime";
                await VerifyLayerDragLifetimeAsync(report);
                report["status"] = "passed";
                return;
            }
            if (Environment.GetEnvironmentVariable("COMPOSITOR_THUMBNAILS_ONLY") == "1")
            {
                report["scope"] = "canvas-layer-thumbnails";
                await VerifyLayerThumbnailsAsync(report);
                report["status"] = "passed";
                return;
            }
            if (Environment.GetEnvironmentVariable("COMPOSITOR_EFFECT_ROWS_ONLY") == "1")
            {
                report["scope"] = "layer-effect-rows";
                await VerifyLayerEffectRowsAsync(report);
                report["status"] = "passed";
                return;
            }
            if (Environment.GetEnvironmentVariable("COMPOSITOR_CLEAR_WORKER_ONLY") == "1")
            {
                report["scope"] = "selection-clear-worker";
                await VerifySelectionClearWorkerAsync(report);
                report["status"] = "passed";
                return;
            }
            if (Environment.GetEnvironmentVariable("COMPOSITOR_CLEAR_PERF_ONLY") == "1")
            {
                report["scope"] = "selection-clear-performance";
                await MeasureSelectionClearAsync(report);
                report["status"] = "passed";
                return;
            }
            if (Environment.GetEnvironmentVariable("COMPOSITOR_DELETE_KEY_ONLY") == "1")
            {
                report["scope"] = "delete-key";
                await VerifyDeleteKeyAsync(report);
                report["status"] = "passed";
                return;
            }
            if (Environment.GetEnvironmentVariable("COMPOSITOR_LAYER_DELETION_ONLY") == "1")
            {
                report["scope"] = "layer-deletion";
                await VerifyLayerDeletionAsync(report);
                report["status"] = "passed";
                return;
            }
            if (Environment.GetEnvironmentVariable("COMPOSITOR_LAYER_SELECTION_ONLY") == "1")
            {
                report["scope"] = "layer-list-selection";
                await VerifyLayerSelectionAsync(report);
                report["status"] = "passed";
                return;
            }
            if (Environment.GetEnvironmentVariable("COMPOSITOR_VISIBILITY_SWIPE_ONLY") == "1")
            {
                report["scope"] = "visibility-swipe";
                await VerifyVisibilitySwipeAsync(report);
                report["status"] = "passed";
                return;
            }
            if (Environment.GetEnvironmentVariable("COMPOSITOR_MASK_SCROLL_ONLY") == "1")
            {
                report["scope"] = "mask-copy-autoscroll";
                await VerifyMaskCopyAutoscrollAsync(report);
                report["status"] = "passed";
                return;
            }
            if (RootGrid.FindName("ToolRailScroll") is not ScrollViewer rail || rail.ActualWidth is not (56 or 88))
                throw new InvalidOperationException("The single/double column tool rail is missing.");
            report["verticalToolRail"] = "passed";
            AppWindow.Resize(new SizeInt32(1180, 820));
            await Task.Delay(150); RootGrid.UpdateLayout();
            double canvasHeight = CanvasScroll.ActualHeight;
            foreach (var item in Enum.GetValues<CanvasTool>())
            {
                OnToolRailClick(toolButtons[item], new RoutedEventArgs()); RootGrid.UpdateLayout();
                if (tool != item || toolButtons.Count(pair => pair.Value.IsChecked == true) != 1
                    || ActiveToolLabel.Text != ToolLabel(item)) throw new InvalidOperationException($"Tool rail did not select {item}.");
                if (Math.Abs(CanvasScroll.ActualHeight - canvasHeight) > 1)
                    throw new InvalidOperationException($"{item} changed the canvas height.");
                if (item is CanvasTool.Brush or CanvasTool.Eraser or CanvasTool.Clone or CanvasTool.Blur or CanvasTool.Healing
                    && BrushControls.Visibility != Visibility.Visible) throw new InvalidOperationException($"Brush parameters missing for {item}.");
            }
            OnToolRailClick(toolButtons[tool], new RoutedEventArgs());
            if (toolButtons.Count(pair => pair.Value.IsChecked == true) != 1) throw new InvalidOperationException("Clicking the active tool cleared selection.");
            report["allToolsAndStableCanvasHeight"] = new { status = "passed", count = toolButtons.Count };
            ToolRailScroll.ChangeView(null, ToolRailScroll.ScrollableHeight, null, true);
            await Task.Delay(50);
            OnPaletteChipClick(BackgroundColorChip, new RoutedEventArgs());
            if (BrushColorPicker.Color != backgroundColor) throw new InvalidOperationException("Background picker opened with the foreground color.");
            Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase.GetAttachedFlyout(PaletteGrid).Hide();
            OnSwapPaletteColors(this, new RoutedEventArgs());
            if (foregroundColor != Microsoft.UI.Colors.White) throw new InvalidOperationException("Palette swap failed.");
            OnResetPaletteColors(this, new RoutedEventArgs());
            ToolRailScroll.ChangeView(null, 0, null, true); report["palettePickerSwapAndReset"] = "passed";

            var scene = EditorDocument.Create(800, 560);
            scene.ImportImage(new Surface(1, 1, [55, 140, 180, 255], 4), "Image");
            scene.AddAdjustment(AdjustmentKind.Levels);
            OpenDocument(scene);
            var originalRows = layerRows.Values.ToArray();
            var thumbRow = (LayerRow)layerRows[scene.Snapshot.Manifest.Layers.Single(layer => layer.Name == "Image").Id].Tag;
            var thumbnail = thumbRow.Thumbnail.Source;
            RefreshLayerThumbnail(thumbRow);
            if (thumbnail is null || !ReferenceEquals(thumbnail, thumbRow.Thumbnail.Source))
                throw new InvalidOperationException("Unchanged thumbnails were rebuilt.");
            report["thumbnailCache"] = "passed";
            OpacityBox.Value = 65;
            if (Math.Abs((SelectedLayer?.Opacity ?? 1) - .65) > .001) throw new InvalidOperationException("Opacity field is not bound.");
            scene.Undo();
            if ((SelectedLayer?.Opacity ?? 1) != 1 || originalRows.Any(row => !layerRows.Values.Contains(row)))
                throw new InvalidOperationException("Undo lost opacity or stable layer rows.");
            if (FooterAdjustmentMenu.Items.Count != Enum.GetValues<AdjustmentKind>().Length)
                throw new InvalidOperationException("Adjustment footer lost a command.");
            report["layerPropertiesUndoAndStableRows"] = "passed";

            foreach (int width in new[] { 202, 252, 352 })
            {
                SetLayersPanelWidth(width); RootGrid.UpdateLayout();
                EnsureInside(OpacityBox, LayersPane); EnsureInside(BlendCombo, LayersPane);
                EnsureInside(LayerCommands, LayersPane);
            }
            SetLayersPanelWidth(100); if (LayersColumn.Width.Value != 202) throw new InvalidOperationException("Minimum panel width was not clamped.");
            SetLayersPanelWidth(900); if (LayersColumn.Width.Value != 352) throw new InvalidOperationException("Maximum panel width was not clamped.");
            report["panelWidths202Through352"] = "passed";

            await WaitForFramesAsync();
            int framesBefore = presentedFrameCount;
            var warm = compositor;
            var selectionTimer = Stopwatch.StartNew();
            scene.SetSelection(Selection.Rectangle(800, 560, 10, 20, 700, 500));
            double selectionNotificationMs = selectionTimer.Elapsed.TotalMilliseconds;
            await WaitForSelectionOutlineAsync(); await WaitForFramesAsync();
            if (!ReferenceEquals(warm, compositor) || presentedFrameCount != framesBefore)
                throw new InvalidOperationException("Session selection recomposited the canvas.");
            ToolCombo.SelectedIndex = (int)CanvasTool.RectSelect;
            ShowMarqueePreview(new(50, 60), new(100, 160), false);
            if (DragPreview.Width != 50 || DragPreview.Height != 100 || DragPreview.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Rectangle preview did not follow the pointer.");
            ToolCombo.SelectedIndex = (int)CanvasTool.EllipseSelect;
            ShowMarqueePreview(new(50, 60), new(100, 160), true);
            if (ShapePreviewEllipse.Width != 100 || ShapePreviewEllipse.Height != 100 || ShapePreviewEllipse.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Ellipse preview did not constrain its geometry.");
            CancelDragPreview();
            FinishMarqueeSelection(new(50, 60), new(100, 160), true);
            await WaitForSelectionOutlineAsync();
            var expectedMarquee = Selection.Ellipse(800, 560, 100, 110, 50, 50, LassoAntialiasCheck.IsChecked == true);
            if (scene.ActiveSelection is not { } marquee || !marquee.Coverage.Pixels.AsSpan().SequenceEqual(expectedMarquee.Coverage.Pixels))
                throw new InvalidOperationException("Released ellipse differs from its preview.");
            BeginLasso(new(0, 0), Windows.System.VirtualKeyModifiers.None);
            for (int i = 1; i <= 1000; i++) MoveLasso(new(i, i % 2));
            if (LassoPreview.Points.Count != 1 || lassoPoints?.Count != 1001)
                throw new InvalidOperationException("Lasso did not retain raw samples while coalescing its preview.");
            OnLassoPreviewFrame(null, EventArgs.Empty);
            if (LassoPreview.Points.Count != 1001) throw new InvalidOperationException("Lasso frame lost pointer samples.");
            CancelLasso(); report["lassoPreviewFrameCoalescing"] = "passed";
            // Add/Subtract chaining and cancellation must not let delayed worker results
            // overwrite a newer selection or a newly opened document.
            QueueSelectionWork((_, _) => { Thread.Sleep(40); return Selection.Rectangle(800, 560, 0, 0, 20, 20); }, false, "probe");
            QueueSelectionWork((previous, _) => LassoSelection.Combine(previous, Selection.Rectangle(800, 560, 30, 30, 20, 20), LassoSelectionMode.Add), true, "probe");
            await WaitForSelectionOutlineAsync();
            if (scene.ActiveSelection is not { } added || added.CoverageAt(10, 10) != 255 || added.CoverageAt(40, 40) != 255)
                throw new InvalidOperationException("Add lost a pending selection result.");
            var originalLayer = selectedLayerId!.Value;
            QueueSelectionWork((_, _) => { Thread.Sleep(40); return Selection.Rectangle(800, 560, 50, 50, 20, 20); }, false, "probe");
            LayerList.SelectedItem = layerRows[thumbRow.Layer.Id];
            LayerList.SelectedItem = layerRows[originalLayer];
            await WaitForSelectionOutlineAsync();
            if (scene.ActiveSelection?.CoverageAt(60, 60) != 255) throw new InvalidOperationException("Layer selection cancelled a pending document selection.");
            QueueSelectionWork((_, _) => { Thread.Sleep(40); return Selection.Rectangle(800, 560, 0, 0, 20, 20); }, false, "probe");
            scene.SetSelection(null); await Task.Delay(60);
            if (scene.ActiveSelection is not null) throw new InvalidOperationException("Deselect accepted a stale worker result.");
            QueueSelectionWork((_, _) => { Thread.Sleep(40); return Selection.Rectangle(800, 560, 0, 0, 20, 20); }, false, "probe");
            var replacement = EditorDocument.Create(640, 480); OpenDocument(replacement); await Task.Delay(60);
            if (replacement.ActiveSelection is not null) throw new InvalidOperationException("A previous document's selection leaked into the new one.");
            OpenDocument(scene);
            report["selectionOverlayWithoutRecomposition"] = new { status = "passed", notificationMs = selectionNotificationMs };

            ToolCombo.SelectedIndex = (int)CanvasTool.Brush; SetLayersPanelWidth(252);
            await Task.Delay(200); RootGrid.UpdateLayout();
            if (LayerList.ActualHeight < 75) throw new InvalidOperationException("Properties squeezed the layer list out of the workspace.");
            var wideLayout = new { width = RootGrid.ActualWidth, height = RootGrid.ActualHeight, layerListHeight = LayerList.ActualHeight };
            await SaveWorkspaceCaptureAsync("workspace-wide.png");
            AppWindow.Resize(new SizeInt32(800, 600)); await Task.Delay(200); RootGrid.UpdateLayout();

            if (ToolRailScroll.ScrollableHeight <= 0) throw new InvalidOperationException("Short windows cannot scroll to the palette.");
            ToolCombo.SelectedIndex = (int)CanvasTool.Gradient; RootGrid.UpdateLayout();
            if (ToolOptionsScroll.ScrollableWidth <= 0) throw new InvalidOperationException("Narrow windows cannot scroll gradient parameters.");
            EnsureInside(OpacityBox, LayersPane); EnsureInside(BlendCombo, LayersPane); EnsureInside(LayerCommands, LayersPane);
            if (LayerList.ActualHeight < 40) throw new InvalidOperationException("A short window cannot display one complete layer row.");
            await Task.Delay(200); await SaveWorkspaceCaptureAsync("workspace-narrow.png");
            report["narrowWindowScrolling"] = "passed";
            report["layoutSamples"] = new { rasterizationScale = RootGrid.XamlRoot.RasterizationScale, wide = wideLayout,
                narrow = new { width = RootGrid.ActualWidth, height = RootGrid.ActualHeight, layerListHeight = LayerList.ActualHeight } };
            var largeScene = EditorDocument.Create(4096, 4096); OpenDocument(largeScene);
            await WaitForFramesAsync();
            var timings = new List<object>();
            foreach (var largeTool in new[] { CanvasTool.RectSelect, CanvasTool.EllipseSelect, CanvasTool.Lasso })
            {
                ToolCombo.SelectedIndex = (int)largeTool;
                int before = presentedFrameCount; var active = compositor;
                var timer = Stopwatch.StartNew();
                if (largeTool != CanvasTool.Lasso) FinishMarqueeSelection(new(200, 200), new(3800, 3800), false);
                else
                {
                    BeginLasso(new(200, 200), Windows.System.VirtualKeyModifiers.None);
                    MoveLasso(new(3800, 200)); MoveLasso(new(3800, 3800)); MoveLasso(new(200, 3800)); FinishLasso();
                }
                double dispatchMs = timer.Elapsed.TotalMilliseconds;
                await WaitForSelectionOutlineAsync(); await WaitForFramesAsync();
                if (presentedFrameCount != before || !ReferenceEquals(active, compositor))
                    throw new InvalidOperationException($"{largeTool} recomposited the 4K canvas.");
                timings.Add(new { tool = largeTool.ToString(), dispatchMs, readyMs = timer.Elapsed.TotalMilliseconds,
                    compositeFrames = presentedFrameCount - before, width = largeScene.ActiveSelection?.Bounds.Width });
            }
            report["selection4KSingleSamples"] = timings;
            await VerifySelectionHistoryAsync(report);
            await VerifySelectionAmountPanelAsync(report);
            await VerifyPixelMoveAsync(report);
            await VerifyLayerDropAsync(report);
            await VerifyLayerRenameAsync(report);
            await VerifyAdjustmentEditorsAsync(report);
            await VerifyFilterPreviewRowsStayHiddenAsync(report);
            await VerifyFilterPreviewRetirementAsync(report);
            await VerifyFilterFinishFeedbackAsync(report);
            await VerifyPanelAnswerKeysAsync(report);
            await VerifyCameraRawPreviewAsync(report);
            await VerifyLensCorrectionAsync(report);
            await VerifyVignetteAsync(report);
            await VerifyTonalContrastAsync(report);
            await VerifyTonalContrastRearmAfterTwoTicksAsync(report);
            await VerifyAddNoiseFilterAsync(report);
            await VerifyGaussianBlurFilterAsync(report);
            await VerifyMotionBlurFilterAsync(report);
            await VerifyBloomFilterAsync(report);
            await VerifyEffectsPanelAsync(report);
            // C07b: the macOS shape options pair a slider with a typed field (ShapeControls.swift:20-45).
            // The field is what reaches the layer and it goes past the slider's own cap, so a radius of
            // 345 is askable without dragging anything.
            ToolCombo.SelectedIndex = (int)CanvasTool.Shape; await WaitForFramesAsync();
            ShapeKindCombo.SelectedIndex = 0;
            ShapeCornerBox.Value = 345;
            Require(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(ShapeCornerBox) == Loc.T("A_ShapeCorner")
                    && Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(ShapeLineWidthBox) == Loc.T("A_ShapeLineWidth"),
                "a shape field carries no accessible name, so the value is unattributable by keyboard.");
            Require(ShapeCornerSlider.Value == 200, $"the radius slider did not mirror the typed value (slider {ShapeCornerSlider.Value}).");
            // Drive a real drag and read the style back off the layer the app created: the field is what
            // CommitShape uses now, and a field nobody's drawing reads is decoration.
            var shapeDocument = document ?? throw new InvalidOperationException("the shape field probe ran without a document.");
            var layersBeforeShape = shapeDocument.Snapshot.Manifest.Layers.Count;
            // The release path's own two calls, not a faked pointer event: BeginPointerStroke is the paint
            // stroke and does nothing for the Shape tool, which is how this assertion first failed.
            (shapePreviewBox, shapePreviewFrom, shapePreviewTo) = ShapeCommit(new(20, 20), new(120, 90), false);
            CommitShape(); await WaitForFramesAsync();
            Require(shapeDocument.Snapshot.Manifest.Layers.Count == layersBeforeShape + 1, "the shape drag created no layer.");
            var typedStyle = ShapeStyle.FromElement(shapeDocument.Snapshot.Manifest.Layers[^1].Shape);
            Require(typedStyle is { Kind: ShapeKind.Rectangle, CornerRadius: 345 },
                $"the typed radius did not reach the new layer ({typedStyle?.CornerRadius}).");
            OnUndo(this, new RoutedEventArgs()); await WaitForFramesAsync();
            Require(shapeDocument.Snapshot.Manifest.Layers.Count == layersBeforeShape, "Undo left the typed shape layer behind.");
            StepShapeNumber(ShapeCornerBox, up: true, shift: true);
            Require(ShapeCornerBox.Value == 355, $"Shift+Up did not step the field by ten (value {ShapeCornerBox.Value}).");
            StepShapeNumber(ShapeCornerBox, up: false, shift: false);
            Require(ShapeCornerBox.Value == 354, $"Down did not step by one (value {ShapeCornerBox.Value}).");
            ShapeCornerBox.Value = 99_999;
            Require(ShapeCornerBox.Value == 5000, $"the field did not clamp to the macOS ceiling (value {ShapeCornerBox.Value}).");
            ShapeLineWidthBox.Value = 250;
            Require(ShapeLineWidthSlider.Value == 100, "the line-width slider did not mirror the typed value.");
            // C05d: Shift+U steps Rectangle → Ellipse → Line, the Mac's ⇧U (EditorCanvas.swift:2223-2224).
            // The combo is the single source, so stepping it is what the key handler must do.
            var kindBeforeCycle = ShapeKindCombo.SelectedIndex;
            StepShapeKind();
            Require(ShapeKindCombo.SelectedIndex == (kindBeforeCycle + 1) % 3 && shapeKind == (ShapeKind)((kindBeforeCycle + 1) % 3),
                $"Shift+U did not step the shape kind (combo {ShapeKindCombo.SelectedIndex}, kind {shapeKind}).");
            StepShapeKind(); StepShapeKind();
            Require(ShapeKindCombo.SelectedIndex == kindBeforeCycle && shapeKind == (ShapeKind)kindBeforeCycle,
                "the shape kind cycle did not wrap back to where it started.");
            ToolCombo.SelectedIndex = (int)CanvasTool.Brush;
            report["shapeOptionsTypedNumbers"] = "passed";

            // The selection-move fixture ends in keyboard-focus assertions that need the window to own
            // the foreground; it runs last so everything before it gets measured even when nobody is
            // at the keyboard.
            await VerifySelectionMoveAsync(report);

            report["status"] = "passed";

            static void EnsureInside(FrameworkElement child, FrameworkElement parent)
            {
                var bounds = child.TransformToVisual(parent).TransformBounds(new Windows.Foundation.Rect(0, 0, child.ActualWidth, child.ActualHeight));
                if (bounds.Left < -1 || bounds.Right > parent.ActualWidth + 1)
                    throw new InvalidOperationException($"{child.Name} overflows its panel: {bounds}, width {parent.ActualWidth}.");
            }

            async Task WaitForSelectionOutlineAsync()
            {
                var wait = Stopwatch.StartNew();
                while (selectionWorkResult is not null || (document?.ActiveSelection is not null && !OutlineIsReady))
                {
                    if (wait.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Selection did not become ready.");
                    await Task.Delay(1);
                }
            }
        }
        catch (Exception ex) { report["status"] = "failed"; report["error"] = ex.ToString(); }
        finally
        {
            WriteProbeReport("workspace-probe.json", report);
            forceClose = true; Close();
        }
    }

    private async Task SaveWorkspaceCaptureAsync(string name, UIElement? target = null)
    {
        // Capture workspace layout; GPU pixels are checked separately through drawing/readback.
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(target ?? RootGrid);
        var pixels = (await bitmap.GetPixelsAsync()).ToArray();
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
        await encoder.FlushAsync();
        using var output = File.Create(Path.Combine(AppContext.BaseDirectory, name));
        stream.Seek(0); await stream.AsStreamForRead().CopyToAsync(output);
    }
}
#endif
