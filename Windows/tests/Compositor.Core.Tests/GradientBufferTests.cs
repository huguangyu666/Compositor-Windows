using System.Text.Json;
using Xunit;

namespace Compositor.Core.Tests;

public class GradientBufferTests
{
    public static IEnumerable<object[]> Scenarios => GradientBatchScenario.Names.Select(name => new object[] { name });

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void ReusedFramesMatchIndependentPreviewsAndKeepTheRetainedFrame(string name)
    {
        var (document, id, mask) = GradientBatchScenario.Create(name);
        var edit = document.BeginGradient(id, mask)!;
        var buffers = new GradientEdit.PreviewBuffers(edit);
        byte[] Pixels(GradientPreview preview) => mask ? preview.Snapshot.Masks[id].Pixels : preview.Snapshot.Images[id].Pixels;
        var start = new PointD(3.12, 2.43);
        var settings = GradientBatchScenario.Settings(name);
        var retained = buffers.Preview(start, new(700, 370), settings, null);
        byte[]? retainedBytes = retained is null ? null : (byte[])Pixels(retained).Clone();
        object? spare = null;
        for (int frame = 0; frame < 6; frame++)
        {
            var end = new PointD(680 - frame * 17, 355 - frame * 3);
            var expected = edit.Preview(start, end, settings);
            var actual = buffers.Preview(start, end, settings, retained);
            if (expected is null) Assert.Null(actual);
            else
            {
                Assert.NotNull(actual);
                Assert.Equal(Pixels(expected), Pixels(actual));
                object output = mask ? actual.Snapshot.Masks[id] : actual.Snapshot.Images[id];
                if (spare is not null) Assert.Same(spare, output);
                spare = output;
            }
            if (retained is not null) Assert.Equal(retainedBytes, Pixels(retained));
        }
        Assert.False(document.IsDirty);
        Assert.False(document.CanUndo);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WarmFramesDoNotAllocateAnotherPixelPlane(bool mask)
    {
        var document = EditorDocument.Create(256, 257); var id = document.Snapshot.Manifest.Layers[0].Id;
        if (mask)
        {
            document.Snapshot.Manifest.Layers[0].MaskFile = "mask.png";
            ((Dictionary<Guid, MaskSurface>)document.Snapshot.Masks)[id] = MaskSurface.Solid(true);
        }
        var edit = document.BeginGradient(id, mask)!; var buffers = new GradientEdit.PreviewBuffers(edit);
        var settings = GradientBatchScenario.Settings("linear");
        GradientPreview? retained = null;
        for (int warm = 0; warm < 3; warm++) retained = buffers.Preview(new(0, 0), new(250 - warm, 250), settings, retained);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int frame = 0; frame < 8; frame++)
            retained = buffers.Preview(new(0, 0), new(240 - frame, 250), settings, retained);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.NotNull(retained);
        Assert.True(allocated < 8 * 16_384, $"Eight warm gradient frames allocated {allocated} bytes.");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ReusingEitherSlotInvalidatesCompositorPixelsAndPreservesHistory(bool mask, bool effects)
    {
        var (document, id, _) = GradientBatchScenario.Create(mask ? "mask" : "linear", 128, 97);
        if (effects) document.Snapshot.Manifest.Layers[0].Effects = JsonSerializer.SerializeToElement(
            new LayerEffects { Shadow = new() { Blur = 3, Distance = 4, Opacity = .8 } },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var original = document.Snapshot;
        var edit = document.BeginGradient(id, mask)!; var buffers = new GradientEdit.PreviewBuffers(edit);
        var settings = GradientBatchScenario.Settings("linear");
        var compositor = new CanvasCompositor(original); compositor.Render();
        GradientPreview? retained = null;
        for (int frame = 0; frame < 6; frame++)
        {
            var end = new PointD(120 - frame * 7, 90 - frame * 3);
            var expected = edit.Preview(new(0, 0), end, settings)!;
            retained = buffers.Preview(new(0, 0), end, settings, retained)!;
            compositor.SetSnapshot(retained.Snapshot);
            Assert.Equal(new CanvasCompositor(expected.Snapshot).Render().Pixels, compositor.Render().Pixels);
        }
        var committed = retained!;
        byte[] committedBytes = (byte[])(mask ? committed.Snapshot.Masks[id].Pixels : committed.Snapshot.Images[id].Pixels).Clone();
        Assert.True(document.CommitGradient(committed));
        for (int frame = 0; frame < 3; frame++) _ = buffers.Preview(new(0, 0), new(90 - frame, 70), settings, committed);
        Assert.Equal(committedBytes, mask ? document.Snapshot.Masks[id].Pixels : document.Snapshot.Images[id].Pixels);
        Assert.True(document.Undo()); Assert.False(document.CanUndo);
        Assert.Same(original.Images[id], document.Snapshot.Images[id]);
        if (mask) Assert.Same(original.Masks[id], document.Snapshot.Masks[id]);
        Assert.True(document.Redo());
        Assert.Equal(committedBytes, mask ? document.Snapshot.Masks[id].Pixels : document.Snapshot.Images[id].Pixels);
    }
}
