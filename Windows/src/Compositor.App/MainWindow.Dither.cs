using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private sealed class DitherOperation(EditorDocument owner, Task retired)
    {
        public readonly EditorDocument Owner = owner;
        public readonly Task Retired = retired;
        public readonly CancellationTokenSource Cancellation = new();
        public readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool OwnsBusy;
    }
    private sealed record DitherInput(Surface Original, int Revision, Surface Working, LayerTransform Placement, string Name, MaskSurface? Clip);
    private DitherOperation? ditherOperation;

    /// <summary>
    /// The filter panel's live preview session (Filters.swift:274-283 holds the same thing as `FilterEdit`:
    /// the original, a preview source, and the settings being dragged). Every tick of the panel re-dithers
    /// the capture made when the panel opened — never the layer as it now stands, which is by then an
    /// earlier tick's result — and writes it as one coalesced undo step. OK closes the step and keeps it;
    /// Cancel hands the captured pixels back and closes it, which leaves no step at all, exactly as the
    /// Mac's `cancelFilter()` (Filters.swift:575-585) simply drops the edit without touching history.
    /// </summary>
    private sealed class DitherPreview
    {
        public required EditorDocument Owner;
        public required Guid LayerId;
        public required DitherInput Capture;
        /// <summary>Set straight after the form is built: the form needs the session to talk back to, and
        /// the session needs the form to read its values. Nothing can ask for a tick before that.</summary>
        public DitherForm Form = null!;
        public readonly object SessionKey = new();
        public Surface? Published;
        public Task? Running;
        public bool Busy;
        public bool Queued;
        public bool Closed;
        public bool CommitRun;
        /// <summary>Why the last preview tick failed, or null. The Mac's `FilterEdit.previewError`
        /// (Filters.swift:293) is set from the render's own error and shown in the sheet (:144-146); a failure
        /// here used to go to the log and nowhere the user could see, so a slider that stopped answering looked
        /// exactly like a slider whose change was still in flight.</summary>
        public string? Error;
        public bool Showing = true;
        public bool Previewed;
    }
    private DitherPreview? ditherPreview;

    private Task<bool> ApplyDitherAsync(EditorDocument owner, Guid layerId, DitherParams parameters, int block,
        DitherPixelShape shape = DitherPixelShape.Square)
        => ApplyDitherAsync(owner, layerId, parameters, block, shape, null);

    private Task<bool> ApplyDitherAsync(EditorDocument owner, Guid layerId, DitherParams parameters, int block,
        DitherPixelShape shape, DitherPreview? session)
    {
        if (windowClosed || !ReferenceEquals(document, owner) || ditherOperation is not null) return Task.FromResult(false);
        var operation = ditherOperation = new DitherOperation(owner, documentRetired.Task);
        var work = RunDitherAsync(operation, layerId, parameters, Math.Clamp(block, 1, 32), shape, session);
        if (session is { } ticking) ticking.Running = work;
#if DEBUG
        ditherProbeWork = work;
#endif
        return work;
    }

    private async Task<bool> RunDitherAsync(DitherOperation operation, Guid layerId, DitherParams parameters, int block,
        DitherPixelShape shape, DitherPreview? session)
    {
        var owner = operation.Owner; var token = operation.Cancellation.Token;
        bool Current() => !windowClosed && !operation.Retired.IsCompleted && ReferenceEquals(document, owner)
            && ReferenceEquals(ditherOperation, operation) && !token.IsCancellationRequested;
        try
        {
            DitherInput input;
            if (session is not null)
            {
                // A preview tick never re-reads the live layer as its source: by the second tick that would
                // be the first tick's dither, and dithering a dither is not the picture OK is meant to write.
                // It works on the capture the panel opened with, and the only thing it demands of the layer
                // is that it still holds what this very session last published.
                var live = session.Published ?? session.Capture.Original;
                if (!Current()) return false;
                // `Working` is what the engine writes over, so each tick gets its own copy of the pristine
                // capture: dithering the previous tick's output is not the picture the panel is showing.
                input = session.Capture with { Original = live, Revision = live.Revision, Working = session.Capture.Working.Clone() };
            }
            else
            {
                FinishPointerStroke();
                if (!await WaitForInteractionWorkAsync(FinishTransformBeforeOutputAsync(waitForDither: false), operation.Retired) || !Current()) return false;
                var captured = new TaskCompletionSource<DitherInput?>(TaskCreationOptions.RunContinuationsAsynchronously);
                QueueCanvasInput(owner, () =>
                {
                    try
                    {
                        var layer = owner.Snapshot.Manifest.Layers.FirstOrDefault(layer => layer.Id == layerId);
                        var source = owner.Snapshot.Images.GetValueOrDefault(layerId);
                        // Capture only after the render reader has retired. The worker never reads
                        // the live mutable pixels, even if a later brush or external update changes them.
                        captured.TrySetResult(Current() && layer is not null && source is not null
                            ? new(source, source.Revision, source.Clone(), CopyTransform(layer.Transform), layer.Name,
                                owner.SelectionCoverageForLayer(layerId, source.Width, source.Height)) : null);
                    }
                    catch (Exception ex) { captured.TrySetException(ex); }
                });
                if (!await WaitForInteractionWorkAsync(captured.Task, operation.Retired) || !Current()) return false;
                if (await captured.Task is not { } fresh) { SetStatus(Loc.T("S_PaintNeedsLayer")); return false; }
                input = fresh;
                operation.OwnsBusy = true; SetBusy(true); RefreshTransformAvailability(true);
            }
#if DEBUG
            var beforeCompute = beforeDitherCompute;
            var beforeUpscale = beforeDitherUpscale;
#endif
            var worker = Task.Run(
#if DEBUG
                async () =>
#else
                () =>
#endif
            {
                try
                {
#if DEBUG
                    if (beforeCompute is not null) await beforeCompute(input.Working).WaitAsync(token);
#endif
                    token.ThrowIfCancellationRequested();
                    // Apply dithers in place, so a clipped run needs the untouched pixels kept
                    // separately: the area outside the selection has to come from the source as it
                    // was captured, never from the live layer surface.
                    var source = input.Working;
                    var surface = input.Clip is null ? source : source.Clone();
                    if (block > 1) surface = DitherEngine.AverageDown(source, block, token);
                    token.ThrowIfCancellationRequested();
                    DitherEngine.Apply(surface, parameters, token);
                    token.ThrowIfCancellationRequested();
                    // A CRT's bloom is the scanned lines blurred wide and added back over themselves, so it
                    // works on the finished picture at the spacing it was scanned at — and the Mac returns
                    // right here, which is another way of saying this style never gets a chunky block
                    // (Dither.swift:131). The beam is driven past the picture, hence the 2.5.
                    if (parameters.Style == DitherStyle.Scanlines && parameters.Glow > 0)
                    {
                        var bloom = DitherEngine.Bloom(surface, DitherEngine.ScanlineSpacing(parameters), token);
                        DitherEngine.Glow(surface, bloom, parameters.Glow * 2.5f);
                    }
                    var dithered = surface;
                    if (block > 1)
                    {
#if DEBUG
                        beforeUpscale?.Invoke();
#endif
                        var full = new Surface(source.Width, source.Height);
                        for (int y = 0; y < source.Height; y++)
                        {
                            token.ThrowIfCancellationRequested();
                            for (int x = 0; x < source.Width; x++)
                            {
                                int from = (y / block) * surface.Stride + (x / block) * 4;
                                int to = y * full.Stride + x * 4;
                                for (int c = 0; c < 4; c++) full.Pixels[to + c] = surface.Pixels[from + c];
                            }
                        }
                        // The Dot shape is cut out of the blown-up block, not the small image: the gaps
                        // between dots are the screen showing through, and they are only the size of a
                        // chunky pixel once it has been scaled back (Dither.swift:135-140). The dark of a
                        // two-color dither is that screen; black otherwise, which is what the caller's
                        // parameters already carry.
                        if (shape == DitherPixelShape.Dot) DitherEngine.Dots(full, block, parameters.Dark);
                        dithered = full;
                    }
                    // A selection restricts the filter: outside it the layer keeps exactly the
                    // pixels it had, so a partial feather blends rather than pops.
                    return input.Clip is { } clip ? DitherEngine.ClipToCoverage(source, dithered, clip, token) : dithered;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return null; }
                // A failed upscale must not expose its intermediate small bitmap. Returning
                // the failure also observes discarded workers after their owner retires.
                catch (Exception ex)
                {
                    // The cancellation above is not a failure; anything reaching here is, and it is the panel
                    // that has to say so — a silent null leaves the user with a canvas that stopped answering.
                    if (session is { } failing) failing.Error = ex.Message;
                    App.Log($"Dither worker failed: {ex}");
                    return null;
                }
            });
#if DEBUG
            ditherProbeWorker = worker;
#endif
            if (!await WaitForInteractionWorkAsync(worker, operation.Retired) || !Current()) return false;
            if (await worker is not { } result) { if (session is null) SetStatus(Loc.T("S_DitherFailed")); return false; }
            var published = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            QueueCanvasInput(owner, () =>
            {
                bool committed = false;
                try
                {
                    if (!Current()) return;
                    // A preview tick that is still running when its panel closes has lost the right to write:
                    // the Cancel has already handed the captured pixels back, and letting it land afterwards
                    // would put the effect back over a session the user walked away from.
                    if (session is not null && session.Closed && !session.CommitRun) return;
                    var layer = owner.Snapshot.Manifest.Layers.FirstOrDefault(layer => layer.Id == layerId);
                    if (!ReferenceEquals(owner.Snapshot.Images.GetValueOrDefault(layerId), input.Original)
                        || input.Original.Revision != input.Revision || layer is null || !layer.Transform.Equals(input.Placement))
                    { if (session is null) SetStatus(Loc.T("S_DitherSourceChanged")); return; }
                    MarkLayerBandDirty(layerId);
                    // One step for the whole session: every tick joins it, and closing it is what decides
                    // whether the panel's work stays in history.
                    committed = owner.ReplaceLayerImage(layerId, result, session?.SessionKey);
                    if (committed)
                    {
                        if (session is { } live) { live.Published = result; live.Previewed = true; }
                        else SetStatus(Loc.F("S_Dithered", input.Name));
                    }
                    else if (session is null) SetStatus(Loc.T("S_DitherFailed"));
                }
                catch (Exception ex) { App.Log($"Dither publication failed: {ex}"); if (Current() && session is null) SetStatus(Loc.T("S_DitherFailed")); }
                finally { published.TrySetResult(committed); }
            });
            return await WaitForInteractionWorkAsync(published.Task, operation.Retired) && Current() && await published.Task;
        }
        catch (Exception ex)
        {
            if (Current()) { App.Log($"Dither request failed: {ex}"); SetStatus(Loc.T("S_DitherFailed")); }
            return false;
        }
        finally
        {
            operation.Completion.TrySetResult();
            if (ReferenceEquals(ditherOperation, operation))
            {
                ditherOperation = null;
                if (operation.OwnsBusy && !windowClosed) { operation.OwnsBusy = false; SetBusy(false); RefreshTransformAvailability(true); }
            }
            operation.Cancellation.Dispose();
        }
    }

    private void RetireDither()
    {
        ditherPreview = null;
        AbandonFloatingPanel("filter");
        if (ditherOperation is not { } operation) return;
        ditherOperation = null; operation.Cancellation.Cancel();
        operation.Completion.TrySetResult();
        if (operation.OwnsBusy && !windowClosed) { operation.OwnsBusy = false; SetBusy(false); }
    }

    /// <summary>
    /// Opens the panel's preview session on the layer as it now stands. The copy is taken here, once, so
    /// every tick and a Cancel have the same untouched picture to work from — the same `previewSource` the
    /// Mac freezes into <c>FilterEdit</c> (Filters.swift:281). Nothing is computed yet: a panel opened and
    /// closed without a change has not touched the document at all.
    /// </summary>
    private DitherPreview? BeginDitherPreview(EditorDocument owner, Guid layerId)
    {
        if (owner.Snapshot.Images.GetValueOrDefault(layerId) is not { } source
            || owner.Snapshot.Manifest.Layers.FirstOrDefault(record => record.Id == layerId) is not { } record) return null;
        return ditherPreview = new DitherPreview
        {
            Owner = owner, LayerId = layerId,
            Capture = new DitherInput(source, source.Revision, source.Clone(), CopyTransform(record.Transform), record.Name,
                owner.SelectionCoverageForLayer(layerId, source.Width, source.Height)),
        };
    }

    /// <summary>
    /// Re-dithers the session's capture with whatever the panel holds right now. One tick runs at a time and
    /// changes arriving while it runs coalesce into a single re-run, so dragging a slider across its whole
    /// travel costs about one dither rather than one per pixel — the Camera Raw panel throttles its preview
    /// the same way, for the same reason.
    /// </summary>
    /// <summary>Hides the effect behind the untouched pixels while the panel keeps its settings.</summary>
    private void ShowDitherCapture(DitherPreview session)
    {
        if (!ReferenceEquals(ditherPreview, session)) return;
        session.Showing = false;
        RestoreDitherCapture(session);
    }

    private void PreviewDither(DitherPreview session)
    {
        if (!ReferenceEquals(ditherPreview, session) || session.Closed) return;
        session.Showing = true;
        if (session.Busy) { session.Queued = true; return; }
        // One driver task per session, covering the drain and every coalesced re-run: closing the panel
        // awaits this one handle and knows the session is quiet when it completes.
        session.Running = RunDitherTickAsync(session);
    }

    private async Task RunDitherTickAsync(DitherPreview session)
    {
        do
        {
            session.Busy = true;
            session.Queued = false;
            try
            {
                var (parameters, block, shape) = session.Form.Current();
                // Cleared before the run, as the Mac's `edit.previewError = nil` (Filters.swift:544): a line
                // that outlives the tick it describes turns a working slider into a reported failure.
                session.Error = null;
                await ApplyDitherAsync(session.Owner, session.LayerId, parameters, block, shape, session);
                // Read after the run, on the dispatcher: the worker's own catch is where the reason is written,
                // and a tick that succeeded has to take the line back down rather than leave the last failure up.
                session.Form.ShowPreviewError(session.Error);
            }
            finally { session.Busy = false; }
        }
        while (session.Queued && !session.Closed && ReferenceEquals(ditherPreview, session));
    }

    /// <summary>
    /// Puts the layer back the way the panel found it, inside the session's own undo step: the Preview
    /// switch, Cancel, and a failed final run all come through here. Closing the step afterwards collapses
    /// it, so a filter the user walked away of leaves no trace in history or in the saved-state flag.
    /// </summary>
    private void RestoreDitherCapture(DitherPreview session)
    {
        if (!ReferenceEquals(document, session.Owner) || windowClosed) return;
        MarkLayerBandDirty(session.LayerId);
        var wrote = session.Owner.ReplaceLayerImage(session.LayerId, session.Capture.Original, session.SessionKey);
        session.Published = session.Capture.Original;
    }

    /// <summary>
    /// Closes the panel's session. OK runs one last tick at full quality and keeps it — the same
    /// <c>commitFilter()</c> that recomputes on the Mac, which is why a cancelled tick never has to be
    /// second-guessed; Cancel hands the captured pixels back. Either way the step the ticks opened is
    /// closed here, and an empty one disappears.
    /// </summary>
    private async Task<bool> FinishDitherPreviewAsync(bool commit)
    {
        if (ditherPreview is not { } session) return false;
        // Closed first: a tick still in flight has lost the right to write, because what happens below is
        // either the layer going back to the capture or the commit's own run taking its place.
        session.Closed = true;
        while (session.Running is { } work)
        {
            try { await work; } catch { }
            if (!ReferenceEquals(session.Running, work)) continue;
            break;
        }
        if (!ReferenceEquals(document, session.Owner) || windowClosed)
        { if (ReferenceEquals(ditherPreview, session)) ditherPreview = null; return false; }
        var applied = false;
        if (commit)
        {
            var (parameters, block, shape) = session.Form.Current();
            session.CommitRun = true;
            applied = await ApplyDitherAsync(session.Owner, session.LayerId, parameters, block, shape, session);
            session.CommitRun = false;
            if (applied) SetStatus(Loc.F("S_Dithered", session.Capture.Name));
            else SetStatus(Loc.T("S_DitherFailed"));
        }
        else if (session.Published is not null) RestoreDitherCapture(session);
        session.Owner.EndTransform(session.SessionKey);
        return applied;
    }

    /// <summary>
    /// Filter ▸ Dither's controls (FilterSheet.swift:184-260). The Mac shows only the parameters the chosen
    /// style can read — a CRT has no halftone screen, an ordered pattern has no diffusion, and a one-pixel
    /// block has no shape to round — so every row here carries the predicate that makes it relevant, in the
    /// sheet's own order. Ranges and defaults are Dither.swift:51-89's.
    /// </summary>
    private sealed class DitherForm
    {
        private readonly MainWindow owner;
        /// <summary>The session this panel is previewing into; the window hands it the same object it opened
        /// the panel with, so a tick can never land in somebody else's document.</summary>
        public readonly DitherPreview Session;
        private readonly ComboBox style = new() { Width = 190 };
        private readonly NumberBox pixelSize = Box(1, 32, 2);
        private readonly NumberBox textSize = Box(6, 64, 14);
        private readonly TextBox characters = new()
        {
            Text = GlyphRasterizer.DefaultCharacters,
            Width = 190,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(GlyphRasterizer.FontFamily),
        };
        private readonly NumberBox lineSpacing = Box(2, 32, 4);
        private readonly NumberBox glow = Box(0, 100, 35);
        private readonly NumberBox scanDots = Box(0, 100, 0);
        private readonly NumberBox wobble = Box(0, 64, 0);
        private readonly NumberBox cellSize = Box(4, 64, 8);
        private readonly NumberBox angle = Box(-90, 90, 45);
        private readonly NumberBox tones = Box(2, 8, 2);
        private readonly NumberBox diffusion = Box(0, 100, 100);
        private readonly NumberBox density = Box(-100, 100, 0);
        private readonly NumberBox contrast = Box(-100, 100, 0);
        private readonly ComboBox colors = new() { Width = 150 };
        private readonly ComboBox pixelShape = new() { Width = 120 };
        private readonly CheckBox lightOnDark = new() { Content = Loc.T("L_DitherLightOnDark"), IsChecked = true };
        private readonly CheckBox preview = new() { Content = Loc.T("L_DitherPreview"), IsChecked = true };
        private readonly List<(string Key, UIElement Row, Func<bool> Visible)> rows = [];
        private TextBlock? notice;

        /// <summary>Puts the panel's orange line up with why the preview failed, or takes it down. Same slot
        /// the Mac's sheet uses for `previewError` (FilterSheet.swift:144-146).</summary>
        /// <summary>What the panel says about the last preview, for a test that must not scrape the tree.</summary>
        public bool PreviewErrorVisible => notice?.Visibility == Visibility.Visible;
        public string PreviewErrorText => notice?.Text ?? string.Empty;

        public void ShowPreviewError(string? message)
        {
            if (notice is null) return;
            notice.Text = message is null ? string.Empty : Loc.F("S_PreviewFailed", message);
            notice.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }
        private AdjustmentColor darkColor = new(0, 0, 0), lightColor = new(1, 1, 1);
        private static readonly (byte R, byte G, byte B) Black = (0, 0, 0), White = (255, 255, 255);

        public DitherForm(MainWindow owner, DitherPreview session)
        {
            this.owner = owner;
            Session = session;
            session.Form = this;
            style.ItemsSource = Enum.GetValues<DitherStyle>().Select(value => Loc.T($"DitherStyle_{value}")).ToList();
            colors.ItemsSource = Enum.GetValues<DitherPalette>().Select(value => Loc.T($"DitherPalette_{value}")).ToList();
            pixelShape.ItemsSource = Enum.GetValues<DitherPixelShape>().Select(value => Loc.T($"DitherShape_{value}")).ToList();
            style.SelectedIndex = 0; colors.SelectedIndex = 0; pixelShape.SelectedIndex = 0;
        }

        /// <summary>The panel's body, handed to the window's floating host (#37). It used to build a
        /// ContentDialog around itself, which dimmed the window and took the pointer — the one thing a live
        /// preview cannot afford, because the picture it is computing is what the mask covers.</summary>
        public StackPanel BuildPanel()
        {
            Add("style", Loc.T("L_DitherStyle"), style);
            Add("pixelSize", Loc.T("L_DitherPixelSize"), pixelSize, () => Chosen.UsesPixelSize());
            Add("textSize", Loc.T("L_DitherTextSize"), textSize, () => Chosen == DitherStyle.Ascii);
            Add("lineSpacing", Loc.T("L_DitherLineSpacing"), lineSpacing, () => Chosen == DitherStyle.Scanlines);
            Add("glow", Loc.T("L_DitherGlow"), glow, () => Chosen == DitherStyle.Scanlines);
            Add("dots", Loc.T("L_DitherScanlineDots"), scanDots, () => Chosen == DitherStyle.Scanlines);
            Add("wobble", Loc.T("L_DitherWobble"), wobble, () => Chosen == DitherStyle.Scanlines);
            Add("cell", Loc.T("L_DitherCellSize"), cellSize, () => Chosen.IsHalftone());
            Add("angle", Loc.T("L_DitherAngle"), angle, () => Chosen.IsHalftone());
            Add("characters", Loc.T("L_DitherCharacters"), characters, () => Chosen == DitherStyle.Ascii);
            Add("tones", Loc.T("L_DitherLevels"), tones, () => Chosen.HasTones());
            Add("diffusion", Loc.T("L_DitherDiffusion"), diffusion, () => Chosen.Diffuses());
            Add("density", Loc.T("L_DitherDensity"), density, () => true);
            Add("contrast", Loc.T("L_DitherContrast"), contrast, () => true);
            Add("colors", Loc.T("L_DitherColors"), colors, () => true);
            Add("dark", owner.ColorChipRow(Loc.T("L_DitherDarkColor"), Win(darkColor), picked => ChooseDarkColor(picked), null),
                () => ChosenColors == DitherPalette.TwoColors);
            Add("light", owner.ColorChipRow(Loc.T("L_DitherLightColor"), Win(lightColor), picked => ChooseLightColor(picked), null),
                () => ChosenColors == DitherPalette.TwoColors);
            Add("shape", Loc.T("L_DitherPixelShape"), pixelShape, () => Chosen.UsesPixelSize() && Int(pixelSize, 2) > 1);
            Add("lightOnDark", lightOnDark, () => Chosen.DrawsMarks());
            Add("preview", preview, () => true);       // the sheet's own Preview switch, last of the rows (FilterSheet.swift:143)
            style.SelectionChanged += (_, _) => Changed();
            colors.SelectionChanged += (_, _) => Changed();
            pixelShape.SelectionChanged += (_, _) => Changed();
            pixelSize.ValueChanged += (_, _) => Changed();
            foreach (var box in new[] { textSize, lineSpacing, glow, scanDots, wobble, cellSize, angle, tones, diffusion, density, contrast })
                box.ValueChanged += (_, _) => Changed();
            characters.TextChanged += (_, _) => Changed();
            preview.Checked += (_, _) => PreviewToggled(true);
            preview.Unchecked += (_, _) => PreviewToggled(false);
            var panel = new StackPanel { Spacing = 8 };
            foreach (var (_, row, _) in rows) panel.Children.Add(row);
            notice = new TextBlock
            {
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
                Visibility = Visibility.Collapsed, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
            };
            panel.Children.Add(notice);
            // The same line the Camera Raw panel shows (FilterSheet.swift:144-149): a filter runs inside the
            // selection, and the picture the panel is promising is only part of the layer.
            if (Session.Capture.Clip is not null)
                panel.Children.Add(new TextBlock
                {
                    Text = Loc.T("L_FilterLimitedToSelection"), Opacity = 0.7,
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                });
            Update();
            return panel;
        }

        /// <summary>The swatches' entry point, so the panel's own chip and a test go through one door.</summary>
        public void ChooseDarkColor(Windows.UI.Color picked) { darkColor = Adjust(picked); Update(); }
        public void ChooseLightColor(Windows.UI.Color picked) { lightColor = Adjust(picked); Update(); }

        /// <summary>Whether one named row is on screen — the panel's answer to the Mac's per-style `if`s.
        /// A row the panel never built is not a hidden row, so it says which one is missing by name.</summary>
        public bool RowShown(string key)
        {
            var entry = rows.FirstOrDefault(row => row.Key == key);
            if (entry.Row is null) throw new InvalidOperationException($"the panel never built a {key} row");
            return entry.Row.Visibility == Visibility.Visible;
        }

        /// <summary>The fields as the engine wants them, for a test that checks what the panel stands for
        /// without waiting for a worker to run.</summary>
        public int ValueOf(string key) => key switch
        {
            "pixelSize" => Int(pixelSize, 2), "textSize" => Int(textSize, 14),
            "lineSpacing" => Int(lineSpacing, 4), "glow" => Int(glow, 35), "dots" => Int(scanDots, 0),
            "wobble" => Int(wobble, 0), "cell" => Int(cellSize, 8), "angle" => Int(angle, 45),
            "tones" => Int(tones, 2), "diffusion" => Int(diffusion, 100), "density" => Int(density, 0),
            "contrast" => Int(contrast, 0), _ => throw new ArgumentException($"The panel has no field {key}."),
        };

        public string ChosenStyleName => Chosen.ToString();
        public bool MarksLightOnDark => lightOnDark.IsChecked == true;
        public CheckBox LightOnDarkField => lightOnDark;

        private DitherStyle Chosen => (DitherStyle)Math.Clamp(style.SelectedIndex, 0, Enum.GetValues<DitherStyle>().Length - 1);
        private DitherPalette ChosenColors => (DitherPalette)Math.Clamp(colors.SelectedIndex, 0, 2);

        private void Update()
        {
            foreach (var (_, row, visible) in rows) row.Visibility = visible() ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>What every control's change handler does: re-decide which rows the chosen style can read,
        /// then hand the new values to the preview, so the canvas shows what OK is about to write.</summary>
        private void Changed()
        {
            Update();
            if (preview.IsChecked == true) owner.PreviewDither(Session);
        }

        /// <summary>The sheet's Preview switch: off puts the untouched pixels back without losing any of the
        /// settings, on dithers them again from the same capture.</summary>
        private void PreviewToggled(bool on)
        {
            if (on) owner.PreviewDither(Session);
            else owner.ShowDitherCapture(Session);
        }

        /// <summary>The values as the engine wants them, with ASCII's glyph cells drawn — what the panel
        /// would commit right now, and therefore what its preview has to be made of.</summary>
        public (DitherParams Parameters, int Block, DitherPixelShape Shape) Current()
        {
            var (parameters, block, shape) = Result;
            if (parameters.Style != DitherStyle.Ascii) return (parameters, block, shape);
            var (maps, coverage, glyphWidth, glyphHeight) = RenderGlyphs();
            return (parameters with
            {
                GlyphMaps = maps, GlyphCoverage = coverage, GlyphWidth = glyphWidth, GlyphHeight = glyphHeight,
            }, block, shape);
        }

        private void Add(string key, string label, UIElement field, Func<bool>? visible = null)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock { Text = label, Width = 130, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(field);
            rows.Add((key, row, visible ?? (() => true)));
        }

        private void Add(string key, UIElement row, Func<bool> visible) => rows.Add((key, row, visible));

        /// <summary>Chooses a style the way the picker does, so the panel's own change handler runs.</summary>
        public void ChooseStyle(int index) => style.SelectedIndex = index;

        /// <summary>Chooses the palette the way the picker does (0 Black & White, 1 Two Colors, 2 Original).</summary>
        public void ChoosePalette(int index) => colors.SelectedIndex = index;

        /// <summary>Chooses the block shape the way the picker does (0 Square, 1 Dot).</summary>
        public void ChooseShape(int index) => pixelShape.SelectedIndex = index;

        /// <summary>ASCII's line height in layer pixels — the Text Size row (Dither.swift:64-65).</summary>
        public int GlyphLineHeight => Int(textSize, 14);

        /// <summary>The characters field itself, so a test types into the same box the user does.</summary>
        public TextBox CharactersField => characters;

        /// <summary>The sheet's Preview switch, so a test can flip it the way a user does.</summary>
        public CheckBox PreviewField => preview;

        /// <summary>What the panel will draw with: the field's characters, newlines dropped and capped at 64,
        /// or the ramp when the field holds nothing (Dither.swift:107 and 172).</summary>
        public string GlyphCharacters => GlyphRasterizer.UsableCharacters(characters.Text) is { Length: > 0 } usable
            ? usable : GlyphRasterizer.DefaultCharacters;

        /// <summary>Draws those characters as glyph cells, on the UI thread that owns the fonts.</summary>
        public (byte[] Maps, float[] Coverage, int Width, int Height) RenderGlyphs() =>
            GlyphRasterizer.Render(GlyphCharacters, GlyphLineHeight);

        /// <summary>Sets one of the panel's number fields through the control itself.</summary>
        public void SetValue(string key, double value)
        {
            (key switch
            {
                "pixelSize" => pixelSize, "textSize" => textSize, "lineSpacing" => lineSpacing,
                "glow" => glow, "dots" => scanDots, "wobble" => wobble,
                "cell" => cellSize, "angle" => angle, "tones" => tones, "diffusion" => diffusion,
                "density" => density, "contrast" => contrast,
                _ => throw new ArgumentException($"The panel has no field {key}."),
            }).Value = value;
        }

        /// <summary>The parameters the panel stands for, the block the chunky-pixel pass uses, and whether
        /// those blocks are squares or dots — the same three things the Mac's apply() reads
        /// (Dither.swift:111-143).</summary>
        public (DitherParams Parameters, int Block, DitherPixelShape Shape) Result
        {
            get
            {
                bool twoColors = ChosenColors == DitherPalette.TwoColors;
                var parameters = new DitherParams
                {
                    Style = Chosen,
                    Levels = Int(tones, 2),
                    Diffusion = Float(diffusion, 100) / 100f,
                    Density = Float(density, 0) / 100f,
                    Contrast = Float(contrast, 0) / 100f,
                    Cell = Int(cellSize, 8),
                    LineSpacing = Int(lineSpacing, 4),
                    Angle = Float(angle, 45) * MathF.PI / 180f,
                    LightOnDark = lightOnDark.IsChecked == true,
                    OriginalColors = ChosenColors == DitherPalette.Original,
                    Dark = twoColors ? Bytes(darkColor) : Black,
                    Light = twoColors ? Bytes(lightColor) : White,
                    Dots = Float(scanDots, 0) / 100f,
                    Wobble = Float(wobble, 0),
                    Glow = Float(glow, 35) / 100f,
                };
                int block = Chosen.UsesPixelSize() ? Int(pixelSize, 2) : 1;
                return (parameters, block, block > 1 && pixelShape.SelectedIndex == 1 ? DitherPixelShape.Dot : DitherPixelShape.Square);
            }
        }

        private static NumberBox Box(double min, double max, double value) => new() { Minimum = min, Maximum = max, Value = value, Width = 110 };
        private int Int(NumberBox box, int fallback) => (int)Math.Round(double.IsNaN(box.Value) ? fallback : Math.Clamp(box.Value, box.Minimum, box.Maximum));
        private float Float(NumberBox box, double fallback) => (float)(double.IsNaN(box.Value) ? fallback : Math.Clamp(box.Value, box.Minimum, box.Maximum));
        private static (byte R, byte G, byte B) Bytes(AdjustmentColor color) =>
            ((byte)Math.Round(Math.Clamp(color.Red, 0, 1) * 255), (byte)Math.Round(Math.Clamp(color.Green, 0, 1) * 255), (byte)Math.Round(Math.Clamp(color.Blue, 0, 1) * 255));
        private static AdjustmentColor Adjust(Windows.UI.Color color) => new(color.R / 255.0, color.G / 255.0, color.B / 255.0);
        private static Windows.UI.Color Win(AdjustmentColor color) => Microsoft.UI.ColorHelper.FromArgb(255,
            (byte)Math.Round(Math.Clamp(color.Red, 0, 1) * 255), (byte)Math.Round(Math.Clamp(color.Green, 0, 1) * 255),
            (byte)Math.Round(Math.Clamp(color.Blue, 0, 1) * 255));
    }
}
