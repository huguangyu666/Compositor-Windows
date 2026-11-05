namespace Compositor.Core;

/// <summary>
/// The optics step of a Camera Raw grade: lens distortion, the chromatic-aberration pull, defringing and
/// vignetting correction. It mirrors `adjust_camera_raw_optics` (AdjustPixels.c:1085) and the four kernels it
/// calls — `lens_distort` (LensPixels.c:4), `optics_chromatic` (:1036), `optics_defringe` (:1018),
/// `optics_vignette_correct` (:1065) — along with the defaults, ranges and normalization of the macOS
/// `CameraRawOpticsSettings` (Document/CameraRawDetailOptics.swift:43-96). The Mac reaches this after effects
/// and grain, and before sharpening, inside one step (<c>CameraRaw.swift:262-264</c>).
/// </summary>
public static class CameraRawOptics
{
    /// <summary>How far the distortion sliders reach on a rendered layer. The Mac has no lens metadata for one
    /// either, so it scales both the manual and the profile amount by the Lens Correction filter's own strength
    /// (<c>PixelFilter.lensStrength</c>, Filters.swift:178, passed at CameraRaw.swift:264).</summary>
    public const double ProfileStrength = 0.35;

    /// <summary>The fixed amount the kernel pulls red and blue apart when the toggle is on
    /// (AdjustPixels.c:1101) — there is no slider for it, as on the Mac.</summary>
    public const double ChromaticStrength = 0.45;

    public const double UnitMax = 100, ToneMin = -100, ToneMax = 100, HueMin = 0, HueMax = 360;

    /// <summary>The bands the sliders start on, and the two centers plus the half-width the eyedropper works
    /// from (CameraRaw.swift:480-481).</summary>
    public const double PurpleDefaultLow = 270, PurpleDefaultHigh = 310, GreenDefaultLow = 60, GreenDefaultHigh = 120;
    public const double PurpleCenter = 290, GreenCenter = 90, PickerSpan = 25;

    /// <summary>What the eyedropper decided about one clicked pixel: which of the two fringes it is, the band to
    /// store for it, and the amount that band should end up holding — 50 when the slider was still at zero,
    /// otherwise its own value, so a second pick does not silently undo the first
    /// (<c>CameraRaw.swift:482-490</c>).</summary>
    public static (bool Purple, double Low, double High, double Amount) SolvedFringe(
        double hue, double purpleAmount, double greenAmount) =>
        Math.Abs(hue - PurpleCenter) < Math.Abs(hue - GreenCenter)
            ? (true, hue - PickerSpan, hue + PickerSpan, purpleAmount == 0 ? 50 : purpleAmount)
            : (false, hue - PickerSpan, hue + PickerSpan, greenAmount == 0 ? 50 : greenAmount);

    /// <summary>Every optics slider in its own range, each hue pair ordered — the Mac's `normalized`
    /// (CameraRawDetailOptics.swift:70-86). A value that is not a number falls back to that slider's own default,
    /// as the Mac's `clamp(_:range:fallback:)` does. This runs on the settings the grade uses and on what the
    /// eyedropper just wrote, so the panel can show a band the user typed while the pixels still get the one the
    /// kernel can read.</summary>
    public static void Normalize(CameraRawSettings settings)
    {
        double Slid(double value, double minimum, double maximum, double fallback) =>
            CameraRawSettings.SliderValue(value, minimum, maximum, fallback);

        settings.OpticsProfileDistortion = Slid(settings.OpticsProfileDistortion, 0, UnitMax, 100);
        settings.OpticsProfileVignetting = Slid(settings.OpticsProfileVignetting, 0, UnitMax, 100);
        settings.OpticsDistortion = Slid(settings.OpticsDistortion, ToneMin, ToneMax, 0);
        settings.OpticsPurpleAmount = Slid(settings.OpticsPurpleAmount, 0, UnitMax, 0);
        settings.OpticsGreenAmount = Slid(settings.OpticsGreenAmount, 0, UnitMax, 0);
        settings.OpticsVignetteAmount = Slid(settings.OpticsVignetteAmount, ToneMin, ToneMax, 0);
        settings.OpticsVignetteMidpoint = Slid(settings.OpticsVignetteMidpoint, 0, UnitMax, 50);
        settings.OpticsPurpleHueLow = Slid(settings.OpticsPurpleHueLow, HueMin, HueMax, PurpleDefaultLow);
        settings.OpticsPurpleHueHigh = Slid(settings.OpticsPurpleHueHigh, HueMin, HueMax, PurpleDefaultHigh);
        settings.OpticsGreenHueLow = Slid(settings.OpticsGreenHueLow, HueMin, HueMax, GreenDefaultLow);
        settings.OpticsGreenHueHigh = Slid(settings.OpticsGreenHueHigh, HueMin, HueMax, GreenDefaultHigh);
        if (settings.OpticsPurpleHueLow > settings.OpticsPurpleHueHigh)
            (settings.OpticsPurpleHueLow, settings.OpticsPurpleHueHigh) = (settings.OpticsPurpleHueHigh, settings.OpticsPurpleHueLow);
        if (settings.OpticsGreenHueLow > settings.OpticsGreenHueHigh)
            (settings.OpticsGreenHueLow, settings.OpticsGreenHueHigh) = (settings.OpticsGreenHueHigh, settings.OpticsGreenHueLow);
    }

    /// <summary>What a −100…100 slider means to the warp kernel: divided by 100 and scaled by the strength a
    /// rendered layer gets, which is the same number the Lens Correction filter uses for its own single slider
    /// (Filters.swift:264 hands <c>lens_distort</c> `settings.distortion / 100 * lensStrength`, and that
    /// constant is :178's 0.35).</summary>
    public static double SliderK(double slider, double strength = ProfileStrength) => slider / 100.0 * strength;

    /// <summary>The single radial amount handed to the distort kernel: the manual slider, plus the profile slider
    /// when the profile toggle is on, both scaled by the strength a rendered layer gets
    /// (CameraRawDetailOptics.swift:91-95). The profile sliders move nothing while their toggle is off.</summary>
    public static double DistortionK(CameraRawSettings settings, double profileStrength = ProfileStrength)
    {
        double manual = SliderK(settings.OpticsDistortion, profileStrength);
        double profile = settings.OpticsLensProfile ? SliderK(settings.OpticsProfileDistortion, profileStrength) : 0;
        return manual + profile;
    }

    /// <summary>Distort, then chromatic, then defringe and vignette on every covered pixel — the Mac's order,
    /// including that the chromatic pull reads the pixels distortion has already moved. The grade works on a
    /// normalized copy, as the Mac's `apply` does on `settings.normalized`.</summary>
    public static void Apply(Surface surface, CameraRawSettings settings)
    {
        var grade = settings.Clone();
        Normalize(grade);
        double k = DistortionK(grade);
        if (k != 0)
        {
            var source = surface.Clone();
            Distort(source, surface, k);
        }
        if (grade.OpticsRemoveChromaticAberration) ChromaticAberration(surface, ChromaticStrength);
        double profileVignette = grade.OpticsLensProfile ? grade.OpticsProfileVignetting / 100.0 : 0;
        double vignette = grade.OpticsVignetteAmount + profileVignette * 35.0;
        if (grade.OpticsPurpleAmount == 0 && grade.OpticsGreenAmount == 0 && vignette == 0) return;
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
                Defringe(ref r, ref g, ref b, grade.OpticsPurpleAmount, grade.OpticsPurpleHueLow, grade.OpticsPurpleHueHigh,
                    grade.OpticsGreenAmount, grade.OpticsGreenHueLow, grade.OpticsGreenHueHigh);
                VignetteCorrect(ref r, ref g, ref b, x, y, surface.Width, surface.Height, vignette, grade.OpticsVignetteMidpoint);
                CameraRawEngine.WritePremultiplied(surface, index, r, g, b, alpha);
            }
        }
    }

    /// <summary>Bilinear resample of a whole layer around its center, destination separate from the source as in
    /// the C. The sample position is pulled toward the center where the square of the distance is large, so a
    /// positive amount takes the bulge out of a wide-angle frame and a negative one puts it back. Neighbors
    /// outside the layer simply contribute nothing — the weights are not renormalized, which is why a pincushion
    /// pull leaves the corners partly transparent (LensPixels.c:4-37).</summary>
    public static void Distort(Surface source, Surface destination, double k)
    {
        int width = source.Width, height = source.Height;
        double cx = width * 0.5, cy = height * 0.5;
        double halfDiagonalSquared = cx * cx + cy * cy;
        for (int y = 0; y < height; y++)
        {
            double dy = y + 0.5 - cy;
            for (int x = 0; x < width; x++)
            {
                double dx = x + 0.5 - cx;
                double scale = 1.0 - k * (dx * dx + dy * dy) / halfDiagonalSquared;
                double sx = cx + dx * scale - 0.5, sy = cy + dy * scale - 0.5;
                double fx0 = Math.Floor(sx), fy0 = Math.Floor(sy);
                double fx = sx - fx0, fy = sy - fy0;
                int x0 = (int)fx0, y0 = (int)fy0;
                double red = 0, green = 0, blue = 0, alpha = 0;
                for (int j = 0; j < 2; j++)
                {
                    int row = y0 + j;
                    if (row < 0 || row >= height) continue;
                    double wy = j == 1 ? fy : 1 - fy;
                    if (wy == 0) continue;
                    for (int i = 0; i < 2; i++)
                    {
                        int column = x0 + i;
                        if (column < 0 || column >= width) continue;
                        double weight = wy * (i == 1 ? fx : 1 - fx);
                        if (weight == 0) continue;
                        int at = row * source.Stride + column * 4;
                        red += weight * source.Pixels[at];
                        green += weight * source.Pixels[at + 1];
                        blue += weight * source.Pixels[at + 2];
                        alpha += weight * source.Pixels[at + 3];
                    }
                }
                int index = y * destination.Stride + x * 4;
                destination.Pixels[index] = Round(red);
                destination.Pixels[index + 1] = Round(green);
                destination.Pixels[index + 2] = Round(blue);
                destination.Pixels[index + 3] = Round(alpha);
            }
        }
    }

    /// <summary>Red sampled inward and blue outward, by an amount that grows with the square of the distance from
    /// the center. Green keeps its own pixel, and so does alpha. Each row is read from a copy taken before the
    /// pass, which is what stops a pixel from being sampled after its own neighbor was already rewritten
    /// (AdjustPixels.c:1036-1063).</summary>
    public static void ChromaticAberration(Surface surface, double strength)
    {
        if (strength <= 0) return;
        var copy = surface.Clone();
        int width = surface.Width, height = surface.Height;
        double cx = width * 0.5, cy = height * 0.5;
        double maxRadius = Math.Sqrt(cx * cx + cy * cy);
        for (int y = 0; y < height; y++)
        {
            int row = y * surface.Stride;
            for (int x = 0; x < width; x++)
            {
                int index = row + x * 4;
                double alpha = surface.Pixels[index + 3];
                if (alpha == 0) continue;
                double dx = x + 0.5 - cx, dy = y + 0.5 - cy;
                double radial = Math.Sqrt(dx * dx + dy * dy) / maxRadius;
                double shift = strength * radial * radial * 2.5;
                int redX = (int)Math.Round(x - shift, MidpointRounding.AwayFromZero);
                int blueX = (int)Math.Round(x + shift, MidpointRounding.AwayFromZero);
                int redAt = row + ClampedIndex(redX, width) * 4;
                int blueAt = row + ClampedIndex(blueX, width) * 4;
                double g = Math.Min(1.0, copy.Pixels[index + 1] / alpha);
                double r = Math.Min(1.0, copy.Pixels[redAt] / Math.Max(1.0, copy.Pixels[redAt + 3]));
                double b = Math.Min(1.0, copy.Pixels[blueAt + 2] / Math.Max(1.0, copy.Pixels[blueAt + 3]));
                CameraRawEngine.WritePremultiplied(surface, index, r, g, b, alpha);
            }
        }
    }

    /// <summary>Desaturates a fringe toward its own luminance by the amount of the band that owns its hue. Two
    /// bands can both claim a pixel and the bigger amount wins; a pixel with no chroma at all is left alone
    /// because there is nothing to remove (AdjustPixels.c:1018-1034).</summary>
    public static void Defringe(ref double r, ref double g, ref double b,
        double purpleAmount, double purpleLow, double purpleHigh, double greenAmount, double greenLow, double greenHigh)
    {
        double hue = HueDegrees(r, g, b);
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double chroma = max - min;
        if (chroma < 1e-6) return;
        double saturation = chroma / max;
        double reduce = 0;
        if (purpleAmount > 0 && HueInRange(hue, purpleLow, purpleHigh)) reduce = Math.Max(reduce, purpleAmount / 100.0);
        if (greenAmount > 0 && HueInRange(hue, greenLow, greenHigh)) reduce = Math.Max(reduce, greenAmount / 100.0);
        if (reduce <= 0) return;
        double lum = CameraRawEngine.Rec709(r, g, b);
        double factor = 1.0 - reduce * saturation;
        r = CameraRawEngine.Clamp01(lum + (r - lum) * factor);
        g = CameraRawEngine.Clamp01(lum + (g - lum) * factor);
        b = CameraRawEngine.Clamp01(lum + (b - lum) * factor);
    }

    /// <summary>Brightens or darkens away from the center over a smoothstep of the normalized distance, the
    /// falloff starting where the midpoint says. Positive lifts toward white, negative multiplies down. Amount is
    /// the already-summed manual plus profile value, which is why it can reach past 100 (AdjustPixels.c:1065-1083).</summary>
    public static void VignetteCorrect(ref double r, ref double g, ref double b, int x, int y, int width, int height,
        double amount, double midpoint)
    {
        if (amount == 0 || width == 0 || height == 0) return;
        double nx = (x + 0.5) / width * 2.0 - 1.0;
        double ny = (y + 0.5) / height * 2.0 - 1.0;
        double dist = Math.Sqrt(nx * nx + ny * ny) / Math.Sqrt(2.0);
        double start = midpoint / 100.0 * 0.85;
        double t = CameraRawEngine.Clamp01((dist - start) / 0.35);
        double mask = t * t * (3.0 - 2.0 * t);
        double lift = amount / 100.0 * mask;
        if (lift > 0)
        {
            r = CameraRawEngine.Clamp01(r + (1.0 - r) * lift);
            g = CameraRawEngine.Clamp01(g + (1.0 - g) * lift);
            b = CameraRawEngine.Clamp01(b + (1.0 - b) * lift);
            return;
        }
        double factor = 1.0 + lift;
        r *= factor;
        g *= factor;
        b *= factor;
    }

    /// <summary>Hue in degrees, the branch order the kernel uses — a tie goes to red, then green, then blue, and
    /// an achromatic pixel reports zero (AdjustPixels.c:824). Also what the defringe eyedropper compares against
    /// its two centers (CameraRaw.swift:496-507).</summary>
    public static double HueDegrees(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double chroma = max - min;
        if (chroma < 1e-6) return 0;
        double hue = max == r ? (g - b) / chroma % 6.0 : max == g ? (b - r) / chroma + 2.0 : (r - g) / chroma + 4.0;
        hue *= 60.0;
        return hue < 0 ? hue + 360.0 : hue;
    }

    /// <summary>The band test as the kernel writes it. Normalizing orders every pair first, so the wrap branch is
    /// only reachable by calling the primitive directly — kept because the pixel math it mirrors keeps it.</summary>
    private static bool HueInRange(double hue, double low, double high) =>
        low <= high ? hue >= low && hue <= high : hue >= low || hue <= high;

    private static int ClampedIndex(int index, int limit) =>
        index < 0 ? 0 : index >= limit ? limit - 1 : index;

    private static byte Round(double value) => (byte)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);
}
