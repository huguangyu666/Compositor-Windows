#if DEBUG
using Compositor.Core;

namespace Compositor.App;

public sealed partial class MainWindow
{
    /// <summary>
    /// D04: what an export puts in the file and what the status line then claims about it. The save picker a
    /// user answered cannot be driven from a probe, so this asks the function the picker path calls, over a
    /// real rendered document.
    /// </summary>
    private async Task VerifyExportResolutionAsync(Dictionary<string, object> report)
    {
        var white = Windows.UI.Color.FromArgb(255, 255, 255, 255);
        var sized = EditorDocument.Create(20, 12);
        sized.Snapshot.Manifest.Resolution = 300;
        var (png, pngStatus) = await ExportBytesAndStatusAsync(sized.Snapshot, "Sized.png", false, 85, white);
        Require(PngMetadata.TryFindChunk(png, "pHYs", out _, out int data),
            $"a PNG export carries no pHYs at all: {png.Length} bytes");
        uint ppm = ((uint)png[data] << 24) | ((uint)png[data + 1] << 16) | ((uint)png[data + 2] << 8) | png[data + 3];
        double dpi = PngMetadata.DpiFromPixelsPerMetre(ppm);
        Require(Math.Abs(dpi - 300) < .5, $"the exported PNG reports {dpi:0.#} DPI, not the document's 300");
        // Digits are language-proof; the words are not — this probe runs in whatever UI language the machine
        // has, so "does not say quality" would be vacuous in Chinese. The number that may not appear here is the
        // JPEG quality the call passed in.
        Require(pngStatus.Contains("300", StringComparison.Ordinal) && !pngStatus.Contains("85", StringComparison.Ordinal),
            $"the PNG status promises a number that is not in the file: {pngStatus}");

        // A new document carries 72 DPI (EditorDocument.Create), so this is the change users actually see:
        // every PNG export used to arrive stamped at WIC's 96, whatever the document said.
        var defaults = EditorDocument.Create(20, 12);
        var (usual, usualStatus) = await ExportBytesAndStatusAsync(defaults.Snapshot, "New.png", false, 85, white);
        Require(PngMetadata.TryFindChunk(usual, "pHYs", out _, out int usualData), "the default export lost its chunk");
        uint usualPpm = ((uint)usual[usualData] << 24) | ((uint)usual[usualData + 1] << 16) | ((uint)usual[usualData + 2] << 8) | usual[usualData + 3];
        Require(usualPpm == 2835 && usualStatus.Contains("72", StringComparison.Ordinal),
            $"a new document's export reports {usualPpm} ppm and says \"{usualStatus}\"");

        // A manifest with no resolution at all — an older or foreign project — must still get the plain WIC
        // bytes rather than an invented number, and its status line must not claim one either.
        var plain = EditorDocument.Create(20, 12);
        plain.Snapshot.Manifest.Resolution = null;
        var (bare, bareStatus) = await ExportBytesAndStatusAsync(plain.Snapshot, "Plain.png", false, 85, white);
        Require(PngMetadata.TryFindChunk(bare, "pHYs", out _, out int bareData), "a plain PNG export lost its chunk");
        uint barePpm = ((uint)bare[bareData] << 24) | ((uint)bare[bareData + 1] << 16) | ((uint)bare[bareData + 2] << 8) | bare[bareData + 3];
        Require(barePpm == 3779, $"a project with no resolution got a stamped DPI anyway ({barePpm} ppm)");
        Require(!bareStatus.Contains("85", StringComparison.Ordinal) && !bareStatus.Contains("3779", StringComparison.Ordinal),
            $"the status invented a number for a file that carries none: {bareStatus}");
        report["exportPngCarriesTheDocumentResolution"] = "passed";

        var (jpeg, jpegStatus) = await ExportBytesAndStatusAsync(sized.Snapshot, "Shot.jpg", true, 62,
            Windows.UI.Color.FromArgb(255, 10, 20, 30));
        Require(!PngMetadata.TryFindChunk(jpeg, "pHYs", out _, out _), "the JPEG export wrote a PNG chunk");
        Require(jpegStatus.Contains("62", StringComparison.Ordinal) && !jpegStatus.Contains("300", StringComparison.Ordinal),
            $"the JPEG status does not report the quality actually used: {jpegStatus}");
        report["exportStatusSaysWhatTheFileHolds"] = "passed";
    }
}
#endif
