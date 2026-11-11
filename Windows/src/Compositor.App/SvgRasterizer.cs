// SVG parsing/drawing uses Microsoft's Win2D (MIT), already included by the editor.
// https://github.com/microsoft/Win2D — Direct2D SVG viewport and alpha are retained here.
using Compositor.Core;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Svg;
using System.Runtime.InteropServices;

namespace Compositor.App;

internal static class SvgRasterizer
{
    public static (Surface Image, LayerTransform Placement) Render(string xml, int width, int height,
        LayerTransform placement, bool trim, CancellationToken cancellation, bool forceSoftware = false)
    {
        try
        {
            try { return Draw(CanvasDevice.GetSharedDevice(forceSoftware)); }
            catch (COMException) when (!forceSoftware && !cancellation.IsCancellationRequested)
            { return Draw(CanvasDevice.GetSharedDevice(true)); }
        }
        catch (COMException ex) { throw new ArgumentException("SVG could not be rendered. Check path syntax or reduce the output dimensions.", ex); }

        (Surface, LayerTransform) Draw(CanvasDevice device)
        {
            cancellation.ThrowIfCancellationRequested();
            using var svg = CanvasSvgDocument.LoadFromXml(device, xml);
            using var target = new CanvasRenderTarget(device, width, height, 96);
            using (var drawing = target.CreateDrawingSession())
            {
                drawing.Clear(Microsoft.UI.Colors.Transparent);
                drawing.DrawSvg(svg, new Windows.Foundation.Size(width, height));
            }
            var pixels = target.GetPixelBytes();
            bool visible = false;
            for (int y = 0; y < height; y++)
            {
                cancellation.ThrowIfCancellationRequested();
                for (int p = y * width * 4; p < (y + 1) * width * 4; p += 4)
                {
                    (pixels[p], pixels[p + 2]) = (pixels[p + 2], pixels[p]);
                    visible |= pixels[p + 3] != 0;
                }
            }
            if (!visible) throw new ArgumentException("SVG produced no visible pixels. Check its viewBox, fill, opacity and placement inside the viewport.");
            var image = new Surface(width, height, pixels, width * 4);
            var result = trim ? GrownLayerGrid.Trim(image, placement) : (image, placement);
            cancellation.ThrowIfCancellationRequested();
            return result;
        }
    }
}
