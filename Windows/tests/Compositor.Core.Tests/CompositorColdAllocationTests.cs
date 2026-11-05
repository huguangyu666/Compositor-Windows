using Xunit;

namespace Compositor.Core.Tests;

public class CompositorColdAllocationTests
{
    [Fact]
    public void CachedColdFrameDoesNotAllocateAnUnusedFullCanvasWorkingBuffer()
    {
        new CanvasCompositor(EditorDocument.Create(16, 16).Snapshot).Render();
        var doc = EditorDocument.Create(1024, 1024);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var compositor = new CanvasCompositor(doc.Snapshot);
        var frame = compositor.Render();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        // Canvas plus placed layer; the shared fallback is not needed by this draw.
        Assert.True(allocated < frame.Pixels.LongLength * 2 + 256 * 1024,
            $"Cached cold frame allocated {allocated} bytes.");
        Assert.Equal(doc.Snapshot.Images.Values.Single().Pixels, frame.Pixels);
    }
}
