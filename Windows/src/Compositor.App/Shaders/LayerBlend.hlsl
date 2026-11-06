// The reference BlendMath/CanvasCompositor formulas through Microsoft's MIT-licensed Win2D.
// https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Effects_PixelShaderEffect.htm
#define D2D_INPUT_COUNT 3
#define D2D_INPUT0_SIMPLE
#define D2D_INPUT1_SIMPLE
#define D2D_INPUT2_COMPLEX
#define D2D_REQUIRES_SCENE_POSITION
#include "ShaderHost.hlsli"
#include "FloatMath.hlsli"
float4 Region;

int Mode;
int Replace;
float Opacity;
float Keep;

float Burn(float b, float s) { return b >= 1 ? 1 : s <= 0 ? 0 : saturate(Add(1, -Div(Add(1, -b), s))); }
float Dodge(float b, float s) { return b <= 0 ? 0 : s >= 1 ? 1 : saturate(Div(b, Add(1, -s))); }
float Screen(float b, float s) { return Add(Add(b, s), -Mul(b, s)); }
float Hard(float b, float s) { return s <= .5 ? Mul(Mul(b, s), 2) : Screen(b, Add(Mul(2, s), -1)); }
float Vivid(float b, float s) { return s <= .5 ? Burn(b, Mul(s, 2)) : Dodge(b, Mul(2, Add(s, -.5))); }
float Blend(float b, float s)
{
    if (Mode == 1) return min(b, s);
    if (Mode == 2) return Mul(b, s);
    if (Mode == 3) return Burn(b, s);
    if (Mode == 4) return saturate(Add(Add(b, s), -1));
    if (Mode == 5) return max(b, s);
    if (Mode == 6) return Screen(b, s);
    if (Mode == 7) return Dodge(b, s);
    if (Mode == 8) return saturate(Add(b, s));
    if (Mode == 9) return Hard(s, b);
    if (Mode == 10)
    {
        float darkened = Add(Mul(Mul(Add(1, -Mul(2, s)), b), b), Mul(Mul(2, s), b));
        float g = b <= .25 ? Mul(Add(Mul(Add(Mul(16, b), -12), b), 4), b) : sqrt(b);
        return s <= .5 ? darkened : Add(b, Mul(Add(Mul(2, s), -1), Add(g, -b)));
    }
    if (Mode == 11) return Hard(b, s);
    if (Mode == 12) return Vivid(b, s);
    if (Mode == 13) return s <= .5 ? saturate(Add(Add(b, Mul(2, s)), -1)) : saturate(Add(b, Mul(2, Add(s, -.5))));
    if (Mode == 14) return s <= .5 ? min(b, Mul(2, s)) : max(b, Add(Mul(2, s), -1));
    if (Mode == 15) return Vivid(b, s) < .5 ? 0 : 1;
    if (Mode == 16) return abs(Add(b, -s));
    if (Mode == 17) return Add(Add(b, s), -Mul(Mul(2, b), s));
    if (Mode == 18) return saturate(Add(s, -b));
    if (Mode == 19) return s <= 0 ? 1 : saturate(Div(b, s));
    return s;
}
float Lum(float3 c) { return Add(Add(Mul(.3, c.r), Mul(.59, c.g)), Mul(.11, c.b)); }
float Sat(float3 c) { return Add(max(c.r, max(c.g, c.b)), -min(c.r, min(c.g, c.b))); }
float3 SetSat(float3 c, float saturation)
{
    float lo = min(c.r, min(c.g, c.b)), hi = max(c.r, max(c.g, c.b));
    if (hi <= lo) return 0;
    float scale = Div(saturation, Add(hi, -lo));
    float3 result = float3(Mul(Add(c.r, -lo), scale), Mul(Add(c.g, -lo), scale), Mul(Add(c.b, -lo), scale));
    if (hi == c.r) result.r = saturation; else if (hi == c.g) result.g = saturation; else result.b = saturation;
    return result;
}
float3 SetLum(float3 c, float luminance)
{
    float difference = Add(luminance, -Lum(c));
    c = float3(Add(c.r, difference), Add(c.g, difference), Add(c.b, difference));
    float lum = Lum(c), lo = min(c.r, min(c.g, c.b)), hi = max(c.r, max(c.g, c.b));
    [unroll] for (int k = 0; k < 3; k++)
    {
        if (lo < 0) c[k] = Add(lum, Div(Mul(Add(c[k], -lum), lum), Add(lum, -lo)));
        if (hi > 1) c[k] = Add(lum, Div(Mul(Add(c[k], -lum), Add(1, -lum)), Add(hi, -lum)));
    }
    return c;
}
float4 main(float4 scenePosition : SV_Position) : SV_Target
{
    if(scenePosition.x<Region.x || scenePosition.y<Region.y || scenePosition.x>=Region.z || scenePosition.y>=Region.w) return Load1(scenePosition.xy);
    float4 source = round(Load0(scenePosition.xy) * 255), backdrop = round(Load1(scenePosition.xy) * 255);
    if (Replace != 0)
    {
        float4 replaced;
        [unroll] for (int k = 0; k < 4; k++) replaced[k] = Add(Mul(source[k], Opacity), Mul(backdrop[k], Keep));
        return round(replaced) / 255;
    }
    if (source.a == 0 || Opacity <= 0) return backdrop / 255;
    float4 sb = Load2(float2(source.r + .5, source.a + .5));
    float4 bb = Load2(float2(backdrop.r + .5, backdrop.a + .5));
    float sa = Mul(sb.a, Opacity), ba = bb.a;
    float keep = Add(1, -sa);
    float4 result;
    if (Mode == 0)
    {
        [unroll] for (int k = 0; k < 3; k++) result[k] = Add(Mul(source[k], Opacity), Mul(backdrop[k], keep));
        result.a = Add(Mul(source.a, Opacity), Mul(Mul(ba, 255), keep));
    }
    else
    {
        float3 s, b;
        [unroll] for (int k = 0; k < 3; k++)
        {
            s[k] = Load2(float2(source[k] + .5, source.a + .5)).g;
            b[k] = Load2(float2(backdrop[k] + .5, backdrop.a + .5)).r;
        }
        float3 color;
        if (Mode == 20) color = SetLum(SetSat(s, Sat(b)), Lum(b));
        else if (Mode == 21) color = SetLum(SetSat(b, Sat(s)), Lum(b));
        else if (Mode == 22) color = SetLum(s, Lum(b));
        else if (Mode == 23) color = SetLum(b, Lum(s));
        else color = float3(Blend(b.r, s.r), Blend(b.g, s.g), Blend(b.b, s.b));
        float alpha = Add(sa, Mul(ba, keep)), kb = Mul(keep, ba);
        [unroll] for (int channel = 0; channel < 3; channel++)
        {
            float value = Add(Add(Mul(Mul(sa, Add(1, -ba)), s[channel]), Mul(Mul(sa, ba), color[channel])), Mul(kb, b[channel]));
            result[channel] = Mul(Mul(value, alpha), 255);
        }
        result.a = Mul(alpha, 255);
    }
    return clamp(round(result), 0, 255) / 255;
}
