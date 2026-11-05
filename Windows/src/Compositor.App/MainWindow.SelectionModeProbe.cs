#if DEBUG
using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.System;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task VerifySelectionModeButtonsAsync(Dictionary<string, object> report)
    {
        var scene = EditorDocument.Create(64, 48); OpenDocument(scene); await WaitForFramesAsync();
        ToolCombo.SelectedIndex = (int)CanvasTool.RectSelect; await Task.Delay(60); RootGrid.UpdateLayout();
        var buttons = new[] { "SelectionReplaceButton", "SelectionAddButton", "SelectionSubtractButton" }
            .Select(name => RootGrid.FindName(name) as ToggleButton ?? throw new InvalidOperationException("Selection modes need three visible buttons: " + name)).ToArray();
        static void Toggle(ToggleButton button) => ((IToggleProvider)new ToggleButtonAutomationPeer(button).GetPattern(PatternInterface.Toggle)).Toggle();
        void Exclusive(int index) => Require(buttons.Count(b => b.IsChecked == true) == 1 && buttons[index].IsChecked == true, "Selection mode has no exclusive visible state.");
        Exclusive(0);
        for (int i = 1; i < buttons.Length; i++)
        {
            var previous = buttons[i - 1].TransformToVisual(SelectionControls).TransformPoint(new(0, 0));
            var current = buttons[i].TransformToVisual(SelectionControls).TransformPoint(new(0, 0));
            Require(current.X >= previous.X + buttons[i - 1].ActualWidth && Math.Abs(current.Y - previous.Y) < 1, "Selection buttons are not shown side by side.");
        }
        Toggle(buttons[1]); Exclusive(1); Toggle(buttons[1]); Exclusive(1);
        Require(!scene.CanUndo && !scene.IsDirty, "Choosing a selection mode edited the document.");
        foreach (var item in new[] { CanvasTool.EllipseSelect, CanvasTool.Lasso, CanvasTool.Wand, CanvasTool.RectSelect })
        {
            ToolCombo.SelectedIndex = (int)item; await Task.Delay(40); RootGrid.UpdateLayout(); Exclusive(1);
            Require(SelectionControls.Visibility == Visibility.Visible && buttons.All(b => b.ActualWidth > 0), "A selection tool lost its shared mode buttons.");
        }
        Toggle(buttons[0]); Exclusive(0);
        Require(SelectionModeAtPress(VirtualKeyModifiers.Shift) == LassoSelectionMode.Add
            && SelectionModeAtPress(VirtualKeyModifiers.Menu) == LassoSelectionMode.Subtract
            && SelectionModeAtPress(VirtualKeyModifiers.None) == LassoSelectionMode.Replace, "Shift/Alt changed the persistent mode.");
        report["selectionModeButtonsAndModifiers"] = "passed";

        async Task Draw(int x0, int y0, int x1, int y1)
        {
            BeginMarqueeSelection(new(x0, y0), VirtualKeyModifiers.None);
            FinishMarqueeSelection(new(x0, y0), new(x1, y1), false); await WaitForSelectionReadyAsync();
        }
        await Draw(2, 2, 12, 12); Require(scene.ActiveSelection!.CoverageAt(5, 5) == 255, "New selection failed.");
        Toggle(buttons[1]); await Draw(16, 2, 24, 12);
        Require(scene.ActiveSelection!.CoverageAt(5, 5) == 255 && scene.ActiveSelection.CoverageAt(20, 6) == 255, "Add button replaced existing coverage.");
        Toggle(buttons[2]); await Draw(4, 4, 8, 8);
        Require(scene.ActiveSelection!.CoverageAt(5, 5) == 0 && scene.ActiveSelection.CoverageAt(20, 6) == 255, "Subtract button lost protected coverage.");
        scene.Undo(); await WaitForSelectionReadyAsync(); Require(scene.ActiveSelection!.CoverageAt(5, 5) == 255, "Subtract Undo failed.");
        scene.Redo(); await WaitForSelectionReadyAsync(); Require(scene.ActiveSelection!.CoverageAt(5, 5) == 0, "Subtract Redo failed.");
        await SaveWorkspaceCaptureAsync("selection-mode-buttons.png", ToolHeader);
        report["selectionModeCoverageAndUndo"] = "passed";
    }
}
#endif
