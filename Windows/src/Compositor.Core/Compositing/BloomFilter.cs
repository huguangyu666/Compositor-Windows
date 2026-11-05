namespace Compositor.Core;

/// <summary>
/// The Bloom / Glow filter, ported from Core Image's <c>CIBloom</c> (Filters.swift:236-241): the
/// picture's own light, blurred and screened back over it, so bright pixels cast a glow that stays
/// visible beyond them. The sheet hands it the grown grid (the Mac's bloomMargin is radius×3+2,
/// Filters.swift:386) exactly as the blur filters do.
///
/// CIBloom's documented composition — "a soft light source using screen blend" — is what this is:
/// blur the premultiplied picture, scale that glow by the intensity, and screen it over the original
/// channel by channel, alpha included, which is why the glow is visible where the layer had nothing
/// (the Mac's test: bloom must remain visible beyond a transparent layer's bright pixels) and why an
/// opaque picture keeps its opacity (screening a full-alpha original gives full alpha).
/// The intensity is Amount ÷ 50 (Filters.swift:239, so 0…100 % maps to 0…2), and the blur radius is
/// the sheet's radius directly (:238) — the same sigma reading the Gaussian filter path uses.
/// A glow stronger than what a screen has left room for is clamped, the way every premultiplied
/// write in this port clamps.
/// </summary>
public static class BloomFilter
{
    public const double DefaultAmount = 40;     // Filters.swift:62, clamped 0…100 at :104
    public const double DefaultRadius = 24;     // Filters.swift:63, clamped 1…150 at :105

    /// <summary>Bloom over the whole premultiplied surface, in place.</summary>
    public static void Run(Surface surface, double amount, double radius)
    {
        if (amount <= 0) return;                // Amount 0 is the identity (and the Mac's OK-cancel rule,
                                                // Filters.swift:605)
        // The glow: the picture blurred with the sheet's radius. Clamping at this surface's own edge is
        // fine here — the sheet runs the filter on the grown grid whose padding is the reach itself,
        // so the interior never reads a clamped edge.
        var glow = surface.Clone();
        AdjustmentEngine.ApplyGaussianBlur(radius, glow, 0, 0, glow.Width, glow.Height);

        double intensity = amount / 50.0;
        var source = surface.Pixels;
        var glowBytes = glow.Pixels;
        for (int i = 0; i < source.Length; i += 4)
        {
            for (int channel = 0; channel < 4; channel++)
            {
                float original = source[i + channel], spread = glowBytes[i + channel];
                // Screen: out = a + b·i·(1 − a), on the 0…1 the bytes stand for; clamped, then back to bytes.
                float over = Math.Min(1f, spread / 255f * (float)intensity);
                float result = original / 255f + over * (1 - original / 255f);
                source[i + channel] = (byte)Math.Clamp(MathF.Round(result * 255f, MidpointRounding.AwayFromZero), 0, 255);
            }
        }
    }
}
