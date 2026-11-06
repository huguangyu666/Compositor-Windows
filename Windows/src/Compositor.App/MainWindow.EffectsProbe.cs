#if DEBUG
using Compositor.Core;
using Compositor.Renderer;
using System.Diagnostics;
using System.Text.Json;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private Action? beforeFrameRender;
    private FrameProbe lastFrameProbe;
    private int presentedFrameCount;
    private long pointerPointStamp;
    private double frameWaitMs;
    private readonly record struct FrameProbe(RectI Band, double InputMs, double CpuMs, double UploadMs, long InputBytes, long WorkerBytes,
        double PreparationMs, double CompositionMs, double BgraMs);

    /// <summary>Opt-in synthetic, unsaved WinUI probe. Real queued stroke helpers,
    /// render worker and GPU region uploads; physical pointer/focus acceptance is separate.</summary>
    internal async Task RunEffectsPerformanceProbeAsync()
    {
        var report = new Dictionary<string, object>();
        try
        {
            await WaitForFramesAsync();
            report["mixedAlphaFullFrames"] = await MeasureMixedAlphaFramesAsync();
            report["gradientPublishedBufferSurvivesSupersededFrames"] = await CheckGradientPublishedBuffersAsync();
            report["gradientCommitWaitsForLatestFrame"] = await CheckGradientCommitWaitAsync();
            report["gradientShiftAndHandlePriority"] = await CheckGradientHandleConstraintsAsync();
            report["gradientRepeatedInputsAndCachedRedraw"] = await CheckGradientRepeatedInputsAsync();
            ToolCombo.SelectedIndex = (int)CanvasTool.Eraser;
            BrushSizeSlider.Value = 12;
            BrushHardnessSlider.Value = 50;
            BrushOpacitySlider.Value = 80;
            var correctness = Scene(256, 24);
            OpenDocument(correctness);
            await WaitForFramesAsync();
            var id = correctness.Snapshot.Manifest.Layers[0].Id;
            var original = correctness.Snapshot.Images[id].Pixels.ToArray();

            // Hold the actual reader while release + Undo arrive. The working surface
            // revision must stay fixed until the worker has completed.
            using (var release = new ManualResetEventSlim())
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                beforeFrameRender = () => { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Held frame timed out."); };
                BeginPointerStroke(new(60, 60));
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
                beforeFrameRender = null;
                var working = correctness.Snapshot.Images[id];
                int revision = working.Revision;
                AppendPointerStroke(new(90, 75));
                FinishPointerStroke(new(110, 85));
                OnUndo(this, new Microsoft.UI.Xaml.RoutedEventArgs());
                if (working.Revision != revision) throw new InvalidOperationException("Pointer changed pixels during a render.");
                release.Set();
                await WaitForFramesAsync();
            }
            if (!original.AsSpan().SequenceEqual(correctness.Snapshot.Images[id].Pixels))
                throw new InvalidOperationException("Release/Undo did not restore source pixels.");
            AssertUploadedFrame(correctness);
            OnRedo(this, new Microsoft.UI.Xaml.RoutedEventArgs());
            await WaitForFramesAsync();
            AssertUploadedFrame(correctness);
            report["releaseUndoRedoWhileBusy"] = "passed";

            // A selected row closes the old target, even when its ending input is queued.
            var alternate = new ProjectLayerRecord { Id = Guid.NewGuid(), Name = "Alternate", IsVisible = false,
                ImageFile = "alternate.png", Transform = new() { Size = new(256, 256) } };
            correctness.Snapshot.Manifest.Layers.Add(alternate);
            ((Dictionary<Guid, Surface>)correctness.Snapshot.Images)[alternate.Id] = new Surface(256, 256);
            RefreshLayerPanel();
            ToolCombo.SelectedIndex = (int)CanvasTool.Eraser;
            BeginPointerStroke(new(135, 125));
            AppendPointerStroke(new(155, 145));
            LayerList.SelectedItem = layerRows[alternate.Id];
            await WaitForFramesAsync();
            if (pointerStroke is not null) throw new InvalidOperationException("Layer switch retained a stroke.");
            if (correctness.Snapshot.Images[alternate.Id].Revision != 0) throw new InvalidOperationException("Ending stroke wrote to the new layer.");
            AssertUploadedFrame(correctness);
            report["layerSwitchTargetAndHalo"] = "passed";

            // Capture loss shares this idempotent finish path; exercise two quick strokes
            // before a frame drains them, then restore both history entries.
            LayerList.SelectedItem = layerRows[id];
            BeginPointerStroke(new(160, 80));
            FinishPointerStroke(new(175, 80));
            FinishPointerStroke();
            BeginPointerStroke(new(170, 105));
            FinishPointerStroke(new(185, 105));
            await WaitForFramesAsync();
            AssertUploadedFrame(correctness);
            OnUndo(this, new Microsoft.UI.Xaml.RoutedEventArgs());
            OnUndo(this, new Microsoft.UI.Xaml.RoutedEventArgs());
            await WaitForFramesAsync();
            AssertUploadedFrame(correctness);
            report["quickStrokesAndIdempotentFinish"] = "passed";

            foreach (int mode in new[] { -1, 0, 1, 2 })
            {
                ToolCombo.SelectedIndex = (int)(mode < 0 ? CanvasTool.Clone : CanvasTool.Blur);
                if (mode < 0)
                {
                    cloneSource = new(90, 75);
                    cloneAlignedOffset = new(123, 123);
                    CloneAlignedCheck.IsChecked = false;
                }
                else BlurModeCombo.SelectedIndex = mode;
                BeginPointerStroke(new(115, 90));
                if (mode < 0 && pointerStroke?.CloneOffset != new PointD(-25, -15))
                    throw new InvalidOperationException("Unaligned clone reused a previous stroke's offset.");
                AppendPointerStroke(new(140, 100));
                FinishPointerStroke(new(160, 115));
                await WaitForFramesAsync();
                AssertUploadedFrame(correctness);
                OnUndo(this, new Microsoft.UI.Xaml.RoutedEventArgs());
                await WaitForFramesAsync();
                AssertUploadedFrame(correctness);
            }
            report["cloneBlurSmudgeLiquifyAndUndo"] = "passed";

            var placed = Scene(256, 8);
            var placedLayer = placed.Snapshot.Manifest.Layers[0];
            var placedImage = new Surface(64, 64);
            Array.Fill(placedImage.Pixels, (byte)255);
            ((Dictionary<Guid, Surface>)placed.Snapshot.Images)[placedLayer.Id] = placedImage;
            placedLayer.Transform = new() { Origin = new(64, 64), Size = new(64, 64) };
            OpenDocument(placed);
            await WaitForFramesAsync();
            ToolCombo.SelectedIndex = (int)CanvasTool.Move;
            using (var release = new ManualResetEventSlim())
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                beforeFrameRender = () => { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Held placement timed out."); };
                RenderCanvas();
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
                beforeFrameRender = null;
                placementDragActive = true;
                interactedLayers.Add(placedLayer.Id);
                moveRawBox = new(64, 64, 64, 64);
                pendingMoveDelta = new(25, 12);
                pendingMoveLayerId = placedLayer.Id;
                pendingMoveFree = true;
                FinishPlacementDrag();
                if (placedLayer.Transform.Origin != new PointD(64, 64)) throw new InvalidOperationException("Release moved a layer during rendering.");
                release.Set();
                await WaitForFramesAsync();
            }
            if (placedLayer.Transform.Origin != new PointD(89, 76)) throw new InvalidOperationException("Final move was lost.");
            AssertUploadedFrame(placed);
            OnUndo(this, new Microsoft.UI.Xaml.RoutedEventArgs());
            await WaitForFramesAsync();
            AssertUploadedFrame(placed);
            report["placementReleaseOldAndNewEffects"] = "passed";

            var masked = Scene(256, 8);
            var maskedLayer = masked.Snapshot.Manifest.Layers[0];
            maskedLayer.MaskFile = "mask.png";
            var mask = new MaskSurface(256, 256);
            Array.Fill(mask.Pixels, (byte)255);
            ((Dictionary<Guid, MaskSurface>)masked.Snapshot.Masks)[maskedLayer.Id] = mask;
            OpenDocument(masked);
            await WaitForFramesAsync();
            ToolCombo.SelectedIndex = (int)CanvasTool.Eraser;
            paintOnMask = true;
            BeginPointerStroke(new(90, 90));
            AppendPointerStroke(new(115, 95));
            FinishPointerStroke(new(130, 105));
            await WaitForFramesAsync();
            AssertUploadedFrame(masked);
            OnUndo(this, new Microsoft.UI.Xaml.RoutedEventArgs());
            await WaitForFramesAsync();
            AssertUploadedFrame(masked);
            paintOnMask = false;
            report["maskRegionsAndUndo"] = "passed";

            var controls = Scene(128, 8);
            OpenDocument(controls);
            await WaitForFramesAsync();
            var controlsId = controls.Snapshot.Manifest.Layers[0].Id;
            var controlKey = new object();
            controls.SetOpacity(controlsId, .8, controlKey);
            await WaitForFramesAsync();
            using (var release = new ManualResetEventSlim())
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                beforeFrameRender = () => { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Held control edit timed out."); };
                RenderCanvas();
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
                beforeFrameRender = null;
                var frozen = renderSnapshot!;
                controls.SetOpacity(controlsId, .4, controlKey);
                if (frozen.Manifest.Layers[0].Opacity != .8) throw new InvalidOperationException("Control edited the worker's metadata.");
                release.Set();
                await WaitForFramesAsync();
            }
            AssertUploadedFrame(controls);
            report["controlEditFrozenMetadata"] = "passed";

            ToolCombo.SelectedIndex = (int)CanvasTool.Gradient;
            using (var release = new ManualResetEventSlim())
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                beforeFrameRender = () => { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Held gradient timed out."); };
                int presentedBefore = presentedFrameCount;
                BeginGradient(new(20, 20));
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
                beforeFrameRender = null;
                MoveGradient(new(80, 75));
                release.Set();
                await WaitForFramesAsync();
                if (presentedFrameCount != presentedBefore + 2) throw new InvalidOperationException("Gradient discarded a valid preceding draft frame.");
            }
            CancelGradientDraft();
            await WaitForFramesAsync();
            AssertUploadedFrame(controls);
            report["gradientStreamsPreviousDraftAndCancel"] = "passed";

            // A delayed old-document frame must never upload into the new document.
            using (var release = new ManualResetEventSlim())
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                beforeFrameRender = () => { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Held old document timed out."); };
                RenderCanvas();
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
                beforeFrameRender = null;
                int presentedBefore = presentedFrameCount;
                var next = Scene(128, 8);
                OpenDocument(next);
                release.Set();
                await WaitForFramesAsync();
                if (presentedFrameCount != presentedBefore + 1) throw new InvalidOperationException("Old document was presented.");
                AssertUploadedFrame(next);
            }
            report["staleDocumentFrame"] = "passed";

            // A tool switch closes the open stroke on the far side of the wait it performs for the render
            // worker. With the worker idle that wait never suspends and the switch is finished before the
            // pointer moves; with the worker inside a frame the switch lands a frame or two later and
            // closes whatever gesture opened in between — which is the window the 4K mask sampling loop
            // fell into once under load. Holding a frame is how to stand in that window on demand.
            var toolBeforeRace = tool;
            var raced = Scene(64, 8);
            OpenDocument(raced);
            await WaitForFramesAsync();
            ToolCombo.SelectedIndex = (int)CanvasTool.Brush;
            await WaitForFramesAsync();
            if (tool != CanvasTool.Brush || pointerStroke is not null)
                throw new InvalidOperationException($"The racing fixture did not rest on a closed Brush stroke (tool {tool}, {pointerStrokeClosedBy}).");
            CanvasTool? openedWith = null; string eatenBy = ""; double landedMs = 0; bool survived = false;
            using (var hold = new ManualResetEventSlim())
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                beforeFrameRender = () => { entered.TrySetResult(); if (!hold.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Held switch frame timed out."); };
                RenderCanvas();
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
                beforeFrameRender = null;
                var switchTimer = Stopwatch.StartNew();
                ToolCombo.SelectedIndex = (int)CanvasTool.Eraser;                // the switch suspends here
                BeginPointerStroke(new(20, 20));                                   // the gesture opened in that window
                openedWith = pointerStroke?.Tool;
                // The other half of the same window: the pointer asked which tool to stamp itself with and
                // got the one the user had already left. Field landing before the wait is what makes the
                // surviving stroke belong to the tool that was selected, not to the one being retired.
                if (tool != CanvasTool.Eraser)
                    throw new InvalidOperationException($"The tool switch was still in flight while the stroke opened: tool {tool}, stroke {openedWith}.");
                if (openedWith != CanvasTool.Eraser)
                    throw new InvalidOperationException($"The stroke opened in the switch window is stamped {openedWith}, not the eraser the user picked.");
                var racedFrames = presentedFrameCount;
                hold.Set();
                // Wait for the handler to finish rather than for the field: the field moved before the wait
                // (#38), so watching it would return at once and this group would read "survived" before the
                // teardown that used to eat the stroke ever got the chance to run.
                if (!await WaitForToolSwitchLandedAsync(CanvasTool.Eraser))
                    throw new InvalidOperationException($"The tool switch never returned once the frame was released (tool {tool}, status {StatusText.Text}).");
                landedMs = switchTimer.Elapsed.TotalMilliseconds;
                eatenBy = pointerStrokeClosedBy;
                survived = pointerStroke is not null;
                if (pointerStroke is null)
                    throw new InvalidOperationException($"The tool switch closed the stroke that began after it "
                        + $"(opened as {openedWith}, closed by {eatenBy}, landed after {landedMs:F0} ms).");
                AppendPointerStroke(new(26, 20));
                await WaitForFramesAsync();
                if (presentedFrameCount == racedFrames)
                    throw new InvalidOperationException($"The surviving stroke drew nothing: the append after the switch was a no-op (closed by {pointerStrokeClosedBy}).");
            }
            FinishPointerStroke();
            await WaitForFramesAsync();
            AssertUploadedFrame(raced);
            if (raced.Snapshot.Images[raced.Snapshot.Manifest.Layers[0].Id].Revision == 0)
                throw new InvalidOperationException("The stroke that survived the tool switch never reached the document.");
            report["toolSwitchRaceWindow"] = new { openedWith, survived, landedMs, lastClosureBeforeCheck = eatenBy };
            report["toolSwitchDoesNotCloseAStrrokeOpenedAfterIt"] = "passed";
            // Hand the run back exactly the tool it had, so the measurement sections below still enter
            // the same window this group just tested instead of finding the switch already landed.
            ToolCombo.SelectedIndex = (int)toolBeforeRace;
            await WaitForFramesAsync();


            var rows = new List<object>();
            foreach (int side in new[] { 1024, 2048, 4096 })
            foreach (int blur in new[] { 8, 40 })
            {
                var scene = Scene(side, blur);
                OpenDocument(scene);
                await WaitForFramesAsync();
                var cold = lastFrameProbe;
                ToolCombo.SelectedIndex = (int)CanvasTool.Eraser;
                // #38 made the field move before the switch's own wait, so this is read straight through: a
                // wait on it would return at once and measure nothing, which is worse than no gate at all.
                // Measuring without the tool landing is how this loop used to time the previous tool's stroke.
                if (tool != CanvasTool.Eraser)
                    throw new InvalidOperationException($"The eraser is not the live tool as soon as it is selected (tool {tool}); the stroke below would measure the previous tool.");
                BrushSizeSlider.Value = 12;
                BrushHardnessSlider.Value = 50;
                BrushOpacitySlider.Value = 80;
                // Settle first: the tool switch above waits on the render worker, and a whole-canvas frame
                // that switch leaves behind would otherwise be billed to the first dab. Waiting for the
                // queue and the dirty region to go quiet makes the claim stricter, not looser — what is
                // counted from here is only frames the dab itself can explain.
                if (!await WaitUntilAsync(() => !renderInputs.HasPending && !HasPendingDirty, 2000))
                    throw new InvalidOperationException($"The canvas never went quiet before the first brush tick (pending input {renderInputs.HasPending}, dirty {HasPendingDirty}).");
                var warmCompositor = compositor;
                BeginPointerStroke(new(300, 300));
                await WaitForFramesAsync();
                var strokeStart = lastFrameProbe;
                if (!ReferenceEquals(warmCompositor, compositor))
                    throw new InvalidOperationException("Raster stroke start discarded its warm compositor.");
                if (strokeStart.Band.Width >= 512 || strokeStart.Band.Height >= 512)
                    throw new InvalidOperationException("First dab requested a full-canvas effects frame.");
                AssertUploadedFrame(scene);
                int x = 300;
                for (int i = 0; i < 2; i++) { AppendPointerStroke(new(x += 2, 300)); await WaitForFramesAsync(); }
                var samples = new List<FrameProbe>();
                var dispatchMs = new List<double>();
                for (int i = 0; i < 5; i++)
                {
                    var timer = Stopwatch.StartNew();
                    AppendPointerStroke(new(x += 2, 300));
                    await WaitForFramesAsync();
                    dispatchMs.Add(timer.Elapsed.TotalMilliseconds);
                    samples.Add(lastFrameProbe);
                }
                FinishPointerStroke();
                await WaitForFramesAsync();
                AssertUploadedFrame(scene);
                rows.Add(new { side, blur, coldFrame = cold, strokeStartFrame = strokeStart,
                    steadyRenderAndUploadMedianMs = Median(samples.Select(s => s.CpuMs + s.UploadMs)),
                    steadyRenderAndUploadMaxMs = samples.Max(s => s.CpuMs + s.UploadMs),
                    steadyCpuMedianMs = Median(samples.Select(s => s.CpuMs)),
                    steadyUploadMedianMs = Median(samples.Select(s => s.UploadMs)),
                    workerAllocatedKiB = Median(samples.Select(s => s.WorkerBytes / 1024.0)),
                    steadyInputMedianMs = Median(samples.Select(s => s.InputMs)),
                    steadyCompleteTickMedianMs = Median(samples.Select(s => s.InputMs + s.CpuMs + s.UploadMs)),
                    steadyCompleteTickMaxMs = samples.Max(s => s.InputMs + s.CpuMs + s.UploadMs),
                    inputAndWorkerAllocatedKiB = Median(samples.Select(s => (s.InputBytes + s.WorkerBytes) / 1024.0)),
                    dispatchToIdleMedianMs = Median(dispatchMs), band = samples[^1].Band, samples = samples.Count });
            }
            report["effectFrames"] = rows;
            var maskRows = new List<object>();
            foreach (int side in new[] { 1024, 2048, 4096 })
            foreach (int blur in new[] { 8, 40 })
                await MeasureMaskFrame(side, blur, false);
            await MeasureMaskFrame(1024, 40, true);
            await MeasureMaskFrame(4096, 40, true);
            report["maskFrames"] = maskRows;

            async Task MeasureMaskFrame(int side, int blur, bool apart)
            {
                var scene = Scene(side, blur); var layer = scene.Snapshot.Manifest.Layers[0];
                Array.Fill(scene.Snapshot.Images[layer.Id].Pixels, (byte)255);
                var originalMask = new MaskSurface(side, side); Array.Fill(originalMask.Pixels, (byte)255);
                scene.AddRasterMask(layer.Id, originalMask);
                if (apart) layer.MaskPlacement = new() { Origin = new(70, 80), Size = new(side - 180, side - 140), Rotation = 17, FlipX = true };
                OpenDocument(scene); await WaitForFramesAsync();
                ToolCombo.SelectedIndex = (int)CanvasTool.Eraser;
                if (!await WaitForToolSwitchLandedAsync(CanvasTool.Eraser))
                    throw new InvalidOperationException($"The eraser switch never finished (tool {tool}, status {StatusText.Text}); the mask stroke below would measure the previous tool's workspace.");
                BrushSizeSlider.Value = 12; BrushHardnessSlider.Value = 50; BrushOpacitySlider.Value = 80;
                paintOnMask = true;
                // Settle first: the tool switch above waits on the render worker, and a whole-canvas frame
                // that switch leaves behind would otherwise be billed to the first dab. Waiting for the
                // queue and the dirty region to go quiet makes the claim stricter, not looser — what is
                // counted from here is only frames the dab itself can explain.
                if (!await WaitUntilAsync(() => !renderInputs.HasPending && !HasPendingDirty, 2000))
                    throw new InvalidOperationException($"The canvas never went quiet before the first mask dab (pending input {renderInputs.HasPending}, dirty {HasPendingDirty}).");
                var warmCompositor = compositor; int before = presentedFrameCount;
                int start = apart ? side / 2 : 300;
                BeginPointerStroke(new(start, start)); await WaitForFramesAsync();
                if (presentedFrameCount != before + 1 || !ReferenceEquals(warmCompositor, compositor))
                    throw new InvalidOperationException("Mask first dab lost its regional frame or warm compositor.");
                if (scene.Snapshot.Masks[layer.Id].Revision == 0) throw new InvalidOperationException("Mask sample did not paint inside its grid.");
                var first = lastFrameProbe;
                if (first.Band.Width <= 40 || first.Band.Width >= 512 || first.Band.Height >= 512)
                    throw new InvalidOperationException("Mask first dab omitted its effect halo or requested a full frame.");
                AssertUploadedFrame(scene);
                int x = start;
                for (int i = 0; i < 2; i++) { AppendPointerStroke(new(x += 2, start)); await WaitForFramesAsync(); }
                var samples = new List<FrameProbe>();
                for (int i = 0; i < 5; i++)
                {
                    before = presentedFrameCount; AppendPointerStroke(new(x += 2, start)); await WaitForFramesAsync();
                    if (presentedFrameCount != before + 1)
                        throw new InvalidOperationException($"Mask sample side={side} blur={blur} apart={apart} index={i} "
                            + $"frameAdvance={presentedFrameCount - before} started={pointerStroke?.Started} last={pointerStroke?.Last} "
                            + $"closedBy={pointerStrokeClosedBy} tool={tool} band={lastFrameProbe.Band}.");
                    samples.Add(lastFrameProbe);
                }
                FinishPointerStroke(); await WaitForFramesAsync(); AssertUploadedFrame(scene);
                OnUndo(this, new Microsoft.UI.Xaml.RoutedEventArgs()); await WaitForFramesAsync(); AssertUploadedFrame(scene);
                if (!ReferenceEquals(originalMask, scene.Snapshot.Masks[layer.Id])) throw new InvalidOperationException("Mask Undo changed the original asset.");
                OnRedo(this, new Microsoft.UI.Xaml.RoutedEventArgs()); await WaitForFramesAsync(); AssertUploadedFrame(scene);
                paintOnMask = false;
                maskRows.Add(new { side, blur, apart, firstDabFrame = first,
                    steadyCompleteTickMedianMs = Median(samples.Select(s => s.InputMs + s.CpuMs + s.UploadMs)),
                    steadyCompleteTickMaxMs = samples.Max(s => s.InputMs + s.CpuMs + s.UploadMs),
                    inputAndWorkerAllocatedKiB = Median(samples.Select(s => (s.InputBytes + s.WorkerBytes) / 1024.0)),
                    band = samples[^1].Band, samples = samples.Count });
            }
            var blurRows = new List<object>();
            foreach (int side in new[] { 1024, 2048, 4096 })
            foreach (double sigma in new[] { 3.0, 10 })
            {
                var scene = Scene(side, 0); var image = scene.Snapshot.Images[scene.Snapshot.Manifest.Layers[0].Id];
                for (int y = 0; y < side; y++)
                for (int column = 0; column < side; column++)
                {
                    int index = y * image.Stride + column * 4;
                    byte value = (byte)(((column / 8 + y / 8) & 1) == 0 ? 40 : 200);
                    image.Pixels[index] = value; image.Pixels[index + 1] = (byte)(255 - value);
                    image.Pixels[index + 2] = 100; image.Pixels[index + 3] = 255;
                }
                OpenDocument(scene); await WaitForFramesAsync();
                ToolCombo.SelectedIndex = (int)CanvasTool.Blur; BlurModeCombo.SelectedIndex = 0;
                if (tool != CanvasTool.Blur)
                    throw new InvalidOperationException($"The blur is not the live tool as soon as it is selected (tool {tool}); the stroke below would measure the previous tool.");
                BrushSizeSlider.Value = 12; BrushHardnessSlider.Value = 50; BrushOpacitySlider.Value = 80;
                BlurRadiusBox.Value = sigma;
                // Settle first, as the brush and mask ticks above do: the tool switch and the three sliders
                // leave work behind, and a whole-canvas frame from that backlog would otherwise be billed to
                // the first warmup append. Waiting for the queue and the dirty region to go quiet makes the
                // claim stricter, not looser — what is counted from here is only frames the stroke explains.
                if (!await WaitUntilAsync(() => !renderInputs.HasPending && !HasPendingDirty, 2000))
                    throw new InvalidOperationException($"The canvas never went quiet before the first blur tick (pending input {renderInputs.HasPending}, dirty {HasPendingDirty}).");
                BeginPointerStroke(new(300, 300)); await WaitForFramesAsync();
                var first = lastFrameProbe; AssertUploadedFrame(scene);
                int x = 300;
                for (int i = 0; i < 2; i++)
                {
                    int before = presentedFrameCount;
                    AppendPointerStroke(new(x += 6, 300)); await WaitForFramesAsync();
                    if (presentedFrameCount != before + 1) throw new InvalidOperationException($"Blur warmup frame advance={presentedFrameCount - before}, radius={pointerStroke?.Brush.Radius}, target={x}.");
                }
                var samples = new List<FrameProbe>();
                for (int i = 0; i < 5; i++)
                {
                    int before = presentedFrameCount;
                    AppendPointerStroke(new(x += 6, 300)); await WaitForFramesAsync();
                    if (presentedFrameCount != before + 1) throw new InvalidOperationException("Blur sample did not produce a fresh frame.");
                    samples.Add(lastFrameProbe);
                }
                int beforeRefill = presentedFrameCount;
                AppendPointerStroke(new(x + 40, 300)); await WaitForFramesAsync();
                if (presentedFrameCount != beforeRefill + 1) throw new InvalidOperationException("Moving blur window did not produce a fresh frame.");
                var refill = lastFrameProbe; AssertUploadedFrame(scene);
                FinishPointerStroke(); await WaitForFramesAsync(); AssertUploadedFrame(scene);
                blurRows.Add(new { side, sigma, firstDabFrame = first, movingWindowFrame = refill,
                    steadyCompleteTickMedianMs = Median(samples.Select(s => s.InputMs + s.CpuMs + s.UploadMs)),
                    steadyCompleteTickMaxMs = samples.Max(s => s.InputMs + s.CpuMs + s.UploadMs),
                    inputAndWorkerAllocatedKiB = Median(samples.Select(s => (s.InputBytes + s.WorkerBytes) / 1024.0)),
                    band = samples[^1].Band, samples = samples.Count });
            }
            report["blurFrames"] = blurRows;
            report["largeGradient"] = await MeasureLargeGradientAsync();
            var guard = Scene(1024, 0);
            OpenDocument(guard);
            await WaitForFramesAsync();
            ToolCombo.SelectedIndex = (int)CanvasTool.Brush;
            BrushSizeSlider.Value = 12;
            BrushHardnessSlider.Value = 50;
            BrushOpacitySlider.Value = 80;
            BeginPointerStroke(new(300, 300));
            await WaitForFramesAsync();
            var guardTimes = new List<double>();
            for (int i = 0; i < 7; i++)
            {
                AppendPointerStroke(new(302 + i * 2, 300));
                await WaitForFramesAsync();
                if (i >= 2) guardTimes.Add(lastFrameProbe.InputMs + lastFrameProbe.CpuMs + lastFrameProbe.UploadMs);
            }
            FinishPointerStroke();
            report["noEffectBrushCompleteTickMedianMs"] = Median(guardTimes);
            report["noEffectBrushCompleteTickMaxMs"] = guardTimes.Max();
            report["status"] = "passed";
        }
        catch (Exception ex) { report["status"] = "failed"; report["error"] = ex.ToString(); App.Log($"Effects probe failed: {ex}"); }
        finally
        {
            beforeFrameRender = null;
            WriteProbeReport("effects-performance.json", report, includeFields: true);
            forceClose = true;
            Close();
        }
    }

    private async Task<string> CheckGradientPublishedBuffersAsync()
    {
        foreach (bool mask in new[] { false, true })
        {
            var scene = EditorDocument.Create(256, 256); var id = scene.Snapshot.Manifest.Layers[0].Id;
            if (mask)
            {
                scene.Snapshot.Manifest.Layers[0].MaskFile = "mask.png";
                ((Dictionary<Guid, MaskSurface>)scene.Snapshot.Masks)[id] = MaskSurface.Solid(true);
            }
            OpenDocument(scene); await WaitForFramesAsync();
            ToolCombo.SelectedIndex = (int)CanvasTool.Gradient; await WaitForFramesAsync();
            paintOnMask = mask; GradientMaskCheck.IsChecked = mask;
            foregroundColor = Microsoft.UI.Colors.Black; backgroundColor = Microsoft.UI.Colors.White;
            GradientShapeCombo.SelectedIndex = 0; GradientStyleCombo.SelectedIndex = 1;
            GradientReverseCheck.IsChecked = false; GradientOpacitySlider.Value = 73;
            var start = new PointD(20.5, 20.5); var end = new PointD(230.5, 220.5);
            BeginGradient(start); MoveGradient(end); EndGradientDrag(); await WaitForFramesAsync();
            var published = gradientRenderedPreview ?? throw new InvalidOperationException("The protected gradient fixture had no pixels.");
            byte[] pixels = (byte[])(mask ? published.Snapshot.Masks[id].Pixels : published.Snapshot.Images[id].Pixels).Clone();
            using var releaseFirst = new ManualResetEventSlim(); using var releaseSecond = new ManualResetEventSlim();
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            beforeFrameRender = () => { first.TrySetResult(); if (!releaseFirst.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("First leased gradient timed out."); };
            try
            {
                BeginGradient(end); MoveGradient(new(211.5, 201.5)); await first.Task.WaitAsync(TimeSpan.FromSeconds(10));
                beforeFrameRender = () => { second.TrySetResult(); if (!releaseSecond.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Second leased gradient timed out."); };
                MoveGradient(new(192.5, 182.5)); releaseFirst.Set(); await second.Task.WaitAsync(TimeSpan.FromSeconds(10));
                beforeFrameRender = null;
                Require(ReferenceEquals(published, gradientRenderedPreview), "A superseded frame replaced the protected cache entry.");
                MoveGradient(end); EndGradientDrag();
                Require(await ApplyGradientDraftAsync().WaitAsync(TimeSpan.FromSeconds(2)), "Returning to the published input could not commit while another frame was held.");
                Require(ReferenceEquals(mask ? published.Snapshot.Masks[id] : (object)published.Snapshot.Images[id],
                    mask ? scene.Snapshot.Masks[id] : scene.Snapshot.Images[id]), "Apply copied the protected frame instead of transferring its ownership.");
                releaseSecond.Set(); await WaitForFramesAsync();
                Require(pixels.AsSpan().SequenceEqual(mask ? scene.Snapshot.Masks[id].Pixels : scene.Snapshot.Images[id].Pixels),
                    "A retired worker overwrote committed gradient pixels.");
                AssertUploadedFrame(scene);
                Require(scene.Undo() && !scene.CanUndo && scene.Redo(), "Protected gradient commit did not preserve one-step Undo/Redo.");
                await WaitForFramesAsync(); AssertUploadedFrame(scene);
                Require(pixels.AsSpan().SequenceEqual(mask ? scene.Snapshot.Masks[id].Pixels : scene.Snapshot.Images[id].Pixels),
                    "Retired gradient work corrupted retained history.");
            }
            finally { beforeFrameRender = null; releaseFirst.Set(); releaseSecond.Set(); }
        }
        return "passed";
    }

    private async Task<string> CheckGradientCommitWaitAsync()
    {
        foreach (string action in new[] { "apply", "cancel", "replace", "mask", "thumbnail", "tool", "layer", "import" })
        {
            var scene = EditorDocument.Create(256, 256);
            var id = scene.Snapshot.Manifest.Layers[0].Id; var original = scene.Snapshot.Images[id];
            var alternate = Guid.NewGuid();
            if (action == "mask")
            {
                scene.Snapshot.Manifest.Layers[0].MaskFile = "mask.png";
                ((Dictionary<Guid, MaskSurface>)scene.Snapshot.Masks)[id] = MaskSurface.Solid(true);
            }
            if (action is "thumbnail" or "layer")
            {
                scene.Snapshot.Manifest.Layers.Add(new() { Id = alternate, Name = "Alternate", ImageFile = "alternate.png",
                    IsVisible = false, Transform = new() { Size = new(256, 256) } });
                ((Dictionary<Guid, Surface>)scene.Snapshot.Images)[alternate] = new Surface(256, 256);
            }
            OpenDocument(scene); await WaitForFramesAsync();
            ToolCombo.SelectedIndex = (int)CanvasTool.Gradient; await WaitForFramesAsync();
            paintOnMask = false; GradientMaskCheck.IsChecked = false;
            foregroundColor = Microsoft.UI.Colors.Black; backgroundColor = Microsoft.UI.Colors.White;
            GradientShapeCombo.SelectedIndex = 0; GradientStyleCombo.SelectedIndex = 1;
            GradientReverseCheck.IsChecked = false; GradientOpacitySlider.Value = 73;
            var start = new PointD(20.5, 20.5); var end = new PointD(230.5, 220.5);
            BeginGradient(start); MoveGradient(end); EndGradientDrag(); await WaitForFramesAsync();
            var latest = new PointD(211.5, 201.5);
            var expected = scene.BeginGradient(id)!.Preview(start, latest, CurrentGradientSettings)!;
            string? importPath = null; Task<FileImportResult>? importing = null;
            if (action == "import")
            {
                importPath = Path.Combine(AppContext.BaseDirectory, $"gradient-import-{Guid.NewGuid():N}.png");
                await File.WriteAllBytesAsync(importPath, await WicImaging.EncodePngAsync(new Surface(2, 2)));
            }
            using var release = new ManualResetEventSlim();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            beforeFrameRender = () => { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Held gradient commit timed out."); };
            try
            {
                BeginGradient(end); MoveGradient(latest); EndGradientDrag();
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(20)); beforeFrameRender = null;
                if (action == "mask") ChangePaintTarget(true);
                if (action == "thumbnail") HandleThumbnailClick(alternate, false, Windows.System.VirtualKeyModifiers.None);
                if (action == "tool") ToolCombo.SelectedIndex = (int)CanvasTool.Brush;
                if (action == "layer") LayerList.SelectedItem = layerRows[alternate];
                if (action == "import") importing = ImportFilesAsync(scene, [importPath!]);
                var applying = ApplyGradientDraftAsync();
                Require(!applying.IsCompleted && !scene.CanUndo && gradientEdit is not null,
                    "Apply recalculated/committed the gradient on the UI thread instead of awaiting the existing frame.");
                Require(ReferenceEquals(applying, ApplyGradientDraftAsync()), "Repeated Apply started another commit.");
                Require(!GradientApplyButton.IsEnabled && GradientCancelButton.IsEnabled && !ToolCombo.IsEnabled && !LayerList.IsEnabled,
                    "Waiting gradient commit did not expose its busy state or keep Cancel available.");
                var heartbeat = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                DispatcherQueue.TryEnqueue(() => heartbeat.TrySetResult()); await heartbeat.Task.WaitAsync(TimeSpan.FromSeconds(2));
                int revision = gradientRevision; MoveGradient(new(latest.X - 1, latest.Y));
                Require(gradientRevision == revision, "A finishing gradient still accepted endpoint changes.");
                if (action == "cancel")
                {
                    CancelGradientDraft(); Require(!await applying.WaitAsync(TimeSpan.FromSeconds(2)), "Cancel did not release the commit waiter.");
                    Require(!scene.CanUndo && ReferenceEquals(original, scene.Snapshot.Images[id]), "Cancel committed unfinished gradient pixels.");
                }
                else if (action == "replace")
                {
                    var replacement = EditorDocument.Create(32, 24); OpenDocument(replacement);
                    Require(!await applying.WaitAsync(TimeSpan.FromSeconds(2)), "Document replacement did not release the commit waiter.");
                    Require(!replacement.IsDirty && !replacement.CanUndo && !scene.CanUndo, "A retired commit changed a document.");
                }
                else
                {
                    release.Set(); Require(await applying.WaitAsync(TimeSpan.FromSeconds(10)), "The latest gradient frame could not commit.");
                    if (importing is not null)
                    {
                        var imported = await importing.WaitAsync(TimeSpan.FromSeconds(10));
                        Require(imported.ImportedFiles == 1 && !imported.Retired && imported.Failures.Count == 0,
                            "Image import did not follow the gradient commit.");
                    }
                    await WaitForFramesAsync(); AssertUploadedFrame(scene);
                    if (action == "mask") Require(paintOnMask, "The mask-target continuation did not follow the gradient commit.");
                    if (action is "thumbnail" or "layer") Require(SelectedLayer?.Id == alternate, "The selected-layer continuation did not follow the gradient commit.");
                    if (action == "tool") Require(tool == CanvasTool.Brush, "The tool continuation did not follow the gradient commit.");
                    Require(expected.Snapshot.Images[id].Pixels.AsSpan().SequenceEqual(scene.Snapshot.Images[id].Pixels),
                        "Apply used the previous endpoints instead of the awaited frame.");
                    if (action == "import") Require(scene.LayerCount == 2 && scene.Undo() && scene.CanUndo,
                        "Image import was not a separate Undo step after the gradient.");
                    Require(scene.Undo() && !scene.CanUndo && ReferenceEquals(original, scene.Snapshot.Images[id]), "Apply did not create exactly one Undo step.");
                }
            }
            finally { beforeFrameRender = null; release.Set(); if (importPath is not null) File.Delete(importPath); }
            await WaitForFramesAsync();
            Require(ToolCombo.IsEnabled && LayerList.IsEnabled && StatusProgress.Visibility != Microsoft.UI.Xaml.Visibility.Visible,
                "Gradient completion left editing disabled.");
            AssertUploadedFrame(document!);
        }
        return "passed";
    }

    private async Task<string> CheckGradientHandleConstraintsAsync()
    {
        var scene = EditorDocument.Create(256, 256);
        var id = scene.Snapshot.Manifest.Layers[0].Id;
        OpenDocument(scene); await WaitForFramesAsync();
        ToolCombo.SelectedIndex = (int)CanvasTool.Gradient; await WaitForFramesAsync();
        paintOnMask = false; GradientMaskCheck.IsChecked = false;
        GradientShapeCombo.SelectedIndex = 0; GradientStyleCombo.SelectedIndex = 1;
        GradientReverseCheck.IsChecked = false; GradientOpacitySlider.Value = 100;
        var start = new PointD(64, 64); var rawEnd = new PointD(144, 84);
        BeginGradient(start); MoveGradient(rawEnd, Windows.System.VirtualKeyModifiers.Shift);
        double length = Math.Sqrt(80 * 80 + 20 * 20);
        Require(Distance(gradientEnd, new(start.X + length, start.Y)) < 1e-9 && gradientStart == start,
            "Shift end drag did not snap to 45-degree steps while retaining its length and opposite anchor.");
        MoveGradient(rawEnd);
        Require(gradientEnd == rawEnd, "Releasing Shift during a drag did not restore free movement.");
        MoveGradient(rawEnd, Windows.System.VirtualKeyModifiers.Shift);
        var end = gradientEnd; int revision = gradientRevision; EndGradientDrag(); await WaitForFramesAsync();
        Require(gradientEnd == end && gradientRevision == revision, "Releasing the gradient changed the last snapped point.");
        BeginGradient(start); var rawStart = new PointD(end.X - 50, end.Y - 45);
        MoveGradient(rawStart, Windows.System.VirtualKeyModifiers.Shift);
        double diagonal = Math.Sqrt((50 * 50 + 45 * 45) / 2d);
        Require(gradientEnd == end && Distance(gradientStart, new(end.X - diagonal, end.Y - diagonal)) < 1e-9,
            "Shift start drag used the moving point as its anchor.");
        EndGradientDrag(); await WaitForFramesAsync();
        var expected = scene.BeginGradient(id)!.Preview(gradientStart, gradientEnd, CurrentGradientSettings)!;
        AssertUploadedFrame(new EditorDocument(expected.Snapshot));
        GradientShapeCombo.SelectedIndex = 1; await WaitForFramesAsync();
        expected = scene.BeginGradient(id)!.Preview(gradientStart, gradientEnd, CurrentGradientSettings)!;
        AssertUploadedFrame(new EditorDocument(expected.Snapshot));
        CancelGradientDraft(); await WaitForFramesAsync();
        foreach (double pointsPerPixel in new[] { .5, 2d })
        {
            viewport.SetZoom(pointsPerPixel * viewport.BackingScale, viewport.Center);
            BeginGradient(new(64, 64)); MoveGradient(new(144, 64)); EndGradientDrag(); await WaitForFramesAsync();
            BeginGradient(new(144, 64 + 9.5 / pointsPerPixel));
            Require(gradientDragHandle == 1, "The end handle did not retain its 10-point hit radius across zoom.");
            EndGradientDrag(); CancelGradientDraft(); await WaitForFramesAsync();
            BeginGradient(new(64, 64)); MoveGradient(new(65, 64)); EndGradientDrag(); await WaitForFramesAsync();
            BeginGradient(new(64, 64));
            Require(gradientDragHandle == 1, "Overlapping gradient handles did not prefer the end handle.");
            EndGradientDrag(); CancelGradientDraft(); await WaitForFramesAsync();
        }
        Require(!scene.IsDirty && !scene.CanUndo, "Gradient handle edits or cancellation wrote history.");
        return "passed";
    }

    private async Task<string> CheckGradientRepeatedInputsAsync()
    {
        var scene = EditorDocument.Create(512, 512);
        var id = scene.Snapshot.Manifest.Layers[0].Id;
        OpenDocument(scene); await WaitForFramesAsync();
        ToolCombo.SelectedIndex = (int)CanvasTool.Gradient; await WaitForFramesAsync();
        paintOnMask = false; GradientMaskCheck.IsChecked = false;
        foregroundColor = Microsoft.UI.Colors.Black; backgroundColor = Microsoft.UI.Colors.White;
        GradientShapeCombo.SelectedIndex = 0; GradientStyleCombo.SelectedIndex = 1;
        GradientReverseCheck.IsChecked = false; GradientOpacitySlider.Value = 100;
        var start = new PointD(20.5, 20.5); var end = new PointD(480.5, 460.5);
        BeginGradient(start); MoveGradient(end); EndGradientDrag(); await WaitForFramesAsync();
        var preview = gradientRenderedPreview ?? throw new InvalidOperationException("The repeated-input fixture has no gradient.");
        int revision = gradientRevision, frames = presentedFrameCount;
        for (int repeat = 0; repeat < 16; repeat++)
        {
            foreach (var point in new[] { start, end })
            {
                BeginGradient(point); MoveGradient(point); EndGradientDrag();
            }
            OnGradientOptionsChanged(this, new()); RefreshPaletteChips();
        }
        await WaitForFramesAsync();
        Require(gradientRevision == revision && presentedFrameCount == frames && ReferenceEquals(preview, gradientRenderedPreview),
            "Repeated unchanged gradient input scheduled another preview/frame.");
        using (var release = new ManualResetEventSlim())
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            beforeFrameRender = () => { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Held gradient redraw timed out."); };
            try
            {
                RenderCanvas(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
                BeginGradient(end); MoveGradient(end); EndGradientDrag();
            }
            finally { beforeFrameRender = null; release.Set(); }
            await WaitForFramesAsync();
        }
        Require(presentedFrameCount == frames + 1 && ReferenceEquals(preview, gradientRenderedPreview),
            "A canvas redraw recalculated the unchanged gradient or repeated input queued an extra frame.");
        AssertUploadedFrame(new EditorDocument(preview.Snapshot));
        GradientOpacitySlider.Value = 37; await WaitForFramesAsync();
        Require(!ReferenceEquals(preview, gradientRenderedPreview), "Changed gradient settings reused stale pixels.");
        var expected = scene.BeginGradient(id)!.Preview(start, end, CurrentGradientSettings)!;
        AssertUploadedFrame(new EditorDocument(expected.Snapshot));
        BeginGradient(end); end = new(end.X - 19, end.Y - 7); MoveGradient(end); await WaitForFramesAsync();
        var moved = gradientRenderedPreview; revision = gradientRevision; frames = presentedFrameCount;
        EndGradientDrag(); await WaitForFramesAsync();
        Require(gradientRevision == revision && presentedFrameCount == frames && ReferenceEquals(moved, gradientRenderedPreview),
            "Releasing an already rendered endpoint recomputed it.");
        CancelGradientDraft(); await WaitForFramesAsync(); AssertUploadedFrame(scene);
        BeginGradient(start); MoveGradient(end); EndGradientDrag(); await WaitForFramesAsync();
        Require(!ReferenceEquals(moved, gradientRenderedPreview), "A new gradient draft reused the retired draft's preview.");
        Require(await ApplyGradientDraftAsync(), "Applying the settled gradient failed.");
        await WaitForFramesAsync(); AssertUploadedFrame(scene);
        Require(scene.Undo() && !scene.CanUndo, "The settled gradient did not commit as one Undo step.");
        await WaitForFramesAsync(); AssertUploadedFrame(scene);
        return "passed";
    }

    private async Task<object> MeasureLargeGradientAsync()
    {
        var scene = EditorDocument.Create(4096, 4096);
        var id = scene.Snapshot.Manifest.Layers[0].Id;
        var original = scene.Snapshot.Images[id];
        OpenDocument(scene); await WaitForFramesAsync();
        ToolCombo.SelectedIndex = (int)CanvasTool.Gradient; await WaitForFramesAsync();
        paintOnMask = false; GradientMaskCheck.IsChecked = false;
        foregroundColor = Microsoft.UI.Colors.Black; backgroundColor = Microsoft.UI.Colors.White;
        GradientShapeCombo.SelectedIndex = 0; GradientStyleCombo.SelectedIndex = 1;
        GradientReverseCheck.IsChecked = false; GradientOpacitySlider.Value = 100;
        var start = new PointD(20.5, 20.5); var end = new PointD(4070.5, 4000.5);
        BeginGradient(start); MoveGradient(end); EndGradientDrag(); await WaitForFramesAsync();
        var first = lastFrameProbe;
        var samples = new List<FrameProbe>();
        for (int frame = 0; frame < 5; frame++)
        {
            int before = presentedFrameCount;
            BeginGradient(end); end = new(end.X - 7, end.Y - 3); MoveGradient(end); EndGradientDrag();
            await WaitForFramesAsync();
            Require(presentedFrameCount > before && gradientRenderedRevision == gradientRevision,
                "The 4K gradient did not present its latest endpoints.");
            samples.Add(lastFrameProbe);
        }
        var retained = gradientRenderedPreview;
        Require(Median(samples.Select(s => (double)s.WorkerBytes)) < 65_536,
            "Warm 4K gradient frames still allocate a document-sized preview.");
        RenderCanvas(); await WaitForFramesAsync();
        Require(ReferenceEquals(retained, gradientRenderedPreview), "The 4K redraw rebuilt an unchanged gradient.");
        var unchangedRedraw = lastFrameProbe;
        var expected = scene.BeginGradient(id)!.Preview(start, end, CurrentGradientSettings)!;
        var rendered = gradientRenderedPreview ?? throw new InvalidOperationException("The 4K gradient had no rendered pixels.");
        Require(expected.Snapshot.Images[id].Pixels.AsSpan().SequenceEqual(rendered.Snapshot.Images[id].Pixels),
            "The 4K gradient differs from its endpoint settings.");
        AssertUploadedFrame(new EditorDocument(expected.Snapshot));
        Require(!scene.IsDirty && !scene.CanUndo && ReferenceEquals(original, scene.Snapshot.Images[id]),
            "Dragging a 4K gradient modified the source or history.");
        CancelGradientDraft(); await WaitForFramesAsync(); AssertUploadedFrame(scene);
        BeginGradient(start); MoveGradient(end); EndGradientDrag(); await WaitForFramesAsync();
        Require(await ApplyGradientDraftAsync(), "The 4K gradient could not commit.");
        await WaitForFramesAsync(); AssertUploadedFrame(scene);
        Require(scene.CanUndo, "The committed 4K gradient had no history.");
        OnUndo(this, new Microsoft.UI.Xaml.RoutedEventArgs()); await WaitForFramesAsync(); AssertUploadedFrame(scene);
        Require(!scene.CanUndo && ReferenceEquals(original, scene.Snapshot.Images[id]), "The 4K gradient did not undo in one step.");
        OnRedo(this, new Microsoft.UI.Xaml.RoutedEventArgs()); await WaitForFramesAsync(); AssertUploadedFrame(scene);
        return new { firstFrame = first, unchangedRedraw, steadyPreparationMedianMs = Median(samples.Select(s => s.PreparationMs)),
            steadyCompleteTickMedianMs = Median(samples.Select(s => s.InputMs + s.CpuMs + s.UploadMs)),
            workerAllocatedMiB = Median(samples.Select(s => s.WorkerBytes / 1048576d)), samples = samples.Count,
            gpuCancelCommitUndoRedo = "passed" };
    }

    private async Task WaitForFramesAsync()
    {
        var timer = Stopwatch.StartNew();
        while (workerBusy || HasPendingInteraction || HasPendingDirty || IsGradientFinishing || (directOutlineEnabled && (directOutlineWorkerBusy || pendingDirectOutline is not null)))
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Frame did not become idle.");
            await Task.Delay(1);
        }
    }

    private void AssertUploadedFrame(EditorDocument owner)
    {
        var expected = new CanvasCompositor(owner.Snapshot).Render();
        var bytes = new byte[expected.Width * expected.Height * 4];
        SurfacePresentation.FillBgra(expected, bytes, expected.Width, 0, 0, expected.Width, expected.Height);
        if (presented is null || !bytes.AsSpan().SequenceEqual(presented.GetPixelBytes()))
            throw new InvalidOperationException("Uploaded GPU frame differs from full composition.");
    }

    private static EditorDocument Scene(int side, int blur)
    {
        var scene = EditorDocument.Create(side, side);
        if (blur > 0) scene.SetLayerEffects(scene.Snapshot.Manifest.Layers[0].Id,
            new() { Shadow = new() { Blur = blur, Distance = 20 } });
        return scene;
    }

    private static double Median(IEnumerable<double> values) { var items = values.Order().ToArray(); return items[items.Length / 2]; }
}
#endif
