using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private GradientEdit? gradientEdit;
    private GradientEdit.PreviewBuffers? gradientBuffers;
    private PointD gradientStart, gradientEnd;
    private int gradientDragHandle = -1;
    private int gradientRevision;
    private GradientPreview? gradientRenderedPreview;
    private int gradientRenderedRevision = -1;
    private GradientRequest? gradientLastInput, gradientRenderedInput;
    private TaskCompletionSource<bool>? gradientFinish;
    private bool IsGradientFinishing => gradientFinish is not null;
    private Line? gradientLine;
    private Ellipse? gradientStartHandle, gradientEndHandle;
    private readonly record struct GradientRequest(GradientEdit Edit, GradientEdit.PreviewBuffers Buffers, PointD Start, PointD End, GradientSettings Settings, int Revision);

    private GradientRequest? CurrentGradientRequest => gradientEdit is { } edit
        ? new(edit, gradientBuffers ??= new(edit), gradientStart, gradientEnd, CurrentGradientSettings, gradientRevision) : null;

    private static bool SameGradientInput(GradientRequest? left, GradientRequest? right) => left is { } a && right is { } b
        && ReferenceEquals(a.Edit, b.Edit) && a.Start == b.Start && a.End == b.End && a.Settings == b.Settings;

    private GradientSettings CurrentGradientSettings => new(
        GradientShapeCombo.SelectedIndex == 1 ? global::Compositor.Core.GradientShape.Radial : global::Compositor.Core.GradientShape.Linear,
        GradientStyleCombo.SelectedIndex == 0 ? GradientStyle.ForegroundToTransparent : GradientStyle.ForegroundToBackground,
        new(foregroundColor.R, foregroundColor.G, foregroundColor.B), new(backgroundColor.R, backgroundColor.G, backgroundColor.B),
        GradientReverseCheck.IsChecked == true, GradientOpacitySlider.Value / 100);

    private void BeginGradient(PointD point)
    {
        if (IsGradientFinishing) return;
        double reach = 10 / viewport.PointsPerPixel;
        if (gradientEdit is not null && Distance(point, gradientEnd) <= reach) gradientDragHandle = 1;
        else if (gradientEdit is not null && Distance(point, gradientStart) <= reach) gradientDragHandle = 0;
        else
        {
            if (selectedLayerIds.Count > 1 || SelectedLayer is not { } layer)
            { SetStatus(Loc.T("S_GradientRefused")); return; }
            // A new line replaces this draft while retaining the original pixels.
            gradientEdit ??= document?.BeginGradient(layer.Id, paintOnMask);
            if (gradientEdit is null) { SetStatus(Loc.T("S_GradientRefused")); return; }
            gradientStart = gradientEnd = point;
            gradientDragHandle = 1;
        }
        RefreshGradientDraft();
    }

    private static double Distance(PointD a, PointD b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private void MoveGradient(PointD point, Windows.System.VirtualKeyModifiers modifiers = Windows.System.VirtualKeyModifiers.None)
    {
        if (IsGradientFinishing || gradientEdit is null || gradientDragHandle < 0) return;
        if (modifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift))
            point = SnapLineAngle(gradientDragHandle == 0 ? gradientEnd : gradientStart, point, MidpointRounding.AwayFromZero);
        if (gradientDragHandle == 0) gradientStart = point;
        else gradientEnd = point;
        RefreshGradientDraft();
    }

    private void EndGradientDrag()
    {
        // The Mac retains the last dragged point; mouse-up can arrive after Shift was released.
        gradientDragHandle = -1;
        if (Distance(gradientStart, gradientEnd) < .5) CancelGradientDraft();
    }

    private void RefreshGradientDraft()
    {
        if (IsGradientFinishing || CurrentGradientRequest is not { } input || SameGradientInput(input, gradientLastInput)) return;
        gradientLastInput = input with { Revision = ++gradientRevision };
        ShowGradientHandles();
        GradientApplyButton.IsEnabled = GradientCancelButton.IsEnabled = true;
        // The serial render worker computes the draft; pointer events only update endpoints.
        MarkInteractionDirty(input.Edit.DirtyBounds(), grow: 0);
        PumpRender();
    }

    private void ShowGradientHandles()
    {
        if (gradientLine is null)
        {
            var stroke = new SolidColorBrush(Microsoft.UI.Colors.DeepSkyBlue);
            gradientLine = new Line { Stroke = stroke };
            gradientStartHandle = new Ellipse { Stroke = stroke, Fill = new SolidColorBrush(Microsoft.UI.Colors.White) };
            gradientEndHandle = new Ellipse { Stroke = stroke, Fill = new SolidColorBrush(Microsoft.UI.Colors.Black) };
            ToolOverlay.Children.Add(gradientLine);
            ToolOverlay.Children.Add(gradientStartHandle);
            ToolOverlay.Children.Add(gradientEndHandle);
        }
        double size = 12 / viewport.PointsPerPixel;
        gradientLine.Visibility = Visibility.Visible;
        gradientLine.StrokeThickness = 1.5 / viewport.PointsPerPixel;
        gradientLine.X1 = gradientStart.X; gradientLine.Y1 = gradientStart.Y;
        gradientLine.X2 = gradientEnd.X; gradientLine.Y2 = gradientEnd.Y;
        void Place(Ellipse handle, PointD point)
        {
            handle.Visibility = Visibility.Visible;
            handle.Width = handle.Height = size;
            handle.StrokeThickness = 1 / viewport.PointsPerPixel;
            Canvas.SetLeft(handle, point.X - size / 2); Canvas.SetTop(handle, point.Y - size / 2);
        }
        Place(gradientStartHandle!, gradientStart); Place(gradientEndHandle!, gradientEnd);
    }

    private void CancelGradientDraft()
    {
        var dirty = gradientEdit?.DirtyBounds();
        ClearGradientDraft();
        if (dirty is { } band) { MarkInteractionDirty(band, grow: 0); PumpRender(); }
    }

    private void ClearGradientDraft()
    {
        if (gradientFinish is { } finishing)
        {
            ReleaseGradientFinish(finishing);
            finishing.TrySetResult(false);
        }
        frameRevision++;
        gradientEdit = null;
        gradientBuffers = null;
        gradientRenderedPreview = null;
        gradientLastInput = gradientRenderedInput = null;
        gradientDragHandle = -1;
        gradientRevision++;
        if (gradientLine is not null)
            gradientLine.Visibility = gradientStartHandle!.Visibility = gradientEndHandle!.Visibility = Visibility.Collapsed;
        if (GradientApplyButton is not null) GradientApplyButton.IsEnabled = GradientCancelButton.IsEnabled = false;
    }

    private Task<bool> ApplyGradientDraftAsync()
    {
        if (gradientFinish is { } pending) return pending.Task;
        if (CurrentGradientRequest is not { } input || document is not { } owner) return Task.FromResult(true);
        if (windowClosed || StatusProgress.Visibility == Visibility.Visible) return Task.FromResult(false);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        gradientFinish = completion;
        _ = FinishGradientAsync(owner, documentRetired.Task, input, completion);
        return completion.Task;
    }

    private async Task FinishGradientAsync(EditorDocument owner, Task retired, GradientRequest input, TaskCompletionSource<bool> completion)
    {
        bool succeeded = false;
        bool Current() => !windowClosed && !retired.IsCompleted && ReferenceEquals(document, owner)
            && ReferenceEquals(gradientEdit, input.Edit) && ReferenceEquals(gradientFinish, completion);
        try
        {
            SetBusy(true); RefreshGradientFinishAvailability();
            int failures = renderFailureEpoch;
            if (!SameGradientInput(input, gradientRenderedInput))
            {
                // The serial reader already owns the source. Wait for its latest publication
                // instead of generating another document-sized preview on the UI thread.
                if (!workerBusy && !HasPendingDirty) MarkInteractionDirty(input.Edit.DirtyBounds(), grow: 0);
                PumpRender();
                while (Current() && !SameGradientInput(input, gradientRenderedInput))
                {
                    if (failures != renderFailureEpoch) return;
                    await Task.Delay(8);
                }
            }
            if (!Current() || !SameGradientInput(input, CurrentGradientRequest)) return;
            var preview = gradientRenderedPreview;
            ReleaseGradientFinish(completion);
            ClearGradientDraft();
            bool applied = preview is not null && owner.CommitGradient(preview);
            compositor = null;
            RenderCanvas();
            SetStatus(Loc.T(applied ? "S_GradientApplied" : "S_GradientUnchanged"));
            succeeded = true;
        }
        catch (Exception ex)
        {
            if (!windowClosed && !retired.IsCompleted && ReferenceEquals(document, owner)) Report(Loc.T("S_GradientFailed"), ex);
        }
        finally
        {
            try { ReleaseGradientFinish(completion); }
            finally { completion.TrySetResult(succeeded); }
        }
    }

    private void ReleaseGradientFinish(TaskCompletionSource<bool> completion)
    {
        if (!ReferenceEquals(gradientFinish, completion)) return;
        gradientFinish = null;
        if (windowClosed) return;
        SetBusy(false); RefreshGradientFinishAvailability();
    }

    private void RefreshGradientFinishAvailability()
    {
        bool editable = !IsGradientFinishing;
        GradientShapeCombo.IsEnabled = GradientStyleCombo.IsEnabled = GradientReverseCheck.IsEnabled = editable;
        GradientOpacitySlider.IsEnabled = GradientMaskCheck.IsEnabled = BrushColorPicker.IsEnabled = editable;
        ForegroundColorChip.IsEnabled = BackgroundColorChip.IsEnabled = SwapColorsChip.IsEnabled = ResetColorsChip.IsEnabled = editable;
        GradientApplyButton.IsEnabled = editable && gradientEdit is not null;
        GradientCancelButton.IsEnabled = gradientEdit is not null;
        RefreshFileImportAvailability();
    }

    private async void OnGradientApply(object sender, RoutedEventArgs args) => _ = await ApplyGradientDraftAsync();
    private void OnGradientCancel(object sender, RoutedEventArgs args) => CancelGradientDraft();
    private void OnGradientOptionsChanged(object sender, RoutedEventArgs args) => RefreshGradientDraft();
    private async void OnGradientTargetChanged(object sender, RoutedEventArgs args)
    {
        var owner = document; var target = SelectedLayer?.Id;
        bool requested = GradientMaskCheck.IsChecked == true;
        if (!await ApplyGradientDraftAsync() || windowClosed || !ReferenceEquals(document, owner) || SelectedLayer?.Id != target)
        { GradientMaskCheck.IsChecked = paintOnMask; return; }
        paintOnMask = requested;
        PaintMaskCheck.IsChecked = paintOnMask;
    }

    private bool HandleGradientKey(KeyRoutedEventArgs args)
    {
        if (renamingLayer || textDialogOpen || args.OriginalSource is TextBox or RichEditBox) return false;
        if (args.KeyStatus.IsMenuKeyDown || Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return false;
        if (tool != CanvasTool.Gradient) return false;
        else if (IsApplyPress(args.Key)) _ = ApplyGradientDraftAsync();
        else if (IsCancelPress(args.Key)) CancelGradientDraft();
        else return false;
        args.Handled = true;
        return true;
    }
}
