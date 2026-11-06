// Uses Compositor's byte rounding between adjustments. Win2D custom effect integration:
// https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Effects_PixelShaderEffect.htm
#define D2D_INPUT_COUNT 3
#define D2D_INPUT0_SIMPLE
#define D2D_INPUT1_COMPLEX
#define D2D_INPUT2_SIMPLE
#define D2D_REQUIRES_SCENE_POSITION
#include "ShaderHost.hlsli"

int Kind;
int PreserveLuminosity;
int Masked;
int ArithmeticBarrier; // Always zero; stops shader-model-4 linking from contracting CPU rounding steps.

float Rounded(float value) { return asfloat(asuint(value) ^ uint(ArithmeticBarrier)); }

float Luminance(float3 value)
{
    float red = Rounded(.299 * value.r), green = Rounded(.587 * value.g), blue = Rounded(.114 * value.b);
    return Rounded(Rounded(red + green) + blue);
}

float3 RoundAway(float3 value)
{
    float3 whole = floor(value);
    return whole + step(.5, value - whole);
}

// Direct3D division may use an approximate reciprocal. Restore IEEE single-precision
// rounding for the positive, normal luminosity ratio, including ties to even.
float LuminosityRatio(float numerator, float denominator)
{
#ifdef FAST_DIVISION
    if (numerator == 0) return 0;
    uint n = asuint(numerator), d = asuint(denominator);
    int exponent = int(n >> 23) - int(d >> 23) + 127;
    uint mantissa = (n & 0x7fffff) | 0x800000, divisor = (d & 0x7fffff) | 0x800000;
    if (mantissa < divisor) { mantissa <<= 1; exponent--; }
    // The reciprocal supplies an estimate. Correct its 48-bit integer product before
    // rounding, so this needs neither double division nor a 23-iteration bit loop.
    // D3D11's reciprocal bound: microsoft/DirectX-Specs, Floating Point Rules 3.1.
    uint quotient = uint(float(mantissa) / float(divisor) * 8388608.0);
    uint qLow = quotient & 0xffff, dLow = divisor & 0xffff;
    uint qHigh = quotient >> 16, dHigh = divisor >> 16;
    uint lo = qLow * dLow, cross = qHigh * dLow + qLow * dHigh;
    uint initialLo = lo; lo += cross << 16;
    uint hi = qHigh * dHigh + (cross >> 16) + (lo < initialLo ? 1 : 0);
    uint targetLo = mantissa << 23, targetHi = mantissa >> 9;
    [loop] while (hi > targetHi || (hi == targetHi && lo > targetLo))
    {
        quotient--; uint oldLo = lo; lo -= divisor; hi -= oldLo < divisor ? 1 : 0;
    }
    uint remainder = targetLo - lo;
    uint upper = targetHi - hi - (targetLo < lo ? 1 : 0);
    [loop] while (upper != 0 || remainder >= divisor)
    {
        quotient++; uint oldRemainder = remainder; remainder -= divisor;
        upper -= oldRemainder < divisor ? 1 : 0;
    }
    uint twice = remainder << 1;
    if (twice > divisor || (twice == divisor && (quotient & 1) != 0)) quotient++;
    if (quotient == 0x1000000) { quotient >>= 1; exponent++; }
    return asfloat((uint(exponent) << 23) | (quotient & 0x7fffff));
#else
    if (numerator == 0) return 0;
    uint n = asuint(numerator), d = asuint(denominator);
    int exponent = int(n >> 23) - int(d >> 23) + 127;
    uint remainder = (n & 0x7fffff) | 0x800000;
    uint divisor = (d & 0x7fffff) | 0x800000;
    if (remainder < divisor) { remainder <<= 1; exponent--; }
    remainder -= divisor;
    uint quotient = 0x800000;
    [loop] for (int bit = 22; bit >= 0; bit--)
    {
        remainder <<= 1;
        if (remainder >= divisor) { remainder -= divisor; quotient |= 1u << bit; }
    }
    uint twice = remainder << 1;
    if (twice > divisor || (twice == divisor && (quotient & 1) != 0)) quotient++;
    if (quotient == 0x1000000) { quotient >>= 1; exponent++; }
    return asfloat((uint(exponent) << 23) | (quotient & 0x7fffff));
#endif
}

float4 main(float4 scenePosition : SV_Position) : SV_Target
{
    precise float4 original = round(Load0(scenePosition.xy) * 255.0);
    if (original.a == 0) return original / 255.0;
    // Preserve the opaque integer lookup. A GPU reciprocal of 255 can leave v-epsilon,
    // which would select the preceding Color Balance table entry after floor().
    precise float3 color = original.a == 255 ? original.rgb : min(255.0, original.rgb * 255.0 / original.a);
    precise float3 adjusted;
    if (Kind == 2) adjusted = original.a - original.rgb;
    else if (Kind == 1)
    {
        float4 red = Load1(float2(original.r + .5, original.a + .5));
        float4 green = Load1(float2(original.g + .5, original.a + .5));
        float4 blue = Load1(float2(original.b + .5, original.a + .5));
        precise float3 unit = float3(red.a, green.a, blue.a);
        adjusted = float3(red.r, green.g, blue.b);
        if (PreserveLuminosity != 0)
        {
            precise float before = Luminance(unit);
            precise float after = Luminance(adjusted);
            if (after > .0001)
            {
                precise float ratio = LuminosityRatio(before, after);
                adjusted = saturate(adjusted * ratio);
            }
        }
        adjusted = clamp(RoundAway(adjusted * original.a), 0.0, original.a);
    }
    else
    {
        float3 lo = floor(color), hi = min(lo + 1.0, 255.0);
        precise float3 low = float3(Load1(float2(lo.r + .5, .5)).r,
            Load1(float2(lo.g + .5, .5)).g,
            Load1(float2(lo.b + .5, .5)).b);
        precise float3 high = float3(Load1(float2(hi.r + .5, .5)).r,
            Load1(float2(hi.g + .5, .5)).g,
            Load1(float2(hi.b + .5, .5)).b);
        adjusted = clamp(RoundAway((low + (high - low) * (color - lo)) * original.a), 0.0, original.a);
    }
    if (Masked != 0)
    {
        uint coverage = (uint)round(Load2(scenePosition.xy).r * 255.0);
        // Quantize the adjustment before blending its mask, as the CPU compositor does.
        uint3 numerator = (uint3)adjusted * coverage + (uint3)original.rgb * (255 - coverage) + 127;
        adjusted = numerator / 255;
    }
    return float4(adjusted, original.a) / 255.0;
}
