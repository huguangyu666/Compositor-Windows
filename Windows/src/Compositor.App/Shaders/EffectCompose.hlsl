// Byte rules and effect order are shared with LayerEffectsRenderer.
#include "ShaderHost.hlsli"
#include "DoubleMath.hlsli"
int Mode, Inset;
float4 Color;
double Opacity;
float4 main(float4 scenePosition:SV_Position):SV_Target
{
    float2 p=scenePosition.xy;
    uint4 previous=uint4(round(Load0(p)*255));
    uint coverage=uint(round(Load1(p).r*255));
    uint2 shape=uint2(round(Load2(p).rg*255));
    if(Mode==6)
    {
        int2 q=int2(p)-Inset;
        uint w,h; Input3.GetDimensions(w,h);
        if(q.x<0 || q.y<0 || q.x>=int(w) || q.y>=int(h) || shape.r==0) return previous/255.0;
        uint4 source=uint4(round(Load3(float2(q)+.5)*255));
        return ((source*shape.g+previous*(255-shape.r)+127)/255 & 255)/255.0;
    }
    if(Mode==2) coverage=(coverage*(255-shape.r)+127)/255;
    else if(Mode==3) coverage=coverage>shape.r ? coverage-shape.r : 0;
    else if(Mode==4) coverage=(shape.r*(255-coverage)+127)/255;
    else if(Mode==5) coverage=shape.r>coverage ? shape.r-coverage : 0;
    float4 result=0;
    if(Mode==0)
    {
        double value=D(double(coverage)*Opacity);
        [unroll] for(int i=0;i<3;i++) result[i]=ByteAway(D(D(double(Color[i])*value)/255.0));
        result.a=ByteAway(value);
    }
    else
    {
        double a=D(D(double(coverage)/255.0)*Opacity),keep=D(1-a);
        [unroll] for(int c=0;c<3;c++) result[c]=ByteAway(D(D(double(Color[c])*a)+D(double(previous[c])*keep)));
        double alpha=D(double(previous.a)+D(D(255.0*a)*D(1-D(double(previous.a)/255.0))));
        result.a=ByteAway(min(255.0,alpha));
    }
    return result/255;
}
