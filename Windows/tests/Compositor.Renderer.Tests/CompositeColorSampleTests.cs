using Compositor.Renderer;
using Xunit;

namespace Compositor.Renderer.Tests;

public class CompositeColorSampleTests
{
    [Fact]
    public void SampleUnpremultipliesSrgbAndRoundsLikeOriginal()
    {
        Assert.Equal(new CompositeColor(199, 100, 0), CompositeColor.FromBgra([0, 50, 100, 128]));
        Assert.Equal(new CompositeColor(255, 255, 255), CompositeColor.FromBgra([200, 128, 129, 128]));
        Assert.Equal(new CompositeColor(0, 0, 255), CompositeColor.FromBgra([255, 0, 0, 255]));
    }

    [Fact]
    public void TransparentPixelsSupplyNoCheckerboardColor()
    {
        Assert.Null(CompositeColor.FromBgra([0, 0, 0, 0]));
    }
}
