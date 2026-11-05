#if DEBUG
using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task VerifyBrushPathAsync(Dictionary<string, object> report)
    {
        static EditorDocument Scene(bool opaque = false, bool mask = false)
        {
            var scene = EditorDocument.Create(64, 64); var id = scene.Snapshot.Manifest.Layers[0].Id;
            if (!opaque) scene.ReplaceLayerImage(id, new Surface(64, 64));
            if (mask) { var plane = new MaskSurface(64, 64); plane.Pixels.AsSpan().Fill(255); scene.AddRasterMask(id, plane); }
            return new(scene.Snapshot);
        }
        async Task Tool(CanvasTool target)
        {
            ToolCombo.SelectedIndex = (int)target;
            Require(await WaitUntilAsync(() => tool == target && toolChangeApplied == toolChangeRevision, 2000), "Brush-path tool did not settle.");
            BrushSizeSlider.Value = 1; BrushHardnessSlider.Value = 100; BrushOpacitySlider.Value = 100;
            foregroundColor = Microsoft.UI.Colors.Black;
            SetViewportZoom(1); await WaitForFramesAsync();
        }
        // A blank layer's first stroke commits trimmed to its touched tiles, so the plane's
        // size is no longer the canvas's: read through the layer's published placement.
        static byte Alpha(EditorDocument scene, Guid id, int x, int y)
        {
            if (!scene.Snapshot.Images.TryGetValue(id, out var surface)) return 0; // painting never started
            var transform = scene.Snapshot.Manifest.Layers.First(item => item.Id == id).Transform;
            var point = BrushEngine.DocumentToPixel(transform, surface.Width, surface.Height, new PointD(x + .5, y + .5));
            if (point.X < 0 || point.Y < 0 || point.X >= surface.Width || point.Y >= surface.Height) return 0;
            return surface.Pixels[(int)point.Y * surface.Stride + (int)point.X * 4 + 3];
        }

        var scene = Scene(); OpenDocument(scene); await WaitForFramesAsync(); await Tool(CanvasTool.Brush);
        BrushSmoothingBox.Value = 17; Require(brushSmoothing == 17 && BrushSmoothingSlider.Value == 17, "Smoothing number entry did not bind the slider.");
        BrushSmoothingSlider.Value = 10; Require(brushSmoothing == 10 && BrushSmoothingBox.Value == 10, "Smoothing slider did not bind the field.");
        Require(BrushSmoothingControls.Visibility == Visibility.Visible
            && !string.IsNullOrWhiteSpace(FrameworkElementAutomationPeer.CreatePeerForElement(BrushSmoothingBox)?.GetName()), "Smoothing controls are hidden or unnamed.");
        await Tool(CanvasTool.Clone); Require(BrushSmoothingControls.Visibility == Visibility.Collapsed, "Clone exposed brush-only smoothing.");
        report["smoothingControlsBindAndFollowBrushToolVisibility"] = "passed";

        foreach (var mode in new[] { "brush", "eraser", "mask" })
        {
            bool mask = mode == "mask", eraser = mode == "eraser";
            scene = Scene(eraser || mask, mask); var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
            OpenDocument(scene); await WaitForFramesAsync(); await Tool(eraser ? CanvasTool.Eraser : CanvasTool.Brush);
            if (mask) { HandleThumbnailClick(id, true, 0); await WaitForFramesAsync(); }
            BrushSmoothingSlider.Value = 10;
            var original = scene.Snapshot.Images[id]; var originalMask = scene.Snapshot.Masks.GetValueOrDefault(id); var warm = compositor;
            int Written(int x) => mask ? 255 - scene.Snapshot.Masks[id].Pixels[10 * 64 + x]
                : eraser ? 255 - Alpha(scene, id, x, 10) : Alpha(scene, id, x, 10);
            BeginPointerStroke(new(10.5, 10.5)); AppendPointerStroke(new(16.5, 10.5)); await WaitForFramesAsync();
            Require(pointerStroke?.Path?.LastPainted == new PointD(10.5, 10.5) && Written(15) == 0, "Slack smoothing painted pointer jitter.");
            AppendPointerStroke(new(30.5, 10.5)); await WaitForFramesAsync();
            Require(pointerStroke?.Path?.LastPainted == new PointD(20.5, 10.5) && Written(20) == 255 && Written(29) == 0, "The brush did not trail on its string.");
            FinishPointerStroke(); await WaitForFramesAsync();
            Require(Written(30) == 255 && ReferenceEquals(warm, compositor), "Release did not flush the string or discarded the compositor.");
            AssertUploadedFrame(scene); OnUndo(this, new()); await WaitForFramesAsync();
            Require(!scene.CanUndo && ReferenceEquals(original, scene.Snapshot.Images[id])
                && (!mask || ReferenceEquals(originalMask, scene.Snapshot.Masks[id])), "Smoothed stroke did not undo once.");
            OnRedo(this, new()); await WaitForFramesAsync(); AssertUploadedFrame(scene);
            report[$"{mode}SmoothingRejectsJitterFlushesAndUndoesOnce"] = "passed";
        }

        scene = Scene(); var targetId = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        OpenDocument(scene); await WaitForFramesAsync(); await Tool(CanvasTool.Brush); BrushSmoothingSlider.Value = 0;
        BeginPointerStroke(new(8.5, 8.5)); FinishPointerStroke();
        BeginPointerStroke(new(40.5, 24.5), true); AppendPointerStroke(new(55.5, 26.5), true); AppendPointerStroke(new(46.5, 48.5), true);
        await WaitForFramesAsync();
        Require(Alpha(scene, targetId, 24, 16) == 255 && Alpha(scene, targetId, 46, 40) == 0
            && pointerStroke?.Path?.Pointer == new PointD(46.5, 24.5), "Queued Shift continuation or latched axis failed.");
        AppendPointerStroke(new(46.5, 48.5)); FinishPointerStroke(); await WaitForFramesAsync();
        Require(Alpha(scene, targetId, 46, 40) == 255, "Releasing Shift did not resume freehand at the same pointer coordinate.");
        AssertUploadedFrame(scene); OnUndo(this, new()); await WaitForFramesAsync();
        Require(Alpha(scene, targetId, 24, 16) == 0 && Alpha(scene, targetId, 8, 8) == 255 && scene.CanUndo, "Shift line did not keep its own Undo boundary.");
        OnUndo(this, new()); await WaitForFramesAsync(); Require(!scene.CanUndo, "Queued strokes added extra Undo steps.");
        report["queuedShiftLineLatchesAxisAndResumesFreehand"] = "passed";

        scene = Scene(true); targetId = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        OpenDocument(scene); await WaitForFramesAsync(); await Tool(CanvasTool.Brush);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var release = new ManualResetEventSlim(false))
        {
            beforeFrameRender = () =>
            {
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Brush-path frame control timed out.");
            };
            try
            {
                RenderCanvas(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                BeginPointerStroke(new(8.5, 8.5)); AppendPointerStroke(new(20.5, 8.5)); FinishPointerStroke();
                BeginHealing(new(40.5, 40.5)); Require(healingStroke is not null, "Queued-anchor healing fixture was refused."); CancelHealingStroke();
                BeginPointerStroke(new(40.5, 8.5), true); FinishPointerStroke();
            }
            finally { beforeFrameRender = null; release.Set(); }
            await WaitForFramesAsync();
        }
        Require(scene.Snapshot.Images[targetId].Pixels[8 * 64 * 4 + 30 * 4] == 0, "Cancelling healing severed a queued predecessor's Shift anchor.");
        AssertUploadedFrame(scene); report["cancelledHealingPreservesQueuedStrokeAnchor"] = "passed";

        scene = Scene(); OpenDocument(scene); await WaitForFramesAsync(); await Tool(CanvasTool.Brush);
        BeginPointerStroke(new(8.5, 8.5)); FinishPointerStroke(); await WaitForFramesAsync();
        OnAddBlankLayer(this, new()); await WaitForFramesAsync(); targetId = SelectedLayer!.Id;
        BeginPointerStroke(new(40.5, 24.5), true); FinishPointerStroke(); await WaitForFramesAsync();
        Require(Alpha(scene, targetId, 24, 16) == 0, "Shift line continued from another layer.");
        scene = new(scene.Snapshot); OpenDocument(scene); await WaitForFramesAsync();
        BeginPointerStroke(new(10.5, 50.5), true); FinishPointerStroke(); await WaitForFramesAsync();
        Require(Alpha(scene, targetId, 25, 37) == 0, "Shift line continued across document ownership.");
        scene = Scene(true, true); targetId = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        OpenDocument(scene); await WaitForFramesAsync(); await Tool(CanvasTool.Brush);
        BeginPointerStroke(new(8.5, 8.5)); FinishPointerStroke(); await WaitForFramesAsync(); HandleThumbnailClick(targetId, true, 0); await WaitForFramesAsync();
        BeginPointerStroke(new(40.5, 24.5), true); FinishPointerStroke(); await WaitForFramesAsync();
        Require(scene.Snapshot.Masks[targetId].Pixels[16 * 64 + 24] == 255 && scene.Snapshot.Masks[targetId].Pixels[24 * 64 + 40] == 0, "Shift line crossed image/mask targets.");
        AssertUploadedFrame(scene); report["shiftLineRespectsLayerMaskAndDocumentIdentity"] = "passed";

        scene = Scene(true); targetId = scene.Snapshot.Manifest.ActiveLayerId!.Value; OpenDocument(scene); await WaitForFramesAsync(); await Tool(CanvasTool.Healing);
        BrushSizeSlider.Value = 2; var pixels = scene.Snapshot.Images[targetId];
        BeginHealing(new(8.5, 8.5)); MoveHealing(new(20.5, 10.5)); await WaitForFramesAsync(); CancelHealingStroke(); await WaitForFramesAsync();
        BeginHealing(new(40.5, 25.5), true); await WaitForFramesAsync();
        Require(healingStroke is { } repair && repair.PreviewSnapshot.Images[targetId].Pixels[18 * 64 * 4 + 30 * 4] < 255, "Healing Shift-click did not connect its preview.");
        MoveHealing(new(55.5, 27.5), true); await WaitForFramesAsync();
        Require(healingPath?.Pointer == new PointD(55.5, 25.5), "Healing did not latch the Shift axis.");
        AssertUploadedFrame(new(healingStroke!.PreviewSnapshot)); CancelHealingStroke(); await WaitForFramesAsync();
        Require(!scene.CanUndo && ReferenceEquals(pixels, scene.Snapshot.Images[targetId]), "Healing-path cancellation changed pixels/history.");
        AssertUploadedFrame(scene); report["healingShiftPreviewSharesAxisAndCancelsWithoutHistory"] = "passed";
    }
}
#endif
