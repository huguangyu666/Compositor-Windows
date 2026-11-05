using System.Text.Json;

namespace Compositor.Core;

/// <summary>Effect kinds, in the panel order the macOS app lists them.</summary>
public enum LayerEffectKind
{
    Stroke,
    Shadow,
    ColorOverlay,
    InnerShadow,
    OuterGlow,
    InnerGlow,
}

/// <summary>A line drawn around what the layer shows, outside its edge or inside it.</summary>
public sealed class StrokeEffect
{
    public bool? Enabled { get; set; }
    public double Size { get; set; } = 4;
    public double Red { get; set; }
    public double Green { get; set; }
    public double Blue { get; set; }
    public double Opacity { get; set; } = 1;
    public bool Inside { get; set; }

    /// <summary>Whether the settings sit in the supported ranges (StrokeEffect.swift).</summary>
    public bool IsValid => Size is >= 0 and <= 500 && Opacity is >= 0 and <= 1
        && Red is >= 0 and <= 1 && Green is >= 0 and <= 1 && Blue is >= 0 and <= 1;
}

/// <summary>The layer's shape repeated behind it, offset and softened.</summary>
public sealed class ShadowEffect
{
    public bool? Enabled { get; set; }
    /// <summary>Where the light comes from, degrees counterclockwise from the right.</summary>
    public double Angle { get; set; } = 90;
    public double Distance { get; set; } = 20;
    public double Blur { get; set; } = 20;
    public double Red { get; set; }
    public double Green { get; set; }
    public double Blue { get; set; }
    public double Opacity { get; set; } = 0.5;

    /// <summary>Where the shadow sits, in layer pixels (y grows downward): away from the light.</summary>
    public (double Dx, double Dy) Offset => (-Math.Cos(Angle * Math.PI / 180) * Distance, Math.Sin(Angle * Math.PI / 180) * Distance);

    /// <summary>Whether the settings sit in the supported ranges (ShadowEffect.swift).</summary>
    public bool IsValid => Angle is >= -360 and <= 360 && Distance is >= 0 and <= 5000 && Blur is >= 0 and <= 500
        && Opacity is >= 0 and <= 1 && Red is >= 0 and <= 1 && Green is >= 0 and <= 1 && Blue is >= 0 and <= 1;
}

/// <summary>A flat color over everything the layer shows.</summary>
public sealed class ColorOverlayEffect
{
    public bool? Enabled { get; set; }
    public double Red { get; set; }
    public double Green { get; set; }
    public double Blue { get; set; }
    public double Opacity { get; set; } = 1;

    /// <summary>Whether the settings sit in the supported ranges (ColorOverlayEffect.swift).</summary>
    public bool IsValid => Opacity is >= 0 and <= 1
        && Red is >= 0 and <= 1 && Green is >= 0 and <= 1 && Blue is >= 0 and <= 1;
}

/// <summary>A shadow cast inside the layer's own edges.</summary>
public sealed class InnerShadowEffect
{
    public bool? Enabled { get; set; }
    public double Angle { get; set; } = 90;
    public double Distance { get; set; } = 10;
    public double Blur { get; set; } = 10;
    public double Red { get; set; }
    public double Green { get; set; }
    public double Blue { get; set; }
    public double Opacity { get; set; } = 0.5;

    public (double Dx, double Dy) Offset => (-Math.Cos(Angle * Math.PI / 180) * Distance, Math.Sin(Angle * Math.PI / 180) * Distance);

    /// <summary>Whether the settings sit in the supported ranges (InnerShadowEffect.swift).</summary>
    public bool IsValid => Angle is >= -360 and <= 360 && Distance is >= 0 and <= 5000 && Blur is >= 0 and <= 500
        && Opacity is >= 0 and <= 1 && Red is >= 0 and <= 1 && Green is >= 0 and <= 1 && Blue is >= 0 and <= 1;
}

/// <summary>A soft glow drawn omnidirectionally around the outside of the layer.</summary>
public sealed class OuterGlowEffect
{
    public bool? Enabled { get; set; }
    public double Size { get; set; } = 20;
    public double Red { get; set; }
    public double Green { get; set; }
    public double Blue { get; set; }
    public double Opacity { get; set; }

    /// <summary>Whether the settings sit in the supported ranges (OuterGlowEffect.swift).</summary>
    public bool IsValid => Size is >= 0 and <= 500 && Opacity is >= 0 and <= 1
        && Red is >= 0 and <= 1 && Green is >= 0 and <= 1 && Blue is >= 0 and <= 1;
}

/// <summary>A soft glow inside the layer's own edges.</summary>
public sealed class InnerGlowEffect
{
    public bool? Enabled { get; set; }
    public double Size { get; set; } = 20;
    public double Red { get; set; }
    public double Green { get; set; }
    public double Blue { get; set; }
    public double Opacity { get; set; }

    /// <summary>Whether the settings sit in the supported ranges (InnerGlowEffect.swift).</summary>
    public bool IsValid => Size is >= 0 and <= 500 && Opacity is >= 0 and <= 1
        && Red is >= 0 and <= 1 && Green is >= 0 and <= 1 && Blue is >= 0 and <= 1;
}

/// <summary>
/// The layer's effects, parsed from the manifest's untyped Effects JSON (which the port
/// preserves verbatim). Parsed properties are read-only views; the renderer consumes them.
/// Unknown or future kinds are ignored.
/// </summary>
public sealed class LayerEffects
{
    public StrokeEffect? Stroke { get; set; }
    public ShadowEffect? Shadow { get; set; }
    public ColorOverlayEffect? ColorOverlay { get; set; }
    public InnerShadowEffect? InnerShadow { get; set; }
    public OuterGlowEffect? OuterGlow { get; set; }
    public InnerGlowEffect? InnerGlow { get; set; }

    public bool IsEmpty => Stroke is null && Shadow is null && ColorOverlay is null
        && InnerShadow is null && OuterGlow is null && InnerGlow is null;

    /// <summary>
    /// Every present effect within its own ranges, absent kinds tolerated — LayerEffects.swift's
    /// isValid, which the save path gates on, so a null Stroke must not sink the whole set.
    /// </summary>
    public bool IsValid => (Stroke?.IsValid ?? true) && (Shadow?.IsValid ?? true) && (ColorOverlay?.IsValid ?? true)
        && (InnerShadow?.IsValid ?? true) && (OuterGlow?.IsValid ?? true) && (InnerGlow?.IsValid ?? true);

    public bool Contains(LayerEffectKind kind) => kind switch
    {
        LayerEffectKind.Stroke => Stroke is not null,
        LayerEffectKind.Shadow => Shadow is not null,
        LayerEffectKind.ColorOverlay => ColorOverlay is not null,
        LayerEffectKind.InnerShadow => InnerShadow is not null,
        LayerEffectKind.OuterGlow => OuterGlow is not null,
        LayerEffectKind.InnerGlow => InnerGlow is not null,
        _ => false,
    };

    public bool IsEnabled(LayerEffectKind kind) => Contains(kind) && kind switch
    {
        LayerEffectKind.Stroke => Stroke!.Enabled != false,
        LayerEffectKind.Shadow => Shadow!.Enabled != false,
        LayerEffectKind.ColorOverlay => ColorOverlay!.Enabled != false,
        LayerEffectKind.InnerShadow => InnerShadow!.Enabled != false,
        LayerEffectKind.OuterGlow => OuterGlow!.Enabled != false,
        LayerEffectKind.InnerGlow => InnerGlow!.Enabled != false,
        _ => false,
    };

    public LayerEffects WithEnabled(LayerEffectKind kind, bool enabled)
    {
        var clone = Parse(Serialize()) ?? new();
        switch (kind)
        {
            case LayerEffectKind.Stroke when clone.Stroke is { } effect: effect.Enabled = enabled; break;
            case LayerEffectKind.Shadow when clone.Shadow is { } effect: effect.Enabled = enabled; break;
            case LayerEffectKind.ColorOverlay when clone.ColorOverlay is { } effect: effect.Enabled = enabled; break;
            case LayerEffectKind.InnerShadow when clone.InnerShadow is { } effect: effect.Enabled = enabled; break;
            case LayerEffectKind.OuterGlow when clone.OuterGlow is { } effect: effect.Enabled = enabled; break;
            case LayerEffectKind.InnerGlow when clone.InnerGlow is { } effect: effect.Enabled = enabled; break;
        }
        return clone;
    }

    /// <summary>The effect's own color.</summary>
    public (byte R, byte G, byte B) Color(LayerEffectKind kind)
    {
        (double R, double G, double B)? rgb = kind switch
        {
            LayerEffectKind.Stroke => Stroke is { } s ? (s.Red, s.Green, s.Blue) : null,
            LayerEffectKind.Shadow => Shadow is { } sh ? (sh.Red, sh.Green, sh.Blue) : null,
            LayerEffectKind.ColorOverlay => ColorOverlay is { } co ? (co.Red, co.Green, co.Blue) : null,
            LayerEffectKind.InnerShadow => InnerShadow is { } iS ? (iS.Red, iS.Green, iS.Blue) : null,
            LayerEffectKind.OuterGlow => OuterGlow is { } og ? (og.Red, og.Green, og.Blue) : null,
            LayerEffectKind.InnerGlow => InnerGlow is { } ig ? (ig.Red, ig.Green, ig.Blue) : null,
            _ => null,
        };
        // The byte conversion follows the palette's own (Document/ColorPalette.swift:358:
        // Int((red * 255).rounded())), and Swift's rounded() breaks ties away from zero — so a
        // channel sitting exactly on .5 must land on the higher byte, not the even one.
        return rgb is null ? ((byte)0, (byte)0, (byte)0)
            : ((byte)Math.Round(Math.Clamp(rgb.Value.Item1, 0, 1) * 255, MidpointRounding.AwayFromZero),
               (byte)Math.Round(Math.Clamp(rgb.Value.Item2, 0, 1) * 255, MidpointRounding.AwayFromZero),
               (byte)Math.Round(Math.Clamp(rgb.Value.Item3, 0, 1) * 255, MidpointRounding.AwayFromZero));
    }

    /// <summary>Opacity of the effect kind, 0–1.</summary>
    public double Opacity(LayerEffectKind kind) => kind switch
    {
        LayerEffectKind.Stroke => Math.Clamp(Stroke?.Opacity ?? 1, 0, 1),
        LayerEffectKind.Shadow => Math.Clamp(Shadow?.Opacity ?? 0.5, 0, 1),
        LayerEffectKind.ColorOverlay => Math.Clamp(ColorOverlay?.Opacity ?? 1, 0, 1),
        LayerEffectKind.InnerShadow => Math.Clamp(InnerShadow?.Opacity ?? 0.5, 0, 1),
        LayerEffectKind.OuterGlow => Math.Clamp(OuterGlow?.Opacity ?? 0, 0, 1),
        LayerEffectKind.InnerGlow => Math.Clamp(InnerGlow?.Opacity ?? 0, 0, 1),
        _ => 0,
    };

    private static double DoubleOf(JsonElement element, string name, double fallback) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d) ? d : fallback;

    private static bool BoolOf(JsonElement element, string name, bool fallback) =>
        !element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False
            ? fallback : value.GetBoolean();

    private static bool? EnabledOf(JsonElement element) => element.TryGetProperty("enabled", out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    /// <summary>Parses the manifest's Effects JSON. Returns null for null/empty/invalid shapes.</summary>
    public static LayerEffects? Parse(JsonElement? effects)
    {
        if (effects is not { } element || element.ValueKind != JsonValueKind.Object) return null;
        StrokeEffect? stroke = null;
        if (element.TryGetProperty("stroke", out var s) && s.ValueKind == JsonValueKind.Object)
        {
            stroke = new StrokeEffect
            {
                Enabled = EnabledOf(s),
                Size = DoubleOf(s, "size", 4),
                Red = DoubleOf(s, "red", 0),
                Green = DoubleOf(s, "green", 0),
                Blue = DoubleOf(s, "blue", 0),
                Opacity = DoubleOf(s, "opacity", 1),
                Inside = BoolOf(s, "inside", false),
            };
        }
        ShadowEffect? shadow = null;
        if (element.TryGetProperty("shadow", out var sh) && sh.ValueKind == JsonValueKind.Object)
        {
            shadow = new ShadowEffect
            {
                Enabled = EnabledOf(sh),
                Angle = DoubleOf(sh, "angle", 90),
                Distance = DoubleOf(sh, "distance", 20),
                Blur = DoubleOf(sh, "blur", 20),
                Red = DoubleOf(sh, "red", 0),
                Green = DoubleOf(sh, "green", 0),
                Blue = DoubleOf(sh, "blue", 0),
                Opacity = DoubleOf(sh, "opacity", 0.5),
            };
        }
        ColorOverlayEffect? overlay = null;
        if (element.TryGetProperty("colorOverlay", out var ov) && ov.ValueKind == JsonValueKind.Object)
        {
            overlay = new ColorOverlayEffect
            {
                Enabled = EnabledOf(ov),
                Red = DoubleOf(ov, "red", 0),
                Green = DoubleOf(ov, "green", 0),
                Blue = DoubleOf(ov, "blue", 0),
                Opacity = DoubleOf(ov, "opacity", 1),
            };
        }
        InnerShadowEffect? innerShadow = null;
        if (element.TryGetProperty("innerShadow", out var iS) && iS.ValueKind == JsonValueKind.Object)
        {
            innerShadow = new InnerShadowEffect
            {
                Enabled = EnabledOf(iS),
                Angle = DoubleOf(iS, "angle", 90),
                Distance = DoubleOf(iS, "distance", 10),
                Blur = DoubleOf(iS, "blur", 10),
                Red = DoubleOf(iS, "red", 0),
                Green = DoubleOf(iS, "green", 0),
                Blue = DoubleOf(iS, "blue", 0),
                Opacity = DoubleOf(iS, "opacity", 0.5),
            };
        }
        OuterGlowEffect? outerGlow = null;
        if (element.TryGetProperty("outerGlow", out var og) && og.ValueKind == JsonValueKind.Object)
        {
            outerGlow = new OuterGlowEffect
            {
                Enabled = EnabledOf(og),
                Size = DoubleOf(og, "size", 20),
                Red = DoubleOf(og, "red", 0),
                Green = DoubleOf(og, "green", 0),
                Blue = DoubleOf(og, "blue", 0),
                Opacity = DoubleOf(og, "opacity", 0),
            };
        }
        InnerGlowEffect? innerGlow = null;
        if (element.TryGetProperty("innerGlow", out var ig) && ig.ValueKind == JsonValueKind.Object)
        {
            innerGlow = new InnerGlowEffect
            {
                Enabled = EnabledOf(ig),
                Size = DoubleOf(ig, "size", 20),
                Red = DoubleOf(ig, "red", 0),
                Green = DoubleOf(ig, "green", 0),
                Blue = DoubleOf(ig, "blue", 0),
                Opacity = DoubleOf(ig, "opacity", 0),
            };
        }
        var result = new LayerEffects
        {
            Stroke = stroke, Shadow = shadow, ColorOverlay = overlay,
            InnerShadow = innerShadow, OuterGlow = outerGlow, InnerGlow = innerGlow,
        };
        return result.IsEmpty ? null : result;
    }

    /// <summary>Serializes back to the manifest's Effects JSON shape (camelCase, nulls kept
    /// for absent kinds so untouched kinds stay absent).</summary>
    public JsonElement Serialize()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (Stroke is { } stroke)
            {
                writer.WriteStartObject("stroke");
                if (stroke.Enabled is { } strokeEnabled) writer.WriteBoolean("enabled", strokeEnabled);
                writer.WriteNumber("size", stroke.Size);
                writer.WriteNumber("red", stroke.Red);
                writer.WriteNumber("green", stroke.Green);
                writer.WriteNumber("blue", stroke.Blue);
                writer.WriteNumber("opacity", stroke.Opacity);
                writer.WriteBoolean("inside", stroke.Inside);
                writer.WriteEndObject();
            }
            if (Shadow is { } shadow)
            {
                writer.WriteStartObject("shadow");
                if (shadow.Enabled is { } shadowEnabled) writer.WriteBoolean("enabled", shadowEnabled);
                writer.WriteNumber("angle", shadow.Angle);
                writer.WriteNumber("distance", shadow.Distance);
                writer.WriteNumber("blur", shadow.Blur);
                writer.WriteNumber("red", shadow.Red);
                writer.WriteNumber("green", shadow.Green);
                writer.WriteNumber("blue", shadow.Blue);
                writer.WriteNumber("opacity", shadow.Opacity);
                writer.WriteEndObject();
            }
            if (ColorOverlay is { } overlay)
            {
                writer.WriteStartObject("colorOverlay");
                if (overlay.Enabled is { } overlayEnabled) writer.WriteBoolean("enabled", overlayEnabled);
                writer.WriteNumber("red", overlay.Red);
                writer.WriteNumber("green", overlay.Green);
                writer.WriteNumber("blue", overlay.Blue);
                writer.WriteNumber("opacity", overlay.Opacity);
                writer.WriteEndObject();
            }
            if (InnerShadow is { } innerShadow)
            {
                writer.WriteStartObject("innerShadow");
                if (innerShadow.Enabled is { } innerShadowEnabled) writer.WriteBoolean("enabled", innerShadowEnabled);
                writer.WriteNumber("angle", innerShadow.Angle);
                writer.WriteNumber("distance", innerShadow.Distance);
                writer.WriteNumber("blur", innerShadow.Blur);
                writer.WriteNumber("red", innerShadow.Red);
                writer.WriteNumber("green", innerShadow.Green);
                writer.WriteNumber("blue", innerShadow.Blue);
                writer.WriteNumber("opacity", innerShadow.Opacity);
                writer.WriteEndObject();
            }
            if (OuterGlow is { } outerGlow)
            {
                writer.WriteStartObject("outerGlow");
                if (outerGlow.Enabled is { } outerGlowEnabled) writer.WriteBoolean("enabled", outerGlowEnabled);
                writer.WriteNumber("size", outerGlow.Size);
                writer.WriteNumber("red", outerGlow.Red);
                writer.WriteNumber("green", outerGlow.Green);
                writer.WriteNumber("blue", outerGlow.Blue);
                writer.WriteNumber("opacity", outerGlow.Opacity);
                writer.WriteEndObject();
            }
            if (InnerGlow is { } innerGlow)
            {
                writer.WriteStartObject("innerGlow");
                if (innerGlow.Enabled is { } innerGlowEnabled) writer.WriteBoolean("enabled", innerGlowEnabled);
                writer.WriteNumber("size", innerGlow.Size);
                writer.WriteNumber("red", innerGlow.Red);
                writer.WriteNumber("green", innerGlow.Green);
                writer.WriteNumber("blue", innerGlow.Blue);
                writer.WriteNumber("opacity", innerGlow.Opacity);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    /// <summary>A copy of this effects object with `kind` added (Mac defaults) or removed.</summary>
    public LayerEffects WithKind(LayerEffectKind kind, bool present)
    {
        var clone = Parse(Serialize()) ?? new LayerEffects();
        if (present)
        {
            switch (kind)
            {
                case LayerEffectKind.Stroke when clone.Stroke is null:
                    clone.Stroke = new StrokeEffect(); break;
                case LayerEffectKind.Shadow when clone.Shadow is null:
                    clone.Shadow = new ShadowEffect(); break;
                case LayerEffectKind.ColorOverlay when clone.ColorOverlay is null:
                    clone.ColorOverlay = new ColorOverlayEffect(); break;
                case LayerEffectKind.InnerShadow when clone.InnerShadow is null:
                    clone.InnerShadow = new InnerShadowEffect(); break;
                case LayerEffectKind.OuterGlow when clone.OuterGlow is null:
                    clone.OuterGlow = new OuterGlowEffect(); break;
                case LayerEffectKind.InnerGlow when clone.InnerGlow is null:
                    clone.InnerGlow = new InnerGlowEffect(); break;
            }
        }
        else
        {
            clone.Stroke = kind == LayerEffectKind.Stroke ? null : clone.Stroke;
            clone.Shadow = kind == LayerEffectKind.Shadow ? null : clone.Shadow;
            clone.ColorOverlay = kind == LayerEffectKind.ColorOverlay ? null : clone.ColorOverlay;
            clone.InnerShadow = kind == LayerEffectKind.InnerShadow ? null : clone.InnerShadow;
            clone.OuterGlow = kind == LayerEffectKind.OuterGlow ? null : clone.OuterGlow;
            clone.InnerGlow = kind == LayerEffectKind.InnerGlow ? null : clone.InnerGlow;
        }
        return clone.IsEmpty ? new LayerEffects() : Parse(clone.Serialize()) ?? clone;
    }
}
