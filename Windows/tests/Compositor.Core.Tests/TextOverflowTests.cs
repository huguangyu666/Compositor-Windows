using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// Whether a paragraph box can hold what it says — the case the macOS editor marks with a plus drawn in the
/// bottom-right handle (InlineTextEditor.swift:420-433). The rule is line-granular because NSLayoutManager lays
/// out whole line fragments only, and it says nothing about ink that overhangs a line sideways.
/// </summary>
public class TextOverflowTests
{
    private static LayerTextStyle Boxed(string content, double boxWidth, double boxHeight, double size = 20) => new()
    {
        Content = content,
        FontName = "Not-A-Real-Font",
        FontSize = size,
        Red = 1,
        BoxSize = new TextBoxSize(boxWidth, boxHeight),
    };

    [Fact]
    public void PointTextHasNoBoxToOverflow()
    {
        var style = new LayerTextStyle { Content = "A line that would never fit the width of anything", FontName = "Not-A-Real-Font", FontSize = 40, Red = 1 };
        Assert.Null(style.BoxSize);
        Assert.False(TextRasterizer.TextOverflows(style));
    }

    [Fact]
    public void ABoxTallEnoughForItsLinesHoldsThemAndOneLineShorterDoesNot()
    {
        var twoLines = Boxed("one\ntwo", 400, 400);
        double line = twoLines.LineHeight;
        // The box's own height is what holds the lines plus the padding on both sides; taking exactly one
        // line away has to be the difference between a mark and no mark.
        var exact = Boxed("one\ntwo", 400, Math.Ceiling(2 * line + 2 * LayerTextStyle.Padding));
        var shortByOneLine = Boxed("one\ntwo", 400, Math.Ceiling(2 * line + 2 * LayerTextStyle.Padding) - Math.Ceiling(line));

        Assert.False(TextRasterizer.TextOverflows(exact));
        Assert.True(TextRasterizer.TextOverflows(shortByOneLine));
    }

    [Fact]
    public void InkThatOverhangsTheBoxSidewaysIsNotWhatTheMarkMeans()
    {
        // One line, a word longer than the box: the port paints it past the edge, as AppKit's line fragments
        // do, and the original's plus is about glyphs that were never laid out at all — a vertical failure.
        Assert.False(TextRasterizer.TextOverflows(Boxed("Supercalifragilisticexpialidocious", 40, 200)));
    }

    [Fact]
    public void AStyleTheDocumentWouldRefuseIsAskedAsTheDocumentRefusesIt()
    {
        var invalid = new LayerTextStyle { Content = "x", FontName = "Not-A-Real-Font", FontSize = 0, Red = 1,
            BoxSize = new TextBoxSize(40, 40) };
        Assert.False(invalid.IsValid);
        Assert.Throws<ProjectFormatException>(() => TextRasterizer.TextOverflows(invalid));
    }
}
