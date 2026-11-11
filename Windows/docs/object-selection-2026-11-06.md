# Local object selection — November 6, 2026

Status: **implemented for point/box object selection and corrections**. This stage does not
implement one-click subject discovery, hair matting or Apple Vision parity.
The current default is **SAM 2.1 Hiera Tiny**; installed MobileSAM files remain supported.

## Use in the editor

1. Choose **Object Selection (Q)** on the tool rail. An already installed model starts preparing
   the current image in the background; this does not select anything or change history.
2. Click inside the intended object, or drag a box around it. Sampling uses the visible composite
   at the base editing state, including layer placement and adjustments.
3. Add include points with ordinary clicks. **Alt-click** adds exclude points. A new box replaces
   the current box while retaining points; **Start over** discards all prompts. Up to 64 points.
4. Inspect the dashed selection contour around the object. The photo is not tinted and old
   prompt dots do not remain over it. A black-and-white hollow circle follows the pointer over
   the image, with its center marking the click location. Dragging shows a selection box.
   New/Add/Subtract combines against the selection that existed at draft start;
   The boundary is fitted to original-image color edges. **Soft edges** estimates fractional
   coverage from neighboring foreground/background colors. **Feather (px)** optionally adds
   0–250 pixels of blur (default 0); changing it reuses the prepared model mask. Turning Soft
   edges off uses binary coverage at the fitted boundary. The dashed contour stays sharp.
5. The usable model contour appears before original-image edge refinement. After 120 ms without
   new input, the app refines the boundary in the background. **Confirm / Enter**, or switching
   to another tool, commits the latest ready contour as one selection history step. A tool switch
   while inference is pending waits for the latest result. **Cancel / Escape** or Undo discards
   the draft without changing the document selection or redo. Conflicting content
   commands and tab switches are refused while a draft is active. An explicitly empty result
   protects all image pixels; Deselect is a separate command that removes the constraint.

Drafts and confirmed selections use the same existing selection outline renderer. The previous
completed contour remains visible while a correction is prepared; confirming reuses that contour,
and cancelling restores the original selection's contour. Source image pixels remain unchanged.
Tiny unprompted islands and enclosed pinholes are removed from the actual selection coverage,
before New/Add/Subtract is applied. The area limit is 16 decoder cells, scaled to document pixels.
Eight-connected openings reaching the canvas edge, substantial holes and explicit include/exclude
points are protected. If all foreground islands are tiny, the largest is retained. Other soft
coverage is retained at the final boundary as described below. This follows SAM's
[`remove_small_regions`](https://github.com/facebookresearch/segment-anything/blob/main/segment_anything/utils/amg.py)
policy, adapted for partial coverage and user prompts without an OpenCV dependency.
When an include point lands in the selected foreground, disconnected foreground components without
an include point are removed as well. Add another include point to retain another separate object;
box-only requests continue to retain substantial components.
The existing layer-mask commands can turn a
confirmed selection into an editable mask. Selections themselves are session state, not saved
in `.comp`; save a layer mask when the coverage needs to persist.

## Optional model installation

First use offers **Download model**, **Import model folder**, or Cancel. The **Local model…**
button reopens these options. The app needs no Python, helper executable, API key or cloud image
service. Images are processed on this computer. The download only contacts the model host.

SAM 2.1 files are stored in `%LOCALAPPDATA%\Compositor\Models\SAM2.1-Tiny` (155,610,424 bytes total).
Download or import is streamed
to temporary files, checked for exact length and SHA-256, then atomically published. Invalid
files are refused; Download model can repair a corrupt installation. Cancelling removes incomplete
temporary files. Complete verified files may be retained for the next installation attempt.

Pinned export revision: `onnx-community/sam2.1-hiera-tiny-ONNX@814a066640debee5a91e70aa401fb8e17e030503`.

| File | Bytes | SHA-256 |
| --- | ---: | --- |
| `vision_encoder.onnx` | 354,238 | `4f30aacd3aaefbca81a0b7fe4c1fc96345570ea0a6f80ced599493d1b3be2e8c` |
| `vision_encoder.onnx_data` | 134,084,864 | `e83df9866a5afe68ea7f0f721f18f65137fc3acbf0da1c74e946d363e09c69cc` |
| `prompt_encoder_mask_decoder.onnx` | 213,114 | `874414704c5d686db7d206a35f6e15d26563d50c8c4468fccc6739bd7e491dcf` |
| `prompt_encoder_mask_decoder.onnx_data` | 20,958,208 | `e9874d900dd4134ed60eab1e97910327c2419e0b2954485d8fd6e7f1a1470f47` |

For an offline machine, download these four exact files from the pinned
[model repository](https://huggingface.co/onnx-community/sam2.1-hiera-tiny-ONNX/tree/814a066640debee5a91e70aa401fb8e17e030503/onnx)
on another computer, place them in one folder and import that folder. The data files must sit
beside their matching ONNX graphs. Arbitrary ONNX models are not accepted by this adapter.

If only MobileSAM is installed, it continues working. **Local model… → Download model** installs
SAM 2.1 and switches only after verification succeeds. Cancellation preserves the previous model.
Legacy offline folders are also accepted. Legacy files remain under
`%LOCALAPPDATA%\Compositor\Models\MobileSAM`, pinned to
`Acly/MobileSAM@0d3b403339b4674a82493d5e97964dd78089ddc8`:

| File | Bytes | SHA-256 |
| --- | ---: | --- |
| `mobile_sam_image_encoder.onnx` | 28,157,093 | `580f5fb648ea1062c0aabc26217aed56921985f03f0cbbd852bba81d760cc749` |
| `sam_mask_decoder_multi.onnx` | 16,496,559 | `8976b90a87ba50a6a72217a5ff994f7d25ce16f2229fcc1ed259e1294c622ffe` |

## Inference and performance

The adapter uses ONNX Runtime DirectML 1.24.4, retaining Windows 10/11 compatibility. It attempts
DirectML for the image encoder on a compatible GPU and retries CPU after provider initialization or runtime failure.
The prompt decoder runs on CPU to avoid GPU transfers of the cached host features.
CPU execution uses at most four worker threads. Inference runs off the UI thread. A newer prompt
cancels the previous request; cancelled, expired or retired-document work cannot publish later.

The encoder cache holds one sampled image, identified by Surface reference and Revision.
Choosing Object Selection prepares installed model sessions and encodes the image before the
first prompt when possible. This moves the cold preparation earlier; it does not remove its cost.
Continuous UI corrections and successive MCP selections reuse this image until document content
changes. Only the prompt decoder reruns for changed points/box. The image cache is bounded to
one composite; the optional model sessions remain loaded until the editor closes.

SAM 2.1 uses a 1024×1024 stretched input and restores both axes independently. MobileSAM retains
aspect ratio and pads to the same square. The feature cache holds all three SAM 2.1 scales
(about 16 MiB) or the one MobileSAM scale.

Initial **MobileSAM** standalone measurements on the official SAM 1800×1200 truck sample:

| Backend | First request, including session load | Following box request | Encoder runs |
| --- | ---: | ---: | ---: |
| CPU | 1,165 ms | 88 ms | 1 |
| DirectML | 5,948 ms | 98 ms | 1 |

These measure the standalone adapter on this machine, not full UI latency or a claim that GPU
is faster. The DirectML first call includes model initialization/compilation. The sample selects
the truck but can retain ground specks; use exclude points and inspect coverage. Soft coverage
is not a dedicated hair/transparency matting model.

Local evidence: `Windows/artifacts/selection-ai-20261106/{cpu,gpu}/report.json`.
Reproduce with the .NET probe, with no Python:

```powershell
dotnet run --project Windows/tools/Compositor.SegmentationProbe -c Release -- `
  "$env:LOCALAPPDATA\Compositor\Models\SAM2.1-Tiny" "C:\samples\truck.jpg" "C:\results\selection" cpu
```

## MCP

Read `editor_state.objectSelection.installed`. If false, ask the user to install the model in
the editor. MCP never downloads models or opens an installation dialog. Inspect the canvas and
use document pixel coordinates; this mode does not accept a natural-language subject name.

```json
{
  "mode": "object",
  "foregroundPoints": [[900, 600]],
  "backgroundPoints": [[1710, 1140]],
  "box": {"x": 270, "y": 240, "width": 1260, "height": 780},
  "softEdges": true,
  "combine": "replace"
}
```

Pass current `appPid`, `documentToken` and `revision` as for other mutations. Each call supplies
the entire point list and optional box. A box alone is allowed; exclude points alone are not.
`feather=0..250` remains available; its default is 0. `softEdges=true` (the default) estimates
local RGB alpha at the fitted boundary; false returns binary coverage. An explicit feather is
added afterward. Each call is one Undo. Object inference is excluded from
`editor_batch`. The editor checks identity/revision after worker preparation and before publication;
on rejection, read state and review the current canvas. MCP waits for the complete refined
result; the two-stage fast preview is for direct UI interaction. No paid request is involved.

## Verification

Initial delivery checks passed with uncommitted source changes above `f303b903`:

- App and MCP Debug/Release builds: zero warnings and errors.
- Solution tests: **2458 passed** (Core 2207, App Infrastructure 193, Renderer 40, Segmentation 18).
- Final DEBUG real-model application probe: **6 groups passed**, PID 49936, normal XAML outlines.
- Independent **Release stdio MCP**: 36 tools discovered; point/exclude selection, box-only
  selection, unchanged PNG preview bytes and Undo/Redo passed, PID 43580, DirectML backend.
- The empty-subtraction regression was observed failing before repair, then passed. Both the
  failure record and final successful evidence are retained; no threshold was relaxed.

Evidence is under `Windows/artifacts/selection-ai-20261106`: `solution-tests.log`, build logs,
`stdio/report.json` and `delivery.json` (Release assembly hashes). The final App process/report
record is `Windows/artifacts/probe-evidence/object-selection-20261106-delivery-*`. These are
working-tree checks (`productTreeDirty=true`), not proof of a committed clean checkout or a
clean-machine release acceptance.

The same-day UI correction removes persistent prompt dots and cyan coverage tint. A real-image
regression first failed on the previous display, then all **7 application groups passed** on the
normal XAML outline path (PID 28376). It checks that the draft's real contour is displayed,
corrections leave no point/tint overlay, Cancel restores the original selection, and Confirm
retains the same contour geometry. Evidence:
`Windows/artifacts/probe-evidence/object-selection-outline-20261106-{red,green}-*`.

### November 7: speckled contours in the model mask

The earlier display correction did not address disconnected regions in the model output itself.
The supplied 831×1080 example reproduced this on both DirectML and CPU. Before cleanup, its
center-point mask contained 49 enclosed pinholes and 20 foreground islands of at most 285 pixels.
After cleanup those small regions were gone; two substantial foreground components remained.
The box mask's 147 small foreground islands were removed, while its three substantial holes
remained. CPU and DirectML produced identical thresholded contours on both prompts (soft coverage
still has small provider rounding differences).

- The new regression failed on both hard and soft edges before the implementation.
- Segmentation tests: **23 passed**, including prompt protection, real gaps, diagonal connectivity,
  canvas openings, soft coverage and the largest-small-object fallback.
- Actual-image DEBUG App probe: **7 groups passed**, PID 16368, normal XAML contours, including
  Confirm/Cancel, one Undo/Redo and unchanged source image bytes.
- Debug and Release builds: **zero warnings and errors**.

Evidence: `Windows/artifacts/object-selection-noise-20261107` and
`Windows/artifacts/probe-evidence/object-selection-noise-20261107-*`. These are local working-tree
checks above `f303b903`, not a new clean-machine release acceptance.

### November 7: corrective prompts and visible black/white contours (superseded below)

On the same 831×1080 example, selecting the highest score across all four SAM mask tokens
could retain the illustration's arm despite an explicit exclude point. Single-click requests now
choose among the three alternative extents; box and multi-click requests use SAM's dedicated
single-mask token. The real-image regression checks include/exclude coordinates and removes
unprompted disconnected background components.

This intermediate version used a 2.5-point black stroke under a 1.25-point dashed white stroke, with widths
adjusted for canvas zoom. The first style-only check missed a WinUI ownership constraint: a
Geometry cannot belong to both Paths. Its ElementName binding silently left the underlay empty;
explicitly assigning the same instance to both Paths reproduced an ownership exception.

Each bounded geometry batch in that version prepared separate black and white instances from the same runs.
The pair is retained for translation, zoom, affine transforms and draft confirmation. Perspective
preparation produces a corresponding pair, and incremental retirement drains both instances.
This retains a second set of native geometry objects while avoiding per-frame reconstruction.

The application probe compares actual canvas pixels with each stroke hidden independently.
On the supplied image, the black stroke changes **5,719** rendered pixels and the white stroke
changes **6,509** pixels. All **11** application groups passed in PID **55060**, including
corrective prompts, unchanged source pixels, Confirm/Cancel, one-step Undo/Redo, paired-stroke
reuse, and retirement after replacement, partial cancellation and document changes.
Segmentation unit tests: **24 passed**.

Evidence: `Windows/artifacts/probe-evidence/object-selection-quality-20261107-contrast-final-*`
and `Windows/artifacts/object-selection-quality-20261107/contrast-final/preferences/corrected-canvas.png`.
The earlier `underlay-check` and `contrast-green` failures remain recorded. These are working-tree
checks above `f303b903`.

### November 7: SAM 2.1 and a single thin black/white contour

The intermediate model still omitted the foreground person's wrist and left false face holes
on the supplied 831×1080 image. A new subject check first failed on the old model. With the exact
same three include and two exclude prompts, SAM 2.1 retains the wrist and removes the face holes;
no extra prompt was added for the wrist. The C# CPU and DirectML paths both produced 461,061
foreground pixels at the 128 threshold, with no enclosed holes on this fixture. Hair near the
upper-right edge remains rough; this is still point/box segmentation, not hair matting.

The old white dash pattern restarted at each short PathFigure, so curves looked like a thick
solid double outline. The default XAML contour now uses one 1.25-DIP stroke with a repeating,
hard black/white gradient in document space. Thickness and pattern size follow canvas zoom.
The paired geometries and their extra retirement work have been removed; the actual selection
coverage, retained affine geometry, and bounded retirement mechanism remain in use. The opt-in
experimental direct-outline backend retains its previous style.

The rendered-pixel check failed on the old white-only stroke (0 dark pixels) and passed on the
new single stroke (1,798 dark and 1,657 light pixels). The DEBUG app check passed **12 groups**,
PID **2948**, including the wrist, actual canvas capture, original image bytes, cancellation,
Undo/Redo, encoder reuse and replaced/partially built/retired-document contour cleanup.
Segmentation tests: **26 passed**. These checks use uncommitted working-tree source above
`f303b903`; they do not establish clean-machine compatibility.
Debug and Release App builds both completed with zero warnings and errors. A standalone run
also verified the shared adapter still loads the legacy MobileSAM model and reuses its encoder.

Standalone SAM 2.1 measurements on the same image:

| Backend | First request, including session load | Following box request | Encoder runs |
| --- | ---: | ---: | ---: |
| CPU | 1,714 ms | 65 ms | 1 |
| DirectML | 2,721 ms | 107 ms | 1 |

Evidence: `Windows/artifacts/object-selection-refinement-20261107` (`wrist-red`, `contour-red`,
`contour-green`, `sam2-csharp-cpu`, `sam2-csharp-gpu`), and matching records under
`Windows/artifacts/probe-evidence/object-selection-refinement-20261107-*`.
The actual canvas capture is `contour-green/preferences/corrected-canvas.png`.

### November 7: soft edges are boundary coverage, not confidence (base mask)

The live MCP check exposed a separate coverage error: applying sigmoid to every model logit
left faintly selected background and partly selected subject interiors. Its nonzero bounds
covered the whole canvas even after the visible 128-threshold contour looked correct.

The unfeathered base mask uses the bilinear logit derivatives to estimate the zero boundary's one-document-pixel
antialiasing footprint. Constant positive/negative regions become 255/0. Cleanup also removes
partial halos around discarded islands and makes filled pinholes fully opaque again. No extra
full-resolution RGB image, model inference, native dependency or image upload is needed.
The implicit-field antialiasing reference is Loop/Blinn's
[GPU Gems 3 chapter 25.5](https://developer.nvidia.com/gpugems/gpugems3/part-iv-image-effects/chapter-25-rendering-vector-art-gpu);
the adapter evaluates its own bilinear mask field on the CPU.

On the supplied image, the include sample `(530,600)` is now 255 and the exclude sample
`(600,80)` is 0. Nonzero coverage bounds are `(x=0,y=178,width=801,height=902)`;
the subject reaches the left and bottom canvas edges. This does not fix the remaining
upper-right hair segmentation errors.

The new flat-interior regression first observed 225 instead of 255, and the filled-pinhole
regression first observed a remaining 170-alpha halo. Both then passed. Segmentation tests:
**29 passed**. The app probe now also checks full interior opacity, clear background and nonzero
bounds around the subject. Evidence and actual canvas capture are under
`Windows/artifacts/object-selection-refinement-20261107/delivery-final` (13 application groups,
PID 30136), with matching `probe-evidence` records. Debug and Release builds passed with zero
warnings/errors after this final coverage cleanup. The earlier checks above remain historical
evidence for their respective intermediate versions.

### November 7: adjustable feather for softer actual edges (superseded below)

The one-pixel antialiasing above left an abrupt cutout edge. Object selection now adds a default
3-pixel feather with the existing Core selection Gaussian approximation. The toolbar exposes
0–250 pixels and keeps the last amount when Soft edges is disabled. Adjusting only this amount
reuses the model mask; preparation warms the feather coverage cache on a worker before publication.
The original contour remains sharp and unchanged. Painting and selection-to-mask operations use
the softened coverage; the photo's source pixels remain unchanged. Feather does not repair the
remaining upper-right hair segmentation errors or add hair matting.

The new default-feather check first failed before the product change. The final real-image Debug
probe passed **15 application groups**, PID **30652**, using DirectML. On its center-point draft,
partial-coverage pixels increased from **3,309** to **19,758**. Checks cover changing 6→0→3 pixels
without replacing the cached mask or modifying history, MCP default 3 and explicit 0, fixed
corrective prompts, unchanged source pixels, cancellation and exact Undo/Redo. Evidence:
`Windows/artifacts/object-selection-softness-20261107/green-final` and
`Windows/artifacts/probe-evidence/object-selection-softness-20261107-green-final-30652*`.
The earlier `green` probe stopped on a test-helper JSON-node ownership error after its product
checks; `green-final` clones the helper arguments and completes the remaining checks.
`preferences/corrected-mask.png` now bakes actual `CoverageAt()` values including feather;
`unfeathered-mask.png` preserves the same draft's base antialiasing for comparison.
Debug and Release builds passed with zero warnings/errors. The Release editor (PID 50980) used
MCP to import each mask and export the actual layer on transparency for visual inspection:
`delivery-live/hard-cutout.png` and `delivery-live/soft-cutout.png`. The saved `soft-example.comp`
keeps the source image and an editable soft mask with its background layer hidden.

### November 7: fit original image edges instead of adding blanket feather

The preceding feather-only change did not meet the visual quality requirement. Default additional
feather returns to 0; the amount remains adjustable. A managed narrow-band graph cut now uses
original-resolution image colors to refine the semantic mask's uncertain boundary. The final band
radius is `ceil(max(width,height)/128)`, clamped to 4–24 pixels (9 pixels on the supplied image).
The earlier 6-pixel band left connected background fragments by the hair; the wider band removes
the prominent fragments while preserving the same five prompts and the wrist. Five sampled
color mixtures and contrast weights follow OpenCV GrabCut. Confirmed interiors and exteriors
outside the band remain unchanged, with explicit include/exclude points preserved. Fractional
alpha is estimated only at the resulting boundary from nearby solid foreground/background colors.
This reduces the supplied image's forearm stair steps and attached background fragments without
blurring the whole contour. It cannot discover large model omissions or perform fine hair matting.

The graph uses at most 300,000 uncertain pixels. If the band exceeds that budget, this stage returns
the coarse mask rather than allocating an unbounded graph. It requires no native OpenCV library or
new model. CPU preparation remains cancellable; the UI keeps its completed contour until replacement.
After cutting, a second component pass removes detached islands of at most the band radius squared,
while preserving explicitly included details, larger components and newly fitted openings. A synthetic
detached-color-island test first failed with coverage 255 instead of 0, then passed after this pass;
the paired include-point case preserves the detail.
The OpenCV image-graph solver was adapted with residual source reachability to keep free ties
consistent. Small synthetic graphs are checked against exhaustive minimum energy. New edge tests
first observed four failures for offset boundaries, mixed-pixel alpha and prompt constraints, then
passed. Aligned-edge tests also exposed a zero-sampled-contrast early return: a regular sampling
grid can miss an edge even on a two-color image. Zero contrast now uses constant neighbor weights
instead of skipping the color fit. Both aligned and offset seven-pixel boundary corrections pass.
Segmentation tests: **39 passed**. GPU image encoding retains its CPU fallback; the small
prompt decoder now runs on CPU. Mask cleanup uses scanline spans instead of a per-pixel neighbor
queue, and coordinate restoration precomputes horizontal sampling coordinates.

Evidence: `Windows/artifacts/object-selection-edge-fit-20261107`. `baseline-cpu/report.json` and
`baseline-gpu/report.json` retain timings from before the new refinement/decoder/cleanup path;
`managed-bounded/refinement-report.json` isolates refinement. The initial generic graph solver
(`managed-profile`) took about 175 ms in its solve stage and was replaced before delivery.
Full-image OpenCV and plain guided-filter experiments remain comparison artifacts, not app code.
These are local working-tree observations on one 831×1080 image; they do not establish Photoshop
CS6 parity, broad image quality, or a clean-machine release acceptance.

Final acceptance uses `app-quality-final` (Debug PID 19136, **15 groups passed**) and the final
9-pixel-band implementation. Debug and Release builds each completed with **0 warnings, 0 errors**.
The unchanged five prompts include the wrist without an extra wrist point; solid interior remains
255 and the checked background remains 0. Adjustable and rapidly changed feather reuse coverage,
and cancellation, stale publication, Undo/Redo and unchanged source pixels passed. Earlier
`app-green` / `app-green-final` runs used the narrower intermediate band.

Final model probes are `accepted-hybrid/report.json` and `accepted-cpu/report.json`. Timings below
include all eight repeated box requests, including variation; they measure segmentation computation,
not end-to-end pointer-to-display latency. Both runs encode the unchanged image once, respect all
five correction prompts and pass cancellation.

| Path | First request including setup | Repeated request range | Repeated median |
| --- | ---: | ---: | ---: |
| DirectML image encoder, CPU decoder | 1968 ms | 94–180 ms | 137 ms |
| CPU encoder and decoder | 1948 ms | 85–166 ms | 93 ms |

The decoder and original-resolution refinement run on CPU in both paths. These single-machine
samples do not demonstrate that the GPU path is faster overall. The wider band is retained for
the visible quality improvement; `quality-band-final/refinement-report.json` isolates its work.

The final Release app is `app-release/Compositor.App.exe`. Live review used PID 13168 and
`delivery-live/edge-example.comp`, with the real application probe's mask imported through MCP.
`delivery-live/edge-cutout.png` is the native layer export: its alpha exactly matches that mask,
the saved source photo pixels match the original, and the wrist/interior/background checks are
255/255/0. The saved project retains an editable mask and hidden background. Release hashes for
App, Core, Renderer **and Segmentation** are in `delivery-live/binary-hashes.json`; the OpenCV
license is present in the build. Work remains uncommitted on the existing dirty working tree.

### November 7: faster direct interaction

The previous UI waited for original-image edge fitting before showing a usable selection. The
UI now publishes the model contour first and runs cancellable fitting after 120 ms without
input. Each new prompt retires the previous work. Confirming or switching tools uses the latest
ready contour immediately; it cancels unfinished fitting so that it cannot replace a selection
already in use. A pending tool switch waits for the latest result. Escape retains the original
selection and redo branch. MCP still waits for full refinement by default.

Choosing Q prepares installed model sessions and the current image in the background. This is
local preparation without downloads or history changes. Model initialization has not disappeared.
The object toolbar also hides the duplicate general feather/invert/deselect controls.

A real Release mouse-drag check exposed a separate problem: the object box gesture was absent
from the navigation guard, so the same drag could translate the canvas. The object press now
blocks navigation until release/cancellation, as other selection gestures do. The added regression
first failed before this guard was changed (PID 12372), then passed.

Local Debug observations on the same 831×1080 image, normal XAML outlines:

| Measurement | Before | Final |
| --- | ---: | ---: |
| First contour, no preparation before the prompt | 4,257 ms | Preparation starts on Q |
| Background preparation, two new editor processes | Not available | 1,677–2,486 ms |
| First contour after preparation | Not measured separately | 150–162 ms |
| Following contour with cached image features | 247 ms | 53–61 ms |

Contour timings include outline readiness and the following application frame, rather than
only model computation. These are individual local observations; the cold row and prepared row
are different scenarios, and no broad hardware or Photoshop speed claim follows from them.

Final application probe: **21 groups passed**, PID **53672**. It covers fast feedback while
refinement is held, cancellation and late-result rejection, Move/Brush switching (including a
pending request), one Undo, unchanged source pixels, the fixed five correction prompts and existing
MCP checks. Model preparation encodes the unchanged image once. Segmentation tests: **39 passed**.
Evidence: `Windows/artifacts/object-selection-interaction-20261107/{red,fast-green,switch-red,prepare-red,drag-red,interaction-green-final}`
and matching `Windows/artifacts/probe-evidence/object-selection-interaction-20261107-*`.

The final Release build (`app-release/Compositor.App.exe`) also passed a real mouse/keyboard
review in PID 59044: dragging the object box retained the canvas position, B switched directly
to Brush with the selection, and one Ctrl+Z restored no selection and the clean document. The
new window is left on Q with the original unmasked image, without an active draft. Existing
editor windows and their drafts were preserved. Debug and Release builds each had zero warnings
and errors. `delivery-live/verification.json` records these observations and four assembly hashes.

Segmentation unit tests cover coordinate/alpha mapping, mask restoration, input validation,
offline import, hash failures and download repair. The DEBUG application probe uses real model
files and a real image to check preview/correction/cache, cancellation with redo preservation,
box confirmation, exact Undo/Redo coverage, unchanged source pixels, MCP use, an empty subtraction,
retired-document refusal, and the missing-model UI/MCP paths.

```powershell
dotnet test Windows/Compositor.Windows.slnx --verbosity minimal
$env:COMPOSITOR_SELECTION_PROBE_IMAGE = 'C:\samples\truck.jpg'
pwsh -NoProfile -File Windows/tools/run-app-probe.ps1 `
  -AppPath Windows/src/Compositor.App/bin/x64/Debug/net9.0-windows10.0.19041.0/win-x64/Compositor.App.exe `
  -Argument --object-selection-probe -ReportFile object-selection-probe.json `
  -LogDirectory Windows/artifacts/object-selection-probe -EvidencePrefix object-selection-local
```

The foreground feel on varied photographs and unfamiliar Windows hardware is separate from the
repeatable probe. One-click subject discovery and hair refinement remain the next stage.

### November 7: hollow circle cursor

Object Selection now uses a cached native black-and-white hollow circle over the document.
The hotspot is at its center. Point/box prompting is unchanged; the pressed-point marker is
removed. Leaving the document, switching tools and temporary navigation restore the appropriate
system cursor. The cursor does not depend on document rendering or add an overlay control.

Debug and Release builds passed with zero warnings/errors. The existing object-selection
application probe passed all 21 groups (PID 47488, normal XAML outlines). Debug mouse checks
showed the circle on light/dark photo areas and its removal when switching to Move. Captures
also showed an additional blue pointer graphic; its source was not established, so these checks
do not establish its removal. The Release app started and opened the unmasked sample in PID
12752. Its entry is `Windows/artifacts/object-selection-interaction-20261107/app-cursor/Compositor.App.exe`.
The loose `App.xbf` and `MainWindow.xbf` were staged from this build's compiler outputs after
the initial `-o` output omitted them. The first probe wrapper invocation also used an invalid
absolute evidence prefix; the successful invocation used `object-cursor-20261107`.
Evidence: `Windows/artifacts/probe-evidence/object-cursor-20261107-*` and
`Windows/artifacts/object-selection-interaction-20261107/circle-review/verification.json`.

## Acknowledgements & Credits

- [SAM 2.1](https://github.com/facebookresearch/sam2), Meta, Apache-2.0: current local image model.
- [ONNX Community exports](https://huggingface.co/onnx-community/sam2.1-hiera-tiny-ONNX): pinned
  SAM 2.1 graphs/data and their multi-scale feature/prompt interface.
- [MobileSAM](https://github.com/ChaoningZhang/MobileSAM), Chaoning Zhang and contributors,
  Apache-2.0: image encoder and upstream model.
- [Segment Anything](https://github.com/facebookresearch/segment-anything), Meta, Apache-2.0:
  prompt labels, decoder interface and coordinate restoration.
- [Acly's ONNX exports](https://huggingface.co/Acly/MobileSAM): pinned export files; the model
  card identifies MIT. Upstream Apache terms and attribution are retained.
- [Microsoft ONNX Runtime](https://github.com/microsoft/onnxruntime), MIT: native DirectML/CPU
  inference. Distribution notices are under `Windows/third-party` and copied into App output.
