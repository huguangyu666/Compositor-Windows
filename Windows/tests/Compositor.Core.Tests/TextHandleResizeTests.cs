using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// The handle and resize maths an on-canvas text editor needs, checked against numbers worked out here
/// rather than against the implementation: which edge a point belongs to, and what a drag does to the
/// paragraph box and the placement. The macOS originals are `LayerTransform.handles`
/// (Document/LayerTransform.swift:96-98), `handle(at:)` and `edgeReach`
/// (Rendering/InlineTextEditor.swift:385-408) and the body of `mouseDragged` (:460-489).
/// </summary>
public class TextHandleResizeTests
{
    /// <summary>A 2× placement: 100×50 document pixels over a 50×25 logical box, top left at (10, 20).</summary>
    private static LayerTransform Placement(double rotation = 0) => new()
    {
        Origin = new PointD(10, 20),
        Size = new SizeD(100, 50),
        Rotation = rotation,
    };

    private static readonly SizeD Logical = new(50, 25);

    [Fact]
    public void HandlesAreTheEightEdgeAndCornerPointsInMacOrder()
    {
        var expected = new[] { new PointD(0, 0), new PointD(.5, 0), new PointD(1, 0), new PointD(1, .5),
            new PointD(1, 1), new PointD(.5, 1), new PointD(0, 1), new PointD(0, .5) };
        Assert.Equal(8, LayerTransform.Handles.Count);
        for (int index = 0; index < expected.Length; index++)
            Assert.Equal(expected[index], LayerTransform.Handles[index]);
    }

    [Fact]
    public void EdgeReachIsTenSixthsOfAHandleButNeverEatsASmallBox()
    {
        // 10/6 of a 6-point handle is 10; a third of the shorter side caps it when the box is smaller.
        Assert.Equal(10, LayerTransform.EdgeReach(6, new SizeD(120, 80)));
        Assert.Equal(6, LayerTransform.EdgeReach(6, new SizeD(30, 18)));
    }

    [Fact]
    public void HandleAtAnswersWithCornerBandsEdgeBandsAndNothingForText()
    {
        var box = new SizeD(120, 80);
        double reach = 10;
        Assert.Equal(7, LayerTransform.HandleAt(new PointD(5, 40), box, reach));    // along the left edge
        Assert.Equal(1, LayerTransform.HandleAt(new PointD(60, 3), box, reach));    // along the top edge
        Assert.Equal(4, LayerTransform.HandleAt(new PointD(118, 78), box, reach));  // bottom-right corner
        Assert.Equal(3, LayerTransform.HandleAt(new PointD(128, 40), box, reach));  // just outside the right edge
        Assert.Null(LayerTransform.HandleAt(new PointD(60, 40), box, reach));       // the text itself
        Assert.Null(LayerTransform.HandleAt(new PointD(140, 40), box, reach));      // past the reach
    }

    [Fact]
    public void ARightHandleDragWidensTheBoxAndHoldsTheLeftEdge()
    {
        var old = Placement();
        // The right handle sits at unit (1, .5) → (110, 45); 20 document pixels along +x is 20 in box units too,
        // because this placement is upright. The box grows from 50 to 60 logical pixels and the placement from
        // 100 to 120, while the left edge — the edge this handle does not own — stays at x = 10.
        var resized = old.ResizeBox(3, Logical, new PointD(110, 45), new PointD(130, 45))!.Value;
        Assert.Equal(60, resized.Box.Width);
        Assert.Equal(25, resized.Box.Height);
        Assert.Equal(120, resized.Placement.Size.Width);
        Assert.Equal(50, resized.Placement.Size.Height);
        Assert.Equal(10, resized.Placement.Origin.X, 9);
        Assert.Equal(20, resized.Placement.Origin.Y, 9);
        Assert.Equal(10, resized.Placement.Point(new PointD(0, 0)).X, 9);
        Assert.Equal(20, resized.Placement.Point(new PointD(0, 0)).Y, 9);
    }

    [Fact]
    public void AQuarterTurnedLayerGrowsTheSameEdgeFromADragAlongItsOwnAxis()
    {
        var old = Placement(90);
        // Unit (1, .5) of the turned placement is (60, 95): the centre (60, 45) plus 50 along +y. Moving the
        // pointer 20 pixels down the document is 20 along this box's own +x, so the width grows exactly as the
        // upright case did — but the pinned top-left corner is now the one at (85, -5), which drags the origin
        // to (0, 30). Worked out by hand: anchor old.Point(0, 0) = centre (60, 45) + (25, -50) = (85, -5);
        // the new box's own corner is centre (70, 45) + (25, -60) = (95, -15); origin += (-10, 10).
        var resized = old.ResizeBox(3, Logical, new PointD(60, 95), new PointD(60, 115))!.Value;
        Assert.Equal(60, resized.Box.Width);
        Assert.Equal(120, resized.Placement.Size.Width, 9);
        Assert.Equal(0, resized.Placement.Origin.X, 9);
        Assert.Equal(30, resized.Placement.Origin.Y, 9);
    }

    [Fact]
    public void TheCornerTheDragDoesNotTouchStaysExactlyWhereItWas()
    {
        var old = Placement(37);
        // A bottom-left handle owns the left and bottom edges, so the top-right corner is the one that must
        // not move. The drag is written as (-20, +10) in the box's own axes and turned into a document vector
        // with the same rotation the implementation uses, so the assertion below is about the invariant, not
        // about the arithmetic repeating itself: box 60×30 logical, placement 120×60, top-right unmoved.
        double radians = 37 % 360 * Math.PI / 180, cos = Math.Cos(radians), sin = Math.Sin(radians);
        var from = old.Point(new PointD(0, 1));
        var to = new PointD(from.X + (-20 * cos - 10 * sin), from.Y + (-20 * sin + 10 * cos));
        var resized = old.ResizeBox(6, Logical, from, to)!.Value;
        Assert.Equal(60, resized.Box.Width);
        Assert.Equal(30, resized.Box.Height);
        Assert.Equal(120, resized.Placement.Size.Width, 9);
        Assert.Equal(60, resized.Placement.Size.Height, 9);
        Assert.Equal(old.Point(new PointD(1, 0)).X, resized.Placement.Point(new PointD(1, 0)).X, 9);
        Assert.Equal(old.Point(new PointD(1, 0)).Y, resized.Placement.Point(new PointD(1, 0)).Y, 9);
    }

    [Fact]
    public void AHandleCannotBeDraggedPastTheSixteenPixelFloor()
    {
        // 16 logical pixels is 32 of this placement's units; whatever further inward the pointer goes, the box
        // stops at the floor the rasterizer will not go under.
        foreach (int inward in new[] { 90, 2_000 })
        {
            var resized = Placement().ResizeBox(3, Logical, new PointD(110, 45), new PointD(110 - inward, 45))!.Value;
            Assert.Equal(16, resized.Box.Width);
            Assert.Equal(32, resized.Placement.Size.Width, 9);
        }
    }

    [Fact]
    public void ABoxOverTheSideOrAreaCeilingRefusesTheDragAltogether()
    {
        // Side: 3,000,000 logical pixels along +x. Area: 20,000×20,000 = 400 MP against the 200 MP ceiling.
        Assert.Null(Placement().ResizeBox(3, Logical, new PointD(110, 45), new PointD(110 + 6_000_000, 45)));
        Assert.Null(Placement().ResizeBox(4, Logical, new PointD(110, 70), new PointD(40_010, 40_020)));
    }

    [Fact]
    public void AnUnknownHandleOrABoxWithNothingInItResizesNothing()
    {
        Assert.Null(Placement().ResizeBox(8, Logical, new PointD(110, 45), new PointD(130, 45)));
        Assert.Null(Placement().ResizeBox(-1, Logical, new PointD(110, 45), new PointD(130, 45)));
        Assert.Null(Placement().ResizeBox(3, new SizeD(0, 25), new PointD(110, 45), new PointD(130, 45)));
        Assert.Null(new LayerTransform { Size = new SizeD(0, 50) }
            .ResizeBox(3, Logical, new PointD(0, 25), new PointD(20, 25)));
    }

    [Fact]
    public void AFlippedPlacementKeepsItsMirrorThroughAResize()
    {
        var old = Placement();
        old.FlipX = true;
        var resized = old.ResizeBox(3, Logical, new PointD(110, 45), new PointD(130, 45))!.Value;
        Assert.True(resized.Placement.FlipX);
        Assert.False(resized.Placement.FlipY);
        Assert.Equal(LayerSampling.HighQuality, resized.Placement.Sampling);
    }
}
