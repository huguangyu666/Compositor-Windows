#if DEBUG
using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task VerifyQuickToolsAsync(Dictionary<string, object> report)
    {
        var failures = new Dictionary<string, string>();
        T Control<T>(string name) where T : class => FilterFinishDescendants(RootGrid).OfType<FrameworkElement>()
            .FirstOrDefault(control => control.Name == name) as T ?? throw new InvalidOperationException("Missing quick control: " + name);
        static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
        async Task SetTool(CanvasTool value)
        {
            ToolCombo.SelectedIndex = (int)value;
            for (int i = 0; i < 200 && toolChangeApplied != toolChangeRevision; i++) await Task.Delay(10);
            await WaitForFramesAsync(); RootGrid.UpdateLayout();
        }
        async Task Wait(Func<bool> ready)
        {
            for (int i = 0; i < 400 && !ready(); i++) await Task.Delay(5);
            Require(ready(), "Quick command did not complete."); await WaitForFramesAsync();
        }
        async Task Check(string name, Func<Task> test)
        {
            try { await test(); report[name] = "passed"; }
            catch (Exception ex) { failures[name] = ex.Message; }
        }
        await Check("quickSelectionCommands", async () =>
        {
            var scene = EditorDocument.Create(64, 48); OpenDocument(scene); await SetTool(CanvasTool.RectSelect);
            var amount = Control<NumberBox>("QuickFeatherAmount"); var feather = Control<Button>("QuickFeatherApply");
            Require(!feather.IsEnabled, "Feather must require an active selection.");
            var selection = Selection.Rectangle(64, 48, 12, 10, 20, 20); scene.SetSelection(selection); await WaitForSelectionReadyAsync();
            amount.Value = 3; Invoke(feather); await Wait(() => scene.ActiveSelection?.Feather == 3);
            OnUndo(this, new()); await WaitForSelectionReadyAsync(); Require(ReferenceEquals(scene.ActiveSelection, selection), "Quick feather Undo changed original coverage.");
            Invoke(Control<Button>("QuickInvertSelection")); await Wait(() => scene.ActiveSelection?.CoverageAt(0, 0) == 255);
            Invoke(Control<Button>("QuickDeselect")); await Wait(() => scene.ActiveSelection is null);
            await SaveWorkspaceCaptureAsync("quick-selection.png", ToolHeader);
        });
        await Check("quickBrushNumbersAndPreview", async () =>
        {
            OpenDocument(EditorDocument.Create(64, 48)); await SetTool(CanvasTool.Brush);
            var size = Control<NumberBox>("QuickBrushSize"); var hardness = Control<NumberBox>("QuickBrushHardness"); var opacity = Control<NumberBox>("QuickBrushOpacity");
            size.Value = 47; hardness.Value = 35; opacity.Value = 62;
            Require(CurrentBrush.Radius == 47 && CurrentBrush.Hardness == .35 && CurrentBrush.Opacity == .62, "Brush numbers did not reach the real stroke settings.");
            BrushSizeSlider.Value = 23; Require(size.Value == 23, "Brush slider and number drifted.");
            Require(Control<Image>("QuickBrushTip").Source is WriteableBitmap { PixelWidth: >= 24 }, "The tip has no live preview.");
            await SetTool(CanvasTool.Eraser); await SetTool(CanvasTool.Brush);
            Require(size.Value == 23 && hardness.Value == 35 && opacity.Value == 62 && !document!.CanUndo, "Parked brush settings or history changed.");
            await SaveWorkspaceCaptureAsync("quick-brush.png", ToolHeader);
        });
        await Check("quickLayerLayoutAndUndo", async () =>
        {
            var scene = EditorDocument.Create(100, 80); var a = scene.Snapshot.Manifest.ActiveLayerId!.Value;
            scene.Snapshot.Manifest.Layers.Single().Transform = new() { Origin = new(10, 10), Size = new(20, 10) };
            var b = scene.ImportImage(new Surface(10, 10), "B").Id; var c = scene.ImportImage(new Surface(30, 10), "C").Id;
            scene.Snapshot.Manifest.Layers.Single(l => l.Id == b).Transform.Origin = new(40, 10);
            scene.Snapshot.Manifest.Layers.Single(l => l.Id == c).Transform.Origin = new(80, 10);
            scene = new(scene.Snapshot); OpenDocument(scene); await SetTool(CanvasTool.Move);
            void SelectAll() { selectingLayer = true; try { scene.SetActiveLayer(a); } finally { selectingLayer = false; } selectedLayerId = a; selectedLayerIds.Clear(); selectedLayerIds.UnionWith(new[] { a, b, c }); RefreshLayerPanel(); }
            SelectAll(); string before = ManifestJson.Serialize(scene.Snapshot.Manifest);
            Invoke(Control<Button>("QuickAlignLeft")); await Wait(() => scene.Snapshot.Manifest.Layers.All(l => l.Transform.Origin.X == 10));
            OnUndo(this, new()); await WaitForFramesAsync(); Require(before == ManifestJson.Serialize(scene.Snapshot.Manifest) && !scene.CanUndo, "Alignment did not undo atomically.");
            SelectAll(); Invoke(Control<Button>("QuickDistributeHorizontal")); await Wait(() => scene.Snapshot.Manifest.Layers.Single(l => l.Id == b).Transform.Origin.X == 50);
            OnUndo(this, new()); await WaitForFramesAsync(); SelectAll();
            await SaveWorkspaceCaptureAsync("quick-layout.png", ToolHeader);
            Invoke(Control<Button>("QuickGroupLayers")); await Wait(() => scene.LayerCount == 4);
            Require(SelectedLayer?.IsGroupOrFalse == true && selectedLayerIds.Count == 1, "Group command left stale child selection.");
            OnUndo(this, new()); await WaitForFramesAsync(); Require(before == ManifestJson.Serialize(scene.Snapshot.Manifest), "Grouping Undo changed the source layers.");
        });
        await Check("quickTextStyleAndUndo", async () =>
        {
            var scene = EditorDocument.Create(300, 180); var text = scene.AddTextLayer(new() { Content = "Quick text", FontName = "Segoe UI", FontSize = 24 }, new(10, 30))!;
            scene = new(scene.Snapshot); OpenDocument(scene); await SetTool(CanvasTool.Text);
            var font = Control<ComboBox>("QuickTextFont"); var size = Control<NumberBox>("QuickTextSize");
            var picker = ((Flyout)Control<Button>("QuickTextColor").Flyout).Content as ColorPicker ?? throw new InvalidOperationException("Missing text color picker.");
            var id = text.Id; string original = ManifestJson.Serialize(scene.Snapshot.Manifest);
            size.Value = 32; await Wait(() => scene.Snapshot.Manifest.Layers.Single(l => l.Id == id).Text!.FontSize == 32);
            OnUndo(this, new()); await WaitForFramesAsync(); Require(ManifestJson.Serialize(scene.Snapshot.Manifest) == original, "Text size Undo changed the source.");
            font.SelectedItem = InstalledFontNames.First(n => n != "Segoe UI");
            await Wait(() => scene.Snapshot.Manifest.Layers.Single(l => l.Id == id).Text!.FontName == (string)font.SelectedItem);
            string beforeColor = ManifestJson.Serialize(scene.Snapshot.Manifest);
            var colorButton = Control<Button>("QuickTextColor"); Invoke(colorButton); await Task.Delay(70);
            Require(scene.ActiveTextDraft is not null, "The color picker did not begin its atomic text edit.");
            picker.Color = Microsoft.UI.Colors.Blue; picker.Color = Microsoft.UI.Colors.Red;
            ((Flyout)colorButton.Flyout).Hide(); await Wait(() => scene.ActiveTextDraft is null);
            await Wait(() => scene.Snapshot.Manifest.Layers.Single(l => l.Id == id).Text!.Red == 1 && scene.Snapshot.Manifest.Layers.Single(l => l.Id == id).Text!.Green == 0);
            OnUndo(this, new()); await WaitForFramesAsync();
            string afterColorUndo = ManifestJson.Serialize(scene.Snapshot.Manifest);
            if (beforeColor != afterColorUndo) report["colorUndoDiagnostic"] = new { beforeColor, afterColorUndo, inlineDraftTrace };
            Require(beforeColor == afterColorUndo, "A color-picker gesture created intermediate Undo steps.");
            OnRedo(this, new()); await WaitForFramesAsync();
            ((IToggleProvider)new ToggleButtonAutomationPeer(Control<ToggleButton>("QuickTextCenter")).GetPattern(PatternInterface.Toggle)).Toggle();
            await Wait(() => scene.Snapshot.Manifest.Layers.Single(l => l.Id == id).Text!.Alignment == global::Compositor.Core.TextAlignment.Center);
            OpenInlineText(scene.Snapshot.Manifest.Layers.Single(l => l.Id == id)); await WaitForFramesAsync();
            Require(scene.ActiveTextDraft is not null, "Text draft did not open.");
            size.Value = 36; await Wait(() => scene.ActiveTextDraft?.Style.FontSize == 36);
            Require(scene.ActiveTextDraft is not null, "Using the toolbar closed the text draft.");
            CommitInlineText(); await WaitForFramesAsync();
            Require(scene.Snapshot.Manifest.Layers.Single(l => l.Id == id).Text!.FontSize == 36, "Toolbar style was lost when typing settled.");
            await SaveWorkspaceCaptureAsync("quick-text.png", ToolHeader);
        });
        await Check("aiContextBarOnlyForSelectionTools", async () =>
        {
            var scene = EditorDocument.Create(64, 48); OpenDocument(scene); await SetTool(CanvasTool.RectSelect);
            Require(AiActions.Visibility == Visibility.Collapsed, "AI actions obstruct a canvas without a selection.");
            scene.SetSelection(Selection.Rectangle(64, 48, 10, 10, 20, 20)); await WaitForSelectionReadyAsync();
            Require(AiActions.Visibility == Visibility.Visible, "Selection tools lost their contextual AI actions.");
            await SetTool(CanvasTool.Brush); Require(AiActions.Visibility == Visibility.Collapsed, "AI actions obstruct ordinary painting.");
            await SetTool(CanvasTool.Text); Require(AiActions.Visibility == Visibility.Collapsed, "AI actions obstruct text editing.");
            await SetTool(CanvasTool.RectSelect); OnAiExpand(this, new());
            Require(aiEditor is not null && AiActions.Visibility == Visibility.Collapsed, "The AI menu is inaccessible without its floating bar.");
            RetireAiEdit();
        });
        if (failures.Count > 0) { report["failures"] = failures; throw new InvalidOperationException(string.Join("; ", failures.Select(p => p.Key + ": " + p.Value))); }
    }
}
#endif
