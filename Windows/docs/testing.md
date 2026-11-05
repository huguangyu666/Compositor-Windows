# Build and verification

Use .NET SDK 9 on Windows. `Windows/ci/global.json` selects that SDK family.

## Component tests

```powershell
./Windows/ci/test-windows.ps1
```

This runs App.Infrastructure, Core and Renderer, and writes TRX results under
`Windows/artifacts/ci-test-results/`. It does not require the editor to be in the foreground.

## Hosted CI

The active workflow is `.github/workflows/windows.yml`. On main-branch pushes, pull requests and
manual dispatch it runs the component tests, publishes self-contained x64 App and MCP executables,
and checks the MCP tool catalog. Test results are uploaded as an Actions artifact.

## Release verification

For this initial Beta, recorded local component results are 2,116 Core, 40 Renderer and 110
App.Infrastructure tests passing. The actual portable EXE was checked for startup, live MCP
state/PNG preview, editable text, rectangle selection, Undo/Redo, save, normal exit, reopening in a
new process and PNG export. MCP discovery exposed 34 tools.

The text export before and after reopening differed at 421 edge pixels by at most 1/255 per
channel; editable text and layer state were retained. This is not a claim of byte-identical
text rasterization. The release's `BUILD-INFO.json` records the packaged binary hashes.

The portable build is unsigned. Clean-machine compatibility, other hardware and a complete
foreground interaction sweep have not been established for this release. CI is a build/component
check, not proof of those properties. No paid cloud request is part of the packaging smoke check.

## Interactive probes

`Windows/ci/verify-all.ps1` and `Windows/tools/run-app-probe.ps1` support deeper local checks.
Some probes require a real foreground window and their evidence is written under ignored
artifact locations. Do not describe an unrun probe or unavailable environment as passing.
