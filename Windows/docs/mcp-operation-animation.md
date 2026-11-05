# MCP operation animation

MCP text creation, PNG import and layer duplication display a virtual AI cursor dragging a frame
from the target's top-left. The badge shows its actual width × height in document pixels. The
element appears after the frame is drawn. Transforms animate from the original placement;
selection, mask and adjustment operations highlight their target. Batches and rapid consecutive
requests use compact feedback.

The overlay follows zoom and pan without moving the real mouse or capturing input. It creates
no extra layers or Undo steps. **View > AI Operation Animation** turns it off and remembers the
choice. Windows animation settings disable movement and the animation wait.

Prepared edits are validated again before publication. Intervening content changes or a document
switch refuse stale results. Saving during the preview preserves the saved state's clean status
after Undo. MCP tool parameters and the shared project format are unchanged.

See the [user guide](user-guide.md#external-editing) and [MCP reference](mcp.md).
