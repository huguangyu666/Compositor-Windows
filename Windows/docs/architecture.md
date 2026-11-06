# Architecture

The Windows port is a separate implementation of Compositor. Its project boundary is the .comp
package. This repository distributes the Windows implementation; the original SwiftUI/AppKit
implementation is maintained upstream and is not referenced by the Windows solution.

## Projects

Multi-document: Compositor.Core's ProjectWorkspace owns the tab registry (order, selection, the
reuse-empty and numbering rules, drag clamping, the dedupe lookup, the quit order, and the
re-entry guard for management flows); the window hosts it and remains the owner of the live
document's bindings: event subscriptions, the folder watcher, the viewport, and the layer-panel
selection. The window's document and projectPath read and write through the selected tab, so the
registry is the single truth of what is open. Switching parks the leaving tab's view and handlers
and rebinds the arriving tab through the same swap OpenDocument runs; the Mac's select-style commit
before switching is inert while the switch gate (ProjectEditsInFlight, the close gate's in-flight
terms without the dirty test) already bars every way a transform can be live. New, Open, close-tab
and the window's own close all route through the registry flows; the strip pills are a mirror
rebuilt from the registry when their signature changes, never a second copy of state. See
port-status-2026-10-07.md (local development reference) for the three delivery steps and their
verification scopes.

P86 distortion captures immutable corner arrays alongside each serial transform render request.
TransformEdit prepares bounded previews and full-resolution warped pixels/masks separately;
EditorDocument accepts only the prepared full result after source identity/revision validation.
The UI publishes one history step, cancels retired-owner work and releases output waiters.
Floating selection requests carry the same corners through preview/merge; their convex outline
maps the retained geometry, while a fold hides the outline. See the
ownership and verification report (local development reference).

DistortWarp.WarpMask shares the RGBA warp's bounds, flip permutation and inverse geometry, but
samples four gray values directly into one output plane. It preserves the uniform mask identity,
outside background and row cancellation checks. No temporary source/output RGBA planes are needed.
RGBA bilinear warps share tap addresses across channels. AVX/SSE4.1 uses four independent double lanes,
with separate multiply/add, ties-to-even rounding and bounded four-byte reads/writes. Unsupported hardware
keeps scalar interpolation with the same shared addresses. Inverse mapping, folded geometry and row cancellation
remain unchanged. See the RGBA warp measurements (local development reference).
Convex gray warps prepare column m11/m12/rx and row m21/m22/ry terms. Hardware vectors batch
the original inverse and bilinear expressions; narrow tails and unsupported hardware use scalar
sampling. Pooled scratch holds five rows of geometry, with no source pixels or document-sized
temporary plane. Folded masks retain the two-triangle path. See the
preparation measurements (local development reference) for byte comparisons and timing limits.
AVX2 gray sampling gathers four target pixels per batch. Each four-byte read starts no later than
the source array's length minus four, then shifts to the requested byte. Short sources and other
hardware use the existing portable sampler. Lerp order and output rounding stay unchanged.
The 2048 preview still needs document-sized mask presentation; its 4K complete frame remains costly.
See the gray warp measurements (local development reference).

    Compositor.App
      WinUI 3 window, commands, canvas interaction, layer panel, file pickers
            |
            +--> Compositor.Renderer
            |      BGRA presentation, viewport coordinates, pixel rulers and palette samples
            |
            +--> Compositor.Core
                   .comp model, validation, WIC imaging, CPU compositor, edit session, watcher

    Compositor.Mcp
      stdio JSON-RPC process --> Compositor.Core --> same .comp package

Compositor.Core is a domain boundary inside this application, not a portable .NET Standard
library: it targets Windows and uses Windows Imaging Component APIs through WinRT. Renderer keeps
WinUI bitmap presentation out of the compositor math. App currently owns most view interaction
and command wiring. Mcp references Core without referencing the UI.

## Project state

ProjectManifest stores document metadata and layer records. ProjectSnapshot pairs that manifest
with decoded raster images and grayscale masks keyed by layer UUID. The manifest is the
serialized source of truth; image and mask pixels live in images/.

ProjectStore.LoadAsync reads a bounded manifest, deserializes and validates it, then checks and
decodes every referenced asset. A load either returns a complete snapshot or fails before the UI
replaces its current document.

App project opens capture the current document and a cancellable request identity before the
picker or decoder runs. A newer open, document replacement or window close cancels the old request.
The App decodes first, confirms unsaved work once, then checks identity again before installation;
the confirmation includes edits made during loading. Late results and faults cannot alter another
document. Recent-project paths honor the same process-local preference directory override as other
preferences. See the project-open follow-up (local development reference).

ProjectStore.SaveAsync validates the manifest, encodes all referenced surfaces, atomically places
assets, then atomically replaces manifest.json. Only after the new manifest exists does it remove
orphan PNGs and the stale QuickLook directory.
App saves are serialized and bound to their starting document. CaptureSave freezes metadata
and dictionaries, shares settled immutable assets, and copies only live brush/mask/clone/blur
buffers. CompleteSave records that capture's asset stamps and disk digest; newer edits remain
dirty. Saving only changes disk bookkeeping, so completion does not invalidate canvas content
or cancel a newer draft. A retired save cannot rebind another document's path, watcher or status.
See the save ownership report (local development reference).

## Compositing

CanvasCompositor traverses the validated folder tree in bottom-to-top draw order. Folders are
pass-through; visibility inherits from ancestors and folder opacity multiplies into descendants.
Raster layers are inverse-mapped through LayerTransform by Resampler, then masked, clipped, and
blended into a premultiplied RGBA8 Surface.
Normal/full-opacity composition retains uniform-alpha copies/skips and batches mixed blocks with
AVX2 integer arithmetic, preserving float-path bytes and clamping. Unsupported hardware, narrow
tails and shared arrays with different strides keep scalar blending. See
mixed-alpha composition (local development reference).

The software engine implements 24 blend modes and all 12 adjustment kinds represented by
AdjustmentKind. Blend colors are computed in gamma-encoded sRGB. The resampler supports nearest,
bilinear (Smooth), and Catmull-Rom bicubic (High quality) sampling. Blur and motion blur sample
neighboring pixels; seeded grain and noise are repeatable for the same document state.

Axis-aligned weighted RGBA sampling uses four independent SSE4.1 float lanes when supported.
Channels share tap indices and weights but retain each scalar horizontal/vertical accumulation order,
ties-to-even rounding and clamping. Four-byte bounded reads/writes preserve cropped SourceWindow
addressing and padded strides. Unsupported hardware keeps the scalar loop; nearest, exact row copies
and rotated sampling retain their existing paths. See the measurement report (local development reference).
When output rows share source taps, non-aliased SSE4.1 draws retain at most two or four horizontally
filtered rows for that draw. Global source-row tags distinguish slots; each valid column is initialized
before use, and pooled vectors are returned in finally. Aliases and non-overlapping rows keep forward
sampling. See the row reuse report (local development reference) for exact-byte and allocation checks.

The CPU engine remains the deterministic pixel reference and handles export. App additionally
hosts a D3D11 SM5 renderer through Vortice on Win2D's device, sharing its final texture for display.
It caches source/revision textures and known dirty uploads under an LRU memory budget. Ordinary,
settled, affine-animation and draft composition attempt the GPU path; unavailable features,
software adapters, memory pressure and device failures recover through CPU. There is no general
tile cache. See [GPU rendering](gpu-rendering.md) for scope and limits.

CanvasThumbnail ports the canvas-framed layer/mask thumbnail layout: 36/30-point slots fitted to
the canvas and two raster pixels per point. Resampler and MaskDisplay place the source, with the
mask edge background outside its bounds. The App caches source identity/revision, a copied placement
and canvas size per thumbnail. Settled transform frames refresh their changed rows; cancellation
restores the document placements. Thumbnail preparation touches at most a 72-square output, without
a document-sized scratch surface. CoreGraphics resampling parity is not claimed.

## Editing session

EditorDocument owns the current snapshot, dirty flag, undo and redo stacks, active selection, and
edit operations. Undo mementos contain a compact manifest plus dictionaries referencing immutable
surfaces and the active selection. Replacing pixels creates a new surface entry, so history does
not need to copy every layer for each operation. SetSelectionUndoable stores only immutable
coverage, active layer and dirty state; its Undo/Redo shares the current snapshot and emits
SelectionChanged without manifest JSON or asset dictionaries. Retained selection coverage counts
toward UndoByteBudget once per identity. Ordinary content history still serializes the manifest.

Current edit operations include layer import, deletion, sibling reorder, visibility, opacity,
blend mode, move, scale, adjustment mutation, mask creation/removal, brush strokes, canvas bounds,
and undo/redo. UI pointer handlers call these operations and mark the changed canvas rectangle for
repainting. Slider and drag updates can share a coalescing key and become one undo step.

Layer/effect drags carry their starting EditorDocument, source IDs and native source items in one
window session. Native completion and effect StartDragAsync cleanup retire only that session;
replacement/close discard it. Hover and drop validate the owner, viewport and destination before
calling the existing one-Undo move/copy operations. Payload text alone cannot route an outside drag.
DragOver remains unhandled so ListViewBase retains its native edge scrolling.

Layer context requests resolve the row/thumbnail/effect target and settle changed selection through
the serial input queue. Menus retain their document, selected IDs and target; a stale menu cannot
execute against another owner or selection. Commands reuse existing edit operations. Same-row
transform/text drafts retain disabled commands. Thumbnail content actions reuse inline text and the
adjustment dock. AddSelectionMask clears the selection inside its existing history transaction, so
Undo restores both the previous selection and absence of the mask.

Blend picker sessions retain their document/layer owner. Highlighting creates a lazy frozen-metadata
snapshot on the render worker, sharing image/mask assets. It uses the existing compositor, dirty band
and revision-checked frame publication; repeated highlights reuse the request. Dismissal retires the
preview, while committing updates the live mode once and retains compositor caches. The deferred
close checks session identity; programmatic popup focus cannot override a pointer highlight.

Opacity gestures retain a document, layer and coalescing key only for the slider or label drag.
Numeric commits and arrow steps use standalone edits. Selection/document changes, focus loss and
Undo end the gesture through the existing EndTransform no-op cleanup. Echo suppression keeps paired
controls from issuing duplicate edits; later ticks retain the compositor and layer rows.

The shared opacity-digit handler routes the existing rebindable digits to brush/gradient settings
or Move's selected layers, retaining the original 600ms two-digit rule. SetLayerOpacities changes
selected records, including folders, in one history transaction without replacing raster assets.
Changed-layer bands and the warm compositor are retained for these appearance-only edits.

AddBlankLayer uses the existing asset-free manifest representation, numbered names and the active
sibling/folder insertion rule. The App selects it immediately against frozen render metadata, so
a stroke queued behind a running frame captures the new target. Brush/fill/all-layer clone operations
prepare its first raster within the pixel budget; ReplaceLayerImage adds the image filename and
publishes the first edit through the normal single Undo transaction. Gradients already support
asset-free sources. Creating a blank layer allocates no image surface and does not change the format.

Footer, context and explicit mask commands share one App creation path. The footer interprets Alt
and the current selection; explicit white/black commands preserve selection. Solid masks use the
existing 1×1 representation, and successful creation targets the mask. The mask-alone badge follows
the derived current target and name. Its close action enters the serial input queue, validates its
owner/target and requests the composite without changing history or the mask paint target.

Pointer raster entry checks the selected-layer count and effective visibility before retaining a
stroke. Its serial first-dab action rechecks inherited visibility against the captured owner, so a
target hidden while rendering cannot acquire pixels or history. Refused presses avoid dirty work;
folder-mask painting continues through the existing Core plane checks.

Delete uses a scoped PreviewKeyDown matcher for canvas/list responders, with field and modifier
guards. After lasso vertices, a selected effect takes precedence over selection pixels and mask/layer
targets; trash also honors the effect target. Effect controls are retained under each native layer row;
effect selection keeps one active layer while clearing native layer highlights, without content history
or rendering. Visibility uses the existing optional per-effect enabled field, preserving parameters through
panel edits and copies. Effect mutations enter the serial input queue, settle an open effect panel before
replacement, and produce one Undo. SelectionPlaneEdit captures settled metadata, selection identity and
asset references/revisions at the serial input boundary, runs the existing pixel/mask kernels on
detached metadata on a worker, and commits one validated replacement and one Undo through that queue.
It also prepares new selection masks; attaching the mask and consuming the selection form one Undo.
Editing pauses during preparation while navigation and the dispatcher remain available. Plane clears,
selection-mask creation and layer deletion share cancellation ownership; document/window retirement cancels the old work,
and an old completion cannot release a newer operation's busy state.
Selection coverage honors the edited plane's placement/flips with retained row rotation terms;
selected fills expand uniform masks without mutating shared coverage or adding no-op history.

LayerDeletion captures settled metadata, asset references/revisions, selected subtrees and surviving
clipping consumers. The App offers Cancel, Remove Links or Bake before the serial input queue commits
one validated snapshot and one Undo. Baking uses the target's source grid and the compositor's source
coverage/effects caches on a worker, with a one-pixel scratch canvas. Target masks/effect settings
remain; baked text/shapes become raster pixels. Adjustment consumers are unlinked, as stated by the
dialog. Changed sources and retired documents reject the result. Deleted asset keys leave the live
snapshot while history retains what Undo needs.

FileImportBatch is a lease for one file request. It validates a detached candidate before opening
history, publishes each success with Changed, and retains one pre-request memento for the batch.
Metadata, selection and asset references/revisions guard against interleaved source changes.
The app queues requests by captured document owner, waits for editing/project access, decodes
off the UI thread, and publishes through the serial render-input queue. Document retirement
releases waiting callers and prevents a completed old decode from modifying the replacement.
PSD parsing itself is not cooperatively canceled. The picker/provider boundary and broader
file-menu/multi-project parity are described in the import report (local development reference).

PSD binary readers share the source byte array but enforce a separate start/end for layer,
info, extra, mask and channel sections. Unsigned PSD/PSB lengths must fit the materialized input,
and RLE row tables/encoded extents are checked before their metadata/output allocations.
After these boundary checks (local development reference), PSD budget admission (local development reference)
checks the remaining image/mask quota before channel decode. Oversized rectangles use the Mac
canvas-cropping fallback while preserving source strides; raw/RLE decode only the target window.
Channel memories share the input. Masks map their patch/default onto the layer grid and retain
placement/linkage through import. The app captures remaining pixels per file and reports crops;
both Core publication paths check the combined quota before opening history. Full-file reads,
cooperative cancellation and process peaks remain separate work.

GradientEdit retains the original pixels and geometry and creates a separate GradientPreview
snapshot. Fixed geometry/metadata and original-grid preparation are reused across previews.
Full-coverage previews batch four pixels with AVX/SSE4.1 double lanes, preserving scalar order
and half-up rounding. Clipped/selected/rotated grids, narrow tails, distant endpoints and unsupported
hardware retain scalar processing. See gradient vector rows (local development reference).
The public Preview API clones its output pixels, and commit detaches the shared read-only draft
manifest. The App's serial reader uses two internal output slots, protecting the last UI-published
preview, even when newer calculated frames were superseded. Reused slots bump their pixel revision
before compositing. Each slot is limited to 64MiB; larger planes retain independent allocation.
Clearing the draft releases its workspace, and commit transfers its protected slot to the document;
any retiring reader can write only the other slot. See buffer ownership (local development reference).
Pointer input changes endpoints/settings; the serial render worker fills and composites
the draft. Identical endpoint/settings events do not queue another frame. A redraw or Apply
can reuse the last published immutable preview when the draft identity, both endpoints and
all settings match; cancellation/replacement releases both request keys and the preview.
Apply and dependent tool/layer/target/save/export/import/close paths await that captured
input's publication on the serial worker. One completion task serves repeated Apply requests;
parameter and target controls pause while Cancel remains available. Cancel or document
retirement releases callers without committing, and continuations validate their document
after the wait. No gradient pixel preview is calculated on the UI thread during Apply.
`CanvasCompositor.SetSnapshot` refreshes lookup references at the same canvas size
while keeping its canvas, scratch buffer, and placed/content/effects caches. Fingerprints include
clipping sources' effective folder opacity. The draft dirty band includes old/new effect bounds,
folder/live-mask consumers and downstream sampling halos; broad grids may still cover the canvas.
Queued bands also wake the serial worker when endpoints change during an in-flight frame.
Applying checks that the source is still current, swaps in the snapshot, and records
one undo step. Discarding leaves the document untouched. Mask placement determines sampling;
linkage determines how later layer move, scale, and transform edits carry a placed mask.

HealingStroke also keeps preview state outside EditorDocument. TileStroke accumulates raw brush
coverage for the C-derived patch search and selection-clipped coverage for the dark wash. On
release the background worker repairs the original pixels, then blends through the selection.
Commit checks snapshot identity, manifest, selection, and image/mask revisions before replacing
the snapshot once. App input queues preview changes between serial render frames; cancellation
invalidates the job without changing history. SpotHealingEngine has an eight-million-pixel work
budget and cooperative cancellation. No native C library is required at runtime.

Selections are session state represented by grayscale coverage. The app displays the selection
outline and the brush engine uses the coverage to restrict painting. The project format has no
serialized selection field. `EditorDocument.SelectionChanged` reports selection-only edits
separately from content `Changed`, preserving the compositor, warm caches, and stable layer rows.
The App queues marquee/lasso rasterization, combination and selection modification on workers.
Undoable commands wait for the preceding UI publication through a completion task, preserving
each history step. Undo/Redo inserts a barrier into this chain and waits for the render reader;
an Add after Undo consumes the restored coverage. A failed command completes with the last
published selection, preserving the input for later Add/Subtract and leaving history untouched.
Epoch/document guards reject stale publication. Contours are
extracted by `SelectionContours` with contiguous row traversal and cancellation. On AVX2, matching
uniform 32-pixel spans in adjacent rows skip scalar scanning only when their left neighbors also
exclude any open vertical edge. Ordered runs, the midpoint threshold and the scalar fallback remain
unchanged. A worker prepares
invariant-culture path data in batches of 256 independent runs; the UI parses native subgeometries
and retains them in a GeometryGroup, yielding between short construction batches. Small shapes
remain a single PathGeometry. Translation and feather reuse the geometry. Floating perspective keeps
the immutable contour runs alongside the root geometry, maps them on one cancellable worker, and
parses projected native batches on the UI thread. Repeated mapping notifications reuse the latest
request or result; newer mappings replace a single pending request. Publication checks owner,
revision, source geometry/runs and selection offset, while cancellation, folds and owner retirement
reject obsolete work. Pointer previews remain visible while geometry is prepared;
lasso samples are retained while preview updates coalesce at `CompositionTarget.Rendering`.
Detached native outline roots stay strongly referenced until their child/figure collections are
emptied on the UI thread. Retirement removes from collection tails and yields at roughly 2ms
intervals through low-priority dispatcher work, keeping new contours and frame publication ahead
of cleanup. Current source/display roots are protected; canceled partial builds use the same queue.
This avoids a wholesale WinUI UI-affinity destructor callback, while temporarily retaining old roots.
See the native retirement report (local development reference) for ownership checks and timing limits.
The follow-up (local development reference) retains per-figure retirement after bulk-clear
experiments caused long calls or failed to drain. DEBUG outline measurements are local to a build and
published only with its current geometry/observation epoch, so a canceled extraction cannot overwrite
the next shape's reported run count or timings.
DEBUG retirement observations retain at most 32 completed records without Geometry references,
plus the current root's figure progress and explicit dispatcher waits, including an in-flight wait.
Elapsed time minus those waits is a wall-clock remainder, not thread CPU time. The
collection investigation (local development reference) separates these observations from
unadopted algorithms and retains the actual drain failure.
The accepted display target is the current WinUI outline pixel for pixel, including antialiasing and dash endpoints.
An opt-in Direct2D path (`COMPOSITOR_DIRECT_OUTLINE=1`) bypasses the WinUI contour object tree.
One preparation worker builds a CanvasGeometry from immutable runs; newer source/projection requests
cancel its token and replace a single pending request. Publication validates owner, selection shape,
source/session identity and offset. The UI owns source and projected resources, disposing replaced
or unpublished results. Translation/feather/affine reuse source geometry; folded projection hides it.
CanvasControl NewDevice resource notifications cancel old direct preparation, retire published resources
and reconstruct the current selection through that same worker. DPI-only notifications retain contours.
See the interaction integration report (local development reference) for full direct-mode
regression scope and the distinction between callback simulation and actual driver/device loss.
The renderer's edges differ from XAML, so the default backend and its original regression gates remain.
See the direct backend report (local development reference) for exact scope and preview launch.
Thumbnail loads use RasterSelection to threshold source alpha or mask black before inverse affine
sampling with four-by-four subpixel coverage. Holes and independent mask placement are preserved;
this approximates the source CGPath tracing and rasterization, not macOS byte parity. Undoable
thumbnail jobs wait for preceding UI publication, preserving per-click history and no-outline
no-ops. Epoch/document guards cancel obsolete publication. The exposed marquee/lasso/wand,
All/Inverse/Deselect and Expand/Contract/Feather commands share selection-only history.
Contract past the middle retains explicit empty coverage; Bounds and CoverageAt treat it as
empty even with feather. App pointer strokes and Core BeginStroke reject it before cloning
pixels or opening history. The ellipse antialias option uses four horizontal subpixel scanlines
with analytic endpoints; its coverage is a Windows approximation of Core Graphics.

Pending render-input mutations block selection entry until a released stroke's final input is
settled, so sampling cannot share pixels with an unfinished queued stroke.
SelectionSampleInput captures frozen manifest metadata on the UI owner and shallow-copies asset
dictionaries while sharing immutable pixels. Wand renders one captured composite or unmasked
active-layer placement on a worker before matching; metadata JSON capture remains on the UI.
Color Range captures one composite at panel entry. Each settings request derives both full
coverage and a bounded gray RGBA preview from one full mask on a worker. Color choices entered
during the initial sample are retained and matched when that sample arrives. Request/session/document
guards publish only the latest result. Preview SetSelection does not write history; Cancel
restores the original, and Apply waits for the latest request, restores the original and commits
one SetSelectionUndoable. Edit/layer controls are disabled during this panel or an amount prompt.
Large in-flight raster loads, matching and morphology currently lack cooperative cancellation;
invalidated jobs can still consume CPU until they finish. Full source menu/transient-state
availability and floating-selection transforms remain separate parity work.

PixelMoveEdit captures a fixed source grid, metadata, assets and selection. Its serial worker
lifts premultiplied pixels through document coverage and updates 256-pixel tiles from immutable
bases. Old target tiles are restored on each move. A borrowed preview raster reuses metadata
and changes only old/new source windows; same-sized originals retain known-change ancestry for
the warm compositor. The logical displayed outline moves separately from document selection.
On release the worker finds the exact nonzero-alpha bounds across source row segments and tiles,
copies a detached result, aligns attached masks with white new coverage, and retires text/shape
metadata. Snapshot/manifest/selection and asset revision guards precede one pixel-plus-selection
history change on the UI owner. Ctrl-arrow commands await prior publication, while cancelled
or replaced owners cannot publish a result. See the pixel report (local development reference).

FloatingSelectionEdit is a separate Core transaction. It captures the
source snapshot/manifest/selection and asset revisions, then prepares document-resolution raw
selected pixels plus a cleared source and temporary sibling on a serial worker. Ordinary previews
share those rasters and detach metadata per frame; perspective/folded previews cap their raster at
2048 pixels and cache copied corner values. Final merge expands the cleared source grid by the
floating document bbox mapped back into that grid, aligns attached masks, and retires source
text/shape/effects as the original FloatingSelection merge does. Source guards precede one
pixel-plus-selection Undo. Affine selection mapping preserves logical off-canvas shapes and
document-pixel feather; folded distortion drops the selection. No transient selection or floating
state is added to the project format. The App connects ordinary Ctrl+T, affine outlines/handles
and queue/owner barriers; corner distortion remains pending. See
the Core report (local development reference) and
the App report (local development reference).

Selection also retains an unclipped source mask and an integer outline offset. Translation shares
that immutable mask; CoverageAt inverse-maps hard samples, while projected and feathered canvas
coverage is generated lazily. History compares logical outlines and counts materialized caches
without forcing projection. The newest oversized memento survives the budget while older steps
are trimmed. The App previews a move outside history, then commits one selection-only memento.
Feather coverage keeps the original three box passes, clipped sample counts and integer half-up
rounding. A byte row ring and column sums replace the full integer intermediate planes. Later
passes reuse the private output after filtering each future source row; only the completed result
is published to the selection cache. Source coverage and outline geometry remain unchanged. See
the cold feather measurements (local development reference).
Fixed-count feather intervals use a 48-bit upward reciprocal with an exact quotient under the
supported mask/count bounds. Horizontal boundary intervals keep integer division; a box covering
the entire row fills its constant middle interval. Scratch ownership and cached bytes are unchanged.
See the division follow-up (local development reference).
Contours are built at source coordinates once, even if movement arrives during extraction; one
TranslateTransform carries the latest offset. A 60Hz dispatcher timer pans the viewport for moved
outlines and marquee drafts, following a fixed pointer without recompositing the document.

LevelsHistogramInput captures only render-related metadata and revision-stamped asset references
on the owner thread. A serial worker freezes the metadata, renders the hierarchy prefix, and
computes alpha-weighted bins. LevelsHistogramCache retains at most eight small results under an
8MiB budget, without retaining pixel surfaces. UI publication checks the current document,
panel, request, cancellation token and input key. Cached Auto Levels also validates before
opening history. Single-pass histogram rendering skips per-layer placed/content buffers;
effect cache limits remain unchanged. Large individual resamples still delay cancellation.

The layer panel keeps stable Grid rows in an ObservableCollection and refreshes their current
record references after Undo. It reuses four recently selected Levels views. Changing only the
active layer refreshes properties and overlays once without invalidating canvas compositing.
Image and mask thumbnails sample into 32x32 bitmaps, reusing weak source identity/revision keys;
they do not composite a document or retain source surfaces. Stroke thumbnails refresh after
queued input settles. The 56-point rail and fixed tool header share the existing tool handlers.
The right divider clamps the panel width and stores only that local preference, outside .comp.
Thumbnail buttons share source target/enable/link/solo/copy actions. The global checkbox and
inspector selector share a serial target switch. Requests retain owner and
primary identity, reject floating/preparing/copy states, and preserve a same-layer persistent
whole-layer draft. Cross-layer thumbnail targets wait for merge and copy before moving focus.
Ctrl coverage loads and mask copies use the selection availability guard even before the floating
Inspector is published. New selected rows settle native layout before selection synchronization;
unchanged selected rows avoid another setter. See the target report (local development reference).
Solo-mask rendering uses the
compositor's existing canvas while retaining normal placed/content/effect caches. It draws raw
grayscale coverage at the mask's placement and fills outside it with a binary tone derived from
a revision-cached 96-pixel thumbnail perimeter. Mode, mask ID or background-tone changes expand
the presentation band to the full canvas. Integer unrotated 1:1 placements expand gray rows to
RGBA directly. Axis-aligned HighQuality placements prepare pooled column clamps/weights and row
clamps/weights, keeping the scalar accumulation order. On AVX2, four-pixel interior batches reuse
the individual horizontal products of four source rows in width-bounded pooled scratch. Row keys
reset on every draw; only terms filled for this call may be read. Clamped groups and tails use scalar
sampling. Other placements/qualities use the scalar resampler. The App uploads the same expanded band.
See the batch report (local development reference) for bounds and retained observations.
Debug frame diagnostics separate preparation,
composition/mask display and BGRA work; the display report (local development reference)
records the remaining complete-frame costs.
Solo display retains a copy of the last mask placement. Geometry/flip/sampling changes also union
the old/new mask footprints at render scale, independent of the composite's pixel-layer bounds.
Stationary mask edits keep the caller's small band.

## Rendering in the window

SurfacePresentation.FillBgra exchanges red/blue in width-bounded integer vector batches on
supported little-endian hardware. Source/target row spans preserve source Stride and packed
output pitch. Tails are scalar; short rows, software, other byte order and shared arrays retain
the old byte path. G and A remain unchanged, and the GPU still draws the checkerboard beneath
premultiplied pixels. Staging ownership and serial dirty uploads are unchanged; see the
presentation report (local development reference) for proofs and observations.

P83 Core records the last 64 known source-pixel edits on Surface and MaskSurface. Effect renders update only
their output halo from a larger input window, retaining the original Gaussian plane's edge
and kernel choice. Known mask edits map into the source grid, including independent placements;
their separately rasterized grids update that rectangle. Unknown revisions, unproven replacement
assets or changed mask geometry/settings invalidate
previous results and conservatively expand the presentation footprint.
Same-sized windows reuse up to 2MiB of workspace within the existing 64MiB effect-entry limit.
The first active outside shadow, or outer glow without an active shadow, may borrow its still-blank
RGBA output as a float workspace for the box blur. The renderer restores zero bytes in finally before
coloring or publishing it. Exact kernels, later effects and already-painted outputs keep independent
scratch; public result ownership and both budgets remain unchanged. See the
output workspace report (local development reference) for allocation and ownership controls.
Source-over of the layer pixels inside an effect window uses exact integer byte arithmetic:
`(source*mask + destination*(255-shapeAlpha) + 127)/255`. The odd denominator cannot produce
a half tie; the original zero-alpha skip and unchecked byte conversion remain. Coverage
generation, full-opacity row copies and effect ordering are unchanged. See the
blend arithmetic report (local development reference).
The three-box Gaussian keeps one full float plane and a ring of horizontal rows needed by
the current box; sum/conversion order, kernel choice and full-plane clamping are preserved.
AVX prepares four independent horizontal rows at once, with three additional ring slots for bounded
lookahead. Vertical columns and final byte rounding are batched without changing coverage bytes.
Small kernels retain exact convolution; without AVX, horizontal rows and output rounding remain
scalar, with vector or scalar column updates as available. See the
effect batch report (local development reference) for byte controls and complete brush limits.
The shared full-canvas layer buffer is allocated on first use by interactive or uncached
draws, including adjustment fallbacks. Cached settled draws use their placed layer buffers.
Integer 1:1 unflipped placements copy clipped rows for all three sampling qualities, respecting
both strides and source-window offsets. Aliased translations retain their established forward
copy order. At Normal/full opacity, uniform alpha blocks are copied or skipped with Vector<byte>;
mixed blocks and scalar fallback preserve float/rounding order. Other modes/opacities keep their
existing path. These changes accelerate placement/composition, not the full effect calculation.
ExpandPixelDirty supplies a conservative presentation band on the serial render owner after
SetSnapshot and before Render. The App passes that expanded band to both Render and FillBgra.
Brush, eraser, clone, blur and warp inputs capture the document, target and settings at stroke
start. RenderInputQueue drains start/path/end and Undo/Redo on the UI owner between serial
workers; callbacks cannot reenter the pump. Placement release also settles on that owner.
BrushPointerPath ports the latched Shift axis and zoom-scaled string smoothing; smoothing applies
only to Brush/Eraser, including masks. Filtering runs when queued input is drained, and release
flushes the remaining string before closing the existing stroke. Endpoint records bind the
document, layer and image/mask target. Pending predecessors remain linked until their starts
resolve, allowing queued Shift-clicks and healing cancellation to preserve the correct anchor;
successful starts release the link. Healing previews share the same axis/continuation geometry.
Changing documents clears pending input and endpoint history. UI publication checks document, frame revision and
compositor identity before uploading, so a superseded worker cannot alter the new texture.
Clone uses TileStroke's continuous coverage and stroke-wide opacity cap, with selection clipping
and source-over blending from frozen local pixels or a frozen canvas composite. Local sampling
carries the document offset through the layer's rotation, scale and flips. Presentation bands
include grid quantization and the resampling footprint, keeping placed stroke edges current.
Render metadata is cloned once and reused during pixel-only strokes, independently of live UI
records. Structural/control edits and pending transforms invalidate that view. A continuing
gradient may present its preceding immutable draft while the next endpoints are queued;
cancellation invalidates the old draft. This keeps motion streaming without accepting a
cancelled preview or caching pixels under concurrently changed metadata.
Effects whose full outputs exceed the cache cap generate only the source-grid window needed
by the requested destination and its filter/effect halo. SourceWindow keeps global sampling
coordinates and full-plane clamps while offsetting memory addresses. One window is retained;
its output counts inside the 2MiB workspace, and apart-mask grids count inside the 64MiB entry.
Window memory dimensions round up to 16 to reuse buffers through small tap-bound fluctuations;
pixel coordinates remain exact. Placed cache hits skip effect generation. Cropped windows
bypass ContentCache and draw directly, including live-mask coverage from hidden sources.
Raster brush/eraser, clone and blur working copies carry weak source identity and the captured
revision. The compositor can merge known edits across up to 64 copy ancestors, falling back
when history expires, an intermediate source changes, or an ancestor is collected. Ordinary
clones and arbitrary replacements do not carry this proof. Weak ancestry does not retain old
pixel surfaces beyond the undo budget. The arrays are still independent full-size copies.
The App retains the compositor only while a raster stroke begins inside RenderInputQueue;
the next worker installs frozen metadata and expands the first dab instead of discarding caches.
Brush/Eraser mask strokes also retain the compositor and prove their working copy through weak
mask ancestry. MaskStrokeGrid gives ordinary mask paint a sparse source-resolution canvas grid;
uniform masks become editable pixels, and only the source plus touched 256-pixel tiles become
assets. Independent masks keep their placement and edge tone as they grow, while image pixels
stay unchanged. Surface/document budgets are checked before density allocation; later segments
inspect only their new tiles. Matching full-canvas masks retain the existing direct paint path.
Mask dimensions/placement changes expand composition to the affected layer/folder/live-mask
footprints even without effects; ordinary pixel edits keep their local band.
Mask painting uses MaskPlacement when present. New/unknown masks, structural
changes and Undo keep conservative invalidation. Live-mask consumers still expand to full closure.
Opening a document still requests the full canvas. Each 4K raster stroke still copies 64MiB;
clone all-layer sampling still has full-image costs. Blur strokes hold the immutable source
and a separate working copy. GaussianBlurSample computes only the dab's source-grid rectangle
and a small reuse margin, with two byte-rounded Gaussian passes. Image Blur uses transparent
extension, placement-scaled sigma and TileStroke's continuous opacity/selection coverage, blending
the frozen sample source-over the original. The clamp mode remains available for kernel controls.
Its output, horizontal scratch, kernel and metadata stay within a 2MiB retained workspace.
Each kernel tap computes its clamped address once for all RGBA channels, preserving the original
float accumulation order and byte rounding for each channel. See the
sampling comparison (local development reference) for isolated and complete-frame measurements.
Oversized dabs use temporary buffers and release them after sampling; they can still be costly.
The kernel's existing clamp-mode bytes remain pinned. GaussianMaskBlurSample samples frozen grayscale mask
windows with the mask's edge tone outside the source and a 2MiB retained workspace. Mask blur scales
sigma by placement, uses TileStroke opacity/selection coverage and drops unchanged Undo steps.
Painting beyond the existing image grid and Mac Core Image pixel equivalence remain separate parity work.

The App ports CanvasViewport.swift into Renderer: zoom is physical pixels per document pixel,
logical scale is zoom divided by backing scale, and pan is unrestricted. Anchored zoom preserves
the document point under the pointer; Fit and DPI/window changes keep the original viewport semantics.
CanvasControl draws at viewport size with that transform; only the document overlay receives
the XAML matrix. A repeating 20×20 checker tile replaces a document-sized checker target, and
the high-zoom grid visits only visible pixel boundaries. Navigation coalesces at display frames
and reuses the composited texture without starting a CPU render or changing document history.
The render worker captures the window's dispatcher before starting, so completion does not
read a canvas control's dispatcher after that control is unloaded.

Rulers use document pixel coordinates and an 18-point strip. Guide movement is a view draft:
release performs one add/move/delete; cancellation preserves document metadata. Eyedropper reads
one premultiplied BGRA pixel from the current composited texture, ignores alpha zero and unpremultiplies
to an eight-bit sRGB palette color. It waits while a document frame is pending.

The App asks CanvasCompositor for a Surface, converts the requested dirty rectangle (the band)
to tightly packed premultiplied BGRA in one reusable staging buffer, and uploads just that region
of a persistent CanvasRenderTarget texture through SetPixelBytes on the UI thread. Workers
hold local staging buffers and never publish shared texture/buffer fields. The draw pass lays the dark
10px checkerboard beneath the bitmap on the GPU, so transparency reads without baking anything
into the pixels; export renders with no display background, so PNG retains transparency and JPEG
composites over white.

Brush and placement inputs coalesce onto the render worker's frame loop at full resolution.
TransformEdit captures a fixed metadata/asset basis outside live document/history; each preview
has independent metadata and shared unmodified pixels. Numeric edits retain fractions; pointer
drafts round pixels/degrees. Single-layer, independent-mask and visible group drafts use the
same serial owner as painting, exact sampling and SetSnapshot cache retention. Commit validates
the captured source and creates one history entry; cancel restores the document. Dirty bounds
include the initial, new and last rendered draft footprints and effect/consumer halos, including
when a smaller draft commits before its own frame. Saving/export waits for the queued commit.
Presentation memory is one band-sized staging buffer plus one document-sized GPU texture,
not a second CPU mirror of the canvas. Original transform interaction parity remains partial.
FloatingTransformSession owns the selected-pixel edit outside the live document and history.
Its temporary display snapshot supplies rows/handles; the serial render worker prepares, previews
and merges pixels. Affine outline updates reuse geometry through MatrixTransform. Publication
validates owner/revision; tool/layer/output continuations wait for the session's completion before
mutating the owner. Document replacement/close completes a retirement signal even when pending
input actions are discarded. Expected floating cancellation only retires the worker frame.
FloatingDuplicateGesture retains the original grab plus the latest pointer/modifiers while the
preceding merge is running. Resume creates a normal duplicate draft from the merged owner;
release/output commits the copy, cancellation preserves the prior merge, and retiring the owner
drops both pending operations. Released capture loss cannot cancel this continuation. Output also
waits for a copy that starts in the queued drain, before the gesture existed at the initial call.
Corner distortion and full floating menu/target transitions remain partial; see the
App report (local development reference).
Upright resize snapping adjusts the pointer before draft rounding, with source proportional,
center and flipped-edge rules. Targets are full rotated layer bounds and existing guides,
independent of render clipping; moving uses the source nearest-offset rule. Current handles are
hit before auto-selection, so a foreground layer cannot steal a selected handle. Full duplicate
drag transactions, snap preferences and selected-pixel transforms remain separate parity work.

Layout-grid dotted and dashed lines use the Mac's screen-point patterns [1, 2] and [4, 3],
with flat caps and a one-physical-pixel stroke. Each window retains dotted/dashed styles for
both orientations. Backing scale converts screen-point patterns into stroke-width units;
the visible document rectangle clips path endpoints, and DashOffset restores the document-zero
phase after clipping. The grid regression report (local development reference) covers both directions,
DPI, zoom and pan. These are view preferences and do not change the saved project format.

## External editing

ProjectWatcher observes manifest.json and images/*.png, debounces events for 350 ms, then compares
a SHA-256 digest over manifest bytes and each asset name and file size. The app reloads only when
the digest differs. It refuses to silently replace a dirty in-memory document.

The digest deliberately does not hash full image contents. To make a same-size PNG replacement
visible, an external writer must also change manifest bytes (for example, by changing a layer
name). External writers should write assets first and atomically rename a complete temporary
manifest over manifest.json.

## MCP process

Compositor.Mcp is a standalone console process. A client starts it and exchanges newline-delimited
JSON-RPC messages over stdin/stdout. The process keeps one open project path. Tool mutations
reload the project from disk, modify the snapshot, validate and save through ProjectStore; the
normal file watcher can then update a clean open UI document. Because its digest uses file sizes
instead of asset contents, a same-size replacement with unchanged manifest bytes is not detected.

MCP does not host an AI model, send prompts to a provider, or make image-generation requests. It is
an automation adapter around a small set of project operations.
