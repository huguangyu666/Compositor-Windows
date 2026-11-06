#if DEBUG
using Compositor.Core;
using Microsoft.UI.Xaml.Controls;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task VerifyBlendPreviewAsync(Dictionary<string, object> report)
    {
        var scene = EditorDocument.Create(32, 32);
        var pixels = new Surface(12, 10);
        for (int i = 0; i < pixels.Pixels.Length; i += 4)
        { pixels.Pixels[i] = 80; pixels.Pixels[i + 1] = 120; pixels.Pixels[i + 2] = 160; pixels.Pixels[i + 3] = 255; }
        var layer = scene.ImportImage(pixels, "Blend preview"); layer.Transform.Origin = new(5, 7);
        scene = new(scene.Snapshot); OpenDocument(scene); await WaitForFramesAsync();
        var original = ManifestJson.Serialize(scene.Snapshot.Manifest); var images = scene.Snapshot.Images;
        var warm = compositor; var rows = layerRows.Values.ToArray();
        ComboBoxItem Item(LayerBlendMode mode) => BlendCombo.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, mode));
        async Task Open()
        {
            BlendCombo.IsDropDownOpen = true;
            Require(await WaitUntilAsync(() => blendPickerSession is not null, 2000), "The blend picker did not begin its session.");
        }
        async Task Cancel()
        {
            BlendCombo.IsDropDownOpen = false;
            Require(await WaitUntilAsync(() => blendPickerSession is null, 2000), "The closed blend picker retained its session.");
            await WaitForFramesAsync();
        }
        var originalFrame = presented!.GetPixelBytes();
        await Open(); var screen = Item(LayerBlendMode.Screen);
        var groups = BlendCombo.Items.OfType<ComboBoxItem>().Select(item => new { mode = item.Tag.ToString(),
            border = item.BorderThickness.ToString(), top = item.BorderThickness.Top, left = item.BorderThickness.Left,
            right = item.BorderThickness.Right, bottom = item.BorderThickness.Bottom, gap = item.Margin.Top }).ToArray();
        report["blendMenuGroups"] = groups;
        Require(BlendCombo.Items.Count == 24 && groups.Count(item => item.top == 1 && item.left == 0 && item.right == 0 && item.bottom == 0) == 5,
            "The blend picker lost modes or the original six groups.");
        PreviewBlendItem(screen); await WaitForFramesAsync();
        report["blendHoverState"] = new { manifestSame = ManifestJson.Serialize(scene.Snapshot.Manifest) == original,
            imagesSame = ReferenceEquals(images, scene.Snapshot.Images), scene.CanUndo, scene.IsDirty,
            preview = blendPreview?.Mode.ToString(), session = blendPickerSession?.Id, open = BlendCombo.IsDropDownOpen,
            selected = (BlendCombo.SelectedItem as ComboBoxItem)?.Tag.ToString() };
        Require(ManifestJson.Serialize(scene.Snapshot.Manifest) == original && ReferenceEquals(images, scene.Snapshot.Images)
            && !scene.CanUndo && !scene.IsDirty && blendPreview?.Mode == LayerBlendMode.Screen, "Hovering edited the live document/history or lost its highlighted mode.");
        Require(ReferenceEquals(warm, compositor) && rows.All(row => layerRows.Values.Contains(row)), "Blend preview discarded compositor/row caches.");
        var preview = blendPreview!; AssertTransformUpload(preview.Snapshot.Value);
        Require(!originalFrame.AsSpan().SequenceEqual(presented.GetPixelBytes()), "The hover fixture showed no visible change.");
        int beforeRepeat = presentedFrameCount; PreviewBlendItem(screen); await WaitForFramesAsync();
        Require(ReferenceEquals(preview, blendPreview) && presentedFrameCount == beforeRepeat, "The same highlighted mode rendered twice.");
        await Cancel();
        Require(blendPreview is null && !scene.CanUndo && !scene.IsDirty, "Cancel retained blend preview state or history.");
        AssertUploadedFrame(scene);
        report["blendHoverIsReadOnlyAndCancelRestoresCanvas"] = "passed";

        await Open(); PreviewBlendItem(screen); await WaitForFramesAsync();
        Require(blendPreview?.Mode == LayerBlendMode.Screen, "The popup's stored-item focus replaced the hover before choosing.");
        AssertTransformUpload(blendPreview!.Snapshot.Value);
        var expected = presented.GetPixelBytes(8, 8, 1, 1); var published = new List<byte[]>();
        frameTimelineProbe = name => { if (name == "pixels-uploaded") published.Add(presented.GetPixelBytes(8, 8, 1, 1)); };
        try
        {
            BlendCombo.SelectedItem = screen;
            await Cancel();
            Require(SelectedLayer?.BlendMode == LayerBlendMode.Screen && scene.CanUndo && blendPreview is null,
                "Choosing a blend item did not commit its mode once.");
            report["blendChoicePublication"] = new { expected = Convert.ToHexString(expected),
                frames = published.Select(Convert.ToHexString).ToArray(), current = Convert.ToHexString(presented.GetPixelBytes(8, 8, 1, 1)) };
            Require(published.Count > 0 && published.All(frame => frame.AsSpan().SequenceEqual(expected)), "Choosing a blend item flashed back to the old mode.");
            AssertUploadedFrame(scene);
        }
        finally { frameTimelineProbe = null; }
        OnUndo(this, new()); await WaitForFramesAsync();
        Require(!scene.CanUndo && SelectedLayer?.BlendMode == LayerBlendMode.Normal, "Blend choice did not undo once.");
        OnRedo(this, new()); await WaitForFramesAsync(); Require(SelectedLayer?.BlendMode == LayerBlendMode.Screen, "Blend choice did not redo.");
        OnUndo(this, new()); await WaitForFramesAsync();
        report["blendChoiceCommitsOneUndoWithoutAnIntermediateOldFrame"] = "passed";

        await Open(); PreviewBlendItem(Item(LayerBlendMode.Multiply)); await WaitForFramesAsync();
        PreviewBlendItem(Item(LayerBlendMode.Normal)); await WaitForFramesAsync();
        Require(blendPreview is null && !scene.CanUndo, "Highlighting the stored mode retained a preview or history."); AssertUploadedFrame(scene);
        BlendCombo.SelectedItem = Item(LayerBlendMode.Normal); await Cancel();
        Require(!scene.CanUndo && scene.CanRedo, "Choosing the stored mode added history or consumed Redo.");
        report["blendStoredModeRestoresPreviewAndKeepsRedo"] = "passed";

        await Open();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var release = new ManualResetEventSlim(false))
        {
            beforeFrameRender = () =>
            {
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Blend preview control timed out.");
            };
            try
            {
                PreviewBlendItem(screen); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                PreviewBlendItem(Item(LayerBlendMode.Overlay)); BlendCombo.IsDropDownOpen = false;
                Require(await WaitUntilAsync(() => blendPickerSession is null, 2000), "Pending preview cancellation did not close.");
                beforeFrameRender = null; release.Set(); await WaitForFramesAsync();
                Require(blendPreview is null && !scene.CanUndo, "The retired worker published preview/history after Cancel.");
                AssertUploadedFrame(scene);
            }
            finally { beforeFrameRender = null; release.Set(); }
        }
        report["blendPendingWorkerCannotRestoreCanceledPreview"] = "passed";

        await Open(); PreviewBlendItem(screen); await WaitForFramesAsync();
        SelectLayerRow(scene.Snapshot.Manifest.Layers[0].Id); await WaitForFramesAsync();
        Require(blendPickerSession is null && blendPreview is null && !scene.CanUndo, "Selecting another layer retained the blend menu owner.");
        AssertUploadedFrame(scene);
        await Open(); PreviewBlendItem(screen); await WaitForFramesAsync();
        var replacement = EditorDocument.Create(16, 16); OpenDocument(replacement); PreviewBlendItem(screen); await WaitForFramesAsync();
        Require(blendPickerSession is null && blendPreview is null && !replacement.CanUndo && (SelectedLayer?.BlendMode ?? LayerBlendMode.Normal) == LayerBlendMode.Normal,
            "A stale blend item edited the replacement document."); AssertUploadedFrame(replacement);
        report["blendPickerRetiresAtLayerAndDocumentBoundaries"] = "passed";
    }
}
#endif
