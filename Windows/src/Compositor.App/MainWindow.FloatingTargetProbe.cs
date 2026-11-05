#if DEBUG
using Compositor.Core;
using Windows.System;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private int renderInputFailureCount, renderPublicationFailureCount;
    private async Task VerifyFloatingTargetsAsync(Dictionary<string, object> report)
    {
        var scene = FloatingTargetScene(); var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        var other = scene.Snapshot.Manifest.Layers.Single(layer => layer.Id != id).Id;
        var selection = scene.ActiveSelection;
        OpenDocument(scene); await WaitForFramesAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(false);
        beforeFrameRender = () => { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Floating target preparation reader timed out."); };
        try
        {
            OnFreeTransform(this, new()); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Require(floatingTransform is { Display: null } && transformInspectorEdit is null, "Preparation fixture already published its inspector.");
            HandleThumbnailClick(other, true, VirtualKeyModifiers.Control);
            report["floatingPreparingTargetsObserved"] = new { maskEnabled = PaintMaskCheck.IsEnabled,
                targetEnabled = transformTargetCombo.IsEnabled, selectionPending = selectionWorkResult is not null };
            Require(selectionWorkResult is null && ReferenceEquals(selection, scene.ActiveSelection) && !scene.CanUndo,
                "Ctrl thumbnail entered selection work while floating pixels were preparing.");
            Require(!PaintMaskCheck.IsEnabled && !transformTargetCombo.IsEnabled, "Preparing floating targets remained editable.");
            PaintMaskCheck.IsChecked = true; transformTargetCombo.SelectedIndex = 1;
            Require(!paintOnMask && PaintMaskCheck.IsChecked == false && transformTargetCombo.SelectedIndex == 0,
                "A disabled target changed the preparing floating paint target.");
            CopyMaskToRow(id, other);
            report["floatingPreparingMaskCopyObserved"] = new { queued = renderInputs.HasPending };
            Require(!renderInputs.HasPending, "Mask copying entered the render queue while floating pixels were preparing.");
        }
        finally { beforeFrameRender = null; release.Set(); }
        await WaitForTransformStateAsync(() => floatingTransform?.Display is not null); await WaitForFramesAsync();
        Require(ReferenceEquals(selection, scene.ActiveSelection) && !scene.CanUndo && !paintOnMask, "Preparation target input invalidated the floating source.");
        AssertTransformUpload(LayerDisplaySnapshot!); report["floatingPreparingThumbnailCtrlAndTargetsKeepSource"] = "passed";
        HandleThumbnailClick(id, false, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift);
        HandleThumbnailClick(other, true, VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu);
        Require(selectionWorkResult is null && ReferenceEquals(selection, scene.ActiveSelection) && !scene.CanUndo,
            "Ready floating Ctrl thumbnails changed selection/history.");
        QueueTransformCancel(); await WaitForFramesAsync(); AssertUploadedFrame(scene);
        Require(PaintMaskCheck.IsEnabled && transformTargetCombo.IsEnabled, "Cancelling floating did not restore source target controls.");
        report["floatingReadyThumbnailCtrlCancelRestoresTargets"] = "passed";

        OnFreeTransform(this, new()); await WaitForFramesAsync(); ChangeTransformNumber(TransformField.X, 65); await WaitForFramesAsync();
        var start = CurrentTransformValue!.Center;
        showTransformControls = false; BeginTransformPointer(start, VirtualKeyModifiers.Menu); await WaitForFramesAsync(); showTransformControls = true;
        var copyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var copyRelease = new ManualResetEventSlim(false);
        beforeFrameRender = () => { copyEntered.TrySetResult(); if (!copyRelease.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Floating target copy reader timed out."); };
        var clicked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            MoveTransformPointer(new(start.X + 20, start.Y + 11), VirtualKeyModifiers.Menu | VirtualKeyModifiers.Control);
            await copyEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            HandleThumbnailClick(other, false, VirtualKeyModifiers.Control);
            Require(selectionWorkResult is null && ReferenceEquals(selection, scene.ActiveSelection), "Pending floating copy accepted a Ctrl thumbnail.");
            PaintMaskCheck.IsChecked = true;
            Require(!paintOnMask && PaintMaskCheck.IsChecked == false, "Pending floating copy changed its paint target.");
            CopyMaskToRow(id, other);
            Require(!renderInputs.HasPending, "Pending floating copy accepted a mask copy.");
            // This input drains after merge publication, before the asynchronous copy resumes.
            QueueCanvasInput(scene, () =>
            {
                report["floatingCopyTargetGapObserved"] = new { merged = floatingTransform is null, copying = floatingDuplicate is not null,
                    maskEnabled = PaintMaskCheck.IsEnabled, targetEnabled = transformTargetCombo.IsEnabled };
                Require(floatingTransform is null && floatingDuplicate is not null, "Target fixture missed the merge/copy gap.");
                Require(!PaintMaskCheck.IsEnabled && !transformTargetCombo.IsEnabled, "Merge/copy gap unlocked target controls.");
                CopyMaskToRow(id, other);
                Require(!renderInputs.HasPending, "Merge/copy gap accepted a mask copy.");
                HandleThumbnailClick(other, true, VirtualKeyModifiers.Menu); clicked.TrySetResult();
            });
        }
        finally { beforeFrameRender = null; copyRelease.Set(); }
        await clicked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForTransformStateAsync(() => SelectedLayer?.Id == other && floatingDuplicate is null && transformInspectorEdit is null);
        await WaitForFramesAsync(); AssertMaskUpload(scene, other);
        Require(scene.LayerCount == 3 && paintOnMask && CurrentMaskAloneId == other && scene.CanUndo,
            "Target switch copied the wrong row or raced the preceding floating copy.");
        Require(LayerList.SelectedItem is Microsoft.UI.Xaml.Controls.ListViewItem { Tag: LayerRow selectedRow } && selectedRow.Layer.Id == other,
            "Layer list selection did not follow the published target row.");
        var copied = scene.Snapshot.Manifest.Layers.Single(layer => layer.Id != id && layer.Id != other);
        report["floatingCopyTargetResultObserved"] = new { copied = copied.Id, origin = copied.Transform.Origin, size = copied.Transform.Size,
            sourceWidth = scene.Snapshot.Images[id].Width, copiedWidth = scene.Snapshot.Images[copied.Id].Width };
        Near(85, copied.Transform.Origin.X, "Target continuation copied X");
        Require(scene.Snapshot.Images[copied.Id].Pixels.AsSpan().SequenceEqual(scene.Snapshot.Images[id].Pixels), "Target switch copied another layer's asset.");
        OnUndo(this, new()); await WaitForFramesAsync(); Require(scene.LayerCount == 2 && scene.CanUndo, "Target switch lost copy Undo.");
        OnUndo(this, new()); await WaitForFramesAsync(); Require(!scene.CanUndo && ReferenceEquals(selection, scene.ActiveSelection), "Target switch added history beyond merge/copy.");
        report["floatingCopyGapThumbnailTargetWaitsForBothStepsAndSolo"] = "passed";

        scene = FloatingTargetScene(); id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        scene.SetSelection(null); OpenDocument(scene); await WaitForFramesAsync();
        OnFreeTransform(this, new()); ChangeTransformNumber(TransformField.X, 11); await WaitForFramesAsync();
        var edit = transformInspectorEdit; var value = CurrentTransformValue;
        PaintMaskCheck.IsChecked = true; await WaitForFramesAsync();
        Require(paintOnMask && transformTargetCombo.SelectedIndex == 1 && ReferenceEquals(edit, transformInspectorEdit)
            && ReferenceEquals(value, CurrentTransformValue) && !scene.CanUndo, "Global target committed or replaced the same-layer persistent draft.");
        transformTargetCombo.SelectedIndex = 0;
        await WaitForTransformStateAsync(() => !paintOnMask); await WaitForFramesAsync();
        Require(PaintMaskCheck.IsChecked == false && ReferenceEquals(edit, transformInspectorEdit) && !scene.CanUndo,
            "Inspector target committed the same-layer persistent draft.");
        QueueTransformCancel(); await WaitForFramesAsync(); AssertUploadedFrame(scene);
        report["globalAndInspectorTargetKeepSameLayerPersistentDraft"] = "passed";

        scene = FloatingTargetScene(); id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        scene.SetSelection(null); OpenDocument(scene); ToolCombo.SelectedIndex = (int)CanvasTool.Brush;
        Require(await WaitForToolSwitchLandedAsync(CanvasTool.Brush),
            "the Brush switch never finished before the fixture began its stroke.");
        await WaitForFramesAsync();
        BeginPointerStroke(new(30, 40));
        Require(pointerStroke?.Tool == CanvasTool.Brush && pointerStroke.OnMask == false, "Target guard fixture did not begin a pixel brush stroke.");
        PaintMaskCheck.IsChecked = true;
        Require(!paintOnMask && PaintMaskCheck.IsChecked == false && pointerStroke?.OnMask == false, "Target changed while a pixel stroke was active.");
        await WaitForFramesAsync();
        Require(pointerStroke?.Started == true && !paintOnMask, "Target guard did not keep the active pixel brush stroke.");
        FinishPointerStroke(); await WaitForFramesAsync(); AssertUploadedFrame(scene);
        report["globalTargetRejectsActiveStroke"] = "passed";

        scene = EditorDocument.Create(200, 140); OpenDocument(scene); await WaitForFramesAsync();
        PaintMaskCheck.IsChecked = true;
        Require(!paintOnMask && PaintMaskCheck.IsChecked == false && !PaintMaskCheck.IsEnabled, "A layer without a mask accepted the mask target.");
        report["globalTargetRejectsMissingMask"] = "passed";

        scene = FloatingTargetScene(); id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        other = scene.Snapshot.Manifest.Layers.Single(layer => layer.Id != id).Id;
        scene.SetSelection(null); OpenDocument(scene); await WaitForFramesAsync();
        var primaryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var primaryRelease = new ManualResetEventSlim(false);
        beforeFrameRender = () => { primaryEntered.TrySetResult(); if (!primaryRelease.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Target primary reader timed out."); };
        try
        {
            RenderCanvas(); await primaryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            HandleThumbnailClick(other, false, VirtualKeyModifiers.None); PaintMaskCheck.IsChecked = true;
            Require(!paintOnMask && renderInputs.HasPending, "Target fixture changed primary before the queued switch.");
        }
        finally { beforeFrameRender = null; primaryRelease.Set(); }
        await WaitForTransformStateAsync(() => SelectedLayer?.Id == other); await WaitForFramesAsync(); AssertUploadedFrame(scene);
        Require(!paintOnMask && PaintMaskCheck.IsChecked == false && !scene.CanUndo, "Queued paint target applied to a different primary layer.");
        report["globalTargetQueuedPrimaryReplacement"] = "passed";

        scene = FloatingTargetScene(); scene.SetSelection(null); OpenDocument(scene); await WaitForFramesAsync();
        var ownerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var ownerRelease = new ManualResetEventSlim(false);
        beforeFrameRender = () => { ownerEntered.TrySetResult(); if (!ownerRelease.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Target owner reader timed out."); };
        var replacement = FloatingTargetScene(); replacement.SetSelection(null);
        try
        {
            RenderCanvas(); await ownerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            PaintMaskCheck.IsChecked = true;
            Require(!paintOnMask && renderInputs.HasPending, "Target input mutated live state before its serial reader ended.");
            OpenDocument(replacement);
        }
        finally { beforeFrameRender = null; ownerRelease.Set(); }
        await WaitForFramesAsync(); AssertUploadedFrame(replacement);
        Require(!paintOnMask && PaintMaskCheck.IsChecked == false && !scene.CanUndo && !replacement.CanUndo, "Retired target input leaked into a new owner.");
        report["globalTargetQueuedOwnerRetirement"] = "passed";
    }

    private static EditorDocument FloatingTargetScene()
    {
        var scene = EditorDocument.Create(200, 140); var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        var image = new Surface(6, 6); image.Pixels.AsSpan().Fill(255);
        var other = scene.ImportImage(image, "Target"); other.Transform.Origin = new(120, 90);
        foreach (var layer in scene.Snapshot.Manifest.Layers)
        {
            layer.MaskFile = ProjectValidator.Uuid(layer.Id) + ".mask.png"; layer.MaskLinked = false;
            layer.MaskPlacement = new() { Origin = layer.Id == id ? new(30, 40) : new(120, 90), Size = new(6, 6), Sampling = LayerSampling.Nearest };
            ((Dictionary<Guid, MaskSurface>)scene.Snapshot.Masks)[layer.Id] = new(3, 3, [255, 255, 255, 255, 0, 255, 255, 255, 255]);
        }
        scene.SetActiveLayer(id); scene = new(scene.Snapshot); scene.SetSelection(Selection.Rectangle(200, 140, 30, 40, 24, 16));
        return scene;
    }
}
#endif
