#if DEBUG
using System.Text.Json;
using System.Diagnostics;
using System.Linq;
using Compositor.Core;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.System;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private Task? currentRenderWork;
    private int renderWorkerFailureCount;
    internal async Task RunNavigationProbeAsync()
    {
        var report = new Dictionary<string, object>();
        // The guide switches persist to the preference files, so the guide section points that folder at a
        // scratch directory: a probe that throws half-way through must not leave the real editor reading
        // Snap To settings a test run turned off.
        var preferencesScratch = Path.Combine(Path.GetTempPath(), $"compositor-navigation-preferences-{Environment.ProcessId}");
        var preferencesPrevious = Environment.GetEnvironmentVariable("COMPOSITOR_PREFERENCES_DIRECTORY");
        try
        {
            await Task.Delay(500);
            if (RootGrid.FindName("CanvasStage") is null || RootGrid.FindName("HorizontalRuler") is null)
                throw new InvalidOperationException("Original free-pan viewport and rulers are missing.");
            AppWindow.Resize(new SizeInt32(1180, 820)); await Task.Delay(100);
            var scene = EditorDocument.Create(320, 200);
            foreach (var image in scene.Snapshot.Images.Values) image.Pixels.AsSpan().Clear();
            var source = new Surface(320, 200);
            int pixel = (60 * 320 + 50) * 4;
            source.Pixels[pixel] = 100; source.Pixels[pixel + 1] = 50; source.Pixels[pixel + 3] = 128;
            source.Pixels[pixel + 4 + 2] = 255; source.Pixels[pixel + 4 + 3] = 255;
            scene.ImportImage(source, "Sample pixels"); OpenDocument(scene); await WaitForFramesAsync();
            string originalManifest = ManifestJson.Serialize(scene.Snapshot.Manifest);
            var originalSnapshot = scene.Snapshot; var originalCompositor = compositor;
            int frames = presentedFrameCount;
            var mappings = new List<object>();
            foreach (double zoom in new[] { .001, .5, 1, 8, 32 })
            {
                SetViewportZoom(zoom); FlushViewport(); RootGrid.UpdateLayout();
                var documentPoint = new PointD(123.25, 78.75);
                var expected = viewport.ToView(documentPoint);
                var actual = CanvasHost.TransformToVisual(CanvasStage).TransformPoint(new Point(documentPoint.X, documentPoint.Y));
                Near(expected.X, actual.X, "XAML X transform"); Near(expected.Y, actual.Y, "XAML Y transform");
                var back = viewport.ToDocument(new(actual.X, actual.Y)); Near(documentPoint.X, back.X, "Document X"); Near(documentPoint.Y, back.Y, "Document Y");
                mappings.Add(new { zoom, scale = viewport.PointsPerPixel, x = actual.X, y = actual.Y });
            }
            OnZoom100(this, new RoutedEventArgs()); FlushViewport(); Near(1 / viewport.BackingScale, viewport.PointsPerPixel, "100% physical pixels");
            report["dpiAndXamlMappings"] = mappings;

            ToolCombo.SelectedIndex = (int)CanvasTool.Hand;
            Require(BeginNavigationAt(new(50, 60), false, false), "Hand did not start on a small image.");
            var origin = viewport.Origin; MoveNavigationTo(new(1050, -740)); EndNavigationAt(new(1050, -740), false);
            Near(origin.X + 1000, viewport.Origin.X, "Unbounded pan X"); Near(origin.Y - 800, viewport.Origin.Y, "Unbounded pan Y");
            ToolCombo.SelectedIndex = (int)CanvasTool.Brush;
            spaceHeld = true; Require(BeginNavigationAt(new(10, 10), false, false), "Space did not pan with Brush.");
            EndNavigationAt(new(20, 20), false); Require(tool == CanvasTool.Brush, "Temporary pan changed the selected tool."); spaceHeld = false;
            Require(BeginNavigationAt(new(10, 10), true, false), "Middle pan did not start."); EndNavigationAt(new(25, 25), false);
            report["handSpaceAndMiddlePan"] = "passed";

            // The shortcut reference used to be a hand copy and had already lost nine bound chords.
            // Its menu rows now come from the live bindings, so the two cannot disagree — asserted here,
            // because a table nobody checks is how "Ctrl+Plus" came to advertise a numpad-only key and a
            // laptop user had no keyboard zoom at all.
            var accelerated = MenuBarRoot.Items.OfType<MenuBarItem>()
                .SelectMany(bar => bar.Items.OfType<MenuFlyoutItemBase>())
                .Where(item => item.KeyboardAccelerators.Count > 0)
                .ToList();
            Require(RemappableShortcuts().Count == accelerated.Count,
                $"{accelerated.Count} menu items carry accelerators but the editor lists {RemappableShortcuts().Count}.");
            Require(accelerated.Any(item => item.KeyboardAccelerators.Any(accelerator =>
                        (int)accelerator.Key == OemPlusCode
                        && accelerator.Modifiers == Windows.System.VirtualKeyModifiers.Control)
                    && string.Join(" ", item.KeyboardAccelerators.Select(ChordLabel)).Contains("Ctrl+=", StringComparison.Ordinal)),
                "Zoom In is not reachable with Ctrl+= on the main keyboard row.");
            Require(accelerated.Any(item => item.KeyboardAccelerators.Any(accelerator =>
                        (int)accelerator.Key == OemMinusCode
                        && accelerator.Modifiers == Windows.System.VirtualKeyModifiers.Control)
                    && string.Join(" ", item.KeyboardAccelerators.Select(ChordLabel)).Contains("Ctrl+-", StringComparison.Ordinal)),
                "Zoom Out is not reachable with Ctrl+- on the main keyboard row.");
            // C05c: the Mac's layer step ⌘] / ⌘[ lives on the same main-row keys, and the brush handler
            // hands Control over to them rather than resizing the brush twice.
            Require(ReferenceEquals(RaiseMenuItem, accelerated.FirstOrDefault(item => item.KeyboardAccelerators.Any(accelerator =>
                        (int)accelerator.Key == CloseBracketCode
                        && accelerator.Modifiers == Windows.System.VirtualKeyModifiers.Control))),
                "Raise is not bound to Ctrl+] (and Lower colliding with it would step the brush instead).");
            Require(accelerated.Any(item => item.KeyboardAccelerators.Any(accelerator =>
                        (int)accelerator.Key == OpenBracketCode
                        && accelerator.Modifiers == Windows.System.VirtualKeyModifiers.Control)),
                "Lower is not bound to Ctrl+[.");
            // Open the real sheet through its DEBUG seam and read what it rendered, so the table cannot
            // pass by being derived but never displayed.
            var dialogRows = new List<string>();
            // Read what the editor actually rendered: a table that is derived correctly but never displayed is
            // how "Ctrl+Plus" once advertised a numpad-only key. The body is the form's own StackPanel now —
            // the scroll moved into the host, so there is no dialog Content to unwrap.
            OpenShortcutEditor();
            if (FloatingPanelBody("shortcuts") is StackPanel panel)
                foreach (var line in panel.Children.OfType<Grid>())
                {
                    var title = line.Children.OfType<TextBlock>().FirstOrDefault()?.Text;
                    var chord = line.Children.OfType<Button>().FirstOrDefault()?.Content as string
                        ?? line.Children.OfType<TextBlock>().Skip(1).FirstOrDefault()?.Text;
                    if (title is not null && chord is not null) dialogRows.Add($"{title}={chord}");
                }
            CloseShortcutEditor(commit: false);
            Require(dialogRows.Count > 0, "The shortcut editor rendered no rows.");
            Require(dialogRows.Any(row => row.Contains("Ctrl+=", StringComparison.Ordinal)),
                "The rendered table does not list Ctrl+= even though the menu carries it.");
            Require(dialogRows.Any(row => row.Contains("Ctrl+-", StringComparison.Ordinal)),
                "The rendered table does not list Ctrl+- even though the menu carries it.");
            report["shortcutRowsRendered"] = dialogRows.Count;
            // The chord guard itself is checked in the numeric-field section below, where a text field is
            // really focused; here the table and the main-row chords are what must hold.
            report["shortcutTableMatchesBindings"] = "passed";
            var zoomWhileEditing = viewport.Zoom; var panWhileEditing = viewport.PanX;
            dragStart = new(10, 10);
            Require(!BeginNavigationAt(new(50, 50), true, false), "Navigation stole an active selection gesture.");
            ApplyViewportWheel(120, true, true, new(50, 50)); ApplyViewportManipulation(2, new(50, 50), new(50, 50));
            Near(zoomWhileEditing, viewport.Zoom, "Editing gesture zoom guard"); Near(panWhileEditing, viewport.PanX, "Editing gesture pan guard");
            dragStart = null; report["activeSelectionBlocksNavigation"] = "passed";

            ToolCombo.SelectedIndex = (int)CanvasTool.Zoom; SetViewportZoom(1); FlushViewport();
            var anchor = new PointD(75, 85); var anchoredPixel = viewport.ToDocument(anchor);
            BeginNavigationAt(anchor, false, false); EndNavigationAt(anchor, false); Near(2, viewport.Zoom, "Zoom click");
            Near(anchoredPixel.X, viewport.ToDocument(anchor).X, "Click anchor X");
            BeginNavigationAt(anchor, false, true); EndNavigationAt(anchor, true); Near(1, viewport.Zoom, "Alt zoom click");
            BeginNavigationAt(anchor, false, false); MoveNavigationTo(new(175, 85)); EndNavigationAt(new(175, 85), false); Near(2, viewport.Zoom, "100-point scrub");
            BeginNavigationAt(anchor, false, false); EndNavigationAt(new(76, 85), false); Near(4, viewport.Zoom, "Subthreshold click");
            NavigationZoomBox.Value = 75; FlushViewport(); Near(.75, viewport.Zoom, "Numeric zoom binding");
            RootGrid.UpdateLayout(); await Task.Delay(30);
            var zoomInput = FindTextInput(NavigationZoomBox);
            Require(CanvasStage.Focus(FocusState.Programmatic), "Canvas could not release the previous numeric focus.");
            await Task.Delay(1);
            report["numericFocusBefore"] = new { state = zoomInput?.FocusState.ToString(), focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(RootGrid.XamlRoot)?.GetType().Name };
            if (zoomInput is not null) zoomInput.GotFocus += (_, args) =>
            {
                report["numericGotFocusSource"] = args.OriginalSource?.GetType().Name ?? "null";
            };
            spaceHeld = true; Require(zoomInput?.Focus(FocusState.Programmatic) == true, "Visible numeric text field could not focus.");
            var focusWait = Stopwatch.StartNew();
            while (spaceHeld && focusWait.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(1);
            Require(!spaceHeld, "A numeric text field retained temporary pan after focus became active.");
            // The same field has to hold the modifier-less palette chord (plain X / D) and the brush
            // brackets off the canvas: a root accelerator resolves before the field sees the character,
            // so without this guard typing a name would swap colors mid-word.
            Require(TypingInField, "A focused text field did not hold the plain X/D chord and the brush brackets off the canvas.");
            CanvasStage.Focus(FocusState.Programmatic);
            Require(!TypingInField, "The typing guard stayed on after focus left the field.");
            ApplyViewportWheel(120, false, true, anchor); FlushViewport(); Near(.75 * Math.Exp(.18), viewport.Zoom, "Wheel zoom");
            double panBefore = viewport.PanX; ApplyViewportWheel(120, true, false, anchor); FlushViewport(); Near(panBefore + 40, viewport.PanX, "Horizontal wheel pan");
            double zoomBefore = viewport.Zoom; ApplyViewportManipulation(1.2, new(15, 20), anchor); FlushViewport(); Near(zoomBefore * 1.2, viewport.Zoom, "Pinch calculation");
            report["zoomClickScrubFieldWheelAndManipulation"] = "passed";

            FitToWindow(); FlushViewport();
            ToolCombo.SelectedIndex = (int)CanvasTool.Eyedropper;
            var samplePoint = viewport.ToView(new(50.5, 60.5));
            Require(BeginNavigationAt(samplePoint, false, false), "Eyedropper did not start.");
            report["compositeSample"] = new { red = (int)foregroundColor.R, green = (int)foregroundColor.G, blue = (int)foregroundColor.B,
                premultipliedBgra = fullTarget?.GetPixelBytes(50, 60, 1, 1).Select(value => (int)value).ToArray() };
            Require(foregroundColor.R == 199 && foregroundColor.G == 100 && foregroundColor.B == 0, "Composite sample failed to unpremultiply sRGB.");
            Require(SampleRingCanvas.Visibility == Visibility.Visible, "Sample ring was not shown."); EndNavigationAt(samplePoint, false);
            var sampled = foregroundColor;
            Require(!SamplePalette(viewport.ToView(new(0, 0)), false) && foregroundColor == sampled, "Transparent sample picked the checkerboard.");
            Require(!SamplePalette(viewport.ToView(new(-1, 0)), false) && foregroundColor == sampled, "Outside sample changed the palette.");
            EyedropperTargetCombo.SelectedIndex = 1; var bluePoint = viewport.ToView(new(51.5, 60.5));
            BeginNavigationAt(bluePoint, false, false); EndNavigationAt(bluePoint, false);
            Require(backgroundColor.B == 255 && backgroundColor.R == 0 && foregroundColor == sampled, "Background target changed the foreground.");
            ToolCombo.SelectedIndex = (int)CanvasTool.Brush; foregroundColor = Microsoft.UI.Colors.Black;
            BeginNavigationAt(samplePoint, false, true); EndNavigationAt(samplePoint, true);
            Require(foregroundColor == sampled && tool == CanvasTool.Brush, "Alt sample did not retain Brush.");
            ToolCombo.SelectedIndex = (int)CanvasTool.Clone; Require(!BeginNavigationAt(samplePoint, false, true), "Alt Clone was intercepted by palette sampling.");
            report["compositePaletteTransparencyTargetsAndAlt"] = "passed";

            foreach (double zoom in new[] { 1.0, 8.0 })
            {
                SetViewportZoom(zoom); FlushViewport();
                var originForPixels = viewport.Origin;
                viewport.Pan(new(10 / viewport.BackingScale - originForPixels.X, 10 / viewport.BackingScale - originForPixels.Y)); FlushViewport();
                using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), (float)viewport.Width, (float)viewport.Height, (float)(96 * viewport.BackingScale));
                using (var drawing = target.CreateDrawingSession())
                { drawing.Clear(Microsoft.UI.ColorHelper.FromArgb(255, 27, 27, 27)); DrawViewportContent(drawing); }
                int x = 10 + (int)(51 * zoom), y = 10 + (int)(60 * zoom);
                foreach (var offset in new[] { (0, 0), ((int)zoom - 1, (int)zoom - 1) })
                {
                    var color = target.GetPixelBytes(x + offset.Item1, y + offset.Item2, 1, 1);
                    Require(color.AsSpan().SequenceEqual(new byte[] { 255, 0, 0, 255 }), $"Viewport at {zoom * 100}% blurred the blue pixel.");
                }
            }
            report["physicalPixelViewportAt100And800Percent"] = "passed";

            ToolCombo.SelectedIndex = (int)CanvasTool.Hand;
            int viewportFramesBefore = viewportFrameCount; BeginNavigationAt(new(10, 10), false, false);
            var timer = Stopwatch.StartNew();
            for (int i = 1; i <= 1000; i++) MoveNavigationTo(new(10 + i * .1, 10 + i * .05));
            double dispatchMs = timer.Elapsed.TotalMilliseconds;
            Require(viewportFrameCount == viewportFramesBefore && viewportFrameQueued, "Pan failed to coalesce until the display frame.");
            EndNavigationAt(new(110, 60), false); Require(viewportFrameCount == viewportFramesBefore + 1, "Pan published multiple synchronous frames.");
            CancelNavigation(); Require(panLast is null && zoomDrag is null && !samplingPalette, "Cancellation left a navigation gesture.");
            await WaitForFramesAsync();
            Require(presentedFrameCount == frames && ReferenceEquals(originalCompositor, compositor) && ReferenceEquals(originalSnapshot, scene.Snapshot)
                && ManifestJson.Serialize(scene.Snapshot.Manifest) == originalManifest, "Navigation modified or recomposited the document.");
            report["coalescedNavigationWithoutDocumentRender"] = new { status = "passed", inputs = 1000, dispatchMs, viewportFrames = 1, compositeFrames = presentedFrameCount - frames };

            RulersToggle.IsChecked = true; RefreshRulersVisibility(); RootGrid.UpdateLayout(); ResizeViewport(); FlushViewport();
            Require(RulerRow.Height.Value == 18 && RulerColumn.Width.Value == 18, "Ruler thickness is not the original 18 points.");
            FitToWindow(); FlushViewport();
            Directory.CreateDirectory(preferencesScratch);
            Environment.SetEnvironmentVariable("COMPOSITOR_PREFERENCES_DIRECTORY", preferencesScratch);
            // The coordinate assertions below are written against the raw pointer position — what the app
            // produced before guides could snap, and what it still produces with View ▸ Snap off. Snapping
            // gets its own group after them, so neither claim has to share a sample with the other.
            SnapToggle.IsChecked = false; OnToggleSnap(SnapToggle, new RoutedEventArgs());
            BeginRulerGuideAt(GuideAxis.Vertical, new(-5, 30)); MoveRulerGuideTo(new(120, 80)); EndRulerGuideAt(new(-1, 80));
            Require(scene.Snapshot.Manifest.Guides is null || scene.Snapshot.Manifest.Guides.Count == 0, "Returning a new guide to a ruler created history.");
            var guidePoint = new PointD(120, 80); double expectedPosition = viewport.ToDocument(guidePoint).Y;
            BeginRulerGuideAt(GuideAxis.Horizontal, new(120, -5)); EndRulerGuideAt(guidePoint);
            Require(scene.Snapshot.Manifest.Guides?.Count == 1 && scene.Snapshot.Manifest.Guides[0].Axis == GuideAxis.Horizontal, "Ruler did not create its axis guide.");
            Near(expectedPosition, scene.Snapshot.Manifest.Guides![0].Position, "Guide document coordinate");
            await WaitForFramesAsync(); Require(presentedFrameCount == frames, "Guide creation recomposited pixels.");
            scene.Undo(); Require(scene.Snapshot.Manifest.Guides is null || scene.Snapshot.Manifest.Guides.Count == 0, "Guide Undo left a guide.");
            scene.Redo(); Require(scene.Snapshot.Manifest.Guides?.Count == 1, "Guide Redo lost the guide.");
            await WaitForFramesAsync(); report["rulerThicknessGuideCreationCancelUndoRedo"] = "passed";
            var committedGuide = scene.Snapshot.Manifest.Guides![0];
            string beforeGuideDrag = ManifestJson.Serialize(scene.Snapshot.Manifest);
            BeginExistingGuideAt(committedGuide, guidePoint); MoveRulerGuideTo(new(150, 150));
            Require(ManifestJson.Serialize(scene.Snapshot.Manifest) == beforeGuideDrag, "Guide preview changed document history before release.");
            CancelNavigation(); Require(scene.Snapshot.Manifest.Guides![0].Position == expectedPosition, "Cancelling a guide changed its position.");
            BeginExistingGuideAt(scene.Snapshot.Manifest.Guides![0], guidePoint); MoveRulerGuideTo(new(150, 150)); EndRulerGuideAt(new(150, 150));
            Near(viewport.ToDocument(new(150, 150)).Y, scene.Snapshot.Manifest.Guides![0].Position, "Moved guide coordinate");
            scene.Undo(); Near(expectedPosition, scene.Snapshot.Manifest.Guides![0].Position, "Single-step guide move Undo");
            BeginExistingGuideAt(scene.Snapshot.Manifest.Guides![0], guidePoint); MoveRulerGuideTo(new(150, 150)); EndRulerGuideAt(new(150, -1));
            Require(scene.Snapshot.Manifest.Guides is null or { Count: 0 }, "Returning an existing guide to a ruler did not delete it.");
            scene.Undo(); Near(expectedPosition, scene.Snapshot.Manifest.Guides![0].Position, "Single-step guide deletion Undo");
            await WaitForFramesAsync(); report["guideDraftCancelMoveDeleteSingleUndo"] = "passed";

            // C10c: with Snap back on, a guide is created and dragged onto an alignment line instead of
            // staying under the pointer (Guides.swift:288-305). Every expectation is hand-computed from the
            // document: the canvas midline and the top edge are bound targets, and the pointers below sit a
            // fraction of the tolerance off them — close enough to snap, and far enough from every other
            // target that "nearest wins" can only choose that one value.
            SnapToggle.IsChecked = true; OnToggleSnap(SnapToggle, new RoutedEventArgs());
            double guideMidline = scene.Snapshot.Manifest.Height / 2.0;
            double guideReach = 10 / Math.Max(viewport.PointsPerPixel, .0001);
            double guideOffset = Math.Min(guideReach / 2, guideMidline / 8);
            Require(guideOffset > .01 && guideReach > guideOffset, $"the guide snap fixture has nothing to snap within (reach {guideReach:R}).");
            // Stage-to-document is affine at this zoom; two known points fix it, which keeps the arithmetic
            // independent of the mapping the product uses.
            double stageScale = (viewport.ToDocument(new PointD(120, 150)).Y - expectedPosition) / 70;
            Require(Math.Abs(stageScale) > .0001, $"the stage-to-document scale came out degenerate ({stageScale:R}).");
            PointD StageFor(double documentY) => new(120, 80 + (documentY - expectedPosition) / stageScale);
            Near(guideMidline + guideOffset, viewport.ToDocument(StageFor(guideMidline + guideOffset)).Y, "Guide snap fixture mapping");

            BeginRulerGuideAt(GuideAxis.Horizontal, new(120, -5));
            var offMidline = StageFor(guideMidline + guideOffset);
            MoveRulerGuideTo(offMidline); EndRulerGuideAt(offMidline);
            var snappedGuide = scene.Snapshot.Manifest.Guides!.Last(guide => guide.Axis == GuideAxis.Horizontal);
            Require(snappedGuide.Position == guideMidline,
                $"creating a guide {guideOffset:R} off the midline (reach {guideReach:R}) committed {snappedGuide.Position}, expected {guideMidline}.");
            report["guideCreationSnapsToTheMidline"] = "passed";

            var rawGuide = scene.Snapshot.Manifest.Guides!.First(guide => guide.Id != snappedGuide.Id);
            BeginExistingGuideAt(rawGuide, guidePoint);
            var nearTop = StageFor(guideOffset / 2);
            MoveRulerGuideTo(nearTop); EndRulerGuideAt(nearTop);
            Require(scene.Snapshot.Manifest.Guides!.Any(guide => guide.Id == rawGuide.Id && guide.Position == 0),
                $"dragging a guide to {guideOffset / 2:R} did not park it on the canvas top.");
            report["guideDragSnapsToTheCanvasEdge"] = "passed";

            // With Snap off the same drag lands exactly where the pointer is, so the switch is the
            // difference rather than a label over one behaviour.
            SnapToggle.IsChecked = false; OnToggleSnap(SnapToggle, new RoutedEventArgs());
            var movedGuide = scene.Snapshot.Manifest.Guides!.First(guide => guide.Id == rawGuide.Id);
            BeginExistingGuideAt(movedGuide, StageFor(0));
            var rawAgain = StageFor(guideMidline + guideOffset);
            MoveRulerGuideTo(rawAgain); EndRulerGuideAt(rawAgain);
            Near(guideMidline + guideOffset, scene.Snapshot.Manifest.Guides!.First(guide => guide.Id == rawGuide.Id).Position,
                "Guide drag with Snap off");
            SnapToggle.IsChecked = true; OnToggleSnap(SnapToggle, new RoutedEventArgs());
            report["guideSnapFollowsTheSwitch"] = "passed";

            // #31: a recorded chord has to reach the live menu item, the preference file, and the next
            // launch — and a set that collides has to reach none of them. Recording is driven through the
            // editor's own entry point, because no probe can synthesize the key press.
            var boundRow = RemappableShortcuts().First(row => row.Id == "CopyMergedMenuItem");
            var builtIn = boundRow.Item.KeyboardAccelerators.Select(each => new ShortcutChord(each.Key, each.Modifiers)).ToList();
            var recorded = FreeChord();
            // Facts are collected inside the sheet and judged outside it: an assertion thrown in here would
            // leave through an async void handler, reach the dispatcher instead of this probe, and show up
            // later as some unrelated failure — one RED round did exactly that.
            bool openedSaveable = false, recordedButNotBound = false;
            OpenShortcutEditor();
            openedSaveable = ShortcutSaveEnabled();
            shortcutEditorProbe!.Record(boundRow.Id, recorded.Key, recorded.Modifiers);
            recordedButNotBound = ChordLabel(boundRow.Item.KeyboardAccelerators[0]) != recorded.Label;
            CloseShortcutEditor(commit: true);
            Require(openedSaveable, "the shortcut editor opened with saving disabled, so the built-in chords collide.");
            Require(recordedButNotBound, "recording rewrote the live menu before the draft was saved.");
            Require(await WaitUntilAsync(() => Bound(boundRow) == recorded, 2000),
                $"saving {recorded.Label} never reached the {boundRow.Title} menu item.");
            var stored = File.ReadAllText(Path.Combine(preferencesScratch, ShortcutsPreference + ".txt"));
            Require(stored.Contains($"{boundRow.Id}={recorded.Store}"), $"the saved chord is not in the preference file ({stored}).");
            // Then the load path a fresh window takes: the item goes back to what its markup declares,
            // the process forgets the override, and the file alone has to put it back.
            Reset(boundRow, builtIn);
            shortcutChords.Clear(); shortcutApplied.Clear();
            LoadAndApplyShortcuts();
            Require(Bound(boundRow) == recorded, $"the reloaded chord is {Bound(boundRow).Label}, not the {recorded.Label} that was saved.");
            // A second command given the same chord: the sheet must refuse, and refuse without touching
            // anything the first save had already applied.
            var otherRow = RemappableShortcuts().First(row => row.Id == "Zoom100MenuItem");
            var otherBuiltIn = otherRow.Item.KeyboardAccelerators.Select(each => new ShortcutChord(each.Key, each.Modifiers)).ToList();
            bool refusedTheConflict = false;
            OpenShortcutEditor();
            shortcutEditorProbe!.Record(otherRow.Id, recorded.Key, recorded.Modifiers);
            refusedTheConflict = !ShortcutSaveEnabled();
            CloseShortcutEditor(commit: true);
            Require(refusedTheConflict, $"the editor let {recorded.Label} be assigned to two commands.");
            await WaitForFramesAsync();
            Require(Bound(otherRow) != recorded, "a refused save still bound the conflicting chord.");
            Require(File.ReadAllText(Path.Combine(preferencesScratch, ShortcutsPreference + ".txt")).Contains($"{otherRow.Id}=") == false,
                "a refused save still wrote the conflicting command to the file.");
            // Restore Defaults then puts every command back on its built-in and stores nothing, which is
            // also the proof that removing an override really unbinds what it had applied.
            bool restorable = false, restoreStayedOutOfTheMenu = false;
            OpenShortcutEditor();
            shortcutEditorProbe!.RestoreDefaults();
            restorable = ShortcutSaveEnabled();
            restoreStayedOutOfTheMenu = ChordLabel(boundRow.Item.KeyboardAccelerators[0]) == recorded.Label;
            CloseShortcutEditor(commit: true);
            Require(restorable, "restoring defaults left the editor unable to save.");
            Require(restoreStayedOutOfTheMenu, "restoring defaults reached the menu before the save.");
            Require(await WaitUntilAsync(() => Bound(boundRow) == builtIn[0] && Bound(otherRow) == otherBuiltIn[0], 2000),
                $"Restore Defaults did not put the built-in chords back ({Bound(boundRow).Label}, {Bound(otherRow).Label}).");
            Require(File.ReadAllText(Path.Combine(preferencesScratch, ShortcutsPreference + ".txt")).Trim() == ShortcutRules.Format,
                "the restored preference file still holds an override.");
            // #37: the sheet is non-modal now, so both halves of that have to hold. The canvas keeps
            // answering while it is up — which is the whole point of moving it — and the sheet's own focus is
            // what the canvas stands down for, the split a dialog used to get for free by living outside the
            // tree. Both are asserted here because the recorder is the one panel that reads every key: a grab
            // that reached past the panel would eat the chord, and one that reached past the canvas would
            // switch tools out from under a user who is only trying to look at the list.
            OpenShortcutEditor();
            Require(!FloatingPanelHoldsFocus, "the shortcut sheet read itself as the responder before anything in it had focus.");
            ToolCombo.SelectedIndex = (int)CanvasTool.Hand;
            Require(await WaitForToolSwitchLandedAsync(CanvasTool.Hand),
                "the tool rail stopped answering while the shortcut sheet was open.");
            Require(FocusFloatingPanel("shortcuts"), "the shortcut sheet offers nothing focusable to stand in for its own control.");
            Require(FloatingPanelHoldsFocus && TypingInField,
                "focus inside the sheet did not read as the sheet's own, so a chord meant for the recorder would have reached the canvas.");
            CanvasStage.Focus(FocusState.Programmatic);
            Require(!FloatingPanelHoldsFocus, "the canvas never got the focus back from the shortcut sheet.");
            CloseShortcutEditor(commit: false);
            report["shortcutsRebindTheLiveMenuAndReload"] = "passed";

            // The canvas half (#39): a rebind there cannot rewrite an accelerator, so the claim is about the
            // translation the matcher consults. Defaults first — every declared letter answers to itself and
            // nothing is swallowed, which is what a user who never opened the sheet gets.
            // Sixteen letters, one per tool the canvas answers to: this table is now the only place a tool
            // letter lives, so a missing or duplicated entry is a tool nobody can reach.
            Require(CanvasToolKeys.Length == 16, $"the canvas letter table holds {CanvasToolKeys.Length} entries, not 16.");
            Require(CanvasToolKeys.Select(entry => entry.Key).Distinct().Count() == CanvasToolKeys.Length,
                "two canvas commands answer to the same declared letter.");
            Require(CanvasToolKeys.Select(entry => entry.Tool).Distinct().Count() == CanvasToolKeys.Length,
                "two canvas letters select the same tool, so one of them is a dead key.");
            foreach (var entry in CanvasToolKeys)
            {
                var (key, swallowed) = CanvasKeyFor(entry.Key, false);
                Require(!swallowed && key == entry.Key, $"{entry.Id} no longer answers to its own letter ({key}, {swallowed}).");
                // The action behind the letter is the combo's selection, and that is what a probe can drive:
                // Lasso, Gradient and Healing used to set it from their own handlers, so their entries are the
                // ones that would go silent if the table and the matcher ever disagreed.
                ToolCombo.SelectedIndex = (int)entry.Tool;
                // #38: the tool field moves before the switch's own wait, so it is read straight through here.
                // Waiting for it would return at once and prove nothing — the same trap as asserting on a
                // value the test itself just set.
                Require(tool == entry.Tool, $"{entry.Id} does not select its tool as soon as the combo says so (live {tool}).");
                // And the routing itself: the table is what the matcher reads, so a letter whose route is
                // missing is a tool no key can reach. Composed with the translation, because a rebind is only
                // useful if the translated letter still lands on the same tool.
                Require(ToolForPress(entry.Key, false) == entry.Tool, $"{entry.Id} no longer routes {entry.Key} to its tool.");
                Require(ToolForPress(CanvasKeyFor(entry.Key, false).Key, false) == entry.Tool,
                    $"{entry.Id} does not route through its own translation.");
            }
            Require(ToolForPress(VirtualKey.M, true) == CanvasTool.EllipseSelect && ToolForPress(VirtualKey.M, false) == CanvasTool.RectSelect,
                "Shift no longer upgrades the marquee letter, or the plain letter stopped being the rectangle.");
            Require(ToolForPress(VirtualKey.G, false) == CanvasTool.Gradient && ToolForPress(VirtualKey.J, false) == CanvasTool.Healing
                && ToolForPress(VirtualKey.L, false) == CanvasTool.Lasso,
                "G, J and L are no longer selected by the table, so the handlers that used to take them are silent.");
            // What the canvas side offers, counted by table: the tool letters, the brush brackets, the sixteen
            // arrow nudges, the eight spacing chords the text editor answers, the two palette chords, the four
            // structural keys and the ten opacity digits. The sum is a tripwire — it forces whoever adds a table
            // to say so here — while the real product fact is the second line: two rows sharing a built-in chord
            // would be a conflict the sheet can never resolve, and a repeated id would make one stored line name
            // two commands.
            Require(CanvasCommands().Count == CanvasToolKeys.Length + CanvasBracketKeys.Length + CanvasArrowKeys.Length
                        + CanvasSpacingKeys.Length + PaletteKeys.Length + CanvasStructuralKeys.Length + CanvasOpacityDigits.Length,
                $"the rebindable canvas set holds {CanvasCommands().Count} rows, not the sum of its tables.");
            Require(CanvasCommands().Select(command => command.Id).Distinct().Count() == CanvasCommands().Count
                    && CanvasCommands().Select(command => command.Default).Distinct().Count() == CanvasCommands().Count,
                "two canvas rows share an id or a built-in chord, so a stored binding could not name exactly one command.");
            Require(CanvasCommands().Count(row => row.Id.Contains("Nudge") || row.Id.Contains("MovePixels")) == CanvasArrowKeys.Length,
                $"the sheet offers {CanvasCommands().Count(row => row.Id.Contains("Nudge") || row.Id.Contains("MovePixels"))} arrow rows, not {CanvasArrowKeys.Length}.");
            foreach (var row in CanvasArrowKeys)
            {
                var declared = new ShortcutChord(row.Key, row.Default);
                var (own, lost) = CanvasTranslate(declared);
                Require(own == declared && !lost, $"{row.Id} no longer answers to its own chord ({own.Label}, {lost}).");
            }
            // An arrow moved onto a free letter: the letter becomes the nudge and the arrow key it left is
            // swallowed, which is the half that keeps a rebind from painting two commands with one press.
            OpenShortcutEditor();
            shortcutEditorProbe!.Record("canvas:NudgeOutline.SC_Left.1", VirtualKey.Q, 0);
            CloseShortcutEditor(commit: true);
            Require(await WaitUntilAsync(() => CanvasChord(shortcutChords, "canvas:NudgeOutline.SC_Left.1")
                == new ShortcutChord(VirtualKey.Q, 0), 2000), "the rebound arrow chord never reached the live set.");
            Require(CanvasTranslate(new ShortcutChord(VirtualKey.Q, 0)) is (var asLeft, false) && asLeft.Key == VirtualKey.Left,
                "the letter the user bound to a nudge never becomes that nudge.");
            Require(CanvasTranslate(new ShortcutChord(VirtualKey.Left, 0)).Swallowed,
                "the arrow key its command left still nudges something else.");
            RestoreCanvasChords();

            // The eight spacing chords the text editor answers (KeyboardShortcuts.swift:119-124), held to the
            // same three things the arrows are: the table offers them, each answers to its own built-in chord,
            // and moving one onto another key moves the command with it — here checked through the very method
            // the inline editor asks, so a handler that read the key directly instead of the table would fail.
            Require(CanvasSpacingKeys.Length == 8 && CanvasSpacingKeys.Select(row => row.Id).Distinct().Count() == 8,
                $"the spacing table offers {CanvasSpacingKeys.Length} rows with {CanvasSpacingKeys.Select(row => row.Id).Distinct().Count()} distinct ids.");
            Require(Loc.T("DlgGroupTextEditing") != "DlgGroupTextEditing"
                    && CanvasSpacingKeys.All(row => Loc.F(row.TitleKey, row.Pixels) != row.TitleKey),
                "the text-editing group or a spacing title is missing from the resources, so the sheet would show a raw key.");
            foreach (var row in CanvasSpacingKeys)
            {
                var declared = new ShortcutChord(row.Key, row.Default);
                Require(CanvasTranslate(declared) is (var own, false) && own == declared,
                    $"{row.Id} no longer answers to its own chord ({CanvasTranslate(declared).Chord.Label}).");
                Require(TrySpacingChord(row.Key, row.Default.HasFlag(VirtualKeyModifiers.Menu),
                            row.Default.HasFlag(VirtualKeyModifiers.Shift), out bool leading, out bool increase, out double step)
                        && leading == row.Id.Contains("Leading", StringComparison.Ordinal)
                        && increase == row.Id.Contains("Increase", StringComparison.Ordinal)
                        && Math.Abs(step - row.Pixels) < 1e-9,
                    $"{row.Id} does not come out of its own chord: leading={leading} increase={increase} step={step:0.#}");
            }
            OpenShortcutEditor();
            var spacingRows = shortcutEditorProbe!.Rows().Where(entry => entry.Id.StartsWith("canvas:Spacing.", StringComparison.Ordinal)).ToList();
            CloseShortcutEditor(commit: false);
            Require(spacingRows.Count == CanvasSpacingKeys.Length
                    && spacingRows.All(entry => entry.Group == Loc.T("DlgGroupTextEditing") && entry.Remappable),
                $"the sheet offers {spacingRows.Count} spacing rows under {spacingRows.Select(entry => entry.Group).Distinct().Count()} group(s), "
                + $"not {CanvasSpacingKeys.Length} under \"DlgGroupTextEditing\".");
            OpenShortcutEditor();
            shortcutEditorProbe!.Record("canvas:Spacing.TrackingIncrease.1", VirtualKey.Q, VirtualKeyModifiers.Menu);
            CloseShortcutEditor(commit: true);
            Require(await WaitUntilAsync(() => CanvasChord(shortcutChords, "canvas:Spacing.TrackingIncrease.1")
                == new ShortcutChord(VirtualKey.Q, VirtualKeyModifiers.Menu), 2000), "the rebound spacing chord never reached the live set.");
            Require(TrySpacingChord(VirtualKey.Q, option: true, shift: false, out _, out bool movedIncrease, out double movedStep)
                    && movedIncrease && Math.Abs(movedStep - 1) < 1e-9,
                "the key the user bound to \"increase tracking\" does not increase tracking.");
            Require(!TrySpacingChord(VirtualKey.Right, option: true, shift: false, out _, out _, out _),
                "the arrow whose spacing command moved still answers as spacing.");
            RestoreCanvasChords();
            Require(TrySpacingChord(VirtualKey.Right, option: true, shift: false, out _, out bool backAgain, out _) && backAgain,
                "restoring the built-in spacing chords did not put Option+Right back to work.");
            report["spacingChordsRebindWithTheRest"] = "passed";

            // The brush tip brackets as one seam: the same chord in, the same slider out.
            var sizeBefore = BrushSizeSlider.Value;
            var hardBefore = BrushHardnessSlider.Value;
            Require(StepBrushTip((VirtualKey)OpenBracketCode, 0) && BrushSizeSlider.Value < sizeBefore
                && BrushHardnessSlider.Value == hardBefore, "the plain bracket no longer steps the size alone.");
            Require(StepBrushTip((VirtualKey)OpenBracketCode, Windows.System.VirtualKeyModifiers.Menu)
                && BrushHardnessSlider.Value < hardBefore, "Alt no longer steps the hardness.");
            Require(!StepBrushTip(VirtualKey.O, 0), "a key that is not a bracket stepped the brush anyway.");
            // Translating before judging is what a rebound bracket needs: the tail used to read the raw key, so
            // moving one onto another key left it firing on the key the user no longer presses.
            OpenShortcutEditor();
            shortcutEditorProbe!.Record("canvas:BrushSizeDecrease", VirtualKey.O, 0);
            CloseShortcutEditor(commit: true);
            var (movedTip, movedLost) = CanvasTranslate(new ShortcutChord(VirtualKey.O, 0));
            Require(!movedLost && (int)movedTip.Key == OpenBracketCode,
                $"the letter bound to the size bracket does not arrive as that bracket ({movedTip.Label}, {movedLost}).");
            var shrinksFrom = BrushSizeSlider.Value;
            Require(StepBrushTip(movedTip.Key, movedTip.Modifiers) && BrushSizeSlider.Value < shrinksFrom,
                "the translated bracket chord no longer steps the size it was bound to.");
            RestoreCanvasChords();
            foreach (var row in CanvasBracketKeys)
            {
                var declared = new ShortcutChord(row.Key, row.Default);
                Require(CanvasTranslate(declared) is (var own, false) && own == declared,
                    $"{row.Id} no longer answers to its own chord ({CanvasTranslate(declared).Chord.Label}).");
            }

            var listed = new List<ShortcutForm.Row>();
            OpenShortcutEditor();
            listed.AddRange(shortcutEditorProbe!.Rows());
            CloseShortcutEditor(commit: false);
            // Every chord the app owns is now offerable, so a row that is merely listed for information would
            // mean the sheet had quietly grown a second, read-only source again.
            Require(listed.Count == CanvasCommands().Count + RemappableShortcuts().Count
                    && listed.All(row => row.Remappable),
                "the shortcut sheet lists a row it does not offer, or a different number of keys than the two tables own.");
            // A letter's boundary can see Shift and nothing else, so Alt is refused the same way Ctrl is.
            bool refusedAlt = false;
            OpenShortcutEditor();
            shortcutEditorProbe!.Record("canvas:Tool_Text", Windows.System.VirtualKey.K, Windows.System.VirtualKeyModifiers.Menu);
            refusedAlt = !ShortcutSaveEnabled();
            CloseShortcutEditor(commit: true);
            Require(refusedAlt, "the editor let a tool letter take Alt, which its guard hands to the menus.");
            // The bracket boundary reads Alt (size versus hardness) and nothing else, and the palette chords
            // are rewritten on the accelerator object that carries the handler — both are checked where they
            // actually land, not in the draft.
            bool refusedBracketModifier = false;
            OpenShortcutEditor();
            shortcutEditorProbe!.Record("canvas:BrushSizeDecrease", (VirtualKey)OpenBracketCode,
                Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Menu);
            refusedBracketModifier = !ShortcutSaveEnabled();
            CloseShortcutEditor(commit: true);
            Require(refusedBracketModifier, "the editor let a bracket take Control, which the layer step owns.");
            var swappedFrom = SwapColorsAccelerator.Key;
            OpenShortcutEditor();
            shortcutEditorProbe!.Record("palette:SwapColors", VirtualKey.K,
                Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Menu);
            Require(SwapColorsAccelerator.Key == swappedFrom, "recording the palette chord rewrote the accelerator before the save.");
            CloseShortcutEditor(commit: true);
            Require(await WaitUntilAsync(() => SwapColorsAccelerator.Key == VirtualKey.K
                && SwapColorsAccelerator.Modifiers == (Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Menu), 2000),
                $"saving did not move the palette chord off {swappedFrom} (now {SwapColorsAccelerator.Key}).");
            Require(Microsoft.UI.Xaml.Controls.ToolTipService.GetToolTip(SwapColorsChip).ToString()!.Contains("Ctrl+Alt+K"),
                $"the swap chip still promises another key: {Microsoft.UI.Xaml.Controls.ToolTipService.GetToolTip(SwapColorsChip)}.");
            // The stored chord is the same object the framework resolves, so the file, the accelerator and the
            // chip have to agree — the alternative would be a label that promises a chord nothing fires.
            Require(CanvasChord(shortcutChords, "palette:SwapColors")
                        == new ShortcutChord(VirtualKey.K, Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Menu),
                    "the saved palette chord is not the one the accelerator now carries.");
            OpenShortcutEditor();
            shortcutEditorProbe!.RestoreDefaults();
            CloseShortcutEditor(commit: true);
            Require(await WaitUntilAsync(() => SwapColorsAccelerator.Key == swappedFrom, 2000),
                "Restore Defaults left the palette accelerator on the rebound key.");
            Require(File.ReadAllText(Path.Combine(preferencesScratch, ShortcutsPreference + ".txt")).Contains("canvas:Tool_Text=") == false,
                "a refused Alt chord was still stored.");
            foreach (var entry in CanvasToolKeys)
            {
                var (key, swallowed) = CanvasKeyFor(entry.Key, false);
                Require(!swallowed && key == entry.Key, $"{entry.Id} no longer answers to its own letter ({key}, {swallowed}).");
            }
            // Then move Brush off B. The sheet drives it; nothing here synthesizes a key event.
            bool canvasListed = false;
            OpenShortcutEditor();
            canvasListed = shortcutEditorProbe!.Rows().Any(row => row.Id == "canvas:Tool_Brush" && row.Remappable);
            shortcutEditorProbe!.Record("canvas:Tool_Brush", Windows.System.VirtualKey.N, 0);
            CloseShortcutEditor(commit: true);
            Require(canvasListed, "the editor does not list the canvas tool letters as remappable.");
            Require(CanvasKeyFor(Windows.System.VirtualKey.N, false) is (Windows.System.VirtualKey.B, false),
                "the letter the user bound to Brush never reaches the Brush branch.");
            Require(ToolForPress(CanvasKeyFor(Windows.System.VirtualKey.N, false).Key, false) == CanvasTool.Brush,
                "the translated letter does not route to the brush.");
            Require(CanvasKeyFor(Windows.System.VirtualKey.B, false) is (_, true),
                "the old B still paints whatever tool sits at B instead of being swallowed.");
            Require(File.ReadAllText(Path.Combine(preferencesScratch, ShortcutsPreference + ".txt"))
                .Contains("canvas:Tool_Brush="), "a canvas rebind did not reach the preference file.");
            // A canvas chord carrying Ctrl can never arrive (the guard hands Ctrl to the menus), so the sheet
            // must refuse it rather than store a binding that does nothing.
            bool refusedCtrl = false;
            OpenShortcutEditor();
            shortcutEditorProbe!.Record("canvas:Tool_Eraser", Windows.System.VirtualKey.K,
                Windows.System.VirtualKeyModifiers.Control);
            refusedCtrl = !ShortcutSaveEnabled();
            CloseShortcutEditor(commit: true);
            Require(refusedCtrl, "the editor let a canvas key take Ctrl.");
            Require(File.ReadAllText(Path.Combine(preferencesScratch, ShortcutsPreference + ".txt"))
                .Contains("canvas:Tool_Eraser=") == false, "a refused canvas chord was still stored.");
            // The two boundaries are validated as one set: taking a letter a menu already answers to is a
            // conflict even though the two matchers are different code.
            var shiftedMenu = RemappableShortcuts().FirstOrDefault(row => row.Item.KeyboardAccelerators.Any(each =>
                each.Modifiers == Windows.System.VirtualKeyModifiers.Shift));
            Require(shiftedMenu.Id is not null, "no menu command is bound to a Shift-only chord, so this claim cannot be tested.");
            var stolen = shiftedMenu.Item.KeyboardAccelerators.First(each =>
                each.Modifiers == Windows.System.VirtualKeyModifiers.Shift);
            bool refusedSteal = false;
            OpenShortcutEditor();
            shortcutEditorProbe!.Record("canvas:Tool_Crop", stolen.Key, stolen.Modifiers);
            refusedSteal = !ShortcutSaveEnabled();
            CloseShortcutEditor(commit: true);
            Require(refusedSteal, $"a canvas key was allowed to take {ChordLabel(stolen)} away from {shiftedMenu.Title}.");
            // Reload from the file the way the next launch does, and only the canvas half comes back.
            Reset(shiftedMenu, shiftedMenu.Item.KeyboardAccelerators
                .Select(each => new ShortcutChord(each.Key, each.Modifiers)).ToList());
            shortcutChords.Clear(); shortcutApplied.Clear();
            LoadAndApplyShortcuts();
            Require(CanvasKeyFor(Windows.System.VirtualKey.N, false) is (Windows.System.VirtualKey.B, false),
                "the reloaded canvas chord is not the one that was saved.");
            Require(Bound(boundRow) == builtIn[0] && Bound(otherRow) == otherBuiltIn[0],
                "the reload kept menu overrides that the last save had refused.");
            report["canvasToolKeysTranslateCollideAndReload"] = "passed";

            // #39's last boundary: the keys that finish or throw away whatever the canvas is in the middle of.
            // Their matchers read the key alone, so these rows are the ones the editor can only ever offer a
            // bare key for — and "the old key means nothing now" has to be decided per key rather than by
            // marking the event handled, because Escape still closes a menu and Space still presses whatever
            // control has focus. The pan is checked in both directions: a release bound to the old key is how
            // a rebound Space leaves the hand cursor latched with nobody holding it.
            bool structuralOffered = false, structuralModifierRefused = false;
            string structuralRefusal = "";
            OpenShortcutEditor();
            structuralOffered = CanvasStructuralKeys.All(entry => shortcutEditorProbe!.Rows()
                .Any(row => row.Id == entry.Id && row.Remappable));
            shortcutEditorProbe!.Record("canvas:ApplyOperation", Windows.System.VirtualKey.Enter,
                Windows.System.VirtualKeyModifiers.Control);
            structuralModifierRefused = !ShortcutSaveEnabled();
            structuralRefusal = shortcutEditorProbe!.ProblemText;
            CloseShortcutEditor(commit: false);
            Require(structuralOffered, "the sheet does not offer the four structural canvas keys as remappable.");
            Require(structuralModifierRefused, $"the editor let a modifier onto a key matched without one: {structuralRefusal}");

            // Backspace is an alias of Delete rather than a row of its own — on the Mac they are one command
            // (KeyboardShortcuts.swift:104) — so it answers only while that row still sits on its built-in.
            Require(IsDeletePress(VirtualKey.Delete) && IsDeletePress(VirtualKey.Back),
                "Backspace and Delete did not both mean the row's command while it was still on its built-in chord.");
            Require(IsApplyPress(VirtualKey.Enter) && IsCancelPress(VirtualKey.Escape) && BeginTemporaryPan(VirtualKey.Space),
                "the structural rows did not answer to the keys they are declared with.");
            ReleaseTemporaryPan(VirtualKey.Space);
            Require(!spaceHeld, "releasing the built-in pan key did not clear hand mode.");

            bool structuralSaved = false; string structuralSaveProblem = "";
            OpenShortcutEditor();
            var structuralEditor = shortcutEditorProbe!;
            structuralEditor.Record("canvas:ApplyOperation", VirtualKey.O, 0);
            structuralEditor.Record("canvas:TemporaryPan", VirtualKey.P, 0);
            structuralEditor.Record("canvas:DeleteTarget", VirtualKey.Q, 0);
            structuralSaved = ShortcutSaveEnabled();
            structuralSaveProblem = structuralEditor.ProblemText;
            CloseShortcutEditor(commit: true);
            Require(structuralSaved, $"the sheet refused three bare-key rebinds of the structural rows: {structuralSaveProblem}");
            Require(IsApplyPress(VirtualKey.O) && !IsApplyPress(VirtualKey.Enter),
                "Enter still finishes an operation after Apply moved, or the key it moved to does nothing.");
            Require(!IsCancelPress(VirtualKey.O) && IsCancelPress(VirtualKey.Escape),
                "Cancel moved without being touched, or Escape kept cancelling.");
            Require(IsDeletePress(VirtualKey.Q) && !IsDeletePress(VirtualKey.Delete) && !IsDeletePress(VirtualKey.Back),
                "the old Delete key or its Backspace alias kept firing after the command moved.");
            var panState = spaceHeld;
            Require(!BeginTemporaryPan(VirtualKey.Space) && spaceHeld == panState,
                "Space started a pan after TemporaryPan moved, or a refused press changed hand mode.");
            Require(BeginTemporaryPan(VirtualKey.P) && spaceHeld, "the key TemporaryPan moved to did not start a pan.");
            ReleaseTemporaryPan(VirtualKey.Space);
            Require(spaceHeld, "releasing the old Space undid a pan the bound key had started.");
            ReleaseTemporaryPan(VirtualKey.P);
            Require(!spaceHeld, "releasing the bound key left the pan latched on the canvas.");
            Require(File.ReadAllText(Path.Combine(preferencesScratch, ShortcutsPreference + ".txt"))
                        .Contains("canvas:TemporaryPan="), "a structural rebind did not reach the preference file.");
            shortcutChords.Clear();
            LoadAndApplyShortcuts();
            Require(IsApplyPress(VirtualKey.O) && IsDeletePress(VirtualKey.Q)
                    && BeginTemporaryPan(VirtualKey.P),
                "the reloaded file did not put the structural chords back.");
            ReleaseTemporaryPan(VirtualKey.P);
            RestoreCanvasChords();
            Require(IsApplyPress(VirtualKey.Enter) && IsDeletePress(VirtualKey.Back) && IsDeletePress(VirtualKey.Delete)
                    && !IsApplyPress(VirtualKey.O),
                "Restore Defaults left a structural row on the rebound key.");
            report["canvasStructuralKeysMoveWithTheirMatchers"] = "passed";

            // The ten opacity digits are the last canvas keys the sheet could not offer. Both handlers that
            // read them take the bare digit and ignore every modifier, so these rows behave like the structural
            // four — and like them, moving one has to take the old key with it rather than leave a second key
            // still typing an opacity.
            bool digitsOffered = false, digitRefused = false, digitStealRefused = false;
            OpenShortcutEditor();
            var digitEditor = shortcutEditorProbe!;
            digitsOffered = CanvasOpacityDigits.All(row =>
                structuralEditor.Rows().Any(listed => listed.Id == row.Id && listed.Remappable));
            structuralEditor.Record("canvas:OpacityDigit.3", VirtualKey.Number3, Windows.System.VirtualKeyModifiers.Shift);
            digitRefused = !ShortcutSaveEnabled();
            var letter = CanvasToolKeys.First(entry => entry.Key == VirtualKey.B);   // Brush's own letter
            digitEditor.Record("canvas:OpacityDigit.7", letter.Key, 0);
            digitStealRefused = !ShortcutSaveEnabled();
            CloseShortcutEditor(commit: false);
            Require(digitsOffered, "the sheet does not offer the ten opacity digits.");
            Require(digitRefused, "the editor let an opacity digit take Shift, which its handler never reads.");
            Require(digitStealRefused, "an opacity digit was allowed to take a letter the brush already answers to.");

            OpenShortcutEditor();
            shortcutEditorProbe!.Record("canvas:OpacityDigit.5", VirtualKey.O, 0);
            CloseShortcutEditor(commit: true);
            Require(OpacityDigitPress(VirtualKey.O) == 5, "the key bound to opacity digit 5 never reaches the opacity entry.");
            Require(OpacityDigitPress(VirtualKey.Number5) is null, "the old 5 still types an opacity after its command moved.");
            Require(OpacityDigitPress(VirtualKey.Number6) == 6, "a digit nobody moved stopped answering.");
            Require(File.ReadAllText(Path.Combine(preferencesScratch, ShortcutsPreference + ".txt"))
                        .Contains("canvas:OpacityDigit.5="), "a digit rebind did not reach the preference file.");
            shortcutChords.Clear();
            LoadAndApplyShortcuts();
            Require(OpacityDigitPress(VirtualKey.O) == 5, "the reloaded file did not put the digit chord back.");
            RestoreCanvasChords();
            Require(OpacityDigitPress(VirtualKey.Number5) == 5 && OpacityDigitPress(VirtualKey.O) is null,
                "Restore Defaults left a digit on the rebound key.");
            report["canvasOpacityDigitsMoveWithTheirHandlers"] = "passed";

            /// <summary>The shortcut editor is non-modal (#37), so a probe opens it, drives it and answers it
            /// with the two buttons the user sees. Nothing is collected inside a callback any more: with the
            /// panel on screen while the probe runs, a claim can be thrown where it is discovered — which is
            /// exactly what the dialog's callback could not do, since that body ran inside an async void the
            /// probe never awaited, and one RED round lost an assertion into the dispatcher because of it.</summary>
            void OpenShortcutEditor()
            {
                ShowShortcutEditor(this, new RoutedEventArgs());
                Require(FloatingPanelIsOpen("shortcuts"), "View ▸ Keyboard Shortcuts opened no panel.");
                Require(shortcutEditorProbe is not null, "the shortcut panel opened without an editor to drive.");
                Require(Equals(FloatingPanelButton("shortcuts", false)?.Content, Loc.T("Dlg_SheetCancel"))
                        && Equals(FloatingPanelButton("shortcuts", true)?.Content, Loc.T("Dlg_Save")),
                    "The shortcut editor does not offer Cancel / Save.");
                // C13e/C13h: walked through the panel's root while the sheet is on screen. This body is the
                // probe's own call stack, not a dialog callback, so the claim can be thrown where it is learned.
                RequireSurfaceControlsNamed(report, "shortcutEditorAccessibility", FloatingPanelRoot("shortcuts"));
                report["shortcutSheetControlsAreNamed"] = "passed";
            }

            void CloseShortcutEditor(bool commit) => CloseFloatingPanel("shortcuts", commit);

            bool ShortcutSaveEnabled() => FloatingPanelConfirmEnabled("shortcuts");

            void RestoreCanvasChords()
            {
                OpenShortcutEditor();
                shortcutEditorProbe!.RestoreDefaults();
                CloseShortcutEditor(commit: true);
                WaitUntilAsync(() => shortcutChords.Count == 0, 2000).GetAwaiter().GetResult();
            }

            static ShortcutChord Bound((string Id, string Title, string Group, MenuFlyoutItemBase Item) row) =>
                row.Item.KeyboardAccelerators.Count == 0 ? ShortcutChord.Empty
                    : new ShortcutChord(row.Item.KeyboardAccelerators[0].Key, row.Item.KeyboardAccelerators[0].Modifiers);

            static void Reset((string Id, string Title, string Group, MenuFlyoutItemBase Item) row, List<ShortcutChord> chords)
            {
                row.Item.KeyboardAccelerators.Clear();
                foreach (var chord in chords)
                    row.Item.KeyboardAccelerators.Add(new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = chord.Key, Modifiers = chord.Modifiers });
            }

            ShortcutChord FreeChord()
            {
                var used = RemappableShortcuts().SelectMany(row => row.Item.KeyboardAccelerators.Select(each => new ShortcutChord(each.Key, each.Modifiers))).ToHashSet();
                foreach (var candidate in new[]
                {
                    new ShortcutChord(Windows.System.VirtualKey.K, Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Menu),
                    new ShortcutChord(Windows.System.VirtualKey.K, Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Menu | Windows.System.VirtualKeyModifiers.Shift),
                    new ShortcutChord(Windows.System.VirtualKey.F9, Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Menu),
                })
                    if (!used.Contains(candidate)) return candidate;
                throw new InvalidOperationException("The fixture found no free chord to bind.");
            }

            // Guides are targets for each other (Guides.swift:293-295), and a guide being dragged is never a
            // target for itself: with the bounds and the layers switched off, the only value within reach of
            // this pointer is the other guide's 100, so a self-targeting bug lands the drag back on the
            // pointer's own 104.6 instead.
            SnapToGuidesToggle.IsChecked = true; OnToggleSnapToGuides(SnapToGuidesToggle, new RoutedEventArgs());
            SnapToBoundsToggle.IsChecked = false; OnToggleSnapToBounds(SnapToBoundsToggle, new RoutedEventArgs());
            SnapToLayersToggle.IsChecked = false; OnToggleSnapToLayers(SnapToLayersToggle, new RoutedEventArgs());
            var anchorGuide = scene.Snapshot.Manifest.Guides!.First(guide => guide.Id != rawGuide.Id);
            Require(Math.Abs(anchorGuide.Position - guideMidline) < .01, $"the second guide drifted off the midline ({anchorGuide.Position:R}).");
            var towardOtherGuide = StageFor(guideMidline + guideOffset);
            BeginExistingGuideAt(scene.Snapshot.Manifest.Guides!.First(guide => guide.Id == rawGuide.Id), towardOtherGuide);
            MoveRulerGuideTo(towardOtherGuide); EndRulerGuideAt(towardOtherGuide);
            Require(scene.Snapshot.Manifest.Guides!.First(guide => guide.Id == rawGuide.Id).Position == anchorGuide.Position,
                "dragging a guide onto another guide did not park it on that guide.");
            // And with every Snap To kind off, the same pointer commits exactly where it points.
            SnapToGuidesToggle.IsChecked = false; OnToggleSnapToGuides(SnapToGuidesToggle, new RoutedEventArgs());
            BeginExistingGuideAt(scene.Snapshot.Manifest.Guides!.First(guide => guide.Id == rawGuide.Id), towardOtherGuide);
            MoveRulerGuideTo(towardOtherGuide); EndRulerGuideAt(towardOtherGuide);
            Near(guideMidline + guideOffset, scene.Snapshot.Manifest.Guides!.First(guide => guide.Id == rawGuide.Id).Position,
                "Guide drag with every Snap To kind off");
            SnapToGuidesToggle.IsChecked = true; SnapToBoundsToggle.IsChecked = true; SnapToLayersToggle.IsChecked = true;
            OnToggleSnapToGuides(SnapToGuidesToggle, new RoutedEventArgs());
            OnToggleSnapToBounds(SnapToBoundsToggle, new RoutedEventArgs());
            OnToggleSnapToLayers(SnapToLayersToggle, new RoutedEventArgs());
            report["guideDragSnapsToAnotherGuideNotItself"] = "passed";

            // This group committed five guide steps of its own (new guide, move, move, move, move). The
            // closing cleanup below counts on the history ending where this group started, so they are taken
            // back here — and the state is checked on the way, because a group that silently changed the
            // depth of the history is exactly how the next assertion starts lying.
            for (int step = 0; step < 5; step++) scene.Undo();
            Require(scene.Snapshot.Manifest.Guides is { Count: 1 } restored
                && restored[0].Id == rawGuide.Id && Math.Abs(restored[0].Position - expectedPosition) < .01,
                "undoing the guide snap group did not hand back the guide state it started with.");

            // C10d: the grid's own lines are drag targets, and only while both switches say so (the Mac feeds
            // snapToGrid into the list only when the grid is shown, Guides.swift:274-276). The other three
            // kinds are switched off first, so this can only be the grid: the default layout grid steps every
            // 8 pixels down a 200-pixel canvas, and the pointer sits well under half of that off 104, so no
            // other line — nor anything else — is nearer.
            SnapToGuidesToggle.IsChecked = false; OnToggleSnapToGuides(SnapToGuidesToggle, new RoutedEventArgs());
            SnapToBoundsToggle.IsChecked = false; OnToggleSnapToBounds(SnapToBoundsToggle, new RoutedEventArgs());
            SnapToLayersToggle.IsChecked = false; OnToggleSnapToLayers(SnapToLayersToggle, new RoutedEventArgs());
            GridToggle.IsChecked = true; OnToggleShowGrid(GridToggle, new RoutedEventArgs());
            SnapToGridToggle.IsChecked = true; OnToggleSnapToGrid(SnapToGridToggle, new RoutedEventArgs());
            double gridLine = 104;
            double gridOffset = Math.Min(guideReach / 4, 2);
            Require(gridOffset > .01 && gridOffset < guideReach, $"the grid snap fixture has nothing to snap within (reach {guideReach:R}).");
            var gridPointer = StageFor(gridLine + gridOffset);
            BeginExistingGuideAt(scene.Snapshot.Manifest.Guides!.First(guide => guide.Id == rawGuide.Id), gridPointer);
            MoveRulerGuideTo(gridPointer); EndRulerGuideAt(gridPointer);
            Require(scene.Snapshot.Manifest.Guides!.First(guide => guide.Id == rawGuide.Id).Position == gridLine,
                $"dragging a guide {gridOffset:R} off grid line {gridLine} (reach {guideReach:R}) did not park it on the grid.");
            // With Snap To ▸ Grid off the same pointer lands where it points, and so it does with the grid
            // shown but hidden: a switch that cannot tell the difference is a label, not a gate.
            SnapToGridToggle.IsChecked = false; OnToggleSnapToGrid(SnapToGridToggle, new RoutedEventArgs());
            BeginExistingGuideAt(scene.Snapshot.Manifest.Guides!.First(guide => guide.Id == rawGuide.Id), gridPointer);
            MoveRulerGuideTo(gridPointer); EndRulerGuideAt(gridPointer);
            Near(gridLine + gridOffset, scene.Snapshot.Manifest.Guides!.First(guide => guide.Id == rawGuide.Id).Position,
                "Guide drag with Snap To ▸ Grid off");
            // A hidden grid is not a target either. This drag aims at the next grid line along, and the
            // pointer has to move the guide: a drag that lands where it already sits commits no step, and the
            // undo count below would then be a lie.
            SnapToGridToggle.IsChecked = true; OnToggleSnapToGrid(SnapToGridToggle, new RoutedEventArgs());
            GridToggle.IsChecked = false; OnToggleShowGrid(GridToggle, new RoutedEventArgs());
            var hiddenPointer = StageFor(120 + gridOffset);
            BeginExistingGuideAt(scene.Snapshot.Manifest.Guides!.First(guide => guide.Id == rawGuide.Id), hiddenPointer);
            MoveRulerGuideTo(hiddenPointer); EndRulerGuideAt(hiddenPointer);
            Near(120 + gridOffset, scene.Snapshot.Manifest.Guides!.First(guide => guide.Id == rawGuide.Id).Position,
                "Guide drag with the grid hidden");
            // Back to where this group started: grid off as it ships, the three other kinds on again, and the
            // three committed drags undone so the captures below see the same state as the group before them.
            SnapToGridToggle.IsChecked = false; OnToggleSnapToGrid(SnapToGridToggle, new RoutedEventArgs());
            SnapToGuidesToggle.IsChecked = true; OnToggleSnapToGuides(SnapToGuidesToggle, new RoutedEventArgs());
            SnapToBoundsToggle.IsChecked = true; OnToggleSnapToBounds(SnapToBoundsToggle, new RoutedEventArgs());
            SnapToLayersToggle.IsChecked = true; OnToggleSnapToLayers(SnapToLayersToggle, new RoutedEventArgs());
            for (int step = 0; step < 3; step++) scene.Undo();
            Require(Math.Abs(scene.Snapshot.Manifest.Guides!.First(guide => guide.Id == rawGuide.Id).Position - expectedPosition) < .01,
                "undoing the grid snap group did not hand back the guide state it started with.");
            report["guideDragSnapsToTheLayoutGrid"] = "passed";

            await SaveRulerCaptureAsync(true, "horizontal-ruler.png"); await SaveRulerCaptureAsync(false, "vertical-ruler.png");
            using (var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), (float)viewport.Width, (float)viewport.Height, (float)(96 * viewport.BackingScale)))
            {
                using (var drawing = target.CreateDrawingSession()) { drawing.Clear(Microsoft.UI.ColorHelper.FromArgb(255, 27, 27, 27)); DrawViewportContent(drawing); }
                using var output = File.Create(Path.Combine(AppContext.BaseDirectory, "navigation-viewport.png"));
                await target.SaveAsync(output.AsRandomAccessStream(), CanvasBitmapFileFormat.Png);
            }
            await SaveWorkspaceCaptureAsync("navigation-workspace.png");
            scene.Undo(); scene.Undo(); Require(scene.Snapshot.Manifest.Layers.Count == 1, "Navigation inserted an undo step before the image import.");
            await VerifyClosedRenderWorkerAsync(); report["windowCloseDuringRender"] = "passed";
            report["status"] = "passed";
            static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
            static void Near(double expected, double actual, string label) { if (Math.Abs(expected - actual) > .01) throw new InvalidOperationException($"{label}: expected {expected}, actual {actual}."); }
            static Microsoft.UI.Xaml.Controls.TextBox? FindTextInput(DependencyObject root)
            {
                if (root is Microsoft.UI.Xaml.Controls.TextBox field) return field;
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                    if (FindTextInput(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
                return null;
            }
        }
        catch (Exception ex) { report["status"] = "failed"; report["error"] = ex.ToString(); }
        finally
        {
            Environment.SetEnvironmentVariable("COMPOSITOR_PREFERENCES_DIRECTORY", preferencesPrevious);
            try { if (Directory.Exists(preferencesScratch)) Directory.Delete(preferencesScratch, recursive: true); }
            catch (Exception ex) { App.Log($"Navigation probe preference scratch cleanup failed: {ex.Message}"); }
            WriteProbeReport("navigation-probe.json", report);
            if (!windowClosed) { forceClose = true; Close(); }
        }
    }

    private async Task VerifyClosedRenderWorkerAsync()
    {
        await WaitForFramesAsync();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var anchorWindow = new Window { Title = "Render teardown probe", Content = new Microsoft.UI.Xaml.Controls.TextBlock { Text = "Checking render cancellation" } };
        anchorWindow.AppWindow.Resize(new SizeInt32(260, 120)); anchorWindow.Activate();
        try
        {
            int failures = renderWorkerFailureCount;
            beforeFrameRender = () => { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Held render did not resume."); };
            RenderCanvas(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var work = currentRenderWork ?? throw new InvalidOperationException("Held render task was not recorded.");
            beforeFrameRender = null; forceClose = true; Close(); release.Set();
            await work.WaitAsync(TimeSpan.FromSeconds(20)); await Task.Delay(50);
            if (renderWorkerFailureCount != failures) throw new InvalidOperationException("A worker accessed the closed canvas.");
        }
        finally { beforeFrameRender = null; release.Set(); anchorWindow.Close(); }
    }

    private async Task SaveRulerCaptureAsync(bool horizontal, string name)
    {
        int width = horizontal ? Math.Max(1, (int)viewport.Width) : 18, height = horizontal ? 18 : Math.Max(1, (int)viewport.Height);
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, (float)(96 * viewport.BackingScale));
        using (var drawing = target.CreateDrawingSession())
        { drawing.Clear(Microsoft.UI.ColorHelper.FromArgb(255, 51, 51, 51)); DrawRulerContent(drawing, horizontal, horizontal ? width : height); }
        using var output = File.Create(Path.Combine(AppContext.BaseDirectory, name));
        await target.SaveAsync(output.AsRandomAccessStream(), CanvasBitmapFileFormat.Png);
    }
}
#endif
