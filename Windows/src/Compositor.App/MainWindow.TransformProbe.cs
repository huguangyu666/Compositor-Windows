#if DEBUG
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Compositor.Core;
using Compositor.Renderer;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using Windows.System;

namespace Compositor.App;

public sealed partial class MainWindow
{
    internal async Task RunTransformProbeAsync()
    {
        var report = new Dictionary<string, object>();
        try
        {
            await Task.Delay(500); AppWindow.Resize(new SizeInt32(1280, 820));
            if (Environment.GetEnvironmentVariable("COMPOSITOR_PROBE_FOREGROUND") == "1")
                report["startupForegroundPrecondition"] = await RequireForegroundForFocusProbeAsync(60000);
            int inputFailures = renderInputFailureCount, publicationFailures = renderPublicationFailureCount;
            await VerifyFileImportsAsync(report);
            await VerifyExportResolutionAsync(report);
            await VerifyPsdBlendImportsAsync(report);
            await VerifyDistortAsync(report);
            await VerifyGrayDistortAsync(report);
            await VerifyFloatingCommandsAsync(report);
            await VerifyFloatingTargetsAsync(report);
            await VerifyFloatingContinuationsAsync(report);
            await VerifyInlineTextAsync(report);
            var scene = TransformScene(); OpenDocument(scene);
            // Recorded before the switch, because it decides which of the two remaining causes a red has: if the
            // combo already read Move, assigning Move raises no SelectionChanged at all and the handler never runs,
            // which means the field/combo disagreement was made earlier. If it read something else, the handler ran
            // and put the field back on its own way out (MainWindow.xaml.cs:358 reverts `tool` without touching the
            // combo, which is the one path that ends with the combo on Move and the tool on the old tool).
            var comboBefore = ToolCombo.SelectedIndex;
            ToolCombo.SelectedIndex = (int)CanvasTool.Move;
            await WaitForFramesAsync(); RootGrid.UpdateLayout();
            var id = scene.Snapshot.Manifest.Layers[0].Id; var original = ManifestJson.Serialize(scene.Snapshot.Manifest);
            var originalImage = scene.Snapshot.Images[id];
            var x = transformFields[TransformField.X];
            // #57's cause, measured 2026-10-21 (Windows/docs/f57-focus-geometry-2026-10-21.md §6): the reds carry
            // `tool=Text combo=0 collapsed=1` — the picker says Move while the app's own tool is another one, and
            // the one writer of the strip's visibility (MainWindow.xaml.cs:390) keeps it Collapsed until `tool`
            // says Move, so `Focus()` is being asked of a field that is not on screen. How the picker and the tool
            // came to disagree is still open (f57-focus-geometry… §7): either the event never ran because the index
            // was already Move, or a switch retired itself on the way out. The earlier version of this gate waited
            // on the field's `IsEnabled`, which is set from the properties dock (MainWindow.Transform.cs:202) and
            // never depends on the tool, so it could not hold anything: it passed while the strip was hidden.
            // Waiting for the tool the assertion actually runs on is stricter than what it replaces — the run now
            // refuses to start early instead of asking for focus of something the user could not see.
            var toolGateWatch = Stopwatch.StartNew();
            var toolLanded = await WaitUntilAsync(() => tool == CanvasTool.Move
                        && transformFields[TransformField.X].IsEnabled, 8000);
            // How long the gate actually held, on every run: the size of the window this group used to race is the
            // number that says whether the wait is load-bearing or a ceremony.
            report["transformFocusToolGateMs"] = Math.Round(toolGateWatch.Elapsed.TotalMilliseconds, 1);
            Require(toolLanded,
                $"the Move tool never applied after the combo selection (tool={tool} combo={ToolCombo.SelectedIndex} "
                + $"comboBefore={comboBefore} controls={TransformControls.Visibility}).");
            // #57's next measurement: the activation stream is recorded on the way past, not only when the
            // assertion fails, so a green run says the same thing a red one does. The flake is being read here as
            // "does this field take focus while the window is not the foreground window" rather than left as a
            // nameless timing jitter.
            report["transformFocusPreconditions"] = $"activations={windowActivationCount} lastState={lastActivationState}"
                + $" focused={Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(RootGrid.XamlRoot)?.GetType().Name}"
                + $" fieldIsThis={ReferenceEquals(x, transformFields[TransformField.X])} loaded={x.IsLoaded}";
            // The next layer down, and the one that has been missing: that readout is identical in the green and the
            // red samples, so the window's activation and the field's identity are already ruled out. What has not
            // been looked at is whether this box is on screen and measured at the instant focus is asked for —
            // `TransformControls` starts Collapsed (MainWindow.xaml:279) and the tool switch fills it on a later
            // dispatcher turn. Whether an unmeasured or hidden-but-enabled box is one WinUI refuses focus for is
            // not something this file may assume; that question is what the injected collapse round answers.
            // Recorded on every run, not only on the failing one, so a green sample is a control for the red one
            // instead of another "读数一字不差".
            string FieldGeometry(Microsoft.UI.Xaml.Controls.TextBox field)
            {
                // Up the visual chain instead of `IsVisible`, which this SDK projection does not expose on the
                // control: the point is which link in the chain is not Visible and which one has no height yet.
                var chain = new List<string>();
                int collapsed = 0, unmeasured = 0;
                if (field.IsLoaded)
                {
                    // The walk itself must not be able to fail the group it is measuring: an ancestor that is not a
                    // visual would otherwise throw here and read as a brand-new flake of my own making.
                    try
                    {
                        for (DependencyObject? at = field; at is not null; )
                        {
                            var element = (UIElement)at;
                            var height = (at as FrameworkElement)?.ActualHeight ?? -1;
                            if (element.Visibility != Visibility.Visible) collapsed++;
                            if (height <= 0) unmeasured++;
                            chain.Add($"{element.GetType().Name}:{element.Visibility}:{height:0.#}");
                            at = chain.Count > 24 ? null : Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(at);
                            if (ReferenceEquals(at, RootGrid)) { chain.Add("RootGrid"); break; }
                        }
                    }
                    catch (Exception ex)
                    {
                        chain.Add($"walk stopped at {chain.Count}: {ex.GetType().Name}");
                    }
                }
                else chain.Add("not-loaded");
                return $"controls={TransformControls.Visibility}/{TransformControls.ActualWidth:0.#}×{TransformControls.ActualHeight:0.#}"
                    + $" field={field.Visibility} loaded={field.IsLoaded}"
                    + $" size={field.ActualWidth:0.#}×{field.ActualHeight:0.#} enabled={field.IsEnabled}"
                    + $" focusState={field.FocusState} chain={chain.Count} collapsed={collapsed} unmeasured={unmeasured}"
                    + $" tool={tool} combo={ToolCombo.SelectedIndex} inspector={transformInspectorValue is not null}"
                    + $" path=[{string.Join(" < ", chain)}]";
            }
            var geometry = FieldGeometry(x);
            report["transformFocusFieldGeometry"] = geometry;
            var tookFocus = x.Focus(FocusState.Programmatic);
            var focusedAfter = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(RootGrid.XamlRoot)?.GetType().Name;
            Require(tookFocus,
                $"the position field was enabled but could not take focus "
                + $"(activations={windowActivationCount} lastState={lastActivationState} "
                + $"focused={Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(RootGrid.XamlRoot)?.GetType().Name} "
                + $"fieldIsThis={ReferenceEquals(x, transformFields[TransformField.X])} loaded={x.IsLoaded}) "
                + $"[{geometry}] focus after the failed call={focusedAfter}.");
            x.Text = "11.125"; await WaitForTransformStateAsync(() => transformInspectorValue?.Origin.X == 11.125); await WaitForFramesAsync();
            Near(11.125, transformInspectorValue!.Origin.X, "Typed fractional X");
            Require(original == ManifestJson.Serialize(scene.Snapshot.Manifest) && !scene.CanUndo, "Typing mutated the document/history.");
            Require(transformRenderedPreview is not null, "Typing did not present a draft."); AssertTransformUpload(transformRenderedPreview!.Snapshot);
            CanvasStage.Focus(FocusState.Programmatic); await WaitForTransformStateAsync(() => transformInspectorEdit is null); await WaitForFramesAsync();
            Near(11.125, scene.Snapshot.Manifest.Layers[0].Transform.Origin.X, "Focus loss commit");
            Require(ReferenceEquals(originalImage, scene.Snapshot.Images[id]), "Numeric transform resampled raster assets.");
            OnUndo(this, new RoutedEventArgs()); await WaitForFramesAsync(); Require(!scene.CanUndo, "Typed value opened more than one undo step.");
            Near(20.25, scene.Snapshot.Manifest.Layers[0].Transform.Origin.X, "Undo X"); AssertUploadedFrame(scene);
            OnRedo(this, new RoutedEventArgs()); await WaitForFramesAsync(); AssertUploadedFrame(scene);
            report["actualTextFocusFractionDraftUndoRedo"] = "passed";

            scene = TransformScene(); OpenDocument(scene); await WaitForFramesAsync();
            OnFreeTransform(this, new RoutedEventArgs()); await WaitForFramesAsync();
            var center = transformInspectorValue!.Center;
            var scale = transformFields[TransformField.Scale]; Require(scale.Focus(FocusState.Programmatic), "Scale focus");
            scale.Text = "150"; await WaitForTransformStateAsync(() => transformInspectorValue?.Size.Width == 30); await WaitForFramesAsync();
            Near(30, transformInspectorValue!.Size.Width, "Scale pixel basis"); Near(center.X, transformInspectorValue.Center.X, "Scale center X");
            var rotation = transformFields[TransformField.Rotation]; Require(rotation.Focus(FocusState.Programmatic), "Rotation focus");
            rotation.Text = "379.875"; await WaitForTransformStateAsync(() => transformInspectorValue?.Rotation == 19.875); await WaitForFramesAsync(); Near(19.875, transformInspectorValue.Rotation, "Rotation remainder");
            CanvasStage.Focus(FocusState.Programmatic); await WaitForFramesAsync();
            Require(transformPersistent && !scene.CanUndo && transformApplyButton.Visibility == Visibility.Visible, "Persistent edit applied on field loss.");
            Require(!OpacitySlider.IsEnabled && !BlendCombo.IsEnabled && LayerCommands.Children.OfType<Microsoft.UI.Xaml.Controls.Control>().All(c => !c.IsEnabled) && !LayerMenu.IsEnabled,
                "Layer controls were editable during a transform draft, contrary to canEditLayers.");
            transformSamplingCombo.SelectedIndex = (int)LayerSampling.Nearest;
            await WaitForTransformStateAsync(() => transformInspectorValue?.Sampling == LayerSampling.Nearest);
            ChangeTransformOption(v => v.FlipX = !v.FlipX); await WaitForFramesAsync();
            Require(transformInspectorValue!.FlipX && transformInspectorValue.Sampling == LayerSampling.Nearest, "Flip/sampling draft binding.");
            AssertTransformUpload(transformRenderedPreview!.Snapshot);
            QueueTransformCancel(); await WaitForFramesAsync(); Require(!scene.CanUndo && transformInspectorEdit is null, "Cancel changed history."); AssertUploadedFrame(scene);
            Require(OpacitySlider.IsEnabled && BlendCombo.IsEnabled && LayerCommands.Children.OfType<Microsoft.UI.Xaml.Controls.Control>().All(c => c.IsEnabled) && LayerMenu.IsEnabled, "Cancel did not restore layer controls.");
            report["persistentScaleRotationSamplingFlipCancel"] = "passed";

            scene = TransformScene(); OpenDocument(scene); await WaitForFramesAsync();
            transformRatioLock = true; ChangeTransformNumber(TransformField.Width, 80); await WaitForFramesAsync();
            // #23's prescription, at the site it missed: the draft arrives through the deferred XAML event path,
            // so one frame is not the guarantee. Reading it before the draft existed used to answer with a bare
            // NullReferenceException.
            await WaitForTransformStateAsync(() => transformInspectorValue is not null);
            Near(40, transformInspectorValue!.Size.Height, "Dimension lock"); Near(20.25, transformInspectorValue.Origin.X, "Dimension origin");
            FinishTransformDraft(); await WaitForFramesAsync(); AssertUploadedFrame(scene);
            Require(scene.Undo() && !scene.CanUndo, "Ratio edit undo"); await WaitForFramesAsync();
            report["ratioAndOrigin"] = "passed";

            scene = TransformScene(); OpenDocument(scene); await WaitForFramesAsync(); transformRatioLock = false;
            var rightHandle = CurrentTransformValue!.Point(TransformDrag.Handles[3]);
            BeginTransformPointer(rightHandle, VirtualKeyModifiers.None); await WaitForFramesAsync();
            report["resizeSnapToleranceDocumentPixels"] = 10 / viewport.PointsPerPixel;
            MoveTransformPointer(new(199, rightHandle.Y), VirtualKeyModifiers.None); await WaitForFramesAsync();
            Near(200, transformInspectorValue!.Origin.X + transformInspectorValue.Size.Width, "Resize snap canvas edge");
            Require(SnapLineX.Visibility == Visibility.Visible && SnapLineX.X1 == 200, "Resize snap guide missing.");
            AssertTransformUpload(transformRenderedPreview!.Snapshot); FinishPlacementDrag(); await WaitForFramesAsync(); AssertUploadedFrame(scene);
            Require(scene.Undo() && !scene.CanUndo, "Resize snap undo count"); await WaitForFramesAsync(); AssertUploadedFrame(scene);
            scene = TransformScene(); OpenDocument(scene); await WaitForFramesAsync();
            rightHandle = CurrentTransformValue!.Point(TransformDrag.Handles[3]);
            BeginTransformPointer(rightHandle, VirtualKeyModifiers.None); await WaitForFramesAsync();
            MoveTransformPointer(new(199, rightHandle.Y), VirtualKeyModifiers.Control); await WaitForFramesAsync();
            Near(199, transformInspectorValue!.Origin.X + transformInspectorValue.Size.Width, "Ctrl bypass resize snap");
            Require(SnapLineX.Visibility == Visibility.Collapsed, "Ctrl resize retained snap guide.");
            QueueTransformCancel(); await WaitForFramesAsync(); AssertUploadedFrame(scene);
            report["resizeSnapGpuReleaseUndoAndControlBypass"] = "passed";

            var overlap = TransformScene(true); overlap.Snapshot.Manifest.Layers[1].Transform.Origin = new(50, 35);
            OpenDocument(overlap); await WaitForFramesAsync(); transformAutoSelect = true;
            rightHandle = CurrentTransformValue!.Point(TransformDrag.Handles[3]);
            BeginTransformPointer(rightHandle, VirtualKeyModifiers.None); await WaitForFramesAsync();
            Require(transformInspectorEdit!.LayerIds.Single() == overlap.Snapshot.Manifest.Layers[0].Id && transformDrag?.Mode == TransformDragMode.Resize,
                "Auto Select picked a foreground layer before checking the selected layer's handle.");
            QueueTransformCancel(); await WaitForFramesAsync(); transformAutoSelect = false;
            overlap.Snapshot.Manifest.Layers[1].Transform.Origin = new(-25.5, 35);
            overlap.Snapshot.Manifest.Guides = [new() { Id = Guid.NewGuid(), Axis = GuideAxis.Vertical, Position = 147.25 }];
            RefreshTransformInspector(true); CacheSnapTargets();
            Require(snapXs!.Contains(-29) && snapXs.Contains(147.25), "Snap targets lost full rotated/off-canvas geometry or guides.");
            report["handleBeforeAutoSelectAndFullSnapTargets"] = "passed";

            scene = TransformScene(); scene.Snapshot.Manifest.Layers[0].Transform.Rotation = 7.5;
            OpenDocument(scene); await WaitForFramesAsync();
            var rotationHandle = TransformDrag.RotationHandle(CurrentTransformValue!);
            BeginTransformPointer(rotationHandle, VirtualKeyModifiers.None); await WaitForFramesAsync();
            MoveTransformPointer(rotationHandle, VirtualKeyModifiers.Shift); await WaitForFramesAsync();
            Near(15, transformInspectorValue!.Rotation, "Shift rotation midpoint"); AssertTransformUpload(transformRenderedPreview!.Snapshot);
            FinishPlacementDrag(); await WaitForFramesAsync(); AssertUploadedFrame(scene);
            Require(scene.Undo() && !scene.CanUndo, "Shift rotation undo count"); await WaitForFramesAsync(); AssertUploadedFrame(scene);
            pendingShift = false; report["shiftRotationMidpointGpuReleaseUndo"] = "passed";

            scene = TransformScene(); OpenDocument(scene); await WaitForFramesAsync();
            var duplicateSourceId = scene.Snapshot.Manifest.Layers[0].Id;
            var duplicateOriginal = ManifestJson.Serialize(scene.Snapshot.Manifest); var duplicateStart = CurrentTransformValue!.Center;
            BeginTransformPointer(duplicateStart, VirtualKeyModifiers.Menu); await WaitForFramesAsync();
            Require(scene.LayerCount == 1 && LayerCountLabel.Text == "1", "Alt press copied before dragging.");
            MoveTransformPointer(new(duplicateStart.X + 20, duplicateStart.Y + 11), VirtualKeyModifiers.Menu | VirtualKeyModifiers.Control);
            await WaitForFramesAsync();
            Require(scene.LayerCount == 1 && duplicateOriginal == ManifestJson.Serialize(scene.Snapshot.Manifest) && !scene.CanUndo, "Alt preview edited the live document/history.");
            Require(LayerCountLabel.Text == "2" && layerItems.Count == 2 && SelectedLayer?.Id != duplicateSourceId, "Alt drag did not show and select the provisional copy.");
            var duplicateCopyId = SelectedLayer!.Id;
            Require(((LayerRow)layerRows[duplicateCopyId].Tag).Thumbnail.Source is not null, "Provisional copy has no thumbnail.");
            AssertTransformUpload(transformRenderedPreview!.Snapshot);
            await SaveWorkspaceCaptureAsync("transform-duplicate-preview.png");
            FinishPlacementDrag(); await WaitForFramesAsync(); Require(scene.LayerCount == 2 && SelectedLayer?.Id == duplicateCopyId, "Alt release lost its copy selection.");
            AssertUploadedFrame(scene); OnUndo(this, new RoutedEventArgs()); await WaitForFramesAsync();
            Require(scene.LayerCount == 1 && !scene.CanUndo && SelectedLayer?.Id == duplicateSourceId, "Alt copy + move was not one undo with original selection.");
            AssertUploadedFrame(scene); OnRedo(this, new RoutedEventArgs()); await WaitForFramesAsync();
            Require(scene.LayerCount == 2 && SelectedLayer?.Id == duplicateCopyId && selectedLayerIds.SetEquals([duplicateCopyId]), "Redo did not select the copied active layer.");
            AssertUploadedFrame(scene); report["altDuplicateRowsThumbnailGpuSingleUndoRedoSelection"] = "passed";

            scene = TransformScene(); OpenDocument(scene); await WaitForFramesAsync();
            duplicateOriginal = ManifestJson.Serialize(scene.Snapshot.Manifest); duplicateStart = CurrentTransformValue!.Center;
            BeginTransformPointer(duplicateStart, VirtualKeyModifiers.Menu); await WaitForFramesAsync();
            pendingTransformPoint = duplicateStart; FinishPlacementDrag(); await WaitForFramesAsync();
            Require(scene.LayerCount == 1 && !scene.CanUndo && duplicateOriginal == ManifestJson.Serialize(scene.Snapshot.Manifest), "Alt click without a drag changed document/history.");
            report["altClickDoesNotCopyOrRoundPlacement"] = "passed";

            scene = TransformScene(); OpenDocument(scene); await WaitForFramesAsync();
            rightHandle = CurrentTransformValue!.Point(TransformDrag.Handles[3]);
            BeginTransformPointer(rightHandle, VirtualKeyModifiers.Menu); await WaitForFramesAsync();
            var resizePoint = new PointD(rightHandle.X + 15, rightHandle.Y);
            var expectedCenterResize = TransformDrag.Updated(transformDrag!, resizePoint, transformRatioLock, false, true);
            expectedCenterResize.Origin = new(Math.Round(expectedCenterResize.Origin.X, MidpointRounding.AwayFromZero), Math.Round(expectedCenterResize.Origin.Y, MidpointRounding.AwayFromZero));
            expectedCenterResize.Size = new(Math.Round(expectedCenterResize.Size.Width, MidpointRounding.AwayFromZero), Math.Round(expectedCenterResize.Size.Height, MidpointRounding.AwayFromZero));
            MoveTransformPointer(resizePoint, VirtualKeyModifiers.Menu | VirtualKeyModifiers.Control); await WaitForFramesAsync();
            Require(scene.LayerCount == 1 && LayerCountLabel.Text == "1", "Alt resize duplicated a layer.");
            Near(expectedCenterResize.Center.X, transformInspectorValue!.Center.X, "Rounded Alt resize center");
            AssertTransformUpload(transformRenderedPreview!.Snapshot); FinishPlacementDrag(); await WaitForFramesAsync(); AssertUploadedFrame(scene);
            Require(scene.Undo() && !scene.CanUndo, "Alt resize history"); await WaitForFramesAsync();
            report["altResizeUsesCenterWithoutCopy"] = "passed";

            scene = TransformScene(); OpenDocument(scene); await WaitForFramesAsync();
            OnFreeTransform(this, new RoutedEventArgs()); await WaitForFramesAsync(); ChangeTransformNumber(TransformField.X, 33); await WaitForFramesAsync();
            duplicateStart = CurrentTransformValue!.Center;
            BeginTransformPointer(duplicateStart, VirtualKeyModifiers.Menu); await WaitForFramesAsync();
            MoveTransformPointer(new(duplicateStart.X + 20, duplicateStart.Y + 11), VirtualKeyModifiers.Menu | VirtualKeyModifiers.Control); await WaitForFramesAsync();
            Require(scene.CanUndo && scene.LayerCount == 1 && !transformPersistent, "Alt did not commit the earlier persistent draft first.");
            Near(33, scene.Snapshot.Manifest.Layers[0].Transform.Origin.X, "Earlier draft commit");
            FinishPlacementDrag(); await WaitForFramesAsync(); AssertUploadedFrame(scene);
            OnUndo(this, new RoutedEventArgs()); await WaitForFramesAsync(); Require(scene.LayerCount == 1 && scene.CanUndo, "Copy undo consumed the earlier transform.");
            Near(33, scene.Snapshot.Manifest.Layers[0].Transform.Origin.X, "Copy undo kept prior draft");
            OnUndo(this, new RoutedEventArgs()); await WaitForFramesAsync(); Require(!scene.CanUndo, "Earlier draft undo count"); AssertUploadedFrame(scene);
            report["altAfterPersistentDraftUsesSeparatePriorUndo"] = "passed";

            var folderScene = TransformScene(true); var folderId = Guid.NewGuid();
            foreach (var layer in folderScene.Snapshot.Manifest.Layers) layer.ParentId = folderId;
            folderScene.Snapshot.Manifest.Layers.Insert(0, new() { Id = folderId, Name = "Folder", IsGroup = true, IsVisible = true,
                Transform = new() { Size = new(200, 160) } }); folderScene.Snapshot.Manifest.ActiveLayerId = folderId;
            OpenDocument(folderScene); collapsedGroups.Add(folderId); RefreshLayerPanel(); await WaitForFramesAsync();
            var folderOriginal = ManifestJson.Serialize(folderScene.Snapshot.Manifest); var folderStart = CurrentTransformValue!.Center;
            BeginTransformPointer(folderStart, VirtualKeyModifiers.Menu); await WaitForFramesAsync();
            MoveTransformPointer(new(folderStart.X + 20, folderStart.Y + 11), VirtualKeyModifiers.Menu | VirtualKeyModifiers.Control); await WaitForFramesAsync();
            Require(layerItems.Count == 2 && selectedLayerIds.Count == 1 && SelectedLayer?.Name == "Folder copy" && collapsedGroups.Contains(SelectedLayer!.Id), "Folder copy selection/collapse differs from source.");
            AssertTransformUpload(transformRenderedPreview!.Snapshot);
            var duplicatePause = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var duplicateRelease = new ManualResetEventSlim(false);
            beforeFrameRender = () => { duplicatePause.TrySetResult(); duplicateRelease.Wait(); };
            MoveTransformPointer(new(folderStart.X + 35, folderStart.Y + 15), VirtualKeyModifiers.Menu | VirtualKeyModifiers.Control);
            try { await duplicatePause.Task.WaitAsync(TimeSpan.FromSeconds(5)); QueueTransformCancel(); }
            finally { beforeFrameRender = null; duplicateRelease.Set(); }
            await WaitForFramesAsync();
            Require(folderOriginal == ManifestJson.Serialize(folderScene.Snapshot.Manifest) && !folderScene.CanUndo && layerItems.Count == 1
                && SelectedLayer?.Id == folderId && selectedLayerIds.SetEquals([folderId]) && collapsedGroups.SetEquals([folderId]), "Cancel did not restore folder rows/selection/collapse/history.");
            AssertUploadedFrame(folderScene); report["altFolderCopyCancelDuringWorkerGpuAndCollapse"] = "passed";

            selectedLayerIds.Add(folderScene.Snapshot.Manifest.Layers[1].Id); OnDuplicateLayers(this, new RoutedEventArgs()); await WaitForFramesAsync();
            Require(folderScene.LayerCount == 6 && selectedLayerIds.Count == 1 && SelectedLayer?.Name == "Folder copy" && collapsedGroups.Contains(SelectedLayer!.Id), "Duplicate menu did not select/collapse only the copied root.");
            OnUndo(this, new RoutedEventArgs()); await WaitForFramesAsync(); Require(folderScene.LayerCount == 3 && !folderScene.CanUndo, "Duplicate menu undo");
            AssertUploadedFrame(folderScene); report["duplicateMenuRootSelectionAndCollapse"] = "passed";

            scene = TransformScene(true); OpenDocument(scene); await WaitForFramesAsync(); selectedLayerIds.Clear();
            var expectedPrimaryName = SelectedLayer!.Name + " copy";
            foreach (var layer in scene.Snapshot.Manifest.Layers) selectedLayerIds.Add(layer.Id);
            OnLayerViaCopy(this, new RoutedEventArgs()); await WaitForFramesAsync();
            Require(scene.LayerCount == 4 && selectedLayerIds.Count == 2 && SelectedLayer?.Name == expectedPrimaryName, "Ctrl+J did not duplicate all selected roots.");
            Require(scene.Snapshot.Manifest.Layers.TakeLast(2).All(l => selectedLayerIds.Contains(l.Id)), "Ctrl+J did not stack copies above the highest original.");
            OnUndo(this, new RoutedEventArgs()); await WaitForFramesAsync(); Require(scene.LayerCount == 2 && !scene.CanUndo, "Ctrl+J multi-root history"); AssertUploadedFrame(scene);
            report["controlJDuplicatesSelectedRoots"] = "passed";

            var group = TransformScene(true); OpenDocument(group); selectedLayerIds.Clear();
            foreach (var layer in group.Snapshot.Manifest.Layers) selectedLayerIds.Add(layer.Id);
            RefreshTransformInspector(true); await WaitForFramesAsync();
            Require(transformInspectorBasis?.IsGroup == true, "Multiselection group box missing.");
            var groupOriginal = ManifestJson.Serialize(group.Snapshot.Manifest); var box = CurrentTransformValue!;
            BeginTransformPointer(box.Center, VirtualKeyModifiers.None); await WaitForFramesAsync();
            MoveTransformPointer(new(box.Center.X + 15, box.Center.Y + 9), VirtualKeyModifiers.Control); await WaitForFramesAsync();
            Require(groupOriginal == ManifestJson.Serialize(group.Snapshot.Manifest), "Pointer draft moved live group.");
            AssertTransformUpload(transformRenderedPreview!.Snapshot);
            FinishPlacementDrag(); await WaitForFramesAsync(); AssertUploadedFrame(group);
            Require(group.Undo() && !group.CanUndo, "Group release undo count"); await WaitForFramesAsync(); AssertUploadedFrame(group);
            report["multilayerPointerDraftReleaseGpuUndo"] = "passed";

            group = TransformScene(true); OpenDocument(group); await WaitForFramesAsync();
            LayerList.SelectedItems.Add(layerRows[group.Snapshot.Manifest.Layers[1].Id]);
            await WaitForTransformStateAsync(() => selectedLayerIds.Count == 2); await WaitForFramesAsync();
            Require(transformInspectorBasis?.IsGroup == true, "First Ctrl+click after open did not extend the active layer.");
            report["firstLayerListExtendedSelectionAfterOpen"] = "passed";

            var maskScene = TransformScene(); var masked = maskScene.Snapshot.Manifest.Layers[0];
            masked.MaskLinked = false; masked.MaskFile = ProjectValidator.Uuid(masked.Id) + ".mask.png";
            var mask = new MaskSurface(20, 10); mask.Pixels.AsSpan().Fill(255); mask.Pixels.AsSpan(0, 50).Clear();
            ((Dictionary<Guid, MaskSurface>)maskScene.Snapshot.Masks)[masked.Id] = mask;
            OpenDocument(maskScene); paintOnMask = false; RefreshTransformInspector(true); await WaitForFramesAsync();
            transformTargetCombo.SelectedIndex = 1; await WaitForTransformStateAsync(() => transformInspectorBasis?.TargetsMask == true); await WaitForFramesAsync();
            Require(transformInspectorBasis?.TargetsMask == true, "Mask target binding.");
            var pixelsTransform = masked.Transform;
            ChangeTransformNumber(TransformField.X, 6.75); await WaitForFramesAsync(); AssertTransformUpload(transformRenderedPreview!.Snapshot);
            FinishTransformDraft(); await WaitForFramesAsync(); Require(maskScene.Snapshot.Manifest.Layers[0].Transform.Equals(pixelsTransform), "Mask moved pixels.");
            Near(6.75, maskScene.Snapshot.Manifest.Layers[0].MaskPlacement!.Origin.X, "Mask placement commit"); AssertUploadedFrame(maskScene);
            Require(maskScene.Undo() && !maskScene.CanUndo, "Mask undo count"); await WaitForFramesAsync(); AssertUploadedFrame(maskScene);
            report["unlinkedMaskTargetGpuCommitUndo"] = "passed";

            scene = TransformScene(true); OpenDocument(scene); paintOnMask = false; selectedLayerIds.Clear();
            selectedLayerId = scene.Snapshot.Manifest.Layers[0].Id; RefreshLayerPanel(); await WaitForFramesAsync();
            transformAutoSelect = false;
            PickTransformLayer(new(90, 50), VirtualKeyModifiers.Control);
            Require(selectedLayerId == scene.Snapshot.Manifest.Layers[1].Id, "Ctrl inverted Auto Select.");
            PickTransformLayer(new(40, 40), VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift);
            Require(selectedLayerIds.Count == 2, "Ctrl+Shift did not extend selection.");
            showTransformControls = false; UpdateTransformHandles(); Require(TransformBox.Visibility == Visibility.Collapsed, "Hidden controls still displayed.");
            showTransformControls = true; UpdateTransformHandles(); Require(TransformBox.Visibility == Visibility.Visible, "Group controls did not return.");
            report["autoSelectModifiersAndShowControls"] = "passed";

            scene = TransformScene(true); var placedMaskLayer = scene.Snapshot.Manifest.Layers[1];
            placedMaskLayer.MaskFile = ProjectValidator.Uuid(placedMaskLayer.Id) + ".mask.png"; placedMaskLayer.MaskLinked = false;
            placedMaskLayer.MaskPlacement = new() { Origin = new(30, 25), Size = new(40, 30) };
            ((Dictionary<Guid, MaskSurface>)scene.Snapshot.Masks)[placedMaskLayer.Id] = new(20, 10);
            scene.Snapshot.Manifest.ActiveLayerId = placedMaskLayer.Id; OpenDocument(scene); paintOnMask = true;
            RefreshLayerPanel(); await WaitForFramesAsync(); transformAutoSelect = true;
            PickTransformLayer(placedMaskLayer.MaskPlacement.Center, VirtualKeyModifiers.None);
            Require(SelectedLayer?.Id == placedMaskLayer.Id && transformInspectorBasis?.TargetsMask == true, "Auto Select picked pixels below the independent mask's edited geometry.");
            PickTransformLayer(placedMaskLayer.MaskPlacement.Center, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift);
            Require(selectedLayerIds.Count == 2, "Explicit Ctrl+Shift pick did not extend behind the mask.");
            report["independentMaskGeometryKeepsFrontLayerUntilExplicitPick"] = "passed";

            scene = TransformScene(); OpenDocument(scene); await WaitForFramesAsync();
            var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var release = new ManualResetEventSlim(false);
            beforeFrameRender = () => { paused.TrySetResult(); release.Wait(); };
            ChangeTransformNumber(TransformField.X, 7);
            try { await paused.Task.WaitAsync(TimeSpan.FromSeconds(5)); QueueTransformCancel(); }
            finally { beforeFrameRender = null; release.Set(); }
            await WaitForFramesAsync(); Require(!scene.CanUndo && transformInspectorEdit is null, "Cancel during worker retained draft/history."); AssertUploadedFrame(scene);
            report["cancelDuringRender"] = "passed";

            scene = TransformScene(); OpenDocument(scene); await WaitForFramesAsync(); transformRatioLock = false;
            ChangeTransformNumber(TransformField.Width, 120); await WaitForFramesAsync();
            var queuedCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var commitRelease = new ManualResetEventSlim(false);
            beforeFrameRender = () => { queuedCommit.TrySetResult(); commitRelease.Wait(); };
            ChangeTransformNumber(TransformField.Y, 32);
            try
            {
                await queuedCommit.Task.WaitAsync(TimeSpan.FromSeconds(5));
                ChangeTransformNumber(TransformField.Width, 15); FinishTransformDraft();
            }
            finally { beforeFrameRender = null; commitRelease.Set(); }
            await WaitForFramesAsync(); Near(15, scene.Snapshot.Manifest.Layers[0].Transform.Size.Width, "Queued shrink commit"); AssertUploadedFrame(scene);
            Require(scene.Undo() && !scene.CanUndo, "Queued commit undo count"); await WaitForFramesAsync(); AssertUploadedFrame(scene);
            report["shrinkAndCommitDuringWorkerClearsPriorDraft"] = "passed";

            AppWindow.Resize(new SizeInt32(760, 620)); RootGrid.UpdateLayout(); await Task.Delay(100);
            Require(ToolOptionsScroll.ScrollableWidth > 0 && TransformControls.Visibility == Visibility.Visible, "Narrow window options are inaccessible.");
            ToolOptionsScroll.ChangeView(ToolOptionsScroll.ScrollableWidth, null, null, true); RootGrid.UpdateLayout();
            await SaveWorkspaceCaptureAsync("transform-workspace-narrow.png");
            AppWindow.Resize(new SizeInt32(1400, 850)); ToolOptionsScroll.ChangeView(0, null, null, true); RootGrid.UpdateLayout(); await Task.Delay(100);
            await SaveWorkspaceCaptureAsync("transform-workspace.png"); report["narrowScroll"] = "passed";
            scene = TransformScene(); OpenDocument(scene); var thumbnailId = scene.Snapshot.Manifest.Layers[0].Id;
            scene.AddRasterMask(thumbnailId, new(3, 3, [255, 255, 255, 255, 73, 255, 255, 255, 255])); await WaitForFramesAsync();
            var thumbnailRow = (LayerRow)layerRows[thumbnailId].Tag;
            report["thumbnailParentObservation"] = new { imageParent = thumbnailRow.MaskThumbnail.Parent?.GetType().Name,
                contentParent = (thumbnailRow.MaskTarget.Content as FrameworkElement)?.Parent?.GetType().Name };
            Require(thumbnailRow.MaskTarget.Content is Microsoft.UI.Xaml.Controls.Grid maskContent
                && maskContent.Children.Contains(thumbnailRow.MaskThumbnail) && thumbnailRow.MaskTarget.IsHitTestVisible && thumbnailRow.MaskTarget.IsEnabled,
                "Mask thumbnail has no interactive target button.");
            report["thumbnailTargetControl"] = "passed";
            await VerifyThumbnailInteractionsAsync(report);
            Require(renderInputFailureCount == inputFailures && renderPublicationFailureCount == publicationFailures,
                "Transform probe recovered from an input/publication failure instead of completing without errors.");
            report["renderInputAndPublicationNoFailures"] = "passed";
            await VerifyInteractionCompletionsAsync(report);
            await VerifyDitherOwnershipAsync(report);
            // Check the loaded XAML value against its localized resource, not just a nonempty literal.
            report["viewMenuPixelGridLabelObserved"] = PixelGridToggle.Text;
            Require(PixelGridToggle.Text == Loc.T("V_ShowGrid/Text"), "The pixel-grid label ignored its localized resource.");
            report["viewMenuPixelGridLabelUsesItsResource"] = "passed";
            // Floating transform ends in keyboard-focus assertions that need the window to own the
            // foreground; running it last measures everything before it even when nobody is at the
            // keyboard. Its fixtures are self-contained (each opens its own document), so moving it
            // changes nothing for the groups around it.
            await VerifyFloatingTransformAsync(report);
            report["status"] = "passed";
        }
        catch (Exception ex) { report["status"] = "failed"; report["error"] = ex.ToString(); App.Log($"Transform probe failed: {ex}"); }
        finally
        {
            beforeFrameRender = null;
            WriteProbeReport("transform-probe.json", report);
            forceClose = true; Close();
        }
    }

    private void AssertTransformUpload(ProjectSnapshot snapshot)
    {
        var expected = new CanvasCompositor(snapshot).Render(); var bytes = new byte[expected.Width * expected.Height * 4];
        SurfacePresentation.FillBgra(expected, bytes, expected.Width, 0, 0, expected.Width, expected.Height);
        Require(presented is not null && bytes.AsSpan().SequenceEqual(presented.GetPixelBytes()), "Presented transform bytes differ from full composition.");
    }

    private async Task VerifyThumbnailInteractionsAsync(Dictionary<string, object> report)
    {
        var scene = TransformScene(true); var first = scene.Snapshot.Manifest.Layers[0]; var second = scene.Snapshot.Manifest.Layers[1];
        first.MaskFile = ProjectValidator.Uuid(first.Id) + ".mask.png"; first.MaskLinked = false;
        first.MaskPlacement = new() { Origin = new(30, 20), Size = new(3, 3), Sampling = LayerSampling.Nearest };
        second.MaskFile = ProjectValidator.Uuid(second.Id) + ".mask.png"; second.MaskEnabled = false;
        second.MaskPlacement = new() { Origin = new(100, 80), Size = new(3, 3), Sampling = LayerSampling.Nearest };
        var mask = new MaskSurface(3, 3, [255, 255, 255, 255, 0, 255, 255, 255, 255]);
        ((Dictionary<Guid, MaskSurface>)scene.Snapshot.Masks)[first.Id] = mask;
        ((Dictionary<Guid, MaskSurface>)scene.Snapshot.Masks)[second.Id] = mask.Clone();
        OpenDocument(scene); await WaitForFramesAsync();
        var row = (LayerRow)layerRows[first.Id].Tag;
        InvokeButton(row.MaskTarget); await WaitForFramesAsync();
        Require(paintOnMask && transformInspectorBasis?.TargetsMask == true && row.MaskTarget.BorderThickness.Left == 2 && !scene.CanUndo,
            "Thumbnail did not select the mask target without history.");
        InvokeButton(row.PixelTarget); await WaitForFramesAsync();
        Require(!paintOnMask && row.PixelTarget.BorderThickness.Left == 2 && !scene.CanUndo, "Pixel thumbnail did not reset the target.");
        HandleThumbnailClick(first.Id, true, VirtualKeyModifiers.Shift); await WaitForFramesAsync();
        Require(first.MaskEnabled == false && paintOnMask && row.MaskDisabledMark.Visibility == Visibility.Visible, "Shift-click did not disable/target mask.");
        OnUndo(this, new()); await WaitForFramesAsync(); Require(!scene.CanUndo && scene.Snapshot.Manifest.Layers[0].MaskEnabled != false, "Mask toggle Undo count.");
        row = (LayerRow)layerRows[first.Id].Tag;
        InvokeButton(row.MaskLink); await WaitForFramesAsync(); Require(scene.Snapshot.Manifest.Layers[0].MaskLinked != false, "Link button did not link mask.");
        OnUndo(this, new()); await WaitForFramesAsync(); Require(!scene.CanUndo && scene.Snapshot.Manifest.Layers[0].MaskLinked == false, "Link button Undo count.");
        report["thumbnailTargetShiftToggleLinkAndUndo"] = "passed";

        HandleThumbnailClick(first.Id, false, VirtualKeyModifiers.None); await WaitForFramesAsync();
        OnFreeTransform(this, new()); ChangeTransformNumber(TransformField.X, 27.125); await WaitForFramesAsync();
        var keptDraft = transformInspectorEdit;
        HandleThumbnailClick(first.Id, true, VirtualKeyModifiers.None); await WaitForFramesAsync();
        Require(ReferenceEquals(keptDraft, transformInspectorEdit) && paintOnMask && !scene.CanUndo,
            "Same-layer thumbnail committed a persistent draft contrary to selectLayer.");
        HandleThumbnailClick(first.Id, true, VirtualKeyModifiers.Shift); await WaitForFramesAsync();
        Require(ReferenceEquals(keptDraft, transformInspectorEdit) && scene.Snapshot.Manifest.Layers[0].MaskEnabled != false,
            "Same-layer Shift thumbnail changed mask enable while transform editing was unavailable.");
        QueueTransformCancel(); await WaitForFramesAsync(); Require(!scene.CanUndo, "Same-layer target/cancel added history.");
        report["sameLayerThumbnailPreservesPersistentDraftAndMaskEnable"] = "passed";

        HandleThumbnailClick(first.Id, false, VirtualKeyModifiers.None); await WaitForFramesAsync();
        long frames = presentedFrameCount;
        HandleThumbnailClick(second.Id, true, VirtualKeyModifiers.Control);
        HandleThumbnailClick(first.Id, true, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift);
        await WaitForSelectionAsync();
        Require(scene.ActiveSelection!.Coverage.Pixels[21 * 200 + 31] == 255 && scene.ActiveSelection.Coverage.Pixels[81 * 200 + 101] == 255, "Queued thumbnail Add lost preceding coverage.");
        OnUndo(this, new()); await WaitForFramesAsync();
        Require(scene.ActiveSelection?.Bounds == new RectI(101, 81, 1, 1), "Queued thumbnail requests collapsed separate selection Undo steps.");
        OnUndo(this, new()); await WaitForFramesAsync(); Require(scene.ActiveSelection is null && !scene.CanUndo, "Queued thumbnail Replace Undo count.");
        Require(presentedFrameCount == frames, "Queued selection history composited the document.");
        report["queuedThumbnailSelectionPreservesPerClickUndo"] = "passed";
        var queuedMask = scene.Snapshot.Masks[second.Id];
        var emptyQueuedMask = new MaskSurface(3, 3); Array.Fill(emptyQueuedMask.Pixels, (byte)255);
        ((Dictionary<Guid, MaskSurface>)scene.Snapshot.Masks)[second.Id] = emptyQueuedMask;
        HandleThumbnailClick(first.Id, true, VirtualKeyModifiers.Control);
        HandleThumbnailClick(second.Id, true, VirtualKeyModifiers.Control);
        await WaitForSelectionAsync();
        Require(scene.ActiveSelection?.Bounds == new RectI(31, 21, 1, 1), "Queued empty source reverted the preceding thumbnail selection.");
        OnUndo(this, new()); await WaitForFramesAsync();
        Require(scene.ActiveSelection is null && !scene.CanUndo && presentedFrameCount == frames, "Queued empty source added history or composited the document.");
        ((Dictionary<Guid, MaskSurface>)scene.Snapshot.Masks)[second.Id] = queuedMask;
        report["queuedEmptyThumbnailPreservesPrecedingSelectionAndHistory"] = "passed";
        HandleThumbnailClick(second.Id, true, VirtualKeyModifiers.Control); await WaitForSelectionAsync(); await WaitForFramesAsync();
        Require(scene.ActiveSelection?.Bounds == new RectI(101, 81, 1, 1) && SelectedLayer?.Id == first.Id && !paintOnMask && scene.CanUndo,
            "Ctrl-mask did not load disabled mask black pixels as an undoable selection without changing layer/target.");
        Require(presentedFrameCount == frames, "Thumbnail selection composited the document.");
        OnUndo(this, new()); await WaitForFramesAsync(); Require(scene.ActiveSelection is null && !scene.CanUndo && presentedFrameCount == frames, "Selection Undo did not preserve the compositor.");
        OnRedo(this, new()); await WaitForFramesAsync(); Require(scene.ActiveSelection?.Bounds == new RectI(101, 81, 1, 1) && presentedFrameCount == frames, "Selection Redo recomposited the document.");
        HandleThumbnailClick(first.Id, true, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift); await WaitForSelectionAsync();
        Require(scene.ActiveSelection!.Coverage.Pixels[21 * 200 + 31] == 255 && scene.ActiveSelection.Coverage.Pixels[81 * 200 + 101] == 255, "Ctrl-Shift did not add mask selection.");
        HandleThumbnailClick(first.Id, true, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift | VirtualKeyModifiers.Menu); await WaitForSelectionAsync();
        Require(scene.ActiveSelection!.Coverage.Pixels[21 * 200 + 31] == 0 && scene.ActiveSelection.Coverage.Pixels[81 * 200 + 101] == 255, "Ctrl-Alt subtraction did not take precedence over Shift.");
        HandleThumbnailClick(first.Id, false, VirtualKeyModifiers.Control); await WaitForSelectionAsync();
        Require(scene.ActiveSelection!.Coverage.Pixels[40 * 200 + 40] == 255 && scene.ActiveSelection.Coverage.Pixels[81 * 200 + 101] == 0,
            "Ctrl-image did not load alpha ignoring its independently placed mask.");
        var retained = scene.ActiveSelection; var white = new MaskSurface(3, 3); Array.Fill(white.Pixels, (byte)255);
        ((Dictionary<Guid, MaskSurface>)scene.Snapshot.Masks)[second.Id] = white;
        HandleThumbnailClick(second.Id, true, VirtualKeyModifiers.Control); await WaitForSelectionAsync();
        Require(ReferenceEquals(retained, scene.ActiveSelection), "Empty mask outline discarded the current selection.");
        OnUndo(this, new()); await WaitForFramesAsync(); Require(scene.ActiveSelection?.Bounds == new RectI(101, 81, 1, 1), "Empty source outline inserted history instead of preserving prior image-selection Undo.");
        scene = new EditorDocument(scene.Snapshot); OpenDocument(scene); await WaitForFramesAsync(); row = (LayerRow)layerRows[first.Id].Tag;
        report["thumbnailCtrlThresholdAddSubtractNoTargetChangeOrComposite"] = "passed";

        BeginMaskCopyGesture(first.Id, new(0, 0)); UpdateMaskCopyGesture(new(2, 0)); FinishMaskCopyGesture(new(2, 0)); await WaitForFramesAsync();
        Require(CurrentMaskAloneId == first.Id && paintOnMask && !scene.CanUndo, "Alt click under threshold did not show mask alone.");
        AssertMaskUpload(scene, first.Id); Require(row.MaskTarget.BorderBrush == layerTargetSolo, "Solo mask outline color.");
        ChangeTransformNumber(TransformField.X, 34); await WaitForFramesAsync(); AssertMaskUpload(transformRenderedPreview!.Snapshot, first.Id);
        QueueTransformCancel(); await WaitForFramesAsync(); Require(!scene.CanUndo, "Solo mask transform cancel changed history."); AssertMaskUpload(scene, first.Id);
        ToolCombo.SelectedIndex = (int)CanvasTool.Eraser; BrushSizeSlider.Value = 12;
        BeginPointerStroke(new(31.5, 21.5)); await WaitForFramesAsync(); AssertMaskUpload(scene, first.Id);
        FinishPointerStroke(); await WaitForFramesAsync(); AssertMaskUpload(scene, first.Id);
        // The stroke grid grows to the mask's full reach and is published with a new placement,
        // so check the dab through that placement instead of a fixed stride: document (30.5, 20.5)
        // is the mask's original white (0,0), which a 12-pixel eraser dab at (31.5, 21.5) must darken.
        var soloMask = scene.Snapshot.Masks[first.Id];
        var soloPlacement = scene.Snapshot.Manifest.Layers[0].MaskPlacement!;
        int soloX = (int)Math.Floor((30.5 - soloPlacement.Origin.X) / soloPlacement.Size.Width * soloMask.Width);
        int soloY = (int)Math.Floor((20.5 - soloPlacement.Origin.Y) / soloPlacement.Size.Height * soloMask.Height);
        Require(soloMask.Pixels[soloY * soloMask.Width + soloX] < 255, "Solo mask stroke did not change coverage.");
        OnUndo(this, new()); await WaitForFramesAsync(); Require(!scene.CanUndo, "Solo mask stroke Undo count."); AssertMaskUpload(scene, first.Id);
        OnRedo(this, new()); await WaitForFramesAsync(); AssertMaskUpload(scene, first.Id);
        OnUndo(this, new()); await WaitForFramesAsync(); ToolCombo.SelectedIndex = (int)CanvasTool.Move;
        report["soloMaskPaintBackgroundChangeGpuUndoRedo"] = "passed";
        HandleThumbnailClick(first.Id, true, VirtualKeyModifiers.Menu); await WaitForFramesAsync();
        Require(CurrentMaskAloneId is null && paintOnMask, "Alt click did not return to composite with mask target."); AssertUploadedFrame(scene);
        HandleThumbnailClick(first.Id, true, VirtualKeyModifiers.Menu); await WaitForFramesAsync(); AssertMaskUpload(scene, first.Id);
        InvokeButton(((LayerRow)layerRows[second.Id].Tag).PixelTarget); await WaitForFramesAsync();
        Require(CurrentMaskAloneId is null && !paintOnMask && SelectedLayer?.Id == second.Id, "Other thumbnail did not exit mask alone/reset target."); AssertUploadedFrame(scene);
        report["thumbnailAltSoloToggleAndOtherTargetGpu"] = "passed";

        HandleThumbnailClick(first.Id, false, VirtualKeyModifiers.None); await WaitForFramesAsync();
        ChangeTransformNumber(TransformField.X, 24.125); await WaitForFramesAsync();
        HandleThumbnailClick(second.Id, true, VirtualKeyModifiers.None); await WaitForFramesAsync();
        Near(24.125, scene.Snapshot.Manifest.Layers[0].Transform.Origin.X, "Thumbnail committed prior numeric draft");
        Require(transformInspectorEdit is null && paintOnMask && SelectedLayer?.Id == second.Id, "Thumbnail did not select mask after draft commit.");
        OnUndo(this, new()); await WaitForFramesAsync(); Require(!scene.CanUndo, "Thumbnail selection added an Undo step after draft commit.");
        report["thumbnailCommitsPriorDraftThenSelectsTarget"] = "passed";

        RootGrid.UpdateLayout(); var dropGrid = layerRows[second.Id];
        var drop = dropGrid.TransformToVisual(RootGrid).TransformPoint(new(dropGrid.ActualWidth / 2, dropGrid.ActualHeight / 2));
        Require(ThumbnailDropTarget(drop) == second.Id, "Mask drop row geometry.");
        BeginMaskCopyGesture(first.Id, new(0, 0)); UpdateMaskCopyGesture(drop); FinishMaskCopyGesture(drop); await WaitForFramesAsync();
        Require(SelectedLayer?.Id == second.Id && paintOnMask && CurrentMaskAloneId is null && ReferenceEquals(scene.Snapshot.Masks[first.Id], scene.Snapshot.Masks[second.Id]),
            "Alt mask drag did not share source mask/select target without toggling solo."); AssertUploadedFrame(scene);
        OnUndo(this, new()); await WaitForFramesAsync(); Require(!scene.CanUndo && !ReferenceEquals(scene.Snapshot.Masks[first.Id], scene.Snapshot.Masks[second.Id]), "Mask copy Undo count.");
        OnRedo(this, new()); await WaitForFramesAsync(); AssertUploadedFrame(scene);
        report["thumbnailAltDragMaskCopyDropSingleUndoRedo"] = "passed";
        await SaveWorkspaceCaptureAsync("thumbnail-targets-workspace.png");
        HandleThumbnailClick(first.Id, true, VirtualKeyModifiers.Control);
        HandleThumbnailClick(second.Id, true, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift);
        var obsoleteSelection = selectionWorkResult;
        var replacement = TransformScene(); OpenDocument(replacement);
        if (obsoleteSelection is not null) await obsoleteSelection;
        await WaitForFramesAsync();
        Require(replacement.ActiveSelection is null && !replacement.CanUndo, "Obsolete thumbnail work published into a new document.");
        AssertUploadedFrame(replacement); report["queuedThumbnailSelectionRejectsReplacedDocument"] = "passed";
        await VerifySoloMaskPerformanceAsync(report);
        await VerifyRefusedToolSwitchKeepsPickerAsync(report);

        async Task WaitForSelectionAsync()
        {
            var timer = Stopwatch.StartNew();
            while (selectionWorkResult is not null || (scene.ActiveSelection is not null && !OutlineIsReady))
            {
                if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Thumbnail selection did not finish.");
                await Task.Delay(1);
            }
        }
        static void InvokeButton(Microsoft.UI.Xaml.Controls.Button button)
        {
            var peer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(button);
            ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
        }
    }

    private void AssertMaskUpload(EditorDocument scene, Guid id)
        => AssertMaskUpload(scene.Snapshot, id);

    private void AssertMaskUpload(ProjectSnapshot snapshot, Guid id)
    {
        var layer = snapshot.Manifest.Layers.Single(l => l.Id == id); var expected = new Surface(snapshot.Manifest.Width, snapshot.Manifest.Height);
        MaskDisplay.Draw(snapshot.Masks[id], layer.MaskPlacement ?? layer.Transform, expected, new(0, 0, expected.Width, expected.Height));
        var bytes = new byte[expected.Pixels.Length]; SurfacePresentation.FillBgra(expected, bytes, expected.Width, 0, 0, expected.Width, expected.Height);
        Require(presented is not null && bytes.AsSpan().SequenceEqual(presented.GetPixelBytes()), "Solo mask GPU bytes differ from raw mask display.");
    }

    private async Task VerifySoloMaskPerformanceAsync(Dictionary<string, object> report)
    {
        var scene = Scene(4096, 0); var layer = scene.Snapshot.Manifest.Layers[0];
        var mask = new MaskSurface(4096, 4096); Array.Fill(mask.Pixels, (byte)255);
        layer.MaskFile = ProjectValidator.Uuid(layer.Id) + ".mask.png";
        ((Dictionary<Guid, MaskSurface>)scene.Snapshot.Masks)[layer.Id] = mask;
        OpenDocument(scene); await WaitForFramesAsync(); HandleThumbnailClick(layer.Id, true, VirtualKeyModifiers.Menu); await WaitForFramesAsync();
        var fullDisplay = lastFrameProbe;
        ToolCombo.SelectedIndex = (int)CanvasTool.Eraser; BrushSizeSlider.Value = 12; BrushOpacitySlider.Value = 80; BrushHardnessSlider.Value = 50;
        BeginPointerStroke(new(300, 300)); await WaitForFramesAsync(); var firstDab = lastFrameProbe;
        var samples = new List<FrameProbe>();
        for (int i = 0; i < 9; i++)
        {
            int before = presentedFrameCount; AppendPointerStroke(new(306 + i * 6, 300)); await WaitForFramesAsync();
            Require(presentedFrameCount == before + 1, "Solo mask sample reused an old frame.");
            if (i >= 2) samples.Add(lastFrameProbe);
        }
        FinishPointerStroke(); await WaitForFramesAsync(); AssertMaskUpload(scene, layer.Id);
        OnUndo(this, new()); await WaitForFramesAsync(); Require(!scene.CanUndo, "4K solo mask Undo count."); AssertMaskUpload(scene, layer.Id);
        report["soloMask4KFrames"] = new { fullDisplay, firstDab, samples = samples.Count,
            steadyCompleteTickMedianMs = Median(samples.Select(s => s.InputMs + s.CpuMs + s.UploadMs)),
            steadyCompleteTickMaxMs = samples.Max(s => s.InputMs + s.CpuMs + s.UploadMs),
            inputAndWorkerAllocatedKiB = Median(samples.Select(s => (s.InputBytes + s.WorkerBytes) / 1024d)), band = samples[^1].Band };
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Near(double expected, double actual, string message)
    { if (Math.Abs(expected - actual) > 1e-8) throw new InvalidOperationException($"{message}: expected={expected}, actual={actual}."); }
    /// <summary>
    /// Waits for a state that the deferred XAML event path sets — a NumberBox value, a floating display, a
    /// selection outline. It polls every 20 ms rather than every 1 ms because a 1 ms poll of this shape timed
    /// out at its own five-second budget inside the full suite, at a site where a 50 ms poll let the state
    /// arrive: a tight loop keeps handing continuations to the dispatcher, and the work that sets the state is
    /// queued behind them. The budget and every assertion the callers make after it are unchanged; only the
    /// waiting got coarser, and the failure now names the gates that were still shut and how long it waited.
    /// </summary>
    /// <summary>
    /// The picker and the tool must never disagree. The Mac shows the tool straight from the one state
    /// (`session.tool`, e.g. BrushControls.swift:7), so a switch that gets retired cannot leave the control
    /// reading one tool while the canvas is in another; on Windows the picker is a separate integer, and the
    /// way-out branch at MainWindow.xaml.cs:358 used to put the field back without putting the picker back.
    /// #57's natural reds were read exactly like that (`tool=Text combo=0 controls=Collapsed`).
    /// This is a standing check, not a forced branch: the refusal needs a document swap inside the switch's own
    /// await, which no probe can pin without racing the dispatcher, so the group switches tools twice and asks
    /// whether the two ever part company. What makes it a gate rather than a decoration is the injection round
    /// recorded in the doc: a `tool = …` written without the picker reds it.
    /// </summary>
    private async Task VerifyRefusedToolSwitchKeepsPickerAsync(Dictionary<string, object> report)
    {
        var first = TransformScene(); OpenDocument(first); await WaitForFramesAsync(); RootGrid.UpdateLayout();
        ToolCombo.SelectedIndex = (int)CanvasTool.Text;
        Require(await WaitUntilAsync(() => tool == CanvasTool.Text, 6000),
            $"the type tool never became the live tool: tool={tool} combo={ToolCombo.SelectedIndex}");
        await PickerMatchesTool("after the type switch");
        int appliedBefore = toolChangeApplied;
        var replacement = TransformScene();
        ToolCombo.SelectedIndex = (int)CanvasTool.Move;
        OpenDocument(replacement);
        Require(await WaitUntilAsync(() => toolChangeApplied != appliedBefore, 6000),
            $"the second switch never reported back: tool={tool} combo={ToolCombo.SelectedIndex} "
            + $"applied={toolChangeApplied}");
        await PickerMatchesTool("after the switch that outlived its own document");
        Require(tool == CanvasTool.Move,
            $"the switch was retired but this run cannot tell which branch it took: {Scene()}");
        report["toolPickerFollowsRefusedSwitch"] = "passed";

        async Task PickerMatchesTool(string where)
        {
            await WaitForFramesAsync();
            Require(ToolCombo.SelectedIndex == (int)tool, $"{where}: {Scene()}.");
            Require(TransformControls.Visibility == (tool == CanvasTool.Move ? Visibility.Visible : Visibility.Collapsed)
                && TextControls.Visibility == (tool == CanvasTool.Text ? Visibility.Visible : Visibility.Collapsed),
                $"{where}: the tool strips do not follow the tool: {Scene()}.");
        }
    }

    private string Scene() => $"tool={tool} combo={ToolCombo.SelectedIndex} selected={ToolCombo.SelectedItem} "
        + $"controls={TransformControls.Visibility} text={TextControls.Visibility}";

    private async Task WaitForTransformStateAsync(Func<bool> ready, [CallerMemberName] string site = "")
    {
        var timer = Stopwatch.StartNew();
        while (!ready())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(5))
                throw new TimeoutException($"Deferred XAML transform event did not arrive in {site} after "
                    + $"{timer.ElapsedMilliseconds} ms: editable={CanEditLayerCommands} core={CanEditSelectionCore} "
                    + $"pending={renderInputs.HasPending} stroke={pointerStroke is not null} busy={StatusProgress.Visibility != Visibility.Visible} "
                    + $"edit={transformInspectorEdit is not null} floating={floatingTransform is not null} "
                    + $"duplicate={floatingDuplicate is not null} value={transformInspectorValue is not null}");
            await Task.Delay(20);
        }
    }
    private static EditorDocument TransformScene(bool two = false)
    {
        var scene = EditorDocument.Create(200, 140); var first = scene.Snapshot.Manifest.Layers[0];
        first.Transform = new() { Origin = new(20.25, 30.5), Size = new(40, 20), Rotation = 0 };
        var image = new Surface(20, 10); image.Pixels.AsSpan().Fill(255);
        for (int i = 0; i < image.Pixels.Length; i += 4) image.Pixels[i + 1] = 20;
        ((Dictionary<Guid, Surface>)scene.Snapshot.Images)[first.Id] = image;
        if (two)
        {
            var id = Guid.NewGuid(); scene.Snapshot.Manifest.Layers.Add(new() { Id = id, Name = "Second", IsVisible = true,
                ImageFile = ProjectValidator.Uuid(id) + ".png", Transform = new() { Origin = new(80, 40), Size = new(25, 35), Rotation = 12 } });
            var next = new Surface(25, 35); next.Pixels.AsSpan().Fill(200); ((Dictionary<Guid, Surface>)scene.Snapshot.Images)[id] = next;
        }
        return scene;
    }
}
#endif
