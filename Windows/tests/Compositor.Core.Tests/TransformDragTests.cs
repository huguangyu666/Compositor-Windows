using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// Locks the ported TransformDrag and TransformSnap semantics against the macOS
/// LayerTransform.swift source, one behavior per test.
/// </summary>
public class TransformDragTests
{
    private static LayerTransform Box(double x = 10, double y = 20, double w = 100, double h = 50, double rotation = 0) => new()
    {
        Origin = new PointD(x, y),
        Size = new SizeD(w, h),
        Rotation = rotation,
    };

    private static TransformDragState Drag(LayerTransform original, PointD start, TransformDragMode mode, int index = 0) =>
        new(original, start, mode, index);

    // --- Move ---

    [Fact]
    public void MoveFollowsThePointer()
    {
        var updated = TransformDrag.Updated(Drag(Box(), new PointD(30, 40), TransformDragMode.Move), new PointD(55, 51), lockRatio: false, shift: false, option: false);
        Assert.Equal(35, updated.Origin.X);
        Assert.Equal(31, updated.Origin.Y);
    }

    [Fact]
    public void MoveShiftLocksToTheDominantAxis()
    {
        var updated = TransformDrag.Updated(Drag(Box(), new PointD(30, 40), TransformDragMode.Move), new PointD(55, 51), lockRatio: false, shift: true, option: false);
        Assert.Equal(35, updated.Origin.X); // dx=25 wins over dy=11
        Assert.Equal(20, updated.Origin.Y); // unchanged
    }

    // --- Resize ---

    [Fact]
    public void CornerResizeUsesTheOppositeCornerAsAnchor()
    {
        // Dragging handle 4 (bottom-right) of a box at (10,20): the anchor is the top-left
        // corner, which must not move.
        var original = Box();
        var updated = TransformDrag.Updated(Drag(original, new PointD(110, 70), TransformDragMode.Resize, 4), new PointD(140, 90), lockRatio: false, shift: false, option: false);
        Assert.Equal(10, updated.Origin.X);
        Assert.Equal(20, updated.Origin.Y);
        Assert.Equal(130, updated.Size.Width);
        Assert.Equal(70, updated.Size.Height);
    }

    [Fact]
    public void EdgeResizeChangesOneAxisOnly()
    {
        // Handle 1 is the top-middle: only the height changes, and the bottom edge stays put.
        var original = Box();
        var updated = TransformDrag.Updated(Drag(original, new PointD(60, 20), TransformDragMode.Resize, 1), new PointD(60, 5), lockRatio: false, shift: false, option: false);
        Assert.Equal(100, updated.Size.Width);
        Assert.Equal(65, updated.Size.Height);
        Assert.Equal(70, updated.Origin.Y + updated.Size.Height); // bottom edge fixed at y=70
    }

    [Fact]
    public void OptionResizeAnchorsAtTheCenter()
    {
        var original = Box(x: 0, y: 0, w: 100, h: 100);
        var updated = TransformDrag.Updated(Drag(original, new PointD(100, 50), TransformDragMode.Resize, 3), new PointD(130, 50), lockRatio: false, shift: false, option: true);
        Assert.Equal(50, updated.Center.X); // center stays at (50, 50)
        Assert.Equal(50, updated.Center.Y);
        Assert.Equal(160, updated.Size.Width); // 130 − (−30): both sides move out
    }

    [Fact]
    public void DraggingPastTheOppositeSideFlips()
    {
        // Handle 0 (top-left) dragged far past the bottom-right: the layer flips on both axes.
        var original = Box(x: 0, y: 0, w: 100, h: 50);
        var updated = TransformDrag.Updated(Drag(original, new PointD(0, 0), TransformDragMode.Resize, 0), new PointD(220, 160), lockRatio: false, shift: false, option: false);
        Assert.True(updated.FlipX);
        Assert.True(updated.FlipY);
        // The anchor corner (100,50) holds; the box lands 120x110 on the far side of it.
        Assert.Equal(120, updated.Size.Width);
        Assert.Equal(110, updated.Size.Height);
        Assert.Equal(100, updated.Origin.X, 1); // the box's origin lands on the anchor
        Assert.Equal(50, updated.Origin.Y, 1);
    }

    [Fact]
    public void LockRatioScalesFromTheDiagonal()
    {
        var original = Box(x: 0, y: 0, w: 100, h: 50);
        var updated = TransformDrag.Updated(Drag(original, new PointD(100, 50), TransformDragMode.Resize, 4), new PointD(200, 100), lockRatio: true, shift: false, option: false);
        // The projection doubles the diagonal: 200×100.
        Assert.Equal(200, updated.Size.Width);
        Assert.Equal(100, updated.Size.Height);
    }

    [Fact]
    public void ShiftOnAResizeInvertsTheRatioLock()
    {
        // Default is free; with Shift the corner drag keeps the aspect.
        var original = Box(x: 0, y: 0, w: 100, h: 50);
        var updated = TransformDrag.Updated(Drag(original, new PointD(100, 50), TransformDragMode.Resize, 4), new PointD(200, 100), lockRatio: false, shift: true, option: false);
        Assert.Equal(200, updated.Size.Width);
        Assert.Equal(100, updated.Size.Height);
    }

    [Fact]
    public void RotatedResizeMeasuresInTheLayerFrame()
    {
        // A 90°-rotated box: dragging the top-middle handle outward changes the height
        // (which renders horizontally) and keeps the center row exact.
        var original = Box(x: 0, y: 0, w: 100, h: 50, rotation: 90);
        var updated = TransformDrag.Updated(Drag(original, new PointD(75, 25), TransformDragMode.Resize, 1), new PointD(115, 25), lockRatio: false, shift: false, option: false);
        Assert.Equal(90, updated.Size.Height, 1); // the dragged axis: 50 + 40
        Assert.Equal(100, updated.Size.Width, 1); // the other axis untouched
    }

    // --- Rotate ---

    [Fact]
    public void RotateFollowsThePointerAngle()
    {
        var original = Box(x: 0, y: 0, w: 100, h: 100);
        var updated = TransformDrag.Updated(Drag(original, new PointD(150, 50), TransformDragMode.Rotate), new PointD(50, 150), lockRatio: false, shift: false, option: false);
        Assert.Equal(90, updated.Rotation, 1);
    }

    [Fact]
    public void RotateShiftSnapsToFifteenDegrees()
    {
        var original = Box(x: 0, y: 0, w: 100, h: 100);
        var updated = TransformDrag.Updated(Drag(original, new PointD(150, 50), TransformDragMode.Rotate), new PointD(140, 95), lockRatio: false, shift: true, option: false);
        Assert.Equal(0, updated.Rotation % 15, 3);
    }

    [Theory]
    [InlineData(7.5, 15)]
    [InlineData(-7.5, -15)]
    [InlineData(37.5, 45)]
    public void RotateShiftRoundsMidpointsAwayFromZeroAsTheSourceDoes(double rotation, double expected)
    {
        var original = Box(x: 0, y: 0, w: 100, h: 100, rotation: rotation);
        var point = new PointD(150, 50);
        var updated = TransformDrag.Updated(Drag(original, point, TransformDragMode.Rotate), point, false, true, false);
        Assert.Equal(expected, updated.Rotation);
    }

    // --- Handles and hit-testing ---

    [Fact]
    public void HandlesLandOnTheBoxCornersAndEdges()
    {
        var handles = TransformDrag.HandlePositions(Box(x: 0, y: 0, w: 100, h: 50));
        Assert.Equal(new PointD(0, 0), handles[0]);
        Assert.Equal(new PointD(50, 0), handles[1]);
        Assert.Equal(new PointD(100, 50), handles[4]);
        Assert.Equal(new PointD(0, 50), handles[6]);
    }

    [Fact]
    public void RotationHandleSitsAboveTheTopMiddle()
    {
        var rotation = TransformDrag.RotationHandle(Box(x: 0, y: 0, w: 100, h: 50));
        Assert.Equal(50, rotation.X, 3);
        Assert.Equal(-28, rotation.Y, 3);
    }

    [Fact]
    public void HitTestPrefersTheRotationHandle()
    {
        var box = Box(x: 0, y: 0, w: 100, h: 50);
        var rotation = TransformDrag.RotationHandle(box);
        var hit = TransformDrag.HitTest(box, new PointD(rotation.X + 5, rotation.Y), reach: 10);
        Assert.Equal(TransformDragMode.Rotate, hit!.Value.Mode);
    }

    [Fact]
    public void HitTestFindsHandlesAndEdges()
    {
        var box = Box(x: 0, y: 0, w: 100, h: 50);
        Assert.Equal((TransformDragMode.Resize, 4), TransformDrag.HitTest(box, new PointD(100, 50), reach: 10)!.Value);
        // The middle of the bottom edge hits handle 5 without touching handle 4 or 6.
        Assert.Equal((TransformDragMode.Resize, 5), TransformDrag.HitTest(box, new PointD(50, 50), reach: 10)!.Value);
        Assert.Null(TransformDrag.HitTest(box, new PointD(50, 25), reach: 10)); // the interior is the body, not a handle
    }

    // --- Snap ---

    [Fact]
    public void SnapMovesToTheNearestTargetWithinTolerance()
    {
        var (offset, x, y) = TransformSnap.OffsetFor(
            new RectD(8, 18, 100, 50),
            xs: [0, 10, 100], ys: [0, 20, 70],
            tolerance: 5);
        Assert.Equal(2, offset.Width); // box left 8 → target 10
        Assert.Equal(10, x);
        Assert.Equal(20, y); // box top 18 → target 20
        Assert.Equal(2, offset.Height);
    }

    [Fact]
    public void SnapIgnoresTargetsBeyondTolerance()
    {
        var (offset, x, y) = TransformSnap.OffsetFor(
            new RectD(8, 18, 100, 50),
            xs: [0, 100], ys: [75],
            tolerance: 5);
        Assert.Equal(0, offset.Width);
        Assert.Equal(0, offset.Height);
        Assert.Null(x);
        Assert.Null(y);
    }

    [Fact]
    public void SnapPicksTheSmallestMovePerAxis()
    {
        // Box left is 2 from 10 and 3 from 0: the nearer target wins.
        var (offset, x, _) = TransformSnap.OffsetFor(
            new RectD(8, 0, 100, 50),
            xs: [0, 10], ys: Array.Empty<double>(),
            tolerance: 5);
        Assert.Equal(2, offset.Width);
        Assert.Equal(10, x);
    }

    [Fact]
    public void InvalidResultsFallBackToTheOriginal()
    {
        // A resize that would collapse the box to nothing keeps the original instead.
        var original = Box();
        var state = Drag(original, new PointD(60, 20), TransformDragMode.Resize, 1);
        // A NaN pointer: the math goes NaN, IsValid fails, the original comes back.
        var updated = TransformDrag.Updated(state, new PointD(double.NaN, 5), lockRatio: false, shift: false, option: false);
        Assert.Equal(original, updated);
    }
}
