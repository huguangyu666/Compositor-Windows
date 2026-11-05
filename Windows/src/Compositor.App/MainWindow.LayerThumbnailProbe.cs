#if DEBUG
using Compositor.Core;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task VerifyLayerThumbnailsAsync(Dictionary<string, object> report)
    {
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        static byte[] Bytes(object source)
        {
            var bitmap = (WriteableBitmap)source; var bytes = new byte[bitmap.PixelBuffer.Length];
            using var stream = bitmap.PixelBuffer.AsStream(); stream.ReadExactly(bytes); return bytes;
        }
        var scene = EditorDocument.Create(100, 50); OpenDocument(scene); await WaitForFramesAsync();
        var id = scene.Snapshot.Manifest.Layers[0].Id; var row = (LayerRow)layerRows[id].Tag;
        var original = row.Thumbnail.Source!; var originalBytes = Bytes(original);
        scene.MoveLayerBy(id, 25, 0); await WaitForFramesAsync();
        if (ReferenceEquals(original, row.Thumbnail.Source)) throw new InvalidOperationException("Moving a layer did not update its canvas-framed thumbnail.");
        report["placementChangesInvalidateLayerThumbnail"] = "passed";
        Require(row.PixelTarget.Width == 36 && row.PixelTarget.Height == 18
            && row.Thumbnail.Source is WriteableBitmap { PixelWidth: 72, PixelHeight: 36 }
            && Bytes(row.Thumbnail.Source)[0] == 82 && Bytes(row.Thumbnail.Source)[(10 * 72 + 30) * 4] == 255,
            "The image thumbnail did not show the translated layer on a canvas-shaped checkerboard.");
        OnUndo(this, new()); await WaitForFramesAsync();
        Require(!scene.CanUndo && originalBytes.SequenceEqual(Bytes(row.Thumbnail.Source!)), "Undo did not restore thumbnail placement.");
        var cached = row.Thumbnail.Source; var warm = compositor; int frames = presentedFrameCount;
        RefreshLayerPanel(); RefreshLayerPanel();
        Require(ReferenceEquals(cached, row.Thumbnail.Source) && ReferenceEquals(warm, compositor) && presentedFrameCount == frames,
            "Unchanged panel refresh rebuilt thumbnails or recomposited the canvas.");
        report["canvasFramingAndUndoPreserveThumbnailCache"] = "passed";

        scene.AddRasterMask(id, new(3, 3, [255, 255, 255, 255, 0, 255, 255, 255, 255]));
        scene.Snapshot.Manifest.Layers[0].MaskPlacement = new() { Origin = new(50, 20), Size = new(10, 10), Sampling = LayerSampling.Nearest };
        scene.Snapshot.Manifest.Layers[0].MaskLinked = false;
        scene = new(scene.Snapshot); OpenDocument(scene); await WaitForFramesAsync(); row = (LayerRow)layerRows[id].Tag;
        Require(row.MaskTarget.Width == 30 && row.MaskTarget.Height == 15 && row.MaskThumbnail.Source is WriteableBitmap { PixelWidth: 60, PixelHeight: 30 },
            "Mask thumbnail did not fit the canvas.");
        var maskBytes = Bytes(row.MaskThumbnail.Source!);
        Require(maskBytes[0] == 255 && maskBytes[(15 * 60 + 33) * 4] == 0, "The independent mask was not placed on its edge background.");
        var oldMask = row.MaskThumbnail.Source; var keptImage = row.Thumbnail.Source;
        var edit = scene.BeginTransformDraft([id], onMask: true)!; var value = edit.Value;
        value = CopyTransform(value); value.Origin = new(10, 20);
        Require(scene.CommitTransform(edit.Preview(value)!), "The mask placement fixture did not move."); await WaitForFramesAsync();
        Require(!ReferenceEquals(oldMask, row.MaskThumbnail.Source) && ReferenceEquals(keptImage, row.Thumbnail.Source),
            "Mask movement failed to invalidate only the mask thumbnail.");
        OnUndo(this, new()); await WaitForFramesAsync(); Require(!scene.CanUndo && maskBytes.SequenceEqual(Bytes(row.MaskThumbnail.Source!)), "Mask placement Undo left stale thumbnail pixels.");
        report["independentMaskPlacementUpdatesWithoutImageChurn"] = "passed";

        var saved = row.Thumbnail.Source; var oldCanvas = row.ThumbnailCanvas;
        Require(scene.SetCanvasBounds(0, 0, 50, 100), "Canvas resize fixture made no change."); await WaitForFramesAsync();
        Require(row.PixelTarget.Width == 18 && row.PixelTarget.Height == 36 && !ReferenceEquals(saved, row.Thumbnail.Source)
            && row.ThumbnailCanvas != oldCanvas, "Canvas resizing did not reframe the thumbnail.");
        OnUndo(this, new()); await WaitForFramesAsync();
        report["canvasResizeReframesBothThumbnails"] = "passed";

        var savedRatio = transformRatioLock;
        try
        {
            var beforeDraft = Bytes(row.Thumbnail.Source!);
            Require(BeginTransformInspector(persistent: true), "Thumbnail draft fixture could not begin.");
            ChangeTransformNumber(TransformField.X, 25); await WaitForFramesAsync();
            Require(transformRenderedPreview is not null && !beforeDraft.SequenceEqual(Bytes(row.Thumbnail.Source!)) && !scene.CanUndo,
                "A published transform preview left the thumbnail at its old placement.");
            QueueTransformCancel(); await WaitForFramesAsync();
            Require(beforeDraft.SequenceEqual(Bytes(row.Thumbnail.Source!)) && !scene.CanUndo, "Cancel retained draft thumbnail pixels or added history.");
        }
        finally { transformRatioLock = savedRatio; }
        report["settledTransformPreviewAndCancelUpdateThumbnail"] = "passed";
    }
}
#endif
