namespace Compositor.Core;

/// <summary>
/// The manifest's raw string for an enum member, shared by the JSON converters and validation.
/// Members not listed keep their C# name, which matches the macOS raw values for them.
/// </summary>
public static class RawEnumText
{
    public static string Resolve<T>(string memberName) where T : struct, Enum => Resolve(typeof(T).Name, memberName);

    public static string Resolve(string enumName, string memberName) => (enumName, memberName) switch
    {
        (nameof(GuideAxis), nameof(GuideAxis.Horizontal)) => "horizontal",
        (nameof(GuideAxis), nameof(GuideAxis.Vertical)) => "vertical",
        (nameof(LayerSampling), nameof(LayerSampling.HighQuality)) => "High quality",
        (nameof(LayerBlendMode), nameof(LayerBlendMode.ColorBurn)) => "Color Burn",
        (nameof(LayerBlendMode), nameof(LayerBlendMode.LinearBurn)) => "Linear Burn",
        (nameof(LayerBlendMode), nameof(LayerBlendMode.ColorDodge)) => "Color Dodge",
        (nameof(LayerBlendMode), nameof(LayerBlendMode.LinearDodge)) => "Linear Dodge (Add)",
        (nameof(LayerBlendMode), nameof(LayerBlendMode.SoftLight)) => "Soft Light",
        (nameof(LayerBlendMode), nameof(LayerBlendMode.HardLight)) => "Hard Light",
        (nameof(LayerBlendMode), nameof(LayerBlendMode.VividLight)) => "Vivid Light",
        (nameof(LayerBlendMode), nameof(LayerBlendMode.LinearLight)) => "Linear Light",
        (nameof(LayerBlendMode), nameof(LayerBlendMode.PinLight)) => "Pin Light",
        (nameof(LayerBlendMode), nameof(LayerBlendMode.HardMix)) => "Hard Mix",
        (nameof(AdjustmentKind), nameof(AdjustmentKind.HueSaturation)) => "Hue/Saturation",
        (nameof(AdjustmentKind), nameof(AdjustmentKind.GradientMap)) => "Gradient Map",
        (nameof(AdjustmentKind), nameof(AdjustmentKind.AddNoise)) => "Add Noise",
        (nameof(AdjustmentKind), nameof(AdjustmentKind.GaussianBlur)) => "Gaussian Blur",
        (nameof(AdjustmentKind), nameof(AdjustmentKind.MotionBlur)) => "Motion Blur",
        (nameof(AdjustmentKind), nameof(AdjustmentKind.Invert)) => "Invert",
        (nameof(AdjustmentKind), nameof(AdjustmentKind.BlackWhite)) => "Black & White",
        (nameof(AdjustmentKind), nameof(AdjustmentKind.ColorBalance)) => "Color Balance",
        (nameof(LevelsChannel), nameof(LevelsChannel.Rgb)) => "RGB",
        _ => memberName,
    };
}
