#if DEBUG
using System.Diagnostics;
using Compositor.Core;
using Windows.System;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task VerifyGrayDistortAsync(Dictionary<string, object> report)
    {
        const int side = 4096;
        var scene = EditorDocument.Create(1, 1); var layer = scene.Snapshot.Manifest.Layers[0];
        scene.Snapshot.Manifest.Width = scene.Snapshot.Manifest.Height = side;
        layer.Transform.Size = new(side, side);
        scene.Snapshot.Images[layer.Id].Pixels.AsSpan().Fill(255);
        var mask = new MaskSurface(side, side);
        for (int y = 0; y < side; y++) for (int x = 0; x < side; x++)
            mask.Pixels[y * side + x] = x < 4 || y < 4 || x >= side - 4 || y >= side - 4
                ? (byte)255 : (byte)(8 + (x * 3 + y * 5) % 240);
        layer.MaskFile = ProjectValidator.Uuid(layer.Id) + ".mask.png"; layer.MaskLinked = false;
        ((Dictionary<Guid, MaskSurface>)scene.Snapshot.Masks)[layer.Id] = mask;
        var originalImage = scene.Snapshot.Images[layer.Id]; var original = ManifestJson.Serialize(scene.Snapshot.Manifest);
        OpenDocument(scene); await WaitForFramesAsync();
        HandleThumbnailClick(layer.Id, true, VirtualKeyModifiers.Menu); await WaitForFramesAsync();
        AssertMaskUpload(scene, layer.Id);
        OnFreeTransform(this, new()); await WaitForFramesAsync();
        var point = CurrentTransformValue!.Point(TransformDrag.Handles[0]);
        BeginTransformPointer(point, VirtualKeyModifiers.Control); await WaitForFramesAsync();
        Require(transformDrag?.Mode == TransformDragMode.Distort && transformInspectorEdit?.TargetsMask == true,
            "4K solo mask did not enter the distortion worker.");
        var ticks = new List<FrameProbe>();
        for (int i = 0; i < 7; i++)
        {
            int before = presentedFrameCount;
            MoveTransformPointer(new(point.X - 17.25 - i, point.Y - 11.5 - i * .5), VirtualKeyModifiers.None);
            await WaitForFramesAsync();
            Require(presentedFrameCount == before + 1, "4K mask distortion reused an old frame.");
            if (i >= 2) ticks.Add(lastFrameProbe);
        }
        FinishPlacementDrag(); await WaitForFramesAsync();
        var preview = transformRenderedPreview!.Snapshot;
        Require(Math.Max(preview.Masks[layer.Id].Width, preview.Masks[layer.Id].Height) == 2048
            && ReferenceEquals(mask, scene.Snapshot.Masks[layer.Id]) && !scene.CanUndo,
            "4K preview changed the source or did not use its 2048 cap.");
        AssertMaskUpload(preview, layer.Id);
        var commit = Stopwatch.StartNew(); FinishTransformDraft(); await WaitForFramesAsync(); commit.Stop();
        Require(distortCompletion is null && scene.CanUndo && scene.Snapshot.Masks[layer.Id].Width > 4096
            && ReferenceEquals(originalImage, scene.Snapshot.Images[layer.Id]), "4K mask commit did not publish full resolution alone.");
        AssertMaskUpload(scene, layer.Id);
        var published = scene.Snapshot.Masks[layer.Id];
        OnUndo(this, new()); await WaitForFramesAsync();
        Require(!scene.CanUndo && ReferenceEquals(mask, scene.Snapshot.Masks[layer.Id])
            && original == ManifestJson.Serialize(scene.Snapshot.Manifest), "4K mask Undo did not restore the original transaction.");
        AssertMaskUpload(scene, layer.Id);
        OnRedo(this, new()); await WaitForFramesAsync();
        Require(ReferenceEquals(published, scene.Snapshot.Masks[layer.Id]), "4K mask Redo lost its published pixels.");
        AssertMaskUpload(scene, layer.Id);
        HandleThumbnailClick(layer.Id, true, VirtualKeyModifiers.Menu); await WaitForFramesAsync(); AssertUploadedFrame(scene);
        report["grayDistort4KSoloPreviewFullCommitOneUndoRedoGpu"] = "passed";
        report["grayDistort4KFrames"] = new { samples = ticks.Count,
            completeTickMedianMs = Median(ticks.Select(s => s.InputMs + s.CpuMs + s.UploadMs)),
            completeTickMaxMs = ticks.Max(s => s.InputMs + s.CpuMs + s.UploadMs),
            inputMedianMs = Median(ticks.Select(s => s.InputMs)), cpuMedianMs = Median(ticks.Select(s => s.CpuMs)),
            preparationMedianMs = Median(ticks.Select(s => s.PreparationMs)), compositionMedianMs = Median(ticks.Select(s => s.CompositionMs)),
            bgraMedianMs = Median(ticks.Select(s => s.BgraMs)),
            uploadMedianMs = Median(ticks.Select(s => s.UploadMs)), frames = ticks,
            inputAndWorkerAllocatedMiB = Median(ticks.Select(s => (s.InputBytes + s.WorkerBytes) / 1048576d)),
            band = ticks[^1].Band, fullCommitAndPresentationMs = commit.Elapsed.TotalMilliseconds,
            kind = "Programmatic input + CPU + GPU upload; no physical input or before/after comparison" };
    }
}
#endif
