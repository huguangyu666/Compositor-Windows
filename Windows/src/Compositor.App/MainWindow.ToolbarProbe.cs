#if DEBUG
using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Windows.Foundation;
using Windows.Graphics;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task VerifyToolbarAsync(Dictionary<string, object> report)
    {
        static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
        Rect Bounds(FrameworkElement item, FrameworkElement parent) => item.TransformToVisual(parent).TransformBounds(new(0, 0, item.ActualWidth, item.ActualHeight));
        async Task SetTool(CanvasTool item)
        {
            ToolCombo.SelectedIndex = (int)item;
            for (int i = 0; i < 200 && toolChangeApplied != toolChangeRevision; i++) await Task.Delay(10);
            RootGrid.UpdateLayout(); await Task.Delay(30);
            Require(toolChangeApplied == toolChangeRevision && tool == item, "Toolbar tool switch did not settle.");
        }
        OpenDocument(EditorDocument.Create(200, 140)); await WaitForFramesAsync();
        double dpi = RootGrid.XamlRoot.RasterizationScale;
        AppWindow.Resize(new SizeInt32((int)(1280 * dpi), (int)(800 * dpi))); await Task.Delay(150); RootGrid.UpdateLayout();
        var palette = Bounds(PaletteGrid, ToolRailHost);
        await SaveWorkspaceCaptureAsync("toolbar-check.png");
        Require(palette.Top >= 0 && palette.Bottom <= ToolRailHost.ActualHeight,
            $"The default toolbar hides the palette below its viewport: palette={palette}, railHeight={ToolRailScroll.ActualHeight}, toolsHeight={ToolButtons.ActualHeight}, railWidth={ToolRailScroll.ActualWidth}, offset={ToolRailScroll.VerticalOffset}.");
        double canvasHeight = CanvasScroll.ActualHeight;
        foreach (var item in Enum.GetValues<CanvasTool>())
        {
            await SetTool(item);
            var bounds = Bounds(toolButtons[item], ToolRailScroll);
            Require(bounds.Top >= 0 && bounds.Bottom <= ToolRailScroll.ActualHeight, "A default toolbar tool is off screen: " + item);
            Require(toolButtons.Count(pair => pair.Value.IsChecked == true) == 1 && toolButtons[item].IsChecked == true, "Toolbar lost its exclusive active state.");
            Require(Math.Abs(canvasHeight - CanvasScroll.ActualHeight) < 1, "Changing tools changed the canvas height.");
        }
        report["allToolsAndPaletteVisible"] = "passed";

        await SetTool(CanvasTool.Brush); BrushSizeSlider.Value = 43;
        var toggle = RootGrid.FindName("ToolRailLayoutButton") as Button ?? throw new InvalidOperationException("The single/double column switch is missing.");
        var move = Bounds(toolButtons[CanvasTool.Move], ToolRailScroll); var rect = Bounds(toolButtons[CanvasTool.RectSelect], ToolRailScroll);
        Require(Math.Abs(move.Top - rect.Top) < 1 && rect.Left > move.Right, "The default rail does not use two columns.");
        Invoke(toggle); await Task.Delay(60); RootGrid.UpdateLayout();
        move = Bounds(toolButtons[CanvasTool.Move], ToolRailScroll); rect = Bounds(toolButtons[CanvasTool.RectSelect], ToolRailScroll);
        Require(Math.Abs(move.Left - rect.Left) < 1 && rect.Top >= move.Bottom && tool == CanvasTool.Brush && BrushSizeSlider.Value == 43,
            "Changing toolbar density changed the tool/settings or failed to form one column.");
        Require(ReadFlag("tool-rail-single-column", false), "Toolbar column preference was not saved.");
        await SetTool(CanvasTool.Zoom);
        var zoom = Bounds(toolButtons[CanvasTool.Zoom], ToolRailScroll);
        Require(zoom.Top >= -1 && zoom.Bottom <= ToolRailScroll.ActualHeight + 1, "A shortcut-selected tool was not brought into view.");
        Invoke(toggle); await Task.Delay(60); RootGrid.UpdateLayout();
        Require(!ReadFlag("tool-rail-single-column", true), "Toolbar did not save its return to two columns.");
        await SetTool(CanvasTool.Move); await SaveWorkspaceCaptureAsync("toolbar-wide.png");
        report["columnSwitchPreservesEditingState"] = "passed";

        AppWindow.Resize(new SizeInt32((int)(800 * dpi), (int)(600 * dpi))); await Task.Delay(150); await SetTool(CanvasTool.Gradient); RootGrid.UpdateLayout(); await Task.Delay(40);
        palette = Bounds(PaletteGrid, ToolRailHost);
        Require(palette.Top >= 0 && palette.Bottom <= ToolRailHost.ActualHeight, "A short window hides the pinned palette.");
        var next = RootGrid.FindName("ToolOptionsNextButton") as Button ?? throw new InvalidOperationException("Tool options have no visible overflow navigation.");
        var previous = (Button)RootGrid.FindName("ToolOptionsPreviousButton");
        Require(ToolOptionsScroll.ScrollableWidth > 0 && next.Visibility == Visibility.Visible && next.IsEnabled && !previous.IsEnabled,
            "Overflow controls do not describe the start of the options strip.");
        Invoke(next); await Task.Delay(70); RootGrid.UpdateLayout();
        Require(ToolOptionsScroll.HorizontalOffset > 0 && previous.IsEnabled, "The options arrow did not reveal more controls.");
        for (int i = 0; i < 20 && next.IsEnabled; i++) { Invoke(next); await Task.Delay(25); }
        Require(ToolOptionsScroll.HorizontalOffset >= ToolOptionsScroll.ScrollableWidth - 1 && !next.IsEnabled, "The end of the options strip is unreachable.");
        await SaveWorkspaceCaptureAsync("toolbar-narrow.png");
        await SetTool(CanvasTool.Brush); await Task.Delay(60);
        Require(ToolOptionsScroll.HorizontalOffset < 1, "A tool switch retained another tool's scrolled options.");
        report["optionsOverflowNavigation"] = "passed";
    }
}
#endif
