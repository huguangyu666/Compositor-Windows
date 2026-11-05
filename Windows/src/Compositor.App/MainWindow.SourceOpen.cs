using Compositor.Core;
using Compositor.Core.Psd;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task OpenSourceFromPickerAsync()
    {
        if (!CanSwitchProjectTabs) return;
        var request = BeginProjectOpen();
        string name = "";
        try
        {
            var file = await PickSourceFileAsync();
            if (file is null || !IsCurrentProjectOpen(request)) return;
            name = file.Name;
            var loaded = await Task.Run(() =>
            {
                var photoshop = PsdReader.ReadFile(file.Path, remainingPixels: Limits.DocumentPixelBudget, cancellationToken: request.Token);
                var conversions = EditorDocument.CollectPsdConversions(photoshop);
                return (Document: EditorDocument.FromPhotoshop(photoshop), Conversions: conversions);
            }, request.Token);
            if (!IsCurrentProjectOpen(request)) return;
            if (loaded.Conversions.Count > 0 && !await ConfirmPsdConversionsAsync(name, loaded.Conversions)) return;
            if (!IsCurrentProjectOpen(request) || !CanSwitchProjectTabs) return;
            var leaving = workspace.Current;
            var added = workspace.AddTab();
            if (!ReferenceEquals(added, leaving)) parkOutgoing(leaving);
            added.DefaultName = name;
            OpenDocument(loaded.Document);
            FitToWindow();
            SetStatus(Loc.F("S_Opened", name));
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (IsCurrentProjectOpen(request)) Report(Loc.F("S_CannotOpen", name), ex);
        }
        finally { EndProjectOpen(request); }
    }

    private async Task<StorageFile?> PickSourceFileAsync()
    {
#if DEBUG
        if (sourceFilePickerProbe is { } probe) return await probe();
#endif
        var picker = new FileOpenPicker { ViewMode = PickerViewMode.List, SettingsIdentifier = "photoshopSourceOpen",
            SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add(".psd"); picker.FileTypeFilter.Add(".psb");
        InitializeWithWindow(picker);
        return await picker.PickSingleFileAsync();
    }
}
