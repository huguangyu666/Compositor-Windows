using Compositor.Core;
using Xunit;

namespace Compositor.Renderer.Tests;

public sealed class RenderInputQueueTests
{
    [Fact]
    public void InputWaitsForReaderAndDrainsInOrderBeforeNextFrame()
    {
        var queue = new RenderInputQueue();
        var owner = new object();
        var events = new List<string>();
        Assert.True(queue.TryBeginFrame(owner));
        queue.StartRender();
        foreach (string value in new[] { "begin", "point1", "point2", "end", "undo" })
            queue.Enqueue(owner, () => events.Add(value));
        Assert.False(queue.TryBeginFrame(owner));
        Assert.Empty(events);
        queue.EndFrame();
        Assert.True(queue.TryBeginFrame(owner));
        Assert.Equal(new[] { "begin", "point1", "point2", "end", "undo" }, events);
        Assert.False(queue.HasPending);
        queue.EndFrame();
    }

    [Fact]
    public void ApplyingInputBlocksReentrantPumpAndIncludesNewInput()
    {
        var queue = new RenderInputQueue();
        var owner = new object();
        int count = 0;
        queue.Enqueue(owner, () =>
        {
            Assert.True(queue.IsApplying);
            Assert.False(queue.TryBeginFrame(owner));
            queue.Enqueue(owner, () => count++);
            count++;
        });
        Assert.True(queue.TryBeginFrame(owner));
        Assert.Equal(2, count);
        queue.StartRender();
        Assert.False(queue.IsApplying);
        queue.EndFrame();
    }

    [Fact]
    public void ReplacingDocumentDiscardsOldInputWithoutUnlockingReader()
    {
        var queue = new RenderInputQueue();
        var oldOwner = new object();
        var newOwner = new object();
        int count = 0;
        Assert.True(queue.TryBeginFrame(oldOwner));
        queue.StartRender();
        queue.Enqueue(oldOwner, () => throw new Exception("Stale input"));
        queue.Clear();
        queue.Enqueue(oldOwner, () => throw new Exception("Wrong owner"));
        queue.Enqueue(newOwner, () => count++);
        Assert.True(queue.IsBusy);
        queue.EndFrame();
        Assert.True(queue.TryBeginFrame(newOwner));
        Assert.Equal(1, count);
        queue.EndFrame();
    }

    [Fact]
    public void StrokeAndUndoCannotMutateSurfaceWhileFrameReadsIt()
    {
        var queue = new RenderInputQueue();
        var document = EditorDocument.Create(64, 64);
        var layer = document.Snapshot.Manifest.Layers[0];
        var key = new object();
        var before = document.Snapshot.Images[layer.Id].Pixels.ToArray();
        Assert.True(queue.TryBeginFrame(document));
        queue.StartRender();
        queue.Enqueue(document, () => Assert.True(document.BeginStroke(key, layer.Id,
            new BrushSettings(4, 1, 1, 200, 50, 20), new PointD(10, 10))));
        queue.Enqueue(document, () => document.ContinueStroke(key, new PointD(40, 40)));
        queue.Enqueue(document, () => document.EndStroke(key));
        queue.Enqueue(document, () => Assert.True(document.Undo()));
        Assert.Equal(before, document.Snapshot.Images[layer.Id].Pixels);
        queue.EndFrame();
        Assert.True(queue.TryBeginFrame(document));
        Assert.Equal(before, document.Snapshot.Images[layer.Id].Pixels);
        Assert.True(document.Redo());
        Assert.NotEqual(before, document.Snapshot.Images[layer.Id].Pixels);
        queue.EndFrame();
    }
}
