using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private LassoSelectionMode selectionMode;
    private bool synchronizingSelectionMode;

    private static string SelectionModeCaption(LassoSelectionMode mode) => Loc.T(mode switch
    {
        LassoSelectionMode.Add => "SelectionMode_Add", LassoSelectionMode.Subtract => "SelectionMode_Subtract", _ => "SelectionMode_New",
    });
    private static string SelectionModeName(LassoSelectionMode mode) => Loc.T("A_SelectionMode") + ": " + SelectionModeCaption(mode);

    private void InitializeSelectionModes()
    {
        foreach (var (button, mode) in new[] { (SelectionReplaceButton, LassoSelectionMode.Replace), (SelectionAddButton, LassoSelectionMode.Add), (SelectionSubtractButton, LassoSelectionMode.Subtract) })
        {
            button.Tag = mode; button.Content = SelectionModeCaption(mode);
            AutomationProperties.SetName(button, SelectionModeName(mode));
            ToolTipService.SetToolTip(button, SelectionModeName(mode) + (mode == LassoSelectionMode.Add ? " (Shift)" : mode == LassoSelectionMode.Subtract ? " (Alt)" : ""));
        }
        SetSelectionMode(LassoSelectionMode.Replace);
    }

    private void OnSelectionModeChanged(object sender, RoutedEventArgs args)
    {
        if (synchronizingSelectionMode || sender is not ToggleButton { Tag: LassoSelectionMode mode } button) return;
        SetSelectionMode(button.IsChecked == true ? mode : selectionMode);
    }

    private void SetSelectionMode(LassoSelectionMode mode)
    {
        bool changed = selectionMode != mode; selectionMode = mode;
        synchronizingSelectionMode = true;
        try
        {
            SelectionReplaceButton.IsChecked = mode == LassoSelectionMode.Replace;
            SelectionAddButton.IsChecked = mode == LassoSelectionMode.Add;
            SelectionSubtractButton.IsChecked = mode == LassoSelectionMode.Subtract;
        }
        finally { synchronizingSelectionMode = false; }
        if (changed) { CancelLasso(); RefreshObjectSelectionOptions(); }
    }
}
