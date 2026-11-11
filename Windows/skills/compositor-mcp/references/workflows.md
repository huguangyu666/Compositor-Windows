# Editing workflows

Use these examples with the current schemas and fresh editor state. Symbolic tokens and IDs below
must be replaced with actual returned values; they are not executable project identifiers.

## External-agent image generation

For new artwork, generate with the agent's own available tool, then import its local PNG with
`editor_layer_import`. Supply x/y and both width/height when exact placement matters. These bounds
describe the full source rectangle; default trimming preserves the visible artwork's position
and scale. Use `trim=false` only when the original image grid is needed. Preview the isolated
layer to check transparency; trimming removes zero-alpha padding, not a solid or faint background.

For a contextual edit:

1. Inspect state/canvas and set a selection for remove/replace. Expansion needs no selection.
2. Call `editor_ai_prepare` with operation remove/replace/expand and expansion edges when needed.
   Read `ai.requestId` and `ai.input` from the returned state. No key or editor cloud request occurs.
3. Use `imagePath` and `maskPath` with the agent's own image tool. The mask uses alpha:
   **transparent is editable, opaque is protected**, with partial alpha for soft boundaries.
   Keep the full `width` × `height` canvas and its framing, not only the selection crop.
4. Refresh state; call `editor_ai_import` with the prepared request ID and the generated local PNG.
   Different output dimensions are resized to the full target. Software reapplies captured
   coverage so protected original pixels remain unchanged. Import makes a preview, not an edit.
5. Compare result/original, then accept the reviewed candidate with `editor_ai_accept`, or discard
   with `editor_ai_cancel`, `discard=true`. Acceptance is one Undo; save/export separately.

The editor displays waiting feedback and retains at most three candidates. Input files are
temporary and are removed on accept/discard/source retirement; keep the session open until the
image tool has finished reading them. The generated result file remains agent-owned. A document
or selection change retires the request: do not paste an old result over a fresh selection without
reviewing and preparing again. Editor cancellation cannot stop the agent's separate generation
job. Tool access, charges and external upload permissions belong to that service; no free or
universal Codex image entitlement is implied. The full workflow supports up to 16 megapixels.

## Model-authored icons and diagrams

Write self-contained SVG with a viewBox and explicit geometry, fill and stroke. Call
`editor_layer_import_svg` with inline `svg`, output `width` and `height`, plus viewport `x`/`y`
when placement matters. A local UTF-8 `.svg` `path` is an alternative to inline code. Transparent
margins are cropped in place by default; use `trim=false` when the authored viewport must remain.
Read state and review the imported layer with `editor_preview` before adding more work.

Use paths, basic shapes, linear/radial gradients, clips and local #id references. Convert lettering
to paths or add it separately with `editor_text`; SVG text, filters, stylesheets and embedded or
external images are refused. Input is limited to 128 KiB UTF-8, output to 8192 per side and
16,777,216 pixels within the document budget. The result is a normal raster layer, not editable
vector paths; choose an adequate output resolution. Import is one Undo and does not save.

## Layer animation

Create a timeline with `editor_timeline` `operation=settings`, for example 120 frames at 24 fps.
Choose a drawable layer ID from state. Set a starting key at frame 0, then an ending key at frame
48. `values` uses absolute layer centers (`positionX/positionY`), base-size multipliers
(`scaleX/scaleY`), clockwise degrees (`rotation`) and opacity 0–1. Set `interpolation=smooth`
on the starting key for ease in/out. Keys and clips are ordinary Undo steps.

Use `operation=seek, frame=24` and `editor_preview` to inspect interpolation. Seeking and preview
do not change source pixels or history. `operation=clip` sets inclusive `inFrame` and exclusive
`outFrame`. `editor_save` retains editable animation; `editor_export` exports the current preview
frame. The GUI also exports an entire transparent PNG sequence. Disable preview with
`operation=preview, enabled=false` before ordinary layer/pixel edits. Current macOS builds reject
animated v12 packages; static Windows projects remain v11. There is no video/audio timeline or
MP4/GIF encoder in this feature.

## Photography grading

Infer the goal from the request and image: natural correction, a visibly stylized grade, or a
specific reference. If the direction is unspecified, state a reasonable visual intent and proceed;
ask only when the choice materially changes the result. Preserve the source layer and build
editable adjustments above the intended image stack.

1. Capture a canvas preview before editing. Inspect bright areas, shadow detail, color casts and
   important subjects. A photo-grading task usually needs native adjustments, not image generation.
2. Use advertised Curves, Exposure or Color Balance adjustments as appropriate. For example,
   `editor_adjustment` can create an Exposure adjustment with this argument shape:

   ```json
   {
     "appPid": 12345,
     "documentToken": "TOKEN_FROM_STATE",
     "revision": 7,
     "kind": "Exposure",
     "settings": {"exposureSettings": {"exposure": 0.35, "offset": 0, "gamma": 1}}
   }
   ```

   The numeric setting illustrates syntax, not a recommended value for every photo. After creation,
   read state to capture the new adjustment's ID and complete settings. Patch that layer for later
   refinements instead of accumulating a new adjustment for every attempt.
3. For a local correction, add or refine a raster mask on that adjustment. Check the actual mask
   grid and placement. Black protects the original appearance; white applies the adjustment;
   gray applies it partially. A soft transition usually avoids a visible boundary.
4. Group a coherent look using `editor_layout`. To compare, toggle that look's group visibility
   and request previews after refreshing state. Restore the chosen final visibility before saving.
   Alternate full looks should normally be compared individually, not stacked accidentally.
5. Compare the result at the same framing. If the user requested a clear stylistic change, ensure
   that it is visible; keep an explicitly requested subtle correction subtle. Stop when the stated
   goal is met. Save the editable `.comp` and export the requested PNG.

## Editable poster or layout

Import the artwork separately, then create text with `editor_text`. Position elements using
document dimensions and visual hierarchy. Check the installed font and rendered preview rather
than assuming a font name or text box will fit.

This `editor_batch` command list creates a title, renames it, and groups it as one Undo step.
The outer call still requires fresh `appPid`, `documentToken` and `revision`. Adjust typography
and geometry to the actual canvas. The `$title` and `$typeGroup` aliases exist only in this batch.

```json
[
  {
    "tool": "editor_text",
    "as": "title",
    "arguments": {
      "content": "ORBIT", "fontName": "Segoe UI", "fontSize": 72,
      "color": [0.95, 0.96, 1], "boxWidth": 600, "boxHeight": 140,
      "x": 60, "y": 80
    }
  },
  {"tool": "editor_layer_rename", "arguments": {"layerId": "$title", "name": "Title"}},
  {
    "tool": "editor_layout", "as": "typeGroup",
    "arguments": {"operation": "group", "layerIds": ["$title"], "name": "Typography"}
  }
]
```

Read `batchAliases` from the response, refresh state, then preview. Keep typography and artwork
as editable layers. Use native align/distribute tools for repeated elements when their geometry
matches the desired composition.

## Mask refinement and transparent assets

For semantic object coverage, read `editor_state.objectSelection.installed`, inspect the canvas,
then use `editor_select` with `mode=object`. The optional local model must be installed in the
editor; do not try to download it through MCP. For example, on an 1800×1200 canvas:

```json
{
  "mode": "object", "foregroundPoints": [[900, 600]],
  "backgroundPoints": [[1710, 1140]],
  "box": {"x": 270, "y": 240, "width": 1260, "height": 780},
  "softEdges": true, "combine": "replace"
}
```

Include fresh mutation identity fields. Each correction sends all points again, at most 64 total;
unchanged image features are reused. A box alone is valid, but negative points alone are not.
This creates a selection, not transparency: convert it to a layer mask with the native workflow
when needed. Inspect edges and isolated previews. Do not call soft coverage hair matting or claim
that one example establishes reliable automatic subject discovery. This operation stays outside
`editor_batch` and uses one Undo per call.

- Inspect `view=layer` and `view=mask` with the intended `layerId`. Use a detail `region` for edges;
  mask preview coordinates refer to the mask grid, not necessarily the full canvas.
- An opaque grayscale PNG can initialize a mask through `editor_mask`, `operation=import`.
  It must match the existing mask grid, or the layer image grid for a new mask. Black hides,
  white reveals. Use native paint for local corrections, with document-space points, radius,
  hardness and opacity. Check whether an existing selection should constrain the edit.
- Mask painting uses values 0–255; editable text colors use 0–1. `editor_mask_color.colors` is a
  flat list such as `[230,180,50,240,190,60]`, not an array of nested triplets.
- Color Range reveals matching colors. Choose the subject's colors to reveal them; selecting a
  background color reveals that background until the new mask is inverted.
- Preview before and after feather/expand/contract. These operations use mask-grid pixels;
  scaling the layer changes their apparent document-space size.
- For a requested transparent cutout, inspect the isolated layer preview and export that raster
  `layerId` when appropriate. A checkerboard-looking pattern baked into RGB is not transparency.
  Trim fully transparent padding only after the edge coverage is satisfactory. Trimming does
  not remove low-opacity haze, and it is not a canvas crop.
