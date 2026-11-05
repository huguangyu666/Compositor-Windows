using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private const string SingleColumnToolsPreference = "tool-rail-single-column";
    private readonly List<Grid> toolButtonGroups = [];
    private bool singleColumnTools, toolbarOverflowQueued;

    private void InitializeToolbarLayout()
    {
        singleColumnTools = ReadFlag(SingleColumnToolsPreference, false);
        ArrangeToolRail();
        foreach (var (button, key) in new[] { (ToolOptionsPreviousButton, "W_PreviousOptions"), (ToolOptionsNextButton, "W_NextOptions") })
        {
            AutomationProperties.SetName(button, Loc.T(key)); ToolTipService.SetToolTip(button, Loc.T(key));
        }
        ToolOptionsScroll.ViewChanged += (_, _) => QueueToolbarOverflowRefresh();
        ToolOptionsScroll.SizeChanged += (_, _) => QueueToolbarOverflowRefresh();
        ToolOptionsPanel.SizeChanged += (_, _) => QueueToolbarOverflowRefresh();
        QueueToolbarOverflowRefresh();
    }

    private void OnToolRailLayoutChanged(object sender, RoutedEventArgs args)
    {
        singleColumnTools = !singleColumnTools; ArrangeToolRail();
        WriteFlag(SingleColumnToolsPreference, singleColumnTools);
    }

    private void ArrangeToolRail()
    {
        int columns = singleColumnTools ? 1 : 2;
        ToolsColumn.Width = new(singleColumnTools ? 56 : 88);
        ToolRailScroll.Width = ToolsColumn.Width.Value;
        foreach (var group in toolButtonGroups)
        {
            group.ColumnDefinitions.Clear(); group.RowDefinitions.Clear();
            for (int i = 0; i < columns; i++) group.ColumnDefinitions.Add(new() { Width = new(40) });
            for (int i = 0; i < (group.Children.Count + columns - 1) / columns; i++) group.RowDefinitions.Add(new() { Height = new(40) });
            for (int i = 0; i < group.Children.Count; i++) { Grid.SetColumn((FrameworkElement)group.Children[i], i % columns); Grid.SetRow((FrameworkElement)group.Children[i], i / columns); }
        }
        string label = Loc.T(singleColumnTools ? "W_DoubleColumnTools" : "W_SingleColumnTools");
        ToolRailLayoutButton.Content = singleColumnTools ? "»" : "«";
        AutomationProperties.SetName(ToolRailLayoutButton, label); ToolTipService.SetToolTip(ToolRailLayoutButton, label);
        ToolRailScroll.ChangeView(null, 0, null, true);
    }

    private void OnToolOptionsPrevious(object sender, RoutedEventArgs args) => ScrollToolOptions(-1);
    private void OnToolOptionsNext(object sender, RoutedEventArgs args) => ScrollToolOptions(1);
    private void ScrollToolOptions(int direction) => ToolOptionsScroll.ChangeView(
        Math.Clamp(ToolOptionsScroll.HorizontalOffset + direction * Math.Max(160, ToolOptionsScroll.ViewportWidth * .7), 0, ToolOptionsScroll.ScrollableWidth), null, null, true);

    private void QueueToolbarOverflowRefresh()
    {
        if (toolbarOverflowQueued) return;
        toolbarOverflowQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            toolbarOverflowQueued = false;
            bool overflow = ToolOptionsScroll.ScrollableWidth > 1;
            ToolOptionsPreviousButton.Visibility = ToolOptionsNextButton.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
            ToolOptionsPreviousButton.IsEnabled = ToolOptionsScroll.HorizontalOffset > 1;
            ToolOptionsNextButton.IsEnabled = ToolOptionsScroll.HorizontalOffset < ToolOptionsScroll.ScrollableWidth - 1;
        });
    }
}
