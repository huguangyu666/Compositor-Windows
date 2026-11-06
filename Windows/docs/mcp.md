# MCP integration

See the [real MCP-made poster](../samples/README.md) for editable typography, an actual window capture
and consolidated tool parameters. The companion skill also includes local photography-grading workflows.

Compositor.Mcp is a standalone stdio server with two tool families: live editor tools operate on
the running window's document, selection, AI session and undo history; the original package tools
operate on saved .comp folders. The editor provides image generation through its configured API
or account connection. MCP itself does not supply a reasoning model.

## Companion skill

The repository includes [compositor-mcp](../skills/compositor-mcp/SKILL.md), an installable agent
skill for live editing, photo grading, editable layouts, masks and AI preview/accept workflows.
It includes focused examples and works without the app's source tree after installation. Configure
the MCP server separately as described below; installing instructions does not connect a server.

For Codex, copy the complete skill directory into the user skill directory. Run from the repository
root; the check preserves any existing installation for review:

```powershell
$taskSkillRoot = Join-Path $env:USERPROFILE '.agents/skills'
$taskSkillPath = Join-Path $taskSkillRoot 'compositor-mcp'
if (Test-Path -LiteralPath $taskSkillPath) { throw 'Review the existing compositor-mcp skill before replacing it.' }
New-Item -ItemType Directory -Path $taskSkillRoot -Force | Out-Null
Copy-Item -LiteralPath './Windows/skills/compositor-mcp' -Destination $taskSkillRoot -Recurse
```

Invoke it with, for example, “Use $compositor-mcp to grade the current photo with editable
adjustment layers, compare before/after and save the project.” Codex can also select it for matching
requests. If it does not appear, restart the client. See [official skill discovery guidance](https://learn.chatgpt.com/docs/build-skills).
Other clients supporting Agent Skills can install the same folder in their own skill location;
otherwise explicitly provide `SKILL.md` and its linked reference. The optional `agents/openai.yaml`
declares the `compositor` server name used by our installer; adapt it if your connection has a different name.

## Live editor connection

Build the App and the MCP server, then run the normal editor. Connect the stdio executable below
from an MCP client. For this computer, `tools/install-editor-mcp.ps1` adds only the `compositor`
server table to the user's Codex configuration; it does not store keys there. Restart that MCP
connection after installing. The [official client setup](https://learn.chatgpt.com/docs/extend/mcp)
describes shared Codex configuration.

The bridge is a same-Windows-user named pipe (`Compositor.Editor.<PID>`), with no TCP listener,
proxy process, downloaded server or extra NuGet dependency. All commands run through the editor's
existing operations on its dispatcher; rendering and image encoding use captured inputs on workers.

Live `editor_state` layer records also expose `locks` (effective inherited protection) and
`ownLocks` (that layer's own flags). Native editing and live tools respect the Windows editor's
local All/Position/Pixels/Transparency protection, including atomic batch/layout checks.
Locks are changed in the Layers pane and remembered on this computer; they are not serialized
into shared `.comp` packages and saved-package tools do not have this local editor state.

| Tool | Behavior |
| --- | --- |
| `editor_list` | List running editor PIDs; supply `appPid` if more than one is open. |
| `editor_state` | Read the live document token/revision, dimensions, layers, selection, undo/redo and AI status. |
| `editor_timeline` | Create/update `settings`, `seek` a frame, toggle `preview`, add `keyframe`, trim `clip`, `deleteKeyframe`, `moveKeyframe`, `shiftTrack` or `clear`. Positions are absolute layer centers; scales are base-size multipliers. Content edits are one Undo. Seeking/preview do not add history. Current animation frame is used by preview and export. |
| `editor_preview` | Return an MCP PNG image: `view=canvas`, `result`, `original`, `layer`, or `mask`. Layer/mask views require `layerId`. `maxSide=64..2048`, default 1024. Optional `region={x,y,width,height}` crops document pixels, or raw mask-grid pixels for mask view. Mask previews are opaque grayscale. No upload or disk write. |
| `editor_mask` | Refine a raster mask through native document-space `paint` points, mask-pixel `feather/expand/contract`, `invert`, opaque grayscale PNG `import`, `enable/disable` or `remove`. Gray values retain partial coverage. Brush value 0 hides, 255 reveals; radius .5–250, opacity greater than 0 through 1, hardness 0–1. Filters take amount 1–250. Import preserves the current mask grid and placement. The active selection constrains pixel edits by default (`respectSelection=false` ignores it). One Undo; source image pixels stay unchanged. |
| `editor_mask_color` | Add an editable raster mask revealing matching colors. Requires `layerId`, 1–16 flat RGB triplets in `colors`, and optional `fuzziness=0..200` (default 40). Uses the existing binary Color Range algorithm, preserves source pixels, refuses an existing mask, and adds one undo step. |
| `editor_select` | Undoable rectangle/ellipse (`x/y/width/height`), polygon (`points=[[x,y],…]`), or canvas-sized opaque grayscale PNG (`mode=mask`, `path`; black protects, white selects). `combine=replace/add/subtract`, `feather=0..250`; `mode=feather` softens the current selection. `all` and `clear` remain available. Preparation runs on a worker. |
| `editor_batch` | Atomically apply 1–32 local layer, text, adjustment, layout or geometric selection commands as one Undo, without recording intermediate full-document history. Each item has `tool`, `arguments`, and optional unique `as` alias; later layer IDs, including entries of `layerIds`, can use `$alias`. Returns `batchAliases`. Failure or a net-zero batch preserves content and redo. Import, mask-file reads, cloud jobs, history, save/open/export and nested batches are excluded. |
| `editor_layout` | `group`, `ungroup`, `align`, `distribute` or `crop`, one Undo. Non-crop operations take 1–256 distinct `layerIds`; selected descendants of selected folders are carried once. Group accepts `name`; ungroup takes one folder and discards its own opacity/blend/mask, as the native command does. Align uses `alignment=left/center/right/top/middle/bottom`, `relativeTo=layers` (2+ roots, default) or `canvas` (1+). Distribute requires 3+ roots, `axis=horizontal/vertical`, `spacing=gaps` (default) or `centers`, keeping endpoints fixed. Geometry uses rotated placement bounds, includes hidden folder content and excludes effects. Linked masks follow; independent masks stay fixed. Crop takes integer `x/y/width/height` inside the canvas, preserving source image pixels and shifting guides, masks and selection. |
| `editor_text` | Omit `layerId` and supply `content` to create editable text; otherwise edit existing text. Optional fontName/fontSize, sRGB `color=[r,g,b]` in 0–1, alignment Left/Center/Right, tracking/leading and boxWidth/boxHeight. x/y place new text. Existing text preserves placement and omitted style fields. One Undo. |
| `editor_adjustment` | Create with an advertised `kind`, or update an existing `layerId`. `settings` patches the exact camelCase adjustment fields in state, including nested objects. Omitted fields are retained; invalid values publish nothing. One Undo. |
| `editor_ai_start` | Start one `remove`, `replace` or `expand` request. Required `model` is an exact advertised ID. Uses saved `connection=api` by default; `account` must be explicit. Resolution is automatic (`resolution=auto`). Accepts prompt and left/right/top/bottom expansion pixels. Uploads inputs and can incur provider charges. |
| `editor_ai_accept` | Accept matching `requestId` and optional zero-based `candidateIndex` as one layer/undo step. |
| `editor_ai_cancel` | Cancel local waiting for matching `requestId`; `discard=true` also closes the preview. |
| `editor_history` | One `action=undo` or `redo`. |
| `editor_save` | Use the editor's captured, conflict-checked save; a first save needs an absolute new `.comp` path. No folder picker. |
| `editor_open` | Open an absolute `.comp` path. Save the current dirty document first. |
| `editor_export` | Export the committed canvas, or an isolated raster `layerId` on transparency, to a new absolute `.png` in an existing folder. |
| `editor_trim_layer` | Crop a raster layer's transparent margins, including a same-grid attached mask, with unchanged placement and one Undo. Editable text/shapes, effects and independent masks are refused. |
| `editor_layer_select` | Select `layerId`, expand its ancestor rows and target its content. Clears mask/effect targeting without creating content history. |
| `editor_layer_import` | Import an absolute local PNG `path`, with optional `name`, centered on the canvas in the current folder. Selects the new layer; one Undo, no save. Uses the normal import validator and document budget. |
| `editor_layer_transform` | Set any of `x`, `y`, `width`, `height`, `rotation` for `layerId` in one Undo. Omitted fields retain their values. Uses the normal visible-layer/folder transform and linked-mask behavior. |
| `editor_layer_reorder` | Place `layerId` at `position=top/bottom/above`; `above` requires `aboveLayerId`. Omitted `parentId` keeps the current folder; explicit null moves to the root. Selects the moved layer; one Undo. |
| `editor_layer_duplicate` | Copy `layerId`, including folder descendants, editable content and masks; select the copied root. One Undo. |
| `editor_layer_delete` | Delete `layerId` and its descendants in one Undo. Refuses surviving clipping/live-mask dependents; resolve those links in the editor first. |
| `editor_layer_rename` | Rename `layerId` with nonempty `name` of at most 512 characters; one Undo. |
| `editor_layer_visibility` | Set `visible` for `layerId`; one Undo. |
| `editor_layer_opacity` | Set `opacity=0..1` for `layerId`; out-of-range values are refused. One Undo. |
| `editor_layer_blend` | Set an advertised `blendMode` for a non-folder `layerId`, such as `Multiply` or `Soft Light`. Exact case-sensitive manifest names; one Undo. |

`editor_state.timeline` includes duration, frame rate, loop, current frame, preview/playback state
and serialized tracks (or null for a static document). `editor_timeline` uses integer frames;
`inFrame` is inclusive and `outFrame` exclusive. `values` accepts `positionX`, `positionY`,
`scaleX`, `scaleY`, `rotation` and `opacity`; interpolation is `linear`, `smooth` or `hold`.
Folders and adjustments support opacity/clip only. `properties` optionally limits key removal or
movement; moving to an occupied property frame replaces that key. Settings default to 120 frames,
24 fps and loop enabled. Frame-rate changes preserve seconds. Disable animation preview with
`operation=preview, enabled=false` before ordinary pixel/layer edits. Save, history and export work
with preview enabled; seek and playback leave document history and source pixels untouched.

Example, with fresh token/revision on each dependent call:

```json
{"operation":"settings","durationFrames":120,"framesPerSecond":24}
{"operation":"keyframe","layerId":"<id>","frame":0,"values":{"positionX":100,"positionY":200,"opacity":0},"interpolation":"smooth"}
{"operation":"keyframe","layerId":"<id>","frame":48,"values":{"positionX":500,"positionY":200,"opacity":1}}
{"operation":"seek","frame":24}
```

Then use `editor_preview` to inspect the middle frame. Timeline is excluded from `editor_batch`.
No animation operation uploads an image or requests a cloud model.

All write tools require the latest `documentToken` and integer `revision` returned by
`editor_state`. Read again after each write. A stale target is refused before changing anything.
Active document drafts/dialogs are refused; an AI preview must be accepted or discarded before an
unrelated edit. These tools do not expose credentials or alter login settings.

Live layer commands join the serial canvas input queue so a rendering worker finishes reading its
inputs before a mutation. PNG reading/decoding runs on a worker; document identity, revision and
idle state are checked again before publishing it. A timed-out queued layer command is retired
and cannot apply later. None of these layer commands uploads images or starts a cloud job.
History uses the same selection synchronization as the editor's Undo/Redo commands, so the layer
rows and transform controls follow the restored active layer.

The editor displays local MCP operation feedback without extra tool parameters: a virtual cursor
draws the exact creation bounds before publication, transforms show their destination, and batches
use compact feedback. **View > AI Operation Animation** controls the overlay; Windows reduced-motion
settings suppress movement and delay. Feedback adds no document layers or history. Targets are
revalidated after the preview, so an intervening document edit or switch refuses stale publication.

`editor_state` includes `selectedLayerIds`; each layer also reports `kind`, `hasMask` and
`blendModeName`. The existing numeric `blendMode` field is retained. Layer arrays are in manifest
order (bottom to top among siblings). Transform x/y describe the unrotated bounds; rotation is
clockwise around the center. Width/height must be 1–300000 and x/y must be within ±1000000.
For a folder, these values address its visible drawable descendants' group bounds. Editable
text/shapes keep the normal editor transform behavior. Text and adjustment metadata are returned
as camelCase `text` and `adjustment` objects. Video tasks are not exposed.

Typical workflow: state → select → state → AI start → poll state until `ai.status=ready` → preview
result/original → accept → undo/redo if needed → save/export. AI start returns `requestId`
immediately, so polling and cancellation need no foreground window. A failed/cancelled job is not
automatically retried or applied; discarding leaves the document intact. Expansion requests opaque
generated output, while the original canvas remains protected by the local coverage mask. Nonempty
AI result layers automatically trim fully transparent margins in preview and on acceptance; the
canvas size, subject position and all nonzero alpha edges remain unchanged. Existing masked cutouts
can be trimmed with `editor_trim_layer` after reviewing the layer.

Live tool failures return MCP `isError=true`. After a connection timeout, read state to learn the
outcome before repeating a write. Cancelling local waiting does not promise to cancel provider billing.

For `connection=api/account`, model IDs remain `gpt-image-2.5-flare` and `gpt-image-2.5-sunburst`.
Invalid connections, unadvertised models and nonautomatic resolution are refused before opening
a draft or uploading. Custom API addresses and credentials are configured in the editor; MCP never
accepts keys. Image edits use a native mask and also protect unselected pixels locally.

`editor_ai_start` accepts up to four `references=[{path,role}]` and `preserve` instructions of at
most 1000 characters. Roles are `general/identity/clothing/style/composition`. Files are bounded
to 14 MiB and 16 megapixels, decoded locally and reduced to at most 1600 pixels before upload.
The canvas is always image 1; reference images follow it. Roles use those actual image numbers. The combined encoded input
budget is 48 MiB, with the selected model's count and size limits checked before uploads. These
constraints guide the model; they do not guarantee identity preservation inside the editable area.

`editor_state.layers[].mask` reports grid dimensions, enable/link flags and placement. Use mask
and layer detail previews to compare edge coverage before and after a refinement. Disabled masks
remain disabled during filter/import operations; enable them explicitly before painting. A stale
target is refused after worker preparation. Square max/min grow/shrink retains gray coverage.

Verification: `node tools/verify-editor-mcp.mjs <Compositor.Mcp.exe> [appPid]` checks discovery and
optionally an actual editor state/image. `--ai-image-probe` now has six groups; its final group
drives a real stdio MCP process against a real App using simulated image responses, including
UTF-8 paths, protected pixels, stale targets, wrong request IDs, accept/history, save/reopen,
color masks and their undo/transparent previews, expansion, cancellation, and invalid connection/resolution rejection before uploading. The live group also drives PNG layer import, selection,
rename, visibility, opacity, blend mode, combined transform/linked mask, reorder, duplication and
deletion; it checks exact Undo/Redo metadata, restored UI selection, invalid/stale requests,
AI-preview exclusion and saved pixels. A separate custom API group checks editable endpoints,
settings persistence, saved credentials, preview and Undo. All cloud responses are simulated.
Discovery exposes 35 tools: 29 live tools and six package tools. Original package/watcher interoperability remains
a separate probe.

## Build and launch

From the repository root:

    dotnet build Windows\tools\Compositor.Mcp\Compositor.Mcp.csproj

Use the built executable as the MCP client's command. Its output path is:

    Windows\tools\Compositor.Mcp\bin\Debug\net9.0-windows10.0.19041.0\Compositor.Mcp.exe

The server reads one JSON-RPC message per line from stdin and writes responses to stdout. Startup
status goes to stderr so it does not corrupt protocol output. It is normally launched and managed
by the MCP client, not by a user shell.

Client configuration formats differ. A common stdio shape is shown below; use the equivalent
fields in the MCP client you actually run:

    {
      "mcpServers": {
        "compositor-windows": {
          "command": "C:\\Users\\YOUR_NAME\\path\\Compositor.Mcp.exe",
          "args": []
        }
      }
    }

There are no server command-line arguments or environment variables. Start the MCP client after
saving configuration. Live tools find a running editor; package tools start with open_project.

## Protocol surface

Previews share settled immutable assets and render at the requested scale instead of composing
the full document and then resizing it. Up to four PNGs, totaling at most 16 MiB, are cached by
document token/revision, view, size, layer and AI request state; `cached` reports a hit. Pixel output
for reduced previews follows the compositor's scaled sampling; full-resolution export is unchanged.

API/account adapters do not expose provider task IDs and do not support interrupted-task recovery.
The retired provider task tools are no longer advertised or accepted. Existing local recovery files
are left untouched.

The server identifies as compositor-windows version 1.1.0, negotiates 2024-11-05 or 2025-06-18,
and declares the tools capability. It handles initialize, ping, tools/list, and tools/call. It
does not expose resources or prompts, and it does not use the official .NET MCP SDK. Treat this as
a narrow protocol implementation; broader client compatibility has not yet been established.

The package tool family holds one current project path at a time and requires open_project first.
Those calls reload the package from disk. Live tools instead use the currently shown in-memory
document, including unsaved edits, and require no package-tool open_project call.

## Saved-package tools

### open_project

Required arguments:

| Name | Type | Meaning |
| --- | --- | --- |
| path | string | Existing .comp project directory containing manifest.json |

Loads and validates the project. Returns canvas dimensions, layer count, and manifest version.

### get_project_state

No arguments. Returns text containing canvas dimensions, resolution, format version, document
UUID, and each layer's UUID, name, kind, transform, visibility, opacity, blend mode, and parent.
Layers are described bottom to top.

### add_layer

| Name | Type | Required | Meaning |
| --- | --- | --- | --- |
| name | string | yes | Layer name |
| pngPath | string | no | PNG file to import |

Adds a visible, normal-blend raster layer above the active layer. Without pngPath, it creates a
blank record covering the canvas. With a PNG, the layer starts at origin 0,0 with natural pixel
size. Sets the new layer active and saves.

### replace_layer_image

| Name | Type | Required | Meaning |
| --- | --- | --- | --- |
| layerId | string | yes | Existing layer UUID |
| pngPath | string | yes | Source PNG path |

Replaces that layer's raster pixels, keeping its UUID, origin, and rotation. The transform size
changes to the new PNG's pixel dimensions. Saves the project.

### update_manifest

Top-level arguments:

| Name | Type | Meaning |
| --- | --- | --- |
| activeLayerId | string | Set the active UUID; it must identify an existing layer |
| layers | array | Partial layer updates; each item must include id |

Each layers item may include:

| Name | Type | Behavior |
| --- | --- | --- |
| id | string | Required layer UUID |
| name | string | Replace name |
| isVisible | boolean | Replace visibility |
| opacity | number | Clamped to 0–1 |
| blendMode | string | Exact Compositor manifest blend-mode name |
| originX, originY | number | Replace one or both origin coordinates |
| width, height | number | Replace one or both transform dimensions |
| rotation | number | Replace clockwise rotation in degrees |
| parentId | string or null | Move under an existing folder, or clear the parent |

This tool does not alter layer order, pixels, masks, or adjustment settings. It validates the
result and saves it. A value that creates metadata forbidden by the manifest's current version
fails validation; there is no automatic format upgrade.

### export_image

| Name | Type | Required | Meaning |
| --- | --- | --- | --- |
| path | string | yes | Destination file path; its folder must already exist — the server writes the file but never creates the folder it was given |
| format | string | yes | png, jpeg, or jpg |
| quality | number | no | JPEG quality 0–100; default 90 |

Renders the current project into the existing destination directory. JPEG transparency is
flattened over white. Export does not change project dirty state.

## Example interaction

The exact client UI varies, but a typical agent sequence is:

1. Call open_project with an absolute path such as C:\Art\Poster.comp.
2. Call get_project_state and keep the existing layer UUIDs.
3. Create or obtain a PNG with the external image tool.
4. Call add_layer with its name and pngPath, or replace a known layer with replace_layer_image.
5. Use update_manifest to set the layer opacity, blend mode, or transform.
6. Call export_image for a flattened preview or deliverable.

An open editor watches the same package and normally reloads after a complete external save. If
the UI contains unsaved edits, it asks before touching them (revert takes the disk, keep mine
declines, closing without an answer leaves the change pending; a stroke, transform commit or
open sheet defers the question with backoff rather than pulling pixels out mid-edit).
The window keeps ONE watcher, bound to the tab currently shown, and it baselines its digest
when it attaches - so a package an agent rewrote while its tab was parked fires no event. The
tab-switch door (rebind) closes that: on arrival it compares the tab document's saved digest
against the disk, and on a mismatch runs the same reload/ask flow the watcher uses (a clean tab
reloads, a dirty one asks), matching the Mac's resume-time check on its per-tab controllers
(`resumeExternalChangeCheck`, IO/ProjectController+ExternalChanges.swift:28-30). The surface is
at ARRIVAL, not live: a background tab shows the change when you switch to it, not while you
look elsewhere - which is the same moment the Mac's dirty-tab ASK waits for.

## Errors and limits

The server answers every request line, including one it cannot parse, and separates which side
failed by JSON-RPC code: `-32700` for a line that is not JSON (its `id` is null, because the id
could not be read), `-32601` for a method the server does not implement, `-32602` for an argument
problem the caller can fix — a missing or mis-typed parameter, an unknown tool name, an export path
whose folder does not exist — and `-32000` for file or project state the server could not read or
validate (`IOException`, `ProjectFormatException`). Clients can key retry behaviour off the code
instead of only the message. The server handles one line at a time and is intended for one
client process. MCP calls are not a substitute for concurrent multi-writer locking.

Only PNG is accepted by package layer-image tools. They operate on local paths accessible to the
server process and do not expose image generation, live selections or undo. Use the `editor_*`
family for live image previews, AI status/cancellation and undo. Neither family exposes a remote
network listener or a general resource URI transport.

## Security and workflow

Treat tool arguments as trusted automation input: calls can overwrite project layers and exported
files. Use a working copy for experiments. Each mutation goes through the same project validator
and atomic store as the app. Package writes compare the acknowledged disk digest and refuse
conflicts; read get_project_state before reviewing/repeating a refused change. Live writes also
check document identity and revision. Prefer live tools while sharing a document with a person.
