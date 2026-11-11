# Model-authored SVG import

`editor_layer_import_svg` lets a model draw icons, marks, diagrams and decorative artwork in SVG
and import the result directly. It makes no cloud image request. Supply a fresh `appPid`,
`documentToken` and `revision` as for other live edits, then:

```json
{
  "svg": "<svg viewBox='0 0 100 100'><path d='M50 5 61 36 95 36 68 56 78 90 50 70 22 90 32 56 5 36 39 36Z' fill='#ffb347'/></svg>",
  "width": 200,
  "height": 200,
  "x": 80,
  "y": 60,
  "name": "Star"
}
```

Alternatively supply an absolute local UTF-8 `.svg` `path`, omitting `svg`. The output viewport
is centered on the document when x/y are omitted. Coordinates refer to the viewport before
trimming. `trim=true` crops zero-alpha margins and adjusts placement to keep all visible pixels
in place; `trim=false` retains the full authored viewport. Empty results are refused.

The new raster layer is selected in the current folder. Creation feedback shows its final
cropped bounds, and one Undo removes it. Save/reopen uses the normal PNG layer asset, requiring
no project-format change. Original SVG source and editable vector nodes are **not** stored;
choose the needed output resolution when importing, and use `editor_text` for editable text.

## Supported SVG

The existing MIT-licensed [Microsoft Win2D](https://github.com/microsoft/Win2D) dependency renders
paths, rectangles, circles/ellipses, lines, polygons/polylines, groups, linear/radial gradients,
clipping and local `#id` reuse. Include a finite positive `viewBox`. Inline presentation styles
are supported. The renderer follows [Direct2D's SVG subset](https://learn.microsoft.com/windows/win32/direct2d/svg-support).

Unsupported elements/attributes return an error, including text, filters, CSS stylesheets, image
elements, scripts and foreignObject. External references, DTDs/entities, missing IDs, recursive
or excessive reuse are refused. SVG loading does not fetch network content or other local files.

Input is capped at 128 KiB UTF-8 and 4096 elements. Output dimensions are 1–8192 per side, with
at most 16,777,216 pixels and within the document's remaining image/mask pixel budget. Rendering
runs on a worker and can use Win2D software rendering when hardware fails. Target identity and
revision are rechecked after rendering and again before publication; stale or timed-out work
does not add a layer. The local pipe admits up to 1 MiB of JSON to carry escaped SVG input.

## Local checks

After building Debug, run the isolated SVG app probe:

```powershell
pwsh -NoProfile -File Windows/tools/run-app-probe.ps1 -AppPath Windows/src/Compositor.App/bin/x64/Debug/net9.0-windows10.0.19041.0/win-x64/Compositor.App.exe -LogDirectory Windows/artifacts/svg-check -EvidencePrefix svg-check -Argument --svg-import-probe -ReportFile svg-import-probe.json -ExpectedGroups 7
```

Parser tests are in `Compositor.App.Infrastructure.Tests/SvgImportTests.cs`; prepared placement
and history tests are in `Compositor.Core.Tests/FileImportTests.cs`. Run records describe the
actual verification scope; this command does not require or certify foreground mouse input.

## Verified locally, 2026-11-06

- Debug and Release x64 App builds and the Release MCP server build passed with no warnings or errors.
- Release solution tests passed: Core 2207, App.Infrastructure 193 and Renderer 40 (2440 total).
  The solution test build reported three existing xUnit analyzer warnings in unrelated tests.
- The isolated App probe passed all seven groups: native/software alpha, trimmed placement and
  creation feedback with Undo/Redo, untrimmed viewport, UTF-8 file/current folder/save/reload,
  rejected input without history, retired document and timeout without late publication.
- The actual stdio MCP server advertised 36 tools. A 36,107-byte UTF-8 SVG traversed the local
  pipe, created the cropped raster layer, and retained exact canvas PNG bytes across Undo/Redo.
  A separate fresh editor process opened the saved package and returned the same canvas PNG.
- Hardware and software rendered transparent pixels correctly. This device quantized a half-opacity
  red fill to 127 on hardware and 128 in software; alpha and premultiplied color differed by at most
  one byte value. Hardware/software output is not claimed to be byte-identical.

Local records are under `Windows/artifacts/probe-evidence/svg-final-20261106-*` and
`Windows/artifacts/svg-import-20261106/transport-pass/`. They were produced from the modified
working tree based on `f303b903`; the App probe records its actual assembly hashes and dirty state.
No cloud image service or image-generation credits were used.

Use the updated App and MCP server together. Clients that cached the earlier tool catalog need
to reconnect before they can discover `editor_layer_import_svg`.
