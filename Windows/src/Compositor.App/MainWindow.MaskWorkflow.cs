using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private void OnAddWorkspaceMask(object sender, RoutedEventArgs e)
        => CreateLayerMask(revealing: !CurrentThumbnailModifiers().HasFlag(VirtualKeyModifiers.Menu), useSelection: true);

    private Task<bool> AddWorkspaceMaskAsync(VirtualKeyModifiers modifiers)
        => CreateLayerMaskAsync(revealing: !modifiers.HasFlag(VirtualKeyModifiers.Menu), useSelection: true);

    private async void CreateLayerMask(bool revealing, bool useSelection)
    {
        try { await CreateLayerMaskAsync(revealing, useSelection); }
        catch (Exception ex) { Report(Loc.T("S_SelectionMaskFailed"), ex); }
    }

    private Task<bool> CreateLayerMaskAsync(bool revealing, bool useSelection)
    {
        if (!CanEditLayerCommands || document is not { } owner || owner.ActiveTextDraft is not null
            || selectedLayerIds.Count != 1 || SelectedLayer is not { } layer) return Task.FromResult(false);
        if (layer.MaskFile is not null) { SetStatus(Loc.F("S_HasMask", layer.Name)); return Task.FromResult(false); }
        if (useSelection && owner.ActiveSelection is { } selection)
            return ApplySelectionPlaneAsync(owner, layer.Id, onMask: true, selection, revealing ? (byte)255 : (byte)0, createMask: true);
        if (!owner.AddRasterMask(layer.Id, MaskSurface.Solid(revealing))) return Task.FromResult(false);
        effectSelection = null; maskAloneLayerId = null; paintOnMask = true;
        SyncPaintTargetControls(); RefreshLayerPanel();
        SetStatus(Loc.F(revealing ? "S_MaskAdded" : "S_HideAllMaskAdded", layer.Name));
        return Task.FromResult(true);
    }

    private void RefreshMaskButtonHint()
    {
        string hint = Loc.T(document?.ActiveSelection is null ? "A_AddMaskAll" : "A_AddMaskSelection");
        if (!Equals(ToolTipService.GetToolTip(MaskWorkspaceButton), hint)) ToolTipService.SetToolTip(MaskWorkspaceButton, hint);
        AutomationProperties.SetName(MaskWorkspaceButton, Loc.T("A_AddLayerMask"));
    }

    private void RefreshMaskAloneBadge()
    {
        bool visible = CurrentMaskAloneId is not null;
        MaskAloneBadge.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        MaskAloneName.Text = visible ? SelectedLayer?.Name ?? "" : "";
        AutomationProperties.SetName(MaskAloneCloseButton, Loc.T("A_StopViewingMask"));
        ToolTipService.SetToolTip(MaskAloneCloseButton, Loc.T("A_StopViewingMask"));
    }

    private void OnCloseMaskAlone(object sender, RoutedEventArgs e)
    {
        if (document is not { } owner || CurrentMaskAloneId is not { } id) return;
        QueueCanvasInput(owner, () =>
        {
            if (!ReferenceEquals(document, owner) || CurrentMaskAloneId != id) return;
            maskAloneLayerId = null;
            RefreshLayerPanel(); RenderCanvas();
        });
    }
}
