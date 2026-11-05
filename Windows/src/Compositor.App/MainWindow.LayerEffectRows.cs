using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Text.Json;
using Windows.System;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private (Guid Layer, LayerEffectKind Kind)? effectSelection;
    private Action<LayerEffectKind>? effectsPanelSelectKind;
    private (Guid Layer, LayerEffectKind Kind)? SelectedEffect => effectSelection is { } selected
        && selected.Layer == selectedLayerId && LayerEffects.Parse(SelectedLayer?.Effects)?.Contains(selected.Kind) == true
        ? selected : null;

    private void RefreshLayerEffectRows(LayerRow row)
    {
        if (row.EffectsJson.HasValue != row.Layer.Effects.HasValue || row.EffectsJson is { } previous
            && !JsonElement.DeepEquals(previous, row.Layer.Effects!.Value))
        {
            row.EffectsJson = row.Layer.Effects;
            row.Effects = LayerEffects.Parse(row.Layer.Effects);
        }
        foreach (var kind in row.EffectRows.Keys.Where(kind => row.Effects?.Contains(kind) != true).ToArray())
        {
            row.EffectsHost.Children.Remove(row.EffectRows[kind].Root);
            row.EffectRows.Remove(kind);
        }
        int index = 0;
        foreach (var (kind, _) in EffectKinds)
        {
            if (row.Effects?.Contains(kind) != true) continue;
            if (!row.EffectRows.TryGetValue(kind, out var controls))
            {
                controls = BuildLayerEffectRow(row, kind);
                row.EffectRows.Add(kind, controls);
                row.EffectsHost.Children.Insert(index, controls.Root);
            }
            bool enabled = row.Effects.IsEnabled(kind);
            controls.Eye.IsChecked = enabled;
            controls.Label.Opacity = enabled ? 1 : .5;
            controls.Root.Padding = new(38 + Math.Min(row.Depth, 8) * 24 + (row.Layer.MaskSourceId is null ? 0 : 24), 0, 0, 0);
            controls.Root.Background = effectSelection == (row.Layer.Id, kind)
                ? layerEffectAccent : null;
            AutomationProperties.SetName(controls.Eye, Loc.F(enabled ? "A_HideEffect" : "A_ShowEffect", EffectKindLabel(kind)));
            index++;
        }
    }

    private readonly Brush layerEffectAccent = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(77, 75, 153, 230));

    private (Grid Root, Button Label, CheckBox Eye) BuildLayerEffectRow(LayerRow row, LayerEffectKind kind)
    {
        var root = new Grid { Name = $"LayerEffect_{kind}", Height = 24 };
        root.ColumnDefinitions.Add(new() { Width = new(20) });
        root.ColumnDefinitions.Add(new());
        var eye = new CheckBox { MinWidth = 0, MinHeight = 0, Padding = new(0), Style = (Style)WorkspaceGrid.Resources["LayerEffectEyeStyle"] };
        eye.Checked += async (_, _) => { if (!suppressPanelEvents && !await ToggleLayerEffectAsync(row.Layer.Id, kind)) RefreshLayerPanel(); };
        eye.Unchecked += async (_, _) => { if (!suppressPanelEvents && !await ToggleLayerEffectAsync(row.Layer.Id, kind)) RefreshLayerPanel(); };
        var label = new Button { Content = EffectKindLabel(kind), FontSize = 11, Padding = new(8, 0, 0, 0),
            MinWidth = 0, MinHeight = 0, BorderThickness = new(0), HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        AutomationProperties.SetName(label, Loc.F("A_EffectRow", EffectKindLabel(kind)));
        ToolTipService.SetToolTip(label, Loc.T("A_EffectRowHelp"));
        label.Click += async (_, _) => await SelectLayerEffectAsync(row.Layer.Id, kind);
        label.DoubleTapped += async (_, args) => { args.Handled = true; await SelectLayerEffectAsync(row.Layer.Id, kind, editing: true); };
        var press = new Windows.Foundation.Point(double.NaN, double.NaN);
        label.PointerPressed += (_, args) =>
        {
            if (args.GetCurrentPoint(label).Properties.IsLeftButtonPressed && args.KeyModifiers.HasFlag(VirtualKeyModifiers.Menu)
                && !args.KeyModifiers.HasFlag(VirtualKeyModifiers.Control) && CanSelectLayerTarget)
                press = args.GetCurrentPoint(label).Position;
        };
        label.PointerMoved += async (_, args) =>
        {
            if (double.IsNaN(press.X)) return;
            var point = args.GetCurrentPoint(label);
            if (!point.Properties.IsLeftButtonPressed || !args.KeyModifiers.HasFlag(VirtualKeyModifiers.Menu)) { press = new(double.NaN, double.NaN); return; }
            double dx = point.Position.X - press.X, dy = point.Position.Y - press.Y;
            if (dx * dx + dy * dy < 9) return;
            press = new(double.NaN, double.NaN);
            var drag = BeginLayerEffectDrag(kind, row.Layer.Id);
            if (drag is null) return;
            try { await label.StartDragAsync(point); }
            catch (InvalidOperationException ex) { App.Log($"Effect row drag refused: {ex.Message}"); }
            finally { EndLayerPanelDrag(drag); }
        };
        label.DragStarting += (_, args) =>
        {
            if (!CanEditLayerCommands || CurrentLayerPanelDrag?.Effect != (kind, row.Layer.Id)) { args.Cancel = true; return; }
            args.Data.SetText($"{EffectDragProperty}|{kind}|{row.Layer.Id:N}");
        };
        Grid.SetColumn(label, 1); root.Children.Add(eye); root.Children.Add(label);
        return (root, label, eye);
    }

    private async Task<bool> SelectLayerEffectAsync(Guid id, LayerEffectKind kind, bool editing = false)
    {
        if (!CanSelectLayerTarget || document is not { } owner) return false;
        long request = ++layerSelectionRequest;
        var retired = documentRetired.Task;
        await FinishTransformBeforeOutputAsync();
        if (!ReferenceEquals(document, owner) || request != layerSelectionRequest || !await ApplyGradientDraftAsync()
            || windowClosed || !ReferenceEquals(document, owner) || request != layerSelectionRequest) return false;
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        QueueCanvasInput(owner, () =>
        {
            try
            {
                if (request != layerSelectionRequest || !ReferenceEquals(document, owner) || !CanSelectLayerTarget)
                { completed.TrySetResult(false); return; }
                if (effectsPanelLayer is { } panelLayer && panelLayer != id) CloseFloatingPanel("effects", commit: false);
                if (LayerEffects.Parse(owner.Snapshot.Manifest.Layers.FirstOrDefault(layer => layer.Id == id)?.Effects)?.Contains(kind) != true)
                { completed.TrySetResult(false); return; }
                SelectLayerRow(id); effectSelection = (id, kind);
                RefreshLayerPanel(); LayerList.Focus(FocusState.Programmatic);
                if (editing) OpenLayerEffects(kind);
                completed.TrySetResult(true);
            }
            catch (Exception ex) { completed.TrySetException(ex); }
        });
        try { return await WaitForInteractionWorkAsync(completed.Task, retired) && await completed.Task; }
        catch (Exception ex)
        {
            if (!windowClosed && ReferenceEquals(document, owner)) Report(Loc.T("S_EffectSelectionFailed"), ex);
            return false;
        }
    }

    private Task<bool> ToggleLayerEffectAsync(Guid id, LayerEffectKind kind) => ChangeLayerEffectAsync(id, kind, remove: false);
    private Task<bool> RemoveSelectedEffectAsync() => SelectedEffect is { } selected
        ? ChangeLayerEffectAsync(selected.Layer, selected.Kind, remove: true) : Task.FromResult(false);

    private async Task<bool> ChangeLayerEffectAsync(Guid id, LayerEffectKind kind, bool remove)
    {
        if (!CanEditLayerCommands || document is not { } owner) return false;
        var retired = documentRetired.Task;
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        QueueCanvasInput(owner, () =>
        {
            try
            {
                if (!ReferenceEquals(document, owner) || !CanEditLayerCommands || remove && SelectedEffect != (id, kind))
                { completed.TrySetResult(false); return; }
                // Settle the panel before replacing its effect so a later Cancel cannot restore the old value.
                if (effectsPanelLayer == id) CloseFloatingPanel("effects", commit: true);
                var effects = LayerEffects.Parse(owner.Snapshot.Manifest.Layers.FirstOrDefault(layer => layer.Id == id)?.Effects);
                if (effects?.Contains(kind) != true) { completed.TrySetResult(false); return; }
                bool enabled = !effects.IsEnabled(kind);
                if (remove) effectSelection = null;
                bool changed = owner.SetLayerEffects(id, remove ? effects.WithKind(kind, false) : effects.WithEnabled(kind, enabled));
                if (changed) SetStatus(Loc.F(remove ? "S_EffectRemoved" : enabled ? "S_EffectShown" : "S_EffectHidden", EffectKindLabel(kind)));
                completed.TrySetResult(changed);
            }
            catch (Exception ex) { completed.TrySetException(ex); }
        });
        try { return await WaitForInteractionWorkAsync(completed.Task, retired) && await completed.Task; }
        catch (Exception ex)
        {
            if (!windowClosed && ReferenceEquals(document, owner)) Report(Loc.T("S_EffectChangeFailed"), ex);
            return false;
        }
    }

    private void ClearLayerEffectSelection()
    {
        if (effectSelection is null) return;
        effectSelection = null; RefreshLayerPanel();
    }
}
