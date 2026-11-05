using Compositor.Core;
using Compositor.Renderer;
using Xunit;

namespace Compositor.Renderer.Tests;

public class CanvasViewportTests
{
    [Fact]
    public void ActualPixelsAndRoundTripsWorkAcrossDisplayScales()
    {
        foreach (double dpi in new[] { 1.0, 1.5, 2.0 })
        {
            var view = new CanvasViewport(1000, 800);
            view.Resize(700, 500, dpi); view.SetZoom(1, new(350, 250));
            Assert.Equal(1 / dpi, view.PointsPerPixel, 10);
            var point = new PointD(123.25, 78.75);
            var recovered = view.ToDocument(view.ToView(point));
            Assert.Equal(point.X, recovered.X, 9); Assert.Equal(point.Y, recovered.Y, 9);
        }
    }

    [Fact]
    public void FitUsesOriginalInsetAndTracksResizing()
    {
        var view = new CanvasViewport(1000, 500);
        view.Resize(800, 600, 2);
        Assert.Equal(1.408, view.Zoom, 10);
        Assert.Equal(new PointD(48, 124), view.ToView(new(0, 0)));
        view.Resize(500, 400, 2);
        Assert.Equal(.808, view.Zoom, 10);
        Assert.True(view.FollowsFit);
    }

    [Fact]
    public void ZoomKeepsThePixelUnderTheAnchorAtEveryTestedScale()
    {
        var view = new CanvasViewport(1000, 800); view.Resize(700, 500, 1.5);
        view.Pan(new(90, -70));
        var anchor = new PointD(48, 325); var before = view.ToDocument(anchor);
        foreach (double zoom in new[] { .001, .5, 1.0, 8.0, 32.0 })
        {
            view.SetZoom(zoom, anchor); var after = view.ToDocument(anchor);
            Assert.Equal(before.X, after.X, 7); Assert.Equal(before.Y, after.Y, 7);
        }
    }

    [Fact]
    public void PanWorksForSmallImagesAndHasNoViewportBounds()
    {
        var view = new CanvasViewport(10, 10); view.Resize(800, 600, 2); view.SetZoom(1, new(400, 300));
        var before = view.ToView(new(0, 0)); view.Pan(new(1000, -800));
        Assert.Equal(before.X + 1000, view.ToView(new(0, 0)).X, 9);
        Assert.Equal(before.Y - 800, view.ToView(new(0, 0)).Y, 9);
        Assert.False(view.FollowsFit);
    }

    [Fact]
    public void DisplayChangesPreserveTheCenterDocumentPoint()
    {
        var view = new CanvasViewport(1000, 800); view.Resize(800, 600, 1); view.SetZoom(2, new(80, 90));
        var before = view.ToDocument(new(400, 300)); view.Resize(1000, 700, 2);
        var after = view.ToDocument(new(500, 350));
        Assert.Equal(before.X, after.X, 9); Assert.Equal(before.Y, after.Y, 9);
    }

    [Fact]
    public void ZoomClampsAtOriginalLimitsAndRejectsNonfiniteValues()
    {
        var view = new CanvasViewport(100, 100); view.Resize(800, 600, 1);
        view.SetZoom(0, new(0, 0)); Assert.Equal(.001, view.Zoom);
        view.SetZoom(100, new(0, 0)); Assert.Equal(32, view.Zoom);
        view.SetZoom(double.NaN, new(0, 0)); Assert.Equal(32, view.Zoom);
        view.SetZoom(double.PositiveInfinity, new(0, 0)); Assert.Equal(32, view.Zoom);
    }

    [Fact]
    public void KeyboardStepsUseOriginalLadderWithoutMovingBeyondItsEnds()
    {
        var view = new CanvasViewport(100, 100); view.Resize(800, 600, 1); view.SetZoom(1, new(0, 0));
        Assert.Equal(1.25, view.KeyboardZoomTarget(1)); Assert.Equal(2.0 / 3.0, view.KeyboardZoomTarget(-1));
        view.SetZoom(16, new(0, 0)); Assert.Equal(16, view.KeyboardZoomTarget(1));
        view.SetZoom(.125, new(0, 0)); Assert.Equal(.125, view.KeyboardZoomTarget(-1));
    }
}
