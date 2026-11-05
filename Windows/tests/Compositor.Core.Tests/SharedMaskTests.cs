using Xunit;
using System.Linq;

namespace Compositor.Core.Tests;

/// <summary>
/// D09: the shared 1×1 uniform mask. A solid mask is the cheapest way to reveal or hide a whole
/// layer, and it moves between layers by identity — so an edit that writes it in place changes
/// every layer that happens to hold the same object.
/// </summary>
public class SharedMaskTests
{
    private static EditorDocument TwoLayersWithMasks(out MaskSurface shared, out Guid first, out Guid second)
    {
        var document = EditorDocument.Create(8, 8);
        var a = document.ImportImage(new Surface(4, 4), "A");
        var b = document.ImportImage(new Surface(4, 4), "B");
        shared = MaskSurface.Solid(revealing: true);
        Assert.True(document.AddRasterMask(a.Id, shared));
        Assert.True(document.AddRasterMask(b.Id, shared));
        first = a.Id; second = b.Id;
        return document;
    }

    [Fact]
    public void AUniformMaskTravelsThroughAWarpByIdentity()
    {
        // The premise, pinned on the production path rather than asserted away: a warp hands the
        // same object back for a 1×1 mask, and the distort commit assigns it to the layer directly.
        var mask = MaskSurface.Solid(revealing: true);
        var (warped, _) = DistortWarp.WarpMask(mask, new LayerTransform { Size = new SizeD(8, 8) },
            [new PointD(0, 0), new(8, 0), new(8, 8), new(0, 8)]);
        Assert.True(ReferenceEquals(mask, warped), "a 1×1 mask is no longer shared by identity, so this test's premise changed.");
    }

    [Fact]
    public void InvertingOneLayersMaskDoesNotInvertTheLayerSharingIt()
    {
        var document = TwoLayersWithMasks(out var shared, out var first, out var second);
        Assert.True(ReferenceEquals(document.Snapshot.Masks[first], shared));
        Assert.True(ReferenceEquals(document.Snapshot.Masks[second], shared));

        Assert.True(document.InvertPixels(first, invertMask: true));
        var inverted = document.Snapshot.Masks[first];
        Assert.False(ReferenceEquals(inverted, shared), "the invert wrote the shared surface in place");
        Assert.Equal(0, inverted.Pixels[0]);
        Assert.True(shared.Pixels[0] == 255, "the object the other layer still holds was rewritten");
        Assert.True(ReferenceEquals(document.Snapshot.Masks[second], shared));
        Assert.Equal(255, document.Snapshot.Masks[second].Pixels[0]);
    }

    [Fact]
    public void InvertingAndUndoingStillRoundTripsAfterTheCopy()
    {
        var document = EditorDocument.Create(8, 8);
        var layer = document.ImportImage(new Surface(4, 4), "A");
        Assert.True(document.AddRasterMask(layer.Id, MaskSurface.Solid(revealing: true)));

        Assert.True(document.InvertPixels(layer.Id, invertMask: true));
        Assert.Equal(0, document.Snapshot.Masks[layer.Id].Pixels[0]);
        Assert.True(document.InvertPixels(layer.Id, invertMask: true));
        Assert.Equal(255, document.Snapshot.Masks[layer.Id].Pixels[0]);
        Assert.True(document.Undo());
        Assert.Equal(0, document.Snapshot.Masks[layer.Id].Pixels[0]);
    }

    [Fact]
    public void FillingOneLayersMaskLeavesTheSharedObjectAlone()
    {
        var document = TwoLayersWithMasks(out var shared, out var first, out var second);

        Assert.True(document.FillMask(first, 0));
        Assert.Equal(255, shared.Pixels[0]);
        Assert.Equal(0, document.Snapshot.Masks[first].Pixels[0]);
        Assert.Equal(255, document.Snapshot.Masks[second].Pixels[0]);
    }
}
