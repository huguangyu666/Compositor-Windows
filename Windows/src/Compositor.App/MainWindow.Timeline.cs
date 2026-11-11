using Compositor.Core;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Diagnostics;
using Windows.Foundation;
using Windows.Storage.Pickers;
using Windows.System;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private CanvasControl timelineCanvas = null!;
    private Grid timelineEditor = null!;
    private Button timelineCreate = null!, timelinePlay = null!, timelineExport = null!;
    private TextBlock timelineTime = null!;
    private ScrollBar timelineHorizontal = null!, timelineVertical = null!;
    private NumberBox timelineDuration = null!, timelineFps = null!, timelineIn = null!, timelineOut = null!;
    private CheckBox timelineLoop = null!;
    private ToggleButton timelinePreview = null!;
    private ComboBox timelineInterpolation = null!;
    private readonly Dictionary<AnimationProperty, NumberBox> timelineFields = [];
    private readonly Dictionary<AnimationProperty, double> pendingTimelineValues = [];
    private readonly List<Button> timelineKeyButtons = [];
    private readonly Stopwatch timelineClock = new();
    private int timelineFrame, timelineStartFrame;
    private Guid? timelineLayer;
    private double timelinePixelsPerFrame = 6;
    private bool timelineVisible, syncingTimeline, timelinePlaying, applyingTimeline, timelineExporting;
    private bool timelineNeedsFit = true;
    private Guid? timelineDocumentId;
    private (bool Preview, bool Exporting)? timelineAppliedMode;
    private CancellationTokenSource? timelineExportCancellation;
    private const double TimelineNamesWidth = 170, TimelineRulerHeight = 24, TimelineTrackHeight = 30;
    private (Guid Layer, string Kind, int Start, int End, int Key, int Anchor, double X, uint Pointer)? timelineDrag;
    private ProjectTimeline? Timeline => document?.Snapshot.Manifest.Timeline;
    private bool TimelinePreviewActive => timelineVisible && Timeline is not null && timelinePreview?.IsChecked == true;
    private bool CanEditTimeline => document is not null && !ProjectEditsInFlight && !timelineExporting && !timelinePlaying
        && !renamingLayer && aiEditor is null && selectionWorkResult is null && !renderInputs.HasPending;

    private void InitializeTimeline()
    {
        var root = new Grid { RowSpacing = 0 };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Padding = new(12, 6, 12, 6) };
        header.Children.Add(new TextBlock { Text = Loc.T("TL_Title"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 8, 0) });
        timelinePlay = TimelineButton("TL_Play", "TimelinePlay", ToggleTimelinePlayback); header.Children.Add(timelinePlay);
        header.Children.Add(TimelineButton("TL_Previous", "TimelinePrevious", () => SeekTimeline(timelineFrame - 1)));
        header.Children.Add(TimelineButton("TL_Next", "TimelineNext", () => SeekTimeline(timelineFrame + 1)));
        timelineTime = new TextBlock { FontFamily = new FontFamily("Consolas"), MinWidth = 118, VerticalAlignment = VerticalAlignment.Center }; header.Children.Add(timelineTime);
        timelineDuration = TimelineNumber("TL_Duration", "TimelineDuration", 5, 1d / 120, 9000, 72);
        timelineFps = TimelineNumber("TL_Fps", "TimelineFps", 24, 1, 120, 62);
        AddTimelineField(header, "TL_Duration", timelineDuration); AddTimelineField(header, "TL_Fps", timelineFps);
        timelineDuration.ValueChanged += (_, _) => ApplyTimelineSettings(); timelineFps.ValueChanged += (_, _) => ApplyTimelineSettings();
        timelineLoop = new CheckBox { Content = Loc.T("TL_Loop"), IsChecked = true, MinWidth = 0 }; header.Children.Add(timelineLoop);
        timelineLoop.Click += (_, _) => ApplyTimelineSettings();
        timelinePreview = new ToggleButton { Content = Loc.T("TL_Preview"), IsChecked = true, Padding = new(8, 3, 8, 3) };
        timelinePreview.Click += (_, _) => { StopTimelinePlayback(); RefreshTimeline(); RefreshTimelineMode(); RenderCanvas(); }; header.Children.Add(timelinePreview);
        timelineExport = TimelineButton("TL_Export", "TimelineExport", () =>
        { if (timelineExporting) timelineExportCancellation?.Cancel(); else _ = ExportTimelineFramesAsync(); });
        header.Children.Add(timelineExport);
        header.Children.Add(TimelineButton("TL_Close", "TimelineClose", () => ShowTimeline(false)));
        root.Children.Add(new ScrollViewer { Content = header, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        timelineCreate = TimelineButton("TL_Create", "TimelineCreate", () =>
        {
            if (CanEditTimeline && document is { } owner) TimelineChange(() => owner.SetAnimationSettings(120, 24, true));
        });
        timelineCreate.Margin = new(12, 14, 12, 18); Grid.SetRow(timelineCreate, 1); root.Children.Add(timelineCreate);
        timelineEditor = new Grid(); timelineEditor.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); timelineEditor.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        timelineEditor.RowDefinitions.Add(new() { Height = GridLength.Auto }); timelineEditor.RowDefinitions.Add(new() { Height = GridLength.Auto });
        timelineCanvas = new CanvasControl { Height = 160, ClearColor = Color(30, 30, 30), IsTabStop = true };
        AutomationProperties.SetName(timelineCanvas, Loc.T("TL_Tracks"));
        timelineCanvas.Draw += DrawTimeline;
        timelineCanvas.SizeChanged += (_, _) => { if (timelineNeedsFit) FitTimeline(); else RefreshTimelineScroll(); };
        RootGrid.SizeChanged += (_, _) => ResizeTimeline();
        timelineCanvas.PointerPressed += OnTimelinePressed; timelineCanvas.PointerMoved += OnTimelineMoved;
        timelineCanvas.PointerReleased += OnTimelineReleased; timelineCanvas.PointerCanceled += OnTimelineCancelled; timelineCanvas.PointerCaptureLost += OnTimelineCancelled;
        timelineCanvas.KeyDown += OnTimelineKeyDown;
        timelineCanvas.PointerWheelChanged += (_, e) =>
        {
            int delta = e.GetCurrentPoint(timelineCanvas).Properties.MouseWheelDelta;
            if (KeyHeld(VirtualKey.Control)) ZoomTimeline(delta > 0 ? 1.25 : .8);
            else timelineVertical.Value = Math.Clamp(timelineVertical.Value - delta / 120d * 3, 0, timelineVertical.Maximum);
            e.Handled = true;
        };
        timelineEditor.Children.Add(CreateTimelineSurface());
        timelineVertical = new ScrollBar { Orientation = Orientation.Vertical, Width = 12, Minimum = 0, SmallChange = 1 };
        timelineVertical.ValueChanged += (_, _) => timelineCanvas.Invalidate(); Grid.SetColumn(timelineVertical, 1); timelineEditor.Children.Add(timelineVertical);
        timelineHorizontal = new ScrollBar { Orientation = Orientation.Horizontal, Height = 12, Minimum = 0, SmallChange = 1, Margin = new(TimelineNamesWidth, 0, 0, 0) };
        timelineHorizontal.ValueChanged += (_, _) => { RefreshTimelinePlayhead(restartAnimation: true); timelineCanvas.Invalidate(); };
        Grid.SetRow(timelineHorizontal, 1); timelineEditor.Children.Add(timelineHorizontal);
        Grid.SetRow(timelineEditor, 2); root.Children.Add(timelineEditor);
        var details = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Padding = new(12, 6, 12, 6) };
        timelineIn = TimelineNumber("TL_In", "TimelineIn", 0, 0, 9000, 74); timelineOut = TimelineNumber("TL_Out", "TimelineOut", 5, 0, 9000, 74);
        var clipFields = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        AddTimelineField(clipFields, "TL_In", timelineIn); AddTimelineField(clipFields, "TL_Out", timelineOut);
        header.Children.Insert(header.Children.Count - 2, clipFields);
        timelineIn.ValueChanged += (_, _) => ApplyTimelineClip(); timelineOut.ValueChanged += (_, _) => ApplyTimelineClip();
        foreach (var property in Enum.GetValues<AnimationProperty>())
        {
            string key = "TL_" + property;
            var box = TimelineNumber(key, "Timeline" + property, 0, property is AnimationProperty.ScaleX or AnimationProperty.ScaleY ? .1 : property == AnimationProperty.Opacity ? 0 : -1_000_000,
                property == AnimationProperty.Opacity ? 100 : property is AnimationProperty.ScaleX or AnimationProperty.ScaleY ? 100_000 : 1_000_000, 64);
            string caption = property switch { AnimationProperty.PositionX => "X", AnimationProperty.PositionY => "Y",
                AnimationProperty.ScaleX => "W %", AnimationProperty.ScaleY => "H %", AnimationProperty.Rotation => "°", _ => "α %" };
            var label = new TextBlock { Text = caption, VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
            ToolTipService.SetToolTip(label, Loc.T(key)); details.Children.Add(label); details.Children.Add(box); timelineFields.Add(property, box);
            box.ValueChanged += (_, _) => { if (!syncingTimeline && double.IsFinite(box.Value)) pendingTimelineValues[property] = box.Value; };
            box.LostFocus += (_, _) => CommitTimelineFields();
            box.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { CommitTimelineFields(); e.Handled = true; } };
        }
        timelineInterpolation = new ComboBox { MinWidth = 92, ItemsSource = new[] { Loc.T("TL_Linear"), Loc.T("TL_Smooth"), Loc.T("TL_Hold") }, SelectedIndex = 0 };
        AutomationProperties.SetName(timelineInterpolation, Loc.T("TL_Interpolation")); details.Children.Add(timelineInterpolation);
        timelineInterpolation.SelectionChanged += (_, _) => ApplyTimelineInterpolation();
        var keyActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new(6, 0, 12, 0) };
        var add = TimelineButton("TL_AddKey", "TimelineAddKey", AddTimelineKey); timelineKeyButtons.Add(add); keyActions.Children.Add(add);
        var remove = TimelineButton("TL_DeleteKey", "TimelineDeleteKey", () =>
        {
            if (CanEditTimeline && document is { } owner && timelineLayer is { } id)
                TimelineChange(() => owner.RemoveAnimationKeys(id, timelineFrame, timelineFields.Where(pair => pair.Value.IsEnabled).Select(pair => pair.Key).ToArray()));
        }); timelineKeyButtons.Add(remove); keyActions.Children.Add(remove);
        details.Children.Add(TimelineButton("TL_ZoomOut", "TimelineZoomOut", () => ZoomTimeline(.8)));
        details.Children.Add(TimelineButton("TL_ZoomIn", "TimelineZoomIn", () => ZoomTimeline(1.25)));
        details.Children.Add(TimelineButton("TL_Fit", "TimelineFit", FitTimeline));
        var footer = new Grid(); footer.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        footer.Children.Add(new ScrollViewer { Content = details, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Grid.SetColumn(keyActions, 1); footer.Children.Add(keyActions);
        Grid.SetRow(footer, 3); root.Children.Add(footer); TimelineHost.Child = root;
    }

    private static Windows.UI.Color Color(byte r, byte g, byte b) => Windows.UI.Color.FromArgb(255, r, g, b);
    private Button TimelineButton(string key, string name, Action action)
    {
        var button = new Button { Name = name, Content = Loc.T(key), Padding = new(8, 3, 8, 3), MinWidth = 28 };
        AutomationProperties.SetName(button, Loc.T(key)); ToolTipService.SetToolTip(button, Loc.T(key)); button.Click += (_, _) => action(); return button;
    }
    private static NumberBox TimelineNumber(string key, string name, double value, double minimum, double maximum, double width)
    {
        var box = new NumberBox { Name = name, Value = value, Minimum = minimum, Maximum = maximum, Width = width, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden, SmallChange = 1 };
        AutomationProperties.SetName(box, Loc.T(key)); ToolTipService.SetToolTip(box, Loc.T(key)); return box;
    }
    private static void AddTimelineField(StackPanel row, string key, FrameworkElement control)
    { row.Children.Add(new TextBlock { Text = Loc.T(key), VerticalAlignment = VerticalAlignment.Center, FontSize = 11 }); row.Children.Add(control); }
    private void OnToggleTimeline(object sender, RoutedEventArgs e) => ShowTimeline(TimelineToggle.IsChecked);
    private void ShowTimeline(bool visible)
    {
        if (ProjectEditsInFlight) { TimelineToggle.IsChecked = timelineVisible; SetStatus(Loc.T("TL_FinishEdit")); return; }
        StopTimelinePlayback(); CancelTimelineDrag(); CommitTimelineFields(); timelineVisible = visible; TimelineToggle.IsChecked = visible;
        TimelineHost.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible) { timelineLayer ??= selectedLayerId; ResizeTimeline(); RefreshTimeline(); FitTimeline(); }
        RefreshTimelineMode(); RenderCanvas();
    }
    private void ResetTimelineForDocument()
    {
        StopTimelinePlayback(); CancelTimelineDrag(); pendingTimelineValues.Clear();
        timelineDocumentId = document?.Snapshot.Manifest.DocumentId; timelineFrame = 0; timelineLayer = selectedLayerId;
        timelinePreview.IsChecked = true; timelineAppliedMode = null;
        timelineNeedsFit = true; ResizeTimeline();
        timelineVisible = Timeline is not null; TimelineToggle.IsChecked = timelineVisible;
        TimelineHost.Visibility = timelineVisible ? Visibility.Visible : Visibility.Collapsed;
        RefreshTimeline(); RefreshTimelineMode();
    }
    private void RefreshTimelineMode()
    {
        bool preview = TimelinePreviewActive;
        if (timelineAppliedMode == (preview, timelineExporting)) return;
        timelineAppliedMode = (preview, timelineExporting);
        CanvasHost.IsHitTestVisible = !preview; ToolOverlay.Visibility = preview ? Visibility.Collapsed : Visibility.Visible;
        RefreshTransformInspector(true);
    }
    private void RefreshTimeline()
    {
        if (timelineCanvas is null) return;
        if (timelineDocumentId != document?.Snapshot.Manifest.DocumentId) { ResetTimelineForDocument(); return; }
#if DEBUG
        long refreshStart = Stopwatch.GetTimestamp(), refreshBytes = GC.GetAllocatedBytesForCurrentThread();
#endif
        var timeline = Timeline; syncingTimeline = true;
        try
        {
            bool enabled = timeline is not null;
            timelineCreate.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible; timelineEditor.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            bool editable = enabled && !timelinePlaying && !timelineExporting && !ProjectEditsInFlight;
            timelinePlay.IsEnabled = enabled && !timelineExporting;
            timelineExport.IsEnabled = enabled;
            timelineExport.Content = Loc.T(timelineExporting ? "TL_CancelExport" : "TL_Export");
            timelineDuration.IsEnabled = timelineFps.IsEnabled = timelineLoop.IsEnabled = editable;
            timelinePreview.IsEnabled = !timelineExporting;
            timelineFrame = Math.Clamp(timelineFrame, 0, (timeline?.DurationFrames ?? 1) - 1);
            timelineDuration.Value = (double)(timeline?.DurationFrames ?? 120) / (timeline?.FramesPerSecond ?? 24); timelineFps.Value = timeline?.FramesPerSecond ?? 24;
            timelineLoop.IsChecked = timeline?.Loop ?? true;
            if (document?.Snapshot.Manifest.Layers.All(layer => layer.Id != timelineLayer) == true) timelineLayer = selectedLayerId;
            var layer = document?.Snapshot.Manifest.Layers.FirstOrDefault(layer => layer.Id == timelineLayer);
            var track = timeline?.Tracks.FirstOrDefault(track => track.LayerId == timelineLayer);
            timelineIn.Value = (double)(track?.InFrame ?? 0) / (timeline?.FramesPerSecond ?? 24);
            timelineOut.Value = (double)(track?.OutFrame ?? timeline?.DurationFrames ?? 120) / (timeline?.FramesPerSecond ?? 24);
            timelineIn.IsEnabled = timelineOut.IsEnabled = editable && layer is not null && document!.CanEditLayer(layer.Id, LayerLocks.None);
            foreach (var (property, field) in timelineFields)
            {
                field.IsEnabled = editable && layer is not null && document!.CanEditLayer(layer.Id, property == AnimationProperty.Opacity ? LayerLocks.None : LayerLocks.Position)
                    && (property == AnimationProperty.Opacity || !layer.IsGroupOrFalse && layer.Adjustment is null);
            }
            foreach (var button in timelineKeyButtons) button.IsEnabled = editable && layer is not null && document!.CanEditLayer(layer.Id, LayerLocks.None);
            timelineInterpolation.IsEnabled = editable && layer is not null;
            RefreshTimelineValues(layer, track); RefreshTimelineScroll();
        }
        finally { syncingTimeline = false; }
        RefreshTimelineMode();
#if DEBUG
        timelineRefreshProbe?.Invoke(Stopwatch.GetElapsedTime(refreshStart).TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - refreshBytes);
#endif
    }
    private void RefreshTimelinePosition()
    {
        var layer = document?.Snapshot.Manifest.Layers.FirstOrDefault(layer => layer.Id == timelineLayer);
        var track = Timeline?.Tracks.FirstOrDefault(track => track.LayerId == timelineLayer);
        syncingTimeline = true;
        try { RefreshTimelineValues(layer, track); }
        finally { syncingTimeline = false; }
    }
    private void RefreshTimelineValues(ProjectLayerRecord? layer, LayerAnimationTrack? track)
    {
        if (layer is not null)
            foreach (var (property, field) in timelineFields)
            {
                if (pendingTimelineValues.ContainsKey(property)) continue;
                double value = TimelineDisplayValue(property, TimelineValue(layer, track, property));
                if (field.Value != value) field.Value = value;
            }
        string interpolation = "linear";
        int highlighted = -1;
        if (track is not null)
            foreach (var property in timelineFields.Keys)
                if (track.Keys(property).FirstOrDefault(key => key.Frame == timelineFrame) is { } key)
                { interpolation = key.Interpolation; highlighted = timelineFrame; break; }
        int index = interpolation switch { "smooth" => 1, "hold" => 2, _ => 0 };
        if (timelineInterpolation.SelectedIndex != index) timelineInterpolation.SelectedIndex = index;
        UpdateTimelineTime();
        if (timelineDrag is { Kind: "key" } || timelineHighlightedFrame != highlighted) timelineCanvas.Invalidate();
        timelineHighlightedFrame = highlighted;
    }
    private double TimelineValue(ProjectLayerRecord layer, LayerAnimationTrack? track, AnimationProperty property)
    {
        double fallback = property switch { AnimationProperty.PositionX => layer.Transform.Center.X, AnimationProperty.PositionY => layer.Transform.Center.Y,
            AnimationProperty.Rotation => layer.Transform.Rotation, AnimationProperty.Opacity => layer.OpacityOrOne, _ => 1 };
        return track is null ? fallback : AnimationEvaluator.Sample(track.Keys(property), timelineFrame, fallback);
    }
    private static double TimelineDisplayValue(AnimationProperty property, double value) => property is AnimationProperty.ScaleX or AnimationProperty.ScaleY or AnimationProperty.Opacity ? value * 100 : value;
    private static double TimelineStoredValue(AnimationProperty property, double value) => property is AnimationProperty.ScaleX or AnimationProperty.ScaleY or AnimationProperty.Opacity ? value / 100 : value;
    private string TimelineEasing => timelineInterpolation.SelectedIndex switch { 1 => "smooth", 2 => "hold", _ => "linear" };
    private void TimelineChange(Func<bool> edit)
    {
        if (!CanEditTimeline) { SetStatus(Loc.T("TL_FinishEdit")); return; }
        applyingTimeline = true;
        try { if (!edit()) SetStatus(Loc.T("TL_Invalid")); }
        finally { applyingTimeline = false; RefreshTimeline(); RefreshTimelineMode(); }
    }
    private void ApplyTimelineSettings()
    {
        if (syncingTimeline || Timeline is null || !double.IsFinite(timelineDuration.Value) || !double.IsFinite(timelineFps.Value) || document is not { } owner) return;
        int fps = (int)Math.Round(timelineFps.Value); int frames = (int)Math.Round(timelineDuration.Value * fps);
        int frame = (int)Math.Round((double)timelineFrame * fps / Timeline.FramesPerSecond);
        StopTimelinePlayback(); TimelineChange(() => owner.SetAnimationSettings(frames, fps, timelineLoop.IsChecked == true));
        if (Timeline?.FramesPerSecond == fps) SeekTimeline(frame);
    }
    private void ApplyTimelineClip()
    {
        if (syncingTimeline || Timeline is not { } timeline || timelineLayer is not { } id || document is not { } owner
            || !double.IsFinite(timelineIn.Value) || !double.IsFinite(timelineOut.Value)) return;
        int start = (int)Math.Round(timelineIn.Value * timeline.FramesPerSecond), end = (int)Math.Round(timelineOut.Value * timeline.FramesPerSecond);
        TimelineChange(() => owner.SetAnimationClip(id, start, end));
    }
    private void CommitTimelineFields()
    {
        if (syncingTimeline || pendingTimelineValues.Count == 0) return;
        var values = pendingTimelineValues.ToDictionary(pair => pair.Key, pair => TimelineStoredValue(pair.Key, pair.Value)); pendingTimelineValues.Clear();
        if (document is { } owner && timelineLayer is { } id) TimelineChange(() => owner.SetAnimationKeys(id, timelineFrame, values, TimelineEasing));
    }
    private void AddTimelineKey()
    {
        if (document is not { } owner || timelineLayer is not { } id) return;
        pendingTimelineValues.Clear();
        var values = timelineFields.Where(pair => pair.Value.IsEnabled).ToDictionary(pair => pair.Key, pair => TimelineStoredValue(pair.Key, pair.Value.Value));
        TimelineChange(() => owner.SetAnimationKeys(id, timelineFrame, values, TimelineEasing));
    }
    private void ApplyTimelineInterpolation()
    {
        if (syncingTimeline || document is not { } owner || timelineLayer is not { } id || Timeline?.Tracks.FirstOrDefault(track => track.LayerId == id) is not { } track) return;
        var values = Enum.GetValues<AnimationProperty>().Where(property => track.Keys(property).Any(key => key.Frame == timelineFrame))
            .ToDictionary(property => property, property => track.Keys(property).Single(key => key.Frame == timelineFrame).Value);
        if (values.Count > 0) TimelineChange(() => owner.SetAnimationKeys(id, timelineFrame, values, TimelineEasing));
    }
    private void SeekTimeline(int frame, bool stop = true)
    {
        if (Timeline is not { } timeline || timelineExporting) return;
        bool stopped = stop && timelinePlaying;
        if (stop) { StopTimelinePlayback(); CommitTimelineFields(); }
        timeline = Timeline!;
        frame = Math.Clamp(frame, 0, timeline.DurationFrames - 1);
        if (timelineFrame == frame) { if (stopped) RefreshTimeline(); return; }
        timelineFrame = frame;
        if (TimelinePreviewActive)
        {
            // A seek changes time, not the document revision. Let the current frame
            // publish while the serial worker coalesces later seeks into the next frame.
            pendingFullRender = true; PumpRender();
        }
        if (timelinePlaying && (TimelineX(frame) < TimelineNamesWidth || TimelineX(frame) > timelineCanvas.ActualWidth - 12))
            timelineHorizontal.Value = Math.Clamp(frame - 1, 0, timelineHorizontal.Maximum);
        if (stopped) RefreshTimeline(); else RefreshTimelinePosition();
    }
    private void ToggleTimelinePlayback()
    {
        if (timelinePlaying) { StopTimelinePlayback(); RefreshTimeline(); return; }
        if (!CanEditTimeline || Timeline is not { } timeline) return;
        CommitTimelineFields(); timelinePreview.IsChecked = true; RefreshTimelineMode();
        if (timelineFrame >= timeline.DurationFrames - 1) timelineFrame = 0;
        timelineStartFrame = timelineFrame; timelinePlaying = true; timelineClock.Restart();
        CompositionTarget.Rendering += OnTimelinePlaybackFrame;
        timelinePlay.Content = Loc.T("TL_Pause"); RefreshTimeline(); RenderCanvas();
    }
    private void StopTimelinePlayback()
    {
        if (!timelinePlaying) return;
        timelinePlaying = false; CompositionTarget.Rendering -= OnTimelinePlaybackFrame;
        timelineClock.Stop(); timelinePlay.Content = Loc.T("TL_Play");
        StopTimelinePlayheadAnimation(); RefreshTimelinePlayhead();
    }
    private void OnTimelinePlaybackFrame(object? sender, object args) => TickTimelinePlayback();
    private void TickTimelinePlayback()
    {
        if (!timelinePlaying || Timeline is not { } timeline || windowClosed) { StopTimelinePlayback(); return; }
        int frame = timelineStartFrame + (int)(timelineClock.Elapsed.TotalSeconds * timeline.FramesPerSecond);
        if (frame >= timeline.DurationFrames && !timeline.Loop)
        { StopTimelinePlayback(); SeekTimeline(timeline.DurationFrames - 1, stop: false); RefreshTimeline(); return; }
        SeekTimeline(frame % timeline.DurationFrames, stop: false);
    }
    private void UpdateTimelineTime()
    {
        int fps = Timeline?.FramesPerSecond ?? 24;
        string text = $"{timelineFrame / fps / 60:00}:{timelineFrame / fps % 60:00}:{timelineFrame % fps:00}  /  {Timeline?.DurationFrames ?? 120}";
        if (timelineTime.Text != text) timelineTime.Text = text;
        RefreshTimelinePlayhead();
    }
    private void RefreshTimelineScroll()
    {
        if (timelineCanvas is null || timelineHorizontal is null || timelineVertical is null) return;
        double frames = Math.Max(1, (timelineCanvas.ActualWidth - TimelineNamesWidth) / timelinePixelsPerFrame);
        timelineHorizontal.ViewportSize = frames; timelineHorizontal.LargeChange = frames * .8;
        timelineHorizontal.Maximum = Math.Max(0, (Timeline?.DurationFrames ?? 120) - frames);
        timelineHorizontal.Value = Math.Min(timelineHorizontal.Value, timelineHorizontal.Maximum);
        double rows = Math.Max(1, (timelineCanvas.ActualHeight - TimelineRulerHeight) / TimelineTrackHeight);
        timelineVertical.ViewportSize = rows; timelineVertical.LargeChange = rows;
        timelineVertical.Maximum = Math.Max(0, (document?.LayerCount ?? 0) - rows);
        timelineVertical.Value = Math.Min(timelineVertical.Value, timelineVertical.Maximum);
        RefreshTimelinePlayhead(restartAnimation: true); timelineCanvas.Invalidate();
    }
    private void ZoomTimeline(double factor)
    { timelinePixelsPerFrame = Math.Clamp(timelinePixelsPerFrame * factor, .02, 30); RefreshTimelineScroll(); }
    private void ResizeTimeline()
    {
        if (timelineCanvas is null || RootGrid.ActualHeight <= 0) return;
        timelineCanvas.Height = Math.Clamp((RootGrid.ActualHeight - 400) * .6, 54, 160);
    }
    private void FitTimeline()
    {
        if (timelineCanvas.ActualWidth <= TimelineNamesWidth + 20) { timelineNeedsFit = true; return; }
        timelineNeedsFit = false;
        timelinePixelsPerFrame = Math.Clamp((timelineCanvas.ActualWidth - TimelineNamesWidth - 12) / (Timeline?.DurationFrames ?? 120), .02, 30);
        timelineHorizontal.Value = 0; RefreshTimelineScroll();
    }
    private double TimelineX(int frame) => TimelineNamesWidth + (frame - timelineHorizontal.Value) * timelinePixelsPerFrame;
    private int TimelineAt(double x) => Math.Clamp((int)Math.Round((x - TimelineNamesWidth) / timelinePixelsPerFrame + timelineHorizontal.Value), 0, (Timeline?.DurationFrames ?? 120) - 1);

    private async Task ExportTimelineFramesAsync()
    {
        StopTimelinePlayback();
        if (!CanEditTimeline || document is not { } owner || Timeline is null) return;
        StopTimelinePlayback(); CommitTimelineFields();
        try
        {
            var picker = new FolderPicker(); picker.FileTypeFilter.Add("*"); InitializeWithWindow(picker);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null || !ReferenceEquals(document, owner) || !CanEditTimeline) return;
            string output = Path.Combine(folder.Path, "Compositor-frames-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
            await ExportTimelineToDirectoryAsync(owner, output);
        }
        catch (Exception ex) { if (!windowClosed) Report(Loc.T("TL_ExportFailed"), ex); }
    }

    private async Task ExportTimelineToDirectoryAsync(EditorDocument owner, string output)
    {
        if (Directory.Exists(output) || File.Exists(output)) throw new ArgumentException("Choose a new directory for the PNG sequence.");
        var snapshot = owner.CaptureSave().Snapshot;
        var timeline = snapshot.Manifest.Timeline ?? throw new InvalidOperationException("Create a timeline first.");
        using var cancellation = new CancellationTokenSource(); timelineExportCancellation = cancellation;
        var token = cancellation.Token;
        timelineExporting = true; RefreshTimeline(); RefreshTimelineMode();
        try
        {
            Directory.CreateDirectory(output); SetStatus(Loc.T("TL_Exporting"));
            await Task.Run(async () =>
            {
                var compositor = new CanvasCompositor(snapshot);
                for (int frame = 0; frame < timeline.DurationFrames; frame++)
                {
                    token.ThrowIfCancellationRequested();
                    compositor.SetSnapshot(AnimationEvaluator.Evaluate(snapshot, frame));
                    byte[] png = await WicImaging.EncodePngAsync(compositor.Render(), token);
                    await File.WriteAllBytesAsync(Path.Combine(output, $"frame-{frame:D6}.png"), png, token);
                    if (frame % 12 == 0 || frame == timeline.DurationFrames - 1)
                    {
                        int completed = frame + 1;
                        DispatcherQueue.TryEnqueue(() => { if (!windowClosed && timelineExporting) SetStatus(Loc.F("TL_ExportProgress", completed, timeline.DurationFrames)); });
                    }
                }
                await File.WriteAllTextAsync(Path.Combine(output, "sequence.json"), System.Text.Json.JsonSerializer.Serialize(new
                { framesPerSecond = timeline.FramesPerSecond, durationFrames = timeline.DurationFrames, pattern = "frame-%06d.png" }), token);
            }, token);
            if (!windowClosed) SetStatus(Loc.F("TL_Exported", output));
        }
        catch (OperationCanceledException) { if (!windowClosed) SetStatus(Loc.F("TL_ExportCancelled", output)); }
        finally
        {
            timelineExportCancellation = null; timelineExporting = false;
            if (!windowClosed) { RefreshTimeline(); RefreshTimelineMode(); }
        }
    }
}
