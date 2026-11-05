using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// Locks the ported HueBand geometry (HueSaturation.swift centered/include/normalize)
/// and the per-range weight inversion.
/// </summary>
public class HueBandTests
{
    // Photoshop's starting Reds band: falloff 315→345, range 345→15, falloff 15→45.
    private static HueBand Reds() => ColorRangeDefaults.Band(ColorRange.Reds);

    [Fact]
    public void CenteredOnMovesTheWholeBandToTheHue()
    {
        var centered = Reds().CenteredOn(180);

        // Core width is preserved (range 345→15 through 0 = 30°).
        Assert.Equal(30, HueBand.Forward(centered.RangeStart, centered.RangeEnd), 3);
        // Leading shoulder preserved too (falloffStart→rangeStart = 30°).
        Assert.Equal(30, HueBand.Forward(centered.FalloffStart, centered.RangeStart), 3);
        // The plateau is centered on the hue.
        double mid = HueBand.Forward(centered.RangeStart, (centered.RangeStart + centered.RangeEnd) / 2 % 360);
        Assert.Equal(15, mid, 1); // half the core width from rangeStart to the middle
    }

    [Fact]
    public void CenteredOnWrapsAroundZero()
    {
        var centered = Reds().CenteredOn(0);

        // A band centered on 0 spans e.g. 345→15 across the wrap.
        Assert.True(centered.RangeStart > centered.RangeEnd, "wraparound band: rangeStart > rangeEnd");
        Assert.Equal(30, HueBand.Forward(centered.RangeStart, centered.RangeEnd), 3);
    }

    [Fact]
    public void IncludeWidensTheNearerEdge()
    {
        var band = Reds(); // range 345→15, falloffs 30° each side

        // Hue 60 is just past the falloffEnd (45): the trailing edge moves to include it.
        var widened = band.Including(60);

        Assert.Equal(1, widened.WeightOf(60), 3); // now fully covered
        // The opposite edge is untouched.
        Assert.Equal(band.FalloffStart, widened.FalloffStart);
        Assert.Equal(band.RangeStart, widened.RangeStart);
    }

    [Fact]
    public void IncludeAlreadyCoveredHueIsANoOp()
    {
        var band = Reds();

        var same = band.Including(0); // 0 is inside the range 345→15 through wrap

        Assert.Equal(band.FalloffStart, same.FalloffStart);
        Assert.Equal(band.RangeStart, same.RangeStart);
        Assert.Equal(band.RangeEnd, same.RangeEnd);
        Assert.Equal(band.FalloffEnd, same.FalloffEnd);
    }

    [Fact]
    public void NormalizedCapsTheBandUnderAFullCircle()
    {
        var runaway = new HueBand { FalloffStart = 0, RangeStart = 0, RangeEnd = 0, FalloffEnd = 359 };

        var normalized = runaway.Normalized();

        Assert.True(HueBand.Forward(normalized.FalloffStart, normalized.FalloffEnd) <= 350);
    }

    [Fact]
    public void InvertRangeInvertsTheWeight()
    {
        var settings = new HueSaturationSettings
        {
            Range = ColorRange.Reds,
            InvertRange = true,
            Bands = new Dictionary<string, HueBand>
            {
                ["Reds"] = ColorRangeDefaults.Band(ColorRange.Reds),
            },
        };

        // Pure red (0°): weight 1 without inversion → 0 with; a green hue flips the other way.
        double red = settings.WeightOf(ColorRange.Reds, 0);
        double green = settings.WeightOf(ColorRange.Reds, 120);

        Assert.Equal(0, red, 3);
        Assert.Equal(1, green, 3);
    }
}


public class HueBandHandleTests
{
    private static HueBand Reds() => ColorRangeDefaults.Band(ColorRange.Reds);

    [Fact]
    public void MovingRangeStartKeepsOrder()
    {
        // Reds: falloff 315, range 345→15 (through wrap), falloff 15.
        var moved = Reds().WithHandle(1, 0); // rangeStart → 0

        Assert.Equal(0, moved.RangeStart, 3);
        Assert.Equal(15, moved.RangeEnd, 3);
        // falloffStart stays behind rangeStart in forward distance.
        double shoulder = HueBand.Forward(moved.FalloffStart, moved.RangeStart);
        Assert.InRange(shoulder, 1, 350);
    }

    [Fact]
    public void InvalidGeometryIsRejected()
    {
        var band = Reds();
        // rangeStart (345) dragged past rangeEnd+wrap → crossing the range end is rejected.
        var rejected = band.WithHandle(1, 20); // forward(345, 20) = 35 → beyond rangeEnd 15

        Assert.Equal(band, rejected);
    }

    [Fact]
    public void FalloffEndCannotPassTheRangeEnd()
    {
        var band = Reds();
        var moved = band.WithHandle(3, 10); // falloffEnd → 10, before rangeEnd 15

        Assert.Equal(band.FalloffEnd, moved.FalloffEnd, 3); // rejected
    }

    [Fact]
    public void FalloffHandlesKeepTheirShoulderDistanceWhenPossible()
    {
        // Drag falloffEnd from 45 to 90: only falloffEnd moves.
        var moved = Reds().WithHandle(3, 90);

        Assert.Equal(90, moved.FalloffEnd, 3);
        Assert.Equal(345, moved.RangeStart, 3); // range untouched
    }
}
