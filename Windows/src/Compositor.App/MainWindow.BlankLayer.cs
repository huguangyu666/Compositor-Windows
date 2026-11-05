using Compositor.Core;
using Microsoft.UI.Xaml;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private void OnAddBlankLayer(object sender, RoutedEventArgs e)
    {
        if (!CanEditLayerCommands || document is not { ActiveTextDraft: null } owner || owner.AddBlankLayer() is not { } layer) return;
        // Like AddFolder, this changes metadata only; render workers hold a frozen manifest.
        // Select immediately so a brush queued behind the current frame captures the new layer.
        if (layer.ParentId is { } parent) collapsedGroups.Remove(parent);
        effectSelection = null; maskAloneLayerId = null; paintOnMask = false;
        selectedLayerIds.Clear(); selectedLayerIds.Add(layer.Id); selectedLayerId = layer.Id;
        SyncPaintTargetControls(); RefreshLayerPanel();
        SetStatus(Loc.F("S_BlankLayerAdded", layer.Name));
    }
}
