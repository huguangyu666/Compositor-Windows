using Xunit;

namespace Compositor.Core.Tests;

public class SurfaceChangeTests
{
    [Fact]
    public void KnownChangesCoalesceAcrossSkippedFramesAndHonorStride()
    {
        var image = new Surface(100, 80, new byte[416 * 80], 416);
        image.MarkModified(new(20, 10, 3, 5)); image.MarkModified(new(-2, 15, 10, 6));
        Assert.True(image.TryGetChangesSince(0, out var dirty));
        Assert.Equal(new RectI(0, 10, 23, 11), dirty);
        Assert.True(image.TryGetChangesSince(2, out dirty)); Assert.Equal(0, dirty.Width);
        Assert.Equal(0, image.Clone().Revision);
    }

    [Fact]
    public void UnknownOrExpiredChangesRequireFullRender()
    {
        var image = new Surface(100, 80);
        image.MarkModified(new(20, 10, 3, 5)); image.MarkModified(); image.MarkModified(new(1, 1, 1, 1));
        Assert.False(image.TryGetChangesSince(0, out _)); Assert.False(image.TryGetChangesSince(1, out _));
        Assert.True(image.TryGetChangesSince(2, out var dirty)); Assert.Equal(new RectI(1, 1, 1, 1), dirty);
        for (int i = 0; i < 65; i++) image.MarkModified(new(1, 1, 1, 1));
        Assert.False(image.TryGetChangesSince(2, out _)); Assert.False(image.TryGetChangesSince(-1, out _));
        Assert.False(image.TryGetChangesSince(image.Revision + 1, out _));
    }
}
