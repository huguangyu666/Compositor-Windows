---
name: compositor-mcp
description: Use Compositor for Windows through MCP to edit the live canvas, grade photos, arrange text and layers, refine masks, and bring images generated with an agent's own tools into the editor. Use for image-editing tasks in Compositor, not for developing its source code.
---

# Compositor MCP

Perform image edits through the editor's MCP tools. Keep the result editable and inspect the
rendered canvas before reporting success. This skill requires a configured Compositor MCP server;
it does not install or start the server, supply an image model, or configure credentials.

## Connect and identify the document

1. Inspect the connected server's tool schemas. Names below are unprefixed; the client may expose
   them as `mcp__compositor__editor_state` or under another configured server name.
2. Call `editor_list`. If several editors are open, match the intended window and pass its
   `appPid` consistently. If none is running, have the user open Compositor and a document.
3. Call `editor_state`, then `editor_preview` with `view=canvas`. Check the project path, canvas
   size, layers, active selection, effective `locks`, `busy`, and `ai.status` before editing.
   Use layer IDs from this state, never names alone or IDs from a previous document.
4. Use the `editor_*` tools for the visible document, including unsaved work and Undo. They do
   not require `open_project`. The separate saved-package family starts with `open_project` and
   changes disk files; use it only for an explicitly offline task. Do not mix both families on
   the same project or use disk edits to bypass a busy editor.

If the tools are unavailable, report the missing connection. Do not silently substitute mouse
automation, direct manifest writes, or app-source modifications. The live surface currently has
no new-document tool; open an existing `.comp` or have the user create a blank document.

## Make edits against fresh state

- Every live write requires `documentToken` and integer `revision` from the latest `editor_state`,
  including selection, save, export, history and AI actions. Read state again after each write.
  Send dependent writes sequentially. A user edit or tab switch can invalidate earlier state.
- On a stale-state rejection, read state, recheck the target and adapt the operation. After a
  timeout, inspect state and preview before repeating a write; it may already have succeeded.
  A disconnected client may cache outdated schemas; reconnect instead of guessing new fields.
- Respect effective layer locks. Finish or resolve an existing draft/AI preview before unrelated
  edits; do not discard the user's preview just to unblock a command.
- Layer arrays are bottom to top among siblings. Transforms use document pixels, unrotated x/y
  bounds and clockwise degrees about the center. Linked masks move with the layer.
- For one coherent local change, `editor_batch` supports 1–32 commands as one atomic Undo.
  Each entry has `tool`, `arguments`, and optional `as`. The outer call carries PID/token/revision;
  inner arguments contain only operation parameters. Later layer-ID fields can use `$alias`.
  Resolve returned `batchAliases` to real IDs for later standalone calls. Imports, cloud jobs,
  history, save/open/export, mask-file reads and nested batches are excluded.
- Live edits do not save automatically. `editor_history` performs one Undo/Redo; after Undo,
  creating a new edit can replace the redo branch. Do not run history tests on unrelated user work.

## Choose the native operation

| Goal | Tools and important details |
| --- | --- |
| Inspect composition or edges | `editor_preview`: canvas, layer, mask, original or result; layer/mask require `layerId`. Start at `maxSide=1024`; use a `region` for details. Mask regions use mask-grid pixels, other regions use document pixels. |
| Import and position an image | `editor_layer_import` accepts an absolute local PNG and centers it in the current folder; inspect state before `editor_layer_transform`. Preserve aspect ratio unless distortion is requested. |
| Draw and import SVG | `editor_layer_import_svg` accepts either inline `svg` or a local UTF-8 `.svg` `path`, with output `width`/`height` and optional `x`/`y`/`name`. Requires viewBox. Supports paths/shapes/gradients/clips; no text/filters/images/stylesheets. Default `trim=true` removes transparent margins in place. Creates a raster layer in one Undo, without a cloud request; vector nodes/source are not retained. Use `editor_text` for typography. |
| Change layer structure | `editor_layer_select`, rename, visibility, opacity, blend, reorder, duplicate or delete. `editor_layout` groups, ungroups, aligns, distributes or crops. Ungroup discards the folder's own mask/blend/opacity. |
| Add editable typography | `editor_text`; omit `layerId` to create, supply it to update. Text colors are sRGB triplets in 0–1. Use `editor_layer_transform` to move existing text. |
| Grade a photo | `editor_adjustment`; create with an advertised `kind`, update with `layerId`. `settings` patches exact camelCase fields from state and retains omitted fields. |
| Select an area | `editor_select`: local `object` point/box selection, rectangle, ellipse, polygon, opaque grayscale mask, all, clear or feather. `combine` is replace/add/subtract. Object mode needs an installed optional model; supply all foreground/background prompts on each correction. Imported selection masks match canvas dimensions. |
| Refine coverage | `editor_mask` preserves image pixels. Paint uses document coordinates and values 0–255; filters use mask-grid pixels. The current selection constrains edits by default. Enable a disabled mask explicitly before painting. |
| Isolate colors | `editor_mask_color` takes flat 0–255 RGB triplets, creates a mask and refuses an existing mask. It reveals matching colors; this is not semantic subject segmentation. |
| Remove transparent padding | `editor_trim_layer` preserves placement and scale. It supports raster layers with compatible masks, and refuses text/shapes, effects and independent masks. |
| Animate layers | `editor_timeline`: settings, clip, keyframe, seek and preview. Positions are layer centers; scale 1 means the base size. Frame indices are integers, outFrame is exclusive. Seek and review with `editor_preview`; save keeps editable keys. Disable preview before ordinary layer/pixel edits. |

Read [references/workflows.md](references/workflows.md) for photo grading, editable layout and
mask examples. Derive the settings and coordinates from the actual image and current tool schemas.

## Images from the agent's own tools

When an image tool is available to the agent, it can generate artwork without the editor's API
key. Use `editor_layer_import` for new PNG assets; optional x/y/width/height place the full input
rectangle, and transparent margins are trimmed afterward by default without moving the artwork.
For selection edits or expansion, use `editor_ai_prepare` to capture input/mask paths, generate
externally, then `editor_ai_import` to preview the PNG with the same request ID. Inspect and accept
with the existing AI tools. See [the external generation workflow](references/workflows.md#external-agent-image-generation)
for framing, mask convention and cancellation. Do not start `editor_ai_start` as well.

Use the agent's actual available capabilities; do not claim every account has image generation
or that it is free. Local preparation does not upload, but passing images to the agent's tool may.
Use the user's existing service/upload authorization. If no image tool is available, choose a
configured editor connection when authorized, or report the missing capability.

## Editor-connected image generation

Use local adjustments for ordinary photo grading. Start a cloud image edit when the user's task
authorizes it; retain any existing authorization and budget. The editor sends the **full composite
canvas**, selection mask, prompt and reference images to the configured provider, even for a small
selection. Explain that scope if it has not been disclosed. Local MCP edits need no image API key;
preview images can still be seen by the reasoning client's service.

1. Establish the intended selection with `editor_select`; refresh state afterward.
2. Call `editor_ai_start` with remove/replace/expand and an exact model ID from the current schema.
   Use the saved API connection by default; account mode must be explicit. Omit resolution to use
   automatic resolution. Credentials and API addresses are configured in the app, never in tools,
   prompts, projects or logs. Reference roles and `preserve` guide generation, not identity locks.
3. Retain the returned `requestId` and poll `editor_state` at reasonable intervals. Do not start
   another request because the first one is slow, and do not automatically retry a failed paid job.
4. When ready, inspect `editor_preview` result and original. Check the intended edit, preserved
   regions, edge coverage and any requested transparency. Accept the reviewed candidate with
   `editor_ai_accept` and the exact request ID, or discard it with `editor_ai_cancel`, `discard=true`.
   Cancellation stops local waiting and cannot guarantee cancellation of provider billing.
5. Refresh state and inspect the accepted canvas. Accepted results become undoable layers and
   automatically trim fully transparent margins. Faint nonzero background is not transparency;
   refine the mask when necessary rather than calling trim a background-removal operation.

## Preview and deliver

Compare the final preview with the initial image. For grading, use local detail previews as needed
to check highlights, shadows and skin tones. For layouts, check clipping, hierarchy and margins.
Avoid fixed grading presets for unrelated photographs or flattening editable text and adjustments.

Save with `editor_save`; a first save needs an absolute new `.comp` directory path, and an existing
project normally needs no path. `editor_open` requires saving a dirty current document first.
Export with `editor_export` to a new absolute PNG path in an existing directory; it refuses an
existing destination. Export uses committed content, so accept an AI preview first. Exporting a
PNG does not save the editable project. Do not promise PSD export or a full app-window screenshot
from these tools; `editor_preview` returns image content.

Report the edits actually made, whether the project was saved, and the project/export paths.
Show the preview or before/after images when available. State any unresolved tool failure or
visual limitation instead of treating a successful call as proof of a satisfactory image.
