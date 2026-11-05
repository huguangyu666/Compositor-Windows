# Portable single-file build

The `Portable-x64` publish profiles produce a self-contained `Compositor.App.exe` and an optional
standalone `Compositor.Mcp.exe`. They bundle their .NET runtime; the app also bundles the Windows
App SDK and XAML resources. Dependencies extract to a per-user temporary location on first launch.
This uses the [Windows App SDK single-file deployment support](https://learn.microsoft.com/windows/apps/package-and-deploy/unpackage-winui-app#single-file-exe).

From the repository root, choose a fresh output directory and a Beta version:

```powershell
Push-Location Windows/ci
dotnet publish ../src/Compositor.App/Compositor.App.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:PublishProfile=Portable-x64 -p:Version=1.0.0-beta.20261105 -o ../artifacts/releases/beta-20261105/app
dotnet publish ../tools/Compositor.Mcp/Compositor.Mcp.csproj -c Release -r win-x64 --self-contained true -p:PublishProfile=Portable-x64 -p:Version=1.0.0-beta.20261105 -o ../artifacts/releases/beta-20261105/mcp
Pop-Location
```

Keep the app filename `Compositor.App.exe`: live MCP discovery currently looks for that process
name. Run it normally by double-clicking. The MCP executable is started by an MCP client with
stdio, not as a desktop app. Package its configuration instructions and the entire
`Windows/skills/compositor-mcp` folder alongside it; see [MCP setup](mcp.md).

Both profiles embed the upstream MIT license. Distribute a readable copy of `LICENSE` and the
app's `Windows/third-party/CLIProxyAPI-LICENSE.txt` with the complete download too. Include current
startup instructions and known limitations. Do not copy local credentials, runtime logs, test
projects or developer output folders into a release.

Check the actual single-file output after publishing: launch it from its distribution directory,
read a live MCP state/preview, exercise save/reopen/export, and close it normally. Record the file
hashes, source revision and actual results. A successful test on the development computer does
not establish clean-machine compatibility. These profiles do not sign, upload or publish a release.
