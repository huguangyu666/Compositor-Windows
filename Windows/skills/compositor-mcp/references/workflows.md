# Editing workflows

Use these examples with the current schemas and fresh editor state. Symbolic tokens and IDs below
must be replaced with actual returned values; they are not executable project identifiers.

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
