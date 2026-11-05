using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;

namespace Compositor.App;

/// <summary>
/// ASCII dither's character cells: every distinct character drawn into a monospaced cell of its own and
/// sorted from least ink to most, so a tone picks the glyph that covers as much of the cell as it needs
/// (the Mac lays the same out in Document/Dither.swift:195-219 with Core Text at
/// <c>monospacedSystemFont(ofSize: lineHeight / 1.2, weight: .bold)</c>). The engine only receives bitmaps,
/// so this is the one place in the Windows port that knows a font exists.
/// </summary>
internal static class GlyphRasterizer
{
    /// Consolas is the shipped monospaced face, and the Mac's own comment says a character is about six
    /// tenths as wide as it is tall (Dither.swift:64) — the width still comes out of the measured advance.
    public const string FontFamily = "Consolas";

    /// Characters are taken in the order they are written, newlines dropped and the run capped at 64,
    /// exactly as <c>DitherSettings.normalized</c> (Dither.swift:107) and the drawing loop do it.
    public const int MaximumCharacters = 64;

    /// The ramp the Mac starts with, and falls back to when the field is emptied (Dither.swift:57 and 172).
    public const string DefaultCharacters = " .:-=+*#%@";

    public static string UsableCharacters(string? text) =>
        string.Concat((text ?? "").Where(c => c != '\n' && c != '\r').Take(MaximumCharacters));

    public static (byte[] Maps, float[] Coverage, int Width, int Height) Render(string characters, int lineHeight)
    {
        int height = Math.Max(1, lineHeight);
        var device = CanvasDevice.GetSharedDevice();
        var format = new CanvasTextFormat
        {
            FontFamily = FontFamily,
            FontSize = height / 1.2f,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            WordWrapping = CanvasWordWrapping.NoWrap,
        };
        // The Mac measures the cell against an "M", the widest thing a monospaced face has to hold, and
        // centers each character's own advance inside it — one advance wide, so that offset is zero here.
        double advance, boxHeight;
        using (var measured = new CanvasTextLayout(device, "M", format, 1000, 1000))
        {
            advance = measured.LayoutBounds.Width;
            boxHeight = measured.LayoutBounds.Height;
        }
        int width = Math.Max(1, (int)Math.Round(advance, MidpointRounding.AwayFromZero));
        // One origin for every character, so all of them share a baseline the way a terminal's line does:
        // the block of text is centered in the cell and then drawn at the same spot each time. The Mac
        // centers on the font's ascender and descender; Win2D publishes no ascender, so the measured
        // line box stands in for it — the same centering, off the same layout.
        int originX = (int)Math.Round((width - advance) / 2, MidpointRounding.AwayFromZero);
        int originY = Math.Max(0, (int)Math.Round((height - boxHeight) / 2, MidpointRounding.AwayFromZero));

        var drawn = new List<(byte[] Map, float Coverage)>();
        var seen = new HashSet<char>();
        foreach (char character in UsableCharacters(characters))
        {
            if (!seen.Add(character)) continue;
            using var cell = new CanvasRenderTarget(device, width, height, 96);
            using (var session = cell.CreateDrawingSession())
            {
                // White on a transparent target: the alpha byte is the ink, which is what the gray
                // Core Graphics bitmap holds for the Mac.
                session.DrawText(character.ToString(), originX, originY, Microsoft.UI.Colors.White, format);
            }
            var bytes = cell.GetPixelBytes();
            var map = new byte[width * height];
            long ink = 0;
            for (int pixel = 0; pixel < map.Length; pixel++)
            {
                byte value = bytes[pixel * 4 + 3];
                map[pixel] = value;
                ink += value;
            }
            drawn.Add((map, (float)ink / (255L * width * height)));
        }
        drawn = drawn.OrderBy(entry => entry.Coverage).ToList();     // least ink first, as the Mac sorts
        var coverage = new float[drawn.Count];
        var blob = new byte[drawn.Count * width * height];
        for (int index = 0; index < drawn.Count; index++)
        {
            Buffer.BlockCopy(drawn[index].Map, 0, blob, index * width * height, width * height);
            coverage[index] = drawn[index].Coverage;
        }
        return (blob, coverage, width, height);
    }
}
