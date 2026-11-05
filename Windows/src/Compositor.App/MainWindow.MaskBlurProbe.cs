#if DEBUG
using Compositor.Core;
using Windows.System;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task VerifyMaskBlurAsync(Dictionary<string, object> report)
    {
        foreach (bool folder in new[] { false, true })
        {
            var scene = EditorDocument.Create(64, 64); var id = scene.Snapshot.Manifest.Layers[0].Id;
            if (folder) id = scene.GroupLayers([id])!.Id;
            var mask = new MaskSurface(32, 32);
            for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++) mask.Pixels[y * 32 + x] = (byte)((x * 37 + y * 53) % 256);
            scene.AddRasterMask(id, mask);
            var layer = scene.Snapshot.Manifest.Layers.Single(layer => layer.Id == id);
            layer.MaskPlacement = new() { Origin = new(10, 12), Size = new(32, 32), Rotation = 17, FlipX = true };
            scene = new(scene.Snapshot); OpenDocument(scene); await WaitForFramesAsync();
            HandleThumbnailClick(id, true, 0); await WaitForFramesAsync();
            ToolCombo.SelectedIndex = (int)CanvasTool.Blur;
            Require(await WaitUntilAsync(() => tool == CanvasTool.Blur && toolChangeApplied == toolChangeRevision, 2000), "Mask blur tool did not settle.");
            BlurModeCombo.SelectedIndex = 0; BlurRadiusBox.Value = 2; BrushSizeSlider.Value = 5; BrushHardnessSlider.Value = 100; BrushOpacitySlider.Value = 40;
            await WaitForFramesAsync(); var images = scene.Snapshot.Images; var warm = compositor;
            var point = BrushEngine.PixelToDocument(layer.MaskPlacement, 32, 32, new(16.5, 16.5));
            BeginPointerStroke(point); await WaitForFramesAsync();
            AppendPointerStroke(new(point.X + 2, point.Y)); FinishPointerStroke(); await WaitForFramesAsync();
            Require(scene.CanUndo && !mask.Pixels.AsSpan().SequenceEqual(scene.Snapshot.Masks[id].Pixels)
                && ReferenceEquals(images, scene.Snapshot.Images) && ReferenceEquals(warm, compositor),
                "Mask blur edited layer pixels, left coverage unchanged or discarded the compositor.");
            AssertUploadedFrame(scene); OnUndo(this, new()); await WaitForFramesAsync();
            Require(!scene.CanUndo && ReferenceEquals(mask, scene.Snapshot.Masks[id])
                && images.All(pair => ReferenceEquals(pair.Value, scene.Snapshot.Images[pair.Key])), "Mask blur did not undo once.");
            OnRedo(this, new()); await WaitForFramesAsync(); AssertUploadedFrame(scene);
            report[folder ? "folderMaskBlurUsesGrayTargetAndOneUndo" : "layerMaskBlurUsesGrayTargetAndOneUndo"] = "passed";
        }

        var refusal = EditorDocument.Create(32, 32); var target = refusal.Snapshot.Manifest.Layers[0].Id;
        var originalMask = new MaskSurface(32, 32); originalMask.Pixels.AsSpan().Fill(255); refusal.AddRasterMask(target, originalMask);
        refusal = new(refusal.Snapshot); OpenDocument(refusal); await WaitForFramesAsync(); HandleThumbnailClick(target, true, 0); await WaitForFramesAsync();
        var sources = refusal.Snapshot.Images;
        foreach (var (targetTool, mode) in new[] { (CanvasTool.Clone, 0), (CanvasTool.Blur, 1), (CanvasTool.Blur, 2) })
        {
            ToolCombo.SelectedIndex = (int)targetTool;
            Require(await WaitUntilAsync(() => tool == targetTool && toolChangeApplied == toolChangeRevision, 2000), "The image-only mask fixture did not settle.");
            BlurModeCombo.SelectedIndex = mode; cloneSource = new(4, 4); CloneAllLayersCheck.IsChecked = true;
            BeginPointerStroke(new(8, 8)); FinishPointerStroke(); await WaitForFramesAsync();
            Require(pointerStroke is null && !refusal.CanUndo && ReferenceEquals(sources, refusal.Snapshot.Images)
                && ReferenceEquals(originalMask, refusal.Snapshot.Masks[target]) && StatusText.Text == Loc.T("S_PaintToolNeedsPixels"),
                "An image-only tool edited pixels while the mask thumbnail was selected.");
        }
        report["cloneSmudgeAndLiquifyRefuseMaskWithoutEditingImage"] = "passed";
    }
}
#endif
