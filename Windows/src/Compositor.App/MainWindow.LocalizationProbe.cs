#if DEBUG
using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Globalization;
using Windows.Graphics;

namespace Compositor.App;

public sealed partial class MainWindow
{
    internal async Task RunLocalizationProbeAsync()
    {
        var report = new Dictionary<string, object>();
        try
        {
            Require(!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("COMPOSITOR_PREFERENCES_DIRECTORY")), "Use isolated preferences for this probe.");
            await WaitForFramesAsync();
            string language = Environment.GetEnvironmentVariable("COMPOSITOR_PROBE_UI_LANGUAGE") ?? CultureInfo.CurrentUICulture.Name;
            bool chinese = UiLanguage.Resolve(language) == "zh-CN";
            Require(workspace.Current.Title == (chinese ? "未命名" : "Untitled")
                && document?.Snapshot.Manifest.Layers.Single().Name == (chinese ? "背景" : "Background")
                && !document.IsDirty && !document.CanUndo, "The fresh document's default names ignored the UI language.");
            await NewProjectTabAsync();
            Require(workspace.Tabs.Count == 2 && workspace.Current.Title == (chinese ? "未命名" : "Untitled")
                && document?.Snapshot.Manifest.Layers.Single().Name == (chinese ? "背景" : "Background"),
                "The New command did not use localized default names.");
            report["newDocumentNames"] = "passed";
            report["observed"] = new
            {
                language, culture = CultureInfo.CurrentUICulture.Name, region = CultureInfo.CurrentCulture.Name,
                primary = Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride,
                xamlFile = MenuBarRoot.Items.OfType<MenuBarItem>().First().Title,
                resourceFile = Loc.T("M_File/Title"), blend = BlendLabel.Text,
                resourceBlend = Loc.T("L_Blend/Text"), move = ToolLabel(CanvasTool.Move),
            };
            Require(MenuBarRoot.Items.OfType<MenuBarItem>().First().Title == (chinese ? "文件" : "File"), "The XAML menu did not use the detected UI language.");
            Require(BlendLabel.Text == (chinese ? "混合" : "Blend"), "The XAML layer label did not use the detected UI language.");
            Require(Loc.T("M_File/Title") == (chinese ? "文件" : "File") && ToolLabel(CanvasTool.Move) == (chinese ? "移动" : "Move"),
                "Code-built controls disagree with the detected UI language.");
            report["staticAndDynamicLanguage"] = "passed";

            var titles = MenuBarRoot.Items.OfType<MenuBarItem>().Select(item => item.Title).ToArray();
            Require(titles.SequenceEqual(chinese ? new[] { "文件", "编辑", "AI", "图层", "视图", "帮助" }
                : new[] { "File", "Edit", "AI", "Layer", "View", "Help" }), "Menu titles mix languages.");
            Require(LayerOpacityLabel.Text == (chinese ? "不透明度" : "Opacity")
                && AiRemoveButton.Content?.ToString() == (chinese ? "AI 移除" : "AI Remove")
                && ToolTipService.GetToolTip(ZoomOutButton)?.ToString() == (chinese ? "缩小" : "Zoom out"),
                "XAML labels, AI actions or attached tooltips ignored the UI language.");
            Require(PixelGridToggle.Text == Loc.T("V_ShowGrid/Text"), "The pixel-grid menu ignored its localized resource.");
            report["menusLabelsAndTooltips"] = "passed";

            var scene = EditorDocument.Create(400, 300); scene.Snapshot.Manifest.Layers[0].Name = "Custom background"; OpenDocument(scene);
            Require(scene.Snapshot.Manifest.Layers[0].Name == "Custom background", "Opening a document translated an existing layer name.");
            ToolCombo.SelectedIndex = (int)CanvasTool.RectSelect; await WaitForFramesAsync(); RootGrid.UpdateLayout();
            var buttons = new[] { SelectionReplaceButton, SelectionAddButton, SelectionSubtractButton };
            Require(buttons.Select(button => button.Content?.ToString()).SequenceEqual(chinese ? new[] { "新建", "添加", "减去" }
                : new[] { "New", "Add", "Subtract" }), "Selection buttons ignored the UI language.");
            Require(AutomationProperties.GetName(SelectionAddButton) == (chinese ? "选区模式: 添加" : "Selection mode: Add"),
                "Selection accessibility text disagrees with the visible language.");
            await SaveWorkspaceCaptureAsync($"localization-{language}-selection.png");
            report["selectionButtonsAndAccessibility"] = "passed";

            AppWindow.Resize(new SizeInt32(800, 600));
            var clipped = new List<string>();
            var captions = new List<object>();
            foreach (var (canvasTool, combo) in new[] { (CanvasTool.Eyedropper, EyedropperTargetCombo), (CanvasTool.Healing, HealingModeCombo),
                (CanvasTool.Gradient, GradientShapeCombo), (CanvasTool.Gradient, GradientStyleCombo), (CanvasTool.Lasso, LassoKindCombo),
                (CanvasTool.Crop, CropRatioCombo), (CanvasTool.Shape, ShapeKindCombo) })
            {
                ToolCombo.SelectedIndex = (int)canvasTool;
                for (int index = 0; index < combo.Items.Count; index++)
                {
                    combo.SelectedIndex = index; await WaitForFramesAsync(); RootGrid.UpdateLayout();
                    string value = ((ComboBoxItem)combo.SelectedItem).Content.ToString()!;
                    var label = Descendants(combo).OfType<TextBlock>().First(text => text.Text == value);
                    var measured = new TextBlock { Text = value, FontFamily = label.FontFamily, FontSize = label.FontSize, FontWeight = label.FontWeight };
                    measured.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                    double available = label.ActualWidth;
                    // ComboBox can arrange full-width text inside a smaller clipped template slot.
                    for (FrameworkElement element = label; element != combo && VisualTreeHelper.GetParent(element) is UIElement parent;
                        element = (FrameworkElement)parent)
                    {
                        var slot = Microsoft.UI.Xaml.Controls.Primitives.LayoutInformation.GetLayoutSlot(element);
                        var origin = label.TransformToVisual(parent).TransformPoint(new(0, 0));
                        available = Math.Min(available, slot.Right - element.Margin.Right - origin.X);
                    }
                    captions.Add(new { combo.Name, value, available, needed = measured.DesiredSize.Width });
                    if (available + 1 < measured.DesiredSize.Width) clipped.Add(combo.Name + ": " + value);
                }
                combo.SelectedIndex = 0;
            }
            report["optionCaptions"] = captions;
            Require(clipped.Count == 0, "Toolbar clips localized option text: " + string.Join("; ", clipped));
            report["localizedOptionLayout"] = "passed";
            ToolCombo.SelectedIndex = (int)CanvasTool.Gradient; await WaitForFramesAsync(); RootGrid.UpdateLayout();
            await SaveWorkspaceCaptureAsync($"localization-{language}-narrow.png");

            OpenAiEditor(AiOperation.Expand); await WaitForFramesAsync(); RootGrid.UpdateLayout();
            var panel = floatingPanels[AiPanelKey];
            var body = panel.Body.Children.OfType<StackPanel>().Single();
            Require(body.Children.OfType<TextBox>().Single(box => box.Name == "AiPrompt").Header?.ToString()
                    == (chinese ? "描述想要的效果" : "Describe the edit")
                && body.Children.OfType<Button>().Single(button => button.Name == "AiGenerate").Content?.ToString()
                    == (chinese ? "生成" : "Generate")
                && panel.Confirm.Content?.ToString() == (chinese ? "接受为新图层" : "Accept as new layer")
                && panel.Cancel.Content?.ToString() == (chinese ? "取消" : "Cancel"), "AI editor or shared panel buttons mix languages.");
            Require(aiEditor?.Work is null && !scene.CanUndo, "Inspecting language started AI work or edited the document.");
            await SaveWorkspaceCaptureAsync($"localization-{language}-ai.png");
            RetireAiEdit();
            report["aiPanelAndSharedButtons"] = "passed";
            report["status"] = "passed";
        }
        catch (Exception ex) { report["status"] = "failed"; report["error"] = ex.ToString(); }
        finally { WriteProbeReport("localization-probe.json", report); forceClose = true; Close(); }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
#endif
