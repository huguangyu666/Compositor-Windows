#if DEBUG
using Compositor.Core;
using Compositor.Renderer;
using Microsoft.Graphics.Canvas;
using System.Diagnostics;
using Windows.System;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private bool lastFrameUsedGpu;
    private Action? beforeGpuPreviewRender;
    // Replays a saved photo's move through the real input queue, worker and GPU upload.
    // This process edits its own in-memory document and never saves the source package.
    internal async Task RunLayerDragPerfProbeAsync()
    {
        var report = new Dictionary<string, object>();
        try
        {
            string path = Environment.GetEnvironmentVariable("COMPOSITOR_LAYER_DRAG_PROJECT")
                ?? throw new InvalidOperationException("Set COMPOSITOR_LAYER_DRAG_PROJECT to the test package.");
            var scene = new EditorDocument(await ProjectStore.LoadAsync(path));
            var movingId = Guid.Parse(Environment.GetEnvironmentVariable("COMPOSITOR_LAYER_DRAG_ID")
                ?? throw new InvalidOperationException("Set COMPOSITOR_LAYER_DRAG_ID to the image layer UUID."));
            scene.SetActiveLayer(movingId);
            if (Environment.GetEnvironmentVariable("COMPOSITOR_REQUIRE_GPU_PREVIEW") == "1")
            {
                VerifyGpuFixtures(report);
                VerifyGpuPrefixes(scene.Snapshot, report);
            }
            OpenDocument(scene); await WaitForFramesAsync();
            ToolCombo.SelectedIndex = (int)CanvasTool.Move;
            Require(await WaitUntilAsync(() => tool == CanvasTool.Move, 2000), "Move tool did not become active.");
            var layer = scene.Snapshot.Manifest.Layers.Single(l => l.Id == scene.Snapshot.Manifest.ActiveLayerId);
            var original = CopyTransform(layer.Transform);
            var start = original.Center;
            BeginTransformPointer(start, VirtualKeyModifiers.Control); await WaitForFramesAsync();
            Require(transformDrag?.Mode == TransformDragMode.Move, "Pointer did not start a layer move.");
            var frames = new List<FrameProbe>(); var waits = new List<double>(); int gpuFrames = 0;
            ProjectSnapshot? sourceMetadata = null;
            int warmAssetUploads = -1;
            for (int i = 1; i <= 24; i++)
            {
                int before = presentedFrameCount;
                var watch = Stopwatch.StartNew();
                MoveTransformPointer(new(start.X + i * 3.25, start.Y + i * 2.5), VirtualKeyModifiers.Control);
                await WaitForFramesAsync();
                Require(presentedFrameCount > before, "A moved point was not uploaded.");
                if (lastFrameUsedGpu) gpuFrames++;
                if (i == 1)
                {
                    sourceMetadata = renderSnapshot;
                    report["firstDraftWorkMs"] = lastFrameProbe.InputMs + lastFrameProbe.CpuMs + lastFrameProbe.UploadMs;
                }
                else Require(ReferenceEquals(sourceMetadata, renderSnapshot), "A detached transform reserialized unchanged source metadata.");
                if (i == 3 && gpuPreviewRenderer is { } warm) warmAssetUploads = warm.AssetUploads;
                if (i > 3 && lastFrameUsedGpu) Require(gpuPreviewRenderer!.AssetUploads == warmAssetUploads, "A warm move uploaded unchanged source, mask or LUT assets.");
                if (i > 3) { frames.Add(lastFrameProbe); waits.Add(watch.Elapsed.TotalMilliseconds); }
            }
            report["stableSourceMetadata"] = "passed";
            report["gpuFrames"] = gpuFrames;
            report["gpuTextureBytes"] = gpuPreviewRenderer?.RetainedTextureBytes ?? 0;
            report["warmAssetUploads"] = warmAssetUploads;
            report["draftComparison"] = CompareGpuFrame(transformRenderedPreview!.Snapshot, presented!);
            report["medianWorkMs"] = Median(frames.Select(f => f.InputMs + f.CpuMs + f.UploadMs));
            report["medianPointToIdleMs"] = Median(waits);
            AssertTransformUpload(transformRenderedPreview!.Snapshot);
            report["draftPixels"] = "passed";
            int unchangedFrame = presentedFrameCount, unchangedRevision = transformDraftRevision;
            MoveTransformPointer(new(start.X + 78.1, start.Y + 60.1), VirtualKeyModifiers.Control);
            await WaitForFramesAsync();
            report["unchangedPointerObserved"] = new { unchangedFrame, afterFrame = presentedFrameCount, unchangedRevision, afterRevision = transformDraftRevision,
                value = transformInspectorValue };
            Require(presentedFrameCount == unchangedFrame && transformDraftRevision == unchangedRevision,
                "An unchanged rounded placement recomputed and uploaded a frame.");
            report["unchangedPointerSkipped"] = "passed";
            FinishPlacementDrag(); await WaitForFramesAsync(); AssertUploadedFrame(scene);
            var committed = scene.Snapshot.Manifest.Layers.Single(l => l.Id == layer.Id).Transform;
            Require(committed.Origin == new PointD(original.Origin.X + 78, original.Origin.Y + 60), "Final move origin changed.");
            Require(scene.Undo() && !scene.CanUndo, "Move did not make exactly one undo step.");
            await WaitForFramesAsync(); AssertUploadedFrame(scene);
            Require(scene.Redo(), "Move redo failed."); await WaitForFramesAsync(); AssertUploadedFrame(scene);
            report["commitUndoRedo"] = "passed";
            report["gpuFrames"] = gpuFrames;
            if (Environment.GetEnvironmentVariable("COMPOSITOR_REQUIRE_GPU_PREVIEW") == "1")
                Require(gpuFrames == 24, $"Expected 24 GPU-computed draft frames, got {gpuFrames}.");
            report["project"] = System.IO.Path.GetFileName(path);
            report["samples"] = frames.Count;
            report["medianInputMs"] = Median(frames.Select(f => f.InputMs));
            report["medianInputBytes"] = Median(frames.Select(f => (double)f.InputBytes));
            report["medianWorkerBytes"] = Median(frames.Select(f => (double)f.WorkerBytes));
            report["medianPreparationMs"] = Median(frames.Select(f => f.PreparationMs));
            report["medianCompositionMs"] = Median(frames.Select(f => f.CompositionMs));
            report["medianBgraMs"] = Median(frames.Select(f => f.BgraMs));
            report["medianUploadMs"] = Median(frames.Select(f => f.UploadMs));
            report["medianWorkMs"] = Median(frames.Select(f => f.InputMs + f.CpuMs + f.UploadMs));
            report["medianPointToIdleMs"] = Median(waits);
            report["p95PointToIdleMs"] = waits.Order().ElementAt((int)Math.Ceiling(waits.Count * .95) - 1);
            if (Environment.GetEnvironmentVariable("COMPOSITOR_REQUIRE_GPU_PREVIEW") == "1")
                await VerifyGpuTransitionsAsync(scene, report);
            report["status"] = "passed";
        }
        catch (Exception ex) { report["status"] = "failed"; report["error"] = ex.ToString(); }
        finally { WriteProbeReport("layer-drag-perf.json", report); forceClose = true; Close(); }
    }

    private static void VerifyGpuPrefixes(ProjectSnapshot snapshot, Dictionary<string, object> report)
    {
        var plan = GpuPreviewScene.TryCreate(snapshot) ?? throw new InvalidOperationException("The measured scene did not qualify for GPU preview.");
        using var renderer = new GpuCanvasRenderer(CanvasDevice.GetSharedDevice(), snapshot.Manifest.Width, snapshot.Manifest.Height);
        var results = new List<object>();
        for (int count = 1; count <= plan.Layers.Count; count++)
        {
            var prefix = plan.Layers.Take(count).ToArray();
            var ids = prefix.Select(layer => layer.Id).ToHashSet();
            var copy = new ProjectSnapshot { Manifest = ManifestJson.Deserialize(ManifestJson.Serialize(snapshot.Manifest)), Images = snapshot.Images, Masks = snapshot.Masks };
            foreach (var layer in copy.Manifest.Layers.Where(layer => !layer.IsGroupOrFalse && !ids.Contains(layer.Id))) layer.IsVisible = false;
            var gpu = renderer.Render(plan with { Layers = prefix });
            var comparison = CompareGpuFrame(copy, gpu);
            results.Add(new { count, kind = prefix[^1].Adjustment?.Kind.ToString() ?? "raster", comparison });
            Require(comparison.Differences == 0, $"GPU prefix {count}: {comparison}");
        }
        report["gpuPrefixes"] = results;
    }

    private sealed record GpuFrameComparison(int Differences, int MaxDelta, int First, string Expected = "", string Actual = "");

    private static GpuFrameComparison CompareGpuFrame(ProjectSnapshot snapshot, CanvasBitmap bitmap)
    {
        var expected = new CanvasCompositor(snapshot).Render(); var bytes = new byte[expected.Width * expected.Height * 4];
        SurfacePresentation.FillBgra(expected, bytes, expected.Width, 0, 0, expected.Width, expected.Height);
        var actual = bitmap.GetPixelBytes(); int differences = 0, maxDelta = 0, first = -1;
        for (int i = 0; i < bytes.Length; i++) if (bytes[i] != actual[i])
        {
            differences++; maxDelta = Math.Max(maxDelta, Math.Abs(bytes[i] - actual[i])); if (first < 0) first = i;
        }
        int pixel = first < 0 ? 0 : first / 4 * 4;
        return new(differences, maxDelta, first, differences == 0 ? "" : Convert.ToHexString(bytes.AsSpan(pixel, 4)),
            differences == 0 ? "" : Convert.ToHexString(actual.AsSpan(pixel, 4)));
    }
}
#endif
