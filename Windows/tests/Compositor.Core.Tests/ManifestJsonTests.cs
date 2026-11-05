using System.Text.Json;
using Xunit;

namespace Compositor.Core.Tests;

public class ManifestJsonTests
{
    private const string MinimalManifestJson = """
        {
          "format": "com.compositor.project",
          "version": 11,
          "colorSpace": "sRGB",
          "documentID": "0C5E7A91-3B2D-4F6A-8E1C-9D0B7A6F5E4D",
          "width": 1920,
          "height": 1080,
          "resolution": 72,
          "activeLayerID": "6F1D3C2A-0B7E-4E8A-9C4D-2A1B3C4D5E6F",
          "layers": [
            {
              "id": "6F1D3C2A-0B7E-4E8A-9C4D-2A1B3C4D5E6F",
              "name": "Background",
              "imageFile": "6F1D3C2A-0B7E-4E8A-9C4D-2A1B3C4D5E6F.png",
              "isVisible": true,
              "isGroup": false,
              "opacity": 1,
              "blendMode": "Normal",
              "transform": {
                "origin": [0, 0],
                "size": [1920, 1080],
                "rotation": 0,
                "flipX": false,
                "flipY": false,
                "sampling": "High quality"
              }
            }
          ]
        }
        """;

    [Fact]
    public void DeserializesTheDocumentedMinimalManifest()
    {
        var manifest = ManifestJson.Deserialize(MinimalManifestJson);

        Assert.Equal(ProjectManifest.FormatIdentifier, manifest.Format);
        Assert.Equal(11, manifest.Version);
        Assert.Equal("sRGB", manifest.ColorSpace);
        Assert.Equal(72, manifest.Resolution);
        Assert.Equal(1920, manifest.Width);
        Assert.Equal(1080, manifest.Height);
        var layer = Assert.Single(manifest.Layers);
        Assert.Equal("Background", layer.Name);
        Assert.True(layer.IsVisible);
        Assert.False(layer.IsGroupOrFalse);
        Assert.Equal(1, layer.Opacity);
        Assert.Equal(LayerBlendMode.Normal, layer.BlendMode);
        Assert.Equal(LayerSampling.HighQuality, layer.Transform.Sampling);
        Assert.Equal(1920, layer.Transform.Size.Width);
    }

    [Fact]
    public void SerializesBackToTheDocumentedShape()
    {
        var manifest = ManifestJson.Deserialize(MinimalManifestJson);
        string roundTripped = ManifestJson.Serialize(manifest);

        // The shape, not the byte order, is the contract: parse and compare to the original.
        using var expected = JsonDocument.Parse(MinimalManifestJson);
        using var actual = JsonDocument.Parse(roundTripped);
        Assert.True(JsonElement.DeepEquals(expected.RootElement, actual.RootElement));

        // And the macOS conventions survive: uppercase UUIDs and array-form points.
        Assert.Contains("6F1D3C2A-0B7E-4E8A-9C4D-2A1B3C4D5E6F", roundTripped);
        Assert.Contains("\"sampling\": \"High quality\"", roundTripped);
        Assert.Contains("\"blendMode\": \"Normal\"", roundTripped);
    }

    [Fact]
    public void OptionalFieldsRoundTripByteForByte()
    {
        var manifest = ManifestJson.Deserialize(MinimalManifestJson);
        var layer = manifest.Layers[0];
        layer.Opacity = 0.5;
        layer.BlendMode = LayerBlendMode.LinearDodge;
        layer.MaskFile = $"{layer.Id}".ToUpperInvariant() + ".mask.png";
        layer.MaskEnabled = false;
        layer.MaskLinked = false;
        layer.MaskPlacement = new LayerTransform
        {
            Origin = new PointD(10, 20),
            Size = new SizeD(300, 200),
            Rotation = -15.5,
            Sampling = LayerSampling.Smooth,
        };
        layer.ParentId = null;
        manifest.Guides =
        [
            new CanvasGuide { Id = Guid.NewGuid(), Axis = GuideAxis.Horizontal, Position = 140.25 },
            new CanvasGuide { Id = Guid.NewGuid(), Axis = GuideAxis.Vertical, Position = -80 },
        ];

        var parsed = ManifestJson.Deserialize(ManifestJson.Serialize(manifest));

        Assert.Equal(manifest.Layers[0].MaskFile, parsed.Layers[0].MaskFile);
        Assert.Equal(manifest.Layers[0].MaskEnabled, parsed.Layers[0].MaskEnabled);
        Assert.Equal(manifest.Layers[0].MaskLinked, parsed.Layers[0].MaskLinked);
        Assert.Equal(manifest.Layers[0].MaskPlacement, parsed.Layers[0].MaskPlacement);
        Assert.Equal(manifest.Guides, parsed.Guides);
        Assert.Equal(LayerBlendMode.LinearDodge, parsed.Layers[0].BlendMode);
    }

    [Fact]
    public void UntouchedFieldsSurviveAsWritten()
    {
        // Fields the Windows app does not model yet must come back exactly as they went in.
        string json = MinimalManifestJson.Replace(
            "\"name\": \"Background\",",
            """
            "name": "Shape",
            "shape": { "kind": "Rectangle", "red": 1, "green": 0, "blue": 0.5, "cornerRadius": 8 },
            "effects": { "stroke": { "size": 4, "red": 0, "green": 0, "blue": 0, "opacity": 1, "inside": true } },
            """,
            StringComparison.Ordinal);
        var first = ManifestJson.Deserialize(json);
        var second = ManifestJson.Deserialize(ManifestJson.Serialize(first));

        Assert.True(JsonElement.DeepEquals(first.Layers[0].Shape!.Value, second.Layers[0].Shape!.Value));
        Assert.True(JsonElement.DeepEquals(first.Layers[0].Effects!.Value, second.Layers[0].Effects!.Value));
    }

    [Fact]
    public void MissingRequiredFieldsAreRejected()
    {
        AssertInvalid(MinimalManifestJson.Replace("\"version\": 11,", "", StringComparison.Ordinal));
        AssertInvalid(MinimalManifestJson.Replace("\"width\": 1920,", "", StringComparison.Ordinal));
        AssertInvalid(MinimalManifestJson.Replace("\"isVisible\": true,", "", StringComparison.Ordinal));
        AssertInvalid(MinimalManifestJson.Replace("\"rotation\": 0,", "", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownFieldsAreIgnoredOnRead()
    {
        // Additive fields from a newer reader must not break an older one.
        string json = MinimalManifestJson.Replace(
            "\"layers\": [",
            "\"someFutureField\": true, \"layers\": [",
            StringComparison.Ordinal);
        var manifest = ManifestJson.Deserialize(json);
        Assert.Single(manifest.Layers);
    }

    [Fact]
    public void BlendModesRoundTripThroughTheirExactStrings()
    {
        foreach (var mode in Enum.GetValues<LayerBlendMode>())
        {
            string raw = RawEnumText.Resolve<LayerBlendMode>(Enum.GetName(mode)!);
            string json = "{ \"format\": \"com.compositor.project\", \"version\": 11, \"colorSpace\": \"sRGB\","
                + $"\"documentID\": \"{Guid.NewGuid():D}\", \"width\": 1, \"height\": 1, \"layers\": [ {{"
                + $"\"id\": \"{Guid.NewGuid():D}\", \"name\": \"L\", \"isVisible\": true, "
                + $"\"opacity\": 1, \"blendMode\": \"{raw}\", "
                + "\"transform\": { \"origin\": [0, 0], \"size\": [1, 1], \"rotation\": 0, \"flipX\": false, \"flipY\": false, \"sampling\": \"Nearest\" } }] }";

            var manifest = ManifestJson.Deserialize(json);
            Assert.Equal(mode, manifest.Layers[0].BlendMode);
            string written = ManifestJson.Serialize(manifest);
            Assert.Contains($"\"blendMode\": \"{raw}\"", written);
        }
    }

    [Fact]
    public void EnumValuesAreCaseSensitive()
    {
        string json = MinimalManifestJson.Replace("\"High quality\"", "\"high quality\"", StringComparison.Ordinal);
        Assert.Throws<ProjectFormatException>(() => ManifestJson.Deserialize(json));
    }

    [Fact]
    public void AdjustmentSettingsRoundTrip()
    {
        string json = $$"""
            {
              "format": "com.compositor.project", "version": 11, "colorSpace": "sRGB",
              "documentID": "0C5E7A91-3B2D-4F6A-8E1C-9D0B7A6F5E4D", "width": 64, "height": 64,
              "layers": [ {
                "id": "A1B2C3D4-E5F6-4A7B-8C9D-0E1F2A3B4C5D", "name": "Warm Grade",
                "isVisible": true, "isGroup": false, "opacity": 1, "blendMode": "Normal",
                "transform": { "origin": [0, 0], "size": [64, 64], "rotation": 0, "flipX": false, "flipY": false, "sampling": "High quality" },
                "adjustment": {
                  "kind": "Curves",
                  "hue": 0, "saturation": 0, "lightness": 0, "colorize": false,
                  "levels": { "channel": "RGB", "ranges": [
                    { "black": 0, "gamma": 1, "white": 255, "outputBlack": 0, "outputWhite": 255 },
                    { "black": 0, "gamma": 1, "white": 255, "outputBlack": 0, "outputWhite": 255 },
                    { "black": 0, "gamma": 1, "white": 255, "outputBlack": 0, "outputWhite": 255 },
                    { "black": 12, "gamma": 1.1, "white": 243, "outputBlack": 5, "outputWhite": 250 } ] },
                  "curves": { "channel": "RGB", "channels": [
                    [ { "x": 0, "y": 0 }, { "x": 255, "y": 255 } ],
                    [ { "x": 0, "y": 0 }, { "x": 120, "y": 147 }, { "x": 255, "y": 255 } ],
                    [ { "x": 0, "y": 0 }, { "x": 100, "y": 114 }, { "x": 255, "y": 255 } ],
                    [ { "x": 0, "y": 0 }, { "x": 115, "y": 97 }, { "x": 255, "y": 238 } ] ] },
                  "exposureSettings": { "exposure": 0.5, "offset": -0.1, "gamma": 1.2 },
                  "gradientMapSettings": { "shadows": { "red": 0.1, "green": 0, "blue": 0 }, "highlights": { "red": 1, "green": 0.9, "blue": 0.8 }, "reversed": true },
                  "grainSettings": { "amount": 30, "size": 2.5, "roughness": 40, "seed": 123456789 },
                  "blackWhiteSettings": { "reds": 41, "yellows": 61, "greens": 40, "cyans": 60, "blues": 20, "magentas": 80, "tint": true, "tintHue": 40, "tintSaturation": 20 },
                  "colorBalanceSettings": { "shadowCyanRed": -10, "shadowMagentaGreen": 0, "shadowYellowBlue": 15, "midCyanRed": 0, "midMagentaGreen": 5, "midYellowBlue": 0, "highlightCyanRed": 0, "highlightMagentaGreen": 0, "highlightYellowBlue": -5, "preserveLuminosity": false },
                  "blurRadius": 14.5, "motionAngle": 33, "motionDistance": 44,
                  "noiseAmount": 12.5, "noiseGaussian": true, "noiseMonochromatic": true, "noiseSeed": 4242424242
                }
              } ]
            }
            """;

        var parsed = ManifestJson.Deserialize(json);
        var adjustment = parsed.Layers[0].Adjustment;
        Assert.NotNull(adjustment);
        Assert.True(adjustment.IsValid);
        Assert.Equal(14.5, adjustment.GaussianRadius);
        Assert.Equal(4242424242u, adjustment.NoiseSeed);
        Assert.Equal(0.5, adjustment.ExposureSettings!.Exposure);
        Assert.True(adjustment.GradientMapSettings!.Reversed);
        Assert.False(adjustment.ColorBalanceSettings!.PreserveLuminosity);
        Assert.Equal(123456789u, adjustment.GrainSettings!.Seed);

        var roundTripped = ManifestJson.Deserialize(ManifestJson.Serialize(parsed));
        Assert.True(adjustment.Equals(roundTripped.Layers[0].Adjustment));
        ProjectValidator.Validate(roundTripped);
    }

    private static void AssertInvalid(string json)
    {
        try
        {
            ManifestJson.Deserialize(json);
            Assert.Fail("Expected a ProjectFormatException");
        }
        catch (ProjectFormatException ex)
        {
            Assert.Equal(ProjectErrorKind.Invalid, ex.Kind);
        }
    }
}
