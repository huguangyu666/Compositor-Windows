using System.Globalization;
using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private TransformEdit? transformInspectorEdit, transformInspectorBasis;
    private LayerTransform? transformInspectorValue;
    private TransformPreview? transformRenderedPreview;
    private PointD[]? transformCorners;
    private TaskCompletionSource<bool>? distortCompletion;
    private CancellationTokenSource? distortCancellation;
    private int transformDraftRevision, transformRenderedRevision = -1;
    private bool transformPersistent, committingTransform, syncingTransform, transformPointerDraftActive;
    private bool transformDuplicateArmed, transformPointerMoved, restoringHistory;
    private sealed record DuplicateSelectionState(Guid? Primary, HashSet<Guid> Selected, HashSet<Guid> Collapsed);
    private DuplicateSelectionState? transformDuplicateSelection;
    private ProjectSnapshot? LayerDisplaySnapshot => floatingTransform?.Display ?? transformInspectorEdit?.DuplicateSnapshot ?? document?.Snapshot;
    private bool transformRatioLock = true, transformAutoSelect = true, showTransformControls = true;
    private readonly Dictionary<TransformField, TextBox> transformFields = new();
    private CheckBox transformAutoCheck = null!, transformShowCheck = null!;
    private ToggleButton transformRatioCheck = null!;
    private ComboBox transformSamplingCombo = null!, transformTargetCombo = null!;
    private Button transformApplyButton = null!, transformCancelButton = null!;
    private (TransformField Field, double StartX, double Value, bool Moved)? transformScrub;
    private (bool Editing, Guid? Layer, int Count, bool HasMask)? transformAvailability;
    private sealed record TransformRequest(TransformEdit Edit, LayerTransform Value, PointD[]? Corners, int Revision, bool Finishing, CancellationToken Token);
    private TransformRequest? CurrentTransformRequest => floatingTransform is null && transformInspectorEdit is { } edit && transformInspectorValue is { } value
        ? new(edit, CopyTransform(value), transformCorners?.ToArray(), transformDraftRevision, distortCompletion is not null,
            distortCancellation?.Token ?? default) : null;
    private LayerTransform? CurrentTransformValue => transformInspectorValue ?? transformInspectorBasis?.Value;
    private static bool KeyHeld(VirtualKey key) => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
        .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
    private static LayerTransform CopyTransform(LayerTransform value) => new()
    { Origin = value.Origin, Size = value.Size, Rotation = value.Rotation, FlipX = value.FlipX, FlipY = value.FlipY, Sampling = value.Sampling };

    /// <summary>Names of the two persisted inspector switches, as the macOS ToolDefaults keeps them.</summary>
    private const string AutoSelectPreference = "transform-auto-select";

    private const string ShowControlsPreference = "transform-show-controls";

    /// <summary>
    /// Reads the two inspector switches from the preferences folder and applies them to the fields and
    /// the checkboxes. Split out so a probe can point the folder at a scratch directory, change the
    /// in-memory state, and confirm a "next launch" really comes from disk.
    /// </summary>
    private void ApplyPersistedTransformOptions()
    {
        transformAutoSelect = ReadFlag(AutoSelectPreference, transformAutoSelect);
        showTransformControls = ReadFlag(ShowControlsPreference, showTransformControls);
        transformAutoCheck.IsChecked = transformAutoSelect;
        transformShowCheck.IsChecked = showTransformControls;
    }

    private void OnTransformAutoSelectClick(object sender, RoutedEventArgs e)
    {
        transformAutoSelect = transformAutoCheck.IsChecked == true != KeyHeld(VirtualKey.Control);
        WriteFlag(AutoSelectPreference, transformAutoSelect);
    }

    private void OnTransformShowControlsClick(object sender, RoutedEventArgs e) =>
        ApplyShowTransformControls(transformShowCheck.IsChecked == true);

    /// <summary>
    /// The one place Show Controls changes: the checkbox and Ctrl+H both come through here, so neither
    /// path can end up with the panel showing one state and the editor running another.
    /// </summary>
    private void ApplyShowTransformControls(bool value)
    {
        showTransformControls = value;
        transformShowCheck.IsChecked = value;
        RefreshTransformModifiers();
        UpdateTransformHandles();
        WriteFlag(ShowControlsPreference, value);
    }

    private void InitializeTransformInspector()
    {
        transformAutoCheck = AddCheck("Tr_AutoSelect", true);
        ToolTipService.SetToolTip(transformAutoCheck, Loc.T("Tr_AutoSelectTip"));
        transformShowCheck = AddCheck("Tr_ShowControls", true);
        ApplyPersistedTransformOptions();
        transformTargetCombo = new ComboBox { Width = 96 };
        transformTargetCombo.Items.Add(Loc.T("Tr_Pixels")); transformTargetCombo.Items.Add(Loc.T("W_MaskLabel"));
        AutomationProperties.SetName(transformTargetCombo, Loc.T("Tr_Target"));
        TransformControls.Children.Add(transformTargetCombo);
        foreach (var field in Enum.GetValues<TransformField>())
        {
            string caption = field switch { TransformField.Width => "W", TransformField.Height => "H", TransformField.Scale => Loc.T("Tr_Scale"), TransformField.Rotation => "°", _ => field.ToString() };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            var label = new ViewportSurface { Content = new TextBlock { Text = caption, FontSize = 12 }, VerticalAlignment = VerticalAlignment.Center };
            label.SetCursor(Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast);
            var box = new TextBox { Width = field == TransformField.Scale ? 64 : 58, Padding = new Thickness(5, 4, 5, 4), Tag = field };
            AutomationProperties.SetName(box, Loc.T("Tr_" + field)); AutomationProperties.SetAutomationId(box, "transform" + field);
            box.TextChanged += (_, _) =>
            {
                if (!syncingTransform && box.FocusState != FocusState.Unfocused && TryTransformNumber(box.Text, out var value))
                    ChangeTransformNumber(field, value);
            };
            box.LostFocus += (_, _) => FinishTransformField();
            box.KeyDown += OnTransformFieldKey;
            label.PointerPressed += (_, args) =>
            {
                if (!args.GetCurrentPoint(label).Properties.IsLeftButtonPressed || CurrentTransformValue is not { } value) return;
                transformScrub = (field, args.GetCurrentPoint(RootGrid).Position.X, TransformNumber(value, field), false);
                label.CapturePointer(args.Pointer); args.Handled = true;
            };
            label.PointerMoved += (_, args) =>
            {
                if (transformScrub is not { } scrub || scrub.Field != field) return;
                double dx = args.GetCurrentPoint(RootGrid).Position.X - scrub.StartX;
                if (!scrub.Moved && Math.Abs(dx) < 1) return;
                transformScrub = (scrub.Field, scrub.StartX, scrub.Value, true);
                var range = field switch { TransformField.Width or TransformField.Height => (1d, 30000d), TransformField.Scale => (.1, 30000d), TransformField.Rotation => (-360d, 360d), _ => (-30000d, 30000d) };
                ChangeTransformNumber(field, Math.Clamp(Math.Round(scrub.Value + dx, MidpointRounding.AwayFromZero), range.Item1, range.Item2));
                args.Handled = true;
            };
            label.PointerReleased += (_, args) => { EndTransformScrub(); label.ReleasePointerCapture(args.Pointer); args.Handled = true; };
            label.PointerCaptureLost += (_, _) => EndTransformScrub();
            row.Children.Add(label); row.Children.Add(box);
            if (field == TransformField.Scale) row.Children.Add(new TextBlock { Text = "%", VerticalAlignment = VerticalAlignment.Center });
            transformFields.Add(field, box); TransformControls.Children.Add(row);
            if (field == TransformField.Height)
            {
                transformRatioCheck = new ToggleButton { Content = new FontIcon { Glyph = "\uE71B", FontSize = 15 }, Width = 32, Padding = new Thickness(0), IsChecked = true };
                ToolTipService.SetToolTip(transformRatioCheck, Loc.T("Tr_LockRatio")); AutomationProperties.SetName(transformRatioCheck, Loc.T("Tr_LockRatio"));
                TransformControls.Children.Add(transformRatioCheck);
            }
        }
        transformSamplingCombo = new ComboBox { Width = 124 };
        foreach (var sampling in Enum.GetValues<LayerSampling>()) transformSamplingCombo.Items.Add(Loc.T("Tr_Sampling" + sampling));
        AutomationProperties.SetName(transformSamplingCombo, Loc.T("Tr_Sampling")); TransformControls.Children.Add(transformSamplingCombo);
        CompactFlipButton(AddButton("Tr_FlipH", () => ChangeTransformOption(value => value.FlipX = !value.FlipX)), "Tr_FlipH", false);
        CompactFlipButton(AddButton("Tr_FlipV", () => ChangeTransformOption(value => value.FlipY = !value.FlipY)), "Tr_FlipV", true);
        transformCancelButton = AddButton("T_Dlg_Cancel", () => QueueTransformCancel());
        transformApplyButton = AddButton("Tr_Apply", () => FinishTransformDraft());
        TransformControls.Children.Remove(transformCancelButton); TransformControls.Children.Remove(transformApplyButton);
        TransformActions.Children.Add(transformCancelButton); TransformActions.Children.Add(transformApplyButton);
        transformAutoCheck.Click += OnTransformAutoSelectClick;
        transformShowCheck.Click += OnTransformShowControlsClick;
        transformRatioCheck.Click += (_, _) => transformRatioLock = transformRatioCheck.IsChecked == true != KeyHeld(VirtualKey.Shift);
        transformSamplingCombo.SelectionChanged += (_, _) =>
        {
            int index = transformSamplingCombo.SelectedIndex;
            if (!syncingTransform && index >= 0) ChangeTransformOption(v => v.Sampling = (LayerSampling)index);
        };
        transformTargetCombo.SelectionChanged += (_, _) =>
        {
            if (!syncingTransform) ChangePaintTarget(transformTargetCombo.SelectedIndex == 1);
        };
        Content.KeyUp += (_, _) => RefreshTransformModifiers();
        RefreshTransformInspector(true);

        CheckBox AddCheck(string key, bool value)
        { var box = new CheckBox { Content = Loc.T(key), IsChecked = value }; TransformControls.Children.Add(box); return box; }
        Button AddButton(string key, Action click)
        { var button = new Button { Content = Loc.T(key), Padding = new Thickness(10, 5, 10, 5) }; button.Click += (_, _) => click(); TransformControls.Children.Add(button); return button; }
        static void CompactFlipButton(Button button, string key, bool vertical)
        {
            string path = vertical ? "M3,12 L21,12 M6,3 L18,3 L12,9 Z M6,21 L18,21 L12,15 Z"
                : "M12,3 L12,21 M3,6 L3,18 L9,12 Z M21,6 L21,18 L15,12 Z";
            button.Content = Microsoft.UI.Xaml.Markup.XamlReader.Load($"<Viewbox xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Width='18' Height='18'><Canvas Width='24' Height='24'><Path Data='{path}' Stroke='#FFE5E5E5' StrokeThickness='1.5' StrokeLineJoin='Round' /></Canvas></Viewbox>");
            button.Width = button.Height = 32; button.MinWidth = 0; button.Padding = new(0);
            AutomationProperties.SetName(button, Loc.T(key)); ToolTipService.SetToolTip(button, Loc.T(key));
        }
    }

    private TransformEdit? TransformBasis() => document?.BeginTransformDraft(
        selectedLayerIds.Count > 1 ? selectedLayerIds : SelectedLayer is { } layer ? new[] { layer.Id } : Array.Empty<Guid>(), paintOnMask);
    private void RefreshTransformInspector(bool rebuildBasis = false)
    {
        if (transformFields.Count == 0) return;
        if (rebuildBasis && transformInspectorEdit is null && floatingTransform is null) transformInspectorBasis = TransformBasis();
        var value = CurrentTransformValue; var edit = transformInspectorEdit ?? transformInspectorBasis;
        syncingTransform = true;
        try
        {
            foreach (var pair in transformFields)
            {
                pair.Value.IsEnabled = !IsFileImporting && distortCompletion is null && value is not null && floatingTransform is not { Display: null } and not { Finishing: true };
                if (pair.Value.FocusState == FocusState.Unfocused) pair.Value.Text = value is null ? "" : FormatTransform(TransformNumber(value, pair.Key));
            }
            transformSamplingCombo.IsEnabled = !IsFileImporting && distortCompletion is null && value is not null && floatingTransform is not { Display: null } and not { Finishing: true };
            transformSamplingCombo.SelectedIndex = value is null ? -1 : (int)value.Sampling;
            PaintMaskCheck.IsEnabled = transformTargetCombo.IsEnabled = CanChangePaintTarget && SelectedLayer is { MaskFile: not null };
            transformTargetCombo.SelectedIndex = paintOnMask ? 1 : 0;
            bool persistent = transformPersistent && (transformInspectorEdit is not null || floatingTransform is not null);
            transformApplyButton.Visibility = transformCancelButton.Visibility = persistent ? Visibility.Visible : Visibility.Collapsed;
            TransformActions.Visibility = tool == CanvasTool.Move && persistent ? Visibility.Visible : Visibility.Collapsed;
            if (tool == CanvasTool.Move) ActiveToolLabel.Text = Loc.T(edit?.TargetsMask == true ? "Tr_TransformMask" : "Tr_Transform");
        }
        finally { syncingTransform = false; }
        RefreshTransformModifiers();
        RefreshTransformAvailability(rebuildBasis);
    }
    private void RefreshTransformAvailability(bool force)
    {
        var state = (Editing: IsFileImporting || IsGradientFinishing || floatingDuplicate is not null || floatingTransform is not null || transformInspectorEdit is not null || colorRangeSampling || selectionAmountPending || pixelMove is not null || pixelNudgePending > 0,
            Layer: SelectedLayer?.Id, Count: selectedLayerIds.Count,
            HasMask: SelectedLayer?.MaskFile is not null);
        if (!force && transformAvailability == state) return;
        transformAvailability = state;
        bool editable = !state.Editing, single = editable && state.Layer is not null && state.Count <= 1;
        LayerMenu.IsEnabled = LayerPropertiesScroll.IsEnabled = editable;
        EditMenu.IsEnabled = !IsFileImporting && !colorRangeSampling && !selectionAmountPending;
        foreach (var command in LayerCommands.Children.OfType<Control>()) command.IsEnabled = editable;
        NewBlankLayerMenuItem.IsEnabled = BlankWorkspaceButton.IsEnabled = editable && !IsDeletePending
            && document is { ActiveTextDraft: null, LayerCount: < Limits.MaxLayers };
        // Adding a mask is not offered twice: the Mac disables the affordance once the active layer has one
        // (LayerMaskMenu.swift:16), so the command never reaches the model that refuses to replace it.
        bool canAddMask = single && state.Count == 1 && !state.HasMask && document?.ActiveTextDraft is null && !IsDeletePending;
        AddMaskMenuItem.IsEnabled = AddHideAllMaskMenuItem.IsEnabled = canAddMask;
        RevealSelectionMaskMenuItem.IsEnabled = HideSelectionMaskMenuItem.IsEnabled = canAddMask;
        // The Mac's two Select-menu loads are offered only over a layer that carries what they trace
        // (CompositorApp.swift:231-242: activeLayer.asset / activeLayer.mask plus canEditSelection).
        LoadLayerPixelsMenuItem.IsEnabled = single && SelectedLayer is { } pixelTarget
            && !pixelTarget.IsGroupOrFalse && document?.Snapshot.Images.ContainsKey(pixelTarget.Id) == true;
        LoadMaskBlackMenuItem.IsEnabled = single && state.HasMask;
        // Layer > "Edit Adjustment…" (CompositorApp.swift:297: .disabled(!canEditLayers ||
        // activeLayer?.adjustment == nil)) — the picked layer, not the pick count: the Mac keys the
        // same way on its active layer, and SelectedLayer is that window-side equivalent.
        EditAdjustmentMenuItem.IsEnabled = editable && SelectedLayer?.Adjustment is not null;
        MaskWorkspaceButton.IsEnabled = canAddMask;
        RefreshMaskButtonHint();
        // The Mac's whole View ▸ guide block is greyed without a document (CompositorApp.swift:129, :133,
        // :137, :140-147, :151 — each carries .disabled(session.document == nil)).
        bool hasDocument = document is not null;
        RulersToggle.IsEnabled = GuidesToggle.IsEnabled = SnapToggle.IsEnabled = LockGuidesToggle.IsEnabled = hasDocument;
        GridToggle.IsEnabled = SnapToGridToggle.IsEnabled = GridSettingsButton.IsEnabled = hasDocument;
        SnapToGuidesToggle.IsEnabled = SnapToLayersToggle.IsEnabled = SnapToBoundsToggle.IsEnabled = hasDocument;
        OpacitySlider.IsEnabled = OpacityBox.IsEnabled = single;
        BlendCombo.IsEnabled = single && SelectedLayer?.IsGroupOrFalse != true;
        VisibleCheck.IsEnabled = single;
        foreach (var grid in layerRows.Values)
        {
            var row = (LayerRow)grid.Tag; row.Eye.IsEnabled = editable; row.MaskLink.IsEnabled = editable;
        }
    }
    private void RefreshTransformModifiers()
    {
        if (transformAutoCheck is null) return;
        transformAutoCheck.IsChecked = transformAutoSelect != KeyHeld(VirtualKey.Control);
        transformRatioCheck.IsChecked = transformRatioLock != KeyHeld(VirtualKey.Shift);
        transformShowCheck.IsChecked = showTransformControls;
    }
    private double TransformNumber(LayerTransform value, TransformField field) => field switch
    {
        TransformField.X => value.Origin.X, TransformField.Y => value.Origin.Y, TransformField.Width => value.Size.Width,
        TransformField.Height => value.Size.Height, TransformField.Rotation => value.Rotation,
        _ => (transformInspectorEdit ?? transformInspectorBasis)?.ScalePercent(value) ?? 100,
    };
    private static string FormatTransform(double value) => Math.Abs(value - Math.Round(value, MidpointRounding.AwayFromZero)) < .005
        ? Math.Round(value, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.CurrentCulture) : value.ToString("0.00", CultureInfo.CurrentCulture);
    private static bool TryTransformNumber(string text, out double value) => double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) && double.IsFinite(value);
    private bool BeginTransformInspector(bool persistent = false)
    {
        if (pixelMove is not null || pixelNudgePending > 0) return false;
        if (floatingTransform is { } floating) return !floating.Finishing && transformInspectorEdit is not null;
        if (transformInspectorEdit is not null) { transformPersistent |= persistent; return true; }
        transformInspectorEdit = TransformBasis();
        transformInspectorValue = transformInspectorEdit?.Value; transformPersistent = persistent;
        if (transformInspectorEdit is null) { SetStatus(Loc.T("Tr_Unavailable")); return false; }
        RefreshTransformInspector(); return true;
    }
    private void ChangeTransformNumber(TransformField field, double number)
    {
        if (IsFileImporting || distortCompletion is not null) return;
        if (document is not { } owner) return;
        QueueCanvasInput(owner, () =>
        {
            if (distortCompletion is not null) return;
            if (!BeginTransformInspector() || transformInspectorValue is not { } value) return;
            if (transformInspectorEdit!.UpdateNumber(value, field, number, transformRatioLock) is not { } next) return;
            SetTransformDraft(next);
        });
    }
    private void ChangeTransformOption(Action<LayerTransform> change)
    {
        if (IsFileImporting || distortCompletion is not null) return;
        if (document is not { } owner) return;
        QueueCanvasInput(owner, () =>
        {
            if (distortCompletion is not null) return;
            if (!BeginTransformInspector() || transformInspectorValue is not { } value) return;
            var next = CopyTransform(value); change(next); SetTransformDraft(next);
            if (!transformPersistent) CommitTransformNow();
        });
    }
    private void SetTransformDraft(LayerTransform value)
    {
        transformInspectorValue = value; transformDraftRevision++; frameRevision++;
        // Wake the serial owner. It expands this tiny trigger to the previous/new draft footprints.
        if (floatingTransform is not null) MarkFloatingTransformDirty(value);
        else MarkInteractionDirty(new RectI(0, 0, 1, 1), grow: 0);
        UpdateFloatingSelectionOutline();
        RefreshTransformInspector(); UpdateTransformHandles();
    }
    private void SetTransformCorners(PointD[] corners)
    {
        if (!DistortWarp.IsUsable(corners) || distortCompletion is not null || floatingTransform?.Finishing == true) return;
        transformCorners = corners.ToArray(); transformPersistent = true;
        SetTransformDraft(transformInspectorValue!);
    }
    private void FinishTransformField()
    {
        if (syncingTransform || document is not { } owner) return;
        QueueCanvasInput(owner, () => { if (!transformPersistent && !transformPointerDraftActive) CommitTransformNow(); });
    }
    private void EndTransformScrub() { if (transformScrub is null) return; transformScrub = null; FinishTransformField(); }
    private void OnTransformFieldKey(object sender, KeyRoutedEventArgs args)
    {
        if (sender is not TextBox { Tag: TransformField field } box) return;
        if (args.Key is VirtualKey.Up or VirtualKey.Down && CurrentTransformValue is { } value)
        {
            double number = TransformNumber(value, field) + (args.Key == VirtualKey.Up ? 1 : -1) * (KeyHeld(VirtualKey.Shift) ? 10 : 1);
            ChangeTransformNumber(field, number); syncingTransform = true; try { box.Text = FormatTransform(number); } finally { syncingTransform = false; }
            args.Handled = true;
        }
        else if (args.Key is VirtualKey.Enter or VirtualKey.Escape)
        {
            if (args.Key == VirtualKey.Escape) QueueTransformCancel();
            else FinishTransformDraft();
            CanvasStage.Focus(FocusState.Programmatic); args.Handled = true;
        }
    }
    private void FinishTransformDraft()
    {
        if (IsFileImporting) return;
        if (floatingDuplicate is { } gesture) { gesture.Released = true; return; }
        if (document is { } owner) QueueCanvasInput(owner, CommitTransformNow);
    }
    private async Task FinishTransformBeforeOutputAsync(bool waitForDither = true)
    {
        var requestedOwner = document;
        var retired = documentRetired.Task;
        if (waitForDither && ditherOperation is { } dithering
            && (!await WaitForInteractionWorkAsync(dithering.Completion.Task, retired) || !ReferenceEquals(document, requestedOwner))) return;
        if (!await WaitForInteractionWorkAsync(pixelNudgeWork, retired) || !ReferenceEquals(document, requestedOwner)) return;
        if (!await WaitForInteractionWorkAsync(FinishPixelMoveAsync(), retired)) return;
        if (floatingDuplicate is { } gesture)
        { gesture.Released = true; if (!await WaitForInteractionWorkAsync(gesture.Completion.Task, retired)) return; }
        if (document is not { } owner || !ReferenceEquals(owner, requestedOwner) || retired.IsCompleted) return;
        var finished = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        QueueCanvasInput(owner, () =>
        {
            try
            {
                ApplyPendingInteraction();
                if (floatingDuplicate is { } copying) copying.Released = true;
                CommitTransformNow(); finished.SetResult(floatingDuplicate?.Completion.Task ?? floatingTransform?.Completion.Task ?? distortCompletion?.Task ?? Task.CompletedTask);
            }
            catch (Exception ex) { finished.SetException(ex); }
        });
        // Replacing/closing the document discards queued actions before they can signal.
        if (!await WaitForInteractionWorkAsync(finished.Task, retired)) return;
        await WaitForInteractionWorkAsync(await finished.Task, retired);
    }
    private static async Task<bool> WaitForInteractionWorkAsync(Task work, Task retired)
    {
        await Task.WhenAny(work, retired);
        if (retired.IsCompleted) return false;
        await work; return true;
    }
    private void CommitTransformNow()
    {
        if (floatingTransform is { } floating)
        {
            if (floating.Finishing) return;
            ApplyPendingInteraction(); floating.Finishing = true; transformDraftRevision++; frameRevision++;
            MarkFloatingTransformDirty(transformInspectorValue!); RefreshTransformInspector(); return;
        }
        if (transformInspectorEdit is not { } edit || transformInspectorValue is not { } value || document is null) return;
        if (transformCorners is not null)
        {
            if (distortCompletion is not null) return;
            distortCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously); distortCancellation = new();
            transformDraftRevision++; frameRevision++; MarkInteractionDirty(new(0, 0, 1, 1), grow: 0); RefreshTransformInspector(); return;
        }
        var preview = transformRenderedRevision == transformDraftRevision ? transformRenderedPreview : edit.Preview(value);
        var previous = compositor?.Snapshot;
        var duplicateSelection = transformDuplicateSelection;
        ClearTransformDraft(keepDuplicateSelection: true);
        committingTransform = true;
        bool committed = false;
        try { if (preview is not null && (committed = document.CommitTransform(preview))) SetStatus(Loc.T("S_TransformApplied")); }
        finally { committingTransform = false; }
        if (!committed && duplicateSelection is not null) { RestoreDuplicateSelection(duplicateSelection); RefreshLayerPanel(); }
        renderSnapshot = null; RefreshTransformInspector(true); UpdateTransformHandles();
        var dirty = preview?.DirtyBounds ?? new(0, 0, 1, 1);
        // Input can shrink a draft and commit it in the same drain, before that new size
        // was ever drawn. Clear the last compositor placement too, including its effects.
        if (previous is not null) dirty = RectI.Union(dirty, CanvasCompositor.ChangedLayersBounds(previous, document.Snapshot, edit.DirtyLayerIds));
        MarkInteractionDirty(dirty, grow: 0);
    }
    private void QueueTransformCancel()
    {
        if (CancelFloatingDuplicate()) return;
        if (document is not { } owner) return;
        QueueCanvasInput(owner, () =>
        {
            if (transformInspectorEdit is null && floatingTransform is null && !transformPointerDraftActive) return;
            bool floated = floatingTransform is not null;
            bool duplicate = transformDuplicateSelection is not null;
            var ids = floatingTransform?.Edit.DirtyLayerIds ?? transformInspectorEdit?.DirtyLayerIds;
            var dirty = ids is not null && compositor is { } active
                ? CanvasCompositor.ChangedLayersBounds(active.Snapshot, owner.Snapshot, ids) : new RectI(0, 0, 1, 1);
            ClearTransformDraft(); MarkInteractionDirty(dirty, grow: 0); RefreshTransformInspector(true); UpdateTransformHandles();
            if (ids is not null)
                foreach (var id in ids)
                    if (layerRows.TryGetValue(id, out var item)) RefreshLayerThumbnail((LayerRow)item.Tag);
            if (duplicate || floated) { RefreshLayerPanel(); RefreshSelectionOutline(); }
        });
    }
    private void ClearTransformDraft(bool keepDuplicateSelection = false, bool keepFloatingDuplicate = false)
    {
        distortCancellation?.Cancel(); distortCancellation?.Dispose(); distortCancellation = null;
        distortCompletion?.TrySetResult(false); distortCompletion = null; transformCorners = null;
        if (!keepFloatingDuplicate) CancelFloatingDuplicate();
        DiscardFloatingTransform();
        if (!keepDuplicateSelection && transformDuplicateSelection is { } selection) RestoreDuplicateSelection(selection);
        transformDuplicateSelection = null; transformDuplicateArmed = transformPointerMoved = false;
        transformInspectorEdit = null; transformInspectorValue = null; transformRenderedPreview = null;
        transformDraftRevision++; frameRevision++; transformPersistent = false; transformScrub = null;
        if (transformPointerDraftActive)
        {
            transformPointerDraftActive = placementDragActive = transformDragActive = false;
            transformDrag = null; pendingTransformPoint = null; placementFinishing = false;
        }
    }
    private void OnFreeTransform(object sender, RoutedEventArgs args)
    {
        if (!CanStartTransform) { SetStatus(Loc.T("Tr_Unavailable")); return; }
        ToolCombo.SelectedIndex = (int)CanvasTool.Move;
        if (document is { } owner) QueueCanvasInput(owner, () =>
        {
            if (!CanStartTransform) return;
            if (transformInspectorEdit is null && floatingTransform is null && BeginFloatingTransform()) return;
            BeginTransformInspector(true); RefreshTransformInspector(); UpdateTransformHandles();
        });
    }
    private bool HandleTransformKey(KeyRoutedEventArgs args)
    {
        if (IsFileImporting) return false;
        RefreshTransformModifiers();
        if (args.OriginalSource is TextBox or RichEditBox || renamingLayer || textDialogOpen) return false;
        if (tool == CanvasTool.Move && args.Key == VirtualKey.H && KeyHeld(VirtualKey.Control))
            ApplyShowTransformControls(!showTransformControls);
        else if ((transformInspectorEdit is not null || floatingTransform is not null) && IsCancelPress(args.Key)) QueueTransformCancel();
        else if ((transformInspectorEdit is not null || floatingTransform is not null) && IsApplyPress(args.Key)) FinishTransformDraft();
        else return false;
        args.Handled = true; return true;
    }

    private void BeginTransformPointer(PointD point, VirtualKeyModifiers modifiers)
    {
        if (IsFileImporting || distortCompletion is not null || floatingTransform?.Finishing == true) return;
        if (document is not { } owner) return;
        transformPointerMoved = false;
        transformPointerDraftActive = placementDragActive = true;
        QueueCanvasInput(owner, () =>
        {
            // EditorCanvas checks the current overlay before auto-selecting under the press.
            // A newly picked layer is moved even if the press happens to land on its edge.
            var hit = showTransformControls && CurrentTransformValue is { } current
                ? transformCorners is { } shape ? TransformDrag.HitTest(shape, point, 10 / viewport.PointsPerPixel)
                    : TransformDrag.HitTest(current, point, 10 / viewport.PointsPerPixel) : null;
            if (hit is null && !PickTransformLayer(point, modifiers)) { transformPointerDraftActive = placementDragActive = false; return; }
            if (!BeginTransformInspector()) { transformPointerDraftActive = placementDragActive = false; return; }
            var value = transformInspectorValue!;
            var mode = hit?.Mode ?? TransformDragMode.Move;
            if (mode == TransformDragMode.Resize && (modifiers.HasFlag(VirtualKeyModifiers.Control) || transformCorners is not null))
            { transformCorners ??= DistortWarp.CornersOf(value); transformPersistent = true; mode = TransformDragMode.Distort; }
            transformDrag = new(CopyTransform(value), point, mode, hit?.Index ?? 0, transformCorners?.ToArray());
            RefreshTransformInspector(); UpdateTransformHandles();
            transformDuplicateArmed = transformDrag.Mode == TransformDragMode.Move && modifiers.HasFlag(VirtualKeyModifiers.Menu);
            transformDragActive = true; snapLockX = snapLockY = null;
            CacheSnapTargets();
        });
    }
    private bool PickTransformLayer(PointD point, VirtualKeyModifiers modifiers)
    {
        if (document is null || LayerDisplaySnapshot is not { } source) return false;
        bool control = modifiers.HasFlag(VirtualKeyModifiers.Control), shift = modifiers.HasFlag(VirtualKeyModifiers.Shift);
        bool group = selectedLayerIds.Count > 1 || SelectedLayer?.IsGroupOrFalse == true;
        var press = TransformPress.Pick(source, point, SelectedLayer?.Id, CurrentTransformValue, group,
            transformInspectorEdit is not null, transformAutoSelect, control, shift, paintOnMask);
        if (press is null) return false;
        if (!press.Picked) return true;
        var previousPrimary = SelectedLayer?.Id;
        if (control && shift)
        {
            if (selectedLayerIds.Count == 0 && SelectedLayer is { } selected) selectedLayerIds.Add(selected.Id);
            if (selectedLayerIds.Contains(press.LayerId) && selectedLayerIds.Count > 1)
            {
                selectedLayerIds.Remove(press.LayerId);
                selectedLayerId = previousPrimary == press.LayerId ? selectedLayerIds.First() : previousPrimary;
            }
            else { selectedLayerIds.Add(press.LayerId); selectedLayerId = press.LayerId; }
        }
        else { selectedLayerIds.Clear(); selectedLayerIds.Add(press.LayerId); selectedLayerId = press.LayerId; }
        if (selectedLayerId != previousPrimary) { paintOnMask = false; PaintMaskCheck.IsChecked = false; }
        selectingLayer = true;
        try { document.SetActiveLayer(selectedLayerId!.Value); }
        finally { selectingLayer = false; }
        RefreshLayerPanel();
        return true;
    }
    private void ApplyPendingTransformDraft(PointD point)
    {
        if (transformDrag is not { } drag || transformInspectorEdit is null) return;
        if (!transformPointerMoved) return;
        if (transformDuplicateArmed && transformPointerMoved && point != drag.Start)
        {
            transformDuplicateArmed = false;
            if (BeginFloatingDuplicate(drag, point)) return;
            if (!BeginDuplicateTransformPointer(drag)) return;
        }
        if (TransformDrag.UpdatedCorners(drag, point, pendingShift) is { } distortedCorners)
        { SnapLineX.Visibility = SnapLineY.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed; SetTransformCorners(distortedCorners); return; }
        double? lineX = null, lineY = null;
        double tolerance = 10 / Math.Max(viewport.PointsPerPixel, .0001);
        if (drag.Mode == TransformDragMode.Resize && !pendingMoveFree)
        {
            var snap = TransformSnap.ResizePoint(drag, point, snapXs ?? [], snapYs ?? [], tolerance, transformRatioLock, pendingShift, pendingOption);
            point = snap.Point; lineX = snap.X; lineY = snap.Y;
        }
        var value = TransformDrag.Updated(drag, point, transformRatioLock, pendingShift, pendingOption);
        value.Origin = new(Math.Round(value.Origin.X, MidpointRounding.AwayFromZero), Math.Round(value.Origin.Y, MidpointRounding.AwayFromZero));
        value.Size = new(Math.Round(value.Size.Width, MidpointRounding.AwayFromZero), Math.Round(value.Size.Height, MidpointRounding.AwayFromZero));
        value.Rotation = Math.Round(value.Rotation, MidpointRounding.AwayFromZero);
        if (drag.Mode == TransformDragMode.Move && !pendingMoveFree)
        {
            var corners = TransformDrag.HandlePositions(value);
            double left = corners.Min(p => p.X), top = corners.Min(p => p.Y), right = corners.Max(p => p.X), bottom = corners.Max(p => p.Y);
            var snap = TransformSnap.OffsetFor(new(left, top, right - left, bottom - top), snapXs ?? [], snapYs ?? [], tolerance);
            value.Origin = new(value.Origin.X + snap.Offset.Width, value.Origin.Y + snap.Offset.Height);
            lineX = snap.X; lineY = snap.Y;
        }
        ShowSnapLines(new(value.Origin.X, value.Origin.Y, value.Size.Width, value.Size.Height), lineX, lineY,
            document!.Snapshot.Manifest.Width, document.Snapshot.Manifest.Height);
        if (value.IsValid) SetTransformDraft(value);
    }
    private void MoveTransformPointer(PointD point, VirtualKeyModifiers modifiers)
    {
        if (floatingDuplicate is { } gesture) { gesture.Point = point; gesture.Modifiers = modifiers; return; }
        transformPointerMoved = true;
        pendingTransformPoint = point;
        pendingShift = modifiers.HasFlag(VirtualKeyModifiers.Shift);
        pendingOption = modifiers.HasFlag(VirtualKeyModifiers.Menu);
        pendingMoveFree = modifiers.HasFlag(VirtualKeyModifiers.Control);
        PumpRender();
    }
    private bool BeginDuplicateTransformPointer(TransformDragState drag)
    {
        if (document is not { } owner) return false;
        bool finishing = placementFinishing;
        // The Mac commits an earlier Ctrl+T/field draft before beginning the copy gesture.
        CommitTransformNow();
        var selected = new HashSet<Guid>(selectedLayerIds);
        if (SelectedLayer is { } primary) selected.Add(primary.Id);
        var selection = new DuplicateSelectionState(selectedLayerId, selected, new(collapsedGroups));
        // Selecting the copied primary resets the mask target in EditorSession.selectLayers.
        var edit = owner.BeginDuplicateTransformDraft(selected);
        if (edit is null) return false;
        transformDuplicateSelection = selection; transformInspectorEdit = edit; transformInspectorValue = edit.Value;
        transformPointerDraftActive = placementDragActive = transformDragActive = true;
        transformPointerMoved = true; placementFinishing = finishing; transformDrag = drag;
        selectedLayerIds.Clear(); selectedLayerIds.UnionWith(edit.DuplicateSelectedIds);
        selectedLayerId = edit.DuplicateSnapshot!.Manifest.ActiveLayerId;
        paintOnMask = false; PaintMaskCheck.IsChecked = false;
        foreach (var pair in edit.DuplicateMapping!) if (collapsedGroups.Contains(pair.Key)) collapsedGroups.Add(pair.Value);
        if (edit.DuplicateSelectedIds.Count > 1 && SelectedLayer?.ParentId is { } parent) collapsedGroups.Remove(parent);
        RefreshLayerPanel(); CacheSnapTargets();
        return true;
    }
    private void RestoreDuplicateSelection(DuplicateSelectionState selection)
    {
        selectedLayerId = selection.Primary; selectedLayerIds.Clear(); selectedLayerIds.UnionWith(selection.Selected);
        collapsedGroups.Clear(); collapsedGroups.UnionWith(selection.Collapsed);
    }
    private void SyncHistorySelection()
    {
        if (document is null) return;
        var primary = document.Snapshot.Manifest.ActiveLayerId;
        paintOnMask = paintOnMask && selectedLayerId == primary && document.Snapshot.Masks.ContainsKey(primary ?? Guid.Empty);
        PaintMaskCheck.IsChecked = paintOnMask;
        selectedLayerId = primary; selectedLayerIds.Clear(); if (primary is { } id) selectedLayerIds.Add(id);
    }
    private bool ApplyHistory(bool redo, Guid? historyId = null)
    {
        restoringHistory = true;
        try
        {
            if (document is not { } owner || !(historyId is { } target ? owner.GoToHistory(target) : redo ? owner.Redo() : owner.Undo())) return false;
            if (owner.Snapshot.Manifest.ActiveLayerId != selectedLayerId) { SyncHistorySelection(); RefreshLayerPanel(); }
            return true;
        }
        finally { restoringHistory = false; }
    }
}
