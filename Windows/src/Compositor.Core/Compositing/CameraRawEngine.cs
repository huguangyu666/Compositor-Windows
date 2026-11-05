namespace Compositor.Core;

/// <summary>
/// The Camera Raw light/color kernel, ported line for line from the native
/// adjust_camera_raw in macOS AdjustPixels.c: unpremultiply → sRGB-to-linear → white
/// balance gains × exposure → contrast pivot at mid-gray → highlight/shadow/white/black
/// luminance scaling (Rec. 709 targets) → vibrance with skin-tone protection → saturation.
/// </summary>
public static class CameraRawEngine
{
    /// <summary>The Option-drag clipping views, in the kernel's own numbering (CameraRaw.swift:38-44):
    /// clipped highlights lit on black, clipped shadows cut out of white. 0 renders the grade.</summary>
    public const int ClippingNone = 0;
    public const int ClippingHighlights = 1;
    public const int ClippingShadows = 2;

    /// <summary>Which read a light slider carries (<c>CameraRawControls.swift:169-179</c>): Exposure, Highlights
    /// and Whites ask for highlights, Shadows and Blacks for shadows, and Contrast — the one light number in
    /// between — carries nothing. Anything else the panel has is none as well.</summary>
    public static int LightClippingOf(string setting) => setting switch
    {
        nameof(CameraRawSettings.Exposure) => ClippingHighlights,
        nameof(CameraRawSettings.Highlights) => ClippingHighlights,
        nameof(CameraRawSettings.Whites) => ClippingHighlights,
        nameof(CameraRawSettings.Shadows) => ClippingShadows,
        nameof(CameraRawSettings.Blacks) => ClippingShadows,
        _ => ClippingNone,
    };

    internal static double Clamp01(double value)
    {
        if (value < 0) return 0;
        if (value > 1) return 1;
        return value;
    }

    private static double SrgbToLinear(double encoded)
    {
        if (encoded <= 0.04045) return encoded / 12.92;
        return Math.Pow((encoded + 0.055) / 1.055, 2.4);
    }

    private static double LinearToSrgb(double linear)
    {
        if (linear <= 0) return 0;
        if (linear >= 1) return 1;
        if (linear <= 0.0031308) return linear * 12.92;
        return 1.055 * Math.Pow(linear, 1.0 / 2.4) - 0.055;
    }

    internal static double Rec709(double r, double g, double b)
    {
        return 0.2126 * r + 0.7152 * g + 0.0722 * b;
    }

    /// <summary>Moves r, g, b so their Rec. 709 luminance becomes target, keeping the hue.
    /// Pure black cannot be scaled, so a lift paints neutral light of that luminance.</summary>
    private static void ScaleLuminance(ref double r, ref double g, ref double b, double target)
    {
        target = Clamp01(target);
        double y = Rec709(r, g, b);
        if (Math.Abs(target - y) < 1e-8) return;
        if (y < 1e-8)
        {
            if (target > y) r = g = b = target;
            return;
        }
        double scale = target / y;
        r = Clamp01(r * scale);
        g = Clamp01(g * scale);
        b = Clamp01(b * scale);
    }

    private static double ToneHighlights(double y, double amount)
    {
        double t = Clamp01((y - 0.5) / 0.5);
        double weight = t * t;
        if (amount >= 0) return Clamp01(y + amount * weight * (1.0 - y));
        return Clamp01(y + amount * weight * (y - 0.5));
    }

    private static double ToneShadows(double y, double amount)
    {
        double t = Clamp01((0.5 - y) / 0.5);
        double weight = t * t;
        if (amount >= 0) return Clamp01(y + amount * weight * (0.5 - y));
        return Clamp01(y + amount * weight * y);
    }

    private static double ToneWhites(double y, double amount)
    {
        if (y <= 0.75) return y;
        return Clamp01(0.75 + (y - 0.75) * (1.0 + amount));
    }

    private static double ToneBlacks(double y, double amount)
    {
        if (y >= 0.25) return y;
        return Clamp01(0.25 + (y - 0.25) * (1.0 - amount));
    }

    private static void VibranceAndSaturation(ref double r, ref double g, ref double b, double vibrance, double saturation)
    {
        double lum = Rec709(r, g, b);
        double maxc = Math.Max(r, Math.Max(g, b));
        double minc = Math.Min(r, Math.Min(g, b));
        double chroma = maxc - minc;
        double sat = maxc <= 1e-8 ? 0 : chroma / maxc;
        double hue = 0;
        if (chroma > 1e-8)
        {
            if (r >= g && r >= b) hue = 60.0 * ((g - b) / chroma % 6.0);
            else if (g >= r && g >= b) hue = 60.0 * ((b - r) / chroma + 2.0);
            else hue = 60.0 * ((r - g) / chroma + 4.0);
            if (hue < 0) hue += 360.0;
        }
        // Skin-tone protection: hues 10–50° get up to 70% less vibrance.
        double skin = 0;
        if (hue >= 10.0 && hue <= 50.0)
        {
            skin = hue <= 30.0 ? (hue - 10.0) / 20.0 : (50.0 - hue) / 20.0;
            skin *= Clamp01((sat - 0.15) / 0.35);
        }
        double amount = vibrance * (1.0 - sat);
        if (vibrance > 0) amount *= 1.0 - 0.7 * skin;
        double factor = 1.0 + amount;
        r = Clamp01(lum + (r - lum) * factor);
        g = Clamp01(lum + (g - lum) * factor);
        b = Clamp01(lum + (b - lum) * factor);
        lum = Rec709(r, g, b);
        factor = 1.0 + saturation;
        r = Clamp01(lum + (r - lum) * factor);
        g = Clamp01(lum + (g - lum) * factor);
        b = Clamp01(lum + (b - lum) * factor);
    }

    internal static void WritePremultiplied(Surface surface, int index, double r, double g, double b, double alpha)
    {
        var pixels = surface.Pixels;
        pixels[index] = (byte)Math.Min(alpha, Math.Max(0.0, Math.Round(r * alpha, MidpointRounding.AwayFromZero)));
        pixels[index + 1] = (byte)Math.Min(alpha, Math.Max(0.0, Math.Round(g * alpha, MidpointRounding.AwayFromZero)));
        pixels[index + 2] = (byte)Math.Min(alpha, Math.Max(0.0, Math.Round(b * alpha, MidpointRounding.AwayFromZero)));
    }

    /// <summary>
    /// Applies the whole grade in place (premultiplied RGBA): geometry first — it replaces the picture the rest
    /// of the passes read — then calibration, then light and color, then the point curve, color mixer and grading
    /// wheels, then effects, then grain, then optics, then detail — the macOS apply() order, with optics and
    /// detail sharing the one step the Mac runs last (<c>CameraRaw.swift:234-265</c>).
    /// <paramref name="visualizePointColor"/> is the one thing that is not a setting: the index of the point whose
    /// range the open panel is showing, dimming everything outside it. A grade with nothing set and no index is
    /// returned untouched (<c>CameraRaw.swift:221-246</c>), and the committed render never passes an index at all,
    /// which is what makes "Visualize Range" something the panel shows and OK does not keep.
    /// </summary>
    public static void Apply(Surface surface, CameraRawSettings settings, int visualizePointColor = -1,
                             int clipping = ClippingNone, bool sharpenMask = false)
    {
        if (settings.IsIdentity && visualizePointColor < 0 && clipping == ClippingNone && !sharpenMask) return;
        // What each view leaves in: the Option-drag clipping view and the sharpen mask both stop at the light
        // pass, and the mask's own kernel returns before optics or detail (CameraRaw.swift:229-241,
        // CameraRawDetailOptics.swift:100-107).
        bool paintColor = clipping == ClippingNone && !sharpenMask
            && (settings.AdjustsCurve || settings.AdjustsMixer || settings.AdjustsGrading || visualizePointColor >= 0);
        bool paintEffects = clipping == ClippingNone && !sharpenMask && settings.AdjustsEffects;
        bool paintDetailOptics = clipping == ClippingNone && (settings.AdjustsDetail || settings.AdjustsOptics || sharpenMask);
        if (clipping == ClippingNone && !sharpenMask && settings.AdjustsGeometry)
        {
            var warped = CameraRawGeometry.Apply(surface, settings);
            if (!ReferenceEquals(warped, surface)) ReplacePixels(surface, warped);
        }
        if (clipping == ClippingNone && !sharpenMask && settings.AdjustsCalibration) ApplyCalibration(surface, settings);
        if (settings.AdjustsLightColor || clipping != ClippingNone) ApplyLightColor(surface, settings, clipping);
        if (paintColor) ApplyCurveColor(surface, settings, visualizePointColor);
        if (paintEffects) ApplyEffects(surface, settings);
        if (paintDetailOptics) ApplyDetailOptics(surface, settings, sharpenMask);
    }

    /// <summary>Detail and optics in the Mac's order, with the mask view taking the whole step for itself.</summary>
    private static void ApplyDetailOptics(Surface surface, CameraRawSettings settings, bool sharpenMask)
    {
        if (sharpenMask)
        {
            ApplySharpenMask(surface, settings);
            return;
        }
        if (settings.AdjustsOptics) CameraRawOptics.Apply(surface, settings);
        if (settings.AdjustsDetail) ApplyDetail(surface, settings);
    }

    /// <summary>Moves a whole-pixel result back into the surface the caller handed in, because every other pass
    /// grades in place and the preview publishes that same buffer.</summary>
    private static void ReplacePixels(Surface target, Surface source)
    {
        for (int y = 0; y < target.Height; y++)
            Array.Copy(source.Pixels, y * source.Stride, target.Pixels, y * target.Stride, target.Width * 4);
    }

    /// <summary>
    /// The primary and shadow-tint shifts, in HSL, on the pixels before anything else touches them. A pixel's
    /// hue and saturation move only for the channel that dominates it, so a red slider leaves a green pixel alone
    /// — and the shadow tint only reaches the bottom third of the range.
    /// </summary>
    private static void ApplyCalibration(Surface surface, CameraRawSettings settings)
    {
        double scale = CameraRawSettings.CalibrationScale(settings.CalibrationProcess);
        double tint = Calibrated(settings.CalibrationShadowTint) / 100.0 * scale;
        double redHue = Calibrated(settings.CalibrationRedHue) / 100.0 * (15.0 / 360.0) * scale;
        double redSaturation = Calibrated(settings.CalibrationRedSaturation) / 100.0 * 0.45 * scale;
        double greenHue = Calibrated(settings.CalibrationGreenHue) / 100.0 * (15.0 / 360.0) * scale;
        double greenSaturation = Calibrated(settings.CalibrationGreenSaturation) / 100.0 * 0.45 * scale;
        double blueHue = Calibrated(settings.CalibrationBlueHue) / 100.0 * (15.0 / 360.0) * scale;
        double blueSaturation = Calibrated(settings.CalibrationBlueSaturation) / 100.0 * 0.45 * scale;

        for (int y = 0; y < surface.Height; y++)
        {
            int row = y * surface.Stride;
            for (int x = 0; x < surface.Width; x++)
            {
                int index = row + x * 4;
                double alpha = surface.Pixels[index + 3];
                if (alpha == 0) continue;
                double r = Math.Min(1.0, surface.Pixels[index] / alpha);
                double g = Math.Min(1.0, surface.Pixels[index + 1] / alpha);
                double b = Math.Min(1.0, surface.Pixels[index + 2] / alpha);
                RgbToHsl(r, g, b, out double h, out double s, out double l);
                if (l < 0.35 && tint != 0)
                {
                    h += tint * 0.06;
                    if (h < 0) h += 1;
                    if (h >= 1) h -= 1;
                }
                double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                if (max - min > 1e-5)
                {
                    if (r >= g && r >= b) { h += redHue; s = Clamp01(s * (1 + redSaturation)); }
                    else if (g >= r && g >= b) { h += greenHue; s = Clamp01(s * (1 + greenSaturation)); }
                    else { h += blueHue; s = Clamp01(s * (1 + blueSaturation)); }
                    if (h < 0) h += 1;
                    if (h >= 1) h -= 1;
                }
                HslToRgb(h, s, l, out r, out g, out b);
                WritePremultiplied(surface, index, r, g, b, alpha);
            }
        }
    }

    /// <summary>What the Mac's sub-settings normalization does to a calibration slider: a range clamp that
    /// falls back to the neutral value rather than clamping a NaN to the edge.</summary>
    private static double Calibrated(double value) => double.IsFinite(value) ? Math.Clamp(value, -100.0, 100.0) : 0;

    /// <summary>Light and color, one pass over the pixels — and the same pass the Option-drag clipping view runs
    /// through, replacing the colors with the clip map once the grade is done (<c>AdjustPixels.c:333-344</c>).
    /// A channel counts as clipped at 0.5/255 below or 254.5/255 above, so a pixel one step off pure black still
    /// reads as clean.</summary>
    private static void ApplyLightColor(Surface surface, CameraRawSettings settings, int clipping = ClippingNone)
    {
        var (redGain, greenGain, blueGain) = settings.Gains;
        double light = Math.Pow(2, settings.Exposure);
        double contrastScale = 1.0 + settings.Contrast / 100.0;
        double highlightAmount = settings.Highlights / 100.0;
        double shadowAmount = settings.Shadows / 100.0;
        double whiteAmount = settings.Whites / 100.0;
        double blackAmount = settings.Blacks / 100.0;
        double vibranceAmount = settings.Vibrance / 100.0;
        double saturationAmount = settings.Saturation / 100.0;

        for (int y = 0; y < surface.Height; y++)
        {
            int row = y * surface.Stride;
            for (int x = 0; x < surface.Width; x++)
            {
                int index = row + x * 4;
                double alpha = surface.Pixels[index + 3];
                if (alpha == 0) continue;
                double r = Math.Min(255.0, surface.Pixels[index] * 255.0 / alpha) / 255.0;
                double g = Math.Min(255.0, surface.Pixels[index + 1] * 255.0 / alpha) / 255.0;
                double b = Math.Min(255.0, surface.Pixels[index + 2] * 255.0 / alpha) / 255.0;
                r = Clamp01(SrgbToLinear(r) * redGain * light);
                g = Clamp01(SrgbToLinear(g) * greenGain * light);
                b = Clamp01(SrgbToLinear(b) * blueGain * light);
                r = Clamp01(0.5 + (LinearToSrgb(r) - 0.5) * contrastScale);
                g = Clamp01(0.5 + (LinearToSrgb(g) - 0.5) * contrastScale);
                b = Clamp01(0.5 + (LinearToSrgb(b) - 0.5) * contrastScale);
                ScaleLuminance(ref r, ref g, ref b, ToneHighlights(Rec709(r, g, b), highlightAmount));
                ScaleLuminance(ref r, ref g, ref b, ToneShadows(Rec709(r, g, b), shadowAmount));
                ScaleLuminance(ref r, ref g, ref b, ToneWhites(Rec709(r, g, b), whiteAmount));
                ScaleLuminance(ref r, ref g, ref b, ToneBlacks(Rec709(r, g, b), blackAmount));
                VibranceAndSaturation(ref r, ref g, ref b, vibranceAmount, saturationAmount);
                if (clipping == ClippingHighlights)
                {
                    bool rc = r >= 254.5 / 255.0, gc = g >= 254.5 / 255.0, bc = b >= 254.5 / 255.0;
                    r = rc ? 1 : 0;
                    g = gc ? 1 : 0;
                    b = bc ? 1 : 0;
                }
                else if (clipping == ClippingShadows)
                {
                    bool rc = r <= 0.5 / 255.0, gc = g <= 0.5 / 255.0, bc = b <= 0.5 / 255.0;
                    if (rc || gc || bc)
                    {
                        r = rc ? 0 : 1;
                        g = gc ? 0 : 1;
                        b = bc ? 0 : 1;
                    }
                    else
                    {
                        r = g = b = 1;
                    }
                }
                WritePremultiplied(surface, index, r, g, b, alpha);
            }
        }
    }

    // --- Point curve and Color Mixer (adjust_camera_raw_curve_color) ---

    /// <summary>
    /// The Hermite evaluation through a 0…1 point list, as the Mac's point() helper: the same
    /// monotone cubic as <see cref="CurvesSettings.Value"/> on the panel's 0–255 scale, computed
    /// in place over the given points with no per-call allocations (the old path built a fresh
    /// CurvesSettings and point list for every probe). A list with fewer than two points, like
    /// the Mac's guard, leaves the input unchanged.
    /// </summary>
    public static double CurveAt(List<CurvePoint> points, double x)
    {
        int count = points.Count;
        if (count < 2) return x;
        double sx = x * 255.0;
        double X(int k) => points[k].X * 255.0;
        double Y(int k) => points[k].Y * 255.0;
        int last = 0;
        for (int k = count - 1; k >= 0; k--)
        {
            if (X(k) <= sx) { last = k; break; }
        }
        int i = Math.Min(count - 2, Math.Max(0, last));
        double Secant(int k) => (Y(k + 1) - Y(k)) / (X(k + 1) - X(k));
        double Slope(int j) =>
            j == 0 ? Secant(0)
            : j == count - 1 ? Secant(count - 2)
            : Secant(j - 1) * Secant(j) <= 0 ? 0
            : 2 / (1 / Secant(j - 1) + 1 / Secant(j));
        double h = X(i + 1) - X(i);
        double t = Math.Clamp((sx - X(i)) / h, 0, 1);
        double y = (2 * t * t * t - 3 * t * t + 1) * Y(i)
            + (t * t * t - 2 * t * t + t) * h * Slope(i)
            + (-2 * t * t * t + 3 * t * t) * Y(i + 1)
            + (t * t * t - t * t) * h * Slope(i + 1);
        return Math.Clamp(y, 0, 255) / 255.0;
    }

    /// <summary>
    /// The parametric bend matched to Photoshop: Darks bends the range below the middle
    /// divider and Lights above it, Shadows and Highlights the ranges past the outer
    /// dividers, each a gamma curve that keeps the curve rising.
    /// </summary>
    private static double Bend(double tone, double lower, double low, double upper, double high)
    {
        const double strength = 1.66;
        if (tone < lower && lower > 0)
        {
            return lower * Math.Pow(tone / lower, Math.Pow(2, -low / 100 * strength));
        }
        if (tone > upper && upper < 1)
        {
            double rest = 1 - upper;
            return 1 - rest * Math.Pow((1 - tone) / rest, Math.Pow(2, high / 100 * strength));
        }
        return tone;
    }

    /// <summary>
    /// The 33 anchors the parametric sliders describe, or null when they are all at zero and
    /// the tone passes through untouched. Building them once per grade lets the LUT loop call
    /// <see cref="CurveAt"/> without allocating.
    /// </summary>
    private static List<CurvePoint>? ParametricAnchors(double shadows, double darks, double lights, double highlights,
        double shadowSplit, double darkSplit, double lightSplit)
    {
        if (shadows == 0 && darks == 0 && lights == 0 && highlights == 0)
        {
            return null;
        }
        shadowSplit /= 100;
        darkSplit /= 100;
        lightSplit /= 100;
        var anchors = new List<CurvePoint>(33);
        for (int index = 0; index <= 32; index++)
        {
            double x = index / 32.0;
            double bent = Bend(Bend(x, shadowSplit, shadows, lightSplit, highlights),
                darkSplit, darks, darkSplit, lights);
            anchors.Add(new CurvePoint(x, bent));
        }
        return anchors;
    }

    /// <summary>The four amounts and the three dividers as the render reads them: the Mac puts the whole settings
    /// through <c>normalized</c> on the way into the kernel (<c>CameraRawColor.swift:110-125</c>, called from
    /// <c>CameraRawSettings.apply</c> at :223), and the only part of that which reaches this pass is the amounts'
    /// ±100 and the dividers' interdependent floors — the shadow divider lives in 5…90, the dark one starts two
    /// points above it, the light one two points above that. The port clamps here instead of copying the settings
    /// because this is where a divider is read; the drag that moves them keeps them ordered by itself
    /// (<see cref="CameraRawTargetedDrag.DividerDrag"/>), so out-of-order numbers mean a file edited by hand.</summary>
    public static (double Shadow, double Dark, double Light) NormalizedSplits(CameraRawSettings settings)
    {
        double shadowSplit = CameraRawSettings.SliderValue(settings.CurveShadowSplit, 5, 90, 25);
        double darkSplit = CameraRawSettings.SliderValue(settings.CurveDarkSplit, shadowSplit + 2, 95, 50);
        double lightSplit = CameraRawSettings.SliderValue(settings.CurveLightSplit, darkSplit + 2, 98, 75);
        return (shadowSplit, darkSplit, lightSplit);
    }

    /// <summary>The parametric curve alone, sampled the way the panel's graph samples it — 64 points across the
    /// box, identity when the four amounts are all at zero (<c>(0..&lt;64).map { raw.curve.parametric(Double($0) / 63) }</c>,
    /// CameraRawColorControls.swift:74). Note which numbers it uses: the graph is drawn from the dividers and the
    /// amounts <b>as stored</b>, because on the Mac <c>parametric</c> reads the struct the panel is bound to and
    /// normalization happens later, inside the render. So when a divider has been dragged under a floor, this
    /// curve and the pixels the kernel will bake are two different curves, and that is the original's behavior —
    /// a stored 2 against a floored 5 gives 0.030000000 at tone 0.03 here and 0.034606043 through the kernel
    /// (Windows/tools/parametric-golden.py, last section).</summary>
    public static IReadOnlyList<double> ParametricSamples(CameraRawSettings settings, int count)
    {
        var anchors = ParametricAnchors(settings.CurveShadows, settings.CurveDarks, settings.CurveLights,
            settings.CurveHighlights, settings.CurveShadowSplit, settings.CurveDarkSplit, settings.CurveLightSplit);
        var samples = new double[count];
        for (int index = 0; index < count; index++)
        {
            double tone = count > 1 ? (double)index / (count - 1) : 0;
            samples[index] = anchors is null ? tone : CurveAt(anchors, tone);
        }
        return samples;
    }

    private static double ParametricTone(List<CurvePoint>? anchors, double tone) =>
        anchors is null ? tone : CurveAt(anchors, tone);

    /// <summary>Runs the tone curve on red, green and blue alike, then the channel curves,
    /// then the mixer's per-family hue/saturation/luminance shifts — the C kernel's order.</summary>
    private static void ApplyCurveColor(Surface surface, CameraRawSettings settings, int visualizePointColor)
    {
        var toneLut = new double[256];
        var redLut = new double[256];
        var greenLut = new double[256];
        var blueLut = new double[256];
        // The render's copy of the four amounts and three dividers: what the Mac's `normalized` hands the kernel,
        // which is a different triple from the one the panel's graph is drawn from when a divider sits under a
        // floor (see <see cref="ParametricSamples"/>).
        var (shadowSplit, darkSplit, lightSplit) = NormalizedSplits(settings);
        List<CurvePoint>? parametricAnchors = ParametricAnchors(
            CameraRawSettings.SliderValue(settings.CurveShadows, -100, 100, 0),
            CameraRawSettings.SliderValue(settings.CurveDarks, -100, 100, 0),
            CameraRawSettings.SliderValue(settings.CurveLights, -100, 100, 0),
            CameraRawSettings.SliderValue(settings.CurveHighlights, -100, 100, 0),
            shadowSplit, darkSplit, lightSplit);
        for (int i = 0; i < 256; i++)
        {
            double tone = i / 255.0;
            toneLut[i] = CurveAt(settings.CurveRgb, ParametricTone(parametricAnchors, tone));
            redLut[i] = CurveAt(settings.CurveRed, tone);
            greenLut[i] = CurveAt(settings.CurveGreen, tone);
            blueLut[i] = CurveAt(settings.CurveBlue, tone);
        }
        double refine = settings.RefineSaturation / 100.0;
        double[] mixerHue = settings.MixerHue, mixerSat = settings.MixerSaturation, mixerLum = settings.MixerLuminance;
        bool familiesActive = mixerHue.Any(v => v != 0) || mixerSat.Any(v => v != 0) || mixerLum.Any(v => v != 0);
        var normalizedPoints = CameraRawMixer.Normalize(settings.MixerPoints);
        double[] points = [.. normalizedPoints.SelectMany(CameraRawMixer.PointFloats)];
        int pointCount = normalizedPoints.Count;
        bool gradingActive = settings.AdjustsGrading;
        double[] grade = settings.GradingFloats();

        for (int y = 0; y < surface.Height; y++)
        {
            int row = y * surface.Stride;
            for (int x = 0; x < surface.Width; x++)
            {
                int index = row + x * 4;
                double alpha = surface.Pixels[index + 3];
                if (alpha == 0) continue;
                double r = Math.Min(1.0, surface.Pixels[index] / alpha);
                double g = Math.Min(1.0, surface.Pixels[index + 1] / alpha);
                double b = Math.Min(1.0, surface.Pixels[index + 2] / alpha);
                // The tone curve works on red, green and blue alike, as Photoshop's does.
                double curvedR = LutAt(toneLut, r), curvedG = LutAt(toneLut, g), curvedB = LutAt(toneLut, b);
                if (refine < 0)
                {
                    double br = r, bg = g, bb = b;
                    ScaleLuminance(ref br, ref bg, ref bb, LutAt(toneLut, Rec709(r, g, b)));
                    double k = -refine;
                    curvedR += (br - curvedR) * k;
                    curvedG += (bg - curvedG) * k;
                    curvedB += (bb - curvedB) * k;
                }
                else if (refine > 0)
                {
                    double lum = Rec709(curvedR, curvedG, curvedB), factor = 1 + refine;
                    curvedR = Clamp01(lum + (curvedR - lum) * factor);
                    curvedG = Clamp01(lum + (curvedG - lum) * factor);
                    curvedB = Clamp01(lum + (curvedB - lum) * factor);
                }
                r = LutAt(redLut, curvedR);
                g = LutAt(greenLut, curvedG);
                b = LutAt(blueLut, curvedB);
                // The kernel converts to HSL and back on every pixel it grades, whether or not anything is
                // set (AdjustPixels.c:759-785), and the point loop reads the color the previous point left
                // while the visualize test at the end reads the source — so the conversion is not something
                // this port may skip when the families happen to be idle. The two loops themselves can be
                // skipped when they would add nothing: every term is multiplied by a zero amount, and
                // dividing a zero delta by a weight sum is still zero.
                RgbToHsl(r, g, b, out double h, out double s, out double l);
                double sourceHue = h, sourceSat = s, sourceLum = l;
                if (familiesActive)
                {
                    double hueDelta = 0, satDelta = 0, lumDelta = 0, weightSum = 0;
                    for (int i = 0; i < CameraRawSettings.MixerFamilies; i++)
                    {
                        double dist = CircularDistance(h, CameraRawSettings.MixerCenters[i] / 360.0);
                        double w = 1 - dist / (40.0 / 360);
                        if (w <= 0) continue;
                        hueDelta += mixerHue[i] / 100.0 * w * (30.0 / 360);
                        satDelta += mixerSat[i] / 100.0 * w;
                        lumDelta += mixerLum[i] / 100.0 * w * 0.25;
                        weightSum += w;
                    }
                    if (weightSum > 1)
                    {
                        hueDelta /= weightSum;
                        satDelta /= weightSum;
                        lumDelta /= weightSum;
                    }
                    h += hueDelta;
                    if (h < 0) h += 1;
                    if (h >= 1) h -= 1;
                    s = Clamp01(s * (1 + satDelta));
                    l = Clamp01(l + lumDelta);
                }
                for (int i = 0; i < pointCount; i++)
                {
                    double w = CameraRawMixer.Weight(h, s, l, points, i);
                    if (w <= 0) continue;
                    int at = i * 9;
                    h += points[at + 3] * w * (30.0 / 360);
                    s = Clamp01(s * (1 + points[at + 4] * w));
                    l = Clamp01(l + points[at + 5] * w * 0.25);
                }
                if (h < 0) h += 1;
                if (h >= 1) h -= 1;
                HslToRgb(h, s, l, out r, out g, out b);
                // Only the selected point whose own Visualize flag is on gets an index at all
                // (Filters.swift:319-323), and the cut is read off the pixel before anything moved it
                // (AdjustPixels.c:808-810).
                if (visualizePointColor >= 0 && visualizePointColor < pointCount
                    && CameraRawMixer.Weight(sourceHue, sourceSat, sourceLum, points, visualizePointColor) <= 0.05)
                {
                    r *= 0.35;
                    g *= 0.35;
                    b *= 0.35;
                }
                if (gradingActive)
                {
                    // Balance moves the crossover between the shadow and highlight wheels.
                    // Toward highlights it has to move down, so more of the picture counts
                    // as highlight and the shadow wheel loses its hold.
                    double split = 0.5 - settings.GradingBalance / 100.0 * 0.2;
                    double reach = 0.12 + settings.GradingBlending / 100.0 * 0.38;
                    double tone = Rec709(r, g, b);
                    double shadowW = Clamp01((split + reach - tone) / Math.Max(0.05, reach * 2));
                    double highlightW = Clamp01((tone - (split - reach)) / Math.Max(0.05, reach * 2));
                    double midW = Clamp01(1 - Math.Abs(tone - split) / (0.35 + reach));
                    double sum = shadowW + midW + highlightW;
                    if (sum > 1e-4)
                    {
                        shadowW /= sum;
                        midW /= sum;
                        highlightW /= sum;
                    }
                    double[] weights = [shadowW, midW, highlightW, 1];
                    for (int wheel = 0; wheel < 4; wheel++)
                    {
                        double wh = grade[wheel * 3], ws = grade[wheel * 3 + 1], wl = grade[wheel * 3 + 2];
                        double w = weights[wheel];
                        if (w <= 0 || (ws <= 0 && wl == 0)) continue;
                        HslToRgb(wh, 1, 0.5, out double cr, out double cg, out double cb);
                        r = Clamp01(r + (cr - 0.5) * ws * w * 0.85);
                        g = Clamp01(g + (cg - 0.5) * ws * w * 0.85);
                        b = Clamp01(b + (cb - 0.5) * ws * w * 0.85);
                        if (wl != 0) ScaleLuminance(ref r, ref g, ref b, Clamp01(Rec709(r, g, b) + wl * 0.25 * w));
                    }
                }
                WritePremultiplied(surface, index, r, g, b, alpha);
            }
        }
    }

    private static double LutAt(double[] lut, double value)
    {
        double scaled = Clamp01(value) * 255.0;
        int lo = (int)scaled;
        int hi = lo < 255 ? lo + 1 : 255;
        double t = scaled - lo;
        return lut[lo] + (lut[hi] - lut[lo]) * t;
    }

    private static double CircularDistance(double a, double b)
    {
        double d = Math.Abs(a - b);
        return d > 0.5 ? 1 - d : d;
    }

    private static void RgbToHsl(double r, double g, double b, out double h, out double s, out double l)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        l = (max + min) * 0.5;
        double d = max - min;
        if (d < 1e-6)
        {
            h = 0;
            s = 0;
            return;
        }
        s = d / (1.0 - Math.Abs(2.0 * l - 1.0));
        if (max == r) h = (g - b) / d % 6.0;
        else if (max == g) h = (b - r) / d + 2.0;
        else h = (r - g) / d + 4.0;
        h /= 6.0;
        if (h < 0) h += 1;
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 0.5) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }

    private static void HslToRgb(double h, double s, double l, out double r, out double g, out double b)
    {
        if (s <= 1e-6)
        {
            r = g = b = l;
            return;
        }
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;
        r = HueToRgb(p, q, h + 1.0 / 3);
        g = HueToRgb(p, q, h);
        b = HueToRgb(p, q, h - 1.0 / 3);
    }

    // --- Effects (adjust_camera_raw_effects) ---

    /// <summary>Edge-clamped box blur with a sliding window; dst may not alias src.</summary>
    private static void BoxBlurPlane(float[] src, float[] dst, int width, int height, int radius)
    {
        if (radius < 1)
        {
            Array.Copy(src, dst, src.Length);
            return;
        }
        int window = radius * 2 + 1;
        var temp = new float[width * height];
        for (int y = 0; y < height; y++)
        {
            double sum = 0;
            for (int k = -radius; k <= radius; k++) sum += src[y * width + ClampedIndex(k, width)];
            for (int x = 0; x < width; x++)
            {
                temp[y * width + x] = (float)(sum / window);
                sum += src[y * width + ClampedIndex(x + radius + 1, width)];
                sum -= src[y * width + ClampedIndex(x - radius, width)];
            }
        }
        for (int x = 0; x < width; x++)
        {
            double sum = 0;
            for (int k = -radius; k <= radius; k++) sum += temp[ClampedIndex(k, height) * width + x];
            for (int y = 0; y < height; y++)
            {
                dst[y * width + x] = (float)(sum / window);
                sum += temp[ClampedIndex(y + radius + 1, height) * width + x];
                sum -= temp[ClampedIndex(y - radius, height) * width + x];
            }
        }
    }

    private static int ClampedIndex(int index, int limit)
    {
        if (index < 0) return 0;
        if (index >= limit) return limit - 1;
        return index;
    }

    private static int EffectsRadius(double radius)
    {
        if (radius < 1) radius = 1;
        if (radius > 64) radius = 64;
        return (int)Math.Round(radius);
    }

    private static void EffectsDehaze(ref double r, ref double g, ref double b, double amount)
    {
        double d = amount / 100.0;
        double y = Rec709(r, g, b);
        double contrast = 1.0 + 0.8 * d;
        double pivot = 0.45 - 0.1 * (d > 0 ? d : 0);
        double y2 = Clamp01(pivot + (y - 0.45) * contrast);
        if (d < 0) y2 = Clamp01(y2 + (-d) * (1.0 - y2) * 0.45);
        else y2 = Clamp01(y2 - d * Math.Max(0.0, 0.4 - y2));
        ScaleLuminance(ref r, ref g, ref b, y2);
        y2 = Rec709(r, g, b);
        double sat = 1.0 + 0.7 * d;
        r = Clamp01(y2 + (r - y2) * sat);
        g = Clamp01(y2 + (g - y2) * sat);
        b = Clamp01(y2 + (b - y2) * sat);
    }

    /// <summary>The vignette's strength at a point of the frame: 0 at its middle, 1 past its edges.</summary>
    private static double VignetteMask(int x, int y, int width, int height, CameraRawSettings settings)
    {
        double nx = (x + 0.5) / width * 2.0 - 1.0;
        double ny = (y + 0.5) / height * 2.0 - 1.0;
        double square = Math.Max(Math.Abs(nx), Math.Abs(ny));
        double circle = Math.Sqrt(nx * nx + ny * ny) / Math.Sqrt(2.0);
        double shape = (1.0 - settings.VignetteRoundness / 100.0) * 0.5;
        double dist = circle + (square - circle) * shape;
        double start = (settings.VignetteMidpoint / 100.0) * 0.85;
        double soft = settings.VignetteFeather / 100.0;
        if (soft < 0.05) soft = 0.05;
        double t = Clamp01((dist - start) / soft);
        return t * t * (3.0 - 2.0 * t);
    }

    private static void EffectsVignette(ref double r, ref double g, ref double b, int x, int y, int width, int height,
        CameraRawSettings settings)
    {
        if (settings.VignetteAmount == 0 || width == 0 || height == 0) return;
        double mask = VignetteMask(x, y, width, height, settings);
        double effect = settings.VignetteAmount / 100.0 * mask;
        // Highlight Priority eases a darkening vignette off bright pixels. The other style does not.
        if (effect < 0 && settings.VignetteStyle == 0)
        {
            double bright = Clamp01((Rec709(r, g, b) - 0.45) / 0.55);
            effect *= 1.0 - settings.VignetteHighlights / 100.0 * bright;
        }
        if (effect < 0)
        {
            double factor = 1.0 + effect;
            r *= factor;
            g *= factor;
            b *= factor;
        }
        else if (effect > 0)
        {
            r += (1.0 - r) * effect;
            g += (1.0 - g) * effect;
            b += (1.0 - b) * effect;
        }
        if (settings.VignetteStyle == 1 && mask > 0)
        {
            double lum = Rec709(r, g, b);
            double sat = 1.0 - 0.75 * mask * Math.Abs(settings.VignetteAmount / 100.0);
            r = Clamp01(lum + (r - lum) * sat);
            g = Clamp01(lum + (g - lum) * sat);
            b = Clamp01(lum + (b - lum) * sat);
        }
    }

    /// <summary>Texture and clarity sharpen through a fine and a coarse luma band, dehaze is a
    /// contrast-plus-decolor pivot, and the vignette wraps it up — the C kernel's order.</summary>
    private static void ApplyEffects(Surface surface, CameraRawSettings settings)
    {
        int width = surface.Width, height = surface.Height;
        if (width == 0 || height == 0) return;
        double texture = settings.Texture / 100.0, clarity = settings.Clarity / 100.0, dehaze = settings.Dehaze / 100.0;
        bool needsLuma = settings.Texture != 0 || settings.Clarity != 0 || settings.Glow > 0;
        var luma = needsLuma ? new float[width * height] : null;
        var fine = settings.Texture != 0 ? new float[width * height] : null;
        var coarse = settings.Clarity != 0 ? new float[width * height] : null;
        float[]? glowPlane = null;
        double glowRed = 0, glowGreen = 0, glowBlue = 0, glowGain = 0;
        if (luma != null)
        {
            for (int y = 0; y < height; y++)
            {
                int row = y * surface.Stride;
                for (int x = 0; x < width; x++)
                {
                    int index = row + x * 4;
                    double alpha = surface.Pixels[index + 3];
                    if (alpha == 0)
                    {
                        luma[y * width + x] = 0;
                        continue;
                    }
                    double r = Math.Min(1.0, surface.Pixels[index] / alpha);
                    double g = Math.Min(1.0, surface.Pixels[index + 1] / alpha);
                    double b = Math.Min(1.0, surface.Pixels[index + 2] / alpha);
                    luma[y * width + x] = (float)Rec709(r, g, b);
                }
            }
            if (fine != null) BoxBlurPlane(luma, fine, width, height, EffectsRadius(1));
            if (coarse != null) BoxBlurPlane(luma, coarse, width, height, EffectsRadius(4));
            if (settings.Glow > 0)
            {
                // The glow source is the luma above a threshold; spread widens the blur, and
                // the style picks the look — Warmth tints Diffusion and Bloom, Halation is red.
                double spread = settings.GlowSpread / 100.0;
                double baseRadius = settings.GlowStyle == 1 ? 2.0 : 5.0;
                double widened = Math.Max(1, baseRadius * (1.0 + spread));
                float threshold = (float)(0.55 + 0.4 * (settings.GlowRange / 100.0));
                var source = new float[width * height];
                glowPlane = new float[width * height];
                float denom = Math.Max(0.05f, 1f - threshold);
                for (int i = 0; i < source.Length; i++)
                {
                    source[i] = Math.Clamp((luma[i] - threshold) / denom, 0, 1);
                }
                BoxBlurPlane(source, glowPlane, width, height, EffectsRadius(widened));
                double warmth = settings.GlowWarmth / 100.0;
                if (settings.GlowStyle == 2)
                {
                    glowRed = 1;
                    glowGreen = 0.35 - 0.3 * warmth;
                    glowBlue = 0.2 - 0.2 * warmth;
                    glowGain = 1;
                }
                else
                {
                    glowRed = 0.75 + 0.25 * warmth;
                    glowGreen = 0.6 + 0.2 * warmth;
                    glowBlue = 0.75 - 0.6 * warmth;
                    glowGain = settings.GlowStyle == 1 ? 1.4 : 1;
                }
            }
        }

        for (int y = 0; y < height; y++)
        {
            int row = y * surface.Stride;
            for (int x = 0; x < width; x++)
            {
                int index = row + x * 4;
                double alpha = surface.Pixels[index + 3];
                if (alpha == 0) continue;
                int plane = y * width + x;
                double r = Math.Min(1.0, surface.Pixels[index] / alpha);
                double g = Math.Min(1.0, surface.Pixels[index + 1] / alpha);
                double b = Math.Min(1.0, surface.Pixels[index + 2] / alpha);
                if (fine != null || coarse != null)
                {
                    double tone = Rec709(r, g, b);
                    double detail = 0;
                    if (fine != null) detail += texture * (tone - fine[plane]);
                    if (coarse != null) detail += clarity * (tone - coarse[plane]);
                    if (detail != 0) ScaleLuminance(ref r, ref g, ref b, Clamp01(tone + detail));
                }
                if (dehaze != 0) EffectsDehaze(ref r, ref g, ref b, settings.Dehaze);
                if (glowPlane != null)
                {
                    double add = glowPlane[plane] * (settings.Glow / 100.0) * glowGain;
                    r = Clamp01(r + add * glowRed);
                    g = Clamp01(g + add * glowGreen);
                    b = Clamp01(b + add * glowBlue);
                }
                EffectsVignette(ref r, ref g, ref b, x, y, width, height, settings);
                WritePremultiplied(surface, index, r, g, b, alpha);
            }
        }

        if (settings.GrainAmount > 0)
        {
            var grain = new GrainSettings
            {
                Amount = settings.GrainAmount,
                Size = settings.GrainKernelSize,
                Roughness = settings.GrainRoughness,
                Seed = settings.GrainSeed,
            };
            AdjustmentEngine.ApplyCameraRawGrain(grain, surface);
        }
    }

    /// <summary>The mean |neighbor − center| over an eight-point ring, as sharpen_edge_at.</summary>
    private static float SharpenEdgeAt(float[] luma, int width, int height, int x, int y, int radius)
    {
        if (radius < 1) radius = 1;
        float center = luma[y * width + x];
        float sum = 0;
        int count = 0;
        for (int dy = -radius; dy <= radius; dy += radius)
        {
            for (int dx = -radius; dx <= radius; dx += radius)
            {
                if (dx == 0 && dy == 0) continue;
                int sx = x + dx, sy = y + dy;
                if (sx < 0 || sy < 0 || sx >= width || sy >= height) continue;
                sum += Math.Abs(luma[sy * width + sx] - center);
                count++;
            }
        }
        return count > 0 ? sum / count : 0;
    }

    /// <summary>The Mac's detail_radius: 0.5 + slider/100 × 2.5, clamped to 0.5…64.</summary>
    private static double DetailRadius(double slider)
    {
        double radius = 0.5 + slider / 100.0 * 2.5;
        return Math.Clamp(radius, 0.5, 64);
    }

    /// <summary>
    /// Manual noise reduction (luminance first, then color) followed by sharpening, in the
    /// adjust_camera_raw_detail order. Both reductions blend toward a blurred plane, eased
    /// off edges so detail survives.
    /// </summary>
    private static void ApplyDetail(Surface surface, CameraRawSettings settings)
    {
        int width = surface.Width, height = surface.Height;
        if (width == 0 || height == 0) return;
        var luma = new float[width * height];
        var work = new float[width * height];

        void FillLuma()
        {
            for (int y = 0; y < height; y++)
            {
                int row = y * surface.Stride;
                for (int x = 0; x < width; x++)
                {
                    int index = row + x * 4;
                    double alpha = surface.Pixels[index + 3];
                    if (alpha == 0)
                    {
                        luma[y * width + x] = 0;
                        continue;
                    }
                    double r = Math.Min(1.0, surface.Pixels[index] / alpha);
                    double g = Math.Min(1.0, surface.Pixels[index + 1] / alpha);
                    double b = Math.Min(1.0, surface.Pixels[index + 2] / alpha);
                    luma[y * width + x] = (float)Rec709(r, g, b);
                }
            }
        }

        if (settings.NoiseLuminance > 0)
        {
            FillLuma();
            int radius = EffectsRadius(1.0 + settings.NoiseLuminance / 50.0);
            BoxBlurPlane(luma, work, width, height, radius);
            double strength = settings.NoiseLuminance / 100.0;
            double preserve = settings.NoiseLuminanceDetail / 100.0;
            double contrast = settings.NoiseLuminanceContrast / 100.0;
            for (int y = 0; y < height; y++)
            {
                int row = y * surface.Stride;
                for (int x = 0; x < width; x++)
                {
                    int index = row + x * 4;
                    double alpha = surface.Pixels[index + 3];
                    if (alpha == 0) continue;
                    int plane = y * width + x;
                    float edge = SharpenEdgeAt(luma, width, height, x, y, 1);
                    double local = strength * (1.0 - preserve * Math.Min(1.0, edge * 6.0));
                    float blurred = work[plane];
                    float target = (float)(luma[plane] * (1.0 - local) + blurred * local);
                    if (contrast != 0) target += (float)(contrast * 0.25 * (luma[plane] - blurred));
                    luma[plane] = target;
                    double r = Math.Min(1.0, surface.Pixels[index] / alpha);
                    double g = Math.Min(1.0, surface.Pixels[index + 1] / alpha);
                    double b = Math.Min(1.0, surface.Pixels[index + 2] / alpha);
                    ScaleLuminance(ref r, ref g, ref b, target);
                    WritePremultiplied(surface, index, r, g, b, alpha);
                }
            }
        }
        if (settings.NoiseColor > 0)
        {
            int radius = EffectsRadius(1.0 + settings.NoiseColorSmoothness / 40.0);
            var chroma = new float[width * height];
            var chromaBlur = new float[width * height];
            for (int y = 0; y < height; y++)
            {
                int row = y * surface.Stride;
                for (int x = 0; x < width; x++)
                {
                    int index = row + x * 4;
                    double alpha = surface.Pixels[index + 3];
                    if (alpha == 0) continue;
                    double r = Math.Min(1.0, surface.Pixels[index] / alpha);
                    double g = Math.Min(1.0, surface.Pixels[index + 1] / alpha);
                    double b = Math.Min(1.0, surface.Pixels[index + 2] / alpha);
                    RgbToHsl(r, g, b, out _, out double s, out _);
                    chroma[y * width + x] = (float)s;
                }
            }
            BoxBlurPlane(chroma, chromaBlur, width, height, radius);
            double strength = settings.NoiseColor / 100.0;
            double preserve = settings.NoiseColorDetail / 100.0;
            for (int y = 0; y < height; y++)
            {
                int row = y * surface.Stride;
                for (int x = 0; x < width; x++)
                {
                    int index = row + x * 4;
                    double alpha = surface.Pixels[index + 3];
                    if (alpha == 0) continue;
                    int plane = y * width + x;
                    float edge = Math.Abs(chroma[plane] - chromaBlur[plane]);
                    double local = strength * (1.0 - preserve * Math.Min(1.0, edge * 4.0));
                    float sat = (float)(chroma[plane] * (1.0 - local) + chromaBlur[plane] * local);
                    double r = Math.Min(1.0, surface.Pixels[index] / alpha);
                    double g = Math.Min(1.0, surface.Pixels[index + 1] / alpha);
                    double b = Math.Min(1.0, surface.Pixels[index + 2] / alpha);
                    RgbToHsl(r, g, b, out double h, out _, out double l);
                    HslToRgb(h, sat, l, out r, out g, out b);
                    WritePremultiplied(surface, index, r, g, b, alpha);
                }
            }
        }
        if (settings.SharpenAmount > 0)
        {
            FillLuma();
            int radius = EffectsRadius(DetailRadius(settings.SharpenRadius));
            BoxBlurPlane(luma, work, width, height, radius);
            double amount = settings.SharpenAmount / 100.0;
            double detailMix = settings.SharpenDetail / 100.0;
            double threshold = settings.SharpenMasking / 100.0 * 0.35;
            for (int y = 0; y < height; y++)
            {
                int row = y * surface.Stride;
                for (int x = 0; x < width; x++)
                {
                    int index = row + x * 4;
                    double alpha = surface.Pixels[index + 3];
                    if (alpha == 0) continue;
                    int plane = y * width + x;
                    float edge = SharpenEdgeAt(luma, width, height, x, y, radius);
                    double mask = Clamp01((edge * (0.5 + detailMix) - threshold) / Math.Max(0.04, 0.35 - threshold * 0.5));
                    double high = luma[plane] - work[plane];
                    double sharpened = Clamp01(luma[plane] + high * amount * mask * (0.5 + detailMix));
                    double r = Math.Min(1.0, surface.Pixels[index] / alpha);
                    double g = Math.Min(1.0, surface.Pixels[index + 1] / alpha);
                    double b = Math.Min(1.0, surface.Pixels[index + 2] / alpha);
                    ScaleLuminance(ref r, ref g, ref b, sharpened);
                    WritePremultiplied(surface, index, r, g, b, alpha);
                }
            }
        }
    }

    /// <summary>
    /// What the sharpening will actually be allowed to do, printed as gray instead of the picture: the same ring
    /// difference the sharpen step reads, eased over the masking threshold (<c>AdjustPixels.c:859-891</c>). The
    /// gray carries the layer's own alpha, and nothing else in the detail step runs — not optics, not the
    /// sharpening itself, and with this view on the panel never reaches curve, effects or grain.
    /// </summary>
    private static void ApplySharpenMask(Surface surface, CameraRawSettings settings)
    {
        int width = surface.Width, height = surface.Height;
        if (width == 0 || height == 0) return;
        var luma = new float[width * height];
        for (int y = 0; y < height; y++)
        {
            int row = y * surface.Stride;
            for (int x = 0; x < width; x++)
            {
                int index = row + x * 4;
                double alpha = surface.Pixels[index + 3];
                if (alpha == 0) { luma[y * width + x] = 0; continue; }
                double r = Math.Min(1.0, surface.Pixels[index] / alpha);
                double g = Math.Min(1.0, surface.Pixels[index + 1] / alpha);
                double b = Math.Min(1.0, surface.Pixels[index + 2] / alpha);
                luma[y * width + x] = (float)Rec709(r, g, b);
            }
        }
        int radius = EffectsRadius(DetailRadius(settings.SharpenRadius));
        double threshold = settings.SharpenMasking / 100.0 * 0.35;
        double detailBoost = 0.5 + settings.SharpenDetail / 100.0;
        for (int y = 0; y < height; y++)
        {
            int row = y * surface.Stride;
            for (int x = 0; x < width; x++)
            {
                int index = row + x * 4;
                double alpha = surface.Pixels[index + 3];
                if (alpha == 0) continue;
                float edge = SharpenEdgeAt(luma, width, height, x, y, radius);
                double mask = Clamp01((edge * detailBoost - threshold) / Math.Max(0.04, 0.35 - threshold * 0.5));
                byte gray = (byte)Math.Round(mask * alpha, MidpointRounding.AwayFromZero);
                surface.Pixels[index] = gray;
                surface.Pixels[index + 1] = gray;
                surface.Pixels[index + 2] = gray;
            }
        }
    }

    /// <summary>
    /// The histogram's two permanent indicators, painted onto an already graded picture: clipped shadows go blue,
    /// clipped highlights go red (<c>AdjustPixels.c:502-524</c>). A channel counts as clipped at 0.5/255 below or
    /// 254.5/255 above, and the two branches are independent — but the highlight test reads the values the shadow
    /// branch just wrote, so a pixel clipped in both directions ends up blue only.
    /// </summary>
    public static void ClipOverlay(Surface surface, bool shadows, bool highlights)
    {
        if (!shadows && !highlights) return;
        for (int y = 0; y < surface.Height; y++)
        {
            int row = y * surface.Stride;
            for (int x = 0; x < surface.Width; x++)
            {
                int index = row + x * 4;
                double alpha = surface.Pixels[index + 3];
                if (alpha == 0) continue;
                double r = Math.Min(1.0, surface.Pixels[index] / alpha);
                double g = Math.Min(1.0, surface.Pixels[index + 1] / alpha);
                double b = Math.Min(1.0, surface.Pixels[index + 2] / alpha);
                if (shadows && (r <= 0.5 / 255.0 || g <= 0.5 / 255.0 || b <= 0.5 / 255.0))
                {
                    r *= 0.35;
                    g *= 0.35;
                    b = b * 0.35 + 0.65;
                }
                if (highlights && (r >= 254.5 / 255.0 || g >= 254.5 / 255.0 || b >= 254.5 / 255.0))
                {
                    r = r * 0.35 + 0.65;
                    g *= 0.35;
                    b *= 0.35;
                }
                WritePremultiplied(surface, index, r, g, b, alpha);
            }
        }
    }
}
