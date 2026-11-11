# Build and verification

Use .NET SDK 9 on Windows. `Windows/ci/global.json` selects that SDK family.

## Component tests

```powershell
./Windows/ci/test-windows.ps1
```

This runs App.Infrastructure, Core, Renderer and Segmentation, and writes TRX results under
`Windows/artifacts/ci-test-results/`. It does not require the editor to be in the foreground.

## Hosted CI

The active workflow is `.github/workflows/windows.yml`. On main-branch pushes, pull requests and
manual dispatch it runs component tests, publishes self-contained x64 App and MCP executables,
and checks the MCP tool catalog. Test results are uploaded as an Actions artifact.

The release's `BUILD-INFO.json` records exact binary hashes, source mapping, verification scope
and its hosted workflow link. The public Windows source snapshot has curated history and docs;
its Git commit is different from the local development revision used to build the release.

## November 6 package scope

The packaged portable editor was launched locally and checked through real MCP for live state,
PNG preview, a layer move and Undo, with identical restored preview bytes. The matching MCP server
advertises 35 tools and passed live state/PNG preview. GPU and timeline development checks are
described in [GPU rendering](gpu-rendering.md) and [timeline scope](timeline.md).

The portable build is unsigned. Clean-machine compatibility, other hardware and a complete
foreground interaction sweep have not been established for this release. Hosted CI is a
build/component check. No paid cloud request is part of the packaging smoke check.

## Interactive probes

`Windows/ci/verify-all.ps1` and `Windows/tools/run-app-probe.ps1` support deeper local checks.
Some probes require a real foreground window; their output belongs in ignored artifact folders.
Do not describe an unrun probe or unavailable environment as passing.

## November 11 update

Fresh local component run: App.Infrastructure 193, Core 2207, Renderer 40, Segmentation 39; 2479 passed, zero failures/skips. Hosted CI also runs Segmentation. The seven-group AI/MCP exchange probe passed during development with deterministic local PNGs and no image-service request; timing, SVG and object-selection development scopes are documented separately. The package records its own startup and live-MCP checks in BUILD-INFO.json.
