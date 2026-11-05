using System.Globalization;
using Compositor.Core;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private (EditorDocument Owner, Guid Id, object Key)? opacityEdit;
    private (double X, double Value, bool Moved)? opacityScrub;
    private bool syncingOpacity, applyingOpacity;

    private void InitializeOpacityControls()
    {
        OpacitySlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => BeginOpacityEdit()), true);
        OpacitySlider.PointerReleased += OnOpacityCommit;
        OpacitySlider.PointerCanceled += OnOpacityCommit;
        OpacitySlider.LostFocus += (_, _) => EndOpacityEdit();
        OpacitySlider.PreviewKeyUp += (_, _) => EndOpacityEdit();
        OpacityBox.PreviewKeyDown += (_, args) =>
        {
            if (HandleOpacityFieldKey(args.Key, SelectionKeyModifiers())) args.Handled = true;
        };
        OpacityBox.LostFocus += (_, _) => FinishOpacityField();
        LayerOpacityScrub.SetCursor(InputSystemCursorShape.SizeWestEast);
        LayerOpacityScrub.PointerPressed += (_, args) =>
        {
            if (!args.GetCurrentPoint(LayerOpacityScrub).Properties.IsLeftButtonPressed
                || !BeginOpacityScrub(args.GetCurrentPoint(RootGrid).Position.X)) return;
            if (!LayerOpacityScrub.CapturePointer(args.Pointer)) EndOpacityEdit();
            args.Handled = true;
        };
        LayerOpacityScrub.PointerMoved += (_, args) =>
        {
            if (opacityScrub is null) return;
            MoveOpacityScrub(args.GetCurrentPoint(RootGrid).Position.X); args.Handled = true;
        };
        LayerOpacityScrub.PointerReleased += (_, args) =>
        {
            EndOpacityEdit(); LayerOpacityScrub.ReleasePointerCapture(args.Pointer); args.Handled = true;
        };
        LayerOpacityScrub.PointerCanceled += OnOpacityCommit;
        LayerOpacityScrub.PointerCaptureLost += OnOpacityCommit;
    }

    private bool BeginOpacityEdit()
    {
        if (!CanEditLayerCommands || selectedLayerIds.Count != 1 || document is not { } owner || SelectedLayer is not { } layer) return false;
        if (opacityEdit is { } current && ReferenceEquals(current.Owner, owner) && current.Id == layer.Id) return true;
        EndOpacityEdit(); opacityEdit = (owner, layer.Id, new object()); return true;
    }

    private void EndOpacityEdit()
    {
        var edit = opacityEdit; opacityEdit = null; opacityScrub = null;
        if (edit is not { } ended) return;
        ended.Owner.EndTransform(ended.Key);
        if (!windowClosed) UpdateTitle();
    }

    private bool ApplyOpacity(double percent, bool grouped)
    {
        if (!double.IsFinite(percent) || !CanEditLayerCommands || selectedLayerIds.Count != 1
            || document is not { } owner || SelectedLayer is not { } layer) return false;
        percent = Math.Clamp(percent, 0, 100);
        syncingOpacity = true;
        try { OpacitySlider.Value = OpacityBox.Value = percent; OpacityLabel.Text = $"{Math.Round(percent)}%"; }
        finally { syncingOpacity = false; }
        if ((layer.Opacity ?? 1) == percent / 100) return false;
        object? key = grouped && opacityEdit is { } edit && ReferenceEquals(edit.Owner, owner) && edit.Id == layer.Id ? edit.Key : null;
        MarkLayerBandDirty(layer.Id);
        applyingOpacity = true;
        try { return owner.SetOpacity(layer.Id, percent / 100, key); }
        finally { applyingOpacity = false; }
    }

    private void OnOpacityChanged(object sender, RoutedEventArgs e)
    {
        if (syncingOpacity || suppressPanelEvents || !BeginOpacityEdit()) return;
        ApplyOpacity(OpacitySlider.Value, grouped: true);
    }

    private void OnOpacityBoxChanged(NumberBox sender, NumberBoxValueChangedEventArgs e)
    {
        if (syncingOpacity || suppressPanelEvents) return;
        EndOpacityEdit(); ApplyOpacity(sender.Value, grouped: false);
    }

    private void OnOpacityCommit(object sender, PointerRoutedEventArgs e) => EndOpacityEdit();

    private bool BeginOpacityScrub(double x)
    {
        if (!double.IsFinite(x) || !BeginOpacityEdit()) return false;
        opacityScrub = (x, (SelectedLayer!.Opacity ?? 1) * 100, false); return true;
    }

    private void MoveOpacityScrub(double x)
    {
        if (opacityScrub is not { } scrub || opacityEdit is not { } edit || !double.IsFinite(x)) return;
        if (!ReferenceEquals(document, edit.Owner) || SelectedLayer?.Id != edit.Id) { EndOpacityEdit(); return; }
        double dx = x - scrub.X;
        if (!scrub.Moved && Math.Abs(dx) < 1) return;
        opacityScrub = (scrub.X, scrub.Value, true);
        ApplyOpacity(scrub.Value + dx, grouped: true);
    }

    private bool HandleOpacityFieldKey(VirtualKey key, VirtualKeyModifiers modifiers)
    {
        if (!CanEditLayerCommands || selectedLayerIds.Count != 1 || SelectedLayer is not { } layer) return false;
        if (key is VirtualKey.Up or VirtualKey.Down)
        {
            EndOpacityEdit();
            double step = modifiers.HasFlag(VirtualKeyModifiers.Shift) ? 10 : 1;
            ApplyOpacity(Math.Round((layer.Opacity ?? 1) * 100, MidpointRounding.AwayFromZero)
                + (key == VirtualKey.Up ? step : -step), grouped: false);
            return true;
        }
        if (key is not (VirtualKey.Enter or VirtualKey.Escape)) return false;
        FinishOpacityField(); CanvasStage.Focus(FocusState.Programmatic); return true;
    }

    private void FinishOpacityField()
    {
        EndOpacityEdit();
        if (double.TryParse(OpacityBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) && double.IsFinite(value))
            ApplyOpacity(value, grouped: false);
        else if (SelectedLayer is { } layer) ApplyOpacity((layer.Opacity ?? 1) * 100, grouped: false);
    }
}
