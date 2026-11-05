using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// Golden tests for adjustment layers: per-kind pixel math on small fixtures, plus the
/// compositing rules (region, opacity) in the compositor.
/// </summary>
public class AdjustmentTests
{
    private static Surface Solid(int width, int height, byte r, byte g, byte b, byte a = 255)
    {
        var surface = new Surface(width, height);
        for (int row = 0; row < height; row++)
        {
            for (int column = 0; column < width; column++)
            {
                int index = row * surface.Stride + column * 4;
                surface.Pixels[index] = r;
                surface.Pixels[index + 1] = g;
                surface.Pixels[index + 2] = b;
                surface.Pixels[index + 3] = a;
            }
        }
        return surface;
    }

    private static byte Pixel(Surface surface, int x, int y, int channel) =>
        surface.Pixels[y * surface.Stride + x * 4 + channel];

    /// <summary>An opaque backdrop layer plus an adjustment layer above it, covering the canvas.</summary>
    private static (ProjectSnapshot Snapshot, ProjectLayerRecord Adjustment) DocumentOver(Surface backdrop, int width, int height)
    {
        var backdropId = Guid.NewGuid();
        var adjustmentId = Guid.NewGuid();
        var snapshot = new ProjectSnapshot
        {
            Manifest = new ProjectManifest
            {
                DocumentId = Guid.NewGuid(),
                Width = width,
                Height = height,
                ActiveLayerId = adjustmentId,
                Layers =
                [
                    new ProjectLayerRecord
                    {
                        Id = backdropId,
                        Name = "Backdrop",
                        IsVisible = true,
                        ImageFile = ProjectValidator.Uuid(backdropId) + ".png",
                        Transform = new LayerTransform { Origin = new PointD(0, 0), Size = new SizeD(width, height) },
                    },
                    new ProjectLayerRecord
                    {
                        Id = adjustmentId,
                        Name = "Adjustment",
                        IsVisible = true,
                        Opacity = 1,
                        BlendMode = LayerBlendMode.Normal,
                        Adjustment = new LayerAdjustment { Kind = AdjustmentKind.Invert },
                        Transform = new LayerTransform { Origin = new PointD(0, 0), Size = new SizeD(width, height) },
                    },
                ],
            },
            Images = new Dictionary<Guid, Surface> { [backdropId] = backdrop },
            Masks = new Dictionary<Guid, MaskSurface>(),
        };
        return (snapshot, snapshot.Manifest.Layers[1]);
    }

    [Theory]
    [InlineData(false, 255, 1)]
    [InlineData(false, 255, 0.5)]
    [InlineData(false, 128, 1)]
    [InlineData(false, 128, 0.5)]
    [InlineData(true, 255, 1)]
    [InlineData(true, 255, 0.5)]
    [InlineData(true, 128, 1)]
    [InlineData(true, 128, 0.5)]
    public void NormalAdjustmentMaskControlsEffectWithoutErasingBackdrop(bool onFolder, byte alpha, double opacity)
    {
        var (snapshot, adjustment) = DocumentOver(Solid(3, 1, alpha, 0, 0, alpha), 3, 1);
        adjustment.Opacity = opacity;
        var masked = adjustment;
        if (onFolder)
        {
            masked = new ProjectLayerRecord
            {
                Id = Guid.NewGuid(), Name = "Grade", IsVisible = true, IsGroup = true,
                Transform = new LayerTransform { Size = new SizeD(3, 1) },
            };
            snapshot.Manifest.Layers.Insert(1, masked);
            adjustment.ParentId = masked.Id;
        }
        masked.MaskFile = ProjectValidator.Uuid(masked.Id) + ".mask.png";
        masked.MaskEnabled = true;
        byte[] coverageValues = [0, 128, 255];
        snapshot = new ProjectSnapshot
        {
            Manifest = snapshot.Manifest, Images = snapshot.Images,
            Masks = new Dictionary<Guid, MaskSurface> { [masked.Id] = new MaskSurface(3, 1, coverageValues) },
        };

        foreach (bool cached in new[] { false, true })
        {
            var compositor = new CanvasCompositor(snapshot, cacheLayers: cached);
            for (int pass = 0; pass < 2; pass++)
            {
                var canvas = compositor.Render();
                for (int x = 0; x < 3; x++)
                {
                    double coverage = coverageValues[x] / 255.0 * opacity;
                    int cyan = (int)Math.Round(alpha * coverage);
                    Assert.InRange((int)Pixel(canvas, x, 0, 0), alpha - cyan - 1, alpha - cyan + 1);
                    Assert.InRange((int)Pixel(canvas, x, 0, 1), cyan - 1, cyan + 1);
                    Assert.InRange((int)Pixel(canvas, x, 0, 2), cyan - 1, cyan + 1);
                    Assert.Equal(alpha, Pixel(canvas, x, 0, 3));
                }
            }
        }
    }

    [Fact]
    public void InvertTurnsRedCyan()
    {
        var (snapshot, adjustment) = DocumentOver(Solid(4, 4, 255, 0, 0), 4, 4);
        adjustment.Adjustment = new LayerAdjustment { Kind = AdjustmentKind.Invert };

        var canvas = new CanvasCompositor(snapshot).Render();

        Assert.Equal(0, Pixel(canvas, 0, 0, 0));
        Assert.Equal(255, Pixel(canvas, 0, 0, 1));
        Assert.Equal(255, Pixel(canvas, 0, 0, 2));
    }

    [Fact]
    public void InvertKeepsPartialAlpha()
    {
        // Straight red at half alpha, stored premultiplied: r = 255 × (128/255) ≈ 128.
        var halfRed = new Surface(1, 1);
        halfRed.Pixels[0] = 128;
        halfRed.Pixels[3] = 128;
        var (snapshot, adjustment) = DocumentOver(halfRed, 1, 1);
        adjustment.Adjustment = new LayerAdjustment { Kind = AdjustmentKind.Invert };

        var canvas = new CanvasCompositor(snapshot).Render();

        // Straight red 255 inverted to straight cyan, then premultiplied back: 0 and 128.
        Assert.Equal(0, Pixel(canvas, 0, 0, 0));
        Assert.Equal(128, Pixel(canvas, 0, 0, 1));
        Assert.Equal(128, Pixel(canvas, 0, 0, 2));
        Assert.Equal(128, Pixel(canvas, 0, 0, 3));
    }

    [Fact]
    public void ExposureLiftsMidtonesByStops()
    {
        // One stop of exposure doubles linear light: 128 encodes to ~0.216 linear, ×2, back
        // through the sRGB curve — about 174.
        var (snapshot, adjustment) = DocumentOver(Solid(1, 1, 128, 128, 128), 1, 1);
        adjustment.Adjustment = new LayerAdjustment
        {
            Kind = AdjustmentKind.Exposure,
            ExposureSettings = new ExposureSettings { Exposure = 1, Offset = 0, Gamma = 1 },
        };

        var canvas = new CanvasCompositor(snapshot).Render();

        double encoded = 128 / 255.0;
        double linear = Math.Pow((encoded + 0.055) / 1.055, 2.4);
        double doubled = linear * 2;
        double back = 1.055 * Math.Pow(doubled, 1 / 2.4) - 0.055;
        int expected = (int)Math.Round(back * 255);
        Assert.Equal(expected, Pixel(canvas, 0, 0, 0));
        Assert.Equal(expected, Pixel(canvas, 0, 0, 1));
        Assert.True(expected > 170 && expected < 180, $"expected {expected} in the plausible range");
    }

    [Fact]
    public void LevelsRemapTheInputRange()
    {
        // Black point 64, white point 192: gray 128 lands at half output → 128 again, but
        // dark gray 96 lands at a quarter → 64.
        var twoGrays = new Surface(2, 1);
        twoGrays.Pixels[0] = twoGrays.Pixels[1] = twoGrays.Pixels[2] = 128;
        twoGrays.Pixels[3] = 255;
        twoGrays.Pixels[4] = twoGrays.Pixels[5] = twoGrays.Pixels[6] = 96;
        twoGrays.Pixels[7] = 255;
        var (snapshot, adjustment) = DocumentOver(twoGrays, 2, 1);
        adjustment.Adjustment = new LayerAdjustment
        {
            Kind = AdjustmentKind.Levels,
            Levels = new LevelsSettings
            {
                Channel = LevelsChannel.Rgb,
                Ranges =
                [
                    new LevelRange { Black = 64, Gamma = 1, White = 192, OutputBlack = 0, OutputWhite = 255 },
                    new(), new(), new(),
                ],
            },
        };

        var canvas = new CanvasCompositor(snapshot).Render();

        Assert.Equal(128, Pixel(canvas, 0, 0, 0));
        Assert.Equal(64, Pixel(canvas, 1, 0, 0));
    }

    [Fact]
    public void IdentityCurvesChangeNothing()
    {
        var (snapshot, adjustment) = DocumentOver(Solid(1, 1, 100, 150, 200), 1, 1);
        adjustment.Adjustment = new LayerAdjustment
        {
            Kind = AdjustmentKind.Curves,
            Curves = new CurvesSettings(),
        };

        var canvas = new CanvasCompositor(snapshot).Render();

        Assert.Equal(100, Pixel(canvas, 0, 0, 0));
        Assert.Equal(150, Pixel(canvas, 0, 0, 1));
        Assert.Equal(200, Pixel(canvas, 0, 0, 2));
    }

    [Fact]
    public void CurvesLiftTheMidtones()
    {
        var (snapshot, adjustment) = DocumentOver(Solid(1, 1, 64, 64, 64), 1, 1);
        adjustment.Adjustment = new LayerAdjustment
        {
            Kind = AdjustmentKind.Curves,
            Curves = new CurvesSettings
            {
                Channel = LevelsChannel.Rgb,
                Channels =
                [
                    new() { new CurvePoint(0, 0), new CurvePoint(128, 220), new CurvePoint(255, 255) },
                    new() { new CurvePoint(0, 0), new CurvePoint(255, 255) },
                    new() { new CurvePoint(0, 0), new CurvePoint(255, 255) },
                    new() { new CurvePoint(0, 0), new CurvePoint(255, 255) },
                ],
            },
        };

        var canvas = new CanvasCompositor(snapshot).Render();

        int value = Pixel(canvas, 0, 0, 0);
        Assert.True(value is > 128 and < 220, $"curve at input 64 should land between 128 and 220, got {value}");
    }

    [Fact]
    public void GradientMapRunsDarkToLight()
    {
        var twoGrays = new Surface(2, 1);
        twoGrays.Pixels[0] = twoGrays.Pixels[1] = twoGrays.Pixels[2] = 60;
        twoGrays.Pixels[3] = 255;
        twoGrays.Pixels[4] = twoGrays.Pixels[5] = twoGrays.Pixels[6] = 180;
        twoGrays.Pixels[7] = 255;
        var (snapshot, adjustment) = DocumentOver(twoGrays, 2, 1);
        adjustment.Adjustment = new LayerAdjustment
        {
            Kind = AdjustmentKind.GradientMap,
            GradientMapSettings = new GradientMapSettings
            {
                Shadows = new AdjustmentColor(0, 0, 0),
                Highlights = new AdjustmentColor(1, 1, 1),
                Reversed = false,
            },
        };

        var canvas = new CanvasCompositor(snapshot).Render();

        // Each gray's luminance maps onto the same level in the black-white ramp.
        int dark = Pixel(canvas, 0, 0, 0);
        int bright = Pixel(canvas, 1, 0, 0);
        Assert.Equal(60, dark);
        Assert.Equal(180, bright);
        Assert.Equal(dark, Pixel(canvas, 0, 0, 1)); // output is neutral
        Assert.True(bright > dark, "brighter input lands higher on the ramp");
    }

    [Fact]
    public void ReversedGradientMapInverts()
    {
        var (snapshot, adjustment) = DocumentOver(Solid(1, 1, 128, 128, 128), 1, 1);
        adjustment.Adjustment = new LayerAdjustment
        {
            Kind = AdjustmentKind.GradientMap,
            GradientMapSettings = new GradientMapSettings { Reversed = true },
        };

        var canvas = new CanvasCompositor(snapshot).Render();

        Assert.Equal(127, Pixel(canvas, 0, 0, 0)); // 255 − the luminance level
    }

    [Fact]
    public void HueShiftRotatesColor()
    {
        var (snapshot, adjustment) = DocumentOver(Solid(1, 1, 255, 0, 0), 1, 1);
        adjustment.Adjustment = new LayerAdjustment
        {
            Kind = AdjustmentKind.HueSaturation,
            Hue = 180,
        };

        var canvas = new CanvasCompositor(snapshot).Render();

        Assert.Equal(0, Pixel(canvas, 0, 0, 0));
        Assert.Equal(255, Pixel(canvas, 0, 0, 1));
        Assert.Equal(255, Pixel(canvas, 0, 0, 2));
    }

    [Fact]
    public void SaturationZeroDesaturates()
    {
        var (snapshot, adjustment) = DocumentOver(Solid(1, 1, 255, 0, 0), 1, 1);
        adjustment.Adjustment = new LayerAdjustment
        {
            Kind = AdjustmentKind.HueSaturation,
            Saturation = -100,
        };

        var canvas = new CanvasCompositor(snapshot).Render();

        int r = Pixel(canvas, 0, 0, 0);
        Assert.Equal(r, Pixel(canvas, 0, 0, 1));
        Assert.Equal(r, Pixel(canvas, 0, 0, 2));
    }

    [Fact]
    public void GaussianBlurLeavesFlatAreasAlone()
    {
        var (snapshot, adjustment) = DocumentOver(Solid(8, 8, 120, 90, 60), 8, 8);
        adjustment.Adjustment = new LayerAdjustment
        {
            Kind = AdjustmentKind.GaussianBlur,
            BlurRadius = 5,
        };

        var canvas = new CanvasCompositor(snapshot).Render();

        Assert.Equal(120, Pixel(canvas, 4, 4, 0));
        Assert.Equal(90, Pixel(canvas, 4, 4, 1));
        Assert.Equal(60, Pixel(canvas, 4, 4, 2));
    }

    [Fact]
    public void AddNoiseIsDeterministicPerSeed()
    {
        // Two renders with the same seed agree; a different seed differs.
        var (snapshot, adjustment) = DocumentOver(Solid(4, 4, 128, 128, 128), 4, 4);
        adjustment.Adjustment = new LayerAdjustment
        {
            Kind = AdjustmentKind.AddNoise,
            NoiseAmount = 100,
            NoiseSeed = 12345,
        };
        var noiseAdjustment = snapshot.Manifest.Layers[1].Adjustment!;
        noiseAdjustment.NoiseSeed = 12345;
        var first = new CanvasCompositor(snapshot).Render().Clone();

        var second = new CanvasCompositor(snapshot).Render().Clone();

        Assert.Equal(first.Pixels, second.Pixels);

        noiseAdjustment.NoiseSeed = 999;
        var third = new CanvasCompositor(snapshot).Render().Clone();
        Assert.NotEqual(first.Pixels, third.Pixels);
    }

    [Fact]
    public void GrainStaysPutAcrossRenders()
    {
        var (snapshot, adjustment) = DocumentOver(Solid(8, 8, 150, 150, 150), 8, 8);
        adjustment.Adjustment = new LayerAdjustment
        {
            Kind = AdjustmentKind.Grain,
            GrainSettings = new GrainSettings { Amount = 40, Size = 2, Roughness = 50, Seed = 7 },
        };
        var first = new CanvasCompositor(snapshot).Render().Clone();
        var second = new CanvasCompositor(snapshot).Render().Clone();
        Assert.Equal(first.Pixels, second.Pixels);
        Assert.NotEqual(Solid(8, 8, 150, 150, 150).Pixels, first.Pixels);
    }

    [Fact]
    public void AdjustmentLayerOnlyAffectsItsRegion()
    {
        // The adjustment covers the left half only: the right half keeps its backdrop.
        var (snapshot, adjustment) = DocumentOver(Solid(4, 4, 255, 0, 0), 4, 4);
        adjustment.Adjustment = new LayerAdjustment { Kind = AdjustmentKind.Invert };
        adjustment.Transform.Size = new SizeD(2, 4);

        var canvas = new CanvasCompositor(snapshot).Render();

        Assert.Equal(0, Pixel(canvas, 0, 0, 0)); // inverted: cyan
        Assert.Equal(255, Pixel(canvas, 0, 0, 1));
        Assert.Equal(255, Pixel(canvas, 3, 0, 0)); // untouched: red
        Assert.Equal(0, Pixel(canvas, 3, 0, 1));
    }

    [Fact]
    public void AdjustmentLayerOpacityBlends()
    {
        var (snapshot, adjustment) = DocumentOver(Solid(1, 1, 255, 0, 0), 1, 1);
        adjustment.Adjustment = new LayerAdjustment { Kind = AdjustmentKind.Invert };
        adjustment.Opacity = 0.5;

        var canvas = new CanvasCompositor(snapshot).Render();

        // Half red, half cyan: every channel lands halfway.
        Assert.Equal(128, Pixel(canvas, 0, 0, 0));
        Assert.Equal(128, Pixel(canvas, 0, 0, 1));
        Assert.Equal(128, Pixel(canvas, 0, 0, 2));
        Assert.Equal(255, Pixel(canvas, 0, 0, 3));
    }

    // --- Port locks: the exact formulas the macOS C routines use ---

    [Fact]
    public void BlackWhiteFollowsThePrimarySecondaryDecomposition()
    {
        // AdjustPixels.c: gray = min + (mid−min)×w[secondary] + (max−mid)×w[primary].
        // Pure red takes the Reds slider (40), pure yellow the Yellows (60), blue the Blues (20).
        var fixture = new Surface(3, 1);
        byte[][] colors = { [255, 0, 0], [255, 255, 0], [0, 0, 255] };
        for (int i = 0; i < 3; i++)
        {
            fixture.Pixels[i * 4] = colors[i][0];
            fixture.Pixels[i * 4 + 1] = colors[i][1];
            fixture.Pixels[i * 4 + 2] = colors[i][2];
            fixture.Pixels[i * 4 + 3] = 255;
        }
        AdjustmentEngine.Apply(new LayerAdjustment
        {
            Kind = AdjustmentKind.BlackWhite,
            BlackWhiteSettings = new BlackWhiteSettings { Tint = false },
        }, fixture, 0, 0, 3, 1);

        Assert.Equal(102, fixture.Pixels[0]); // 255 × 0.4 reds
        Assert.Equal(153, fixture.Pixels[4]); // 255 × 0.6 yellows
        Assert.Equal(51, fixture.Pixels[8]); // 255 × 0.2 blues
    }

    [Fact]
    public void ColorBalanceUsesTheMacTonalWeights()
    {
        // tonal_weights at channel value 128/255 ≈ 0.502 gives mid ≈ 0.7 of the full shift;
        // a 50% midtone blue shift lands at 0.502 + 0.35 ≈ 0.852 → 217.
        var fixture = Solid(1, 1, 128, 128, 128);
        AdjustmentEngine.Apply(new LayerAdjustment
        {
            Kind = AdjustmentKind.ColorBalance,
            ColorBalanceSettings = new ColorBalanceSettings { MidYellowBlue = 50, PreserveLuminosity = false },
        }, fixture, 0, 0, 1, 1);

        Assert.Equal(128, fixture.Pixels[0]);
        Assert.Equal(128, fixture.Pixels[1]);
        Assert.Equal(217, fixture.Pixels[2]);
    }

    [Fact]
    public void AddNoiseMatchesTheMacHashChain()
    {
        // NoisePixels.c: base = noise_hash(seed ^ noise_hash(px·φ ^ noise_hash(py·φ'))),
        // uniform from the top 24 bits, uniform noise scaled by amount/100 × 127.5.
        var fixture = Solid(1, 1, 128, 128, 128);
        AdjustmentEngine.Apply(new LayerAdjustment
        {
            Kind = AdjustmentKind.AddNoise,
            NoiseAmount = 100,
            NoiseGaussian = false,
            NoiseMonochromatic = false,
            NoiseSeed = 42,
        }, fixture, 0, 0, 1, 1);

        uint Hash(uint x)
        {
            x ^= x >> 16;
            x *= 0x7feb352du;
            x ^= x >> 15;
            x *= 0x846ca68bu;
            x ^= x >> 16;
            return x;
        }
        float Unit(uint key) => (Hash(key) >> 8) * (1f / 16777216f);
        uint @base = Hash(42u ^ Hash(0u ^ Hash(0u)));
        float spread = 127.5f;
        for (int channel = 0; channel < 3; channel++)
        {
            float noise = (Unit(@base + (uint)channel * 0x9e3779b9u) * 2f - 1f) * spread;
            float expected = Math.Clamp(128 + noise, 0, 255);
            Assert.Equal((byte)MathF.Round(expected), fixture.Pixels[channel]);
        }
    }

    [Fact]
    public void HueSaturationHonorsPerRangeAdjustments()
    {
        // A Reds-only hue shift of 180 turns red cyan and leaves green alone.
        var fixture = new Surface(2, 1);
        fixture.Pixels[0] = 255; fixture.Pixels[3] = 255;
        fixture.Pixels[4] = 0; fixture.Pixels[5] = 255; fixture.Pixels[6] = 0; fixture.Pixels[7] = 255;
        AdjustmentEngine.Apply(new LayerAdjustment
        {
            Kind = AdjustmentKind.HueSaturation,
            HsvSettings = new HueSaturationSettings
            {
                Range = ColorRange.Reds,
                Adjustments = new Dictionary<string, RangeAdjustment>
                {
                    ["Reds"] = new RangeAdjustment { Hue = 180 },
                },
            },
        }, fixture, 0, 0, 2, 1);

        Assert.Equal(0, fixture.Pixels[0]); // red rotated to cyan
        Assert.Equal(255, fixture.Pixels[1]);
        Assert.Equal(255, fixture.Pixels[2]);
        Assert.Equal(0, fixture.Pixels[4]); // green untouched
        Assert.Equal(255, fixture.Pixels[5]);
    }

    [Fact]
    public void ColorBalanceShiftsTowardTheSliders()
    {
        var (snapshot, adjustment) = DocumentOver(Solid(1, 1, 128, 128, 128), 1, 1);
        adjustment.Adjustment = new LayerAdjustment
        {
            Kind = AdjustmentKind.ColorBalance,
            ColorBalanceSettings = new ColorBalanceSettings
            {
                MidYellowBlue = 100, // push midtones toward blue
                PreserveLuminosity = false,
            },
        };

        var canvas = new CanvasCompositor(snapshot).Render();

        Assert.True(Pixel(canvas, 0, 0, 2) > 128, "blue should rise");
        Assert.Equal(128, Pixel(canvas, 0, 0, 0)); // the cyan-red pair is untouched
        Assert.Equal(128, Pixel(canvas, 0, 0, 1)); // so is magenta-green
    }
}


public class LevelsCurvesEditTests
{
    private static EditorDocument DocumentWithLevels()
    {
        var document = EditorDocument.Create(8, 8);
        var layer = document.AddAdjustment(AdjustmentKind.Levels);
        return document;
    }

    [Fact]
    public void MutateLevelsChangesBlackPointAndUndoes()
    {
        var document = EditorDocument.Create(8, 8);
        var levelsLayer = document.AddAdjustment(AdjustmentKind.Levels);
        var key = new object();

        Assert.True(document.MutateLevels(levelsLayer.Id, levels =>
        {
            levels.Ranges[0].Black = 64;
        }, key));
        Assert.Equal(64, document.Snapshot.Manifest.Layers[1].Adjustment!.Levels.Ranges[0].Black);

        document.Undo();
        Assert.Equal(0, document.Snapshot.Manifest.Layers[1].Adjustment!.Levels.Ranges[0].Black);
    }

    [Fact]
    public void MutateCurvesAddsAPointAndUndoRemovesIt()
    {
        var document = EditorDocument.Create(8, 8);
        var curvesLayer = document.AddAdjustment(AdjustmentKind.Curves);
        var key = new object();

        document.MutateCurves(curvesLayer.Id, curves =>
        {
            curves.Channels[0].Insert(1, new CurvePoint(128, 200));
        }, key);
        Assert.Equal(3, document.Snapshot.Manifest.Layers[1].Adjustment!.Curves.Channels[0].Count);
        Assert.True(document.Snapshot.Manifest.Layers[1].Adjustment!.Curves.IsValid);

        document.Undo();
        Assert.Equal(2, document.Snapshot.Manifest.Layers[1].Adjustment!.Curves.Channels[0].Count);
    }

    [Fact]
    public void SliderDragCoalescesToOneUndoStep()
    {
        var document = EditorDocument.Create(8, 8);
        var levelsLayer = document.AddAdjustment(AdjustmentKind.Levels);
        var key = new object();

        for (int black = 1; black <= 30; black++)
        {
            document.MutateLevels(levelsLayer.Id, levels => levels.Ranges[0].Black = black, key);
        }
        Assert.Equal(30, document.Snapshot.Manifest.Layers[1].Adjustment!.Levels.Ranges[0].Black);

        document.Undo();
        Assert.Equal(0, document.Snapshot.Manifest.Layers[1].Adjustment!.Levels.Ranges[0].Black);
    }
}


public class LevelsHistogramTests
{
    private static Surface Solid(int width, int height, byte r, byte g, byte b, byte a = 255)
    {
        var surface = new Surface(width, height);
        for (int row = 0; row < height; row++)
        {
            for (int column = 0; column < width; column++)
            {
                int index = row * surface.Stride + column * 4;
                surface.Pixels[index] = r;
                surface.Pixels[index + 1] = g;
                surface.Pixels[index + 2] = b;
                surface.Pixels[index + 3] = a;
            }
        }
        return surface;
    }

    [Fact]
    public void ChannelsBinTheirStraightColors()
    {
        var surface = Solid(2, 1, 200, 100, 50);
        var bins = LevelsHistogram.Sum(surface, null);

        Assert.Equal(2, bins[1][200]); // red channel at 200
        Assert.Equal(2, bins[2][100]);
        Assert.Equal(2, bins[3][50]);
        Assert.Equal(2.0 / 3, bins[0][200], 6); // RGB mean row gets weight/3
    }

    [Fact]
    public void TransparentPixelsAreSkipped()
    {
        var surface = Solid(1, 1, 200, 200, 200, a: 0);
        var bins = LevelsHistogram.Sum(surface, null);
        Assert.Equal(0, bins[1][200]);
        Assert.Equal(0, bins[0][200]);
    }

    [Fact]
    public void CoverageWeightsTheBins()
    {
        var surface = Solid(2, 1, 120, 120, 120);
        var half = new MaskSurface(2, 1, [255, 0]);
        var bins = LevelsHistogram.Sum(surface, half);
        Assert.Equal(1, bins[1][120]); // only the covered pixel counts
    }
}
