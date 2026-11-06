// Exact two-pass RGBA8 Gaussian and tapered motion blur from AdjustmentEngine.
#include "ShaderHost.hlsli"
#include "FloatMath.hlsli"
int Kind, TapCount, Axis, Masked;
float4 Bounds;
bool Inside(float2 p) { return p.x>=Bounds.x && p.y>=Bounds.y && p.x<Bounds.z && p.y<Bounds.w; }
float4 V4(float4 v) { return asfloat(asuint(v) ^ uint(ArithmeticBarrier)); }
float4 Away(float4 x) { return floor(x) + step(.5, x-floor(x)); }
double C(int tap,int row)
{
    uint4 w=uint4(Load1(float2(tap+.5,row+.5)));
    return asdouble(w.x | w.y<<16,w.z | w.w<<16);
}
float4 main(float4 scenePosition : SV_Position) : SV_Target
{
    float2 p=scenePosition.xy;
    if(!Inside(p)) return 0;
    float4 sum=0;
    [loop] for(int i=0;i<TapCount;i++)
    {
        float2 q=p;
        if(Kind==7) q += Axis==0 ? float2(i-int(uint(TapCount)>>1),0) : float2(0,i-int(uint(TapCount)>>1));
        else q = float2(round(float(double(p.x-.5)+C(i,1)))+.5,round(float(double(p.y-.5)+C(i,2)))+.5);
        float weight=Load1(float2(i+.5,.5)).r;
        uint w,h; Input0.GetDimensions(w,h); q=clamp(q,.5,float2(w-.5,h-.5));
        float4 pixel=Inside(q) ? round(Load0(q)*255) : 0;
        sum=V4(sum+V4(pixel*weight));
    }
    float4 result=clamp(Away(sum),0,255);
    if(Masked!=0)
    {
        uint coverage=uint(round(Load2(p).r*255));
        uint4 original=uint4(round(Load3(p)*255));
        result=(uint4(result)*coverage+original*(255-coverage)+127)/255;
    }
    return result/255;
}
