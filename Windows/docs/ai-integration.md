# AI integration

The AI panel supports selection-based removal/replacement and canvas expansion through a custom
API connection. Configure the endpoint and key in the app. Credentials are encrypted for the
current Windows user and stored outside the project. The optional account OAuth adapter is
experimental; successful API use does not establish account compatibility.

## Workflow

Select the region, describe the edit and optionally add reference images. The editor captures the
input and document identity, displays progress and holds results as previews. Accepting creates an
ordinary layer and one Undo step. Unselected pixels are protected locally; expansion preserves
the original canvas. Switching documents or invalidating the input retires stale results.

Up to four references can carry general, identity, clothing, style or composition roles. Reference
files are bounded and resized before upload. These instructions guide generation; they do not
guarantee identity preservation. Transparent margins are trimmed on accepted nonempty results;
nonzero background haze needs mask refinement.

## MCP

External agents can use [live MCP tools](mcp.md) to inspect the canvas, select regions, start a
request, poll status, inspect original/result previews, accept or discard, undo, save and export.
All writes use fresh document tokens and revisions; accepting requires the matching request ID.
The server exposes no credentials or login-setting mutation tools.

Local photo grading, typography, masks and other native edits need no image-generation API key.
The [companion skill](../skills/compositor-mcp/SKILL.md) explains these workflows.

## Privacy and errors

Image generation uploads the complete composite canvas, selection mask, prompt and reference
images to the configured service, even when the selected region is small. Review its data policy
before sending sensitive material. MCP previews also enter the reasoning client's context.

Cancellation stops local waiting and cannot guarantee cancellation of provider processing or
billing. Failed jobs are not automatically retried or applied. Review the current status before
retrying. The [testing guide](testing.md) distinguishes component, local and release checks.
