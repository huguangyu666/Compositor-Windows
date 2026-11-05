# Windows verification

Run `./Windows/ci/test-windows.ps1` from the repository root for the three component suites.
The active GitHub workflow is `.github/workflows/windows.yml`; it additionally publishes both
self-contained executables and verifies MCP discovery. Results are Actions artifacts.

`verify-all.ps1` supports deeper local App/foreground probes. See
[testing](../docs/testing.md) and [single-file builds](../docs/portable-build.md).
