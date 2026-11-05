using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private Guid? maskAloneLayerId;
    private Guid? CurrentMaskAloneId => paintOnMask && SelectedLayer?.Id == maskAloneLayerId
        && LayerDisplaySnapshot?.Masks.ContainsKey(maskAloneLayerId ?? Guid.Empty) == true ? maskAloneLayerId : null;
    private sealed record MaskCopyGesture(EditorDocument Owner, Guid Source, Point Start, bool Moved);
    private MaskCopyGesture? maskCopyGesture;
    private readonly Brush layerTargetAccent = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 75, 153, 230));
    private readonly Brush layerTargetEdge = new SolidColorBrush(Microsoft.UI.Colors.DimGray);
    private readonly Brush layerTargetSolo = new SolidColorBrush(Microsoft.UI.Colors.White);

    private Button BuildThumbnailTarget(LayerRow row, bool mask, Grid content)
    {
        content.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var button = new Button { Content = content, MinWidth = 0, MinHeight = 0, Padding = new(0), Width = mask ? 30 : 36,
            Height = 30, BorderThickness = new(1), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        string tooltip = Loc.T(mask ? "A_MaskThumbnail" : "A_PixelThumbnail");
        ToolTipService.SetToolTip(button, tooltip); AutomationProperties.SetName(button, tooltip);
        button.Click += (_, _) => HandleThumbnailClick(row.Layer.Id, mask, CurrentThumbnailModifiers());
        button.DoubleTapped += async (_, args) =>
        {
            args.Handled = true;
            await OpenThumbnailContentAsync(row.Layer.Id);
        };
        content.PointerPressed += (_, args) =>
        {
            if (!args.GetCurrentPoint(content).Properties.IsLeftButtonPressed) return;
            if (mask && args.KeyModifiers.HasFlag(VirtualKeyModifiers.Menu) && !args.KeyModifiers.HasFlag(VirtualKeyModifiers.Control)
                && CanSelectLayerTarget && document is { } owner)
            {
                BeginMaskCopyGesture(row.Layer.Id, args.GetCurrentPoint(RootGrid).Position);
                // The source row can be recycled when autoscroll carries it out of view.
                if (!LayerList.CapturePointer(args.Pointer)) CancelMaskCopyGesture();
            }
            else HandleThumbnailClick(row.Layer.Id, mask, args.KeyModifiers);
            layerSelectionPointerTarget = null; args.Handled = true;
        };
        return button;
    }

    private void OnLayerGesturePointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (visibilitySwipe is not null)
        {
            if (!args.GetCurrentPoint(LayerList).Properties.IsLeftButtonPressed) FinishVisibilitySwipe();
            else UpdateVisibilitySwipe(args.GetCurrentPoint(RootGrid).Position);
            args.Handled = true;
            return;
        }
        if (maskCopyGesture is null) return;
        if (!args.GetCurrentPoint(LayerList).Properties.IsLeftButtonPressed) { CancelMaskCopyGesture(); return; }
        UpdateMaskCopyGesture(args.GetCurrentPoint(RootGrid).Position);
        args.Handled = true;
    }

    private void OnLayerGesturePointerReleased(object sender, PointerRoutedEventArgs args)
    {
        layerSelectionPointerTarget = null;
        if (visibilitySwipe is not null) { FinishVisibilitySwipe(); args.Handled = true; return; }
        if (maskCopyGesture is null) return;
        FinishMaskCopyGesture(args.GetCurrentPoint(RootGrid).Position);
        args.Handled = true;
    }

    private void OnLayerGesturePointerCaptureLost(object sender, PointerRoutedEventArgs args)
    {
        layerSelectionPointerTarget = null;
        if (ReferenceEquals(args.OriginalSource, LayerList)) { FinishVisibilitySwipe(); CancelMaskCopyGesture(); }
    }

    private bool CanSelectLayerTarget => !IsFileImporting && distortCompletion is null && document is not null && pixelMove is null && pixelNudgePending == 0 && pointerStroke is null && healingStroke is null && !healingFinishing
        && StatusProgress.Visibility != Visibility.Visible && !renamingLayer;
    private bool CanChangePaintTarget => CanSelectLayerTarget && floatingTransform is null && floatingDuplicate is null
        && selectionMove is null && !textDialogOpen && !colorRangeSampling && !selectionAmountPending && selectedLayerIds.Count <= 1;

    private async void ChangePaintTarget(bool mask)
    {
        if (document is not { } owner || SelectedLayer is not { } layer || !CanChangePaintTarget
            || (mask && LayerDisplaySnapshot?.Masks.ContainsKey(layer.Id) != true))
        { SyncPaintTargetControls(); return; }
        var id = layer.Id;
        // Keep the displayed target until the serial render reader releases its source.
        SyncPaintTargetControls();
        if (!await ApplyGradientDraftAsync() || windowClosed || !ReferenceEquals(document, owner) || SelectedLayer?.Id != id)
        { SyncPaintTargetControls(); return; }
        QueueCanvasInput(owner, () =>
        {
            if (!CanChangePaintTarget || SelectedLayer?.Id != id || (mask && LayerDisplaySnapshot?.Masks.ContainsKey(id) != true))
            { SyncPaintTargetControls(); return; }
            ClearLayerEffectSelection();
            paintOnMask = mask; SyncPaintTargetControls(); SyncMaskAloneTarget();
            RefreshTransformInspector(true); UpdateTransformHandles();
            foreach (var grid in layerRows.Values) RefreshLayerThumbnail((LayerRow)grid.Tag, id);
        });
    }

    private void SyncPaintTargetControls()
    {
        bool previous = syncingTransform; syncingTransform = true;
        try
        {
            PaintMaskCheck.IsChecked = paintOnMask;
            if (transformTargetCombo is not null) transformTargetCombo.SelectedIndex = paintOnMask ? 1 : 0;
        }
        finally { syncingTransform = previous; }
    }
    private static VirtualKeyModifiers CurrentThumbnailModifiers() => (KeyHeld(VirtualKey.Control) ? VirtualKeyModifiers.Control : 0)
        | (KeyHeld(VirtualKey.Shift) ? VirtualKeyModifiers.Shift : 0) | (KeyHeld(VirtualKey.Menu) ? VirtualKeyModifiers.Menu : 0);

    private void BeginMaskCopyGesture(Guid source, Point point)
    {
        FinishVisibilitySwipe();
        CancelMaskCopyGesture();
        if (CanSelectLayerTarget && document is { } owner) maskCopyGesture = new(owner, source, point, false);
    }
    private void UpdateMaskCopyGesture(Point point)
    {
        if (maskCopyGesture is not { } gesture) return;
        double dx = point.X - gesture.Start.X, dy = point.Y - gesture.Start.Y;
        if (dx * dx + dy * dy >= 9) maskCopyGesture = gesture with { Moved = true };
        TrackLayerAutoscroll(point);
    }
    private void FinishMaskCopyGesture(Point point)
    {
        if (maskCopyGesture is not { } gesture) return;
        CancelMaskCopyGesture();
        if (!ReferenceEquals(document, gesture.Owner)) return;
        if (gesture.Moved) CopyMaskToRow(gesture.Source, ThumbnailDropTarget(point));
        else HandleThumbnailClick(gesture.Source, true, VirtualKeyModifiers.Menu);
    }

    private async void HandleThumbnailClick(Guid id, bool mask, VirtualKeyModifiers modifiers)
    {
        if (!CanSelectLayerTarget || document is not { } owner) return;
        if (modifiers.HasFlag(VirtualKeyModifiers.Control))
        {
            if (!CanEditSelection) return;
            ClearLayerEffectSelection();
            LoadThumbnailSelection(id, mask, modifiers); return;
        }
        if (!await ApplyGradientDraftAsync() || windowClosed || !ReferenceEquals(document, owner)) return;
        if (floatingTransform is not null || floatingDuplicate is not null)
        {
            if (id == floatingTransform?.Edit.FloatingLayerId) return;
            QueueAfterTransform(owner, SelectTarget);
        }
        else QueueCanvasInput(owner, SelectTarget);
        void SelectTarget()
        {
            effectSelection = null;
            LayerList.Focus(FocusState.Programmatic);
            var previousPrimary = SelectedLayer?.Id; var previousSolo = CurrentMaskAloneId;
            bool toggleSolo = mask && modifiers.HasFlag(VirtualKeyModifiers.Menu);
            if (previousPrimary != id) CommitTransformNow();
            if (!owner.Snapshot.Manifest.Layers.Any(l => l.Id == id)) return;
            selectedLayerId = id; selectedLayerIds.Clear(); selectedLayerIds.Add(id);
            selectingLayer = true;
            try { owner.SetActiveLayer(id); } finally { selectingLayer = false; }
            paintOnMask = mask && owner.Snapshot.Masks.ContainsKey(id);
            if (previousPrimary != id || !paintOnMask) maskAloneLayerId = null;
            if (toggleSolo && paintOnMask) maskAloneLayerId = previousSolo == id ? null : id;
            PaintMaskCheck.IsChecked = paintOnMask;
            if (mask && modifiers.HasFlag(VirtualKeyModifiers.Shift) && !toggleSolo && paintOnMask && transformInspectorEdit is null
                && !colorRangeSampling && !selectionAmountPending)
                owner.SetMaskEnabled(id, owner.Snapshot.Manifest.Layers.First(l => l.Id == id).MaskEnabled == false);
            RefreshLayerPanel();
            if (previousSolo != CurrentMaskAloneId) RenderCanvas();
        }
    }

    private void SyncMaskAloneTarget()
    {
        if (maskAloneLayerId is not null && CurrentMaskAloneId is null)
        { maskAloneLayerId = null; RenderCanvas(); }
        RefreshMaskAloneBadge();
    }

    private Guid? ThumbnailDropTarget(Point point)
    {
        var listPoint = LayerList.TransformToVisual(RootGrid).Inverse.TransformPoint(point);
        if (listPoint.X < 0 || listPoint.X >= LayerList.ActualWidth || listPoint.Y < 0 || listPoint.Y >= LayerList.ActualHeight) return null;
        foreach (var grid in layerItems)
        {
            var local = grid.TransformToVisual(RootGrid).Inverse.TransformPoint(point);
            if (local.X >= 0 && local.X < grid.ActualWidth && local.Y >= 0 && local.Y < grid.ActualHeight) return ((LayerRow)grid.Tag).Layer.Id;
        }
        return null;
    }

    private void CopyMaskToRow(Guid source, Guid? target)
    {
        if (!CanEditSelection || target is not { } id || document is not { } owner) return;
        QueueCanvasInput(owner, () =>
        {
            if (floatingTransform is not null || floatingDuplicate is not null || transformInspectorEdit is not null || gradientEdit is not null) return;
            if (!owner.CopyMask(source, id)) return;
            effectSelection = null;
            selectedLayerId = id; selectedLayerIds.Clear(); selectedLayerIds.Add(id);
            paintOnMask = true; PaintMaskCheck.IsChecked = true; maskAloneLayerId = null; RefreshLayerPanel(); RenderCanvas();
        });
    }

    /// <summary>Layer > "Edit Adjustment…" (CompositorApp.swift:295-297): re-opens the adjustment
    /// panel for the picked layer through the SAME door as the row's double-tap
    /// (OpenThumbnailContentAsync) — one editor path, so the panel cannot disagree with itself.
    /// The item is greyed unless the picked layer carries an adjustment, and the handler re-reads
    /// the model before acting: without one it refuses rather than falling through to the
    /// double-tap's rename answer.</summary>
    private async void OnEditAdjustment(object sender, RoutedEventArgs e)
    {
        if (!CanSelectLayerTarget || selectedLayerId is not { } id) return;
        if (document?.Snapshot.Manifest.Layers.FirstOrDefault(layer => layer.Id == id)?.Adjustment is null) return;
        await OpenThumbnailContentAsync(id);
    }

    /// <summary>Select > "Layer's Pixels" (CompositorApp.swift:231-234): the menu runs the active
    /// layer's load, which is exactly what a Ctrl+click on that row's thumbnail does — one engine,
    /// one set of rules (replace mode, groups and pixel-less layers answer with nothing, as the
    /// Mac's disabled state and beeping guard do).</summary>
    private void OnLoadLayerPixels(object sender, RoutedEventArgs e)
    {
        if (!CanSelectLayerTarget || !CanEditSelection || selectedLayerId is not { } id) return;
        LoadThumbnailSelection(id, mask: false, VirtualKeyModifiers.None);
    }

    /// <summary>Select > "Mask's Black Areas" (CompositorApp.swift:239-243), the same door on the
    /// mask side; with no mask on the active layer the Mac disables the item and this window has
    /// nothing to trace, so nothing happens.</summary>
    private void OnLoadMaskBlack(object sender, RoutedEventArgs e)
    {
        if (!CanSelectLayerTarget || !CanEditSelection || selectedLayerId is not { } id) return;
        LoadThumbnailSelection(id, mask: true, VirtualKeyModifiers.None);
    }

    private void LoadThumbnailSelection(Guid id, bool mask, VirtualKeyModifiers modifiers)
    {
        if (document is not { } owner) return;
        var layer = owner.Snapshot.Manifest.Layers.FirstOrDefault(l => l.Id == id); if (layer is null) return;
        var image = owner.Snapshot.Images.GetValueOrDefault(id); var sourceMask = owner.Snapshot.Masks.GetValueOrDefault(id);
        if (mask ? sourceMask is null : layer.IsGroupOrFalse || image is null) return;
        var transform = CopyTransform(mask ? layer.MaskPlacement ?? layer.Transform : layer.Transform);
        int width = owner.Snapshot.Manifest.Width, height = owner.Snapshot.Manifest.Height;
        var mode = modifiers.HasFlag(VirtualKeyModifiers.Menu) ? LassoSelectionMode.Subtract
            : modifiers.HasFlag(VirtualKeyModifiers.Shift) ? LassoSelectionMode.Add : LassoSelectionMode.Replace;
        QueueSelectionWork((previous, _) =>
        {
            var incoming = mask ? RasterSelection.FromMask(width, height, sourceMask!, transform) : RasterSelection.FromImage(width, height, image!, transform);
            return incoming is null ? previous : LassoSelection.Combine(previous, incoming, mode);
        }, mode != LassoSelectionMode.Replace, Loc.T(mask ? "S_LoadMaskSelection" : "S_LoadLayerSelection"), true);
    }
}
