// Exact inverse mapping and CPU Catmull-Rom weights through Microsoft's MIT-licensed Win2D.
#define D2D_INPUT_COUNT 4
#define D2D_INPUT0_COMPLEX
#define D2D_INPUT1_COMPLEX
#define D2D_INPUT2_COMPLEX
#define D2D_INPUT3_SIMPLE
#define D2D_REQUIRES_SCENE_POSITION
#include "ShaderHost.hlsli"
#include "FloatMath.hlsli"
int Sampling;
float2 SourceSize;
float4 Bounds;

double Constant(int index)
{
    float4 words = Load1(float2(index + .5, .5));
    return asdouble(uint(words.x) | (uint(words.y) << 16), uint(words.z) | (uint(words.w) << 16));
}
double D(double value)
{
    uint lo, hi; asuint(value, lo, hi);
    return asdouble(lo ^ uint(ArithmeticBarrier), hi);
}
float4 V(float4 v) { return asfloat(asuint(v) ^ uint(ArithmeticBarrier)); }
float4 Pixel(int x, int y)
{
    return round(Load0(float2(clamp(x, 0, int(SourceSize.x) - 1) + .5,
        clamp(y, 0, int(SourceSize.y) - 1) + .5)) * 255);
}
float Weight(float t)
{
    float a = abs(t);
    return a >= 2 ? 0 : Load2(float2(min(511, int(Mul(a, 256))) + .5, .5)).r;
}
float4 CubicRow(int x, int y, float4 w)
{
    return V(V(V(V(w.x * Pixel(x - 1, y)) + V(w.y * Pixel(x, y))) + V(w.z * Pixel(x + 1, y))) + V(w.w * Pixel(x + 2, y)));
}
float4 main(float4 scenePosition : SV_Position) : SV_Target
{
    float2 p = scenePosition.xy;
    float4 backdrop = round(Load3(scenePosition.xy) * 255);
    if (p.x < Bounds.x || p.y < Bounds.y || p.x >= Bounds.z || p.y >= Bounds.w) return backdrop / 255;
    double dx = D(double(p.x) - Constant(0)), dy = D(double(p.y) - Constant(1));
    double cos = Constant(2), sin = Constant(3);
    double bx = D(D(dx * cos) + D(dy * sin)), by = D(D(-dx * sin) + D(dy * cos));
    double u = D(D(bx * Constant(4)) + .5), v = D(D(by * Constant(5)) + .5);
    double sx = D(D(u * Constant(6)) + Constant(8)), sy = D(D(v * Constant(7)) + Constant(9));
    float4 source = 0;
    if (Sampling == 0)
    {
        if (sx < 0 || sy < 0 || sx >= SourceSize.x || sy >= SourceSize.y) return backdrop / 255;
        source = Pixel(int(sx), int(sy));
    }
    else
    {
        if (sx < -.5 || sy < -.5 || sx > double(SourceSize.x) - .5 || sy > double(SourceSize.y) - .5) return backdrop / 255;
        double ux = D(sx - .5), uy = D(sy - .5);
        int x = int(ux), y = int(uy);
        if (double(x) > ux) x--; if (double(y) > uy) y--;
        float tx = float(D(ux - double(x))), ty = float(D(uy - double(y)));
        if (Sampling == 1)
        {
            float4 a = Pixel(x, y), b = Pixel(x + 1, y), c = Pixel(x, y + 1), d = Pixel(x + 1, y + 1);
            float4 top = V(a + V(V(b - a) * tx)), bottom = V(c + V(V(d - c) * tx));
            source = V(top + V(V(bottom - top) * ty));
        }
        else
        {
            float4 wx = float4(Weight(-1 - tx), Weight(-tx), Weight(1 - tx), Weight(2 - tx));
            float4 wy = float4(Weight(-1 - ty), Weight(-ty), Weight(1 - ty), Weight(2 - ty));
            source = V(V(V(V(wy.x * CubicRow(x, y - 1, wx)) + V(wy.y * CubicRow(x, y, wx)))
                + V(wy.z * CubicRow(x, y + 1, wx))) + V(wy.w * CubicRow(x, y + 2, wx)));
        }
        source = clamp(round(source), 0, 255);
    }
    if (source.a == 0) return backdrop / 255;
    float keep = Add(1, -Div(source.a, 255));
    return clamp(round(V(source + V(backdrop * keep))), 0, 255) / 255;
}
