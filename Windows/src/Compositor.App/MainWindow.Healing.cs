using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private HealingStroke? healingStroke;
    private CancellationTokenSource? healingCancellation;
    private readonly List<(PointD Point, bool Shift)> pendingHealingPoints = new();
    private BrushPointerPath? healingPath;
    private BrushEndpoint? healingEndpoint;
    private bool healingLastShift;
    private bool healingFinishing, healingPointerDown;

    private void BeginHealing(PointD point, bool shift = false)
    {
        if (IsFileImporting) return;
        if (healingFinishing) { SetStatus(Loc.T("S_HealingBusy")); return; }
        CancelHealingStroke();
        if (document is null || SelectedLayer is not { } layer || selectedLayerIds.Count > 1)
        { SetStatus(Loc.T("S_HealingRefused")); return; }
        try
        {
            healingStroke = document.BeginHealingStroke(layer.Id, CurrentBrush, (SpotHealingMode)HealingModeCombo.SelectedIndex, paintOnMask);
            if (healingStroke is null) { SetStatus(Loc.T("S_HealingRefused")); return; }
            healingCancellation = new CancellationTokenSource();
            healingPointerDown = true;
            healingEndpoint = new(document, layer.Id, false, point, lastBrushEndpoint);
            lastBrushEndpoint = healingEndpoint; healingLastShift = shift;
            HealingModeCombo.IsEnabled = false;
            pendingHealingPoints.Add((point, shift));
            compositor = null;
            RenderCanvas();
        }
        catch (Exception ex) { CancelHealingStroke(); Report(Loc.T("S_HealingFailed"), ex); }
    }

    private void MoveHealing(PointD point, bool shift = false)
    {
        if (healingStroke is null || !healingPointerDown || healingFinishing) return;
        healingLastShift = shift; pendingHealingPoints.Add((point, shift));
        PumpRender();
    }

    private void ApplyPendingHealing()
    {
        if (healingStroke is not { } stroke) { pendingHealingPoints.Clear(); return; }
        try
        {
            var snapshot = stroke.PreviewSnapshot;
            var layer = snapshot.Manifest.Layers.First(layer => layer.Id == stroke.LayerId);
            var image = snapshot.Images[layer.Id];
            void Paint(PointD point)
            {
                var dirty = stroke.Append(point);
                if (healingEndpoint is { } endpoint) { endpoint.Point = point; endpoint.Valid = true; endpoint.Previous = null; }
                if (dirty.Width == 0 || dirty.Height == 0) return;
                var corners = new[] { new PointD(dirty.X, dirty.Y), new PointD(dirty.X + dirty.Width, dirty.Y),
                    new PointD(dirty.X, dirty.Y + dirty.Height), new PointD(dirty.X + dirty.Width, dirty.Y + dirty.Height) }
                    .Select(p => BrushEngine.PixelToDocument(layer.Transform, image.Width, image.Height, p)).ToArray();
                double x0 = corners.Min(p => p.X), y0 = corners.Min(p => p.Y);
                int left = (int)Math.Floor(x0), top = (int)Math.Floor(y0);
                MarkInteractionDirty(new RectI(left, top, (int)Math.Ceiling(corners.Max(p => p.X)) - left,
                    (int)Math.Ceiling(corners.Max(p => p.Y)) - top), grow: 2);
            }
            foreach (var input in pendingHealingPoints)
            {
                if (healingPath is null)
                {
                    var previous = LastValidBrushEndpoint(healingEndpoint?.Previous);
                    var start = input.Shift && previous is not null && ReferenceEquals(previous.Owner, document)
                        && previous.LayerId == stroke.LayerId && !previous.OnMask ? previous.Point : input.Point;
                    healingPath = new(start); Paint(start);
                    if (start != input.Point && healingPath.Move(input.Point, viewport.Zoom) is { } connected) Paint(connected);
                    healingPath.ResetAxis(input.Point, input.Shift);
                }
                else if (healingPath.Move(input.Point, viewport.Zoom, input.Shift) is { } painted) Paint(painted);
            }
            pendingHealingPoints.Clear();
        }
        catch (Exception ex) { CancelHealingStroke(); Report(Loc.T("S_HealingFailed"), ex); }
    }

    private async Task FinishHealingAsync(PointD? point, bool? shift = null)
    {
        if (healingStroke is not { } stroke || healingCancellation is not { } cancellation || healingFinishing) return;
        var owner = document;
        var token = cancellation.Token;
        healingPointerDown = false;
        healingFinishing = true;
        if (point is { } endpoint) pendingHealingPoints.Add((endpoint, shift ?? healingLastShift));
        PumpRender();
        try
        {
            // Preview pixels change only between serial frames; the repair reads their
            // separate original pixels after the final queued segment has been flushed.
            while (workerBusy && ReferenceEquals(stroke, healingStroke)) await Task.Delay(10, token);
            if (!ReferenceEquals(stroke, healingStroke)) return;
            ApplyPendingHealing();
            token.ThrowIfCancellationRequested();
            SetStatus(Loc.T("S_HealingWorking"));
            uint seed = (uint)Random.Shared.NextInt64(0, 1L << 32);
            var result = await Task.Run(() => stroke.Render(seed, token), token);
            if (!ReferenceEquals(stroke, healingStroke) || !ReferenceEquals(document, owner) || token.IsCancellationRequested) return;
            ClearHealingStroke(cancel: false);
            compositor = null;
            bool applied = result is not null && owner!.CommitHealingStroke(result);
            RenderCanvas();
            SetStatus(Loc.T(applied ? "S_HealingApplied" : "S_HealingUnchanged"));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (ReferenceEquals(stroke, healingStroke)) { CancelHealingStroke(); Report(Loc.T("S_HealingFailed"), ex); }
            else App.Log($"Discarded healing job failed: {ex}");
        }
        finally
        {
            if (ReferenceEquals(stroke, healingStroke)) CancelHealingStroke();
            cancellation.Dispose();
        }
    }

    private void ClearHealingStroke(bool cancel)
    {
        var cancellation = healingCancellation;
        bool wasFinishing = healingFinishing;
        healingStroke = null; healingCancellation = null;
        healingEndpoint = null; healingPath = null; healingLastShift = false;
        healingPointerDown = healingFinishing = false;
        pendingHealingPoints.Clear();
        if (HealingModeCombo is not null) HealingModeCombo.IsEnabled = true;
        if (cancel)
        {
            cancellation?.Cancel();
            if (!wasFinishing) cancellation?.Dispose();
        }
    }

    private void CancelHealingStroke()
    {
        bool hadStroke = healingStroke is not null;
        ClearHealingStroke(cancel: true);
        if (hadStroke) { compositor = null; RenderCanvas(); }
    }

    private void OnHealingPointerCanceled(object sender, PointerRoutedEventArgs args)
    {
        if (objectSelectionPress is not null) { objectSelectionPress = null; RefreshObjectSelectionMarkers(); RefreshObjectSelectionControls(); }
        CancelFloatingPointerForFocus();
        CancelPixelMove(focusLoss: true);
        EndSelectionMove(); StopSelectionAutoscroll();
        FinishPointerStroke();
        if (placementDragActive) FinishPlacementDrag();
        if (healingPointerDown) CancelHealingStroke();
    }

    private bool HealingAllowsOutput()
    {
        if (healingStroke is null) return true;
        SetStatus(Loc.T("S_HealingBusy"));
        return false;
    }

    private bool HandleHealingKey(KeyRoutedEventArgs args)
    {
        if (renamingLayer || textDialogOpen || args.OriginalSource is TextBox or RichEditBox || args.KeyStatus.IsMenuKeyDown
            || Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return false;
        if (tool != CanvasTool.Healing) return false;
        else if (IsCancelPress(args.Key)) { CancelHealingStroke(); SetStatus(Loc.T("S_HealingCanceled")); }
        else if (args.Key == VirtualKey.Tab && healingStroke is null) HealingModeCombo.SelectedIndex = (HealingModeCombo.SelectedIndex + 1) % 3;
        else return false;
        args.Handled = true;
        return true;
    }
}
