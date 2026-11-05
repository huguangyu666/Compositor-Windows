#if DEBUG
using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task VerifyDistortAsync(Dictionary<string, object> report)
    {
        var scene = TransformScene(); OpenDocument(scene); await WaitForFramesAsync();
        var start = CurrentTransformValue!.Point(TransformDrag.Handles[0]);
        BeginTransformPointer(start, VirtualKeyModifiers.Control); await WaitForFramesAsync();
        report["distortControlHandleObserved"] = new { mode = transformDrag?.Mode.ToString(), persistent = transformPersistent };
        Require(transformDrag?.Mode.ToString() == "Distort" && transformPersistent,
            "Ctrl-handle press did not enter a persistent distortion draft.");
        var basis = transformCorners!.ToArray();
        MoveTransformPointer(new(start.X - 7.25, start.Y - 2.5), VirtualKeyModifiers.Shift); await WaitForFramesAsync();
        Near(basis[0].X - 7.25, transformCorners![0].X, "Distort Shift X"); Near(basis[0].Y, transformCorners[0].Y, "Distort Shift Y");
        FinishPlacementDrag(); await WaitForFramesAsync();
        Require(!scene.CanUndo && transformPersistent && DistortBox.Visibility == Visibility.Visible && TransformBox.Visibility == Visibility.Collapsed
            && RotationHandleDot.Visibility == Visibility.Collapsed && RotationStem.Visibility == Visibility.Collapsed, "Distort released early or overlay stayed affine.");
        AssertTransformUpload(transformRenderedPreview!.Snapshot);
        var edge = TransformDrag.HandlePositions(transformCorners)[3]; var previous = transformCorners.ToArray();
        BeginTransformPointer(edge, VirtualKeyModifiers.None); await WaitForFramesAsync();
        MoveTransformPointer(new(edge.X + 3.125, edge.Y + 1.75), VirtualKeyModifiers.None); FinishPlacementDrag(); await WaitForFramesAsync();
        Near(previous[1].X + 3.125, transformCorners![1].X, "Distort edge first"); Near(previous[2].Y + 1.75, transformCorners[2].Y, "Distort edge second");
        Require(transformDrag is null && !scene.CanUndo, "Distort edge ended the persistent transaction.");
        report["distortCtrlCornerShiftPairedEdgePersistentOverlayGpu"] = "passed";
        FinishTransformDraft(); await WaitForFramesAsync(); Require(distortCompletion is null && transformCorners is null && scene.CanUndo, "Distort worker did not publish its commit.");
        AssertUploadedFrame(scene); OnUndo(this, new()); await WaitForFramesAsync(); Require(!scene.CanUndo, "Distort used more than one Undo.");
        OnRedo(this, new()); await WaitForFramesAsync(); AssertUploadedFrame(scene);
        report["distortFullResolutionWorkerCommitOneUndoRedoGpu"] = "passed";

        scene = TransformScene(); OpenDocument(scene); await WaitForFramesAsync();
        var original = scene.Snapshot.Images.Values.First(); var originalManifest = ManifestJson.Serialize(scene.Snapshot.Manifest);
        await StartCorner(); var good = transformCorners!.ToArray();
        SetTransformCorners([new(1, 1), new(2, 2), new(3, 3), new(4, 4)]); await WaitForFramesAsync();
        Require(good.SequenceEqual(transformCorners!), "Collapsed shape replaced the last usable draft.");
        QueueTransformCancel(); await WaitForFramesAsync(); Require(ReferenceEquals(original, scene.Snapshot.Images.Values.First())
            && originalManifest == ManifestJson.Serialize(scene.Snapshot.Manifest) && !scene.CanUndo, "Cancel changed pixels/history."); AssertUploadedFrame(scene);
        report["distortCollapsedShapeIgnoredCancelRestoresSourceGpu"] = "passed";

        foreach (bool linked in new[] { false, true })
        {
            scene = TransformScene(); var layer = scene.Snapshot.Manifest.Layers[0];
            var mask = new MaskSurface(20, 10); mask.Pixels.AsSpan().Fill(255); mask.Pixels[50] = 0;
            ((Dictionary<Guid, MaskSurface>)scene.Snapshot.Masks)[layer.Id] = mask; layer.MaskFile = ProjectValidator.Uuid(layer.Id) + ".mask.png";
            layer.MaskLinked = linked; layer.MaskPlacement = new() { Origin = new(22, 32), Size = new(20, 10) };
            var oldPlacement = layer.MaskPlacement; var pixels = scene.Snapshot.Images[layer.Id];
            OpenDocument(scene); await WaitForFramesAsync();
            if (!linked) { ChangePaintTarget(true); await WaitForFramesAsync(); }
            await StartCorner(); AssertTransformUpload(transformRenderedPreview!.Snapshot); FinishTransformDraft(); await WaitForFramesAsync(); AssertUploadedFrame(scene);
            Require(scene.CanUndo && (linked ? !ReferenceEquals(pixels, scene.Snapshot.Images[layer.Id]) : ReferenceEquals(pixels, scene.Snapshot.Images[layer.Id])),
                "Mask/pixel target warped the wrong asset.");
            Require(!Equals(oldPlacement, scene.Snapshot.Manifest.Layers[0].MaskPlacement), "Placed mask did not follow its distortion.");
            OnUndo(this, new()); await WaitForFramesAsync(); Require(!scene.CanUndo && ReferenceEquals(mask, scene.Snapshot.Masks[layer.Id]), "Placed mask Undo count/assets.");
            report[linked ? "distortLinkedPlacedMaskPixelsCommitGpu" : "distortIndependentMaskOnlyBackgroundCommitGpu"] = "passed";
        }

        scene = TransformScene(two: true); var ids = scene.Snapshot.Manifest.Layers.Select(l => l.Id).ToArray();
        OpenDocument(scene); await WaitForFramesAsync(); selectedLayerIds.Clear(); selectedLayerIds.UnionWith(ids); RefreshLayerPanel(); RefreshTransformInspector(true);
        await StartCorner(); Require(transformInspectorEdit!.IsGroup, "Selected layers did not use the group box.");
        AssertTransformUpload(transformRenderedPreview!.Snapshot); FinishTransformDraft(); await WaitForFramesAsync(); AssertUploadedFrame(scene);
        OnUndo(this, new()); await WaitForFramesAsync(); Require(!scene.CanUndo, "Multi-layer distortion opened per-member history.");
        report["distortVisibleMultiLayerSharedPerspectiveOneUndoGpu"] = "passed";

        foreach (bool fold in new[] { false, true })
        {
            scene = FloatingTargetScene(); original = scene.Snapshot.Images[scene.Snapshot.Manifest.ActiveLayerId!.Value];
            OpenDocument(scene); await WaitForFramesAsync(); OnFreeTransform(this, new()); await WaitForFramesAsync();
            await StartCorner();
            if (fold)
            {
                var corners = transformCorners!.ToArray(); BeginTransformPointer(corners[0], VirtualKeyModifiers.None); await WaitForFramesAsync();
                MoveTransformPointer(new(corners[2].X + 5, corners[2].Y + 5), VirtualKeyModifiers.None); FinishPlacementDrag(); await WaitForFramesAsync();
            }
            else
            {
                if (directOutlineEnabled)
                {
                    await WaitForDirectOutlineAsync();
                    var rootPoints = directSourceOutline!.Points; var shown = directProjectedOutline!.Points;
                    Require(!ReferenceEquals(directSourceOutline, directProjectedOutline) && shown.Length == rootPoints.Length
                        && directOutlinePlacement == System.Numerics.Matrix3x2.Identity,
                        "Perspective selection still used an affine outline.");
                    var offset = scene.ActiveSelection!.OutlineOffset; var first = rootPoints[0];
                    var expected = DistortWarp.DocumentMap(floatingTransform!.Edit.Value, transformCorners!)(new(first.X + offset.X, first.Y + offset.Y));
                    Require(shown[0] == new System.Numerics.Vector2((float)expected.X, (float)expected.Y),
                        "Direct perspective changed the projected float endpoint.");
                    report["distortPerspectiveOutlineObserved"] = new { backend = "direct", sourcePoints = rootPoints.Length, displayedPoints = shown.Length };
                }
                else
                {
                    var rootFigures = SelectionOutlineFigures(selectionOutlineGeometry).ToArray();
                    var displayedFigures = SelectionOutlineFigures(SelectionOutline.Data).ToArray();
                    report["distortPerspectiveOutlineObserved"] = new { data = SelectionOutline.Data?.GetType().Name, root = selectionOutlineGeometry is not null,
                        same = ReferenceEquals(SelectionOutline.Data, selectionOutlineGeometry), figures = (SelectionOutline.Data as PathGeometry)?.Figures.Count,
                        rootFigures = rootFigures.Length, transform = SelectionOutline.RenderTransform?.GetType().Name,
                        matrix = (SelectionOutline.RenderTransform as MatrixTransform)?.Matrix,
                        corners = transformCorners, floating = floatingTransform is not null };
                    Require(SelectionOutline.Data is { } path && !ReferenceEquals(path, selectionOutlineGeometry)
                        && displayedFigures.Length == rootFigures.Length && IdentityOutlineTransform(),
                        "Perspective selection still used an affine outline.");
                    var rootPoint = rootFigures[0].StartPoint; var offset = scene.ActiveSelection!.OutlineOffset;
                    var expected = DistortWarp.DocumentMap(floatingTransform!.Edit.Value, transformCorners!)(new(rootPoint.X + offset.X, rootPoint.Y + offset.Y));
                    var point = displayedFigures[0].StartPoint;
                    Near(expected.X, point.X, "Perspective outline X"); Near(expected.Y, point.Y, "Perspective outline Y");
                }
            }
            Require(fold ? !OutlineIsVisible : OutlineIsVisible, "Fold/perspective outline visibility.");
            AssertTransformUpload(LayerDisplaySnapshot!); FinishTransformDraft(); await WaitForFramesAsync(); AssertUploadedFrame(scene);
            Require(scene.CanUndo && scene.LayerCount == 2 && (fold ? scene.ActiveSelection is null : scene.ActiveSelection is not null), "Floating warp did not merge its pixels/selection.");
            OnUndo(this, new()); await WaitForFramesAsync(); Require(!scene.CanUndo && ReferenceEquals(original, scene.Snapshot.Images[scene.Snapshot.Manifest.ActiveLayerId!.Value]), "Floating distort Undo source.");
            report[fold ? "distortFloatingFoldHiddenOutlineMergeUndoGpu" : "distortFloatingPerspectiveOutlineMergeSelectionOneUndoGpu"] = "passed";
        }

        scene = TransformScene(); OpenDocument(scene); await WaitForFramesAsync(); await StartCorner();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var release = new ManualResetEventSlim(false);
        beforeFrameRender = () => { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Distort commit reader timed out."); };
        Task? output = null;
        try
        {
            output = FinishTransformBeforeOutputAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Require(!output.IsCompleted && !scene.CanUndo && distortCompletion is not null && !transformFields[TransformField.X].IsEnabled,
                "Output did not wait for the distorted full-resolution reader.");
            OpenDocument(TransformScene()); await output.WaitAsync(TimeSpan.FromSeconds(5));
            Require(!scene.CanUndo && !document!.CanUndo && distortCompletion is null, "Retired distortion mutated history or held its output waiter.");
        }
        finally { beforeFrameRender = null; release.Set(); if (output is not null) await output; }
        await WaitForFramesAsync(); AssertUploadedFrame(document!); report["distortHeldFullCommitOwnerRetirementOutputWaiterGpu"] = "passed";

        scene = TransformScene(); OpenDocument(scene); await WaitForFramesAsync();
        var commandEdit = scene.BeginTransformDraft([scene.Snapshot.Manifest.Layers[0].Id])!;
        var commandCorners = DistortWarp.CornersOf(commandEdit.Value); commandCorners[0] = new(15.5, 25.125);
        Require(BeginDistortCornersCommand(scene, commandEdit, commandCorners), "Numeric corner command did not enter the worker queue.");
        await WaitForFramesAsync(); AssertUploadedFrame(scene); Require(scene.CanUndo && distortCompletion is null, "Numeric corner worker did not publish history.");
        OnUndo(this, new()); await WaitForFramesAsync(); Require(!scene.CanUndo, "Numeric corner command opened more than one Undo.");
        OpenDocument(TransformScene()); await WaitForFramesAsync();
        Require(!BeginDistortCornersCommand(scene, commandEdit, commandCorners) && !document!.CanUndo, "Retired numeric dialog edited a replacement document.");
        report["distortNumericCornersSharedWorkerOneUndoRetiredOwner"] = "passed";

        scene = TransformScene(); OpenDocument(scene); await WaitForFramesAsync(); await StartCorner();
        var copyStart = CurrentTransformValue!.Center;
        BeginTransformPointer(copyStart, VirtualKeyModifiers.Menu); await WaitForFramesAsync();
        MoveTransformPointer(new(copyStart.X + 20, copyStart.Y), VirtualKeyModifiers.Menu); FinishPlacementDrag(); await WaitForFramesAsync();
        report["distortAltCopyIdleObserved"] = new { layers = scene.LayerCount, undo = scene.CanUndo, pendingCopy = floatingDuplicate is not null,
            released = floatingDuplicate?.Released, priorComplete = floatingDuplicate?.Prior.IsCompleted,
            copyComplete = floatingDuplicate?.Completion.Task.IsCompleted, workerBusy, pendingInput = HasPendingInteraction };
        // Frame idle does not include the continuation scheduled by the preceding warp.
        if (floatingDuplicate is { } copy) Require(await copy.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5)), "Distorted copy continuation did not start.");
        await WaitForFramesAsync();
        Require(scene.LayerCount == 2 && floatingDuplicate is null, "Distorted Alt-copy did not wait for its preceding full warp."); AssertUploadedFrame(scene);
        OnUndo(this, new()); await WaitForFramesAsync(); Require(scene.LayerCount == 1 && scene.CanUndo, "Copy Undo lost the preceding distorted pixels.");
        OnUndo(this, new()); await WaitForFramesAsync(); Require(!scene.CanUndo, "Distort and copy did not have separate ordered history.");
        report["distortAltCopyAfterFullWarpOrderedTwoUndoGpu"] = "passed";

        async Task StartCorner()
        {
            var point = CurrentTransformValue!.Point(TransformDrag.Handles[0]); BeginTransformPointer(point, VirtualKeyModifiers.Control); await WaitForFramesAsync();
            MoveTransformPointer(new(point.X - 4.25, point.Y - 2.5), VirtualKeyModifiers.None); FinishPlacementDrag(); await WaitForFramesAsync();
        }
        bool IdentityOutlineTransform() => SelectionOutline.RenderTransform is MatrixTransform matrix && matrix.Matrix.M11 == 1
            && matrix.Matrix.M12 == 0 && matrix.Matrix.M21 == 0 && matrix.Matrix.M22 == 1 && matrix.Matrix.OffsetX == 0 && matrix.Matrix.OffsetY == 0;
    }
}
#endif
