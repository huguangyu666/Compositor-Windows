# Windows implementation

Use this checkout for Windows development. Do not create project copies or additional worktrees
unless requested. Read the user guide before describing UI behavior and the MCP reference before
changing or describing tools.

Build and test from the repository root:

```powershell
./Windows/ci/test-windows.ps1
Push-Location Windows/ci
dotnet build ../src/Compositor.App/Compositor.App.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64
dotnet build ../tools/Compositor.Mcp/Compositor.Mcp.csproj -c Release
Pop-Location
```

Match local naming and comments. Use American spelling. Keep rendering and document operations
in Core, WinUI presentation in Renderer and interaction in App. Read `docs/project-format.md`
before changing persistence, and `docs/architecture.md` before changing ownership, rendering or
watchers. Preserve the asset-first, atomic-manifest-last contract. Format changes must be documented
in both format references and verified with round-trip tests; consider compatibility with upstream.
