#if DEBUG
using Compositor.Core;
using Microsoft.UI.Xaml.Controls;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task VerifyPsdBlendImportsAsync(Dictionary<string, object> report)
    {
        var directory = Path.Combine(Path.GetTempPath(), "compositor-psd-blend-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        // An unsupported blend key now reports through the conversion sheet first; these groups
        // test the blend fallback itself, so the dialog answers itself here.
        projectDialogProbe = _ => Task.FromResult(ContentDialogResult.Primary);
        try
        {
            var scene = EditorDocument.Create(1, 1);
            new byte[] { 200, 100, 50, 255 }.CopyTo(scene.Snapshot.Images[scene.Snapshot.Manifest.ActiveLayerId!.Value].Pixels, 0);
            OpenDocument(scene); await WaitForFramesAsync();
            string path = Path.Combine(directory, "Multiply.psd");
            await File.WriteAllBytesAsync(path, FileImportBlendPsd("mul "));
            var imported = await ImportFilesAsync(scene, [path]); await WaitForFramesAsync();
            Require(imported.ImportedFiles == 1 && imported.Failures.Count == 0
                && scene.Snapshot.Manifest.Layers.Single(layer => layer.Name == "Blend").BlendMode == LayerBlendMode.Multiply,
                "PSD blend mode was lost between disk decode and file-batch publication.");
            Require(new CanvasCompositor(scene.Snapshot).Render().Pixels.AsSpan().SequenceEqual(new byte[] { 100, 25, 38, 255 }),
                "Imported Multiply pixels rendered as Normal.");
            AssertUploadedFrame(scene);
            OnUndo(this, new()); await WaitForFramesAsync(); Require(!scene.CanUndo && scene.LayerCount == 1, "PSD blend import split its Undo.");
            OnRedo(this, new()); await WaitForFramesAsync(); AssertUploadedFrame(scene);
            Require(scene.Snapshot.Manifest.Layers.Single(layer => layer.Name == "Blend").BlendMode == LayerBlendMode.Multiply,
                "Redo lost the imported PSD blend mode.");
            report["psdMultiplyDiskImportGpuAndOneUndo"] = "passed";

            scene = EditorDocument.Create(1, 1); OpenDocument(scene); await WaitForFramesAsync();
            path = Path.Combine(directory, "Unsupported.psd"); await File.WriteAllBytesAsync(path, FileImportBlendPsd("zzzz"));
            imported = await ImportFilesAsync(scene, [path]); await WaitForFramesAsync();
            Require(imported.ImportedFiles == 1 && imported.Failures.Count == 0 && StatusText.Text.Contains("zzzz", StringComparison.Ordinal),
                "Unsupported PSD blend fallback was silent.");
            Require(scene.Snapshot.Manifest.Layers.Single(layer => layer.Name == "Blend").BlendMode == LayerBlendMode.Normal,
                "Unsupported PSD blend mode did not fall back to Normal.");
            report["unsupportedPsdBlendImportHasVisibleFallback"] = "passed";
        }
        finally { projectDialogProbe = null; Directory.Delete(directory, true); }
    }

    private static byte[] FileImportBlendPsd(string key)
    {
        using var record = new MemoryStream();
        U32(record, 0); U32(record, 0); U32(record, 1); U32(record, 1); U16(record, 4);
        foreach (int channel in new[] { 0, 1, 2, -1 }) { U16(record, channel); U32(record, 3); }
        record.Write("8BIM"u8); record.Write(System.Text.Encoding.ASCII.GetBytes(key)); record.Write([255, 0, 0, 0]);
        U32(record, 16); U32(record, 0); U32(record, 0); record.Write([5, .. "Blend"u8, 0, 0]);
        using var info = new MemoryStream(); U16(info, 1); info.Write(record.ToArray());
        foreach (byte channel in new byte[] { 128, 64, 192, 255 }) { U16(info, 0); info.WriteByte(channel); }
        using var file = new MemoryStream(); file.Write("8BPS"u8); U16(file, 1); file.Write(new byte[6]); U16(file, 4);
        U32(file, 1); U32(file, 1); U16(file, 8); U16(file, 3); U32(file, 0); U32(file, 0);
        U32(file, (int)info.Length + 4); U32(file, (int)info.Length); file.Write(info.ToArray()); return file.ToArray();
        static void U16(Stream stream, int value) => stream.Write([(byte)(value >> 8), (byte)value]);
        static void U32(Stream stream, int value) => stream.Write([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
    }
}
#endif
