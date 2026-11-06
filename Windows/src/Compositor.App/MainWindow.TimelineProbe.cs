#if DEBUG
using Compositor.Core;
using Microsoft.UI.Xaml;
using System.Text.Json.Nodes;
using Windows.Graphics;

namespace Compositor.App;

public sealed partial class MainWindow
{
    internal async Task RunTimelineProbeAsync()
    {
        var report = new Dictionary<string, object>();
        try
        {
            string root = Environment.GetEnvironmentVariable("COMPOSITOR_PREFERENCES_DIRECTORY")
                ?? throw new InvalidOperationException("Use isolated preferences for the timeline probe.");
            Directory.CreateDirectory(root); await WaitForFramesAsync();
            var scene = EditorDocument.Create(640, 400);
            scene.FillPixels(scene.Snapshot.Manifest.Layers[0].Id, 18, 25, 40);
            scene.AddTextLayer(new LayerTextStyle { Content = "LAYERS IN MOTION", FontName = "Segoe UI", FontSize = 32,
                Red = .88, Green = .93, Blue = 1 }, new(36, 32));
            var tile = new Surface(80, 80);
            for (int p = 0; p < tile.Pixels.Length; p += 4)
            { tile.Pixels[p] = 255; tile.Pixels[p + 1] = 176; tile.Pixels[p + 2] = 80; tile.Pixels[p + 3] = 255; }
            var moving = scene.ImportImage(tile, "Animated square");
            moving.Transform.Origin = new(60, 200);
            OpenDocument(scene); await WaitForFramesAsync();
            ShowTimeline(true); TimelineChange(() => scene.SetAnimationSettings(120, 24, true));
            Require(TimelinePreviewActive && !CanvasHost.IsHitTestVisible && !OpacitySlider.IsEnabled,
                "Animation preview did not protect base editing.");
            AddTimelineKey();
            SeekTimeline(48);
            timelineFields[AnimationProperty.PositionX].Value = 500;
            timelineFields[AnimationProperty.ScaleX].Value = 150;
            timelineFields[AnimationProperty.ScaleY].Value = 150;
            timelineFields[AnimationProperty.Rotation].Value = 180;
            timelineFields[AnimationProperty.Opacity].Value = 55;
            CommitTimelineFields(); await WaitForFramesAsync();
            var track = scene.Snapshot.Manifest.Timeline!.Tracks.Single();
            Require(track.PositionX.Count == 2 && track.PositionX[1].Value == 500 && track.Opacity[1].Value == .55,
                "Editing the keyframe fields did not persist the requested keys.");
            report["keyframeFieldsAndPreviewProtection"] = "passed";

            string source = ManifestJson.Serialize(scene.Snapshot.Manifest); var history = scene.CurrentHistoryId;
            SeekTimeline(24); await WaitForFramesAsync();
            var evaluated = AnimationEvaluator.Evaluate(scene.Snapshot, 24);
            var expected = new CanvasCompositor(evaluated).Render();
            Require(presented is not null, "The canvas has no GPU presentation.");
            var gpu = presented!.GetPixelBytes();
            int[] samples = [((220 * 640) + 300) * 4, ((20 * 640) + 20) * 4];
            foreach (int p in samples)
                Require(gpu[p] == expected.Pixels[p + 2] && gpu[p + 1] == expected.Pixels[p + 1]
                    && gpu[p + 2] == expected.Pixels[p] && gpu[p + 3] == expected.Pixels[p + 3], "GPU preview differs from the evaluated frame.");
            report["playbackBefore"] = new { CanEditTimeline, ProjectEditsInFlight, workerBusy, timelineFrame,
                renamingLayer, ai = aiEditor is not null, selection = selectionWorkResult is not null, pending = renderInputs.HasPending };
            ToggleTimelinePlayback();
            bool started = timelinePlaying;
            await WaitUntilAsync(() => timelineFrame > 24 || !timelinePlaying, 3000);
            report["playbackAfter"] = new { started, timelinePlaying, timelineFrame, workerBusy,
                sameSource = source == ManifestJson.Serialize(scene.Snapshot.Manifest), sameHistory = history == scene.CurrentHistoryId };
            StopTimelinePlayback(); await WaitForFramesAsync();
            Require(timelineFrame > 24 && source == ManifestJson.Serialize(scene.Snapshot.Manifest) && history == scene.CurrentHistoryId,
                "Playback changed source metadata/history or did not advance.");
            report["gpuFrameAndPlaybackWithoutHistory"] = "passed";

            timelineIn.Value = .5; timelineOut.Value = 4;
            Require(track != scene.Snapshot.Manifest.Timeline.Tracks[0], "Clip edit reused mutable timeline metadata.");
            track = scene.Snapshot.Manifest.Timeline.Tracks[0];
            Require(track.InFrame == 12 && track.OutFrame == 96, "Clip fields ignored seconds or the exclusive out point.");
            var stationaryOut = (moving.Id, "out", 12, 96, -1, 96, TimelineX(96), 1u);
            Require(TimelineDragClip(stationaryOut) == (12, 96), "Clicking a trim edge without dragging changed the clip.");
            report["clipFieldsAndStationaryTrim"] = "passed";

            async Task<JsonObject> Call(string name, JsonObject? args = null)
            {
                args ??= new();
                if (name != "editor_preview")
                {
                    var state = EditorState(); args["documentToken"] = state["documentToken"]!.DeepClone(); args["revision"] = state["revision"]!.DeepClone();
                }
                return await HandleEditorToolAsync(new() { ["tool"] = name, ["arguments"] = args });
            }
            await Call("editor_timeline", new() { ["operation"] = "seek", ["frame"] = 0 });
            var first = await Call("editor_preview", new() { ["maxSide"] = 640 });
            await Call("editor_timeline", new() { ["operation"] = "seek", ["frame"] = 24 });
            var middle = await Call("editor_preview", new() { ["maxSide"] = 640 });
            Require(first["content"]![1]!["data"]!.GetValue<string>() != middle["content"]![1]!["data"]!.GetValue<string>(),
                "MCP preview cache ignored the animation frame.");
            var cached = await Call("editor_preview", new() { ["maxSide"] = 640 });
            Require(JsonNode.Parse(cached["content"]![0]!["text"]!.GetValue<string>())!["cached"]!.GetValue<bool>(), "Same-frame preview did not reuse its cache.");
            await Call("editor_timeline", new() { ["operation"] = "moveKeyframe", ["layerId"] = moving.Id.ToString(), ["fromFrame"] = 48, ["frame"] = 60 });
            Require(scene.Snapshot.Manifest.Timeline.Tracks[0].PositionX[1].Frame == 60, "MCP key move did not persist.");
            await Call("editor_history", new() { ["action"] = "undo" });
            Require(scene.Snapshot.Manifest.Timeline!.Tracks[0].PositionX[1].Frame == 48, "Animation history failed in preview mode.");
            var invalidHistory = scene.CurrentHistoryId; bool rejected = false;
            try { await Call("editor_timeline", new() { ["operation"] = "keyframe", ["layerId"] = moving.Id.ToString(),
                ["frame"] = 24, ["values"] = new JsonObject { ["opacity"] = 3 } }); }
            catch (ArgumentException) { rejected = true; }
            Require(rejected && scene.CurrentHistoryId == invalidHistory, "Invalid MCP key edited history.");
            report["mcpSeekCacheMoveUndoAndValidation"] = "passed";

            string saved = Path.Combine(root, "Timeline-demo.comp");
            await ProjectStore.SaveAsync(scene.CaptureSave().Snapshot, saved);
            var reopened = new EditorDocument(await ProjectStore.LoadAsync(saved));
            OpenDocument(reopened, saved); await WaitForFramesAsync(); SeekTimeline(24); await WaitForFramesAsync();
            Require(TimelinePreviewActive && Timeline?.Tracks[0].PositionX[1].Frame == 48, "Reopening lost the timeline or its panel.");
            string output = Path.Combine(root, "frames");
            await ExportTimelineToDirectoryAsync(reopened, output);
            Require(File.Exists(Path.Combine(output, "sequence.json")) && Directory.GetFiles(output, "frame-*.png").Length == 120,
                "PNG sequence is incomplete.");
            var exported = await WicImaging.DecodeSurfaceAsync(await File.ReadAllBytesAsync(Path.Combine(output, "frame-000024.png")));
            Require(exported.Pixels.AsSpan().SequenceEqual(new CanvasCompositor(AnimationEvaluator.Evaluate(reopened.Snapshot, 24)).Render().Pixels),
                "PNG sequence does not match the requested animation frame.");
            var cancelled = ExportTimelineToDirectoryAsync(reopened, Path.Combine(root, "cancelled-frames"));
            timelineExportCancellation!.Cancel(); await cancelled;
            Require(!File.Exists(Path.Combine(root, "cancelled-frames", "sequence.json")) && !timelineExporting && CanEditTimeline,
                "Cancel left the editor blocked or marked a partial sequence as complete.");
            report["saveReopenAndSequenceExportCancellation"] = "passed";

            AppWindow.Resize(new SizeInt32(1440, 980)); await Task.Delay(200); RootGrid.UpdateLayout(); FitTimeline(); await WaitForFramesAsync();
            await SaveWorkspaceCaptureAsync("timeline-wide.png");
            AppWindow.Resize(new SizeInt32(1000, 720)); await Task.Delay(200); RootGrid.UpdateLayout(); FitTimeline(); await WaitForFramesAsync();
            report["layoutMeasurements"] = new { timelineHeight = TimelineHost.ActualHeight, canvasHeight = CanvasScroll.ActualHeight, timelineWidth = timelineCanvas.ActualWidth };
            Require(TimelineHost.ActualHeight > 100 && CanvasScroll.ActualHeight > 100 && timelineCanvas.ActualWidth > 300,
                "The timeline consumes the workspace at a normal laptop size.");
            await SaveWorkspaceCaptureAsync("timeline-narrow.png");
            report["wideAndNarrowLayout"] = "passed";

            var fresh = EditorDocument.Create(320, 200); OpenDocument(fresh); await WaitForFramesAsync();
            ShowTimeline(true); TimelineChange(() => fresh.SetAnimationSettings(120, 24, true));
            OnUndo(this, new RoutedEventArgs()); await WaitForFramesAsync();
            Require(Timeline is null && !TimelinePreviewActive && CanvasHost.IsHitTestVisible && OpacitySlider.IsEnabled,
                "Undoing timeline creation left base editing disabled.");
            OnRedo(this, new RoutedEventArgs()); await WaitForFramesAsync();
            Require(Timeline is not null && TimelinePreviewActive, "Redo did not restore the animation preview.");
            ToggleTimelinePlayback(); OpenDocument(EditorDocument.Create(80, 60)); await WaitForFramesAsync();
            Require(!timelinePlaying && !timelineTimer.IsEnabled && !TimelinePreviewActive, "Replacing a document left playback running.");
            report["undoRedoCreationAndDocumentLifecycle"] = "passed";
            report["sampleProject"] = saved; report["status"] = "passed";
        }
        catch (Exception ex) { report["status"] = "failed"; report["error"] = ex.ToString(); }
        finally { WriteProbeReport("timeline-probe.json", report); forceClose = true; Close(); }
    }
}
#endif
