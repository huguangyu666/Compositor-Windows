#if DEBUG
using System.Text;
using Compositor.Core;
using Microsoft.UI.Xaml;

namespace Compositor.App;

public sealed partial class MainWindow
{
    /// <summary>
    /// C03a: the two inspector switches have to survive a restart. The probe points the preferences
    /// folder at a scratch directory with a process-scoped variable, so the real user settings are
    /// neither read nor written, then checks the round trip both ways: click → file, and file → state
    /// after wiping what the session holds. A load path that only ever agreed with memory would pass
    /// any number of checks built the other way round.
    /// </summary>
    private async Task VerifyTransformOptionPersistenceAsync(Dictionary<string, object> report)
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"compositor-preferences-probe-{Environment.ProcessId}");
        var previous = Environment.GetEnvironmentVariable("COMPOSITOR_PREFERENCES_DIRECTORY");
        Directory.CreateDirectory(scratch);
        Environment.SetEnvironmentVariable("COMPOSITOR_PREFERENCES_DIRECTORY", scratch);
        try
        {
            transformAutoCheck.IsChecked = true;
            OnTransformAutoSelectClick(this, new RoutedEventArgs());
            transformShowCheck.IsChecked = false;
            OnTransformShowControlsClick(this, new RoutedEventArgs());
            Require(ReadPreferenceFile(scratch, "transform-auto-select") == "true", "ticking Auto Select did not persist true");
            Require(ReadPreferenceFile(scratch, "transform-show-controls") == "false", "unticking Show Controls did not persist false");
            report["transformOptionClicksWritePreferences"] = "passed";

            // Wipe what the session holds — field and checkbox both — then load: only the files can
            // put these values back, so this fails if ApplyPersistedTransformOptions reads memory.
            transformAutoSelect = false;
            showTransformControls = true;
            transformAutoCheck.IsChecked = false;
            transformShowCheck.IsChecked = true;
            ApplyPersistedTransformOptions();
            Require(transformAutoSelect, "Auto Select did not come from disk");
            Require(!showTransformControls, "Show Controls did not come from disk");
            Require(transformAutoCheck.IsChecked == true && transformShowCheck.IsChecked == false,
                "the checkboxes were not realigned with the persisted state");
            report["transformOptionsReloadFromDiskNotFromMemory"] = "passed";

            // A file that is not a flag is a first launch, not a crash: the running state stays.
            File.WriteAllText(Path.Combine(scratch, "transform-auto-select.txt"), "maybe");
            transformAutoSelect = true;
            ApplyPersistedTransformOptions();
            Require(transformAutoSelect, "a malformed preference overwrote the running state");
            Require(ReadPreferenceFile(scratch, "transform-show-controls") == "false", "the other flag vanished");
            report["malformedPreferenceFallsBackWithoutCrashing"] = "passed";

            // Ctrl+H reaches the editor through ApplyShowTransformControls, the same method the
            // checkbox handler calls; the key matching in front of it is not covered here.
            ApplyShowTransformControls(true);
            Require(showTransformControls && transformShowCheck.IsChecked == true
                && ReadPreferenceFile(scratch, "transform-show-controls") == "true",
                "the shared Show Controls path did not update state, checkbox and file together");
            report["showControlsPathKeepsCheckboxAndPreferenceAligned"] = "passed";
        }
        finally
        {
            Environment.SetEnvironmentVariable("COMPOSITOR_PREFERENCES_DIRECTORY", previous);
            try { Directory.Delete(scratch, recursive: true); }
            catch (Exception ex) { App.Log($"Preference probe cleanup failed for {scratch}: {ex.Message}"); }
        }
        await Task.CompletedTask;
    }

    /// <summary>
    /// C10d: View ▸ Grid and Snap To ▸ Grid. The switch is checked where it has to bite — the drag's target
    /// list, the file on disk, and actual pixels in the viewport — not as a bool that agrees with itself.
    /// The pixel expectations are hand-computed on a 128×96 document: the default grid is a major at 0, 64
    /// and 128 with subdivisions every 8 pixels, so (64, 48) sits on a major, (8, 44) on a subdivision column
    /// with no horizontal line within nine device pixels, and (4, 52) is the point with no line of either
    /// kind through it.
    /// </summary>
    private async Task VerifyGridSwitchAsync(Dictionary<string, object> report)
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"compositor-grid-preferences-probe-{Environment.ProcessId}");
        var previous = Environment.GetEnvironmentVariable("COMPOSITOR_PREFERENCES_DIRECTORY");
        var wasGrid = showsGrid; var wasSnapToGrid = snapToGrid; var wasZoom = viewport.Zoom;
        var wasPixelGrid = showPixelGrid;
        Directory.CreateDirectory(scratch);
        Environment.SetEnvironmentVariable("COMPOSITOR_PREFERENCES_DIRECTORY", scratch);
        var scene = EditorDocument.Create(128, 96);
        OpenDocument(scene);
        await WaitForFramesAsync();
        try
        {
            Require(!showsGrid && !snapToGrid, "the grid shipped on, which the Mac says it does not");
            ToolCombo.SelectedIndex = (int)CanvasTool.Move;
            // A patch rather than a pixel: the render target is in device pixels (view points times the
            // backing scale) and a grid line is one of them wide, so exact-index arithmetic would miss on a
            // scaled display. The bare point uses the tightest patch, four document pixels from any line.
            byte[] Sample(int documentX, int documentY, int radius, int tallRadius = -1)
            {
                if (tallRadius < 0) tallRadius = radius;
                var view = viewport.ToView(new PointD(documentX, documentY));
                using var target = new Microsoft.Graphics.Canvas.CanvasRenderTarget(Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice(),
                    (float)viewport.Width, (float)viewport.Height, (float)(96 * viewport.BackingScale));
                using (var drawing = target.CreateDrawingSession())
                {
                    drawing.Clear(Microsoft.UI.ColorHelper.FromArgb(255, 27, 27, 27));
                    DrawViewportContent(drawing);
                }
                int x = Math.Max(0, (int)Math.Round(view.X * viewport.BackingScale) - radius);
                int y = Math.Max(0, (int)Math.Round(view.Y * viewport.BackingScale) - tallRadius);
                return target.GetPixelBytes(x, y, radius * 2 + 1, tallRadius * 2 + 1);
            }

            // A tall strip down a grid column, compared row by row from grid-off to grid-on by how much each
            // row moved rather than by equality: an antialiased hairline gap still differs by a unit or two,
            // so only a clear high/low alternation says a dash is there. A solid line is one flat level, so
            // every row lands on the high side and the map has no low rows at all.
            string RowProfile(byte[] on, byte[] off, int width)
            {
                int rows = on.Length / (width * 4);
                var delta = new int[rows];
                int max = 0;
                for (int row = 0; row < rows; row++)
                {
                    for (int column = 0; column < width; column++)
                    {
                        int start = (row * width + column) * 4;
                        for (int channel = 0; channel < 3; channel++)
                            delta[row] = Math.Max(delta[row], Math.Abs(on[start + channel] - off[start + channel]));
                    }
                    max = Math.Max(max, delta[row]);
                }
                if (max == 0) return new string('.', rows);
                var map = new StringBuilder();
                foreach (var d in delta) map.Append(d * 2 > max ? '#' : '.');
                return map.ToString();
            }

            // Both halves are rendered at the same zoom, because the document's own pixels and their
            // resampling differ otherwise — the only thing allowed to change is whether a line is drawn.
            // The sampled subdivision column is per zoom on purpose: at 30% the default one (document x=8)
            // sits 2.4 device pixels from the major line at x=0, so a difference there can be that major's
            // antialiasing instead of a subdivision, and the claim then depends on where the fractional
            // viewport lands. Document x=32 is still a subdivision column, and at that zoom it is 9.6 device
            // pixels from either major — far enough that only a subdivision can reach the patch.
            async Task<(bool Major, bool Sub, bool SubDotted, string Rows, bool BareOnLine)> DiffersWithGridAt(
                double zoom, int subColumn = 8, int subRow = 44)
            {
                SetViewportZoom(zoom); await WaitForFramesAsync();
                GridToggle.IsChecked = false; OnToggleShowGrid(GridToggle, new RoutedEventArgs()); await WaitForFramesAsync();
                var offMajor = Sample(64, 48, 2); var offSub = Sample(subColumn, subRow, 2); var offBare = Sample(4, 52, 1);
                var offStrip = Sample(subColumn, subRow, 1, 9);
                GridToggle.IsChecked = true; OnToggleShowGrid(GridToggle, new RoutedEventArgs()); await WaitForFramesAsync();
                var rows = RowProfile(Sample(subColumn, subRow, 1, 9), offStrip, 3);
                return (!Sample(64, 48, 2).AsSpan().SequenceEqual(offMajor),
                    !Sample(subColumn, subRow, 2).AsSpan().SequenceEqual(offSub),
                    rows.Contains('#') && rows.Contains('.'),
                    rows,
                    Sample(4, 52, 1).AsSpan().SequenceEqual(offBare));
            }

            // Zoomed in, where a bare point is twelve device pixels from any line, so the four claims below
            // are about the grid rather than about rounding.
            var drawn = await DiffersWithGridAt(2);
            report["gridSubdivisionRows"] = drawn.Rows;
            Require(drawn.Major, "View ▸ Grid on drew no major line at the document's own pixels");
            Require(drawn.Sub, "View ▸ Grid on drew no subdivision line");
            Require(drawn.SubDotted, "subdivisions drew as an unbroken line instead of the Mac's dotted one");
            Require(drawn.BareOnLine, "the grid drew where no line exists");

            // Zoomed out past the Mac's four-screen-point floor, subdivisions stop and majors stay
            // (TransformOverlay.swift:203-210). No bare-point claim here: at 30% the gap between two grid
            // lines is under two device pixels, so it would measure rounding, not the grid.
            // (32, 24) at this zoom: a subdivision column and row, 9.6 and 7.2 device pixels from the nearest
            // major — see the note on DiffersWithGridAt.
            var thinned = await DiffersWithGridAt(.3, 32, 24);
            Require(thinned.Major, "majors disappeared with the subdivisions");
            Require(!thinned.Sub, "subdivisions kept drawing under four screen points apart");
            SetViewportZoom(wasZoom); await WaitForFramesAsync();

            // Snap To ▸ Grid: the grid's own columns join the drag's targets only when both switches say so.
            CacheSnapTargets();
            Require(snapXs?.Contains(8d) != true, "grid lines snapped with Snap To ▸ Grid off");
            SnapToGridToggle.IsChecked = true; OnToggleSnapToGrid(SnapToGridToggle, new RoutedEventArgs());
            CacheSnapTargets();
            Require(snapXs?.Contains(8d) == true && snapXs.Contains(16d), "Snap To ▸ Grid on did not add the grid's columns");
            GridToggle.IsChecked = false; OnToggleShowGrid(GridToggle, new RoutedEventArgs());
            CacheSnapTargets();
            Require(snapXs?.Contains(8d) != true, "a hidden grid still snapped");
            GridToggle.IsChecked = true; OnToggleShowGrid(GridToggle, new RoutedEventArgs());
            Require(ReadPreferenceFile(scratch, "show-grid") == "true" && ReadPreferenceFile(scratch, "snap-to-grid") == "true",
                "the grid clicks did not land in their preference files");
            report["gridSwitchDrawsAndSnapsOnlyWhenAsked"] = "passed";

            // Forced to the opposite of the files, so only a real read can bring them back.
            File.WriteAllText(Path.Combine(scratch, "grid-spacing.txt"), "128");
            File.WriteAllText(Path.Combine(scratch, "grid-subdivisions.txt"), "4");
            showsGrid = false; snapToGrid = false; gridSpacing = 64; gridSubdivisions = 8;
            ApplyViewVisibilityOptions();
            Require(showsGrid && snapToGrid && gridSpacing == 128 && gridSubdivisions == 4 && LayoutGrid.Step == 32,
                "the grid switches and its numbers did not come back from disk");
            Require(GridToggle.IsChecked == true && SnapToGridToggle.IsChecked == true, "the grid menu ticks were not realigned");
            File.WriteAllText(Path.Combine(scratch, "grid-spacing.txt"), "not a number");
            gridSpacing = 96;
            ApplyViewVisibilityOptions();
            Require(gridSpacing == 96 && LayoutGrid.Spacing == 96, "a malformed grid number overwrote the running state");
            report["gridNumbersReloadFromDisk"] = "passed";

            // View ▸ Show Pixel Grid belongs to the person rather than to the document, which is what the Mac
            // says by keeping it with the other tool switches (EditorSession.swift:277, "pixelGrid"). The
            // claim runs through the menu handler and then through a reload forced to the opposite of the
            // file, so a flag set only in memory could not pass it.
            PixelGridToggle.IsChecked = true; OnShowPixelGrid(PixelGridToggle, new RoutedEventArgs());
            Require(ReadPreferenceFile(scratch, "show-pixel-grid") == "true",
                "the pixel grid click never reached its preference file");
            PixelGridToggle.IsChecked = false; OnShowPixelGrid(PixelGridToggle, new RoutedEventArgs());
            Require(ReadPreferenceFile(scratch, "show-pixel-grid") == "false",
                "turning the pixel grid off left the file saying true");
            File.WriteAllText(Path.Combine(scratch, "show-pixel-grid.txt"), "true");
            showPixelGrid = false;
            ApplyViewVisibilityOptions();
            Require(showPixelGrid && PixelGridToggle.IsChecked == true, "the pixel grid did not come back from disk");
            report["pixelGridSwitchReloadsFromDisk"] = "passed";
        }
        finally
        {
            Environment.SetEnvironmentVariable("COMPOSITOR_PREFERENCES_DIRECTORY", previous);
            showsGrid = wasGrid; snapToGrid = wasSnapToGrid;
            GridToggle.IsChecked = showsGrid; SnapToGridToggle.IsChecked = snapToGrid;
            showPixelGrid = wasPixelGrid; PixelGridToggle.IsChecked = showPixelGrid;
            snapXs = snapYs = null;
            SetViewportZoom(wasZoom);
            OpenDocument(EditorDocument.Create(64, 48));
            try { Directory.Delete(scratch, recursive: true); }
            catch (Exception ex) { App.Log($"Grid probe cleanup failed for {scratch}: {ex.Message}"); }
        }
    }

    private static string? ReadPreferenceFile(string directory, string name) =>
        File.Exists(Path.Combine(directory, name + ".txt")) ? File.ReadAllText(Path.Combine(directory, name + ".txt")).Trim() : null;

    /// <summary>
    /// C10a: View ▸ Guides / Snap / Lock Guides. Every check runs through the menu handler and then through
    /// what the switch is for — the drawn line, the guide pickup, the drag targets — because a flag that is
    /// only read back from memory would pass without ever reaching the canvas. Preferences point at a
    /// scratch folder for the duration, so the real user settings are neither read nor written.
    /// </summary>
    private async Task VerifyViewSwitchesAsync(Dictionary<string, object> report)
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"compositor-view-preferences-probe-{Environment.ProcessId}");
        var previous = Environment.GetEnvironmentVariable("COMPOSITOR_PREFERENCES_DIRECTORY");
        var wasGuides = showsGuides; var wasSnap = snapEnabled; var wasLock = locksGuides;
        var wasToGuides = snapToGuides; var wasToLayers = snapToLayers; var wasToBounds = snapToBounds;
        var wasTool = ToolCombo.SelectedIndex;
        Directory.CreateDirectory(scratch);
        Environment.SetEnvironmentVariable("COMPOSITOR_PREFERENCES_DIRECTORY", scratch);
        var scene = EditorDocument.Create(64, 48); OpenDocument(scene); await WaitForFramesAsync();
        ToolCombo.SelectedIndex = (int)CanvasTool.Move;
        var guide = scene.AddGuide(GuideAxis.Vertical, 20);
        RefreshGuides(); await WaitForFramesAsync();
        // How far a click may stray from the line, in document pixels, at the zoom this window is at.
        double reach = 6 / viewport.PointsPerPixel;
        try
        {
            Require(guideLines.Count == 1, "a guide on a fresh document drew no line, so there is nothing to hide");
            Require(HitGuide(new PointD(20, 8))?.Id == guide.Id, "the guide under the pointer was not picked");
            Require(HitGuide(new PointD(20 + reach * 5, 8)) is null, "a click far outside the guide still grabbed it");

            // Guides off takes the line down and stops the pickup. Lock Guides keeps the line — the Mac
            // gates drawing only on showsGuides (Guides.swift:157, :244) — and refuses to move it.
            GuidesToggle.IsChecked = false; OnToggleGuides(GuidesToggle, new RoutedEventArgs());
            Require(guideLines.Count == 0 && HitGuide(new PointD(20, 8)) is null,
                "Guides off left the line drawn or the hidden guide pickable");
            GuidesToggle.IsChecked = true; OnToggleGuides(GuidesToggle, new RoutedEventArgs());
            LockGuidesToggle.IsChecked = true; OnToggleLockGuides(LockGuidesToggle, new RoutedEventArgs());
            Require(guideLines.Count == 1 && HitGuide(new PointD(20, 8)) is null,
                "Lock Guides took the line down instead of only freezing it");
            BeginExistingGuideAt(guide, new PointD(20, 8));
            Require(draggingGuideId is null && rulerGuideAxis is null, "a locked guide started a drag anyway");
            report["guidesOffHidesTheLineAndLockFreezesThePickup"] = "passed";

            SnapToggle.IsChecked = false; OnToggleSnap(SnapToggle, new RoutedEventArgs());
            Require(ReadPreferenceFile(scratch, "show-guides") == "true"
                  && ReadPreferenceFile(scratch, "lock-guides") == "true"
                  && ReadPreferenceFile(scratch, "snap-enabled") == "false",
                "a view switch click did not land in its preference file");
            report["viewSwitchClicksWritePreferences"] = "passed";

            // Snap off empties the lists the drag reads, and rebuilding them produces nothing. A hidden
            // guide is not a target either (Guides.swift:278: "hidden extras do not snap").
            CacheSnapTargets();
            Require(snapXs is null && snapYs is null, "Snap off still built snap targets");
            LockGuidesToggle.IsChecked = false; OnToggleLockGuides(LockGuidesToggle, new RoutedEventArgs());
            SnapToggle.IsChecked = true; OnToggleSnap(SnapToggle, new RoutedEventArgs());
            CacheSnapTargets();
            Require(snapXs?.Contains(20) == true, "Snap on lost the guide as a target");
            GuidesToggle.IsChecked = false; OnToggleGuides(GuidesToggle, new RoutedEventArgs());
            CacheSnapTargets();
            Require(snapXs?.Contains(20) != true, "a hidden guide still snapped to");
            GuidesToggle.IsChecked = true; OnToggleGuides(GuidesToggle, new RoutedEventArgs());
            report["snapSwitchFeedsTheDragTargets"] = "passed";

            // Snap To (CompositorApp.swift:138-148 → Guides.swift:251-283): each sub-switch takes its own
            // block out of the list the drag reads. A second layer goes in first, because the selected layer
            // is not a target for itself — with one layer there would be no layer geometry to lose.
            var shape = scene.AddShapeLayer(new ShapeStyle { Kind = ShapeKind.Rectangle, Red = 1 }, 16, 12, new PointD(30, 10));
            selectedLayerId = scene.Snapshot.Manifest.Layers[0].Id;
            selectedLayerIds.Clear(); selectedLayerIds.Add(scene.Snapshot.Manifest.Layers[0].Id);
            RefreshLayerPanel(); await WaitForFramesAsync();
            CacheSnapTargets();
            Require(snapXs!.Contains(0) && snapXs.Contains(64) && snapXs.Contains(20)
                && snapXs.Contains(30) && snapXs.Contains(46),
                $"all three Snap To switches on still lost a target (xs = {string.Join(',', snapXs)})");
            SnapToBoundsToggle.IsChecked = false; OnToggleSnapToBounds(SnapToBoundsToggle, new RoutedEventArgs());
            Require(snapXs is null && snapYs is null, "Snap To off left a drag snapping to a stale list");
            CacheSnapTargets();
            Require(!snapXs!.Contains(0) && !snapXs.Contains(64) && !snapXs.Contains(32)
                && snapXs.Contains(20) && snapXs.Contains(30),
                "Snap To ▸ Document Bounds off did not remove exactly the bounds");
            SnapToBoundsToggle.IsChecked = true; OnToggleSnapToBounds(SnapToBoundsToggle, new RoutedEventArgs());
            SnapToLayersToggle.IsChecked = false; OnToggleSnapToLayers(SnapToLayersToggle, new RoutedEventArgs());
            CacheSnapTargets();
            Require(!snapXs!.Contains(30) && !snapXs.Contains(46) && snapXs.Contains(0) && snapXs.Contains(20),
                "Snap To ▸ Layers off did not remove exactly the layer edges");
            SnapToLayersToggle.IsChecked = true; OnToggleSnapToLayers(SnapToLayersToggle, new RoutedEventArgs());
            SnapToGuidesToggle.IsChecked = false; OnToggleSnapToGuides(SnapToGuidesToggle, new RoutedEventArgs());
            CacheSnapTargets();
            Require(!snapXs!.Contains(20) && snapXs.Contains(0) && snapXs.Contains(30),
                "Snap To ▸ Guides off did not remove exactly the guide");
            SnapToGuidesToggle.IsChecked = true; OnToggleSnapToGuides(SnapToGuidesToggle, new RoutedEventArgs());
            Require(ReadPreferenceFile(scratch, "snap-to-guides") == "true"
                && ReadPreferenceFile(scratch, "snap-to-layers") == "true"
                && ReadPreferenceFile(scratch, "snap-to-bounds") == "true",
                "a Snap To click did not land in its preference file");
            scene.DeleteLayer(shape.Id);
            report["snapToSwitchesChooseTheirOwnTargets"] = "passed";

            // A locked document refuses a new guide from the ruler; an unlocked one with guides hidden takes
            // the switch back on, because Mac beginGuideCreation would otherwise drag an invisible line
            // (Guides.swift:174-178).
            LockGuidesToggle.IsChecked = true; OnToggleLockGuides(LockGuidesToggle, new RoutedEventArgs());
            GuidesToggle.IsChecked = false; OnToggleGuides(GuidesToggle, new RoutedEventArgs());
            BeginRulerGuideAt(GuideAxis.Horizontal, new PointD(10, 10));
            Require(rulerGuideAxis is null, "a locked document created a guide from the ruler");
            int guidesWhileLocked = scene.Snapshot.Manifest.Guides?.Count ?? 0;
            OnAddVerticalGuide(this, new RoutedEventArgs());
            Require((scene.Snapshot.Manifest.Guides?.Count ?? 0) == guidesWhileLocked
                && StatusText.Text == Loc.T("S_GuidesLocked"),
                "the View menu added a guide to a locked document, or did so without saying it could not");
            LockGuidesToggle.IsChecked = false; OnToggleLockGuides(LockGuidesToggle, new RoutedEventArgs());
            BeginRulerGuideAt(GuideAxis.Horizontal, new PointD(10, 10));
            Require(rulerGuideAxis is not null, "the ruler did not start a guide draft");
            Require(showsGuides && GuidesToggle.IsChecked == true && ReadPreferenceFile(scratch, "show-guides") == "true",
                "creating a guide left View ▸ Guides off, so the draft was invisible");
            CancelNavigation();
            // The menu path turns guides on the same way (Guides.swift:236-242), instead of adding a guide
            // that the user then cannot find.
            GuidesToggle.IsChecked = false; OnToggleGuides(GuidesToggle, new RoutedEventArgs());
            int guidesBeforeMenu = scene.Snapshot.Manifest.Guides?.Count ?? 0;
            OnAddHorizontalGuide(this, new RoutedEventArgs());
            Require((scene.Snapshot.Manifest.Guides?.Count ?? 0) == guidesBeforeMenu + 1 && showsGuides
                && guideLines.Count == (scene.Snapshot.Manifest.Guides?.Count ?? 0),
                "Add Horizontal Guide with Guides off inserted an invisible line");
            report["rulerGuideCreationRestoresVisibility"] = "passed";

            // Forced to the opposite of the files, only a real disk read can put these back — a load path
            // that agreed with memory would pass nothing else.
            WriteFlag(GuidesPreference, true); WriteFlag(SnapPreference, false); WriteFlag(LockedGuidesPreference, true);
            WriteFlag(SnapToGuidesPreference, false); WriteFlag(SnapToLayersPreference, true); WriteFlag(SnapToBoundsPreference, false);
            showsGuides = false; snapEnabled = true; locksGuides = false;
            snapToGuides = true; snapToLayers = false; snapToBounds = true;
            GuidesToggle.IsChecked = false; SnapToggle.IsChecked = true; LockGuidesToggle.IsChecked = false;
            SnapToGuidesToggle.IsChecked = true; SnapToLayersToggle.IsChecked = false; SnapToBoundsToggle.IsChecked = true;
            ApplyViewVisibilityOptions();
            Require(showsGuides && !snapEnabled && locksGuides
                && !snapToGuides && snapToLayers && !snapToBounds,
                "the view switches came back from memory, not from disk");
            Require(GuidesToggle.IsChecked == true && SnapToggle.IsChecked == false && LockGuidesToggle.IsChecked == true
                && SnapToGuidesToggle.IsChecked == false && SnapToLayersToggle.IsChecked == true
                && SnapToBoundsToggle.IsChecked == false,
                "the menu ticks were not realigned with the persisted switches");
            report["viewSwitchesReloadFromDiskNotFromMemory"] = "passed";

            // A file that is not a flag is a first launch, not a crash: that switch keeps what the session
            // holds while the other two still load from their files.
            File.WriteAllText(Path.Combine(scratch, "snap-enabled.txt"), "maybe");
            snapEnabled = false;
            ApplyViewVisibilityOptions();
            Require(showsGuides && !snapEnabled && locksGuides,
                "a malformed snap preference overwrote the running state or the other switches");
            report["malformedViewPreferenceFallsBackWithoutCrashing"] = "passed";
        }
        finally
        {
            Environment.SetEnvironmentVariable("COMPOSITOR_PREFERENCES_DIRECTORY", previous);
            showsGuides = wasGuides; snapEnabled = wasSnap; locksGuides = wasLock;
            snapToGuides = wasToGuides; snapToLayers = wasToLayers; snapToBounds = wasToBounds;
            GuidesToggle.IsChecked = showsGuides; SnapToggle.IsChecked = snapEnabled; LockGuidesToggle.IsChecked = locksGuides;
            SnapToGuidesToggle.IsChecked = snapToGuides; SnapToLayersToggle.IsChecked = snapToLayers; SnapToBoundsToggle.IsChecked = snapToBounds;
            RefreshGuides();
            ToolCombo.SelectedIndex = wasTool;
            // The scene with its probe guide stays current on purpose: the next group opens its own document
            // on its first line, and handing one over here put enough refresh work on the dispatcher to make
            // a later focus-settle assertion in that group miss its 30 ms window.
            try { Directory.Delete(scratch, recursive: true); }
            catch (Exception ex) { App.Log($"Preference probe cleanup failed for {scratch}: {ex.Message}"); }
        }
        await Task.CompletedTask;
    }
}
#endif
