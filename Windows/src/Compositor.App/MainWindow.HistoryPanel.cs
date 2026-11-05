using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private Grid historyHost = null!;
    private ListView historyList = null!;
    private TextBlock historyHint = null!;
    private ToggleButton historyTab = null!, layersTab = null!;
    private bool historyVisible, syncingHistoryTabs, historyRefreshQueued;
    private EditorDocument? historyListOwner;
    private Guid historyListCurrent;
    private int historyListCount = -1;
    [Microsoft.UI.Xaml.Data.Bindable]
    public sealed class HistoryRow(Guid id, string label, bool future)
    {
        public Guid Id { get; } = id;
        public string Label { get; } = label;
        public bool Future { get; set; } = future;
        public override string ToString() => Label;
    }

    private void InitializeHistoryPanel()
    {
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        layersTab = NameQuickControl(new ToggleButton { Content = Loc.T("H_Layers"), IsChecked = true, MinWidth = 0, Padding = new(8, 3, 8, 3) }, "LayersTab", "H_Layers");
        historyTab = NameQuickControl(new ToggleButton { Content = Loc.T("H_Title"), MinWidth = 0, Padding = new(8, 3, 8, 3) }, "HistoryTab", "H_Title");
        layersTab.Checked += (_, _) => ShowHistoryPanel(false); historyTab.Checked += (_, _) => ShowHistoryPanel(true);
        layersTab.Unchecked += (_, _) => { if (!historyVisible) ShowHistoryPanel(false); };
        historyTab.Unchecked += (_, _) => { if (historyVisible) ShowHistoryPanel(true); };
        foreach (var text in LayersHeader.Children.OfType<TextBlock>().Where(text => !ReferenceEquals(text, LayerCountLabel)).ToArray()) LayersHeader.Children.Remove(text);
        tabs.Children.Add(layersTab); tabs.Children.Add(historyTab); LayersHeader.Children.Insert(0, tabs);
        LayerCountLabel.VerticalAlignment = VerticalAlignment.Center;

        historyHost = new Grid { Visibility = Visibility.Collapsed, RowSpacing = 6, Padding = new(0, 6, 0, 8) };
        historyHost.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        historyHost.RowDefinitions.Add(new() { Height = GridLength.Auto });
        historyList = NameQuickControl(new ListView { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = true, DisplayMemberPath = nameof(HistoryRow.Label),
            ItemContainerTransitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection(),
            HorizontalContentAlignment = HorizontalAlignment.Stretch }, "HistoryList", "H_Title");
        historyList.ItemClick += (_, args) => { if (args.ClickedItem is HistoryRow row) NavigateHistory(row.Id); };
        historyList.SelectionChanged += (_, _) =>
        {
            if (document is { } owner && historyList.SelectedItem is HistoryRow row && owner.CurrentHistoryId != row.Id) NavigateHistory(row.Id);
        };
        historyList.ContainerContentChanging += (_, args) =>
        {
            if (args.Item is not HistoryRow row || args.InRecycleQueue) return;
            args.ItemContainer.Opacity = row.Future ? .45 : 1;
            ToolTipService.SetToolTip(args.ItemContainer, row.Label);
            AutomationProperties.SetName(args.ItemContainer, row.Label);
        };
        historyHost.Children.Add(historyList);
        historyHint = new TextBlock { Text = Loc.T("H_Hint"), TextWrapping = TextWrapping.Wrap, FontSize = 11, Opacity = .7, Margin = new(12, 4, 12, 0) };
        Grid.SetRow(historyHint, 1); historyHost.Children.Add(historyHint);
        Grid.SetRow(historyHost, 1); Grid.SetRowSpan(historyHost, 4); LayersPane.Children.Add(historyHost);
        var command = new MenuFlyoutItem { Text = Loc.T("H_Title"), Name = "HistoryMenuItem" };
        command.Click += (_, _) => ShowHistoryPanel(!historyVisible);
        var viewMenu = MenuBarRoot.Items.OfType<MenuBarItem>().First(item => item.Items.Contains(SnapToggle));
        viewMenu.Items.Add(new MenuFlyoutSeparator()); viewMenu.Items.Add(command);
    }

    private void ShowHistoryPanel(bool show)
    {
        if (syncingHistoryTabs) return;
        historyVisible = show; syncingHistoryTabs = true;
        try { historyTab.IsChecked = show; layersTab.IsChecked = !show; }
        finally { syncingHistoryTabs = false; }
        historyHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        foreach (var element in new FrameworkElement[] { LayerAppearance, LayerList, LayerDropFeedback, LayerCommands })
            element.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        LayerPropertiesScroll.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        LayerCountLabel.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        if (show) RefreshHistoryPanel(force: true);
        else { ResetHistoryPanel(); RefreshLayerPanel(); ResizeLayerProperties(); }
    }

    private void ResetHistoryPanel()
    { historyListOwner = null; historyListCount = -1; historyList.ItemsSource = null; }

    private void QueueHistoryPanelRefresh()
    {
        if (historyRefreshQueued || !historyVisible || windowClosed) return;
        historyRefreshQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        { historyRefreshQueued = false; if (!windowClosed) RefreshHistoryPanel(); });
    }

    private void RefreshHistoryPanel(bool force = false)
    {
        if (!historyVisible || document is not { } owner) return;
        var states = owner.HistoryStates;
        if (!force && ReferenceEquals(owner, historyListOwner) && historyListCurrent == owner.CurrentHistoryId && historyListCount == states.Count) return;
        historyListOwner = owner; historyListCurrent = owner.CurrentHistoryId; historyListCount = states.Count;
        var rows = historyList.Items.Cast<HistoryRow>().ToArray();
        if (!rows.Select(row => row.Id).SequenceEqual(states.Select(state => state.Id)))
        {
            rows = states.Select((state, index) => new HistoryRow(state.Id,
                state.IsBaseline ? Loc.T(state.Operation == "Initial" ? "H_Initial" : "H_Oldest") : $"{index}  {HistoryOperationLabel(state.Operation)}", state.IsFuture)).ToArray();
            historyList.ItemsSource = rows;
        }
        for (int i = 0; i < rows.Length; i++)
        {
            rows[i].Future = states[i].IsFuture;
            if (historyList.ContainerFromIndex(i) is ListViewItem container) container.Opacity = rows[i].Future ? .45 : 1;
        }
        var current = rows.Single(row => row.Id == owner.CurrentHistoryId);
        historyList.SelectedItem = current; historyList.ScrollIntoView(current);
        historyHint.Text = Loc.T(owner.CanRedo ? "H_RedoHint" : "H_Hint");
    }

    private void NavigateHistory(Guid id)
    {
        if (document is not { } owner || ProjectEditsInFlight || selectionWorkResult is not null || !CanEditSelectionState || IsDeletePending)
        { SetStatus(Loc.T("H_Busy")); RefreshHistoryPanel(force: true); return; }
        Guid starting = owner.CurrentHistoryId;
        QueueCanvasInput(owner, () =>
        {
            if (!ReferenceEquals(document, owner) || owner.CurrentHistoryId != starting || owner.ActiveTextDraft is not null
                || selectionWorkResult is not null || !CanEditSelectionState) return;
            SetStatus(Loc.T(ApplyHistory(false, id) ? "H_Restored" : "H_Expired")); RefreshHistoryPanel(force: true);
        });
    }

    private static string HistoryOperationLabel(string operation)
    {
        string key = operation switch
        {
            "Brush" or "BeginGrowingMaskStroke" or "BeginStrokeCore" => "Brush",
            "Erase" => "Erase",
            "MoveLayerBy" => "Move", "ScaleLayer" => "Scale",
            "BeginTransform" or "UpdateTransform" or "CommitTransform" or "editor_layer_transform" => "Transform",
            "FillPixels" or "FillMask" or "ContentFillSelection" => "Fill",
            "InvertPixels" => "Invert", "CutSelectionMasked" or "CommitSelectionPlaneEdit" => "Clear",
            "Selection" => "Selection", "Deselect" => "Deselect",
            "AddTextLayer" or "UpdateTextLayer" or "Text" or "editor_text" => "Text",
            "AcceptGeneratedImage" => "AI", "CommitGradient" => "Gradient", "CommitHealingStroke" => "Healing",
            "BeginCloneStroke" or "OpenGrowingPlane" or "CloneStampApply" or "Clone" => "Clone",
            "BeginBlurStroke" => "Blur", "EndWarpStroke" => "Warp",
            "CommitPixelMove" or "CommitFloatingSelection" => "PixelsMove",
            "ImportImage" or "ImportPsd" or "PasteExternal" => "Import", "AddBlankLayer" => "NewLayer", "AddFolder" => "NewFolder",
            "AddShapeLayer" => "Shape", "DuplicateLayers" or "DuplicateLayersAt" or "editor_layer_duplicate" => "Duplicate",
            "CommitLayerDeletion" or "editor_layer_delete" => "Delete", "GroupLayers" => "Group", "Ungroup" => "Ungroup",
            "MergePicked" or "MergeLayers" or "MergeDown" or "MergeGroup" => "Merge",
            "PlaceLayer" or "PlaceLayers" or "MoveLayerWithinSiblings" or "MoveOutOfFolder" or "editor_layer_reorder" => "Reorder",
            "SetOpacity" or "editor_layer_opacity" => "Opacity", "SetBlendMode" or "editor_layer_blend" => "Blend",
            "SetVisible" or "editor_layer_visibility" => "Visibility", "RenameLayer" or "editor_layer_rename" => "Rename",
            "AddAdjustment" or "MutateAdjustment" or "MutateLevels" or "MutateCurves" or "editor_adjustment" => "Adjustment",
            "SetLayerEffects" or "CopyEffect" => "Effects",
            "AddRasterMask" or "RemoveMask" or "ReplaceLayerMask" or "SetMaskEnabled" or "SetMaskLinked" or "ToggleClippingMask" => "Mask",
            "SetCanvasBounds" or "SetCanvasSize" or "TrimCanvas" => "Canvas", "ResizeImage" => "Resize",
            "FlipCanvas" or "FlipLayers" => "Flip", "AddGuide" or "MoveGuide" or "RemoveGuide" or "ClearGuides" => "Guides",
            "editor_layout" => "Layout", "Batch" => "Batch", _ => "Pixels",
        };
        return Loc.T("H_" + key);
    }
}
