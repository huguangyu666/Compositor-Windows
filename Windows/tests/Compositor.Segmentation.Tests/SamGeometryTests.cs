using Compositor.Core;
using Compositor.Segmentation;
using Xunit;

public sealed class SamGeometryTests
{
    [Fact]
    public void FilledPinholesDoNotLeaveFaintTransparencyInsideTheSubject()
    {
        const int side = 256;
        var logits = Enumerable.Repeat(-10f, side * side).ToArray();
        for (int y = 30; y < 180; y++) Array.Fill(logits, 2f, y * side + 30, 150);
        logits[60 * side + 60] = -10;
        var mask = new SamGeometry(side, side).Restore(logits, side, side, true, default, [new(new(90, 90))]);
        Assert.Equal((byte)255, mask.Pixels[60 * side + 60]);
        Assert.Equal((byte)255, mask.Pixels[60 * side + 59]);
    }

    [Fact]
    public void SoftEdgesAntialiasTheBoundaryWithoutConfidenceLeakingAcrossTheCanvas()
    {
        var geometry = new SamGeometry(12, 8, stretch: true);
        var logits = Enumerable.Repeat(2f, 16).ToArray();
        Assert.All(geometry.Restore(logits, 4, 4, true, default).Pixels, value => Assert.Equal((byte)255, value));
        Array.Fill(logits, -2f);
        Assert.All(geometry.Restore(logits, 4, 4, true, default).Pixels, value => Assert.Equal((byte)0, value));
        for (int y = 0; y < 4; y++) { logits[y * 4] = logits[y * 4 + 1] = -1; logits[y * 4 + 2] = logits[y * 4 + 3] = 2; }
        var mask = geometry.Restore(logits, 4, 4, true, default);
        Assert.Equal((byte)0, mask.Pixels[4 * 12 + 4]);
        Assert.InRange(mask.Pixels[4 * 12 + 5], (byte)127, (byte)128);
        Assert.Equal((byte)255, mask.Pixels[4 * 12 + 6]);
    }

    [Fact]
    public void Sam2StretchesBothAxesAndRestoresTheFullNonSquareMask()
    {
        var geometry = new SamGeometry(2000, 1000, stretch: true);
        Assert.Equal(1024, geometry.Width);
        Assert.Equal(1024, geometry.Height);
        Assert.Equal(new PointD(512, 512), geometry.ToModel(new(1000, 500)));
        var small = new SamGeometry(4, 2, stretch: true);
        var mask = small.Restore(new float[] { 10, 10, 10, 10, -10, -10, -10, -10 }, 4, 2, false, default);
        Assert.Equal(new byte[] { 255, 255, 255, 255, 0, 0, 0, 0 }, mask.Pixels);
    }

    [Fact]
    public void KeepsNonSquareImagesAndPromptsInTheSameUnpaddedCoordinates()
    {
        var geometry = new SamGeometry(2000, 1000);
        Assert.Equal(1024, geometry.Width);
        Assert.Equal(512, geometry.Height);
        Assert.Equal(new PointD(512, 256), geometry.ToModel(new(1000, 500)));
        var image = new Surface(2, 1);
        image.Pixels[0] = image.Pixels[3] = 128;
        var rgb = SamGeometry.SampleRgb(image, 2, 1, default);
        Assert.Equal(new float[] { 255, 127, 127, 255, 255, 255 }, rgb);
    }

    [Fact]
    public void RestoresOnlyTheUnpaddedMaskAndPreservesGrayEdgeCoverage()
    {
        var geometry = new SamGeometry(4, 2);
        var logits = new float[] { 10, 10, 10, 10, -10, -10, -10, -10 };
        var mask = geometry.Restore(logits, 4, 2, false, default);
        Assert.Equal(4, mask.Width); Assert.Equal(2, mask.Height);
        Assert.All(mask.Pixels, value => Assert.Equal((byte)255, value));
        var empty = geometry.Restore(new float[8], 4, 2, true, default);
        Assert.All(empty.Pixels, value => Assert.Equal((byte)0, value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestoredObjectMaskRemovesSpecklesAndPinholesButKeepsRealGaps(bool soft)
    {
        const int side = 256;
        var logits = Enumerable.Repeat(-10f, side * side).ToArray();
        Fill(30, 30, 150, 180, 10);
        Fill(200, 80, 20, 20, 10); // A separate, substantial object is still selected.
        Fill(200, 10, 2, 2, 10);
        Fill(60, 60, 3, 3, -10);
        Fill(100, 100, 12, 12, -10);
        Fill(30, 160, 2, 2, -10); // An open notch must not be treated as a hole.

        var mask = new SamGeometry(side, side).Restore(logits, side, side, soft, default);
        Assert.Equal((byte)0, mask.Pixels[10 * side + 200]);
        Assert.Equal((byte)255, mask.Pixels[60 * side + 60]);
        Assert.True(mask.Pixels[80 * side + 200] >= 128);
        Assert.True(mask.Pixels[100 * side + 100] < 128);
        Assert.True(mask.Pixels[160 * side + 30] < 128);

        void Fill(int x, int y, int width, int height, float value)
        {
            for (int row = y; row < y + height; row++) Array.Fill(logits, value, row * side + x, width);
        }
    }

    [Fact]
    public void ExplicitPromptsProtectSmallObjectsAndExcludedHoles()
    {
        const int side = 256;
        var logits = Enumerable.Repeat(-10f, side * side).ToArray();
        for (int y = 30; y < 180; y++) Array.Fill(logits, 2f, y * side + 30, 150);
        logits[60 * side + 60] = -10;
        logits[10 * side + 200] = 10;
        logits[70 * side + 70] = -.5f;
        var points = new SelectionPoint[] { new(new(90, 90)), new(new(60, 60), false), new(new(200, 10)), new(new(70, 70), false) };
        var mask = new SamGeometry(side, side).Restore(logits, side, side, true, default, points);
        Assert.InRange(mask.Pixels[60 * side + 60], (byte)0, (byte)127);
        Assert.InRange(mask.Pixels[10 * side + 200], (byte)128, (byte)255);
        Assert.InRange(mask.Pixels[70 * side + 70], (byte)1, (byte)127);
        Assert.Equal((byte)255, mask.Pixels[90 * side + 90]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PointSelectionRetainsPromptedObjectsAndDropsSeparateBackgroundObjects(bool soft)
    {
        const int side = 256;
        var logits = Enumerable.Repeat(-10f, side * side).ToArray();
        for (int y = 30; y < 130; y++) Array.Fill(logits, 10f, y * side + 30, 100);
        for (int y = 160; y < 210; y++) Array.Fill(logits, 10f, y * side + 160, 50);
        var geometry = new SamGeometry(side, side);
        var mask = geometry.Restore(logits, side, side, soft, default, [new(new(90, 90))]);
        Assert.Equal((byte)255, mask.Pixels[90 * side + 90]);
        Assert.Equal((byte)0, mask.Pixels[180 * side + 180]);
        Assert.DoesNotContain(mask.Pixels.Skip(150 * side), value => value != 0);
        mask = geometry.Restore(logits, side, side, soft, default, [new(new(90, 90)), new(new(180, 180))]);
        Assert.Equal((byte)255, mask.Pixels[180 * side + 180]);
    }

    [Fact]
    public void CleanupKeepsTheLargestObjectWhenEveryIslandIsSmall()
    {
        const int side = 256;
        var logits = Enumerable.Repeat(-10f, side * side).ToArray();
        for (int y = 30; y < 33; y++) Array.Fill(logits, 10f, y * side + 30, 3);
        logits[60 * side + 60] = 10;
        var mask = new SamGeometry(side, side).Restore(logits, side, side, false, default);
        Assert.Equal(9, mask.Pixels.Count(value => value == 255));
        Assert.Equal((byte)255, mask.Pixels[30 * side + 30]);
        Assert.Equal((byte)0, mask.Pixels[60 * side + 60]);
        Array.Fill(logits, -10f);
        Assert.All(new SamGeometry(side, side).Restore(logits, side, side, false, default).Pixels,
            value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public void DiagonalConnectionsAndCanvasOpeningsArePreserved()
    {
        const int side = 256;
        var logits = Enumerable.Repeat(10f, side * side).ToArray();
        logits[100] = logits[side + 101] = logits[2 * side + 102] = -10;
        var mask = new SamGeometry(side, side).Restore(logits, side, side, false, default);
        Assert.Equal(3, mask.Pixels.Count(value => value == 0));

        Array.Fill(logits, -10f);
        for (int y = 30; y < 180; y++) Array.Fill(logits, 10f, y * side + 30, 150);
        logits[180 * side + 180] = 10;
        mask = new SamGeometry(side, side).Restore(logits, side, side, false, default);
        Assert.Equal((byte)255, mask.Pixels[180 * side + 180]);
    }

    [Theory]
    [InlineData(double.NaN, 2)]
    [InlineData(-1, 2)]
    [InlineData(20, 2)]
    public void RefusesPromptsOutsideTheCanvas(double x, double y)
        => Assert.Throws<ArgumentException>(() => new SamGeometry(20, 10).ToModel(new(x, y)));

    [Fact]
    public void CancelsBeforeAllocatingImageOrMaskWork()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => SamGeometry.SampleRgb(new Surface(1, 1), 1024, 1024, cancelled.Token));
        Assert.Throws<OperationCanceledException>(() => new SamGeometry(4, 2).Restore(new float[8], 4, 2, true, cancelled.Token));
    }
}
