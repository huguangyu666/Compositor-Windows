#if DEBUG
using System.Diagnostics;
using Compositor.Core;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Windows.System;

namespace Compositor.App;

public sealed partial class MainWindow
{
    /// <summary>#37's third panel and the first time the amount prompt was ever driven: Select ▸ Expand,
    /// Contract and Feather all call it, every selection probe so far went straight to
    /// <c>ApplySelectionModification</c>, and nothing checked where the number comes from, what a cleared field
    /// answers, or what happens when the document goes away while the panel is waiting for an answer.</summary>
    private async Task VerifySelectionAmountPanelAsync(Dictionary<string, object> report)
    {
        var scene = EditorDocument.Create(64, 48);
        OpenDocument(scene); await WaitForFramesAsync();
        scene.SetSelection(Selection.Rectangle(64, 48, 20, 15, 20, 15));
        await WaitForSelectionReadyAsync();

        NumberBox AmountBox() => (NumberBox)((StackPanel)((StackPanel)FloatingPanelBody("selectionAmount")!).Children[1]).Children[1];
        Slider AmountSlider() => (Slider)((StackPanel)((StackPanel)FloatingPanelBody("selectionAmount")!).Children[1]).Children[0];

        var bounds = scene.ActiveSelection!.Bounds;
        var pending = PromptSelectionModificationAsync(SelectionModification.Expand, 500);
        Require(await WaitUntilAsync(() => FloatingPanelIsOpen("selectionAmount"), 2000), "Select ▸ Expand opened no amount panel.");
        Require(Equals(FloatingPanelButton("selectionAmount", false)?.Content, Loc.T("Dlg_SheetCancel"))
                && Equals(FloatingPanelButton("selectionAmount", true)?.Content, Loc.T("Dlg_OK")),
            "The selection amount sheet does not offer Cancel / OK.");
        var box = AmountBox();
        Require(Math.Abs(box.Value - 1) < .001, $"the expand panel did not open on expand's own last amount ({box.Value}).");
        box.Value = 7;
        Require(Math.Abs(AmountSlider().Value - 7) < .001, "the field and the slider are not the same number.");
        CloseFloatingPanel("selectionAmount", commit: true);
        await pending.WaitAsync(TimeSpan.FromSeconds(20));
        await WaitForSelectionReadyAsync();
        var expandedRect = scene.ActiveSelection!.Bounds;
        var expandedSelection = scene.ActiveSelection;
        Require(expandedRect.X == bounds.X - 7 && expandedRect.Width == bounds.Width + 14,
            $"expanding by 7 through the panel did not reach the selection ({expandedRect} from {bounds}).");

        // A cleared field is not a zero: it answers nothing, even when OK is pressed anyway.
        pending = PromptSelectionModificationAsync(SelectionModification.Expand, 500);
        Require(await WaitUntilAsync(() => FloatingPanelIsOpen("selectionAmount"), 2000), "the second expand opened no panel.");
        AmountBox().Value = 9;
        Require(FloatingPanelConfirmEnabled("selectionAmount"), "a whole number inside the range left OK disabled.");
        AmountBox().Value = double.NaN;
        Require(!FloatingPanelConfirmEnabled("selectionAmount"), "OK stayed reachable with the field cleared.");
        CloseFloatingPanel("selectionAmount", commit: true);
        await pending.WaitAsync(TimeSpan.FromSeconds(20));
        await WaitForSelectionReadyAsync();
        // The bounds alone would not catch it: expanding by zero leaves the same rectangle, so what is claimed
        // is that no selection job ran at all — the selection object is still the one the panel left behind.
        Require(ReferenceEquals(scene.ActiveSelection, expandedSelection) && scene.ActiveSelection!.Bounds == expandedRect,
            "a cleared amount field still ran a selection job.");

        // Each operation remembers its own number, as the Mac keeps three fields
        // (EditorSession.swift:274 feather 2, :312-313 expand and contract 1).
        var feathered = PromptSelectionModificationAsync(SelectionModification.Feather, 250);
        Require(await WaitUntilAsync(() => FloatingPanelIsOpen("selectionAmount"), 2000), "feather opened no panel.");
        Require(Math.Abs(AmountBox().Value - 2) < .001, $"feather did not open on feather's own last amount ({AmountBox().Value}).");
        AmountBox().Value = 5;
        CloseFloatingPanel("selectionAmount", commit: true);
        await feathered.WaitAsync(TimeSpan.FromSeconds(20));
        await WaitForSelectionReadyAsync();
        Require(scene.ActiveSelection!.Feather == 5, "the feather amount never reached the selection.");
        var again = PromptSelectionModificationAsync(SelectionModification.Expand, 500);
        Require(await WaitUntilAsync(() => FloatingPanelIsOpen("selectionAmount"), 2000), "the third expand opened no panel.");
        Require(Math.Abs(AmountBox().Value - 7) < .001,
            $"the feather amount leaked into expand's field ({AmountBox().Value} instead of 7).");
        CloseFloatingPanel("selectionAmount", commit: false);
        await again.WaitAsync(TimeSpan.FromSeconds(20));

        // The claim only a non-modal panel can make: the document may go away underneath a prompt that is
        // still waiting for its answer, and then the answer has to come — as nothing. A modal dialog could
        // not be caught in this position, which is why no probe ever tried.
        var abandoned = PromptSelectionModificationAsync(SelectionModification.Contract, 500);
        Require(await WaitUntilAsync(() => FloatingPanelIsOpen("selectionAmount"), 2000), "contract opened no panel.");
        var beforeSwitch = scene.ActiveSelection!.Bounds;
        OpenDocument(EditorDocument.Create(32, 24));
        await WaitForFramesAsync();
        try { await abandoned.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (TimeoutException) { throw new InvalidOperationException("replacing the document left the amount prompt waiting for an answer nobody will give."); }
        Require(!FloatingPanelIsOpen("selectionAmount"), "replacing the document left the amount panel on screen.");
        Require(scene.ActiveSelection!.Bounds == beforeSwitch, "a prompt answered by a document switch still reshaped the old selection.");
        report["selectionAmountPanelAnswersOrStaysSilent"] = "passed";
    }

    private async Task VerifySelectionHistoryAsync(Dictionary<string, object> report)
    {
        var scene = EditorDocument.Create(64, 48); OpenDocument(scene); await WaitForFramesAsync();
        var warm = compositor; int frames = presentedFrameCount;
        ToolCombo.SelectedIndex = (int)CanvasTool.RectSelect;
        FinishMarqueeSelection(new(4, 4), new(20, 16), false); await WaitForSelectionReadyAsync();
        Require(scene.CanUndo, "Released marquee has no selection Undo step.");
        var selected = scene.ActiveSelection;
        OnUndo(this, new()); await WaitForSelectionReadyAsync(); await WaitForFramesAsync();
        Require(scene.ActiveSelection is null && !scene.CanUndo, "Marquee Undo did not restore the previous selection.");
        OnRedo(this, new()); await WaitForSelectionReadyAsync();
        Require(ReferenceEquals(selected, scene.ActiveSelection), "Marquee Redo lost its coverage.");
        report["marqueeSelectionUndoRedo"] = "passed";

        OnSelectAll(this, new()); await WaitForSelectionReadyAsync();
        Require(scene.ActiveSelection?.Bounds == new RectI(0, 0, 64, 48), "Select All failed.");
        OnInvertSelection(this, new()); await WaitForSelectionReadyAsync(); Require(scene.ActiveSelection is null, "Inverse of all did not deselect.");
        OnUndo(this, new()); await WaitForSelectionReadyAsync(); Require(scene.ActiveSelection?.Bounds == new RectI(0, 0, 64, 48), "Inverse Undo lost all.");
        OnUndo(this, new()); await WaitForSelectionReadyAsync(); Require(ReferenceEquals(selected, scene.ActiveSelection), "Select All Undo lost marquee.");
        OnDeselect(this, new()); await WaitForSelectionReadyAsync(); Require(scene.ActiveSelection is null, "Deselect failed.");
        OnUndo(this, new()); await WaitForSelectionReadyAsync(); Require(ReferenceEquals(selected, scene.ActiveSelection), "Deselect Undo lost marquee.");
        report["allInverseDeselectHistory"] = "passed";

        BeginMarqueeSelection(new(24, 4), VirtualKeyModifiers.Shift);
        Require(!MarqueeSquare(VirtualKeyModifiers.Shift), "Shift held at press constrained instead of choosing Add.");
        FinishMarqueeSelection(new(24, 4), new(36, 20), false); await WaitForSelectionReadyAsync();
        Require(scene.ActiveSelection!.CoverageAt(6, 6) == 255 && scene.ActiveSelection.CoverageAt(26, 18) == 255
            && scene.ActiveSelection.CoverageAt(38, 8) == 0, "Marquee Add did not preserve the original rectangular extent.");
        Require(!MarqueeSquare(VirtualKeyModifiers.None) && MarqueeSquare(VirtualKeyModifiers.Shift), "Repressed Shift did not arm square constraint.");
        OnUndo(this, new()); await WaitForSelectionReadyAsync(); Require(ReferenceEquals(selected, scene.ActiveSelection), "Add Undo lost prior selection.");
        BeginMarqueeSelection(new(4, 4), VirtualKeyModifiers.Menu | VirtualKeyModifiers.Shift);
        FinishMarqueeSelection(new(4, 4), new(20, 16), false); await WaitForSelectionReadyAsync();
        Require(scene.ActiveSelection is { IsEmpty: true }, "Alt marquee subtraction did not retain explicit empty coverage.");
        OnUndo(this, new()); await WaitForSelectionReadyAsync(); Require(ReferenceEquals(selected, scene.ActiveSelection), "Subtract Undo lost prior selection.");
        Require(presentedFrameCount == frames && ReferenceEquals(compositor, warm), "Selection history recomposited the document.");
        report["marqueeModifiersAndHistoryWithoutComposition"] = "passed";

        scene = EditorDocument.Create(64, 48); OpenDocument(scene); await WaitForFramesAsync();
        ToolCombo.SelectedIndex = (int)CanvasTool.EllipseSelect; SetSelectionMode(LassoSelectionMode.Replace);
        LassoAntialiasCheck.IsChecked = true; BeginMarqueeSelection(new(4, 4), VirtualKeyModifiers.None);
        FinishMarqueeSelection(new(4, 4), new(20, 16), false); await WaitForSelectionReadyAsync();
        var softEllipse = scene.ActiveSelection!;
        Require(softEllipse.Coverage.Pixels.Any(value => value > 0 && value < 255), "Ellipse ignored the shared antialias option.");
        LassoAntialiasCheck.IsChecked = false; BeginMarqueeSelection(new(4, 4), VirtualKeyModifiers.None);
        FinishMarqueeSelection(new(4, 4), new(20, 16), false); await WaitForSelectionReadyAsync();
        Require(scene.ActiveSelection!.Coverage.Pixels.All(value => value is 0 or 255), "Ellipse disabled antialias still has partial coverage.");
        OnUndo(this, new()); await WaitForSelectionReadyAsync(); Require(ReferenceEquals(softEllipse, scene.ActiveSelection), "Ellipse option Undo lost antialias coverage.");
        LassoAntialiasCheck.IsChecked = true; RootGrid.UpdateLayout(); await SaveWorkspaceCaptureAsync("selection-workspace.png");
        report["ellipseAntialiasOptionAndUndo"] = "passed";

        scene = EditorDocument.Create(64, 48); OpenDocument(scene); await WaitForFramesAsync();
        ToolCombo.SelectedIndex = (int)CanvasTool.RectSelect; BeginMarqueeSelection(new(4, 4), VirtualKeyModifiers.None);
        FinishMarqueeSelection(new(4, 4), new(20, 16), false); await WaitForSelectionReadyAsync();
        selected = scene.ActiveSelection; var layerId = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        var unpainted = scene.Snapshot.Images[layerId];
        ToolCombo.SelectedIndex = (int)CanvasTool.Brush; BrushSizeSlider.Value = 12; BrushHardnessSlider.Value = 100;
        foregroundColor = Microsoft.UI.Colors.Red;
        BeginPointerStroke(new(12, 10)); FinishPointerStroke(); await WaitForFramesAsync(); AssertUploadedFrame(scene);
        var painted = scene.Snapshot.Images[layerId];
        int interior = 10 * painted.Stride + 12 * 4;
        Require(painted.Pixels[interior + 3] > 0 && painted.Pixels[interior + 1] < unpainted.Pixels[interior + 1], "Selected red brush did not change the white interior.");
        for (int y = 0; y < 48; y++) for (int x = 0; x < 64; x++)
            if (x < 4 || x >= 20 || y < 4 || y >= 16)
                Require(painted.Pixels.AsSpan(y * painted.Stride + x * 4, 4).SequenceEqual(unpainted.Pixels.AsSpan(y * unpainted.Stride + x * 4, 4)), "Selected brush changed pixels outside coverage.");
        OnUndo(this, new()); await WaitForSelectionReadyAsync(); await WaitForFramesAsync(); AssertUploadedFrame(scene);
        Require(ReferenceEquals(unpainted, scene.Snapshot.Images[layerId]) && ReferenceEquals(selected, scene.ActiveSelection), "Content Undo did not restore pixels and selection together.");
        frames = presentedFrameCount; warm = compositor;
        OnUndo(this, new()); await WaitForSelectionReadyAsync(); await WaitForFramesAsync();
        Require(scene.ActiveSelection is null && !scene.CanUndo && ReferenceEquals(warm, compositor) && presentedFrameCount == frames, "Selection Undo after paint rebuilt content or lost history.");
        OnRedo(this, new()); await WaitForSelectionReadyAsync(); OnRedo(this, new()); await WaitForFramesAsync(); AssertUploadedFrame(scene);
        Require(ReferenceEquals(painted, scene.Snapshot.Images[layerId]) && ReferenceEquals(selected, scene.ActiveSelection), "Paint/selection Redo order.");
        report["selectionPaintGpuClipAndMixedHistory"] = "passed";

        scene = EditorDocument.Create(64, 48); OpenDocument(scene); await WaitForFramesAsync();
        using (var release = new ManualResetEventSlim())
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            beforeFrameRender = () => { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Held selection frame timed out."); };
            try
            {
                BeginPointerStroke(new(12, 10)); await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
                beforeFrameRender = null; AppendPointerStroke(new(18, 10)); FinishPointerStroke();
                Require(pointerStroke is null && renderInputs.HasPending, "Selection after stroke fixture did not retain ending input.");
                OnColorRange(this, new()); Require(colorRangeSession is null, "Color Range captured an unfinished mutable stroke.");
                OnSelectAll(this, new()); Require(selectionWorkResult is null, "Selection command overtook pending stroke input.");
            }
            finally { beforeFrameRender = null; release.Set(); }
            await WaitForFramesAsync(); AssertUploadedFrame(scene);
        }
        OnColorRange(this, new()); await WaitForColorRangeReadyAsync(); Require(colorRangeSample is not null, "Color Range stayed blocked after the stroke finished.");
        OnColorRangeCancel(this, new());
        report["selectionSamplingWaitsForStrokeToSettle"] = "passed";

        scene = EditorDocument.Create(64, 48); OpenDocument(scene); await WaitForFramesAsync(); ToolCombo.SelectedIndex = (int)CanvasTool.Lasso;
        LassoKindCombo.SelectedIndex = 0; SetSelectionMode(LassoSelectionMode.Replace);
        BeginLasso(new(4, 4), VirtualKeyModifiers.None); MoveLasso(new(20, 4)); MoveLasso(new(20, 16)); MoveLasso(new(4, 16)); FinishLasso();
        await WaitForSelectionReadyAsync(); var lasso = scene.ActiveSelection;
        OnUndo(this, new()); await WaitForSelectionReadyAsync(); Require(scene.ActiveSelection is null && !scene.CanUndo, "Lasso Undo count.");
        OnRedo(this, new()); await WaitForSelectionReadyAsync(); Require(ReferenceEquals(lasso, scene.ActiveSelection), "Lasso Redo lost coverage.");
        LassoKindCombo.SelectedIndex = 1; BeginLasso(new(28, 8), VirtualKeyModifiers.Shift);
        BeginLasso(new(40, 8), VirtualKeyModifiers.None); BeginLasso(new(40, 20), VirtualKeyModifiers.None); FinishLasso(); await WaitForSelectionReadyAsync();
        Require(scene.ActiveSelection!.CoverageAt(8, 8) == 255 && scene.ActiveSelection.CoverageAt(38, 10) > 0, "Polygonal Add lost prior coverage.");
        OnUndo(this, new()); await WaitForSelectionReadyAsync(); Require(ReferenceEquals(lasso, scene.ActiveSelection), "Polygonal Add Undo count.");
        report["freehandAndPolygonalSelectionHistory"] = "passed";

        scene = SelectionColorScene(); OpenDocument(scene); await WaitForFramesAsync(); ToolCombo.SelectedIndex = (int)CanvasTool.Wand;
        wandSettings = new() { Tolerance = 0, Contiguous = false }; SetSelectionMode(LassoSelectionMode.Replace);
        SelectWithWand(new(8, 8), VirtualKeyModifiers.None); await WaitForSelectionReadyAsync();
        Require(scene.ActiveSelection?.Bounds == new RectI(0, 0, 32, 48), "Worker wand did not select red half.");
        SelectWithWand(new(40, 8), VirtualKeyModifiers.Shift); await WaitForSelectionReadyAsync();
        Require(scene.ActiveSelection?.Bounds == new RectI(0, 0, 64, 48), "Wand Add did not combine matching coverage.");
        SelectWithWand(new(8, 8), VirtualKeyModifiers.Menu | VirtualKeyModifiers.Shift); await WaitForSelectionReadyAsync();
        Require(scene.ActiveSelection?.Bounds == new RectI(32, 0, 32, 48), "Wand Alt subtraction precedence.");
        OnUndo(this, new()); await WaitForSelectionReadyAsync(); Require(scene.ActiveSelection?.Bounds == new RectI(0, 0, 64, 48), "Wand subtraction Undo count.");
        report["workerWandAddSubtractHistory"] = "passed";

        scene = EditorDocument.Create(64, 48); OpenDocument(scene); await WaitForFramesAsync();
        var original = Selection.Rectangle(64, 48, 4, 4, 16, 12).Feathered(3); scene.SetSelection(original);
        ApplySelectionModification(SelectionModification.Expand, 2); await WaitForSelectionReadyAsync(); Require(scene.CanUndo && scene.ActiveSelection!.Feather == 3, "Expand history/feather.");
        OnUndo(this, new()); await WaitForSelectionReadyAsync(); Require(ReferenceEquals(original, scene.ActiveSelection), "Expand Undo.");
        ApplySelectionModification(SelectionModification.Feather, 4); await WaitForSelectionReadyAsync(); Near(5, scene.ActiveSelection!.Feather, "Feather combined history");
        OnUndo(this, new()); await WaitForSelectionReadyAsync(); Require(ReferenceEquals(original, scene.ActiveSelection), "Feather Undo.");
        ApplySelectionModification(SelectionModification.Contract, 40); await WaitForSelectionReadyAsync(); Require(scene.ActiveSelection is { IsEmpty: true }, "Contract to empty deselected.");
        var beforePaint = scene.Snapshot.Images[scene.Snapshot.Manifest.ActiveLayerId!.Value];
        ToolCombo.SelectedIndex = (int)CanvasTool.Brush; BeginPointerStroke(new(8, 8)); FinishPointerStroke(); await WaitForFramesAsync();
        Require(ReferenceEquals(beforePaint, scene.Snapshot.Images[scene.Snapshot.Manifest.ActiveLayerId!.Value]), "Empty contracted selection cloned pixels or opened a stroke.");
        OnUndo(this, new()); await WaitForSelectionReadyAsync(); Require(ReferenceEquals(original, scene.ActiveSelection) && !scene.CanUndo, "Empty paint inserted history after Contract.");
        report["modifyHistoryAndEmptySelectionBlocksPainting"] = "passed";

        scene = EditorDocument.Create(64, 48); OpenDocument(scene); await WaitForFramesAsync();
        var first = Selection.Rectangle(64, 48, 1, 1, 10, 10); var second = Selection.Rectangle(64, 48, 20, 20, 10, 10);
        QueueSelectionWork((_, _) => { Thread.Sleep(40); return first; }, false, "probe");
        QueueSelectionWork((_, _) => second, false, "probe"); OnUndo(this, new());
        await WaitForSelectionReadyAsync(); Require(ReferenceEquals(first, scene.ActiveSelection), "Immediate Undo overtook completed selection commands.");
        OnUndo(this, new()); await WaitForSelectionReadyAsync(); Require(scene.ActiveSelection is null && !scene.CanUndo, "Consecutive Replace collapsed history.");
        OnRedo(this, new()); OnRedo(this, new()); await WaitForSelectionReadyAsync(); Require(ReferenceEquals(second, scene.ActiveSelection), "Consecutive Redo lost order.");
        report["queuedSelectionUndoAndRedoOrder"] = "passed";

        scene = EditorDocument.Create(64, 48); OpenDocument(scene); await WaitForFramesAsync();
        QueueSelectionWork((_, _) => { Thread.Sleep(40); return first; }, false, "probe");
        QueueSelectionWork((_, _) => second, false, "probe"); OnUndo(this, new());
        QueueSelectionWork((previous, _) => LassoSelection.Combine(previous, Selection.Rectangle(64, 48, 40, 2, 8, 8), LassoSelectionMode.Add), true, "probe");
        await WaitForSelectionReadyAsync();
        Require(scene.ActiveSelection!.CoverageAt(3, 3) == 255 && scene.ActiveSelection.CoverageAt(42, 4) == 255
            && scene.ActiveSelection.CoverageAt(23, 23) == 0 && !scene.CanRedo, "Add consumed the pre-Undo selection.");
        OnUndo(this, new()); await WaitForSelectionReadyAsync(); Require(ReferenceEquals(first, scene.ActiveSelection), "Add after queued Undo lost its preceding selection.");
        report["queuedUndoThenAddConsumesRestoredSelection"] = "passed";

        scene = EditorDocument.Create(64, 48); OpenDocument(scene); await WaitForFramesAsync();
        QueueSelectionWork((_, _) => { Thread.Sleep(40); return first; }, false, "probe");
        QueueSelectionWork((_, _) => throw new InvalidOperationException("Intentional selection worker failure."), false, "probe");
        QueueSelectionWork((previous, _) => LassoSelection.Combine(previous, Selection.Rectangle(64, 48, 40, 2, 8, 8), LassoSelectionMode.Add), true, "probe");
        await WaitForSelectionReadyAsync();
        Require(scene.ActiveSelection!.CoverageAt(3, 3) == 255 && scene.ActiveSelection.CoverageAt(42, 4) == 255, "Failed worker lost the preceding published selection before Add.");
        OnUndo(this, new()); await WaitForSelectionReadyAsync(); Require(ReferenceEquals(first, scene.ActiveSelection), "Failed worker inserted an Undo step.");
        OnUndo(this, new()); await WaitForSelectionReadyAsync(); Require(scene.ActiveSelection is null && !scene.CanUndo, "Failed worker altered the earlier history.");
        report["failedSelectionWorkerPreservesPrecedingResultAndHistory"] = "passed";

        await VerifyColorRangeHistoryAsync(report);
        await VerifySelectionWorkers4KAsync(report);
        await VerifySelectionCancellationAsync(report);
        if (directOutlineEnabled) await VerifyDirectOutlineSuiteAsync(report);
        else
        {
            await VerifyOutlineMetricsOwnershipAsync(report);
            await VerifyOutlineBuildCostAsync(report);
            await VerifySelectionOutlineBatchesAsync(report);
            await VerifyOutlineRetirementAsync(report);
            await VerifyPerspectiveOutlineOwnershipAsync(report);
            await VerifyPerspectiveOutlineCostAsync(report, traceFrames: true);
        }
        await VerifyFillAndClearCommandsAsync(report);
        await VerifyFlipCommandsAsync(report);
        await VerifyCanvasSizeAsync(report);
        await VerifyMaskPaintTargetsAsync(report);
        await VerifyTransformOptionPersistenceAsync(report);
        await VerifyViewSwitchesAsync(report);
        await VerifyGridSwitchAsync(report);
        await VerifyGridSettingsAsync(report);
        await VerifyGridDashPatternAsync(report);
    }

    /// <summary>
    /// B02: what one selection outline build actually costs. Each contour run remains an independent
    /// figure. Record extraction, path preparation, native construction and dispatcher turns, with
    /// the old per-object loop retained as a same-process comparison. The
    /// shapes are the three the audit named: a big marquee (four straight runs),
    /// a noisy mask (a small checker crossing the native batch boundary), and a
    /// complex lasso (a jagged boundary at every scale). All three are synthetic and repeatable;
    /// the production outline path is measured directly.
    /// </summary>
    private async Task VerifyOutlineBuildCostAsync(Dictionary<string, object> report)
    {
        var scene = SelectionColorScene(4096, 4096); OpenDocument(scene); await WaitForFramesAsync();
        async Task<OutlineBuild> Measure(Selection selection)
        {
            ResetOutlineBuildProbe();
            scene.SetSelection(selection);
            // The overlay may still hold the previous shape's geometry, so "a build started" is the
            // barrier: waiting for non-null data would pass immediately and report the old shape.
            var started = await WaitUntilAsync(() => OutlineBuildProbe.Builds >= 1, 5000);
            Require(started, "the outline build for the measured shape never started.");
            await WaitForSelectionReadyAsync();
            return OutlineBuildProbe;
        }
        object Shape(OutlineBuild build) => new { runs = build.Runs, yields = build.Yields,
            extractMs = Math.Round(build.ExtractMs, 3), uiBuildMs = Math.Round(build.UiBuildMs, 3),
            prepareMs = Math.Round(build.PrepareMs, 3), nativeBatches = build.NativeBatches,
            maxBatchMs = Math.Round(build.MaxBatchMs, 3), totalMs = Math.Round(build.TotalMs, 3), builds = build.Builds };

        var marquee = Selection.Rectangle(4096, 4096, 200, 200, 3600, 3600);
        var marqueeBuild = await Measure(marquee);
        // Feather is kept apart from the coverage mask, so a feather-only change must reuse the
        // geometry the marquee built. Nothing gets scheduled for it, which makes this a bounded
        // observation: hand the dispatcher a turn, then require no build and the same geometry.
        var feathered = SelectionCommands.Modify(marquee, SelectionModification.Feather, 16)!;
        ResetOutlineBuildProbe();
        scene.SetSelection(feathered);
        await Task.Delay(150);
        await WaitForFramesAsync();
        var featherBuild = OutlineBuildProbe;
        var noise = new MaskSurface(512, 512);
        for (int y = 0; y < 512; y++)
            for (int x = 0; x < 512; x++)
                noise.Pixels[y * 512 + x] = (byte)((((x >> 2) + (y >> 2)) & 1) == 0 ? 255 : 0);
        var noisySelection = new Selection(noise, new RectI(0, 0, 512, 512));
        var noiseBuild = await Measure(noisySelection);
        var points = new List<PointD>();
        for (int step = 0; step < 240; step++)
        {
            double angle = step * Math.PI / 120, radius = step % 2 == 0 ? 1500 : 1200;
            points.Add(new PointD(2048 + radius * Math.Cos(angle), 2048 + radius * Math.Sin(angle)));
        }
        var lasso = LassoSelection.Rasterize(4096, 4096, points, antialiased: true);
        var legacyEdges = await Task.Run(() => SelectionContours.Build(lasso));
        var legacyStarted = Stopwatch.StartNew();
        var legacyGeometry = await BuildLegacySelectionOutlineAsync(legacyEdges);
        double legacyUiMs = legacyStarted.Elapsed.TotalMilliseconds;
        var lassoBuild = await Measure(lasso);
        RequireOutlineRuns(selectionOutlineGeometry, legacyEdges);

        // A superseded build must stop mid-loop instead of publishing, and the newest selection must
        // still get its own outline; that is the "no dispatcher flood left behind" half of B02.
        ResetOutlineBuildProbe();
        scene.SetSelection(noisySelection);
        scene.SetSelection(Selection.Rectangle(4096, 4096, 8, 8, 24, 24));
        await WaitForSelectionReadyAsync();
        var superseded = OutlineBuildProbe;
        Require(superseded.Builds >= 2, "the second selection did not start its own outline build.");
        Require(SelectionOutline.Data is not null && ReferenceEquals(outlinedSelection, scene.ActiveSelection),
            "the newest selection never published its own outline.");

        report["selectionOutlineBuildCost"] = new { marquee = Shape(marqueeBuild), feathered = Shape(featherBuild),
            noise = Shape(noiseBuild), lasso = Shape(lassoBuild), legacyUiMs,
            candidateOverLegacy = lassoBuild.UiBuildMs / legacyUiMs, supersededBuilds = superseded.Builds,
            supersededAbandonedAt = superseded.AbandonedAt, supersededAbandonedMs = Math.Round(superseded.AbandonedMs, 3),
            featherSameCoverage = ReferenceEquals(feathered.OutlineCoverage, marquee.OutlineCoverage),
            featherIsNewSelection = !ReferenceEquals(feathered, marquee) };
        Require(featherBuild.Builds == 0, "a feather-only change started an outline build.");
        // Pin contour count, reuse and dispatcher turns independently of the construction strategy.
        // Compare the jagged star with the retained old loop on the same UI thread and process.
        Require(marqueeBuild.Runs == 4, $"the 4K marquee built {marqueeBuild.Runs} runs, not the four straight edges of a rectangle.");
        Require(noiseBuild.Runs > 100, "the checker mask did not hand the overlay a noisy run count.");
        Require(lassoBuild.Runs == legacyEdges.Length && lassoBuild.Runs > 10_000 && lassoBuild.Yields > 0,
            $"the complex lasso lost contour runs or stopped yielding ({Shape(lassoBuild)}).");
        Require(lassoBuild.UiBuildMs < legacyUiMs * .5,
            $"Outline construction did not halve legacy UI time: candidate={lassoBuild.UiBuildMs:F3}ms legacy={legacyUiMs:F3}ms.");
        GC.KeepAlive(legacyGeometry);
        RetireOutline(legacyGeometry);
        // The small rectangle that superseded the noisy build must be the one that ends up published:
        // four runs, not the 510 the abandoned shape would have built.
        Require(superseded.Runs == 4 && superseded.Builds >= 2,
            $"the superseding outline did not own the published geometry ({Shape(superseded)}).");
        report["selectionOutlineBuildCostMeasured"] = "passed";
    }

    /// <summary>
    /// C05: Fill With Foreground/Background and Clear Selection Pixels run through the real menu
    /// handlers on a live document. The compositing math is pinned in the Core suite, so this group
    /// owns the parts only the app can show: the command reaches the plane the layer panel targets,
    /// it lands as exactly one Undo step, and a command with nothing to act on reports instead of
    /// inventing history.
    /// </summary>
    private async Task VerifyFillAndClearCommandsAsync(Dictionary<string, object> report)
    {
        var scene = EditorDocument.Create(64, 48);
        var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        var pixels = new Surface(64, 48);
        for (int y = 0; y < 48; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                int i = y * pixels.Stride + x * 4;
                pixels.Pixels[i] = 10; pixels.Pixels[i + 1] = 20; pixels.Pixels[i + 2] = 30; pixels.Pixels[i + 3] = 255;
            }
        }
        ((Dictionary<Guid, Surface>)scene.Snapshot.Images)[id] = pixels;
        OpenDocument(scene); await WaitForFramesAsync();
        foregroundColor = Microsoft.UI.Colors.Red;
        backgroundColor = Microsoft.UI.Colors.Blue;
        scene.SetSelection(Selection.Rectangle(64, 48, 8, 8, 16, 16));

        OnFillForeground(this, new()); await WaitForFramesAsync();
        var image = ((Dictionary<Guid, Surface>)scene.Snapshot.Images)[id];
        Require(image.Pixels[10 * image.Stride + 10 * 4] == 255 && image.Pixels[10 * image.Stride + 10 * 4 + 3] == 255,
            "Fill With Foreground did not reach the pixels inside the selection.");
        Require(image.Pixels[2 * image.Stride + 2 * 4] == 10 && image.Pixels[2 * image.Stride + 2 * 4 + 2] == 30,
            "Fill With Foreground painted outside the selection.");
        Require(scene.Undo(), "Fill could not be undone.");
        image = ((Dictionary<Guid, Surface>)scene.Snapshot.Images)[id];
        Require(image.Pixels[10 * image.Stride + 10 * 4] == 10, "Undo did not put the filled pixels back.");
        Require(scene.Redo(), "Redo of the fill failed.");

        OnFillBackground(this, new()); await WaitForFramesAsync();
        image = ((Dictionary<Guid, Surface>)scene.Snapshot.Images)[id];
        Require(image.Pixels[10 * image.Stride + 10 * 4 + 2] == 255 && image.Pixels[10 * image.Stride + 10 * 4] == 0,
            "Fill With Background did not use the background color.");
        report["fillForegroundAndBackgroundUsePalette"] = "passed";

        // Delete on an empty selection has nothing to clear: it must say so, not create history.
        var undoDepth = scene.CanUndo;
        scene.SetSelection(null);
        await DeletePlaneTargetAsync(clearSelection: true); await WaitForFramesAsync();
        Require(scene.CanUndo == undoDepth, "Clear without a selection still wrote history.");
        image = ((Dictionary<Guid, Surface>)scene.Snapshot.Images)[id];
        Require(image.Pixels[10 * image.Stride + 10 * 4] == 0, "Clear without a selection changed pixels.");

        scene.SetSelection(Selection.Rectangle(64, 48, 8, 8, 16, 16));
        // The clear runs on the deletion worker; the event handler is async void, so the probe
        // awaits the work itself the way ClearWorkerProbe does, instead of racing its frames.
        Require(await DeletePlaneTargetAsync(clearSelection: true), "Clear reported no change.");
        await WaitForFramesAsync();
        image = ((Dictionary<Guid, Surface>)scene.Snapshot.Images)[id];
        Require(image.Pixels[10 * image.Stride + 10 * 4 + 3] == 0, "Clear left the selected pixels opaque.");
        Require(image.Pixels[2 * image.Stride + 2 * 4 + 3] == 255, "Clear punched a hole outside the selection.");
        Require(scene.Undo() && ((Dictionary<Guid, Surface>)scene.Snapshot.Images)[id].Pixels[10 * 4 + 3] == 255,
            "Undo of the clear did not bring the pixels back.");
        report["clearSelectionPixelsPunchesHolesAndUndoes"] = "passed";

        // The mask plane: the same command follows whatever the layer panel targets.
        scene.SetSelection(null);
        OnAddMask(this, new()); await WaitForFramesAsync();
        Require(scene.Snapshot.Masks.ContainsKey(id), "The mask fixture did not attach a mask.");
        HandleThumbnailClick(id, true, VirtualKeyModifiers.None); await WaitForFramesAsync();
        Require(paintOnMask, "Clicking the mask thumbnail did not make the mask the paint target.");
        foregroundColor = Microsoft.UI.Colors.Black;
        scene.SetSelection(Selection.Rectangle(64, 48, 8, 8, 16, 16));
        OnFillForeground(this, new()); await WaitForFramesAsync();
        var mask = ((Dictionary<Guid, MaskSurface>)scene.Snapshot.Masks)[id];
        Require(mask.Pixels[10 * 64 + 10] == 0, "Fill on the mask target wrote the image plane instead.");
        Require(mask.Pixels[2 * 64 + 2] == 255, "Fill hid the part of the mask outside the selection.");
        report["fillFollowsTheMaskTargetNotTheImagePlane"] = "passed";

        // Leave the window as it was found: the mask target and a live selection would otherwise
        // steer every group that runs after this one.
        HandleThumbnailClick(id, false, VirtualKeyModifiers.None); await WaitForFramesAsync();
        Require(!paintOnMask, "Returning to the image target failed.");
        scene.SetSelection(null);
    }

    /// <summary>
    /// C05b: the two flip commands through the menu handlers. The mirror arithmetic is pinned in the
    /// Core suite; what only the app can show is that a flip recomposites the canvas (it changes no
    /// pixels, so a stale frame would look right to the eye), and that a canvas flip carries the
    /// live selection and the guides with it instead of leaving them behind.
    /// </summary>
    private async Task VerifyFlipCommandsAsync(Dictionary<string, object> report)
    {
        var scene = EditorDocument.Create(64, 48);
        var near = scene.ImportImage(new Surface(8, 8), "Near");
        var far = scene.ImportImage(new Surface(8, 8), "Far");
        near.Transform = new LayerTransform { Origin = new PointD(0, 0), Size = new SizeD(8, 8) };
        far.Transform = new LayerTransform { Origin = new PointD(20, 0), Size = new SizeD(8, 8) };
        scene.Snapshot.Manifest.Guides = [new CanvasGuide { Id = Guid.NewGuid(), Axis = GuideAxis.Vertical, Position = 5 }];
        OpenDocument(scene); await WaitForFramesAsync();
        // Read placements back out of the live snapshot: a flip writes the record the manifest holds,
        // and Undo can hand back different instances, so a captured reference would go stale quietly.
        ProjectLayerRecord Placement(string name) => scene.Snapshot.Manifest.Layers.Single(layer => layer.Name == name);
        var nearId = near.Id;
        selectedLayerId = nearId; selectedLayerIds.Clear(); selectedLayerIds.Add(nearId);
        scene.SetSelection(Selection.Rectangle(64, 48, 0, 0, 8, 8));
        var frames = presentedFrameCount;

        OnFlipLayerHorizontal(this, new()); await WaitForFramesAsync();
        Require(Placement("Near").Transform.FlipX && Placement("Near").Transform.Origin.X == 0,
            "Flip Layer about its own middle moved the box.");
        Require(presentedFrameCount > frames, "A placement-only flip did not recomposite the canvas.");
        Require(scene.Undo() && !Placement("Near").Transform.FlipX, "Flip Layer did not undo as one step.");
        report["flipLayerTogglesPlacementAndRecomposites"] = "passed";

        frames = presentedFrameCount;
        OnFlipCanvasHorizontal(this, new()); await WaitForFramesAsync();
        Require(Placement("Near").Transform.Origin.X == 56 && Placement("Far").Transform.Origin.X == 36,
            $"Flip Canvas did not mirror both layers across the middle ({Placement("Near").Transform.Origin.X}, {Placement("Far").Transform.Origin.X}).");
        Require(scene.Snapshot.Manifest.Guides![0].Position == 59, "Flip Canvas left the guide where it was.");
        Require(scene.ActiveSelection is { } flipped && flipped.CoverageAt(60, 4) == 255 && flipped.CoverageAt(4, 4) == 0,
            "Flip Canvas did not mirror the live selection.");
        Require(presentedFrameCount > frames, "Flip Canvas did not recomposite the canvas.");
        Require(scene.Undo() && Placement("Near").Transform.Origin.X == 0 && scene.Snapshot.Manifest.Guides![0].Position == 5,
            "Flip Canvas did not undo layers and guides together.");
        report["flipCanvasMirrorsLayersGuidesAndSelectionTogether"] = "passed";
    }

    /// <summary>
    /// C05c: Canvas Size through the same path the dialog's Confirm button runs. What only the app
    /// can show is that a bigger canvas is recomposed at the new size — a document whose render
    /// targets kept the old rectangle would still answer with 64×48 pixels.
    /// </summary>
    private async Task VerifyCanvasSizeAsync(Dictionary<string, object> report)
    {
        var scene = EditorDocument.Create(64, 48);
        var block = scene.ImportImage(new Surface(8, 8), "Block");
        block.Transform = new LayerTransform { Origin = new PointD(0, 0), Size = new SizeD(8, 8) };
        OpenDocument(scene); await WaitForFramesAsync();
        var frames = presentedFrameCount;

        ApplyCanvasSize(80, 48, anchor: 4, extensionFill: null);
        await WaitForFramesAsync();
        Require(scene.Snapshot.Manifest.Width == 80 && scene.Snapshot.Manifest.Height == 48,
            "Canvas Size did not restamp the document dimensions.");
        Require(scene.Snapshot.Manifest.Layers.Single(layer => layer.Name == "Block").Transform.Origin.X == 8,
            "Canvas Size did not slide the layer by the anchor's offset (expected +8 for a middle anchor).");
        Require(presentedFrameCount > frames, "Canvas Size did not recomposite the canvas.");
        report["canvasSizeResizesAndSlidesLayers"] = "passed";

        ApplyCanvasSize(96, 64, anchor: 4, extensionFill: (255, 0, 0));
        await WaitForFramesAsync();
        var composite = new CanvasCompositor(scene.Snapshot).Render();
        Require(composite.Width == 96 && composite.Height == 64, $"the composite is still {composite.Width}×{composite.Height}.");
        // The old 80×48 canvas sits at (8,8). A new document's Background layer is solid white, so
        // inside the old rectangle the composite must be that white and never the fill color, while
        // the gained band carries the fill.
        Require(composite.Pixels[0] == 255 && composite.Pixels[1] == 0 && composite.Pixels[3] == 255,
            "the extension fill did not reach the new corner.");
        Require(composite.Pixels[24 * composite.Stride + 24 * 4 + 1] == 255 && composite.Pixels[24 * composite.Stride + 24 * 4 + 2] == 255,
            $"the extension fill bled over the old canvas area (r={composite.Pixels[24 * composite.Stride + 24 * 4]} " +
            $"g={composite.Pixels[24 * composite.Stride + 24 * 4 + 1]} b={composite.Pixels[24 * composite.Stride + 24 * 4 + 2]} at 24,24).");
        Require(scene.Undo() && scene.Snapshot.Manifest.Width == 80, "Canvas Size undid to the wrong width.");
        report["canvasSizeExtensionFillCompositesUnderTheArtwork"] = "passed";
    }

    private static EditorDocument SelectionColorScene(int width = 64, int height = 48)
    {
        var scene = EditorDocument.Create(width, height); var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        var pixels = new Surface(width, height);
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        { int i = y * pixels.Stride + x * 4; pixels.Pixels[i + (x < width / 2 ? 0 : 2)] = 255; pixels.Pixels[i + 3] = 255; }
        ((Dictionary<Guid, Surface>)scene.Snapshot.Images)[id] = pixels;
        return scene;
    }

    private async Task VerifyColorRangeHistoryAsync(Dictionary<string, object> report)
    {
        var scene = SelectionColorScene(); OpenDocument(scene); await WaitForFramesAsync();
        var original = Selection.Rectangle(64, 48, 4, 4, 8, 8); scene.SetSelection(original);
        scene.SetSelectionUndoable(Selection.Rectangle(64, 48, 8, 8, 10, 10)); scene.Undo(); Require(scene.CanRedo && !scene.CanUndo, "Color Range fixture history.");
        foregroundColor = Microsoft.UI.Colors.Red; var warm = compositor; int frames = presentedFrameCount;
        OnColorRange(this, new()); OnColorRangeForeground(this, new()); await WaitForColorRangeReadyAsync();
        Require(scene.ActiveSelection?.Bounds == new RectI(0, 0, 32, 48), "Color Range dropped colors entered while loading its sample.");
        report["colorRangeRemembersColorsEnteredDuringSampling"] = "passed";
        Require(!EditMenu.IsEnabled && !LayerMenu.IsEnabled, "Color Range left content/history menu enabled.");
        Require(scene.ActiveSelection?.Bounds == new RectI(0, 0, 32, 48) && !scene.CanUndo && scene.CanRedo, "Color Range preview wrote history.");
        AppWindow.Resize(new(1180, 820)); await Task.Delay(150); RootGrid.UpdateLayout(); await SaveWorkspaceCaptureAsync("color-range-workspace.png");
        OnColorRangeClear(this, new()); await WaitForColorRangeReadyAsync(); Require(ReferenceEquals(original, scene.ActiveSelection), "No-color preview did not restore original.");
        OnColorRangeForeground(this, new()); var obsolete = colorRangeSession!.Work; OnColorRangeCancel(this, new());
        if (obsolete is not null) await obsolete;
        Require(ReferenceEquals(original, scene.ActiveSelection) && scene.CanRedo && !scene.CanUndo, "Color Range cancel/stale worker lost selection or Redo.");
        Require(EditMenu.IsEnabled && LayerMenu.IsEnabled, "Color Range cancel did not restore controls.");
        report["colorRangePreviewClearCancelPreservesHistory"] = "passed";

        OnColorRange(this, new()); await WaitForColorRangeReadyAsync(); OnColorRangeForeground(this, new());
        ColorRangeFuzzinessSlider.Value = 10; ColorRangeFuzzinessSlider.Value = 20;
        OnColorRangeApply(this, new()); await WaitForColorRangeClosedAsync(); await WaitForSelectionReadyAsync();
        var result = scene.ActiveSelection;
        Require(result?.Bounds == new RectI(0, 0, 32, 48) && scene.CanUndo && !scene.CanRedo, "Color Range Apply did not wait for current preview/commit history.");
        OnUndo(this, new()); await WaitForSelectionReadyAsync(); Require(ReferenceEquals(original, scene.ActiveSelection) && !scene.CanUndo, "Color Range preview changes created multiple Undo steps.");
        OnRedo(this, new()); await WaitForSelectionReadyAsync(); Require(ReferenceEquals(result, scene.ActiveSelection), "Color Range Redo lost coverage.");
        Require(ReferenceEquals(warm, compositor) && presentedFrameCount == frames, "Color Range history recomposited document.");
        report["colorRangeApplySingleUndoRedoWithoutComposition"] = "passed";

        OnColorRange(this, new()); await WaitForColorRangeReadyAsync(); OnColorRangeForeground(this, new()); obsolete = colorRangeSession!.Work;
        var replacement = EditorDocument.Create(32, 32); OpenDocument(replacement);
        if (obsolete is not null) await obsolete;
        await WaitForFramesAsync(); Require(replacement.ActiveSelection is null && !replacement.CanUndo && !colorRangeSampling, "Color Range leaked into replacement document.");
        report["colorRangeRejectsReplacedDocument"] = "passed";

    }

    private async Task VerifySelectionWorkers4KAsync(Dictionary<string, object> report)
    {
        var scene = SelectionColorScene(4096, 4096); OpenDocument(scene); await WaitForFramesAsync();
        var warm = compositor; int frames = presentedFrameCount;
        scene.SetSelection(Selection.Rectangle(4096, 4096, 200, 200, 3600, 3600)); await WaitForSelectionReadyAsync();
        long beforeBytes = GC.GetAllocatedBytesForCurrentThread(); var timer = Stopwatch.StartNew();
        ApplySelectionModification(SelectionModification.Expand, 5);
        double dispatchMs = timer.Elapsed.TotalMilliseconds, allocatedKiB = (GC.GetAllocatedBytesForCurrentThread() - beforeBytes) / 1024.0;
        await WaitForSelectionReadyAsync();
        Require(scene.ActiveSelection?.Bounds == new RectI(195, 195, 3610, 3610), "4K expansion coverage.");
        report["selectionModify4KSingleSample"] = new { dispatchMs, dispatchAllocatedKiB = allocatedKiB, readyMs = timer.Elapsed.TotalMilliseconds };

        beforeBytes = GC.GetAllocatedBytesForCurrentThread(); timer.Restart(); OnColorRange(this, new());
        dispatchMs = timer.Elapsed.TotalMilliseconds; allocatedKiB = (GC.GetAllocatedBytesForCurrentThread() - beforeBytes) / 1024.0;
        await WaitForColorRangeReadyAsync();
        Require(colorRangeSample is { Width: 4096, Height: 4096 }, "4K Color Range sample missing.");
        report["colorRangeSample4KSingleSample"] = new { dispatchMs, dispatchAllocatedKiB = allocatedKiB, readyMs = timer.Elapsed.TotalMilliseconds };
        foregroundColor = Microsoft.UI.Colors.Red;
        beforeBytes = GC.GetAllocatedBytesForCurrentThread(); timer.Restart(); OnColorRangeForeground(this, new());
        dispatchMs = timer.Elapsed.TotalMilliseconds; allocatedKiB = (GC.GetAllocatedBytesForCurrentThread() - beforeBytes) / 1024.0;
        await WaitForColorRangeReadyAsync(); await WaitForSelectionReadyAsync();
        Require(scene.ActiveSelection?.Bounds == new RectI(0, 0, 2048, 4096) && ColorRangePreviewImage.Source is Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap { PixelWidth: 200, PixelHeight: 200 }, "4K Color Range coverage/preview mismatch.");
        report["colorRangePreview4KSingleSample"] = new { dispatchMs, dispatchAllocatedKiB = allocatedKiB, readyMs = timer.Elapsed.TotalMilliseconds };
        OnColorRangeApply(this, new()); await WaitForColorRangeClosedAsync(); await WaitForSelectionReadyAsync();
        OnUndo(this, new()); await WaitForSelectionReadyAsync(); Require(scene.ActiveSelection?.Bounds == new RectI(195, 195, 3610, 3610), "4K Color Range commit Undo.");
        Require(ReferenceEquals(warm, compositor) && frames == presentedFrameCount, "4K selection workers rebuilt the displayed document.");
        report["selectionWorkers4KWithoutComposition"] = "passed";
    }

    // B01: a superseded selection build must stop on its token instead of finishing a 4K distance
    // transform behind the newest request, and must not publish when it ends.
    private async Task VerifySelectionCancellationAsync(Dictionary<string, object> report)
    {
        var scene = SelectionColorScene(4096, 4096); OpenDocument(scene); await WaitForFramesAsync();
        scene.SetSelection(Selection.Rectangle(4096, 4096, 200, 200, 3600, 3600)); await WaitForSelectionReadyAsync();
        var published = scene.ActiveSelection!.Bounds;
        int abandonedBefore = abandonedSelectionBuilds;

        var watch = Stopwatch.StartNew();
        ApplySelectionModification(SelectionModification.Expand, 40);
        await WaitForSelectionReadyAsync(); watch.Stop();
        double fullMs = watch.Elapsed.TotalMilliseconds;
        Require(scene.ActiveSelection?.Bounds == new RectI(160, 160, 3680, 3680), "4K expand coverage.");
        Require(fullMs > 20, $"the control expand only took {fullMs} ms, too quick to compare against");

        scene.SetSelection(Selection.Rectangle(4096, 4096, 200, 200, 3600, 3600)); await WaitForSelectionReadyAsync();
        Require(scene.ActiveSelection?.Bounds == published, "the cancelled-run setup lost the control selection.");
        ApplySelectionModification(SelectionModification.Expand, 40);
        await Task.Delay(30);
        CancelSelectionWork();
        await Task.Delay(500);
        Require(scene.ActiveSelection?.Bounds == published, "a cancelled build still changed the selection.");
        Require(abandonedSelectionBuilds > abandonedBefore, "the superseded build ran to completion instead of stopping on the token.");
        Require(lastAbandonedBuildMs < fullMs, $"the cancelled build took {lastAbandonedBuildMs} ms against a {fullMs} ms control.");

        ApplySelectionModification(SelectionModification.Contract, 40);
        await WaitForSelectionReadyAsync();
        Require(scene.ActiveSelection?.Bounds != published, "the request after a cancellation did not publish.");
        report["selectionCancellationStopsSupersededBuild"] = new
        {
            controlMs = Math.Round(fullMs, 3),
            abandonedMs = Math.Round(lastAbandonedBuildMs, 3),
            abandonedBuilds = abandonedSelectionBuilds - abandonedBefore,
        };
    }

    private async Task WaitForColorRangeReadyAsync()    {
        var timer = Stopwatch.StartNew();
        while (colorRangeSession is not null && (colorRangeSample is null || colorRangeSession.Work is { IsCompleted: false }))
        { if (timer.ElapsedMilliseconds > 10000) throw new TimeoutException("Color Range did not finish."); await Task.Delay(1); }
    }

    private async Task WaitForColorRangeClosedAsync()
    {
        var timer = Stopwatch.StartNew();
        while (colorRangeSession is not null)
        { if (timer.ElapsedMilliseconds > 10000) throw new TimeoutException("Color Range did not close."); await Task.Delay(1); }
    }

    private async Task WaitForSelectionReadyAsync()
    {
        var timer = Stopwatch.StartNew();
        while (selectionWorkResult is not null || (document?.ActiveSelection is not null && !OutlineIsReady))
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Selection did not finish.");
            await Task.Delay(1);
        }
    }
}
#endif
