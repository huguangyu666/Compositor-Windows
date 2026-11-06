// Compositor reference pixel math; native hosting: Vortice.Windows (MIT).
#include "ShaderHost.hlsli"
#include "FloatMath.hlsli"
int Kind, Masked, Identity, Seed, Monochromatic, Gaussian;
float Strength, Roughness;
float3 Away(float3 x) { return floor(x) + step(.5, x - floor(x)); }
float V(float x) { return asfloat(asuint(x) ^ uint(ArithmeticBarrier)); }
float4 V4(float4 x) { return asfloat(asuint(x) ^ uint(ArithmeticBarrier)); }
double D(double x) { uint lo, hi; asuint(x, lo, hi); return asdouble(lo ^ uint(ArithmeticBarrier), hi); }
double C(int index)
{
    uint4 words = uint4(Load1(float2(index + .5, .5)));
    return asdouble(words.x | words.y << 16, words.z | words.w << 16);
}
float4 Cube(int3 p) { return Load1(float2(p.x + .5, p.y + p.z * 33 + .5)); }
float4 Lerp4(float4 a, float4 b, float t) { return V4(a + V4(V4(b - a) * t)); }
uint Hash(uint x) { x ^= x >> 16; x *= 0x7feb352d; x ^= x >> 15; x *= 0x846ca68b; return x ^ (x >> 16); }
float Unit(uint x) { return Mul(Hash(x) >> 8, 1.0 / 16777216); }
float Lattice(int x, int y, uint seed)
{
    uint h = Hash(uint(x) * 0x9E3779B1 ^ Hash(uint(y) * 0x85EBCA77 ^ seed));
    return Add(Add(Div(h & 0xffff, 65535), Div(h >> 16, 65535)), -1);
}
float Field(float2 p, double scale, uint seed)
{
    double ux = D(double(p.x) / scale), uy = D(double(p.y) / scale);
    int x = int(ux), y = int(uy);
    float tx = float(D(ux - double(x))), ty = float(D(uy - double(y)));
    tx = Mul(Mul(tx, tx), Add(3, -Mul(2, tx))); ty = Mul(Mul(ty, ty), Add(3, -Mul(2, ty)));
    float a = Lattice(x, y, seed), b = Lattice(x + 1, y, seed), c = Lattice(x, y + 1, seed), d = Lattice(x + 1, y + 1, seed);
    float top = Add(a, Mul(Add(b, -a), tx)), bottom = Add(c, Mul(Add(d, -c), tx));
    return Mul(Add(top, Mul(Add(bottom, -top), ty)), 1.6);
}
float4 main(float4 scenePosition : SV_Position) : SV_Target
{
    float4 original = round(Load0(scenePosition.xy) * 255);
    if (original.a == 0) return original / 255;
    float3 result = original.rgb;
    float3 rgb = float3(Div(Mul(original.r, 255), original.a), Div(Mul(original.g, 255), original.a), Div(Mul(original.b, 255), original.a));
    if (Kind == 4)
    {
        uint a = uint(original.a); uint3 v = uint3(original.rgb);
        if (a < 255) v = min(255, (v * 255 + a / 2) / a);
        uint level = min(255, (2126 * v.r + 7152 * v.g + 722 * v.b + 5000) / 10000);
        uint3 mapped = uint3(Load1(float2(level + .5, .5)).rgb);
        result = (mapped * a + 127) / 255;
    }
    else if (Kind == 0 && Identity == 0)
    {
        float3 p = V4(float4(min(255, rgb), 0) * (32.0 / 255)).rgb;
        int3 lo = min(int3(p), 31); float3 f = p - lo;
        float4 x00 = Lerp4(Cube(lo), Cube(lo + int3(1,0,0)), f.x);
        float4 x10 = Lerp4(Cube(lo + int3(0,1,0)), Cube(lo + int3(1,1,0)), f.x);
        float4 x01 = Lerp4(Cube(lo + int3(0,0,1)), Cube(lo + int3(1,0,1)), f.x);
        float4 x11 = Lerp4(Cube(lo + int3(0,1,1)), Cube(lo + int3(1,1,1)), f.x);
        result = clamp(Away(V4(Lerp4(Lerp4(x00, x10, f.y), Lerp4(x01, x11, f.y), f.z) * original.a).rgb), 0, original.a);
    }
    else if (Kind == 10)
    {
        float3 unit = float3(Div(min(255, rgb.r),255), Div(min(255, rgb.g),255), Div(min(255, rgb.b),255));
        float mx = max(unit.r, max(unit.g, unit.b)), mn = min(unit.r, min(unit.g, unit.b));
        float md = Add(Add(Add(Add(unit.r, unit.g), unit.b), -mx), -mn);
        int primary, secondary;
        if (mx == unit.r) { primary = 0; secondary = unit.g >= unit.b ? 1 : 5; }
        else if (mx == unit.g) { primary = 2; secondary = unit.r >= unit.b ? 1 : 3; }
        else { primary = 4; secondary = unit.g >= unit.r ? 3 : 5; }
        float gray = saturate(Add(Add(mn, Mul(Add(md,-mn), Load1(float2(secondary+.5,.5)).r)), Mul(Add(mx,-md), Load1(float2(primary+.5,.5)).r)));
        float3 value = gray;
        if (Load1(float2(9.5,.5)).r != 0)
        {
            double c = D(D(1.0 - abs(D(2.0 * double(gray)) - 1.0)) * C(6));
            double xx = D(c * C(7)), m = D(double(gray) - D(c / 2.0));
            int sector = int(Load1(float2(8.5,.5)).r);
            double r = 0, g = 0, b = 0;
            if (sector < 1) { r=c; g=xx; } else if (sector < 2) { r=xx; g=c; }
            else if (sector < 3) { g=c; b=xx; } else if (sector < 4) { g=xx; b=c; }
            else if (sector < 5) { r=xx; b=c; } else { r=c; b=xx; }
            value = float3(clamp(D(r+m),0.0,1.0),clamp(D(g+m),0.0,1.0),clamp(D(b+m),0.0,1.0));
        }
        result = clamp(Away(V4(float4(value,0) * original.a).rgb), 0, original.a);
    }
    else if (Kind == 6)
    {
        uint2 p = uint2(scenePosition.xy);
        uint baseHash = Hash(uint(Seed) ^ Hash(p.x * 0x9e3779b9 ^ Hash(p.y * 0x85ebca6b)));
        [unroll] for (int channel=0; channel<3; channel++)
        {
            uint key = Monochromatic != 0 ? baseHash : baseHash + uint(channel) * 0x9e3779b9;
            float noise;
            if (Gaussian != 0)
            {
                float basis = Load4(scenePosition.xy)[Monochromatic!=0 ? 0 : channel];
                noise = Mul(Mul(basis,Strength),2.0/3);
            }
            else noise = Mul(Add(Mul(Unit(key),2),-1),Strength);
            float v = clamp(Add(rgb[channel],noise),0,255);
            result[channel] = Away(Div(Mul(v,original.a),255)).r;
        }
    }
    else if (Kind == 5 && Strength > 0)
    {
        float smooth = Field(scenePosition.xy,C(0),uint(Seed));
        float fine = Field(scenePosition.xy,C(1),Hash(uint(Seed)^0xA511E9B3));
        float noise = Add(smooth,Mul(Add(fine,-smooth),Roughness));
        float unpremultiply = original.a == 255 ? 1 : Div(255,original.a);
        float3 v = V4(float4(original.rgb,0)*unpremultiply).rgb;
        float level = min(1,Div(Add(Add(Mul(.2126,v.r),Mul(.7152,v.g)),Mul(.0722,v.b)),255));
        float delta = Mul(Mul(noise,Strength),Add(.4,Mul(Mul(2.4,level),Add(1,-level))));
        float coverage = Div(original.a,255);
        [unroll] for (int i=0;i<3;i++) result[i] = floor(Add(Mul(clamp(Add(v[i],delta),0,255),coverage),.5));
    }
    if (Masked != 0)
    {
        uint coverage = uint(round(Load2(scenePosition.xy).r * 255));
        result = (uint3(result)*coverage + uint3(original.rgb)*(255-coverage)+127)/255;
    }
    return float4(result,original.a)/255;
}
