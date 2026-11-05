#if DEBUG
using System.IO;
using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices.WindowsRuntime;

namespace Compositor.App;

/// <summary>
/// Drives the window's tab switch directly: two live tabs, park and restore, refusal while an edit is
/// in flight. The registry already carries its own unit tests; what lives here is everything the window
/// holds around it — the document handlers, the watcher, the viewport, the layer-panel selection — so
/// this is where switching is proved. The strip and the commands will call the same entry next.
/// </summary>
public sealed partial class MainWindow
{
    private async Task VerifyProjectTabSwitchingAsync(Dictionary<string, object> report)
    {
        var directory = Path.Combine(Path.GetTempPath(), "compositor-tab-switch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        // The recent-menu checks write the preferences file; point it at a scratch directory so the
        // probe can never rewrite the list the real editor reads (the GridSettingsProbe's overlay).
        var preferencesPrevious = Environment.GetEnvironmentVariable("COMPOSITOR_PREFERENCES_DIRECTORY");
        Environment.SetEnvironmentVariable("COMPOSITOR_PREFERENCES_DIRECTORY", Path.Combine(directory, "prefs"));
        string pathB = Path.Combine(directory, "Tab B");
        ResetTabsForProbe();
        var tabA = workspace.Current;
        if (tabA.Document is null) OpenDocument(EditorDocument.Create(4, 4));
        var docA = tabA.Document!;
        await ProjectStore.SaveAsync(EditorDocument.Create(24, 20).Snapshot, pathB);   // a real project for the open flows
        try
        {
            // A second tab built the way the registry builds an opened project: a document and a path,
            // waiting for the window to move onto it.
            ProjectWorkspaceTab NewLiveTab()
            {
                var tab = new ProjectWorkspaceTab { Document = EditorDocument.Create(64, 48), ProjectPath = pathB };
                workspace.Tabs.Add(tab);
                return tab;
            }
            void CloseTab(ProjectWorkspaceTab tab)
            {
                parkedViews.Remove(tab.Id);
                workspace.Tabs.Remove(tab);
            }
            async Task ReadyAsync()
            {
                if (!await WaitUntilAsync(() => !ProjectEditsInFlight, 5000))
                    throw new TimeoutException("the window never settled for a tab switch");
            }

            // Moving onto a tab takes the window with it: document, path, watcher, title, layer
            // selection; moving back restores exactly the zoom, pan and pick that were parked.
            await ReadyAsync();
            var tabB = NewLiveTab();
            var docB = tabB.Document!;
            Require(SwitchProjectTab(tabB.Id), "the window refused to move onto a live tab.");
            await WaitForFramesAsync();
            Require(ReferenceEquals(document, docB) && projectPath == pathB && watcher is not null
                && Title.Contains("Tab B", StringComparison.Ordinal), "switching did not move the window: " + Title);
            Require(selectedLayerId == docB.Snapshot.Manifest.ActiveLayerId, "a first-shown tab did not take its own layer selection.");
            viewport.SetZoom(2, viewport.Center);
            viewport.Pan(new(7, 5));
            double zoom = viewport.Zoom, panX = viewport.PanX, panY = viewport.PanY;
            var picked = docB.Snapshot.Manifest.Layers[0].Id;
            selectingLayer = true;
            try { selectedLayerIds.Clear(); selectedLayerIds.Add(picked); selectedLayerId = picked; }
            finally { selectingLayer = false; }
            await ReadyAsync();
            Require(SwitchProjectTab(tabA.Id), "switching back was refused.");
            await WaitForFramesAsync();
            Require(ReferenceEquals(document, docA) && projectPath is null && watcher is null
                && !Title.Contains("Tab B", StringComparison.Ordinal), "switching back left the window bound to the other tab.");
            await ReadyAsync();
            Require(SwitchProjectTab(tabB.Id), "the second switch onto the parked tab was refused.");
            await WaitForFramesAsync();
            Require(Math.Abs(viewport.Zoom - zoom) < 1e-9 && Math.Abs(viewport.PanX - panX) < 1e-9
                && Math.Abs(viewport.PanY - panY) < 1e-9 && selectedLayerId == picked,
                "the parked view did not come back: " + viewport.Zoom + "/" + viewport.PanX + "/" + viewport.PanY);
            viewport.Pan(new(3, 3));
            zoom = viewport.Zoom; panX = viewport.PanX; panY = viewport.PanY;
            await ReadyAsync();
            Require(SwitchProjectTab(tabA.Id), "the switch before the second park was refused.");
            await ReadyAsync();
            Require(SwitchProjectTab(tabB.Id) && Math.Abs(viewport.PanX - panX) < 1e-9
                && Math.Abs(viewport.PanY - panY) < 1e-9 && Math.Abs(viewport.Zoom - zoom) < 1e-9,
                "a second park lost the view.");
            await WaitForFramesAsync();
            await ReadyAsync();
            Require(SwitchProjectTab(tabA.Id), "the switch closing the first fixture failed.");
            CloseTab(tabB);
            report["switchMovesTheWindowBetweenLiveTabs"] = "passed";

            // Parked documents stay independent: each tab keeps its own history and dirtiness, and a
            // dirty tab never blocks a switch — the Mac parks unsaved work without asking, because
            // nothing is lost by leaving the tab off screen.
            Require(!docA.CanUndo, "the launch document grew a history from tab traffic.");
            var tabC = NewLiveTab();
            var docC = tabC.Document!;
            await ReadyAsync();
            Require(SwitchProjectTab(tabC.Id), "the switch onto the second live tab failed.");
            await WaitForFramesAsync();
            docC.RenameLayer(docC.Snapshot.Manifest.Layers[0].Id, "Edited here");
            Require(docC.IsDirty && docC.CanUndo && !docA.IsDirty && !docA.CanUndo,
                "tab edits bled into the other document.");
            await ReadyAsync();
            Require(SwitchProjectTab(tabA.Id), "a dirty tab blocked the switch, unlike the original.");
            await ReadyAsync();
            Require(SwitchProjectTab(tabC.Id) && docC.CanUndo
                && docC.Snapshot.Manifest.Layers[0].Name == "Edited here", "the parked tab lost its history or its edit.");
            await WaitForFramesAsync();
            Require(docC.Undo(), "the parked edit could not be undone on its tab.");
            await ReadyAsync();
            Require(SwitchProjectTab(tabA.Id), "the switch closing the history fixture failed.");
            CloseTab(tabC);
            report["parkedTabsKeepTheirDocumentsAndHistoriesApart"] = "passed";

            // A draft dialog is window-level here, so a switch refuses rather than parking it unseen —
            // the original's canSwitch rule, and the gate the strip will hit on click.
            ToolCombo.SelectedIndex = (int)CanvasTool.Gradient;
            await WaitForFramesAsync();
            BeginGradient(new(4, 24)); MoveGradient(new(24, 24)); EndGradientDrag();
            await WaitForFramesAsync();
            Require(gradientEdit is not null && ProjectEditsInFlight, "the gradient fixture did not open a draft.");
            var tabD = NewLiveTab();
            Require(!SwitchProjectTab(tabD.Id) && workspace.SelectedId == tabA.Id && ReferenceEquals(document, docA),
                "switching parked a window across an open gradient draft.");
            CancelGradientDraft();
            await WaitForFramesAsync();
            await ReadyAsync();
            Require(SwitchProjectTab(tabD.Id), "the switch stayed barred after the draft closed.");
            await WaitForFramesAsync();
            await ReadyAsync();
            Require(SwitchProjectTab(tabA.Id), "the switch closing the draft fixture failed.");
            CloseTab(tabD);
            report["tabSwitchRefusesWhileAnEditIsInFlight"] = "passed";

            // A filter sheet left open is the Mac's filterEdit term in canSwitch (ProjectWorkspace.swift:29):
            // the switch refuses while the sheet holds its frozen capture, rather than parking the session
            // off screen to edit a document nobody is looking at. Cancelling the sheet opens the gate again.
            var tabE = NewLiveTab();
            OnTonalContrast(this, new RoutedEventArgs());
            Require(FloatingPanelIsOpen("tonalContrast"), "the sheet fixture opened no panel.");
            await WaitForFramesAsync();
            Require(ProjectEditsInFlight, "an open filter sheet left the gate closed to nothing.");
            Require(!SwitchProjectTab(tabE.Id) && workspace.SelectedId == tabA.Id
                && ReferenceEquals(document, docA),
                "a switch ran across an open filter sheet; the session would edit the parked document.");
            CloseFloatingPanel("tonalContrast", commit: false);
            Require(await WaitUntilAsync(() => tonalPreview is null, 8000), "the cancelled sheet never finished.");
            await ReadyAsync();
            Require(SwitchProjectTab(tabE.Id), "the switch stayed barred after the sheet closed.");
            await WaitForFramesAsync();
            await ReadyAsync();
            Require(SwitchProjectTab(tabA.Id), "the switch closing the sheet fixture failed.");
            CloseTab(tabE);
            report["tabSwitchRefusesWhileAFilterSheetIsOpen"] = "passed";

            // The window never moves onto a tab it cannot show or that does not exist.
            Require(!SwitchProjectTab(Guid.NewGuid()), "an unknown tab moved the window.");
            var blank = new ProjectWorkspaceTab();
            workspace.Tabs.Add(blank);
            Require(!SwitchProjectTab(blank.Id) && ReferenceEquals(document, docA),
                "the window bound itself to a tab without a document.");
            workspace.Tabs.Remove(blank);
            Require(workspace.Current.Id == tabA.Id && ReferenceEquals(document, docA),
                "the fixture did not leave the launch tab on screen.");
            await WaitForFramesAsync();
            report["anUnknownOrBlankTabNeverMovesTheWindow"] = "passed";

            // Dropping files ON A PILL imports into that tab (the Mac's ProjectTabDropDelegate with
            // destination: tab.id): the window moves onto the pill's tab and the files land there,
            // while the tab that was visible keeps its own document untouched. The probe drives the
            // same seam the pill's Drop handler calls — a real OLE drag needs a human hand.
            var tabF = NewLiveTab();
            var docF = tabF.Document!;
            var layersOnA = docA.LayerCount;
            var dropPath = Path.Combine(directory, "pill-drop.psd");
            await File.WriteAllBytesAsync(dropPath, FileImportBlendPsd("mul "));
            await ReceiveDroppedItemsAsync(new[] { dropPath }, null, tabF.Id);
            await WaitForFramesAsync();
            Require(workspace.SelectedId == tabF.Id && ReferenceEquals(document, docF),
                "the pill drop did not move the window onto the destination tab.");
            Require(docF.LayerCount > 1 && docA.LayerCount == layersOnA && !docA.IsDirty,
                $"the pill drop imported into the wrong document: dest {docF.LayerCount}, visible {docA.LayerCount}.");
            await ReadyAsync();
            Require(SwitchProjectTab(tabA.Id), "the switch closing the pill-drop fixture failed.");
            CloseTab(tabF);
            report["aDropOnAPillImportsIntoThatTab"] = "passed";

            // A LAYER dragged onto another pill copies it there (the delegate's layerType branch:
            // receiveProviders -> copyLayers, ProjectWorkspace.swift:176-184, 208-248). The Mac reads
            // its drag pasteboard; this window routes the live drag the same way its own panel drop
            // does, so the probe installs a real drag record and drives the pill's copy door.
            // The drag starts on the VISIBLE tab (CurrentLayerPanelDrag refuses a stale owner) and the
            // pill names the destination — the user's direction: source on screen, drop onto another pill.
            var sourceTab = NewLiveTab();
            var sourceDoc = sourceTab.Document!;
            var moved = sourceDoc.ImportImage(new Surface(6, 6), "Dragged me");
            int sourceBefore = sourceDoc.LayerCount, ownBefore = docA.LayerCount;
            await ReadyAsync();
            Require(SwitchProjectTab(sourceTab.Id), "the layer-copy fixture could not move onto its source tab.");
            await WaitForFramesAsync();
            layerPanelDrag = new(sourceDoc, [moved.Id], null);
            // The Mac's copy waits on canSwitch (the same park the file drop proved in
            // aPillDropWaitsForTheGateThenLands); this door refuses while the window is busy and says
            // so, rather than parking unattended — so an in-flight sheet must refuse with the busy
            // line, and the SAME call must land once the sheet is gone.
            OnTonalContrast(this, new RoutedEventArgs());
            Require(FloatingPanelIsOpen("tonalContrast"), "the gate fixture opened no sheet.");
            Require(!CopyDraggedLayersToTab(tabA.Id) && workspace.SelectedId == sourceTab.Id
                    && docA.LayerCount == ownBefore && StatusText.Text == Loc.T("S_LayerCopyBusy"),
                "the copy did not refuse a busy window with its own line (moved tabs or wrong line).");
            CloseFloatingPanel("tonalContrast", commit: false);
            Require(await WaitUntilAsync(() => tonalPreview is null, 8000), "the sheet cancel never finished.");
            await ReadyAsync();
            Require(CopyDraggedLayersToTab(tabA.Id), "the pill refused a legal cross-tab layer drop.");
            await WaitForFramesAsync();
            Require(workspace.SelectedId == tabA.Id && ReferenceEquals(document, docA),
                "the copy did not land the user on the destination tab (the Mac's selectedID, :243).");
            Require(docA.LayerCount == ownBefore + 1,
                $"the copy did not arrive in the destination document ({docA.LayerCount} vs {ownBefore + 1}).");
            Require(sourceDoc.LayerCount == sourceBefore
                    && sourceDoc.Snapshot.Manifest.Layers.Any(l => l.Id == moved.Id),
                "a cross-tab copy took the layer OUT of the source — the Mac's copyLayers never moves.");
            Require(docA.Snapshot.Manifest.Layers.Any(l => l.Name == "Dragged me" && l.Id != moved.Id),
                "the copy lost its name or the source's id.");
            Require(selectedLayerId is { } sel && docA.Snapshot.Manifest.ActiveLayerId == sel
                    && StatusText.Text.Length > 0,
                "the copies landed unselected or said nothing.");
            Require(docA.Undo() && docA.LayerCount == ownBefore, "the copy was not one undo step.");
            layerPanelDrag = null;
            // canReceiveDrag (ProjectWorkspace.swift:395-398): a layer drag onto the tab that already
            // holds it would do nothing, so it is not a target.
            await ReadyAsync();
            Require(SwitchProjectTab(tabA.Id), "the fixture could not return to the launch tab.");
            layerPanelDrag = new(docA, [docA.Snapshot.Manifest.Layers[0].Id], null);
            Require(!CopyDraggedLayersToTab(tabA.Id) && docA.LayerCount == ownBefore,
                "a layer drag onto its own tab took the drop anyway.");
            layerPanelDrag = null;
            // A drag whose head layer no tab holds is not a layer drag at all.
            layerPanelDrag = new(docA, [Guid.NewGuid()], null);
            Require(!CopyDraggedLayersToTab(sourceTab.Id) && workspace.SelectedId == tabA.Id,
                "a drag of an unknown layer reported a copy.");
            layerPanelDrag = null;
            CloseTab(sourceTab);
            report["aLayerDroppedOnAnotherPillCopiesItThere"] = "passed";

            // The same door with a FOLDER: the roots dedup and the whole-tree copy are App-side code
            // no Core test reaches. Two claims: DraggedLayerRows keeps only the folder when its child
            // rides the selection (the dragged set's HasDraggedAncestor rule), and one pill copy of
            // that folder lands the ENTIRE tree - folder plus children, parent links intact - as one
            // undo step, with the source untouched (the Mac's copyLayers "folders with all they hold"
            // :208-212, single-item drag sessions NativeLayerList:1196/:1318).
            var folderTab = NewLiveTab();
            var folderDoc = folderTab.Document!;
            var folderLeaf = folderDoc.ImportImage(new Surface(5, 5), "Folder leaf");
            var folderRecord = folderDoc.GroupLayers(folderLeaf.Id)!;
            Require(folderRecord.IsGroupOrFalse
                && folderDoc.Snapshot.Manifest.Layers.First(layer => layer.Id == folderLeaf.Id).ParentId == folderRecord.Id,
                "the folder fixture never built a real group shape.");
            await ReadyAsync();
            Require(SwitchProjectTab(folderTab.Id), "the folder fixture could not move onto its tab.");
            await WaitForFramesAsync();
            // The panel lists rows top-to-bottom as the reverse of the manifest (folders precede
            // their children there), so the child sits BELOW the folder in the manifest and one row
            // ABOVE it here - assert adjacency, not a direction invented on the spot.
            var panelIds = layerItems.Select(grid => ((LayerRow)grid.Tag).Layer.Id).ToList();
            int folderRow = panelIds.IndexOf(folderRecord.Id), leafRow = panelIds.IndexOf(folderLeaf.Id);
            Require(folderRow >= 0 && leafRow >= 0 && Math.Abs(folderRow - leafRow) == 1,
                $"the folder and its child were not adjacent rows in the panel (rows {folderRow} and {leafRow}).");
            var starting = new List<object> {
                LayerList.ContainerFromIndex(folderRow),
                LayerList.ContainerFromIndex(leafRow) };
            var roots = DraggedLayerRows(starting);
            Require(roots.Count == 1 && roots[0] == folderRecord.Id,
                $"the dragged set kept a child under its dragged folder ({string.Join(",", roots.Select(id => id.ToString("N")[..4]))}).");

            int treeOwnBefore = docA.LayerCount;
            layerPanelDrag = new(folderDoc, roots, null);
            int folderSourceBefore = folderDoc.LayerCount;
            Require(CopyDraggedLayersToTab(tabA.Id), "the pill refused a legal folder drop.");
            await WaitForFramesAsync();
            var copiedFolder = docA.Snapshot.Manifest.Layers.FirstOrDefault(layer => layer.Name == folderRecord.Name && layer.Id != folderRecord.Id);
            var copiedLeaf = docA.Snapshot.Manifest.Layers.FirstOrDefault(layer => layer.Name == "Folder leaf" && layer.Id != folderLeaf.Id);
            Require(docA.LayerCount == treeOwnBefore + 2 && copiedFolder is { IsGroupOrFalse: true } folderCopy
                    && copiedLeaf is { } leafCopy && leafCopy.ParentId == folderCopy.Id,
                $"the folder copy did not arrive as a two-layer tree with its link ({docA.LayerCount - treeOwnBefore} layers).");
            Require(ReferenceEquals(document, docA) && copiedFolder is not null && selectedLayerId == copiedFolder.Id,
                "the folder copy landed without the window on the destination or the folder selected.");
            Require(folderDoc.LayerCount == folderSourceBefore
                    && folderDoc.Snapshot.Manifest.Layers.Any(layer => layer.Id == folderLeaf.Id),
                "the folder copy took the tree out of its source.");
            Require(docA.Undo() && docA.LayerCount == treeOwnBefore,
                "the folder tree was not one undo step.");
            layerPanelDrag = null;
            Require(SwitchProjectTab(tabA.Id), "the folder cleanup could not return to the launch tab.");
            CloseTab(folderTab);
            Require(workspace.Tabs.Count == 1 && ReferenceEquals(document, docA),
                "the folder fixture did not leave the launch tab standing alone.");
            report["aFolderDroppedOnThePillArrivesAsATree"] = "passed";

            // The slot's door: a layer drag with NO destination project opens one fresh tab per row,
            // sized to the source (the Mac's addTab(reuseEmpty: false) + createDocument, :222/:238,
            // and receiveProviders calling copyLayer once per pasteboard item, :176-184). The source
            // keeps everything; the new tab holds only the copy.
            var slotSource = NewLiveTab();
            var slotDoc = slotSource.Document!;
            var slotRow = slotDoc.ImportImage(new Surface(6, 6), "Slot row");
            int tabsBefore = workspace.Tabs.Count, slotOwn = slotDoc.LayerCount;
            await ReadyAsync();
            Require(SwitchProjectTab(slotSource.Id) && ReferenceEquals(document, slotDoc),
                "the slot fixture could not stand on its source tab.");
            layerPanelDrag = new(slotDoc, [slotRow.Id], null);
            Require(CopyDraggedLayersToTab(Guid.Empty), "the new-project slot refused a legal layer drop.");
            await WaitForFramesAsync();
            Require(workspace.Tabs.Count == tabsBefore + 1 && ReferenceEquals(document, workspace.Current.Document)
                    && !ReferenceEquals(document, slotDoc),
                "the slot did not open exactly one fresh project tab for the row.");
            Require(document!.Snapshot.Manifest.Width == slotDoc.Snapshot.Manifest.Width
                    && document.Snapshot.Manifest.Height == slotDoc.Snapshot.Manifest.Height,
                "the new project did not take the source document's size.");
            // A fresh port tab always carries its pristine canvas (the launch adaptation the registry
            // records), so the new project shows that canvas plus the one copy — and nothing else.
            Require(document.LayerCount == 2
                    && document.Snapshot.Manifest.Layers.Count(l => l.Name == "Slot row") == 1
                    && slotDoc.LayerCount == slotOwn && slotDoc.Snapshot.Manifest.Layers.Any(l => l.Id == slotRow.Id),
                $"the copy did not arrive alone ({document.LayerCount} layers), or the source lost its row.");
            Require(selectedLayerId == document.Snapshot.Manifest.ActiveLayerId && StatusText.Text.Length > 0,
                "the slot copy landed unselected or said nothing.");
            layerPanelDrag = null;
            // No drag open means the slot has nothing to take.
            Require(!CopyDraggedLayersToTab(Guid.Empty) && workspace.Tabs.Count == tabsBefore + 1,
                "the slot invented a project with no drag in flight.");
            // The rows came ONE PER PROJECT: a two-row drag opens two tabs, not one with two layers.
            var pairA = document!.Snapshot.Manifest.Layers[0].Id;   // the fresh tab's only layer
            var pairRow = slotDoc.ImportImage(new Surface(4, 4), "Second row");
            await ReadyAsync();
            Require(SwitchProjectTab(slotSource.Id), "the pair fixture could not return to the source.");
            layerPanelDrag = new(slotDoc, [slotRow.Id, pairRow.Id], null);
            Require(CopyDraggedLayersToTab(Guid.Empty), "the slot refused a two-row drop.");
            await WaitForFramesAsync();
            // three new projects in all: one tab for the single row, then one per row again — the
            // Mac re-runs the per-provider loop over whatever the second drag carries, same rows and all.
            Require(workspace.Tabs.Count == tabsBefore + 3 && document!.LayerCount == 2
                    && document.Snapshot.Manifest.Layers.Count(l => l.Name == "Second row") == 1
                    && !document.Snapshot.Manifest.Layers.Any(l => l.Name == "Slot row"),
                "a two-row drop did not open one new project per row.");
            layerPanelDrag = null;
            await ReadyAsync();
            Require(SwitchProjectTab(tabA.Id), "the switch closing the slot fixture failed.");
            foreach (var extra in workspace.Tabs.Where(t => t.Id != tabA.Id && t.Id != slotSource.Id).ToList())
                CloseTab(extra);
            CloseTab(slotSource);
            Require(workspace.Tabs.Count == 1 && ReferenceEquals(document, docA),
                "the slot fixture did not leave exactly the launch tab standing.");
            report["theNewProjectSlotOpensOneTabPerDraggedRow"] = "passed";

            // The slot's file branch (the Mac's receive(into: nil), ProjectWorkspace.swift:157-175 +
            // EditorSession.insert :850-852): every dropped image becomes its OWN project, its canvas
            // sized to the image; a lone pristine tab is reused rather than stacked. The probe writes
            // real files and drives the seam the slot's Drop handler calls.
            string alphaPath = Path.Combine(directory, "alpha.png"), betaPath = Path.Combine(directory, "beta.png");
            string bogusPath = Path.Combine(directory, "bogus.psd"), notePath = Path.Combine(directory, "note.txt");
            string junkPath = Path.Combine(directory, "junk.png");
            async Task WritePngAsync(string path, int width, int height)
            {
                var tile = new Surface(width, height);
                for (int i = 0; i < tile.Pixels.Length; i += 4) { tile.Pixels[i] = 42; tile.Pixels[i + 3] = 255; }
                await File.WriteAllBytesAsync(path, await WicImaging.EncodePngAsync(tile));
            }
            await WritePngAsync(alphaPath, 5, 4);
            await WritePngAsync(betaPath, 3, 7);
            // A PSD header that reads fine (5×4) over bytes too short for the real reader: the size
            // probe passes, the import fails later — the tab stays empty, the window keeps showing it.
            await File.WriteAllBytesAsync(bogusPath, [
                .. "8BPS"u8, 0, 1, .. new byte[6], 0, 3, 0, 0, 0, 4, 0, 0, 0, 5, 0, 8, 0, 3]);
            await File.WriteAllTextAsync(notePath, "not an image");
            await File.WriteAllTextAsync(junkPath, "truncated pretend png");

            Require(await ProbeImportPixelSizeAsync(alphaPath) == (5, 4), "the PNG header probe misread the file.");
            Require(await ProbeImportPixelSizeAsync(bogusPath) == (5, 4), "the PSD header probe misread the canvas.");
            Require(await ProbeImportPixelSizeAsync(junkPath) is null, "a truncated PNG was not refused as unreadable.");
            Require(await ProbeImportPixelSizeAsync(Path.Combine(directory, "gone.png")) is null,
                "a missing file invented a size.");

            Require(workspace.Tabs.Count == 1 && ReferenceEquals(document, docA) && !docA.IsDirty,
                "the slot file fixture did not start on a lone pristine tab.");
            await ReceiveDroppedItemsOnSlotAsync([alphaPath]);
            await WaitForFramesAsync();
            Require(workspace.Tabs.Count == 1 && !ReferenceEquals(document, docA),
                "a lone pristine tab was not reused for the first image (the Mac's addTab default).");
            Require(document!.Snapshot.Manifest.Width == 5 && document.Snapshot.Manifest.Height == 4,
                $"the canvas did not take the image's size ({document.Snapshot.Manifest.Width}×{document.Snapshot.Manifest.Height}).");
            var alphaLayer = document.Snapshot.Manifest.Layers.FirstOrDefault(layer => layer.Name == "alpha");
            Require(document.LayerCount == 2 && alphaLayer is not null
                    && alphaLayer.Transform.Origin.X == 0 && alphaLayer.Transform.Origin.Y == 0,
                "the image did not arrive centred as its own layer on the fresh canvas.");
            Require(!docA.IsDirty && !docA.CanUndo,
                "the seam wrote into the pristine launch document instead of replacing it.");

            // Mixed drop: beta opens a project, the broken PSD opens its (empty) tab, the txt takes nothing.
            await ReceiveDroppedItemsOnSlotAsync([betaPath, bogusPath, notePath]);
            await WaitForFramesAsync();
            var betaTab = workspace.Tabs.FirstOrDefault(tab =>
                tab.Document is { } t && t.Snapshot.Manifest.Width == 3 && t.Snapshot.Manifest.Height == 7);
            Require(workspace.Tabs.Count == 3 && betaTab is not null
                    && betaTab.Document!.LayerCount == 2
                    && betaTab.Document.Snapshot.Manifest.Layers.Any(layer => layer.Name == "beta"),
                $"the drop did not open one project per image (tabs {workspace.Tabs.Count}, beta found: {betaTab is not null}).");
            Require(document!.Snapshot.Manifest.Layers.Count(layer => layer.Name == "note") == 0,
                "the text file was imported after all.");
            Require(!document.CanUndo, "the failed PSD import pushed an undo step onto the tab it did not change.");
            // The Mac's single "Import Image" step: one undo removes exactly the import, the
            // image-sized canvas survives.
            await ReadyAsync();
            Require(SwitchProjectTab(betaTab!.Id) && betaTab.Document!.Undo()
                    && betaTab.Document.LayerCount == 1 && betaTab.Document.Snapshot.Manifest.Width == 3,
                "the drop was not one undo step on its own canvas.");

            // The gate: a drop that arrives while a sheet holds the window parks, then lands.
            var tabsAtPark = workspace.Tabs.Count;
            OnTonalContrast(this, new RoutedEventArgs());
            Require(FloatingPanelIsOpen("tonalContrast"), "the park fixture opened no sheet.");
            var parked = ReceiveDroppedItemsOnSlotAsync([alphaPath]);
            await Task.Delay(200);
            Require(!parked.IsCompleted && workspace.Tabs.Count == tabsAtPark,
                "the slot file drop forced its way across the open sheet instead of waiting.");
            CloseFloatingPanel("tonalContrast", commit: false);
            Require(await WaitUntilAsync(() => tonalPreview is null, 8000), "the sheet cancel never finished.");
            await parked;
            await WaitForFramesAsync();
            Require(workspace.Tabs.Count == tabsAtPark + 1, "after the gate opened the waiting image never landed.");

            // Leave the fixture exactly as the group found it: the launch tab showing docA, alone.
            await ReadyAsync();
            Require(SwitchProjectTab(tabA.Id), "the cleanup could not return to the launch tab.");
            OpenDocument(docA);
            foreach (var extra in workspace.Tabs.Where(tab => tab.Id != tabA.Id).ToList()) CloseTab(extra);
            Require(workspace.Tabs.Count == 1 && ReferenceEquals(document, docA),
                "the slot file fixture did not leave the launch tab standing alone.");
            report["thePlusSlotGivesEveryDroppedImageItsOwnProject"] = "passed";

            // File > Open Recent > Clear Menu (CompositorApp.swift:64-65). The menu rebuilds from the
            // list on every change, so the probe reads the MENU itself, not a private field: entries
            // come and go, the Clear item survives rebuilds and disables itself with nothing to
            // clear, and an empty list still shows its placeholder.
            string realA = Path.Combine(directory, "Recent A");
            string realB = Path.Combine(directory, "Recent B");
            Directory.CreateDirectory(realA); Directory.CreateDirectory(realB);
            foreach (var stale in recentProjects.ToList()) ForgetRecentProject(stale);
            // An empty list: separator + Clear (disabled) + placeholder.
            Require(OpenRecentMenu.Items.Count == 3 && !ClearRecentMenuItem.IsEnabled
                    && OpenRecentMenu.Items.OfType<MenuFlyoutItem>().Any(i => i.Text == Loc.T("M_NoRecentProjects") && !i.IsEnabled),
                $"an emptied recent list did not show its placeholder with Clear disabled ({OpenRecentMenu.Items.Count} items).");
            RememberRecentProject(realA); RememberRecentProject(realB);
            var entryNames = OpenRecentMenu.Items.OfType<MenuFlyoutItem>().Select(i => i.Text).ToList();
            Require(recentProjects.Count == 2 && recentProjects[0] == realB && recentProjects[1] == realA
                    && entryNames.Contains("Recent B") && entryNames.Contains("Recent A")
                    && ClearRecentMenuItem.IsEnabled,
                $"remembering two projects did not fill the menu newest-first: {string.Join(",", entryNames)}.");
            // The file on disk mirrors the list (the Mac persists through NSDocumentController).
            Require(File.ReadLines(RecentFilePath)
                       .Where(l => l.Trim().Length > 0).Select(l => l.Trim()).SequenceEqual(new[] { realB, realA }),
                "the recent file did not match the menu's order.");

            OnClearRecentProjects(this, new RoutedEventArgs());
            await WaitForFramesAsync();
            Require(recentProjects.Count == 0 && File.ReadAllText(RecentFilePath).Trim().Length == 0,
                "Clear Menu left entries in the list or the file.");
            Require(!ClearRecentMenuItem.IsEnabled && OpenRecentMenu.Items.Contains(ClearRecentMenuItem),
                "after clearing, the surviving Clear item did not disable itself.");
            Require(Directory.Exists(realA) && Directory.Exists(realB),
                "Clear Menu touched the projects themselves.");
            report["theRecentMenuClearsCompletely"] = "passed";


            // The non-file provider (ImageFileDrop.importProviders :21-24): image bytes that were
            // never a file are decoded, written to a temporary PNG, and handed to the SAME import
            // loop. The probe builds the package in-process — the same view the window's drop reads —
            // so no real OLE drag is needed to see the copy happen.
            var canvasBefore = document!.LayerCount;
            var bitmap = new Surface(4, 3);
            for (int i = 0; i < bitmap.Pixels.Length; i += 4)
            { bitmap.Pixels[i] = 9; bitmap.Pixels[i + 3] = 255; }
            var encoded = await WicImaging.EncodePngAsync(bitmap);
            string? tempFile;
            using (var memory = new Windows.Storage.Streams.InMemoryRandomAccessStream())
            {
                await memory.WriteAsync(encoded.AsBuffer());
                memory.Seek(0);
                var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                package.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromStream(memory));
                tempFile = await TakeInMemoryImageDropAsync(package.GetView());
            }
            Require(tempFile is not null && File.Exists(tempFile)
                    && System.IO.Path.GetExtension(tempFile) == ".png",
                "the in-memory image never became a copyable PNG file.");
            // A package that carries nothing readable says so by returning null — no file, no layer.
            Require(await TakeInMemoryImageDropAsync(new Windows.ApplicationModel.DataTransfer.DataPackage().GetView()) is null,
                "an empty package invented a file.");
            // The copy it made is a real import: the SAME receive loop lays it down as a layer.
            await ReceiveDroppedItemsAsync([tempFile!], null);
            await WaitForFramesAsync();
            Require(document!.LayerCount == canvasBefore + 1,
                "the temporary PNG did not import as a layer through the ordinary loop.");
            var landed = document!.Snapshot.Images[document!.Snapshot.Manifest.ActiveLayerId!.Value]!;
            Require(landed.Width == 4 && landed.Height == 3 && landed.Pixels[0] == 9 && landed.Pixels[3] == 255,
                $"the round trip through the temp PNG changed the pixels ({landed.Width}x{landed.Height} [{landed.Pixels[0]},{landed.Pixels[3]}]).");
            report["anInMemoryImageDropBecomesAFileImport"] = "passed";

            // The Mac's receive parks on `while !canSwitch` (ProjectWorkspace.swift:160-163): a drop
            // that arrives while the window is busy waits, then lands — it does not force the move or
            // drop the files on whatever was visible. An open filter sheet is that busy moment.
            var tabG = NewLiveTab();
            var docG = tabG.Document!;
            OnTonalContrast(this, new RoutedEventArgs());
            Require(FloatingPanelIsOpen("tonalContrast"), "the gate fixture opened no sheet.");
            var pending = ReceiveDroppedItemsAsync(new[] { dropPath }, null, tabG.Id);
            await Task.Delay(200);
            Require(!pending.IsCompleted && workspace.SelectedId == tabA.Id,
                "the pill drop forced its way across the open sheet instead of waiting.");
            CloseFloatingPanel("tonalContrast", commit: false);
            Require(await WaitUntilAsync(() => tonalPreview is null, 8000), "the sheet cancel never finished.");
            await pending;
            await WaitForFramesAsync();
            Require(workspace.SelectedId == tabG.Id && ReferenceEquals(document, docG) && docG.LayerCount > 1,
                "after the gate opened the waiting drop never landed on its tab.");
            await ReadyAsync();
            Require(SwitchProjectTab(tabA.Id), "the switch closing the waiting-drop fixture failed.");
            CloseTab(tabG);
            report["aPillDropWaitsForTheGateThenLands"] = "passed";

            // File > New takes its own tab and leaves the visible project's unsaved work alone: with
            // tabs there is nothing to discard and no prompt to ask (the Mac's newCanvas).
            docA.RenameLayer(docA.Snapshot.Manifest.Layers[0].Id, "Keep me");
            Require(docA.IsDirty, "the New fixture did not leave the visible project dirty.");
            await NewProjectTabAsync(); await WaitForFramesAsync();
            Require(workspace.Tabs.Count == 2 && !ReferenceEquals(document, docA) && projectPath is null
                && docA.IsDirty && docA.Snapshot.Manifest.Layers[0].Name == "Keep me",
                "New replaced or prompted instead of taking its own tab.");
            var second = document;
            Require(Title.Contains("Untitled 2", StringComparison.Ordinal) || Title.Contains(Loc.T("M_Untitled"), StringComparison.Ordinal),
                "the new tab did not title itself: " + Title);
            await NewProjectTabAsync(); await WaitForFramesAsync();
            Require(workspace.Tabs.Count == 3 && ReferenceEquals(second, workspace.Tabs[1].Document),
                "a second New did not add another tab.");
            report["newProjectTakesItsOwnTabWithoutPrompting"] = "passed";

            // The strip mirrors the registry: one pill per project, the selected one lit, each with a
            // close button, plus the new-project slot after them (the Mac's NewTabDropSlot — a Border,
            // deliberately outside the pill walk the reorder and selection paint read).
            Require(ProjectTabStrip.Children.OfType<Grid>().Count() == 3
                    && ProjectTabStrip.Children.OfType<Grid>().Count(pill => pill.Children.OfType<Button>().Any()) == 3
                    && ProjectTabStrip.Children.OfType<Border>().Count() == 1,
                "the strip does not show every project with a way to close it, with one new-project slot.");
            report["theTabStripMirrorsTheRegistry"] = "passed";

            // Dragging a pill reorders the registry the Mac's way: the press selects, and the drop
            // lands the tab in the slot its center crossed. Driving the same core the pointer calls;
            // a real pointer would just find the gate closed until the switch's uploads drain, so the
            // fixture waits for that too.
            async Task ReadyToDragAsync()
            {
                if (!await WaitUntilAsync(() => CanSwitchProjectTabs, 5000))
                    throw new TimeoutException("the window never opened to a tab drag");
                ProjectTabStrip.UpdateLayout();     // a just-rebuilt pill reports ActualWidth 0 until measured
            }
            await ReadyToDragAsync();
            var pills = ProjectTabStrip.Children.OfType<Grid>().ToList();
            var order0 = workspace.Tabs.Select(tab => tab.Id).ToList();
            var thirdPill = pills[2]; var thirdTab = workspace.Tabs[2];
            pressedTab = thirdTab; pressedPill = thirdPill;
            BeginPillPressCore();
            Require(workspace.SelectedId == thirdTab.Id, "pressing a pill did not select its tab.");
            await ReadyToDragAsync();     // a hand moves 3 points over tens of ms; the select's uploads drain by then
            MovePillPress(2);                                   // under the three-point threshold
            Require(!pressDragging, "a two-point press began the reorder.");
            var c0 = PillCenter(pills[0]); var c2 = PillCenter(thirdPill);
            MovePillPress(c0 - c2);                             // the dragged center meets the first pill's
            Require(pressDragging, "the reorder never began above the threshold.");
            EndPillPress(); await WaitForFramesAsync();
            var order1 = workspace.Tabs.Select(tab => tab.Id).ToList();
            Require(order1.SequenceEqual(new[] { thirdTab.Id, order0[0], order0[1] }),
                "the drag did not land the tab in the crossed slot: " + string.Join(",", order1));
            Require(ProjectTabStrip.Children.OfType<Grid>().Count() == 3, "the strip did not settle to the reordered registry.");
            // Dragging back: the first pill past the second lands at index one.
            await ReadyToDragAsync();
            pressedTab = workspace.Tabs[0]; pressedPill = ProjectTabStrip.Children.OfType<Grid>().First();
            BeginPillPressCore();
            await ReadyToDragAsync();
            var rd0 = PillCenter(ProjectTabStrip.Children.OfType<Grid>().ElementAt(1));
            var rdSelf = PillCenter(pressedPill);
            MovePillPress(rd0 - rdSelf + 2);
            var began = pressDragging;
            EndPillPress(); await WaitForFramesAsync();
            Require(began, $"return drag never began: dx={rd0 - rdSelf} canSwitch={CanSwitchProjectTabs} tabs={workspace.Tabs.Count}");
            Require(workspace.Tabs.Select(tab => tab.Id).SequenceEqual(new[] { order0[0], thirdTab.Id, order0[1] }),
                "the return drag did not land its slot. order=" + string.Join(",", workspace.Tabs.Select(t => Array.IndexOf(order0.ToArray(), t.Id))) + " want=" + Array.IndexOf(order0.ToArray(), order0[0]) + "," + Array.IndexOf(order0.ToArray(), thirdTab.Id) + "," + Array.IndexOf(order0.ToArray(), order0[1]));
            report["draggingAPillReordersTheRegistry"] = "passed";

            // Busy by the time the drag ends — a draft opened mid-gesture here, a reload from disk on
            // the Mac — and the tab goes back where it was, exactly the Mac's ended-case rule.
            var firstPill = ProjectTabStrip.Children.OfType<Grid>().First();
            var beforeDrag = workspace.Tabs.Select(tab => tab.Id).ToList();
            await ReadyToDragAsync();
            pressedTab = workspace.Tabs[0]; pressedPill = firstPill;
            BeginPillPressCore();
            await ReadyToDragAsync();     // press selected the tab; its uploads must drain before a move counts
            MovePillPress(PillCenter(ProjectTabStrip.Children.OfType<Grid>().ElementAt(1))
                - PillCenter(firstPill) + 2);
            Require(pressDragging, "the busy fixture's drag never began.");
            ToolCombo.SelectedIndex = (int)CanvasTool.Gradient; await WaitForFramesAsync();
            BeginGradient(new(4, 24)); MoveGradient(new(24, 24)); EndGradientDrag(); await WaitForFramesAsync();
            Require(!CanSwitchProjectTabs, "the mid-gesture draft did not bar switching.");
            EndPillPress(); await WaitForFramesAsync();
            Require(gradientEdit is not null, "the mid-gesture draft fixture vanished.");
            CancelGradientDraft(); await WaitForFramesAsync();
            Require(workspace.Tabs.Select(tab => tab.Id).ToList().SequenceEqual(beforeDrag),
                "a drag released over in-flight work reordered anyway, unlike the original.");
            report["aDragReleasedWhileBusyGoesBackWhereItWas"] = "passed";

            // A dropped project package opens as a project (the Mac's receive loop routes .comp by
            // extension), and images that came behind it in the drop land on the newly opened tab.
            var beforeLayers = document!.LayerCount;
            var psdPath = Path.Combine(directory, "dropped.psd");
            await File.WriteAllBytesAsync(psdPath, FileImportBlendPsd("mul "));
            projectDialogProbe = _ => Task.FromResult(ContentDialogResult.Primary);
            await ReceiveDroppedItemsAsync(new[] { pathB, psdPath }, null);
            await WaitForFramesAsync();
            Require(projectPath == pathB && workspace.Tabs.Any(tab => tab.ProjectPath == pathB)
                && document!.LayerCount > beforeLayers,
                $"the drop did not open the project and import behind it: path={projectPath} layers {beforeLayers}->{document!.LayerCount}");
            projectDialogProbe = null;
            report["aDroppedProjectFolderOpensAndImagesFollowIt"] = "passed";

            // Opening a project lands on its own tab; opening the same path again selects that tab
            // instead of loading it twice (the Mac's loadProject dedupe).
            await OpenProjectAsync(pathB); await WaitForFramesAsync();
            var openedTab = workspace.Current;
            Require(projectPath == pathB && workspace.Tabs.Count == 4 && ReferenceEquals(document, openedTab.Document)
                && openedTab.Id != tabA.Id, "a fresh open did not land on its own tab.");
            var openedDocument = document;
            int tabs = workspace.Tabs.Count;
            await OpenProjectAsync(pathB); await WaitForFramesAsync();
            Require(workspace.Tabs.Count == tabs && workspace.Current.Id == openedTab.Id
                && ReferenceEquals(document, openedDocument),
                "reopening a visible project reloaded or duplicated its tab.");
            report["openingAProjectAlreadyOnTheStripSelectsItsTab"] = "passed";

            // Closing a tab answers its unsaved changes first; declining leaves every tab as it was,
            // and confirming removes the project and moves the window onto the neighbour.
            var closing = workspace.Current; var closingDocument = closing.Document;
            closingDocument!.RenameLayer(closingDocument.Snapshot.Manifest.Layers[0].Id, "Unsaved work");
            closeConfirmProbe = () => Task.FromResult(false);          // the person answered Cancel
            Require(!await CloseProjectTabAsync(closing.Id) && workspace.Tabs.Contains(closing)
                && ReferenceEquals(document, closingDocument) && projectPath == pathB,
                "declining to close removed the tab or moved off it.");
            closeConfirmProbe = () => Task.FromResult(true);           // Don't Save
            Require(await CloseProjectTabAsync(closing.Id) && !workspace.Tabs.Contains(closing)
                && workspace.Tabs.Count == tabs - 1 && !ReferenceEquals(document, closingDocument),
                "closing did not remove the tab and move the window on.");
            closeConfirmProbe = null;
            report["closingATabConfirmsThenRemovesIt"] = "passed";

            // The window's own close walks the Mac's tab order: the visible project answers first,
            // then the rest of the dirty ones left to right — a clean project answers for itself
            // without a word (production shows the dialog only over unsaved changes), and a refusal
            // stops the walk there.
            var visibleTab = workspace.Current;
            visibleTab.Document!.RenameLayer(visibleTab.Document.Snapshot.Manifest.Layers[0].Id, "Visible unsaved");
            docA.RenameLayer(docA.Snapshot.Manifest.Layers[0].Id, "Still unsaved");             // dirty, not visible
            var order = new List<Guid>();
            closeConfirmProbe = () =>
            {
                if (document is { IsDirty: true }) order.Add(workspace.SelectedId);
                return Task.FromResult(true);
            };
            Require(await ConfirmCloseAllProjectsAsync()
                && order.SequenceEqual(new[] { visibleTab.Id, tabA.Id }),
                "the quit loop missed a dirty project, went out of order, or asked a clean one: " + string.Join(",", order));
            order.Clear();
            closeConfirmProbe = () =>
            {
                if (document is { IsDirty: true }) { order.Add(workspace.SelectedId); return Task.FromResult(false); }
                return Task.FromResult(true);
            };
            // The completed walk left the window on the last tab it visited (the Mac's confirmQuit
            // assigns selectedID as it loops); clean tabs passed without a word, so this walk stops
            // at the first project that still has unsaved changes.
            var firstAsking = workspace.QuitOrder().First(tab => tab.Document is { IsDirty: true }).Id;
            Require(!await ConfirmCloseAllProjectsAsync() && order.SequenceEqual(new[] { firstAsking }),
                "refusing the first project did not stop the walk there: " + string.Join(",", order));
            closeConfirmProbe = null;
            report["closingTheWindowAsksEachProjectInTabOrder"] = "passed";

            Require(workspace.Current.Id == tabA.Id, "the fixture left the wrong tab selected.");
            await WaitForFramesAsync();

            // File > Close Project, the Mac's saveItem entry (CompositorApp.swift:85-91): the controller
            // resolves its own tab and hands it to workspace.close (:307-318) — for this window that is
            // the selected tab. The unsaved question's answers are pinned by closingATabConfirmsThenRemovesIt;
            // what this group owns is the MENU route (the item, its Ctrl+W chord, and the window-move) plus
            // the last-tab corner the Mac keeps in removeTab (ProjectWorkspace.swift:110-111: an emptied
            // list re-opens a fresh project) — which in this window also needs the document the registry
            // cannot supply (a replacement tab is document-less; the swap below opens the launch default).
            // It runs last because it closes everything: the function's own tail above still expects tabA.
            Require(CloseProjectMenuItem is not null
                    && CloseProjectMenuItem.KeyboardAccelerators.Any(chord =>
                        chord.Key == Windows.System.VirtualKey.W
                        && chord.Modifiers == Windows.System.VirtualKeyModifiers.Control),
                "Close Project carries no Ctrl+W chord, or not the Ctrl+W the Mac binds (⌘W).");
            var menuTab = NewLiveTab();
            Require(SwitchProjectTab(menuTab.Id), "the close-menu fixture never reached its tab.");
            await WaitForFramesAsync();
            menuTab.Document!.RenameLayer(menuTab.Document.Snapshot.Manifest.Layers[0].Id, "Close-menu unsaved");
            int menuTabsBefore = workspace.Tabs.Count;

            // Over an open sheet the window refuses — the Mac's guard canSwitch (:100) — and nothing moves.
            OnTonalContrast(this, new RoutedEventArgs());
            Require(FloatingPanelIsOpen("tonalContrast"), "the close-menu gate fixture opened no sheet.");
            Require(!await CloseProjectTabAsync(workspace.SelectedId)
                    && workspace.Tabs.Count == menuTabsBefore && ReferenceEquals(document, menuTab.Document),
                "the menu's close went through across an open sheet.");
            CloseFloatingPanel("tonalContrast", commit: false);
            Require(await WaitUntilAsync(() => tonalPreview is null, 8000), "the sheet cancel never finished.");
            await ReadyAsync();

            // Declining keeps the tab exactly as it was (the seam's rule, via the menu route).
            closeConfirmProbe = () => Task.FromResult(false);
            OnCloseProject(this, new RoutedEventArgs());
            Require(await WaitUntilAsync(() => workspace.Tabs.Count == menuTabsBefore
                    && ReferenceEquals(document, menuTab.Document) && menuTab.Document.IsDirty, 8000),
                "declining the menu close removed, moved off, or cleaned the project.");
            closeConfirmProbe = () => Task.FromResult(true);

            // The menu command itself (not the seam call): answering Don't Save closes THIS tab and the
            // window moves to a neighbour.
            OnCloseProject(this, new RoutedEventArgs());
            Require(await WaitUntilAsync(() => !workspace.Tabs.Contains(menuTab) && menuTabsBefore - workspace.Tabs.Count == 1, 8000),
                "the menu close never removed its tab.");
            Require(!ReferenceEquals(document, menuTab.Document), "the window still shows the closed document.");

            // The last tab: closing the only project must leave one fresh default document on screen —
            // the registry's replacement tab has none, and the window opens the launch shape for it.
            closeConfirmProbe = () => Task.FromResult(true);
            while (workspace.Tabs.Count > 1)
            {
                var doomed = workspace.Current;
                Require(await CloseProjectTabAsync(doomed.Id), $"the drain could not close a tab ({workspace.Tabs.Count} left).");
                await WaitUntilAsync(() => !workspace.Tabs.Contains(doomed), 8000);
            }
            var lastTab = workspace.Current;
            Require(await CloseProjectTabAsync(lastTab.Id)
                    && workspace.Tabs.Count == 1 && !ReferenceEquals(workspace.Current, lastTab),
                $"closing the last tab did not replace it (tabs {workspace.Tabs.Count}).");
            Require(document is not null && document.Snapshot.Manifest.Width == 1280
                    && document.Snapshot.Manifest.Height == 800 && document.LayerCount == 1 && !document.CanUndo,
                $"the last-tab replacement is not a fresh default canvas ({document?.Snapshot.Manifest.Width}×{document?.Snapshot.Manifest.Height} layers {document?.LayerCount}).");
            Require(ProjectTabStrip.Children.Count == 2,
                "the strip did not rebuild to one pill plus the slot after the last close.");
            closeConfirmProbe = null;
            report["closeProjectMenuClosesTheFrontProject"] = "passed";
        }
        finally
        {
            projectDialogProbe = null;
            Environment.SetEnvironmentVariable("COMPOSITOR_PREFERENCES_DIRECTORY", preferencesPrevious);
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }
}
#endif
