#if DEBUG
using Compositor.Core;
using Compositor.Segmentation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json.Nodes;
using System.Diagnostics;

namespace Compositor.App;

public sealed partial class MainWindow
{
    internal async Task RunObjectSelectionProbeAsync()
    {
        var report = new Dictionary<string, object>();
        try
        {
            string root = Environment.GetEnvironmentVariable("COMPOSITOR_PREFERENCES_DIRECTORY")
                ?? throw new InvalidOperationException("Use isolated preferences for Object Selection.");
            string imagePath = Environment.GetEnvironmentVariable("COMPOSITOR_SELECTION_PROBE_IMAGE")
                ?? throw new InvalidOperationException("Provide COMPOSITOR_SELECTION_PROBE_IMAGE.");
            bool verifySoftEdges = Environment.GetEnvironmentVariable("COMPOSITOR_SELECTION_PROBE_SOFT") == "1";
            await WaitForFramesAsync();
            var image = await WicImaging.DecodeSurfaceAsync(await File.ReadAllBytesAsync(imagePath), Limits.MaxSide, Limits.MaxSurfacePixels);
            var scene = EditorDocument.Create(image.Width, image.Height); scene.ImportImage(image, "Truck");
            await ProjectStore.SaveAsync(scene.Snapshot, Path.Combine(root, "object-sample.comp"));
            OpenDocument(scene); await WaitForFramesAsync();
            scene.SetSelectionUndoable(Selection.Rectangle(image.Width, image.Height, 3, 4, 20, 30));
            scene.RenameLayer(scene.Snapshot.Manifest.ActiveLayerId!.Value, "Undo me"); scene.Undo();
            var baseline = scene.ActiveSelection; var history = scene.CurrentHistoryId;
            var beforePreparation = scene.CurrentHistoryId;
            var preparationWatch = Stopwatch.StartNew();
            ToolCombo.SelectedIndex = (int)CanvasTool.ObjectSelect;
            await WaitForFramesAsync();
            if (Environment.GetEnvironmentVariable("COMPOSITOR_SELECTION_PROBE_PREWARM") == "1")
            {
                Require(await WaitForToolSwitchLandedAsync(CanvasTool.ObjectSelect), "Object Selection tool did not activate.");
                Require(objectSelectionWarmUp is not null, "Choosing Object Selection did not start preparing the installed model.");
                await objectSelectionWarmUp!.WaitAsync(TimeSpan.FromSeconds(20));
                Require(objectSegmenter?.EncoderRuns == 1 && scene.CurrentHistoryId == beforePreparation && scene.ActiveSelection == baseline && scene.CanRedo,
                    "Background preparation failed to cache image features or changed selection history.");
                report["backgroundPreparationMs"] = preparationWatch.Elapsed.TotalMilliseconds;
                report["toolActivationPreparesImageWithoutEditing"] = "passed";
            }
            if (Environment.GetEnvironmentVariable("COMPOSITOR_SELECTION_PROBE_INTERACTION") == "1")
            {
                var origin = viewport.Origin; double zoom = viewport.Zoom;
                BeginObjectSelectionPointer(new(image.Width * .5, image.Height * .5), 0);
                try
                {
                    Require(objectSelectionPress is not null, "Object box gesture did not start.");
                    bool navigationStarted = BeginNavigationAt(new(50, 50), true, false);
                    CancelNavigation();
                    ApplyViewportWheel(120, true, true, new(50, 50));
                    ApplyViewportManipulation(1.2, new(30, 40), new(50, 50));
                    Require(!navigationStarted && viewport.Zoom == zoom && viewport.Origin == origin,
                        "Object box dragging also moved or zoomed the canvas.");
                }
                finally { CancelObjectSelection(); }
                report["objectBoxGestureBlocksViewportNavigation"] = "passed";
            }
            var pixelBytes = image.Pixels.ToArray();
            var initialWatch = Stopwatch.StartNew();
            var initialPreview = PromptObjectSelectionAsync(new(new(image.Width * .5, image.Height * .5)), null);
            Require(await WaitUntilAsync(() => ReferenceEquals(outlinedSelection, objectSelectionDraft?.Result) && OutlineIsReady, 10000),
                "The first object contour did not become ready.");
            await WaitForFramesAsync();
            report["firstContourMs"] = initialWatch.Elapsed.TotalMilliseconds;
            await initialPreview;
            if (verifySoftEdges)
            {
                Require(objectSelectionDraft?.Result?.Feather == 0,
                    "Object selection still applies blanket feather instead of fitting the original image edge.");
                var draft = objectSelectionDraft!; var raw = draft.Coverage!;
                var softened = await SampleCoverageAsync(draft.Result!);
                int rawTransition = raw.Pixels.Count(value => value > 0 && value < 255);
                int softTransition = softened.Pixels.Count(value => value > 0 && value < 255);
                Require(rawTransition > 0 && softTransition == rawTransition, "Image-guided alpha was lost or blurred before publication.");
                foreach (int amount in new[] { 6, 3, 0 })
                {
                    objectSelectionFeather.Value = amount;
                    Require(await WaitUntilAsync(() => draft is { Busy: false, Result: not null } && draft.Result.Feather == amount, 10000),
                        $"The feather control did not preview {amount}px.");
                    Require(ReferenceEquals(raw, draft.Coverage) && scene.CurrentHistoryId == history && scene.ActiveSelection == baseline && scene.CanRedo,
                        "Changing feather reran the model or changed selection history before confirmation.");
                }
                objectSelectionFeather.Value = 6; objectSelectionFeather.Value = 9; objectSelectionFeather.Value = 0;
                Require(await WaitUntilAsync(() => draft is { Busy: false, Result: not null } && draft.Result.Feather == 0, 10000)
                    && ReferenceEquals(raw, draft.Coverage), "Rapid feather changes reran inference or reused stale prompt coverage.");
                report["featherTransitionPixels"] = new { rawTransition, softTransition };
                report["adjustableFeatherReusesMaskAndPreservesHistory"] = "passed";
            }
            await File.WriteAllBytesAsync(Path.Combine(root, "point-mask.png"), await WicImaging.EncodePngAsync(objectSelectionDraft!.Result!.Coverage));
            Require(objectSelectionDraft?.Result is { IsEmpty: false } && scene.ActiveSelection == baseline
                && scene.CurrentHistoryId == history && scene.CanRedo && !CanEditLayerCommands,
                "Object preview changed the selection/history/redo or allowed conflicting edits.");
            Require(objectSelectionOverlay.Children.Count == 0,
                "Object preview still leaves tint or prompt dots over the image instead of the selection boundary.");
            Require(await WaitUntilAsync(() => ReferenceEquals(outlinedSelection, objectSelectionDraft!.Result) && OutlineIsReady, 10000),
                "The object draft did not display its native selection contour.");
            Require(SelectionOutline.StrokeThickness * viewport.PointsPerPixel <= 1.5,
                "The selection contour is too thick in screen coordinates.");
            var contourPixels = await CaptureCanvasAsync();
            byte[] withoutContour;
            try
            {
                SelectionOutline.Opacity = 0;
                withoutContour = await CaptureCanvasAsync();
            }
            finally { SelectionOutline.Opacity = 1; }
            int blackPixels = 0, whitePixels = 0;
            for (int p = 0; p < contourPixels.Length; p += 4)
            {
                if (withoutContour[p] - contourPixels[p] > 32 && withoutContour[p + 1] - contourPixels[p + 1] > 32
                    && withoutContour[p + 2] - contourPixels[p + 2] > 32) blackPixels++;
                if (contourPixels[p] - withoutContour[p] > 32 && contourPixels[p + 1] - withoutContour[p + 1] > 32
                    && contourPixels[p + 2] - withoutContour[p + 2] > 32) whitePixels++;
            }
            Require(blackPixels > 32 && whitePixels > 32,
                $"A single thin contour must render both black and white dashes across short edge runs: black={blackPixels}, white={whitePixels}.");
            report["outlineContrastPixels"] = new { blackPixels, whitePixels };
            report["highContrastSelectionOutline"] = "passed";
            int encoded = objectSegmenter!.EncoderRuns;
            await PromptObjectSelectionAsync(new(new(image.Width * .95, image.Height * .95), false), null);
            Require(objectSelectionDraft?.Points.Count == 2 && objectSegmenter.EncoderRuns == encoded,
                "Correction points lost the session or reran the image encoder.");
            Require(objectSelectionOverlay.Children.Count == 0, "Correction points remained painted over the subject.");
            CancelObjectSelection();
            Require(scene.ActiveSelection == baseline && scene.CurrentHistoryId == history && scene.CanRedo
                && objectSelectionOverlay.Children.Count == 0 && ReferenceEquals(DisplayedSelection, baseline),
                "Cancel changed history or retained preview visuals.");
            report["previewCorrectionCacheAndCancelPreserveRedo"] = "passed";

            if (Environment.GetEnvironmentVariable("COMPOSITOR_SELECTION_PROBE_INTERACTION") == "1")
            {
                var refinement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                objectSelectionRefinementProbe = refinement.Task;
                var watch = Stopwatch.StartNew();
                var preview = PromptObjectSelectionAsync(new(new(image.Width * .5, image.Height * .5)), null);
                try
                {
                    Require(await WaitUntilAsync(() => objectSelectionDraft is { Busy: false, Result: not null }
                        && ReferenceEquals(outlinedSelection, objectSelectionDraft.Result) && OutlineIsReady, 10000),
                        "No usable object contour appeared before edge refinement.");
                    await WaitForFramesAsync();
                    report["cachedFirstContourMs"] = watch.Elapsed.TotalMilliseconds;
                    Require(!preview.IsCompleted, "Object feedback still waits for edge refinement to finish.");
                    Require(scene.ActiveSelection == baseline && scene.CurrentHistoryId == history && scene.CanRedo,
                        "The immediate object preview changed history before confirmation.");
                }
                finally
                {
                    refinement.TrySetResult(); await preview;
                    if (ReferenceEquals(objectSelectionRefinementProbe, refinement.Task)) objectSelectionRefinementProbe = null;
                }
                CancelObjectSelection();
                report["usableContourBeforeEdgeRefinement"] = "passed";

                refinement = new(TaskCreationOptions.RunContinuationsAsynchronously);
                objectSelectionRefinementProbe = refinement.Task;
                preview = PromptObjectSelectionAsync(new(new(image.Width * .5, image.Height * .5)), null);
                try
                {
                    Require(await WaitUntilAsync(() => objectSelectionDraft is { Busy: false, Result: not null }, 10000),
                        "The cancellable fast preview did not appear.");
                    CancelObjectSelection();
                }
                finally { refinement.TrySetResult(); await preview; objectSelectionRefinementProbe = null; }
                await WaitForFramesAsync();
                Require(objectSelectionDraft is null && scene.ActiveSelection == baseline && scene.CurrentHistoryId == history
                    && scene.CanRedo && ReferenceEquals(DisplayedSelection, baseline),
                    "Idle refinement overwrote a cancelled preview or changed history/redo.");
                report["cancelFastPreviewRejectsLateRefinement"] = "passed";

                refinement = new(TaskCreationOptions.RunContinuationsAsynchronously);
                objectSelectionRefinementProbe = refinement.Task;
                preview = PromptObjectSelectionAsync(new(new(image.Width * .5, image.Height * .5)), null);
                Selection? toUse = null;
                try
                {
                    Require(await WaitUntilAsync(() => objectSelectionDraft is { Busy: false, Result: not null }, 10000),
                        "The fast preview was not ready for Move.");
                    toUse = objectSelectionDraft!.Result!;
                    ToolCombo.SelectedIndex = (int)CanvasTool.Move;
                    Require(await WaitForToolSwitchLandedAsync(CanvasTool.Move) && objectSelectionDraft is null
                        && ReferenceEquals(scene.ActiveSelection, toUse) && CanEditLayerCommands,
                        "Switching to Move discarded the prepared object selection or still required Confirm.");
                }
                finally { refinement.TrySetResult(); await preview; objectSelectionRefinementProbe = null; }
                Require(ReferenceEquals(scene.ActiveSelection, toUse), "Late refinement replaced the selection already in use by Move.");
                scene.Undo();
                Require(scene.CurrentHistoryId == history && scene.ActiveSelection == baseline && scene.CanRedo,
                    "Using the object selection from another tool was not one Undo.");
                ToolCombo.SelectedIndex = (int)CanvasTool.ObjectSelect;
                Require(await WaitForToolSwitchLandedAsync(CanvasTool.ObjectSelect), "Object Selection did not reactivate.");
                report["toolSwitchKeepsSelectionWithSingleUndo"] = "passed";

                var preparing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                objectSelectionPreparationProbe = preparing.Task;
                var waiting = PromptObjectSelectionAsync(new(new(image.Width * .5, image.Height * .5)), null);
                ToolCombo.SelectedIndex = (int)CanvasTool.Brush;
                await WaitForFramesAsync();
                try { Require(objectSelectionDraft is not null, "A tool switch cancelled the latest pending object request."); }
                finally { preparing.TrySetResult(); await waiting; objectSelectionPreparationProbe = null; }
                Require(await WaitForToolSwitchLandedAsync(CanvasTool.Brush) && objectSelectionDraft is null
                    && scene.ActiveSelection is { IsEmpty: false } && scene.CurrentHistoryId != history,
                    "The pending selection did not become usable by the chosen tool.");
                scene.Undo(); Require(scene.ActiveSelection == baseline && scene.CurrentHistoryId == history, "Pending tool switch broke Undo.");
                ToolCombo.SelectedIndex = (int)CanvasTool.ObjectSelect;
                Require(await WaitForToolSwitchLandedAsync(CanvasTool.ObjectSelect), "Object Selection did not reactivate after Brush.");
                report["pendingToolSwitchUsesLatestResult"] = "passed";
            }

            await PromptObjectSelectionAsync(null, new(image.Width * .15, image.Height * .2, image.Width * .7, image.Height * .65));
            var expected = objectSelectionDraft!.Result!;
            Require(await WaitUntilAsync(() => ReferenceEquals(outlinedSelection, expected) && OutlineIsReady, 10000),
                "The boxed draft did not display its actual selection contour.");
            var previewOutline = OutlineDisplayIdentity;
            Require(ConfirmObjectSelection() && scene.ActiveSelection is { IsEmpty: false } && !scene.CanRedo,
                "Confirm did not commit the prepared selection.");
            Require(ReferenceEquals(previewOutline, OutlineDisplayIdentity), "Confirm rebuilt or changed the displayed draft contour.");
            report["nativeContourWithoutPersistentDotsOrTint"] = "passed";
            scene.Undo(); Require(scene.ActiveSelection == baseline && scene.CurrentHistoryId == history, "Confirm is not one Undo.");
            scene.Redo(); Require(Enumerable.Range(0, image.Width * image.Height).All(i =>
                scene.ActiveSelection!.CoverageAt(i % image.Width, i / image.Width) == expected.CoverageAt(i % image.Width, i / image.Width)), "Redo changed coverage.");
            Require(image.Pixels.SequenceEqual(pixelBytes), "Selection changed source image pixels.");
            report["boxConfirmSingleUndoRedoAndOriginalPixels"] = "passed";

            if (Environment.GetEnvironmentVariable("COMPOSITOR_SELECTION_PROBE_PROMPTS") is { Length: > 0 } promptJson)
            {
                var input = ObjectSelectionRequest.Parse(JsonNode.Parse(promptJson)!.AsObject(), image.Width, image.Height);
                foreach (var point in input.Points) await PromptObjectSelectionAsync(point, null);
                var corrected = objectSelectionDraft!.Result!;
                foreach (var point in input.Points)
                    Require((corrected.CoverageAt((int)point.Position.X, (int)point.Position.Y) >= 128) == point.Include,
                        $"Correction ignored the {(point.Include ? "include" : "exclude")} point at {point.Position}.");
                if (Environment.GetEnvironmentVariable("COMPOSITOR_SELECTION_PROBE_SUBJECT") == "1")
                {
                    Require(corrected.CoverageAt(80, 1000) >= 128,
                        "The foreground person's wrist is still missing after the same five corrective prompts.");
                    Require(corrected.CoverageAt(600, 80) == 0 && corrected.CoverageAt(530, 600) == 255 && corrected.Bounds.Y >= 150,
                        "Soft object edges leaked confidence into the background or made the subject interior translucent.");
                    report["foregroundSubjectIncludesWristWithoutExtraPrompts"] = "passed";
                    report["softEdgesKeepSolidInteriorAndClearBackground"] = "passed";
                }
                Require(await WaitUntilAsync(() => ReferenceEquals(outlinedSelection, corrected) && OutlineIsReady, 10000),
                    "Corrected contour did not finish.");
                await WaitForFramesAsync();
                var correctedCoverage = await SampleCoverageAsync(corrected);
                await File.WriteAllBytesAsync(Path.Combine(root, "unfeathered-mask.png"), await WicImaging.EncodePngAsync(corrected.Coverage));
                await File.WriteAllBytesAsync(Path.Combine(root, "corrected-mask.png"), await WicImaging.EncodePngAsync(correctedCoverage));
                var capture = new RenderTargetBitmap(); await capture.RenderAsync(CanvasStage);
                var rgba = (await capture.GetPixelsAsync()).ToArray();
                for (int p = 0; p < rgba.Length; p += 4) (rgba[p], rgba[p + 2]) = (rgba[p + 2], rgba[p]);
                await File.WriteAllBytesAsync(Path.Combine(root, "corrected-canvas.png"), await WicImaging.EncodePngAsync(
                    new Surface(capture.PixelWidth, capture.PixelHeight, rgba, capture.PixelWidth * 4)));
                report["correctivePromptsRespectIncludeExcludeAndCanvasCapture"] = "passed";
                CancelObjectSelection();
            }

            var args = new JsonObject { ["mode"] = "object", ["foregroundPoints"] = new JsonArray(new JsonArray(image.Width * .5, image.Height * .5)),
                ["backgroundPoints"] = new JsonArray(new JsonArray(image.Width * .95, image.Height * .95)), ["softEdges"] = false };
            history = scene.CurrentHistoryId;
            await Call(args); await WaitForFramesAsync();
            Require(scene.CurrentHistoryId != history && objectSegmenter.EncoderRuns == encoded, "MCP did not reuse the canvas feature cache.");
            scene.Undo(); Require(scene.CurrentHistoryId == history, "MCP object selection is not one Undo.");
            report["mcpPointCorrectionAndUndo"] = "passed";
            if (verifySoftEdges)
            {
                Require(scene.ActiveSelection?.Feather == 0, "Undo did not restore the image-guided object selection.");
                var softArgs = args.DeepClone().AsObject(); softArgs["softEdges"] = true;
                await Call(softArgs); Require(scene.ActiveSelection?.Feather == 0, "MCP still applied blanket feather by default.");
                scene.Undo();
                softArgs["feather"] = 3;
                await Call(softArgs); Require(scene.ActiveSelection?.Feather == 3, "Explicit MCP feather was ignored.");
                scene.Undo();
                softArgs["feather"] = 0;
                await Call(softArgs); Require(scene.ActiveSelection?.Feather == 0, "Explicit zero feather was ignored.");
                scene.Undo();
                report["mcpSoftDefaultAndExplicitZeroFeather"] = "passed";
            }

            scene.SetSelectionUndoable(null); history = scene.CurrentHistoryId;
            SetSelectionMode(LassoSelectionMode.Subtract);
            await PromptObjectSelectionAsync(new(new(image.Width * .5, image.Height * .5)), null);
            Require(objectSelectionDraft?.Result is { IsEmpty: true } && ConfirmObjectSelection()
                && scene.ActiveSelection is { IsEmpty: true }, "An empty subtraction must protect every pixel, not remove the selection constraint.");
            scene.Undo(); Require(scene.CurrentHistoryId == history && scene.ActiveSelection is null, "Empty subtraction broke Undo.");
            SetSelectionMode(LassoSelectionMode.Replace);
            report["emptySubtractionProtectsAllPixels"] = "passed";

            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            objectSelectionPreparationProbe = gate.Task;
            var pending = PromptObjectSelectionAsync(new(new(100, 100)), null);
            CancelObjectSelection(); gate.SetResult(); await pending;
            Require(objectSelectionDraft is null && scene.CurrentHistoryId == history, "A cancelled draft published late.");
            objectSelectionPreparationProbe = null;
            gate = new(TaskCreationOptions.RunContinuationsAsynchronously); editorAutomationPreparationProbe = gate.Task;
            var stale = Call(args.DeepClone().AsObject());
            OpenDocument(EditorDocument.Create(100, 80)); await WaitForFramesAsync();
            gate.SetResult(); await Refused(stale); editorAutomationPreparationProbe = null;
            Require(document!.ActiveSelection is null && !document.CanUndo, "A retired MCP selection reached the new document.");
            report["cancelAndRetiredMcpPublication"] = "passed";

            var installed = objectSelectionStore;
            objectSelectionStore = new(Path.Combine(root, "missing-model"), ModelStore.MobileSamFiles);
            var missing = new JsonObject { ["mode"] = "object", ["foregroundPoints"] = new JsonArray(new JsonArray(50, 40)) };
            await Refused(Call(missing));
            projectDialogProbe = _ => Task.FromResult(ContentDialogResult.None);
            await PromptObjectSelectionAsync(new(new(50, 40)), null);
            Require(objectSelectionDraft is null && !document.CanUndo && !Directory.Exists(Path.Combine(root, "missing-model")),
                "Missing-model cancellation downloaded files or published selection history.");
            projectDialogProbe = null; objectSelectionStore = installed;
            report["missingModelMcpErrorAndUiCancel"] = "passed";
            if (!directOutlineEnabled) await VerifyOutlineRetirementAsync(report);
            report["backend"] = objectSegmenter.Backend; report["encoderRuns"] = encoded;
            report["status"] = "passed";

            async Task<JsonObject> Call(JsonObject input)
            {
                var state = EditorState(); input["documentToken"] = state["documentToken"]!.DeepClone(); input["revision"] = state["revision"]!.DeepClone();
                return await HandleEditorToolAsync(new() { ["tool"] = "editor_select", ["arguments"] = input.DeepClone() });
            }
            async Task<byte[]> CaptureCanvasAsync()
            {
                var bitmap = new RenderTargetBitmap();
                await bitmap.RenderAsync(CanvasStage);
                return (await bitmap.GetPixelsAsync()).ToArray();
            }
            Task<MaskSurface> SampleCoverageAsync(Selection selection) => Task.Run(() =>
            {
                if (selection.Feather <= 0) return selection.Coverage;
                var mask = new MaskSurface(image.Width, image.Height);
                for (int y = 0; y < mask.Height; y++)
                    for (int x = 0; x < mask.Width; x++) mask.Pixels[y * mask.Width + x] = selection.CoverageAt(x, y);
                return mask;
            });
            static async Task Refused(Task<JsonObject> task)
            {
                try { await task; }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OperationCanceledException) { return; }
                throw new InvalidOperationException("Expected object selection rejection.");
            }
        }
        catch (Exception ex) { report["status"] = "failed"; report["error"] = ex.ToString(); }
        finally
        {
            objectSelectionPreparationProbe = null; objectSelectionRefinementProbe = null;
            editorAutomationPreparationProbe = null; projectDialogProbe = null;
            WriteProbeReport("object-selection-probe.json", report); forceClose = true; Close();
        }
    }
}
#endif
