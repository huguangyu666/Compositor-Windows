#if DEBUG
using System.Numerics;
using Compositor.Core;
using Microsoft.Graphics.Canvas;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task VerifyGridDashPatternAsync(Dictionary<string, object> report)
    {
        bool wasGrid = showsGrid, wasPixels = showPixelGrid, wasFit = viewport.FollowsFit;
        double wasZoom = viewport.Zoom; var wasLayout = LayoutGrid; var wasAppearance = GridAppearance;
        var scene = EditorDocument.Create(1024, 1024); OpenDocument(scene); await WaitForFramesAsync();
        var samples = new List<object>(); report["gridDashPatternSamples"] = samples;
        var phaseControls = new Dictionary<(double Backing, double Zoom, int Pattern, bool Vertical), (double Top, int[] Delta)>();
        try
        {
            showPixelGrid = false;
            foreach (double backing in new[] { 1.0, 1.5, 2.0 })
            foreach (double zoom in new[] { .5, 1.0, 2.0, 3.25 })
            foreach (int pattern in new[] { 0, 1, 2 })
            foreach (bool vertical in new[] { true, false })
            foreach (double top in new[] { -13.25, -15.25 })
            {
                LayoutGrid = pattern == 2 ? new(512, 2) : new(256, 1);
                GridAppearance = new(GridAppearance.Preset.Cyan, style: pattern == 1
                    ? GridAppearance.LineStyle.DashedLines : GridAppearance.LineStyle.Dots, opacity: 100);
                int viewWidth = vertical ? 96 : 32, viewHeight = vertical ? 32 : 96;
                viewport.Resize(viewWidth, viewHeight, backing); viewport.SetZoom(zoom, viewport.Center);
                double pointsPerPixel = viewport.PointsPerPixel;
                var origin = viewport.Origin;
                double across = 48.25 - 256 * pointsPerPixel;
                viewport.Pan(vertical ? new(across - origin.X, top - origin.Y) : new(top - origin.X, across - origin.Y));
                double on = pattern == 1 ? 4 : 1, off = pattern == 1 ? 3 : 2;
                byte[] Render(bool grid, bool reference)
                {
                    using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), viewWidth, viewHeight, (float)(96 * backing));
                    using (var drawing = target.CreateDrawingSession())
                    {
                        showsGrid = grid; drawing.Clear(Microsoft.UI.Colors.Transparent); DrawViewportContent(drawing);
                        if (reference)
                        {
                            var at = viewport.Origin;
                            drawing.Transform = Matrix3x2.CreateScale((float)pointsPerPixel)
                                * Matrix3x2.CreateTranslation((float)at.X, (float)at.Y);
                            var color = GridDrawColor(GridAppearance.Color, pattern == 2
                                ? GridAppearance.SubdivisionAlpha : GridAppearance.MajorAlpha);
                            // Independent reference: explicit solid segments whose lengths
                            // are the Mac's screen points, anchored at document zero.
                            for (int i = 0; ; i++)
                            {
                                double begin = i * (on + off) / pointsPerPixel;
                                if (begin >= 1024) break;
                                double end = Math.Min(1024, begin + on / pointsPerPixel);
                                if (vertical) drawing.DrawLine(256, (float)begin, 256, (float)end, color, (float)(1 / zoom));
                                else drawing.DrawLine((float)begin, 256, (float)end, 256, color, (float)(1 / zoom));
                            }
                        }
                    }
                    int x = (int)Math.Round(48.25 * backing) - 2;
                    int length = (int)(32 * backing);
                    if (vertical) return target.GetPixelBytes(x, 0, 5, length);
                    var horizontal = target.GetPixelBytes(0, x, length, 5);
                    var transposed = new byte[horizontal.Length];
                    for (int along = 0; along < length; along++) for (int cross = 0; cross < 5; cross++)
                        horizontal.AsSpan((cross * length + along) * 4, 4).CopyTo(transposed.AsSpan((along * 5 + cross) * 4, 4));
                    return transposed;
                }
                var blank = Render(false, false); var expected = Render(false, true); var actual = Render(true, false);
                string wanted = GridRows(expected, blank, 5), observed = GridRows(actual, blank, 5);
                int[] Delta(byte[] pixels) => Enumerable.Range(0, pixels.Length / 20).Select(row =>
                    Enumerable.Range(0, 5).SelectMany(column => Enumerable.Range(0, 3).Select(channel =>
                        Math.Abs(pixels[row * 20 + column * 4 + channel] - blank[row * 20 + column * 4 + channel]))).Max()).ToArray();
                var expectedDelta = Delta(expected); var actualDelta = Delta(actual);
                double physicalPeriod = (on + off) * backing;
                int cyclePixels = (int)Math.Round(physicalPeriod * (physicalPeriod % 1 == 0 ? 1 : 2));
                int maxCycleError = 0;
                for (int start = 0; start + cyclePixels <= actualDelta.Length; start += cyclePixels)
                    maxCycleError = Math.Max(maxCycleError, Math.Abs(actualDelta.AsSpan(start, cyclePixels).ToArray().Sum()
                        - expectedDelta.AsSpan(start, cyclePixels).ToArray().Sum()));
                var key = (backing, zoom, pattern, vertical); int phaseError = 0;
                if (phaseControls.TryGetValue(key, out var first))
                {
                    int rowsMoved = (int)Math.Round((first.Top - top) * backing);
                    for (int row = 0; row + rowsMoved < actualDelta.Length; row++)
                        phaseError = Math.Max(phaseError, Math.Abs(actualDelta[row] - first.Delta[row + rowsMoved]));
                }
                else phaseControls[key] = (top, actualDelta);
                samples.Add(new { backing, zoom, pattern = pattern == 2 ? "subdivision" : GridAppearance.Style.ToString(),
                    vertical, top, origin = viewport.Origin, expected = wanted, actual = observed,
                    expectedDelta, actualDelta, cyclePixels, maxCycleError, phaseError,
                    dashValues = GridMajorStroke.CustomDashStyle, dashCap = GridMajorStroke.DashCap.ToString() });
                Require(wanted.Contains('#') && wanted.Contains('.'), "Grid dash reference captured no on/off rhythm.");
                // D2D dashes and separate solid segments distribute antialiasing
                // differently at endpoints. Integrate complete physical periods,
                // allowing one byte of quantization per row, then check phase
                // independently by translating the same actual signal.
                Require(maxCycleError <= cyclePixels, $"Grid dash coverage disagrees with source segments: backing={backing}, "
                    + $"zoom={zoom}, pattern={pattern}, vertical={vertical}, top={top}, cycleError={maxCycleError}/{cyclePixels}.");
                Require(phaseError <= 1, $"Grid dash phase reset during pan: backing={backing}, zoom={zoom}, "
                    + $"pattern={pattern}, vertical={vertical}, phaseError={phaseError}.");
            }
            report["gridDashLengthsDpiZoomAndDocumentPhase"] = "passed";
        }
        finally
        {
            showsGrid = wasGrid; showPixelGrid = wasPixels; LayoutGrid = wasLayout; GridAppearance = wasAppearance;
            ResizeViewport(); if (wasFit) FitToWindow(); else SetViewportZoom(wasZoom); FlushViewport();
        }
    }
}
#endif
