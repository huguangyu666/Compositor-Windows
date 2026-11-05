#if DEBUG
using Compositor.Core;
using Microsoft.UI.Xaml.Controls;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task VerifyPsdBudgetsAsync(string directory, Dictionary<string, object> report)
    {
        // A cropped layer now goes through the conversion sheet; the budget groups test the import,
        // not the gate, so the dialog answers itself with the primary button here.
        projectDialogProbe = _ => Task.FromResult(ContentDialogResult.Primary);
        string first = Path.Combine(directory, "Wide.psd"), second = Path.Combine(directory, "Wide2.psd");
        var bytes = PsdBudgetProbeFile();
        await File.WriteAllBytesAsync(first, bytes); await File.WriteAllBytesAsync(second, bytes);
        var scene = EditorDocument.Create(100, 80); OpenDocument(scene); await WaitForFramesAsync();
        var result = await ImportFilesAsync(scene, [first]); await WaitForFramesAsync(); AssertUploadedFrame(scene);
        var layer = scene.Snapshot.Manifest.Layers.Single(l => l.MaskFile is not null);
        Require(result.ImportedFiles == 1 && layer.Transform.Origin == new PointD(0, 0) && layer.Transform.Size == new SizeD(2, 1)
            && scene.Snapshot.Images[layer.Id].Pixels.AsSpan().SequenceEqual(new byte[] { 116, 45, 42, 128, 116, 45, 42, 128 })
            && scene.Snapshot.Masks[layer.Id].Pixels.AsSpan().SequenceEqual(new byte[] { 200, 200 })
            && layer.MaskPlacement!.Origin == layer.Transform.Origin && layer.MaskPlacement.Size == layer.Transform.Size && layer.MaskLinked == true,
            "PSD canvas crop lost source pixels, mask grid or linkage.");
        Require(StatusText.Text == Loc.F("S_ImportedPsdCropped", Loc.F("S_ImportedLayers", "Wide.psd", 1), 1),
            "PSD cropping did not surface a localized conversion notice.");
        OnUndo(this, new()); await WaitForFramesAsync(); AssertUploadedFrame(scene);
        Require(!scene.CanUndo && scene.LayerCount == 1, "PSD crop opened more than one history step.");
        OnRedo(this, new()); await WaitForFramesAsync(); AssertUploadedFrame(scene);
        report["filePsdCanvasCropLayerMaskWarningGpuUndo"] = "passed";

        scene = EditorDocument.Create(100, 80); OpenDocument(scene); await WaitForFramesAsync();
        var observed = new List<long>(); beforeImportBudget = observed.Add;
        try { result = await ImportFilesAsync(scene, [first, second]); }
        finally { beforeImportBudget = null; }
        await WaitForFramesAsync(); AssertUploadedFrame(scene);
        Require(result.ImportedFiles == 2 && observed.SequenceEqual(new[] { Limits.DocumentPixelBudget - 8000, Limits.DocumentPixelBudget - 8004 }),
            "PSD decode did not capture existing and earlier batch image/mask pixels.");
        OnUndo(this, new()); await WaitForFramesAsync(); AssertUploadedFrame(scene);
        Require(!scene.CanUndo && scene.LayerCount == 1, "Two cropped PSD files did not share batch history.");
        report["filePsdBudgetIncludesExistingAndEarlierBatchPixels"] = "passed";

        string padded = Path.Combine(directory, "Mapped.psd");
        await File.WriteAllBytesAsync(padded, bytes);
        using (var file = new FileStream(padded, FileMode.Open, FileAccess.Write, FileShare.None)) file.SetLength(64 * 1024 * 1024);
        scene = EditorDocument.Create(100, 80); OpenDocument(scene); await WaitForFramesAsync();
        long allocated = -1; psdDecodeAllocated = value => allocated = value;
        try { result = await ImportFilesAsync(scene, [padded]); }
        finally { psdDecodeAllocated = null; }
        await WaitForFramesAsync(); AssertUploadedFrame(scene);
        layer = scene.Snapshot.Manifest.Layers.Single(l => l.MaskFile is not null);
        Require(result.ImportedFiles == 1 && allocated >= 0 && allocated < 1024 * 1024
            && scene.Snapshot.Images[layer.Id].Pixels.AsSpan().SequenceEqual(new byte[] { 116, 45, 42, 128, 116, 45, 42, 128 })
            && scene.Snapshot.Masks[layer.Id].Pixels.AsSpan().SequenceEqual(new byte[] { 200, 200 }),
            "App PSD import copied the 64MiB file or lost decoded pixels/mask.");
        using (var exclusive = new FileStream(padded, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        OnUndo(this, new()); await WaitForFramesAsync(); AssertUploadedFrame(scene);
        Require(!scene.CanUndo && scene.LayerCount == 1, "Mapped PSD import did not keep batch history.");
        report["filePsdMappedWorkerGpuUndoAndReleasedHandles"] = "passed";
        report["filePsdMappedWorkerAllocatedBytes"] = allocated;
    }

    private static byte[] PsdBudgetProbeFile()
    {
        using var record = new MemoryStream(); using var channels = new MemoryStream();
        Write32(record, 0); Write32(record, -1); Write32(record, 1); Write32(record, 30_000); Write16(record, 5);
        foreach (var (id, value) in new (int, byte)[] { (0, 232), (1, 90), (2, 84), (-1, 128), (-2, 200) })
        {
            using var runs = new MemoryStream();
            for (int x = 0; x < 30_001;)
            { int count = Math.Min(128, 30_001 - x); runs.WriteByte(unchecked((byte)(1 - count))); runs.WriteByte(value); x += count; }
            Write16(record, id); Write32(record, (int)runs.Length + 4);
            Write16(channels, 1); Write16(channels, (int)runs.Length); channels.Write(runs.ToArray());
        }
        record.Write("8BIMnorm"u8); record.Write([255, 0, 0, 0]); Write32(record, 32); Write32(record, 20);
        Write32(record, 0); Write32(record, -1); Write32(record, 1); Write32(record, 30_000); record.Write([30, 0, 0, 0]);
        Write32(record, 0); record.Write([1, (byte)'L', 0, 0]);
        using var file = new MemoryStream(); file.Write("8BPS"u8); Write16(file, 1); file.Write(new byte[6]); Write16(file, 3);
        Write32(file, 1); Write32(file, 2); Write16(file, 8); Write16(file, 3); Write32(file, 0); Write32(file, 0);
        int info = 2 + (int)record.Length + (int)channels.Length;
        Write32(file, info + 4); Write32(file, info); Write16(file, 1); file.Write(record.ToArray()); file.Write(channels.ToArray()); return file.ToArray();
        static void Write16(Stream stream, int value) => stream.Write([(byte)(value >> 8), (byte)value]);
        static void Write32(Stream stream, int value) => stream.Write([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
    }
}
#endif
