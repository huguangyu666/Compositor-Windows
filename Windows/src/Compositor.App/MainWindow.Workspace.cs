using System.Globalization;
using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Input;
using Windows.System;

namespace Compositor.App;

public sealed class WorkspaceResizeEdge : Control
{
    public WorkspaceResizeEdge()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        Template = (ControlTemplate)XamlReader.Load("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Control'><Border Background='{TemplateBinding Background}'/></ControlTemplate>");
    }
}

public sealed partial class MainWindow
{
    private readonly Dictionary<CanvasTool, ToggleButton> toolButtons = new();
    private (double X, double Width)? panelResizeStart;
    private static string WorkspaceWidthPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Compositor", "layers-panel-width.txt");

    /// <summary>
    /// Where one-value preference files live, the same shape as the log directory override: a process
    /// can point it elsewhere with COMPOSITOR_PREFERENCES_DIRECTORY so a probe or a verification run
    /// never rewrites what the real editor reads. Nothing is written to the system environment.
    /// </summary>
    private static string PreferencesDirectory => Path.Combine(
        Environment.GetEnvironmentVariable("COMPOSITOR_PREFERENCES_DIRECTORY") is { Length: > 0 } overlay
            ? overlay
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Compositor"));

    // View ▸ Guides / Snap / Lock Guides: the Mac keeps these three as switches on the session
    // (CompositorApp.swift:128-129, :136-137, :150-151) with persisted values defaulting to on, on, off
    // (EditorSession.swift:301, :304, :309). Same single-value files the ruler visibility and the transform
    // switches already use.
    private const string GuidesPreference = "show-guides";
    private const string SnapPreference = "snap-enabled";
    private const string LockedGuidesPreference = "lock-guides";

    private bool showsGuides = true, snapEnabled = true, locksGuides;

    // View ▸ Snap To (CompositorApp.swift:138-148 → Guides.swift:251-283): which kinds of alignment lines a
    // drag may use. Defaults match EditorSession.swift:305/306/307/308: guides, layers and bounds on, grid off.
    private const string SnapToGuidesPreference = "snap-to-guides";
    private const string SnapToGridPreference = "snap-to-grid";
    private const string SnapToLayersPreference = "snap-to-layers";
    private const string SnapToBoundsPreference = "snap-to-bounds";

    private bool snapToGuides = true, snapToGrid, snapToLayers = true, snapToBounds = true;

    // View ▸ Grid (⌘', CompositorApp.swift:126-127) and Grid Settings' two numbers, persisted as the Mac
    // persists them (EditorSession.swift:279 showsGrid=false, :281 layoutGrid=64 split eight ways).
    private const string ShowGridPreference = "show-grid";
    private const string GridSpacingPreference = "grid-spacing";
    private const string GridSubdivisionsPreference = "grid-subdivisions";

    private bool showsGrid;
    private int gridSpacing = 64, gridSubdivisions = 8;

    // View ▸ Show Pixel Grid (EditorSession.swift:277 keeps this one under "pixelGrid"): it belongs to the
    // person, not to the document, so a relaunch comes back with it as it was left.
    private const string PixelGridPreference = "show-pixel-grid";

    /// The grid the canvas draws and the drags snap to, rebuilt whenever either number changes.
    private LayoutGrid LayoutGrid { get; set; } = LayoutGrid.Default;

    // View ▸ Grid Settings… (CompositorApp.swift:131-132 → GridSettingsSheet.swift) keeps four more values,
    // under the same names the Mac keeps them (EditorSession.swift:288-297): the color preset's and style's
    // display names, the custom color as #RRGGBB, and the opacity in percent.
    private const string GridColorPreference = "grid-color";
    private const string GridCustomColorPreference = "grid-custom-color";
    private const string GridStylePreference = "grid-style";
    private const string GridOpacityPreference = "grid-opacity";

    /// How the grid is drawn: color, the majors' pattern, and how solid they are.
    private GridAppearance GridAppearance { get; set; } = GridAppearance.Default;

    private void ApplyViewVisibilityOptions()
    {
        showsGuides = ReadFlag(GuidesPreference, showsGuides);
        snapEnabled = ReadFlag(SnapPreference, snapEnabled);
        locksGuides = ReadFlag(LockedGuidesPreference, locksGuides);
        snapToGuides = ReadFlag(SnapToGuidesPreference, snapToGuides);
        snapToGrid = ReadFlag(SnapToGridPreference, snapToGrid);
        snapToLayers = ReadFlag(SnapToLayersPreference, snapToLayers);
        snapToBounds = ReadFlag(SnapToBoundsPreference, snapToBounds);
        showsGrid = ReadFlag(ShowGridPreference, showsGrid);
        showPixelGrid = ReadFlag(PixelGridPreference, showPixelGrid);
        gridSpacing = ReadNumber(GridSpacingPreference, gridSpacing, LayoutGrid.SpacingMin, LayoutGrid.SpacingMax);
        gridSubdivisions = ReadNumber(GridSubdivisionsPreference, gridSubdivisions, LayoutGrid.SubdivisionMin, LayoutGrid.SubdivisionMax);
        LayoutGrid = new LayoutGrid(gridSpacing, gridSubdivisions);
        GridAppearance = ReadGridAppearance();
        GuidesToggle.IsChecked = showsGuides;
        SnapToggle.IsChecked = snapEnabled;
        LockGuidesToggle.IsChecked = locksGuides;
        SnapToGuidesToggle.IsChecked = snapToGuides;
        SnapToGridToggle.IsChecked = snapToGrid;
        SnapToLayersToggle.IsChecked = snapToLayers;
        SnapToBoundsToggle.IsChecked = snapToBounds;
        GridToggle.IsChecked = showsGrid;
        PixelGridToggle.IsChecked = showPixelGrid;
        McpAnimationToggle.IsChecked = ReadFlag("mcp-operation-animation", true);
        RefreshGuides();
        QueueViewportFrame();
    }

    /// <summary>Reads the four appearance values as one. A custom color the file cannot express leaves the
    /// one already in use standing, so a half-written file never erases a picked color.</summary>
    private GridAppearance ReadGridAppearance()
    {
        AdjustmentColor? hex = GridColorFromHex(ReadText(GridCustomColorPreference));
        return new GridAppearance(
            GridAppearance.ParsePreset(ReadText(GridColorPreference)),
            hex ?? GridAppearance.CustomColor,
            GridAppearance.ParseStyle(ReadText(GridStylePreference)),
            ReadNumber(GridOpacityPreference, GridAppearance.Opacity, GridAppearance.OpacityMin, GridAppearance.OpacityMax));
    }

    /// <summary>
    /// View ▸ Grid Settings… comes here twice: once per edit as the live preview the Mac gives
    /// (GridSettingsSheet.swift:119-123 previews on every change), and once on OK with <paramref name="persist"/>
    /// set, which is when the values reach the files (EditorSession.swift:283-297). Cancel is the same call with
    /// the values the sheet opened with and nothing written.
    /// </summary>
    private void SetGridSettings(LayoutGrid grid, GridAppearance appearance, bool persist)
    {
        gridSpacing = grid.Spacing; gridSubdivisions = grid.Subdivisions; LayoutGrid = grid;
        GridAppearance = appearance;
        if (persist)
        {
            WriteText(GridSpacingPreference, gridSpacing.ToString());
            WriteText(GridSubdivisionsPreference, gridSubdivisions.ToString());
            WriteText(GridColorPreference, GridAppearance.RawName(appearance.ColorPreset));
            WriteText(GridCustomColorPreference, GridColorToHex(appearance.CustomColor));
            WriteText(GridStylePreference, GridAppearance.RawName(appearance.Style));
            WriteText(GridOpacityPreference, appearance.Opacity.ToString());
        }
        QueueViewportFrame();
    }

    /// <summary>Reads one persisted number, clamped into the range the model would clamp it into anyway, so
    /// what a launch loads is what it will draw. Anything unparseable is a first launch, not an error.</summary>
    private static int ReadNumber(string name, int fallback, int min, int max)
    {
        try
        {
            string path = Path.Combine(PreferencesDirectory, name + ".txt");
            if (!File.Exists(path)) return fallback;
            string text = File.ReadAllText(path).Trim();
            if (int.TryParse(text, out int value)) return Math.Min(Math.Max(value, min), max);
            App.Log($"Preference {name} held \"{text}\", which is not a number.");
            return fallback;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Log($"Preference {name} load failed: {ex.Message}");
            return fallback;
        }
    }

    /// <summary>Reads one persisted line of text, or null when there is nothing readable: the model then
    /// chooses its own default, the way the Mac reads an empty <c>ToolDefaults.string</c> (EditorSession.swift:289-291).</summary>
    private static string? ReadText(string name)
    {
        try
        {
            string path = Path.Combine(PreferencesDirectory, name + ".txt");
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Log($"Preference {name} load failed: {ex.Message}");
            return null;
        }
    }

    private static void WriteText(string name, string value)
    {
        try
        {
            Directory.CreateDirectory(PreferencesDirectory);
            File.WriteAllText(Path.Combine(PreferencesDirectory, name + ".txt"), value);
        }
        catch (IOException ex) { App.Log($"Preference {name} save failed: {ex.Message}"); }
        catch (UnauthorizedAccessException ex) { App.Log($"Preference {name} save failed: {ex.Message}"); }
    }

    /// <summary>The Mac's #RRGGBB (ColorPalette.swift:361-367): a leading # is optional, three digits are
    /// doubled, and anything else is not a color — which is exactly what a first launch looks like.</summary>
    private static AdjustmentColor? GridColorFromHex(string? text)
    {
        if (text is not { Length: > 0 }) return null;
        string digits = text.StartsWith('#') ? text[1..] : text;
        if (digits.Length == 3) digits = string.Concat(digits.Select(digit => new string(digit, 2)));
        if (digits.Length != 6 || !uint.TryParse(digits, System.Globalization.NumberStyles.HexNumber, null, out uint value)) return null;
        return new AdjustmentColor(((value >> 16) & 0xFF) / 255.0, ((value >> 8) & 0xFF) / 255.0, (value & 0xFF) / 255.0);
    }

    private static string GridColorToHex(AdjustmentColor color)
    {
        byte Channel(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
        return $"#{Channel(color.Red):X2}{Channel(color.Green):X2}{Channel(color.Blue):X2}";
    }

    /// <summary>
    /// The one way the Guides switch changes: the menu tick, the drawing flag, the file on disk and the
    /// viewport all move together. The Mac gets this from a SwiftUI binding whose setter persists
    /// (EditorSession.swift:301), and pulling a guide out of a ruler turns guides back on — a guide the
    /// user cannot see is not a guide — which is why drag creation calls through here too.
    /// </summary>
    private void SetGuidesVisible(bool value)
    {
        showsGuides = value;
        GuidesToggle.IsChecked = value;
        WriteFlag(GuidesPreference, value);
        RefreshGuides();
    }

    private void OnToggleGuides(object sender, RoutedEventArgs e) => SetGuidesVisible(GuidesToggle.IsChecked == true);

    private void OnToggleSnap(object sender, RoutedEventArgs e)
    {
        snapEnabled = SnapToggle.IsChecked == true;
        WriteFlag(SnapPreference, snapEnabled);
        // A drag already in flight cached its targets when it started; dropping them now stops it from
        // snapping against a list built while snap was still on — the Mac clears its cached snap guides in
        // the same way for the crop snap switch (EditorSession.swift:182-185).
        if (!snapEnabled) snapXs = snapYs = null;
    }

    private void OnToggleLockGuides(object sender, RoutedEventArgs e)
    {
        locksGuides = LockGuidesToggle.IsChecked == true;
        WriteFlag(LockedGuidesPreference, locksGuides);
    }

    // Changing which kinds of lines count invalidates the list a drag in flight is using, the same way
    // switching Snap off does: the cached set was built under the old configuration.
    private void OnToggleSnapToGuides(object sender, RoutedEventArgs e)
    {
        snapToGuides = SnapToGuidesToggle.IsChecked == true;
        WriteFlag(SnapToGuidesPreference, snapToGuides);
        snapXs = snapYs = null;
    }

    private void OnToggleSnapToLayers(object sender, RoutedEventArgs e)
    {
        snapToLayers = SnapToLayersToggle.IsChecked == true;
        WriteFlag(SnapToLayersPreference, snapToLayers);
        snapXs = snapYs = null;
    }

    private void OnToggleSnapToBounds(object sender, RoutedEventArgs e)
    {
        snapToBounds = SnapToBoundsToggle.IsChecked == true;
        WriteFlag(SnapToBoundsPreference, snapToBounds);
        snapXs = snapYs = null;
    }

    private void OnToggleShowGrid(object sender, RoutedEventArgs e) => SetGridVisible(GridToggle.IsChecked == true);

    /// <summary>
    /// The one way the Grid switch changes: the menu tick, the drawing flag, the file on disk and the viewport
    /// move together — the same shape as <see cref="SetGuidesVisible"/>. View ▸ Grid Settings… also runs through
    /// here, because the Mac turns the grid on for as long as that sheet is open so an edit has something to
    /// change on screen, and puts back whatever the switch said when the sheet closes
    /// (ProjectController.swift:136-137, :155).
    /// </summary>
    private void SetGridVisible(bool value)
    {
        showsGrid = value;
        GridToggle.IsChecked = value;
        WriteFlag(ShowGridPreference, value);
        // The grid is drawn by the viewport pass, so it needs a frame rather than a document rebuild.
        QueueViewportFrame();
    }

    private void OnToggleSnapToGrid(object sender, RoutedEventArgs e)
    {
        snapToGrid = SnapToGridToggle.IsChecked == true;
        WriteFlag(SnapToGridPreference, snapToGrid);
        snapXs = snapYs = null;
    }

    /// <summary>Reads one persisted flag, defaulting to <paramref name="fallback"/> for anything the
    /// editor cannot understand: a missing, unreadable or malformed file is a first launch, not an error.</summary>
    private static bool ReadFlag(string name, bool fallback)
    {
        try
        {
            string path = Path.Combine(PreferencesDirectory, name + ".txt");
            if (!File.Exists(path)) return fallback;
            string text = File.ReadAllText(path).Trim();
            if (bool.TryParse(text, out bool value)) return value;
            App.Log($"Preference {name} held \"{text}\", which is not a flag.");
            return fallback;
        }
        catch (IOException ex) { App.Log($"Preference {name} load failed: {ex.Message}"); return fallback; }
        catch (UnauthorizedAccessException ex) { App.Log($"Preference {name} load failed: {ex.Message}"); return fallback; }
    }

    /// <summary>Writes one persisted flag. A preference that cannot be saved must not interrupt editing.</summary>
    private static void WriteFlag(string name, bool value)
    {
        try
        {
            Directory.CreateDirectory(PreferencesDirectory);
            File.WriteAllText(Path.Combine(PreferencesDirectory, name + ".txt"), value ? "true" : "false");
        }
        catch (IOException ex) { App.Log($"Preference {name} save failed: {ex.Message}"); }
        catch (UnauthorizedAccessException ex) { App.Log($"Preference {name} save failed: {ex.Message}"); }
    }

    private void InitializeWorkspace()
    {
        // Group the rail without replacing the handler that owns drafts and parked brush tips.
        CanvasTool[] order = [CanvasTool.Move, CanvasTool.RectSelect, CanvasTool.EllipseSelect,
            CanvasTool.Lasso, CanvasTool.Wand, CanvasTool.Crop, CanvasTool.Brush, CanvasTool.Eraser,
            CanvasTool.Clone, CanvasTool.Healing, CanvasTool.Blur, CanvasTool.Eyedropper, CanvasTool.Gradient, CanvasTool.Text, CanvasTool.Shape,
            CanvasTool.Hand, CanvasTool.Zoom];
        Grid? toolGroup = null;
        int toolIndex = 0;
        foreach (var item in order)
        {
            if (toolIndex is 0 or 6 or 12 or 15)
            {
                if (toolGroup is not null) ToolButtons.Children.Add(new Border { Height = 1, Margin = new Thickness(6, 6, 6, 6), Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 65, 65, 65)) });
                toolGroup = new Grid(); toolButtonGroups.Add(toolGroup); ToolButtons.Children.Add(toolGroup);
            }
            var button = new ToggleButton
            {
                Width = 36, Height = 36, MinWidth = 0, Padding = new Thickness(0), Margin = new Thickness(2),
                CornerRadius = new CornerRadius(5), Tag = item,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Content = WorkspaceToolIcon(item),
            };
            string label = ToolLabel(item), shortcut = ToolShortcut(item);
            foreach (string state in new[] { "Checked", "CheckedPointerOver", "CheckedPressed" })
            {
                button.Resources[$"ToggleButtonBackground{state}"] = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 62, 62, 62));
                button.Resources[$"ToggleButtonBorderBrush{state}"] = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 90, 90, 90));
                button.Resources[$"ToggleButtonForeground{state}"] = new SolidColorBrush(Microsoft.UI.Colors.White);
            }
            ToolTipService.SetToolTip(button, $"{label} ({shortcut})");
            AutomationProperties.SetName(button, label);
            AutomationProperties.SetAutomationId(button, $"Tool_{item}");
            button.Click += OnToolRailClick;
            toolButtons.Add(item, button); toolGroup!.Children.Add(button); toolIndex++;
        }
        InitializeToolbarLayout();
        InitializeSelectionModes();
        InitializeQuickTools();
        InitializeLayerProtection();
        InitializeHistoryPanel();
        foreach (var kind in Enum.GetValues<AdjustmentKind>())
        {
            var command = new MenuFlyoutItem { Text = Loc.Name("Adjustment", kind.ToString()), Tag = kind.ToString() };
            command.Click += OnAddAdjustment; FooterAdjustmentMenu.Items.Add(command);
        }
        foreach (var button in LayerCommands.Children.OfType<Button>())
            if (ToolTipService.GetToolTip(button) is string label) AutomationProperties.SetName(button, label);
        AutomationProperties.SetName(ForegroundColorChip, (string)ToolTipService.GetToolTip(ForegroundColorChip));
        AutomationProperties.SetName(BackgroundColorChip, (string)ToolTipService.GetToolTip(BackgroundColorChip));
        // The two zoom buttons are a glyph and the two palette chips an arrow: neither says anything to a reader
        // on its own, and the header has no label beside them to borrow. Their tooltips already carry the
        // localized words, so the same "a string tooltip names the button" rule the layer commands use applies.
        // The two zoom buttons are a glyph and the two palette chips an arrow. Their tooltips say what they do,
        // but a tooltip is rewritten whenever a shortcut is rebound (the navigation probe reads the live text),
        // while the name a reader gets is set once — so these carry their own strings instead of borrowing one
        // that moves underneath them.
        AutomationProperties.SetName(ZoomInButton, Loc.T("A_ZoomIn"));
        AutomationProperties.SetName(ZoomOutButton, Loc.T("A_ZoomOut"));
        AutomationProperties.SetName(SwapColorsChip, Loc.T("A_SwapColors"));
        AutomationProperties.SetName(ResetColorsChip, Loc.T("A_ResetColors"));
        // The ten controls a strip shows without a word beside it. The macOS originals are all Pickers and
        // Sliders that carry their own label ("Mode", "Sample", "Tolerance"), which the strip's layout draws;
        // here the same words never reach the screen, so the reader has to be told out of the resources.
        // x:Uid can set AutomationProperties.Name, but nothing can read that string back — the loader only
        // answers the plain keys — so these are named the way the transform panel's targets already are.
        AutomationProperties.SetName(NavigationZoomBox, Loc.T("A_NavZoom"));
        AutomationProperties.SetName(EyedropperTargetCombo, Loc.T("A_EyeTarget"));
        AutomationProperties.SetName(HealingModeCombo, Loc.T("A_HealMode"));
        AutomationProperties.SetName(GradientShapeCombo, Loc.T("A_GradientShape"));
        AutomationProperties.SetName(GradientStyleCombo, Loc.T("A_GradientStyle"));
        AutomationProperties.SetName(LassoKindCombo, Loc.T("A_LassoKind"));
        AutomationProperties.SetName(CropRatioCombo, Loc.T("A_CropRatio"));
        AutomationProperties.SetName(ShapeKindCombo, Loc.T("A_ShapeKind"));
        AutomationProperties.SetName(BlurModeCombo, Loc.T("A_BlurMode"));
        LayersPane.SizeChanged += (_, _) => ResizeLayerProperties();
        LayerAppearance.SizeChanged += (_, _) => ResizeLayerProperties();
        RootGrid.SizeChanged += (_, _) => SetLayersPanelWidth(LayersColumn.Width.Value);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            try
            {
                if (File.Exists(WorkspaceWidthPath) && double.TryParse(File.ReadAllText(WorkspaceWidthPath),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out double width) && double.IsFinite(width))
                    SetLayersPanelWidth(width);
            }
            catch (IOException ex) { App.Log($"Workspace width load failed: {ex.Message}"); }
            catch (UnauthorizedAccessException ex) { App.Log($"Workspace width load failed: {ex.Message}"); }
        });
    }

    private static string ToolShortcut(CanvasTool item) => item switch
    {
        CanvasTool.Move => "V", CanvasTool.RectSelect => "M", CanvasTool.EllipseSelect => "Shift+M",
        CanvasTool.Lasso => "L", CanvasTool.Wand => "W", CanvasTool.Crop => "C", CanvasTool.Brush => "B",
        CanvasTool.Eraser => "E", CanvasTool.Clone => "S", CanvasTool.Healing => "J", CanvasTool.Blur => "R",
        CanvasTool.Gradient => "G", CanvasTool.Text => "T", CanvasTool.Shape => "U", CanvasTool.Eyedropper => "I", CanvasTool.Hand => "H", CanvasTool.Zoom => "Z", _ => "",
    };

    private static UIElement WorkspaceToolIcon(CanvasTool item)
    {
        string data = item switch
        {
            CanvasTool.Move => "M12,2 L12,22 M2,12 L22,12 M8,6 L12,2 L16,6 M8,18 L12,22 L16,18 M6,8 L2,12 L6,16 M18,8 L22,12 L18,16",
            CanvasTool.RectSelect => "M3,3 L7,3 M10,3 L14,3 M17,3 L21,3 L21,7 M21,10 L21,14 M21,17 L21,21 L17,21 M14,21 L10,21 M7,21 L3,21 L3,17 M3,14 L3,10 M3,7 L3,3",
            CanvasTool.EllipseSelect => "M7,3 Q3,4 3,8 M3,11 Q2,15 5,18 M8,21 Q12,23 16,21 M19,18 Q22,15 21,11 M21,8 Q20,4 16,3 M13,2 L10,2",
            CanvasTool.Lasso => "M6,17 C0,14 2,3 12,3 C23,3 25,15 17,17 C10,20 5,16 8,13 C11,11 13,17 9,20 L6,22",
            CanvasTool.Wand => "M4,21 L17,8 L20,11 L7,24 M3,4 L8,4 M5.5,1.5 L5.5,6.5 M17,2 L17,5 M21,6 L24,6",
            CanvasTool.Crop => "M6,1 L6,18 L23,18 M1,6 L18,6 L18,23 M9,15 L21,3",
            CanvasTool.Brush => "M10,15 C9,12 17,4 21,2 C23,5 16,13 13,16 Z M9,15 C2,14 7,21 2,22 C11,23 14,18 9,15 Z",
            CanvasTool.Eraser => "M2,15 L13,3 L22,11 L11,22 L8,22 Z M7,10 L16,18 M11,22 L23,22",
            CanvasTool.Clone => "M8,14 L8,10 C3,3 8,1 12,1 C17,1 21,4 16,10 L16,14 M5,14 L19,14 L21,20 L3,20 Z M4,23 L20,23",
            CanvasTool.Healing => "M3,12 L12,3 C18,-3 27,6 21,12 L12,21 C6,27 -3,18 3,12 Z M7,8 L16,17 M9,6 L18,15 M10,10 L11,11 M13,13 L14,14",
            CanvasTool.Blur => "M12,2 C10,7 4,11 4,16 C4,25 20,25 20,16 C20,11 14,7 12,2 Z M8,16 Q8,20 12,20",
            CanvasTool.Gradient => "M3,4 L21,4 L21,20 L3,20 Z M7,4 L7,20 M10,4 L10,20 M12,4 L12,20 M14,4 L14,20 M16,4 L16,20 M18,4 L18,20 M20,4 L20,20",
            CanvasTool.Text => "M3,6 L3,3 L21,3 L21,6 M12,3 L12,22 M7,22 L17,22",
            CanvasTool.Shape => "M3,3 L21,3 L21,21 L3,21 Z",
            CanvasTool.Eyedropper => "M5,16 L2,22 L8,20 L18,10 M5,16 L9,20 M13,5 L20,12 M16,3 C20,-1 27,6 23,10 L18,10 L16,8 Z",
            CanvasTool.Hand => "M5,12 L5,6 Q6,3 8,6 L8,11 L8,3 Q10,0 12,3 L12,10 L12,3 Q14,1 16,4 L16,11 L16,6 Q19,3 20,7 L20,17 Q19,23 13,23 L9,23 L2,15 Q0,10 3,11 L7,15",
            CanvasTool.Zoom => "M18,10 A8,8 0 1 1 2,10 A8,8 0 1 1 18,10 M16,16 L23,23 M6,10 L14,10 M10,6 L10,14",
            _ => "",
        };
        return (UIElement)XamlReader.Load($"<Viewbox xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Width='19' Height='19'><Canvas Width='26' Height='26'><Path Data='{data}' Stroke='#FFE2E2E2' StrokeThickness='1.6' StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeLineJoin='Round'/></Canvas></Viewbox>");
    }

    private void OnToolRailClick(object sender, RoutedEventArgs args)
    {
        if (sender is ToggleButton { Tag: CanvasTool next }) ToolCombo.SelectedIndex = (int)next;
        RefreshToolRail(); // Clicking the selected button must leave one tool selected.
        CanvasScroll.Focus(FocusState.Programmatic);
    }

    private void RefreshToolRail()
    {
        ActiveToolLabel.Text = ToolLabel(tool);
        foreach (var (item, button) in toolButtons) button.IsChecked = item == tool;
        ToolTipService.SetToolTip(ActiveToolLabel, ActiveToolLabel.Text);
        if (toolButtons.TryGetValue(tool, out var selected)) selected.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        ToolOptionsScroll.ChangeView(0, null, null, true);
        UpdateTransformHandles();
    }

    /// <summary>The letter-to-tool routing, kept as its own call so a probe can drive it: no probe can
    /// synthesize the key press itself, and the whole point of the table is that the matcher and the sheet
    /// agree on it. ⇧M picks the ellipse, as the Mac's marquee key does, and every other letter is the tool
    /// its row names.</summary>
    internal static CanvasTool? ToolForPress(VirtualKey key, bool shift)
    {
        var hit = CanvasToolKeys.FirstOrDefault(entry => entry.Key == key);
        return hit.Id is null ? null : shift && hit.Tool == CanvasTool.RectSelect ? CanvasTool.EllipseSelect : hit.Tool;
    }

    private bool HandleWorkspaceToolKey(KeyRoutedEventArgs args)
    {
        if (renamingLayer || textDialogOpen || args.OriginalSource is TextBox or RichEditBox
            || args.KeyStatus.IsMenuKeyDown || Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return false;
        bool shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        // Remapped canvas keys are translated once, here, rather than in each matcher below: the switch keeps
        // reading the letters it was written with, so a rebind cannot leave a stale branch behind, and a key
        // whose tool has moved away is swallowed instead of reaching whatever now sits at that letter.
        var translated = CanvasKeyFor(args.Key, shift);
        if (translated.Swallowed) { args.Handled = true; return true; }
        var canvasKey = translated.Key;
        // The Mac's ⇧U steps the shape kind once the Shape tool is already active, and just selects Shape
        // from any other tool (EditorCanvas.swift:2223-2224 → ShapeTool.swift:107). Its Tab-mode cycling
        // (EditorCanvas.swift:2202-2206) is deliberately not ported: on Windows Tab is how focus moves, and
        // taking it at Content.KeyDown would break keyboard navigation for a shortcut the Mac reserves for
        // the canvas being the responder. The kinds stay reachable by ⇧U and through the combo.
        if (canvasKey == VirtualKey.U && shift && tool == CanvasTool.Shape)
        {
            StepShapeKind();
            args.Handled = true; return true;
        }
        if (ToolForPress(canvasKey, shift) is not { } selected) return false;
        ToolCombo.SelectedIndex = (int)selected;
        RefreshToolRail(); args.Handled = true; return true;
    }

    // The ⇧U step, kept as its own call so the cycling can be driven straight from a probe (no probe can
    // synthesize the key event itself).
    internal void StepShapeKind()
        => ShapeKindCombo.SelectedIndex = (ShapeKindCombo.SelectedIndex + 1) % Math.Max(1, ShapeKindCombo.Items.Count);

    private void SetLayersPanelWidth(double width)
    {
        double available = RootGrid.ActualWidth > 0 ? Math.Max(202, RootGrid.ActualWidth - 56 - 5 - 240) : 352;
        LayersColumn.Width = new GridLength(Math.Clamp(width, 202, Math.Min(352, available)));
    }

    private void ResizeLayerProperties()
    {
        bool compact = LayersPane.ActualHeight < 260;
        LayersHeader.Padding = new Thickness(12, compact ? 6 : 10, 12, compact ? 6 : 10);
        LayerAppearance.Padding = new Thickness(12, compact ? 6 : 10, 12, compact ? 6 : 10);
        LayerAppearance.Spacing = compact ? 4 : 8;
        foreach (var button in LayerCommands.Children.OfType<Button>()) button.Height = compact ? 30 : 36;
        LayerPropertiesScroll.MaxHeight = Math.Max(0, Math.Min(280,
            LayersPane.ActualHeight - LayersHeader.ActualHeight - LayerAppearance.ActualHeight - LayerCommands.ActualHeight - 80));
    }

    private void OnPanelResizePressed(object sender, PointerRoutedEventArgs args)
    {
        if (!args.GetCurrentPoint(PanelResizeEdge).Properties.IsLeftButtonPressed) return;
        panelResizeStart = (args.GetCurrentPoint(RootGrid).Position.X, LayersColumn.Width.Value);
        PanelResizeEdge.CapturePointer(args.Pointer); args.Handled = true;
    }

    private void OnPanelResizeMoved(object sender, PointerRoutedEventArgs args)
    {
        if (panelResizeStart is not { } start) return;
        SetLayersPanelWidth(start.Width + start.X - args.GetCurrentPoint(RootGrid).Position.X);
        args.Handled = true;
    }

    private void OnPanelResizeReleased(object sender, PointerRoutedEventArgs args)
    {
        if (panelResizeStart is null) return;
        panelResizeStart = null; PanelResizeEdge.ReleasePointerCapture(args.Pointer); args.Handled = true;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(WorkspaceWidthPath)!);
            File.WriteAllText(WorkspaceWidthPath, LayersColumn.Width.Value.ToString(CultureInfo.InvariantCulture));
        }
        catch (IOException ex) { App.Log($"Workspace width save failed: {ex.Message}"); }
        catch (UnauthorizedAccessException ex) { App.Log($"Workspace width save failed: {ex.Message}"); }
    }
}
