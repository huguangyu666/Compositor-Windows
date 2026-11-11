# User guide

The Windows editor is a focused compositing tool. It covers the first editing loop well: open a
.comp project, place raster layers, adjust their appearance, paint, crop, and export. It does not
yet aim to reproduce every Photoshop or macOS Compositor panel.

## SVG through MCP

External AI agents can also draw self-contained SVG and import it through MCP as a transparent
raster layer, with optional exact dimensions/placement, automatic transparent-margin trimming
and one Undo. See [SVG import](svg-mcp.md). SVG text, filters and editable vector nodes are not
supported; use the editor's editable text tool for typography.

## Interface language

The interface follows the Windows display language at startup. Chinese language variants use
the Simplified Chinese translation; English and other languages use English. Menus, tooltips,
tool options and panels use the same language. Restart Compositor after changing the Windows
display language. Regional number formatting still follows your Windows regional settings.

## Window layout

The workspace follows the original Compositor layout:

- The menu bar contains File, Edit, Layer, View, and Help commands.
- The document strip shows one pill per open project — the selected one lit, each with its dirty dot
  and a close button — plus New, Fit, 100%, and zoom commands. Several projects are open at once,
  each with its own unsaved work; a pill click moves the window onto it, and dragging a pill
  reorders the strip.
- The left rail groups Move, marquee tools, Lasso, Wand, Crop, Brush/Eraser, Clone,
  Spot Healing, Blur, Gradient, Text, Shape, Eyedropper, Hand, and Zoom. The palette below sets foreground/background
  colors, swaps them, or resets black/white. Two columns are shown by default; the chevron above
  the tools switches to a 56-point single column and remembers that choice. Tools scroll in a short
  window, while the palette stays pinned below them. Shortcut-selected tools scroll into view.
- Active tool parameters occupy a fixed-height top header. Scroll it horizontally when options
  exceed the window width, or use the previous/more arrows that appear at its edges. Flip commands
  use compact icons with named tooltips. Changing tools resets the strip to its start and keeps
  the canvas area at the same height.
- The center canvas displays the GPU or CPU composite on a checkerboard transparency background.
- The right Layers panel shows the stack from top to bottom with image/mask thumbnails, eye
  buttons, opacity, blend mode, and footer commands. Drag its left divider to resize it;
  the width is remembered locally. Adjustment properties scroll below the layer list.

The status line reports import, save, export, watcher, and validation errors. The title adds
Edited while the in-memory document is dirty.

### GPU acceleration

View > GPU Acceleration (Automatic Fallback) is on by default and remembers your choice.
The canvas uses the Win2D device's hardware GPU, including compatible integrated graphics,
for transforms, blending, masks, adjustments and layer effects. Software devices, unsupported
shader features, texture size/memory limits and device errors use the complete CPU compositor.
Turn the setting off to force CPU rendering; turn it on again to retry the hardware path.
Saving, exports, source-pixel tools and solo-mask display still use CPU processing.
See GPU canvas details (local development reference) for the measured scope and remaining limits.

Tool shortcuts are V (Move), M (Rectangle), Shift+M (Ellipse), L (Lasso), W (Wand), C (Crop),
B (Brush), E (Eraser), S (Clone), J (Healing), R (Blur), G (Gradient), T (Text), and U (Shape).
I selects Eyedropper, H selects Hand, and Z selects Zoom.
Typing in a text field keeps those keystrokes in the field.

With the canvas or Layers list focused, Delete/Backspace removes the last vertex of an active
lasso draft. Otherwise it clears pixels inside the selection; on a mask, it fills that area with
the background gray level. An explicitly empty selection changes nothing. Without a selection,
it removes a single targeted mask or deletes the selected layers. Text fields keep their own
Delete/Backspace behavior. Rebinding Delete in Shortcuts also disables the default Backspace alias.

## Layer animation timeline

Open **View > Timeline**, then choose **Create layer animation timeline**. The default is five
seconds at 24 fps. Duration, frame rate and looping are editable in the header. Changing frame
rate preserves key times in seconds; shortening past an existing key is refused.

Select a track by its layer name. The time ruler seeks the preview; Space plays/pauses when the
tracks have focus, Left/Right step one frame, and Home/End go to the first/last frame. Drag the
blue bar's ends to trim the layer's visible time, or move its body to shift the clip and its keys.
In/Out fields use seconds; the out point is the first invisible frame.

The lower fields edit center X/Y, width/height scale percentages, clockwise rotation and opacity.
At the starting frame, choose **Add keyframe**, move the playhead, then change the desired fields
and press Enter or leave the field. Each committed edit is one Undo. Drag a diamond to move all
its properties together; Delete removes the current frame's keys. Linear, Ease in/out and Hold
control the segment after the selected key. Outside the key range the nearest value is held.
Folders and adjustment layers support opacity and visible time; animate individual drawable
layers for position, scale and rotation.

**Animation preview** keeps ordinary pixel/transform edits disabled while viewing the animation.
Turn it off, or close the timeline, to edit the base artwork. Preview playback and seeking do not
change source pixels or add history. Playback follows display refresh; if rendering is slower,
pending frames are skipped to keep time. Scrubbing shows completed frames and settles on the
last requested time. Seeking with preview off leaves the base canvas unchanged.
High-frequency dragging uses the latest pointer position per display frame; releasing keeps the
exact final time. Keyframes keep their grab offset, and each keyframe or clip drag undoes in one step.
The blue time pointer follows subframe mouse motion immediately and snaps to a frame on release.
During playback it moves continuously; the image preview still advances at the timeline frame rate
and the available rendering speed.
Undo/Redo and Save remain available. Reopening an animated
project restores the timeline at frame zero. New layers without tracks remain visible throughout.
Canvas crop/size, image size and canvas flip carry position/rotation keys into the new document
coordinates. Merging or deleting layers removes their tracks; Undo restores them.

**Export PNG sequence…** writes all frames with transparency and a `sequence.json` frame-rate
record into a new subfolder. The button becomes **Cancel export** during work; cancellation leaves
partial frames in that subfolder without the completion record. Ordinary PNG/JPEG export and MCP
canvas previews use the current animation frame when preview is enabled. MP4/GIF encoding, audio,
video clips and folder transform keyframes are not implemented. Animated `.comp` projects use v12;
current macOS builds accept v1–11 and refuse v12. Static projects remain v11.

## Canvas navigation and rulers

Hand (H), holding Space with an editing tool, or dragging with the middle mouse button pans
the view, including when the image is smaller than the viewport. These gestures do not edit
pixels or add undo steps. Navigation waits until an active editing drag ends.

Zoom (Z) doubles zoom on click and halves it on Alt-click. Drag horizontally to zoom continuously
about the initial pointer position; moving 100 logical points doubles zoom. Its percentage field
accepts 0.1% through 3200%. Ctrl/Alt+wheel zooms about the pointer; plain wheel pans vertically,
and Shift+wheel pans horizontally. Touch translation/pinch navigates rather than painting.
Escape, focus loss, or losing pointer capture cancels navigation. Physical touch and mouse
workflows still require acceptance; the application probes verify the calculations and control wiring.

100% maps one document pixel to one physical display pixel, including on high DPI displays.
Fit leaves a 96-point total inset and follows window resizing until the view is manually changed.
High zoom uses nearest-neighbor display sampling; the pixel grid appears from 800% when enabled.

View > Show Rulers (Ctrl+R) toggles 18-point pixel rulers. The switch is remembered locally.
Drag from the top ruler to create a horizontal guide, or from the left to create a vertical guide.
Returning a new guide to a ruler cancels it. Drag an existing guide back to either ruler to delete
it. Guide drags preview until release, then create one undo step; Escape cancels the draft.
Full guide locking, layout grids and original snap preferences remain separate parity work.

Eyedropper (I) samples the visible composited sRGB texture into the foreground or background color.
Transparent pixels and points outside the document retain the current color. The sample ring
compares the previous and sampled colors. Alt temporarily samples foreground while Brush,
Eraser, Healing or Gradient remains selected. Alt with Clone continues to set its source point.

## Open and save

Use **File > Open Source** (Ctrl+O) to choose a PSD or PSB file. It opens in its own document at
the Photoshop canvas size, preserving supported layers, masks, placement and transparency; it
does not add a white background or an import Undo step. Conversion notices use the existing PSD
confirmation sheet. The original project remains on its tab. The new source document has unsaved
work; Save writes a `.comp` project, leaving the Photoshop file unchanged. Canceling the picker or
retiring an earlier open changes nothing. PNG/JPEG/TIFF placement remains under **Import Image**.

Use **File > Open Project Folder** (Ctrl+Shift+O) to select a folder containing manifest.json. The folder must have an images
directory and every referenced PNG must pass the project validator. The editor loads and validates
the package first, then the project opens on its own tab: nothing on screen is asked to discard,
because nothing is replaced. Opening a path that is already open selects that tab instead of
loading it a second time. Cancelling the folder picker or a failed load changes nothing. If a
draft, stroke or other edit is mid-flight, the open is refused until it settles: the folder picker
still opens and the package still loads, but the busy window refuses to adopt it (the load returns
unchanged). The menu item stays clickable - unlike the Mac, which greys File > New/Open while an
edit is mid-flight; the port has not ported the greying for this folder command, only the refusal at the door. If another open request or document switch takes over,
the earlier load cannot replace it when it finishes.

File > Open Recent lists the projects this app opened or saved last, newest first; a project that
was moved or deleted stays in the menu but cannot be clicked, and each entry carries a remove
affordance that forgets it from the menu without touching the project. Clear Menu empties the whole
list — the projects themselves are untouched — and is disabled while there is nothing to clear.

File > Save writes the current project. File > Save As writes a new package inside the selected
folder, or reuses the selected folder when it is already a project. Saving is asynchronous from
the UI's point of view, but the package protocol is ordered: image assets first, manifest second,
cleanup last.
Changes made while a save is running remain unsaved and can be written with Save again. The
status line distinguishes this from saving the current version. A completed save for a document
you have left does not change the new document's name or saved mark.

File > New takes its own tab and leaves the visible project's unsaved work exactly where it is;
across an open text or gradient draft it is refused instead. Closing a tab — its own close button, or File > Close Project (Ctrl+W) — asks that project's
unsaved-changes question first; declining keeps every tab as it was. Close Project closes just the
front project and moves the window to a neighbour; closing the last one leaves a fresh default
project standing, the shape File > New gives you. Closing the window walks
the projects in tab order, the visible one first, asking each project that still has unsaved
changes; a clean project passes without a word, and a refusal stops the walk there. Settled
canvas text and gradient drafts still carry one Undo step each, and a pending save finishes
before the discard decision proceeds; repeated window-close requests share one question.

Undo history, the active selection, zoom, scroll position, and the current mask-paint target are
session state. They are not serialized into manifest.json. Within a window session each tab keeps
its own of all of them: moving between tabs restores the zoom, scroll and layer pick you left
there, and closing a tab discards them.

## Import and placement

A drop that contains a project folder opens that project on its own tab; images dropped behind it
become layers there, the order of the drop deciding which tab gets what.
Dropping files onto a specific tab pill adds them to THAT tab — the window moves onto it first, so
the files land where you meant them even when another project was on screen. While any edit is in
flight the waiting drop holds until the window can move, exactly like switching tabs. Dragging a
layer row onto another project's pill COPIES it there (folders bring everything inside): the copy
keeps its name, lands centred on the destination canvas, and the source project loses nothing — an
in-flight sheet refuses it with its own notice, and a Ctrl duplicate stays inside the panel, like the
original's Option rule. The "+" pill after the tabs starts a new project on a click, and catching a
dragged layer there opens one fresh project per row, sized like the project it came from; dropped
Dropping FILES on the "+" pill gives every image its own new project, sized to the image (a project
package still opens as a project); one difference from the original is recorded: the port's fresh
canvas carries its standard background layer, so a transparent image arrives over white.
The picker and drag-and-drop accept PNG, JPG, JPEG, TIFF, PSD and PSB. A drag that carries image
bytes instead of a file — a screenshot thumbnail, a picture pulled out of a browser — is copied to a
temporary PNG and imported the same way, as one layer named Dropped. Raster images create
natural-size layers. PSD import supports an 8-bit RGB subset including raster layers, masks, and
folders; a Photoshop fill record for a rectangle or ellipse (with a uniform corner radius) becomes
an editable shape layer, redrawn from its style rather than resampled, a type layer becomes
editable text re-typeset from its own style and placed where the descriptor's matrix put it, and a
Photoshop Levels, Curves or Hue/Saturation adjustment layer becomes a live adjustment you can keep
editing in the panel. A layered file with anything to convert shows a sheet naming each layer and
what was lost, and applies nothing until you continue; Cancel skips just that file. Curved vector
masks are not available; vertical or sheared type, and a paragraph frame that will not fit, stay
the Photoshop pixels instead. PSB reads
through the same version-2 path in the Core reader. HEIC is not offered: the decoder for it has to
be installed separately, so the format is left out rather than surfaced and then failing.

Select several files in one picker request to import them in order. Successful files appear
individually; Undo removes that request's successes together. Failed files leave other successes
in place, and Details lists the failures. Separate requests have separate undo steps.
Raster layers keep their natural size and center on the canvas, or on a drop point inside the
canvas viewport. A drop on other window chrome uses the canvas center. Imported layers go into
the selected folder, or the selected layer's parent. A layered PSD gets its own named folder;
a merged-only PSD becomes one raster layer named after the file.

Import waits for the current stroke, repair, selection publication and transform to finish,
and cancels a crop draft. Content commands, tool changes and Undo/Redo pause during the request.
Decode runs in the background; a retired document cannot receive its old decode result.
Actual picker and drag/provider interaction still needs physical acceptance.

The Windows core stores pixels as premultiplied RGBA8. WIC decodes JPEG or PNG input into this
representation, and project saves always write PNG assets named after the layer UUID.

## Layer panel

Layers are stored bottom to top in the manifest; the last layer draws on top. The panel displays
the opposite reading order so the visually highest layer is first.

Ctrl-click rows to add or remove individual layers; Shift-click selects a range, and Ctrl+Shift
adds another range. The list uses native extended selection, including keyboard selection. Selected
rows stay selected across panel refreshes and can be moved, transformed, or dragged together.

The Lock row above the layer list offers transparency, image pixels, position/transform and all
edits. It applies to the selected layers; a mixed selection displays mixed states. A lock symbol
marks protected rows. Parent-folder protection is inherited, and the hint identifies when the
parent must be unlocked. Visibility and selection remain available on a fully locked layer.
Locks are local editor preferences keyed by project/layer identity: they survive reopening on
this computer, do not create Undo or mark content edited, and do not travel inside the `.comp`
package to another computer. Unlock before editing a protected target. Undo can still restore
earlier content; the lock remains in place.

Position protection blocks movement/transform and prevents painting from extending the layer grid.
Pixel protection blocks image edits while allowing placement and independent mask editing.
Transparency protection preserves the existing alpha bytes while painting, cloning, blurring or
filling a raster layer; transparent holes and soft edges remain. Erasing, clearing, gradient/healing
drafts and operations that change the protected grid require unlocking first. Folder locks affect
their children. Layout and multi-layer transforms preflight the entire selection.
Removing the primary row hands the editing target to a remaining row. With no rows selected there
is no editing target; clicking an image or mask thumbnail selects only that layer and target.

Drag selected rows above another row, into a folder, or to the bottom of the stack; hold Ctrl to
copy instead of moving. While the drag hovers the list, a blue line marks where the row will be
inserted (or the folder row's band lights up when the drop goes into it); effect and mask drags
show no line. Canceling the drag leaves content unchanged. Opening another document
retires the pending drag, including when it contains the same layer IDs.

Right-click a row for duplicate, rename, delete, clipping, group/ungroup, merge, mask and visibility
commands. Right-clicking an already selected row keeps the selected set; a thumbnail targets that
layer's pixels or mask, and an effect row targets the effect. Commands are unavailable during an
active transform or text draft. Double-click a thumbnail to edit existing text or expose adjustment
properties — for an adjustment layer the same editor opens from Layer > Edit Adjustment…, which is
offered only when the picked layer carries one; ordinary image/folder thumbnails start inline rename.

Press an eye to toggle that row. Keep the button down and drag over other rows to give them
the same visibility, including folders; revisiting a row keeps that state. The selected layer
stays selected, and the entire swipe is one Undo. Hold near the top or bottom of the list to
scroll through more rows. Releasing the button or losing pointer capture ends the swipe and
keeps the changes already made. Keyboard and accessibility eye toggles remain available.

For the selected layer, the panel supports:

- Select a layer.
- Toggle visibility.
- Set opacity from 0 to 100 percent.
- Select any of the 24 manifest blend modes for non-folder layers.
- Raise or lower one step among siblings.
- Delete all selected layers, including selected folders and their contents, in one Undo.
- Double-click a layer name to rename it inline; Enter commits and Escape cancels.

The Blend picker separates its 24 modes into six groups. Hover a mode to preview it on the canvas
without changing the document or Undo history. Choose it to commit one edit, or dismiss the menu to
restore the current mode. Changing layers or documents discards the pending preview.

Drag the Opacity label horizontally to adjust one percentage point per screen point; fractional
values are retained. One label or slider drag is one Undo, while separate numeric edits undo
individually. In the number field, Up/Down changes 1%, or 10% with Shift. Enter or Escape applies
valid text and returns focus to the canvas; invalid text restores the current value. Changing layers
or documents ends the previous opacity gesture.

With Move, number keys set every selected layer's opacity together, including selected folders.
With Brush, Eraser, Clone, Blur, Healing or Gradient, they set that tool's opacity instead.
1–9 means 10–90%, 0 means 100%; two digits within 600ms give an exact percentage, such as
4 then 5 for 45%, or 0 then 5 for 5%, with a 1% minimum. Text fields and active brush strokes
keep their input. These keys use the existing rebindable Opacity digit commands.

Use the first footer button, Layer > New Blank Layer, or Ctrl+Shift+N to add a transparent layer.
It sits above the active layer, or at the top of the selected folder, which expands to show it.
Creation is one Undo step; its first paint is another. The command is also in Keyboard Shortcuts.

Footer commands add a blank layer, folder or mask, open layer effects, add an adjustment,
raise/lower/duplicate from More, or delete. Click an image or mask thumbnail to choose the
editing target; its border indicates the target. Clicking the same layer preserves a Ctrl+T
draft, while selecting another layer commits it. The Paint mask checkbox also selects the target.

Pixel and mask thumbnails keep the canvas's aspect ratio and show their own placement, rotation and
flips within it. Masks use their edge background outside their placed pixels — on the thumbnails and
on the composite alike: a reveal-all mask placed apart from its layer keeps revealing beyond its
rectangle, a hide-all mask keeps hiding. Settled transform previews update the affected thumbnails;
Cancel and Undo restore them. Text and folders keep square icons. The thumbnails render through the
Windows sampler; the composite applies the same edge tone from the mask's own pixels.

The trash button and Layer > Delete remove a single targeted mask even when a canvas selection
exists; with pixels targeted, they delete the selected layers. Mask fills follow the mask's own
placement and flips. A selected fill expands a shared uniform mask onto an editable pixel grid,
leaving other layers that share the original mask unchanged.

Clearing selected pixels or filling the selected mask area prepares in the background with editing
briefly paused. Navigation remains available; replacing the document cancels the pending clear.
Creating a mask from a selection uses the same background preparation and cancellation. The selection
is consumed and the mask becomes the editing target only after success; one Undo restores both.
Plain white or black masks without a selection are still created immediately.

If deleted layers supply clipping masks to layers that remain, choose **Bake and Delete**,
**Remove Links and Delete**, or **Cancel**. Bake writes the clipping coverage into dependent pixel
layers and rasterizes their text or shapes. Their own masks and effect settings remain editable;
effects are recalculated from the baked pixels. Remove Links keeps the original pixels and reveals
them. Dependent adjustment layers have their clipping links removed with either choice, as the
dialog explains. Deletion and its link/pixel changes undo together. Baking runs in the background
with editing temporarily paused; replacing the document retires the pending deletion.

Shift-click a mask thumbnail to enable or disable it; a slash marks a disabled mask. Click the
link between thumbnails to link or unlink their placement. These property edits are unavailable
during a transform draft. Alt-click a mask thumbnail to show its grayscale coverage alone;
Alt-click again, select another layer, or choose pixels to return to the composite. A badge at the
bottom of the canvas names the viewed mask; its close button returns to the composite while keeping
that mask as the paint target, without adding history. The mask's
own placement and sampling apply, including when the mask or layer is disabled or hidden.
Alt-drag the mask at least three screen points onto another visible non-folder row to copy it,
including placement and enabled/link flags, in one Undo. Hold near the top or bottom of the Layers
list to scroll while dragging a mask; returning to the middle or leaving the list horizontally stops
scrolling. Release over a visible row to copy; releasing outside the list cancels the copy.

Ctrl-click an image thumbnail to load source pixels whose alpha is at least 128 as a selection.
Ctrl-click a mask thumbnail loads its black values below 128, regardless of mask enablement.
Ctrl+Shift adds and Ctrl+Alt subtracts; Alt takes precedence if both are held. Loading uses the
asset's placement and preserves the selected layer and editing target. A source with no qualifying
pixels preserves the current selection. Each changed thumbnail load records one Undo; consecutive
clicks run in order on a worker. Threshold tracing and edge antialiasing still differ from macOS.

The Paint mask checkbox and Move target selector share one target switch. Switching targets on
the same layer keeps a persistent whole-layer transform draft. Targets stay disabled while a
floating selection prepares, edits, merges, or waits to resume an Alt copy. Ctrl thumbnail loads
and mask copies are ignored during that transaction. Clicking another thumbnail first finishes
the merge/copy, then selects its pixel or mask target and solo display. Active strokes retain
their paint target; queued target requests are discarded if their document or primary changes.

Folders are pass-through containers. A folder has no image, can contain descendants, and its
opacity multiplies into the content. Folders always use Normal blend mode in the validator.

## Move and scale

Choose Move, select a layer, and drag on the canvas to move it in document pixels. Transform
handles resize and rotate it; Ctrl during a placement drag bypasses move snapping. The top bar
provides X/Y, W/H, ratio lock, scale percent, rotation, sampling and horizontal/vertical flips.
Scroll the bar horizontally in a narrow window. Width/height retain the origin; scale retains
the center and uses a fixed pixel basis. Labels scrub values; Up/Down step by 1, or 10 with Shift.

With two or more independent layers selected, Move shows six alignment buttons and Group before
the transform fields. Three or more enable horizontal and vertical equal-gap distribution.
Alignment uses the outer placement bounds of the selection; distribution holds the outermost
layers in place. Folders and their selected children count as one root. Each changed command is
one Undo step, and grouping selects the resulting folder.

Ordinary field editing previews a draft and commits one Undo on focus loss or Enter. Ctrl+T
keeps Free Transform active until Apply/Enter or Cancel/Escape; its action buttons stay at the
right edge. Escape discards the draft. Auto Select defaults on (an explicitly saved preference is
retained): click visible layer pixels to select their layer, or drag to select and move it.
Transparent pixels and enabled mask holes let clicks reach the layer below; hidden and zero-opacity
layers do not catch clicks. Ctrl temporarily inverts Auto Select;
Ctrl+Shift adds/removes a layer under the pointer. Show Controls defaults on; Ctrl+H toggles it.

Right-click the canvas to choose among the visible layers beneath the pointer, from top to bottom.
The menu uses the same transformed-pixel, mask and clipping rules as Auto Select, marks selected
and protected layers, and identifies a containing folder. Picking a layer selects its pixel target
in both the canvas and Layers panel without adding Undo. Empty space preserves the selection.
A menu opened before the document changed cannot act on the changed document. At most 100 hits
are listed; additional matches direct you to the Layers panel.
Shift inverts handle ratio locking and Alt resizes around the center. Upright resize handles snap
to canvas edges/centers, visible layers and guides within 10 screen points; Ctrl bypasses it.
Proportional resizing snaps the nearer edge and preserves the ratio. Visible pixel members of
multi-selected layers or a folder transform together.

Alt-drag with Move duplicates the selected whole layers or folders on the first movement.
Copies appear selected with thumbnails during the draft; release records copy + move in one Undo,
and Escape restores the original selection. Alt-click without movement leaves history unchanged.
Several copies stack together above the highest original; folders include their descendants.
Root names gain " copy", while descendant names stay unchanged. Duplicate Layers and Ctrl+J
without a pixel selection use the same rules. Copying a new primary selects its pixels.
Selected-pixel/floating-content transforms remain partial; see the transform report (local development reference).

Transform fields are floating-point document coordinates. A layer's image pixels remain at their
source resolution; moving or scaling changes the transform rather than baking the pixels.

## Selections

Rectangular select and Elliptical select create an in-memory coverage mask. Rectangle, Ellipse,
Lasso and Wand share three adjacent **New / Add / Subtract** buttons with one highlighted mode,
plus the Anti-alias option. Clicking the selected button keeps it selected; choosing a mode does
not edit the document or add an Undo step. Shift held at the press adds; Alt subtracts and takes
precedence over Shift. To constrain a marquee to a square or circle, release Shift after the
press and press it again. New mode clears the selection on a click without a drag; Add/Subtract
leave it alone. Ctrl+D clears it. Ellipse Anti-alias softens boundary coverage.

Selections can:

- Show an outline on the canvas.
- Constrain subsequent brush or eraser coverage.
- Move or duplicate part of the active pixel layer.

Selection operations are not saved in the project. Magic Wand, Color Range, Expand, Contract,
Feather, and Lasso are available.

**Object Selection (Q)** runs SAM 2.1 Tiny locally on the visible composite canvas. Existing MobileSAM
installations remain compatible; **Local model… → Download model** upgrades to SAM 2.1. Click an object
or drag a box around it; further clicks include areas and Alt-clicks exclude areas. The draft uses
the normal dashed contour around the object, with no photo tint or persistent prompt dots. A
black-and-white hollow circle follows the pointer over the image; its center is the click location.
Dragging still shows a selection box. Choose New/Add/Subtract to
combine with the selection that existed when the
draft started. A usable contour appears first; after 120 ms without new input, the app refines
its edges in the background. **Confirm / Enter**, or switching to another editing tool, uses the
latest ready selection as one Undo step. Switching while a result is pending waits for that result.
**Cancel / Escape** or Undo discards the draft and preserves the original selection and redo history. Start over clears
the prompts. Tiny disconnected speckles and enclosed pinholes are cleaned from the result;
substantial gaps, canvas openings and explicitly included/excluded small regions are retained.
The model boundary is fitted to the original image with a narrow-band color/contrast graph cut.
**Soft edges** estimates fractional coverage from nearby foreground/background colors. Additional
**Feather (px)** is optional (0–250, default 0); changing it reuses the prepared mask. Turning Soft
edges off keeps the fitted boundary with binary coverage. The contour stays sharp while actual
coverage is used when painting or creating a mask. This is local edge fitting, not hair matting.

The first use offers a roughly 156 MB model download or an offline model folder import. Files are
SHA-256 verified. No image is uploaded, no API key or Python installation is needed. Compatible
GPUs use DirectML for image encoding, with a CPU prompt decoder. Unavailable or failed GPU encoding falls back to CPU.
Choosing Q starts preparing an already installed model and the current image in the background;
first-time model initialization still takes time. Continuous corrections reuse image features.
See [local object selection](object-selection-2026-11-06.md) for file names,
measurements and MCP examples.

One-click **Select Subject (AI)** and **Remove Subject (AI)** remain unconnected; the macOS original
uses Apple Vision. Windows object selection needs a point or box. Hair refinement and automatic
whole-scene subject discovery are not implemented in this stage.

The marquee, lasso and wand options include a feather amount (1–250 pixels), Feather, Inverse
and Deselect. Feather uses the existing selection worker and one Undo step. Feather and Deselect
are disabled without a nonempty selection; these controls share the Select menu behavior.

Select > "Layer's Pixels" turns the picked layer's visible pixels into the selection and
Select > "Mask's Black Areas" the mask's dark areas — the same loads a Ctrl+click on that row's
image or mask thumbnail performs; both need exactly one layer picked.

With a marquee, lasso or wand tool in New mode, drag inside the current outline to move it.
Shift constrains the dominant axis. The outline snaps to canvas/layer edges and guides; press Ctrl
after starting the outline drag to bypass snapping. Its shape and feather survive dragging outside the document;
you can grab the off-canvas portion visible in the viewport to drag it back. Release records one
Undo. Clicking without moving deselects with marquee/lasso, or selects afresh with the wand.
Arrow keys move the outline by 1 pixel, Shift-arrow by 10, with one Undo per command.
Marquee drafts and moved outlines autoscroll near the viewport edges. These actions reuse the
outline geometry without recompositing the image.

To move the selected pixels, hold Ctrl before pressing inside the selection with a marquee, lasso
or wand tool. Ctrl+Alt duplicates them in the same layer. Shift constrains the dominant axis; pixel
movement does not snap. Release commits the pixels and moved outline in one Undo. Returning to the
starting point leaves history unchanged. Escape or moving focus away before release cancels.
With any tool, Ctrl-arrow moves selected pixels by 1 pixel, Ctrl+Shift-arrow by 10, with one Undo
per command. Pixel movement edits the pixel target; it does not move selected mask pixels.
With a nonempty pixel selection, Ctrl+T now floats those pixels into a temporary sibling above
their source. Numeric fields and ordinary move/resize/rotation handles edit the floating content;
the outline follows the affine transform. Releasing a drag retains the draft. Apply/Enter merges
pixels and selection back into the source in one Undo; Escape/Cancel restores the source exactly.
Switching layers/tools or saving/exporting waits for merge. Unchanged Apply preserves pixels/Redo.
Merging retires source text/shape/effects as in the original app; Cancel/Undo restores them.
Alt-drag after a floating draft merges it first, then copies the entire merged source layer.
The drag keeps the floating grab's original placement/size, as in the original canvas code.
A changed floating merge and copy are separate Undo steps; an unchanged merge adds no history.
Alt-click without movement keeps the floating draft. Cancelling the copy preserves the prior merge.
Finish or cancel a floating or whole-layer transform before using layer/pixel editing or clipboard
commands. These commands leave the draft and its source unchanged until the transform ends.
Ctrl-drag a transform handle to enter Free Distortion. Drag a corner independently or an edge
with both endpoints; Shift locks to the dominant axis. Subsequent handle drags keep distorting
without Ctrl, and dragging the body moves all corners. Releasing keeps the draft; use Apply/Enter
or Cancel/Escape. The rotation handle is hidden during distortion. Pixel layers, independent masks,
visible selected layers/folders and floating selections use the same controls.
Floating convex perspective carries the selection outline; folded shapes hide it and clear the
selection on Apply. Complete menu/target transitions and physical/Mac validation remain open;
see the distort report (local development reference).

Choose Lasso (L), then Freehand to drag an outline, or Polygonal to place vertices with clicks.
In Polygonal mode, Enter, a double-click, or clicking within eight screen pixels of the first
vertex closes the outline. Escape discards the draft; Backspace/Delete removes the last vertex;
Tab switches between Freehand and Polygonal. The options offer New/Add/Subtract and Anti-alias.
Shift held at the first point adds, Alt subtracts; the operation stays fixed for that outline.
Subtracting everything or contracting past the middle keeps an explicit empty selection.
Brush strokes then refuse to start without copying pixels or adding history; Ctrl+D allows
painting across the document again. Inverse with no selection does nothing; inverting Select All
deselects, while inverting an explicit empty selection selects all.
Selection contours follow the coverage mask rather than just its bounding box.

Dragging updates a lightweight overlay without recompositing the image. Freehand lasso retains
the sampled points and updates its preview once per display frame. Marquee and lasso masks,
combination, wand sampling/matching, Expand/Contract/Feather and contour extraction run in the
background; the preview stays visible until the
outline is ready. While the mask is being built, painting waits for that selection, and another
selection can replace or combine with the pending result. Completed commands and Ctrl+D retain
their order and individual Undo steps; Undo waits for earlier selection commands. Opening a
different project rejects stale results. Large or noisy masks can still take time to finish.

Color Range captures one composited sample in the background. Color/fuzziness changes generate
selection coverage and the small gray preview from one worker mask; only the latest request
publishes. Clear colors restores the selection from before the panel opened. Cancel restores it
without changing history; Apply waits for the current preview and records one Undo step.
The layer content menus/properties and Undo/Redo stay unavailable while this panel or a selection
amount prompt is active. Selection entry is also unavailable until a released brush stroke's
queued input settles.

The Core behavior is tested. Actual mouse, double-click, cancellation, and focus workflows still
need interactive acceptance on Windows; build/startup smoke does not establish those behaviors.

## Gradient

Choose Gradient (G), select one visible layer, and drag a line. Choose Linear or Radial,
Foreground to transparent or Foreground to background, Reverse, and opacity. The shared palette
supplies the colors. Paint mask edits an existing enabled mask, including folder/adjustment masks.

Releasing the pointer retains a draft: drag either endpoint to revise it, press Enter or Apply to
commit, or Escape or Cancel to discard it. A new line replaces the draft over the same original
pixels. Applying makes one undo step. Undo first discards an unapplied draft. Switching tools,
layers, or paint targets applies it; saving, exporting, and closing also resolve it. Outdated
drafts are discarded. Lines shorter than half a document pixel and unchanged drafts create no history.
Hold Shift while dragging either endpoint to constrain the line to 45° steps while preserving
its length from the other endpoint. Releasing Shift restores free movement on the next move.
Releasing the pointer retains the last dragged position, including its constrained angle.
If the latest preview is still rendering, Apply waits without blocking the interface. Parameters
and target controls pause until it finishes; Cancel or Escape can still discard the draft.

The gradient respects selection coverage and layer rotation, scale, and flips. Pixel/mask grids
can grow to cover the canvas; attached mask coverage expands without stretching. Mask gradients
use the palette's red channel as gray. Number keys 1–9 set 10–90% opacity, 0 sets 100%, and two
digits within 600ms set an exact percentage.

Rendering runs on the background worker and reuses its layer caches across draft frames. It
updates the affected layer/consumer footprint; expanding the grid may still affect the full canvas.
Core pixels and undo behavior have tests; endpoint
gestures, keyboard focus, target switches, zoom, and visual parity still need interactive Windows
acceptance. The measured 4K full-frame case remains slow; the cache optimization does not
establish interactive large-document performance. See the current performance and buffer report (local development reference).

## Spot healing

Choose Spot Healing (J) and select one visible image layer. Choose Content-Aware, Create Texture,
or Proximity Match; Tab cycles modes. Radius, hardness, and opacity use the brush controls, with
a separately remembered tip. Number keys set opacity as for Gradient; brackets adjust the tip.
Groups, adjustment layers, mask targets, and explicit empty selections are refused.

Drag over a spot. The dark wash is a temporary preview; releasing starts background repair from
the original pixels. Selection coverage clips the preview and final repair. A successful stroke
creates one undo step and retires editable text/shape metadata on the painted raster layer.
Escape, Undo, switching tools/layers/targets, opening another document, or closing discards a
pending stroke. Save/export wait for the stroke to finish or be canceled. A late result cannot
overwrite a changed document. Failure, cancellation, and unchanged pixels do not consume redo.

Kernel pixels are checked against 21 original C reference fixtures. Actual pointer capture,
keyboard focus, visual matching, and large-photo responsiveness still need interactive acceptance.
The repair work region is capped at eight million pixels; exceeding it reports failure and keeps
the document. The preview still allocates a full working grid, so large-image memory is unverified.

## Brush, eraser, and masks

The Brush tool exposes radius, hardness, opacity, color, and a Paint mask checkbox. The Eraser
tool uses the same brush geometry while removing premultiplied pixel coverage.

Radius, hardness and opacity have number fields beside their sliders. Numbers, sliders and
keyboard changes stay synchronized with the actual stroke settings. The small checkerboard tip
preview responds to radius, edge softness, opacity and foreground color; its size is scaled to
fit the toolbar rather than showing a document-pixel ruler. Switching brush families retains
their existing parked settings and does not create history.

Painting rules:

- Painting edits the selected raster layer in its local pixels through the current transform.
- Select one layer to paint. Brush, Eraser, Clone, Blur, Smudge and Liquify refuse multiple targets
  and layers hidden by their own eye or a parent folder, with a status explanation. A queued first
  dab checks visibility again before editing pixels.
- A blank layer gains its first raster when painted, filled, used as an all-layer clone target, or
  committed from a gradient. Before that, it saves without an image asset.
- Adjustment layers and folders cannot receive brush strokes on their image plane; their masks can.
- A selection, when present, clips the stroke.
- One pointer stroke is one undo step. Pointer updates are coalesced into a dirty-region repaint.

Shift-click continues a straight line from the previous stroke on the same layer and image/mask
target. While dragging, Shift chooses a horizontal or vertical axis from the first movement and
keeps it until released. These gestures also work with Clone, Blur and Healing.
Brush and Eraser offer Smoothing from 0 to 100, through a slider or number field. Zero keeps the
normal response; higher values let the brush trail the pointer and ignore small shakes. Release
finishes the line at the pointer. Smoothing follows zoom and also works when painting a mask.

For Clone, Alt-click sets the source. Aligned keeps the source offset between strokes; Sample All
Layers samples the visible composite. Each stroke samples the image at pointer-down, follows the
layer's placement and stays inside the selection. Overlapping passes share the stroke opacity cap;
transparent source pixels leave the destination unchanged.

Image Blur follows the layer's placement and uses canvas-sized blur radius, stroke opacity and
selection coverage. Dragging paints a continuous path from a frozen sample; another stroke softens
the committed result again. Blur currently edits within the image's existing bounds; extending
a layer by painting outside remains pending.

Blur also softens the selected mask through its placement, brush opacity and selection. An unchanged
mask blur adds no Undo step. Clone, Smudge and Liquify require the image target. Mask blur uses the
Windows Gaussian implementation; Mac Core Image pixel equivalence has not been established.

Brush, eraser, clone, blur and warp strokes keep the layer and settings selected at their start.
Their start, path and ending inputs wait between serial rendering frames. Switching tools or
layers finishes that stroke; Undo waits for its ending input. Effect halos are included in the
texture update. Programmatic GPU comparisons cover these paths; physical pointer/focus and
visual acceptance remain separate. Small edits to large effect layers use source-grid windows,
including the first dab of raster strokes. Starting a stroke still copies the layer's pixels;
Small blur dabs now prepare only the area they need. Known mask strokes update local effects and
paint through the mask's own placement. Opening a large document, painting broad mask regions,
sampling all layers with clone, and using large blur tips can remain slow.

To paint a mask, select a layer and choose Layer > Add Reveal Mask first. The new mask starts as
a uniform white plane and becomes the paint target immediately. Paint mask also selects existing masks:
white reveals and black hides. The Paint mask checkbox is disabled when the selected layer has no
mask. Layer > Remove Mask removes the raster mask metadata and asset.
The footer's Add Layer Mask button reveals the selected area, or adds a white mask with no selection.
Alt-click hides the selected area, or adds a black mask. Its hint follows the selection. The new mask
becomes the paint target; selection masks consume the selection and undo together with it. The row
context menu uses the same behavior. Explicit Layer > Add Reveal Mask / Add Hide-All Mask commands
always create a uniform mask while keeping any selection. Uniform masks use a 1×1 plane until editing
needs a larger grid. Painting a uniform mask now edits a local area instead of its entire plane.
Brush strokes can extend a mask beyond its existing placement toward the canvas, continuing its
white/black edge tone without moving the image. The stroke remains one Undo step, including growth.
Brush and eraser strokes also extend a layer's image past its pixel grid toward the canvas: the
original pixels keep their density, rotation and world position, only the touched area is kept,
and an attached mask re-edges the new area instead of stretching with the pixels. The Clone Stamp
samples the source frozen at stroke start (its own pixels, or the composite when sampling all
layers) in document space, and the Blur keeps its radius tied to the original grid with the
transparent-edge rule, so both grow the layer the same way. Painting a layer that still carries
editable text or shape metadata rasterizes it: the record retires with one Undo restoring pixels
and style together.
Mask creation is unavailable for multiple selected layers or an active text/transform draft.
The current mask-painting slice and editable 4K projects await physical acceptance (local development reference).

Move's target control can select an existing mask. An unlinked mask transforms independently;
a linked mask follows the layer. Layer menu controls also enable/link masks. Folder-mask painting
and full mask-property parity remain partial; thumbnail target switching is available.

## Crop

Choose Crop, drag a rectangle, and select Apply Crop. The document bounds become the selected
rectangle. Layer origins, unlinked mask placements, and guides are offset into the new coordinate
space; pixels outside the new canvas are clipped by compositing. Cancel Crop discards the preview.

Crop is one undoable operation. Ratio choices and resize handles are available; detailed
snapping and modifier parity still need interactive acceptance.

## Adjustment layers

Layer > Add Adjustment exposes:

- Invert
- Exposure
- Hue/Saturation
- Levels
- Curves
- Color Balance
- Black & White
- Gradient Map
- Grain
- Gaussian Blur
- Motion Blur
- Add Noise

An adjustment layer has no image asset. It covers the canvas by default and applies to the
composited content below it within its folder. Its opacity controls effect strength. The CPU
engine uses deterministic seeds for grain and noise so the same manifest produces the same sampled
pattern.

Levels, Curves, Hue/Saturation bands, Exposure, Gaussian Blur, Motion Blur, and Add Noise have
editors. Gradient Map, Grain, Black & White, and Color Balance still require structured project
or MCP edits for their full settings. A Camera Raw pixel-adjustment panel exists; this does not
provide camera RAW import/development.

Edit ▸ Add Noise opens a destructive noise filter: one row (Amount, the Mac's 0.1–400 %) plus the
Gaussian and Monochromatic switches, OK writing grain into the layer as one undo step. The grain is
drawn once when the panel opens, so raising Amount scales the same speckle instead of reshuffling it,
and a selection keeps the noise inside itself. This is separate from the Add Noise adjustment layer,
which stays live.

Edit ▸ Gaussian Blur softens the layer against transparency, not against its own edge: it gives the
layer a padded grid the size of the blur's reach, blurs that, and on OK cuts it back to what has
coverage — so the layer really ends up slightly bigger and its attached mask rides onto the new grid.
A selection blurs only inside itself; raising the radius re-grows the room but only ever grows. This is
separate from the live Gaussian Blur adjustment layer, which stays non-destructive.

Edit ▸ Motion Blur smears the layer along a direction (Angle −90…90, Distance 1–2000 px) on a grid
grown by half the streak, so the travel runs past the layer's edge instead of stopping at it; OK trims
back to what has coverage and the mask rides along, exactly like Gaussian Blur.

Edit ▸ Bloom / Glow casts the layer's own light back over it — Amount 0…100 and Radius 1–150 px — on a
grid grown by the glow's reach, so the light runs past the layer's edge and OK trims the result back.
Amount 0 closes the way Cancel does, without an undo step.

One filter sheet is open at a time: picking a second filter while a sheet is open says so in the
status line and leaves the open sheet exactly as it was — the Mac greys the whole Filter menu while a
sheet is held open, because each sheet froze the layer's pixels and the second would publish onto a
picture the first is rewriting.

The Dither, Tonal Contrast, Vignette, Add Noise, Gaussian Blur, Motion Blur, Bloom, Lens Correction,
and Camera Raw
filter panels stay
visible while a confirmed filter is applied. Their controls and close buttons are disabled, and an
Applying… indicator shows that the operation is still in progress. The panel closes when it finishes.

Floating filter, effect, and selection-amount panels use Cancel / OK. The keyboard shortcut editor
uses Cancel / Save. With focus in a panel, Enter confirms and Esc cancels when the focused control
leaves that key to the panel. Fields and open pickers handle their editing keys first; a multiline
field keeps Enter for a newline. Panels follow the configured Apply/Cancel bindings, while rebound
letters still type normally in text fields. The shortcut editor keeps its fixed Enter/Esc answers
when it is not recording a shortcut.

The Levels histogram shows the composite below that adjustment, weighted by transparency.
It computes in the background; Auto Contrast/Color/Neutral become available when the current
input is ready. RGB, Red, Green, and Blue reuse the computed bins. Reselecting or renaming a
layer reuses the result when its input pixels are unchanged; edits below it refresh the result.

## Blend modes

The renderer implements the 24 manifest modes:

Normal, Darken, Multiply, Color Burn, Linear Burn, Lighten, Screen, Color Dodge, Linear Dodge
(Add), Overlay, Soft Light, Hard Light, Vivid Light, Linear Light, Pin Light, Hard Mix,
Difference, Exclusion, Subtract, Divide, Hue, Saturation, Color, and Luminosity.

The core uses premultiplied RGBA8 storage and computes blend colors in gamma-encoded sRGB. Whole
pixel identity and 90-degree transforms have exact sampling tests; high-quality sampling uses
Catmull-Rom bicubic reconstruction.

## Layer effects

Present effects appear below their layer. Click a row to select the effect, double-click to edit
it, or Alt-drag its label onto another layer to copy it. The eye hides an effect while keeping its
parameters. Delete/Backspace and the trash button remove a selected effect before targeting canvas
pixels, masks or layers; this is one Undo. Clicking a layer/thumbnail or working on the canvas
returns to the layer target.

Layer > Layer Effects opens a floating panel for the selected pixel layer: stroke, drop shadow, inner
shadow, color overlay, outer glow, and inner glow. Each kind has an add/remove switch and its own
parameters; changes preview on the canvas as you edit and commit as one undoable step. Effects
follow the layer's opacity and blend mode, move with it, and clipping masks built on the layer
clip to the effect's shape. A layer without effects renders exactly as before.

## Text

The Text tool places a text layer: click the canvas, set the content, font, size, tracking,
leading, and alignment in the dialog, and confirm. The letters take the foreground color, the
first baseline lands on the click, and the layer is named after its text. The style is stored in
the manifest alongside the pixels, so the text stays editable: a Text-tool click on existing text
opens it INLINE on the canvas (the Mac's own door, InlineTextEditor), while NEW text still composes
in the dialog. The top options bar exposes font, size, color and three alignment buttons;
More opens the existing dialog for tracking, leading and the other settings. Fonts come from the ones installed on Windows; a
missing face falls back to a generic sans-serif.

Editing keeps the layer name, transformed upper-left corner, pixel scale, rotation, flips, and
existing color. One Undo restores the old text, pixels, and transform. Changing all the content
clears per-character overrides; changing the face clears font overrides. Drawing, filtering,
cutting, or replacing its pixels through MCP turns it into a normal pixel layer; Core Undo restores
its text editability. The inline draft drags and resizes its paragraph box through eight handles
(the plus marks a box too small for its words), and a selection inside it takes a chosen face or
color as a per-run override; whole-content edits clear those runs. Font and color controls respect
the inline character selection. Size and alignment apply to the entire text style. Using the
toolbar keeps the inline draft open; committing it records one text-edit Undo. On a selected
committed text layer, direct settings update the layer and can be undone; a color-picker session
groups its intermediate previews into one Undo when the picker closes. Without a text layer,
the controls set the defaults for new text. Tracking and leading remain in More.

## Selections and feather

Select > Feather keeps the fade as a persistent property of the selection: feathering again
combines the two amounts (the square root of the sum of their squares, capped at 250), growing or
shrinking the selection keeps the fade, inverting keeps it, and new selections start crisp.
Painting inside a feathered selection blends through its soft edge.

## Undo and redo

Choose **History** beside **Layers** in the right pane, or use **View > History**. Each retained
state has an operation label; the selected row is the current state. Click an earlier row to
restore it, or a dimmed later row to redo up to that state. Arrow keys also select history rows.
The canvas, active layer, selection and normal Undo/Redo stay synchronized. A new edit from an
earlier state removes its future branch. Finish an active text/stroke/transform/selection edit
before jumping through history; the panel refuses to discard an unfinished draft.

History belongs to each document tab and is kept only in memory. The existing Undo memory budget
can discard old states, in which case the first row says **Oldest retained state**. Locks remain
local editor state and are not undone. History rows add labels and identities to the existing
mementos rather than keeping a second canvas history. Return to **Layers** to edit layer properties.

Edit > Undo and Edit > Redo operate on in-memory mementos. Content mementos include the serialized
manifest, active selection, and references to immutable image and mask surfaces. Thumbnail loads,
marquee/lasso/wand, All/Inverse/Deselect, Expand/Contract/Feather and Color Range Apply use
selection-only mementos without manifest serialization or canvas recomposition. Slider drags, movement drags, scale
drags, adjustment slider drags, and brush strokes are coalesced into a single logical edit.

Saving clears the dirty flag but does not discard undo history. Opening a project starts a fresh
document and fresh history. External reload replaces the document and therefore also resets
session undo state.

## Export

File > Export PNG writes an RGBA PNG at canvas dimensions. File > Export JPEG asks for a quality
(0–100, remembered between exports) and a matte color that transparent pixels flatten over, then
writes the JPEG. Export is a derivative operation and does not mark the project saved.

Use View > Fit to Window to fit the full document in the canvas viewport and View > Zoom 100% to
return to document scale. The canvas also has horizontal and vertical scroll bars for navigation.

The MCP export_image tool accepts an explicit JPEG quality from 0 to 100.

## AI editing (experimental)

Open **Reference images and preservation** to add up to four local PNG/JPEG/WebP images. Assign
each a general, person/identity, clothing, style or composition role, and enter features to preserve.
References are uploaded only when Generate is pressed, after bounded local decoding and resizing
to at most 1600 pixels. The source canvas remains the first image. Preservation instructions guide
the model; pixels outside the selection remain protected locally. Reference choices are session-only.

Select an area, then use **AI Remove** or **Replace** beside the selection (also in the AI menu).
This floating bar appears only with a nonempty selection and a marquee, lasso or wand tool.
It stays hidden while painting, editing text or using the AI panel; AI menu commands remain available.
**Expand** works without a selection: enter the pixels to add at each edge. Describe the result,
choose **API Key** or the experimental ChatGPT account connection. Choose the model
directly in the panel; **Connection settings and key** contains credentials.
API keys can be used once or saved with Windows user encryption; account login opens your browser.
The API base URL defaults to OpenAI and accepts HTTPS compatible endpoints. **Save key** stores the
address with the key and restores it on reopening the panel. A saved key cannot be used at a different
address: enter and save a key for that address. Connection and model choices persist
outside projects. API image edits upload the image and mask as multipart files.

Switching connections clears an unsaved key.

API mode shows a privacy warning above Generate, including when connection settings are collapsed.
Generation sends the full composited canvas, mask and prompt to the configured service. Third-party
relays may retain, sell or misuse that data; check the provider's data policy and avoid uploading
personal or confidential content. Local key encryption does not protect data after it reaches a provider.

**Generate** uploads the visible canvas, selection mask and instructions to the selected service.
Only the selected area (or the newly added canvas area) is applied locally. A result stays in preview
until **Accept as new layer**. Compare the original and up to three retained versions before accepting.
Accept adds a top-level pixel layer and retains the source layers; canvas expansion is included in
the same undo step. Nonempty AI results automatically crop fully transparent margins, so a small
cutout gets controls around its content. Its position, scale and partially transparent edges stay
intact; the document canvas is not cropped. **Stop** cancels the local request; it cannot guarantee that the service stops
processing or billing. Changing the document/selection, switching projects or closing retires the session.

AI edits are limited to 16,777,216 target/output pixels, the document budget and 14 MiB per encoded input.
While generating, the target area shows a cyan/violet light sweep, a softly pulsing border and an
"AI is creating" badge. The effect fades after the request finishes or stops, and clears immediately
when the session retires. Windows animation preferences are respected with a static indicator.
These effects use the UI animation layer without rerendering document pixels.

Outputs with different dimensions are resized to the target canvas, which can change their aspect ratio.
Local API workflows are verified with simulated responses. A real compatible Flare API edit and
expansion were accepted, undone and saved/reloaded on October 28. Account generation remains
experimental and needs its own authenticated acceptance.

## External editing

Codex and other agents with their own image tools can generate a PNG and send it to Compositor
through MCP, without configuring the editor's API key. New artwork can be placed at exact bounds
and automatically trimmed to remove transparent padding. For selection edits/expansion, the
editor prepares local canvas/mask files, waits for the agent's result, then offers original/result
comparison and **Accept as new layer**. Acceptance is one Undo; protected original pixels stay
unchanged. The external-agent panel has no connection settings. The agent's service determines
its own tool availability, upload policy and charges. See [the workflow](agent-image-mcp.md).

Agents can now refine existing layer masks with the native mask brush or gray-preserving edge
filters, inspect grayscale masks and cropped layer details, and Undo the change. Original image
pixels are preserved. Brush coordinates use document pixels; filter amounts use mask pixels.

The API adapters do not expose provider task IDs or support resuming interrupted cloud jobs.

External agents can also create/edit text and adjustment layers, import exact selection masks,
and group multiple local edits into a single undoable batch. See the MCP reference for arguments.

MCP creation requests show a virtual AI cursor dragging a frame from the element's top-left,
with its actual width × height in document pixels. The element appears after the frame is drawn;
transforms animate toward the requested placement, while masks and adjustments highlight their
target. Batches and rapid consecutive requests use shorter feedback. The overlay follows zoom
and pan, does not capture input, and adds no layers or Undo steps. **View > AI Operation Animation**
turns it off and remembers the choice. Windows animation settings are respected: when animations
are disabled, feedback is static and editing has no animation delay.

AI layout commands can group/ungroup layers, align their rotated placement bounds to each other
or the canvas, distribute equal gaps or centers, and crop the canvas without deleting original
image pixels. Folder content, including hidden children, moves together; linked masks follow and
independent masks stay fixed. Ungroup uses the native behavior, discarding the folder's own
opacity/blend/mask. Each request is one Undo and can join a local editing batch.

Selection, mask and PNG layer-import requests each have a 20-second preparation/publication deadline. Expired
results are discarded even if background decoding or computation finishes later.

Batch edits keep one final undo step without generating full-document undo snapshots for every
intermediate command. Failed or net-zero batches preserve the existing redo history.

External agents can use the [live editor MCP tools](mcp.md) to select/import layers, move/resize/rotate
them, reorder/copy/delete them, and change names, visibility, opacity and blend modes in the current
unsaved document. Each content operation uses normal Undo/Redo; selecting a layer adds no content
history. Import accepts a local PNG and centers it in the current folder. Transform uses the same
visible-content and linked-mask rules as the Move tool. Deleting a clipping/live-mask source with
surviving dependents is refused. These local layer tools do not upload images. Restart an existing
MCP connection after updating the server to discover new tools.

When a saved project is open, ProjectWatcher observes manifest.json and images/*.png. It waits
350 ms after the last event, computes a digest from manifest bytes plus asset names and sizes, and
reloads only when that digest changes.

If the document has unsaved edits, the Windows shell does not replace it silently: it asks, offering
Revert (take the disk version and give up the unsaved work) or Keep Mine (keep editing and let the
next deliberate save overwrite what is on disk). Closing the question without answering leaves the
change pending, and a save is refused until it is resolved. While a stroke, transform commit or open
filter sheet holds the pixels, the question waits (with backoff) rather than pulling the document out
mid-edit. The window watches the tab on screen, so a project edited from elsewhere while parked shows
up the moment you switch back to its tab — a clean tab reloads on arrival, a dirty one asks then.
