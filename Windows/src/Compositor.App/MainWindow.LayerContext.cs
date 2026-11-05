using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private void AttachLayerContextMenu(ListViewItem item, LayerRow row)
    {
        var menu = new MenuFlyout(); item.ContextFlyout = menu;
        item.ContextRequested += async (_, args) =>
        {
            args.Handled = true;
            bool positioned = args.TryGetPosition(item, out var point);
            bool? mask = null; LayerEffectKind? effect = null;
            for (var node = args.OriginalSource as DependencyObject; node is not null && !ReferenceEquals(node, item); node = VisualTreeHelper.GetParent(node))
            {
                if (ReferenceEquals(node, row.MaskTarget)) { mask = true; break; }
                if (ReferenceEquals(node, row.PixelTarget)) { mask = false; break; }
                foreach (var pair in row.EffectRows)
                    if (ReferenceEquals(node, pair.Value.Root)) { effect = pair.Key; break; }
                if (effect is not null) break;
            }
            var prepared = await PrepareLayerContextAsync(row.Layer.Id, mask, effect);
            if (prepared is null || windowClosed || item.XamlRoot is null
                || !layerRows.TryGetValue(row.Layer.Id, out var current) || !ReferenceEquals(current, item)) return;
            menu.Items.Clear();
            foreach (var entry in prepared.Items.ToArray()) { prepared.Items.Remove(entry); menu.Items.Add(entry); }
            menu.ShowAt(item, positioned ? new FlyoutShowOptions { Position = point } : new FlyoutShowOptions());
        };
    }

    private async Task<MenuFlyout?> PrepareLayerContextAsync(Guid id, bool? mask = null, LayerEffectKind? effect = null)
    {
        if (effect is { } kind)
            return await SelectLayerEffectAsync(id, kind) ? BuildLayerContextMenu() : null;
        if (!CanSelectLayerTarget || document is not { } owner) return null;
        var ids = LayerList.SelectedItems.OfType<ListViewItem>().Select(item => ((LayerRow)item.Tag).Layer.Id).ToHashSet();
        if (mask is not null || !ids.Contains(id)) ids = [id];
        bool changesTarget = !ids.SetEquals(selectedLayerIds) || selectedLayerId != id || mask is { } requested && requested != paintOnMask;
        long request = ++layerSelectionRequest; var retired = documentRetired.Task;
        try
        {
            if (changesTarget)
            {
                CommitInlineText();
                await FinishTransformBeforeOutputAsync();
                if (!await ApplyGradientDraftAsync()) return null;
            }
            if (windowClosed || !ReferenceEquals(document, owner) || request != layerSelectionRequest) return null;
            var completed = new TaskCompletionSource<MenuFlyout?>(TaskCreationOptions.RunContinuationsAsynchronously);
            QueueCanvasInput(owner, () =>
            {
                try
                {
                    if (!ReferenceEquals(document, owner) || request != layerSelectionRequest || !CanSelectLayerTarget
                        || !owner.Snapshot.Manifest.Layers.Any(layer => layer.Id == id))
                    { completed.TrySetResult(null); return; }
                    if (changesTarget) CloseFloatingPanel("effects", commit: false);
                    bool samePrimary = selectedLayerId == id;
                    effectSelection = null; selectedLayerId = id;
                    selectedLayerIds.Clear(); selectedLayerIds.UnionWith(ids);
                    selectingLayer = true;
                    try { owner.SetActiveLayer(id); } finally { selectingLayer = false; }
                    paintOnMask = ids.Count == 1 && owner.Snapshot.Masks.ContainsKey(id) && (mask ?? (samePrimary && paintOnMask));
                    SyncPaintTargetControls(); RefreshLayerPanel(); LayerList.Focus(FocusState.Programmatic);
                    completed.TrySetResult(BuildLayerContextMenu());
                }
                catch (Exception ex) { completed.TrySetException(ex); }
            });
            return await WaitForInteractionWorkAsync(completed.Task, retired) ? await completed.Task : null;
        }
        catch (Exception ex)
        {
            if (!windowClosed && ReferenceEquals(document, owner)) Report(Loc.T("S_LayerContextFailed"), ex);
            return null;
        }
    }

    private MenuFlyout BuildLayerContextMenu()
    {
        var menu = new MenuFlyout();
        if (document is not { } owner || SelectedLayer is not { } layer) return menu;
        var ids = selectedLayerIds.ToHashSet(); var selectedEffect = SelectedEffect; bool onMask = paintOnMask;
        bool Current() => !windowClosed && ReferenceEquals(document, owner) && selectedLayerId == layer.Id
            && selectedLayerIds.SetEquals(ids) && paintOnMask == onMask && SelectedEffect == selectedEffect
            && CanEditLayerCommands && owner.ActiveTextDraft is null;
        MenuFlyoutItem Item(string name, string text, bool enabled, Action action)
        {
            var item = new MenuFlyoutItem { Name = name, Text = text, IsEnabled = enabled };
            item.Click += (_, _) =>
            {
                if (!Current()) return;
                try { action(); }
                catch (Exception ex) { Report(Loc.T("S_LayerContextFailed"), ex); }
            };
            return item;
        }
        void Add(string name, string text, bool enabled, Action action) => menu.Items.Add(Item(name, text, enabled, action));
        bool editable = CanEditLayerCommands && owner.ActiveTextDraft is null, single = ids.Count == 1, hasMask = layer.MaskFile is not null;
        Add("LayerContextDuplicate", Loc.T("M_Duplicate"), editable, () => OnDuplicateLayers(this, new()));
        Add("LayerContextRename", Loc.T("M_Rename"), editable && single, RenameSelectedLayer);
        string delete = selectedEffect is { } effect ? Loc.F("M_ContextDeleteEffect", EffectKindLabel(effect.Kind))
            : onMask && hasMask ? Loc.T("M_ContextDeleteMask") : Loc.T(single ? "M_ContextDeleteLayer" : "M_ContextDeleteLayers");
        Add("LayerContextDelete", delete, editable, () => OnDeleteLayer(this, new()));
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("LayerContextClipping", Loc.T(layer.MaskSourceId is null ? "M_CreateClipping" : "M_ReleaseClipping"),
            editable && owner.CanToggleClippingMask(layer.Id), () => OnToggleClippingMask(this, new()));
        Add("LayerContextGroup", Loc.T("M_Group/Text"), editable && owner.LayerCount < 10_000, () => OnGroupLayers(this, new()));
        if (layer.IsGroupOrFalse) Add("LayerContextUngroup", Loc.T("M_Ungroup/Text"), editable, () => OnUngroupLayer(this, new()));
        Add("LayerContextMoveOut", Loc.T("M_MoveOut"), editable && layer.ParentId is not null, () => OnMoveOutOfFolder(this, new()));
        Add("LayerContextMerge", MergeMenuItem.Text, editable && CanMergeLayerContext(layer, ids), () => OnMergeLayers(this, new()));
        menu.Items.Add(new MenuFlyoutSeparator());
        var addMask = new MenuFlyoutSubItem { Name = "LayerContextAddMask", Text = Loc.T("M_ContextAddMask"), IsEnabled = editable && single && !hasMask };
        addMask.Items.Add(Item("LayerContextRevealMask", Loc.T("M_ContextRevealMask"), addMask.IsEnabled, () => CreateLayerMask(revealing: true, useSelection: true)));
        addMask.Items.Add(Item("LayerContextHideMask", Loc.T("M_ContextHideMask"), addMask.IsEnabled, () => CreateLayerMask(revealing: false, useSelection: true)));
        menu.Items.Add(addMask);
        void MaskAction(Action action) { effectSelection = null; paintOnMask = false; SyncPaintTargetControls(); action(); RefreshLayerPanel(); }
        Add("LayerContextToggleMask", Loc.T(layer.MaskEnabled == false ? "M_EnableMask" : "M_DisableMask"), editable && single && hasMask,
            () => MaskAction(() => OnToggleMaskEnabled(this, new())));
        Add("LayerContextDeleteMask", Loc.T("M_ContextDeleteMask"), editable && single && hasMask, () => MaskAction(() => OnRemoveMask(this, new())));
        Add("LayerContextLinkMask", Loc.T(layer.MaskLinked == false ? "M_LinkMask" : "M_UnlinkMask"),
            editable && hasMask && !layer.IsGroupOrFalse && layer.Adjustment is null, () => OnToggleMaskLink(this, new()));
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("LayerContextVisibility", Loc.T(layer.IsVisible ? "M_ContextHideLayer" : "M_ContextShowLayer"), editable,
            () => owner.SetVisible(layer.Id, !layer.IsVisible));
        return menu;
    }

    private bool CanMergeLayerContext(ProjectLayerRecord layer, HashSet<Guid> ids)
    {
        var layers = document!.Snapshot.Manifest.Layers;
        if (ids.Count > 1 || layer.IsGroupOrFalse)
        {
            var byId = layers.ToDictionary(candidate => candidate.Id);
            return layers.Any(candidate => !candidate.IsGroupOrFalse && Included(candidate));
            bool Included(ProjectLayerRecord candidate)
            {
                for (int depth = 0; depth < 64; depth++)
                {
                    if (ids.Contains(candidate.Id)) return true;
                    if (candidate.ParentId is not { } parent || !byId.TryGetValue(parent, out var next)) return false;
                    candidate = next;
                }
                return false;
            }
        }
        int index = layers.FindIndex(candidate => candidate.Id == layer.Id);
        return layers.Take(index).LastOrDefault(candidate => candidate.ParentId == layer.ParentId) is { IsGroupOrFalse: false };
    }

    private async Task<bool> OpenThumbnailContentAsync(Guid id)
    {
        if (document?.ActiveTextDraft?.LayerId == id && inlineEditor is not null) return true;
        if (await PrepareLayerContextAsync(id, mask: false) is null || !CanEditLayerCommands || SelectedLayer is not { } layer) return false;
        if (layer.Text is not null)
        {
            CommitInlineText(); OpenInlineText(layer);
            return document?.ActiveTextDraft?.LayerId == id;
        }
        if (layer.Adjustment is not null)
        {
            RebuildAdjustmentPanel(layer); LayerPropertiesScroll.StartBringIntoView();
            return AdjustmentPanel.Visibility == Visibility.Visible;
        }
        RenameSelectedLayer(); return renamingLayer;
    }
}
