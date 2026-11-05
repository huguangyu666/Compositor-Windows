#if DEBUG
using Compositor.Core;
using Windows.Storage;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private Func<Task<StorageFile?>>? sourceFilePickerProbe;

    private async Task VerifySourceOpenAsync(Dictionary<string, object> report)
    {
        var owner = EditorDocument.Create(40, 30); owner.RenameLayer(owner.Snapshot.Manifest.ActiveLayerId!.Value, "Keep my work");
        OpenDocument(owner); await WaitForFramesAsync();
        string path = Path.Combine(PreferencesDirectory, "分层源文件.psd"); Directory.CreateDirectory(PreferencesDirectory);
        await File.WriteAllBytesAsync(path, FileImportCompositePsd());
        var file = await StorageFile.GetFileFromPathAsync(path);
        int sourcePicks = 0, folderPicks = 0;
        sourceFilePickerProbe = () => { sourcePicks++; return Task.FromResult<StorageFile?>(file); };
        projectFolderPickerProbe = () => { folderPicks++; return Task.FromResult<StorageFolder?>(null); };
        try
        {
            OnOpen(this, new()); await Task.Delay(50);
            Require(sourcePicks == 1 && folderPicks == 0, "Open Source still uses the project folder picker.");
            for (int i = 0; i < 500 && ReferenceEquals(document, owner); i++) await Task.Delay(10);
            await WaitForFramesAsync();
            Require(document is { LayerCount: 1, CanUndo: false, IsDirty: true } && document.Snapshot.Manifest.Width == 2 && document.Snapshot.Manifest.Height == 2
                && projectPath is null && workspace.Current.Title == "分层源文件.psd", "PSD did not open as an unsaved source document at its native size.");
            Require(workspace.Tabs.Any(tab => ReferenceEquals(tab.Document, owner)) && owner.IsDirty && owner.CanUndo, "Opening PSD discarded the previous project.");
            var pixels = document!.Snapshot.Images.Values.Single().Pixels;
            Require(pixels[0] == 232 && pixels[1] == 90 && pixels[2] == 84, "Opened PSD changed its merged pixels.");
            report["psdSourceFilePickerAndNewDocument"] = "passed";

            var opened = document; int count = workspace.Tabs.Count;
            sourceFilePickerProbe = () => Task.FromResult<StorageFile?>(null);
            OnOpen(this, new()); await Task.Delay(60);
            Require(ReferenceEquals(document, opened) && workspace.Tabs.Count == count, "Canceled source picker changed tabs.");
            var delayed = new TaskCompletionSource<StorageFile?>(TaskCreationOptions.RunContinuationsAsynchronously);
            sourceFilePickerProbe = () => delayed.Task;
            OnOpen(this, new());
            var replacement = EditorDocument.Create(31, 23); OpenDocument(replacement);
            delayed.TrySetResult(file); await Task.Delay(100);
            Require(ReferenceEquals(document, replacement) && workspace.Tabs.Count == count, "A retired source picker replaced a newer document.");
            report["sourcePickerCancelAndRetirement"] = "passed";
        }
        finally { sourceFilePickerProbe = null; projectFolderPickerProbe = null; }
    }
}
#endif
