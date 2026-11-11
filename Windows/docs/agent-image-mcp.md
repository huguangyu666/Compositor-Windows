# External-agent image generation

Implemented for the Windows live MCP bridge. An agent can use its own available image tools,
then return a local PNG to Compositor. The editor requires no image API key or account login
for this path and makes no cloud request. Tool availability, billing and image upload policy
belong to the agent's chosen service; this feature does not promise free generation.

## New artwork

Generate an image using the agent's own tool and retain its local PNG path. Call
`editor_layer_import` with that path and optional name and placement:

```json
{"path":"C:\\work\\generated.png","name":"Generated artwork","x":120,"y":80,"width":600,"height":400}
```

Include fresh `appPid`, `documentToken` and `revision` on every write. Dimensions refer to the
full source rectangle and must be supplied together; omitting both keeps the source size.
Transparent margins are cropped by default after placement, preserving artwork position and
scale. `trim=false` keeps the source grid. Faint nonzero alpha and opaque backgrounds are retained;
trimming does not remove a background. The creation animation uses the final content bounds.
If the cropped placement would fall below the format's one-pixel minimum or exceed coordinate
limits, import retains the valid full source rectangle instead.
Import creates one selected layer and one Undo, in the current folder, without saving.

## Selection edits and expansion

1. Read state and preview the canvas. For remove/replace, establish a nonempty selection first;
   feathered and imported coverage are supported.
2. Call `editor_ai_prepare`, `operation=remove/replace` (replace is the default), or `expand`
   with nonzero `left/right/top/bottom`. Each edge is 0–8192 pixels. The complete target is
   limited to 16,777,216 pixels and the document's remaining budget.
3. Retain `ai.requestId`. `ai.source` is `agent`, `status` is `waiting`, and `ai.input` returns:

   - `imagePath`: absolute local PNG of the full captured composite, with expansion padding.
   - `maskPath`: absolute PNG, **transparent = editable, opaque = protected**. Intermediate alpha
     preserves partial coverage; this is not the opaque grayscale convention for selection import.
   - `width`, `height`, `left`, `top` and `resultLayout=fullCanvas` describe the exact input layout.

4. Pass these inputs and the user's instructions to the agent's own available image tool.
   Keep the complete canvas framing and return a PNG. The editor does not run that tool or
   transmit these files; the agent must follow the user's existing upload authorization.
5. Refresh state and call `editor_ai_import` with the exact request ID and local result `path`.
   The output represents the **full prepared canvas**, not a crop of the selection. Different
   dimensions are resized to the target and may change aspect ratio. Frozen coverage is reapplied
   locally, protecting original pixels outside the selection or in the original expansion area.
6. Inspect `editor_preview`, `view=result` and `original`. Import only creates a review candidate;
   the document and history are unchanged. Up to three candidates are retained.
7. Use `editor_ai_accept` with the matching ID and optional `candidateIndex`, or
   `editor_ai_cancel`, `discard=true`. Acceptance creates a normal top-level raster layer,
   trims transparent margins and includes expansion in the same Undo. Save/export separately.

The editor shows a localized external-agent panel and the existing cyan/violet working effect
while waiting. It offers original/result comparison and candidate selection, without API settings.
Windows reduced-motion preferences remain respected.

## Ownership and cancellation

The request binds the captured document, selection and source assets. Changed source pixels,
selection changes, document replacement or closing retire the draft. A late result, wrong request
ID or stale document token/revision is refused. Reading and preparing each local input/result has
a 20-second deadline, with target checks again before publication. Failed result imports retain
the previous waiting/ready draft so a corrected file can be supplied.

`editor_ai_cancel` stops editor-side work/waiting. It cannot cancel the agent's generation tool;
the agent must stop its own job separately. `discard=true` also closes the review. The editor owns
only the two input files under its per-request temporary directory: accept/discard/retirement
removes those files and the empty request directory. It never deletes the supplied generated PNG.
A process crash can leave temporary input files behind; no startup cleanup is implemented.

The existing `editor_ai_start` continues to use the editor's configured API/account. It is a
separate choice; agents should not start a duplicate paid editor job while using their own tool.

## Local verification, 2026-11-08

The regression first failed because the old server did not advertise `editor_ai_prepare`.
Separate checks exposed the old PNG placement behavior and a crop below the format minimum;
both failed before their corresponding fixes. Final Debug PID 55680 passed all seven AI/MCP
probe groups through real stdio/named-pipe transport with isolated preferences. The new group
removes the test editor key, checks that no image-service calls occur, and covers exact PNG
placement, trimming and its size fallback, frozen soft coverage, original/result comparison,
accept/Undo/Redo/save/reload, cancellation, stale selection rejection and protected expansion.
The source server advertises 38 tools (32 live, six package tools).

Debug and Release app builds completed with zero warnings/errors. Release PID 3812 started,
opened the saved 1080×1920, 18-layer Lime project cleanly, and passed MCP state/PNG preview checks.
The previous clean timeline review closed normally; the original editor stayed open. The installed
companion skill and local MCP command were updated; existing clients must reconnect for new schemas.

Local records: `Windows/artifacts/agent-image-20261108/verification.json` and
`Windows/artifacts/probe-evidence/agent-image-20261108-final-55680.report.json`.
This is a dirty working-tree build, not a clean-checkout claim. Tests use deterministic local PNGs;
they verify the exchange and editor behavior, not the quality or entitlement of an external model.
