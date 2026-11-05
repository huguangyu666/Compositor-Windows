using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private void OnDeleteKeyPreview(object sender, KeyRoutedEventArgs args)
    {
        var modifiers = CurrentThumbnailModifiers();
        if (args.KeyStatus.IsMenuKeyDown) modifiers |= VirtualKeyModifiers.Menu;
        var work = TryDeleteKeyAsync(new(args.Key, modifiers), args.OriginalSource as DependencyObject);
        if (work is null) return;
        args.Handled = true;
        _ = work;
    }

    private bool IsDeleteEditingSurface(DependencyObject? source)
    {
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is TextBox or RichEditBox or PasswordBox or NumberBox or ComboBox) return false;
            if (ReferenceEquals(node, LayerList) || ReferenceEquals(node, CanvasScroll)) return true;
        }
        return false;
    }

    private Task<bool>? TryDeleteKeyAsync(ShortcutChord input, DependencyObject? source)
    {
        if (!IsDeletePress(input.Key) || (input.Modifiers & (VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu)) != 0
            || renamingLayer || textDialogOpen || !IsDeleteEditingSurface(source) || FloatingPanelHoldsFocus) return null;
        if (tool == CanvasTool.Lasso && lassoPoints is not null)
        {
            RemoveLastLassoPoint();
            return Task.FromResult(true);
        }
        if (SelectedEffect is not null) return RemoveSelectedEffectAsync();
        return document?.ActiveSelection is not null ? DeletePlaneTargetAsync(clearSelection: true) : DeleteLayerOrMaskAsync();
    }

    private Task<bool> DeleteLayerOrMaskAsync()
    {
        if (SelectedEffect is not null) return RemoveSelectedEffectAsync();
        // The trash command ignores the canvas selection, but honors a single mask thumbnail.
        if (paintOnMask && SelectedLayer is { MaskFile: not null } layer && LayerList.SelectedItems.Count == 1
            && LayerList.SelectedItems[0] is ListViewItem { Tag: LayerRow row } && row.Layer.Id == layer.Id)
            return DeletePlaneTargetAsync(clearSelection: false);
        return DeleteSelectedLayersAsync();
    }

    private async Task<bool> DeletePlaneTargetAsync(bool clearSelection)
    {
        if (!CanEditSelectionState || pixelMove is not null || pixelNudgePending > 0 || cropRectangle is not null
            || selectionWorkResult is not null || document is not { } owner || LayerList.SelectedItems.Count != 1
            || LayerList.SelectedItems[0] is not ListViewItem { Tag: LayerRow row }) return false;
        var id = row.Layer.Id;
        bool onMask = paintOnMask && selectedLayerId == id;
        var selection = owner.ActiveSelection;
        if (clearSelection ? selection is null || selection.IsEmpty : !onMask) return false;
        layerSelectionRequest++; layerSelectionPointerTarget = null;
        byte level = backgroundColor.R;
        if (clearSelection) return await ClearSelectionPlaneAsync(owner, id, onMask, selection!, level);
        var retired = documentRetired.Task;
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        QueueCanvasInput(owner, () =>
        {
            try
            {
                if (!ReferenceEquals(document, owner))
                { completed.TrySetResult(false); return; }
                var layer = owner.Snapshot.Manifest.Layers.FirstOrDefault(layer => layer.Id == id);
                if (layer is null)
                { completed.TrySetResult(false); return; }
                selectedLayerIds.Clear(); selectedLayerIds.Add(id); selectedLayerId = id;
                paintOnMask = onMask; SyncPaintTargetControls();
                selectingLayer = true;
                try { owner.SetActiveLayer(id); } finally { selectingLayer = false; }
                RefreshLayerPanel();
                bool changed = owner.RemoveMask(id);
                if (changed)
                {
                    paintOnMask = false; maskAloneLayerId = null; SyncPaintTargetControls(); RefreshLayerPanel();
                    SetStatus(Loc.F("S_MaskRemoved", layer.Name));
                }
                completed.TrySetResult(changed);
            }
            catch (Exception ex) { completed.TrySetException(ex); }
        });
        try { return await WaitForInteractionWorkAsync(completed.Task, retired) && await completed.Task; }
        catch (Exception ex)
        {
            if (!windowClosed && ReferenceEquals(document, owner)) { App.Log($"Delete target failed: {ex}"); Report(Loc.T("S_DeletePlaneFailed"), ex); }
            return false;
        }
    }
}
