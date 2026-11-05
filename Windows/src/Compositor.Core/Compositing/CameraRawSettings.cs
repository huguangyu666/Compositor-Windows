namespace Compositor.Core;

/// <summary>
/// Camera Raw filter settings. First scope: the Light group (exposure, contrast,
/// highlights, shadows, whites, blacks) and the Color group (temperature, tint, vibrance,
/// saturation). Second scope, still ported from the macOS CameraRawSettings and
/// CameraRawColor: the point tone curve (RGB + three channels, plus the parametric
/// sliders), the eight-family Color Mixer, and the Effects group (texture, clarity,
/// dehaze, vignette, grain). The pixel math lives in <see cref="CameraRawEngine"/> and
/// mirrors the native adjust_camera_raw / adjust_camera_raw_curve_color /
/// adjust_camera_raw_effects kernels. The calibration group — a process version plus the shadow tint and the
/// three primaries — runs in front of all of them, and the optics group (distortion, the chromatic pull, defringe
/// and vignetting correction) runs immediately before sharpening, as it does on the Mac. Ahead of the whole grade
/// sits the geometry bend, because on the Mac it replaces the image the rest of the passes read.
/// </summary>
public sealed class CameraRawSettings
{
    public const double ExposureMin = -5, ExposureMax = 5;
    public const double ToneMin = -100, ToneMax = 100;
    public const double GeometryRotateMin = -45, GeometryRotateMax = 45;

    /// <summary>Share of a full warm/cool swing applied to red and blue. Kept beside the solver that inverts it,
    /// so the eyedropper and the light pass cannot drift apart (<c>CameraRaw.swift:51-56</c>).</summary>
    public const double TemperatureGain = 0.35;

    /// <summary>Magenta/green swing shared by red and blue.</summary>
    public const double TintRedBlue = 0.15;

    /// <summary>Magenta/green swing on green, opposite the other two channels.</summary>
    public const double TintGreen = 0.30;

    /// <summary>The panel's white-balance modes, in the Mac's picker order. The mode is state, not an adjustment:
    /// choosing one moves no pixel until the temperature and tint it wrote are counted as usual.</summary>
    public const int WhiteBalanceCustom = 0, WhiteBalanceAuto = 1;

    public static readonly string[] WhiteBalanceNames = ["Custom", "Auto"];

    /// <summary>Stops of linear light, −5…5.</summary>
    public double Exposure { get; set; }

    public double Contrast { get; set; }
    public double Highlights { get; set; }
    public double Shadows { get; set; }
    public double Whites { get; set; }
    public double Blacks { get; set; }

    /// <summary>Relative cool-to-warm, −100…100. Positive is warmer.</summary>
    public double Temperature { get; set; }

    /// <summary>Green-to-magenta, −100…100. Positive is magenta.</summary>
    public double Tint { get; set; }

    /// <summary><see cref="WhiteBalanceCustom"/> or <see cref="WhiteBalanceAuto"/>. Remembering which one the
    /// panel left selected is the whole job; the pixels still come from temperature and tint.</summary>
    public int WhiteBalance { get; set; }

    public double Vibrance { get; set; }
    public double Saturation { get; set; }

    // --- Point curve (CameraRawCurveSettings) ---

    public double CurveShadows { get; set; }
    public double CurveDarks { get; set; }
    public double CurveLights { get; set; }
    public double CurveHighlights { get; set; }

    /// <summary>Parametric dividers, 0…100, kept in order.</summary>
    public double CurveShadowSplit { get; set; } = 25;
    public double CurveDarkSplit { get; set; } = 50;
    public double CurveLightSplit { get; set; } = 75;

    /// <summary>Point curves, 0…1 on both axes, endpoints pinned to the corners.</summary>
    public List<CurvePoint> CurveRgb { get; set; } = LinearCurve();
    public List<CurvePoint> CurveRed { get; set; } = LinearCurve();
    public List<CurvePoint> CurveGreen { get; set; } = LinearCurve();
    public List<CurvePoint> CurveBlue { get; set; } = LinearCurve();

    /// <summary>How much the composite curve also changes saturation, −100…100.</summary>
    public double RefineSaturation { get; set; }

    public static List<CurvePoint> LinearCurve() => [new(0, 0), new(1, 1)];

    // --- Color Mixer (CameraRawMixerSettings): Reds, Oranges, Yellows, Greens, Aquas, Blues, Purples, Magentas ---

    public const int MixerFamilies = 8;

    public double[] MixerHue { get; set; } = new double[MixerFamilies];
    public double[] MixerSaturation { get; set; } = new double[MixerFamilies];
    public double[] MixerLuminance { get; set; } = new double[MixerFamilies];

    /// <summary>The family centers, in degrees, as on the Mac.</summary>
    public static readonly double[] MixerCenters = [0, 30, 60, 120, 180, 240, 270, 300];
    public static readonly string[] MixerNames = ["Reds", "Oranges", "Yellows", "Greens", "Aquas", "Blues", "Purples", "Magentas"];

    /// <summary>The mixer's three pages and the HSL page's one component at a time, in the Mac's own words
    /// (<c>CameraRawMixerPage</c> and <c>CameraRawMixerTab</c>, CameraRawColor.swift:5-6). Which of them is
    /// showing is the panel's business, not the settings'.</summary>
    public static readonly string[] MixerPageNames = ["HSL", "Color", "Point Color"];
    public static readonly string[] MixerComponentNames = ["Hue", "Saturation", "Luminance"];
    public const int MixerPageHsl = 0, MixerPageColor = 1, MixerPagePoint = 2;

    /// <summary>The curve's two pages in the Mac's own words (<c>CameraRawCurvePage</c>,
    /// CameraRawColor.swift:3) and the four tonal regions the parametric page lifts, listed the way the panel
    /// lists them — brightest first (CameraRawColorControls.swift:36-39), which is the reverse of the order the
    /// dividers cut the range into. Which page is showing is the panel's business, and the Mac defaults to
    /// Parametric (Filters.swift:308).</summary>
    public static readonly string[] CurvePageNames = ["Parametric", "Point"];
    public static readonly string[] CurveRegionNames = ["Highlights", "Lights", "Darks", "Shadows"];
    public const int CurvePageParametric = 0, CurvePagePoint = 1;

    /// <summary>The colors picked on the picture, in the order they were picked. Only the first eight are ever
    /// read — a ninth is filed by the panel and ignored by the render — and each one is applied on top of the
    /// families, in the kernel's own order, on the color the previous point already moved
    /// (<c>mixer.points</c>, CameraRawColor.swift:157, 177-183; AdjustPixels.c:776-783).</summary>
    public List<CameraRawMixerPoint> MixerPoints { get; set; } = [];

    // --- Effects ---

    public double Texture { get; set; }
    public double Clarity { get; set; }
    public double Dehaze { get; set; }

    public double VignetteAmount { get; set; }
    public double VignetteMidpoint { get; set; } = 50;
    public double VignetteRoundness { get; set; }
    public double VignetteFeather { get; set; } = 50;
    public double VignetteHighlights { get; set; }

    /// <summary>0 eases a darkening vignette off bright pixels, 1 drains their color instead.</summary>
    public int VignetteStyle { get; set; }

    public double GrainAmount { get; set; }
    public double GrainSize { get; set; } = 25;
    public double GrainRoughness { get; set; } = 50;

    /// <summary>The pattern stays put while the panel is open, as on the Mac.</summary>
    public uint GrainSeed { get; set; }

    /// <summary>Camera Raw's 0…100 size, in the pixel scale the grain kernel already uses.</summary>
    public double GrainKernelSize => 0.5 + GrainSize / 100 * 19.5;

    // --- Glow (three looks; warmth tints Diffusion and Bloom, Halation stays red) ---

    public double Glow { get; set; }

    /// <summary>0 diffusion, 1 bloom, 2 halation — the kernel values.</summary>
    public int GlowStyle { get; set; }
    public double GlowRange { get; set; }
    public double GlowSpread { get; set; }
    public double GlowWarmth { get; set; }

    // --- Color Grading: four wheels plus how the tonal ones overlap ---

    public double GradingShadowsHue { get; set; }
    public double GradingShadowsSat { get; set; }
    public double GradingShadowsLum { get; set; }
    public double GradingMidtonesHue { get; set; }
    public double GradingMidtonesSat { get; set; }
    public double GradingMidtonesLum { get; set; }
    public double GradingHighlightsHue { get; set; }
    public double GradingHighlightsSat { get; set; }
    public double GradingHighlightsLum { get; set; }
    public double GradingGlobalHue { get; set; }
    public double GradingGlobalSat { get; set; }
    public double GradingGlobalLum { get; set; }

    /// <summary>0…100. Higher values let the three tonal wheels overlap more.</summary>
    public double GradingBlending { get; set; } = 50;

    /// <summary>−100…100. Negative favors shadows, positive favors highlights.</summary>
    public double GradingBalance { get; set; }

    /// <summary>The wheels in kernel order: shadows, midtones, highlights, global (weight 1).</summary>
    public double[] GradingFloats()
    {
        double[] hue = [GradingShadowsHue, GradingMidtonesHue, GradingHighlightsHue, GradingGlobalHue];
        double[] sat = [GradingShadowsSat, GradingMidtonesSat, GradingHighlightsSat, GradingGlobalSat];
        double[] lum = [GradingShadowsLum, GradingMidtonesLum, GradingHighlightsLum, GradingGlobalLum];
        return Enumerable.Range(0, 4).SelectMany(i => new[] { hue[i] / 360.0, sat[i] / 100.0, lum[i] / 100.0 }).ToArray();
    }

    /// <summary>
    /// An independent copy: the four curve lists, the three mixer arrays and the guide lines are duplicated, so a
    /// clone handed to a background preview thread never sees the panel's next edit.
    /// </summary>
    public CameraRawSettings Clone() => new()
    {
        Exposure = Exposure, Contrast = Contrast, Highlights = Highlights, Shadows = Shadows,
        Whites = Whites, Blacks = Blacks, Temperature = Temperature, Tint = Tint, WhiteBalance = WhiteBalance,
        Vibrance = Vibrance, Saturation = Saturation,
        CurveShadows = CurveShadows, CurveDarks = CurveDarks, CurveLights = CurveLights, CurveHighlights = CurveHighlights,
        CurveShadowSplit = CurveShadowSplit, CurveDarkSplit = CurveDarkSplit, CurveLightSplit = CurveLightSplit,
        CurveRgb = [.. CurveRgb], CurveRed = [.. CurveRed], CurveGreen = [.. CurveGreen], CurveBlue = [.. CurveBlue],
        RefineSaturation = RefineSaturation,
        MixerHue = [.. MixerHue], MixerSaturation = [.. MixerSaturation], MixerLuminance = [.. MixerLuminance],
        MixerPoints = [.. MixerPoints],
        Texture = Texture, Clarity = Clarity, Dehaze = Dehaze,
        VignetteAmount = VignetteAmount, VignetteMidpoint = VignetteMidpoint,
        VignetteRoundness = VignetteRoundness, VignetteFeather = VignetteFeather,
        VignetteHighlights = VignetteHighlights, VignetteStyle = VignetteStyle,
        GrainAmount = GrainAmount, GrainSize = GrainSize, GrainRoughness = GrainRoughness, GrainSeed = GrainSeed,
        Glow = Glow, GlowStyle = GlowStyle, GlowRange = GlowRange, GlowSpread = GlowSpread, GlowWarmth = GlowWarmth,
        GradingShadowsHue = GradingShadowsHue, GradingShadowsSat = GradingShadowsSat, GradingShadowsLum = GradingShadowsLum,
        GradingMidtonesHue = GradingMidtonesHue, GradingMidtonesSat = GradingMidtonesSat, GradingMidtonesLum = GradingMidtonesLum,
        GradingHighlightsHue = GradingHighlightsHue, GradingHighlightsSat = GradingHighlightsSat, GradingHighlightsLum = GradingHighlightsLum,
        GradingGlobalHue = GradingGlobalHue, GradingGlobalSat = GradingGlobalSat, GradingGlobalLum = GradingGlobalLum,
        GradingBlending = GradingBlending, GradingBalance = GradingBalance,
        SharpenAmount = SharpenAmount, SharpenRadius = SharpenRadius, SharpenDetail = SharpenDetail, SharpenMasking = SharpenMasking,
        NoiseLuminance = NoiseLuminance, NoiseLuminanceDetail = NoiseLuminanceDetail, NoiseLuminanceContrast = NoiseLuminanceContrast,
        NoiseColor = NoiseColor, NoiseColorDetail = NoiseColorDetail, NoiseColorSmoothness = NoiseColorSmoothness,
        OpticsRemoveChromaticAberration = OpticsRemoveChromaticAberration, OpticsLensProfile = OpticsLensProfile,
        OpticsProfileDistortion = OpticsProfileDistortion, OpticsProfileVignetting = OpticsProfileVignetting,
        OpticsDistortion = OpticsDistortion, OpticsPurpleAmount = OpticsPurpleAmount,
        OpticsPurpleHueLow = OpticsPurpleHueLow, OpticsPurpleHueHigh = OpticsPurpleHueHigh,
        OpticsGreenAmount = OpticsGreenAmount, OpticsGreenHueLow = OpticsGreenHueLow, OpticsGreenHueHigh = OpticsGreenHueHigh,
        OpticsVignetteAmount = OpticsVignetteAmount, OpticsVignetteMidpoint = OpticsVignetteMidpoint,
        GeometryProjection = GeometryProjection, GeometryUpright = GeometryUpright,
        GeometryGuides = [.. GeometryGuides], GeometryVertical = GeometryVertical, GeometryHorizontal = GeometryHorizontal,
        GeometryRotate = GeometryRotate, GeometryAspect = GeometryAspect, GeometryScale = GeometryScale,
        GeometryOffsetX = GeometryOffsetX, GeometryOffsetY = GeometryOffsetY, GeometryConstrainCrop = GeometryConstrainCrop,
        CalibrationProcess = CalibrationProcess, CalibrationShadowTint = CalibrationShadowTint,
        CalibrationRedHue = CalibrationRedHue, CalibrationRedSaturation = CalibrationRedSaturation,
        CalibrationGreenHue = CalibrationGreenHue, CalibrationGreenSaturation = CalibrationGreenSaturation,
        CalibrationBlueHue = CalibrationBlueHue, CalibrationBlueSaturation = CalibrationBlueSaturation,
    };

    /// <summary>The ten groups the panel can hide one at a time, in the order the Mac declares its ten
    /// <c>showsCameraRaw*</c> flags (Filters.swift:298-307).</summary>
    public const int GroupLight = 0, GroupColor = 1, GroupEffects = 2, GroupCurve = 3, GroupMixer = 4,
        GroupGrading = 5, GroupDetail = 6, GroupOptics = 7, GroupGeometry = 8, GroupCalibration = 9;
    public const int Groups = 10;

    /// <summary>What each group is called on the Mac's panel headers (CameraRawControls.swift:372-383), which is
    /// what the eye's help and accessible name are built from ("Hide Light in the preview").</summary>
    public static readonly string[] GroupNames = ["Light", "Color", "Effects", "Curve", "Color Mixer",
        "Color Grading", "Detail", "Optics", "Geometry", "Calibration"];

    /// <summary>Sliders as they will be rendered: a hidden group contributes nothing
    /// (<c>renderSettings()</c>, Filters.swift:451-461, through <c>applying(shows…:)</c>, CameraRaw.swift:171-204).
    /// Light zeroes its six numbers and Color its four — the white-balance *mode* stays, because the Mac's
    /// <c>temperature/tint/vibrance/saturation</c> list does not include it. Effects zeroes only the six amounts the
    /// Mac names, leaving the size/roughness/style sub-controls alone, and the remaining seven groups are replaced
    /// whole by their defaults (<c>result.curve = CameraRawCurveSettings()</c> and its six siblings), which is why
    /// a hidden curve page loses its anchors and a hidden Geometry page its guide lines.
    /// A group missing from <paramref name="shown"/> counts as shown, like the Mac's defaulted parameters.
    /// This is a copy: the draft keeps every number, so opening the eye again restores the grade.</summary>
    public CameraRawSettings Applying(IReadOnlyList<bool> shown)
    {
        var result = Clone();
        var fresh = new CameraRawSettings();
        for (int group = 0; group < Groups; group++)
        {
            if (group >= shown.Count || shown[group]) continue;
            result.RestoreDefaults(group, fresh);
        }
        return result;
    }

    private void RestoreDefaults(int group, CameraRawSettings fresh)
    {
        switch (group)
        {
            case GroupLight:
                Exposure = fresh.Exposure; Contrast = fresh.Contrast; Highlights = fresh.Highlights;
                Shadows = fresh.Shadows; Whites = fresh.Whites; Blacks = fresh.Blacks;
                break;
            case GroupColor:
                Temperature = fresh.Temperature; Tint = fresh.Tint;
                Vibrance = fresh.Vibrance; Saturation = fresh.Saturation;
                break;
            case GroupEffects:
                Texture = fresh.Texture; Clarity = fresh.Clarity; Dehaze = fresh.Dehaze;
                Glow = fresh.Glow; VignetteAmount = fresh.VignetteAmount; GrainAmount = fresh.GrainAmount;
                break;
            case GroupCurve:
                CurveShadows = fresh.CurveShadows; CurveDarks = fresh.CurveDarks;
                CurveLights = fresh.CurveLights; CurveHighlights = fresh.CurveHighlights;
                CurveShadowSplit = fresh.CurveShadowSplit; CurveDarkSplit = fresh.CurveDarkSplit;
                CurveLightSplit = fresh.CurveLightSplit;
                CurveRgb = fresh.CurveRgb; CurveRed = fresh.CurveRed;
                CurveGreen = fresh.CurveGreen; CurveBlue = fresh.CurveBlue;
                RefineSaturation = fresh.RefineSaturation;
                break;
            case GroupMixer:
                MixerHue = fresh.MixerHue; MixerSaturation = fresh.MixerSaturation;
                MixerLuminance = fresh.MixerLuminance; MixerPoints = fresh.MixerPoints;
                break;
            case GroupGrading:
                GradingShadowsHue = fresh.GradingShadowsHue; GradingShadowsSat = fresh.GradingShadowsSat;
                GradingShadowsLum = fresh.GradingShadowsLum;
                GradingMidtonesHue = fresh.GradingMidtonesHue; GradingMidtonesSat = fresh.GradingMidtonesSat;
                GradingMidtonesLum = fresh.GradingMidtonesLum;
                GradingHighlightsHue = fresh.GradingHighlightsHue; GradingHighlightsSat = fresh.GradingHighlightsSat;
                GradingHighlightsLum = fresh.GradingHighlightsLum;
                GradingGlobalHue = fresh.GradingGlobalHue; GradingGlobalSat = fresh.GradingGlobalSat;
                GradingGlobalLum = fresh.GradingGlobalLum;
                GradingBlending = fresh.GradingBlending; GradingBalance = fresh.GradingBalance;
                break;
            case GroupDetail:
                SharpenAmount = fresh.SharpenAmount; SharpenRadius = fresh.SharpenRadius;
                SharpenDetail = fresh.SharpenDetail; SharpenMasking = fresh.SharpenMasking;
                NoiseLuminance = fresh.NoiseLuminance; NoiseLuminanceDetail = fresh.NoiseLuminanceDetail;
                NoiseLuminanceContrast = fresh.NoiseLuminanceContrast;
                NoiseColor = fresh.NoiseColor; NoiseColorDetail = fresh.NoiseColorDetail;
                NoiseColorSmoothness = fresh.NoiseColorSmoothness;
                break;
            case GroupOptics:
                OpticsRemoveChromaticAberration = fresh.OpticsRemoveChromaticAberration;
                OpticsLensProfile = fresh.OpticsLensProfile;
                OpticsProfileDistortion = fresh.OpticsProfileDistortion;
                OpticsProfileVignetting = fresh.OpticsProfileVignetting;
                OpticsDistortion = fresh.OpticsDistortion;
                OpticsPurpleAmount = fresh.OpticsPurpleAmount;
                OpticsPurpleHueLow = fresh.OpticsPurpleHueLow; OpticsPurpleHueHigh = fresh.OpticsPurpleHueHigh;
                OpticsGreenAmount = fresh.OpticsGreenAmount;
                OpticsGreenHueLow = fresh.OpticsGreenHueLow; OpticsGreenHueHigh = fresh.OpticsGreenHueHigh;
                OpticsVignetteAmount = fresh.OpticsVignetteAmount;
                OpticsVignetteMidpoint = fresh.OpticsVignetteMidpoint;
                break;
            case GroupGeometry:
                GeometryProjection = fresh.GeometryProjection; GeometryUpright = fresh.GeometryUpright;
                GeometryGuides = fresh.GeometryGuides;
                GeometryVertical = fresh.GeometryVertical; GeometryHorizontal = fresh.GeometryHorizontal;
                GeometryRotate = fresh.GeometryRotate; GeometryAspect = fresh.GeometryAspect;
                GeometryScale = fresh.GeometryScale; GeometryOffsetX = fresh.GeometryOffsetX;
                GeometryOffsetY = fresh.GeometryOffsetY; GeometryConstrainCrop = fresh.GeometryConstrainCrop;
                break;
            case GroupCalibration:
                CalibrationProcess = fresh.CalibrationProcess; CalibrationShadowTint = fresh.CalibrationShadowTint;
                CalibrationRedHue = fresh.CalibrationRedHue; CalibrationRedSaturation = fresh.CalibrationRedSaturation;
                CalibrationGreenHue = fresh.CalibrationGreenHue; CalibrationGreenSaturation = fresh.CalibrationGreenSaturation;
                CalibrationBlueHue = fresh.CalibrationBlueHue; CalibrationBlueSaturation = fresh.CalibrationBlueSaturation;
                break;
        }
    }

    // --- Detail: sharpening plus manual noise reduction (amount is 0…150) ---

    public double SharpenAmount { get; set; }
    public double SharpenRadius { get; set; } = 10;
    public double SharpenDetail { get; set; } = 25;
    public double SharpenMasking { get; set; }
    public double NoiseLuminance { get; set; }
    public double NoiseLuminanceDetail { get; set; } = 50;
    public double NoiseLuminanceContrast { get; set; }
    public double NoiseColor { get; set; }
    public double NoiseColorDetail { get; set; } = 50;
    public double NoiseColorSmoothness { get; set; } = 50;

    public const double SharpenAmountMax = 150;

    // --- Optics: lens distortion, the chromatic-aberration pull, defringe and vignetting correction ---

    /// <summary>The kernel's own fixed strength rides with this toggle; there is no slider for it, as on the Mac.</summary>
    public bool OpticsRemoveChromaticAberration { get; set; }

    /// <summary>On the Mac this stands for real lens metadata; a rendered layer carries none, so the toggle only
    /// lets the two profile sliders below mean anything at all (CameraRawDetailOptics.swift:41-42).</summary>
    public bool OpticsLensProfile { get; set; }

    public double OpticsProfileDistortion { get; set; } = 100;
    public double OpticsProfileVignetting { get; set; } = 100;

    /// <summary>−100…100, the same sign convention as the Lens Correction filter: positive takes a barrel out.</summary>
    public double OpticsDistortion { get; set; }

    public double OpticsPurpleAmount { get; set; }
    public double OpticsPurpleHueLow { get; set; } = CameraRawOptics.PurpleDefaultLow;
    public double OpticsPurpleHueHigh { get; set; } = CameraRawOptics.PurpleDefaultHigh;
    public double OpticsGreenAmount { get; set; }
    public double OpticsGreenHueLow { get; set; } = CameraRawOptics.GreenDefaultLow;
    public double OpticsGreenHueHigh { get; set; } = CameraRawOptics.GreenDefaultHigh;

    /// <summary>−100…100. Positive brightens the corners, negative darkens them. Stacks with the profile slider
    /// at a third of its strength, so the pair can add up past 100.</summary>
    public double OpticsVignetteAmount { get; set; }

    public double OpticsVignetteMidpoint { get; set; } = 50;

    // --- Geometry: the numeric half of the Mac's Geometry band (projection, seven sliders, Constrain Crop) ---

    /// <summary>Perspective lets the keystone reach full strength; Rectilinear keeps the warp gentler
    /// (CameraRawGeometryCalibration.swift:137, the projection's own factor).</summary>
    public const int GeometryProjectionPerspective = 0, GeometryProjectionRectilinear = 1;

    public static readonly string[] GeometryProjectionNames = ["Perspective", "Rectilinear"];

    /// <summary>The projection's strength on the keystone axes.</summary>
    public static double GeometryProjectionStrength(int projection) =>
        projection == GeometryProjectionRectilinear ? 0.55 : 1.0;

    /// <summary>Off uses the seven numbers as typed. Guided adds what the drawn lines say on top of them —
    /// it does not replace them (CameraRawGeometryCalibration.swift:102-110).</summary>
    public const int GeometryUprightOff = 0, GeometryUprightGuided = 1;

    public static readonly string[] GeometryUprightNames = ["Off", "Guided"];

    public int GeometryUpright { get; set; }

    /// <summary>Normalised 0…1 with y measured downward from the top-left of the pixel grid, which is the space
    /// the Mac's guide picker hands over (<c>CameraRaw.swift:601-607</c>) — its own struct comment says the
    /// opposite, and the solve reads the numbers exactly as stored, so the port stores them the same way.</summary>
    public List<CameraRawGeometryGuide> GeometryGuides { get; set; } = [];

    public int GeometryProjection { get; set; }

    /// <summary>−100…100 unless noted. Straightens vertical lines toward the center.</summary>
    public double GeometryVertical { get; set; }

    public double GeometryHorizontal { get; set; }

    /// <summary>−45…45 degrees, the only slider in this band whose range is not ±100.</summary>
    public double GeometryRotate { get; set; }

    /// <summary>Stretches width against height about the center: the axes move in opposite directions.</summary>
    public double GeometryAspect { get; set; }

    public double GeometryScale { get; set; }
    public double GeometryOffsetX { get; set; }
    public double GeometryOffsetY { get; set; }

    /// <summary>Trims the empty edges the warp leaves and fits what is left back into the frame. It moves no
    /// pixel by itself — it only changes what the transform's result is cropped to.</summary>
    public bool GeometryConstrainCrop { get; set; }

    // --- Calibration: the process version scales every slider below it ---

    /// <summary>1…6, the process version's own number. Version 6 is the current default and applies the seven
    /// sliders below at full strength; Version 1 reaches a little over half that far.</summary>
    public int CalibrationProcess { get; set; } = CalibrationProcessLatest;

    /// <summary>−100…100, green to magenta. Only tones below a third of the range carry it.</summary>
    public double CalibrationShadowTint { get; set; }

    public double CalibrationRedHue { get; set; }
    public double CalibrationRedSaturation { get; set; }
    public double CalibrationGreenHue { get; set; }
    public double CalibrationGreenSaturation { get; set; }
    public double CalibrationBlueHue { get; set; }
    public double CalibrationBlueSaturation { get; set; }

    public const int CalibrationProcessLatest = 6;

    /// <summary>The picker's rows, untranslated on purpose: they name Adobe's process versions, not features.</summary>
    public static readonly string[] CalibrationProcessNames = ["Version 1", "Version 2", "Version 3", "Version 4", "Version 5", "Version 6"];

    /// <summary>How far the calibration sliders reach, the kernel's per-version scale.</summary>
    public static double CalibrationScale(int process) =>
        process <= 1 ? 0.55 : process == 2 ? 0.65 : process == 3 ? 0.75 : process == 4 ? 0.85 : process == 5 ? 0.92 : 1.0;

    /// <summary>One slider back into its own range, or onto its own default when the stored value is not a number
    /// at all — the macOS <c>clamp(_:range:fallback:)</c> every <c>normalized</c> is built on
    /// (ImageAdjustments.swift:14-16).</summary>
    public static double SliderValue(double value, double minimum, double maximum, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;

    /// <summary>Channel multipliers for temperature and tint. Neutral is 1, 1, 1.</summary>
    public (double Red, double Green, double Blue) Gains
    {
        get
        {
            double warm = Temperature / 100;
            double magenta = Tint / 100;
            return (1 + TemperatureGain * warm + TintRedBlue * magenta,
                    1 - TintGreen * magenta,
                    1 - TemperatureGain * warm + TintRedBlue * magenta);
        }
    }

    public bool CurveIsLinear(List<CurvePoint> points) =>
        points.Count == 2 && points[0].X == 0 && points[0].Y == 0 && points[1].X == 1 && points[1].Y == 1;

    public bool AdjustsCurve =>
        CurveShadows != 0 || CurveDarks != 0 || CurveLights != 0 || CurveHighlights != 0 || RefineSaturation != 0
        || !CurveIsLinear(CurveRgb) || !CurveIsLinear(CurveRed) || !CurveIsLinear(CurveGreen) || !CurveIsLinear(CurveBlue);

    /// <summary>A picked color moves nothing until one of its three shifts is moved: the point list on its own
    /// is not an adjustment, exactly as on the Mac (<c>adjusts</c>, CameraRawColor.swift:159-162).</summary>
    public bool AdjustsMixer =>
        MixerHue.Any(v => v != 0) || MixerSaturation.Any(v => v != 0) || MixerLuminance.Any(v => v != 0)
        || MixerPoints.Any(point => point.HueShift != 0 || point.SaturationShift != 0 || point.LuminanceShift != 0);

    public bool AdjustsEffects =>
        Texture != 0 || Clarity != 0 || Dehaze != 0 || Glow > 0 || VignetteAmount != 0 || GrainAmount > 0;

    public bool AdjustsGrading =>
        GradingShadowsSat != 0 || GradingShadowsLum != 0 || GradingMidtonesSat != 0 || GradingMidtonesLum != 0
        || GradingHighlightsSat != 0 || GradingHighlightsLum != 0 || GradingGlobalSat != 0 || GradingGlobalLum != 0;

    public bool AdjustsDetail =>
        SharpenAmount != 0 || NoiseLuminance != 0 || NoiseColor != 0;

    /// <summary>The two profile sliders move nothing while their toggle is off, and the midpoint only steers a
    /// vignette that is being asked for, so neither of them counts here on its own
    /// (CameraRawDetailOptics.swift:65-68).</summary>
    public bool AdjustsOptics =>
        OpticsRemoveChromaticAberration || OpticsLensProfile || OpticsDistortion != 0 || OpticsPurpleAmount != 0
        || OpticsGreenAmount != 0 || OpticsVignetteAmount != 0;

    /// <summary>The seven numbers are the adjustment; the projection and Constrain Crop only shape how they are
    /// applied, so neither counts on its own (CameraRawGeometryCalibration.swift:41-44).</summary>
    public bool AdjustsGeometry =>
        GeometryUsesGuides || GeometryVertical != 0 || GeometryHorizontal != 0 || GeometryRotate != 0
        || GeometryAspect != 0 || GeometryScale != 0 || GeometryOffsetX != 0 || GeometryOffsetY != 0;

    /// <summary>Choosing Guided is not yet a request to bend: a line has to be long enough to read a direction off
    /// it, so an empty Guided choice leaves the picture alone (:46-49).</summary>
    public bool GeometryUsesGuides =>
        GeometryUpright == GeometryUprightGuided && GeometryGuides.Any(guide => guide.Length > 0.01);

    /// <summary>The light group and the color group, apart (CameraRaw.swift:103-108): the panel's two eyes need
    /// them separately, while one pass of the kernel serves both.</summary>
    public bool AdjustsLight =>
        Exposure != 0 || Contrast != 0 || Highlights != 0 || Shadows != 0 || Whites != 0 || Blacks != 0;

    public bool AdjustsColor =>
        Temperature != 0 || Tint != 0 || Vibrance != 0 || Saturation != 0;

    public bool AdjustsLightColor => AdjustsLight || AdjustsColor;

    /// <summary>Whether the group named by <see cref="GroupNames"/> has anything to hide — the condition the Mac
    /// puts in front of each eye (CameraRawControls.swift:139-148).</summary>
    public bool AdjustsGroup(int group) => group switch
    {
        GroupLight => AdjustsLight,
        GroupColor => AdjustsColor,
        GroupEffects => AdjustsEffects,
        GroupCurve => AdjustsCurve,
        GroupMixer => AdjustsMixer,
        GroupGrading => AdjustsGrading,
        GroupDetail => AdjustsDetail,
        GroupOptics => AdjustsOptics,
        GroupGeometry => AdjustsGeometry,
        GroupCalibration => AdjustsCalibration,
        _ => false,
    };

    public bool AdjustsCalibration =>
        CalibrationShadowTint != 0 || CalibrationRedHue != 0 || CalibrationRedSaturation != 0
        || CalibrationGreenHue != 0 || CalibrationGreenSaturation != 0
        || CalibrationBlueHue != 0 || CalibrationBlueSaturation != 0;

    public bool IsIdentity =>
        !AdjustsLightColor
        && !AdjustsCurve && !AdjustsMixer && !AdjustsEffects && !AdjustsGrading && !AdjustsDetail && !AdjustsOptics
        && !AdjustsCalibration && !AdjustsGeometry;

    public bool IsValid =>
        Exposure is >= ExposureMin and <= ExposureMax && !double.IsNaN(Exposure)
        && new[] { Contrast, Highlights, Shadows, Whites, Blacks, Temperature, Tint, Vibrance, Saturation }
            .All(v => !double.IsNaN(v) && v is >= ToneMin and <= ToneMax)
        && new[] { CurveShadows, CurveDarks, CurveLights, CurveHighlights, RefineSaturation, Texture, Clarity, Dehaze, VignetteAmount }
            .All(v => !double.IsNaN(v) && v is >= ToneMin and <= ToneMax)
        && SharpenAmount is >= 0 and <= SharpenAmountMax;
}
