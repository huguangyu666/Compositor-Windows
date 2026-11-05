# Troubleshooting

## dotnet cannot find the target framework

Install the .NET 9 SDK and restore the solution:

    dotnet --info
    dotnet restore Windows\Compositor.Windows.slnx

If only a newer SDK is installed, confirm it can resolve the
net9.0-windows10.0.19041.0 targeting pack and Windows SDK build tools from NuGet.

## WinUI or Windows App SDK restore fails

The first build needs NuGet access for Microsoft.WindowsAppSDK and
Microsoft.Windows.SDK.BuildTools. Retry restore on a network that can reach the configured NuGet
feeds. This project is unpackaged and self-contained at the Windows App SDK level; it does not
require a separately installed MSIX package.

## The executable is not at the documented path

Build with the same configuration and runtime identifier as the path:

    dotnet build Windows\src\Compositor.App\Compositor.App.csproj -p:Platform=x64 -p:RuntimeIdentifier=win-x64

Debug/Release, architecture, SDK, and runtime identifier change the output directory. Search under
Windows\src\Compositor.App\bin for Compositor.App.exe.

## XAML theme resources are missing when launched from a packaged tool

If the editor's log reports `Cannot locate resource` for
`ms-appx:///Microsoft.UI.Xaml/Themes/themeresources.xaml`, inspect the launch environment before
changing XAML or runtime dependencies. A child process can inherit its packaged tool host's MSIX
identity. This was reproduced from the Codex desktop host while ordinary app resources were present.

The log lives in `%LOCALAPPDATA%\Compositor\Logs\compositor-app-<pid>.log`; see
[Application logging](logging.md) for the naming, rotation and the `COMPOSITOR_LOG_DIRECTORY`
override a verification script sets for its child process.

The protocol/startup smoke uses `tools/start-unpackaged-app.ps1`. It checks the parent package
identity and launches the unpackaged app with the desktop-app process policy when needed; ordinary
terminals use Start-Process. Windows documents the policy in
[UpdateProcThreadAttribute](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute).
To run the Debug effects probe from such a host:

    $app = & Windows/tools/start-unpackaged-app.ps1 -FilePath Windows/src/Compositor.App/bin/x64/Debug/net9.0-windows10.0.19041.0/win-x64/Compositor.App.exe -Argument --effects-perf
    $app.WaitForExit()
    $app.ExitCode
    $app.Dispose()

Check both exit code and the timestamp/status of the newly written effects-performance.json.
An older passed report does not verify the current launch.

## A project does not open

Check that the selected directory directly contains manifest.json and images/. Check that:

- JSON is valid and uses the exact, case-sensitive Compositor field and enum names;
- format is com.compositor.project and color space is sRGB;
- each referenced layer PNG or grayscale mask exists and has the exact UUID-based filename;
- the manifest version is 1–11;
- dimensions, layer count, encoded file sizes, nesting, opacity, and links pass validation.

The error may intentionally group malformed metadata, missing assets, and unsupported limits to
avoid replacing the current document with a partial load.

## Save fails after editing an older project

The app accepts manifests v1–v11 but currently preserves the loaded version number. It does not
upgrade an older file automatically. A new mask, folder opacity, adjustment, guide, or other
version-gated field can make a legacy document invalid for its declared version.

Use a copy of the project and check the format gate for the edited feature. Until migration is
implemented, do not manually bump the version unless every field in the manifest conforms to that
version's schema.

## External changes do not appear

The watcher waits 350 ms after the last file event and checks the manifest bytes plus asset names
and sizes. Ensure the external writer:

1. Writes every changed PNG fully into images/.
2. Writes a temporary manifest in the project directory.
3. Atomically renames the temporary manifest over manifest.json.

If the replacement PNG has exactly the same byte length and manifest bytes are unchanged, the
current digest cannot distinguish it. Change a harmless manifest field such as the layer name, or
write a PNG with a different encoded size.

If the editor has unsaved edits, it does not replace them silently or merely report: it asks, offering
Revert (take the disk version, giving up the unsaved work) or Keep Mine (keep editing; the outside
write becomes the baseline the next deliberate save may overwrite). Closing the question without
answering leaves the change pending and the next save is refused until it is resolved. While a stroke,
transform commit or open filter sheet holds the pixels, the question waits and is re-presented once
that work settles, rather than pulling the document out mid-edit. (The watcher probe pins each of
these answers and the deferral.) The window watches the tab on screen, so a background tab edited
from elsewhere shows the change when you switch back to it.

## MCP client cannot start the server

Check that the configured command is the full path to Compositor.Mcp.exe, that the file was built,
and that the MCP client supports local stdio servers. Configuration formats differ across hosts;
use the host's own MCP server configuration shape.

Do not add startup text to stdout. The server writes protocol responses there and diagnostics to
stderr. Running the server directly in a shell waits for JSON-RPC lines from stdin.

## MCP returns an error

Open the project first with open_project. Confirm tool arguments use exact casing and types, layer
IDs are real UUIDs, PNG paths are readable by the server process, and the project is valid. The
server answers with `-32700` (a line that is not JSON), `-32601` (unknown method), `-32602`
(argument the caller can fix: missing or mis-typed parameter, unknown tool, an export path whose
folder does not exist) or `-32000` (a file or project it could not read or validate); see
[mcp.md](mcp.md). An `export_image` failure saying "No folder exists for the export path" means the
caller must create the folder first — the server writes files, it does not build directory trees.

The MCP server reloads from disk before each operation. If another process changes the project
concurrently, a tool call can read or overwrite a newer state; the current implementation has no
revision conflict check.

## Large documents render slowly or fail

The renderer and compositing engine run on the CPU. One surface is limited to 200 million pixels,
and a document's source-image and mask pixel budgets each depend on physical memory. The UI also
keeps a document-sized display buffer. Try a smaller canvas, fewer large source layers, or reduced
source dimensions. GPU rendering and tile-based compositing are not implemented.

## JPEG looks white behind transparent pixels

This is expected. JPEG has no alpha channel. Both the UI export command and MCP export flatten
transparency over white. Use PNG when transparent output is required.
