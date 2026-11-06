#if DEBUG
using Compositor.Core;
using Compositor.Renderer;
using Windows.System;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task VerifyPixelMoveAsync(Dictionary<string, object> report)
    {
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        var scene = EditorDocument.Create(64, 48); OpenDocument(scene); await WaitForFramesAsync();
        ToolCombo.SelectedIndex = (int)CanvasTool.RectSelect;
        var original = Selection.Rectangle(64, 48, 4, 4, 16, 12); scene.SetSelection(original); await WaitForSelectionReadyAsync();
        var source = scene.Snapshot; var pixels = source.Images[source.Manifest.ActiveLayerId!.Value]; var warm = compositor;
        Require(TryBeginPixelMove(new(10, 10), VirtualKeyModifiers.Control), "Ctrl inside the selection did not begin pixel movement.");
        MovePixelsTo(new(30, 10), VirtualKeyModifiers.Control); await WaitForFramesAsync();
        Require(ReferenceEquals(scene.Snapshot, source) && ReferenceEquals(scene.ActiveSelection, original) && !scene.CanUndo, "Pixel preview mutated the document.");
        Require(ReferenceEquals(warm, compositor), "Pixel preview replaced the warm compositor.");
        await FinishPixelMoveAsync(); await WaitForSelectionReadyAsync(); await WaitForFramesAsync();
        var expectedFrame = new CanvasCompositor(scene.Snapshot).Render();
        var expectedBytes = new byte[64 * 48 * 4]; SurfacePresentation.FillBgra(expectedFrame, expectedBytes, 64, 0, 0, 64, 48);
        var actualBytes = presented!.GetPixelBytes(); int firstDifference = -1;
        for (int i = 0; i < expectedBytes.Length; i++) if (expectedBytes[i] != actualBytes[i]) { firstDifference = i; break; }
        report["selectedPixelCommitFrameDiagnostic"] = new { firstDifference, expected = firstDifference < 0 ? -1 : expectedBytes[firstDifference],
            actual = firstDifference < 0 ? -1 : actualBytes[firstDifference], band = lastFrameProbe.Band, offset = scene.ActiveSelection?.OutlineOffset };
        AssertUploadedFrame(scene);
        Require(scene.CanUndo && scene.ActiveSelection?.OutlineOffset == new PointD(20, 0), "Pixel movement did not commit its selection.");
        OnUndo(this, new()); await WaitForSelectionReadyAsync(); await WaitForFramesAsync(); AssertUploadedFrame(scene);
        Require(ReferenceEquals(pixels, scene.Snapshot.Images[source.Manifest.ActiveLayerId!.Value]) && ReferenceEquals(original, scene.ActiveSelection) && !scene.CanUndo,
            "Pixel movement did not undo in one step.");
        report["selectedPixelMovePreviewWarmCacheAndSingleUndo"] = "passed";

        Require(scene.CanRedo && TryBeginPixelMove(new(10, 10), VirtualKeyModifiers.Control), "No-op move did not start.");
        MovePixelsTo(new(30, 10), VirtualKeyModifiers.Control); MovePixelsTo(new(10, 10), VirtualKeyModifiers.Control);
        await FinishPixelMoveAsync(); await WaitForFramesAsync(); AssertUploadedFrame(scene);
        Require(scene.CanRedo && !scene.CanUndo && ReferenceEquals(scene.ActiveSelection, original), "Return to origin invalidated Redo.");
        Require(!TryBeginPixelMove(new(1, 1), VirtualKeyModifiers.Control) && !TryBeginPixelMove(new(10, 10), VirtualKeyModifiers.None), "Pixel move ignored inside/Control guards.");
        report["selectedPixelMoveNoOpRedoAndEntryGuards"] = "passed";

        Require(TryBeginPixelMove(new(10, 10), VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu), "Ctrl+Alt duplicate did not start.");
        MovePixelsTo(new(23, 23), VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift);
        Require(pixelMove?.Offset == new PointD(13, 0) && DisplayedSelection?.OutlineOffset == new PointD(13, 0), "Shift tie did not lock horizontally.");
        var finishing = FinishPixelMoveAsync(); CancelPixelMove(focusLoss: true); await finishing; await WaitForFramesAsync(); AssertUploadedFrame(scene);
        Require(scene.LayerCount == 1 && scene.ActiveSelection?.OutlineOffset == new PointD(13, 0), "Duplicate created a layer or capture loss cancelled released pixels.");
        OnUndo(this, new()); await WaitForSelectionReadyAsync(); await WaitForFramesAsync(); Require(!scene.CanUndo && ReferenceEquals(original, scene.ActiveSelection), "Duplicate Undo was not one step.");
        report["selectedPixelDuplicateShiftAndReleasedCaptureGuard"] = "passed";

        Require(TryBeginPixelMove(new(10, 10), VirtualKeyModifiers.Control), "Cancel move did not start.");
        MovePixelsTo(new(30, 10), VirtualKeyModifiers.Control); OnHealingPointerCanceled(this, null!);
        await WaitForFramesAsync(); AssertUploadedFrame(scene); Require(pixelMove is null && !scene.CanUndo && scene.CanRedo, "Capture cancellation entered history.");
        ToolCombo.SelectedIndex = (int)CanvasTool.Wand; CanvasStage.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
        Require(TryBeginPixelMove(new(10, 10), VirtualKeyModifiers.Control), "Wand pixel move did not start.");
        MovePixelsTo(new(30, 10), VirtualKeyModifiers.Control); RootGrid.UpdateLayout();
        var text = FindText(WandToleranceBox); Require(text?.Focus(Microsoft.UI.Xaml.FocusState.Programmatic) == true, "Wand tolerance field could not take focus.");
        // The focus change is what ends the move, but WinUI delivers it on the dispatcher, so wait for the
        // state rather than for a stopwatch: the same shape as D12c, which went red 2 of 5 runs as soon as
        // another group queued work ahead of its fixed 30 ms.
        Require(await WaitUntilAsync(() => pixelMove is null && !scene.CanUndo
            && ReferenceEquals(original, scene.ActiveSelection), 2000), "Text focus did not discard moved pixels.");
        await WaitForFramesAsync(); AssertUploadedFrame(scene);
        report["selectedPixelCaptureAndRealTextFocusCancel"] = "passed";

        ToolCombo.SelectedIndex = (int)CanvasTool.Brush; CanvasStage.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
        Require(!NudgePixels(VirtualKey.Right, VirtualKeyModifiers.None) && !NudgePixels(VirtualKey.Right, VirtualKeyModifiers.Control, true)
            && !NudgePixels(VirtualKey.Right, VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu), "Pixel arrows intercepted plain/text/Alt shortcuts.");
        for (int i = 0; i < 6; i++) Require(NudgePixels(VirtualKey.Right, VirtualKeyModifiers.Control), "Repeated Ctrl arrow was refused.");
        Require(NudgePixels(VirtualKey.Up, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift), "Ctrl+Shift arrow was refused.");
        await pixelNudgeWork; await WaitForSelectionReadyAsync(); await WaitForFramesAsync();
        var accumulated = scene.ActiveSelection?.OutlineOffset;
        OnUndo(this, new());
        // OnUndo is async void and waits for the nudge chain on its own, so awaiting that chain here
        // only proves the arrows landed; the undo's continuation still has to reach the document.
        var undoLanded = await WaitUntilAsync(() => scene.ActiveSelection?.OutlineOffset == new PointD(6, 0), 5000);
        var observedUndo = scene.ActiveSelection?.OutlineOffset;
        await WaitForFramesAsync(); AssertUploadedFrame(scene);
        report["selectedPixelArrowUndoWaitsForChain"] = new { accumulated, undoLanded, observedUndo };
        Require(accumulated == new PointD(6, -10), "Seven pixel arrows did not accumulate into one offset before the undo.");
        Require(undoLanded, $"Undo did not wait for queued pixel arrows (observed {observedUndo}).");
        for (int i = 0; i < 6; i++)
        {
            var beforeUndo = scene.ActiveSelection?.OutlineOffset;
            OnUndo(this, new());
            await WaitUntilAsync(() => scene.ActiveSelection?.OutlineOffset != beforeUndo, 5000);
            await WaitForSelectionReadyAsync(); await WaitForFramesAsync();
        }
        Require(!scene.CanUndo && ReferenceEquals(original, scene.ActiveSelection), "Pixel arrows did not retain individual history steps.");
        report["selectedPixelArrowsAnyToolOrderedHistory"] = "passed";

        ToolCombo.SelectedIndex = (int)CanvasTool.RectSelect;
        Require(TryBeginPixelMove(new(10, 10), VirtualKeyModifiers.Control), "Owner fixture did not start."); MovePixelsTo(new(30, 10), VirtualKeyModifiers.Control);
        var abandoned = FinishPixelMoveAsync(); var replacement = EditorDocument.Create(64, 48); OpenDocument(replacement);
        await abandoned; await WaitForFramesAsync(); AssertUploadedFrame(replacement);
        Require(!replacement.CanUndo && pixelMove is null, "A released old document committed into its replacement.");
        report["selectedPixelOldOwnerPublicationRejected"] = "passed";

        foreach (bool independent in new[] { false, true }) foreach (bool effects in new[] { false, true })
        {
            scene = EditorDocument.Create(128, 128); var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
            var layer = scene.Snapshot.Manifest.Layers[0]; var asset = new Surface(32, 32);
            for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++)
            { int index = y * asset.Stride + x * 4; asset.Pixels[index] = (byte)(x * 4); asset.Pixels[index + 1] = (byte)(y * 4); asset.Pixels[index + 3] = 160; }
            ((Dictionary<Guid, Surface>)scene.Snapshot.Images)[id] = asset;
            layer.Transform = new() { Origin = new(20, 20), Size = new(64, 64), Rotation = 30, FlipX = true };
            var mask = new MaskSurface(32, 32); for (int i = 0; i < mask.Pixels.Length; i++) mask.Pixels[i] = (byte)(96 + i % 160);
            ((Dictionary<Guid, MaskSurface>)scene.Snapshot.Masks)[id] = mask; layer.MaskFile = ProjectValidator.Uuid(id) + ".mask.png";
            if (independent) { layer.MaskLinked = false; layer.MaskPlacement = new() { Origin = new(16, 24), Size = new(72, 48) }; }
            if (effects) scene.SetLayerEffects(id, new() { Shadow = new() { Blur = 8, Distance = 5 } });
            scene = new(scene.Snapshot); OpenDocument(scene); ToolCombo.SelectedIndex = (int)CanvasTool.RectSelect; await WaitForFramesAsync();
            original = Selection.Rectangle(128, 128, 24, 24, 40, 40).Feathered(2); scene.SetSelection(original); await WaitForSelectionReadyAsync();
            Require(TryBeginPixelMove(new(40, 40), VirtualKeyModifiers.Control), "Transformed masked pixel move did not start.");
            MovePixelsTo(new(60, 46), VirtualKeyModifiers.Control); await WaitForFramesAsync();
            AssertUploadedFrame(new EditorDocument(compositor!.Snapshot));
            await FinishPixelMoveAsync(); await WaitForFramesAsync(); AssertUploadedFrame(scene);
            Require(scene.ActiveSelection?.OutlineOffset == new PointD(20, 6), "Masked commit lost its outline.");
            OnUndo(this, new()); await WaitForFramesAsync(); AssertUploadedFrame(scene);
            Require(!scene.CanUndo && ReferenceEquals(asset, scene.Snapshot.Images[id]) && ReferenceEquals(original, scene.ActiveSelection), "Masked move Undo lost source state.");
            paintOnMask = true;
            Require(!TryBeginPixelMove(new(40, 40), VirtualKeyModifiers.Control) && HandlePixelMovePress(new(40, 40), VirtualKeyModifiers.Control)
                && pixelMove is null && !scene.CanUndo && ReferenceEquals(original, scene.ActiveSelection), "Ctrl-inside on a mask target began a marquee or pixel edit.");
            paintOnMask = false;
            report[$"selectedPixelRotatedFlipFeatherGpuMask{independent}Effects{effects}"] = "passed";
        }

        scene = EditorDocument.Create(4096, 4096); OpenDocument(scene); await WaitForFramesAsync();
        original = Selection.Rectangle(4096, 4096, 200, 200, 32, 32); scene.SetSelection(original); await WaitForSelectionReadyAsync();
        Require(TryBeginPixelMove(new(210, 210), VirtualKeyModifiers.Control), "4K pixel movement did not start.");
        MovePixelsTo(new(215, 211), VirtualKeyModifiers.Control); await WaitForFramesAsync(); warm = compositor;
        long frames = presentedFrameCount, allocated = GC.GetAllocatedBytesForCurrentThread(); var timer = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 240; i++) MovePixelsTo(new(210 + i % 20 + 1, 210 + i % 8), VirtualKeyModifiers.Control);
        timer.Stop(); long inputBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        await WaitForFramesAsync(); Require(ReferenceEquals(warm, compositor) && ReferenceEquals(original, scene.ActiveSelection), "4K movement lost its compositor or mutated its selection.");
        var ticks = new List<double>(); var workerBytes = new List<long>();
        for (int i = 0; i < 12; i++)
        {
            MovePixelsTo(new(220 + i, 213), VirtualKeyModifiers.Control); await WaitForFramesAsync();
            ticks.Add(lastFrameProbe.InputMs + lastFrameProbe.CpuMs + lastFrameProbe.UploadMs); workerBytes.Add(lastFrameProbe.WorkerBytes);
        }
        AssertUploadedFrame(new EditorDocument(compositor!.Snapshot));
        report["selectedPixel4K240InputAndWarmFrames"] = new { dispatchMs = timer.Elapsed.TotalMilliseconds, inputKiB = inputBytes / 1024d,
            dispatchFrames = presentedFrameCount - frames - 12, tickMedianMs = ticks.Order().ElementAt(ticks.Count / 2), tickMaxMs = ticks.Max(),
            workerMedianKiB = workerBytes.Order().ElementAt(workerBytes.Count / 2) / 1024d, previewPixels = pixelMove!.Edit.PixelBounds };
        var releaseTimer = System.Diagnostics.Stopwatch.StartNew(); await FinishPixelMoveAsync(); await WaitForFramesAsync(); releaseTimer.Stop(); AssertUploadedFrame(scene);
        report["selectedPixel4KCommitSingleSampleMs"] = releaseTimer.Elapsed.TotalMilliseconds;
        Require(LayerMenu.IsEnabled && LayerPropertiesScroll.IsEnabled && OpacityBox.IsEnabled && pixelMove is null,
            "Pixel commit left the layer properties locked.");
        report["selectedPixelCommitRestoresLayerControls"] = "passed";
        RootGrid.UpdateLayout(); await Task.Delay(80);
        await SaveWorkspaceCaptureAsync("pixel-move-workspace.png");

        scene = EditorDocument.Create(64, 48); OpenDocument(scene); await WaitForFramesAsync();
        scene.SetSelection(Selection.Rectangle(64, 48, 4, 4, 16, 12)); await WaitForSelectionReadyAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var release = new ManualResetEventSlim(false);
        beforeFrameRender = () => { entered.TrySetResult(); release.Wait(); };
        try
        {
            Require(TryBeginPixelMove(new(10, 10), VirtualKeyModifiers.Control), "Held worker fixture did not start.");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); MovePixelsTo(new(35, 20), VirtualKeyModifiers.Control);
            var pending = FinishPixelMoveAsync(); CancelPixelMove(focusLoss: true); beforeFrameRender = null; release.Set(); await pending;
        }
        finally { beforeFrameRender = null; release.Set(); }
        await WaitForFramesAsync(); AssertUploadedFrame(scene); Require(scene.ActiveSelection?.OutlineOffset == new PointD(25, 10) && scene.CanUndo, "Released worker lost the latest pixel offset.");
        report["selectedPixelReleaseDuringWorkerKeepsLatestOffset"] = "passed";

        static Microsoft.UI.Xaml.Controls.TextBox? FindText(Microsoft.UI.Xaml.DependencyObject root)
        {
            if (root is Microsoft.UI.Xaml.Controls.TextBox text) return text;
            for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i++)
                if (FindText(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i)) is { } found) return found;
            return null;
        }
    }
}
#endif
