using Compositor.Core;
using Compositor.Core.Psd;
using Compositor.Renderer;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Compositor.App;

/// <summary>
/// The window's share of multi-document: the tab on screen parks and restores through the registry.
/// The original keeps these pieces inside each tab's session (ProjectWorkspace.select simply reads the
/// next session after committing what the visible one was mid-way through); this port keeps the render
/// loop, the watcher and the layer-panel selection in the window, so a switch moves that per-window
/// view state between tabs instead. Command routing and the strip itself attach here as the next step.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Everything the window remembers about a tab it is not showing. The document and path
    /// travel with the tab; zoom, pan and which layer was picked are view state the window owns.</summary>
    private sealed record ParkedView(
        double Zoom, double PanX, double PanY, bool FollowsFit,
        Guid? SelectedLayerId, IReadOnlyCollection<Guid> SelectedLayerIds,
        Guid? MaskAloneLayerId, bool PaintOnMask);

    private readonly Dictionary<Guid, ParkedView> parkedViews = new();

    /// <summary>Switching tabs is a project operation, so it asks the same thing the menu asks: nothing
    /// on this window may be mid-flight. The original's canSwitch reads the session's drafts because a
    /// tab carries its own session; here a draft is window-level state, so the switch refuses rather
    /// than parking a draft unseen. A dirty document does not block it, exactly as on the Mac: each
    /// tab keeps its unsaved work. And because this gate already counts a live transform, the
    /// original's commit-before-switch has nothing to commit: the way this window moves a transform is
    /// what bars the switch, and it must be applied or cancelled first. Once the strip ships,
    /// relaxing the gate is what would reintroduce the commit, not a call.</summary>
    private bool CanSwitchProjectTabs => workspace.CanSwitch;

    /// <summary>The original's <c>select</c>: move the selection or change nothing. Refuses while
    /// anything is mid-flight, while the leaving tab has no document, or for an unknown tab.</summary>
    private bool SwitchProjectTab(Guid id)
    {
        var leaving = workspace.Current;
        if (id == leaving.Id) return true;
        if (!CanSwitchProjectTabs || leaving.Document is null) return false;
        // A tab can exist before its document lands (the registry's fresh tabs); the window always
        // shows a document, so the switch only moves onto a tab that has one.
        if (workspace.Tabs.FirstOrDefault(tab => tab.Id == id)?.Document is null) return false;
        if (!workspace.Select(id)) return false;
        parkOutgoing(leaving);
        rebind(workspace.Current);
        return true;
    }

    /// <summary>Remembers the leaving tab's view and lets go of its document and folder until the
    /// window comes back to it. The document itself stays alive in the tab — parked edits survive.</summary>
    private void parkOutgoing(ProjectWorkspaceTab tab)
    {
        parkedViews[tab.Id] = new ParkedView(viewport.Zoom, viewport.PanX, viewport.PanY,
            viewport.FollowsFit, selectedLayerId, selectedLayerIds.ToArray(), maskAloneLayerId, paintOnMask);
        if (tab.Document is { } documentToKeep)
        {
            documentToKeep.Changed -= OnDocumentChanged;
            documentToKeep.PixelsChanged -= OnDocumentPixelsChanged;
            documentToKeep.SelectionChanged -= OnSelectionChanged;
        }
        watcher?.Dispose();
        watcher = null;
        // A panel belongs to the document that opened it — the same rule OpenDocument applies when it
        // swaps documents: answering one here would write the leaving project's state into the next.
        AbandonFloatingPanel("effects");
        AbandonFloatingPanel("selectionAmount");
        AbandonFloatingPanel(CameraRawPanelKey);
        CancelColorRange(false);
    }

    /// <summary>Binds the window to the tab now selected: its document receives edits through the same
    /// handlers, its own watcher watches its own folder, and the view comes back either as parked or —
    /// the first time, as on a fresh open — fitted to the window. The render caches are dropped rather
    /// than carried, the same way OpenDocument swaps documents.</summary>
    private void rebind(ProjectWorkspaceTab tab)
    {
        if (tab.Document is not { } arrived) return;
        layerSelectionRequest++;
        layerSelectionPointerTarget = null;
        effectSelection = null;
        frameRevision++;
        renderSnapshot = renderSnapshotSource = null;
        renderInputs.Clear();
        bandBuffer = null;
        compositor = null;
        fullTarget?.Dispose();
        fullTarget = null;
        presented = null;
        pendingFullRender = true;
        viewport = new CanvasViewport(arrived.Snapshot.Manifest.Width, arrived.Snapshot.Manifest.Height);
        ResizeViewport();
        if (parkedViews.Remove(tab.Id, out var parked))
        {
            // A parked follow-the-fit tab needs nothing: the fresh viewport above is already fitted to
            // the document at the window's current size.
            if (!parked.FollowsFit)
            {
                // The viewport keeps its setters private, so the public moves restore exactly: the
                // parked pan is the document-anchored result of the same anchored operations, so
                // anchoring SetZoom at the center and then panning the residual lands the document
                // where it was left.
                viewport.SetZoom(parked.Zoom, viewport.Center);
                viewport.Pan(new PointD(parked.PanX - viewport.PanX, parked.PanY - viewport.PanY));
            }
            selectedLayerId = parked.SelectedLayerId;
            selectedLayerIds.Clear();
            foreach (var layer in parked.SelectedLayerIds) selectedLayerIds.Add(layer);
            if (selectedLayerId is { } active) arrived.SetActiveLayer(active);
            maskAloneLayerId = parked.MaskAloneLayerId;
            paintOnMask = parked.PaintOnMask;
            PaintMaskCheck.IsChecked = parked.PaintOnMask;
        }
        else
        {
            selectedLayerId = arrived.Snapshot.Manifest.ActiveLayerId ?? arrived.Snapshot.Manifest.Layers.LastOrDefault()?.Id;
            selectedLayerIds.Clear();
            if (selectedLayerId is { } initialLayer) selectedLayerIds.Add(initialLayer);
        }
        arrived.Changed += OnDocumentChanged;
        arrived.PixelsChanged += OnDocumentPixelsChanged;
        arrived.SelectionChanged += OnSelectionChanged;
        if (tab.ProjectPath is { } path)
        {
            watcher = new ProjectWatcher(path);
            watcher.ProjectChanged += OnExternalChange;
        }
        // The watcher baselines its digest when it attaches, so a package an agent rewrote while
        // this tab was parked fires no event for it: without a look at the disk, arriving would
        // keep showing the stale picture the tab left behind. The document remembers the package it
        // last agreed with (MarkSaved's digest; null until a document has met its folder), so the
        // arrival door compares and, on a mismatch, runs the very state machine the watcher's event
        // uses - a clean document reloads, a dirty one asks - which is what the Mac's per-tab
        // controllers do when a background tab is resumed
        // (IO/ProjectController+ExternalChanges.swift:28-30, :50).
        bool staleOnArrival = tab.ProjectPath is { } arrivedPath && arrived.SavedDiskDigest is { } baseline
            && DiskDigestNow(arrivedPath) is { } current && !baseline.SequenceEqual(current);
        // Attach first, then check: the arrival check may reload through OpenDocument, which disposes
        // and re-attaches this watcher itself, so there is never a double watch. Declining or keeping
        // mine runs no reload, and the tab still needs the watcher live - attaching only for a fresh
        // arrival would leave a dirty tab that refused the change watching nothing at all.
        if (staleOnArrival) OnExternalChange();
        UpdateTitle();
        UpdateDocSizeLabel();
        RefreshAll();
    }

    /// <summary>The package digest as the disk holds it right now, or null when nothing
    /// comparable is there (missing folder, mid-write read, locked volume): the arrival check
    /// simply holds until the next door.</summary>
    private static byte[]? DiskDigestNow(string path)
    {
        try { return Directory.Exists(path) ? ProjectStore.ComputeDigest(path) : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>Probe fixture: the window back to one project, as a fresh launch. Probes that swap
    /// documents directly (OpenDocument) bypass the tab flows, so they reset between cases the way a
    /// relaunch would; user-facing paths never call this. (The probe classes compile in Release too,
    /// so this helper cannot sit behind the DEBUG gate.)</summary>
    private void ResetTabsForProbe()
    {
        foreach (var tab in workspace.Tabs)
        {
            if (tab.Document is { } released)
            {
                released.Changed -= OnDocumentChanged;
                released.PixelsChanged -= OnDocumentPixelsChanged;
                released.SelectionChanged -= OnSelectionChanged;
            }
        }
        parkedViews.Clear();
        workspace.ResetForNewWindow();
        lastStripSignature = "";
    }

    // A pill press doubles as the Mac's tab drag: the press selects (drag or click, either way it
    // selects, like Safari and Chrome), moving three points or more begins the reorder, and the drop
    // lands the tab in the slot its center crossed. A press whose pill disappears mid-gesture — the
    // capture lost some other way — ends it. The dragged pill rides above the rest with a plain
    // offset; the Mac's neighbours easing into their new slots is the polish that rides with the
    // pointer, not the contract.
    private ProjectWorkspaceTab? pressedTab;
    private Grid? pressedPill;
    private Windows.Foundation.Point pressStart;
    private bool pressDragging;
    private TranslateTransform? pressTransform;

    private void BeginPillPress(Grid pill, ProjectWorkspaceTab tab, PointerRoutedEventArgs args)
    {
        pressedTab = tab;
        pressedPill = pill;
        pressStart = args.GetCurrentPoint(ProjectTabStrip).Position;
        pill.CapturePointer(args.Pointer);
        BeginPillPressCore();
    }

    /// <summary>The press without the event plumbing: selecting the pill's tab and taking the strip's
    /// paint. The pointer capture itself is left to the gesture, and a driven press (the probe) reads
    /// the same state afterwards.</summary>
    private void BeginPillPressCore()
    {
        SwitchProjectTab(pressedTab!.Id);   // the Mac: dragging a tab selects it
        PaintStripSelection();
    }

    /// <summary>The press has travelled this far along the strip; past three points the pill leaves
    /// its slot, which is the Mac's gesture threshold and its canSwitch gate at the same time.</summary>
    private void MovePillPress(double dx)
    {
        if (pressedTab is null || pressedPill is null) return;
        if (!pressDragging)
        {
            if (Math.Abs(dx) < 3 || !CanSwitchProjectTabs || workspace.Tabs.Count < 2) return;
            pressDragging = true;
            pressTransform = new TranslateTransform();
            pressedPill.RenderTransform = pressTransform;
            Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(pressedPill, 1);
        }
        pressTransform!.X = dx;
    }

    private void MovePillPress(PointerRoutedEventArgs args)
        => MovePillPress(args.GetCurrentPoint(ProjectTabStrip).Position.X - pressStart.X);

    private void EndPillPress(PointerRoutedEventArgs? args) => EndPillPress();

    /// <summary>Releasing a press that never crossed three points is a plain select, which already
    /// happened at press; a drag lands the tab in the slot its center crossed and lets the strip
    /// settle to what the registry now says.</summary>
    private void EndPillPress()
    {
        if (pressedTab is not { } tab) return;
        var pill = pressedPill;
        if (pressDragging)
        {
            // Busy by the time the drag ends (a reload from disk, say): the tab goes back where it
            // was, exactly as the Mac's ended-case comment describes.
            if (pill is not null && CanSwitchProjectTabs)
            {
                // PillCenter already rides the drag's RenderTransform, so the pill's own center is the
                // drop point; adding the translate again would overshoot the slot.
                var dropCenter = PillCenter(pill);
                var others = ProjectTabStrip.Children.OfType<Grid>()
                    .Where(child => !ReferenceEquals(child, pill))
                    .Select(PillCenter);
                workspace.MoveTab(tab.Id, TabDropIndex(dropCenter, others));
            }
            if (pill is not null)
            {
                pill.RenderTransform = null;
                Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(pill, 0);
            }
        }
        pressedTab = null;
        pressedPill = null;
        pressDragging = false;
        pressTransform = null;
        lastStripSignature = "";            // the strip settles to whatever the registry now says
        RefreshProjectTabStrip();
    }

    private double PillCenter(Grid pill)
    {
        var origin = pill.TransformToVisual(ProjectTabStrip).TransformPoint(
            new Windows.Foundation.Point(pill.ActualWidth / 2, 0));
        return origin.X;
    }

    /// <summary>The registry's final-order index: the tab lands after every other pill whose center
    /// the drop point has crossed.</summary>
    private static int TabDropIndex(double dropCenter, IEnumerable<double> otherCenters)
    {
        int index = 0;
        foreach (var center in otherCenters) if (center < dropCenter) index += 1;
        return index;
    }

    /// <summary>The Mac's ProjectTabDropDelegate (ProjectTabs.swift:401-423): the pill accepts while the
    /// window can switch (validateDrop's canSwitch term) and the tab is still on the strip, and the drop
    /// hands the payload to the destination — files through the same receive loop the window's drop
    /// uses, layers through copyLayers. Handled here, the routed event stops at the pill instead of
    /// bubbling to the window's own drop, which would land either payload on the visible tab.</summary>
    private void OnTabPillDragOver(object sender, DragEventArgs e)
    {
        if (sender is not Grid pill) return;
        var id = pill.Tag is Guid tab ? tab : Guid.Empty;   // the "+" slot carries no tab
        e.Handled = true;
        e.AcceptedOperation = CanReceiveDropOnPill(id, e) ? DataPackageOperation.Copy : DataPackageOperation.None;
    }

    /// <summary>What the pill accepts, the Mac's validateDrop (:405-410): a layer drag only when it
    /// comes from ANOTHER project (canReceiveDrag, ProjectWorkspace.swift:395-398 — onto its own tab it
    /// would do nothing, so it is not a target) and not while Option-dragging a duplicate inside the
    /// panel (:407; on Windows that is the Ctrl row-copy, which stays inside the panel); a file drop as
    /// long as the window can move. The port routes a same-window layer drag from its live record,
    /// exactly as the panel's own drop does — the Mac reads the drag pasteboard because its drag can
    /// come from another window, which this build's single window has no second copy of.</summary>
    private bool CanReceiveDropOnPill(Guid destination, DragEventArgs e)
    {
        if (!workspace.CanSwitch) return false;
        if (destination == Guid.Empty ? RejectOptionDrag(CurrentLayerPanelDrag) is not null
                                      : LayerDragFromOtherTab(destination) is not null) return true;
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return false;
        return destination == Guid.Empty || workspace.Tabs.Any(tab => tab.Id == destination);
    }

    /// <summary>The dragged layers when this pill may take them: the Mac's draggedLayerSource resolved
    /// to a tab (ProjectWorkspace.swift:387-392), refused when the drag holds an effect row, when the
    /// duplicate key is held (Option on the Mac, Ctrl here — a copy belongs inside the panel), or when
    /// the layers already live on this pill's tab (:395-398).</summary>
    private LayerPanelDrag? LayerDragFromOtherTab(Guid destination)
    {
        if (CurrentLayerPanelDrag is not { Effect: null } drag || drag.Layers.Count == 0) return null;
        if (KeyHeld(Windows.System.VirtualKey.Control)) return null;
        var head = drag.Layers[0];
        var source = workspace.Tabs.FirstOrDefault(tab =>
            tab.Document?.Snapshot.Manifest.Layers.Any(layer => layer.Id == head) == true);
        // Guid.Empty is the new-project slot's destination: a copy into a project that does not exist
        // yet is never "onto the tab that holds it" (canReceiveDrag: "Other tabs, a new tab, and
        // every file drag still are", ProjectWorkspace.swift:394-398).
        return source is null || (destination != Guid.Empty && source.Id == destination) ? null : drag;
    }

    /// <summary>copyLayer onto the pill's tab (ProjectWorkspace.swift:208-248): the drop point is the
    /// canvas centre (the Mac passes no point from a pill, ProjectTabs.swift:420), the copies land in
    /// one step on the destination, and the window ends there with the copies selected — the Mac's
    /// activeLayerID = mapping[id] and selectedLayerIDs = the mapped set, which here is the window's
    /// own row selection. A refusal (the budget, a vanished tab, the gate) says so in one status line
    /// the way the Mac's importError does, and touches nothing.</summary>
    internal bool CopyDraggedLayersToTab(Guid destination)
    {
        var drag = destination == Guid.Empty ? RejectOptionDrag(CurrentLayerPanelDrag) : LayerDragFromOtherTab(destination);
        if (drag is null) return false;
        // The Mac's copyLayers parks on canSwitch like the receive loop it runs from
        // (ProjectWorkspace.swift:160-163); the port answers with its own line and touches nothing,
        // because a sheet the user never closes would strand the wait. Refusing is also what the
        // strip's own switch does, so the slot cannot smuggle a move the pills refuse.
        if (!workspace.CanSwitch)
        {
            SetStatus(Loc.T("S_LayerCopyBusy"));
            return false;
        }
        if (destination != Guid.Empty && !workspace.Tabs.Any(tab => tab.Id == destination && tab.Document is not null)) return false;
        var source = drag.Owner;
        // One new project per dragged row, the Mac's per-provider loop (receiveProviders calls
        // copyLayer once for every pasteboard item, ProjectWorkspace.swift:176-184): each row — a
        // folder with its whole subtree, the seam's rule — lands ALONE in its own fresh document at
        // the source's size (addTab(reuseEmpty: false) then createDocument at :222/:238). A copy
        // the budget refuses leaves its empty tab standing, exactly as the Mac's early return does.
        int made = 0;
        var rows = destination == Guid.Empty ? drag.Layers : [drag.Layers[0]];
        foreach (var head in rows)
        {
            if (windowClosed) break;
            if (!source.Snapshot.Manifest.Layers.Any(layer => layer.Id == head)) continue;   // missing head skips
            if (destination == Guid.Empty)
            {
                var size = source.Snapshot.Manifest;
                workspace.AddTab(reuseEmpty: false);
                OpenDocument(EditorDocument.Create(size.Width, size.Height));
            }
            else if (!SwitchProjectTab(destination)) return false;
            var copies = document!.ImportLayersFrom(source, [head]);
            if (copies is null) continue;   // the Mac's budget return leaves this tab empty and moves on
            SelectLayerRow(copies[head]);   // the Mac: activeLayerID = mapping[id] (:235-236)
            made += 1;
        }
        EndLayerPanelDrag(drag);            // one gesture, one consumption — even a failed copy
        if (made == 0)
        {
            SetStatus(Loc.T("S_LayerCopyFailed"));
            return false;
        }
        SetStatus(Loc.F("S_LayerCopied", made, workspace.Current.Title));
        return true;
    }

    /// <summary>canReceiveDrag's "a new tab still is" (ProjectWorkspace.swift:394-398): a slot drop is
    /// never onto the layer's own tab, so only the duplicate key and an absent drag refuse it.</summary>
    private LayerPanelDrag? RejectOptionDrag(LayerPanelDrag? drag)
        => drag is { Effect: null } plain && plain.Layers.Count > 0 && !KeyHeld(Windows.System.VirtualKey.Control) ? plain : null;

    /// <summary>The Mac's receive loop with <c>destination == nil</c> (ProjectWorkspace.swift:157-175)
    /// for the "+" slot: a project package still opens as its own project, and every dropped image
    /// becomes ITS OWN new project — addTab, then the first insert fills a nil document at the
    /// image's size (EditorSession.insert :850-852). This window never holds a nil document (the
    /// launch adaptation the registry records), so "the canvas is sized to the image" is honoured by
    /// opening an image-sized document and importing into it: a pristine single tab is reused
    /// (addTab's reuseEmpty), anything else takes a fresh one. The image lands centred, as the Mac's
    /// nil-point insert does. The one recorded difference: the port's canvas carries its standard
    /// background layer, so a transparent PNG arrives over white rather than over bare checker —
    /// the SIZE matches exactly, the background is this window's invariant. Unreadable or
    /// over-limit files are skipped before any tab is opened; an empty drop says nothing, the Mac's
    /// loop simply has nothing to walk — the Mac would name the bad file in its per-URL import
    /// error; the port skips it silently, the same choice an unreadable in-memory drop made.
    /// A package's own name is honoured by OpenProjectAsync, an
    /// image layer's by the file name the import already reads.</summary>
    internal async Task ReceiveDroppedItemsOnSlotAsync(IReadOnlyList<string> paths)
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
            var file = paths[index];
            index += 1;
            if (!IsImportableExtension(System.IO.Path.GetExtension(file))) continue;
            var size = await ProbeImportPixelSizeAsync(file);
            if (size is not { } wh) continue;                       // unreadable — no tab, no claim
            // The Mac's park, the same beat the pill file-drop proved.
            while (!windowClosed && !workspace.CanSwitch) await Task.Delay(30);
            if (windowClosed) return;
            // The Mac's addTab() default reuseEmpty (ProjectWorkspace.swift:39-46, called bare at :171):
            // a lone pristine tab is handed the image-sized document; anything else gets its own tab.
            var leaving = workspace.Current;
            var tab = workspace.AddTab();
            if (!ReferenceEquals(tab, leaving)) parkOutgoing(leaving);
            OpenDocument(EditorDocument.Create(wh.Width, wh.Height));
            await ImportFilesAsync(document!, [file], point: null);
        }
    }

    /// <summary>The size an import would produce, read from the header rather than decoded: WIC's
    /// decoder reports dimensions before any pixel work, and the PSD header is two big-endian
    /// integers (height at 14, width at 18 — PsdReader :53-54). Null when the file cannot be read
    /// or a fresh document could not host it: the Mac guards size against its pixel budget per URL
    /// (:24-28); here the window cannot show a tab whose document throws, so both the side limit
    /// and the surface cap the canvas itself enforces gate the file BEFORE any tab is opened.</summary>
    internal static async Task<(int Width, int Height)?> ProbeImportPixelSizeAsync(string path)
    {
        try
        {
            if (PsdReader.IsPhotoshopFile(path))
            {
                var head = new byte[22];
                await using (var source = File.OpenRead(path))
                {
                    int read = 0;
                    while (read < head.Length)
                    {
                        int got = await source.ReadAsync(head.AsMemory(read));
                        if (got == 0) return null;
                        read += got;
                    }
                }
                if (!head.AsSpan(0, 4).SequenceEqual("8BPS"u8)) return null;
                int height = (head[14] << 24) | (head[15] << 16) | (head[16] << 8) | head[17];
                int width = (head[18] << 24) | (head[19] << 16) | (head[20] << 8) | head[21];
                if (!HostableCanvas(width, height)) return null;
                return (width, height);
            }
            using var stream = File.OpenRead(path);
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(
                stream.AsRandomAccessStream());
            int w = (int)decoder.OrientedPixelWidth, h = (int)decoder.OrientedPixelHeight;
            if (!HostableCanvas(w, h)) return null;
            return (w, h);
        }
        catch (Exception ex)
        {
            App.Log($"import size probe refused {path}: {ex}", LogLevel.Diagnostic);
            return null;
        }

        // What a fresh EditorDocument can hold at all: the same gates Surface's own constructor
        // throws on — a file past either one would fail mid-swap instead of being skipped up front.
        static bool HostableCanvas(int width, int height) =>
            width is >= 1 and <= Limits.MaxSide && height is >= 1 and <= Limits.MaxSide
            && (long)width * height <= Limits.MaxSurfacePixels;
    }

    private async void OnTabPillDrop(object sender, DragEventArgs e)
    {
        if (sender is not Grid pill) return;
        var id = pill.Tag is Guid tab ? tab : Guid.Empty;   // the "+" slot carries no tab
        e.Handled = true;
        if (!CanReceiveDropOnPill(id, e)) return;
        // The Mac lights the pill accent-coloured while targeted (ProjectTabs.swift:254-256); this
        // keeps the strip's own selected/idle greys and marks the moment with the accent instead.
        var previous = pill.Background;
        pill.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(77, 96, 158, 255));
        try
        {
            if (LayerDragFromOtherTab(id) is not null)   // the Mac's copyLayer branch (:416-421)
            {
                CopyDraggedLayersToTab(id);
                return;
            }
            var deferral = e.GetDeferral();
            string[] paths = [];
            try
            {
                var items = await e.DataView.GetStorageItemsAsync();
                paths = items.OfType<IStorageItem>()
                    .Where(item => item is StorageFile file
                        ? IsImportableExtension(System.IO.Path.GetExtension(file.Name))
                        : item is StorageFolder folder && ProjectStore.IsProjectDirectory(folder.Path))
                    .Select(item => item.Path).ToArray();
            }
            catch (Exception ex) { Report(Loc.T("SC_ImportImage"), ex); }
            finally { deferral.Complete(); }
            // No drop point: the Mac's pill drop calls receiveProviders without one (ProjectTabs.swift:420).
            await ReceiveDroppedItemsAsync(paths, null, id);
        }
        finally { pill.Background = previous; }
    }

    private void PaintStripSelection()
    {
        int slot = 0;
        foreach (var child in ProjectTabStrip.Children.OfType<Grid>())
        {
            if (slot >= workspace.Tabs.Count) break;
            child.Background = new SolidColorBrush(workspace.Tabs[slot].Id == workspace.SelectedId
                ? Windows.UI.Color.FromArgb(255, 62, 62, 64) : Windows.UI.Color.FromArgb(255, 42, 42, 44));
            slot += 1;
        }
    }

    private int IndexOfTab(Guid id)
    {
        for (int i = 0; i < workspace.Tabs.Count; i++) if (workspace.Tabs[i].Id == id) return i;
        return -1;
    }

    /// <summary>Rebuilds the strip's pills from the registry — one per project, the selected one lit,
    /// each carrying its own unsaved dot and a close button. The Mac reorders tabs by dragging through
    /// MoveTab; wiring the drag rides with the switch UI's follow-up. The pills are a mirror of the
    /// registry, never a second copy of the truth, so UpdateTitle refreshes them on every path that
    /// already knows the name changed: open, switch, save, any edit.</summary>
    private string lastStripSignature = "";

    private void RefreshProjectTabStrip()
    {
        if (pressedTab is not null) return;      // never pull a pressed pill out from under its drag
        // Rebuilding fires from every title update, which is every pixel edit; only rebuild when the
        // strip would actually look different, the way the old label compared its text first.
        var signature = string.Join("|", workspace.Tabs.Select(tab =>
            (tab.Id == workspace.SelectedId ? "*" : "") + (tab.Document is { IsDirty: true } ? "•" : "") + tab.Title));
        // one pill per tab plus the new-project slot after them
        if (signature == lastStripSignature && ProjectTabStrip.Children.Count == workspace.Tabs.Count + 1) return;
        lastStripSignature = signature;
        ProjectTabStrip.Children.Clear();
        foreach (var tab in workspace.Tabs)
        {
            var pill = new Grid
            {
                Background = new SolidColorBrush(tab.Id == workspace.SelectedId
                    ? Windows.UI.Color.FromArgb(255, 62, 62, 64) : Windows.UI.Color.FromArgb(255, 42, 42, 44)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 74, 74, 74)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 2, 2),
                Height = 26,
                MaxWidth = 180,
            };
            pill.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            pill.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = new TextBlock
            {
                Text = (tab.Document is { IsDirty: true } ? "• " : "") + tab.Title,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            AutomationProperties.SetName(label, "ProjectTab " + label.Text);
            ToolTipService.SetToolTip(label, tab.Title);
            var captured = tab.Id;
            var close = new Button
            {
                Content = new FontIcon { Glyph = "", FontSize = 9 },
                Width = 20,
                Height = 20,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
            };
            AutomationProperties.SetName(close, "ProjectTabClose");
            close.PointerPressed += (_, args) => { args.Handled = true; };   // closing a pill must not also select it
            close.Click += (_, _) => _ = CloseProjectTabAsync(captured);
            pill.Children.Add(label);
            Grid.SetColumn(close, 1);
            pill.Children.Add(close);
            pill.Tag = captured;   // the drop handlers below find their tab through it
            pill.AllowDrop = true;
            pill.DragOver += OnTabPillDragOver;
            pill.Drop += OnTabPillDrop;
            pill.PointerPressed += (_, args) => { args.Handled = true; BeginPillPress(pill, tab, args); };
            pill.PointerMoved += (_, args) => MovePillPress(args);
            pill.PointerReleased += (_, args) => EndPillPress(args);
            pill.PointerCaptureLost += (_, _) => EndPillPress(null);
            ProjectTabStrip.Children.Add(pill);
        }
        BuildNewProjectSlot();
    }

    /// <summary>The Mac's NewTabDropSlot (ProjectTabs.swift:243-260) and the toolbar's New-canvas
    /// button wearing NewProjectDropTarget (ContentView.swift:164-165, ProjectTabs.swift:373-384): a
    /// "+" pill after the tabs that takes dropped files and dragged layers into a NEW project. The
    /// strip is also the port's only New-canvas affordance, so unlike the Mac's slot — which appears
    /// only while a pasteboard drag is in the air — this one stays on the strip. Its drop routes
    /// through the same two doors as a tab pill, with Guid.Empty as the destination: files open one
    /// project per image run (the Mac's receive(into: nil), ProjectWorkspace.swift:171), layers copy
    /// into a fresh source-sized document (copyLayers' addTab branch, :222/:238).</summary>
    private void BuildNewProjectSlot()
    {
        // A Border, not a Grid: the strip's pill walks (selection paint, drag-reorder centres) read
        // Children.OfType<Grid>() positionally and must never meet this chrome.
        var slot = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 42, 42, 44)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 74, 74, 74)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 0, 7, 0),
            Height = 26,
            Child = new FontIcon { Glyph = "", FontSize = 11 },
        };
        ToolTipService.SetToolTip(slot, Loc.T("H_NewProjectDrop"));
        AutomationProperties.SetName(slot, Loc.T("H_NewProjectDrop"));
        slot.PointerPressed += (_, args) => { args.Handled = true; _ = NewProjectTabAsync(); };
        slot.AllowDrop = true;
        slot.DragOver += (_, e) =>
        {
            e.Handled = true;
            var takesLayers = LayerDragFromOtherTab(Guid.Empty) is not null;
            var takesFiles = e.DataView.Contains(StandardDataFormats.StorageItems) && workspace.CanSwitch;
            e.AcceptedOperation = takesLayers || takesFiles ? DataPackageOperation.Copy : DataPackageOperation.None;
        };
        slot.Drop += async (_, e) =>
        {
            e.Handled = true;
            if (LayerDragFromOtherTab(Guid.Empty) is not null) { CopyDraggedLayersToTab(Guid.Empty); return; }
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var deferral = e.GetDeferral();
            string[] paths = [];
            try
            {
                var items = await e.DataView.GetStorageItemsAsync();
                paths = items.OfType<IStorageItem>()
                    .Where(item => item is StorageFile file
                        ? IsImportableExtension(System.IO.Path.GetExtension(file.Name))
                        : item is StorageFolder folder && ProjectStore.IsProjectDirectory(folder.Path))
                    .Select(item => item.Path).ToArray();
            }
            catch (Exception ex) { Report(Loc.T("SC_ImportImage"), ex); }
            finally { deferral.Complete(); }
            await ReceiveDroppedItemsOnSlotAsync(paths);
        };
        ProjectTabStrip.Children.Add(slot);
    }

    /// <summary>Any project on this window that a close must ask about. The original asks per tab,
    /// in its quit order; a clean tab answers for itself without a word.</summary>
    private bool AnyProjectNeedsCloseConfirmation()
    {
        if (NeedsProjectCloseConfirmation) return true;
        foreach (var tab in workspace.QuitOrder())
        {
            if (tab.Id == workspace.SelectedId) continue;
            if (tab.Document is { IsDirty: true } or { ActiveTextDraft: not null }) return true;
        }
        return false;
    }

    /// <summary>The Mac's settlePendingEdits for the pieces the window's gate now refuses on: every open
    /// filter sheet is cancelled the way its Cancel button does — the frozen capture goes back inside the
    /// session's own undo step, the sheet leaves the screen, no prompt — and the two panels whose sessions
    /// hang off answers (selection amount, Camera Raw) are abandoned the way a parked tab abandons them.
    /// The strokes and drafts the gate also names (gradient, pixel move, text) are left alone: they settle
    /// on their own, and the close simply waits for them, as the Mac's canSwitch does after settling.</summary>
    private async Task SettlePendingEditsAsync()
    {
        // Each sheet comes down the way its Cancel button does (MainWindow.FloatingPanel.cs:243-264):
        // the panel leaves the screen FIRST, then the session's own finish puts the frozen capture back
        // inside the step it opened. Awaiting the finish without closing the panel — the first shape
        // this had — cancelled the session and left the sheet standing over the next document.
        if (tonalPreview is not null) { AbandonFloatingPanel("tonalContrast"); await FinishTonalContrastAsync(commit: false); }
        if (vignettePreview is not null) { AbandonFloatingPanel("vignette"); await FinishVignetteAsync(commit: false); }
        if (lensPreview is not null) { AbandonFloatingPanel("lensCorrection"); await FinishLensCorrectionAsync(commit: false); }
        if (gaussianPreview is not null) { AbandonFloatingPanel("gaussianBlur"); await FinishGaussianBlurAsync(commit: false); }
        if (motionBlurPreview is not null) { AbandonFloatingPanel("motionBlur"); await FinishMotionBlurAsync(commit: false); }
        if (bloomPreview is not null) { AbandonFloatingPanel("bloomGlow"); await FinishBloomAsync(commit: false); }
        if (addNoisePreview is not null) { AbandonFloatingPanel("addNoiseFilter"); await FinishAddNoiseFilterAsync(commit: false); }
        if (ditherPreview is not null) { AbandonFloatingPanel("filter"); await FinishDitherPanelAsync(commit: false); }
        AbandonFloatingPanel("selectionAmount");
        AbandonFloatingPanel(CameraRawPanelKey);
        CancelColorRange(restore: false);
    }

    /// <summary>The original's <c>confirmQuit</c>: settle what is on screen, then ask each project in
    /// the tab order — the one you are viewing first, then left to right. Declining any of them leaves
    /// the window exactly as it was, and a tab whose own answer was Save keeps its saved state.</summary>
    private async Task<bool> ConfirmCloseAllProjectsAsync()
    {
        // The Mac's confirmQuit settles what is on screen before it manages or asks (settlePendingEdits,
        // ProjectWorkspace.swift:126-137): an open filter sheet is cancelled — its frozen pixels put back,
        // no prompt — and only then does the walk over the tabs begin. Without this a sheet would either
        // refuse the close at the guard or be stranded by a park, its session outliving its document.
        await SettlePendingEditsAsync();
        if (!workspace.TryBeginManaging()) return false;
        try
        {
            foreach (var tab in workspace.QuitOrder())
            {
                // The Mac's confirmQuit walks with isManaging held and moves selectedID directly —
                // the guarded switch could never run inside it. Everything mid-flight was refused
                // at the door; between tabs here nothing can start, because the gate bars it.
                if (tab.Id != workspace.SelectedId)
                {
                    var leaving = workspace.Current;
                    if (leaving.Document is not null) parkOutgoing(leaving);
                    if (!workspace.MoveSelection(tab.Id)) return false;
                    rebind(workspace.Current);
                }
                if (!await ConfirmDiscardChanges()) return false;
            }
            return true;
        }
        finally { workspace.EndManaging(); }
    }

    /// <summary>File &gt; New, the original's way: <c>newCanvas</c> always adds a tab — the untouched one
    /// on screen is never replaced, because with tabs there is nothing to discard and no prompt to ask.
    /// The leaving tab's view parks and its handlers come off before the swap binds the window to the
    /// fresh canvas; the registry's reuse-empty rule stays what the Mac uses it for: drops and imports.</summary>
    private async Task NewProjectTabAsync()
    {
        var leaving = workspace.Current;
        if (!CanSwitchProjectTabs || leaving.Document is null) return;
        workspace.AddTab(reuseEmpty: false);        // selects the new, still-empty tab
        parkOutgoing(leaving);
        // The document lands on the selected (new) tab through the same swap the launch uses, so the
        // window's handlers, watcher and title all follow it.
        OpenDocument(EditorDocument.Create());
        SetStatus(Loc.T("S_NewDoc"));
        await Task.CompletedTask;
    }

    /// <summary>Closing a tab the original's way: the window refuses over in-flight work (its
    /// <c>guard canSwitch</c>), and the project answers the unsaved-changes question — the port asks
    /// after moving onto the tab, because the discard flow reads window-level drafts that only the
    /// visible project can settle. Declining leaves every tab exactly as it was.</summary>
    private async Task<bool> CloseProjectTabAsync(Guid id)
    {
        if (!workspace.CanSwitch) return false;
        var tab = workspace.Tabs.FirstOrDefault(entry => entry.Id == id);
        if (tab is null || workspace.Current.Document is null) return false;
        // The unsaved-changes question reads window-level drafts, which only the visible project can
        // answer, so the tab moves on screen first. Switching needs the gate open, so the management
        // guard starts after the move; a declined answer leaves the tab on screen — closed is undone,
        // and nothing else about the strip changes.
        if (id != workspace.SelectedId && !SwitchProjectTab(id)) return false;
        if (!workspace.TryBeginManaging()) return false;
        EditorDocument? documentToRelease = null;
        try
        {
            if (!await ConfirmDiscardChanges()) return false;
            documentToRelease = tab.Document;
            workspace.RemoveTab(id);
            parkedViews.Remove(id);
            // The selected tab the registry moved on to binds now; when the list emptied, the
            // replacement fresh tab has no document yet, so the window opens one the way launch does.
            if (workspace.Current.Document is null) OpenDocument(EditorDocument.Create());
            else rebind(workspace.Current);
                return true;
        }
        finally
        {
            workspace.EndManaging();
            if (documentToRelease is { } released)
            {
                released.Changed -= OnDocumentChanged;
                released.PixelsChanged -= OnDocumentPixelsChanged;
                released.SelectionChanged -= OnSelectionChanged;
            }
        }
    }
}
