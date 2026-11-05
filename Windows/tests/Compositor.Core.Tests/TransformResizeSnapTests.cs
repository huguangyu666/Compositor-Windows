using Xunit;

namespace Compositor.Core.Tests;

public class TransformResizeSnapTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(360)]
    [InlineData(-360)]
    public void UprightCornerSnapsBothDraggedEdgesWithoutMovingTheOppositeCorner(double rotation)
    {
        var original = new LayerTransform { Origin = new(10, 20), Size = new(100, 50), Rotation = rotation };
        var drag = new TransformDragState(original, new(110, 70), TransformDragMode.Resize, 4);
        var snap = TransformSnap.ResizePoint(drag, new(137, 94), [142], [98], 10, false, false, false);
        var value = TransformDrag.Updated(drag, snap.Point, false, false, false);
        Assert.Equal(new PointD(142, 98), snap.Point);
        Assert.Equal(new PointD(10, 20), value.Origin);
        Assert.Equal(new SizeD(132, 78), value.Size);
        Assert.Equal(142, snap.X); Assert.Equal(98, snap.Y);
    }

    [Theory]
    [InlineData(143, 89, 133, 66.5, true)]
    [InlineData(143, 86, 132, 66, false)]
    public void ProportionalResizeUsesOnlyTheNearerEdge(double x, double y, double width, double height, bool horizontal)
    {
        var original = new LayerTransform { Origin = new(10, 20), Size = new(100, 50) };
        var drag = new TransformDragState(original, new(110, 70), TransformDragMode.Resize, 4);
        var snap = TransformSnap.ResizePoint(drag, new(138, 89), [x], [y], 10, true, false, false);
        var value = TransformDrag.Updated(drag, snap.Point, true, false, false);
        Assert.Equal(width, value.Size.Width, 8); Assert.Equal(height, value.Size.Height, 8);
        Assert.Equal(horizontal ? x : null, snap.X); Assert.Equal(horizontal ? null : y, snap.Y);
        Assert.Equal(original.Origin.X, value.Origin.X, 8); Assert.Equal(original.Origin.Y, value.Origin.Y, 8);
    }

    [Fact]
    public void ShiftInvertsTheResizeSnapRatioRule()
    {
        var original = new LayerTransform { Origin = new(10, 20), Size = new(100, 50) };
        var drag = new TransformDragState(original, new(110, 70), TransformDragMode.Resize, 4);
        var free = TransformSnap.ResizePoint(drag, new(138, 89), [143], [86], 10, true, true, false);
        Assert.Equal(new PointD(143, 86), free.Point);
        var proportional = TransformSnap.ResizePoint(drag, new(138, 89), [143], [86], 10, false, true, false);
        Assert.Null(proportional.X); Assert.Equal(86, proportional.Y);
    }

    [Fact]
    public void EdgeResizeIgnoresTargetsOnItsUnchangedAxisAndPreservesCenterWithAlt()
    {
        var original = new LayerTransform { Origin = new(10, 20), Size = new(100, 50) };
        var drag = new TransformDragState(original, new(110, 45), TransformDragMode.Resize, 3);
        var snap = TransformSnap.ResizePoint(drag, new(118, 45), [120], [46], 10, false, false, true);
        var value = TransformDrag.Updated(drag, snap.Point, false, false, true);
        Assert.Equal(new PointD(120, 45), snap.Point); Assert.Null(snap.Y);
        Assert.Equal(original.Center, value.Center); Assert.Equal(new SizeD(120, 50), value.Size);
    }

    [Fact]
    public void CrossingTheOppositeCornerSnapsThePointerSideAndKeepsFlips()
    {
        var original = new LayerTransform { Origin = new(10, 20), Size = new(100, 50) };
        var drag = new TransformDragState(original, new(10, 20), TransformDragMode.Resize, 0);
        var snap = TransformSnap.ResizePoint(drag, new(150, 95), [152], [96], 10, false, false, false);
        var value = TransformDrag.Updated(drag, snap.Point, false, false, false);
        Assert.Equal(new PointD(152, 96), snap.Point); Assert.Equal(new PointD(110, 70), value.Origin);
        Assert.Equal(new SizeD(42, 26), value.Size); Assert.True(value.FlipX); Assert.True(value.FlipY);
    }

    [Theory]
    [InlineData(45, TransformDragMode.Resize)]
    [InlineData(0, TransformDragMode.Move)]
    [InlineData(0, TransformDragMode.Rotate)]
    public void TurnedLayersAndOtherModesDoNotResizeSnap(double rotation, TransformDragMode mode)
    {
        var original = new LayerTransform { Origin = new(10, 20), Size = new(100, 50), Rotation = rotation };
        var drag = new TransformDragState(original, new(110, 70), mode, 4);
        var point = new PointD(137, 94);
        var snap = TransformSnap.ResizePoint(drag, point, [142], [98], 10, false, false, false);
        Assert.Equal(point, snap.Point); Assert.Null(snap.X); Assert.Null(snap.Y);
    }

    [Fact]
    public void TargetsOutsideToleranceDoNotPullAndTiesUseTheFirstTarget()
    {
        var original = new LayerTransform { Origin = new(10, 20), Size = new(100, 50) };
        var drag = new TransformDragState(original, new(110, 70), TransformDragMode.Resize, 4);
        var snap = TransformSnap.ResizePoint(drag, new(140, 94), [130, 150], [105], 10, false, false, false);
        Assert.Equal(new PointD(130, 94), snap.Point); Assert.Equal(130, snap.X); Assert.Null(snap.Y);
    }

    [Fact]
    public void MinimumSizeClampDoesNotDivideByAnEdgeThatStoppedFollowingThePointer()
    {
        var original = new LayerTransform { Origin = new(10, 20), Size = new(100, 50) };
        var drag = new TransformDragState(original, new(110, 70), TransformDragMode.Resize, 4);
        var snap = TransformSnap.ResizePoint(drag, new(10.2, 70), [11.5], [], 10, false, false, false);
        Assert.True(double.IsFinite(snap.Point.X));
        Assert.True(TransformDrag.Updated(drag, snap.Point, false, false, false).IsValid);
    }
}
