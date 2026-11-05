#if DEBUG
using System.Diagnostics;
using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private Func<Surface, Task>? beforeDitherCompute;
    private Action? beforeDitherUpscale;
    private Func<ContentDialog, Task<ContentDialogResult>>? projectDialogProbe;
    /// <summary>The panel Filter ▸ Dither has open right now, so a probe can choose a style through the picker
    /// and read back which rows the panel kept.</summary>
    private DitherForm? ditherFormProbe;
    private Task<bool>? ditherProbeWork;
    /// <summary>Signals that the panel's whole preview session is over — every coalesced tick drained and the
    /// keep-or-restore decision applied. A probe that only awaits the last tick would read the layer while
    /// the session was still writing it, and a cancelled panel closes with nothing applied, so "closed" and
    /// "the commit landed" are two different facts and are reported as two.</summary>
    private TaskCompletionSource<bool>? ditherProbeFinish;
    private bool ditherProbeApplied;
    // The request task answers as soon as the document retires, so it cannot show when the worker
    // thread itself lets go of the CPU. The probe times the thread-pool task next to it.
    private Task? ditherProbeWorker;

    private async Task VerifyDitherOwnershipAsync(Dictionary<string, object> report)
    {
        var failures = new List<string>();
        int inputFailures = renderInputFailureCount, publicationFailures = renderPublicationFailureCount;
        var parameters = new DitherParams { Style = DitherStyle.Atkinson, Diffusion = 0 };
        async Task Check(string name, Func<Task> check)
        {
            try { await check(); report[name] = "passed"; }
            catch (Exception ex) { failures.Add(name + ": " + ex.Message); report[name] = ex.ToString(); }
            finally { beforeDitherCompute = null; beforeDitherUpscale = null; projectDialogProbe = null; ditherProbeWorker = null; ditherFormProbe = null; ditherProbeFinish = null; ditherProbeApplied = false; }
        }
        foreach (int block in new[] { 1, 3 })
        {
            await Check("ditherBlock" + block + "ExactPixelsGpuAndOneUndo", async () =>
            {
                var scene = DitherScene(); OpenDocument(scene); await WaitForFramesAsync();
                var id = scene.Snapshot.Manifest.ActiveLayerId!.Value; var original = scene.Snapshot.Images[id];
                Require(await ApplyDitherAsync(scene, id, parameters, block), "Dither did not commit."); await WaitForFramesAsync();
                var result = scene.Snapshot.Images[id];
                Require(result.Width == 7 && result.Height == 5, "Dither changed dimensions.");
                for (int i = 0; i < result.Pixels.Length; i += 4)
                    Require(result.Pixels[i] == 0 && result.Pixels[i + 1] == 0 && result.Pixels[i + 2] == 0 && result.Pixels[i + 3] == 255,
                        "Independent opaque gray 96, two levels, zero diffusion must be black.");
                AssertUploadedFrame(scene);
                OnUndo(this, new()); await WaitForFramesAsync();
                Require(!scene.CanUndo && ReferenceEquals(original, scene.Snapshot.Images[id]), "Dither Undo did not restore the original in one step.");
                AssertUploadedFrame(scene);
            });
        }
        await Check("ditherStaysInsideTheSelection", async () =>
        {
            var scene = DitherScene(); OpenDocument(scene); await WaitForFramesAsync();
            var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
            var before = scene.Snapshot.Images[id];
            var untouched = (byte[])before.Pixels.Clone();
            // Three columns by two rows of the layer's own 7×5 grid, selected through its document
            // placement at (2,3): the clip has to arrive mapped onto the layer, not the canvas.
            scene.SetSelection(Selection.Rectangle(32, 24, 2, 3, 3, 2));
            Require(await ApplyDitherAsync(scene, id, parameters, 1), "The clipped dither did not commit.");
            await WaitForFramesAsync();
            var after = scene.Snapshot.Images[id];
            Require(!ReferenceEquals(after, before), "the clipped dither published the surface it read");
            for (int y = 0; y < 5; y++)
            {
                for (int x = 0; x < 7; x++)
                {
                    int at = y * after.Stride + x * 4;
                    if (x < 3 && y < 2)
                    {
                        Require(after.Pixels[at] == 0 && after.Pixels[at + 3] == 255, $"({x},{y}) is inside the selection but was not dithered.");
                    }
                    else
                    {
                        Require(after.Pixels[at] == untouched[at] && after.Pixels[at + 3] == untouched[at + 3],
                            $"({x},{y}) sits outside the selection but the dither changed it.");
                    }
                }
            }
        });
        await Check("ditherRetirementDoesNotWaitForHeldWorker", async () =>
        {
            var scene = DitherScene(); OpenDocument(scene); await WaitForFramesAsync();
            var id = scene.Snapshot.Manifest.ActiveLayerId!.Value; var original = scene.Snapshot.Images[id];
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            beforeDitherCompute = _ => { entered.TrySetResult(); return release.Task; };
            var work = ApplyDitherAsync(scene, id, parameters, 1);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var next = DitherScene(); OpenDocument(next); await WaitForFramesAsync();
                Require(!await work.WaitAsync(TimeSpan.FromSeconds(2)), "Retired dither claimed a commit.");
                Require(!release.Task.IsCompleted && !scene.CanUndo && !next.CanUndo && ReferenceEquals(original, scene.Snapshot.Images[id]),
                    "Retirement completed the worker or changed history.");
                AssertUploadedFrame(next);
            }
            finally { release.TrySetResult(); await work.WaitAsync(TimeSpan.FromSeconds(5)); }
        });
        await Check("ditherEngineStopsWhenRetiredMidRun", async () =>
        {
            // The engine's own sweeps are the cost of a large dither, so a 4K layer is the case
            // retirement used to be unable to interrupt: it could only bail once Apply returned.
            var large = new DitherParams { Style = DitherStyle.Atkinson, Diffusion = 0.75f };
            var scene = DitherFullScene(4096); OpenDocument(scene); await WaitForFramesAsync();
            var id = scene.Snapshot.Manifest.ActiveLayerId!.Value; var original = scene.Snapshot.Images[id];
            var timer = Stopwatch.StartNew();
            Require(await ApplyDitherAsync(scene, id, large, 1), "Full-size dither did not commit.");
            double controlMs = timer.Elapsed.TotalMilliseconds;
            Require(scene.CanUndo && !ReferenceEquals(original, scene.Snapshot.Images[id]), "Full-size dither left no history or image.");
            OnUndo(this, new()); await WaitForFramesAsync(); Require(!scene.CanUndo, "Full-size dither needed more than one Undo.");

            scene = DitherFullScene(4096); OpenDocument(scene); await WaitForFramesAsync();
            id = scene.Snapshot.Manifest.ActiveLayerId!.Value; original = scene.Snapshot.Images[id];
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            beforeDitherCompute = _ => { entered.TrySetResult(); return Task.CompletedTask; };
            var work = ApplyDitherAsync(scene, id, large, 1);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await Task.Delay(20);
            timer.Restart();
            var next = DitherScene(); OpenDocument(next); await WaitForFramesAsync();
            Require(!await work.WaitAsync(TimeSpan.FromSeconds(20)), "Retired dither claimed a commit.");
            var retiredWorker = ditherProbeWorker;
            Require(retiredWorker is not null, "Retired dither worker was never captured.");
            await retiredWorker!.WaitAsync(TimeSpan.FromSeconds(20));
            double abandonedMs = timer.Elapsed.TotalMilliseconds;
            report["ditherEngineRetirementMidRun"] = new { controlMs, abandonedMs };
            Require(abandonedMs < Math.Max(60, controlMs / 3), "Retiring a running engine took as long as finishing it.");
            Require(!scene.CanUndo && ReferenceEquals(original, scene.Snapshot.Images[id]), "Retired dither changed the retired document or its history.");
            Require(!next.CanUndo, "Retired dither added history to the replacement document.");
            // The worker's private copy is the price of never reading live pixels; the engine change
            // does not touch it, so its cost is reported next to the run's rather than assumed small.
            var snapshotCost = Stopwatch.StartNew();
            var clone = scene.Snapshot.Images[id].Clone();
            report["ditherSourceSnapshot4KCost"] = new { sourceCloneMs = snapshotCost.Elapsed.TotalMilliseconds,
                bytes = clone.Pixels.Length };
        });
        await Check("ditherLateOldWorkerKeepsNewBusyAndBothDocuments", async () =>
        {
            var scene = DitherScene(); OpenDocument(scene); await WaitForFramesAsync();
            var id = scene.Snapshot.Manifest.ActiveLayerId!.Value; var original = scene.Snapshot.Images[id];
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            beforeDitherCompute = _ => { entered.TrySetResult(); return release.Task; };
            var work = ApplyDitherAsync(scene, id, parameters, 3);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var next = DitherScene(); OpenDocument(next); await WaitForFramesAsync();
                // A separate owner of the progress indicator is a deliberate synthetic fence check.
                SetBusy(true); SetStatus("new-owner-busy"); release.TrySetResult();
                await work.WaitAsync(TimeSpan.FromSeconds(5)); await Task.Delay(100); await WaitForFramesAsync();
                Require(!scene.CanUndo && !next.CanUndo && ReferenceEquals(original, scene.Snapshot.Images[id]), "Late worker wrote to a retired document.");
                Require(StatusProgress.Visibility == Visibility.Visible && StatusText.Text == "new-owner-busy", "Old worker cleared another owner's busy/status.");
                AssertUploadedFrame(next);
            }
            finally { release.TrySetResult(); await work.WaitAsync(TimeSpan.FromSeconds(5)); SetBusy(false); }
        });
        foreach (string change in new[] { "replace", "revision", "placement" })
        {
            await Check("ditherRejectsChanged" + change, async () =>
            {
                var scene = DitherScene(); OpenDocument(scene); await WaitForFramesAsync();
                var id = scene.Snapshot.Manifest.ActiveLayerId!.Value; var source = scene.Snapshot.Images[id];
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Surface? workerSource = null;
                beforeDitherCompute = input => { workerSource = input; entered.TrySetResult(); return release.Task; };
                var work = ApplyDitherAsync(scene, id, parameters, 1);
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    if (change == "replace") scene.ReplaceLayerImage(id, source.Clone());
                    else if (change == "revision") { source.Pixels[0] = 200; source.MarkModified(); }
                    else scene.Snapshot.Manifest.Layers[0].Transform.Origin = new(4, 2);
                    var expected = scene.Snapshot.Images[id]; bool hadUndo = scene.CanUndo;
                    Require(workerSource is not null && !ReferenceEquals(workerSource, source) && workerSource.Pixels[0] == 96,
                        "Dither worker read the mutable live source.");
                    release.TrySetResult();
                    Require(!await work.WaitAsync(TimeSpan.FromSeconds(5)), "Stale dither overwrote a changed source or placement.");
                    Require(ReferenceEquals(expected, scene.Snapshot.Images[id]) && scene.CanUndo == hadUndo, "Rejected dither changed the image/history.");
                    Require(StatusText.Text == Loc.T("S_DitherSourceChanged"), "Rejected dither had no visible explanation.");
                }
                finally { release.TrySetResult(); await work.WaitAsync(TimeSpan.FromSeconds(5)); }
            });
        }
        foreach (string fault in new[] { "compute", "upscale" })
        {
            await Check("dither" + fault + "FailureHasFeedbackAndNoPartialCommit", async () =>
            {
                var scene = DitherScene(); OpenDocument(scene); await WaitForFramesAsync();
                var id = scene.Snapshot.Manifest.ActiveLayerId!.Value; var source = scene.Snapshot.Images[id];
                SetStatus("before-dither-failure");
                if (fault == "compute") beforeDitherCompute = _ => Task.FromException(new IOException("Expected dither compute fault."));
                else beforeDitherUpscale = () => throw new IOException("Expected dither upscale fault.");
                Require(!await ApplyDitherAsync(scene, id, parameters, 3).WaitAsync(TimeSpan.FromSeconds(5)), "Failed dither claimed success.");
                await WaitForFramesAsync();
                Require(!scene.CanUndo && ReferenceEquals(source, scene.Snapshot.Images[id]), "Failed dither published a partial small image.");
                Require(StatusText.Text == Loc.T("S_DitherFailed") && StatusProgress.Visibility != Visibility.Visible, "Dither failure was silent or left busy state.");
                AssertUploadedFrame(scene);
            });
        }
        await Check("ditherDialogRejectsReplacedOwnerWithSameLayerId", async () =>
        {
            var scene = DitherScene(); OpenDocument(scene); await WaitForFramesAsync();
            var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
            var replacement = new EditorDocument(new ProjectSnapshot { Manifest = ManifestJson.Deserialize(ManifestJson.Serialize(scene.Snapshot.Manifest)),
                Images = new Dictionary<Guid, Surface> { [id] = scene.Snapshot.Images[id].Clone() }, Masks = new Dictionary<Guid, MaskSurface>() });
            int computed = 0; beforeDitherCompute = _ => { Interlocked.Increment(ref computed); return Task.CompletedTask; }; ditherProbeWork = null;
            OnDither(this, new RoutedEventArgs());
            Require(FloatingPanelIsOpen("filter"), "Filter ▸ Dither opened no panel.");
            OpenDocument(replacement);
            // The modal dialog used to stay up over a document it no longer belonged to; a panel that is part
            // of the window has to go with the session, or it names a layer of a document that is gone.
            Require(!FloatingPanelIsOpen("filter"), "replacing the document left the filter panel on screen.");
            await WaitForFramesAsync();
            Require(computed == 0 && !scene.CanUndo && !replacement.CanUndo, "Old dither panel began work in a replacement document with the same layer ID.");
            AssertUploadedFrame(replacement);
        });
        await Check("ditherOnlyOneWorkerOwnsTheOperation", async () =>
        {
            var scene = DitherScene(); OpenDocument(scene); await WaitForFramesAsync(); var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int workers = 0;
            beforeDitherCompute = _ => { Interlocked.Increment(ref workers); entered.TrySetResult(); return release.Task; };
            var first = ApplyDitherAsync(scene, id, parameters, 1); Task<bool>? second = null;
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); second = ApplyDitherAsync(scene, id, parameters, 1);
                Require(!await second.WaitAsync(TimeSpan.FromSeconds(2)) && workers == 1, "A second dither began while one was active.");
            }
            finally { release.TrySetResult(); await first.WaitAsync(TimeSpan.FromSeconds(5)); if (second is not null) await second.WaitAsync(TimeSpan.FromSeconds(5)); }
        });
        await Check("ditherPublicationWaitsForActiveCanvasReader", async () =>
        {
            var scene = DitherScene(); OpenDocument(scene); await WaitForFramesAsync(); var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
            var original = scene.Snapshot.Images[id]; var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            beforeDitherCompute = _ => { entered.TrySetResult(); return release.Task; };
            var work = ApplyDitherAsync(scene, id, parameters, 1);
            using var readerRelease = new ManualResetEventSlim(); var readerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                beforeFrameRender = () => { readerEntered.TrySetResult(); if (!readerRelease.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Dither reader fixture timed out."); };
                RenderCanvas(); await readerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); beforeFrameRender = null;
                release.TrySetResult(); await Task.Delay(100);
                Require(!work.IsCompleted && ReferenceEquals(original, scene.Snapshot.Images[id]) && !scene.CanUndo,
                    "Dither published while the renderer still owned a reader.");
                readerRelease.Set(); Require(await work.WaitAsync(TimeSpan.FromSeconds(5)), "Queued dither publication was lost.");
                await WaitForFramesAsync(); AssertUploadedFrame(scene);
            }
            finally { beforeFrameRender = null; readerRelease.Set(); release.TrySetResult(); await work.WaitAsync(TimeSpan.FromSeconds(5)); await WaitForFramesAsync(); }
        });
        await Check("ditherHeldSaveWaitsAndWritesFinishedPixels", async () =>
        {
            var scene = DitherScene(); OpenDocument(scene); await WaitForFramesAsync(); var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
            string path = Path.Combine(AppContext.BaseDirectory, "dither-save-" + Guid.NewGuid().ToString("N") + ".comp");
            await ProjectStore.SaveAsync(scene.Snapshot, path); scene.MarkSaved(); projectPath = path;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            beforeDitherCompute = _ => { entered.TrySetResult(); return release.Task; };
            var work = ApplyDitherAsync(scene, id, parameters, 1); Task<bool>? saving = null;
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); saving = SaveNow(); await Task.Delay(100);
                Require(!saving.IsCompleted, "Save overtook the held dither worker.");
                release.TrySetResult(); Require(await work.WaitAsync(TimeSpan.FromSeconds(5)) && await saving.WaitAsync(TimeSpan.FromSeconds(5)), "Dither/save did not finish.");
                var loaded = await ProjectStore.LoadAsync(path);
                Require(loaded.Images[id].Pixels.AsSpan().SequenceEqual(scene.Snapshot.Images[id].Pixels) && loaded.Images[id].Pixels[0] == 0,
                    "Save wrote the original instead of the completed dither.");
                Require(!scene.IsDirty && scene.CanUndo, "Saved dither has wrong dirty/history state.");
                OnUndo(this, new()); await WaitForFramesAsync(); Require(!scene.CanUndo && scene.Snapshot.Images[id].Pixels[0] == 96, "Saved dither had extra history.");
            }
            finally { release.TrySetResult(); await work.WaitAsync(TimeSpan.FromSeconds(5)); if (saving is not null) await saving.WaitAsync(TimeSpan.FromSeconds(5)); }
        });
        await Check("ditherOutputRetiresWithoutCompletingHeldWorker", async () =>
        {
            var scene = DitherScene(); OpenDocument(scene); await WaitForFramesAsync(); var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            beforeDitherCompute = _ => { entered.TrySetResult(); return release.Task; };
            var work = ApplyDitherAsync(scene, id, parameters, 1);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); var output = FinishTransformBeforeOutputAsync(); var saving = SaveNow(); await Task.Delay(100);
                Require(!output.IsCompleted && !saving.IsCompleted, "Output/save did not wait for active dither.");
                var replacement = DitherScene(); OpenDocument(replacement); await output.WaitAsync(TimeSpan.FromSeconds(5));
                Require(!await saving.WaitAsync(TimeSpan.FromSeconds(5)), "Retired save continued into the replacement document.");
                Require(!await work.WaitAsync(TimeSpan.FromSeconds(5)) && !release.Task.IsCompleted && !scene.CanUndo && !replacement.CanUndo,
                    "Retired output committed dither or completed the held worker.");
            }
            finally { release.TrySetResult(); await work.WaitAsync(TimeSpan.FromSeconds(5)); }
        });
        await Check("ditherDialogLocalizedStylesAndClearedPixelSize", async () =>
        {
            var scene = DitherScene(); OpenDocument(scene); await WaitForFramesAsync(); string[]? labels = null; ditherProbeWork = null;
            OnDither(this, new RoutedEventArgs());
            var content = (StackPanel)FloatingPanelBody("filter")!;
            var stylePicker = (ComboBox)((StackPanel)content.Children[0]).Children[1];
            labels = ((System.Collections.IEnumerable)stylePicker.ItemsSource).Cast<string>().ToArray();
            ((NumberBox)((StackPanel)content.Children[1]).Children[1]).Value = double.NaN;
            CloseFloatingPanel("filter", commit: true);
            Require(await AwaitDitherSessionAsync(5) && ditherProbeApplied, "Cleared pixel size did not reach the document.");
            Require(labels is not null && labels.SequenceEqual(Enum.GetValues<DitherStyle>().Select(style => Loc.T("DitherStyle_" + style))), "Dither style labels did not use their existing translations.");
            await WaitForFramesAsync(); AssertUploadedFrame(scene);
        });
        await Check("ditherPanelShowsOnlyWhatTheStyleCanUse", async () =>
        {
            var scene = DitherScene(); OpenDocument(scene); await WaitForFramesAsync();
            string committed = "";
            string? panelFault = null;
            void ReadPanel()
            {
                var panel = ditherFormProbe ?? throw new InvalidOperationException("The dither panel never published itself.");
                Require(panel.RowShown("pixelSize") && panel.RowShown("tones") && panel.RowShown("diffusion")
                        && panel.RowShown("shape") && panel.RowShown("density") && panel.RowShown("contrast"),
                    "the classic diffusion style lost the controls it reads");
                Require(!panel.RowShown("lineSpacing") && !panel.RowShown("glow") && !panel.RowShown("dots")
                        && !panel.RowShown("wobble")
                        && !panel.RowShown("cell") && !panel.RowShown("angle") && !panel.RowShown("lightOnDark")
                        && !panel.RowShown("dark") && !panel.RowShown("light")
                        && !panel.RowShown("textSize") && !panel.RowShown("characters"),
                    "Atkinson was handed scanline, screen, glyph or mark controls it cannot read");
                panel.ChooseStyle((int)DitherStyle.Scanlines);
                Require(!panel.RowShown("pixelSize") && !panel.RowShown("shape") && !panel.RowShown("tones")
                        && !panel.RowShown("diffusion") && !panel.RowShown("lightOnDark") && !panel.RowShown("cell")
                        && !panel.RowShown("textSize") && !panel.RowShown("characters")
                        && panel.RowShown("lineSpacing") && panel.RowShown("glow")
                        && panel.RowShown("dots") && panel.RowShown("wobble"),
                    "the CRT panel is not the scanline one");
                int openedBloom = panel.ValueOf("glow");
                Require(openedBloom == 35, $"the bloom did not open on the sheet's 35% (it read {openedBloom}).");
                panel.ChooseStyle((int)DitherStyle.HalftoneDots);
                Require(panel.RowShown("cell") && panel.RowShown("angle") && panel.RowShown("lightOnDark")
                        && panel.RowShown("pixelSize") && !panel.RowShown("tones") && !panel.RowShown("diffusion")
                        && !panel.RowShown("lineSpacing") && !panel.RowShown("glow")
                        && !panel.RowShown("textSize") && !panel.RowShown("characters"),
                    "the halftone panel lost its screen, or kept diffusion");
                panel.ChooseStyle((int)DitherStyle.Ascii);
                Require(!panel.RowShown("pixelSize") && !panel.RowShown("shape") && panel.RowShown("lightOnDark")
                        && panel.RowShown("textSize") && panel.RowShown("characters")
                        && !panel.RowShown("cell") && !panel.RowShown("angle") && !panel.RowShown("lineSpacing")
                        && !panel.RowShown("glow"),
                    "ASCII kept the block it draws at full resolution, or lost the two rows that make it ASCII");
                Require(panel.GlyphLineHeight == 14 && panel.GlyphCharacters == GlyphRasterizer.DefaultCharacters,
                    $"the glyph rows did not open on the Mac's defaults ({panel.GlyphLineHeight}, {panel.GlyphCharacters}).");
                panel.SetValue("textSize", 24);
                panel.CharactersField.Text = " .o";
                Require(panel.GlyphLineHeight == 24 && panel.GlyphCharacters == " .o",
                    "the text size or the characters never left the panel's own fields");
                panel.CharactersField.Text = "";
                Require(panel.GlyphCharacters == GlyphRasterizer.DefaultCharacters,
                    "an emptied characters field gave up instead of falling back to the ramp (Dither.swift:172)");
                panel.CharactersField.Text = new string('c', 70);
                Require(panel.GlyphCharacters == new string('c', 64),
                    $"the field's 64-character cap did not survive normalization (Dither.swift:107): {panel.GlyphCharacters.Length} characters.");
                panel.CharactersField.Text = GlyphRasterizer.DefaultCharacters;
                panel.SetValue("textSize", 14);
                panel.ChooseStyle((int)DitherStyle.HalftoneLines);
                panel.SetValue("pixelSize", 1);
                Require(!panel.RowShown("shape"), "a one-pixel block still offers a shape to round it into");
                panel.SetValue("pixelSize", 4); panel.SetValue("cell", 12); panel.SetValue("angle", -30);
                panel.SetValue("density", -50); panel.SetValue("contrast", 25); panel.SetValue("diffusion", 60);
                panel.SetValue("tones", 5); panel.SetValue("dots", 40); panel.SetValue("wobble", 6);
                // The full-resolution rule is the panel's too: the CRT must not inherit a block just because
                // the row that shows it is hidden (Dither.swift:114).
                panel.ChooseStyle((int)DitherStyle.Scanlines);
                panel.SetValue("glow", 60);
                var crt = panel.Result;
                Require(crt.Block == 1 && crt.Shape == DitherPixelShape.Square,
                    "the CRT was given a chunky block it does not draw, or a shape it cannot round");
                Require(Math.Abs(crt.Parameters.Glow - .6f) < .001,
                    $"the bloom slider never reached the parameters ({crt.Parameters.Glow:R}).");
                panel.ChooseStyle((int)DitherStyle.HalftoneLines);
                panel.ChoosePalette(1);
                Require(panel.RowShown("dark") && panel.RowShown("light"), "the two-color palette did not show its two swatches");
                panel.ChooseShape(1);
                panel.ChooseDarkColor(Microsoft.UI.ColorHelper.FromArgb(255, 12, 34, 56));
                panel.ChoosePalette(2);
                var original = panel.Result.Parameters;
                Require(original.OriginalColors && original.Dark.R == 0 && original.Dark.G == 0 && original.Dark.B == 0
                        && original.Light.R == 255 && original.Light.G == 255 && original.Light.B == 255,
                    "the Original palette still carried the picked pair into the engine");
                panel.ChoosePalette(1);
                panel.LightOnDarkField.IsChecked = false;      // the marks switch, flipped by hand
                var (parameters, block, shape) = panel.Result;
                Require(block == 4 && shape == DitherPixelShape.Dot && parameters.Style == DitherStyle.HalftoneLines,
                    $"the panel's own fields did not reach the parameters (block {block}, shape {shape}, style {parameters.Style}).");
                Require(parameters.Cell == 12 && parameters.Angle > -0.5236 - .001 && parameters.Angle < -0.5236 + .001,
                    $"the screen lost its size or angle (cell {parameters.Cell}, angle {parameters.Angle:R}).");
                Require(parameters.Dark == (12, 34, 56) && parameters.Light == (255, 255, 255) && !parameters.OriginalColors,
                    "the two-color swatches did not reach the engine, or Original was still claimed");
                Require(Math.Abs(parameters.Density + .5f) < .001 && Math.Abs(parameters.Contrast - .25f) < .001
                        && Math.Abs(parameters.Diffusion - .6f) < .001 && Math.Abs(parameters.Dots - .4f) < .001
                        && parameters.Wobble == 6f && parameters.Levels == 5 && !parameters.LightOnDark,
                    "a slider or the marks switch did not survive the trip to the parameters");
                committed = $"{block}|{shape}";
            }
            OnDither(this, new RoutedEventArgs());
            // C13e/C13h: same seam as the effects panel — the floating panel's root is the part a sweep can walk.
            // Held to nothing unnamed plus a non-empty sweep, because the rows this panel shows change with the
            // style, so a fixed sample size would be a claim about the picker rather than about the names.
            RequireSurfaceControlsNamed(report, "ditherPanelAccessibility", FloatingPanelRoot("filter"));
            report["ditherPanelControlsAreNamed"] = "passed";
            try { ReadPanel(); }
            catch (Exception ex) { panelFault = ex.Message; }
            CloseFloatingPanel("filter", commit: false);                             // Cancel: the panel must not have applied
            Require(await AwaitDitherSessionAsync(5), "the cancelled dither panel never finished its session.");
            Require(panelFault is null, $"the panel failed its own claim: {panelFault}");
            Require(committed == "4|Dot", "the panel closed without the probe reading its own fields");
            Require(!scene.CanUndo, "a cancelled dither panel changed the document.");
            report["ditherPanelRows"] = committed;
        });

        await Check("ditherDotShapeCarvesTheScreenOutOfThePublishedLayer", async () =>
        {
            // A white layer through Atkinson with two tones is already quantized, so the only thing that can
            // move a pixel here is the shape pass: a 4×4 block whose radius is 1.68 leaves its own corner
            // (sqrt(1.5²+1.5²) = 2.12 from the middle) as the dark color and its (1,1) whole.
            var scene = WhiteScene(8);
            OpenDocument(scene); await WaitForFramesAsync();
            var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
            var parameters = new DitherParams { Style = DitherStyle.Atkinson, Diffusion = 0, Levels = 2 };
            Require(await ApplyDitherAsync(scene, id, parameters, 4, DitherPixelShape.Dot), "the dot-shaped dither never committed.");
            await WaitForFramesAsync();
            var result = scene.Snapshot.Images[id];
            byte At(int x, int y) => result.Pixels[y * result.Stride + x * 4];
            AssertUploadedFrame(scene);
            Require(At(1, 1) == 255 && At(2, 2) == 255, $"the dot itself was carved away ({At(1, 1)}, {At(2, 2)}).");
            Require(At(0, 0) < 40 && At(3, 3) < 40, $"the gaps between dots are not the screen ({At(0, 0)}, {At(3, 3)}).");
            scene.Undo();
            var restored = scene.Snapshot.Images[id];
            Require(!ReferenceEquals(restored, result) && AllWhite(restored),
                "the dot pass did not arrive as one undo step back to the layer it started from.");
            Require(await ApplyDitherAsync(scene, id, parameters, 4, DitherPixelShape.Square), "the square dither never committed.");
            await WaitForFramesAsync();
            var squares = scene.Snapshot.Images[id];
            Require(AllWhite(squares), "the square block carved something too, so the difference was never the shape");
        });

        await Check("theGlyphCellsSortByInkAndShareABaseline", async () =>
        {
            // The engine only ever sees bytes, so everything that makes ASCII read as a terminal is decided
            // here: the ramp sorted least ink first, and every character sitting on one shared baseline.
            var (maps, coverage, width, height) = GlyphRasterizer.Render(GlyphRasterizer.DefaultCharacters, 14);
            Require(width >= 3 && height == 14, $"the cell came out the wrong shape ({width}×{height}).");
            Require(maps.Length == coverage.Length * width * height, "the maps are not one cell of bytes apiece.");
            Require(coverage.Length == GlyphRasterizer.DefaultCharacters.Length,
                $"the ramp lost a character ({coverage.Length} of {GlyphRasterizer.DefaultCharacters.Length}).");
            Require(coverage.Zip(coverage.Skip(1), (less, more) => less <= more).All(sorted => sorted),
                "the glyphs are not sorted from least ink to most (Dither.swift:217).");
            Require(coverage[0] == 0, $"the first cell is not blank: it holds {coverage[0]:R} of its own area.");
            Require(coverage[^1] > .1f,
                $"the fullest character of {GlyphRasterizer.DefaultCharacters} inked only {coverage[^1]:R}.");
            // Small letters are the Mac's other end of the size range, and a blank ramp there would make
            // every cell pick the same blank glyph.
            var (tiny, tinyCoverage, tinyWidth, _) = GlyphRasterizer.Render(GlyphRasterizer.DefaultCharacters, 6);
            Require(tinyWidth >= 1 && tinyCoverage[^1] > 0 && tiny.Length == tinyCoverage.Length * tinyWidth * 6,
                $"at the smallest text size the fullest glyph inked {tinyCoverage[^1]:R} of the cell.");
            // A repeated character is drawn once, in the order it was first written (Dither.swift:204).
            var (_, repeated, _, _) = GlyphRasterizer.Render("aa..z", 14);
            Require(repeated.Length == 3, $"the ramp wrote the same character {repeated.Length - 1} more than once.");
            var (_, orderTest, _, _) = GlyphRasterizer.Render("@. ", 14);
            Require(orderTest[0] < orderTest[^1], "sorting moved the ink around but not least-first.");
            // The field is a single-line box, so the newline rule is read off the normalizer itself.
            Require(GlyphRasterizer.UsableCharacters("a\nb\rc") == "abc", "the field's newlines were not dropped.");
            Require(GlyphRasterizer.UsableCharacters(new string('c', 70)).Length == 64, "the 64-character cap is gone.");

            // "E" and "e" have no descender, so on one baseline both end on the same row while the capital
            // reaches higher; centered on their own bounds instead, the small one's bottom sits above the
            // capital's and the two rows part company.
            var (pair, pairCoverage, pairWidth, pairHeight) = GlyphRasterizer.Render("Ee", 48);
            Require(pairCoverage.Length == 2, $"the two characters came back as {pairCoverage.Length} cells.");
            var (firstTop, firstBottom) = InkRows(pair, 0, pairWidth, pairHeight);
            var (secondTop, secondBottom) = InkRows(pair, 1, pairWidth, pairHeight);
            int taller = firstBottom - firstTop >= secondBottom - secondTop ? 0 : 1;
            var (capTop, capBottom) = taller == 0 ? (firstTop, firstBottom) : (secondTop, secondBottom);
            var (lowTop, lowBottom) = taller == 0 ? (secondTop, secondBottom) : (firstTop, firstBottom);
            Require(Math.Abs(capBottom - lowBottom) <= 1,
                $"the two characters do not share a baseline: their inks end at rows {capBottom} and {lowBottom}.");
            Require(lowTop - capTop >= 3, $"the short glyph starts only {lowTop - capTop} rows under the tall one.");
            report["asciiGlyphCells"] = new { cells = coverage.Length, width, height,
                least = coverage[0], fullest = coverage[^1], tinyFullest = tinyCoverage[^1],
                baselineRows = new[] { capTop, capBottom, lowTop, lowBottom } };
            await Task.CompletedTask;
        });

        await Check("asciiThroughThePanelDrawsItsCharactersIntoTheLayer", async () =>
        {
            // A mid gray layer sits inside the ramp, so both the glyphs and the screen shapes they replace
            // have something to mark: the panel's own characters, at the panel's own text size.
            var (scene, parameters) = await RunAsciiPanel(GlyphRasterizer.DefaultCharacters, 14);
            var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
            var drawn = scene.Snapshot.Images[id];
            int tones = DistinctRed(drawn);
            Require(tones > 2, $"the ASCII result holds only {tones} tone(s), so nothing was drawn from a glyph.");
            Require(Enumerable.Range(0, drawn.Pixels.Length / 4).All(pixel => drawn.Pixels[pixel * 4 + 3] == 255),
                "the mark path erased alpha on its way through.");
            scene.Undo(); await WaitForFramesAsync();
            Require(AllGray(scene.Snapshot.Images[id]), "the ASCII dither did not land as one undo step.");

            // The very same parameters with no glyph cells are the engine's fallback, which marks with the
            // screen shapes: two hard tones and nothing in between. Any difference above belongs to the glyphs.
            var plain = GrayScene(64); OpenDocument(plain); await WaitForFramesAsync();
            var plainId = plain.Snapshot.Manifest.ActiveLayerId!.Value;
            Require(await ApplyDitherAsync(plain, plainId, parameters with { GlyphMaps = [], GlyphCoverage = [] }, 1),
                "the glyph-less ASCII run did not commit.");
            await WaitForFramesAsync();
            var screen = plain.Snapshot.Images[plainId];
            Require(DistinctRed(screen) == 2,
                $"without glyph cells the path still made {DistinctRed(screen)} tones, so the difference above was never the glyphs.");
            Require(!SamePixels(drawn, screen), "the glyph cells arrived and changed nothing at all.");

            var (large, _) = await RunAsciiPanel(GlyphRasterizer.DefaultCharacters, 28);
            Require(!SamePixels(drawn, large.Snapshot.Images[large.Snapshot.Manifest.ActiveLayerId!.Value]),
                "the text size row never reached the engine.");
            var (sparse, _) = await RunAsciiPanel(" .", 14);
            Require(!SamePixels(drawn, sparse.Snapshot.Images[sparse.Snapshot.Manifest.ActiveLayerId!.Value]),
                "the characters field never reached the engine.");
            report["asciiPanelTones"] = new { glyphTones = tones, screenTones = DistinctRed(screen) };
        });

        await Check("scanlineGlowBloomsThePublishedLayerAndNeverDarkensIt", async () =>
        {
            // The bloom is the finished lines blurred and added back (Dither.swift:131), so each pair of
            // runs below differs by that one step: everything the scanlines did is shared, and no pixel
            // may lose light.
            var parameters = new DitherParams { Style = DitherStyle.Scanlines, LineSpacing = 4 };
            async Task<(Surface Bare, Surface Bloomed)> Pair(int spacing)
            {
                var bare = HalfLightScene(64); OpenDocument(bare); await WaitForFramesAsync();
                var bareId = bare.Snapshot.Manifest.ActiveLayerId!.Value;
                Require(await ApplyDitherAsync(bare, bareId, parameters with { LineSpacing = spacing }, 1),
                    "the bloomless CRT run did not commit.");
                await WaitForFramesAsync();
                var lines = bare.Snapshot.Images[bareId];
                var lit = HalfLightScene(64); OpenDocument(lit); await WaitForFramesAsync();
                var litId = lit.Snapshot.Manifest.ActiveLayerId!.Value;
                Require(await ApplyDitherAsync(lit, litId, parameters with { LineSpacing = spacing, Glow = .6f }, 1),
                    "the bloomed CRT run did not commit.");
                await WaitForFramesAsync();
                AssertUploadedFrame(lit);
                return (lines, lit.Snapshot.Images[litId]);
            }
            int Reaches(Surface lines, Surface glowing, int column) =>
                Enumerable.Range(0, glowing.Height).Select(y => glowing.Pixels[y * glowing.Stride + column * 4]
                    - lines.Pixels[y * lines.Stride + column * 4]).Max();
            // The whole dark half, summed: one column's worth of delta rides on which scanline the phase
            // happens to put under the probe, and this comparison has to hold up when the two pairs are
            // scanned at different spacings.
            int LightIn(Surface lines, Surface glowing) =>
                Enumerable.Range(0, glowing.Height).SelectMany(y => Enumerable.Range(glowing.Width / 2, glowing.Width / 2)
                    .Select(x => glowing.Pixels[y * glowing.Stride + x * 4] - lines.Pixels[y * lines.Stride + x * 4])).Sum();

            var (tightLines, tightGlowing) = await Pair(4);
            int changed = 0, widest = 0;
            for (int y = 0; y < tightLines.Height; y++)
            {
                for (int x = 0; x < tightLines.Width; x++)
                {
                    int at = y * tightLines.Stride + x * 4;
                    Require(tightLines.Pixels[at + 3] == tightGlowing.Pixels[at + 3], $"({x},{y}) lost its alpha to the bloom.");
                    int delta = tightGlowing.Pixels[at] - tightLines.Pixels[at];
                    Require(delta >= 0, $"({x},{y}) went darker under a bloom meant to add light ({tightLines.Pixels[at]} → {tightGlowing.Pixels[at]}).");
                    if (delta > 0) changed++;
                    widest = Math.Max(widest, delta);
                }
            }
            Require(changed > 0, "the bloom never reached the published layer at all.");
            Require(Reaches(tightLines, tightGlowing, 36) > Reaches(tightLines, tightGlowing, 56),
                "the smear got denser the further it ran from the light.");
            Require(Reaches(tightLines, tightGlowing, 56) < widest,
                "the bloom laid the same light across the whole dark half.");

            // How far the light runs is the line spacing it came off (Dither.swift:150), not the halftone
            // cell and not a fixed radius: four times the spacing has to carry more of the same beam into
            // the dark half. The sum over that half is what is compared, because one column's worth of
            // delta rides on which scanline the phase happens to put under the probe. Measured here at
            // spacing 4 and 16: 96 056 against 145 337 — the light cannot cross at its full strength,
            // because the half it came from saturates at its own alpha first, which is why the margin
            // asked for is a quarter rather than a multiple.
            var (looseLines, looseGlowing) = await Pair(16);
            int tightLight = LightIn(tightLines, tightGlowing), looseLight = LightIn(looseLines, looseGlowing);
            Require(looseLight * 4 > tightLight * 5,
                $"spacing 16 carried {looseLight} of light into the dark half and spacing 4 carried {tightLight} — the bloom is not measuring the spacing.");

            var litBack = HalfLightScene(64); OpenDocument(litBack); await WaitForFramesAsync();
            var litIdBack = litBack.Snapshot.Manifest.ActiveLayerId!.Value;
            var untouched = litBack.Snapshot.Images[litIdBack];
            Require(await ApplyDitherAsync(litBack, litIdBack, parameters with { Glow = .6f }, 1), "the undo fixture did not commit.");
            await WaitForFramesAsync();
            litBack.Undo(); await WaitForFramesAsync();
            var back = litBack.Snapshot.Images[litIdBack];
            Require(Enumerable.Range(0, back.Pixels.Length / 4).All(pixel =>
                    back.Pixels[pixel * 4] == (pixel % back.Width < back.Width / 2 ? 255u : 0u))
                    && !litBack.CanUndo && ReferenceEquals(back, untouched),
                "the bloom did not land as one undo step back to the layer it started from.");
            report["scanlineGlow"] = new { pixelsChanged = changed, widestDelta = widest,
                tightNear = Reaches(tightLines, tightGlowing, 36), tightFar = Reaches(tightLines, tightGlowing, 56),
                looseFar = Reaches(looseLines, looseGlowing, 56), tightLightIn = tightLight, looseLightIn = looseLight };
        });

        await Check("ditherPanelPreviewsEveryChangeAndCancelsWithoutLeavingAStep", async () =>
        {
            // The panel is a preview, not a form that only acts on OK: a change has to reach the canvas
            // while the dialog is still open, and walking away has to leave nothing behind at all.
            var scene = DitherScene(); OpenDocument(scene); await WaitForFramesAsync();
            var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
            var before = scene.Snapshot.Images[id];
            var dirtyBefore = scene.IsDirty;
            var stepsBefore = scene.CanUndo;
            DitherPreview? sessionProbe = null;
            string fault = null!;
            bool previewed = false, single = false, repeated = false;
            // The panel is non-modal now (#37), so the probe drives it in its own await. The body used to be
            // handed to a dialog seam that ran it and threw the result away, which is why a failure had to be
            // carried out in a string; it still is, so the claims read the same either way.
            OnDither(this, new RoutedEventArgs());
            try
            {
                {
                    var panel = ditherFormProbe ?? throw new InvalidOperationException("the dither panel never published itself");
                    sessionProbe = panel.Session;
                    ditherProbeWork = null;
                    panel.ChooseStyle((int)DitherStyle.HalftoneDots);      // the panel's own change handler ticks
                    if (ditherProbeWork is not { } first) throw new InvalidOperationException("changing the style never began a preview");
                    await first.WaitAsync(TimeSpan.FromSeconds(20));
                    previewed = !ReferenceEquals(before, scene.Snapshot.Images[id])
                        && scene.Snapshot.Images[id].Pixels[0] != before.Pixels[0];
                    var firstResult = scene.Snapshot.Images[id];
                    panel.SetValue("cell", 12);                            // a second change, same session
                    if (ditherProbeWork is not { } second) throw new InvalidOperationException("the second change never ticked");
                    await second.WaitAsync(TimeSpan.FromSeconds(20));
                    // The second tick has to preview from the panel's capture again, with the layer already
                    // holding the first one: a session that only guards against the pixels it opened with
                    // would reject its own second tick and quietly freeze the preview on the first result.
                    repeated = !ReferenceEquals(firstResult, scene.Snapshot.Images[id]);
                    single = scene.CanUndo && !ReferenceEquals(before, scene.Snapshot.Images[id]);
                }
                CloseFloatingPanel("filter", commit: false);                            // Cancel
            }
            catch (Exception ex) { fault = ex.Message; CloseFloatingPanel("filter", commit: false); }
            Require(await AwaitDitherSessionAsync(30), "the previewing dither panel never finished its session.");
            Require(fault is null, $"the preview failed its own claim: {fault}");
            Require(previewed, "changing the style in the panel never reached the canvas while the panel was open.");
            Require(repeated, "the second change left the canvas on the first tick's picture: the session guarded against the wrong pixels.");
            Require(single, "the panel's preview never became a step the user could undo.");
            Require(ReferenceEquals(before, scene.Snapshot.Images[id]),
                "Cancel did not hand back the very pixels the panel opened with "
                + $"(live is still {scene.Snapshot.Images[id].GetHashCode()}, which is the capture: "
                + $"{ReferenceEquals(sessionProbe?.Capture.Original, scene.Snapshot.Images[id])}).");
            Require(scene.CanUndo == stepsBefore && scene.IsDirty == dirtyBefore,
                "a dither panel that was cancelled still left history or a saved-state change behind.");
        });

        await Check("ditherPreviewToggleHidesTheEffectThenBringsTheSameOneBack", async () =>
        {
            var scene = DitherScene(); OpenDocument(scene); await WaitForFramesAsync();
            var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
            var before = scene.Snapshot.Images[id];
            string fault = null!;
            Surface afterTicks = null!;
            bool offLookedClean = false, backLookedDithered = false;
            OnDither(this, new RoutedEventArgs());
            try
            {
                {
                    var panel = ditherFormProbe ?? throw new InvalidOperationException("the dither panel never published itself");
                    ditherProbeWork = null;
                    panel.ChooseStyle((int)DitherStyle.Bayer4);
                    await (ditherProbeWork ?? throw new InvalidOperationException("no preview began")).WaitAsync(TimeSpan.FromSeconds(20));
                    panel.PreviewField.IsChecked = false;                   // the switch, flipped by hand
                    offLookedClean = ReferenceEquals(before, scene.Snapshot.Images[id]);
                    ditherProbeWork = null;
                    panel.PreviewField.IsChecked = true;
                    if (ditherProbeWork is { } again) await again.WaitAsync(TimeSpan.FromSeconds(20));
                    backLookedDithered = !ReferenceEquals(before, scene.Snapshot.Images[id]);
                    afterTicks = scene.Snapshot.Images[id];            // the OK run replaces this instance
                }
                CloseFloatingPanel("filter", commit: true);
            }
            catch (Exception ex) { fault = ex.Message; CloseFloatingPanel("filter", commit: false); }
            Require(await AwaitDitherSessionAsync(30), "the previewing dither panel never finished its session.");
            Require(fault is null, $"the preview switch failed its own claim: {fault}");
            // OK is its own run, and every run publishes a fresh surface: waiting for that instance to
            // change is the only way to know the commit landed without guessing at a delay.
            Require(await WaitUntilAsync(() => !ReferenceEquals(afterTicks, scene.Snapshot.Images[id]), 4000),
                "OK never published its own run after the panel closed.");
            await WaitForFramesAsync();
            Require(offLookedClean, "turning Preview off left the effect on the canvas.");
            Require(backLookedDithered, "turning Preview back on did not re-dither from the same capture.");
            Require(scene.CanUndo && !ReferenceEquals(before, scene.Snapshot.Images[id]),
                "OK after a preview toggle wrote no step, or wrote over the layer the panel opened on.");
            // Several ticks and the commit OK made have to be one step, not one per change: undoing once
            // has to land back on the pixels the panel opened with, with nothing older swallowed.
            scene.Undo();
            Require(ReferenceEquals(before, scene.Snapshot.Images[id]) && !scene.CanUndo,
                "a session of preview ticks and its commit did not undo as the single step the Mac's OK is.");
        });

        // #37: the whole reason the panel is no longer a ContentDialog. A dialog dims the window and holds the
        // pointer, so "drag a slider and look at the picture" was really "drag a slider and look at a mask".
        // Non-modal costs one thing the dialog gave for free — keystrokes reach the canvas too — so both halves
        // are claimed here: the canvas still takes work while the panel is up, and the panel's own keys stay
        // its own.
        await Check("ditherPanelLeavesTheCanvasWorkable", async () =>
        {
            var scene = DitherFullScene(64); OpenDocument(scene); await WaitForFramesAsync();
            var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
            var painted = scene.Snapshot.Images[id];
            var revisionBefore = painted.Revision;
            ToolCombo.SelectedIndex = (int)CanvasTool.Brush;
            Require(await WaitForToolSwitchLandedAsync(CanvasTool.Brush), "the fixture never got a brush to paint with.");
            OnDither(this, new RoutedEventArgs());
            Require(FloatingPanelIsOpen("filter"), "Filter ▸ Dither opened no panel.");
            Require(((StackPanel)FloatingPanelBody("filter")!).Children.Count > 6,
                "the panel opened with nothing in it.");
            // A real gesture on the real layer, with the panel up.
            BeginPointerStroke(new(20, 20)); await WaitForFramesAsync();
            AppendPointerStroke(new(24, 20)); await WaitForFramesAsync();
            FinishPointerStroke(); await WaitForFramesAsync();
            Require(scene.Snapshot.Images[id].Revision > revisionBefore,
                "a brush stroke on the canvas was refused while the filter panel was open.");
            Require(ditherPreview is not null && FloatingPanelIsOpen("filter"),
                "painting over the canvas took the filter panel down with it.");
            // A tool switch is the other half of "still workable": the letter keys and the rail both still land.
            ToolCombo.SelectedIndex = (int)CanvasTool.Eraser;
            Require(await WaitForToolSwitchLandedAsync(CanvasTool.Eraser), "the tool rail stopped answering while the panel was open.");
            // And the panel's own keys are the panel's: focus inside it is what the canvas stands down for,
            // measured at the guard itself rather than by synthesizing a key event, which no probe can do.
            Require(!FloatingPanelHoldsFocus && !TypingInField,
                $"the canvas read the panel as holding the focus before anything was focused ({StatusText.Text}).");
            Require(FocusFloatingPanel("filter"), "the panel offered nothing focusable to stand in for its own control.");
            Require(FloatingPanelHoldsFocus && TypingInField,
                "focus inside the panel did not read as the panel's own, so a Space or a letter would have gone to the canvas.");
            CanvasStage.Focus(FocusState.Programmatic);
            Require(!FloatingPanelHoldsFocus, "the canvas never got the focus back from the panel.");
            CloseFloatingPanel("filter", commit: false);
            Require(await AwaitDitherSessionAsync(10), "the cancelled panel never finished its session.");
            Require(!FloatingPanelIsOpen("filter"), "Cancel left the panel on screen.");
        });

        await Check("ditherPanelSaysWhenAPreviewFails", async () =>
        {
            var scene = DitherScene(); OpenDocument(scene); await WaitForFramesAsync();
            var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
            var before = scene.Snapshot.Images[id];
            OnDither(this, new RoutedEventArgs());
            var panel = ditherFormProbe ?? throw new InvalidOperationException("the dither panel never published itself");
            Require(!panel.PreviewErrorVisible, "the panel opened already reporting a failed preview.");
            // A thrown engine pass is the failure the user cannot see for themselves: the picture just stops
            // changing, which looks identical to a tick that is still in flight. The hook is the only way to
            // stand in for it, and it throws where the engine would.
            beforeDitherCompute = _ => Task.FromException(new InvalidOperationException("the dither engine ran out of scratch"));
            ditherProbeWork = null;
            panel.ChooseStyle((int)DitherStyle.Bayer4);
            await (ditherProbeWork ?? throw new InvalidOperationException("the failing change never began a preview"))
                .WaitAsync(TimeSpan.FromSeconds(20));
            await WaitForFramesAsync();
            Require(panel.PreviewErrorVisible && panel.PreviewErrorText.Contains("ran out of scratch"),
                $"a failed preview said nothing in the panel (visible {panel.PreviewErrorVisible}, text {panel.PreviewErrorText}).");
            Require(ReferenceEquals(before, scene.Snapshot.Images[id]), "a failed preview still published a picture.");
            beforeDitherCompute = null;
            ditherProbeWork = null;
            panel.ChooseStyle((int)DitherStyle.HalftoneDots);
            await (ditherProbeWork ?? throw new InvalidOperationException("the recovery tick never began")).WaitAsync(TimeSpan.FromSeconds(20));
            Require(!panel.PreviewErrorVisible,
                $"the line stayed up after a preview that worked ({panel.PreviewErrorText}).");
            Require(!ReferenceEquals(before, scene.Snapshot.Images[id]), "the tick after the failure never published.");
            CloseFloatingPanel("filter", commit: false);
            Require(await AwaitDitherSessionAsync(10), "the panel with the failure line never finished its session.");
        });

        report["ditherRenderFailureCounters"] = new { inputBefore = inputFailures, inputAfter = renderInputFailureCount,
            publicationBefore = publicationFailures, publicationAfter = renderPublicationFailureCount };
        if (inputFailures != renderInputFailureCount || publicationFailures != renderPublicationFailureCount)
            failures.Add("Dither probe recovered from a render input/publication failure.");
        report["ditherFaultsObserved"] = failures.ToArray();
        if (failures.Count != 0) throw new InvalidOperationException(string.Join("; ", failures));
    }

    /// <summary>Waits for the panel's preview session to be over: the ticks, their coalesced re-runs, and
    /// the keep-or-restore that closes them out. Returns whether the document ended up changed.</summary>
    private async Task<bool> AwaitDitherSessionAsync(int seconds)
    {
        if (ditherProbeFinish is null && !await WaitUntilAsync(() => ditherProbeFinish is not null, 2000)) return false;
        try { ditherProbeApplied = await ditherProbeFinish!.Task.WaitAsync(TimeSpan.FromSeconds(seconds)); }
        catch (TimeoutException) { return false; }
        return true;
    }

    /// <summary>
    /// Opens a mid gray 64×64 layer, runs Filter ▸ Dither the way the user does — through the panel, which
    /// picks ASCII, types the characters and sets the text size — and waits for that run to publish. The
    /// parameters the panel itself read back come along, so a comparison can change one thing at a time.
    /// </summary>
    private async Task<(EditorDocument Scene, DitherParams Parameters)> RunAsciiPanel(string characters, int textSize)
    {
        var scene = GrayScene(64);
        OpenDocument(scene); await WaitForFramesAsync();
        DitherParams? stashed = null;
        ditherProbeWork = null;
        OnDither(this, new RoutedEventArgs());
        var ascii = ditherFormProbe ?? throw new InvalidOperationException("the dither panel never published itself");
        ascii.ChooseStyle((int)DitherStyle.Ascii);
        ascii.SetValue("textSize", textSize);
        ascii.CharactersField.Text = characters;
        stashed = ascii.Result.Parameters;
        CloseFloatingPanel("filter", commit: true);
        Require(await AwaitDitherSessionAsync(30) && ditherProbeApplied, "the ASCII panel's dither did not commit.");
        Require(stashed is not null, "the panel never reported its own parameters.");
        await WaitForFramesAsync();
        return (scene, stashed!);
    }

    /// <summary>The first and last row a glyph inks, for a claim about where a character sits in its cell.</summary>
    private static (int Top, int Bottom) InkRows(byte[] maps, int index, int width, int height)
    {
        int at = index * width * height, top = -1, bottom = -1;
        for (int row = 0; row < height; row++)
        {
            for (int column = 0; column < width; column++)
            {
                if (maps[at + row * width + column] == 0) continue;
                if (top < 0) top = row;
                bottom = row;
                break;
            }
        }
        Require(top >= 0, $"glyph {index} drew no ink at all.");
        return (top, bottom);
    }

    private static int DistinctRed(Surface surface) =>
        Enumerable.Range(0, surface.Pixels.Length / 4).Select(pixel => surface.Pixels[pixel * 4]).Distinct().Count();

    private static bool SamePixels(Surface left, Surface right) => left.Pixels.AsSpan().SequenceEqual(right.Pixels);

    /// <summary>A document whose one layer is opaque white everywhere: already at the top of the ramp, so
    /// whatever the dither itself does to it is nothing, and only the shape pass can move a pixel.</summary>
    private static EditorDocument WhiteScene(int side)
    {
        var scene = EditorDocument.Create(side, side); var layer = scene.Snapshot.Manifest.Layers[0];
        var image = new Surface(side, side);
        for (int i = 0; i < image.Pixels.Length; i += 4)
        {
            image.Pixels[i] = image.Pixels[i + 1] = image.Pixels[i + 2] = 255; image.Pixels[i + 3] = 255;
        }
        ((Dictionary<Guid, Surface>)scene.Snapshot.Images)[layer.Id] = image;
        return scene;
    }

    /// <summary>A document whose one layer is opaque mid gray everywhere: inside the ramp rather than at
    /// either end, so both the glyphs and the screen shapes they replace have a tone to mark.</summary>
    private static EditorDocument GrayScene(int side)
    {
        var scene = EditorDocument.Create(side, side); var layer = scene.Snapshot.Manifest.Layers[0];
        var image = new Surface(side, side);
        for (int i = 0; i < image.Pixels.Length; i += 4)
        {
            image.Pixels[i] = image.Pixels[i + 1] = image.Pixels[i + 2] = 128; image.Pixels[i + 3] = 255;
        }
        ((Dictionary<Guid, Surface>)scene.Snapshot.Images)[layer.Id] = image;
        return scene;
    }

    /// <summary>A document whose one layer fills the canvas split down the middle: opaque white on the left,
    /// opaque black on the right, so a bloom has a hard edge to spill over and a dark field to measure it in.</summary>
    private static EditorDocument HalfLightScene(int side)
    {
        var scene = EditorDocument.Create(side, side); var layer = scene.Snapshot.Manifest.Layers[0];
        layer.Transform = new() { Origin = new(0, 0), Size = new(side, side) };
        var image = new Surface(side, side);
        for (int y = 0; y < side; y++)
        {
            for (int x = 0; x < side; x++)
            {
                int at = y * image.Stride + x * 4;
                byte value = x < side / 2 ? (byte)255 : (byte)0;
                image.Pixels[at] = image.Pixels[at + 1] = image.Pixels[at + 2] = value; image.Pixels[at + 3] = 255;
            }
        }
        ((Dictionary<Guid, Surface>)scene.Snapshot.Images)[layer.Id] = image;
        return scene;
    }

    private static bool AllGray(Surface surface) => Enumerable.Range(0, surface.Pixels.Length / 4)
        .All(pixel => surface.Pixels[pixel * 4] == 128 && surface.Pixels[pixel * 4 + 3] == 255);

    private static bool AllWhite(Surface surface) => Enumerable.Range(0, surface.Pixels.Length / 4)
        .All(pixel => surface.Pixels[pixel * 4] == 255 && surface.Pixels[pixel * 4 + 3] == 255);

    private static EditorDocument DitherScene()
    {
        var scene = EditorDocument.Create(32, 24); var layer = scene.Snapshot.Manifest.Layers[0];
        layer.Transform = new() { Origin = new(2, 3), Size = new(7, 5) };
        var image = new Surface(7, 5);
        for (int i = 0; i < image.Pixels.Length; i += 4) { image.Pixels[i] = image.Pixels[i + 1] = image.Pixels[i + 2] = 96; image.Pixels[i + 3] = 255; }
        ((Dictionary<Guid, Surface>)scene.Snapshot.Images)[layer.Id] = image;
        return scene;
    }

    /// <summary>A document whose only layer fills the canvas, so the engine walks `side` × `side` pixels.</summary>
    private static EditorDocument DitherFullScene(int side)
    {
        var scene = EditorDocument.Create(side, side); var layer = scene.Snapshot.Manifest.Layers[0];
        layer.Transform = new() { Origin = new(0, 0), Size = new(side, side) };
        var image = new Surface(side, side);
        for (int i = 0; i < image.Pixels.Length; i += 4) { image.Pixels[i] = image.Pixels[i + 1] = image.Pixels[i + 2] = 96; image.Pixels[i + 3] = 255; }
        ((Dictionary<Guid, Surface>)scene.Snapshot.Images)[layer.Id] = image;
        return scene;
    }
}
#endif
