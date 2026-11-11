using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using System.Text.Json.Nodes;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private bool quickToolsReady;
    private NumberBox quickFeatherAmount = null!;
    private Button quickFeatherApply = null!, quickInvertSelection = null!, quickDeselect = null!;
    private StackPanel quickLayout = null!;
    private readonly List<(Button Button, int Minimum)> quickLayoutButtons = [];

    private T NameQuickControl<T>(T control, string name, string label) where T : FrameworkElement
    {
        control.Name = name;
        AutomationProperties.SetName(control, Loc.T(label)); AutomationProperties.SetAutomationId(control, name);
        ToolTipService.SetToolTip(control, Loc.T(label)); return control;
    }

    private Button QuickButton(string name, string label, Action action, string? path = null)
    {
        var button = NameQuickControl(new Button { Height = 32, MinWidth = 0, Padding = new(8, 4, 8, 4), Content = Loc.T(label) }, name, label);
        if (path is not null)
        {
            button.Width = 32; button.Padding = new(0);
            button.Content = XamlReader.Load($"<Viewbox xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Width='18' Height='18'><Canvas Width='24' Height='24'><Path Data='{path}' Stroke='#FFE5E5E5' StrokeThickness='1.5' StrokeLineJoin='Round'/></Canvas></Viewbox>");
        }
        button.Click += (_, _) => action(); return button;
    }

    private NumberBox QuickNumber(string name, string label, double min, double max, double value) => NameQuickControl(new NumberBox
    {
        Width = 68, Minimum = min, Maximum = max, Value = value, SmallChange = 1, LargeChange = 10,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden, ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten,
    }, name, label);

    private void InitializeQuickTools()
    {
        quickFeatherAmount = QuickNumber("QuickFeatherAmount", "QT_Feather", 1, 250, lastSelectionAmounts[SelectionModification.Feather]);
        quickFeatherApply = QuickButton("QuickFeatherApply", "QT_Feather", () =>
        {
            if (!double.IsFinite(quickFeatherAmount.Value)) return;
            int amount = (int)Math.Clamp(quickFeatherAmount.Value, 1, 250);
            lastSelectionAmounts[SelectionModification.Feather] = amount;
            ApplySelectionModification(SelectionModification.Feather, amount);
        });
        quickInvertSelection = QuickButton("QuickInvertSelection", "QT_Invert", () => OnInvertSelection(this, new()));
        quickDeselect = QuickButton("QuickDeselect", "QT_Deselect", () => OnDeselect(this, new()));
        SelectionControls.Children.Add(quickFeatherAmount); SelectionControls.Children.Add(quickFeatherApply);
        SelectionControls.Children.Add(quickInvertSelection); SelectionControls.Children.Add(quickDeselect);

        quickLayout = new() { Orientation = Orientation.Horizontal, Spacing = 4, Visibility = Visibility.Collapsed };
        ToolOptionsPanel.Children.Insert(1, quickLayout);
        var commands = new (string Name, string Label, string Operation, string Value, string Path)[]
        {
            ("QuickAlignLeft", "QT_AlignLeft", "align", "left", "M4,2 L4,22 M7,5 L21,5 L21,10 L7,10 Z M7,14 L16,14 L16,19 L7,19 Z"),
            ("QuickAlignCenter", "QT_AlignCenter", "align", "center", "M12,2 L12,22 M3,5 L21,5 L21,10 L3,10 Z M7,14 L17,14 L17,19 L7,19 Z"),
            ("QuickAlignRight", "QT_AlignRight", "align", "right", "M20,2 L20,22 M3,5 L17,5 L17,10 L3,10 Z M8,14 L17,14 L17,19 L8,19 Z"),
            ("QuickAlignTop", "QT_AlignTop", "align", "top", "M2,4 L22,4 M5,7 L10,7 L10,21 L5,21 Z M14,7 L19,7 L19,16 L14,16 Z"),
            ("QuickAlignMiddle", "QT_AlignMiddle", "align", "middle", "M2,12 L22,12 M5,3 L10,3 L10,21 L5,21 Z M14,7 L19,7 L19,17 L14,17 Z"),
            ("QuickAlignBottom", "QT_AlignBottom", "align", "bottom", "M2,20 L22,20 M5,3 L10,3 L10,17 L5,17 Z M14,8 L19,8 L19,17 L14,17 Z"),
            ("QuickDistributeHorizontal", "QT_DistributeHorizontal", "distribute", "horizontal", "M3,4 L7,4 L7,20 L3,20 Z M10,7 L14,7 L14,17 L10,17 Z M17,4 L21,4 L21,20 L17,20 Z"),
            ("QuickDistributeVertical", "QT_DistributeVertical", "distribute", "vertical", "M4,3 L20,3 L20,7 L4,7 Z M7,10 L17,10 L17,14 L7,14 Z M4,17 L20,17 L20,21 L4,21 Z"),
        };
        foreach (var command in commands)
        {
            var button = QuickButton(command.Name, command.Label, () => RunQuickLayout(command.Operation, command.Value, command.Label), command.Path);
            quickLayout.Children.Add(button); quickLayoutButtons.Add((button, command.Operation == "distribute" ? 3 : 2));
        }
        var group = QuickButton("QuickGroupLayers", "QT_Group", () => RunQuickLayout("group", "", "QT_Group"));
        quickLayout.Children.Add(group); quickLayoutButtons.Add((group, 2));
        InitializeQuickBrushControls(); InitializeQuickTextControls();
        quickToolsReady = true; RefreshQuickToolControls();
    }

    private int QuickLayoutRootCount()
    {
        if (document is null || selectedLayerIds.Count < 2) return selectedLayerIds.Count;
        var byId = document.Snapshot.Manifest.Layers.ToDictionary(l => l.Id);
        return selectedLayerIds.Count(id =>
        {
            if (!byId.TryGetValue(id, out var layer)) return false;
            for (var parent = layer.ParentId; parent is { } p; parent = byId[p].ParentId)
                if (selectedLayerIds.Contains(p)) return false;
            return true;
        });
    }

    private void RefreshQuickToolControls()
    {
        if (!quickToolsReady) return;
        quickFeatherAmount.Visibility = quickFeatherApply.Visibility = quickInvertSelection.Visibility = quickDeselect.Visibility
            = tool == CanvasTool.ObjectSelect ? Visibility.Collapsed : Visibility.Visible;
        bool selected = document?.ActiveSelection is { IsEmpty: false };
        quickFeatherAmount.IsEnabled = quickFeatherApply.IsEnabled = quickDeselect.IsEnabled = selected && CanEditSelectionState;
        quickInvertSelection.IsEnabled = document is not null && CanEditSelectionState;
        int roots = tool == CanvasTool.Move ? QuickLayoutRootCount() : 0;
        bool show = roots >= 2;
        if (show && quickLayout.Visibility != Visibility.Visible) ToolOptionsScroll.ChangeView(0, null, null, true);
        quickLayout.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (button, min) in quickLayoutButtons) button.IsEnabled = roots >= min && CanEditSelectionState && cropRectangle is null
            && document is { } owner && selectedLayerIds.All(id => owner.CanEditLayer(id, min == 2 && button.Name == "QuickGroupLayers" ? LayerLocks.None : LayerLocks.Position, branch: true));
        RefreshQuickTextControls();
    }

    private async void RunQuickLayout(string operation, string value, string label)
    {
        if (document is not { } owner || !CanEditSelectionState) return;
        var ids = selectedLayerIds.ToArray();
        await FinishTransformBeforeOutputAsync();
        if (!ReferenceEquals(document, owner) || !selectedLayerIds.SetEquals(ids)) return;
        QueueCanvasInput(owner, () =>
        {
            if (!CanEditLayerCommands || !selectedLayerIds.SetEquals(ids)) return;
            try
            {
                if (operation == "group")
                {
                    var folder = owner.GroupLayers(ids);
                    if (folder is not null) SelectEditorLayer(owner, folder.Id);
                }
                else
                {
                    var args = new JsonObject { ["operation"] = operation, ["layerIds"] = new JsonArray(ids.Select(id => (JsonNode?)JsonValue.Create(id.ToString())).ToArray()) };
                    args[operation == "align" ? "alignment" : "axis"] = value;
                    EditorAutomation.Apply(owner, "editor_layout", args);
                }
                SetStatus(Loc.T(label));
            }
            catch (ArgumentException ex) { SetStatus(ex.Message); }
        });
    }
}
