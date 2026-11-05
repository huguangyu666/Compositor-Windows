#if DEBUG
using System.Collections.Generic;
using System.Diagnostics;
using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Compositor.App;

public sealed partial class MainWindow
{
    /// <summary>
    /// C06: the four adjustment editors. Before this slice the panel answered with placeholder text
    /// for these kinds while Core already rendered every parameter, so the group's job is to prove
    /// the controls exist and that moving one reaches the document — a panel that builds zero rows
    /// would otherwise look identical to a working one in a test that only counts green checks.
    /// </summary>
    private async Task VerifyAdjustmentEditorsAsync(Dictionary<string, object> report)
    {
        var scene = EditorDocument.Create(64, 48);
        OpenDocument(scene); await WaitForFramesAsync();
        string placeholder = Loc.T("M_AdjustmentNeedsProject");
        adjustmentPanelOpenMs = 0;
        // C13d: the same reader's inventory asked where the controls actually exist. The window's strips are all
        // the transform scene can reach; every editor here is built on demand, so this is the first time any of
        // them is asked what it reports.
        var panelSweeps = new List<string>();
        var bareCount = 0;
        void Sweep(string editor)
        {
            var sweep = AccessibleControls(AdjustmentPanel);
            bareCount += sweep.Unnamed.Count;
            panelSweeps.Add($"{editor}: controls={sweep.Named + sweep.Unnamed.Count} unnamed={sweep.Unnamed.Count} "
                + $"missing=[{string.Join(", ", sweep.Unnamed)}]");
        }

        var gradientMap = scene.AddAdjustment(AdjustmentKind.GradientMap);
        var panel = await SelectAdjustmentAsync(gradientMap.Id);
        Require(panel.Children.Count == 3, $"Gradient Map built {panel.Children.Count} rows, expected 3 (two endpoints + reversed).");
        Require(panel.Children.All(child => !ContainsText(child, placeholder)), "Gradient Map still shows the placeholder.");
        Require(panel.Children[2] is CheckBox, "Gradient Map's third row is not the reversed switch.");
        ((CheckBox)panel.Children[2]).IsChecked = true;
        Require(scene.Snapshot.Manifest.Layers.Single(layer => layer.Id == gradientMap.Id)
            .Adjustment?.GradientMapSettings?.Reversed == true, "Ticking Reversed did not reach the document.");
        Sweep("gradientMap");
        report["adjustmentEditorGradientMap"] = "passed";

        var blackWhite = scene.AddAdjustment(AdjustmentKind.BlackWhite);
        panel = await SelectAdjustmentAsync(blackWhite.Id);
        Require(panel.Children.Count == 9, $"Black & White built {panel.Children.Count} rows, expected 9 (six weights + tint + hue + saturation).");
        Require(FirstSlider(panel.Children[0]).Value != 100, "Black & White's first slider did not carry its value.");
        FirstSlider(panel.Children[0]).Value = 100;
        Require(ReadAdjustment(blackWhite.Id)?.BlackWhiteSettings?.Reds == 100, "Dragging the Reds slider did not reach the document.");
        ((CheckBox)panel.Children[6]).IsChecked = true;
        Require(ReadAdjustment(blackWhite.Id)?.BlackWhiteSettings?.Tint == true, "Tint checkbox did not reach the document.");
        Sweep("blackWhite");
        report["adjustmentEditorBlackWhite"] = "passed";

        var colorBalance = scene.AddAdjustment(AdjustmentKind.ColorBalance);
        panel = await SelectAdjustmentAsync(colorBalance.Id);
        Require(panel.Children.Count == 10, $"Color Balance built {panel.Children.Count} rows, expected 10 (nine pairs + preserve luminosity).");
        FirstSlider(panel.Children[4]).Value = -30;
        Require(ReadAdjustment(colorBalance.Id)?.ColorBalanceSettings?.MidMagentaGreen == -30,
            "The midtones magenta/green slider did not reach the document.");
        ((CheckBox)panel.Children[9]).IsChecked = false;
        Require(ReadAdjustment(colorBalance.Id)?.ColorBalanceSettings?.PreserveLuminosity == false,
            "Preserve luminosity did not reach the document.");
        Sweep("colorBalance");
        report["adjustmentEditorColorBalance"] = "passed";

        var grain = scene.AddAdjustment(AdjustmentKind.Grain);
        panel = await SelectAdjustmentAsync(grain.Id);
        Require(panel.Children.Count == 4, $"Grain built {panel.Children.Count} rows, expected 4 (amount + size + roughness + seed).");
        FirstSlider(panel.Children[1]).Value = 7;
        Require(ReadAdjustment(grain.Id)?.GrainSettings?.Size == 7, "The grain size slider did not reach the document.");
        var seed = ((Grid)panel.Children[3]).Children.OfType<NumberBox>().Single();
        seed.Value = 4321;
        Require(ReadAdjustment(grain.Id)?.GrainSettings?.Seed == 4321, "The seed box did not reach the document.");
        Sweep("grain");
        report["adjustmentEditorGrain"] = "passed";
        report["adjustmentPanelOpenMs"] = adjustmentPanelOpenMs;
        report["panelAccessibility"] = string.Join(" | ", panelSweeps);
        // Asked as a rule now that every editor has been swept: a control that reports nothing is one a keyboard
        // and a reader cannot pair up, and the four editors are where the panel builders make the most of them.
        Require(bareCount == 0,
            $"the adjustment editors built {bareCount} controls that report no name to a reader: {string.Join(" | ", panelSweeps)}");
        report["adjustmentEditorsAreNamed"] = "passed";
    }

    /// <summary>Selects the adjustment layer so the panel rebuilds, and returns that panel body.</summary>
    private async Task<StackPanel> SelectAdjustmentAsync(Guid layerId)
    {
        RefreshLayerPanel();
        LayerList.SelectedItem = layerRows[layerId];
        // Selecting a layer finishes any live transform first and only then queues the work that rebuilds
        // the panel, so the panel is not open when the click returns. Waiting until the canvas is idle does
        // not close that gap either: with that hop delayed on purpose, this group read the previous layer's
        // body as the new one ("Black & White built 3 rows, expected 9"). So wait for the panel of this
        // layer, and keep how long it took — that number used to be guessed.
        var timer = Stopwatch.StartNew();
        // "A panel is open" is not what is being waited for: a click that has not landed yet leaves the
        // previous layer's editor on screen, which is exactly how a stale body gets read as a fresh one.
        // The title is what a user reads to know whose panel this is, so the wait keys on it.
        bool opened = await WaitUntilAsync(() => PanelShows(layerId), 2000);
        adjustmentPanelOpenMs = Math.Max(adjustmentPanelOpenMs, timer.Elapsed.TotalMilliseconds);
        await WaitForFramesAsync();
        // When this fires intermittently the useful fact is what the panel and the selection actually were,
        // not that the count was low: RebuildAdjustmentPanel clears the children and collapses the panel
        // whenever it believes the selected record carries no adjustment, so "cleared" and "never rebuilt"
        // look identical from the count alone.
        var selectedNow = (LayerList.SelectedItem as Microsoft.UI.Xaml.FrameworkElement)?.Tag is LayerRow row ? row.Layer.Id : Guid.Empty;
        Require(opened, $"Selecting an adjustment layer never opened the adjustment panel within 2s "
            + $"(wanted {layerId}, selected {selectedNow}, still the same layer: {selectedNow == layerId}, "
            + $"children {AdjustmentPanel.Children.Count}, visibility {AdjustmentPanel.Visibility}, "
            + $"selected record has adjustment: {document!.Snapshot.Manifest.Layers.Any(l => l.Id == layerId && l.Adjustment is not null)}, "
            + $"document is {document!.Snapshot.Manifest.Layers.Count} layers).");
        Require(AdjustmentPanel.Children.Count >= 2,
            $"Selecting an adjustment layer did not open the adjustment panel (wanted {layerId}, "
            + $"selected {selectedNow}, still the same layer: {selectedNow == layerId}, "
            + $"children {AdjustmentPanel.Children.Count}, visibility {AdjustmentPanel.Visibility}, "
            + $"selected record has adjustment: {document!.Snapshot.Manifest.Layers.Any(l => l.Id == layerId && l.Adjustment is not null)}, "
            + $"document is {document!.Snapshot.Manifest.Layers.Count} layers).");
        var panel = AdjustmentPanel.Children[^1] as StackPanel;
        Require(panel is not null, "The adjustment panel body is not a StackPanel.");
        return panel!;
    }

    /// <summary>Whether the panel on screen is the editor of this layer. The list's selection is only the
    /// click; the document's active layer and the panel's own title are what the rebuild left behind.</summary>
    private bool PanelShows(Guid layerId)
    {
        if (AdjustmentPanel.Visibility != Microsoft.UI.Xaml.Visibility.Visible || AdjustmentPanel.Children.Count < 2) return false;
        if (document?.Snapshot.Manifest.ActiveLayerId != layerId) return false;
        if (document!.Snapshot.Manifest.Layers.FirstOrDefault(layer => layer.Id == layerId)?.Adjustment is not { } adjustment) return false;
        return (AdjustmentPanel.Children[0] as TextBlock)?.Text
            == Loc.F("S_AdjustmentSuffix", Loc.Name("Adjustment", Enum.GetName(adjustment.Kind)!));
    }

    /// <summary>#37's second panel, and the first time the layer effects sheet was ever driven: it opens in the
    /// floating host now, so the claims are the ones the Mac's sheet makes — every change previews, Cancel hands
    /// the layer back what it opened on, and one session is one undo step — plus the two that only a non-modal
    /// panel has to get right: it leaves when its layer does, and it shares the window with another panel.</summary>
    private async Task VerifyEffectsPanelAsync(Dictionary<string, object> report)
    {
        var scene = EditorDocument.Create(64, 48);
        OpenDocument(scene); await WaitForFramesAsync();
        var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        string Effects() => scene.Snapshot.Manifest.Layers.Single(layer => layer.Id == id).Effects is { } raw
            ? raw.GetRawText() : "";
        var before = Effects();

        OnLayerEffects(this, new RoutedEventArgs());
        Require(FloatingPanelIsOpen("effects"), "the layer effects command opened no panel.");
        Require(Equals(FloatingPanelButton("effects", false)?.Content, Loc.T("Dlg_SheetCancel"))
                && Equals(FloatingPanelButton("effects", true)?.Content, Loc.T("Dlg_OK")),
            "The effects sheet does not offer Cancel / OK.");
        // C13e/C13h: the sheets live under the floating panel's own root, which is the part a sweep can walk —
        // the probe's field access goes through a wrapper object, not the tree.
        RequireSurfaceControlsNamed(report, "effectsPanelAccessibility", FloatingPanelRoot("effects"));
        report["effectsPanelControlsAreNamed"] = "passed";
        var body = (StackPanel)FloatingPanelBody("effects")!;
        Require(body.Children.Count == 3, $"the effects panel is missing its kind picker or its parameters ({body.Children.Count} blocks).");
        var switches = (Grid)body.Children[0];
        Require(switches.Children.Count == 6, $"the effects panel built {switches.Children.Count} switches, one per kind is 6.");
        Require(!scene.CanUndo, "opening the effects panel changed the document before anything was touched.");
        switches.Children.OfType<CheckBox>().First().IsChecked = true;
        await WaitForFramesAsync();
        Require(Effects() != before, "ticking an effect switch never reached the layer.");
        Require(scene.CanUndo, "the previewed effect is not a step the user can undo.");
        CloseFloatingPanel("effects", commit: false);
        await WaitForFramesAsync();
        Require(Effects() == before, "Cancel left the effect on the layer.");
        Require(!scene.CanUndo, "a cancelled effects panel still left history behind.");
        report["effectsPanelPreviewsAndCancelsAsOneStep"] = "passed";

        OnLayerEffects(this, new RoutedEventArgs());
        Require(FloatingPanelIsOpen("effects"), "the second effects panel opened nowhere.");
        Require(Effects() == before, "the effects panel changed the layer before anything was ticked.");
        OnDither(this, new RoutedEventArgs());
        Require(FloatingPanelIsOpen("filter") && FloatingPanelIsOpen("effects"),
            "the filter and the effects panels could not both be open, which is how the Mac keeps them.");
        CloseFloatingPanel("effects", commit: false);
        Require(FloatingPanelIsOpen("filter") && !FloatingPanelIsOpen("effects"),
            "closing one panel took the other one down with it.");
        CloseFloatingPanel("filter", commit: false);
        await WaitForFramesAsync();

        // The Mac force-closes the effects sheet when the layer list changes (ContentView.swift:234-241) and
        // closing it is what hands the layer back its pixels; a modal dialog made that question impossible to ask.
        OnLayerEffects(this, new RoutedEventArgs());
        var switchesAgain = (Grid)((StackPanel)FloatingPanelBody("effects")!).Children[0];
        switchesAgain.Children.OfType<CheckBox>().First().IsChecked = true;
        await WaitForFramesAsync();
        Require(Effects() != before, "the reopened effects panel stopped previewing.");
        Require(FloatingPanelIsOpen("effects"), "the sheet was not open to be closed by the switch below.");
        var levels = scene.AddAdjustment(AdjustmentKind.Levels);
        RefreshLayerPanel();
        LayerList.SelectedItem = layerRows[levels.Id];
        await WaitForFramesAsync();
        Require(!FloatingPanelIsOpen("effects"), "the effects sheet stayed open over a different layer than the one it opened on.");
        Require(Effects() == before, "the layer the sheet previewed kept the effect after the sheet stopped answering for it.");
        report["effectsPanelLeavesWithItsLayerAndSharesTheWindow"] = "passed";
    }

    /// <summary>The slowest panel rebuild the group above waited for, so a probe that passes in 5 ms and one
    /// that passes in 1 900 ms are not read as the same product.</summary>
    private double adjustmentPanelOpenMs;

    private LayerAdjustment? ReadAdjustment(Guid layerId) =>
        document!.Snapshot.Manifest.Layers.Single(layer => layer.Id == layerId).Adjustment;

    private static Slider FirstSlider(Microsoft.UI.Xaml.DependencyObject element)
    {
        Slider? found = element switch
        {
            Slider slider => slider,
            Panel panel => panel.Children.Select(FindSlider).FirstOrDefault(candidate => candidate is not null),
            ContentControl content when content.Content is Microsoft.UI.Xaml.DependencyObject inner => FindSlider(inner),
            _ => null,
        };
        return found ?? throw new InvalidOperationException("The row carries no slider.");
    }

    private static Slider? FindSlider(Microsoft.UI.Xaml.DependencyObject element) => element switch
    {
        Slider slider => slider,
        Panel panel => panel.Children.Select(FindSlider).FirstOrDefault(candidate => candidate is not null),
        ContentControl content when content.Content is Microsoft.UI.Xaml.DependencyObject inner => FindSlider(inner),
        _ => null,
    };

    private static bool ContainsText(Microsoft.UI.Xaml.DependencyObject element, string text) => element switch
    {
        TextBlock block => block.Text == text,
        Panel panel => panel.Children.Any(child => ContainsText(child, text)),
        ContentControl content => content.Content is Microsoft.UI.Xaml.DependencyObject inner && ContainsText(inner, text),
        _ => false,
    };

    /// <summary>A document whose only layer fills the canvas with a hard-edged ramp: a grade changes every
    /// pixel of it, and a picture averaged down and enlarged is distinguishable from one graded at full size.</summary>
    private static EditorDocument CameraRawScene(int width, int height)
    {
        var scene = EditorDocument.Create(width, height);
        var layer = scene.Snapshot.Manifest.Layers[0];
        var image = new Surface(width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int at = y * image.Stride + x * 4;
                byte value = (byte)(((x >> 3) + y) % 2 == 0 ? 40 : 200);
                image.Pixels[at] = image.Pixels[at + 1] = image.Pixels[at + 2] = value;
                image.Pixels[at + 3] = 255;
            }
        ((Dictionary<Guid, Surface>)scene.Snapshot.Images)[layer.Id] = image;
        return scene;
    }

    /// <summary>The sheet's own Apply or Cancel, found on the panel host the way a person finds it. The body is
    /// the wrong place to look: on the Mac the two answers sit in the sheet's bottom row
    /// (FilterSheet.swift:152-167), which this port's floating-panel host owns.</summary>
    private Button CameraRawSheetButton(bool confirm)
    {
        var button = FloatingPanelButton(CameraRawPanelKey, confirm);
        Require(button is not null, $"the Camera Raw sheet has no {(confirm ? "Apply" : "Cancel")} button to press.");
        return button!;
    }

    private static void CollectBands(Microsoft.UI.Xaml.DependencyObject root, List<Expander> into)
    {
        if (root is Expander band) into.Add(band);
        for (int i = 0, n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i < n; i++)
            CollectBands(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i), into);
    }

    /// <summary>#84: what a reader gets when every band is open. With the sheet closed at eight rows the same
    /// sweep reports 26 named controls (#65's measurement), so the claim here is that the rows inside the bands
    /// exist to a reader at all — and that none of them is unnamed.</summary>
    private async Task VerifyCameraRawBandRowsAsync(Dictionary<string, object> report)
    {
        var scene = CameraRawScene(320, 200);
        OpenDocument(scene); await WaitForFramesAsync();
        var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        OpenCameraRawPanel(scene, id);
        await WaitForFramesAsync();
        RootGrid.UpdateLayout();
        var sheet = FloatingPanelRoot(CameraRawPanelKey);
        Require(sheet is not null, "no sheet to open bands on.");
        var closed = AccessibleControls(sheet!);
        var bands = new List<Expander>();
        CollectBands(sheet!, bands);
        Require(bands.Count == 8, $"the sheet shows {bands.Count} bands, expected the 8 groups Camera Raw grades.");
        foreach (var band in bands) band.IsExpanded = true;
        await WaitForFramesAsync();
        RootGrid.UpdateLayout();
        var open = AccessibleControls(sheet!);
        report["cameraRawBandRows"] = new
        {
            closedNamed = closed.Named, openNamed = open.Named, rows = open.Named - closed.Named,
            unnamed = open.Unnamed.Count, missing = open.Unnamed,
        };
        Require(open.Named >= closed.Named + 30,
            $"opening all eight bands brought {open.Named - closed.Named} controls into the reader's tree, which "
            + "is not the rows those bands build — an Expander that shows nothing still has its sliders somewhere.");
        Require(open.Unnamed.Count == 0,
            $"the rows inside the bands report {open.Unnamed.Count} controls with no name to a reader: "
            + $"[{string.Join(", ", open.Unnamed)}].");
        PressChip(CameraRawSheetButton(confirm: false));
        Require(await WaitUntilAsync(() => !FloatingPanelIsOpen(CameraRawPanelKey), 8000),
            "the sheet with every band open did not close on Cancel.");
        report["cameraRawBandRowsReachAReader"] = "passed";
    }

    private static int CountBands(Microsoft.UI.Xaml.DependencyObject root)
    {
        var found = root is Expander ? 1 : 0;
        for (int i = 0, n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i < n; i++)
            found += CountBands(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i));
        return found;
    }

    private StackPanel OpenCameraRawPanel(EditorDocument scene, Guid layerId)
    {
        RefreshLayerPanel();
        LayerList.SelectedItem = layerRows[layerId];
        OnCameraRawFilter(this, new RoutedEventArgs());
        // Every group below asks this panel to be reading the layer it names, so say so here: a command that
        // refused would otherwise leave the previous group's detached body and scope canvas to be measured.
        Require(cameraRawSessionKey is not null && cameraRawSessionLayer == layerId && ReferenceEquals(document, scene),
            $"Filter ▸ Camera Raw Filter… opened no session for this layer (session live "
            + $"{cameraRawSessionKey is not null}, session layer {cameraRawSessionLayer} wanted {layerId}, document "
            + $"matches {ReferenceEquals(document, scene)}, can edit {CanEditLayerCommands}, busy "
            + $"{StatusProgress.Visibility}, dock children {AdjustmentPanel.Children.Count}.");
        Require(FloatingPanelIsOpen(CameraRawPanelKey),
            $"the command opened a session but no sheet (dock children {AdjustmentPanel.Children.Count}).");
        return (StackPanel)FloatingPanelBody(CameraRawPanelKey)!;
    }

    /// <summary>C08e's remaining half: the live preview is worked out on a copy no larger than the Mac's
    /// <c>previewLimit</c> (Filters.swift:359, 427-444) and enlarged back into the layer's own grid. Before this
    /// slice the Camera Raw panel graded the layer's full pixel count on every tick — and had no test at all.
    /// The wrapper hands the run back the document it had, because a Camera Raw session left alive on this
    /// scene's layer makes the panel skip its rebuild for whoever reads next.</summary>
    private async Task VerifyCameraRawPreviewAsync(Dictionary<string, object> report)
    {
        var previous = document;
        await VerifyCameraRawOpensFromTheFilterMenuAsync(report);
        await VerifyCameraRawBandRowsAsync(report);
        await VerifyCameraRawPreviewScaleAsync(report);
        await VerifyCameraRawPreviewSurvivesADocumentSwitchAsync(report);
        await VerifyCameraRawStaysInsideSelectionAsync(report);
        await VerifyCameraRawCalibrationAsync(report);
        await VerifyCameraRawWhiteBalanceAsync(report);
        await VerifyCameraRawOpticsAsync(report);
        await VerifyCameraRawGeometryAsync(report);
        await VerifyCameraRawGuidesAsync(report);
        await VerifyCameraRawMixerAsync(report);
        await VerifyCameraRawTargetedDragAsync(report);
        await VerifyCameraRawParametricAsync(report);
        await VerifyCameraRawCurvePresetsAsync(report);
        await VerifyCameraRawEyesAsync(report);
        await VerifyCameraRawOptionViewsAsync(report);
        await VerifyCameraRawScopeAsync(report);
        await VerifyCameraRawClipIndicatorsAsync(report);
        if (document is { } still) EndCameraRawSession(still);
        cameraRawNotice = null;
        if (previous is not null) { OpenDocument(previous); await WaitForFramesAsync(); }
    }

    /// <summary>#88's first half: the door. The Mac raises this panel because a filter-edit session exists
    /// (ContentView.swift:249-257) and Camera Raw is a Filter-menu kind (CompositorApp.swift:283-288,
    /// Filters.swift:16) that is deliberately not in AdjustmentKind (LayerAdjustment.swift:4-10) — so selecting
    /// a pixel layer must not raise it, and the panel must not outlive the session that opened it.</summary>
    private async Task VerifyCameraRawOpensFromTheFilterMenuAsync(Dictionary<string, object> report)
    {
        var scene = CameraRawScene(200, 120);
        var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        OpenDocument(scene); await WaitForFramesAsync();
        var menu = MenuBarRoot.Items.OfType<MenuBarItem>()
            .FirstOrDefault(bar => bar.Items.Any(item => ReferenceEquals(item, CameraRawFilterMenuItem)));
        Require(menu is not null, "Camera Raw Filter… is not in any menu bar.");
        // Not a resource lookup: Loc.T on an x:Uid key logs "missing resource" (the guard turns that into a
        // failed run), and the Uid form is not the code form. What is claimable is the Mac's own rule (see below).
        Require(!string.IsNullOrWhiteSpace(CameraRawFilterMenuItem.Text), "the command carries no label at all.");
        Require(CameraRawFilterMenuItem.Text.EndsWith("…", StringComparison.Ordinal),
            $"a command that opens a panel is labelled \"{CameraRawFilterMenuItem.Text}\"; the Mac puts the "
            + "ellipsis on every Filter-menu command that one (CompositorApp.swift:285).");
        // "Selection alone opens nothing" only bites if a selection change really happens, and opening a
        // document already selects its layer — so the scene grows a second pixel layer and the group walks the
        // selection onto it and back, then reads the two facts that say the event ran.
        Require(scene.DuplicateLayersAt(new[] { id }, null, id, false, out var copies), "the scene grew no second layer.");
        RefreshLayerPanel();
        Require(layerRows.Count == 2, $"the list shows {layerRows.Count} rows after duplicating, so a selection change is not askable.");
        LayerList.SelectedItem = layerRows[copies[0]];
        // Waited for, not assumed: the app answers SelectedLayer from the layer list it displays, and that
        // catches up a frame or two after the list itself moves — one run read it after WaitForFramesAsync
        // and the control said the selection never happened at all.
        Require(await WaitUntilAsync(() => SelectedLayer?.Id == copies[0], 2000),
            "the layer list never moved the selection, so the two checks below watched nothing.");
        LayerList.SelectedItem = layerRows[id];
        Require(await WaitUntilAsync(() => SelectedLayer?.Id == id, 2000),
            "the selection never came back to the layer the command is about to grade.");
        await WaitForFramesAsync();
        Require(!FloatingPanelIsOpen(CameraRawPanelKey),
            "selecting a pixel layer still raised the Camera Raw sheet by itself.");
        Require(cameraRawSessionKey is null, "a layer selection opened a Camera Raw session with no command behind it.");
        OnCameraRawFilter(this, new RoutedEventArgs());
        Require(FloatingPanelIsOpen(CameraRawPanelKey),
            $"Filter ▸ Camera Raw Filter… opened no sheet (busy {StatusProgress.Visibility}, "
            + $"can edit {CanEditLayerCommands}).");
        Require(cameraRawSessionKey is not null, "the command opened a panel with no undo session behind it.");
        // Where the sheet actually is, not merely that it exists. The Mac copies its frame from the document
        // window's right edge, 440 px wide and the window's full height (FloatingPanel.swift:125, 127-136), and
        // 440 is wider than the 252 px layers pane it sits on top of — which is the part an in-window host can
        // get wrong by parking the sheet inside the canvas column instead.
        var sheet = FloatingPanelRoot(CameraRawPanelKey);
        Require(sheet is not null, "the sheet came down without a root to measure.");
        await WaitForFramesAsync();
        RootGrid.UpdateLayout();
        var place = sheet!.TransformToVisual(WorkspaceGrid).TransformBounds(new Windows.Foundation.Rect(0, 0,
            sheet.ActualWidth, sheet.ActualHeight));
        var workspace = new Windows.Foundation.Rect(0, 0, WorkspaceGrid.ActualWidth, WorkspaceGrid.ActualHeight);
        var pane = LayersPane.TransformToVisual(WorkspaceGrid)
            .TransformBounds(new Windows.Foundation.Rect(0, 0, LayersPane.ActualWidth, LayersPane.ActualHeight));
        report["cameraRawSheetGeometry"] = new
        {
            width = Math.Round(sheet.ActualWidth, 1), height = Math.Round(sheet.ActualHeight, 1),
            right = Math.Round(place.Right, 1), workspaceRight = Math.Round(workspace.Right, 1),
            top = Math.Round(place.Top, 1), bottom = Math.Round(place.Bottom, 1),
            paneLeft = Math.Round(pane.Left, 1), paneWidth = Math.Round(pane.Width, 1),
        };
        Require(Math.Abs(sheet.ActualWidth - 440) < 1,
            $"the docked sheet is {sheet.ActualWidth:0.#} px wide; the Mac's dock is 440 "
            + "(FloatingPanel.swift:125).");
        Require(sheet.ActualHeight > 200 && Math.Abs(place.Bottom - workspace.Bottom) < 2 && Math.Abs(place.Top) < 2,
            $"the sheet is not the workspace's full height (top {place.Top:0.#}, bottom {place.Bottom:0.#} of "
            + $"{workspace.Bottom:0.#}).");
        Require(Math.Abs(place.Right - workspace.Right) < 2,
            $"the sheet is not flush with the window's right edge ({place.Right:0.#} against {workspace.Right:0.#}).");
        Require(pane.Width > 0 && place.Left <= pane.Left + 2,
            $"the sheet stops short of the layers pane (its left {place.Left:0.#}, the pane's {pane.Left:0.#}); "
            + "a 440 px dock covers it, and parking the panel in the canvas column does not.");
        // The sweep is also the proof the sheet is not an empty frame: ten bands' worth of rows, each with its
        // eye, plus the scope's mode switches. A panel that measured zero would sweep nearly nothing, and the
        // unnamed count would go back to the eight disclosure rows this line and the naming above exist for.
        var sheetSweep = AccessibleControls(sheet!);
        var bandsOnTree = CountBands(sheet!);
        report["cameraRawSheetBands"] = new { bands = bandsOnTree, named = sheetSweep.Named };
        // Measured on screen, with every band closed: eight disclosure rows and their eight eyes, and nothing
        // from inside a band — an unexpanded Expander keeps its content out of the visual tree, which is what
        // #65 is about and why this claim is about the rows rather than the sliders.
        Require(bandsOnTree == 8, $"the sheet shows {bandsOnTree} bands in the visual tree, expected the 8 groups "
            + "Camera Raw grades.");
        Require(sheetSweep.Named >= bandsOnTree * 2,
            $"the sheet reports {sheetSweep.Named} named controls with {bandsOnTree} bands open on screen, which "
            + "is fewer than one disclosure row and one eye per band.");
        Require(sheetSweep.Unnamed.Count == 0,
            $"the sheet's own controls report {sheetSweep.Unnamed.Count} names missing to a reader: "
            + $"[{string.Join(", ", sheetSweep.Unnamed)}].");

        // Re-running the command while the sheet is open is refused, not restarted: beginFilter guards on
        // filterEdit == nil and beeps (Filters.swift:481). The draft is what shows that — a restarted session
        // would hand back zeroed sliders, and a refused one keeps them.
        var armed = SliderNamed((StackPanel)FloatingPanelBody(CameraRawPanelKey)!, Loc.T("L_Exposure"));
        Require(armed is not null, "the Camera Raw sheet carries no Exposure row to leave a number in.");
        armed!.Value = 1.5;
        var refusedKey = cameraRawSessionKey;
        OnCameraRawFilter(this, new RoutedEventArgs());
        Require(ReferenceEquals(cameraRawSessionKey, refusedKey),
            "the command restarted a session that was already open, instead of refusing like beginFilter does.");
        var stillOpen = SliderNamed((StackPanel)FloatingPanelBody(CameraRawPanelKey)!, Loc.T("L_Exposure"));
        Require(stillOpen is null || Math.Abs(stillOpen.Value - 1.5) < 0.01,
            "the refused command rebuilt the sheet and lost the number that was in it.");

        // Cancel is the sheet's own answer, and it takes the sheet down with the session
        // (Filters.swift:575-585 clears filterEdit; ContentView.swift:250 closes on that).
        PressChip(CameraRawSheetButton(confirm: false));
        Require(await WaitUntilAsync(() => !FloatingPanelIsOpen(CameraRawPanelKey) && cameraRawSessionKey is null, 8000),
            $"Cancel left something behind (sheet open {FloatingPanelIsOpen(CameraRawPanelKey)}, "
            + $"session live {cameraRawSessionKey is not null}).");
        report["cameraRawOpensFromTheFilterMenu"] = "passed";
    }

    /// <summary>#94: what the Preview switch owes. The Mac stores every number and only withholds the render
    /// (Filters.swift:517-536: `preview` false clears the pending job and bumps the revision), so hiding the
    /// picture must not lose a row dragged underneath it. Each family is asked the same two things — nothing
    /// reaches the layer while it is hidden, and the frame that comes back when it is shown again is the frame
    /// the same number produces later on. The second half is what stops the first being a vacuous "nothing
    /// ever happened": it proves the number was taken and that the layer is live.</summary>
    private async Task VerifyFilterPreviewRowsStayHiddenAsync(Dictionary<string, object> report)
    {
        async Task CheckTonal()
        {
            var scene = TonalContrastScene();
            OpenDocument(scene); await WaitForFramesAsync();
            var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
            var before = scene.Snapshot.Images[id]!.Clone();
            Surface Current() => scene.Snapshot.Images[id]!;
            OnTonalContrast(this, new RoutedEventArgs());
            var body = FloatingPanelBody("tonalContrast")!;
            SliderNamed(body, Loc.T("L_TonalRadius"))!.Value = 4;
            SliderNamed(body, Loc.T("L_TonalMidtones"))!.Value = 60;
            Require(await WaitUntilAsync(() => tonalPreview is { Busy: false, Queued: false }, 8000),
                "the tonal sheet never settled on its opening values.");
            var switchRow = tonalFormProbe!.PreviewSwitch;
            switchRow.IsChecked = false;
            Require(await WaitUntilAsync(() => Differ(Current(), before) < 0, 8000),
                "taking tonal Preview off did not put the untouched pixels back.");
            SliderNamed(body, Loc.T("L_TonalMidtones"))!.Value = 100;
            Require(!await WaitUntilAsync(() => Differ(Current(), before) >= 0, 1500),
                "a tonal row moved while Preview was off reached the layer.");
            switchRow.IsChecked = true;
            Require(await WaitUntilAsync(() => tonalPreview is { Busy: false, Queued: false }
                    && Differ(Current(), before) >= 0, 8000),
                "showing the tonal preview again did not bring back the number typed while it was hidden.");
            var shownWith100 = Current().Clone();
            SliderNamed(body, Loc.T("L_TonalMidtones"))!.Value = 60;
            Require(await WaitUntilAsync(() => tonalPreview is { Busy: false, Queued: false }
                    && Differ(Current(), before) >= 0 && Differ(Current(), shownWith100) >= 0, 8000),
                "back to 60 midtones did not move the tonal picture, so the comparison below proves nothing.");
            SliderNamed(body, Loc.T("L_TonalMidtones"))!.Value = 100;
            Require(await WaitUntilAsync(() => tonalPreview is { Busy: false, Queued: false }
                    && Differ(Current(), shownWith100) < 0, 8000),
                $"re-entering 100 midtones later gives a different picture from the one Preview showed when it "
                + "came back ({TonalFingerprint(Current())} against {TonalFingerprint(shownWith100)}).");
            CloseFloatingPanel("tonalContrast", commit: false);
            Require(await WaitUntilAsync(() => tonalPreview is null, 8000), "the tonal cancel never finished.");
        }

        async Task CheckVignette()
        {
            var scene = VignetteScene();
            OpenDocument(scene); await WaitForFramesAsync();
            var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
            var before = scene.Snapshot.Images[id]!.Clone();
            Surface Current() => scene.Snapshot.Images[id]!;
            OnVignette(this, new RoutedEventArgs());
            var body = FloatingPanelBody("vignette")!;
            SliderNamed(body, Loc.T("L_VignetteAmount"))!.Value = 0;
            Require(await WaitUntilAsync(() => vignettePreview is { Busy: false, Queued: false }, 8000),
                "the vignette sheet never settled on its opening values.");
            var switchRow = vignetteFormProbe!.PreviewSwitch;
            switchRow.IsChecked = false;
            Require(await WaitUntilAsync(() => Differ(Current(), before) < 0, 8000),
                "taking vignette Preview off did not put the untouched pixels back.");
            SliderNamed(body, Loc.T("L_VignetteAmount"))!.Value = 100;
            Require(!await WaitUntilAsync(() => Differ(Current(), before) >= 0, 1500),
                "the vignette amount moved while its Preview was hidden still reached the layer.");
            switchRow.IsChecked = true;
            var shown = await WaitUntilAsync(() => vignettePreview is { Busy: false, Queued: false }
                    && Differ(Current(), before) >= 0, 8000);
            Require(shown, "showing the vignette preview again did not bring back the amount typed under it.");
            CloseFloatingPanel("vignette", commit: false);
            Require(await WaitUntilAsync(() => vignettePreview is null, 8000), "the vignette cancel never finished.");
        }

        async Task CheckCameraRaw()
        {
            var scene = CameraRawScene(320, 200);
            OpenDocument(scene); await WaitForFramesAsync();
            var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
            var before = scene.Snapshot.Images[id]!.Clone();
            Surface Current() => scene.Snapshot.Images[id]!;
            var panel = OpenCameraRawPanel(scene, id);
            var switchRow = CheckBoxNamed(panel, Loc.T("L_DitherPreview"));
            Require(switchRow is not null, "the Camera Raw sheet has no Preview switch to hide its picture.");
            switchRow!.IsChecked = false;
            Require(await WaitUntilAsync(() => Differ(Current(), before) < 0, 8000),
                "taking Camera Raw Preview off left a grade on the layer.");
            SliderNamed(panel, Loc.T("L_Exposure"))!.Value = 2;
            Require(!await WaitUntilAsync(() => Differ(Current(), before) >= 0, 1500),
                "a Camera Raw row moved while Preview was hidden still reached the layer.");
            switchRow.IsChecked = true;
            Require(await WaitUntilAsync(() => cameraRawPreviewQueuedDoc is null
                    && Differ(Current(), before) >= 0, 12000),
                "showing the Camera Raw preview again did not bring back the exposure typed under it.");
            PressChip(CameraRawSheetButton(confirm: false));
            Require(await WaitUntilAsync(() => !FloatingPanelIsOpen(CameraRawPanelKey), 8000),
                "the Camera Raw sheet stayed open after Cancel.");
        }

        await CheckTonal();
        await CheckVignette();
        await CheckCameraRaw();
        report["filterPreviewRowsStayHidden"] = "passed";
    }

    private async Task VerifyCameraRawPreviewScaleAsync(Dictionary<string, object> report)
    {
        var scene = CameraRawScene(4096, 512);
        OpenDocument(scene); await WaitForFramesAsync();
        var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        var capture = scene.Snapshot.Images[id]!;
        var panel = OpenCameraRawPanel(scene, id);
        await WaitForFramesAsync();
        // C13e/C13h: this panel is the widest surface the port builds in code, and it reaches the reader only
        // through its rows' own names. Held to the two claims that survive its sample changing with the style
        // or the expanded bands: nothing unnamed, and a non-empty sweep.
        RequireSurfaceControlsNamed(report, "cameraRawAccessibility", panel);
        report["cameraRawControlsAreNamed"] = "passed";
        Require(cameraRawPreviewSource is { } copy && copy.Width == 2048 && copy.Height == 256,
            $"the preview copy is {cameraRawPreviewSource?.Width}×{cameraRawPreviewSource?.Height}, not the layer halved (2048×256 of 4096×512).");
        Require(!ReferenceEquals(cameraRawPreviewSource, capture), "the preview copy is the layer itself: a tick would grade the live pixels.");

        var timer = Stopwatch.StartNew();
        FirstSlider(panel).Value = 2;
        Require(await WaitUntilAsync(() => !ReferenceEquals(scene.Snapshot.Images[id], capture), 8000),
            "moving a Camera Raw slider never published a preview.");
        double previewMs = timer.Elapsed.TotalMilliseconds;
        var preview = scene.Snapshot.Images[id]!;
        // The published surface keeps the layer's dimensions: the transform, the mask, the thumbnail and the
        // effects grid are all sized to those pixels, so the copy is enlarged back before it is handed over.
        Require(preview.Width == capture.Width && preview.Height == capture.Height,
            $"the preview changed the layer's pixel size ({preview.Width}×{preview.Height} of {capture.Width}×{capture.Height}).");

        // Same settings, same source, graded at full size: if the preview were that, the copy would be doing
        // nothing. The pixels have to differ while the dimensions do not.
        var exact = capture.Clone();
        var draft = cameraRawDraft!.Clone();
        timer.Restart();
        CameraRawEngine.Apply(exact, draft);
        double fullMs = timer.Elapsed.TotalMilliseconds;
        int differing = 0;
        for (int i = 0; i < preview.Pixels.Length; i += 4)
            if (Math.Abs(preview.Pixels[i] - exact.Pixels[i]) > 3) differing++;
        Require(differing > 0, "the preview is byte-for-byte the full-size grade, so no copy was ever graded.");
        // The saving is measured between the two grades themselves, on this thread, one after the other, with
        // the same settings. The tick's own wall time is reported but not gated: it crosses a worker hand-off,
        // a dispatcher hop, an enlargement and a document publish, so comparing it against a bare grade measures
        // the pipeline as much as the pixels — and on a loaded machine that is the comparison that flakes first.
        var smallGrade = cameraRawPreviewSource!.Clone();
        timer.Restart();
        CameraRawEngine.Apply(smallGrade, draft);
        double smallMs = timer.Elapsed.TotalMilliseconds;
        Require(smallMs < fullMs, $"grading the preview copy ({smallMs:F0} ms) cost no less than grading the whole layer "
            + $"({fullMs:F0} ms), for a quarter of the pixels.");
        report["cameraRawPreviewTickMs"] = Math.Round(previewMs, 1);
        report["cameraRawFullGradeMs"] = Math.Round(fullMs, 1);
        report["cameraRawPreviewGradeMs"] = Math.Round(smallMs, 1);
        report["cameraRawPreviewGradesADownscaledCopy"] = "passed";
    }

    /// <summary>The other half of the same slice: a filter runs inside the selection (Filters.swift:268-269) and
    /// the sheet says so (FilterSheet.swift:144-149). Windows had neither, so with a selection live the grade
    /// quietly covered the whole layer.</summary>
    /// <summary>
    /// One grade runs at a time, and a tick that arrives while it runs is meant to be re-run for the document
    /// that asked last. That was not what happened: the re-run carried the document the *running* tick came from,
    /// so switching layers mid-preview drained the queued move into the old document — and the session guard
    /// dropped the result, leaving the new session's first slider move with nothing painted at all. A 4096-wide
    /// grade runs for hundreds of milliseconds, which is the window this switch lands inside on purpose.
    /// </summary>
    private async Task VerifyCameraRawPreviewSurvivesADocumentSwitchAsync(Dictionary<string, object> report)
    {
        var first = CameraRawScene(4096, 512);
        OpenDocument(first); await WaitForFramesAsync();
        var firstId = first.Snapshot.Manifest.ActiveLayerId!.Value;
        var firstPanel = OpenCameraRawPanel(first, firstId);
        var second = CameraRawScene(640, 400);
        FirstSlider(firstPanel).Value = 2;
        OpenDocument(second); await WaitForFramesAsync();
        var secondId = second.Snapshot.Manifest.ActiveLayerId!.Value;
        var panel = OpenCameraRawPanel(second, secondId);
        var capture = second.Snapshot.Images[secondId]!;
        SliderNamed(panel, Loc.T("L_Contrast"))!.Value = 60;
        Require(await WaitUntilAsync(() => !ReferenceEquals(second.Snapshot.Images[secondId], capture), 8000),
            "the first slider move after a mid-preview document switch never painted: the queued grade went back to "
            + "the document the running tick came from instead of the one that asked last.");
        report["cameraRawPreviewSurvivesADocumentSwitch"] = "passed";
    }

    private async Task VerifyCameraRawStaysInsideSelectionAsync(Dictionary<string, object> report)
    {
        var scene = CameraRawScene(1200, 400);   // under the preview limit, so the copy is the layer and only the clip is in play
        OpenDocument(scene); await WaitForFramesAsync();
        var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        var capture = scene.Snapshot.Images[id]!;
        var untouched = (byte[])capture.Pixels.Clone();
        // The document was opened with its layer already selected, so the panel is up before the selection
        // exists. A user does the opposite — select a region, then Filter ▸ Camera Raw — and re-opening the
        // panel for the same layer is exactly that: selecting it again is a no-op the list never reports.
        OpenCameraRawPanel(scene, id);
        scene.SetSelection(Selection.Rectangle(1200, 400, 100, 50, 200, 80));
        PressChip(CameraRawSheetButton(confirm: false));
        Require(await WaitUntilAsync(() => !FloatingPanelIsOpen(CameraRawPanelKey), 8000),
            "Cancel left its Camera Raw sheet on screen.");
        var panel = OpenCameraRawPanel(scene, id);
        await WaitForFramesAsync();
        Require(cameraRawClip is not null, "a live selection never reached the Camera Raw session's clip.");
        Require(ContainsText(panel, Loc.T("L_FilterLimitedToSelection")), "the panel ran a clipped filter without saying so.");

        // Opening the panel publishes its own identity preview. Compare the slider's
        // result with that settled frame, rather than mistaking it for the slider tick.
        Require(await WaitUntilAsync(() => !cameraRawPreviewBusy && cameraRawPreviewQueuedDoc is null, 8000),
            "Initial Camera Raw preview did not settle.");
        var beforeSlider = scene.Snapshot.Images[id]; int beforeTicket = cameraRawPreviewTicket;
        report["cameraRawSelectionBeforeSlider"] = new { currentIsOriginalCapture = ReferenceEquals(scene.Snapshot.Images[id], capture),
            busy = cameraRawPreviewBusy, queued = cameraRawPreviewQueuedDoc is not null, ticket = cameraRawPreviewTicket };
        FirstSlider(panel).Value = 2;
        Require(await WaitUntilAsync(() => !ReferenceEquals(scene.Snapshot.Images[id], beforeSlider)
            && cameraRawPreviewTicket > beforeTicket && !cameraRawPreviewBusy && cameraRawPreviewQueuedDoc is null, 8000),
            "the clipped Camera Raw preview never published.");
        var preview = scene.Snapshot.Images[id]!;
        report["cameraRawSelectionAtPixelRead"] = new { busy = cameraRawPreviewBusy,
            queued = cameraRawPreviewQueuedDoc is not null, ticket = cameraRawPreviewTicket };
        int changedInside = 0, changedOutside = 0;
        for (int y = 0; y < preview.Height; y++)
            for (int x = 0; x < preview.Width; x++)
            {
                int at = y * preview.Stride + x * 4;
                bool same = preview.Pixels[at] == untouched[at] && preview.Pixels[at + 3] == untouched[at + 3];
                if (x >= 100 && x < 300 && y >= 50 && y < 130) { if (!same) changedInside++; }
                else if (!same) changedOutside++;
            }
        Require(changedOutside == 0, $"{changedOutside} pixels outside the selection were graded anyway.");
        Require(changedInside > 1000, $"only {changedInside} pixels inside the selection changed, so the clip starved the grade.");

        // OK is the persistent result, so it has to respect the same boundary the preview showed — and write the
        // full-resolution grade, not the copy the drag was looking at.
        var wanted = cameraRawDraft!.Clone();
        // The two answers live on the sheet's host row now, so the old "last row holding a button is the Apply
        // row" hunt would press some other control.
        PressChip(CameraRawSheetButton(confirm: true));
        Require(await WaitUntilAsync(() => !ReferenceEquals(scene.Snapshot.Images[id], preview), 8000),
            "Apply never replaced the preview it was shown.");
        var exact = capture.Clone();
        CameraRawEngine.Apply(exact, wanted);
        var expected = DitherEngine.ClipToCoverage(capture, exact, cameraRawClip!);
        var applied = scene.Snapshot.Images[id]!;
        Require(applied.Width == capture.Width && applied.Height == capture.Height,
            $"Apply changed the layer's pixel size ({applied.Width}×{applied.Height}).");
        for (int i = 0; i < applied.Pixels.Length; i++)
            Require(applied.Pixels[i] == expected.Pixels[i],
                $"Apply did not write the clipped full-size grade (byte {i}: {applied.Pixels[i]} against {expected.Pixels[i]}).");
        report["cameraRawFilterStaysInsideTheSelection"] = "passed";
    }

    /// <summary>
    /// CR-a: the Calibration group, driven through the panel rather than the engine. The numbers are the Mac's
    /// (AdjustPixels.c:1117, mirrored in Windows/tools/calibration-golden.py), and the point of running them
    /// here is the two hops the Core tests cannot see: that the rows exist under a collapsed expander a reader
    /// never reaches, and that moving one of them re-grades the layer instead of just redrawing the row.
    /// </summary>
    private async Task VerifyCameraRawCalibrationAsync(Dictionary<string, object> report)
    {
        var scene = CalibrationScene();
        OpenDocument(scene); await WaitForFramesAsync();
        var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        var panel = OpenCameraRawPanel(scene, id);
        var calibration = CameraRawBand(panel, Loc.T("L_Calibration"));
        Require(calibration is not null, "the Camera Raw panel has no Calibration group.");
        calibration!.IsExpanded = true;
        calibration.UpdateLayout();
        await WaitForFramesAsync();
        // #65, re-measured 2026-10-22 after the sheet came on screen (#88 片二): what kept a band's rows from a
        // reader was not the dock being collapsed. That was the 2026-10-21 reading, and the same sweep now finds
        // eight disclosure rows, each with a name, while every band stays closed. An unexpanded Expander keeps
        // its own content out of the visual tree — that is the half of #65 left standing, and it is why the
        // sweep below counts only the rows the panel builds outside a band.
        static bool InVisualTree(Microsoft.UI.Xaml.DependencyObject root, Microsoft.UI.Xaml.DependencyObject target)
        {
            if (ReferenceEquals(root, target)) return true;
            for (int i = 0, n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i < n; i++)
                if (InVisualTree(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i), target)) return true;
            return false;
        }
        object Realization(string stage) => new
        {
            stage,
            isExpanded = calibration.IsExpanded,
            inRootVisualTree = InVisualTree(RootGrid, calibration),
            expanderVisualChildren = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(calibration),
            bandRows = (calibration.Content as StackPanel)?.Children.Count ?? -1,
            bandSize = $"{calibration.ActualWidth:0.#}×{calibration.ActualHeight:0.#}",
            dock = AdjustmentPanel.Visibility.ToString(),
            swept = AccessibleControls(panel).Named + AccessibleControls(panel).Unnamed.Count,
        };
        var stages = new System.Collections.Generic.List<object> { Realization("after band UpdateLayout") };
        RootGrid.UpdateLayout();
        await WaitForFramesAsync();
        stages.Add(Realization("after root UpdateLayout"));
        report["cameraRawBandRealization"] = stages;
        // What a reader reaches today is the 16 controls outside the bands (16 counted on 2026-10-21, same before
        // and after a root layout pass). The rows under a band do carry names — the check below finds them by
        // walking the elements the panel built, which is where a name lives — but they are not in the visual tree,
        // so no sweep and no screen reader can count them while the dock is collapsed. Filed as #65: making the
        // dock Visible is the prerequisite, and it is not a one-liner — putting the Camera Raw body on screen
        // makes the enclosing LayerList.UpdateLayout() fail with E_INVALIDARG (measured 2026-10-21: the crash
        // needs the body, not the dock; a visible dock with no body gets through the same group fine).
        RequireSurfaceControlsNamed(report, "cameraRawCalibrationAccessibility", panel);
        var calibrationRows = new[] { Loc.T("L_ShadowTint"), Loc.T("L_RedHue"), Loc.T("L_RedSaturation"),
            Loc.T("L_GreenHue"), Loc.T("L_GreenSaturation"), Loc.T("L_BlueHue"), Loc.T("L_BlueSaturation") };
        var namelessRows = calibrationRows.Where(label => SliderNamed(panel, label) is null).ToList();
        Require(namelessRows.Count == 0,
            $"the Calibration band built {calibrationRows.Length} rows, {namelessRows.Count} of them answer to no "
            + $"reader name: [{string.Join(", ", namelessRows)}].");
        Require(ComboBoxNamed(panel, Loc.T("L_Process")) is not null, "the process picker answers to no reader name.");
        report["cameraRawCalibrationRowsAreNamed"] = "passed";

        var capture = scene.Snapshot.Images[id]!;
        var redHue = SliderNamed(panel, Loc.T("L_RedHue"));
        Require(redHue is not null, "the Calibration group has no red hue slider a probe can move.");
        redHue!.Value = 100;
        Require(await WaitUntilAsync(() => !ReferenceEquals(scene.Snapshot.Images[id], capture), 8000),
            "moving the red hue slider never published a preview.");
        var graded = scene.Snapshot.Images[id]!;
        Require(PixelAt(graded, 8, 8) == (255, 109, 60),
            $"the red half came out {PixelAt(graded, 8, 8)}, the Mac's Version 6 red hue at 100 is (255, 109, 60).");
        Require(PixelAt(graded, 56, 8) == (30, 30, 60),
            $"the red primary moved a pixel it does not dominate: {PixelAt(graded, 56, 8)}.");
        report["cameraRawCalibrationRedHueReachesTheLayer"] = "passed";

        var published = graded;
        var shadowTint = SliderNamed(panel, Loc.T("L_ShadowTint"));
        Require(shadowTint is not null, "the Calibration group has no shadow tint slider.");
        shadowTint!.Value = 100;
        Require(await WaitUntilAsync(() => !ReferenceEquals(scene.Snapshot.Images[id], published), 8000),
            "moving the shadow tint slider never published a preview.");
        graded = scene.Snapshot.Images[id]!;
        Require(PixelAt(graded, 56, 8) == (41, 30, 60),
            $"the shadow tint gave the dark half {PixelAt(graded, 56, 8)}, the Mac's is (41, 30, 60).");
        Require(PixelAt(graded, 8, 8) == (255, 109, 60),
            $"the shadow tint reached a tone above its third of the range: {PixelAt(graded, 8, 8)}.");
        report["cameraRawCalibrationShadowTintStaysInTheShadows"] = "passed";

        // The process picker has to do two things: say what it does under itself, and scale the seven sliders
        // that are already set. Both are checked on the same pixels the Version 6 numbers came from.
        var process = ComboBoxNamed(panel, Loc.T("L_Process"));
        Require(process is not null, "the Calibration group has no process picker.");
        published = graded;
        process!.SelectedIndex = 0;
        Require(await WaitUntilAsync(() => !ReferenceEquals(scene.Snapshot.Images[id], published), 8000),
            "choosing Version 1 never re-graded the layer.");
        graded = scene.Snapshot.Images[id]!;
        Require(ContainsText(panel, Loc.T("L_ProcessV1Summary")), "the process picker changed the pixels but never its own caption.");
        Require(PixelAt(graded, 8, 8) == (255, 87, 60),
            $"Version 1 graded the red half to {PixelAt(graded, 8, 8)}, the Mac's earliest process reaches (255, 87, 60).");
        Require(PixelAt(graded, 56, 8) == (36, 30, 60),
            $"Version 1 graded the dark half to {PixelAt(graded, 56, 8)}, the Mac's earliest process reaches (36, 30, 60).");
        report["cameraRawCalibrationProcessVersionScalesTheSliders"] = "passed";
    }

    /// <summary>Two flat halves: a saturated red one for the red primary, and a dark blue one that the shadow
    /// tint owns and the red primary must leave alone. Flat because a graded pixel is only worth reading where
    /// nothing else in the pipeline is moving it.</summary>
    private static EditorDocument CalibrationScene()
    {
        var scene = EditorDocument.Create(64, 48);
        var layer = scene.Snapshot.Manifest.Layers[0];
        var image = new Surface(64, 48);
        for (int y = 0; y < 48; y++)
            for (int x = 0; x < 64; x++)
            {
                int at = y * image.Stride + x * 4;
                bool red = x < 32;
                image.Pixels[at] = (byte)(red ? 255 : 30);
                image.Pixels[at + 1] = (byte)(red ? 60 : 30);
                image.Pixels[at + 2] = (byte)(red ? 60 : 60);
                image.Pixels[at + 3] = 255;
            }
        ((Dictionary<Guid, Surface>)scene.Snapshot.Images)[layer.Id] = image;
        return scene;
    }

    /// <summary>Numbers for the four-band scene below, transcribed from the macOS solve exactly as the Core tests
    /// do (Windows/tools/white-balance-golden.py): what Auto has to answer for this layer's average, and what the
    /// selector has to answer for the two pixels the group clicks on. The strong one is deliberately beyond the
    /// sliders' range, so the floor it lands on is part of the claim.</summary>
    private const double BalanceAutoTemperature = -5.996022, BalanceAutoTint = -100;
    private const double BalanceMildTemperature = -71.658935, BalanceMildTint = -9.046226;
    private const double BalanceWarmTemperature = -100, BalanceWarmTint = 17.098998;

    /// <summary>A layer in four bands: strong warm, mild warm, strong cool, neutral. The mix is what makes Auto
    /// answer on one axis and the selector answer on the other, and the neutral band keeps the average from
    /// being a two-color coin flip.</summary>
    private static EditorDocument WhiteBalanceScene()
    {
        var scene = EditorDocument.Create(64, 48);
        var layer = scene.Snapshot.Manifest.Layers[0];
        var image = new Surface(64, 48);
        var bands = new[] { ((byte)200, (byte)120, (byte)90), ((byte)140, (byte)120, (byte)110),
            ((byte)60, (byte)90, (byte)200), ((byte)90, (byte)90, (byte)90) };
        for (int y = 0; y < 48; y++)
            for (int x = 0; x < 64; x++)
            {
                int at = y * image.Stride + x * 4;
                var (r, g, b) = bands[x / 16];
                image.Pixels[at] = r; image.Pixels[at + 1] = g; image.Pixels[at + 2] = b; image.Pixels[at + 3] = 255;
            }
        ((Dictionary<Guid, Surface>)scene.Snapshot.Images)[layer.Id] = image;
        return scene;
    }

    /// <summary>CR-b: the row the Mac puts at the head of Color. Auto is answered off the UI thread and has to
    /// land on the two sliders, the selector has to solve the pixel that was clicked rather than the average,
    /// and either answer has to stop at the sliders' ±100 — which is where the Mac's normalization would clamp
    /// it. The mode itself is not an adjustment, so that claim is checked too.</summary>
    private async Task VerifyCameraRawWhiteBalanceAsync(Dictionary<string, object> report)
    {
        var scene = WhiteBalanceScene();
        OpenDocument(scene); await WaitForFramesAsync();
        var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        var panel = OpenCameraRawPanel(scene, id);
        await WaitForFramesAsync();
        var mode = ComboBoxNamed(panel, Loc.T("L_WhiteBalance"));
        var selector = ToggleButtonNamed(panel, Loc.T("A_WhiteBalanceSelector"));
        Require(mode is not null && selector is not null,
            $"the white balance row is missing its picker ({mode is not null}) or its selector ({selector is not null}).");
        Require(cameraRawDraft!.WhiteBalance == CameraRawSettings.WhiteBalanceCustom,
            "a fresh Camera Raw session does not start on Custom, as the Mac's default does.");
        Require(ShownTextVisibility(panel, Loc.T("L_WhiteBalanceClickHint")) != Visibility.Visible,
            "the panel explains a click the selector is not listening for, before anyone armed it.");

        // Choosing Auto moves the picker on the click and only then answers: the scan runs off the UI thread,
        // and the answer may be thrown away if the user has moved on. Both halves are observable here.
        mode!.SelectedIndex = 1;
        Require(cameraRawDraft!.WhiteBalance == CameraRawSettings.WhiteBalanceAuto,
            "picking Auto did not even mark the mode before the scan started, so the picker would not move on the click.");
        Require(await WaitUntilAsync(() => Math.Abs(cameraRawDraft!.Temperature - BalanceAutoTemperature) < 1e-3, 8000),
            $"Auto never wrote the temperature the layer's average solves to (still {cameraRawDraft!.Temperature}).");
        Require(Math.Abs(cameraRawDraft!.Tint - BalanceAutoTint) < 1e-3,
            $"Auto solved the tint to {cameraRawDraft!.Tint}, the Mac's answer clamped to the floor is {BalanceAutoTint}.");
        var temperatureRow = SliderNamed(panel, Loc.T("L_Temperature"));
        var tintRow = SliderNamed(panel, Loc.T("L_Tint"));
        Require(temperatureRow is not null && Math.Abs(temperatureRow!.Value - BalanceAutoTemperature) < 1e-3
                && Math.Abs(tintRow!.Value - BalanceAutoTint) < 1e-3,
            $"Auto moved the draft but not the rows it just wrote: {temperatureRow?.Value}, {tintRow?.Value}.");
        Require(ContainsText(panel, Loc.T("S_WhiteBalanceAutoApplied")) || StatusText.Text == Loc.T("S_WhiteBalanceAutoApplied"),
            $"Auto landed but the status line says {StatusText.Text} instead.");
        report["cameraRawWhiteBalanceAutoSolvesTheLayer"] = "passed";

        // Dragging either slider by hand is a manual edit: the mode drops back to Custom so a later answer
        // cannot arrive over the user's own numbers.
        var before = cameraRawDraft!.Temperature;
        temperatureRow!.Value = 3;
        Require(cameraRawDraft!.WhiteBalance == CameraRawSettings.WhiteBalanceCustom,
            "moving the temperature slider left the picker on Auto, which is how a stale answer overwrites a manual edit.");
        Require(Math.Abs(cameraRawDraft!.Temperature - 3) < 1e-6 && Math.Abs(before - BalanceAutoTemperature) < 1e-3,
            $"the manual edit did not replace Auto's answer ({before} then {cameraRawDraft!.Temperature}).");

        selector!.IsChecked = true;
        Require(cameraRawSamplesWhiteBalance, "arming the selector left the session not listening for the click.");
        Require(ShownTextVisibility(panel, Loc.T("L_WhiteBalanceClickHint")) == Visibility.Visible,
            "the selector is armed and the panel never said what a click does.");
        Require(SampleCameraRawWhiteBalance(new PointD(24, 8)), "the selector refused a pixel of the layer under it.");
        Require(Math.Abs(cameraRawDraft!.Temperature - BalanceMildTemperature) < 1e-3
                && Math.Abs(cameraRawDraft!.Tint - BalanceMildTint) < 1e-3,
            $"the mild band solves to ({BalanceMildTemperature}, {BalanceMildTint}); the panel holds ({cameraRawDraft!.Temperature}, {cameraRawDraft!.Tint}).");
        Require(cameraRawDraft!.WhiteBalance == CameraRawSettings.WhiteBalanceCustom,
            "a picked pixel left the picker on Auto.");
        Require(Math.Abs(temperatureRow!.Value - BalanceMildTemperature) < 1e-3,
            $"the picker row still shows {temperatureRow.Value} after the selector solved {BalanceMildTemperature}.");

        // A strong color solves past the sliders' range. The Mac clamps it in `normalized`; here the same ceiling
        // is applied on the way in, so what the sliders show is what the grade uses.
        Require(SampleCameraRawWhiteBalance(new PointD(8, 8)), "the selector refused the warm band's pixel.");
        Require(Math.Abs(cameraRawDraft!.Temperature - BalanceWarmTemperature) < 1e-3,
            $"the warm band solves to -204.9 unclamped; the panel must stop at the floor, and holds {cameraRawDraft!.Temperature}.");
        Require(Math.Abs(cameraRawDraft!.Tint - BalanceWarmTint) < 1e-3,
            $"the warm band's tint is {cameraRawDraft!.Tint}, the Mac's is {BalanceWarmTint}.");
        Require(cameraRawSamplesWhiteBalance, "the selector stopped listening after one click; the Mac keeps it armed.");

        // Pick the same over-range pixel a second time. The row already sits at the floor, so its own
        // value-changed write cannot correct the draft this time: whatever the draft holds afterwards is only
        // what the picker wrote, which is the one place the ceiling can still be missed.
        Require(SampleCameraRawWhiteBalance(new PointD(8, 8)), "the second pick of the same pixel was refused.");
        Require(Math.Abs(cameraRawDraft!.Temperature - BalanceWarmTemperature) < 1e-3,
            $"picking an over-range color twice left the draft at {cameraRawDraft!.Temperature} "
            + "while the row sits at the floor.");

        report["cameraRawWhiteBalanceSelectorPicksTheClickedPixel"] = "passed";

        var graded = scene.Snapshot.Images[id]!;
        selector.IsChecked = false;
        Require(!cameraRawSamplesWhiteBalance, "disarming the selector left the session still listening for the click.");
        Require(ShownTextVisibility(panel, Loc.T("L_WhiteBalanceClickHint")) != Visibility.Visible,
            "disarming the selector left the canvas promised to the panel.");
        Require(!SampleCameraRawWhiteBalance(new PointD(56, 8)), "a disarmed selector still took the click off the tool.");
        Require(cameraRawDraft!.Temperature == BalanceWarmTemperature, "the disarmed selector still moved the draft.");
        Require(ReferenceEquals(scene.Snapshot.Images[id], graded), "the disarmed selector republished the grade.");
        report["cameraRawWhiteBalanceSelectorStopsWhenDisarmed"] = "passed";
    }

    /// <summary>Four flat bands, one per thing the defringe rows have to tell apart: a purple fringe the purple
    /// band owns, a green fringe the green band owns, a red no band claims, and a neutral with no chroma to
    /// remove. Flat, because a graded pixel is only worth reading where nothing else in the pipeline moves it.</summary>
    private static EditorDocument FringeBandsScene()
    {
        var scene = EditorDocument.Create(64, 48);
        var layer = scene.Snapshot.Manifest.Layers[0];
        var image = new Surface(64, 48);
        var bands = new[] { ((byte)200, (byte)60, (byte)220), ((byte)100, (byte)200, (byte)60),
            ((byte)220, (byte)60, (byte)60), ((byte)140, (byte)140, (byte)140) };
        for (int y = 0; y < 48; y++)
            for (int x = 0; x < 64; x++)
            {
                int at = y * image.Stride + x * 4;
                var (r, g, b) = bands[x / 16];
                image.Pixels[at] = r; image.Pixels[at + 1] = g; image.Pixels[at + 2] = b; image.Pixels[at + 3] = 255;
            }
        ((Dictionary<Guid, Surface>)scene.Snapshot.Images)[layer.Id] = image;
        return scene;
    }

    /// <summary>A purple half against a green half at 32×32: the smallest frame in which the chromatic pull
    /// reaches a whole pixel, and the one the geometry numbers below were taken on
    /// (Windows/tools/optics-golden.py, the fringe32 rows).</summary>
    private static EditorDocument Fringe32Scene()
    {
        var scene = EditorDocument.Create(32, 32);
        var layer = scene.Snapshot.Manifest.Layers[0];
        var image = new Surface(32, 32);
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
            {
                bool purple = x < 16;
                int at = y * image.Stride + x * 4;
                image.Pixels[at] = (byte)(purple ? 200 : 100);
                image.Pixels[at + 1] = (byte)(purple ? 60 : 200);
                image.Pixels[at + 2] = (byte)(purple ? 220 : 60);
                image.Pixels[at + 3] = 255;
            }
        ((Dictionary<Guid, Surface>)scene.Snapshot.Images)[layer.Id] = image;
        return scene;
    }

    /// <summary>CR-c: the Optics band, driven through the panel. Every expected byte is the Mac's
    /// (AdjustPixels.c:1018-1115 and LensPixels.c:4, mirrored line for line in Windows/tools/optics-golden.py);
    /// what the Core tests cannot see is the two hops this group is for — that a row under a collapsed band
    /// reaches the layer, and that a clicked fringe lands on the rows and on the pixels at once. The layers here
    /// are smaller than the preview limit, so the published preview is the full-size grade.</summary>
    private async Task VerifyCameraRawOpticsAsync(Dictionary<string, object> report)
    {
        var scene = FringeBandsScene();
        OpenDocument(scene); await WaitForFramesAsync();
        var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        var panel = OpenCameraRawPanel(scene, id);
        Expander OpticsBand(StackPanel body)
        {
            var band = CameraRawBand(body, Loc.T("L_Optics"));
            Require(band is not null, "the Camera Raw panel has no Optics group.");
            band!.IsExpanded = true;
            band.UpdateLayout();
            return band;
        }

        var optics = OpticsBand(panel);
        await WaitForFramesAsync();
        report["cameraRawOpticsRealization"] = new {
            isExpanded = optics.IsExpanded,
            loaded = optics.IsLoaded,
            expanderVisualChildren = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(optics),
            bandRows = (optics.Content as StackPanel)?.Children.Count ?? -1,
            swept = AccessibleControls(panel).Named + AccessibleControls(panel).Unnamed.Count,
        };
        RequireSurfaceControlsNamed(report, "cameraRawOpticsAccessibility", panel);
        var labels = new[] { "L_Distortion", "L_ProfileDistortion", "L_ProfileVignetting", "L_PurpleAmount",
            "L_PurpleHueLow", "L_PurpleHueHigh", "L_GreenAmount", "L_GreenHueLow", "L_GreenHueHigh",
            "L_LensVignetting", "L_LensMidpoint" };
        var nameless = labels.Where(label => SliderNamed(panel, Loc.T(label)) is null).ToList();
        Require(nameless.Count == 0,
            $"the Optics band built {labels.Length} rows, {nameless.Count} of them answer to no reader name: "
            + $"[{string.Join(", ", nameless)}].");
        Require(CheckBoxNamed(panel, Loc.T("L_RemoveChromaticAberration")) is not null
            && CheckBoxNamed(panel, Loc.T("L_LensProfile")) is not null
            && ToggleButtonNamed(panel, Loc.T("A_DefringeSelector")) is not null,
            "the Optics band is missing a toggle, or the fringe picker, that a reader can name.");
        Require(ShownTextVisibility(panel, Loc.T("L_DefringeClickHint")) != Visibility.Visible,
            "the panel explains a click the fringe picker is not listening for, before anyone armed it.");
        report["cameraRawOpticsRowsAreNamed"] = "passed";

        // Read the capture *before* moving the row: the preview lands roughly 180 ms later, and a capture read
        // after the move can already be the graded surface, which then never "changes" again. That ordering was
        // the red in this group, not the product.
        var capture = scene.Snapshot.Images[id]!;
        SliderNamed(panel, Loc.T("L_PurpleAmount"))!.Value = 100;
        Require(await WaitUntilAsync(() => !ReferenceEquals(scene.Snapshot.Images[id], capture), 8000),
            "moving the purple amount slider never published a preview.");
        var graded = scene.Snapshot.Images[id]!;
        Require(PixelAt(graded, 8, 8) == (128, 90, 134),
            $"the purple band at full amount came out {PixelAt(graded, 8, 8)}, the Mac's is (128, 90, 134).");
        Require(PixelAt(graded, 24, 8) == (100, 200, 60),
            $"the purple amount reached a hue outside its band: {PixelAt(graded, 24, 8)}.");
        Require(PixelAt(graded, 40, 8) == (220, 60, 60), "a red no band claims was still desaturated.");
        Require(PixelAt(graded, 56, 8) == (140, 140, 140), "a neutral with no chroma to remove was still touched.");
        report["cameraRawDefringeAmountRowReachesTheLayer"] = "passed";

        // The green row owns the other fringe, and the four hue rows decide who gets what — moved one at a time,
        // so each step is a different reason for the same pixel to change or not.
        SliderNamed(panel, Loc.T("L_GreenAmount"))!.Value = 100;
        Require(await WaitUntilAsync(() => PixelAt(scene.Snapshot.Images[id]!, 24, 8) == (148, 178, 136), 8000),
            "the green amount never reached the green fringe it names.");
        SliderNamed(panel, Loc.T("L_GreenHueLow"))!.Value = 170;
        SliderNamed(panel, Loc.T("L_GreenHueHigh"))!.Value = 200;
        Require(await WaitUntilAsync(() => PixelAt(scene.Snapshot.Images[id]!, 24, 8) == (100, 200, 60), 8000),
            "moving the green band off that hue left it defringed anyway.");
        SliderNamed(panel, Loc.T("L_PurpleHueLow"))!.Value = 60;
        SliderNamed(panel, Loc.T("L_PurpleHueHigh"))!.Value = 310;
        Require(await WaitUntilAsync(() => PixelAt(scene.Snapshot.Images[id]!, 24, 8) == (148, 178, 136), 8000),
            "widening the purple band over the same hue did not hand the pixel to the purple amount.");
        Require(PixelAt(scene.Snapshot.Images[id]!, 8, 8) == (128, 90, 134), "the purple band's own fringe went cold.");
        report["cameraRawDefringeHueRowsMoveTheBand"] = "passed";

        // The eyedropper, on a session with nothing set: the first pick of a family both bands it and lifts the
        // amount from zero, as on the Mac (CameraRaw.swift:482-490).
        scene = FringeBandsScene();
        OpenDocument(scene); await WaitForFramesAsync();
        id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        panel = OpenCameraRawPanel(scene, id);
        OpticsBand(panel);
        await WaitForFramesAsync();
        var selector = ToggleButtonNamed(panel, Loc.T("A_DefringeSelector"));
        Require(selector is not null, "the Optics band has no fringe picker a probe can arm.");
        var draft = cameraRawDraft!;
        selector!.IsChecked = true;
        Require(cameraRawSamplesDefringe, "arming the fringe picker left the session not listening for the click.");
        Require(ShownTextVisibility(panel, Loc.T("L_DefringeClickHint")) == Visibility.Visible,
            "the picker is armed and the panel never said what a click does.");
        Require(SampleCameraRawDefringe(new PointD(8, 8)), "the picker refused a pixel of the layer under it.");
        Require(Math.Abs(draft.OpticsPurpleHueLow - 267.5) < 1e-6 && Math.Abs(draft.OpticsPurpleHueHigh - 317.5) < 1e-6,
            $"a fringe at hue 292.5 bands 267.5…317.5; the draft holds {draft.OpticsPurpleHueLow}…{draft.OpticsPurpleHueHigh}.");
        Require(Math.Abs(draft.OpticsPurpleAmount - 50) < 1e-6,
            $"picking a fringe onto an amount of zero lifts it to 50, as on the Mac; it holds {draft.OpticsPurpleAmount}.");
        var purpleLowRow = SliderNamed(panel, Loc.T("L_PurpleHueLow"))!;
        Require(Math.Abs(purpleLowRow.Value - 267.5) < 1e-6,
            $"the picker solved {draft.OpticsPurpleHueLow} but its own row still says {purpleLowRow.Value}.");
        Require(SampleCameraRawDefringe(new PointD(24, 8)), "the second pick was refused.");
        Require(Math.Abs(draft.OpticsGreenHueLow - 77.857143) < 1e-4 && Math.Abs(draft.OpticsGreenHueHigh - 127.857143) < 1e-4,
            $"hue 102.857143 bands 77.857…127.857; the draft holds {draft.OpticsGreenHueLow}…{draft.OpticsGreenHueHigh}.");
        Require(Math.Abs(draft.OpticsGreenAmount - 50) < 1e-6 && Math.Abs(draft.OpticsPurpleAmount - 50) < 1e-6,
            $"the green pick moved the purple amount it had no business touching: {draft.OpticsPurpleAmount}.");
        Require(SampleCameraRawDefringe(new PointD(40, 8)), "the picker refused the red band.");
        Require(Math.Abs(draft.OpticsGreenHueLow) < 1e-6 && Math.Abs(draft.OpticsGreenHueHigh - 25) < 1e-6,
            $"a hue of 0 bands -25…25 and the sliders stop at the floor, 0…25; the draft holds "
            + $"{draft.OpticsGreenHueLow}…{draft.OpticsGreenHueHigh}.");
        Require(Math.Abs(draft.OpticsGreenAmount - 50) < 1e-6, "a second pick in the same family reset its amount.");
        // Pick the same near-zero hue again. Its row already sits at the floor, so the row's own value-changed
        // write cannot correct the draft this time: what the draft holds afterwards is only what the picker wrote,
        // and that is the one place the ceiling can still be missed.
        Require(SampleCameraRawDefringe(new PointD(40, 8)), "the second pick of the same fringe was refused.");
        Require(Math.Abs(draft.OpticsGreenHueLow) < 1e-6,
            $"picking a hue of 0 twice left the draft at {draft.OpticsGreenHueLow} while its row sits at the floor.");
        Require(await WaitUntilAsync(() => PixelAt(scene.Snapshot.Images[id]!, 8, 8) == (164, 75, 177), 8000),
            $"picking a fringe never moved its pixels: the purple band still reads {PixelAt(scene.Snapshot.Images[id]!, 8, 8)}, "
            + "and half the amount on that hue is (164, 75, 177).");
        Require(cameraRawSamplesDefringe, "the picker stopped listening after a click; the Mac keeps it armed.");
        report["cameraRawDefringePickerCentersTheBandOnTheFringe"] = "passed";

        var picked = scene.Snapshot.Images[id]!;
        selector.IsChecked = false;
        Require(!cameraRawSamplesDefringe, "disarming the picker left the session still listening for the click.");
        Require(ShownTextVisibility(panel, Loc.T("L_DefringeClickHint")) != Visibility.Visible,
            "disarming the picker left the canvas promised to the panel.");
        Require(!SampleCameraRawDefringe(new PointD(56, 8)), "a disarmed picker still took the click off the tool.");
        Require(Math.Abs(draft.OpticsGreenHueHigh - 25) < 1e-6, "the disarmed picker still moved the band.");
        Require(ReferenceEquals(scene.Snapshot.Images[id], picked), "the disarmed picker republished the grade.");
        report["cameraRawDefringePickerStopsWhenDisarmed"] = "passed";

        // The geometry rows, on a frame big enough for the shift to reach a pixel.
        scene = Fringe32Scene();
        OpenDocument(scene); await WaitForFramesAsync();
        id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        panel = OpenCameraRawPanel(scene, id);
        OpticsBand(panel);
        await WaitForFramesAsync();
        var profile = CheckBoxNamed(panel, Loc.T("L_LensProfile"))!;
        var chromatic = CheckBoxNamed(panel, Loc.T("L_RemoveChromaticAberration"))!;
        var untouched = scene.Snapshot.Images[id]!;
        SliderNamed(panel, Loc.T("L_ProfileDistortion"))!.Value = 0;
        Require(await WaitUntilAsync(() => !ReferenceEquals(scene.Snapshot.Images[id], untouched), 8000),
            "the profile slider moved no preview at all, so nothing here can tell a skipped grade from a missing tick.");
        Require(PixelAt(scene.Snapshot.Images[id]!, 0, 0) == (200, 60, 220),
            "the profile sliders graded the layer while their toggle was off.");
        Require(!cameraRawDraft!.AdjustsOptics, "the profile sliders alone count as an adjustment; on the Mac they do not.");
        profile.IsChecked = true;
        Require(await WaitUntilAsync(() => PixelAt(scene.Snapshot.Images[id]!, 0, 0) == (219, 128, 232), 8000),
            $"the profile vignette leaves (219, 128, 232) at the corner, this left {PixelAt(scene.Snapshot.Images[id]!, 0, 0)}.");
        Require(PixelAt(scene.Snapshot.Images[id]!, 16, 16) == (100, 200, 60),
            "the vignette reached the middle of the frame, where its own mask is zero.");
        report["cameraRawOpticsProfileNeedsItsToggle"] = "passed";

        profile.IsChecked = false;
        chromatic.IsChecked = true;
        Require(await WaitUntilAsync(() => PixelAt(scene.Snapshot.Images[id]!, 15, 0) == (200, 60, 60), 8000),
            $"the blue half of a purple fringe pulled outward reads (200, 60, 60), this reads {PixelAt(scene.Snapshot.Images[id]!, 15, 0)}.");
        Require(PixelAt(scene.Snapshot.Images[id]!, 16, 0) == (200, 200, 60),
            "the red side of the seam did not take the fringe from the other half.");
        Require(PixelAt(scene.Snapshot.Images[id]!, 17, 0) == (100, 200, 60),
            "the pull walked one pixel further than the row copy allows: a live read hands 17 the red that 16 just took.");
        Require(PixelAt(scene.Snapshot.Images[id]!, 0, 16) == (200, 60, 220),
            "the chromatic shift moved a pixel at the middle of its own half.");
        report["cameraRawOpticsChromaticTogglePullsTheFringeApart"] = "passed";

        chromatic.IsChecked = false;
        SliderNamed(panel, Loc.T("L_Distortion"))!.Value = 100;
        Require(await WaitUntilAsync(() => PixelAt(scene.Snapshot.Images[id]!, 15, 0) == (192, 72, 207), 8000),
            $"a 100 bend moves the seam to (192, 72, 207), this left {PixelAt(scene.Snapshot.Images[id]!, 15, 0)}.");
        Require(PixelAt(scene.Snapshot.Images[id]!, 16, 0) == (108, 188, 73),
            $"the other side of the seam reads {PixelAt(scene.Snapshot.Images[id]!, 16, 0)}, the Mac's is (108, 188, 73).");
        Require(PixelAt(scene.Snapshot.Images[id]!, 0, 0) == (200, 60, 220),
            "the seam was carried into a flat half, where nothing should move.");
        report["cameraRawOpticsDistortionMovesTheSeam"] = "passed";
    }

    /// <summary>A 100×100 layer whose every pixel is its own address: a translation shows up as a byte-for-byte
    /// copy at a known offset, and nothing in the pattern is symmetric enough to survive a wrong mapping.</summary>
    private static EditorDocument SpeckleScene()
    {
        var scene = EditorDocument.Create(100, 100);
        var layer = scene.Snapshot.Manifest.Layers[0];
        var image = new Surface(100, 100);
        for (int y = 0; y < 100; y++)
            for (int x = 0; x < 100; x++)
            {
                int at = y * image.Stride + x * 4;
                image.Pixels[at] = (byte)((x * 31 + y * 17) % 9 == 0 ? 240 : 30);
                image.Pixels[at + 1] = (byte)((x * 7 + y * 13) % 5 == 0 ? 200 : 45);
                image.Pixels[at + 2] = 90;
                image.Pixels[at + 3] = 255;
            }
        ((Dictionary<Guid, Surface>)scene.Snapshot.Images)[layer.Id] = image;
        return scene;
    }

    private static byte AlphaAt(Surface surface, int x, int y) => surface.Pixels[y * surface.Stride + x * 4 + 3];

    /// <summary>CR-d: the Geometry band. The corner numbers and the crop arithmetic are the Mac's own and are
    /// locked in Core (Windows/tests/Compositor.Core.Tests/CameraRawGeometryTests.cs, numbered from
    /// Windows/tools/geometry-golden.py); what this group adds is that a row in the band reaches the document, and
    /// that it does so through the sampler the port has rather than the Core Image one the Mac has — so the pixel
    /// claims here are the ones no filter can get wrong: a whole-pixel slide, the edge a keystone leaves
    /// untouched, and the bounds a bend really leaves behind.</summary>
    private async Task VerifyCameraRawGeometryAsync(Dictionary<string, object> report)
    {
        var scene = SpeckleScene();
        OpenDocument(scene); await WaitForFramesAsync();
        var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        var panel = OpenCameraRawPanel(scene, id);
        Expander GeometryBand(StackPanel body)
        {
            var band = CameraRawBand(body, Loc.T("L_Geometry"));
            Require(band is not null, "the Camera Raw panel has no Geometry group.");
            band!.IsExpanded = true;
            band.UpdateLayout();
            return band;
        }

        var geometry = GeometryBand(panel);
        await WaitForFramesAsync();
        report["cameraRawGeometryRealization"] = new {
            isExpanded = geometry.IsExpanded,
            loaded = geometry.IsLoaded,
            expanderVisualChildren = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(geometry),
            bandRows = (geometry.Content as StackPanel)?.Children.Count ?? -1,
            swept = AccessibleControls(panel).Named + AccessibleControls(panel).Unnamed.Count,
        };
        RequireSurfaceControlsNamed(report, "cameraRawGeometryAccessibility", panel);
        var labels = new[] { "L_GeometryVertical", "L_GeometryHorizontal", "L_GeometryRotate", "L_GeometryAspect",
            "L_GeometryScale", "L_GeometryOffsetX", "L_GeometryOffsetY" };
        var nameless = labels.Where(label => SliderNamed(panel, Loc.T(label)) is null).ToList();
        Require(nameless.Count == 0,
            $"the Geometry band built {labels.Length} sliders, {nameless.Count} of them answer to no reader name: "
            + $"[{string.Join(", ", nameless)}].");
        Require(ComboBoxNamed(panel, Loc.T("L_Projection")) is not null
            && CheckBoxNamed(panel, Loc.T("L_ConstrainCrop")) is not null,
            "the Geometry band is missing its projection picker or its crop toggle under a reader name.");
        Require(ShownTextVisibility(panel, Loc.T("L_GeometryRefused")) != Visibility.Visible,
            "the band warns about a bend it has not been asked to make.");
        report["cameraRawGeometryRowsAreNamed"] = "passed";

        var capture = scene.Snapshot.Images[id]!;
        var offsetRow = SliderNamed(panel, Loc.T("L_GeometryOffsetX"))!;
        // Twenty of the offset is 15% of the frame: three whole pixels to the right, which is the one mapping
        // every sampler agrees on. The empty band the slide leaves behind is the port's own, as on the Mac.
        offsetRow.Value = 20;
        Require(await WaitUntilAsync(() => PixelAt(scene.Snapshot.Images[id]!, 3, 0) == PixelAt(capture, 0, 0)
            && AlphaAt(scene.Snapshot.Images[id]!, 0, 50) == 0, 8000),
            "moving the offset slider never published a slid layer.");
        var slid = scene.Snapshot.Images[id]!;
        for (int y = 0; y < 100; y += 7)
            for (int x = 3; x < 100; x += 5)
                Require(PixelAt(slid, x, y) == PixelAt(capture, x - 3, y),
                    $"the three-pixel slide is wrong at ({x},{y}): {PixelAt(slid, x, y)} against {PixelAt(capture, x - 3, y)}.");
        Require(AlphaAt(slid, 0, 0) == 0 && AlphaAt(slid, 2, 99) == 0, "the slid frame did not leave its empty edge.");
        report["cameraRawGeometryOffsetSlidesTheLayerWholePixels"] = "passed";

        offsetRow.Value = 0;
        Require(await WaitUntilAsync(() => PixelAt(scene.Snapshot.Images[id]!, 0, 0) == PixelAt(capture, 0, 0)
            && AlphaAt(scene.Snapshot.Images[id]!, 0, 0) == 255, 8000), "the reset never brought the layer back.");

        // The keystone: Vertical at −100 pulls the top edge in by 18% of the width on each side, so the frame's
        // top corners hold nothing while its bottom corners still hold the layer. Coverage, not color, is the part
        // no sampler can argue with — which is what a bend this deep is measurable by here.
        var verticalRow = SliderNamed(panel, Loc.T("L_GeometryVertical"))!;
        verticalRow.Value = -100;
        Require(await WaitUntilAsync(() => AlphaAt(scene.Snapshot.Images[id]!, 0, 0) == 0
            && AlphaAt(scene.Snapshot.Images[id]!, 0, 99) == 255, 8000),
            "the keystone never left the top corner empty while the bottom one stayed covered.");
        var keystone = scene.Snapshot.Images[id]!;
        Require(AlphaAt(keystone, 12, 0) == 0, "with the top edge 18 columns in, column 12 should still be outside the picture.");
        Require(AlphaAt(keystone, 50, 0) == 255 && AlphaAt(keystone, 99, 99) == 255,
            "the keystone punched holes inside the bend it was asked to make.");
        report["cameraRawGeometryKeystoneMovesOneEdgeOnly"] = "passed";

        // Rectilinear is the same shape of bend at 55% of the strength: the top edge stops pulling in sooner, and a
        // pixel 12 columns from the corner is covered again.
        var projection = ComboBoxNamed(panel, Loc.T("L_Projection"))!;
        projection.SelectedIndex = 1;
        Require(await WaitUntilAsync(() => AlphaAt(scene.Snapshot.Images[id]!, 12, 0) > 0, 8000),
            "switching the projection changed nothing in the pixels, so the picker is not reaching the bend.");
        var rectilinear = scene.Snapshot.Images[id]!;
        Require(AlphaAt(rectilinear, 12, 0) > 0 && AlphaAt(rectilinear, 0, 99) == 255,
            $"the gentler projection did not keep the edges where the keystone left them: {CameraRawGeometry.BoundsOf(rectilinear)}.");
        projection.SelectedIndex = 0;
        verticalRow.Value = 0;
        Require(await WaitUntilAsync(() => AlphaAt(scene.Snapshot.Images[id]!, 0, 0) == 255, 8000),
            "the band never let go of the keystone.");
        report["cameraRawGeometryProjectionCutsTheBend"] = "passed";

        // Scale at −100 pinches every corner onto the center: the port refuses it and says so, rather than
        // publishing what Core Image would do to a quad with no area.
        var scaleRow = SliderNamed(panel, Loc.T("L_GeometryScale"))!;
        scaleRow.Value = -100;
        Require(ShownTextVisibility(panel, Loc.T("L_GeometryRefused")) == Visibility.Visible,
            "the bend collapsed the picture and the band kept the refusal to itself.");
        Require(await WaitUntilAsync(() => !ReferenceEquals(scene.Snapshot.Images[id], rectilinear), 8000),
            "the refused bend published nothing at all, so the preview pipeline cannot be told from a stall.");
        var refused = scene.Snapshot.Images[id]!;
        Require(PixelAt(refused, 0, 0) == PixelAt(capture, 0, 0) && AlphaAt(refused, 50, 50) == 255,
            "a refused geometry still changed the layer.");
        scaleRow.Value = -50;
        Require(ShownTextVisibility(panel, Loc.T("L_GeometryRefused")) != Visibility.Visible,
            "the band still warns about a bend that now has an area to warp.");
        Require(await WaitUntilAsync(() => AlphaAt(scene.Snapshot.Images[id]!, 0, 0) == 0, 8000),
            "halving the picture never left the frame's corner empty.");
        var shrunk = scene.Snapshot.Images[id]!;
        Require(CameraRawGeometry.BoundsOf(shrunk) == (25, 25, 75, 75),
            $"a half-scale bend covers {CameraRawGeometry.BoundsOf(shrunk)}, the corner math says 25…75.");
        report["cameraRawGeometryRefusesACollapsedQuad"] = "passed";

        var crop = CheckBoxNamed(panel, Loc.T("L_ConstrainCrop"))!;
        crop.IsChecked = true;
        Require(await WaitUntilAsync(() => CameraRawGeometry.BoundsOf(scene.Snapshot.Images[id]!).Right
            - CameraRawGeometry.BoundsOf(scene.Snapshot.Images[id]!).Left >= 96, 8000),
            "Constrain Crop did not fit the shrunken picture back into the frame.");
        var refit = scene.Snapshot.Images[id]!;
        var bounds = CameraRawGeometry.BoundsOf(refit);
        Require(bounds.Left == 0 && bounds.Top == 0 && bounds.Right - bounds.Left >= 96 && bounds.Bottom - bounds.Top >= 96,
            $"the trim and fit left {bounds}, which is not a frame filled again.");
        Require(refit.Width == capture.Width && refit.Height == capture.Height,
            $"Constrain Crop changed the layer's size to {refit.Width}×{refit.Height}.");
        report["cameraRawGeometryConstrainCropFillsTheFrameAgain"] = "passed";
    }

    /// <summary>CR-d2: Upright ▸ Guided, driven through the panel and through the three functions the canvas calls.
    /// The solve and the corner arithmetic are locked in Core
    /// (Windows/tests/Compositor.Core.Tests/CameraRawGeometryTests.cs, numbered from
    /// Windows/tools/geometry-golden.py); what this group adds is the round trip — that the numbers a
    /// press-drag-release files are the ones the Mac's picker files, that a drawn pair lands on the layer as
    /// exactly the pixels the Vertical slider alone produces, and that the line list belongs to the settings
    /// rather than to the mode picker. A probe cannot inject a pointer, so the drag goes through
    /// BeginCameraRawGuide / ContinueCameraRawGuide / CommitCameraRawGuide, which are the same three the canvas
    /// routes to (MainWindow.xaml.cs:2351, 2474, 2671); the routing itself stays outside this evidence.</summary>
    private async Task VerifyCameraRawGuidesAsync(Dictionary<string, object> report)
    {
        var scene = SpeckleScene();
        OpenDocument(scene); await WaitForFramesAsync();
        var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        var panel = OpenCameraRawPanel(scene, id);
        var band = CameraRawBand(panel, Loc.T("L_Geometry"));
        Require(band is not null, "the Camera Raw panel has no Geometry group.");
        band!.IsExpanded = true;
        band.UpdateLayout();
        await WaitForFramesAsync();
        var picker = ComboBoxNamed(panel, Loc.T("L_Upright"));
        var draw = ToggleButtonNamed(panel, Loc.T("A_DrawGuides"));
        var clear = ButtonNamed(panel, Loc.T("A_ClearGuides"));
        var verticalRow = SliderNamed(panel, Loc.T("L_GeometryVertical"));
        Require(picker is not null && draw is not null && clear is not null && verticalRow is not null,
            "the Geometry band is missing its Upright picker, its Draw Guides or Clear Guides button, or its Vertical row "
            + $"(picker {picker is not null}, draw {draw is not null}, clear {clear is not null}, slider {verticalRow is not null}).");
        var capture = scene.Snapshot.Images[id]!;
        bool At(double actual, double expected) => Math.Abs(actual - expected) < 1e-9;
        // Byte-for-byte, because both sides of every comparison below go through the same sampler: the claim is
        // about which numbers reached the grade, and nothing weaker says that.
        void Press(Button button)
        {
            var invoke = (Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)new Microsoft.UI.Xaml.Automation.Peers
                .ButtonAutomationPeer(button).GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke);
            invoke.Invoke();
        }
        bool Drawn(double startX, double startY, double endX, double endY)
        {
            if (!BeginCameraRawGuide(new PointD(startX, startY))) return false;
            ContinueCameraRawGuide(new PointD(endX, endY));
            return CommitCameraRawGuide();
        }

        // --- Guided rows, and what a dragged line is filed as ---
        Require(picker!.SelectedIndex == 0, "a fresh Camera Raw session does not open Upright on Off.");
        Require(draw!.Visibility == Visibility.Collapsed && clear!.Visibility == Visibility.Collapsed,
            "the guided rows show themselves while Upright is still Off.");
        Require(ShownTextVisibility(panel, Loc.T("L_GuidesDragHint")) != Visibility.Visible,
            "the band explains a drag nobody has armed.");
        Require(!BeginCameraRawGuide(new PointD(20, 30)), "the canvas hands a click to the guide drawer while Upright is Off.");
        picker.SelectedIndex = 1;
        Require(draw.Visibility == Visibility.Visible, "choosing Guided did not bring out the Draw Guides button.");
        Require(clear!.Visibility == Visibility.Collapsed, "the band offers to clear guides that have not been drawn.");
        draw.IsChecked = true;
        Require(cameraRawDrawsGuide, "arming Draw Guides left the canvas not listening for the press.");
        Require(ShownTextVisibility(panel, Loc.T("L_GuidesDragHint")) == Visibility.Visible,
            "the drawer is armed and the band never said what a drag does.");
        Require(BeginCameraRawGuide(new PointD(20, 30)), "the armed drawer refused a point inside the layer.");
        // The stored numbers are the picker's, in the space the Mac's picker hands over: normalized with y counted
        // down from the top (CameraRaw.swift:601-607), which is why (20,30) files 0.3 rather than 0.7.
        Require(cameraRawGuideDraft is { } opened && At(opened.StartX, 0.2) && At(opened.StartY, 0.3)
                && At(opened.EndX, 0.2) && At(opened.EndY, 0.3),
            $"the press at (20,30) opened {cameraRawGuideDraft}, and the Mac's picker files (0.2, 0.3) at both ends.");
        ContinueCameraRawGuide(new PointD(80, 30));
        Require(cameraRawGuideDraft is { } dragged && At(dragged.EndX, 0.8) && At(dragged.EndY, 0.3),
            $"dragging to (80,30) left the far end at {cameraRawGuideDraft?.EndX}, {cameraRawGuideDraft?.EndY}.");
        ContinueCameraRawGuide(new PointD(140, 50));
        Require(cameraRawGuideDraft is { } held && At(held.EndX, 0.8) && At(held.EndY, 0.3),
            "a drag that left the layer moved the line instead of dropping the point, as the Mac's guard does.");
        var level = cameraRawGuideDraft!.Value;
        var beforeFirst = scene.Snapshot.Images[id]!;
        Require(CommitCameraRawGuide(), "releasing over a drafted line refused to commit it.");
        Require(cameraRawGuideDraft is null, "committing left the drafted line under the cursor.");
        Require(cameraRawDraft!.GeometryGuides.SequenceEqual(new[] { level }),
            $"the commit filed {cameraRawDraft!.GeometryGuides.Count} lines: [{string.Join(", ", cameraRawDraft!.GeometryGuides)}].");
        Require(await WaitUntilAsync(() => !ReferenceEquals(scene.Snapshot.Images[id], beforeFirst), 8000),
            "the first commit published no preview, so the round trip cannot be told from a stall.");

        // A line already under the cursor survives the mode picker moving off it (the picker stops the drawing,
        // CameraRawGeometryCalibrationControls.swift:57) and the release still files it — and puts Guided back,
        // because a line left on Off is a line the solve will not read (CameraRaw.swift:596).
        Require(BeginCameraRawGuide(new PointD(60, 70)), "the drawer refused a second press.");
        picker.SelectedIndex = 0;
        Require(!cameraRawDrawsGuide, "moving Upright off left the drawer listening for the drag.");
        Require(cameraRawGuideDraft is not null, "leaving Guided threw away the line already under the cursor.");
        // The drafted line stays put once the mode let go of the drawing: the Mac's continue guard reads the same
        // flag the button toggles (CameraRaw.swift:585), so a drag that arrives after the switch moves nothing.
        ContinueCameraRawGuide(new PointD(70, 75));
        Require(cameraRawGuideDraft is { } frozen && At(frozen.EndX, 0.6) && At(frozen.EndY, 0.7),
            $"a drag after the mode moved off moved the line to {cameraRawGuideDraft?.EndX}, {cameraRawGuideDraft?.EndY}.");
        var beforeSecond = scene.Snapshot.Images[id]!;
        Require(CommitCameraRawGuide(), "the release over a drafted line refused to commit after the mode moved off.");
        Require(cameraRawDraft!.GeometryGuides.Count == 2
                && cameraRawDraft!.GeometryUpright == CameraRawSettings.GeometryUprightGuided,
            $"a line committed while the picker sat on Off left {cameraRawDraft!.GeometryGuides.Count} lines "
            + $"and mode {cameraRawDraft!.GeometryUpright}.");
        Require(picker.SelectedIndex == 1, $"the commit chose Guided but the picker row still reads {picker.SelectedIndex}.");
        Require(await WaitUntilAsync(() => !ReferenceEquals(scene.Snapshot.Images[id], beforeSecond), 8000),
            "the commit that switched the mode published no preview.");
        // Neither line bends anything: one is level, so the rotate it solves to is zero, and the other is a click
        // without a drag, which normalization drops (CameraRawGeometryCalibration.swift:60-62).
        Require(Differ(scene.Snapshot.Images[id]!, capture) < 0,
            $"a level line and a zero-length one bent the layer at {Where(Differ(scene.Snapshot.Images[id]!, capture), 100)}.");
        report["cameraRawGuidesArmAndDrawALine"] = "passed";

        // --- The pair of lines reaches the layer as the number it solves to ---
        Press(clear!);
        Require(cameraRawDraft!.GeometryGuides.Count == 0,
            $"Clear Guides emptied its own button but left {cameraRawDraft!.GeometryGuides.Count} lines filed.");
        draw.IsChecked = true;
        Require(Drawn(10, 30, 90, 30) && Drawn(40, 20, 40, 80), "the level-and-plumb pair refused to be drawn.");
        // A level line solves to no rotation and a plumb line drawn downward to +25 of vertical
        // (CameraRawGeometryCalibration.swift:112-132), so Guided with the sliders at rest has to produce the
        // bytes the Vertical slider at 25 produces with Upright left on Off.
        Require(await WaitUntilAsync(() => Differ(scene.Snapshot.Images[id]!, capture) >= 0, 8000),
            "a level-and-plumb pair published a layer that is still the untouched original.");
        var guidedBent = scene.Snapshot.Images[id]!.Clone();
        picker.SelectedIndex = 0;
        Require(await WaitUntilAsync(() => Differ(scene.Snapshot.Images[id]!, capture) < 0, 8000),
            "moving Upright off never let go of the bend the drawn lines had made.");
        verticalRow!.Value = 25;
        Require(await WaitUntilAsync(() => Differ(scene.Snapshot.Images[id]!, capture) >= 0, 8000),
            "the Vertical slider on Off bent nothing, so the comparison below has no other side.");
        Require(Differ(scene.Snapshot.Images[id]!, guidedBent) < 0,
            $"the drawn pair is not the +25 keystone it solves to: first difference at "
            + $"{Where(Differ(scene.Snapshot.Images[id]!, guidedBent), 100)}.");
        // And Guided adds to the sliders instead of replacing them: 10 of Vertical plus the pair's 25 is the same
        // layer as 35 on the sliders alone. One change per wait, so every comparison below reads a settled layer.
        var beforeMode = scene.Snapshot.Images[id]!;
        picker.SelectedIndex = 1;
        Require(await WaitUntilAsync(() => !ReferenceEquals(scene.Snapshot.Images[id]!, beforeMode), 8000),
            "putting the mode back on Guided over a filed pair published no new layer.");
        var beforeTyped = scene.Snapshot.Images[id]!;
        verticalRow.Value = 10;
        Require(await WaitUntilAsync(() => !ReferenceEquals(scene.Snapshot.Images[id]!, beforeTyped), 8000),
            "moving the Vertical slider while Guided published no new layer.");
        var guidedAdds = scene.Snapshot.Images[id]!.Clone();
        picker.SelectedIndex = 0;
        var backOnOff = scene.Snapshot.Images[id]!;
        Require(await WaitUntilAsync(() => !ReferenceEquals(scene.Snapshot.Images[id]!, backOnOff), 8000),
            "taking the mode off over the same typed number published no new layer.");
        verticalRow.Value = 35;
        var offAgain = scene.Snapshot.Images[id]!;
        Require(await WaitUntilAsync(() => !ReferenceEquals(scene.Snapshot.Images[id]!, offAgain), 8000),
            "typing 35 with the mode off published no new layer.");
        Require(Differ(scene.Snapshot.Images[id]!, guidedAdds) < 0,
            "Guided with 10 typed and 25 drawn is not the same layer as Off with 35 typed: first difference at "
            + $"{Where(Differ(scene.Snapshot.Images[id]!, guidedAdds), 100)}.");
        // Which end the plumb line is drawn from is the sign, because the Mac's solve reads atan2 of the line as
        // filed rather than an undirected angle (CameraRawGeometryCalibration.swift:126-128): the same line drawn
        // upward asks for −25 instead of +25.
        Press(clear);
        verticalRow.Value = 0;
        Require(await WaitUntilAsync(() => Differ(scene.Snapshot.Images[id]!, capture) < 0, 8000),
            "Upright off with nothing typed never gave the layer back.");
        picker.SelectedIndex = 1;
        draw.IsChecked = true;
        Require(Drawn(10, 30, 90, 30) && Drawn(40, 80, 40, 20), "the plumb line drawn upward refused to be drawn.");
        Require(await WaitUntilAsync(() => Differ(scene.Snapshot.Images[id]!, capture) >= 0, 8000),
            "the pair drawn upward left the layer flat, so its keystone never reached the grade.");
        var guidedUp = scene.Snapshot.Images[id]!.Clone();
        picker.SelectedIndex = 0;
        verticalRow.Value = -25;
        Require(await WaitUntilAsync(() => Differ(scene.Snapshot.Images[id]!, guidedUp) < 0, 8000),
            "the pair drawn upward never became the −25 keystone the Mac's solve asks for: last difference at "
            + $"{Where(Differ(scene.Snapshot.Images[id]!, guidedUp), 100)}.");
        report["cameraRawGuidesBendTheLayerTheWayTheSlidersDo"] = "passed";

        // --- The list belongs to the settings, not to the mode or the button ---
        // Group 2 left a typed −25 and a filed pair behind, so this block first takes both back: below, the only
        // thing that can bend the layer is the pair drawn here.
        picker.SelectedIndex = 1;
        Press(clear!);
        Require(cameraRawDraft!.GeometryGuides.Count == 0,
            $"the band is back on Guided over a list it had emptied, and now holds {cameraRawDraft!.GeometryGuides.Count} lines.");
        verticalRow!.Value = 0;
        Require(await WaitUntilAsync(() => Differ(scene.Snapshot.Images[id]!, capture) < 0, 8000),
            $"the typed number and the emptied list never gave the layer back: still differs at "
            + $"{Where(Differ(scene.Snapshot.Images[id]!, capture), 100)}.");
        draw.IsChecked = true;
        Require(Drawn(10, 30, 90, 30) && Drawn(40, 20, 40, 80), "the bending pair refused to be drawn again.");
        Require(await WaitUntilAsync(() => Differ(scene.Snapshot.Images[id]!, capture) >= 0, 8000),
            "Guided with a bending pair drawn published the untouched original.");
        var bentAgain = scene.Snapshot.Images[id]!.Clone();
        // Clearing empties the list and nothing else: drawing stays armed, because the user who just threw away a
        // bad line is about to drag the next one (CameraRawGeometryCalibrationControls.swift:32-34).
        Press(clear);
        Require(await WaitUntilAsync(() => Differ(scene.Snapshot.Images[id]!, capture) < 0, 8000),
            "Clear Guides left the bend on the layer.");
        Require(cameraRawDraft!.GeometryGuides.Count == 0, "Clear Guides hid its own button over a list it did not empty.");
        Require(clear.Visibility == Visibility.Collapsed, "the list is empty and the band still offers to clear it.");
        Require(cameraRawDrawsGuide && draw.IsChecked == true, "Clear Guides took the drawing off, which the Mac leaves on.");
        Require(ShownTextVisibility(panel, Loc.T("L_GuidesDragHint")) == Visibility.Visible,
            "Clear Guides hid the caption that goes with the armed drawer.");
        Require(Drawn(10, 30, 90, 30) && Drawn(40, 20, 40, 80), "the drawer stopped taking lines after its own clear button.");
        picker.SelectedIndex = 0;
        Require(await WaitUntilAsync(() => Differ(scene.Snapshot.Images[id]!, capture) < 0, 8000),
            "Off kept bending the layer with lines it is not supposed to read.");
        Require(cameraRawDraft!.GeometryGuides.Count == 2,
            $"Off threw away the {cameraRawDraft!.GeometryGuides.Count} lines it was only meant to stop reading.");
        Require(draw.Visibility == Visibility.Collapsed && clear.Visibility == Visibility.Collapsed
                && ShownTextVisibility(panel, Loc.T("L_GuidesDragHint")) != Visibility.Visible,
            "Upright is Off and the band still shows the rows that only Guided has a use for.");
        picker.SelectedIndex = 1;
        Require(await WaitUntilAsync(() => Differ(scene.Snapshot.Images[id]!, bentAgain) < 0, 8000),
            $"coming back to Guided did not put the bend back: first difference at "
            + $"{Where(Differ(scene.Snapshot.Images[id]!, bentAgain), 100)} against the layer the same two lines made.");
        Require(clear.Visibility == Visibility.Visible, "the lines came back but the button to clear them did not.");
        report["cameraRawGuidesBelongToTheSettingsNotTheMode"] = "passed";
    }

    /// <summary>Fourteen columns per band so a click lands in a known color: the green the golden rows are
    /// picked on, deep blue as its neighbour-by-one (the dimming rows read it because 0.35 of an even channel
    /// is a rounding tie), red, and a band of nothing at all — alpha zero, the pixel the eyedropper refuses.</summary>
    private static EditorDocument MixerBandsScene()
    {
        var scene = EditorDocument.Create(64, 48);
        var layer = scene.Snapshot.Manifest.Layers[0];
        var image = new Surface(64, 48);
        var bands = new[] { ((byte)60, (byte)220, (byte)90, (byte)255), ((byte)71, (byte)91, (byte)221, (byte)255),
            ((byte)220, (byte)70, (byte)70, (byte)255), ((byte)0, (byte)0, (byte)0, (byte)0) };
        for (int y = 0; y < 48; y++)
            for (int x = 0; x < 64; x++)
            {
                int at = y * image.Stride + x * 4;
                var (r, g, b, a) = bands[x / 16];
                image.Pixels[at] = r;
                image.Pixels[at + 1] = g;
                image.Pixels[at + 2] = b;
                image.Pixels[at + 3] = a;
            }
        ((Dictionary<Guid, Surface>)scene.Snapshot.Images)[layer.Id] = image;
        return scene;
    }

    /// <summary>CR-e: the Color Mixer's pages and its targeted points, driven through the panel. The lobe
    /// arithmetic is locked in Core (Windows/tests/Compositor.Core.Tests/CameraRawMixerTests.cs, numbered from
    /// Windows/tools/targeted-golden.py); what a probe adds is the round trip the panel and the eyedropper make
    /// — that a page lists the rows the Mac lists, that a click files the clicked pixel and not its neighbour,
    /// that a picked color moves nothing until a shift is moved, that the dim is a preview-only pass that OK
    /// does not carry into the layer, and that the ninth color is refused. A probe cannot inject a pointer, so
    /// the click goes through SampleCameraRawPointColor, the same function the canvas routes to
    /// (MainWindow.xaml.cs:2344); the routing itself stays outside this evidence, as it did for CR-b, CR-c and
    /// CR-d2.</summary>
    private async Task VerifyCameraRawMixerAsync(Dictionary<string, object> report)
    {
        var scene = MixerBandsScene();
        OpenDocument(scene); await WaitForFramesAsync();
        var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        var panel = OpenCameraRawPanel(scene, id);
        var band = CameraRawBand(panel, Loc.T("L_ColorMixer"));
        Require(band is not null, "the Camera Raw panel has no Color Mixer group.");
        band!.IsExpanded = true;
        band.UpdateLayout();
        await WaitForFramesAsync();
        var page = ComboBoxNamed(panel, Loc.T("L_MixerPage"));
        var component = ComboBoxNamed(panel, Loc.T("L_MixerComponent"));
        var family = ComboBoxNamed(panel, Loc.T("L_MixerFamily"));
        var picker = ToggleButtonNamed(panel, Loc.T("A_PointColorPicker"));
        Require(page is not null && component is not null && family is not null && picker is not null,
            "the Color Mixer band is missing a reader-named page picker, component picker, family picker or eyedropper "
            + $"(page {page is not null}, component {component is not null}, family {family is not null}, picker {picker is not null}).");

        // --- The three pages, and what each one lists ---
        Require(page!.SelectedIndex == 0, "the mixer does not open on the Mac's first page, HSL.");
        Require(family!.Visibility == Visibility.Collapsed && picker!.Visibility == Visibility.Collapsed,
            "the HSL page shows the rows that belong to the other two pages.");
        var hslRows = CameraRawSettings.MixerNames
            .Select(name => $"{name} {CameraRawSettings.MixerComponentNames[component!.SelectedIndex]}")
            .Where(label => SliderNamed(panel, label) is null).ToList();
        Require(hslRows.Count == 0,
            $"the HSL page built eight families of one component and {hslRows.Count} of them are missing: "
            + $"[{string.Join(", ", hslRows)}].");
        page.SelectedIndex = 1;
        Require(component!.Visibility == Visibility.Collapsed && SliderNamed(panel, Loc.T("L_Hue")) is not null
                && SliderNamed(panel, Loc.T("L_Luminance")) is not null,
            "the Color page does not show one family's three sliders, or the HSL component picker is still in the way.");
        page.SelectedIndex = 0;
        component.SelectedIndex = 2;
        Require(SliderNamed(panel, $"{CameraRawSettings.MixerNames[0]} Luminance") is not null
                && SliderNamed(panel, $"{CameraRawSettings.MixerNames[0]} Hue") is null,
            "moving the component picker did not change which component the eight rows are listing.");
        component.SelectedIndex = 0;
        Require(SliderNamed(panel, $"{CameraRawSettings.MixerNames[0]} Hue") is not null
                && SliderNamed(panel, $"{CameraRawSettings.MixerNames[0]} Luminance") is null,
            "putting the component picker back did not bring the eight Hue rows.");
        var hueRow = SliderNamed(panel, $"{CameraRawSettings.MixerNames[3]} Hue")!;
        var capture = scene.Snapshot.Images[id]!;
        // Byte-for-byte, because every side of each comparison below goes through the same kernel: the claim is
        // about which numbers reached the grade, and nothing weaker says that.
        hueRow.Value = 40;
        Require(await WaitUntilAsync(() => !ReferenceEquals(scene.Snapshot.Images[id], capture), 8000),
            "a family slider moved and no preview was published, so the row cannot be told from a stall.");
        Require(PixelAt(scene.Snapshot.Images[id]!, 8, 8) != PixelAt(capture, 8, 8),
            "the Greens family's Hue row left its own band untouched.");
        hueRow.Value = 0;
        Require(await WaitUntilAsync(() => PixelAt(scene.Snapshot.Images[id]!, 8, 8) == PixelAt(capture, 8, 8), 8000),
            "putting the family slider back never gave the band its color again.");
        // Choosing a page is the panel's own business: it moves no pixel, because the settings hold one list of
        // twenty-four family numbers however they are displayed (Filters.swift:308-313). The switch publishes
        // nothing at all, which is the other half of the same claim.
        var shown = scene.Snapshot.Images[id]!;
        page.SelectedIndex = 2;
        Require(ReferenceEquals(scene.Snapshot.Images[id], shown), "a page switch asked for a new grade.");
        Require(Differ(scene.Snapshot.Images[id]!, capture) < 0,
            "switching mixer pages moved the layer: " + Where(Differ(scene.Snapshot.Images[id]!, capture), 64));
        Require(picker!.Visibility == Visibility.Visible
                && ShownTextVisibility(panel, Loc.T("L_PointColorClickHint")) != Visibility.Visible,
            "the Point Color page shows its eyedropper and explains a click nobody has armed.");
        Require(pointRowsVisible(panel) == 0,
            "the Point Color page lists the six rows before any color has been picked, over an empty list.");
        report["cameraRawMixerPagesListWhatTheMacLists"] = "passed";

        // --- The eyedropper, and what one click files ---
        Require(!SampleCameraRawPointColor(new PointD(8, 8)), "the picker took a click while it was not armed.");
        picker.IsChecked = true;
        Require(cameraRawSamplesPointColor, "arming the picker left the canvas not listening for the click.");
        Require(ShownTextVisibility(panel, Loc.T("L_PointColorClickHint")) == Visibility.Visible,
            "the picker is armed and the band never said what a click does.");
        Require(!SampleCameraRawPointColor(new PointD(56, 8)), "the picker filed a pixel with no alpha at all.");
        Require(cameraRawDraft!.MixerPoints.Count == 0, "a refused click still filed a color.");
        Require(!SampleCameraRawPointColor(new PointD(200, 8)), "the picker filed a point outside the layer.");
        Require(cameraRawDraft!.MixerPoints.Count == 0, "a point outside the layer became a stored color.");
        // The green band's own pixel: (60,220,90) is hue 131.25, chroma-over-max 160/220 and (max+min)/2 140/255,
        // which is what CameraRaw.swift:609-637 files — not the HSL saturation the kernel then compares against.
        Require(SampleCameraRawPointColor(new PointD(8, 8)), "the picker refused a pixel of the layer under it.");
        Require(cameraRawDraft!.MixerPoints.Count == 1, $"one click filed {cameraRawDraft!.MixerPoints.Count} colors.");
        var filed = cameraRawDraft!.MixerPoints[0];
        AssertPickedGreen(filed, "the first picked color");
        Require(filed.HueShift == 0 && filed.SaturationShift == 0 && filed.LuminanceShift == 0,
            "a picked color arrives with a move already asked of it.");
        Require(pointRowsVisible(panel) == 6, "the one picked color did not bring its six rows.");
        // A picked color on its own is not an adjustment (CameraRawColor.swift:159-162): filing it must move
        // nothing, or every stray click would be a grade.
        var beforeShift = scene.Snapshot.Images[id]!;
        Require(Differ(beforeShift, capture) < 0, "filing a color bent the layer before any shift was moved.");
        var shiftRow = SliderNamed(panel, Loc.T("L_PointHueShift"))!;
        shiftRow.Value = 50;
        Require(await WaitUntilAsync(() => PixelAt(scene.Snapshot.Images[id]!, 8, 8) == (60, 220, 127), 8000),
            $"the picked green's Hue Shift row never reached the layer; it holds {PixelAt(scene.Snapshot.Images[id]!, 8, 8)}.");
        Require(PixelAt(scene.Snapshot.Images[id]!, 24, 8) == PixelAt(capture, 24, 8)
                && PixelAt(scene.Snapshot.Images[id]!, 40, 8) == PixelAt(capture, 40, 8),
            "a point centred on the green band moved the blue or the red band too, so its own range was not the limit.");
        report["cameraRawMixerPickerFilesTheClickedPixel"] = "passed";

        // --- Appending, re-picking, selecting, and the eighth color ---
        // Every pick or chip click rebuilds the six rows, so a row found once is a detached slider a moment
        // later; each read goes back through the panel.
        Slider PointRow(string label) => SliderNamed(panel, Loc.T(label))!;
        cameraRawPointIndex = cameraRawDraft!.MixerPoints.Count; // the panel's own append path: nothing selected at that index
        Require(SampleCameraRawPointColor(new PointD(8, 8)), "a click with no point selected at the index filed nothing.");
        Require(cameraRawDraft!.MixerPoints.Count == 2, $"an unselected index filed {cameraRawDraft!.MixerPoints.Count} colors.");
        Require(cameraRawPointIndex == 1, $"appending left the selected index at {cameraRawPointIndex}.");
        AssertPicked(cameraRawDraft!.MixerPoints[1], "the appended color", 131.25, 160.0 / 220.0, 140.0 / 255.0);
        var appendedRow = PointRow("L_PointHueShift");
        Require(Math.Abs(appendedRow.Value) < 1e-6,
            $"after appending, the six rows still show the first point's {appendedRow.Value}.");
        Require(pointChips(panel) == 2, $"the chip row lists {pointChips(panel)} colors for a list of two.");
        var firstChip = ButtonNamed(panel, $"{Loc.T("A_PointColorChip")} 1");
        Require(firstChip is not null, "the first picked color has no chip a reader can reach.");
        PressChip(firstChip!);
        Require(cameraRawPointIndex == 0, $"the first chip left the selected index at {cameraRawPointIndex}.");
        var firstPointRow = PointRow("L_PointHueShift");
        Require(Math.Abs(firstPointRow.Value - 50) < 1e-6,
            $"choosing the first chip left the rows at {firstPointRow.Value}, which is not that point's 50.");
        firstPointRow.Value = 0;
        Require(await WaitUntilAsync(() => PixelAt(scene.Snapshot.Images[id]!, 8, 8) == PixelAt(capture, 8, 8), 8000),
            "putting the first point's shift back never gave its band its color again.");
        Require(Math.Abs(cameraRawDraft!.MixerPoints[0].HueShift) < 1e-6 && Math.Abs(cameraRawDraft!.MixerPoints[1].HueShift) < 1e-6,
            "moving one point's row wrote another point as well.");
        // The green band answers to the second point now, and to nothing else.
        PressChip(ButtonNamed(panel, $"{Loc.T("A_PointColorChip")} 2")!);
        Require(cameraRawPointIndex == 1, $"the second chip left the selected index at {cameraRawPointIndex}.");
        PointRow("L_PointHueShift").Value = 50;
        Require(await WaitUntilAsync(() => PixelAt(scene.Snapshot.Images[id]!, 8, 8) == (60, 220, 127), 8000),
            "the second point's shift never reached the layer on its own.");
        Require(Math.Abs(cameraRawDraft!.MixerPoints[1].HueShift - 50) < 1e-6
                && Math.Abs(cameraRawDraft!.MixerPoints[0].HueShift) < 1e-6,
            "moving a row wrote the wrong point.");
        Require(SampleCameraRawPointColor(new PointD(24, 8)), "re-picking refused the pixel under the cursor.");
        Require(cameraRawDraft!.MixerPoints.Count == 2, $"re-picking filed a third color: {cameraRawDraft!.MixerPoints.Count}.");
        var refilled = cameraRawDraft!.MixerPoints[1];
        Require(Math.Abs(refilled.Hue - 232) < 1e-6 && Math.Abs(refilled.Saturation - 150.0 / 221.0) < 1e-6
                && Math.Abs(refilled.Luminance - 146.0 / 255.0) < 1e-6,
            $"re-picking over the deep blue band filed ({refilled.Hue}, {refilled.Saturation}, {refilled.Luminance}).");
        Require(refilled.HueShift == 50, "re-picking threw away the shift that was already asked of this color.");
        Require(await WaitUntilAsync(() => PixelAt(scene.Snapshot.Images[id]!, 24, 8) == (88, 71, 221), 8000),
            $"the kept shift never moved the band the color was re-picked from: {PixelAt(scene.Snapshot.Images[id]!, 24, 8)}.");
        Require(PixelAt(scene.Snapshot.Images[id]!, 8, 8) == PixelAt(capture, 8, 8),
            "a point re-picked onto another band kept moving the one it came from.");
        while (cameraRawDraft!.MixerPoints.Count < CameraRawMixer.PointsMax)
        {
            cameraRawPointIndex = cameraRawDraft!.MixerPoints.Count;
            Require(SampleCameraRawPointColor(new PointD(12, 20)), $"the pick that would reach number {cameraRawDraft!.MixerPoints.Count + 1} was refused.");
        }
        Require(cameraRawDraft!.MixerPoints.Count == CameraRawMixer.PointsMax,
            $"the list stopped at {cameraRawDraft!.MixerPoints.Count}.");
        cameraRawPointIndex = CameraRawMixer.PointsMax;
        Require(SampleCameraRawPointColor(new PointD(8, 20)), "the ninth pick neither filed a color nor answered.");
        Require(cameraRawDraft!.MixerPoints.Count == CameraRawMixer.PointsMax,
            $"a ninth color joined the list: {cameraRawDraft!.MixerPoints.Count}.");
        Require(pointChips(panel) == CameraRawMixer.PointsMax,
            $"the chip row lists {pointChips(panel)} colors for a list of {CameraRawMixer.PointsMax}.");
        report["cameraRawMixerPicksAppendAndStopAtEight"] = "passed";

        // --- Visualize is a preview pass, not a grade ---
        // Both points at rest, so the dim is the only thing that can move a pixel and the deep blue band reads
        // its own capture bytes.
        PressChip(ButtonNamed(panel, $"{Loc.T("A_PointColorChip")} 2")!);
        SliderNamed(panel, Loc.T("L_PointHueShift"))!.Value = 0;
        Require(await WaitUntilAsync(() => PixelAt(scene.Snapshot.Images[id]!, 24, 8) == PixelAt(capture, 24, 8), 8000),
            "the second point's shift never let go of its band.");
        PressChip(ButtonNamed(panel, $"{Loc.T("A_PointColorChip")} 1")!);
        var visualize = CheckBoxNamed(panel, Loc.T("L_VisualizeRange"))!;
        visualize.IsChecked = true;
        Require(cameraRawDraft!.MixerPoints[0].Visualize, "the Visualize Range row did not write its own point.");
        Require(cameraRawDraft!.MixerPoints[1].Visualize == false, "one point's flag wrote another point's.");
        Require(await WaitUntilAsync(() => PixelAt(scene.Snapshot.Images[id]!, 24, 8) == (25, 32, 77), 8000),
            $"turning Visualize Range on never dimmed the band outside the point: {PixelAt(scene.Snapshot.Images[id]!, 24, 8)}.");
        Require(PixelAt(scene.Snapshot.Images[id]!, 8, 8) == PixelAt(capture, 8, 8),
            "the band the point was picked from was dimmed too, so the cut is not reading the point's own reach.");
        // The dim reads the color before anything moved it, so a grade the point makes does not lift it out
        // of the dimmed set — and the green band, which the point covers, keeps its grade beside it.
        PointRow("L_PointHueShift").Value = 50;
        Require(await WaitUntilAsync(() => PixelAt(scene.Snapshot.Images[id]!, 8, 8) == (60, 220, 127), 8000),
            $"the point's own band lost its grade to the dim: {PixelAt(scene.Snapshot.Images[id]!, 8, 8)}.");
        Require(PixelAt(scene.Snapshot.Images[id]!, 24, 8) == (25, 32, 77),
            $"the dimmed band came back with the grade: {PixelAt(scene.Snapshot.Images[id]!, 24, 8)}.");
        var dimmed = scene.Snapshot.Images[id]!;
        // OK is the exact result and the dim is not part of it: the Mac's caption promises as much
        // (CameraRawColorControls.swift:388), and Filters.swift:462-471 sets the index only on the preview job.
        // The two answers live on the sheet's host row now, so the old "last row holding a button is the Apply
        // row" hunt would press some other control.
        PressChip(CameraRawSheetButton(confirm: true));
        Require(await WaitUntilAsync(() => !ReferenceEquals(scene.Snapshot.Images[id], dimmed), 12000),
            "OK never replaced the preview the panel was looking at.");
        var committed = scene.Snapshot.Images[id]!;
        Require(PixelAt(committed, 24, 8) == PixelAt(capture, 24, 8),
            $"OK committed the Visualize Range dim into the layer: {PixelAt(committed, 24, 8)}.");
        Require(PixelAt(committed, 8, 8) == (60, 220, 127),
            $"OK dropped the grade the point was asked to make: {PixelAt(committed, 8, 8)}.");
        Require(PixelAt(committed, 40, 8) == PixelAt(capture, 40, 8),
            "OK painted a band no point covers.");
        report["cameraRawMixerVisualizeIsPreviewOnly"] = "passed";
    }

    /// <summary>Pressing a picked color's chip the only way a probe can: through its invoke pattern.</summary>
    private static void PressChip(Button chip)
    {
        var invoke = (Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)new Microsoft.UI.Xaml.Automation.Peers
            .ButtonAutomationPeer(chip).GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke);
        invoke.Invoke();
    }

    /// <summary>Four bands, one per thing the targeted drag keys on: the green whose tone the curve branch reads,
    /// a hue exactly 150 degrees — halfway between Greens and Aquas, so a drag there moves two families by a
    /// quarter each — red on the wheel's seam, and a band of nothing at all.</summary>
    private static EditorDocument TargetedBandsScene()
    {
        var scene = EditorDocument.Create(64, 48);
        var layer = scene.Snapshot.Manifest.Layers[0];
        var image = new Surface(64, 48);
        var bands = new[] { ((byte)60, (byte)220, (byte)90, (byte)255), ((byte)100, (byte)200, (byte)150, (byte)255),
            ((byte)220, (byte)70, (byte)70, (byte)255), ((byte)0, (byte)0, (byte)0, (byte)0) };
        for (int y = 0; y < 48; y++)
            for (int x = 0; x < 64; x++)
            {
                int at = y * image.Stride + x * 4;
                var (r, g, b, a) = bands[x / 16];
                image.Pixels[at] = r;
                image.Pixels[at + 1] = g;
                image.Pixels[at + 2] = b;
                image.Pixels[at + 3] = a;
            }
        ((Dictionary<Guid, Surface>)scene.Snapshot.Images)[layer.Id] = image;
        return scene;
    }

    /// <summary>CR-f: the Targeted Adjustment drag, driven through the three functions the canvas calls. The
    /// arithmetic is locked in Core (Windows/tests/Compositor.Core.Tests/CameraRawTargetedDragTests.cs, numbered
    /// from the drag section of Windows/tools/targeted-golden.py); what the panel adds is that a press samples the
    /// pixel under it, that every move recomputes from that press rather than from the last one, that the two arms
    /// cannot both listen, and that the rows the drag rewrote show the new numbers. As with CR-b, CR-c, CR-d2 and
    /// CR-e a probe cannot inject a pointer, so the routing in MainWindow.xaml.cs:2351-2364 stays outside this
    /// evidence and only its three callees are driven.</summary>
    private async Task VerifyCameraRawTargetedDragAsync(Dictionary<string, object> report)
    {
        var scene = TargetedBandsScene();
        OpenDocument(scene); await WaitForFramesAsync();
        var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        var panel = OpenCameraRawPanel(scene, id);
        foreach (var header in new[] { "L_Curve", "L_ColorMixer" })
        {
            var band = CameraRawBand(panel, Loc.T(header));
            Require(band is not null, $"the Camera Raw panel has no {header} group.");
            band!.IsExpanded = true;
            band.UpdateLayout();
        }
        await WaitForFramesAsync();
        var curveArm = ToggleButtonNamed(panel, Loc.T("A_TargetedCurve"));
        var mixerArm = ToggleButtonNamed(panel, Loc.T("A_TargetedMixer"));
        Require(curveArm is not null && mixerArm is not null,
            $"a Targeted Adjustment arm is missing from the panel (curve {curveArm is not null}, mixer {mixerArm is not null}).");
        var capture = scene.Snapshot.Images[id]!;
        var hueRow = new Func<string, Slider?>(label => SliderNamed(panel, label));

        // --- One arm at a time, and nothing until a press ---
        Require(!BeginCameraRawTargetedDrag(new PointD(24, 8)), "the canvas hands a press to a drag nobody armed.");
        Require(cameraRawDrag is null, "an unarmed press started a drag anyway.");
        mixerArm!.IsChecked = true;
        Require(cameraRawTargetsMixer && !cameraRawTargetsCurve,
            "arming the mixer did not take the curve arm off, as the Mac's own handler does.");
        curveArm!.IsChecked = true;
        Require(cameraRawTargetsCurve && !cameraRawTargetsMixer,
            $"arming the curve left the mixer listening too ({cameraRawTargetsMixer}).");
        Require(mixerArm.IsChecked == false, "the curve arm cleared its own flag but not the other button.");
        // ... and back the other way: whichever handler runs has to clear the other flag, not only its own.
        mixerArm.IsChecked = true;
        Require(cameraRawTargetsMixer && !cameraRawTargetsCurve,
            "arming the mixer again left the curve arm listening behind its own button.");
        Require(curveArm.IsChecked == false, "the mixer arm cleared its own flag but not the other button.");
        Require(cameraRawDrag is null && Differ(scene.Snapshot.Images[id]!, capture) < 0,
            "arming an arm moved the layer, though no press has happened yet.");
        Require(!BeginCameraRawTargetedDrag(new PointD(56, 8)), "a press on a transparent pixel started a drag.");
        Require(!BeginCameraRawTargetedDrag(new PointD(300, 8)), "a press outside the layer started a drag.");
        Require(cameraRawDrag is null, "a refused press left a drag behind to be moved.");
        report["cameraRawTargetedArmsOneDragAtATime"] = "passed";

        // --- The mixer arm: twenty pixels down is seven slider units, split by overlap ---
        // The mixer keeps its page across sessions as the Mac's edit does, and the previous group left it
        // on Point Color, where no family row exists at all.
        var page = ComboBoxNamed(panel, Loc.T("L_MixerPage"))!;
        var component = ComboBoxNamed(panel, Loc.T("L_MixerComponent"))!;
        page.SelectedIndex = 0;
        component.SelectedIndex = 0;
        curveArm.IsChecked = false;
        mixerArm.IsChecked = true;
        Require(BeginCameraRawTargetedDrag(new PointD(24, 8)), "the armed drag refused the pixel under it.");
        Require(cameraRawDrag is { } started && Math.Abs(started.Hue - 150) < 1e-6,
            $"the press did not sample the hue under the pointer: {cameraRawDrag?.Hue}.");
        DragCameraRawTargeted(new PointD(24, 28));
        Require(Math.Abs(cameraRawDraft!.MixerHue[3] + 1.75) < 1e-9
                && Math.Abs(cameraRawDraft!.MixerHue[4] + 1.75) < 1e-9,
            $"twenty pixels down over a 150-degree hue moved Greens to {cameraRawDraft!.MixerHue[3]} and "
            + $"Aquas to {cameraRawDraft!.MixerHue[4]}, and the Mac's window says -1.75 for both.");
        Require(Enumerable.Range(0, 8).Where(i => i != 3 && i != 4).All(i => cameraRawDraft!.MixerHue[i] == 0),
            "the drag reached families forty degrees away from the hue it started on.");
        Require(Math.Abs(hueRow("Greens Hue")!.Value + 1.75) < 1e-9,
            "the drag rewrote the numbers but the row a user reads still shows the old one.");
        Require(await WaitUntilAsync(() => Differ(scene.Snapshot.Images[id]!, capture) >= 0, 8000),
            "the drag moved the numbers and never reached the layer.");
        var atTwenty = cameraRawDraft!.MixerHue[3];
        // Back to the same place is the same number, whatever route the pointer took to get there.
        DragCameraRawTargeted(new PointD(24, 48));
        DragCameraRawTargeted(new PointD(24, 28));
        Require(Math.Abs(cameraRawDraft!.MixerHue[3] - atTwenty) < 1e-9,
            $"a drag that doubled back did not return to the number its own press implies ({cameraRawDraft!.MixerHue[3]}).");
        Require(Math.Abs(cameraRawDraft!.MixerHue[3] + 1.75) < 1e-9,
            "coming back to twenty pixels down is no longer seven slider units at a quarter weight.");
        EndCameraRawTargetedDrag();
        var released = cameraRawDraft!.MixerHue[3];
        DragCameraRawTargeted(new PointD(24, 8));
        Require(Math.Abs(cameraRawDraft!.MixerHue[3] - released) < 1e-9,
            "the release left a drag behind that the next move still answered to.");
        // The tab the drag writes is the tab the panel is showing, not whichever array is first.
        component.SelectedIndex = 1;
        Require(BeginCameraRawTargetedDrag(new PointD(24, 8)), "the second press refused its own pixel.");
        DragCameraRawTargeted(new PointD(24, 28));
        Require(Math.Abs(cameraRawDraft!.MixerSaturation[3] + 1.75) < 1e-9 && cameraRawDraft!.MixerHue[3] == released,
            $"on the Saturation tab the drag wrote hue {cameraRawDraft!.MixerHue[3]} and saturation "
            + $"{cameraRawDraft!.MixerSaturation[3]}; the Mac writes only the component being shown.");
        EndCameraRawTargetedDrag();
        // Back to rest through the rows, so the panel is what moves the numbers here too.
        component.SelectedIndex = 0;
        hueRow("Greens Hue")!.Value = 0;
        hueRow("Aquas Hue")!.Value = 0;
        component.SelectedIndex = 1;
        SliderNamed(panel, "Greens Saturation")!.Value = 0;
        SliderNamed(panel, "Aquas Saturation")!.Value = 0;
        Require(await WaitUntilAsync(() => Differ(scene.Snapshot.Images[id]!, capture) < 0, 8000),
            "with every family back at rest the layer still carries the drag's work.");
        report["cameraRawTargetedDragMovesTheFamiliesItOverlaps"] = "passed";

        // --- The curve arm: the tone under the pointer picks which point moves ---
        mixerArm.IsChecked = false;
        curveArm.IsChecked = true;
        // The band opens on the Mac's default page, Parametric (Filters.swift:308), and this group is about the
        // point page's branch, so the page has to be switched before the press means what the group says it means.
        var curvePageBox = ComboBoxNamed(panel, Loc.T("L_CurvePage"))!;
        Require(cameraRawCurvePage == CameraRawSettings.CurvePageParametric && curvePageBox.SelectedIndex == 0,
            $"the curve band opened on page {curvePageBox.SelectedIndex}, and the Mac defaults to Parametric.");
        curvePageBox.SelectedIndex = CameraRawSettings.CurvePagePoint;
        Require(!cameraRawTargetsMixer, "the curve arm left the mixer armed behind its own button.");
        Require(BeginCameraRawTargetedDrag(new PointD(8, 8)), "the curve press refused the pixel under it.");
        Require(Math.Abs(cameraRawDrag!.Value.Tone - (0.2126 * 60 / 255 + 0.7152 * 220 / 255 + 0.0722 * 90 / 255)) < 1e-9,
            $"the press sampled tone {cameraRawDrag?.Tone}, not the rec709 luminance of the pixel under it.");
        DragCameraRawTargeted(new PointD(8, 28));
        Require(Math.Abs(cameraRawDraft!.CurveRgb[1].Y - 0.93) < 1e-9 && cameraRawDraft!.CurveRgb[0].Y == 0
                && cameraRawDraft!.CurveRgb[1].X == 1,
            $"the drag at that tone wrote {cameraRawDraft!.CurveRgb[1]} and {cameraRawDraft!.CurveRgb[0]}, "
            + "and the Mac nudges the nearest point's y to 0.93.");
        Require(cameraRawDraft!.AdjustsCurve && !cameraRawDraft!.IsIdentity,
            "a curve the drag moved is still reported as no adjustment.");
        Require(await WaitUntilAsync(() => Differ(scene.Snapshot.Images[id]!, capture) >= 0, 8000),
            "the curve drag moved the draft and never reached the layer.");
        var dragged = scene.Snapshot.Images[id]!.Clone();
        // A channel the panel is not showing must not be the one that moves.
        var channel = ComboBoxNamed(panel, Loc.T("L_CurveChannel"))!;
        channel.SelectedIndex = 1;
        Require(BeginCameraRawTargetedDrag(new PointD(8, 8)), "the second curve press refused its pixel.");
        DragCameraRawTargeted(new PointD(8, 28));
        Require(Math.Abs(cameraRawDraft!.CurveRed[1].Y - 0.93) < 1e-9
                && Math.Abs(cameraRawDraft!.CurveRgb[1].Y - 0.93) < 1e-9,
            $"the Red channel left the RGB curve at {cameraRawDraft!.CurveRgb[1].Y} and wrote {cameraRawDraft!.CurveRed[1].Y}");
        EndCameraRawTargetedDrag();
        channel.SelectedIndex = 0;
        curveArm.IsChecked = false;
        Require(cameraRawDrag is null, "taking the arm off left a drag in place; the Mac's release ends it.");
        Require(Differ(scene.Snapshot.Images[id]!, dragged) < 0,
            "unarming the arm undid the grade the drag had already made.");
        report["cameraRawTargetedDragNudgesTheCurveUnderThePointer"] = "passed";
    }

    /// <summary>CR-g: the curve's Parametric page — the four tonal regions, the three dividers between them, and
    /// the two drags that live inside the graph. The arithmetic is locked in Core
    /// (Windows/tests/Compositor.Core.Tests/CameraRawParametricTests.cs, numbered from
    /// Windows/tools/parametric-golden.py); what the panel adds is that the page decides which rows and which
    /// graph are showing, that a press inside the graph grabs either a region or a divider, that a region is
    /// measured from the value the press found (200 slider units to a full graph height, not the picture's 0.35 a
    /// document pixel), that a divider stops two points short of its neighbour, and that moving a divider changes
    /// which region the *picture's* drag writes. As in CR-f a probe cannot inject a pointer, so the canvas's three
    /// pointer handlers are exercised through the same three callees the graph calls.</summary>
    private async Task VerifyCameraRawParametricAsync(Dictionary<string, object> report)
    {
        const double size = 255;
        var scene = TargetedBandsScene();
        OpenDocument(scene); await WaitForFramesAsync();
        var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        var panel = OpenCameraRawPanel(scene, id);
        var band = CameraRawBand(panel, Loc.T("L_Curve"));
        Require(band is not null, "the Camera Raw panel has no Curve group.");
        band!.IsExpanded = true;
        band.UpdateLayout();
        await WaitForFramesAsync();
        var page = ComboBoxNamed(panel, Loc.T("L_CurvePage"));
        Require(page is not null, "the curve band has no page picker under the name the panel gives it.");
        var regionRows = new Func<int, Slider?>(row => SliderNamed(panel, CameraRawSettings.CurveRegionNames[row]));
        var channel = ComboBoxNamed(panel, Loc.T("L_CurveChannel"));
        var refine = SliderNamed(panel, Loc.T("L_RefineSat"));
        Require(channel is not null && refine is not null,
            $"the curve band lost a control it is built with (channel {channel is not null}, refine {refine is not null}).");
        // Whether a row is showing is the row's own Visibility *and* every container above it: the band collapses
        // the stack that holds the four region rows, which leaves each slider inside it claiming to be Visible.
        // Reading the slider alone would read a property that never changes (same trap as CR-e's eyedropper row).
        static string ParentState(FrameworkElement element) =>
            (element.Parent as FrameworkElement)?.Visibility.ToString() ?? "no parent";

        // --- One page at a time ---
        // The page is session state, and CR-f's group left the band on the point page, so this group asks for the
        // page it means rather than reading the order of the groups as a default. That the band *opens* on
        // Parametric is asserted there, where the band is first built.
        page!.SelectedIndex = CameraRawSettings.CurvePageParametric;
        Require(cameraRawCurvePage == CameraRawSettings.CurvePageParametric,
            $"asking for the parametric page left the session on page {cameraRawCurvePage}.");
        Require(Enumerable.Range(0, 4).All(row => Shown(regionRows(row))),
            "the parametric page does not show all four tonal regions.");
        Require(!Shown(refine) && !Shown(channel),
            "the parametric page is still showing the point page's channel picker or Refine Saturation "
            + $"(channel {channel!.Visibility} over {ParentState(channel)}, refine {refine!.Visibility} over {ParentState(refine)}).");
        page.SelectedIndex = CameraRawSettings.CurvePagePoint;
        Require(Enumerable.Range(0, 4).All(row => !Shown(regionRows(row))),
            "the point page still lists the four regions.");
        Require(Shown(channel) && Shown(refine),
            "the point page hides the channel picker or Refine Saturation, which the Mac shows there.");
        // Refine Saturation is the composite curve's own (CameraRawColorControls.swift:54-56): the Mac puts it
        // away as soon as the point page is editing one channel.
        channel!.SelectedIndex = 1;
        Require(!Shown(refine) && Shown(channel),
            "Red keeps the curve's Refine Saturation slider up, and the Mac hides it off RGB.");
        channel.SelectedIndex = 0;
        Require(Shown(refine), "back on RGB the Refine Saturation row did not come back.");
        // A grab decided on one page means nothing on the other, so the page lets go of it.
        page.SelectedIndex = CameraRawSettings.CurvePageParametric;
        Require(BeginCameraRawCurveGraphDrag(100, 100), "the parametric graph refused a press in the middle of a region.");
        Require(cameraRawGraphDrag is not null, "a press in the graph left nothing to drag.");
        page.SelectedIndex = CameraRawSettings.CurvePagePoint;
        Require(cameraRawGraphDrag is null, "switching the page left the previous page's grab in place.");
        page.SelectedIndex = CameraRawSettings.CurvePageParametric;
        Require(Shown(regionRows(0)), "returning to Parametric did not bring the rows back.");
        report["cameraRawCurvePageListsRegionsNotAnchors"] = "passed";

        // --- A region drag, measured from the press ---
        var capture = scene.Snapshot.Images[id]!;
        Require(BeginCameraRawCurveGraphDrag(100, 100), "the graph refused the press again.");
        Require(cameraRawGraphDrag is { Divider: false, Index: 1, Start: 0 },
            $"a press at x=100 grabbed {cameraRawGraphDrag}, and 100/255 is 0.392 — between the 0.25 and 0.5 dividers, so Darks.");
        ContinueCameraRawCurveGraphDrag(100, 87.25);
        Require(Math.Abs(cameraRawDraft!.CurveDarks - 10) < 1e-9 && cameraRawDraft!.CurveShadows == 0
                && cameraRawDraft!.CurveLights == 0 && cameraRawDraft!.CurveHighlights == 0,
            $"twelve and three quarters of a graph up wrote Shadows {cameraRawDraft!.CurveShadows}, Darks {cameraRawDraft!.CurveDarks}, "
            + $"Lights {cameraRawDraft!.CurveLights}, Highlights {cameraRawDraft!.CurveHighlights}; the Mac lifts only Darks by 10.");
        Require(regionRows(2)?.Value == 10, "the Darks row did not follow the drag that moved Darks.");
        ContinueCameraRawCurveGraphDrag(100, 74.5);
        Require(Math.Abs(cameraRawDraft!.CurveDarks - 20) < 1e-9, "a drag to twenty did not reach twenty.");
        Require(await WaitUntilAsync(() => Differ(scene.Snapshot.Images[id]!, capture) >= 0, 8000),
            "the region drag moved the draft and never reached the layer.");
        var dragged = scene.Snapshot.Images[id]!.Clone();
        // Back to where the press landed: the number is measured from the press, so a drag that doubles back
        // returns to the press's own value rather than having spent what it moved.
        ContinueCameraRawCurveGraphDrag(100, 100);
        Require(cameraRawDraft!.CurveDarks == 0,
            $"a drag that doubled back left Darks at {cameraRawDraft!.CurveDarks}, and its own press implies 0.");
        EndCameraRawCurveGraphDrag();
        ContinueCameraRawCurveGraphDrag(100, 24);
        Require(cameraRawDraft!.CurveDarks == 0, "the move after the release still answered to the drag.");
        Require(Differ(scene.Snapshot.Images[id]!, dragged) < 0 || Differ(scene.Snapshot.Images[id]!, capture) < 0,
            "a drag back to its own press left the layer somewhere neither the press nor the release can account for.");
        // A press exactly on a divider belongs to the region above it (the Mac's three tests are strict `<`),
        // and a move that is not a whole unit is written as one (Ctrls:199 `.rounded()`).
        Require(BeginCameraRawCurveGraphDrag(63.75, 100), "a press exactly on the shadow divider was refused.");
        Require(cameraRawGraphDrag is { Divider: false, Index: 1 },
            $"a press at 25% grabbed {cameraRawGraphDrag}, and a tone on the divider is not inside the shadows.");
        ContinueCameraRawCurveGraphDrag(63.75, 88);
        Require(Math.Abs(cameraRawDraft!.CurveDarks - 9) < 1e-9,
            $"twelve units up wrote {cameraRawDraft!.CurveDarks}, and 12/255 of a full graph is 9.41 units — the Mac keeps whole numbers.");
        EndCameraRawCurveGraphDrag();
        // The number the press remembers is the region's own, not zero (Ctrls:158 keeps `raw.curve[keyPath: key]`),
        // so a region the rows already lifted keeps what it had.
        regionRows(2)!.Value = 30;
        Require(Math.Abs(cameraRawDraft!.CurveDarks - 30) < 1e-9, "the Darks row did not write 30 through its own handler.");
        Require(BeginCameraRawCurveGraphDrag(100, 100), "a press on a lifted region was refused.");
        Require(Math.Abs(cameraRawGraphDrag!.Value.Start - 30) < 1e-9,
            $"the press remembered {cameraRawGraphDrag?.Start}, and the region it landed in was at 30.");
        ContinueCameraRawCurveGraphDrag(100, 87.25);
        Require(Math.Abs(cameraRawDraft!.CurveDarks - 40) < 1e-9,
            $"a tenth of a graph up from 30 wrote {cameraRawDraft!.CurveDarks}, and the Mac lifts it to 40.");
        ContinueCameraRawCurveGraphDrag(100, 100);
        Require(Math.Abs(cameraRawDraft!.CurveDarks - 30) < 1e-9,
            $"doubling back to the press left Darks at {cameraRawDraft!.CurveDarks}, and the press itself implies 30.");
        EndCameraRawCurveGraphDrag();
        regionRows(2)!.Value = 0;
        Require(await WaitUntilAsync(() => Differ(scene.Snapshot.Images[id]!, capture) < 0, 8000),
            "putting the region back to zero never took the layer back to the bytes it started from.");
        report["cameraRawGraphDragLiftsTheRegionUnderThePointer"] = "passed";

        // --- A divider drag, and the picture that follows it ---
        // The bottom band belongs to the dividers: 18 of the Mac's 150 points, which on this box is 30.6.
        Require(BeginCameraRawCurveGraphDrag(0.375 * size, size - 5), "a press along the bottom found no divider.");
        Require(cameraRawGraphDrag is { Divider: true, Index: 0 },
            $"a press halfway between the 25 and 50 dividers grabbed {cameraRawGraphDrag}, and the Mac's min takes the earlier one.");
        ContinueCameraRawCurveGraphDrag(0.6 * size, size - 5);
        Require(Math.Abs(cameraRawDraft!.CurveShadowSplit - 48) < 1e-9 && cameraRawDraft!.CurveDarkSplit == 50,
            $"dragging the shadow divider to 60 wrote {cameraRawDraft!.CurveShadowSplit}, and it stops at darkSplit minus two.");
        EndCameraRawCurveGraphDrag();
        Require(BeginCameraRawCurveGraphDrag(0.9 * size, size - 5), "the second divider press found nothing.");
        Require(cameraRawGraphDrag is { Divider: true, Index: 2 },
            $"a press at 90 should be nearest the light divider, grabbed {cameraRawGraphDrag}.");
        ContinueCameraRawCurveGraphDrag(0.6 * size, size - 5);
        Require(Math.Abs(cameraRawDraft!.CurveLightSplit - 60) < 1e-9,
            $"dragging the light divider down to 60 left it at {cameraRawDraft!.CurveLightSplit}.");
        EndCameraRawCurveGraphDrag();
        // The middle divider is the one squeezed from both sides. (A press that is exactly halfway between two
        // of them takes the earlier one — that is asserted above, at 37.5%, where the two distances really are
        // equal; 55% is not one of those, because the pointer's own division lands either side of it.)
        Require(BeginCameraRawCurveGraphDrag(0.54 * size, size - 5), "no divider answered the press next to the middle one.");
        Require(cameraRawGraphDrag is { Divider: true, Index: 1 },
            $"a press four points from the middle divider, with six to each of the others, grabbed {cameraRawGraphDrag}.");
        ContinueCameraRawCurveGraphDrag(0.9 * size, size - 5);
        Require(Math.Abs(cameraRawDraft!.CurveDarkSplit - 58) < 1e-9,
            $"the middle divider dragged to 90 stopped at {cameraRawDraft!.CurveDarkSplit}, and the Mac caps it at lightSplit minus two.");
        EndCameraRawCurveGraphDrag();
        Require(cameraRawDraft!.AdjustsCurve == false,
            "moving a divider alone is reported as an adjustment; with all four regions at zero it bends nothing, and neither does the Mac.");
        // Now the picture's drag, which reads the dividers the graph just moved: the green band's tone, 0.6925,
        // is above the new light divider at 60, so the drag writes Highlights rather than the Lights it would
        // have written before.
        var curveArm = ToggleButtonNamed(panel, Loc.T("A_TargetedCurve"))!;
        curveArm.IsChecked = true;
        Require(BeginCameraRawTargetedDrag(new PointD(8, 8)), "the curve press refused its pixel on the parametric page.");
        DragCameraRawTargeted(new PointD(8, 28));
        Require(Math.Abs(cameraRawDraft!.CurveHighlights - (-7)) < 1e-9 && cameraRawDraft!.CurveLights == 0
                && cameraRawDraft!.CurveRgb[1].Y == 1,
            $"the picture drag wrote Highlights {cameraRawDraft!.CurveHighlights} and Lights {cameraRawDraft!.CurveLights} "
            + $"with RGB still at {cameraRawDraft!.CurveRgb[1]}; after the divider moved to 60 the tone 0.6925 is in Highlights, "
            + "and the Mac's parametric branch never touches the anchors.");
        Require(await WaitUntilAsync(() => Differ(scene.Snapshot.Images[id]!, capture) >= 0, 8000),
            "the parametric picture drag moved the draft and never reached the layer.");
        // The branch is read at the move, exactly as the Mac reads the page there (CameraRaw.swift:540), so
        // switching pages under a live arm sends the *next* press to the other branch while the numbers the
        // first branch made stay.
        curveArm.IsChecked = false;
        page.SelectedIndex = CameraRawSettings.CurvePagePoint;
        curveArm.IsChecked = true;
        Require(BeginCameraRawTargetedDrag(new PointD(8, 8)), "the point page press refused its pixel.");
        DragCameraRawTargeted(new PointD(8, 28));
        Require(Math.Abs(cameraRawDraft!.CurveRgb[1].Y - 0.93) < 1e-9 && cameraRawDraft!.CurveHighlights == -7,
            $"the point branch wrote RGB {cameraRawDraft!.CurveRgb[1]} and left Highlights at {cameraRawDraft!.CurveHighlights}; "
            + "the Mac nudges the nearest anchor and keeps the region the earlier drag made.");
        EndCameraRawTargetedDrag();
        curveArm.IsChecked = false;
        // Put the band back the way the Mac ships it: dividers and all four regions at their defaults, and the
        // preview that follows the write, because a draft nobody rendered still paints the old grade.
        page.SelectedIndex = CameraRawSettings.CurvePageParametric;
        cameraRawDraft = cameraRawDraft!.Clone();
        cameraRawDraft.CurveHighlights = 0;
        cameraRawDraft.CurveRgb = [new CurvePoint(0, 0), new CurvePoint(1, 1)];
        cameraRawDraft.CurveShadowSplit = 25;
        cameraRawDraft.CurveDarkSplit = 50;
        cameraRawDraft.CurveLightSplit = 75;
        PreviewCameraRaw(document!, id);
        Require(await WaitUntilAsync(() => Differ(scene.Snapshot.Images[id]!, capture) < 0, 8000),
            "the group finished without getting the layer back to the bytes it started from.");
        report["cameraRawDividerDragKeepsItsNeighbourAndThePictureFollowsThePage"] = "passed";
    }

    /// <summary>CR-h: the point page's presets and its In／Out readout. The three shapes and the equality that
    /// reads them back are locked in Core (Windows/tests/Compositor.Core.Tests/CameraRawCurvePresetTests.cs,
    /// transcribed from CameraRawColor.swift:35-37 and CameraRawColorControls.swift:244-259); what the panel adds
    /// is that the picker lists the Mac's four items, writes only the channel it is showing, reads back what that
    /// channel currently is rather than what was last chosen, and that pressing the graph takes a point and says
    /// its input and output. The press and the move are driven through the canvas's own two functions, which the
    /// pointer handlers call — a probe cannot inject a pointer, same rule as CR-f and CR-g.</summary>
    private async Task VerifyCameraRawCurvePresetsAsync(Dictionary<string, object> report)
    {
        var scene = TargetedBandsScene();
        OpenDocument(scene); await WaitForFramesAsync();
        var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        var panel = OpenCameraRawPanel(scene, id);
        var band = CameraRawBand(panel, Loc.T("L_Curve"));
        Require(band is not null, "the Camera Raw panel has no Curve group.");
        band!.IsExpanded = true;
        band.UpdateLayout();
        await WaitForFramesAsync();
        ComboBoxNamed(panel, Loc.T("L_CurvePage"))!.SelectedIndex = CameraRawSettings.CurvePagePoint;
        var preset = ComboBoxNamed(panel, Loc.T("L_CurvePreset"));
        var channel = ComboBoxNamed(panel, Loc.T("L_CurveChannel"));
        Require(preset is not null && channel is not null,
            $"the point page is missing its picker or its channel ({preset is not null}, {channel is not null}).");
        var canvas = cameraRawCurveCanvas;
        var readout = cameraRawCurveReadout;
        Require(canvas is not null && readout is not null,
            $"the curve band never handed over its canvas or its readout (canvas {canvas is not null}, readout {readout is not null}).");
        var capture = scene.Snapshot.Images[id]!;
        var shapeOf = new Func<List<CurvePoint>, CurvePoint[]>(list => list.ToArray());
        var spelled = new Func<CurvePoint[], string>(list => string.Join(" ", list.Select(p => $"({p.X}, {p.Y})")));
        CurvePoint[] Linear() => [new(0, 0), new(1, 1)];
        CurvePoint[] Medium() => [new(0, 0), new(0.25, 0.18), new(0.75, 0.82), new(1, 1)];
        CurvePoint[] Strong() => [new(0, 0), new(0.25, 0.10), new(0.75, 0.90), new(1, 1)];

        // --- The picker's items and the shape it installs ---
        var listed = (preset!.ItemsSource as IEnumerable<string>)?.ToArray() ?? Array.Empty<string>();
        Require(listed.SequenceEqual(new[] { "Custom", "Linear", "Medium Contrast", "Strong Contrast" }),
            $"the preset picker lists [{string.Join(", ", listed)}]; the Mac lists Custom, Linear, "
            + "Medium Contrast, Strong Contrast in that order.");
        Require(preset.SelectedIndex == CameraRawCurvePresets.Linear,
            $"an untouched curve reads back as item {preset.SelectedIndex}, and the Mac matches it to Linear.");
        Require(readout!.Text.Length == 0,
            $"a fresh point page already reports \"{readout.Text}\" with nothing selected.");
        preset.SelectedIndex = CameraRawCurvePresets.MediumContrast;
        Require(shapeOf(cameraRawDraft!.CurveRgb).SequenceEqual(Medium()),
            $"Medium Contrast wrote {spelled(shapeOf(cameraRawDraft!.CurveRgb))}; the Mac's list is (0, 0) (0.25, 0.18) (0.75, 0.82) (1, 1).");
        Require(preset.SelectedIndex == CameraRawCurvePresets.MediumContrast,
            "after installing Medium Contrast the picker read something else back.");
        Require(await WaitUntilAsync(() => Differ(scene.Snapshot.Images[id]!, capture) >= 0, 8000),
            "the preset never reached the layer.");
        preset.SelectedIndex = CameraRawCurvePresets.Custom;
        Require(shapeOf(cameraRawDraft!.CurveRgb).SequenceEqual(Medium()),
            "choosing Custom rewrote the curve; the Mac's nil preset stores nothing.");
        // Nudge a point and the same read-back has to fall to Custom: `matching` is whole-list equality.
        canvas!.PressPoint(0.25 * 255, 255 - 0.18 * 255, false);
        Require(canvas.SelectedIndex == 1, $"the press on the middle point left the selection at {canvas.SelectedIndex}.");
        canvas.MovePoint(0.25 * 255, 200);
        Require(preset.SelectedIndex == CameraRawCurvePresets.Custom,
            "a curve one-fifth of a step off Medium Contrast still read back as a preset.");
        Require(shapeOf(cameraRawDraft!.CurveRgb).Length == 4,
            $"nudged a point and the count moved to {shapeOf(cameraRawDraft!.CurveRgb).Length}.");
        preset.SelectedIndex = CameraRawCurvePresets.Linear;
        Require(shapeOf(cameraRawDraft!.CurveRgb).SequenceEqual(Linear()), "Linear did not restore the straight line.");
        report["cameraRawCurvePresetListsWhatTheMacLists"] = "passed";

        // --- Only the channel the picker is showing ---
        preset.SelectedIndex = CameraRawCurvePresets.StrongContrast;
        Require(shapeOf(cameraRawDraft!.CurveRgb).SequenceEqual(Strong())
                && shapeOf(cameraRawDraft!.CurveRed).SequenceEqual(Linear())
                && shapeOf(cameraRawDraft!.CurveGreen).SequenceEqual(Linear()),
            $"Strong Contrast on RGB also wrote {spelled(shapeOf(cameraRawDraft!.CurveRed))} to Red; the Mac installs into the shown channel only.");
        channel!.SelectedIndex = 1;
        Require(preset.SelectedIndex == CameraRawCurvePresets.Linear,
            $"on the Red channel the picker read {preset.SelectedIndex}, and Red is still the straight line.");
        preset.SelectedIndex = CameraRawCurvePresets.MediumContrast;
        Require(shapeOf(cameraRawDraft!.CurveRed).SequenceEqual(Medium())
                && shapeOf(cameraRawDraft!.CurveRgb).SequenceEqual(Strong()),
            "the Red channel's preset moved the RGB curve too, or Red did not take it.");
        Require(await WaitUntilAsync(() => Differ(scene.Snapshot.Images[id]!, capture) >= 0, 8000),
            "a preset applied to Red never reached the layer.");
        channel.SelectedIndex = 0;
        Require(preset.SelectedIndex == CameraRawCurvePresets.StrongContrast,
            "back on RGB the picker did not read the strong curve that is there.");
        preset.SelectedIndex = CameraRawCurvePresets.Linear;
        channel.SelectedIndex = 1;
        preset.SelectedIndex = CameraRawCurvePresets.Linear;
        channel.SelectedIndex = 0;
        Require(await WaitUntilAsync(() => Differ(scene.Snapshot.Images[id]!, capture) < 0, 8000),
            "putting both channels back to Linear never took the layer back to the bytes it started from.");
        report["cameraRawCurvePresetWritesOnlyTheChannelShown"] = "passed";

        // --- The selected point says what goes in and what comes out ---
        canvas.PressPoint(128, 100, false);
        Require(canvas.SelectedIndex == 1 && shapeOf(cameraRawDraft!.CurveRgb).Length == 3,
            $"a press in an empty part of the graph selected {canvas.SelectedIndex} in a curve of "
            + $"{shapeOf(cameraRawDraft!.CurveRgb).Length} points.");
        Require(readout.Text == Loc.F("CE_InOut", 128.0, 155.0),
            $"the readout says \"{readout.Text}\" for the point added at input 128, and the Mac prints In 128   Out 155.");
        canvas.PressPoint(128, 100, false);
        Require(shapeOf(cameraRawDraft!.CurveRgb).Length == 3,
            "a second press on the same point added another one.");
        canvas.MovePoint(128, 60);
        Require(readout.Text == Loc.F("CE_InOut", 128.0, 195.0),
            $"after dragging it up the readout says \"{readout.Text}\", and the Mac follows the point it moved.");
        channel.SelectedIndex = 1;
        Require(canvas.SelectedIndex == -1 && readout.Text.Length == 0,
            $"changing the channel left a selection ({canvas.SelectedIndex}) and a readout (\"{readout.Text}\"); "
            + "the Mac drops both with the channel.");
        channel.SelectedIndex = 0;
        canvas.PressPoint(128, 60, false);
        Require(canvas.SelectedIndex == 1, $"taking the point again selected {canvas.SelectedIndex}.");
        canvas.PressPoint(128, 60, true);
        Require(shapeOf(cameraRawDraft!.CurveRgb).SequenceEqual(Linear()) && readout.Text.Length == 0,
            "removing a point left it selected, or left the extra point in the curve.");
        // Sixteen is the page's own ceiling (Ctrls:167), not the model's 32.
        for (int added = 0; added < 20 && shapeOf(cameraRawDraft!.CurveRgb).Length < CameraRawCurvePresets.PointsMax; added++)
        {
            canvas.PressPoint(8 + added * 12, 120, false);
        }
        Require(shapeOf(cameraRawDraft!.CurveRgb).Length == CameraRawCurvePresets.PointsMax,
            $"the page stopped adding at {shapeOf(cameraRawDraft!.CurveRgb).Length} points; the Mac's guard is sixteen.");
        canvas.PressPoint(8 + 20 * 12, 40, false);
        Require(shapeOf(cameraRawDraft!.CurveRgb).Length == CameraRawCurvePresets.PointsMax,
            "a seventeenth point got in, and the Mac's press refuses it.");
        preset.SelectedIndex = CameraRawCurvePresets.Linear;
        Require(await WaitUntilAsync(() => Differ(scene.Snapshot.Images[id]!, capture) < 0, 8000),
            "the group ended without putting the layer back the way it found it.");
        report["cameraRawSelectedPointReadsItsInAndOut"] = "passed";
    }

    /// <summary>CR-i: the ten eyes on Camera Raw's section headers. The field-by-field arithmetic of what each
    /// group gives up is locked in Core (Windows/tests/Compositor.Core.Tests/CameraRawGroupVisibilityTests.cs,
    /// transcribed from CameraRaw.swift:171-204); what the panel adds is the three things a reader can only check
    /// here — an eye appears only where there is something to hide, closing one changes the preview without
    /// touching the sliders, and OK bakes what the eyes left open (Filters.swift:451-461 and :600). The eyes are
    /// pressed through the same invoke path the chips use, since a probe cannot click.</summary>
    private async Task VerifyCameraRawEyesAsync(Dictionary<string, object> report)
    {
        var scene = TargetedBandsScene();
        OpenDocument(scene); await WaitForFramesAsync();
        var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        var panel = OpenCameraRawPanel(scene, id);
        var capture = scene.Snapshot.Images[id]!.Clone();
        var exposure = SliderNamed(panel, Loc.T("L_Exposure"));
        var saturation = SliderNamed(panel, Loc.T("L_Saturation"));
        Require(exposure is not null && saturation is not null,
            $"the panel is missing a slider the eyes are tried with (exposure {exposure is not null}, saturation {saturation is not null}).");
        Require(cameraRawDraft is { Exposure: 0, Saturation: 0 },
            $"the panel this group was handed is not a fresh session (draft exposure {cameraRawDraft?.Exposure}, "
            + $"saturation {cameraRawDraft?.Saturation}, session layer {cameraRawSessionLayer}, wanted {id}).");
        var hide = new Func<string, Button?>(group => ButtonNamed(panel, Loc.F("A_HideGroup", group)));
        var show = new Func<string, Button?>(group => ButtonNamed(panel, Loc.F("A_ShowGroup", group)));
        var current = new Func<Surface>(() => scene.Snapshot.Images[id]!);

        // An eye's own Visibility is the contract here: the band sets it on the button itself, so walking the
        // parent chain would read the Expander's header presenter rather than the control.
        var eyeShown = new Func<Button?, bool>(eye => eye?.Visibility == Visibility.Visible);
        Require(!eyeShown(hide("Light")) && !eyeShown(hide("Color")),
            "a panel that has changed nothing still shows eyes; the Mac puts each one behind that group's adjusts.");
        exposure!.Value = 1;
        Require(eyeShown(hide("Light")), "an exposure of +1 did not produce the Light group's eye.");
        Require(!eyeShown(hide("Color")),
            "the Color eye appeared for a change that touched no color number — the two groups are separate even "
            + "though one pass of the kernel serves them (CameraRaw.swift:103-108).");
        exposure.Value = 0;
        Require(!eyeShown(hide("Light")) && !eyeShown(show("Light")),
            "the Light eye was still offering itself after its group stopped adjusting anything.");
        report["cameraRawEyesAppearOnlyForGroupsThatAdjustSomething"] = "passed";

        // --- Hiding changes the render, not the draft ---
        exposure.Value = 1;
        saturation!.Value = 60;
        Require(await WaitUntilAsync(() => Differ(current(), capture) >= 0, 8000),
            "a graded light and color never reached the preview.");
        var both = current().Clone();
        PressChip(hide("Light")!);
        Require(Math.Abs(exposure.Value - 1) < 1e-9,
            $"closing the eye moved the exposure slider to {exposure.Value}; the Mac hides what is rendered and leaves the numbers alone.");
        Require(eyeShown(show("Light")) && !eyeShown(hide("Light")),
            "the eye did not turn into its opposite, so a reader cannot tell what pressing it will do.");
        Require(await WaitUntilAsync(() => Differ(current(), both) >= 0, 8000),
            "closing the Light eye changed nothing in the preview.");
        Require(Differ(current(), capture) >= 0,
            "hiding Light took the Color group's work with it; the Mac hides one group at a time.");
        PressChip(show("Light")!);
        Require(await WaitUntilAsync(() => Differ(current(), both) < 0, 8000),
            "opening the eye again did not bring the same grade back, so the draft was not left intact.");
        report["cameraRawEyeHidesItsGroupAndLeavesTheDraftAlone"] = "passed";

        // --- OK bakes what the eyes left open ---
        // Its own document: a committed grade is the one thing a preview cannot walk back, because the panel
        // restores its layer by zeroing the draft while OK writes into the layer itself.
        exposure.Value = 0;
        saturation.Value = 0;
        Require(await WaitUntilAsync(() => Differ(current(), capture) < 0, 8000),
            "putting both groups back to zero left a grade on the layer.");
        var okScene = TargetedBandsScene();
        OpenDocument(okScene); await WaitForFramesAsync();
        var okId = okScene.Snapshot.Manifest.ActiveLayerId!.Value;
        var okCapture = okScene.Snapshot.Images[okId]!.Clone();
        var okPanel = OpenCameraRawPanel(okScene, okId);
        var okImage = new Func<Surface>(() => okScene.Snapshot.Images[okId]!);
        SliderNamed(okPanel, Loc.T("L_Exposure"))!.Value = 2;
        Require(await WaitUntilAsync(() => Differ(okImage(), okCapture) >= 0, 8000),
            "a +2 exposure never reached the second panel's preview.");
        PressChip(ButtonNamed(okPanel, Loc.F("A_HideGroup", "Light"))!);
        Require(await WaitUntilAsync(() => Differ(okImage(), okCapture) < 0, 8000),
            "with the only graded group hidden, the preview is not the untouched picture.");
        PressChip(CameraRawSheetButton(confirm: true));
        // The commit ends its session inside the publish callback, after the layer's pixels are swapped, so
        // "no session key" is what proves the bake has landed — the picture on screen at the instant OK is
        // pressed is already the untouched one the eye left behind, and waiting on pixels would be satisfied
        // before the commit thread ever ran. The panel goes with the session, as it does on the Mac
        // (Filters.swift:616 clears filterEdit, and ContentView.swift:250 closes the sheet on that).
        Require(await WaitUntilAsync(() => cameraRawSessionKey is null, 8000),
            $"OK never ended the session it committed (draft {cameraRawDraft?.Exposure}, "
            + $"eyes open: {cameraRawShows.Count(shown => shown)} of {cameraRawShows.Length}).");
        Require(await WaitUntilAsync(() => !FloatingPanelIsOpen(CameraRawPanelKey), 8000),
            "OK left the Camera Raw sheet on screen after ending the session that opened it.");
        var handedBack = new Func<bool>(() => cameraRawSessionKey is null);
        Require(Differ(okImage(), okCapture) < 0,
            "OK baked a grade the eyes had hidden; the Mac commits renderSettings(), not the draft (Filters.swift:600).");
        // The same button does bake what is visible — otherwise the line above would only prove OK is dead.
        var reopened = OpenCameraRawPanel(okScene, okId);
        Require(!eyeShown(ButtonNamed(reopened, Loc.F("A_HideGroup", "Light"))),
            $"the rebuilt panel offered a Light eye over a zero draft (eyes open: "
            + $"{cameraRawShows.Count(shown => shown)} of {cameraRawShows.Length}).");
        SliderNamed(reopened, Loc.T("L_Exposure"))!.Value = 2;
        Require(await WaitUntilAsync(() => Differ(okImage(), okCapture) >= 0, 8000),
            "the reopened panel's +2 exposure never reached its preview.");
        Require(eyeShown(ButtonNamed(reopened, Loc.F("A_HideGroup", "Light"))),
            "the rebuilt panel never got eyes wired, so the OK pressed below belongs to a session the panel is not reading.");
        PressChip(CameraRawSheetButton(confirm: true));
        Require(await WaitUntilAsync(handedBack, 8000),
            "the second commit never ended its session either.");
        Require(Differ(okImage(), okCapture) >= 0,
            "OK baked nothing even with every eye open, so the hidden-group result above says nothing.");
        report["cameraRawOkBakesOnlyWhatTheEyesLeftOpen"] = "passed";
    }

    /// <summary>Whether a control is actually showing. A collapsed row leaves what is inside it reporting
    /// Visible, and on WinUI these children have no Parent to ask, so the port sets the state on every control in
    /// a collapsed row — which is what this walks. Reading the control alone would read a property that never
    /// changes (the same trap CR-e hit with the eyedropper's row).</summary>
    private static bool Shown(FrameworkElement? element)
    {
        for (var node = element; node is not null; node = node.Parent as FrameworkElement)
        {
            if (node.Visibility != Visibility.Visible) return false;
        }
        return element is not null;
    }

    /// <summary>The two views that replace the picture while Option is held on a slider: the clipping read of the
    /// light pass, and the sharpen mask. Neither one is a setting, so what the panel has to get right is which
    /// frame it asks the render for, that it moves no number to get it, and — the part the Mac is strictest about
    /// — that neither of them ever reaches the layer.</summary>
    private async Task VerifyCameraRawOptionViewsAsync(Dictionary<string, object> report)
    {
        static bool PureClipMap(Surface image)
        {
            for (int y = 0; y < image.Height; y++)
                for (int x = 0; x < image.Width; x++)
                {
                    int at = y * image.Stride + x * 4;
                    if (image.Pixels[at + 3] == 0) continue;
                    for (int channel = 0; channel < 3; channel++)
                        if (image.Pixels[at + channel] is not (0 or 255)) return false;
                }
            return true;
        }

        static bool Grayscale(Surface image)
        {
            bool any = false;
            for (int y = 0; y < image.Height; y++)
                for (int x = 0; x < image.Width; x++)
                {
                    int at = y * image.Stride + x * 4;
                    if (image.Pixels[at + 3] == 0) continue;
                    if (image.Pixels[at] != image.Pixels[at + 1] || image.Pixels[at + 1] != image.Pixels[at + 2]) return false;
                    any = true;
                }
            return any;
        }

        var scene = TargetedBandsScene();
        OpenDocument(scene); await WaitForFramesAsync();
        var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        var panel = OpenCameraRawPanel(scene, id);
        var capture = scene.Snapshot.Images[id]!.Clone();
        var current = new Func<Surface>(() => scene.Snapshot.Images[id]!);
        var exposure = SliderNamed(panel, Loc.T("L_Exposure"));
        Require(exposure is not null, "the Camera Raw panel has no Exposure row for the clipping view to read.");
        Require(cameraRawDraft is { Exposure: 0 } && cameraRawClipping == CameraRawEngine.ClippingNone,
            $"the panel this group was handed is not a fresh session (draft {cameraRawDraft?.Exposure}, clipping {cameraRawClipping}).");

        // --- The view paints the preview and moves no number ---
        exposure!.Value = 1;
        Require(await WaitUntilAsync(() => Differ(current(), capture) >= 0, 8000), "a +1 exposure never reached the preview.");
        var graded = current().Clone();
        SetCameraRawClipping(CameraRawEngine.ClippingHighlights, true);
        PreviewCameraRaw(document!, id);
        Require(await WaitUntilAsync(() => Differ(current(), graded) >= 0, 8000),
            "arming the highlights view changed nothing in the preview, so the panel never asked the render for it.");
        Require(Math.Abs(exposure.Value - 1) < 1e-9,
            $"arming the view moved the exposure slider to {exposure.Value}; the Mac paints a different picture over the same numbers.");
        Require(PureClipMap(current()),
            "the clipping view left a channel at a value between pure black and pure white; the kernel prints 0 or 1 per channel.");
        // The same call the keyboard makes when Option comes up.
        ReleaseCameraRawViews();
        Require(await WaitUntilAsync(() => Differ(current(), graded) < 0, 8000),
            "releasing the view did not put the grade back the way it was.");
        // And a row that carries no clipping mode takes it off too, whatever the modifier is doing — the Mac's
        // `assign(…, clipping: nil)` (CameraRawControls.swift:331-334), which this panel runs for every row.
        SetCameraRawClipping(CameraRawEngine.ClippingShadows, true);
        PreviewCameraRaw(document!, id);
        Require(await WaitUntilAsync(() => Differ(current(), graded) >= 0, 8000),
            "re-arming the view showed nothing, so the row below cannot be seen taking it off either.");
        SliderNamed(panel, Loc.T("L_Temperature"))!.Value = 30;
        Require(CameraRawClippingView == CameraRawEngine.ClippingNone,
            "moving a row that carries no clipping mode left the view armed; on the Mac every assignment without one clears it.");
        Require(await WaitUntilAsync(() => !PureClipMap(current()), 8000),
            "the picture stayed a clip map after a row that carries no clipping mode moved.");
        // With the view back up, the groups the light pass does not own must not reach the picture at all.
        // Texture and grain go in through the draft here, because the claim is about the render the panel asks
        // for — those two bands' rows are CR-a's and CR-c's own coverage.
        SetCameraRawClipping(CameraRawEngine.ClippingHighlights, true);
        PreviewCameraRaw(document!, id);
        Require(await WaitUntilAsync(() => PureClipMap(current()) && Differ(current(), capture) >= 0, 8000),
            "the highlights view never came back up, so the two numbers below would have nothing to leave out.");
        var clipFrame = current();
        cameraRawDraft!.Texture = 50;
        cameraRawDraft.GrainAmount = 60;
        SetCameraRawClipping(CameraRawEngine.ClippingHighlights, true);
        PreviewCameraRaw(document!, id);
        Require(await WaitUntilAsync(() => !ReferenceEquals(current(), clipFrame), 8000),
            "no tick was published after texture and grain moved, so the claim below would measure nothing.");
        Require(Differ(current(), clipFrame) < 0 && PureClipMap(current()),
            "texture or grain reached the picture while the clipping view was up; the Mac's paint rules stop at the light pass.");
        report["cameraRawClippingViewPaintsThePreviewAndLeavesTheNumberAlone"] = "passed";

        // --- OK bakes the grade, not the view: its own document, because a commit cannot be walked back ---
        var okScene = TargetedBandsScene();
        OpenDocument(okScene); await WaitForFramesAsync();
        var okId = okScene.Snapshot.Manifest.ActiveLayerId!.Value;
        var okCapture = okScene.Snapshot.Images[okId]!.Clone();
        var okPanel = OpenCameraRawPanel(okScene, okId);
        var okImage = new Func<Surface>(() => okScene.Snapshot.Images[okId]!);
        SliderNamed(okPanel, Loc.T("L_Exposure"))!.Value = 2;
        Require(await WaitUntilAsync(() => Differ(okImage(), okCapture) >= 0, 8000),
            "the second panel's +2 exposure never reached its preview.");
        var okGraded = okImage().Clone();
        SetCameraRawClipping(CameraRawEngine.ClippingShadows, true);
        PreviewCameraRaw(document!, okId);
        Require(await WaitUntilAsync(() => Differ(okImage(), okGraded) >= 0, 8000),
            "the shadows view did not replace this preview, so OK would have had nothing to skip.");
        PressChip(CameraRawSheetButton(confirm: true));
        Require(await WaitUntilAsync(() => cameraRawSessionKey is null, 8000),
            "OK never ended the session it committed with a clipping view armed.");
        Require(Differ(okImage(), okGraded) < 0,
            "OK baked the clipping view into the layer; the Mac's commit job carries no cameraRawClipping (Filters.swift:620).");
        report["cameraRawClippingViewNeverReachesTheLayer"] = "passed";

        // --- The mask is the Masking row's own, and it paints gray over everything ---
        var detailScene = TargetedBandsScene();
        OpenDocument(detailScene); await WaitForFramesAsync();
        var detailId = detailScene.Snapshot.Manifest.ActiveLayerId!.Value;
        var detailCapture = detailScene.Snapshot.Images[detailId]!.Clone();
        var detailPanel = OpenCameraRawPanel(detailScene, detailId);
        var detailImage = new Func<Surface>(() => detailScene.Snapshot.Images[detailId]!);
        var band = CameraRawBand(detailPanel, Loc.T("L_Detail"));
        Require(band is not null, "the Camera Raw panel has no Detail band.");
        band!.IsExpanded = true;
        band.UpdateLayout();
        await WaitForFramesAsync();
        var sharpening = SliderNamed(band, Loc.T("L_Sharpening"));
        var radiusRow = SliderNamed(band, Loc.T("L_Radius"));
        var maskingRow = SliderNamed(band, Loc.T("L_Masking"));
        Require(sharpening is not null && radiusRow is not null && maskingRow is not null,
            $"the Detail band is missing a row the mask is tried with (amount {sharpening is not null}, "
            + $"radius {radiusRow is not null}, masking {maskingRow is not null}).");
        Require(!Grayscale(detailCapture), "the fixture has no color to begin with, so a gray frame would prove nothing.");
        sharpening!.Value = 80;
        Require(await WaitUntilAsync(() => Differ(detailImage(), detailCapture) >= 0, 8000),
            "the +80 sharpening never reached the preview.");
        Require(!CameraRawShowsSharpenMask, "moving the Amount row put the mask view on; only the Masking row asks for it.");
        var sharpened = detailImage().Clone();
        SetCameraRawSharpenMask(true, true);
        PreviewCameraRaw(document!, detailId);
        Require(await WaitUntilAsync(() => Differ(detailImage(), sharpened) >= 0, 8000),
            "arming the mask changed nothing in the preview.");
        Require(Grayscale(detailImage()), "the mask view left color in the picture; the kernel prints one gray per pixel.");
        radiusRow!.Value = 20;
        Require(!CameraRawShowsSharpenMask,
            "a row other than Masking left the mask view on; the Mac switches it off with every other number "
            + "in that band (CameraRawDetailOpticsControls.swift:70-77).");
        Require(await WaitUntilAsync(() => Differ(detailImage(), sharpened) >= 0, 8000),
            "the radius change never reached the preview, so that row is not the one that took the mask off.");
        maskingRow!.Value = 40;
        Require(!CameraRawShowsSharpenMask, "the Masking row turned its own view on with no Option held.");
        report["cameraRawSharpenMaskViewFollowsTheMaskingRow"] = "passed";
    }

    /// <summary>
    /// The panel's scope. Three things are claimed here: the ribbons are drawn from the grade the layer is
    /// showing, the mode is a session fact that paints no pixels, and the count happens before any view is
    /// painted over the picture — so arming the Option clipping read must leave the histogram exactly as it was
    /// (CameraRaw.swift:400-409, "The histogram is counted before this").
    /// </summary>
    private async Task VerifyCameraRawScopeAsync(Dictionary<string, object> report)
    {
        var scene = TargetedBandsScene();
        OpenDocument(scene); await WaitForFramesAsync();
        var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        var panel = OpenCameraRawPanel(scene, id);
        var current = new Func<Surface>(() => scene.Snapshot.Images[id]!);
        var canvas = cameraRawScopeCanvas;
        Require(canvas is not null, "the Camera Raw panel built no scope frame.");
        var name = new Func<string>(() => Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(canvas!));
        Require(await WaitUntilAsync(() => name() == Loc.T("A_ScopeHistogram") && canvas!.Children.Count == 3, 8000),
            $"the fresh panel drew no ribbons (name {name()}, children {canvas!.Children.Count}).");

        // A graded picture must move the counts, or the ribbons could be a static picture of anything.
        var before = cameraRawScope;
        SliderNamed(panel, Loc.T("L_Exposure"))!.Value = 2;
        Require(await WaitUntilAsync(() => cameraRawScope is { } fresh && !ReferenceEquals(fresh, before), 8000),
            "a +2 exposure never reached the scope.");
        report["cameraRawScopeDrawsRibbonsOfTheGradedPicture"] = "passed";

        // --- The mode is a view of the same numbers ---
        var cells = cameraRawScope!.Vectorscope.Count(value => value > 0);
        Require(cells > 0, $"the fixture put nothing on the vectorscope grid ({cells} cells), so switching to it proves nothing.");
        var pixels = current().Clone();
        SetCameraRawScopeMode(ScopeVectorscope);
        Require(await WaitUntilAsync(() => canvas!.Children.Count == cells && name() == Loc.T("L_ScopeVectorscope"), 8000),
            $"the vectorscope drew {canvas!.Children.Count} of the {cells} busy cells, or renamed itself to {name()}.");
        Require(Differ(current(), pixels) < 0,
            "switching the scope's mode moved pixels; the Mac keeps it a session fact (Filters.swift:339).");
        SetCameraRawScopeMode(ScopeHistogram);
        Require(await WaitUntilAsync(() => canvas!.Children.Count == 3 && name() == Loc.T("A_ScopeHistogram"), 8000),
            "coming back to the histogram did not put the three ribbons back.");
        report["cameraRawScopeModeSwitchesBetweenRibbonsAndCells"] = "passed";

        // --- Counted before painted ---
        var scoped = cameraRawScope;
        SetCameraRawClipping(CameraRawEngine.ClippingHighlights, true);
        PreviewCameraRaw(document!, id);
        Require(await WaitUntilAsync(() => Differ(current(), pixels) >= 0, 8000),
            "the clipping view never replaced the picture, so the claim below would measure nothing.");
        // A new count is made for this tick — every tick makes one — so identity is not the claim: the numbers
        // have to come out of the clean render, not of the frame the view replaced it with.
        Require(!ReferenceEquals(cameraRawScope, scoped)
                && cameraRawScope!.Red.SequenceEqual(scoped.Red)
                && cameraRawScope.Green.SequenceEqual(scoped.Green)
                && cameraRawScope.Blue.SequenceEqual(scoped.Blue)
                && cameraRawScope.Vectorscope.SequenceEqual(scoped.Vectorscope),
            "the scope was recounted from the painted-over frame; the Mac counts the grade before the view.");
        ReleaseCameraRawViews();
        Require(await WaitUntilAsync(() => Differ(current(), pixels) < 0, 8000),
            "the view out did not put the graded picture back.");
        report["cameraRawScopeIsCountedBeforeAnythingIsPainted"] = "passed";

        // --- Grey has no hue, but still has a level ---
        var greyScene = TargetedBandsScene();
        OpenDocument(greyScene); await WaitForFramesAsync();
        var greyId = greyScene.Snapshot.Manifest.ActiveLayerId!.Value;
        var greyPanel = OpenCameraRawPanel(greyScene, greyId);
        SliderNamed(greyPanel, Loc.T("L_Saturation"))!.Value = 60;
        Require(await WaitUntilAsync(() => cameraRawScope is { } warm && warm.Vectorscope.Sum() > 0, 8000),
            "a saturated grade never reached the vectorscope grid.");
        SliderNamed(greyPanel, Loc.T("L_Saturation"))!.Value = -100;   // zero on the slider is no change at all
        Require(await WaitUntilAsync(() => cameraRawScope is { } cold && cold.Vectorscope.Sum() == 0, 8000),
            "taking the color out left the grid populated; the Mac skips any pixel with no chroma (CameraRaw.swift:370).");
        Require(cameraRawScope!.Red.Sum() > 0 && cameraRawScope.Blue.Sum() > 0,
            "the histogram emptied with the grid; chroma is not what the rows count.");
        report["cameraRawScopeGridGoesEmptyWhenThePictureDoes"] = "passed";
    }

    /// <summary>
    /// The two permanent clipping indicators under the scope: they paint on the grade, they leave the histogram
    /// alone, an Option view outranks them, and OK never carries them (CameraRaw.swift:400-412).
    /// </summary>
    private async Task VerifyCameraRawClipIndicatorsAsync(Dictionary<string, object> report)
    {
        var scene = TargetedBandsScene();
        OpenDocument(scene); await WaitForFramesAsync();
        var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
        var panel = OpenCameraRawPanel(scene, id);
        var capture = scene.Snapshot.Images[id]!.Clone();
        var current = new Func<Surface>(() => scene.Snapshot.Images[id]!);
        SliderNamed(panel, Loc.T("L_Exposure"))!.Value = 2;
        Require(await WaitUntilAsync(() => Differ(current(), capture) >= 0, 8000), "a +2 exposure never reached the preview.");
        var plainGrade = current().Clone();
        var shadow = ButtonNamed(panel, Loc.T("A_ShadowClipping"));
        var highlight = ButtonNamed(panel, Loc.T("A_HighlightClipping"));
        Require(shadow is not null && highlight is not null,
            $"the panel is missing a clipping indicator (shadow {shadow is not null}, highlight {highlight is not null}).");
        Require(!CameraRawCurrentShadowClipping && !CameraRawCurrentHighlightClipping,
            "a fresh session opened with an indicator already on; the Mac starts both off.");
        // Which triangle can show anything depends on which way the picture is pushed: +2 stops clips nothing at
        // the bottom, so the shadow one would correctly paint nothing. Highlights first, then a pulled-down grade
        // for shadows.
        PressChip(highlight!);
        Require(CameraRawCurrentHighlightClipping
                && ((Microsoft.UI.Xaml.Media.SolidColorBrush)highlight!.Foreground).Color == Microsoft.UI.Colors.Red,
            "pressing the highlight indicator did not both flip the session bit and turn the triangle red.");
        Require(await WaitUntilAsync(() => Differ(current(), plainGrade) >= 0, 8000),
            "the highlight indicator painted nothing on a +2 frame, where channels do sit at pure white.");
        var tinted = current().Clone();
        // Measured on this fixture: exposure −5 alone leaves nothing at pure black (the darkest band starts at
        // 60/255), while −5 with Blacks at −100 puts 144 channels there. Only then can the shadow indicator show
        // anything at all, so the number is not a stylistic choice.
        SliderNamed(panel, Loc.T("L_Exposure"))!.Value = -5;
        SliderNamed(panel, Loc.T("L_Blacks"))!.Value = -100;
        Require(await WaitUntilAsync(() => Differ(current(), tinted) >= 0, 8000), "the pulled-down grade never reached the preview.");
        var darkened = current().Clone();
        // The count that this press must not move. Measured, this is the press that changes pixels — the frame
        // has 144 channels at pure black. Where this check first sat (the *second* press) compared two ticks
        // whose frames are byte-identical, because nothing on a −5/Blacks −100 frame sits at pure white either
        // way, and the "count the histogram after painting it" mutation walked straight through that.
        var scoped = cameraRawScope!;
        PressChip(shadow!);
        Require(CameraRawCurrentShadowClipping
                && ((Microsoft.UI.Xaml.Media.SolidColorBrush)shadow!.Foreground).Color == Microsoft.UI.Colors.MediumBlue,
            "pressing the shadow indicator did not both flip the session bit and turn the triangle blue.");
        Require(await WaitUntilAsync(() => Differ(current(), darkened) >= 0, 8000),
            "the shadow indicator painted nothing, although this frame has 144 channels at pure black.");
        // Every tick makes a new scope (CR-k), so without this the four rows below could agree because nobody
        // recounted at all: a stale scope compared with itself always passes.
        Require(await WaitUntilAsync(() => !ReferenceEquals(cameraRawScope, scoped), 8000),
            "the indicator press never recounted the scope, so the claim below would compare a value with itself.");
        Require(cameraRawScope!.Red.SequenceEqual(scoped.Red) && cameraRawScope.Green.SequenceEqual(scoped.Green)
                && cameraRawScope.Blue.SequenceEqual(scoped.Blue)
                && cameraRawScope.Vectorscope.SequenceEqual(scoped.Vectorscope),
            "the histogram moved with the indicators; the Mac counts the grade before painting them "
            + "(CameraRaw.swift:407-409). Green and blue are in the claim because the blue tint moves those two rows.");
        report["cameraRawClippingIndicatorsDoNotMoveTheHistogram"] = "passed";
        var bothTinted = current().Clone();
        // Turning the highlight one back off cannot show up as a pixel change here: this frame has nothing left
        // at pure white (the grade is at −5 with blacks at −100), so that branch paints nothing either way.
        // What has to hold is that the bit flipped and the shadow tint stayed.
        PressChip(highlight!);
        Require(await WaitUntilAsync(() => !CameraRawCurrentHighlightClipping
                && Differ(current(), darkened) >= 0 && Differ(current(), bothTinted) < 0, 8000),
            "switching the highlight indicator back off either left it on or took the shadow tint with it.");

        // --- An Option view outranks them, and letting go puts the tint back ---
        var tintedFrame = current().Clone();
        SetCameraRawClipping(CameraRawEngine.ClippingShadows, true);
        PreviewCameraRaw(document!, id);
        Require(await WaitUntilAsync(() => Differ(current(), tintedFrame) >= 0, 8000),
            "the highlights view did not replace the tinted frame; the Mac's view wins (CameraRaw.swift:410).");
        ReleaseCameraRawViews();
        Require(await WaitUntilAsync(() => Differ(current(), tintedFrame) < 0, 8000),
            "letting go of the view did not put the tinted frame back.");
        report["cameraRawOptionViewsOutrankTheClippingIndicators"] = "passed";

        // --- And none of it reaches the layer ---
        // Every wait in this tail is on a *published frame*, not on the draft or the session bits: a slider write
        // or a triangle press flips those synchronously and grades a dispatcher turn later. Reading the frame
        // before a publish lands is what made the old claim here compare against a copy from several ticks back.
        // A tick always hands over a freshly made scope (CR-k), so its reference moving is the proof of a landing.
        var scopeAt = cameraRawScope!;
        bool Landed() => !ReferenceEquals(cameraRawScope, scopeAt);
        void Rearm() => scopeAt = cameraRawScope!;
        SliderNamed(panel, Loc.T("L_Blacks"))!.Value = 0;
        SliderNamed(panel, Loc.T("L_Exposure"))!.Value = 2;
        Require(await WaitUntilAsync(() => cameraRawDraft is { Exposure: 2, Blacks: 0 } && Landed(), 8000),
            "the +2 grade never came back down to the layer.");
        Rearm();
        // Put both triangles down and keep THAT frame as what the layer has to end up holding. The claim used to
        // be "the committed frame differs from the tinted one", which cannot bite twice over: Differ reports a
        // size mismatch as differing, so any difference at all satisfied it, and the tinted frame it was
        // measured against was a stale copy. The mutation that bakes the tint into the commit went through both
        // holes. Byte equality against the plain grade is the only claim here that says anything.
        if (CameraRawCurrentHighlightClipping) PressChip(highlight!);
        if (CameraRawCurrentShadowClipping) PressChip(shadow!);
        Require(await WaitUntilAsync(() => !CameraRawCurrentHighlightClipping && !CameraRawCurrentShadowClipping
                && Landed(), 8000),
            "the frame with both indicators off never landed, so the equality below would have a stale reference.");
        var cleanPlus = current().Clone();
        // Highlight is the one that paints on a +2 frame (shadow has nothing at pure black there), so this is
        // the indicator the commit must not carry.
        PressChip(highlight!);
        Require(await WaitUntilAsync(() => CameraRawCurrentHighlightClipping && Differ(current(), cleanPlus) >= 0, 8000),
            "the highlight indicator never came back on, so OK would be pressed with nothing to bake.");
        var tintedPlus = current().Clone();
        PressChip(CameraRawSheetButton(confirm: true));
        Require(await WaitUntilAsync(() => cameraRawSessionKey is null, 8000),
            "OK never ended the session it committed with an indicator on.");
        Require(await WaitUntilAsync(() => Differ(current(), cleanPlus) < 0, 8000),
            "OK baked the clipping tint into the layer: what it wrote is not byte-for-byte the plain grade the "
            + "preview showed with both indicators off.");
        var committed = current().Clone();
        Require(Differ(committed, tintedPlus) >= 0, "the committed frame is the tinted preview frame, not the grade.");
        // Ending the session puts both views off (the Mac holds them on FilterEdit, Filters.swift:336-339), and
        // the old buttons are detached controls by now, so this is read off the session bits rather than colour.
        Require(!CameraRawCurrentHighlightClipping && !CameraRawCurrentShadowClipping,
            "the session rebuilt by OK started with an indicator still on; the commit job carries neither the "
            + "flags nor the buttons' state (Filters.swift:620-622).");
        report["cameraRawClippingIndicatorsStayOutOfTheLayer"] = "passed";
    }

    /// <summary>A Camera Raw band by the title it carries. The header used to be that string; it is now the
    /// title plus the group's eye, so a finder asks the band's Tag instead of unpicking the header.</summary>
    private static Expander? CameraRawBand(Panel root, string title) =>
        root.Children.OfType<Expander>().FirstOrDefault(row => row.Tag as string == title);
    /// <summary>How many of the Point Color page's six rows the panel has built right now.</summary>
    private static int pointRowsVisible(Microsoft.UI.Xaml.DependencyObject element)
    {
        var labels = new[] { "L_PointHueShift", "L_PointSaturationShift", "L_PointLuminanceShift",
            "L_PointHueRange", "L_PointSaturationRange", "L_PointLuminanceRange" };
        return labels.Count(label => SliderNamed(element, Loc.T(label)) is not null);
    }

    private static int pointChips(Microsoft.UI.Xaml.DependencyObject element) =>
        Enumerable.Range(1, CameraRawMixer.PointsMax + 2)
            .Count(index => ButtonNamed(element, $"{Loc.T("A_PointColorChip")} {index}") is not null);

    private static void AssertPickedGreen(CameraRawMixerPoint point, string what)
    {
        AssertPicked(point, what, 131.25, 160.0 / 220.0, 140.0 / 255.0);
    }

    private static void AssertPicked(CameraRawMixerPoint point, string what, double hue, double saturation, double luminance)
    {
        Require(Math.Abs(point.Hue - hue) < 1e-6 && Math.Abs(point.Saturation - saturation) < 1e-6
                && Math.Abs(point.Luminance - luminance) < 1e-6,
            $"{what} was filed as ({point.Hue}, {point.Saturation}, {point.Luminance}), "
            + $"and the eyedropper reads the clicked pixel as ({hue}, {saturation}, {luminance}).");
    }

    /// <summary>The first byte the two layers disagree on, as an index into a row-major grid, or -1. Byte-for-byte comparisons are what say a number reached the grade rather than the sampler.</summary>
    private static int Differ(Surface a, Surface b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
                for (int channel = 0; channel < 4; channel++)
                {
                    int at = y * a.Stride + x * 4 + channel;
                    if (a.Pixels[at] != b.Pixels[at]) return y * a.Width + x;
                }
        return -1;
    }

    private static string Where(int index, int width) =>
        index < 0 ? "nowhere" : $"({index % width}, {index / width})";

    private static (byte, byte, byte) PixelAt(Surface surface, int x, int y)
    {
        int at = y * surface.Stride + x * 4;
        return (surface.Pixels[at], surface.Pixels[at + 1], surface.Pixels[at + 2]);
    }

    /// <summary>Finds a row by what it reports to a reader, because the panel builds these rows in the order
    /// the Mac lists them and an index would move with the next group the same way a label would not.</summary>
    private static Slider? SliderNamed(Microsoft.UI.Xaml.DependencyObject element, string name) => element switch
    {
        Slider slider when Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(slider) == name => slider,
        Panel panel => panel.Children.Select(child => SliderNamed(child, name)).FirstOrDefault(found => found is not null),
        ContentControl content when content.Content is Microsoft.UI.Xaml.DependencyObject inner => SliderNamed(inner, name),
        _ => null,
    };

    private static ComboBox? ComboBoxNamed(Microsoft.UI.Xaml.DependencyObject element, string name) => element switch
    {
        ComboBox box when Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(box) == name => box,
        Panel panel => panel.Children.Select(child => ComboBoxNamed(child, name)).FirstOrDefault(found => found is not null),
        ContentControl content when content.Content is Microsoft.UI.Xaml.DependencyObject inner => ComboBoxNamed(inner, name),
        _ => null,
    };

/// <summary>How a line of the panel's own text is being shown right now, or null when the panel has no such
    /// line. <see cref="ContainsText"/> cannot answer this: it walks the elements the panel built, and a hidden
    /// caption is still one of them.</summary>
    private static Visibility? ShownTextVisibility(Microsoft.UI.Xaml.DependencyObject element, string text) => element switch
    {
        TextBlock block when block.Text == text => block.Visibility,
        Panel panel => panel.Children.Select(child => ShownTextVisibility(child, text)).FirstOrDefault(found => found is not null),
        ContentControl content when content.Content is Microsoft.UI.Xaml.DependencyObject inner => ShownTextVisibility(inner, text),
        _ => null,
    };

    private static ToggleButton? ToggleButtonNamed(Microsoft.UI.Xaml.DependencyObject element, string name) => element switch
    {
        ToggleButton button when Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(button) == name => button,
        Panel panel => panel.Children.Select(child => ToggleButtonNamed(child, name)).FirstOrDefault(found => found is not null),
        ContentControl content when content.Content is Microsoft.UI.Xaml.DependencyObject inner => ToggleButtonNamed(inner, name),
        _ => null,
    };

    private static CheckBox? CheckBoxNamed(Microsoft.UI.Xaml.DependencyObject element, string name) => element switch
    {
        CheckBox box when Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(box) == name => box,
        Panel panel => panel.Children.Select(child => CheckBoxNamed(child, name)).FirstOrDefault(found => found is not null),
        ContentControl content when content.Content is Microsoft.UI.Xaml.DependencyObject inner => CheckBoxNamed(inner, name),
        _ => null,
    };

    /// <summary>Plain buttons by reader name too — Upright's Clear Guides is one, and a ToggleButton is a Button,
    /// so the name is what tells them apart here.</summary>
    private static Button? ButtonNamed(Microsoft.UI.Xaml.DependencyObject element, string name) => element switch
    {
        Button button when Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(button) == name => button,
        Panel panel => panel.Children.Select(child => ButtonNamed(child, name)).FirstOrDefault(found => found is not null),
        ContentControl content when content.Content is Microsoft.UI.Xaml.DependencyObject inner => ButtonNamed(inner, name),
        _ => null,
    };
}
#endif
