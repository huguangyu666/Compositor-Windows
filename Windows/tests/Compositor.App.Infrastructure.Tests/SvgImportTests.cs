using Compositor.App;
using System.Xml.Linq;
using Xunit;

namespace Compositor.App.Infrastructure.Tests;

public sealed class SvgImportTests
{
    [Fact]
    public void NormalizesTheViewportWithoutChangingTheArtworkCoordinates()
    {
        var xml = SvgImport.Prepare("<svg viewBox='-10 0 100 50' width='100' height='50'><path d='M0 0h20v20z' fill='#ff8800'/></svg>", 400, 200, 80000);
        var root = XDocument.Parse(xml).Root!;
        Assert.Equal("http://www.w3.org/2000/svg", root.Name.NamespaceName);
        Assert.Equal("-10 0 100 50", root.Attribute("viewBox")!.Value);
        Assert.Equal("400", root.Attribute("width")!.Value);
        Assert.Equal("200", root.Attribute("height")!.Value);
        Assert.Equal("M0 0h20v20z", root.Elements().Single().Attribute("d")!.Value);
    }

    [Fact]
    public void AcceptsGradientsClippingAndLocalReuse()
    {
        var svg = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" viewBox="0 0 100 100">
              <defs><linearGradient id="paint"><stop offset="0" stop-color="red"/><stop offset="1" stop-color="blue"/></linearGradient>
                <clipPath id="clip"><circle cx="50" cy="50" r="40"/></clipPath><path id="tile" d="M0 0h100v100z"/></defs>
              <g clip-path="url(#clip)"><use xlink:href="#tile" style="fill: url('#paint'); opacity: .5"/></g>
            </svg>
            """;
        Assert.Contains("linearGradient", SvgImport.Prepare(svg, 100, 100, 10000));
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<foreignObject/>")]
    [InlineData("<text>Use editor_text</text>")]
    [InlineData("<image href='file:///C:/private.png'/>")]
    [InlineData("<filter/>")]
    [InlineData("<style>path {fill:red}</style>")]
    [InlineData("<path d='M0 0' onclick='alert(1)'/>")]
    [InlineData("<path d='M0 0' fill='url(https://example.test/paint)'/>")]
    [InlineData("<path d='M0 0' style='fill:url(file:///C:/private.svg#paint)'/>")]
    [InlineData("<path d='M0 0' filter='url(#blur)'/>")]
    [InlineData("<use href='https://example.test/a.svg#tile'/>")]
    [InlineData("<g xmlns='urn:foreign'><path/></g>")]
    public void RefusesUnsupportedOrExternalContent(string content)
        => Assert.Throws<ArgumentException>(() => SvgImport.Prepare("<svg viewBox='0 0 10 10'>" + content + "</svg>", 10, 10, 100));

    [Theory]
    [InlineData("<!DOCTYPE svg [<!ENTITY payload 'x'>]><svg viewBox='0 0 1 1'>&payload;</svg>")]
    [InlineData("<svg viewBox='0 0 1 1'><path></svg>")]
    [InlineData("<svg viewBox='0 0 -1 1'/>")]
    [InlineData("<svg viewBox='0 0 NaN 1'/>")]
    [InlineData("<svg viewBox='0 0 1'/>")]
    [InlineData("<svg width='10' height='10'/>")]
    [InlineData("<html/>")]
    [InlineData("<svg viewBox='0 0 1 1'><path id='a'/><path id='a'/></svg>")]
    [InlineData("<svg viewBox='0 0 1 1'><use href='#missing'/></svg>")]
    [InlineData("<svg viewBox='0 0 1 1'><g id='a'><use href='#a'/></g></svg>")]
    [InlineData("<svg viewBox='0 0 1 1'><defs><linearGradient id='a' href='#b'/><linearGradient id='b' href='#a'/></defs></svg>")]
    public void RefusesMalformedDocumentsAndRecursiveReferences(string svg)
        => Assert.Throws<ArgumentException>(() => SvgImport.Prepare(svg, 10, 10, 100));

    [Fact]
    public void BoundsOutputSizeInputSizeDepthAndExpandedReferences()
    {
        const string svg = "<svg viewBox='0 0 1 1'><rect width='1' height='1'/></svg>";
        Assert.Throws<ArgumentException>(() => SvgImport.Prepare(svg, 0, 10, 100));
        Assert.Throws<ArgumentException>(() => SvgImport.Prepare(svg, 8193, 1, 100000));
        Assert.Throws<ArgumentException>(() => SvgImport.Prepare(svg, 8192, 8192, long.MaxValue));
        Assert.Throws<ArgumentException>(() => SvgImport.Prepare(svg, 10, 10, 99));
        Assert.Throws<ArgumentException>(() => SvgImport.Prepare(svg + new string(' ', 131072), 10, 10, 100));
        var deep = "<svg viewBox='0 0 1 1'>" + string.Concat(Enumerable.Repeat("<g>", 70)) + string.Concat(Enumerable.Repeat("</g>", 70)) + "</svg>";
        Assert.Throws<ArgumentException>(() => SvgImport.Prepare(deep, 10, 10, 100));
        var reuse = "<svg viewBox='0 0 1 1'><defs><rect id='a0' width='1' height='1'/>";
        for (int i = 1; i <= 20; i++) reuse += $"<g id='a{i}'><use href='#a{i - 1}'/><use href='#a{i - 1}'/></g>";
        Assert.Throws<ArgumentException>(() => SvgImport.Prepare(reuse + "</defs><use href='#a20'/></svg>", 10, 10, 100));
    }
}
