using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Compositor.Core;

/// <summary>
/// The pixel work behind adjustment layers — a direct port of the macOS routines, so results
/// match the Mac byte for byte wherever the format fixes them:
///
/// - Levels/Exposure/Curves go through the macOS levels_apply (LevelsPixels.c): float lookup
///   tables with linear interpolation, on unpremultiplied colors.
/// - Invert is the macOS vImage matrix: each premultiplied channel becomes alpha − channel.
/// - Gradient Map, Black &amp; White, Color Balance and Grain are ports of AdjustPixels.c.
/// - Add Noise is NoisePixels.c (noise_add_at), pattern stable per seed and document spot.
/// - Hue/Saturation builds the same 33³ color cube as HueSaturationFilter and applies it with
///   the macOS cube_apply trilinear lookup.
/// - Gaussian/Motion blur match the Core Image filters the Mac runs: a true gaussian with
///   sigma = radius, and a line kernel of half length distance / √12 at the angle (Core
///   Image measures counterclockwise in a y-up space; document space is y-down).
/// </summary>
public static class AdjustmentEngine
{
    // Independent point operations split large photo regions into rows. Small brush bands
    // stay serial so scheduling does not dominate their pixel work.
    internal const long ParallelPointPixels = 1 << 18;
    internal static readonly ParallelOptions ParallelPointOptions = new() { MaxDegreeOfParallelism = Math.Min(8, Environment.ProcessorCount) };

    /// <summary>Applies the adjustment to the premultiplied surface region in place.</summary>
    public static void Apply(LayerAdjustment adjustment, Surface surface, int x0, int y0, int x1, int y1)
    {
        switch (adjustment.Kind)
        {
            case AdjustmentKind.Invert:
                ApplyInvert(surface, x0, y0, x1, y1);
                break;
            case AdjustmentKind.Exposure:
                float[] exposure = ExposureTable(adjustment.ExposureSettings ?? new ExposureSettings());
                ApplyLevelsTables(surface, x0, y0, x1, y1, exposure, exposure, exposure);
                break;
            case AdjustmentKind.Levels:
                ApplyLevelsSettings(adjustment.Levels, surface, x0, y0, x1, y1);
                break;
            case AdjustmentKind.Curves:
                ApplyCurvesSettings(adjustment.Curves, surface, x0, y0, x1, y1);
                break;
            case AdjustmentKind.GradientMap:
                ApplyGradientMap(adjustment.GradientMapSettings ?? new GradientMapSettings(), surface, x0, y0, x1, y1);
                break;
            case AdjustmentKind.BlackWhite:
                ApplyBlackWhite(adjustment.BlackWhiteSettings ?? new BlackWhiteSettings(), surface, x0, y0, x1, y1);
                break;
            case AdjustmentKind.ColorBalance:
                ApplyColorBalance(adjustment.ColorBalanceSettings ?? new ColorBalanceSettings(), surface, x0, y0, x1, y1);
                break;
            case AdjustmentKind.HueSaturation:
                ApplyHueSaturation(adjustment.ResolvedHsv, surface, x0, y0, x1, y1);
                break;
            case AdjustmentKind.GaussianBlur:
                ApplyGaussianBlur(adjustment.GaussianRadius, surface, x0, y0, x1, y1);
                break;
            case AdjustmentKind.MotionBlur:
                ApplyMotionBlur(adjustment.ResolvedMotionAngle, adjustment.ResolvedMotionDistance, surface, x0, y0, x1, y1);
                break;
            case AdjustmentKind.AddNoise:
                ApplyAddNoise(adjustment, surface, x0, y0, x1, y1);
                break;
            case AdjustmentKind.Grain:
                ApplyGrain(adjustment.GrainSettings ?? new GrainSettings(), surface, x0, y0, x1, y1);
                break;
        }
    }

    /// <summary>The halo an adjustment needs beyond its region, in document pixels (the macOS samplingMargin).</summary>
    public static double SamplingMargin(LayerAdjustment adjustment) => adjustment.Kind switch
    {
        AdjustmentKind.GaussianBlur => adjustment.GaussianRadius * 3 + 2,
        AdjustmentKind.MotionBlur => adjustment.ResolvedMotionDistance / 2 + 2,
        _ => 0,
    };

    // --- levels_apply (LevelsPixels.c), fed by the macOS float tables ---

    // The compositor uses this only for one mask on the same document pixel grid. Applying
    // and blending each channel together avoids two full-size scratch copies per frame.
    internal static bool TryApplyMaskedPoint(LayerAdjustment adjustment, MaskSurface mask, Surface surface, int x0, int y0, int x1, int y1)
    {
        switch (adjustment.Kind)
        {
            case AdjustmentKind.Exposure:
                float[] table = ExposureTable(adjustment.ExposureSettings ?? new());
                ApplyLevelsTables(surface, x0, y0, x1, y1, table, table, table, mask);
                return true;
            case AdjustmentKind.Curves:
                ApplyCurvesSettings(adjustment.Curves, surface, x0, y0, x1, y1, mask);
                return true;
            case AdjustmentKind.Levels:
                ApplyLevelsSettings(adjustment.Levels, surface, x0, y0, x1, y1, mask);
                return true;
            default: return false;
        }
    }

    private static void ApplyLevelsTables(Surface surface, int x0, int y0, int x1, int y1, float[] red, float[] green, float[] blue, MaskSurface? mask = null)
    {
        float[][] tables = [red, green, blue];
        // Opaque pixels take a precomputed byte LUT per channel: at alpha 255 the table
        // lookup collapses to table[value] with no interpolation and no unpremultiplication
        // (Round(table[v]·255) lands in the LUT), which is most pixels of a photo and what
        // a drag re-grades every tick.
        byte[][] opaqueLuts = new byte[3][];
        for (int channel = 0; channel < 3; channel++)
        {
            var lut = new byte[256];
            float[] table = tables[channel];
            for (int i = 0; i < 256; i++)
            {
                lut[i] = (byte)Math.Clamp(MathF.Round(table[i] * 255f, MidpointRounding.AwayFromZero), 0f, 255f);
            }
            opaqueLuts[channel] = lut;
        }
        if ((long)(x1 - x0) * (y1 - y0) >= ParallelPointPixels)
            Parallel.For(y0, y1, ParallelPointOptions, ApplyRow);
        else
            for (int y = y0; y < y1; y++) ApplyRow(y);

        void ApplyRow(int y)
        {
            int row = y * surface.Stride;
            for (int x = x0; x < x1; x++)
            {
                byte coverage = mask?.Pixels[y * mask.Width + x] ?? 255;
                if (coverage == 0) continue;
                int index = row + x * 4;
                float alpha = surface.Pixels[index + 3];
                if (alpha == 0) continue;
                if (alpha == 255f && coverage == 255)
                {
                    surface.Pixels[index] = opaqueLuts[0][surface.Pixels[index]];
                    surface.Pixels[index + 1] = opaqueLuts[1][surface.Pixels[index + 1]];
                    surface.Pixels[index + 2] = opaqueLuts[2][surface.Pixels[index + 2]];
                    continue;
                }
                for (int channel = 0; channel < 3; channel++)
                {
                    byte original = surface.Pixels[index + channel];
                    byte adjusted;
                    if (alpha == 255f) adjusted = opaqueLuts[channel][original];
                    else
                    {
                        float tableX = Math.Min(255f, original * 255f / alpha);
                        int lo = (int)tableX;
                        int hi = lo < 255 ? lo + 1 : 255;
                        float[] table = tables[channel];
                        float result = table[lo] + (table[hi] - table[lo]) * (tableX - lo);
                        adjusted = (byte)Math.Clamp(MathF.Round(result * alpha, MidpointRounding.AwayFromZero), 0f, alpha);
                    }
                    surface.Pixels[index + channel] = coverage == 255 ? adjusted
                        : (byte)((adjusted * coverage + original * (255 - coverage) + 127) / 255);
                }
            }
        }
    }

    private static void ApplyLevelsSettings(LevelsSettings settings, Surface surface, int x0, int y0, int x1, int y1, MaskSurface? mask = null)
    {
        if (settings.IsIdentity) return;
        float[][] tables = LevelsTables(settings);
        ApplyLevelsTables(surface, x0, y0, x1, y1, tables[0], tables[1], tables[2], mask);
    }

    internal static float[][] LevelsTables(LevelsSettings settings)
    {
        float[][] tables = new float[3][];
        for (int channel = 0; channel < 3; channel++)
        {
            // The macOS LevelsFilter: the channel's own range applies first, then RGB.
            var table = new float[256];
            for (int i = 0; i < 256; i++)
            {
                table[i] = (float)settings.Ranges[0].Apply(settings.Ranges[channel + 1].Apply(i / 255.0));
            }
            tables[channel] = table;
        }
        return tables;
    }

    private static void ApplyCurvesSettings(CurvesSettings settings, Surface surface, int x0, int y0, int x1, int y1, MaskSurface? mask = null)
    {
        float[][] tables = CurvesTables(settings);
        ApplyLevelsTables(surface, x0, y0, x1, y1, tables[0], tables[1], tables[2], mask);
    }

    internal static float[][] CurvesTables(CurvesSettings settings)
    {
        float[][] tables = new float[3][];
        for (int channel = 0; channel < 3; channel++)
        {
            var table = new float[256];
            for (int i = 0; i < 256; i++)
            {
                // The macOS CurvesSettings.apply: channel curve first, then the RGB curve.
                table[i] = (float)(settings.Value(settings.Value(i, channel + 1), 0) / 255.0);
            }
            tables[channel] = table;
        }
        return tables;
    }

    /// <summary>The macOS ExposureSettings.table: sRGB → linear, exposure and offset, gamma, back.</summary>
    internal static float[] ExposureTable(ExposureSettings settings)
    {
        var table = new float[256];
        double scale = Math.Pow(2, settings.Exposure);
        for (int i = 0; i < 256; i++)
        {
            double encoded = i / 255.0;
            double linear = encoded <= 0.04045 ? encoded / 12.92 : Math.Pow((encoded + 0.055) / 1.055, 2.4);
            linear = Math.Pow(Math.Max(0, linear * scale + settings.Offset), 1 / settings.Gamma);
            double output = linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
            table[i] = (float)Math.Clamp(output, 0, 1);
        }
        return table;
    }

    // --- Invert: the macOS vImageMatrixMultiply_ARGB8888, on premultiplied bytes ---

    private static void ApplyInvert(Surface surface, int x0, int y0, int x1, int y1)
    {
        for (int y = y0; y < y1; y++)
        {
            int row = y * surface.Stride;
            for (int x = x0; x < x1; x++)
            {
                int index = row + x * 4;
                byte alpha = surface.Pixels[index + 3];
                surface.Pixels[index] = (byte)(alpha - surface.Pixels[index]);
                surface.Pixels[index + 1] = (byte)(alpha - surface.Pixels[index + 1]);
                surface.Pixels[index + 2] = (byte)(alpha - surface.Pixels[index + 2]);
            }
        }
    }

    // --- Gradient Map (AdjustPixels.c adjust_gradient_map) ---

    private static void ApplyGradientMap(GradientMapSettings settings, Surface surface, int x0, int y0, int x1, int y1)
    {
        var dark = settings.Reversed ? settings.Highlights : settings.Shadows;
        var light = settings.Reversed ? settings.Shadows : settings.Highlights;
        var table = new byte[256 * 3];
        for (int i = 0; i < 256; i++)
        {
            double t = i / 255.0;
            table[i * 3] = GradientChannel(dark.Red, light.Red, t);
            table[i * 3 + 1] = GradientChannel(dark.Green, light.Green, t);
            table[i * 3 + 2] = GradientChannel(dark.Blue, light.Blue, t);
        }
        for (int y = y0; y < y1; y++)
        {
            int row = y * surface.Stride;
            for (int x = x0; x < x1; x++)
            {
                int index = row + x * 4;
                uint alpha = surface.Pixels[index + 3];
                if (alpha == 0) continue;
                uint r = surface.Pixels[index], g = surface.Pixels[index + 1], b = surface.Pixels[index + 2];
                if (alpha < 255)
                {
                    r = Math.Min(255u, (r * 255u + alpha / 2u) / alpha);
                    g = Math.Min(255u, (g * 255u + alpha / 2u) / alpha);
                    b = Math.Min(255u, (b * 255u + alpha / 2u) / alpha);
                }
                uint level = Math.Min(255u, (2126u * r + 7152u * g + 722u * b + 5000u) / 10000u);
                surface.Pixels[index] = (byte)((table[level * 3] * alpha + 127u) / 255u);
                surface.Pixels[index + 1] = (byte)((table[level * 3 + 1] * alpha + 127u) / 255u);
                surface.Pixels[index + 2] = (byte)((table[level * 3 + 2] * alpha + 127u) / 255u);
            }
        }
    }

    private static byte GradientChannel(double from, double to, double t)
    {
        double value = from + (to - from) * t;
        double scaled = value * 255;
        return (byte)Math.Clamp(Math.Round(scaled, MidpointRounding.AwayFromZero), 0, 255);
    }

    // --- Black & White (AdjustPixels.c adjust_black_white) ---

    private static void ApplyBlackWhite(BlackWhiteSettings settings, Surface surface, int x0, int y0, int x1, int y1)
    {
        float[] weights =
        {
            (float)(settings.Reds / 100), (float)(settings.Yellows / 100), (float)(settings.Greens / 100),
            (float)(settings.Cyans / 100), (float)(settings.Blues / 100), (float)(settings.Magentas / 100),
        };
        bool tint = settings.Tint && settings.TintSaturation > 0;
        double tintHue = settings.TintHue, tintSaturation = settings.TintSaturation / 100.0;

        for (int y = y0; y < y1; y++)
        {
            int row = y * surface.Stride;
            for (int x = x0; x < x1; x++)
            {
                int index = row + x * 4;
                float alpha = surface.Pixels[index + 3];
                if (alpha == 0) continue;
                float r = Math.Min(255f, surface.Pixels[index] * 255f / alpha) / 255f;
                float g = Math.Min(255f, surface.Pixels[index + 1] * 255f / alpha) / 255f;
                float b = Math.Min(255f, surface.Pixels[index + 2] * 255f / alpha) / 255f;
                float mx = MathF.Max(r, MathF.Max(g, b)), mn = MathF.Min(r, MathF.Min(g, b));
                float md = r + g + b - mx - mn;

                // weights: 0 red, 1 yellow, 2 green, 3 cyan, 4 blue, 5 magenta — the color is
                // gray of min, plus (mid−min) of the secondary, plus (max−mid) of the primary.
                int primary, secondary;
                if (mx == r) { primary = 0; secondary = g >= b ? 1 : 5; }
                else if (mx == g) { primary = 2; secondary = r >= b ? 1 : 3; }
                else { primary = 4; secondary = g >= r ? 3 : 5; }
                float gray = mn + (md - mn) * weights[secondary] + (mx - md) * weights[primary];
                gray = Math.Clamp(gray, 0f, 1f);
                float outR = gray, outG = gray, outB = gray;
                if (tint)
                {
                    // The gray becomes the lightness of a color at the chosen hue.
                    double c = (1.0 - Math.Abs(2.0 * gray - 1.0)) * tintSaturation;
                    double hp = tintHue % 360.0 / 60.0;
                    double xx = c * (1.0 - Math.Abs(hp % 2.0 - 1.0));
                    double r1 = 0, g1 = 0, b1 = 0;
                    if (hp < 1) { r1 = c; g1 = xx; }
                    else if (hp < 2) { r1 = xx; g1 = c; }
                    else if (hp < 3) { g1 = c; b1 = xx; }
                    else if (hp < 4) { g1 = xx; b1 = c; }
                    else if (hp < 5) { r1 = xx; b1 = c; }
                    else { r1 = c; b1 = xx; }
                    double m = gray - c / 2.0;
                    outR = (float)Math.Clamp(r1 + m, 0.0, 1.0);
                    outG = (float)Math.Clamp(g1 + m, 0.0, 1.0);
                    outB = (float)Math.Clamp(b1 + m, 0.0, 1.0);
                }
                surface.Pixels[index] = (byte)Math.Clamp(MathF.Round(outR * alpha, MidpointRounding.AwayFromZero), 0f, alpha);
                surface.Pixels[index + 1] = (byte)Math.Clamp(MathF.Round(outG * alpha, MidpointRounding.AwayFromZero), 0f, alpha);
                surface.Pixels[index + 2] = (byte)Math.Clamp(MathF.Round(outB * alpha, MidpointRounding.AwayFromZero), 0f, alpha);
            }
        }
    }

    // --- Color Balance (AdjustPixels.c adjust_color_balance + tonal_weights) ---

    private static void ApplyColorBalance(ColorBalanceSettings settings, Surface surface, int x0, int y0, int x1, int y1)
    {
        if (settings.IsIdentity) return;
        float[] shadows = { (float)(settings.ShadowCyanRed / 100), (float)(settings.ShadowMagentaGreen / 100), (float)(settings.ShadowYellowBlue / 100) };
        float[] midtones = { (float)(settings.MidCyanRed / 100), (float)(settings.MidMagentaGreen / 100), (float)(settings.MidYellowBlue / 100) };
        float[] highlights = { (float)(settings.HighlightCyanRed / 100), (float)(settings.HighlightMagentaGreen / 100), (float)(settings.HighlightYellowBlue / 100) };
        // Opaque photo channels have only 256 inputs. Retain the original float values
        // until luminosity preservation and final rounding, so this is an exact lookup.
        var units = new float[256];
        float[][] opaque = [new float[256], new float[256], new float[256]];
        for (int i = 0; i < 256; i++)
        {
            float value = units[i] = i / 255f;
            for (int channel = 0; channel < 3; channel++) opaque[channel][i] = Shift(value, channel);
        }

        if ((long)(x1 - x0) * (y1 - y0) >= ParallelPointPixels)
            Parallel.For(y0, y1, ParallelPointOptions, ApplyRow);
        else
            for (int y = y0; y < y1; y++) ApplyRow(y);

        void ApplyRow(int y)
        {
            int row = y * surface.Stride;
            int first = Avx2.IsSupported
                ? ApplyOpaqueColorBalanceRow(surface.Pixels, row, x0, x1, units, opaque, settings.PreserveLuminosity) : x0;
            for (int x = first; x < x1; x++)
            {
                int index = row + x * 4;
                float alpha = surface.Pixels[index + 3];
                if (alpha == 0) continue;
                byte red = surface.Pixels[index], green = surface.Pixels[index + 1], blue = surface.Pixels[index + 2];
                float r, g, b, before;
                if (alpha == 255f)
                {
                    before = 0.299f * units[red] + 0.587f * units[green] + 0.114f * units[blue];
                    r = opaque[0][red]; g = opaque[1][green]; b = opaque[2][blue];
                }
                else
                {
                    r = Math.Min(255f, red * 255f / alpha) / 255f;
                    g = Math.Min(255f, green * 255f / alpha) / 255f;
                    b = Math.Min(255f, blue * 255f / alpha) / 255f;
                    before = 0.299f * r + 0.587f * g + 0.114f * b;
                    r = Shift(r, 0); g = Shift(g, 1); b = Shift(b, 2);
                }
                if (settings.PreserveLuminosity)
                {
                    float after = 0.299f * r + 0.587f * g + 0.114f * b;
                    if (after > 0.0001f)
                    {
                        float ratio = before / after;
                        r = Math.Clamp(r * ratio, 0f, 1f);
                        g = Math.Clamp(g * ratio, 0f, 1f);
                        b = Math.Clamp(b * ratio, 0f, 1f);
                    }
                }
                surface.Pixels[index] = (byte)Math.Clamp(MathF.Round(r * alpha, MidpointRounding.AwayFromZero), 0f, alpha);
                surface.Pixels[index + 1] = (byte)Math.Clamp(MathF.Round(g * alpha, MidpointRounding.AwayFromZero), 0f, alpha);
                surface.Pixels[index + 2] = (byte)Math.Clamp(MathF.Round(b * alpha, MidpointRounding.AwayFromZero), 0f, alpha);
            }
        }

        float Shift(float value, int channel)
        {
            return ColorBalanceShift(value, shadows[channel], midtones[channel], highlights[channel]);
        }
    }

    internal static float ColorBalanceShift(float value, float shadow, float midtone, float highlight)
    {
        TonalWeights(value, out float s, out float m, out float h);
        return Math.Clamp(value + shadow * s + midtone * m + highlight * h, 0f, 1f);
    }

    // Eight opaque photo pixels share the same arithmetic, with independent lanes. Stop
    // before a partial-alpha block; the scalar path also handles the row's short tail.
    private static unsafe int ApplyOpaqueColorBalanceRow(byte[] pixels, int row, int x0, int x1, float[] units, float[][] opaque, bool preserve)
    {
        var mask = Vector256.Create(255);
        var redWeight = Vector256.Create(.299f); var greenWeight = Vector256.Create(.587f); var blueWeight = Vector256.Create(.114f);
        var one = Vector256.Create(1f);
        int x = x0;
        fixed (float* unitTable = units, redTable = opaque[0], greenTable = opaque[1], blueTable = opaque[2])
        {
            for (; x <= x1 - 8; x += 8)
            {
                int index = row + x * 4;
                var packed = Vector256.LoadUnsafe(ref pixels[index]).AsInt32();
                if (Avx2.MoveMask(Avx2.CompareEqual(Avx2.ShiftRightLogical(packed, 24), mask).AsByte()) != -1) break;
                var red = Avx2.And(packed, mask);
                var green = Avx2.And(Avx2.ShiftRightLogical(packed, 8), mask);
                var blue = Avx2.And(Avx2.ShiftRightLogical(packed, 16), mask);
                var r = Avx2.GatherVector256(redTable, red, 4);
                var g = Avx2.GatherVector256(greenTable, green, 4);
                var b = Avx2.GatherVector256(blueTable, blue, 4);
                if (preserve)
                {
                    // Keep the scalar multiply/add order; fused operations change rounding.
                    var before = Avx.Add(Avx.Add(Avx.Multiply(redWeight, Avx2.GatherVector256(unitTable, red, 4)),
                        Avx.Multiply(greenWeight, Avx2.GatherVector256(unitTable, green, 4))),
                        Avx.Multiply(blueWeight, Avx2.GatherVector256(unitTable, blue, 4)));
                    var after = Avx.Add(Avx.Add(Avx.Multiply(redWeight, r), Avx.Multiply(greenWeight, g)), Avx.Multiply(blueWeight, b));
                    var nonzero = Avx.CompareGreaterThan(after, Vector256.Create(.0001f));
                    var denominator = Avx.BlendVariable(one, after, nonzero);
                    var ratio = Avx.BlendVariable(one, Avx.Divide(before, denominator), nonzero);
                    r = Avx.Max(Vector256<float>.Zero, Avx.Min(one, Avx.Multiply(r, ratio)));
                    g = Avx.Max(Vector256<float>.Zero, Avx.Min(one, Avx.Multiply(g, ratio)));
                    b = Avx.Max(Vector256<float>.Zero, Avx.Min(one, Avx.Multiply(b, ratio)));
                }
                var result = Avx2.Or(Avx2.Or(RoundChannel(r), Avx2.ShiftLeftLogical(RoundChannel(g), 8)),
                    Avx2.Or(Avx2.ShiftLeftLogical(RoundChannel(b), 16), Vector256.Create(unchecked((int)0xff000000))));
                result.AsByte().StoreUnsafe(ref pixels[index]);
            }
        }
        return x;

        static Vector256<int> RoundChannel(Vector256<float> value)
        {
            var scaled = Avx.Multiply(value, Vector256.Create(255f));
            var floor = Avx.ConvertToVector256Int32WithTruncation(scaled);
            // Adding .5 before truncation can round a value just below a midpoint up.
            // Compare its exact fraction instead, matching Round(AwayFromZero).
            var fraction = Avx.Subtract(scaled, Avx.ConvertToVector256Single(floor));
            return Avx2.Subtract(floor, Avx.CompareGreaterThanOrEqual(fraction, Vector256.Create(.5f)).AsInt32());
        }
    }

    /// <summary>
    /// How much a tone belongs to the shadows, midtones and highlights: three overlapping
    /// curves that sum to about one across the range (AdjustPixels.c tonal_weights).
    /// </summary>
    private static void TonalWeights(float v, out float shadow, out float mid, out float highlight)
    {
        const float a = 0.25f, b = 0.333f, scale = 0.7f;
        float s = Math.Clamp((v - b) / -a + 0.5f, 0f, 1f);
        float h = Math.Clamp((v + b - 1.0f) / a + 0.5f, 0f, 1f);
        float m1 = Math.Clamp((v - b) / a + 0.5f, 0f, 1f);
        float m2 = Math.Clamp((v + b - 1.0f) / -a + 0.5f, 0f, 1f);
        shadow = s * scale;
        mid = m1 * m2 * scale;
        highlight = h * scale;
    }

    // --- Hue/Saturation: the macOS 33³ color cube + cube_apply ---

    private const int CubeDimension = 33;

    private static void ApplyHueSaturation(HueSaturationSettings settings, Surface surface, int x0, int y0, int x1, int y1)
    {
        // Identity settings never build a cube, as on the Mac.
        bool identity = !settings.Colorize && settings.Adjustments.Values.All(value =>
            value.Hue == 0 && value.Saturation == 0 && value.Lightness == 0);
        if (identity) return;

        float[] cube = BuildCube(settings);
        float scale = (CubeDimension - 1) / 255f;
        int dy = CubeDimension, dz = CubeDimension * CubeDimension;

        for (int y = y0; y < y1; y++)
        {
            int row = y * surface.Stride;
            for (int x = x0; x < x1; x++)
            {
                int index = row + x * 4;
                float alpha = surface.Pixels[index + 3];
                if (alpha == 0) continue;

                float position0 = Math.Min(255f, surface.Pixels[index] * 255f / alpha) * scale;
                float position1 = Math.Min(255f, surface.Pixels[index + 1] * 255f / alpha) * scale;
                float position2 = Math.Min(255f, surface.Pixels[index + 2] * 255f / alpha) * scale;
                int lo0 = Math.Min((int)position0, CubeDimension - 2);
                int lo1 = Math.Min((int)position1, CubeDimension - 2);
                int lo2 = Math.Min((int)position2, CubeDimension - 2);
                float fraction0 = position0 - lo0, fraction1 = position1 - lo1, fraction2 = position2 - lo2;

                int cubeBase = (lo0 + lo1 * dy + lo2 * dz) * 4;
                int sx = 4, sy = dy * 4, sz = dz * 4;
                for (int channel = 0; channel < 3; channel++)
                {
                    int c = cubeBase + channel;
                    float x00 = cube[c] + (cube[c + sx] - cube[c]) * fraction0;
                    float x10 = cube[c + sy] + (cube[c + sy + sx] - cube[c + sy]) * fraction0;
                    float x01 = cube[c + sz] + (cube[c + sz + sx] - cube[c + sz]) * fraction0;
                    float x11 = cube[c + sz + sy] + (cube[c + sz + sy + sx] - cube[c + sz + sy]) * fraction0;
                    float y0v = x00 + (x10 - x00) * fraction1;
                    float y1v = x01 + (x11 - x01) * fraction1;
                    float result = y0v + (y1v - y0v) * fraction2;
                    surface.Pixels[index + channel] = (byte)Math.Clamp(MathF.Round(result * alpha, MidpointRounding.AwayFromZero), 0f, alpha);
                }
            }
        }
    }

    internal static float[] BuildCube(HueSaturationSettings settings)
    {
        // The macOS buildCube: every corner converted to HSL, adjusted, and back.
        var response = HueResponse(settings);
        var values = new float[CubeDimension * CubeDimension * CubeDimension * 4];
        double step = CubeDimension - 1;
        int index = 0;
        for (int blue = 0; blue < CubeDimension; blue++)
        {
            for (int green = 0; green < CubeDimension; green++)
            {
                for (int red = 0; red < CubeDimension; red++)
                {
                    (double r, double g, double b) = AdjustColor(red / step, green / step, blue / step, settings, response);
                    values[index] = (float)r;
                    values[index + 1] = (float)g;
                    values[index + 2] = (float)b;
                    values[index + 3] = 1;
                    index += 4;
                }
            }
        }
        return values;
    }

    private static (double R, double G, double B) AdjustColor(double red, double green, double blue, HueSaturationSettings settings, (double Shift, double Saturation, double Lightness)[] response)
    {
        ToHsl(red, green, blue, out double hue, out double saturation, out double lightness);
        double lightnessAmount;
        if (settings.Colorize)
        {
            hue = settings.Hue % 360;
            saturation = Math.Clamp(settings.Saturation / 100, 0, 1);
            lightnessAmount = settings.Lightness / 100;
        }
        else
        {
            // Every range contributes, weighted by how strongly it claims the original hue.
            var sampled = response[Math.Clamp((int)Math.Round(hue), 0, 360)];
            lightnessAmount = sampled.Lightness / 100;
            hue = (hue + sampled.Shift) % 360;
            if (hue < 0) hue += 360;
            saturation = AdjustedSaturation(saturation, sampled.Saturation);
        }
        double amount = Math.Clamp(lightnessAmount, -1, 1);
        lightness = amount >= 0 ? lightness + (1 - lightness) * amount : lightness * (1 + amount);
        return ToRgb(hue, saturation, Math.Clamp(lightness, 0, 1));
    }

    /// <summary>
    /// Photoshop's Saturation: below 0 it scales toward gray (−100 is gray); above 0 it
    /// divides by what's left, so +50 doubles it and +100 takes any color all the way.
    /// Multiplicative both ways, so neutral grays stay neutral.
    /// </summary>
    private static double AdjustedSaturation(double saturation, double amount)
    {
        amount = Math.Clamp(amount / 100, -1, 1);
        if (amount > 0)
        {
            return amount >= 1 ? (saturation > 0 ? 1 : 0) : Math.Min(1, saturation / (1 - amount));
        }
        return Math.Max(0, saturation * (1 + amount));
    }

    private static (double Shift, double Saturation, double Lightness)[] HueResponse(HueSaturationSettings settings)
    {
        var response = new (double, double, double)[361];
        for (int degree = 0; degree <= 360; degree++)
        {
            double shift = 0, saturation = 0, lightness = 0;
            foreach (var (rangeName, adjustment) in settings.Adjustments)
            {
                if (adjustment.Hue == 0 && adjustment.Saturation == 0 && adjustment.Lightness == 0) continue;
                if (!Enum.TryParse(rangeName, out ColorRange colorRange)) continue;
                double weight = settings.WeightOf(colorRange, degree);
                if (weight <= 0) continue;
                shift += adjustment.Hue * weight;
                saturation += adjustment.Saturation * weight;
                lightness += adjustment.Lightness * weight;
            }
            response[degree] = (shift, saturation, lightness);
        }
        return response;
    }

    private static void ToHsl(double red, double green, double blue, out double hue, out double saturation, out double lightness)
    {
        double high = Math.Max(red, Math.Max(green, blue)), low = Math.Min(red, Math.Min(green, blue));
        lightness = (high + low) / 2;
        double delta = high - low;
        if (delta <= 0)
        {
            hue = 0;
            saturation = 0;
            return;
        }
        saturation = Math.Min(1, delta / (1 - Math.Abs(2 * lightness - 1)));
        if (high == red) hue = (green - blue) / delta;
        else if (high == green) hue = (blue - red) / delta + 2;
        else hue = (red - green) / delta + 4;
        hue *= 60;
        if (hue < 0) hue += 360;
    }

    private static (double R, double G, double B) ToRgb(double hue, double saturation, double lightness)
    {
        if (saturation <= 0) return (lightness, lightness, lightness);
        double chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        double sector = hue / 60;
        double second = chroma * (1 - Math.Abs(sector % 2 - 1));
        double @base = lightness - chroma / 2;
        double r, g, b;
        switch ((int)sector)
        {
            case 0: (r, g, b) = (chroma, second, 0); break;
            case 1: (r, g, b) = (second, chroma, 0); break;
            case 2: (r, g, b) = (0, chroma, second); break;
            case 3: (r, g, b) = (0, second, chroma); break;
            case 4: (r, g, b) = (second, 0, chroma); break;
            default: (r, g, b) = (chroma, 0, second); break;
        }
        return (Math.Clamp(r + @base, 0, 1), Math.Clamp(g + @base, 0, 1), Math.Clamp(b + @base, 0, 1));
    }

    // --- Gaussian blur: Core Image's applyingGaussianBlur(sigma: radius), float, separable ---

    internal static void ApplyGaussianBlur(double radius, Surface surface, int x0, int y0, int x1, int y1)
    {
        double sigma = Math.Max(0.1, radius);
        int kernelRadius = Math.Max(1, (int)Math.Ceiling(sigma * 3));
        var kernel = new float[kernelRadius * 2 + 1];
        double sum = 0;
        for (int i = 0; i < kernel.Length; i++)
        {
            double x = i - kernelRadius;
            kernel[i] = (float)Math.Exp(-x * x / (2 * sigma * sigma));
            sum += kernel[i];
        }
        for (int i = 0; i < kernel.Length; i++) kernel[i] /= (float)sum;

        // The horizontal pass reads the source and writes a separate plane, so the pixels
        // can serve as that source directly; only the vertical pass writes them back.
        var horizontal = new byte[surface.Pixels.Length];
        // Split by rows: an output row reads source rows and writes its own row, and the clamp is to the
        // surface's edges, not to the region's, so a row computed on a worker is byte-for-byte the row the
        // single thread would write. Measured need (Windows/docs/filterpreview-4k-perf-2026-10-21.md): the
        // Tonal Contrast base is this pass times a radius, 33.2 s per preview tick at 4096 on one core.
        // Below the threshold the row work is smaller than the scheduling, and a small window keeps the
        // single-threaded path the allocation tests measure.
        if ((long)(y1 - y0) * (x1 - x0) < parallelGaussianPixels)
        {
            ConvolveHorizontal(surface.Pixels, horizontal, surface, kernel, x0, y0, x1, y1);
            ConvolveVertical(horizontal, surface.Pixels, surface, kernel, x0, y0, x1, y1);
            return;
        }
        Parallel.For(y0, y1, y => ConvolveHorizontal(surface.Pixels, horizontal, surface, kernel, x0, y, x1, y + 1));
        Parallel.For(y0, y1, y => ConvolveVertical(horizontal, surface.Pixels, surface, kernel, x0, y, x1, y + 1));
    }

    /// <summary>How much region one blur pass has to cover before it is worth splitting across threads.
    /// Measured in one process against the same pixels run in bands that stay under this line
    /// (Windows/docs/filterpreview-4k-perf-2026-10-21.md §7): 7.3× at 512², 5.3× at 4096², so the split pays at
    /// the threshold already. Whether a smaller threshold pays too is unmeasured — the 25×25 windows the blur
    /// stamp tests allocate against stay on this side of the line deliberately.</summary>
    internal const long parallelGaussianPixels = 1 << 18;

    private static void ConvolveHorizontal(byte[] source, byte[] target, Surface surface, float[] kernel, int x0, int y0, int x1, int y1)
    {
        int kernelRadius = kernel.Length / 2;
        for (int y = y0; y < y1; y++)
        {
            int row = y * surface.Stride;
            for (int x = x0; x < x1; x++)
            {
                int index = row + x * 4;
                for (int channel = 0; channel < 4; channel++)
                {
                    float sum = 0;
                    for (int i = 0; i < kernel.Length; i++)
                    {
                        int sampleX = Math.Clamp(x + i - kernelRadius, 0, surface.Width - 1);
                        sum += kernel[i] * source[row + sampleX * 4 + channel];
                    }
                    target[index + channel] = (byte)Math.Clamp(MathF.Round(sum, MidpointRounding.AwayFromZero), 0, 255);
                }
            }
        }
    }

    private static void ConvolveVertical(byte[] source, byte[] target, Surface surface, float[] kernel, int x0, int y0, int x1, int y1)
    {
        int kernelRadius = kernel.Length / 2;
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                int index = y * surface.Stride + x * 4;
                for (int channel = 0; channel < 4; channel++)
                {
                    float sum = 0;
                    for (int i = 0; i < kernel.Length; i++)
                    {
                        int sampleY = Math.Clamp(y + i - kernelRadius, 0, surface.Height - 1);
                        sum += kernel[i] * source[sampleY * surface.Stride + x * 4 + channel];
                    }
                    target[index + channel] = (byte)Math.Clamp(MathF.Round(sum, MidpointRounding.AwayFromZero), 0, 255);
                }
            }
        }
    }

    // --- Motion blur: Core Image's CIMotionBlur, a line kernel of half length radius ---

    /// <summary>The streak a motion blur draws, with explicit geometry: the destructive filter sheet
    /// runs it on a grown grid (the picture dissolves into the padding along the travel), while the
    /// live adjustment reads its own stored angle and distance through this same door. The Mac hands
    /// Core Image distance/√12 as the radius (Filters.swift:173-175) and measures the angle
    /// counterclockwise in y-up space; this grid is y-down.</summary>
    internal static void ApplyMotionBlur(double angleDegrees, double distance, Surface surface, int x0, int y0, int x1, int y1)
    {
        // The macOS filter multiplies the distance by 1/√12 before handing it to Core Image.
        double halfLength = distance / Math.Sqrt(12);
        double deltaX = Math.Cos(angleDegrees * Math.PI / 180);
        // Core Image measures the angle counterclockwise in a y-up space; ours is y-down.
        double deltaY = -Math.Sin(angleDegrees * Math.PI / 180);
        int steps = Math.Max(2, Math.Min(128, (int)Math.Ceiling(halfLength * 2)));
        // A gaussian that tapers along the travel — full weight at the center of the line,
        // exp(−½) at its ends, σ the half length — instead of a box kernel, whose hard ends
        // print as banding across the blur. The samples and weights depend only on the
        // geometry, so both are computed once per pass.
        var offsets = new double[steps];
        var weights = new float[steps];
        double weightSum = 0;
        for (int step = 0; step < steps; step++)
        {
            double t = step / (double)(steps - 1);
            double offset = (t - 0.5) * halfLength * 2;
            offsets[step] = offset;
            weights[step] = (float)Math.Exp(-offset * offset / (2 * halfLength * halfLength));
            weightSum += weights[step];
        }
        for (int step = 0; step < steps; step++) weights[step] /= (float)weightSum;
        byte[] working = (byte[])surface.Pixels.Clone();
        for (int y = y0; y < y1; y++)
        {
            int row = y * surface.Stride;
            for (int x = x0; x < x1; x++)
            {
                int index = row + x * 4;
                for (int channel = 0; channel < 4; channel++)
                {
                    float sum = 0;
                    for (int step = 0; step < steps; step++)
                    {
                        int sampleX = Math.Clamp((int)MathF.Round((float)(x + deltaX * offsets[step])), 0, surface.Width - 1);
                        int sampleY = Math.Clamp((int)MathF.Round((float)(y + deltaY * offsets[step])), 0, surface.Height - 1);
                        sum += weights[step] * working[sampleY * surface.Stride + sampleX * 4 + channel];
                    }
                    surface.Pixels[index + channel] = (byte)Math.Clamp(MathF.Round(sum, MidpointRounding.AwayFromZero), 0, 255);
                }
            }
        }
    }

    // --- Add Noise (NoisePixels.c noise_add_at) ---

    private static void ApplyAddNoise(LayerAdjustment adjustment, Surface surface, int x0, int y0, int x1, int y1)
        => ApplyAddNoise(surface, x0, y0, x1, y1, adjustment.ResolvedNoiseAmount,
            adjustment.NoiseGaussian == true, adjustment.NoiseMonochromatic == true, adjustment.NoiseSeed ?? 0);

    /// <summary>Add Noise over a region with explicit parameters: the destructive filter sheet holds one
    /// seed while it is open (Filters.swift:301, so moving Amount does not reshuffle the grain), which a
    /// LayerAdjustment cannot carry. The adjustment path reads its stored seed through the same door.</summary>
    public static void ApplyAddNoise(Surface surface, int x0, int y0, int x1, int y1,
        double amount, bool gaussian, bool monochromatic, uint seed)
    {

        float spread = (float)amount / 100.0f * 127.5f;
        for (int y = y0; y < y1; y++)
        {
            int row = y * surface.Stride;
            for (int x = x0; x < x1; x++)
            {
                int index = row + x * 4;
                uint alpha = surface.Pixels[index + 3];
                if (alpha == 0) continue;
                uint px = (uint)x;
                uint py = (uint)y;
                uint baseHash = NoiseHash(seed ^ NoiseHash(px * 0x9e3779b9u ^ NoiseHash(py * 0x85ebca6bu)));
                for (int channel = 0; channel < 3; channel++)
                {
                    uint key = monochromatic ? baseHash : baseHash + (uint)channel * 0x9e3779b9u;
                    float noise;
                    if (gaussian)
                    {
                        // Box–Muller: two uniform values make one normally distributed one.
                        float u1 = NoiseUnit(key), u2 = NoiseUnit(key ^ 0x68e31da4u);
                        noise = MathF.Sqrt(-2f * MathF.Log(1f - u1)) * MathF.Cos(6.2831853f * u2) * spread * (2f / 3f);
                    }
                    else
                    {
                        noise = (NoiseUnit(key) * 2f - 1f) * spread;
                    }
                    float value = surface.Pixels[index + channel] * 255f / alpha + noise;
                    value = Math.Clamp(value, 0f, 255f);
                    surface.Pixels[index + channel] = (byte)MathF.Round(value * alpha / 255f, MidpointRounding.AwayFromZero);
                }
            }
        }
    }

    private static uint NoiseHash(uint x)
    {
        x ^= x >> 16;
        x *= 0x7feb352du;
        x ^= x >> 15;
        x *= 0x846ca68bu;
        x ^= x >> 16;
        return x;
    }

    private static float NoiseUnit(uint key) => (NoiseHash(key) >> 8) * (1f / 16777216f);

    // The deterministic Box-Muller basis is independent of image pixels and Amount.
    // GPU presentation reuses it instead of relying on driver-specific log/cos approximations.
    internal static float[] GaussianNoiseBasis(int width, int height, uint seed)
    {
        var values = new float[checked(width * height * 4)];
        Parallel.For(0, height, ParallelPointOptions, y =>
        {
            for (int x = 0; x < width; x++)
            {
                uint hash = NoiseHash(seed ^ NoiseHash((uint)x * 0x9e3779b9u ^ NoiseHash((uint)y * 0x85ebca6bu)));
                for (uint channel = 0; channel < 3; channel++)
                {
                    uint key = hash + channel * 0x9e3779b9u;
                    float u1 = NoiseUnit(key), u2 = NoiseUnit(key ^ 0x68e31da4u);
                    values[(y * width + x) * 4 + channel] = MathF.Sqrt(-2f * MathF.Log(1f - u1)) * MathF.Cos(6.2831853f * u2);
                }
            }
        });
        return values;
    }

    // --- Grain (AdjustPixels.c adjust_grain) ---

    /// <summary>Camera Raw's grain pass: the same adjust_grain kernel over the whole surface.</summary>
    public static void ApplyCameraRawGrain(GrainSettings settings, Surface surface) =>
        ApplyGrain(settings, surface, 0, 0, surface.Width, surface.Height);

    private static void ApplyGrain(GrainSettings settings, Surface surface, int x0, int y0, int x1, int y1)
    {
        if (settings.Amount <= 0) return;
        double size = settings.Size > 0 ? settings.Size : 1;
        float strength = (float)(settings.Amount > 100 ? 1.0 : settings.Amount / 100.0) * 0.35f * 255.0f;
        float rough = (float)Math.Clamp(settings.Roughness / 100.0, 0, 1);
        uint fineSeed = NoiseHash(settings.Seed ^ 0xA511E9B3u);
        // Roughness adds smaller, less regular particles, as in Photoshop, but their size
        // remains proportional to the Size control instead of collapsing to one-pixel noise.
        double detailSize = Math.Max(0.5, size * 0.35);

        for (int y = y0; y < y1; y++)
        {
            // Document pixels at one unit per pixel; the pattern stays fixed in the document.
            double v = y + 0.5;
            int row = y * surface.Stride;
            for (int x = x0; x < x1; x++)
            {
                int index = row + x * 4;
                uint alpha = surface.Pixels[index + 3];
                if (alpha == 0) continue;
                double u = x + 0.5;
                float smooth = GrainField(u, v, size, settings.Seed);
                float fine = GrainField(u, v, detailSize, fineSeed);
                float noise = smooth + (fine - smooth) * rough;
                float unpremultiply = alpha == 255 ? 1.0f : 255f / alpha;
                float r = surface.Pixels[index] * unpremultiply;
                float g = surface.Pixels[index + 1] * unpremultiply;
                float b = surface.Pixels[index + 2] * unpremultiply;
                float level = Math.Min(1f, (0.2126f * r + 0.7152f * g + 0.0722f * b) / 255f);
                // Film grain shows most in the midtones.
                float delta = noise * strength * (0.4f + 2.4f * level * (1.0f - level));
                float coverage = alpha / 255f;
                surface.Pixels[index] = (byte)(Clamp255(r + delta) * coverage + 0.5f);
                surface.Pixels[index + 1] = (byte)(Clamp255(g + delta) * coverage + 0.5f);
                surface.Pixels[index + 2] = (byte)(Clamp255(b + delta) * coverage + 0.5f);
            }
        }
    }

    private static float Clamp255(float value) => value < 0 ? 0 : value > 255 ? 255 : value;

    /// <summary>A value in −1…1 for an integer lattice point, fixed by the point and the seed.
    /// Two uniform halves summed give a triangular spread, closer to film grain than flat noise.</summary>
    private static float Lattice(long ix, long iy, uint seed)
    {
        uint hash = Mix32((uint)ix * 0x9E3779B1u ^ Mix32((uint)iy * 0x85EBCA77u ^ seed));
        return (hash & 0xFFFFu) / 65535f + (hash >> 16) / 65535f - 1f;
    }

    private static uint Mix32(uint x)
    {
        x ^= x >> 16;
        x *= 0x7feb352du;
        x ^= x >> 15;
        x *= 0x846ca68bu;
        x ^= x >> 16;
        return x;
    }

    /// <summary>Smooth seeded noise whose features follow `scale` document pixels.</summary>
    private static float GrainField(double u, double v, double scale, uint seed)
    {
        double cellX = Math.Floor(u / scale), cellY = Math.Floor(v / scale);
        float tx = (float)(u / scale - cellX), ty = (float)(v / scale - cellY);
        tx = tx * tx * (3f - 2f * tx);
        ty = ty * ty * (3f - 2f * ty);
        long ix = (long)cellX, iy = (long)cellY;
        float n00 = Lattice(ix, iy, seed), n10 = Lattice(ix + 1, iy, seed);
        float n01 = Lattice(ix, iy + 1, seed), n11 = Lattice(ix + 1, iy + 1, seed);
        float top = n00 + (n10 - n00) * tx, bottom = n01 + (n11 - n01) * tx;
        // Blending neighboring lattice values narrows the spread; restore approximately its original range.
        return (top + (bottom - top) * ty) * 1.6f;
    }
}
