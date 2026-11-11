using Compositor.Core;
using Compositor.Core.Psd;
using Compositor.Renderer;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Compositor.App;

/// <summary>One row of the layer panel, top of the stack first like the macOS panel.</summary>
public sealed class LayerRow
{
    public LayerRow(ProjectLayerRecord layer, int depth)
    {
        IsGroupView = layer.IsGroupOrFalse;
        Update(layer, depth);
    }

    internal void Update(ProjectLayerRecord layer, int depth)
    {
        Layer = layer;
        Visible = layer.IsVisible;
        Depth = depth;
        Display = new string(' ', depth * 2) + layer.Name;
    }

    public ProjectLayerRecord Layer { get; private set; } = null!;

    public bool Visible { get; private set; }

    public int Depth { get; private set; }

    public string Display { get; private set; } = "";
    internal bool IsGroupView { get; }
    internal CheckBox Eye { get; set; } = null!;
    internal TextBlock Label { get; set; } = null!;
    internal Button? Disclosure { get; set; }
    internal Image Thumbnail { get; set; } = null!;
    internal Image MaskThumbnail { get; set; } = null!;
    internal FontIcon KindIcon { get; set; } = null!;
    internal Button PixelTarget { get; set; } = null!;
    internal Button MaskTarget { get; set; } = null!;
    internal Button MaskLink { get; set; } = null!;
    internal Line MaskDisabledMark { get; set; } = null!;
    internal WeakReference<object>? ThumbnailSource { get; set; }
    internal WeakReference<object>? MaskThumbnailSource { get; set; }
    internal int ThumbnailRevision { get; set; } = -1;
    internal int MaskThumbnailRevision { get; set; } = -1;
    internal LayerTransform? ThumbnailPlacement { get; set; }
    internal LayerTransform? MaskThumbnailPlacement { get; set; }
    internal SizeD ThumbnailCanvas { get; set; }
    internal SizeD MaskThumbnailCanvas { get; set; }
    internal StackPanel EffectsHost { get; set; } = null!;
    internal System.Text.Json.JsonElement? EffectsJson { get; set; }
    internal LayerEffects? Effects { get; set; }
    internal Dictionary<LayerEffectKind, (Grid Root, Button Label, CheckBox Eye)> EffectRows { get; } = new();
}

public enum CanvasTool
{
    Move,
    RectSelect,
    EllipseSelect,
    Brush,
    Eraser,
    Crop,
    Shape,
    Wand,
    Clone,
    Blur,
    Text,
    Lasso,
    Gradient,
    Healing,
    Eyedropper,
    Hand,
    Zoom,
    ObjectSelect,
}

/// <summary>The crop frame's three gestures: drag out a new box, drag inside to move it,
/// drag a handle to resize — the macOS CropDrag.Mode.</summary>
public enum CropDragMode
{
    Create,
    Move,
    Resize,
}

public sealed partial class MainWindow : Window
{
    /// <summary>The projects this window keeps, in the original's sense of <c>ProjectWorkspace</c>: the tab
    /// on screen holds the document and the path the window draws, so switching tabs later means switching
    /// what the window reads instead of copying state around. Multi-document step 2a: the window hosts one
    /// tab today; the strip and the switch command are the next step.</summary>
    private readonly Compositor.Core.ProjectWorkspace workspace = new();

    /// <summary>The document on screen, owned by the selected tab. Reading and writing it through the
    /// registry is what keeps window and tab unable to disagree about what is open; the retirement checks
    /// elsewhere compare this reference, so a queued stroke or import still lands only on the document it
    /// started from.</summary>
    private EditorDocument? document
    {
        get => workspace.Current.Document;
        set => workspace.Current.Document = value;
    }

    private ProjectWatcher? watcher;

    /// <summary>The project folder of the tab on screen, like the original's <c>session.projectURL</c>.</summary>
    private string? projectPath
    {
        get => workspace.Current.ProjectPath;
        set => workspace.Current.ProjectPath = value;
    }
    private Guid? selectedLayerId;
    private bool suppressPanelEvents;
    private long lastSlowFrameNoticeMs; // Environment.TickCount64 of the last slow-frame notice
    private bool renamingLayer; // an in-place name editor is open in the layer list
    private bool forceClose; // set once the close confirmation has been answered
    private bool closeConfirmationPending;

    private CanvasTool tool = CanvasTool.Move;
    private PointD? dragStart; // document pixels, for the selection and crop drags
    private PointD? dragLast; // document pixels, for the move and brush drags
    private object? moveKey;
    private Rect shapePreviewBox;
    private PointD? shapePreviewFrom, shapePreviewTo; // fractions of the box while a line drags

    private RectI? cropRectangle;
    private Rect cropDragRect; // the live crop frame, document pixels
    private Windows.UI.Color foregroundColor = Microsoft.UI.Colors.Black;
    private Windows.UI.Color backgroundColor = Microsoft.UI.Colors.White;
    private bool palettePickerBackground; // which chip the open picker edits
    private readonly Dictionary<int, (double Size, double Hardness, double Opacity)> parkedBrushTips = new()
    {
        // Each tip family remembers its own brush shape, as on the Mac; the eraser starts soft.
        [1] = (40, 0, 100),
        [2] = (20, 75, 100),
    };
    private bool cropHasBox;   // a committed (draggable) frame exists
    private bool cropDragging;
    private CropDragMode cropDragMode;
    private PointD cropDragStart;
    private Rect cropDragOriginal;
    private TransformDragState? cropResizeDrag;
    private bool paintOnMask;
    private ShapeKind shapeKind = ShapeKind.Rectangle;
    private object? transformKey;
    private TransformDragState? transformDrag;
    private bool transformDragActive;

    // The presentation pipeline: one band-sized BGRA staging buffer, renders coalesced onto
    // the UI tick, and only the dirty rectangle recomposited and uploaded. Painting costs
    // its stroke, not the document. Every frame is full resolution: a placement drag (move
    // or transform) places from the compositor's position-free content cache, and the frame
    // that settles recomputes through the exact single-pass path.
    private byte[]? bandBuffer;
    private bool pendingFullRender;
    private int dirtyX0, dirtyY0, dirtyX1, dirtyY1; // render region accumulated between ticks
    private RectI? interactionDirty; // the region the current drag touches
    private CanvasCompositor? compositor;
    private bool placementDragActive; // a move/transform drag is live: renders place from the content cache
    private readonly HashSet<Guid> interactedLayers = new(); // dragged this gesture; caches dropped at settle
    private List<double>? snapXs, snapYs; // snap targets cached at drag start
    private readonly List<Line> guideLines = new();
    private readonly HashSet<Guid> collapsedGroups = new();
    private readonly HashSet<Guid> selectedLayerIds = new();
    private Guid? layerSelectionPointerTarget;
    private long layerSelectionRequest;
    private bool selectingLayer;
    private readonly ObservableCollection<ListViewItem> layerItems = new();
    private readonly Dictionary<Guid, ListViewItem> layerRows = new();
    private EditorDocument? layerRowsDocument;
    private Guid? draggingGuideId;
    // Pointer events arrive at polling rate (up to 1000 Hz); the interaction they describe is
    // applied once per render tick (60 Hz) from these pending fields.
    private PointD? pendingTransformPoint;
    private bool pendingShift, pendingOption;
    private PointD? pendingMoveDelta;
    private Guid? pendingMoveLayerId;
    private bool pendingMoveFree; // Ctrl held: the drag moves without snapping, as on the Mac

    public MainWindow()
        : this(null)
    {
    }

    public MainWindow(string? openOnLaunch)
    {
        var started = Stopwatch.StartNew();
        firstDrawAt = Stopwatch.StartNew();
        InitializeComponent();
        WandContiguousCheck.IsChecked = true; // defaults here: an inline IsChecked with x:Uid fails at parse
        App.Log($"startup: XAML {started.ElapsedMilliseconds} ms");
        Title = Loc.T("T_AppName");
        // Match the pinned dark chrome: a native light title bar over dark content reads as
        // a bug even when the text below is fine.
        if (AppWindow.TitleBar is { } titleBar)
        {
            var chrome = Microsoft.UI.ColorHelper.FromArgb(255, 30, 30, 30);
            var hover = Microsoft.UI.ColorHelper.FromArgb(255, 51, 51, 51);
            var ink = Microsoft.UI.Colors.White;
            titleBar.BackgroundColor = chrome;
            titleBar.ForegroundColor = ink;
            titleBar.InactiveBackgroundColor = chrome;
            titleBar.InactiveForegroundColor = ink;
            titleBar.ButtonBackgroundColor = chrome;
            titleBar.ButtonForegroundColor = ink;
            titleBar.ButtonHoverBackgroundColor = hover;
            titleBar.ButtonHoverForegroundColor = ink;
            titleBar.ButtonPressedBackgroundColor = chrome;
        }

        InitializeNavigation();
        workspace.SwitchGuard = () => ProjectEditsInFlight;   // the registry asks the window before it moves
        if (directOutlineEnabled) App.Log("Selection outline backend=direct (experimental)");
        InitializeTransformInspector();
        InitializeOpacityControls();
        CanvasHost.PointerCanceled += (_, _) => { if (transformPointerDraftActive) QueueTransformCancel(); };
        CanvasHost.PointerCaptureLost += (_, _) => { if (transformPointerDraftActive && !placementFinishing) QueueTransformCancel(); };

        InitializeBlendPicker();

        foreach (var tool in Enum.GetValues<CanvasTool>())
        {
            ToolCombo.Items.Add(ToolLabel(tool));
        }
        InitializeWorkspace();
        InitializeTimeline();
        ToolCombo.SelectedIndex = 0;
        ShapeKindCombo.SelectedIndex = 0;
        // C07b added these two fields with a plain TextBlock label beside them, which a screen reader and
        // the accessibility tree never connect (the Mac's fields carry real labels). Same naming call the
        // transform inspector already makes.
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ShapeCornerBox, Loc.T("A_ShapeCorner"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ShapeLineWidthBox, Loc.T("A_ShapeLineWidth"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(BlankWorkspaceButton, Loc.T("M_NewBlankLayer/Text"));
        BrushColorPicker.Color = Microsoft.UI.Colors.Black;
        StatusText.Text = Loc.T("L_Ready");
        // The recent list is file IO the window doesn't need to show: load it at low
        // priority instead of paying for it inside the constructor's first paint.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, LoadRecentProjects);
        App.Log($"startup: controls {started.ElapsedMilliseconds} ms");
        // The Mac's bracket shortcuts: [ / ] step the brush size (Alt steps hardness, matching the
        // handler below) while a brush or eraser is active.
        Content.KeyDown += OnContentKeyDown;
        Content.PreviewKeyDown += OnDeleteKeyPreview;
        AddCodeOnlyAccelerators();
        // What the menu carries now is the built-in; the user's own chords go on top of it, so the two
        // never mix and a removed override can be put back exactly as it was declared.
        CaptureBuiltinShortcuts();
        LoadAndApplyShortcuts();
        ApplyViewVisibilityOptions();
        // Dirty documents ask before closing throws the changes away, as on the Mac — and now every
        // project asks, the original's confirmQuit loop over the tab order.
        AppWindow.Closing += async (sender, args) =>
        {
            if (forceClose || !closeConfirmationPending && !AnyProjectNeedsCloseConfirmation()) return;
            args.Cancel = true;
            if (closeConfirmationPending) return;
            closeConfirmationPending = true;
            bool discard;
            try { discard = await ConfirmCloseAllProjectsAsync(); }
            catch (Exception ex)
            {
                // The request is already cancelled here. Letting the fault escape would swallow it in
                // UnhandledException and leave every later close cancelling the same way, so the user
                // could not leave the document at all.
                App.Log($"Close confirmation failed: {ex}", LogLevel.Error);
                SetStatus(Loc.F("S_CloseConfirmFail", ex.Message));
                return;
            }
            finally { closeConfirmationPending = false; }
            if (discard)
            {
                forceClose = true;
                Close();
            }
        };
        Closed += (_, _) =>
        {
            InvalidateObjectSelectionImage(); objectSegmenter?.Dispose();
            StopTimelinePlayback(); CancelTimelineDrag(); timelineExportCancellation?.Cancel();
            editorPipe?.Dispose();
            TrackEditorDocument(null);
            RetireAiEdit();
            windowClosed = true;
            EndOpacityEdit();
            RetireBlendPreview(redraw: false);
            layerPanelDrag = null;
            RetireDeletion();
            FinishVisibilitySwipe();
            CancelMaskCopyGesture();
            CancelProjectOpen();
            documentRetired.TrySetResult(); RetireFileImports(); RetireDither(); ClearTransformDraft();
            pixelNudgeEpoch++; CancelPixelMove();
            ClearSelectionMove(); StopSelectionAutoscroll();
            if (viewportFrameQueued) CompositionTarget.Rendering -= OnViewportFrame;
            CancelSelectionWork(); outlineCancellation?.Cancel();
            DisposeDirectOutlines();
            CancelColorRange(false);
            CancelLasso();
            frameRevision++;
            renderInputs.Clear();
#if DEBUG
            if (pointerStroke is { } closing) pointerStrokeClosedBy = $"window closed (tool={closing.Tool}, started={closing.Started})";
#endif
            pointerStroke = null;
            CancelLevelsHistogram(); levelsHistogramCache.Clear();
            // The canvas is detached here, so no later draw pass can reach these Direct2D objects.
            fullTarget?.Dispose(); fullTarget = null; presented = null;
            gpuPreviewRenderer?.Dispose(); gpuPreviewRenderer = null;
            checkerBrush?.Dispose(); checkerBrush = null;
            checkerTile?.Dispose(); checkerTile = null;
            // The window is gone, so this is the earliest point that may freeze the accepted log set.
            // A close the user cancelled never reaches here and the logger keeps working.
            AppLogging.RequestShutdown();
        };

        if (openOnLaunch is not null && ProjectStore.IsProjectDirectory(openOnLaunch))
        {
            _ = OpenProjectAsync(openOnLaunch);
        }
        else
        {
            OpenNewDocument();
        }
        StartEditorMcp();
    }

    private static int TipFamily(CanvasTool tool) => tool == CanvasTool.Eraser ? 1 : tool == CanvasTool.Healing ? 2 : 0;

    private static string ToolLabel(CanvasTool tool) => tool switch
    {
        CanvasTool.Move => Loc.T("Tool_Move"),
        CanvasTool.RectSelect => Loc.T("Tool_RectSelect"),
        CanvasTool.EllipseSelect => Loc.T("Tool_EllipseSelect"),
        CanvasTool.Brush => Loc.T("Tool_Brush"),
        CanvasTool.Eraser => Loc.T("Tool_Eraser"),
        CanvasTool.Crop => Loc.T("Tool_Crop"),
        CanvasTool.Shape => Loc.T("Tool_Shape"),
        CanvasTool.Wand => Loc.T("Tool_Wand"),
        CanvasTool.ObjectSelect => Loc.T("Tool_ObjectSelect"),
        CanvasTool.Clone => Loc.T("Tool_Clone"),
        CanvasTool.Blur => Loc.T("Tool_Blur"),
        CanvasTool.Text => Loc.T("Tool_Text"),
        CanvasTool.Lasso => Loc.T("Tool_Lasso"),
        CanvasTool.Gradient => Loc.T("Tool_Gradient"),
        CanvasTool.Healing => Loc.T("Tool_Healing"),
        CanvasTool.Eyedropper => Loc.T("Tool_Eyedropper"),
        CanvasTool.Hand => Loc.T("Tool_Hand"),
        CanvasTool.Zoom => Loc.T("Tool_Zoom"),
        _ => tool.ToString(),
    };

    private int toolChangeRevision;
    /// <summary>The revision of the newest switch whose own work has finished. `tool` moves the moment the user
    /// picks (#38), so the field cannot be the barrier for "the switch is done" — and the status line cannot be
    /// either, because a slow-frame report writes over it the instant a big canvas renders. This is the one
    /// signal that only goes up, and only when a switch has nothing left to do.</summary>
    private int toolChangeApplied;
    private bool toolInitialized;

    /// <summary>Gives the tool back when a switch is refused part-way. The field moves first on purpose:
    /// assigning the combo re-enters this handler, and with the field still holding the tool that was
    /// never adopted the re-entry runs a second teardown for it instead of returning early.</summary>
    private void RevertToolSelection(CanvasTool previous)
    {
        tool = previous;
        ToolCombo.SelectedIndex = (int)previous;
    }

    private async void OnToolChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ToolCombo.SelectedIndex < 0) return;
        if (IsFileImporting) { if (ToolCombo.SelectedIndex != (int)tool) ToolCombo.SelectedIndex = (int)tool; return; }
        CommitInlineText(); // the original settles the draft on a tool switch rather than dropping the words
        int request = ++toolChangeRevision;
        if (toolInitialized && ToolCombo.SelectedIndex == (int)tool) { toolChangeApplied = request; return; }
        var owner = document;
        // Whatever is open when the switch is dispatched is the gesture this switch retires. The wait
        // below can span a frame or two at full size, and a press that lands in that window belongs to
        // the tool the user had just picked — closing it would truncate a stroke nobody asked to end.
        var strokeAtEntry = pointerStroke;
        // The field moves before the wait, not after it. BeginPointerStroke stamps the stroke with this
        // field and every key handler branches on it, so a gesture opened inside the window the wait
        // leaves would otherwise be painted — and kept painted — by the tool the user just left.
        var previous = tool;
        tool = (CanvasTool)ToolCombo.SelectedIndex;
        await FinishObjectSelectionForToolChangeAsync();
        await FinishTransformBeforeOutputAsync();
        // A later switch has already moved the field to its own tool; putting this one's back would
        // undo a choice the user made after this handler stopped mattering.
        if (request != toolChangeRevision) return;
        // Retired because the document is gone or the window closed: give back the field *and* the picker. The Mac
        // has no second state to fall out of agreement — its tool header reads `session.tool`
        // (BrushControls.swift:7) — so a switch that never landed cannot leave the control showing a tool the
        // canvas is not in. Reverting only the field leaves exactly the shape #57's reds measured
        // (tool=Text, combo=Move, the transform strip collapsed); whether those runs came out through this branch
        // is still open — Windows/docs/f57-focus-geometry-2026-10-21.md §7.
        if (!ReferenceEquals(document, owner) || windowClosed) { RevertToolSelection(previous); toolChangeApplied = request; return; }
        EndSelectionMove(); StopSelectionAutoscroll();
        if (IsFileImporting) { RevertToolSelection(previous); toolChangeApplied = request; return; }
        bool gradientApplied = await ApplyGradientDraftAsync();
        if (request != toolChangeRevision) return;
        if (!gradientApplied || windowClosed || !ReferenceEquals(document, owner))
        { RevertToolSelection(previous); toolChangeApplied = request; return; }
        if (ReferenceEquals(pointerStroke, strokeAtEntry)) FinishPointerStroke();
        if (placementDragActive) FinishPlacementDrag();
        CancelHealingStroke();
        // Each tip family parks its brush shape when another one takes the sliders — the
        // macOS parkedBrushTips, families being brush (0), eraser (1), and healing (2).
        int fromFamily = TipFamily(previous), toFamily = TipFamily(tool);
        if (fromFamily != toFamily && parkedBrushTips.TryGetValue(toFamily, out var parkedTip))
        {
            parkedBrushTips[fromFamily] = (BrushSizeSlider.Value, BrushHardnessSlider.Value, BrushOpacitySlider.Value);
            BrushSizeSlider.Value = parkedTip.Size;
            BrushHardnessSlider.Value = parkedTip.Hardness;
            BrushOpacitySlider.Value = parkedTip.Opacity;
        }
        toolInitialized = true;
        BrushControls.Visibility = tool is CanvasTool.Brush or CanvasTool.Eraser or CanvasTool.Healing or CanvasTool.Clone or CanvasTool.Blur ? Visibility.Visible : Visibility.Collapsed;
        BrushSmoothingControls.Visibility = tool is CanvasTool.Brush or CanvasTool.Eraser ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(BrushSmoothingControls, Loc.T("H_BrushSmoothing"));
        PaintMaskCheck.Visibility = tool is CanvasTool.Clone or CanvasTool.Blur ? Visibility.Collapsed : Visibility.Visible;
        HealingControls.Visibility = tool == CanvasTool.Healing ? Visibility.Visible : Visibility.Collapsed;
        CropControls.Visibility = tool == CanvasTool.Crop ? Visibility.Visible : Visibility.Collapsed;
        TextControls.Visibility = tool == CanvasTool.Text ? Visibility.Visible : Visibility.Collapsed;
        ShapeControls.Visibility = tool == CanvasTool.Shape ? Visibility.Visible : Visibility.Collapsed;
        WandControls.Visibility = tool == CanvasTool.Wand ? Visibility.Visible : Visibility.Collapsed;
        CloneControls.Visibility = tool == CanvasTool.Clone ? Visibility.Visible : Visibility.Collapsed;
        BlurControls.Visibility = tool == CanvasTool.Blur ? Visibility.Visible : Visibility.Collapsed;
        LassoControls.Visibility = tool == CanvasTool.Lasso ? Visibility.Visible : Visibility.Collapsed;
        SelectionControls.Visibility = tool is CanvasTool.RectSelect or CanvasTool.EllipseSelect or CanvasTool.Lasso or CanvasTool.Wand or CanvasTool.ObjectSelect ? Visibility.Visible : Visibility.Collapsed;
        LassoAntialiasCheck.Visibility = tool == CanvasTool.ObjectSelect ? Visibility.Collapsed : Visibility.Visible;
        RefreshObjectSelectionControls();
        GradientControls.Visibility = tool == CanvasTool.Gradient ? Visibility.Visible : Visibility.Collapsed;
        NavigationControls.Visibility = tool == CanvasTool.Zoom ? Visibility.Visible : Visibility.Collapsed;
        EyedropperControls.Visibility = tool == CanvasTool.Eyedropper ? Visibility.Visible : Visibility.Collapsed;
        TransformControls.Visibility = tool == CanvasTool.Move ? Visibility.Visible : Visibility.Collapsed;
        CancelNavigation();
        GradientMaskCheck.IsChecked = paintOnMask;
        RefreshToolRail();
        RefreshTransformInspector(true);
        CancelDragPreview();
        RefreshQuickToolControls();
        RefreshAiActions();
        SetStatus(ToolHint(tool));
        if (tool == CanvasTool.ObjectSelect) BeginObjectSelectionWarmUp();
        toolChangeApplied = request;
    }

    /// <summary>The line each tool leaves on the status bar. It is the switch's own last step, which makes it
    /// the barrier a probe can wait on to know the switch ran to the end rather than only landed the field
    /// (see <see cref="WaitForToolSwitchLandedAsync"/>).</summary>
    private static string ToolHint(CanvasTool tool) => tool switch
    {
        CanvasTool.Move => Loc.T("S_ToolMoveHint"),
        CanvasTool.RectSelect => Loc.T("S_ToolRectSelectHint"),
        CanvasTool.EllipseSelect => Loc.T("S_ToolEllipseSelectHint"),
        CanvasTool.Brush => Loc.T("S_ToolBrushHint"),
        CanvasTool.Eraser => Loc.T("S_ToolEraserHint"),
        CanvasTool.Crop => Loc.T("S_ToolCropHint"),
        CanvasTool.Shape => Loc.T("S_ToolShapeHint"),
        CanvasTool.Wand => Loc.T("S_ToolWandHint"),
        CanvasTool.ObjectSelect => Loc.T("OS_Hint"),
        CanvasTool.Clone => Loc.T("S_ToolCloneHint"),
        CanvasTool.Blur => Loc.T("S_ToolBlurHint"),
        CanvasTool.Text => Loc.T("S_ToolTextHint"),
        CanvasTool.Lasso => Loc.T("S_ToolLassoHint"),
        CanvasTool.Gradient => Loc.T("S_ToolGradientHint"),
        CanvasTool.Healing => Loc.T("S_ToolHealingHint"),
        CanvasTool.Eyedropper => Loc.T("S_ToolEyedropperHint"),
        CanvasTool.Hand => Loc.T("S_ToolHandHint"),
        CanvasTool.Zoom => Loc.T("S_ToolZoomHint"),
        _ => "",
    };

    /// <summary>Waits until a tool switch has done everything it owes. The field answers as soon as the user
    /// picks (#38), so waiting on it proves only the pick landed; the panels, the rail and the retired gesture
    /// are still owed by the switch's own wait. A probe that waits on the field alone reads a tool that is live
    /// in a workspace that is still the previous one — and a probe that waits on the status hint races whatever
    /// else writes that line.</summary>
#if DEBUG
    internal Task<bool> WaitForToolSwitchLandedAsync(CanvasTool expected, int timeoutMs = 2000)
    {
        int request = toolChangeRevision;
        return WaitUntilAsync(() => tool == expected && toolChangeApplied >= request, timeoutMs);
    }
#endif

    // --- Document plumbing ---

    private void OpenDocument(EditorDocument newDocument, string? path = null)
    {
        InvalidateObjectSelectionImage();
        StopTimelinePlayback();
        RetireBlendPreview(redraw: false);
        EndOpacityEdit();
        layerPanelDrag = null;
        effectSelection = null;
        RetireDeletion();
        layerSelectionRequest++;
        layerSelectionPointerTarget = null;
        FinishVisibilitySwipe();
        CancelProjectOpen();
        RetireAiEdit();
        documentRetired.TrySetResult(); documentRetired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CloseInlineText(cancelling: true); // the draft belongs to the document that opened it
        RetireFileImports();
        RetireDither();
        foreach (var panel in floatingPanels.Values.Where(panel => panel.Finishing).ToArray()) AbandonFloatingPanel(panel.Key);
        // A panel belongs to the document that opened it. Answering it here would write the previous
        // document's session key into the new one, so it comes down without an answer.
        AbandonFloatingPanel("effects");
        AbandonFloatingPanel("selectionAmount");
        AbandonFloatingPanel(CameraRawPanelKey);
        pixelNudgeEpoch++; CancelPixelMove();
        ClearSelectionMove(); StopSelectionAutoscroll();
        CancelColorRange(false);
        CancelSelectionWork(); outlineCancellation?.Cancel();
        marqueeMode = LassoSelectionMode.Replace; marqueeConstrainArmed = true; selectionAmountPending = false;
        frameRevision++;
        renderSnapshot = renderSnapshotSource = null;
        renderInputs.Clear();
        ClearTransformDraft(); transformInspectorBasis = null;
#if DEBUG
        if (pointerStroke is { } abandoning) pointerStrokeClosedBy = $"OpenDocument (tool={abandoning.Tool}, started={abandoning.Started})";
#endif
        pointerStroke = null;
        lastBrushEndpoint = null;
        placementFinishing = placementDragActive = transformDragActive = false;
        pendingMoveDelta = pendingTransformPoint = null;
        pendingMoveLayerId = transformLayerId = null;
        transformDrag = null;
        transformKey = moveKey = null;
        dragLast = null;
        interactedLayers.Clear();
        CancelLevelsHistogram();
        levelsHistogramCache.Clear();
        CancelHealingStroke();
        ClearGradientDraft();
        CancelLasso();
        watcher?.Dispose();
        watcher = null;
        if (document is not null)
        {
            document.Changed -= OnDocumentChanged;
            document.PixelsChanged -= OnDocumentPixelsChanged;
            document.SelectionChanged -= OnSelectionChanged;
            document.LayerLocksChanged -= OnLayerProtectionChanged;
            document.HistoryChanged -= QueueHistoryPanelRefresh;
        }
        document = newDocument;
        maskAloneLayerId = null; CancelMaskCopyGesture();
        paintOnMask = false; PaintMaskCheck.IsChecked = false;
        CancelNavigation();
        viewport = new(newDocument.Snapshot.Manifest.Width, newDocument.Snapshot.Manifest.Height);
        ResizeViewport();
        projectPath = path;
        selectedLayerId = document.Snapshot.Manifest.ActiveLayerId ?? document.Snapshot.Manifest.Layers.LastOrDefault()?.Id;
        selectedLayerIds.Clear();
        if (selectedLayerId is { } initialLayer) selectedLayerIds.Add(initialLayer);
        document.Changed += OnDocumentChanged;
        document.PixelsChanged += OnDocumentPixelsChanged;
        LoadLayerProtection(document);
        document.SelectionChanged += OnSelectionChanged;
        document.LayerLocksChanged += OnLayerProtectionChanged;
        document.HistoryChanged += QueueHistoryPanelRefresh;
        ResetHistoryPanel();
        QueueHistoryPanelRefresh();
        bandBuffer = null;
        compositor = null;
        // Release the previous document's render target here rather than whenever the finalizer
        // runs: it is the largest single GPU allocation this window holds, and the draw pass reads
        // `presented`, so both references to the same object go together.
        fullTarget?.Dispose();
        fullTarget = null;
        gpuPreviewRenderer?.Dispose(); gpuPreviewRenderer = null; gpuPreviewUnavailable = gpuFramePresented = false;
        presented = null;
        pendingFullRender = true;
        if (path is not null)
        {
            watcher = new ProjectWatcher(path);
            watcher.ProjectChanged += OnExternalChange;
        }
        UpdateTitle();
        UpdateDocSizeLabel();
        ResetTimelineForDocument();
        RefreshAll();
    }

    private async void OnExternalChange()
    {
        if (document is null) return;
        var owner = document; var retired = documentRetired.Task;
        await WaitForFileImportsAsync();
        if (retired.IsCompleted || !ReferenceEquals(document, owner)) return;
        if (ReferenceEquals(projectSaveOwner, owner)) { ScheduleExternalConflictRecheck(); return; }
        var reverting = false;
        if (document.IsDirty)
        {
            // The macOS watcher asks before it replaces unsaved work
            // (ProjectController+ExternalChanges.swift:52-55), and it only asks once nothing is
            // mid-flight: pulling the document out from under a stroke or a transform commit is worse
            // than the delay, so a busy editor defers with a backoff instead of dropping the change.
            // A filter panel left open is a session holding the layer's pixels mid-edit — the fact the modal
            // dialog's depth used to stand in for (#37 makes the panel non-modal, so depth no longer says it).
            if (committingTransform || pointerStroke is not null || projectDialogDepth > 0 || ditherPreview is not null
                || IsFileImporting)
            {
                SetStatus(Loc.T("S_ExternalWait"));
                ScheduleExternalConflictRecheck();
                return;
            }

            externalConflictAskAttempt = 0;
            var answer = await AskExternalConflictAsync();
            if (retired.IsCompleted || !ReferenceEquals(document, owner)) return;
            switch (answer)
            {
                case ContentDialogResult.Primary:
                    reverting = true; // Revert: the package on disk is taken as it stands.
                    break;
                case ContentDialogResult.Secondary:
                    // "Keep Mine": the outside write becomes the baseline this document may save over,
                    // so the same change is not asked about again while the work stays unsaved.
                    document.AdoptDiskState(projectPath);
                    watcher?.AcknowledgeExternalChange();
                    SetStatus(Loc.T("S_ExternalKept"));
                    return;
                default:
                    // Closed without answering: nothing was taken from disk and nothing was written,
                    // so the change stays pending and the next save is still refused.
                    SetStatus(Loc.T("S_DiskChanged"));
                    return;
            }
        }
        try
        {
            var snapshot = await ProjectStore.LoadAsync(projectPath!);
            // Answering Revert is the consent to give up the unsaved work, so a dirty document no longer
            // blocks the load; the same check still guards the clean path, where an edit made while the
            // package was loading must not be thrown away without a word.
            if (retired.IsCompleted || !ReferenceEquals(document, owner) || IsFileImporting || (owner.IsDirty && !reverting)) return;
            document.Changed -= OnDocumentChanged;
            var path = projectPath;
            OpenDocument(new EditorDocument(snapshot), path);
            // A clean reload is by definition what the package holds, so saves after it may skip
            // the assets nobody touched.
            document?.MarkSaved(path);
            // The document now holds exactly what the package holds, so this change is answered.
            watcher?.AcknowledgeExternalChange();
            SetStatus(Loc.T("S_Reloaded"));
        }
        catch (ProjectFormatException ex)
        {
            SetStatus(Loc.F("S_ExternalFail", ex.Message));
        }
        catch (IOException ex)
        {
            // A package caught mid-write, or an asset another process holds, is a read failure the
            // user has to see: this handler is async void, so an escaping IOException would reach
            // the global handler and leave the window looking as if nothing happened.
            SetStatus(Loc.F("S_ExternalFail", ex.Message));
            // The watcher's own recovery is counter-only, so it goes to the log here: without this
            // line a burst that gave up (or a buffer that failed to re-arm) leaves no trace.
            App.Log($"watcher state after a failed reload: {watcher?.Status() ?? "no watcher"}");
        }
        catch (UnauthorizedAccessException ex)
        {
            SetStatus(Loc.F("S_ExternalFail", ex.Message));
            App.Log($"watcher state after a denied reload: {watcher?.Status() ?? "no watcher"}");
        }
    }

    // Retries a conflict prompt the editor was too busy to show. The wait doubles per attempt and
    // stops at the ceiling the macOS scheduleRecheck uses, so a long operation is never polled; the
    // change itself stays pending in the watcher, so a later disk event can answer it too.
    private int externalConflictAskAttempt;

    // Re-presents a change the app has not answered yet. Kept as its own call so the activation hook and a
    // probe drive the same code; it does nothing when nothing is pending, so a focused click is harmless.
    internal bool ResumeDeferredExternalChange()
        => document is not null && watcher?.RecheckPendingChange() == true;

    private void ScheduleExternalConflictRecheck()
    {
        if (windowClosed || externalConflictAskAttempt >= 7) return;
        var delay = 250 * (1 << externalConflictAskAttempt++);
        _ = Task.Run(async () =>
        {
            await Task.Delay(delay);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!windowClosed && watcher?.HasPendingChange == true) watcher.RecheckPendingChange();
            });
        });
    }

    private void OnDocumentChanged()
    {
        InvalidateObjectSelectionImage();
        if (!applyingTimeline) StopTimelinePlayback();
        RefreshTimeline();
        if (!applyingOpacity) EndOpacityEdit();
        if (applyingBlendMode || applyingLayerOpacityKeys)
        {
            OnDocumentPixelsChanged(); RefreshLayerPanel(); UpdateTitle(); return;
        }
        renderSnapshot = null; RetireBlendPreview();
        if (applyingVisibilitySwipe) { OnDocumentPixelsChanged(); UpdateTitle(); return; }
        FinishVisibilitySwipe(closeHistory: false);
        if (!committingTransform) CancelPixelMove();
        if (selectionMove is not null) ClearSelectionMove();
        // SetActiveLayer changes editor metadata only. OnLayerSelected refreshes the
        // properties/overlays once; rebuilding the compositor here redraws identical pixels.
        if (selectingLayer || creatingRulerGuide) { UpdateTitle(); return; }
        if (committingTransform) { renderSnapshot = null; frameRevision++; RefreshLayerPanel(); UpdateTitle(); return; }
        CancelColorRange();
        ClearTransformDraft();
        if (restoringHistory) SyncHistorySelection();
        if (rulerGuideOwner is not null) CancelNavigation();
        CancelSelectionWork();
        renderSnapshot = null;
        frameRevision++;
        CancelHealingStroke();
        CancelGradientDraft();
        if (startingRasterStroke && renderInputs.IsApplying)
        {
            // The working copy opens history and may retire text, but preserves geometry.
            // Keep the warm compositor; the serial worker installs its new frozen snapshot
            // and proves the copied source's first-dab halo instead of rebuilding the canvas.
            RefreshLayerPanel();
            UpdateTitle();
            return;
        }
        // Structure changed (undo, import, crop, mask created): the compositor holds the old
        // snapshot dictionaries, so it must rebuild before the next render.
        if (placementDragActive)
        {
            // A placement drag's first tick merely opens its undo step (EndChange → Changed).
            // The transform edits the manifest in place, so it needs neither the panel nor a
            // full-canvas render — the drag's own band pipeline covers the pixels, and the
            // compositor's fingerprints invalidate the moved layer on their own.
            UpdateTitle();
            return;
        }
        compositor = null;
        RefreshAll();
    }

    private void UpdateTitle()
    {
        string name = projectPath is null ? Loc.T("M_Untitled") : System.IO.Path.GetFileName(projectPath.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        string title = Loc.F("S_TitleFormat", name, document?.IsDirty == true ? Loc.T("L_EditedSuffix") : "");
        if (Title != title) Title = title;
        RefreshProjectTabStrip();
    }

    private void RefreshAll()
    {
        RefreshTimeline();
        RefreshAiActions();
        UpdateDocSizeLabel();
        RenderCanvas();
        RefreshLayerPanel();
        UpdateTitle();
    }

    private void RenderCanvas()
    {
        // Full render, straight away: structure changed (open, undo, import, crop).
        if (document is null) return;
        frameRevision++;
        try
        {
            pendingFullRender = true;
            dirtyX0 = dirtyY0 = 0;
            dirtyX1 = document.Snapshot.Manifest.Width;
            dirtyY1 = document.Snapshot.Manifest.Height;
            PumpRender();
        }
        catch (Exception ex)
        {
            // A render pass must never let an exception escape into its UI event handler.
            App.Log($"RenderCanvas failed: {ex}");
        }
    }

    private void OnDocumentPixelsChanged()
    {
        InvalidateObjectSelectionImage();
        if (document?.ActiveTextDraft is not null) RefreshQuickTextControls();
        RetireBlendPreview(redraw: false);
        if (!applyingVisibilitySwipe) FinishVisibilitySwipe(closeHistory: false);
        if (!renderInputs.IsApplying) renderSnapshot = null;
        frameRevision++;
        CancelHealingStroke();
        QueueLevelsHistogramRefresh();
        // Pixel-only change: accumulate the touched region and coalesce renders onto the
        // UI tick, so a burst of pointer moves costs one repaint. While a placement drag is
        // live the tick places from the content cache; the settle pass is exact.
        if (document is null) return;
        var dirty = interactionDirty;
        interactionDirty = null; // consumed; the next event starts a fresh region
        if (dirty is { } rect)
        {
            dirtyX0 = Math.Min(dirtyX0, rect.X);
            dirtyY0 = Math.Min(dirtyY0, rect.Y);
            dirtyX1 = Math.Max(dirtyX1, rect.X + rect.Width);
            dirtyY1 = Math.Max(dirtyY1, rect.Y + rect.Height);
        }
        else
        {
            pendingFullRender = true;
            dirtyX0 = dirtyY0 = 0;
            dirtyX1 = document.Snapshot.Manifest.Width;
            dirtyY1 = document.Snapshot.Manifest.Height;
        }
        PumpRender();
    }

    /// <summary>
    /// Kicks exactly one render loop. Frames run back to back while anything is dirty; an
    /// earlier bug let timer-triggered renders overlap, where they cleared each other's
    /// dirty state and cancelled each other's bitmap uploads — the picture froze mid-drag
    /// and only jumped to the end when the pointer was released.
    /// </summary>
    /// <summary>
    /// Kicks one background render pass. Overlapping passes corrupted the dirty state and
    /// cancelled uploads, so passes are strictly serialized: while the worker runs, new
    /// pointer interactions only accumulate in the pending fields, and the worker's
    /// completion re-enqueues a pass that applies the latest.
    /// </summary>
    private void PumpRender()
    {
        if (workerBusy || windowClosed) return;
        RenderPendingAsync();
    }

    private bool HasPendingDirty => document is not null && (pendingFullRender || dirtyX0 < dirtyX1);

    /// <summary>
    /// Applies the pointer interactions accumulated since the last tick. Events arrive at
    /// polling rate; the document mutation and dirty tracking run at frame rate.
    /// </summary>
    private void ApplyPendingInteraction()
    {
        if (document is null) return;
        // Detached drafts supply their own immutable placement snapshot. Only legacy
        // moves mutate the live records and need to freeze the source metadata again.
        if (pendingTransformPoint is not null && !transformPointerDraftActive || pendingMoveDelta is not null) renderSnapshot = null;
        ApplyPendingHealing();
        if (transformPointerDraftActive && pendingTransformPoint is { } draftPoint)
        {
            ApplyPendingTransformDraft(draftPoint);
            pendingTransformPoint = null;
        }
        if (pendingTransformPoint is { } transformPoint && transformDrag is { } drag && transformLayerId is { } dragId
            && document.Snapshot.Manifest.Layers.FirstOrDefault(layer => layer.Id == dragId) is { } dragLayer && transformKey is { } key)
        {
            interactedLayers.Add(dragLayer.Id);
            var draft = TransformDrag.Updated(drag, transformPoint, lockRatio: false, pendingShift, pendingOption);
            // Dragging lands on whole document pixels, as the macOS canvas rounds its drag
            // drafts — typed inspector values stay exact. Whole-pixel placements take the
            // compositor's row-copy path instead of a per-pixel resample.
            draft = new LayerTransform
            {
                Origin = new PointD(Math.Round(draft.Origin.X), Math.Round(draft.Origin.Y)),
                Size = new SizeD(Math.Round(draft.Size.Width), Math.Round(draft.Size.Height)),
                Rotation = draft.Rotation,
                FlipX = draft.FlipX,
                FlipY = draft.FlipY,
                Sampling = draft.Sampling,
            };
            MarkInteractionDirty(BoundsOf(dragLayer), grow: 2); // the old placement leaves the band
            document.UpdateTransform(key, dragLayer.Id, draft);
            MarkInteractionDirty(BoundsOf(dragLayer), grow: 2); // the new one enters it
            UpdateTransformHandles();
            if (drag.Mode == TransformDragMode.Rotate)
            {
                StatusText.Text = Loc.F("S_Rotation", draft.Rotation, draft.Size.Width, draft.Size.Height);
            }
            else
            {
                StatusText.Text = Loc.F("S_TransformPos", draft.Size.Width, draft.Size.Height, draft.Origin.X, draft.Origin.Y);
            }
        }
        if (pendingMoveDelta is { } delta && pendingMoveLayerId is { } moveId)
        {
            var layer = document.Snapshot.Manifest.Layers.FirstOrDefault(candidate => candidate.Id == moveId);
            if (layer is not null)
            {
                interactedLayers.Add(moveId);
                // Whole-pixel move steps, with the subpixel debt carried forward so a slow
                // drag still adds up (the macOS canvas rounds its drag drafts the same way).
                var target = new PointD(moveRemainder.X + delta.X, moveRemainder.Y + delta.Y);
                var step = new PointD(Math.Round(target.X), Math.Round(target.Y));
                moveRemainder = new PointD(target.X - step.X, target.Y - step.Y);
                if (step.X != 0 || step.Y != 0)
                {
                    // The snap judges the RAW track — where the drag would be without any
                    // snapping. Judging from the layer's snapped position re-snapped from
                    // distance ~0 every tick, pinning the layer onto the target: it could
                    // never be dragged past (and dense targets — every layer's edges and
                    // centers — chained into a wall).
                    moveRawBox = new RectD(moveRawBox.X + step.X, moveRawBox.Y + step.Y, moveRawBox.Width, moveRawBox.Height);
                    double applyX = step.X, applyY = step.Y;
                    double? lineX = null, lineY = null;
                    if (pendingMoveFree)
                    {
                        snapLockX = snapLockY = null;
                    }
                    else
                    {
                        double reach = 10 / viewport.PointsPerPixel;
                        var (moveX, targetX) = SnapAxis([moveRawBox.MinX, moveRawBox.MidX, moveRawBox.MaxX], snapXs ?? [], reach, ref snapLockX);
                        var (moveY, targetY) = SnapAxis([moveRawBox.MinY, moveRawBox.MidY, moveRawBox.MaxY], snapYs ?? [], reach, ref snapLockY);
                        var current = BoundsOf(layer);
                        applyX = (moveRawBox.X - current.X) + moveX;
                        applyY = (moveRawBox.Y - current.Y) + moveY;
                        lineX = targetX;
                        lineY = targetY;
                    }
                    ShowSnapLines(moveRawBox, lineX, lineY, document.Snapshot.Manifest.Width, document.Snapshot.Manifest.Height);
                    if (applyX != 0 || applyY != 0)
                    {
                        MarkInteractionDirty(BoundsOf(layer), grow: Math.Max(Math.Abs(applyX), Math.Abs(applyY)) + 2);
                        document.MoveLayerBy(moveId, applyX, applyY, moveKey ??= new object());
                        MarkInteractionDirty(BoundsOf(layer), grow: 2);
                    }
                    UpdateTransformHandles(); // the box tracks the layer live, not on the next refresh
                }
            }
        }
        pendingTransformPoint = null;
        pendingMoveDelta = null;
        pendingMoveLayerId = null;
        pendingMoveFree = false;
    }

    private PointD moveRemainder; // subpixel debt while a drag rounds its steps to whole pixels
    private RectD moveRawBox; // where the drag would be WITHOUT snapping — the snap judge
    private double? snapLockX, snapLockY; // the alignment target currently pinned, per axis

    /// <summary>
    /// One axis of move snapping with a pinned target: while locked, only that target is
    /// considered and the layer stays glued until the raw box drags past it by 1.6× the
    /// reach — then the pin breaks and the layer catches up to the cursor in one step. An
    /// unpinned pass over all targets re-locks to the nearest within reach.
    /// </summary>
    private static (double Move, double? Target) SnapAxis(double[] guides, List<double> targets, double reach, ref double? lockTarget)
    {
        if (lockTarget is { } pinned)
        {
            double nearest = double.MaxValue, nearestMove = 0;
            foreach (double guide in guides)
            {
                double move = pinned - guide;
                if (Math.Abs(move) < nearest)
                {
                    nearest = Math.Abs(move);
                    nearestMove = move;
                }
            }
            if (nearest <= reach * 1.6)
            {
                return (nearestMove, pinned);
            }
            lockTarget = null; // dragged past: the pin breaks
        }
        (double Move, double? Target)? hit = null;
        foreach (double guide in guides)
        {
            foreach (double target in targets)
            {
                double move = target - guide;
                if (Math.Abs(move) > reach) continue;
                if (hit is { } current && Math.Abs(current.Move) <= Math.Abs(move)) continue;
                hit = (move, target);
            }
        }
        lockTarget = hit?.Target;
        return hit ?? (0, null);
    }

    private void ShowSnapLines(RectD rect, double? snapX, double? snapY, double canvasWidth, double canvasHeight)
    {
        if (snapX is { } lineX)
        {
            SnapLineX.Visibility = Visibility.Visible;
            SnapLineX.X1 = SnapLineX.X2 = lineX;
            SnapLineX.Y1 = 0;
            SnapLineX.Y2 = canvasHeight;
        }
        else
        {
            SnapLineX.Visibility = Visibility.Collapsed;
        }
        if (snapY is { } lineY)
        {
            SnapLineY.Visibility = Visibility.Visible;
            SnapLineY.Y1 = SnapLineY.Y2 = lineY;
            SnapLineY.X1 = 0;
            SnapLineY.X2 = canvasWidth;
        }
        else
        {
            SnapLineY.Visibility = Visibility.Collapsed;
        }
    }

    private void RenderPendingAsync()
    {
        if (document is not { } owner || workerBusy || windowClosed) return;
        try
        {
#if DEBUG
            var inputWatch = System.Diagnostics.Stopwatch.StartNew();
            var frameTrace = frameTimelineProbe;
            frameTrace?.Invoke("input-begin");
            long inputAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
#endif
            // The queue owns the frame before invoking callbacks: PixelsChanged/Changed
            // may pump recursively, but cannot start another reader or drain input twice.
            if (!renderInputs.TryBeginFrame(owner)) return;
            ApplyPendingInteraction();
#if DEBUG
            frameTrace?.Invoke("input-applied");
#endif
            if (!ReferenceEquals(document, owner))
            {
                renderInputs.EndFrame();
                PumpRender();
                return;
            }
            if (interactionDirty is { } tail)
            {
                interactionDirty = null;
                dirtyX0 = Math.Min(dirtyX0, tail.X);
                dirtyY0 = Math.Min(dirtyY0, tail.Y);
                dirtyX1 = Math.Max(dirtyX1, tail.X + tail.Width);
                dirtyY1 = Math.Max(dirtyY1, tail.Y + tail.Height);
            }
            if (!pendingFullRender && dirtyX0 >= dirtyX1)
            {
                renderInputs.EndFrame();
                return;
            }

            var manifest = owner.Snapshot.Manifest;
            int width = manifest.Width, height = manifest.Height;
            if (ToolOverlay.Width != width || ToolOverlay.Height != height)
            {
                ToolOverlay.Width = width;
                ToolOverlay.Height = height;
                CanvasHost.Width = width; CanvasHost.Height = height;
                viewport.SetDocument(width, height); QueueViewportFrame();
                pendingFullRender = true;
                UpdateTransformHandles();
            }
            var band = pendingFullRender
                ? new RectI(0, 0, width, height)
                : RectI.Normalize(dirtyX0, dirtyY0, dirtyX1, dirtyY1, width, height);
            pendingFullRender = false;
            dirtyX0 = dirtyY0 = int.MaxValue;
            dirtyX1 = dirtyY1 = int.MinValue;
            if (band.Width <= 0 || band.Height <= 0)
            {
                renderInputs.EndFrame();
                return;
            }

            var frameSource = healingStroke?.PreviewSnapshot ?? CaptureRenderSnapshot(owner.Snapshot);
            int? animationFrame = TimelinePreviewActive ? timelineFrame : null;
            var blendRequest = blendPreview;
            compositor ??= new CanvasCompositor(frameSource);
            var active = compositor;
            var gradientRequest = CurrentGradientRequest;
            // A redraw can reuse immutable draft pixels when endpoints and settings still match.
            var cachedGradientPreview = gradientRenderedPreview;
            bool reuseGradientPreview = SameGradientInput(gradientRequest, gradientRenderedInput);
            var transformRequest = CurrentTransformRequest;
            var floatingRequest = CurrentFloatingTransformRequest;
            var pixelRequest = CurrentPixelMoveRequest;
            var maskAloneId = CurrentMaskAloneId;
            var staging = bandBuffer;
            long revision = frameRevision;
            bool interactive = placementDragActive && transformRequest is null && floatingRequest is null;
            bool cacheAdjustments = transformRequest is not { Finishing: false };
            bool allowGpu = useGpuRendering && !gpuPreviewUnavailable && maskAloneId is null
                && Environment.GetEnvironmentVariable("COMPOSITOR_DISABLE_GPU_PREVIEW") != "1";
            bool cpuCanvasStale = gpuFramePresented;
            var residentNoise = gpuPreviewRenderer?.NoiseSeeds ?? [];
            long noiseBudget = gpuPreviewRenderer?.TextureBudget ?? (384L << 20);
#if DEBUG
            var beforeRender = beforeFrameRender;
            inputWatch.Stop();
            double inputMs = inputWatch.Elapsed.TotalMilliseconds;
            long inputBytes = GC.GetAllocatedBytesForCurrentThread() - inputAllocatedBefore;
            frameTrace?.Invoke("worker-queued");
#endif
            renderInputs.StartRender();
            // Keep the UI owner's queue before the worker starts. A canvas control can be
            // unloaded while rendering; its DispatcherQueue is then no longer available.
            var frameDispatcher = DispatcherQueue;
            var renderWork = Task.Run(() =>
            {
                try
                {
#if DEBUG
                    beforeRender?.Invoke();
                    long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
#endif
                    var watch = System.Diagnostics.Stopwatch.StartNew();
#if DEBUG
                    frameTrace?.Invoke("worker-start");
#endif
                    var gradientPreview = gradientRequest is { } request
                        ? reuseGradientPreview ? cachedGradientPreview : request.Buffers.Preview(request.Start, request.End, request.Settings, cachedGradientPreview) : null;
                    var transformPreview = transformRequest is { } draftRequest ? draftRequest.Finishing
                        ? draftRequest.Edit.PrepareDistortCommit(draftRequest.Value, draftRequest.Corners!, draftRequest.Token)
                        : draftRequest.Edit.Preview(draftRequest.Value, draftRequest.Corners) : null;
                    var floatingPreview = floatingRequest is { Finishing: false } floatDraft
                        ? floatDraft.Session.Edit.Preview(floatDraft.Value, floatDraft.Corners, floatDraft.Token) : null;
                    var floatingResult = floatingRequest is { Finishing: true } floatFinish
                        ? floatFinish.Session.Edit.PrepareCommit(floatFinish.Value, floatFinish.Corners, floatFinish.Token) : null;
                    var floatingSnapshot = floatingResult?.Snapshot ?? floatingPreview?.Snapshot;
                    if (floatingRequest is not null)
                    {
                        floatingSnapshot ??= frameSource;
                        band = RectI.Union(band, floatingPreview?.DirtyBounds
                            ?? CanvasCompositor.ChangedLayersBounds(active.Snapshot, floatingSnapshot, floatingRequest.Session.Edit.DirtyLayerIds));
                    }
                    var pixelPreview = pixelRequest is { Finishing: false } moving ? moving.Session.Edit.Preview(moving.Offset) : null;
                    var pixelResult = pixelRequest is { Finishing: true } finishing ? finishing.Session.Edit.PrepareCommit(finishing.Offset) : null;
                    var pixelSnapshot = pixelResult?.Snapshot ?? pixelPreview?.Snapshot;
                    if (pixelPreview is not null) band = RectI.Union(band, pixelPreview.DirtyBounds);
                    if (pixelSnapshot is not null && pixelRequest is not null)
                    {
                        var id = pixelRequest.Session.Edit.DirtyLayerIds[0];
                        var previousPlacement = active.Snapshot.Manifest.Layers.First(layer => layer.Id == id).Transform;
                        var nextPlacement = pixelSnapshot.Manifest.Layers.First(layer => layer.Id == id).Transform;
                        if (!previousPlacement.Equals(nextPlacement))
                            band = RectI.Union(band, CanvasCompositor.ChangedLayersBounds(active.Snapshot, pixelSnapshot, pixelRequest.Session.Edit.DirtyLayerIds));
                    }
                    if (transformPreview is not null)
                        band = RectI.Union(band, CanvasCompositor.ChangedLayersBounds(active.Snapshot, transformPreview.Snapshot, transformRequest!.Edit.DirtyLayerIds));
                    active.SetSnapshot(floatingSnapshot ?? pixelSnapshot ?? transformPreview?.Snapshot ?? gradientPreview?.Snapshot ?? blendRequest?.Snapshot.Value
                        ?? (animationFrame is { } frame ? AnimationEvaluator.Evaluate(frameSource, frame) : frameSource));
#if DEBUG
                    double preparationMs = watch.Elapsed.TotalMilliseconds;
                    frameTrace?.Invoke("preview-prepared");
#endif
                    // The same band must reach both the compositor and the texture upload.
                    // Source-grid changes can spread through effects and live mask consumers.
                    var gpuScene = allowGpu ? GpuPreviewScene.TryCreate(active.Snapshot) : null;
                    Dictionary<uint, float[]>? noise = null;
                    if (gpuScene is not null)
                    {
                        try { noise = gpuScene.PrepareGaussianNoise(residentNoise.ToHashSet(), noiseBudget); }
                        catch (NotSupportedException) { gpuScene = null; }
                    }
                    Surface? surface = null;
                    if (maskAloneId is { } maskId) surface = active.RenderMaskAlone(maskId, ref band);
                    else if (gpuScene is null)
                    {
                        // GPU drafts did not update the CPU canvas. Settle/fallback must
                        // restore the whole reference frame before incremental bands resume.
                        if (cpuCanvasStale) band = new(0, 0, width, height);
                        band = active.ExpandPixelDirty(band);
                        surface = active.Render(band.X, band.Y, band.X + band.Width, band.Y + band.Height, interactive, cacheAdjustments);
                    }
#if DEBUG
                    double compositionMs = watch.Elapsed.TotalMilliseconds - preparationMs;
                    frameTrace?.Invoke("composition-finished");
#endif
                    if (gpuScene is null)
                    {
                        long needed = (long)band.Width * band.Height * 4;
                        if (staging is null || staging.LongLength < needed) staging = new byte[needed];
                        SurfacePresentation.FillBgra(surface!, staging, band.Width, band.X, band.Y, band.X + band.Width, band.Y + band.Height);
                    }
                    watch.Stop();
#if DEBUG
                    long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                    double bgraMs = watch.Elapsed.TotalMilliseconds - preparationMs - compositionMs;
                    frameTrace?.Invoke("publication-queued");
#endif
                    frameDispatcher.TryEnqueue(() =>
                    {
#if DEBUG
                        frameTrace?.Invoke("publication-entered");
#endif
                        try
                        {
                            // A new document, undo, draft or control edit can supersede this
                            // reader. Upload only a current frame, on the UI owner thread.
                            if (windowClosed) return;
                            if (!ReferenceEquals(document, owner) || revision != frameRevision || !ReferenceEquals(compositor, active))
                            {
                                pendingFullRender = true;
                                return;
                            }
                            var uploadWatch = System.Diagnostics.Stopwatch.StartNew();
                            if (gpuScene is not null)
                            {
                                try
                                {
#if DEBUG
                                    beforeGpuPreviewRender?.Invoke();
#endif
                                    if (gpuPreviewRenderer?.MatchesSize(width, height) == false)
                                    { gpuPreviewRenderer.Dispose(); gpuPreviewRenderer = null; }
                                    gpuPreviewRenderer ??= new(CanvasDevice.GetSharedDevice(), width, height);
                                    presented = gpuPreviewRenderer.Render(gpuScene, noise);
#if DEBUG
                                    if (Environment.GetEnvironmentVariable("COMPOSITOR_REQUIRE_GPU_PREVIEW") == "1")
                                        _ = presented.GetPixelBytes(0, 0, 1, 1); // Fence GPU completion without downloading the whole frame.
#endif
                                }
                                catch (Exception ex)
                                {
                                    gpuPreviewUnavailable = true;
                                    gpuPreviewRenderer?.Dispose(); gpuPreviewRenderer = null;
                                    presented = fullTarget;
                                    App.Log($"GPU preview unavailable; using CPU: {ex}", LogLevel.Diagnostic);
                                    pendingFullRender = true;
                                    return;
                                }
                            }
                            else
                            {
                                if (fullTarget is null || fullTarget.SizeInPixels.Width != width || fullTarget.SizeInPixels.Height != height)
                                {
                                    fullTarget?.Dispose();
                                    fullTarget = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 96,
                                        Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized,
                                        Microsoft.Graphics.Canvas.CanvasAlphaMode.Premultiplied);
                                }
                                fullTarget.SetPixelBytes(staging!, band.X, band.Y, band.Width, band.Height);
                                bandBuffer = staging;
                                presented = fullTarget;
                            }
                            uploadWatch.Stop();
                            gpuFramePresented = gpuScene is not null;
#if DEBUG
                            lastFrameUsedGpu = gpuScene is not null;
                            lastFrameProbe = new(band, inputMs, watch.Elapsed.TotalMilliseconds, uploadWatch.Elapsed.TotalMilliseconds, inputBytes, allocated,
                                preparationMs, compositionMs, bgraMs);
                            // Input → the frame that shows it. Cleared here so a frame that had no accepted
                            // point of its own can't report the previous one's wait.
                            frameWaitMs = pointerPointStamp == 0 ? 0
                                : (System.Diagnostics.Stopwatch.GetTimestamp() - pointerPointStamp) * 1000d / System.Diagnostics.Stopwatch.Frequency;
                            pointerPointStamp = 0;
                            presentedFrameCount++;
                            frameTrace?.Invoke("pixels-uploaded");
#endif
                            if (gradientRequest is { } rendered && rendered.Revision == gradientRevision && ReferenceEquals(rendered.Edit, gradientEdit))
                            {
                                gradientRenderedPreview = gradientPreview;
                                gradientRenderedInput = rendered;
                                gradientRenderedRevision = rendered.Revision;
                            }
                            if (transformRequest is { } renderedTransform && renderedTransform.Revision == transformDraftRevision && ReferenceEquals(renderedTransform.Edit, transformInspectorEdit))
                            {
                                transformRenderedPreview = transformPreview;
                                transformRenderedRevision = renderedTransform.Revision;
                                if (renderedTransform.Finishing) PublishDistortCommit(renderedTransform, transformPreview);
                            }
                            if (pixelRequest is not null) PublishPixelMove(pixelRequest, pixelResult);
                            if (floatingRequest is not null) PublishFloatingTransform(floatingRequest, floatingPreview, floatingResult);
#if DEBUG
                            frameTrace?.Invoke("draft-published");
#endif
                            CanvasControl.Invalidate();
                            if (pointerStroke is null && !placementDragActive && !HasPendingInteraction)
                            {
                                if (transformRequest is { } thumbnailTransform && ReferenceEquals(thumbnailTransform.Edit, transformInspectorEdit)
                                    && transformPreview is not null)
                                {
                                    foreach (var id in thumbnailTransform.Edit.DirtyLayerIds)
                                        if (layerRows.TryGetValue(id, out var item)) RefreshLayerThumbnail((LayerRow)item.Tag, preview: transformPreview.Snapshot);
                                }
                                else if (SelectedLayer is { } thumbLayer && layerRows.TryGetValue(thumbLayer.Id, out var thumbGrid))
                                    RefreshLayerThumbnail((LayerRow)thumbGrid.Tag);
                            }
#if DEBUG
                            frameTrace?.Invoke("thumbnail-refreshed");
#endif
                            RefreshGuides();
#if DEBUG
                            frameTrace?.Invoke("guides-refreshed");
#endif
                            double ms = watch.Elapsed.TotalMilliseconds + uploadWatch.Elapsed.TotalMilliseconds;
                            if (ms >= 16)
                            {
                                App.Log($"slow frame band={band.Width}x{band.Height} render+upload={ms:0.0} ms interactive={interactive}", LogLevel.Diagnostic);
                                if (Environment.TickCount64 - lastSlowFrameNoticeMs >= 5000)
                                {
                                    lastSlowFrameNoticeMs = Environment.TickCount64;
                                    SetStatus(Loc.F("S_SlowFrame", band.Width, band.Height, ms));
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            renderFailureEpoch++;
#if DEBUG
                            renderPublicationFailureCount++;
#endif
                            App.Log($"Frame publication failed: {ex}");
                            SetStatus(Loc.T("S_CanvasUpdateFailed"));
                            pendingFullRender = true;
                        }
                        finally
                        {
                            renderInputs.EndFrame();
#if DEBUG
                            frameTrace?.Invoke("publication-finished");
#endif
                            if (!windowClosed && (HasPendingInteraction || HasPendingDirty)) PumpRender();
                        }
                    });
                }
                catch (OperationCanceledException) when (floatingRequest?.Token.IsCancellationRequested == true || transformRequest?.Token.IsCancellationRequested == true)
                {
                    frameDispatcher.TryEnqueue(() =>
                    {
                        try
                        {
                            renderInputs.EndFrame();
                            if (!windowClosed && (HasPendingInteraction || HasPendingDirty)) PumpRender();
                        }
                        catch (Exception recovery) { ReportRecoveryFault(recovery); }
                    });
                }
                catch (Exception ex)
                {
#if DEBUG
                    Interlocked.Increment(ref renderWorkerFailureCount);
#endif
                    App.Log($"worker failed: {ex}");
                    frameDispatcher.TryEnqueue(() =>
                    {
                        try
                        {
                            renderFailureEpoch++;
                            renderInputs.EndFrame();
                            if (windowClosed) return;
                            if (pixelRequest is not null && ReferenceEquals(pixelRequest.Session, pixelMove))
                            { CancelPixelMove(); SetStatus(Loc.T("S_SelectedPixelsFailed")); }
                            if (floatingRequest is not null && ReferenceEquals(floatingRequest.Session, floatingTransform))
                            { ClearTransformDraft(); RefreshLayerPanel(); RefreshSelectionOutline(); SetStatus(Loc.T("Tr_Unavailable")); }
                            if (transformRequest is { Finishing: true } && ReferenceEquals(transformRequest.Edit, transformInspectorEdit))
                            { ClearTransformDraft(); RefreshLayerPanel(); RefreshSelectionOutline(); RefreshTransformInspector(true); SetStatus(Loc.T("Tr_Unavailable")); }
                            pendingFullRender = true;
                            PumpRender();
                        }
                        catch (Exception recovery) { ReportRecoveryFault(recovery); }
                    });
                }
            });
#if DEBUG
            currentRenderWork = renderWork;
#endif
        }
        catch (Exception ex)
        {
            renderFailureEpoch++;
#if DEBUG
            renderInputFailureCount++;
#endif
            App.Log($"RenderPendingAsync failed: {ex}");
            renderInputs.EndFrame();
            pendingFullRender = true;
        }
    }

    /// <summary>
    /// A throw that escapes a dispatcher callback is not an ordinary UI fault in WinUI — the process
    /// fast-fails with no managed handler, so the paths that put the renderer back on its feet catch
    /// their own faults here and leave the retry queued. The log line is the durable record; the
    /// render loop keeps reporting through the existing failure statuses if it faults again.
    /// </summary>
    private void ReportRecoveryFault(Exception recovery)
    {
        pendingFullRender = true;
        App.Log($"Render recovery failed: {recovery}", LogLevel.Error);
    }

    private CanvasRenderTarget? fullTarget;
    private CanvasBitmap? presented;
    private GpuCanvasRenderer? gpuPreviewRenderer;
    private bool gpuPreviewUnavailable, gpuFramePresented;
    private bool useGpuRendering = true;

    private void OnToggleGpuRendering(object sender, RoutedEventArgs args)
    {
        useGpuRendering = GpuRenderingToggle.IsChecked;
        WriteFlag("gpu-rendering", useGpuRendering);
        gpuPreviewUnavailable = false;
        if (!useGpuRendering)
        {
            presented = null;
            gpuPreviewRenderer?.Dispose(); gpuPreviewRenderer = null;
        }
        RenderCanvas();
    }

    private bool HasPendingInteraction =>
        renderInputs.HasPending || pendingTransformPoint is not null || pendingMoveDelta is not null || pendingHealingPoints.Count > 0 || interactionDirty is not null;

    private bool workerBusy => renderInputs.IsBusy;

    /// <summary>
    /// The Draw pass: uploads the filled BGRA buffer as a GPU texture and draws it in one
    /// composition pass. CanvasControl presents at composition rate — atomic, no tear, no
    /// XAML source swaps.
    /// </summary>
    private string guidesSignature = "";

    /// <summary>Syncs the guide lines with the manifest; called after every present, but the
    /// rebuild is guarded by a signature so an unchanged guide set costs one string compare —
    /// tearing down and recreating the Line elements every frame churned the visual tree.</summary>
    private void RefreshGuides()
    {
        foreach (var line in guideLines) line.StrokeThickness = 1 / viewport.Zoom;
        string signature;
        // View ▸ Guides off (⌘;, CompositorApp.swift:128-129 → EditorCanvas.swift:13): treated as no guides
        // at all, so the same signature path removes the lines instead of leaving them up.
        if (document?.Snapshot.Manifest.Guides is { Count: > 0 } live && showsGuides)
        {
            signature = string.Join(';', live.Where(guide => guide.Id != draggingGuideId).Select(guide => $"{guide.Axis}:{guide.Position:R}"));
        }
        else
        {
            signature = "";
        }
        signature += $":{document?.Snapshot.Manifest.Width}:{document?.Snapshot.Manifest.Height}";
        if (signature == guidesSignature) return;
        guidesSignature = signature;
        foreach (var line in guideLines) ToolOverlay.Children.Remove(line);
        guideLines.Clear();
        if (document is null) return;
        var manifest = document.Snapshot.Manifest;
        if (!showsGuides || manifest.Guides is not { Count: > 0 }) return;
        foreach (var guide in manifest.Guides)
        {
            if (guide.Id == draggingGuideId) continue;
            var line = new Line
            {
                StrokeThickness = 1 / viewport.Zoom,
                Stroke = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0, 200, 255)),
                IsHitTestVisible = false,
            };
            if (guide.Axis == GuideAxis.Vertical)
            {
                line.X1 = line.X2 = guide.Position;
                line.Y1 = 0;
                line.Y2 = ToolOverlay.Height;
            }
            else
            {
                line.Y1 = line.Y2 = guide.Position;
                line.X1 = 0;
                line.X2 = ToolOverlay.Width;
            }
            ToolOverlay.Children.Add(line);
            guideLines.Add(line);
        }
    }

    private CanvasBitmap? checkerTile;
    private Microsoft.Graphics.Canvas.Brushes.CanvasImageBrush? checkerBrush;
    private bool showPixelGrid; // the document-pixel grid, drawn from 800% zoom as on the Mac
    private Stopwatch? firstDrawAt; // cold-start instrumentation: ctor → first canvas draw

    /// <summary>The pixel grid appears from 800%, as the Mac's pixelGridZoom.</summary>
    private const int PixelGridZoom = 8;

    /// <summary>
    /// The Mac's subdivisions and Dots majors use [1, 2] screen points; dashed majors use [4, 3].
    /// Win2D custom dashes multiply their values by the stroke width, which is one physical pixel here.
    /// Convert screen points to physical pixels when the display scale changes, and use flat dash caps.
    /// CustomDashStyle overrides DashStyle; no enum cast or preset approximation is needed.
    /// </summary>
    private readonly Microsoft.Graphics.Canvas.Geometry.CanvasStrokeStyle gridDottedStroke = new()
    {
        CustomDashStyle = [1, 2],
        DashCap = Microsoft.Graphics.Canvas.Geometry.CanvasCapStyle.Flat,
    };

    /// <summary>
    /// The Dashed Lines major style (Guides.swift:99).
    /// </summary>
    private readonly Microsoft.Graphics.Canvas.Geometry.CanvasStrokeStyle gridDashedStroke = new()
    {
        CustomDashStyle = [4, 3],
        DashCap = Microsoft.Graphics.Canvas.Geometry.CanvasCapStyle.Flat,
    };

    private readonly Microsoft.Graphics.Canvas.Geometry.CanvasStrokeStyle gridDottedHorizontalStroke = new()
    {
        CustomDashStyle = [1, 2],
        DashCap = Microsoft.Graphics.Canvas.Geometry.CanvasCapStyle.Flat,
    };
    private readonly Microsoft.Graphics.Canvas.Geometry.CanvasStrokeStyle gridDashedHorizontalStroke = new()
    {
        CustomDashStyle = [4, 3],
        DashCap = Microsoft.Graphics.Canvas.Geometry.CanvasCapStyle.Flat,
    };

    private double gridDashBackingScale = 1;

    private void ConfigureGridDashScale()
    {
        if (gridDashBackingScale == viewport.BackingScale) return;
        float scale = (float)viewport.BackingScale;
        gridDottedStroke.CustomDashStyle = [scale, 2 * scale];
        gridDashedStroke.CustomDashStyle = [4 * scale, 3 * scale];
        gridDottedHorizontalStroke.CustomDashStyle = [scale, 2 * scale];
        gridDashedHorizontalStroke.CustomDashStyle = [4 * scale, 3 * scale];
        gridDashBackingScale = viewport.BackingScale;
    }

    private void ConfigureGridDashPhase(double top, double left)
    {
        void SetPhase(Microsoft.Graphics.Canvas.Geometry.CanvasStrokeStyle style, double start, int period)
        {
            float phase = (float)((start * viewport.Zoom) % (period * viewport.BackingScale));
            if (style.DashOffset != phase) style.DashOffset = phase;
        }
        SetPhase(gridDottedStroke, top, 3); SetPhase(gridDashedStroke, top, 7);
        SetPhase(gridDottedHorizontalStroke, left, 3); SetPhase(gridDashedHorizontalStroke, left, 7);
    }

    /// <summary>
    /// A solid line needs no dash at all; the Lines style passes this so every major draw gets a real style
    /// object rather than a null one.
    /// </summary>
    private static readonly Microsoft.Graphics.Canvas.Geometry.CanvasStrokeStyle gridSolidStroke = new()
    {
        DashStyle = Microsoft.Graphics.Canvas.Geometry.CanvasDashStyle.Solid,
    };

    /// The grid's color at the given alpha: the Mac composites the same way, the opacity being a percentage
    /// of 255 with the sub-band a ratio of the major band (Guides.swift:115-119).
    private static Windows.UI.Color GridDrawColor(AdjustmentColor color, double alpha) =>
        Microsoft.UI.ColorHelper.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255),
            (byte)Math.Round(Math.Clamp(color.Red, 0, 1) * 255),
            (byte)Math.Round(Math.Clamp(color.Green, 0, 1) * 255),
            (byte)Math.Round(Math.Clamp(color.Blue, 0, 1) * 255));

    /// The picker's three styles in the vertical direction.
    private Microsoft.Graphics.Canvas.Geometry.CanvasStrokeStyle GridMajorStroke =>
        GridAppearance.Style switch
        {
            GridAppearance.LineStyle.DashedLines => gridDashedStroke,
            GridAppearance.LineStyle.Dots => gridDottedStroke,
            _ => gridSolidStroke,
        };

    private Microsoft.Graphics.Canvas.Geometry.CanvasStrokeStyle GridMajorHorizontalStroke =>
        GridAppearance.Style switch
        {
            GridAppearance.LineStyle.DashedLines => gridDashedHorizontalStroke,
            GridAppearance.LineStyle.Dots => gridDottedHorizontalStroke,
            _ => gridSolidStroke,
        };

    private void OnCanvasDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        try
        {
            if (firstDrawAt is { IsRunning: true })
            {
                firstDrawAt.Stop();
                App.Log($"startup: first draw {firstDrawAt.ElapsedMilliseconds} ms");
            }
            DrawViewportContent(args.DrawingSession);
            DrawDirectSelectionOutline(args.DrawingSession);
        }
        catch (Exception ex)
        {
            App.Log($"OnCanvasDraw failed: {ex}");
        }
    }

    private void DrawViewportContent(CanvasDrawingSession drawing)
    {
        if (document is null) return;
        var manifest = document.Snapshot.Manifest;
        var bounds = new Rect(0, 0, manifest.Width, manifest.Height);
        var origin = viewport.Origin;
        drawing.Transform = System.Numerics.Matrix3x2.CreateScale((float)viewport.PointsPerPixel)
            * System.Numerics.Matrix3x2.CreateTranslation((float)origin.X, (float)origin.Y);
        // A repeated 20x20 tile replaces the document-sized checker texture. Its cells
        // stay in document coordinates while the viewport is rendered directly at display DPI.
        if (checkerBrush is null)
        {
            var pixels = new byte[20 * 20 * 4];
            for (int y = 0; y < 20; y++)
            for (int x = 0; x < 20; x++)
            {
                int index = (y * 20 + x) * 4;
                byte gray = (byte)(((x / 10 + y / 10) % 2 == 0) ? 89 : 77);
                pixels[index] = pixels[index + 1] = pixels[index + 2] = gray; pixels[index + 3] = 255;
            }
            checkerTile = CanvasBitmap.CreateFromBytes(CanvasDevice.GetSharedDevice(), pixels, 20, 20,
                Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 96);
            checkerBrush = new Microsoft.Graphics.Canvas.Brushes.CanvasImageBrush(CanvasDevice.GetSharedDevice(), checkerTile)
            {
                ExtendX = CanvasEdgeBehavior.Wrap,
                ExtendY = CanvasEdgeBehavior.Wrap,
                SourceRectangle = new Rect(0, 0, 20, 20),
            };
        }
        drawing.FillRectangle(bounds, checkerBrush);
        if (presented is { } bitmap)
            drawing.DrawImage(bitmap, bounds, new Rect(0, 0, bitmap.Size.Width, bitmap.Size.Height), 1,
                viewport.Zoom >= 2 ? CanvasImageInterpolation.NearestNeighbor : CanvasImageInterpolation.Linear);
            // The original pixel grid draws only visible document boundaries, one physical
            // display pixel wide. Pan/zoom repaint this overlay without a document composite.
            if (showPixelGrid && viewport.Zoom >= PixelGridZoom)
            {
                var topLeft = viewport.ToDocument(new(0, 0));
                var bottomRight = viewport.ToDocument(new(viewport.Width, viewport.Height));
                int left = Math.Max(1, (int)Math.Floor(topLeft.X)), right = Math.Min(manifest.Width, (int)Math.Ceiling(bottomRight.X));
                int top = Math.Max(1, (int)Math.Floor(topLeft.Y)), bottom = Math.Min(manifest.Height, (int)Math.Ceiling(bottomRight.Y));
                var hairlineColor = Microsoft.UI.ColorHelper.FromArgb(115, 140, 140, 140);
                float hairline = (float)(1 / viewport.Zoom);
                for (int column = left; column < right; column++)
                    drawing.DrawLine(column, Math.Max(0, top - 1), column, Math.Min(manifest.Height, bottom + 1), hairlineColor, hairline);
                for (int row = top; row < bottom; row++)
                    drawing.DrawLine(Math.Max(0, left - 1), row, Math.Min(manifest.Width, right + 1), row, hairlineColor, hairline);
            }
        // View ▸ Grid (⌘'; the Mac draws it in TransformOverlay.swift:190-232, over the document in document
        // coordinates): majors in the chosen style, subdivisions dotted and fainter, one hairline each.
        // Subdivisions are dropped while they would sit closer than four screen points together, and only the
        // lines crossing the visible rectangle are drawn.
        if (showsGrid)
        {
            ConfigureGridDashScale();
            var grid = LayoutGrid;
            var appearance = GridAppearance;
            var majorColor = GridDrawColor(appearance.Color, appearance.MajorAlpha);
            var subdivisionColor = GridDrawColor(appearance.Color, appearance.SubdivisionAlpha);
            var majorStroke = GridMajorStroke;
            var majorHorizontalStroke = GridMajorHorizontalStroke;
            float lineWidth = (float)(1 / viewport.Zoom);
            var near = viewport.ToDocument(new(0, 0));
            var far = viewport.ToDocument(new(viewport.Width, viewport.Height));
            double left = Math.Max(0, Math.Floor(near.X)), right = Math.Min(manifest.Width, Math.Ceiling(far.X));
            double top = Math.Max(0, Math.Floor(near.Y)), bottom = Math.Min(manifest.Height, Math.Ceiling(far.Y));
            // Keep the original document phase while clipping long paths to the
            // viewport. DashOffset is measured in stroke-width units.
            ConfigureGridDashPhase(top, left);
            if (grid.Step * Math.Max(viewport.PointsPerPixel, .0001) >= 4)
            {
                foreach (var x in grid.Lines(manifest.Width))
                    if (!grid.IsMajor(x) && x >= left && x <= right) drawing.DrawLine((float)x, (float)top, (float)x, (float)bottom, subdivisionColor, lineWidth, gridDottedStroke);
                foreach (var y in grid.Lines(manifest.Height))
                    if (!grid.IsMajor(y) && y >= top && y <= bottom) drawing.DrawLine((float)left, (float)y, (float)right, (float)y, subdivisionColor, lineWidth, gridDottedHorizontalStroke);
            }
            foreach (var x in grid.Lines(manifest.Width))
                if (grid.IsMajor(x) && x >= left && x <= right) drawing.DrawLine((float)x, (float)top, (float)x, (float)bottom, majorColor, lineWidth, majorStroke);
            foreach (var y in grid.Lines(manifest.Height))
                if (grid.IsMajor(y) && y >= top && y <= bottom) drawing.DrawLine((float)left, (float)y, (float)right, (float)y, majorColor, lineWidth, majorHorizontalStroke);
        }
        // The outline stays one physical pixel at all zoom levels.
        drawing.DrawRectangle(bounds, Microsoft.UI.ColorHelper.FromArgb(33, 255, 255, 255), (float)(1 / viewport.Zoom));
        drawing.Transform = System.Numerics.Matrix3x2.Identity;
    }

    /// <summary>
    /// Folding or opening a folder row. The Mac hands the selection to the folder when you fold one that
    /// holds the layer you were working on (LayerGroups.swift:270-276): that child's row disappears with the
    /// fold, and a properties panel still pointed at a row nobody can see reads like the layer went away.
    /// Opening a folder changes nothing, and folding one that does not hold the selection leaves the
    /// selection where it was.
    /// </summary>
    private void ToggleFolderExpansion(Guid id)
    {
        if (collapsedGroups.Remove(id))
        {
            RefreshLayerPanel();
            return;
        }
        if (selectedLayerId is { } active && IsInsideLayer(active, id)) SelectLayerRow(id);
        collapsedGroups.Add(id);
        RefreshLayerPanel();
    }

    private void RefreshLayerPanel()
    {
        SyncMaskAloneTarget();
        suppressPanelEvents = true;
        try
        {
            if (document is null) return;
            if (renamingLayer) return; // an in-place rename must not be torn down mid-edit
            if (SelectedEffect is null) effectSelection = null;
            var manifest = LayerDisplaySnapshot!.Manifest;
            var primaryId = SelectedLayer?.Id;
            LayerCountLabel.Text = manifest.Layers.Count.ToString();
            var byId = manifest.Layers.ToDictionary(record => record.Id, record => record);
            if (!ReferenceEquals(layerRowsDocument, document))
            {
                layerRowsDocument = document;
                layerRows.Clear(); layerItems.Clear();
            }
            foreach (var removed in layerRows.Keys.Where(id => !byId.ContainsKey(id)).ToArray()) layerRows.Remove(removed);
            if (!ReferenceEquals(LayerList.ItemsSource, layerItems)) LayerList.ItemsSource = layerItems;
            selectedLayerIds.RemoveWhere(id => !byId.ContainsKey(id)); // undo/delete can drop members
            var order = DrawOrder.Resolve(manifest.Layers);

            // The panel reads top of the stack first, so reverse the hierarchy order, and rows
            // keep their controls and handlers across selection/rename/undo. The row's record
            // reference is refreshed after undo instead of capturing the abandoned manifest.
            var items = new List<ListViewItem>();
            foreach (var id in order.Order.AsEnumerable().Reverse())
            {
                if (!byId.TryGetValue(id, out var record)) continue;
                int depth = 0;
                var parent = record.ParentId;
                bool hidden = false;
                while (depth < 64 && parent is { } parentId && byId.TryGetValue(parentId, out var parentNode))
                {
                    if (collapsedGroups.Contains(parentId)) hidden = true;
                    depth += 1;
                    parent = parentNode.ParentId;
                }
                if (hidden) continue; // inside a folded folder
                if (!layerRows.TryGetValue(id, out var rowGrid) || ((LayerRow)rowGrid.Tag).IsGroupView != record.IsGroupOrFalse)
                {
                    rowGrid = BuildLayerRow(new LayerRow(record, depth));
                    layerRows[id] = rowGrid;
                }
                var row = (LayerRow)rowGrid.Tag;
                row.Update(record, depth);
                RefreshLayerEffectRows(row);
                RefreshLayerThumbnail(row, primaryId);
                string display = document.GetLayerLocks(id) == LayerLocks.None ? row.Display : "\U0001F512 " + row.Display;
                if (row.Label.Text != display) row.Label.Text = display;
                if (row.Eye.IsChecked != row.Visible) row.Eye.IsChecked = row.Visible;
                if (row.Disclosure is { } disclosure) disclosure.Content = collapsedGroups.Contains(id) ? "▸" : "▾";
                rowGrid.Background = record.Id != selectedLayerId && selectedLayerIds.Contains(record.Id)
                    ? new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 38, 68, 114)) : null;
                items.Add(rowGrid);
            }
            var wanted = items.ToHashSet();
            for (int index = layerItems.Count - 1; index >= 0; index--)
                if (!wanted.Contains(layerItems[index])) layerItems.RemoveAt(index);
            for (int index = 0; index < items.Count; index++)
            {
                if (index < layerItems.Count && ReferenceEquals(layerItems[index], items[index])) continue;
                int existing = layerItems.IndexOf(items[index]);
                if (existing >= 0) layerItems.Move(existing, index);
                else layerItems.Insert(index, items[index]);
            }
            UpdateMergeMenuText();
            UpdateMaskLinkMenuText();
            UpdateMaskEnabledMenuText();
            UpdateClippingMaskMenuText();

            var selected = SelectedLayer;
            if (selected is not null)
            {
                selectedLayerId = selected.Id;
                SelectedLayerName.Text = selected.Name;
                OpacitySlider.Value = (selected.Opacity ?? 1) * 100;
                OpacityLabel.Text = $"{Math.Round((selected.Opacity ?? 1) * 100)}%";
                syncingOpacity = true;
                OpacityBox.Value = (selected.Opacity ?? 1) * 100;
                syncingOpacity = false;
                if (!BlendCombo.IsDropDownOpen)
                    BlendCombo.SelectedItem = BlendCombo.Items.OfType<ComboBoxItem>().Single(item => (LayerBlendMode)item.Tag == (selected.BlendMode ?? LayerBlendMode.Normal));
                VisibleCheck.IsChecked = selected.IsVisible;
                PaintMaskCheck.IsEnabled = selected.MaskFile is not null;
            }
            else
            {
                SelectedLayerName.Text = Loc.T("L_NoLayer");
                VisibleCheck.IsChecked = false;
                PaintMaskCheck.IsEnabled = false;
            }
            // Preserve WinUI's range anchor: change only the membership that differs. Native
            // selection now drives Ctrl/Shift, keyboard selection, accessibility and row dragging.
            var selectedRows = items.Where(item => effectSelection is null && item.Tag is LayerRow row
                && (row.Layer.Id == selected?.Id || selectedLayerIds.Contains(row.Layer.Id))).ToHashSet();
            var nativeItems = LayerList.SelectedItems.ToArray();
            var nativeRows = nativeItems.OfType<ListViewItem>().ToHashSet();
            foreach (var item in nativeItems)
                if (item is not ListViewItem rowItem || !selectedRows.Contains(rowItem)) LayerList.SelectedItems.Remove(item);
            foreach (var item in items)
                if (selectedRows.Contains(item) && !nativeRows.Contains(item)) LayerList.SelectedItems.Add(item);
            RefreshSelectionOutline();
            RebuildAdjustmentPanel(selected);
            RefreshTransformInspector(true);
            UpdateTransformHandles();
        }
        finally
        {
            suppressPanelEvents = false;
        }
        RefreshQuickToolControls();
        RefreshLayerProtection();
    }

    // --- Transform handles (ported overlay geometry) ---

    // Cached element arrays and the reused rotation: this runs every frame of a transform
    // drag, and rebuilding them per frame churned the render loop.
    private Rectangle[]? transformHandles;
    private Rectangle[]? cropHandles;
    private readonly RotateTransform transformBoxRotation = new();

    private Rectangle[] HandleFields() => transformHandles ??= [Handle0, Handle1, Handle2, Handle3, Handle4, Handle5, Handle6, Handle7];

    private Rectangle[] CropHandleFields() => cropHandles ??= [CropHandle0, CropHandle1, CropHandle2, CropHandle3, CropHandle4, CropHandle5, CropHandle6, CropHandle7];

    private void UpdateTransformHandles()
    {
        var transform = tool == CanvasTool.Move && showTransformControls ? CurrentTransformValue : null;
        if (transform is null)
        {
            TransformBox.Visibility = Visibility.Collapsed;
            DistortBox.Visibility = Visibility.Collapsed;
            RotationStem.Visibility = Visibility.Collapsed;
            RotationHandleDot.Visibility = Visibility.Collapsed;
            foreach (var handle in HandleFields())
            {
                handle.Visibility = Visibility.Collapsed;
            }
            return;
        }
        var handles = transformCorners is { } shape ? TransformDrag.HandlePositions(shape) : TransformDrag.HandlePositions(transform);
        var rotation = TransformDrag.RotationHandle(transform);
        var top = handles[1];

        void Place(FrameworkElement element, double centerX, double centerY)
        {
            Canvas.SetLeft(element, centerX - element.Width / 2);
            Canvas.SetTop(element, centerY - element.Height / 2);
            element.Visibility = Visibility.Visible;
        }

        // The box through the four corners; handles sit on the corners, edge midpoints, top.
        // The element is placed so its CENTER lands on the transform's center, then rotated
        // around that center — placing at a rotated corner and rotating around the element
        // center drifts the outline away from the handles.
        TransformBox.Visibility = Visibility.Visible;
        var center = transform.Center;
        double halfW = transform.Size.Width, halfH = transform.Size.Height;
        TransformBox.Width = halfW;
        TransformBox.Height = halfH;
        Canvas.SetLeft(TransformBox, center.X - halfW / 2);
        Canvas.SetTop(TransformBox, center.Y - halfH / 2);
        transformBoxRotation.Angle = transform.Rotation;
        transformBoxRotation.CenterX = halfW / 2;
        transformBoxRotation.CenterY = halfH / 2;
        TransformBox.RenderTransform = transformBoxRotation;

        var fields = HandleFields();
        if (transformCorners is { } corners)
        {
            TransformBox.Visibility = Visibility.Collapsed; DistortBox.Visibility = Visibility.Visible;
            DistortBox.Points.Clear(); foreach (var corner in corners) DistortBox.Points.Add(new(corner.X, corner.Y));
            DistortBox.StrokeThickness = 1 / viewport.Zoom;
        }
        else DistortBox.Visibility = Visibility.Collapsed;
        for (int i = 0; i < fields.Length; i++)
        {
            fields[i].Width = fields[i].Height = 9 / viewport.PointsPerPixel;
            fields[i].StrokeThickness = 1 / viewport.Zoom;
            Place(fields[i], handles[i].X, handles[i].Y);
        }
        RotationHandleDot.Width = RotationHandleDot.Height = 11 / viewport.PointsPerPixel;
        RotationHandleDot.StrokeThickness = RotationStem.StrokeThickness = TransformBox.StrokeThickness = 1 / viewport.Zoom;
        RotationStem.Visibility = Visibility.Visible;
        RotationStem.X1 = top.X;
        RotationStem.Y1 = top.Y;
        RotationStem.X2 = rotation.X;
        RotationStem.Y2 = rotation.Y;
        Place(RotationHandleDot, rotation.X, rotation.Y);
        if (transformCorners is not null) RotationStem.Visibility = RotationHandleDot.Visibility = Visibility.Collapsed;
    }

    private void OnRenameLayer(object sender, RoutedEventArgs e) => RenameSelectedLayer();

    /// <summary>
    /// Layer ▸ Rename Layer… (CompositorApp.swift:318, whose row-menu twin is renameLayerAction at
    /// NativeLayerList.swift:267-271): the way into a row's own rename field that is not a double-click, which
    /// until now was the only entry the panel had. The Mac scrolls the row into view first
    /// (NativeLayerList.swift:98-99) because the selected layer can sit well below the fold — starting a
    /// rename on a row that was never realized would look like the menu item did nothing.
    /// </summary>
    internal void RenameSelectedLayer()
    {
        if (!CanEditLayerCommands || renamingLayer || SelectedLayer is not { } layer) return;
        if (layerItems.FirstOrDefault(grid => ((LayerRow)grid.Tag).Layer.Id == layer.Id) is not { } grid) return;
        LayerList.ScrollIntoView(grid);
        var row = (LayerRow)grid.Tag;
        StartRename(row, row.Label);
    }

    /// <summary>
    /// Swaps the row's name for a TextBox: Enter or losing focus commits through
    /// RenameLayer (the Changed refresh rebuilds the row), Escape restores the old name.
    /// </summary>
    private void StartRename(LayerRow row, TextBlock name)
    {
        if (renamingLayer || document is null) return;
        renamingLayer = true;
        var grid = (Grid)name.Parent;
        var editor = new TextBox { Text = row.Layer.Name, MinWidth = 80 };
        Grid.SetColumn(editor, Grid.GetColumn(name));

        void Finish(bool commit)
        {
            renamingLayer = false;
            var proposed = editor.Text.Trim();
            if (commit && proposed.Length > 0 && proposed != row.Layer.Name)
            {
                string oldName = row.Layer.Name;
                document?.RenameLayer(row.Layer.Id, proposed);
                SetStatus(Loc.F("S_Renamed", oldName, proposed));
            }
            else
            {
                name.Text = row.Display; // Esc or empty: put the old label back
            }
            if (grid.Children.Contains(editor)) grid.Children.Remove(editor);
            name.Visibility = Visibility.Visible;
        }

        editor.KeyDown += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Enter) { args.Handled = true; Finish(true); }
            else if (args.Key == Windows.System.VirtualKey.Escape) { args.Handled = true; Finish(false); }
        };
        editor.LostFocus += (_, _) => { if (renamingLayer) Finish(true); };

        name.Visibility = Visibility.Collapsed;
        grid.Children.Add(editor);
        editor.Focus(FocusState.Programmatic);
        editor.SelectAll();
    }

    private ListViewItem BuildLayerRow(LayerRow row)
    {
        var grid = new Grid { Padding = new Thickness(2, 2, 2, 2), Tag = row };
        grid.PointerPressed += (_, args) =>
        {
            if (args.GetCurrentPoint(grid).Properties.IsLeftButtonPressed) layerSelectionPointerTarget = row.Layer.Id;
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        if (row.Layer.IsGroupOrFalse)
        {
            bool collapsed = collapsedGroups.Contains(row.Layer.Id);
            var disclosure = new Button
            {
                Content = collapsed ? "▸" : "▾",
                MinWidth = 20,
                MinHeight = 0,
                Padding = new Thickness(0),
                FontSize = 10,
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            };
            disclosure.Click += (_, _) => ToggleFolderExpansion(row.Layer.Id);
            Grid.SetColumn(disclosure, 0);
            grid.Children.Add(disclosure);
            row.Disclosure = disclosure;
        }

        var visible = new CheckBox
        {
            IsChecked = row.Visible,
            MinWidth = 0,
            Padding = new Thickness(0),
            Style = (Style)WorkspaceGrid.Resources["LayerEyeStyle"],
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(visible, Loc.T("A_LayerVisibility"));
        visible.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) =>
        {
            if (!args.GetCurrentPoint(visible).Properties.IsLeftButtonPressed || !BeginVisibilitySwipe(row.Layer.Id)) return;
            if (!LayerList.CapturePointer(args.Pointer)) FinishVisibilitySwipe();
            layerSelectionPointerTarget = null;
            args.Handled = true;
        }), true);
        // The row's eye toggles that layer's visibility directly, as the macOS panel's does —
        // not only the selected layer's checkbox in the properties stack.
        visible.Checked += (_, _) =>
        {
            if (!suppressPanelEvents && CanEditLayerCommands) document?.SetVisible(row.Layer.Id, true);
        };
        visible.Unchecked += (_, _) =>
        {
            if (!suppressPanelEvents && CanEditLayerCommands) document?.SetVisible(row.Layer.Id, false);
        };
        Grid.SetColumn(visible, 1);

        var name = new TextBlock
        {
            Text = row.Display,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        name.DoubleTapped += (_, _) => StartRename(row, name);
        Grid.SetColumn(name, 4);

        row.Thumbnail = new Image { Width = 32, Height = 28 };
        row.KindIcon = new FontIcon { FontSize = 18, Foreground = new SolidColorBrush(Microsoft.UI.Colors.LightGray) };
        var preview = new Grid(); preview.Children.Add(row.Thumbnail); preview.Children.Add(row.KindIcon);
        row.PixelTarget = BuildThumbnailTarget(row, false, preview);
        Grid.SetColumn(row.PixelTarget, 2); grid.Children.Add(row.PixelTarget);
        row.MaskThumbnail = new Image { Width = 26, Height = 24 };
        row.MaskDisabledMark = new Line { X1 = 2, Y1 = 24, X2 = 26, Y2 = 2, StrokeThickness = 2,
            Stroke = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed), IsHitTestVisible = false };
        var maskPreview = new Grid(); maskPreview.Children.Add(row.MaskThumbnail); maskPreview.Children.Add(row.MaskDisabledMark);
        row.MaskTarget = BuildThumbnailTarget(row, true, maskPreview);
        row.MaskLink = new Button { Width = 16, MinWidth = 0, MinHeight = 0, Padding = new(0), BorderThickness = new(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Content = new FontIcon { Glyph = "\uE71B", FontSize = 12 } };
        ToolTipService.SetToolTip(row.MaskLink, Loc.T("A_MaskLink"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(row.MaskLink, Loc.T("A_MaskLink"));
        row.MaskLink.Click += (_, _) =>
        {
            if (CanEditLayerCommands && document is { } owner)
                QueueCanvasInput(owner, () => { if (CanEditLayerCommands) owner.SetMaskLinked(row.Layer.Id, row.Layer.MaskLinked == false); });
        };
        var maskTargets = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        maskTargets.Children.Add(row.MaskLink); maskTargets.Children.Add(row.MaskTarget);
        Grid.SetColumn(maskTargets, 3); grid.Children.Add(maskTargets);

        grid.Children.Add(visible);
        grid.Children.Add(name);
        row.Eye = visible; row.Label = name;
        // Reuse the native container as well as its Grid when Extended selection moves rows.
        row.EffectsHost = new StackPanel();
        var item = new ListViewItem { Content = new StackPanel { Children = { grid, row.EffectsHost } }, Tag = row };
        AttachLayerContextMenu(item, row);
        return item;
    }

    private ProjectLayerRecord? SelectedLayer
    {
        get
        {
            if (document is null) return null;
            var manifest = LayerDisplaySnapshot!.Manifest;
            if (selectedLayerId is { } id)
            {
                var found = manifest.Layers.FirstOrDefault(layer => layer.Id == id);
                if (found is not null) return found;
            }
            var active = manifest.ActiveLayerId is { } activeId
                ? manifest.Layers.FirstOrDefault(layer => layer.Id == activeId)
                : null;
            return active;
        }
    }

    // Status colors are set on every status line and every drag tick — one brush each, not a
    // fresh SolidColorBrush per call.
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush StatusBrush = new(Microsoft.UI.ColorHelper.FromArgb(255, 178, 178, 178));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush ErrorBrush = new(Microsoft.UI.Colors.Salmon);

    /// <summary>The standing document-size readout beside the zoom label.</summary>
    private void UpdateDocSizeLabel()
    {
        DocSizeLabel.Text = document is null
            ? ""
            : $"{document.Snapshot.Manifest.Width} × {document.Snapshot.Manifest.Height}";
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
        StatusText.Foreground = StatusBrush;
        ErrorDetailsButton.Visibility = Visibility.Collapsed;
        lastErrorDetails = null;
    }

    private string? lastErrorDetails;

    /// <summary>
    /// The failure path of SetStatus, matching the Mac's typed error alerts: the message
    /// turns red, and when an exception carries the story a Details control opens a dialog
    /// with the whole chain. A null error keeps the red note but no details.
    /// </summary>
    private void Report(string message, Exception? error)
    {
        StatusText.Text = message;
        StatusText.Foreground = ErrorBrush;
        lastErrorDetails = error is null ? null : Describe(error);
        ErrorDetailsButton.Visibility = lastErrorDetails is null ? Visibility.Collapsed : Visibility.Visible;
        if (error is not null) App.Log($"failed: {message} — {lastErrorDetails}");
    }

    private static string Describe(Exception error)
    {
        var lines = new List<string>();
        for (var current = (Exception?)error; current is not null; current = current.InnerException)
        {
            lines.Add($"{current.GetType().Name}: {current.Message}");
        }
        return string.Join("\n\n", lines);
    }

    private async void OnErrorDetails(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = Loc.T("Dlg_FailedTitle"),
            Content = new ScrollViewer
            {
                MaxHeight = 360,
                Content = new TextBlock
                {
                    Text = lastErrorDetails ?? "",
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                },
            },
            CloseButtonText = Loc.T("Dlg_OK"),
            XamlRoot = Content.XamlRoot,
        };
        await dialog.ShowAsync();
    }

    /// <summary>The status bar's busy indicator for long imports and exports.</summary>
    private void SetBusy(bool busy)
    {
        StatusProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    // --- Overlay drawing ---

    private void RefreshSelectionOutline()
    {
        RefreshMaskSelectionOutline(DisplayedSelection);
        UpdateFloatingSelectionOutline();
    }

    private void ShowDragPreview(Rect rect)
    {
        DragPreview.Visibility = Visibility.Visible;
        Canvas.SetLeft(DragPreview, rect.X);
        Canvas.SetTop(DragPreview, rect.Y);
        DragPreview.Width = Math.Max(1, rect.Width);
        DragPreview.Height = Math.Max(1, rect.Height);
    }

    // --- Shape tool ---

    private void OnShapeKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ShapeKindCombo.SelectedIndex is >= 0 and int index)
        {
            shapeKind = (ShapeKind)index;
        }
    }

    // The box a drag from anchor to point spans, in whole pixels; square evens the sides.
    // Ported from the macOS DragBox, shared by the marquees and the Shape tool.
    private static Rect DragBoxRect(PointD anchor, PointD point, bool square)
    {
        double dx = Math.Round(point.X) - Math.Round(anchor.X);
        double dy = Math.Round(point.Y) - Math.Round(anchor.Y);
        if (square)
        {
            double side = Math.Max(Math.Abs(dx), Math.Abs(dy));
            dx = dx < 0 ? -side : side;
            dy = dy < 0 ? -side : side;
        }
        return new Rect(Math.Min(anchor.X, anchor.X + dx), Math.Min(anchor.Y, anchor.Y + dy), Math.Abs(dx), Math.Abs(dy));
    }

    // Shift snaps a line's angle to eighths of a turn — flat, upright, or 45° — as on the Mac.
    private static PointD SnapLineAngle(PointD anchor, PointD point, MidpointRounding rounding = MidpointRounding.ToEven)
    {
        double dx = point.X - anchor.X, dy = point.Y - anchor.Y;
        double angle = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4), rounding) * (Math.PI / 4);
        double length = Math.Sqrt(dx * dx + dy * dy);
        return new PointD(anchor.X + Math.Cos(angle) * length, anchor.Y + Math.Sin(angle) * length);
    }

    private void ShowShapePreview(Rect box, PointD? lineFrom, PointD? lineTo)
    {
        DragPreview.Visibility = shapeKind == ShapeKind.Rectangle ? Visibility.Visible : Visibility.Collapsed;
        if (DragPreview.Visibility == Visibility.Visible)
        {
            Canvas.SetLeft(DragPreview, box.X);
            Canvas.SetTop(DragPreview, box.Y);
            DragPreview.Width = Math.Max(1, box.Width);
            DragPreview.Height = Math.Max(1, box.Height);
        }
        ShapePreviewEllipse.Visibility = shapeKind == ShapeKind.Ellipse ? Visibility.Visible : Visibility.Collapsed;
        if (ShapePreviewEllipse.Visibility == Visibility.Visible)
        {
            Canvas.SetLeft(ShapePreviewEllipse, box.X);
            Canvas.SetTop(ShapePreviewEllipse, box.Y);
            ShapePreviewEllipse.Width = Math.Max(1, box.Width);
            ShapePreviewEllipse.Height = Math.Max(1, box.Height);
        }
        ShapePreviewLine.Visibility = shapeKind == ShapeKind.Line && lineFrom is { } && lineTo is { } ? Visibility.Visible : Visibility.Collapsed;
        if (ShapePreviewLine.Visibility == Visibility.Visible)
        {
            ShapePreviewLine.X1 = lineFrom!.Value.X;
            ShapePreviewLine.Y1 = lineFrom.Value.Y;
            ShapePreviewLine.X2 = lineTo!.Value.X;
            ShapePreviewLine.Y2 = lineTo.Value.Y;
        }
    }

    private void HideShapePreview()
    {
        DragPreview.Visibility = Visibility.Collapsed;
        ShapePreviewEllipse.Visibility = Visibility.Collapsed;
        ShapePreviewLine.Visibility = Visibility.Collapsed;
    }

    // The shape a drag commits: the box for rectangles and ellipses; for lines the two points
    // plus their box, which grows by half the stroke thickness so the round ends fit inside,
    // with the ends kept as box fractions — the macOS finishShape bookkeeping.
    private (Rect Box, PointD? LineFrom, PointD? LineTo) ShapeCommit(PointD anchor, PointD point, bool square)
    {
        if (shapeKind == ShapeKind.Line)
        {
            PointD to = square ? SnapLineAngle(anchor, point) : point;
            double thickness = ShapeLineWidthSlider.Value;
            double x = Math.Min(anchor.X, to.X) - thickness / 2;
            double y = Math.Min(anchor.Y, to.Y) - thickness / 2;
            double width = Math.Abs(to.X - anchor.X) + thickness;
            double height = Math.Abs(to.Y - anchor.Y) + thickness;
            var box = new Rect(x, y, width, height);
            PointD from = new((anchor.X - box.X) / box.Width, (anchor.Y - box.Y) / box.Height);
            PointD ends = new((to.X - box.X) / box.Width, (to.Y - box.Y) / box.Height);
            return (box, from, ends);
        }
        return (DragBoxRect(anchor, point, square), null, null);
    }

    // The macOS shape options pair a slider with a typed field (ShapeControls.swift:20-45): the slider
    // stops at 200 / 100 while the field takes up to 5000, and ↑/↓ step the field, Shift by ten. Without
    // the field the only way to ask for a radius of exactly 37 is to drag a slider by mouse.
    private bool syncingShapeOptions;

    private void SyncShapeOptions(Action apply)
    {
        if (syncingShapeOptions) return;
        syncingShapeOptions = true;
        try { apply(); }
        finally { syncingShapeOptions = false; }
    }

    private void OnShapeCornerBoxChanged(Microsoft.UI.Xaml.Controls.NumberBox sender,
        Microsoft.UI.Xaml.Controls.NumberBoxValueChangedEventArgs args)
        => SyncShapeOptions(() => ShapeCornerSlider.Value = Math.Min(200, sender.Value));

    private void OnShapeLineWidthBoxChanged(Microsoft.UI.Xaml.Controls.NumberBox sender,
        Microsoft.UI.Xaml.Controls.NumberBoxValueChangedEventArgs args)
        => SyncShapeOptions(() => ShapeLineWidthSlider.Value = Math.Min(100, sender.Value));

    private void OnShapeCornerSliderChanged(object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs args)
    {
        // The slider's own initial Value fires this during XAML load, before the field declared after it
        // exists; assigning through a null field surfaces as a XamlParseException on the slider line.
        if (ShapeCornerBox is null) return;
        SyncShapeOptions(() => ShapeCornerBox.Value = args.NewValue);
    }

    private void OnShapeLineWidthSliderChanged(object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs args)
    {
        if (ShapeLineWidthBox is null) return;
        SyncShapeOptions(() => ShapeLineWidthBox.Value = args.NewValue);
    }

    private void OnShapeNumberKey(object sender, KeyRoutedEventArgs args)
    {
        if (sender is not Microsoft.UI.Xaml.Controls.NumberBox box) return;
        if (args.Key is Windows.System.VirtualKey.Up or Windows.System.VirtualKey.Down)
        {
            StepShapeNumber(box, args.Key == Windows.System.VirtualKey.Up, KeyHeld(Windows.System.VirtualKey.Shift));
            args.Handled = true;
        }
        else if (args.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Escape)
        {
            // The number is already live; both keys only hand the keyboard back to the canvas, as the
            // zoom field does.
            CanvasStage.Focus(FocusState.Programmatic);
            args.Handled = true;
        }
    }

    internal static void StepShapeNumber(Microsoft.UI.Xaml.Controls.NumberBox box, bool up, bool shift)
    {
        double current = double.IsFinite(box.Value) ? box.Value : box.Minimum;
        double step = shift ? 10 : 1;
        box.Value = Math.Clamp(current + (up ? step : -step), box.Minimum, box.Maximum);
    }

    /// <summary>The value a shape field means: finite, and inside the macOS 5000 ceiling / its own floor.</summary>
    private static double ShapeNumber(double value, double floor)
        => double.IsFinite(value) ? Math.Clamp(Math.Round(value), floor, 5000) : floor;

    private void CommitShape()
    {
        int width = (int)Math.Round(shapePreviewBox.Width);
        int height = (int)Math.Round(shapePreviewBox.Height);
        if (width < 1 || height < 1 || document is null)
        {
            return; // a click without a drag makes nothing (except the Line dot, above)
        }
        var style = new ShapeStyle
        {
            Kind = shapeKind,
            Red = foregroundColor.R / 255.0,
            Green = foregroundColor.G / 255.0,
            Blue = foregroundColor.B / 255.0,
            CornerRadius = shapeKind == ShapeKind.Rectangle ? ShapeNumber(ShapeCornerBox.Value, 0) : 0,
            LineWidth = shapeKind == ShapeKind.Line ? ShapeNumber(ShapeLineWidthBox.Value, 1) : null,
            Start = shapePreviewFrom,
            End = shapePreviewTo,
        };
        try
        {
            var layer = document.AddShapeLayer(style, width, height, new PointD(shapePreviewBox.X, shapePreviewBox.Y));
            SetStatus(Loc.F("S_ShapeAdded", layer.Name));
        }
        catch (ArgumentOutOfRangeException)
        {
            SetStatus(Loc.T("S_ShapeTooBig"));
        }
    }

    private void ShowCropPreview(Rect rect)
    {
        CropPreview.Visibility = Visibility.Visible;
        Canvas.SetLeft(CropPreview, rect.X);
        Canvas.SetTop(CropPreview, rect.Y);
        CropPreview.Width = Math.Max(1, rect.Width);
        CropPreview.Height = Math.Max(1, rect.Height);
        UpdateCropHandles(rect);
    }

    /// <summary>The frame's eight handles, in TransformDrag's handle order.</summary>
    private void UpdateCropHandles(Rect rect)
    {
        var transform = new LayerTransform
        {
            Origin = new PointD(rect.X, rect.Y),
            Size = new SizeD(rect.Width, rect.Height),
        };
        var positions = TransformDrag.HandlePositions(transform);
        var handles = CropHandleFields();
        for (int i = 0; i < handles.Length; i++)
        {
            handles[i].Visibility = Visibility.Visible;
            Canvas.SetLeft(handles[i], positions[i].X - 4.5);
            Canvas.SetTop(handles[i], positions[i].Y - 4.5);
        }
    }

    private void HideCropHandles()
    {
        foreach (var handle in CropHandleFields())
        {
            handle.Visibility = Visibility.Collapsed;
        }
    }

    // --- Crop geometry, ported from the macOS Crop.swift ---

    private static Rect CropSnapped(Rect rect)
    {
        double x = Math.Floor(rect.X), y = Math.Floor(rect.Y);
        return new Rect(x, y, Math.Max(1, Math.Round(rect.Right) - x), Math.Max(1, Math.Round(rect.Bottom) - y));
    }

    private static Rect CropCreated(PointD start, PointD end, double? ratio)
    {
        double dx = end.X - start.X, dy = end.Y - start.Y;
        if (ratio is { } r)
        {
            if (Math.Abs(dx) > Math.Abs(dy) * r) dy = (dy < 0 ? -1 : 1) * Math.Abs(dx) / r;
            else dx = (dx < 0 ? -1 : 1) * Math.Abs(dy) * r;
        }
        return CropSnapped(new Rect(Math.Min(start.X, start.X + dx), Math.Min(start.Y, start.Y + dy), Math.Abs(dx), Math.Abs(dy)));
    }

    private string CropRatioChoice => CropRatioCombo.SelectedIndex switch
    {
        1 => "Original",
        2 => "1:1",
        3 => "4:3",
        4 => "3:4",
        5 => "16:9",
        6 => "9:16",
        _ => "Free",
    };

    private double? CropRatioValue => CropRatioChoice switch
    {
        "Original" when document is { } doc => (double)doc.Snapshot.Manifest.Width / doc.Snapshot.Manifest.Height,
        "1:1" => 1,
        "4:3" => 4 / 3.0,
        "3:4" => 3 / 4.0,
        "16:9" => 16 / 9.0,
        "9:16" => 9 / 16.0,
        _ => null,
    };

    /// <summary>
    /// The frame after a drag, its edges snapped to the cached targets (canvas and layer
    /// edges, no centers — the macOS cropSnapTargets): a move snaps both edges and keeps
    /// the size, a create or resize snaps only the edges on the dragged side, and with a
    /// fixed ratio only moves snap so the ratio stays exact.
    /// </summary>
    private Rect SnapCrop(Rect rect, CropDragMode mode, int resizeIndex, PointD point)
    {
        if (snapXs is null || snapYs is null) return rect;
        double reach = 8 / viewport.PointsPerPixel;
        double? Nearest(double value, List<double> targets)
        {
            double? best = null;
            foreach (var target in targets)
            {
                if (Math.Abs(target - value) > reach) continue;
                if (best is { } current && Math.Abs(current - value) <= Math.Abs(target - value)) continue;
                best = target;
            }
            return best;
        }
        bool ratioLocked = CropRatioValue is not null && mode != CropDragMode.Move;
        var result = rect;
        switch (mode)
        {
            case CropDragMode.Move:
            {
                double Shift(double[] edges, List<double> targets)
                {
                    var shifts = new List<double>();
                    foreach (var edge in edges)
                    {
                        if (Nearest(edge, targets) is { } target) shifts.Add(target - edge);
                    }
                    return shifts.Count == 0 ? 0 : shifts.OrderBy(Math.Abs).First();
                }
                return new Rect(
                    result.X + Shift([result.Left, result.Right], snapXs),
                    result.Y + Shift([result.Top, result.Bottom], snapYs),
                    result.Width, result.Height);
            }
            case CropDragMode.Create when ratioLocked:
            case CropDragMode.Resize when ratioLocked:
                return result;
            case CropDragMode.Resize:
            {
                var handle = TransformDrag.Handles[resizeIndex % TransformDrag.Handles.Length];
                // The dragged edge is the one the handle sits on.
                if (handle.X != 0.5)
                {
                    if (handle.X < 0.5 && Nearest(result.Left, snapXs) is { } left && left < result.Right)
                        result = new Rect(left, result.Top, result.Right - left, result.Height);
                    else if (handle.X > 0.5 && Nearest(result.Right, snapXs) is { } right && right > result.Left)
                        result = new Rect(result.Left, result.Top, right - result.Left, result.Height);
                }
                if (handle.Y != 0.5)
                {
                    if (handle.Y < 0.5 && Nearest(result.Top, snapYs) is { } top && top < result.Bottom)
                        result = new Rect(result.Left, top, result.Width, result.Bottom - top);
                    else if (handle.Y > 0.5 && Nearest(result.Bottom, snapYs) is { } bottom && bottom > result.Top)
                        result = new Rect(result.Left, result.Top, result.Width, bottom - result.Top);
                }
                return result;
            }
            default:
            {
                // A fresh drag snaps the edges on the pointer's side.
                if (Nearest(result.Left, snapXs) is { } left && left < result.Right && Math.Abs(point.X - result.Left) <= Math.Abs(point.X - result.Right))
                    result = new Rect(left, result.Top, result.Right - left, result.Height);
                else if (Nearest(result.Right, snapXs) is { } right && right > result.Left)
                    result = new Rect(result.Left, result.Top, right - result.Left, result.Height);
                if (Nearest(result.Top, snapYs) is { } top && top < result.Bottom && Math.Abs(point.Y - result.Top) <= Math.Abs(point.Y - result.Bottom))
                    result = new Rect(result.Left, top, result.Width, result.Bottom - top);
                else if (Nearest(result.Bottom, snapYs) is { } bottom && bottom > result.Top)
                    result = new Rect(result.Left, result.Top, result.Width, bottom - result.Top);
                return result;
            }
        }
    }

    private Rect UpdateCropRect(PointD point)
    {
        Rect rect = cropDragMode switch
        {
            CropDragMode.Create => CropCreated(cropDragStart, point, CropRatioValue),
            CropDragMode.Move => CropSnapped(new Rect(
                cropDragOriginal.X + point.X - cropDragStart.X,
                cropDragOriginal.Y + point.Y - cropDragStart.Y,
                cropDragOriginal.Width, cropDragOriginal.Height)),
            _ => CropSnapped(RectFromTransform(TransformDrag.Updated(
                cropResizeDrag!, point,
                lockRatio: CropRatioValue is not null, shift: false, option: false))),
        };
        return SnapCrop(rect, cropDragMode, cropResizeDrag?.ResizeIndex ?? 0, point);
    }

    private static Rect RectFromTransform(LayerTransform transform) =>
        new(transform.Origin.X, transform.Origin.Y, transform.Size.Width, transform.Size.Height);

    private LayerTransform CropTransform() => new()
    {
        Origin = new PointD(cropDragRect.X, cropDragRect.Y),
        Size = new SizeD(cropDragRect.Width, cropDragRect.Height),
    };

    private void UpdateCropSizeLabel()
    {
        CropSizeLabel.Text = $"{(int)cropDragRect.Width:0} × {(int)cropDragRect.Height:0} px";
    }

    private void ClearCropBox()
    {
        cropHasBox = false;
        cropRectangle = null;
        CropPreview.Visibility = Visibility.Collapsed;
        HideCropHandles();
        CropSizeLabel.Text = "";
    }

    /// <summary>The ratio picker reshapes the existing frame around its center, as on the Mac.</summary>
    private void OnCropRatioChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CropRatioCombo.SelectedIndex < 0 || document is null) return;
        if (CropRatioValue is not { } ratio || !cropHasBox) return;
        double height = cropDragRect.Width / ratio;
        var next = CropSnapped(new Rect(
            cropDragRect.X, cropDragRect.Y + cropDragRect.Height / 2 - height / 2, cropDragRect.Width, height));
        if (next.Width >= 1 && next.Height >= 1)
        {
            cropDragRect = next;
            cropRectangle = new RectI((int)next.X, (int)next.Y, (int)next.Width, (int)next.Height);
            ShowCropPreview(next);
            UpdateCropSizeLabel();
        }
    }

    private void CancelDragPreview()
    {
        marqueePreviewPending = false;
        CancelLasso();
        DragPreview.Visibility = Visibility.Collapsed;
        ShapePreviewEllipse.Visibility = Visibility.Collapsed;
        ShapePreviewLine.Visibility = Visibility.Collapsed;
        CropPreview.Visibility = Visibility.Collapsed;
        HideCropHandles();
        dragStart = null;
        cropRectangle = null;
        cropHasBox = false;
        cropDragging = false;
    }

    // --- Canvas interaction ---

    private PointD DocumentPoint(PointerRoutedEventArgs e)
    {
        var position = e.GetCurrentPoint(ToolOverlay).Position;
        return new PointD(position.X, position.Y);
    }

    private void OnCanvasPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(CanvasStage).Properties.IsRightButtonPressed) { e.Handled = true; return; }
        if (document is null) return;
        if (TryBeginNavigation(e)) return;
        if (IsFileImporting || IsGradientFinishing || IsDeletePending) return;
        if (placementFinishing) return;
        if (healingFinishing) { SetStatus(Loc.T("S_HealingBusy")); return; }
        ClearLayerEffectSelection();
        if (selectionWorkResult is not null && tool is not (CanvasTool.RectSelect or CanvasTool.EllipseSelect or CanvasTool.Lasso))
        { SetStatus(Loc.T("S_SelectionWorking", "Building selection…")); return; }
        var point = DocumentPoint(e);
        if (pixelMove is not null || pixelNudgePending > 0) { e.Handled = true; return; }
        if (e.GetCurrentPoint(CanvasStage).Properties.IsLeftButtonPressed && HandlePixelMovePress(viewport.ToDocument(StagePoint(e, CanvasStage)), e.KeyModifiers))
        { CanvasStage.Focus(FocusState.Pointer); CanvasStage.CapturePointer(e.Pointer); e.Handled = true; return; }
        if (e.GetCurrentPoint(CanvasStage).Properties.IsLeftButtonPressed && TryBeginSelectionMove(viewport.ToDocument(StagePoint(e, CanvasStage)), e.KeyModifiers))
        { CanvasStage.Focus(FocusState.Pointer); CanvasStage.CapturePointer(e.Pointer); e.Handled = true; return; }
        ((UIElement)sender).CapturePointer(e.Pointer);
        CacheSnapTargets();
        if (colorRangeSampling)
        {
            // The panel owns the canvas: clicks pick colors instead of driving the tool.
            SampleColorRangePoint(point, e.KeyModifiers);
            return;
        }
        if (cameraRawSamplesWhiteBalance)
        {
            // Same arrangement for Camera Raw's white-balance selector (EditorCanvas.swift:1742-1747): the click
            // belongs to the panel whether or not it found a pixel worth solving, so a miss never paints.
            _ = SampleCameraRawWhiteBalance(point);
            return;
        }
        if (cameraRawSamplesPointColor)
        {
            // Point Color's eyedropper, the Mac's next branch (EditorCanvas.swift:1748-1752): the click belongs
            // to the panel whether or not it found a pixel worth filing, so a miss never paints either.
            _ = SampleCameraRawPointColor(point);
            return;
        }
        if (cameraRawSamplesDefringe)
        {
            // And for the defringe picker one line later (EditorCanvas.swift:1753-1757), with the same rule: the
            // white-balance selector is asked first, as it is on the Mac, when both happen to be armed.
            _ = SampleCameraRawDefringe(point);
            return;
        }
        if (cameraRawDrawsGuide)
        {
            // Upright ▸ Draw Guides, next in the Mac's list (EditorCanvas.swift:1758-1761). The press only opens
            // a draft: the drag continues it and the release commits it, so a click without a pull lands as a
            // zero-length line that normalization then drops.
            _ = BeginCameraRawGuide(point);
            return;
        }
        if (cameraRawTargetsCurve || cameraRawTargetsMixer)
        {
            // Targeted Adjustment, next in the Mac's list (EditorCanvas.swift:1762-1766): the press belongs to
            // the panel even when it found no pixel to key on, so a miss on nothing never paints.
            _ = BeginCameraRawTargetedDrag(point);
            return;
        }

        if (tool == CanvasTool.ObjectSelect)
        {
            BeginObjectSelectionPointer(point, e.KeyModifiers);
            if (objectSelectionPress is not null) { CanvasStage.Focus(FocusState.Pointer); CanvasStage.CapturePointer(e.Pointer); }
            e.Handled = true; return;
        }

        if (tool == CanvasTool.Lasso)
        {
            if (!e.GetCurrentPoint((UIElement)sender).Properties.IsRightButtonPressed) BeginLasso(point, e.KeyModifiers);
            e.Handled = true;
            return;
        }

        if (tool == CanvasTool.Gradient)
        {
            if (!e.GetCurrentPoint((UIElement)sender).Properties.IsRightButtonPressed) BeginGradient(point);
            e.Handled = true;
            return;
        }

        if (tool == CanvasTool.Healing)
        {
            if (!e.GetCurrentPoint((UIElement)sender).Properties.IsRightButtonPressed) BeginHealing(point, e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift));
            e.Handled = true;
            return;
        }

        // Guide hit-test: within reach of a guide line the drag moves that guide. The Mac refuses the same
        // pickup when guides are hidden or locked (Guides.swift:157), so a line the user cannot see is not
        // nudged by a click that lands on it, and a locked guide passes the gesture on to the tool.
        if (HitGuide(point) is { } pickedGuide)
        {
            BeginExistingGuideAt(pickedGuide, StagePoint(e, CanvasStage));
            return; // guide drag owns the gesture
        }

        switch (tool)
        {
            case CanvasTool.Move:
                BeginTransformPointer(point, e.KeyModifiers);
                break;
            case CanvasTool.RectSelect:
            case CanvasTool.EllipseSelect:
                BeginMarqueeSelection(point, e.KeyModifiers);
                break;
            case CanvasTool.Crop:
                CacheSnapTargets(includeCenters: false);
                if (cropHasBox)
                {
                    var cropBox = CropTransform();
                    // A handle drag resizes; inside the frame moves it; outside starts a fresh one.
                    if (TransformDrag.HitTest(cropBox, point, reach: 10 / viewport.PointsPerPixel, rotationVisible: false)
                        is { Mode: TransformDragMode.Resize, Index: var handleIndex })
                    {
                        cropDragMode = CropDragMode.Resize;
                        cropResizeDrag = new TransformDragState(cropBox, point, TransformDragMode.Resize, handleIndex);
                        cropDragging = true;
                        break;
                    }
                    if (point.X >= cropDragRect.X && point.X <= cropDragRect.Right
                        && point.Y >= cropDragRect.Y && point.Y <= cropDragRect.Bottom)
                    {
                        cropDragMode = CropDragMode.Move;
                        cropDragStart = point;
                        cropDragOriginal = cropDragRect;
                        cropDragging = true;
                        break;
                    }
                }
                cropDragMode = CropDragMode.Create;
                cropDragStart = point;
                cropDragging = true;
                break;
            case CanvasTool.Shape:
                dragStart = point;
                shapePreviewBox = default;
                shapePreviewFrom = shapePreviewTo = null;
                break;
            case CanvasTool.Wand:
            {
                SelectWithWand(point, e.KeyModifiers);
                break;
            }
            case CanvasTool.Clone when e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Menu):
                cloneSource = point;
                cloneAlignedOffset = null;
                SetStatus(Loc.F("S_CloneSourceSet", point.X, point.Y));
                break;
            case CanvasTool.Clone:
            case CanvasTool.Blur:
                BeginPointerStroke(point, e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift));
                break;
            case CanvasTool.Text:
                // The Mac's beginTextGesture (:516-531): a press on existing text edits it where it is, and a
                // press anywhere else only marks the anchor — the release decides point text or a dragged box.
                if (document?.TextLayerAt(point) is { } spoken)
                {
                    CommitInlineText();
                    OpenInlineText(spoken);
                }
                else BeginTextBand(point);
                break;
            case CanvasTool.Brush:
            case CanvasTool.Eraser:
                BeginPointerStroke(point, e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift));
                break;
        }
    }

    private BrushSettings CurrentBrush => new(
        Radius: BrushSizeSlider.Value,
        Hardness: BrushHardnessSlider.Value / 100,
        Opacity: BrushOpacitySlider.Value / 100,
        Red: foregroundColor.R,
        Green: foregroundColor.G,
        Blue: foregroundColor.B);

    private void OnCanvasPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch) return;
        if (objectSelectionPress is not null) { MoveObjectSelectionPointer(DocumentPoint(e)); e.Handled = true; return; }
        if (cameraRawGuideDraft is not null) { ContinueCameraRawGuide(DocumentPoint(e)); e.Handled = true; return; }
        if (cameraRawDrag is not null) { DragCameraRawTargeted(DocumentPoint(e)); e.Handled = true; return; }
        if (pixelMove is not null) { MovePixelsTo(viewport.ToDocument(StagePoint(e, CanvasStage)), e.KeyModifiers); e.Handled = true; return; }
        if (selectionMove is not null)
        { var stage = StagePoint(e, CanvasStage); MoveSelectionTo(viewport.ToDocument(stage), e.KeyModifiers); TrackSelectionAutoscroll(stage, e.KeyModifiers); e.Handled = true; return; }
        if (tool == CanvasTool.Healing && healingPointerDown) { MoveHealing(DocumentPoint(e), e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift)); return; }
        if (tool == CanvasTool.Gradient && gradientDragHandle >= 0) { MoveGradient(DocumentPoint(e), e.KeyModifiers); return; }
        if (tool == CanvasTool.Lasso && lassoPoints is not null)
        {
            MoveLasso(DocumentPoint(e));
            return;
        }
        if (draggingGuideId is not null)
        {
            MoveRulerGuideTo(StagePoint(e, CanvasStage));
            return;
        }

        if (document is null) return;
        var point = DocumentPoint(e);
        switch (tool)
        {
            case CanvasTool.Move when transformPointerDraftActive:
                MoveTransformPointer(point, e.KeyModifiers);
                break;
            case CanvasTool.Move when transformDragActive && transformDrag is { }:
                // Applied once per render tick — see RenderPendingAsync. The pump is what
                // makes the tick run; without it the picture would freeze until release.
                pendingTransformPoint = point;
                pendingShift = e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift);
                pendingOption = e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control);
                PumpRender();
                break;

            case CanvasTool.Move when dragLast is { } last:
                pendingMoveDelta = new PointD((pendingMoveDelta?.X ?? 0) + point.X - last.X, (pendingMoveDelta?.Y ?? 0) + point.Y - last.Y);
                pendingMoveLayerId = SelectedLayer?.Id;
                // Ctrl drags freely, bypassing the snap targets — the macOS control-drag.
                pendingMoveFree = e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control);
                dragLast = point;
                PumpRender();
                break;

            case CanvasTool.Brush or CanvasTool.Eraser when pointerStroke is not null:
                AppendPointerStroke(point, e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift));
                break;
            case CanvasTool.RectSelect or CanvasTool.EllipseSelect when dragStart is { } selectionAnchor:
                ShowMarqueePreview(selectionAnchor, viewport.ToDocument(StagePoint(e, CanvasStage)), MarqueeSquare(e.KeyModifiers));
                TrackSelectionAutoscroll(StagePoint(e, CanvasStage), e.KeyModifiers);
                break;
            case CanvasTool.Text when textDragFrom is not null:
                MoveTextBand(point);
                break;

            case CanvasTool.Shape when dragStart is { } shapeAnchor:
            {
                // Preview only — the document changes once at release, so plain UI updates
                // here cost nothing per event.
                bool square = e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift);
                (shapePreviewBox, shapePreviewFrom, shapePreviewTo) = ShapeCommit(shapeAnchor, point, square);
                ShowShapePreview(shapePreviewBox, shapePreviewFrom, shapePreviewTo);
                break;
            }
            case CanvasTool.Clone or CanvasTool.Blur when pointerStroke is not null:
                AppendPointerStroke(point, e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift));
                break;
            case CanvasTool.Crop when cropDragging:
                // Preview only — Apply writes the document once, as on the Mac.
                cropDragRect = UpdateCropRect(point);
                ShowCropPreview(cropDragRect);
                UpdateCropSizeLabel();
                break;
        }
    }

    /// <summary>The document rectangle a layer currently covers, for dirty tracking.</summary>
    private RectI BoundsOf(ProjectLayerRecord layer)
    {
        var (x0, y0, x1, y1) = Resampler.BoundingRect(layer.Transform, document!.Snapshot.Manifest.Width, document.Snapshot.Manifest.Height);
        return new RectI(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }

    /// <summary>Snap targets never change during a drag: the canvas plus every other drawn
    /// layer's edges and center, computed once at pointer-down. The crop tool passes
    /// includeCenters: false — its edges snap to edges only, as on the Mac.</summary>
    private void CacheSnapTargets(bool includeCenters = true)
    {
        if (document is null) return;
        // View ▸ Snap off (⌘⇧;, CompositorApp.swift:136-137 → EditorSession.swift:304) makes the Mac's
        // alignmentSnapTargets answer with nothing at all (Guides.swift:248), which is what stops a drag from
        // being pulled out from under the pointer. Every consumer reads these two lists, so gating them here
        // is the whole switch rather than a check at each call site.
        if (!snapEnabled) { snapXs = snapYs = null; return; }
        var snapshot = LayerDisplaySnapshot!;
        var manifest = snapshot.Manifest;
        var byId = manifest.Layers.ToDictionary(record => record.Id, record => record);
        var order = DrawOrder.Resolve(manifest.Layers);
        var skip = SelectedLayer?.Id;
        var xs = new List<double>();
        var ys = new List<double>();
        // View ▸ Snap To ▸ Document Bounds (CompositorApp.swift:145-147 → Guides.swift:251-257): the canvas
        // edges, and their midpoints when the caller wants centers.
        if (snapToBounds)
        {
            xs.AddRange([0, manifest.Width]);
            ys.AddRange([0, manifest.Height]);
            if (includeCenters)
            {
                xs.Add(manifest.Width / 2.0);
                ys.Add(manifest.Height / 2.0);
            }
        }
        foreach (var otherId in order.Drawn)
        {
            if (!snapToLayers) break; // Snap To ▸ Layers (Guides.swift:259-273), the whole per-layer block
            if (otherId == skip || tool == CanvasTool.Move && transformInspectorEdit?.LayerIds.Contains(otherId) == true || !byId.TryGetValue(otherId, out var other)) continue;
            if (tool == CanvasTool.Move && snapshot.Images.ContainsKey(otherId))
            {
                // Guides.swift uses the full rotated geometry, rounded to document pixels,
                // including off-canvas edges. Render bounds clip and expand that geometry.
                var corners = TransformDrag.HandlePositions(other.Transform);
                double left = corners.Min(p => p.X), right = corners.Max(p => p.X);
                double top = corners.Min(p => p.Y), bottom = corners.Max(p => p.Y);
                xs.AddRange(new[] { left, (left + right) / 2, right }.Select(v => Math.Round(v, MidpointRounding.AwayFromZero)));
                ys.AddRange(new[] { top, (top + bottom) / 2, bottom }.Select(v => Math.Round(v, MidpointRounding.AwayFromZero)));
            }
            else if (tool != CanvasTool.Move)
            {
                var otherBounds = BoundsOf(other);
                xs.AddRange(includeCenters ? new double[] { otherBounds.X, otherBounds.X + otherBounds.Width / 2.0, otherBounds.X + otherBounds.Width }
                    : [otherBounds.X, otherBounds.X + otherBounds.Width]);
                ys.AddRange(includeCenters ? new double[] { otherBounds.Y, otherBounds.Y + otherBounds.Height / 2.0, otherBounds.Y + otherBounds.Height }
                    : [otherBounds.Y, otherBounds.Y + otherBounds.Height]);
            }
        }
        // View ▸ Grid is a snap source only while it is shown (Guides.swift:274-276: `snapToGrid, showsGrid`),
        // and the grid's own lines are every step, majors included.
        if (snapToGrid && showsGrid)
        {
            xs.AddRange(LayoutGrid.Lines(manifest.Width));
            ys.AddRange(LayoutGrid.Lines(manifest.Height));
        }
        // Mac Guides.swift:277-283 only feeds guides into the snap list when Snap To ▸ Guides is on and the
        // guides are shown: "hidden extras do not snap, matching Photoshop". Locking does not gate this — a
        // locked guide is visible and stays a target, it just cannot be moved.
        if (tool == CanvasTool.Move && showsGuides && snapToGuides && manifest.Guides is { } guides)
            foreach (var guide in guides)
                (guide.Axis == GuideAxis.Vertical ? xs : ys).Add(guide.Position);
        snapXs = xs;
        snapYs = ys;
    }

    private void MarkInteractionDirty(RectI rect, double grow)
    {
        int g = (int)Math.Ceiling(grow);
        var grown = new RectI(rect.X - g, rect.Y - g, rect.Width + g * 2, rect.Height + g * 2);
        interactionDirty = interactionDirty is { } current
            ? RectI.Union(current, grown)
            : grown;
    }

    /// <summary>
    /// Marks one layer's effect region dirty BEFORE a control-driven mutation (opacity,
    /// blend, adjustment sliders) — its PixelsChanged then renders that band instead of the
    /// whole canvas, which is what keeps slider drags at frame rate on large documents.
    /// Adjustment layers grow by their sampling halo, as the compositor's margin does.
    /// </summary>
    private void MarkLayerBandDirty(Guid layerId)
    {
        if (document is null) return;
        var layer = document.Snapshot.Manifest.Layers.FirstOrDefault(candidate => candidate.Id == layerId);
        if (layer is null) return;
        double halo = layer.Adjustment is { } adjustment
            ? AdjustmentEngine.SamplingMargin(adjustment) + 2
            : 2;
        MarkInteractionDirty(BoundsOf(layer), grow: halo);
    }

    private void ShowDragOrCropPreview(PointD start, PointD point)
    {
        double x = Math.Min(start.X, point.X);
        double y = Math.Min(start.Y, point.Y);
        double width = Math.Abs(point.X - start.X);
        double height = Math.Abs(point.Y - start.Y);
        if (tool == CanvasTool.Crop)
        {
            ShowCropPreview(new Rect(x, y, width, height));
        }
        else
        {
            ShowDragPreview(new Rect(x, y, width, height));
        }
    }

    private async void OnCanvasPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch) return;
        if (objectSelectionPress is not null)
        { EndObjectSelectionPointer(DocumentPoint(e)); CanvasStage.ReleasePointerCapture(e.Pointer); e.Handled = true; return; }
        if (document is null) return;
        if (cameraRawGuideDraft is not null)
        {
            // The release commits the line the drag last moved (EditorCanvas.swift:2026-2028); the endpoint is
            // whatever the final moved event carried, as on the Mac.
            // The release ends a targeted drag before anything else it handles, as the Mac's mouseUp
            // does (EditorCanvas.swift:2029), and the guide line the same gesture commits is filed first
            // (:2026-2028) — the two can be live together only through the panel, and the order is the Mac's.
            EndCameraRawTargetedDrag();
            _ = CommitCameraRawGuide();
            CanvasStage.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
            return;
        }
        if (pixelMove is not null) { _ = FinishPixelMoveAsync(); CanvasStage.ReleasePointerCapture(e.Pointer); e.Handled = true; return; }
        if (selectionMove is not null)
        { EndSelectionMove(click: true); e.Handled = true; return; }
        StopSelectionAutoscroll();
        var point = DocumentPoint(e);
        if (draggingGuideId is not null)
        {
            EndRulerGuideAt(StagePoint(e, CanvasStage));
            ((UIElement)sender).ReleasePointerCapture(e.Pointer);
            e.Handled = true;
            return;
        }
        if (pointerStroke is not null)
        {
            // Queue the final segment and close before capture-lost can try to finish again.
            FinishPointerStroke(point, shift: e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift));
            ((UIElement)sender).ReleasePointerCapture(e.Pointer);
            e.Handled = true;
            return;
        }
        if (tool == CanvasTool.Healing) healingPointerDown = false;
        if (!transformPointerDraftActive) ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        if (tool == CanvasTool.Healing) { e.Handled = true; await FinishHealingAsync(point, e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift)); return; }
        if (tool == CanvasTool.Gradient) { EndGradientDrag(); e.Handled = true; return; }
        if (tool == CanvasTool.Lasso)
        {
            if (lassoPoints is not null && !lassoPolygonal)
            {
                MoveLasso(point);
                FinishLasso();
            }
            lassoDrawing = false;
            e.Handled = true;
            return;
        }
        // The drag's region has been streaming in cached-placement frames; apply any interaction that arrived
        // with the release and make one final full-quality pass cover it.
        if (placementDragActive)
        {
            if (transformPointerDraftActive)
            {
                pendingTransformPoint = point;
                pendingShift = e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift);
                pendingOption = e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Menu);
                pendingMoveFree = e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control);
            }
            FinishPlacementDrag();
            ((UIElement)sender).ReleasePointerCapture(e.Pointer);
            e.Handled = true;
            return;
        }
        switch (tool)
        {
            case CanvasTool.RectSelect or CanvasTool.EllipseSelect when dragStart is { } start:
            {
                DragPreview.Visibility = Visibility.Collapsed;
                ShapePreviewEllipse.Visibility = Visibility.Collapsed;
                dragStart = null;
                FinishMarqueeSelection(start, viewport.ToDocument(StagePoint(e, CanvasStage)), MarqueeSquare(e.KeyModifiers));
                break;
            }

            case CanvasTool.Crop when cropDragging:
            {
                cropDragging = false;
                if (cropDragRect.Width >= 1 && cropDragRect.Height >= 1)
                {
                    cropHasBox = true;
                    cropRectangle = new RectI((int)cropDragRect.X, (int)cropDragRect.Y,
                        (int)cropDragRect.Width, (int)cropDragRect.Height);
                    ShowCropPreview(cropDragRect);
                    UpdateCropSizeLabel();
                    SetStatus(Loc.F("S_CropLive", cropDragRect.Width, cropDragRect.Height));
                }
                else
                {
                    ClearCropBox();
                }
                break;
            }

            case CanvasTool.Text when textDragFrom is not null:
                FinishTextGesture();
                break;

            case CanvasTool.Shape when dragStart is { } shapeAnchor:
            {
                HideShapePreview();
                dragStart = null;
                // A drag leaves its preview behind; a click goes straight here and commits
                // from the release's own modifiers (a plain click draws a Line dot, as on
                // the Mac — rectangles and ellipses need at least one pixel).
                if (shapePreviewBox.Width == 0 && shapePreviewFrom is null)
                {
                    (shapePreviewBox, shapePreviewFrom, shapePreviewTo) = ShapeCommit(
                        shapeAnchor, point,
                        e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift));
                }
                CommitShape();
                break;
            }
        }
    }

    private void OnPaletteColorChanged(ColorPicker sender, ColorChangedEventArgs args) => ApplyPaletteColor(args.NewColor);

    /// <summary>
    /// Everything a report from the picker does: remember which chip was being picked, restyle the chips, and
    /// paint any text that is open in the editor. Split out because a probe cannot build the event args and a
    /// test that called only the inner half left this line unguarded — dropping it kept every group green.
    /// </summary>
    private void ApplyPaletteColor(Windows.UI.Color picked)
    {
        // Nothing is written to the palette until the picker reports it; the chips follow.
        if (palettePickerBackground) backgroundColor = picked;
        else foregroundColor = picked;
        RefreshPaletteChips();
        ApplyPaletteColorToDraft();
    }

    // The chip fills reuse two brushes (a picker drag recolors them at pointer rate) — set
    // once here, since the XAML declares plain Black/White fills.
    private readonly SolidColorBrush foregroundChipBrush = new(Microsoft.UI.Colors.Black);
    private readonly SolidColorBrush backgroundChipBrush = new(Microsoft.UI.Colors.White);

    /// <summary>The overlapped chips and their fills, mirroring the Mac's ColorPaletteControls.</summary>
    private void RefreshPaletteChips()
    {
        foregroundChipBrush.Color = foregroundColor;
        backgroundChipBrush.Color = backgroundColor;
        ForegroundChipFill.Fill = foregroundChipBrush;
        BackgroundChipFill.Fill = backgroundChipBrush;
        RefreshQuickBrushTip();
        RefreshQuickTextControls();
        RefreshGradientDraft();
    }

    private void OnPaletteChipClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string target })
        {
            palettePickerBackground = target == "background";
        }
        BrushColorPicker.Color = palettePickerBackground ? backgroundColor : foregroundColor;
        Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase.ShowAttachedFlyout(PaletteGrid);
    }

    private void OnSwapPaletteColors(object sender, RoutedEventArgs e)
    {
        (foregroundColor, backgroundColor) = (backgroundColor, foregroundColor);
        RefreshPaletteChips();
    }

    private void OnResetPaletteColors(object sender, RoutedEventArgs e)
    {
        foregroundColor = Microsoft.UI.Colors.Black;
        backgroundColor = Microsoft.UI.Colors.White;
        RefreshPaletteChips();
    }

    // VK_OEM_PLUS and VK_OEM_MINUS: the main-row = / + and - / _ keys. The VirtualKey projection gives
    // neither a name (which is also why the brush brackets are matched by code below), so the chords are
    // built from the codes.
    internal const int OemPlusCode = ShortcutVK.OemPlusCode, OemMinusCode = ShortcutVK.OemMinusCode,
        OpenBracketCode = ShortcutVK.OpenBracketCode, CloseBracketCode = ShortcutVK.CloseBracketCode,
        SemicolonCode = ShortcutVK.SemicolonCode, QuoteCode = ShortcutVK.QuoteCode;

    // The macOS app binds zoom to ⌘= and ⌘-, and layer stepping to ⌘] and ⌘[. XAML's VirtualKey parser has
    // no name for these main-row keys (which is also why the brush brackets below are matched by code), so
    // a menu declared in markup ends up numeric-pad only — leaving a laptop without keyboard zoom, and the
    // layer commands unbound. They go into the same live collection the shortcut dialog reads from, so the
    // promise and the binding stay one fact rather than two.
    private void AddCodeOnlyAccelerators()
    {
        ZoomInItem.KeyboardAccelerators.Add(new Microsoft.UI.Xaml.Input.KeyboardAccelerator
        {
            Modifiers = Windows.System.VirtualKeyModifiers.Control,
            Key = (Windows.System.VirtualKey)OemPlusCode,
        });
        ZoomInItem.KeyboardAccelerators.Add(new Microsoft.UI.Xaml.Input.KeyboardAccelerator
        {
            Modifiers = Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift,
            Key = (Windows.System.VirtualKey)OemPlusCode,
        });
        ZoomOutItem.KeyboardAccelerators.Add(new Microsoft.UI.Xaml.Input.KeyboardAccelerator
        {
            Modifiers = Windows.System.VirtualKeyModifiers.Control,
            Key = (Windows.System.VirtualKey)OemMinusCode,
        });
        RaiseMenuItem.KeyboardAccelerators.Add(new Microsoft.UI.Xaml.Input.KeyboardAccelerator
        {
            Modifiers = Windows.System.VirtualKeyModifiers.Control,
            Key = (Windows.System.VirtualKey)CloseBracketCode,
        });
        LowerMenuItem.KeyboardAccelerators.Add(new Microsoft.UI.Xaml.Input.KeyboardAccelerator
        {
            Modifiers = Windows.System.VirtualKeyModifiers.Control,
            Key = (Windows.System.VirtualKey)OpenBracketCode,
        });
        // The Mac's three view switches hang off ⌘;, ⌘⇧; and ⌘⌥; (CompositorApp.swift:128-129, :136-137,
        // :150-151); the semicolon is another key the VirtualKey projection leaves unnamed.
        GuidesToggle.KeyboardAccelerators.Add(new Microsoft.UI.Xaml.Input.KeyboardAccelerator
        {
            Modifiers = Windows.System.VirtualKeyModifiers.Control,
            Key = (Windows.System.VirtualKey)SemicolonCode,
        });
        SnapToggle.KeyboardAccelerators.Add(new Microsoft.UI.Xaml.Input.KeyboardAccelerator
        {
            Modifiers = Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift,
            Key = (Windows.System.VirtualKey)SemicolonCode,
        });
        LockGuidesToggle.KeyboardAccelerators.Add(new Microsoft.UI.Xaml.Input.KeyboardAccelerator
        {
            Modifiers = Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Menu,
            Key = (Windows.System.VirtualKey)SemicolonCode,
        });
        // ⌘' for View ▸ Grid (CompositorApp.swift:126-127); the quote is the fourth main-row key the
        // VirtualKey projection leaves unnamed.
        GridToggle.KeyboardAccelerators.Add(new Microsoft.UI.Xaml.Input.KeyboardAccelerator
        {
            Modifiers = Windows.System.VirtualKeyModifiers.Control,
            Key = (Windows.System.VirtualKey)QuoteCode,
        });
    }

    // The macOS app asks the responder chain ("is the first responder an NSText?") before any shortcut
    // runs. A modifier-less accelerator here resolves before the focused field sees the character, so
    // typing a layer name or a text draft would swap the palette or resize the brush instead.
    private bool TypingInField => renamingLayer || textDialogOpen || FloatingPanelHoldsFocus
        || FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox or RichEditBox;

    // The plain X / D keys, as the macOS canvas handles them — the chip tooltips promise them.
    private void OnSwapColorsAccelerator(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
        Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        if (TypingInField) return; // leave the keystroke to the field
        (foregroundColor, backgroundColor) = (backgroundColor, foregroundColor);
        RefreshPaletteChips();
        args.Handled = true;
    }

    private void OnResetColorsAccelerator(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
        Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        if (TypingInField) return;
        foregroundColor = Microsoft.UI.Colors.Black;
        backgroundColor = Microsoft.UI.Colors.White;
        RefreshPaletteChips();
        args.Handled = true;
    }

    private void OnPaintTargetChanged(object sender, RoutedEventArgs e)
    {
        if (suppressPanelEvents || syncingTransform || PaintMaskCheck.IsChecked == paintOnMask) return;
        ChangePaintTarget(PaintMaskCheck.IsChecked == true);
    }

    private void OnApplyCrop(object sender, RoutedEventArgs e)
    {
        if (document is null || !cropHasBox)
        {
            SetStatus(Loc.T("S_CropFirst"));
            return;
        }
        var rect = CropSnapped(cropDragRect);
        if (document.SetCanvasBounds((int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height))
        {
            UpdateDocSizeLabel();
            SetStatus(Loc.F("S_Cropped", (int)rect.Width, (int)rect.Height));
        }
        else
        {
            Report(Loc.T("S_CropRange"), null);
        }
        ClearCropBox();
    }

    private void OnCancelCrop(object sender, RoutedEventArgs e)
    {
        CancelDragPreview();
        SetStatus(Loc.T("S_CropCancelled"));
    }

    /// <summary>
    /// The Mac's bracket shortcuts on the brush tip: [ shrinks and ] grows by a fifth
    /// (always at least one unit), Shift+[ / Shift+] step hardness by a quarter. Only while
    /// the brush or eraser is active.
    /// </summary>
    private void OnContentKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (IsGradientFinishing)
        {
            if (IsCancelPress(args.Key)) CancelGradientDraft();
            args.Handled = true; return;
        }
        // A floating panel is in this tree, not outside it like a ContentDialog was, so the canvas has to be
        // told to stand down while the panel is the responder: Space belongs to the focused button there,
        // and a letter typed into a field must not resize the brush.
        if (FloatingPanelHoldsFocus) return;
        if (HandleObjectSelectionKey(args)) return;
        if (HandlePixelMoveKey(args)) return;
        if (HandleSelectionMoveKey(args)) return;
        if (HandleTransformKey(args)) return;
        if (HandleNavigationKey(args)) return;
        if (HandleWorkspaceToolKey(args)) return;
        if (HandleLassoKey(args)) return;
        if (HandleOpacityKey(args.Key, SelectionKeyModifiers() | (args.KeyStatus.IsMenuKeyDown ? Windows.System.VirtualKeyModifiers.Menu : 0),
            TypingInField || args.OriginalSource is TextBox or RichEditBox))
        { args.Handled = true; return; }
        if (HandleGradientKey(args)) return;
        if (HandleHealingKey(args)) return;
        // Ctrl+[ and Ctrl+] are the Mac's layer step, bound here as a menu accelerator (C05c); a pressed
        // Control belongs to that command and must not also resize the brush.
        if (KeyHeld(Windows.System.VirtualKey.Control) || TypingInField) return;
        // The brackets are rebindable, so the pressed chord is translated before anything is judged: the guard
        // used to read the raw key, which meant a bracket moved onto another key simply stopped working.
        var (tip, swallowed) = CanvasTranslate(new ShortcutChord(args.Key,
            (args.KeyStatus.IsMenuKeyDown ? Windows.System.VirtualKeyModifiers.Menu : 0)
            | (KeyHeld(Windows.System.VirtualKey.Shift) ? Windows.System.VirtualKeyModifiers.Shift : 0)));
        if (swallowed) { args.Handled = true; return; }
        if (!StepBrushTip(tip.Key, tip.Modifiers)) return;
        args.Handled = true;
    }

    /// <summary>
    /// The Mac's bracket shortcuts on the brush tip: [ shrinks and ] grows by a fifth
    /// (always at least one unit), Alt+[ / Alt+] step hardness by a quarter. Only while a brush, eraser or
    /// healing tool is active. Kept as its own call with the chord as an argument, because no probe can
    /// synthesize the press itself — the same seam the tool letters use.
    /// </summary>
    internal bool StepBrushTip(Windows.System.VirtualKey key, Windows.System.VirtualKeyModifiers modifiers)
    {
        int bracket = (int)key;
        if (bracket is not (OpenBracketCode or CloseBracketCode)) return false; // the projection names neither key
        if (tool is not (CanvasTool.Brush or CanvasTool.Eraser or CanvasTool.Healing)) return false;
        bool increase = bracket == CloseBracketCode;
        if (modifiers.HasFlag(Windows.System.VirtualKeyModifiers.Menu))
        {
            double step = (increase ? 1 : -1) * 25.0;
            BrushHardnessSlider.Value = Math.Clamp(BrushHardnessSlider.Value + step, 0, 100);
        }
        else
        {
            double current = BrushSizeSlider.Value;
            double stepped = increase
                ? Math.Max(current + 1, Math.Round(current * 1.2))
                : Math.Min(current - 1, Math.Round(current / 1.2));
            BrushSizeSlider.Value = Math.Clamp(stepped, BrushSizeSlider.Minimum, BrushSizeSlider.Maximum);
        }
        return true;
    }

    private void OnShowPixelGrid(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleMenuFlyoutItem toggle)
        {
            showPixelGrid = toggle.IsChecked;
            WriteFlag(PixelGridPreference, showPixelGrid);
        }
        CanvasControl.Invalidate();
    }

    private void OnDeselect(object sender, RoutedEventArgs e)
    {
        if (!CanEditSelection) return;
        CancelLasso();
        QueueSelectionWork((_, _) => null, false, Loc.T("S_Deselected"));
    }

    // Expand / Contract / Feather reshape the session selection through an amount prompt,
    // as the Mac's Select > Modify does: 1–500 pixels, feather 1–250, last amount remembered.

    // The Mac keeps one remembered amount per operation (EditorSession.swift:274 feather 2, :312-313 expand and
    // contract 1), because the last feather you typed is no hint about the next expand.
    private readonly Dictionary<SelectionModification, int> lastSelectionAmounts = new()
    {
        [SelectionModification.Expand] = 1, [SelectionModification.Contract] = 1, [SelectionModification.Feather] = 2,
    };

    private async void OnExpandSelection(object sender, RoutedEventArgs e)
    {
        await PromptSelectionModificationAsync(SelectionModification.Expand, 500);
    }

    private async void OnContractSelection(object sender, RoutedEventArgs e)
    {
        await PromptSelectionModificationAsync(SelectionModification.Contract, 500);
    }

    private async void OnFeatherSelection(object sender, RoutedEventArgs e)
    {
        await PromptSelectionModificationAsync(SelectionModification.Feather, 250);
    }

    // Copy / Cut / Paste take the active layer's pixels through the document's own clipboard
    // (the selection clips them, or the whole canvas without one) and mirror the PNG to the
    // system clipboard with an id tag, as the Mac's store() does. Paste trusts the internal
    // clipboard only while that id still matches — anything else pastes the system PNG centered.

    private const string ClipIdFormat = "com.compositor.clip-id";
    private Guid? systemClipId;

    private async void OnCopyPixels(object sender, RoutedEventArgs e)
    {
        if (!CanEditLayerCommands) return;
        if (document is null || SelectedLayer is not { } layer) return;
        var copied = document.CopySelection(layer.Id);
        if (copied is null) { SetStatus(Loc.F("S_NoPixelsToCopy", layer.Name)); return; }
        SetStatus(await WriteSystemClipboardAsync(copied)
            ? Loc.F("S_CopiedPixels", $"{copied.Pixels.Width} × {copied.Pixels.Height}")
            : Loc.T("S_SystemClipboardFailed"));
    }

    private async void OnCutPixels(object sender, RoutedEventArgs e)
    {
        if (!CanEditLayerCommands) return;
        if (document is null || SelectedLayer is not { } layer) return;
        var cut = document.CutSelection(layer.Id);
        if (cut is null) { SetStatus(Loc.F("S_NoPixelsToCopy", layer.Name)); return; }
        SetStatus(await WriteSystemClipboardAsync(cut)
            ? Loc.F("S_CutPixels", $"{cut.Pixels.Width} × {cut.Pixels.Height}")
            : Loc.T("S_SystemClipboardFailed"));
    }

    private async void OnCopyMerged(object sender, RoutedEventArgs e)
    {
        if (!CanEditLayerCommands) return;
        if (document is null) return;
        var merged = document.CopyMergedSelection();
        if (merged is null) { SetStatus(Loc.T("S_NothingToGroup")); return; }
        await WriteSystemClipboardAsync(merged);
        SetStatus(Loc.F("S_Merged", $"{merged.Pixels.Width} × {merged.Pixels.Height}"));
    }

    /// <summary>Puts the copied PNG and its id on the system clipboard through the OLE
    /// (WinForms) clipboard, as the Mac's store(). False keeps the copy internal-only.</summary>
    private async Task<bool> WriteSystemClipboardAsync(EditorDocument.ClipboardPixels clip)
    {
        try
        {
            var png = await WicImaging.EncodePngAsync(clip.Pixels);
            using var image = new System.Drawing.Bitmap(new MemoryStream(png));
            var data = new System.Windows.Forms.DataObject();
            data.SetImage(image);
            data.SetData(ClipIdFormat, clip.Id.ToString());
            System.Windows.Forms.Clipboard.SetDataObject(data, true);
            systemClipId = clip.Id;
            return true;
        }
        catch (Exception ex)
        {
            systemClipId = null;
            App.Log($"system clipboard write failed: {ex.Message}");
            return false;
        }
    }

    private async void OnPastePixels(object sender, RoutedEventArgs e)
    {
        if (!CanEditLayerCommands) return;
        if (document is null) return;
        bool hasBitmap = false;
        Guid? clipboardId = null;
        DataPackageView? content = null;
        try
        {
            content = Clipboard.GetContent();
            hasBitmap = content.Contains(StandardDataFormats.Bitmap);
            if (content.AvailableFormats.Contains(ClipIdFormat)
                && await content.GetDataAsync(ClipIdFormat) is Windows.Storage.Streams.IRandomAccessStream { } idStream)
            {
                using var idMemory = new MemoryStream();
                idStream.AsStreamForRead().CopyTo(idMemory);
                if (Guid.TryParse(System.Text.Encoding.UTF8.GetString(idMemory.ToArray()), out var parsed)) clipboardId = parsed;
            }
        }
        catch (Exception ex)
        {
            App.Log($"system clipboard read failed: {ex.Message}");
        }

        // The id proves the system clipboard still holds what we copied: paste back in place.
        if (clipboardId is { } id && id == systemClipId && document.PasteClipboard() is { } own)
        {
            SetStatus(Loc.F("S_PastedPixels", own.Name));
            return;
        }
        if (hasBitmap && content is not null)
        {
            try
            {
                var reference = await content.GetBitmapAsync();
                using var stream = await reference.OpenReadAsync();
                using var memory = new MemoryStream();
                stream.AsStreamForRead().CopyTo(memory);
                var surface = await WicImaging.DecodeSurfaceAsync(memory.ToArray());
                var pasted = document.PasteExternal(surface);
                SetStatus(pasted is null
                    ? Loc.T("S_PasteTooLarge")
                    : Loc.F("S_PastedExternal", surface.Width, surface.Height));
                return;
            }
            catch (Exception ex)
            {
                App.Log($"system clipboard decode failed: {ex.Message}");
            }
        }
        // Degraded mode: the system clipboard is unusable, the internal copy still is.
        var internalPasted = document.PasteClipboard();
        SetStatus(internalPasted is null ? Loc.T("S_ClipboardEmpty") : Loc.F("S_PastedPixels", internalPasted.Name));
    }

    private void OnLayerViaCopy(object sender, RoutedEventArgs e)
    {
        if (!CanEditLayerCommands) return;
        if (document is null || SelectedLayer is not { } layer) return;
        if (document.ActiveSelection is null) { OnDuplicateLayers(sender, e); return; }
        var created = document.LayerViaCopy(layer.Id);
        if (created is not null)
        {
            selectedLayerId = created.Id; selectedLayerIds.Clear(); selectedLayerIds.Add(created.Id);
            paintOnMask = false; PaintMaskCheck.IsChecked = false; RefreshLayerPanel();
        }
        SetStatus(created is null
            ? Loc.F("S_NoPixelsToCopy", layer.Name)
            : Loc.F("S_LayerViaCopy", created.Name));
    }

    private WandSettings wandSettings = new();

    // Clone Stamp session state: Option/Alt-click sets the source; the offset is taken when
    // a stroke starts and, when aligned, kept across strokes (CloneStamp.swift's model).
    private PointD? cloneSource;
    private PointD? cloneAlignedOffset;
    private double warpStrength = 0.5;

    private void OnWandToleranceChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => ReadWandOptions();

    private void OnWandOptionChanged(object sender, RoutedEventArgs e) => ReadWandOptions();

    private void ReadWandOptions()
    {
        if (WandToleranceBox is null || WandSampleCombo is null || WandContiguousCheck is null) return;
        wandSettings = wandSettings with
        {
            Tolerance = double.IsNaN(WandToleranceBox.Value) ? 32 : (int)Math.Clamp(WandToleranceBox.Value, 0, 255),
            SampleRadius = WandSampleCombo.SelectedIndex switch { 1 => 1, 2 => 2, _ => 0 },
            Contiguous = WandContiguousCheck.IsChecked == true,
            SampleAllLayers = WandAllLayersCheck.IsChecked == true,
        };
    }

    // Color Range: a non-modal panel — the canvas clicks sample the cached composite
    // (click = replace, Shift = add, Alt = exclude), the live selection re-applies on every
    // change, Apply keeps it, Cancel restores what there was. Foreground-color buttons stay
    // as a keyboard-free alternative to clicking the canvas.

    private readonly List<byte[]> colorRangeInclude = new();
    private readonly List<byte[]> colorRangeExclude = new();
    private Surface? colorRangeSample;
    private sealed class ColorRangeSession(EditorDocument owner)
    {
        public readonly EditorDocument Owner = owner;
        public Selection? Original;
        public bool Ready, Finishing, Failed;
        public long Request;
        public Task? Work;
        // Every slider move supersedes the previous full-document match, so the older worker is
        // cancelled rather than left to finish behind the newest one.
        public CancellationTokenSource Cancellation = new();
    }
    private ColorRangeSession? colorRangeSession;
    private bool colorRangeSampling => colorRangeSession is not null;

    private async void OnColorRange(object sender, RoutedEventArgs e)
    {
        if (!CanEditSelection || document is not { } owner) return;
        var session = new ColorRangeSession(owner); colorRangeSession = session;
        RefreshTransformAvailability(true);
        var preceding = selectionWorkResult;
        colorRangeInclude.Clear(); colorRangeExclude.Clear(); colorRangeSample = null; ColorRangePreviewImage.Source = null;
        ColorRangeFuzzinessSlider.Value = 40; ColorRangeInvertCheck.IsChecked = false;
        RefreshColorRangeCounts();
        ColorRangeControls.Visibility = Visibility.Visible;
        try
        {
            if (preceding is not null) await preceding;
            if (!ReferenceEquals(colorRangeSession, session) || !ReferenceEquals(document, owner) || windowClosed) return;
            session.Original = owner.ActiveSelection; session.Ready = true;
            var input = SelectionSampleInput.Capture(owner.Snapshot);
            SetStatus(Loc.T("S_SelectionWorking"));
            var sample = await Task.Run(() => input.Render(true, null));
            if (!ReferenceEquals(colorRangeSession, session) || !ReferenceEquals(document, owner) || windowClosed) return;
            colorRangeSample = sample; SetStatus(Loc.T("S_ColorRangeSampling"));
            if (colorRangeInclude.Count > 0) ApplyColorRangePreview();
        }
        catch (Exception ex) { App.Log($"Color Range sample failed: {ex}"); if (ReferenceEquals(colorRangeSession, session)) CancelColorRange(); }
    }

    private void SampleColorRangePoint(PointD point, Windows.System.VirtualKeyModifiers modifiers)
    {
        if (colorRangeSample is null) return;
        var color = EditorDocument.ColorAt(colorRangeSample, point);
        if (color is null) return;
        byte[] triplet = [color.Value.R, color.Value.G, color.Value.B];
        bool add = modifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift);
        bool remove = modifiers.HasFlag(Windows.System.VirtualKeyModifiers.Menu); // Alt, the Mac's Option
        if (remove) colorRangeExclude.Add(triplet);
        else if (add) colorRangeInclude.Add(triplet);
        else
        {
            colorRangeInclude.Clear();
            colorRangeExclude.Clear();
            colorRangeInclude.Add(triplet);
        }
        ApplyColorRangePreview();
        RefreshColorRangeCounts();
    }

    private void OnColorRangeForeground(object sender, RoutedEventArgs e)
    {
        byte[] triplet = [foregroundColor.R, foregroundColor.G, foregroundColor.B];
        if (sender is FrameworkElement { Tag: "exclude" }) colorRangeExclude.Add(triplet);
        else colorRangeInclude.Add(triplet);
        ApplyColorRangePreview();
        RefreshColorRangeCounts();
    }

    private void OnColorRangeClear(object sender, RoutedEventArgs e)
    {
        colorRangeInclude.Clear();
        colorRangeExclude.Clear();
        ApplyColorRangePreview();
        RefreshColorRangeCounts();
    }

    private void OnColorRangePreviewChanged(object sender, RoutedEventArgs e) => ApplyColorRangePreview();

    private void OnColorRangeSliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) => ApplyColorRangePreview();

    private void ApplyColorRangePreview()
    {
        if (colorRangeSession is not { Ready: true, Finishing: false } session || colorRangeSample is not { } sample) return;
        long request = ++session.Request;
        session.Cancellation.Cancel();
        var cancellation = session.Cancellation = new CancellationTokenSource();
        var include = colorRangeInclude.SelectMany(color => color).ToArray();
        var exclude = colorRangeExclude.SelectMany(color => color).ToArray();
        int fuzziness = (int)Math.Round(ColorRangeFuzzinessSlider.Value); bool invert = ColorRangeInvertCheck.IsChecked == true;
        session.Work = UpdateColorRangePreviewAsync(session, request, sample, include, exclude, fuzziness, invert, cancellation.Token);
    }

    private async Task UpdateColorRangePreviewAsync(ColorRangeSession session, long request, Surface sample,
        byte[] include, byte[] exclude, int fuzziness, bool invert, CancellationToken cancellation = default)
    {
        try
        {
            var result = await Task.Run(() => SelectionSampling.ColorRange(sample, include, exclude, fuzziness, invert, cancellation: cancellation), cancellation);
            if (windowClosed || !ReferenceEquals(colorRangeSession, session) || !ReferenceEquals(document, session.Owner) || request != session.Request) return;
            session.Failed = false;
            var selection = include.Length == 0 ? session.Original : result.Selection;
            publishingSelection = true;
            try { session.Owner.SetSelection(selection); } finally { publishingSelection = false; }
            RefreshColorRangePreviewImage(result.Preview);
            SetStatus(selection is null ? Loc.T("S_ColorRangeEmpty") : Loc.F("S_ColorRangeSelected", selection.Bounds.Width * selection.Bounds.Height));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A superseded preview is normal while the slider moves; it is not a failure to report.
        }
        catch (Exception ex)
        {
            App.Log($"Color Range preview failed: {ex}");
            if (ReferenceEquals(colorRangeSession, session) && request == session.Request)
            { session.Failed = true; SetStatus(Loc.T("S_SelectionFailed")); }
        }
    }

    /// <summary>The worker makes both full coverage and the small gray preview from one mask.</summary>
    private void RefreshColorRangePreviewImage(Surface? preview)
    {
        if (preview is null) { ColorRangePreviewImage.Source = null; return; }
        var bitmap = new WriteableBitmap(preview.Width, preview.Height);
        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(preview.Pixels, 0, preview.Pixels.Length);
        }
        bitmap.Invalidate();
        ColorRangePreviewImage.Source = bitmap;
    }

    private void RefreshColorRangeCounts()
    {
        if (ColorRangeCountsText is null) return;
        ColorRangeCountsText.Text = Loc.F("L_ColorRangeIncludeCount", colorRangeInclude.Count)
            + "  " + Loc.F("L_ColorRangeExcludeCount", colorRangeExclude.Count);
    }

    private async void OnColorRangeApply(object sender, RoutedEventArgs e)
    {
        if (colorRangeSession is not { Ready: true, Finishing: false } session || colorRangeSample is null) return;
        session.Finishing = true;
        if (session.Work is { } work) await work;
        if (windowClosed || !ReferenceEquals(colorRangeSession, session) || !ReferenceEquals(document, session.Owner)) return;
        var selection = session.Owner.ActiveSelection;
        CloseColorRangePanel();
        publishingSelection = true;
        try
        {
            session.Owner.SetSelection(session.Original);
            if (colorRangeInclude.Count > 0 && !session.Failed) session.Owner.SetSelectionUndoable(selection);
        }
        finally { publishingSelection = false; }
    }

    private void OnColorRangeCancel(object sender, RoutedEventArgs e)
    {
        CancelColorRange();
        SetStatus(Loc.T("S_ColorRangeCancelled"));
    }

    private void CloseColorRangePanel()
    {
        colorRangeSession = null;
        colorRangeSample = null;
        ColorRangeControls.Visibility = Visibility.Collapsed;
        if (!windowClosed) RefreshTransformAvailability(true);
    }

    private void CancelColorRange(bool restore = true)
    {
        var session = colorRangeSession; if (session is null) return;
        // Stop the running match before the panel goes away: its result must not land later.
        session.Cancellation.Cancel();
        CloseColorRangePanel();
        if (restore && session.Ready && ReferenceEquals(document, session.Owner))
        {
            publishingSelection = true;
            try { session.Owner.SetSelection(session.Original); } finally { publishingSelection = false; }
        }
    }

    private void OnInvertPixels(object sender, RoutedEventArgs e)
    {
        if (!CanEditLayerCommands) return;
        if (document is null || SelectedLayer is not { } layer) return;
        if (document.InvertPixels(layer.Id)) SetStatus(Loc.F("S_PixelsInverted", layer.Name));
        else SetStatus(Loc.F("S_NoPixelsToInvert", layer.Name));
    }

    private void OnBlurModeChanged(object sender, SelectionChangedEventArgs e)
    {
        // The active stroke keeps the mode captured at pointer-down until release.
    }

    private void OnInvertMask(object sender, RoutedEventArgs e)
    {
        if (!CanEditLayerCommands) return;
        if (document is null || SelectedLayer is not { } layer) return;
        if (document.InvertPixels(layer.Id, invertMask: true)) SetStatus(Loc.F("S_MaskInverted", layer.Name));
        else SetStatus(Loc.F("S_NoMask", layer.Name));
    }

    private void OnFillForeground(object sender, RoutedEventArgs e) => FillSelection(withBackground: false);

    private void OnFillBackground(object sender, RoutedEventArgs e) => FillSelection(withBackground: true);

    /// <summary>
    /// Edit &gt; Fill: the palette color the targeted plane wants (the mask takes gray, like the
    /// macOS `CGColor(gray: value.red)`), clipped by the selection. No accelerator for the
    /// background variant: Ctrl+Backspace is the text-editing "delete previous word" chord in
    /// WinUI text boxes, and a menu-wide accelerator would eat it while a layer name is open.
    /// </summary>
    private void FillSelection(bool withBackground)
    {
        if (!CanEditLayerCommands) return;
        if (document is null || SelectedLayer is not { } layer) return;
        var color = withBackground ? backgroundColor : foregroundColor;
        if (paintOnMask)
        {
            if (document.FillMask(layer.Id, color.R)) SetStatus(Loc.F("S_MaskFilled", layer.Name));
            else if (!document.Snapshot.Masks.ContainsKey(layer.Id)) SetStatus(Loc.F("S_NoMask", layer.Name));
            return;
        }
        if (document.FillPixels(layer.Id, color.R, color.G, color.B)) SetStatus(Loc.F("S_Filled", layer.Name));
        else SetStatus(Loc.F("S_NoPixelsToInvert", layer.Name));
    }

    private async void OnClearSelectionPixels(object sender, RoutedEventArgs e) => await DeletePlaneTargetAsync(clearSelection: true);

    private void OnFlipLayerHorizontal(object sender, RoutedEventArgs e) => FlipLayers(horizontally: true);

    private void OnFlipLayerVertical(object sender, RoutedEventArgs e) => FlipLayers(horizontally: false);

    /// <summary>
    /// Layer &gt; Flip: one selected layer mirrors about its own middle, several about the middle of
    /// the box around them (macOS LayerFlip.swift:24). The flip is placement only — no pixels are
    /// resampled — so the layer keeps its surface and the undo is one step.
    /// </summary>
    private void FlipLayers(bool horizontally)
    {
        if (!CanEditLayerCommands) return;
        if (document is null || SelectedLayer is not { } layer) return;
        var ids = selectedLayerIds.Count > 1 ? selectedLayerIds.ToList() : [layer.Id];
        if (document.FlipLayers(ids, horizontally))
            SetStatus(Loc.F(horizontally ? "S_LayersFlippedHorizontal" : "S_LayersFlippedVertical", ids.Count));
        else SetStatus(Loc.T("S_FlipRefused"));
    }

    private void OnFlipCanvasHorizontal(object sender, RoutedEventArgs e) => FlipCanvas(horizontally: true);

    private void OnFlipCanvasVertical(object sender, RoutedEventArgs e) => FlipCanvas(horizontally: false);

    /// <summary>
    /// Flip Canvas: layers, placed masks, the selection and the guides all mirror across the canvas
    /// middle in one step (macOS LayerFlip.swift:55).
    /// </summary>
    private void FlipCanvas(bool horizontally)
    {
        if (!CanEditLayerCommands) return;
        if (document is null) return;
        if (document.FlipCanvas(horizontally))
            SetStatus(Loc.T(horizontally ? "S_CanvasFlippedHorizontal" : "S_CanvasFlippedVertical"));
        else SetStatus(Loc.T("S_FlipRefused"));
    }

    private void OnSelectSubject(object sender, RoutedEventArgs e)
    {
        if (document is null) return;
        var subject = document.SelectSubject();
        if (subject is null)
        {
            SetStatus(AiSegmentation.Registered is null ? Loc.T("OS_SubjectPending") : Loc.T("S_AiNoSubject"));
            return;
        }
        if (CanEditSelection) document.SetSelectionUndoable(subject);
        SetStatus(Loc.T("S_SubjectSelected"));
    }

    private void OnRemoveSubject(object sender, RoutedEventArgs e)
    {
        if (!CanEditLayerCommands) return;
        if (document is null) return;
        var subject = document.SelectSubject();
        if (subject is null)
        {
            SetStatus(AiSegmentation.Registered is null ? Loc.T("OS_SubjectPending") : Loc.T("S_AiNoSubject"));
            return;
        }
        // Remove Subject: clear the subject's pixels on the active layer, one undoable step.
        var layer = SelectedLayer;
        if (layer is null) return;
        if (document.CutSelectionMasked(layer.Id, subject)) SetStatus(Loc.T("S_SubjectRemoved"));
        else SetStatus(Loc.F("S_NoPixelsToInvert", layer.Name));
    }

    private void OnContentFill(object sender, RoutedEventArgs e)
    {
        if (!CanEditLayerCommands) return;
        if (document is null || SelectedLayer is not { } layer) return;
        if (document.ActiveSelection is null)
        {
            SetStatus(Loc.T("S_SelectionMaskNeedsSelection"));
            return;
        }
        if (document.ContentFillSelection(layer.Id)) SetStatus(Loc.F("S_ContentFilled", layer.Name));
        else SetStatus(Loc.T("S_ContentFillNoDonors"));
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        if (!CanEditSelection || document is null) return;
        var manifest = document.Snapshot.Manifest;
        int width = manifest.Width, height = manifest.Height;
        QueueSelectionWork((_, _) => Selection.Rectangle(width, height, 0, 0, width, height), false, Loc.T("S_SelectAll"));
    }

    private void OnInvertSelection(object sender, RoutedEventArgs e)
    {
        if (!CanEditSelection || document is null) return;
        var manifest = document.Snapshot.Manifest;
        int width = manifest.Width, height = manifest.Height;
        QueueSelectionWork((current, token) => SelectionCommands.Invert(current, width, height, token), true, Loc.T("S_Inverted"));
    }

    private void OnAddMask(object sender, RoutedEventArgs e) => CreateLayerMask(revealing: true, useSelection: false);

    private void OnAddHideAllMask(object sender, RoutedEventArgs e) => CreateLayerMask(revealing: false, useSelection: false);

    private void OnRevealSelectionMask(object sender, RoutedEventArgs e) => AddSelectionMask(revealing: true);

    private void OnHideSelectionMask(object sender, RoutedEventArgs e) => AddSelectionMask(revealing: false);

    private void AddSelectionMask(bool revealing)
    {
        if (!CanEditLayerCommands) return;
        if (document is null || SelectedLayer is not { } layer) return;
        if (document.ActiveSelection is null)
        {
            SetStatus(Loc.T("S_SelectionMaskNeedsSelection"));
            return;
        }
        CreateLayerMask(revealing, useSelection: true);
    }

    private void OnRemoveMask(object sender, RoutedEventArgs e)
    {
        if (!CanEditLayerCommands) return;
        if (document is null || SelectedLayer is not { } layer) return;
        if (document.RemoveMask(layer.Id)) SetStatus(Loc.F("S_MaskRemoved", layer.Name));
        else SetStatus(Loc.F("S_NoMask", layer.Name));
    }

    // --- Adjustment layers ---

    private void OnAddAdjustment(object sender, RoutedEventArgs e)
    {
        if (!CanEditLayerCommands) return;
        if (document is null) return;
        if (sender is FrameworkElement { Tag: string name }
            && Enum.TryParse(name, out AdjustmentKind kind))
        {
            var layer = document.AddAdjustment(kind);
            selectedLayerId = layer.Id;
            SetStatus(Loc.F("S_AdjustmentAdded", Loc.Name("Adjustment", name)));
        }
    }

    // --- Menu and panel handlers ---

    private async void OnNew(object sender, RoutedEventArgs e) => await NewProjectAsync();

    private async Task NewProjectAsync() => await NewProjectTabAsync();

    private async void OnOpen(object sender, RoutedEventArgs e)
        => await OpenSourceFromPickerAsync();

    private async void OnOpenProjectFolder(object sender, RoutedEventArgs e)
        => await OpenProjectFromPickerAsync();

    private async Task OpenProjectFromPickerAsync()
    {
        var request = BeginProjectOpen();
        try
        {
            var folder = await PickFolderAsync();
            if (folder is not null && IsCurrentProjectOpen(request)) await LoadAndOpenProjectAsync(folder.Path, request);
        }
        finally { EndProjectOpen(request); }
    }

    public async Task OpenProjectAsync(string path)
    {
        var request = BeginProjectOpen();
        try { await LoadAndOpenProjectAsync(path, request); }
        finally { EndProjectOpen(request); }
    }

    private sealed record ProjectOpenRequest(EditorDocument? Owner, CancellationTokenSource Cancellation)
    {
        internal CancellationToken Token { get; } = Cancellation.Token;
    }

    private ProjectOpenRequest? projectOpenRequest;

    private ProjectOpenRequest BeginProjectOpen()
    {
        CancelProjectOpen();
        return projectOpenRequest = new(document, new CancellationTokenSource());
    }

    private void CancelProjectOpen()
    {
        projectOpenRequest?.Cancellation.Cancel();
        projectOpenRequest = null;
    }

    private void EndProjectOpen(ProjectOpenRequest request)
    {
        if (ReferenceEquals(projectOpenRequest, request)) projectOpenRequest = null;
        request.Cancellation.Dispose();
    }

    private bool IsCurrentProjectOpen(ProjectOpenRequest request) => !windowClosed && !request.Token.IsCancellationRequested
        && ReferenceEquals(projectOpenRequest, request) && ReferenceEquals(document, request.Owner);

    private async Task LoadAndOpenProjectAsync(string path, ProjectOpenRequest request)
    {
        if (!IsCurrentProjectOpen(request)) return;
        if (!ProjectStore.IsProjectDirectory(path))
        {
            SetStatus(Loc.T("S_NotProject"));
            return;
        }
        EditorDocument? openedDocument = null;
        try
        {
            var loaded = Stopwatch.StartNew();
            var snapshot = await LoadProjectForOpenAsync(path, request.Token);
            if (!IsCurrentProjectOpen(request)) return;
            // Decode first: a failed/cancelled picker or load never touches the open tabs.
            App.Log($"startup: project load {loaded.ElapsedMilliseconds} ms");
            // The original's loadProject: a path already on the strip is selected instead of loaded
            // twice, and a new project lands on its own tab. Nothing is ever asked to discard,
            // because nothing is thrown away; a draft or other mid-flight edit bars the move, the
            // Mac's canSwitch at the command's door rather than the old replace-with-prompt.
            if (workspace.OpenedTab(path) is { } existing)
            {
                // The window follows its own switch rules, which restore the parked view: an open of a
                // visible project does not refit or reset what the person had framed.
                if (!SwitchProjectTab(existing.Id)) return;
                openedDocument = existing.Document;
            }
            else
            {
                if (!CanSwitchProjectTabs) return;
                openedDocument = new EditorDocument(snapshot);
                openedDocument.MarkSaved(path);
                var leaving = workspace.Current;
                var added = workspace.AdoptOpened(openedDocument, path);
                if (leaving.Document is not null) parkOutgoing(leaving);   // a launch placeholder has nothing to park
                rebind(added);
                FitToWindow();
            }
            RememberRecentProject(path);
            SetStatus(Loc.F("S_Opened", System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar))));
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { }
        catch (Exception ex)
        {
#if DEBUG
            projectOpenFailureProbe?.Invoke(ex);
#endif
            if (!windowClosed && (IsCurrentProjectOpen(request) || openedDocument is not null && ReferenceEquals(document, openedDocument)))
                Report(Loc.F("S_CannotOpen", System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar))), ex);
        }
    }

    private static Task<ProjectSnapshot> LoadProjectForOpenAsync(string path, CancellationToken token)
    {
#if DEBUG
        if (projectOpenLoadProbe is { } probe) return probe(path, token);
#endif
        return ProjectStore.LoadAsync(path, token);
    }

    // --- Recent projects (the Mac inherits this menu from the system's document support) ---

    private static string RecentFilePath => System.IO.Path.Combine(PreferencesDirectory, "recent.txt");

    private readonly List<string> recentProjects = new();

    private void LoadRecentProjects()
    {
        try
        {
            if (File.Exists(RecentFilePath))
            {
                recentProjects.Clear();
                recentProjects.AddRange(File.ReadAllLines(RecentFilePath)
                    .Select(line => line.Trim())
                    .Where(line => line.Length > 0)
                    .Take(10));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A missing, locked, or access-denied list just means an empty menu.
            App.Log($"recent list unreadable: {ex.Message}");
        }
        RebuildRecentMenu();
    }

    private void RememberRecentProject(string path)
    {
        path = path.TrimEnd(System.IO.Path.DirectorySeparatorChar);
        recentProjects.RemoveAll(entry => string.Equals(entry, path, StringComparison.OrdinalIgnoreCase));
        recentProjects.Insert(0, path);
        if (recentProjects.Count > 10) recentProjects.RemoveRange(10, recentProjects.Count - 10);
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(RecentFilePath)!);
            File.WriteAllLines(RecentFilePath, recentProjects);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The menu still works this session; the list just won't persist.
            App.Log($"recent list unwritable: {ex.Message}");
        }
        RebuildRecentMenu();
    }

    private void RebuildRecentMenu()
    {
        // A closing WinUI submenu can retain its presenter. Keep the reused Clear command attached.
        for (int index = OpenRecentMenu.Items.Count - 1; index >= 0; index--)
            if (!ReferenceEquals(OpenRecentMenu.Items[index], ClearRecentMenuItem)) OpenRecentMenu.Items.RemoveAt(index);
        int insertion = 0;
        foreach (var path in recentProjects)
        {
            bool exists = Directory.Exists(path);
            string name = System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar));
            var item = new MenuFlyoutItem { Text = name, IsEnabled = exists };
            ToolTipService.SetToolTip(item, path);
            if (exists)
            {
                var captured = path;
                item.Click += async (_, _) => await OpenProjectAsync(captured);
            }
            OpenRecentMenu.Items.Insert(insertion++, item);
            // A remove affordance per entry: drops it from the menu and recent.txt without
            // touching the project on disk.
            var remove = new MenuFlyoutItem { Text = "✕", Tag = path };
            remove.Click += (_, _) => ForgetRecentProject(path);
            OpenRecentMenu.Items.Insert(insertion++, remove);
        }
        if (recentProjects.Count == 0)
        {
            OpenRecentMenu.Items.Insert(insertion++, new MenuFlyoutItem { Text = Loc.T("M_NoRecentProjects"), IsEnabled = false });
        }
        // The Mac's Clear Menu at the foot of the list, behind a divider (:58-66), kept by every
        // rebuild and disabled while there is nothing to clear (:64-65).
        if (ClearRecentMenuItem is not null)
        {
            ClearRecentMenuItem.IsEnabled = recentProjects.Count > 0;
            OpenRecentMenu.Items.Insert(insertion, new MenuFlyoutSeparator());
            if (!OpenRecentMenu.Items.Contains(ClearRecentMenuItem)) OpenRecentMenu.Items.Add(ClearRecentMenuItem);
        }
    }

    /// <summary>File > Open Recent > Clear Menu, the Mac's item (:64-65): empties the list and the
    /// file — the projects on disk are untouched, and like the Mac's item it carries no keyboard
    /// shortcut. The menu is rebuilt from the list, so the item's enabled state is decided the Mac's
    /// way: nothing to clear, nothing to click.</summary>
    private void OnClearRecentProjects(object sender, RoutedEventArgs e) => ClearRecentProjects();

    private void ClearRecentProjects()
    {
        recentProjects.Clear();
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(RecentFilePath)!);
            File.WriteAllLines(RecentFilePath, System.Array.Empty<string>());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The menu is empty this session; the stale file retries on the next change.
            App.Log($"recent list unwritable: {ex.Message}");
        }
        RebuildRecentMenu();
    }

    /// <summary>Drops one entry from the recent list (and recent.txt); the project on disk stays.</summary>
    private void ForgetRecentProject(string path)
    {
        recentProjects.RemoveAll(entry => string.Equals(entry, path, StringComparison.OrdinalIgnoreCase));
        try
        {
            File.WriteAllLines(RecentFilePath, recentProjects);
        }
        catch (IOException)
        {
            // The menu is already updated; persistence retries on the next change.
        }
        RebuildRecentMenu();
    }

    /// <summary>File > Close Project, the Mac's saveItem entry (CompositorApp.swift:85-91): it closes
    /// THIS window's frontmost project — the controller resolves its own tab and hands it to
    /// workspace.close (:307-318) — which is this window's selected tab, and the seam behind it already
    /// answers the unsaved-changes question. Ctrl+W: the bare W stays the Wand tool, because the canvas
    /// key table returns false while Control is down (MainWindow.Workspace.cs HandleWorkspaceToolKey).</summary>
    private async void OnCloseProject(object sender, RoutedEventArgs e) => await CloseProjectTabAsync(workspace.SelectedId);

    private async void OnSave(object sender, RoutedEventArgs e) => _ = await SaveNow();

    private async void OnSaveAs(object sender, RoutedEventArgs e) => _ = await SaveAsNow();

    private readonly SemaphoreSlim projectSaveGate = new(1, 1);
    private EditorDocument? projectSaveOwner;

    // True means the current version is saved, so a close/open confirmation may proceed.
    private Task<bool> SaveNow() => SaveProjectAsync(saveAs: false);
    private Task<bool> SaveAsNow() => SaveProjectAsync(saveAs: true);

    private async Task<bool> SaveProjectAsync(bool saveAs, string? destination = null)
    {
        if (IsFileImporting || document is not { } owner) return false;
        var retired = documentRetired.Task;
        bool Current() => !windowClosed && !retired.IsCompleted && ReferenceEquals(document, owner);
        await projectSaveGate.WaitAsync();
        string? path = null;
        try
        {
            if (!Current() || IsFileImporting) return false;
            CommitInlineText();
            if (owner.ActiveTextDraft is not null) return false;
            FinishPointerStroke();
            if (placementDragActive) FinishPlacementDrag();
            await FinishTransformBeforeOutputAsync();
            if (!Current() || !HealingAllowsOutput() || !await ApplyGradientDraftAsync() || !Current() || IsFileImporting) return false;
            path = destination ?? (saveAs ? null : projectPath);
            if (path is null)
            {
                var folder = await PickFolderAsync();
                if (folder is null || !Current()) return false;
                path = ProjectStore.IsProjectDirectory(folder.Path) ? folder.Path : System.IO.Path.Combine(folder.Path, "Untitled.comp");
            }
            var capture = owner.CaptureSave();
            var unchanged = owner.UnchangedAssetNames(path);
            byte[]? digest = !saveAs && string.Equals(projectPath, path, StringComparison.OrdinalIgnoreCase) ? owner.SavedDiskDigest : null;
            if (digest is null && ProjectStore.HasPackage(path))
                App.Log($"save to {System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar))}"
                    + " has no acknowledged on-disk baseline: this write is not conflict-checked.", LogLevel.Diagnostic);
            projectSaveOwner = owner;
            await WriteProjectSaveAsync(capture.Snapshot, path, unchanged, digest);
            bool currentVersionSaved = owner.CompleteSave(capture, path);
            if (!Current()) return false;
            currentVersionSaved &= owner.ActiveTextDraft is null && pointerStroke is null && healingStroke is null
                && gradientEdit is null && pixelMove is null && floatingTransform is null && transformInspectorEdit is null
                && !renderInputs.HasPending;
            if (!string.Equals(projectPath, path, StringComparison.OrdinalIgnoreCase))
            {
                watcher?.Dispose(); projectPath = path;
                watcher = new ProjectWatcher(path); watcher.ProjectChanged += OnExternalChange;
            }
            watcher?.MarkOwnSave();
            RememberRecentProject(path);
            SetStatus(!currentVersionSaved ? Loc.T("S_SavedWithNewChanges") : saveAs ? Loc.F("S_SavedTo", path) : Loc.T("S_Saved"));
            UpdateTitle();
            return currentVersionSaved;
        }
        catch (ProjectConflictException ex)
        {
            App.Log($"Save refused by conflict guard: {ex.Message}");
            if (Current()) { SetStatus(Loc.T("S_SaveConflict")); watcher?.RecheckPendingChange(); }
            return false;
        }
        catch (Exception ex)
        {
            if (Current()) Report(Loc.F("S_CannotSaveTo", path ?? Loc.T("M_Untitled")), ex);
            return false;
        }
        finally { projectSaveOwner = null; projectSaveGate.Release(); }
    }

    private static Task WriteProjectSaveAsync(ProjectSnapshot snapshot, string path, IReadOnlySet<string>? unchanged, byte[]? digest = null)
    {
#if DEBUG
        if (projectSaveWriteProbe is { } probe) return probe(snapshot, path, unchanged, digest);
#endif
        return ProjectStore.SaveAsync(snapshot, path, unchanged, expectedDiskDigest: digest);
    }

    /// <summary>
    /// True while an edit or a background flow is mid-flight on this window. The original tests the same
    /// set before switching tabs (its session canSwitch/canStartProjectOperation): a dirty document alone
    /// never blocks — the tab keeps its unsaved work — but nothing may park a half-dragged marquee, an
    /// open filter dialog or an in-flight import out of sight.
    /// </summary>
    private bool ProjectEditsInFlight => document is { ActiveTextDraft: not null }
        || ObjectSelectionActive
        || timelineExporting
        || gradientEdit is not null || transformInspectorEdit is not null || floatingTransform is not null
        || pixelMove is not null || floatingDuplicate is not null || pointerStroke is not null || placementDragActive
        || healingStroke is not null || ditherOperation is not null || !pixelNudgeWork.IsCompleted
        || renderInputs.HasPending || IsFileImporting || projectSaveGate.CurrentCount == 0
        // The Mac's canSwitch keeps working through its whole canStartProjectOperation, which ends in
        // filterEdit == nil and levels == nil (ProjectWorkspace.swift:27-31): a filter sheet, the selection
        // amount sheet, a color-range pick or an open dialog REFUSES the move rather than parking the
        // session out of sight. The port's sheets hold their session until their own Finish path runs,
        // so a stranded sheet would edit a parked document through the window's live handlers.
        || tonalPreview is not null || vignettePreview is not null || lensPreview is not null
        || addNoisePreview is not null || gaussianPreview is not null || motionBlurPreview is not null || bloomPreview is not null || ditherPreview is not null
        || FloatingPanelIsOpen(CameraRawPanelKey) || FloatingPanelIsOpen("selectionAmount")
        || colorRangeSession is not null || projectDialogDepth > 0;

    /// <summary>
    /// One filter sheet at a time, the Mac's guard (Filters.swift:481: filterEdit == nil, and the menu
    /// greys every Filter command through the same while one is open): each sheet froze the layer's
    /// pixels when it opened, and a second sheet freezing them again would publish onto a picture the
    /// first one is rewriting. The refusal lands as a status line — the port's answer to the beep — and
    /// the open sheet is left exactly as it was.
    /// The sheet is counted by its PANEL on screen, not by its session field: a commit retired by a
    /// document swap takes its panel down while the blocked finish still holds the field, and the Mac
    /// calls that filterEdit nil — the edit is over for the user. A sheet re-asking for ITS OWN panel
    /// keeps its separate door (FloatingPanelIsFinishing), which reopens the committing panel rather
    /// than starting a second edit.
    /// </summary>
    internal bool AnotherFilterSheetOpen => FloatingPanelIsOpen("tonalContrast")
        || FloatingPanelIsOpen("vignette") || FloatingPanelIsOpen("lensCorrection")
        || FloatingPanelIsOpen("gaussianBlur") || FloatingPanelIsOpen("motionBlur")
        || FloatingPanelIsOpen("bloomGlow") || FloatingPanelIsOpen("addNoiseFilter")
        || FloatingPanelIsOpen("filter") || FloatingPanelIsOpen(CameraRawPanelKey);

    private void RefuseSecondFilterSheet() => SetStatus(Loc.T("S_FilterEditOpen"));

    /// <summary>
    /// True when switching away is safe. A dirty document asks first — Save runs the normal
    /// save pipeline (a failed or cancelled save aborts the switch), Don't Save discards,
    /// Cancel keeps everything as it was — the Mac's close confirmation.
    /// </summary>
    private bool NeedsProjectCloseConfirmation => ProjectEditsInFlight || document is { IsDirty: true };

    private async Task<bool> ConfirmDiscardChanges()
    {
        if (IsFileImporting) return false;
        var requestedOwner = document;
        var retired = documentRetired.Task;
        bool Current() => !windowClosed && !retired.IsCompleted && ReferenceEquals(document, requestedOwner);
        // A close must not terminate an asset write that is still in progress. Release before
        // asking: choosing Save below uses the same gate.
        await projectSaveGate.WaitAsync();
        projectSaveGate.Release();
        if (!Current() || IsFileImporting) return false;
        if (requestedOwner?.ActiveTextDraft is { Style.IsValid: false }) return false;
        CommitInlineText();
        if (requestedOwner?.ActiveTextDraft is not null) return false;
        FinishPointerStroke();
        if (placementDragActive) FinishPlacementDrag();
        await FinishTransformBeforeOutputAsync();
        if (!Current()) return false;
        CancelHealingStroke();
        if (!await ApplyGradientDraftAsync() || !Current()) return false;
#if DEBUG
        if (closeConfirmProbe is { } probe) return await probe() && Current();
#endif
        if (document is not { IsDirty: true }) return true;
        string name = projectPath is { } path
            ? System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar))
            : Loc.T("M_Untitled");
        var dialog = new ContentDialog
        {
            Title = Loc.T("Dlg_SaveChanges"),
            Content = Loc.F("Dlg_Unsaved", name),
            PrimaryButtonText = Loc.T("Dlg_Save"),
            SecondaryButtonText = Loc.T("Dlg_DontSave"),
            CloseButtonText = Loc.T("T_Dlg_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        var result = await dialog.ShowAsync();
        if (!Current()) return false;
        switch (result)
        {
            case ContentDialogResult.Primary: return await SaveNow() && Current();
            case ContentDialogResult.Secondary: return true;
            default: return false;
        }
    }

    private async void OnImportImage(object sender, RoutedEventArgs e)
    {
        if (document is not { } owner || IsFileImporting) return;
        var retired = documentRetired.Task;
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.Thumbnail,
            SettingsIdentifier = "imageImport",
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
        };
        foreach (var extension in ImportableExtensions) picker.FileTypeFilter.Add(extension);
        InitializeWithWindow(picker);
        try
        {
            var files = await picker.PickMultipleFilesAsync();
            if (files.Count == 0 || retired.IsCompleted || !ReferenceEquals(owner, document)) return;
            await ImportFilesAsync(owner, files.Select(file => file.Path).ToArray());
        }
        catch (Exception ex) { if (!retired.IsCompleted) Report(Loc.T("SC_ImportImage"), ex); }
    }

    // Trim: remembers the last options for the session; basedOn maps to the combo order.

    private EditorDocument.TrimOptions trimOptions = new();

    private async void OnExportPng(object sender, RoutedEventArgs e) => await ExportAsync(false);

    private async void OnExportJpeg(object sender, RoutedEventArgs e) => await ExportAsync(true);

    // The Mac remembers the last export's quality (jpegExportQuality) and matte color.
    private static double lastJpegQuality = 85;
    private static Windows.UI.Color lastJpegMatte = Microsoft.UI.Colors.White;

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private async void OnUndo(object sender, RoutedEventArgs e)
    {
        if (ObjectSelectionActive) { CancelObjectSelection(); return; }
        if (timelineExporting) return;
        StopTimelinePlayback(); CommitTimelineFields();
        EndOpacityEdit();
        if (IsDeletePending) return;
        if (IsFileImporting) return;
        var owner = document; var retired = documentRetired.Task;
        if (!await WaitForInteractionWorkAsync(pixelNudgeWork, retired) || windowClosed || !ReferenceEquals(document, owner)) return;
        if (IsFileImporting) return;
        if (pixelMove is not null) { CancelPixelMove(); return; }
        if (colorRangeSampling || selectionAmountPending) return;
        // While a text box is open on the canvas the words answer ⌘Z themselves (InlineTextEditor.swift:21-25),
        // so the document's history is not what a keystroke takes back — including when the box has nothing left.
        if (inlineEditor is not null) { UndoInlineText(); return; }
        EndSelectionMove(); StopSelectionAutoscroll();
        if (transformInspectorEdit is not null || floatingTransform is not null) { QueueTransformCancel(); return; }
        if (rulerGuideOwner is not null) { CancelNavigation(); return; }
        if (healingStroke is not null) { CancelHealingStroke(); return; }
        if (gradientEdit is not null) { CancelGradientDraft(); return; }
        FinishPointerStroke();
        QueueSelectionHistory(false);
    }

    private async void OnRedo(object sender, RoutedEventArgs e)
    {
        if (ObjectSelectionActive) { CancelObjectSelection(); return; }
        if (timelineExporting) return;
        StopTimelinePlayback(); CommitTimelineFields();
        EndOpacityEdit();
        if (IsDeletePending) return;
        if (IsFileImporting) return;
        var owner = document; var retired = documentRetired.Task;
        if (!await WaitForInteractionWorkAsync(pixelNudgeWork, retired) || windowClosed || !ReferenceEquals(document, owner)) return;
        if (IsFileImporting) return;
        if (pixelMove is not null) { CancelPixelMove(); return; }
        if (colorRangeSampling || selectionAmountPending) return;
        if (inlineEditor is not null) { RedoInlineText(); return; }
        EndSelectionMove(); StopSelectionAutoscroll();
        QueueTransformCancel();
        CancelNavigation();
        CancelHealingStroke();
        CancelGradientDraft();
        FinishPointerStroke();
        QueueSelectionHistory(true);
    }

    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        if (!CanEditLayerCommands) return;
        if (document?.AddFolder() == true) SetStatus(Loc.T("S_FolderAdded"));
    }

    private void OnGroupLayers(object sender, RoutedEventArgs e)
    {
        if (!CanEditLayerCommands) return;
        if (SelectedLayer is not { } layer) { SetStatus(Loc.T("S_SelectGroup")); return; }
        // The extended selection groups as one, roots computed in Core; a single selection
        // groups just that layer.
        var ids = selectedLayerIds.Count > 1 ? selectedLayerIds : new HashSet<Guid> { layer.Id };
        var folder = document?.GroupLayers(ids);
        SetStatus(folder is null
            ? Loc.T("S_NothingToGroup")
            : ids.Count > 1 ? Loc.F("S_GroupMany", folder.Name, ids.Count) : Loc.F("S_GroupOne", folder.Name, layer.Name));
    }

    private void OnMergeLayers(object sender, RoutedEventArgs e)
    {
        if (!CanEditLayerCommands) return;
        if (SelectedLayer is not { } layer) { SetStatus(Loc.T("S_SelectGroup")); return; }
        ProjectLayerRecord? merged;
        if (selectedLayerIds.Count > 1)
        {
            merged = document?.MergeLayers(selectedLayerIds);
        }
        else if (layer.IsGroupOrFalse)
        {
            merged = document?.MergeGroup(layer.Id);
        }
        else
        {
            merged = document?.MergeDown(layer.Id);
        }
        SetStatus(merged is null ? Loc.T("S_NothingToGroup") : Loc.F("S_Merged", merged.Name));
    }

    /// <summary>The merge command names its mode: layers together, down, or a whole group.</summary>
    private void UpdateMergeMenuText()
    {
        if (MergeMenuItem is null) return;
        MergeMenuItem.Text = selectedLayerIds.Count > 1 ? Loc.T("M_Merge/Text")
            : SelectedLayer is { IsGroupOrFalse: true } ? Loc.T("M_MergeGroup")
            : Loc.T("M_MergeDown");
    }

    private void UpdateMaskLinkMenuText()
    {
        if (MaskLinkMenuItem is null) return;
        MaskLinkMenuItem.Text = SelectedLayer is { MaskFile: not null, MaskLinked: false }
            ? Loc.T("M_LinkMask")
            : Loc.T("M_UnlinkMask");
    }

    private void UpdateMaskEnabledMenuText()
    {
        if (MaskEnabledMenuItem is null) return;
        MaskEnabledMenuItem.Text = SelectedLayer is { MaskFile: not null, MaskEnabled: false }
            ? Loc.T("M_EnableMask")
            : Loc.T("M_DisableMask");
    }

    private void UpdateClippingMaskMenuText()
    {
        if (ClippingMaskMenuItem is null) return;
        ClippingMaskMenuItem.Text = SelectedLayer is { MaskSourceId: not null }
            ? Loc.T("M_ReleaseClipping")
            : Loc.T("M_CreateClipping");
    }

    private void OnToggleClippingMask(object sender, RoutedEventArgs e)
    {
        if (!CanEditLayerCommands) return;
        if (document is null || SelectedLayer is not { } layer) return;
        if (document.ToggleClippingMask(layer.Id))
        {
            UpdateClippingMaskMenuText();
            SetStatus(Loc.F(layer.MaskSourceId is null ? "S_ClippingReleased" : "S_ClippingCreated", layer.Name));
        }
        else
        {
            SetStatus(Loc.T("S_ClippingRefused"));
        }
    }

    private void OnToggleMaskLink(object sender, RoutedEventArgs e)
    {
        if (!CanEditLayerCommands) return;
        if (document is null || SelectedLayer is not { } layer) return;
        bool linking = layer.MaskLinked == false;
        if (document.SetMaskLinked(layer.Id, linking)) SetStatus(Loc.F(linking ? "S_MaskLinked" : "S_MaskUnlinked", layer.Name));
    }

    private void OnToggleMaskEnabled(object sender, RoutedEventArgs e)
    {
        if (!CanEditLayerCommands) return;
        if (document is null || SelectedLayer is not { } layer) return;
        bool enabling = layer.MaskEnabled == false;
        if (document.SetMaskEnabled(layer.Id, enabling)) SetStatus(Loc.F(enabling ? "S_MaskEnabled" : "S_MaskDisabled", layer.Name));
    }

    private void OnDuplicateLayers(object sender, RoutedEventArgs e)
    {
        if (!CanEditLayerCommands || document is null) return;
        if (SelectedLayer is not { } layer) { SetStatus(Loc.T("S_SelectGroup")); return; }
        var ids = selectedLayerIds.Append(layer.Id).ToHashSet();
        var copies = document.DuplicateLayers(ids, out var mapping);
        if (copies is { Count: > 0 })
        {
            var copiedIds = copies.Select(l => l.Id).ToHashSet(); selectedLayerIds.Clear();
            selectedLayerIds.UnionWith(copies.Where(l => l.ParentId is null || !copiedIds.Contains(l.ParentId.Value)).Select(l => l.Id));
            selectedLayerId = document!.Snapshot.Manifest.ActiveLayerId; paintOnMask = false; PaintMaskCheck.IsChecked = false;
            foreach (var pair in mapping) if (collapsedGroups.Contains(pair.Key)) collapsedGroups.Add(pair.Value);
            if (selectedLayerIds.Count > 1 && SelectedLayer?.ParentId is { } parent) collapsedGroups.Remove(parent);
            RefreshLayerPanel();
        }
        SetStatus(copies is { Count: > 0 } ? Loc.F("S_Duplicated", copies.Count) : Loc.T("S_NothingToGroup"));
    }

    private void OnMoveOutOfFolder(object sender, RoutedEventArgs e)
    {
        if (!CanEditLayerCommands) return;
        if (SelectedLayer is not { } layer) { SetStatus(Loc.T("S_SelectGroup")); return; }
        var moved = document is not null && document.MoveOutOfFolder(layer.Id);
        SetStatus(moved ? Loc.T("S_MovedOut") : Loc.T("S_SelectGroup"));
    }

    private void OnUngroupLayer(object sender, RoutedEventArgs e)
    {
        if (!CanEditLayerCommands) return;
        if (SelectedLayer is not { } layer) { SetStatus(Loc.T("S_SelectUngroup")); return; }
        bool ungrouped = document is { } doc && doc.Ungroup(layer.Id);
        SetStatus(ungrouped ? Loc.T("S_Ungrouped") : Loc.T("S_SelectUngroup"));
    }

    private void OnRaiseLayer(object sender, RoutedEventArgs e)
    {
        if (!CanEditLayerCommands) return;
        var layer = SelectedLayer;
        if (layer is null) return;
        if (document!.MoveLayerWithinSiblings(layer.Id, towardTop: true)) SetStatus(Loc.F("S_Raised", layer.Name));
    }

    private void OnLowerLayer(object sender, RoutedEventArgs e)
    {
        if (!CanEditLayerCommands) return;
        var layer = SelectedLayer;
        if (layer is null) return;
        if (document!.MoveLayerWithinSiblings(layer.Id, towardTop: false)) SetStatus(Loc.F("S_Lowered", layer.Name));
    }

    private async void OnDeleteLayer(object sender, RoutedEventArgs e) => await DeleteLayerOrMaskAsync();

    private async void OnLayerSelected(object sender, SelectionChangedEventArgs e)
    {
        if (suppressPanelEvents) return;
        StopTimelinePlayback(); CommitTimelineFields();
        RetireBlendPreview();
        EndOpacityEdit();
        effectSelection = null;
        long request = ++layerSelectionRequest;
        var ids = LayerList.SelectedItems.OfType<ListViewItem>().Select(item => ((LayerRow)item.Tag).Layer.Id).ToHashSet();
        Guid? targetId = layerSelectionPointerTarget is { } pointed && ids.Contains(pointed) ? pointed
            : e.AddedItems.OfType<ListViewItem>().Select(item => (Guid?)((LayerRow)item.Tag).Layer.Id).LastOrDefault();
        layerSelectionPointerTarget = null;
        targetId ??= selectedLayerId is { } current && ids.Contains(current) ? current
            : layerItems.Select(grid => ((LayerRow)grid.Tag).Layer.Id).Where(ids.Contains).Select(id => (Guid?)id).FirstOrDefault();
        // #37: the Mac closes the effects sheet when the layer list changes (ContentView.swift:234-241), and
        // closing it is what hands the layer back the pixels the sheet opened on. With the sheet non-modal that
        // has to happen here, or a sheet for one layer sits over the document while another is being picked.
        CloseFloatingPanel("effects", commit: false);
        if (IsFileImporting) { RefreshLayerPanel(); return; }
        if (document is { } owner)
        {
            if (targetId is not null && floatingTransform?.Edit.FloatingLayerId == targetId && ids.Count == 1) return;
            var strokeAtEntry = pointerStroke;   // see OnToolChanged: the switch retires the gesture that
                                                  // was open when it was dispatched, not one that starts
                                                  // while it waits for the render worker
            await FinishTransformBeforeOutputAsync();
            if (request != layerSelectionRequest || IsFileImporting || !ReferenceEquals(document, owner)) return;
            EndSelectionMove(); StopSelectionAutoscroll();
            if (ReferenceEquals(pointerStroke, strokeAtEntry)) FinishPointerStroke();
            if (placementDragActive) FinishPlacementDrag();
            CancelHealingStroke();
            if (!await ApplyGradientDraftAsync() || request != layerSelectionRequest || windowClosed || !ReferenceEquals(document, owner)) return;
            QueueCanvasInput(owner, () =>
            {
                if (request != layerSelectionRequest || !ReferenceEquals(document, owner)) return;
                var live = owner.Snapshot.Manifest.Layers.Select(layer => layer.Id).ToHashSet();
                ids.IntersectWith(live);
                selectedLayerIds.Clear(); selectedLayerIds.UnionWith(ids);
                selectedLayerId = targetId is { } primary && ids.Contains(primary) ? primary
                    : layerItems.Select(grid => ((LayerRow)grid.Tag).Layer.Id).Where(ids.Contains).Select(id => (Guid?)id).FirstOrDefault();
                if (ids.Count != 1 || owner.Snapshot.Manifest.ActiveLayerId != selectedLayerId)
                { paintOnMask = false; PaintMaskCheck.IsChecked = false; }
                selectingLayer = true;
                timelineLayer = selectedLayerId;
                try { owner.SetActiveLayer(selectedLayerId); }
                finally { selectingLayer = false; }
                if (selectedLayerIds.Count > 1) SetStatus(Loc.F("S_MultiSelect", selectedLayerIds.Count));
                RefreshLayerPanel();
            });
        }
    }

    private bool syncingImageSize; // image-size dialog box echo suppression

    private void OnVisibleChanged(object sender, RoutedEventArgs e)
    {
        if (!CanEditLayerCommands) return;
        if (suppressPanelEvents || document is null) return;
        var layer = SelectedLayer;
        if (layer is null) return;
        document.SetVisible(layer.Id, VisibleCheck.IsChecked == true);
    }

    private void OnAddVerticalGuide(object sender, RoutedEventArgs e)
    {
        if (document is null) return;
        // Mac addGuide runs behind canEditGuides (Guides.swift:236-242), so a locked document refuses a new
        // guide from the menu exactly as it refuses one from the ruler — and it says so instead of silently
        // doing nothing.
        if (locksGuides) { SetStatus(Loc.T("S_GuidesLocked")); return; }
        var guide = document!.AddGuide(GuideAxis.Vertical, document.Snapshot.Manifest.Width / 2.0);
        SetGuidesVisible(true); // Mac addGuide turns guides on so the command is visible (Guides.swift:236-242)
        selectedLayerId = guide.Id; // not a layer id, but keeps focus semantics stable
        SetStatus(Loc.T("S_GuideV"));
    }

    private void OnAddHorizontalGuide(object sender, RoutedEventArgs e)
    {
        if (document is null) return;
        if (locksGuides) { SetStatus(Loc.T("S_GuidesLocked")); return; }
        var guide = document!.AddGuide(GuideAxis.Horizontal, document.Snapshot.Manifest.Height / 2.0);
        SetGuidesVisible(true);
        selectedLayerId = guide.Id;
        SetStatus(Loc.T("S_GuideH"));
    }

    private void OnClearGuides(object sender, RoutedEventArgs e)
    {
        if (document?.ClearGuides() == true) SetStatus(Loc.T("S_GuidesCleared"));
        else SetStatus(Loc.T("S_NoGuides"));
    }

    private void OnFitToWindow(object sender, RoutedEventArgs e) => FitToWindow();

    private void OnZoom100(object sender, RoutedEventArgs e)
    {
        SetViewportZoom(1);
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) => ZoomStep(1);

    private void OnZoomOut(object sender, RoutedEventArgs e) => ZoomStep(-1);

    /// <summary>Steps to the next stable level with the viewport center staying put —
    /// The viewport keeps the document point under its center anchored.</summary>
    private void ZoomStep(int step)
    {
        SetViewportZoom(viewport.KeyboardZoomTarget(step));
    }

    private void FitToWindow()
    {
        if (document is null) return;
        viewport.Fit(); QueueViewportFrame();
    }

    // --- Drag and drop ---

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (document is not { } owner) return;
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var retired = documentRetired.Task;
        var position = e.GetPosition(CanvasStage);
        var point = FileDropPoint(new(position.X, position.Y));
        var deferral = e.GetDeferral();
        Task? job = null;
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            if (!retired.IsCompleted && ReferenceEquals(owner, document))
            {
                var paths = items.OfType<IStorageItem>()
                    .Where(item => item is StorageFile file
                        ? IsImportableExtension(System.IO.Path.GetExtension(file.Name))
                        : item is StorageFolder folder && ProjectStore.IsProjectDirectory(folder.Path))
                    .Select(item => item.Path).ToArray();
                job = ReceiveDroppedItemsAsync(paths, point);
                if (paths.Length == 0)
                {
                    // The Mac's other branch: image data that is not a file on disk — a screenshot's
                    // thumbnail, a picture dragged out of a browser — is written to a temporary file
                    // under the name the drop carries and handed to the SAME import loop
                    // (ImageFileDrop.importProviders :21-24, temporaryFile :36-59: "the file only
                    // exists until the provider's closure returns, so it is copied, not referenced").
                    var image = await TakeInMemoryImageDropAsync(e.DataView);
                    if (image is not null) job = ReceiveDroppedItemsAsync([image], point);
                }
            }
        }
        catch (Exception ex) { if (!retired.IsCompleted) Report(Loc.T("SC_ImportImage"), ex); }
        finally { deferral.Complete(); }
        if (job is not null) await job;
    }

    /// <summary>The non-file provider of the original's drop (ImageFileDrop.temporaryFile): the
    /// bitmap the drag carries in memory is decoded, written as a PNG under a name from the drop's
    /// own properties, and passed to the file loop so the import, the layer name and the conversion
    /// report all behave as if a file had been dragged. Null when the drop holds no image bytes.</summary>
    internal static async Task<string?> TakeInMemoryImageDropAsync(Windows.ApplicationModel.DataTransfer.DataPackageView view)
    {
        if (!view.Contains(StandardDataFormats.Bitmap)) return null;
        try
        {
            var reference = await view.GetBitmapAsync();
            using var stream = await reference.OpenReadAsync();
            Surface? pixels;
            try { pixels = await WicImaging.DecodeSurfaceAsync(stream); }
            catch (Exception ex) { App.Log($"bitmap drop decode failed: {ex}", LogLevel.Diagnostic); return null; }
            var png = await WicImaging.EncodePngAsync(pixels);
            // The Mac keeps the provider's file name for the copy (temporaryFile :43-51); raw image
            // bytes carry no name through WinUI's property set, so the Mac's own empty-name fallback,
            // "Dropped", is what arrives here.
            var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "compositor-drop-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var file = System.IO.Path.Combine(folder, "Dropped.png");
            await File.WriteAllBytesAsync(file, png);
            return file;
        }
        catch (Exception ex) { App.Log($"bitmap drop unreadable: {ex}", LogLevel.Diagnostic); return null; }
    }

    /// <summary>The original's receive loop (ProjectWorkspace.receive, :157-175), in order: a dropped
    /// project package opens as a project (its own tab, through the same open path the picker uses),
    /// and a run of dropped images becomes layers — on the tab named by <paramref name="destination"/>
    /// when the drop landed on that pill (the Mac selects the destination before each import, so the
    /// files land on-screen there), or on whatever is visible when its turn comes otherwise, exactly
    /// like the Mac's sequential per-url loop. Waiting for the gate before moving mirrors the Mac's
    /// <c>while !canSwitch</c> park: a pill drop that arrives mid-upload waits the same beat.</summary>
    internal async Task ReceiveDroppedItemsAsync(IReadOnlyList<string> paths, PointD? point, Guid? destination = null)
    {
        for (int index = 0; index < paths.Count; )
        {
            if (windowClosed) return;
            if (ProjectStore.IsProjectDirectory(paths[index]))
            {
                await OpenProjectAsync(paths[index]);
                index += 1;
                continue;
            }
            var run = new List<string>();
            while (index < paths.Count && !ProjectStore.IsProjectDirectory(paths[index]))
            {
                run.Add(paths[index]);
                index += 1;
            }
            if (destination is { } target && workspace.SelectedId != target)
            {
                while (!windowClosed && !workspace.CanSwitch) await Task.Delay(30);
                if (windowClosed) return;
                // The Mac's guard let continue: a destination that vanished mid-wait skips this run.
                if (!SwitchProjectTab(target)) continue;
            }
            if (document is { } owner) await ImportFilesAsync(owner, run.ToArray(), point);
        }
    }

    // --- Pickers (unpackaged apps need the window handle) ---

    private void InitializeWithWindow(object picker)
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);
    }

    private async Task<StorageFolder?> PickFolderAsync()
    {
#if DEBUG
        if (projectFolderPickerProbe is { } probe) return await probe();
#endif
        var picker = new FolderPicker
        {
            // The identifier makes this picker remember its last directory independently
            // of the image pickers.
            SettingsIdentifier = "projectFolder",
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow(picker);
        return await picker.PickSingleFolderAsync();
    }
}
