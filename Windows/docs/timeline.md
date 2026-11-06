# Layer animation timeline

Open **View > Timeline**, then choose **Create layer animation timeline**. The default is 120
frames at 24 fps. Set duration, frame rate and loop; use Play/Pause, stepping, zoom and fit.

- Each layer has a visibility clip. Drag its edges to trim, or its body to move the clip and keys.
- Add position X/Y, scale X/Y, rotation and opacity keys. Drag diamonds to change key timing.
- Choose linear, smoothstep or hold interpolation. Content edits support Undo/Redo.
- Export a transparent PNG sequence on a worker with progress and cancellation. Completed
  sequences include `sequence.json` with frame rate and filename pattern.
- Control animation with MCP's `editor_timeline`; `editor_state` reports tracks and the current
  frame, and preview/PNG export use the currently evaluated frame. See [MCP](mcp.md).

Seeking and playback share source assets and compositor caches without creating Undo steps.
Playback skips frames when rendering is busy. Disable **Animation preview** or close the panel
before ordinary artwork edits. Previewed transforms do not overwrite the base layer geometry.

Position keys are absolute layer centers; scale keys multiply the base layer dimensions. Clip
in-points are inclusive and out-points exclusive. Rotation can include multiple turns. Folders
and adjustment tracks support opacity and visible time, without folder transform keys.

Animation is stored in the native project. Creating it upgrades that document to **format v12**
in the same Undo step. New static documents remain v11. Current macOS builds reject v12 rather
than discard animation. Removing animation does not downgrade the format. The canonical
[v12 schema](../../docs/project-format.md#version-12-layer-animation-windows) describes validation.

This is a layer animation timeline: audio/video clips and MP4/GIF encoding are not implemented.
Local development probes exercised interpolation, editing, save/reopen, playback, export and MCP.
Hosted CI verifies component behavior; it does not certify mouse interaction or every GPU driver.
