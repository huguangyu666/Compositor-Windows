using Xunit;

namespace Compositor.Core.Tests;

public class TextRasterizerTests
{
    /// <summary>A face no machine has, so every test runs in GenericSansSerif and no assertion depends
    /// on some system font being installed — the rasterizer's own graceful fallback.</summary>
    private const string FallbackFont = "Not-A-Real-Font";

    private static LayerTextStyle Style(string content, double fontSize) => new()
    {
        Content = content,
        FontName = FallbackFont,
        FontSize = fontSize,
        Red = 1,
        Green = 0,
        Blue = 0,
    };

    private static byte AlphaAt(Surface surface, int x, int y) => surface.Pixels[y * surface.Stride + x * 4 + 3];

    /// <summary>The rows and columns that hold any ink, or (-1, -1, -1, -1) when there is none.</summary>
    private static (int Left, int Top, int Right, int Bottom) InkBounds(Surface surface)
    {
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        for (int y = 0; y < surface.Height; y++)
            for (int x = 0; x < surface.Width; x++)
                if (AlphaAt(surface, x, y) > 0)
                {
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x);
                    bottom = Math.Max(bottom, y);
                }
        return (left, top, right, bottom);
    }

    private static bool AllTransparent(Surface surface)
    {
        for (int i = 3; i < surface.Pixels.Length; i += 4)
            if (surface.Pixels[i] != 0) return false;
        return true;
    }

    [Fact]
    public void PointTextPaintsSolidGlyphsInsideItsPadding()
    {
        var style = Style("Hg", 60);
        var box = TextRasterizer.TextBoxSize(style);

        var surface = TextRasterizer.Rasterize(style);

        Assert.Equal((int)Math.Ceiling(box.Width), surface.Width);
        Assert.Equal((int)Math.Ceiling(box.Height), surface.Height);
        var (left, top, right, bottom) = InkBounds(surface);
        // The ink lands within the padded box, never nearer an edge than most of the padding.
        Assert.True(right >= left, "no ink at all");
        Assert.True(left >= 8 && top >= 8, $"ink too close to the top-left: ({left}, {top})");
        Assert.True(right <= surface.Width - 9 && bottom <= surface.Height - 9,
            $"ink too close to the bottom-right: ({right}, {bottom}) in {surface.Width}x{surface.Height}");
        // A 60-pixel glyph has a solid interior, and there it is the style's own red, straight where
        // alpha is full: premultiplied 255-alpha red is (255, 0, 0, 255).
        bool solidRed = false;
        for (int y = top; y <= bottom && !solidRed; y++)
            for (int x = left; x <= right && !solidRed; x++)
            {
                int i = y * surface.Stride + x * 4;
                if (surface.Pixels[i + 3] == 255)
                    solidRed = surface.Pixels[i] >= 250 && surface.Pixels[i + 1] <= 5 && surface.Pixels[i + 2] <= 5;
            }
        Assert.True(solidRed, "no solid interior pixel in the text's own color");
    }

    [Fact]
    public void EmptyTextRasterizesToAQuietBoxWithoutThrowing()
    {
        var style = Style("", 40);

        var box = TextRasterizer.TextBoxSize(style);
        var surface = TextRasterizer.Rasterize(style);

        Assert.True(box.Width >= 16 && box.Height >= 16, "the empty box keeps the caret's minimum");
        Assert.Equal((int)Math.Ceiling(box.Width), surface.Width);
        Assert.Equal((int)Math.Ceiling(box.Height), surface.Height);
        Assert.True(AllTransparent(surface));
    }

    [Fact]
    public void TextGrowsTallerWithTheFontSize()
    {
        var small = TextRasterizer.Rasterize(Style("Hg", 30));
        var large = TextRasterizer.Rasterize(Style("Hg", 120));

        // Auto leading is 120% of the size, so the layer box grows with it…
        Assert.True(large.Height > small.Height + 60, $"{small.Height}px vs {large.Height}px");

        // …and so does the ink itself, roughly threefold between 30 and 120 pixels.
        var (_, smallTop, _, smallBottom) = InkBounds(small);
        var (_, largeTop, _, largeBottom) = InkBounds(large);
        Assert.True(largeBottom - largeTop > (smallBottom - smallTop) * 2,
            $"ink {(smallBottom - smallTop)}px tall vs {(largeBottom - largeTop)}px");
    }

    [Fact]
    public void FixedBoxDropsWholeLinesThatDoNotFit()
    {
        // Auto leading of a 40px font is a 48px line box; the 80px box holds 56px inside, exactly one
        // line. Of the CRLF pair only the first is laid out — NSLayoutManager drops the second whole,
        // rather than drawing its top half — and the CRLF itself counts as one break.
        var style = Style("HH\r\nHH", 40);
        style.BoxSize = new TextBoxSize(150, 80);

        var surface = TextRasterizer.Rasterize(style);

        Assert.Equal(150, surface.Width);
        Assert.Equal(80, surface.Height);
        var (left, top, right, bottom) = InkBounds(surface);
        Assert.True(right >= left, "no ink at all");
        Assert.True(top >= 8 && bottom < 62,
            $"ink rows {top}..{bottom} spill past the one line box that fits (12..60)");

        // A box too shallow for even one line box lays out nothing, quietly.
        var none = Style("HH", 40);
        none.BoxSize = new TextBoxSize(150, 30);
        Assert.True(AllTransparent(TextRasterizer.Rasterize(none)));
    }

    [Fact]
    public void BoxTextWrapsWordsToANewLine()
    {
        // The box is sized halfway between what one word measures and what both do, so it surely
        // holds the first word but not the pair — wherever GenericSansSerif puts their widths.
        var one = TextRasterizer.TextBoxSize(Style("mmmm", 24));
        var both = TextRasterizer.TextBoxSize(Style("mmmm nnnn", 24));
        var style = Style("mmmm nnnn", 24);
        style.BoxSize = new TextBoxSize((one.Width + both.Width) / 2, 110);

        var surface = TextRasterizer.Rasterize(style);

        // Two 28.8px line boxes inside 110px: ink must reach both halves of the box.
        var (_, top, _, bottom) = InkBounds(surface);
        Assert.True(bottom >= 55, $"all the ink ends at row {bottom}; the second word never wrapped");
        Assert.True(top < 55, "the first word is missing");

        // The same words as point text stay on one line, well inside a single line box.
        var point = TextRasterizer.Rasterize(Style("mmmm nnnn", 24));
        var (_, pointTop, _, pointBottom) = InkBounds(point);
        Assert.True(pointBottom - pointTop < 29,
            $"point text ink spans {pointBottom - pointTop}px, more than one 28.8px line box");
    }

    [Fact]
    public void ColorRunsPaintEachLetterItsOwnColor()
    {
        var style = Style("Hg", 60);
        style.ColorRuns =
        [
            new LayerTextColorRun { Location = 0, Length = 1, Red = 1, Green = 0, Blue = 0 },
            new LayerTextColorRun { Location = 1, Length = 1, Red = 0, Green = 0, Blue = 1 },
        ];

        var surface = TextRasterizer.Rasterize(style);

        bool red = false, blue = false;
        for (int i = 0; i < surface.Pixels.Length; i += 4)
        {
            if (surface.Pixels[i + 3] < 250) continue;
            if (surface.Pixels[i] >= 250 && surface.Pixels[i + 2] <= 5) red = true;
            if (surface.Pixels[i + 2] >= 250 && surface.Pixels[i] <= 5) blue = true;
        }
        Assert.True(red, "no red pixel from the run at UTF-16 offset 0");
        Assert.True(blue, "no blue pixel from the run at UTF-16 offset 1");
    }

    [Fact]
    public void InvalidAndOversizedStylesAreRefusedLikeTheMac()
    {
        // The Mac's textImage refuses an invalid style; so does the port, with the same kind.
        var noSize = Style("Hg", 0);
        Assert.False(noSize.IsValid);
        Assert.Equal(ProjectErrorKind.Invalid,
            Assert.Throws<ProjectFormatException>(() => TextRasterizer.Rasterize(noSize)).Kind);

        // A valid style whose rasterization would not fit is too large, not invalid.
        var enormous = Style(new string('W', 60_000), 2000);
        Assert.True(enormous.IsValid);
        var error = Assert.Throws<ProjectFormatException>(() => TextRasterizer.Rasterize(enormous));
        Assert.Equal(ProjectErrorKind.TooLarge, error.Kind);
    }

    [Fact]
    public void AddTextLayerPlacesNamedPixelsAndTheStyle()
    {
        var document = EditorDocument.Create(64, 64);
        var style = Style("Hg", 20);
        double baseline = TextRasterizer.FirstBaseline(style);
        Assert.True(baseline > LayerTextStyle.Padding, "the first baseline sits below the top padding");

        var record = document.AddTextLayer(style, new PointD(6, 8));
        Assert.NotNull(record);
        Assert.Equal("Hg", record!.Name); // the content becomes the layer's name
        Assert.Equal(6, record.Transform.Origin.X);
        Assert.Equal(8, record.Transform.Origin.Y);
        Assert.Equal(style, record.Text); // the style rides in the manifest for later editing
        Assert.True(document.Snapshot.Images[record.Id].Pixels.Length > 0);

        Assert.True(document.Undo()); // one undoable step like any import
        Assert.Equal(1, document.Snapshot.Manifest.Layers.Count);
    }

    [Fact]
    public void AddTextLayerRefusesInvalidStylesWithoutAnUndoStep()
    {
        var document = EditorDocument.Create(64, 64);
        Assert.Null(document.AddTextLayer(Style("Hg", 0), new PointD(0, 0)));
        Assert.False(document.Undo()); // no phantom step
    }

    [Fact]
    public void TextLayerNamesFlattenWhitespace()
    {
        Assert.Equal("Hello world", EditorDocument.TextLayerName("Hello\tworld"));
        Assert.Equal("A B", EditorDocument.TextLayerName("A\r\n  B"));
        Assert.Equal("Text", EditorDocument.TextLayerName("   "));
        Assert.Equal(new string('w', 40), EditorDocument.TextLayerName(new string('w', 80)));
    }
}
