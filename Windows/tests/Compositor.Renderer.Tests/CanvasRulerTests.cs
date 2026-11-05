using Compositor.Renderer;
using Xunit;

namespace Compositor.Renderer.Tests;

public class CanvasRulerTests
{
    [Fact]
    public void TickSpacingMatchesOriginalNiceStepTable()
    {
        Assert.Equal(100, CanvasRuler.MajorStep(1));
        Assert.Equal(200, CanvasRuler.MajorStep(.5));
        Assert.Equal(10, CanvasRuler.MajorStep(8));
        Assert.Equal(50000, CanvasRuler.MajorStep(.001));
    }

    [Fact]
    public void NegativeTicksAndMidpointsStayAlignedWithDocumentZero()
    {
        var ticks = CanvasRuler.Ticks(25, 215, 1);
        Assert.Contains(ticks, tick => tick.Value == 0 && tick.Position == 25 && tick.Length == 8);
        Assert.Contains(ticks, tick => tick.Value == 50 && tick.Position == 75 && tick.Length == 5);
        Assert.Contains(ticks, tick => tick.Value == 100 && tick.Position == 125 && tick.Length == 8);
        Assert.Contains(ticks, tick => tick.Value == -20 && tick.Position == 5 && tick.Length == 3);
    }

    [Fact]
    public void RulersUseLogicalScaleAndIncludeTheClippedEdgeTicks()
    {
        var ticks = CanvasRuler.Ticks(12, 300, .5);
        Assert.Equal(CanvasRuler.MajorStep(.5), ticks.Where(tick => tick.Length == 8).Select(tick => tick.Value).Skip(1).First());
        Assert.Contains(ticks, tick => tick.Value == 0 && tick.Position == 12);
        Assert.True(ticks[0].Position <= 0); Assert.True(ticks[^1].Position >= 300);
    }
}
