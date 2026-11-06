// Reference integer mask multiplication/interpolation through Microsoft's MIT-licensed Win2D.
#define D2D_INPUT_COUNT 5
#define D2D_INPUT0_SIMPLE
#define D2D_INPUT1_SIMPLE
#define D2D_INPUT2_COMPLEX
#define D2D_INPUT3_COMPLEX
#define D2D_INPUT4_COMPLEX
#define D2D_REQUIRES_SCENE_POSITION
#include "ShaderHost.hlsli"
#include "FloatMath.hlsli"
int Interpolate;
int Outside;
int Rotated, Live;
float Opacity;
double D(double v) { uint lo,hi; asuint(v,lo,hi); return asdouble(lo ^ uint(ArithmeticBarrier),hi); }
double C(int index)
{
    uint4 w=uint4(Load5(float2(index+.5,.5)));
    return asdouble(w.x | w.y<<16,w.z | w.w<<16);
}
float4 main(float4 scenePosition : SV_Position) : SV_Target
{
    float2 p = scenePosition.xy;
    float x = Load3(float2(p.x, .5)).r;
    float y = Load4(float2(p.y, .5)).r;
    bool outside=x<0 || y<0;
    if(Rotated!=0)
    {
        double dx=D(double(p.x)-C(0)),dy=D(double(p.y)-C(1)),co=C(2),si=C(3);
        double ux=D(D(D(D(dx*co)+D(dy*si))/C(4))+.5),uy=D(D(D(D(-dx*si)+D(dy*co))/C(5))+.5);
        if(C(6)!=0) ux=D(1-ux); if(C(7)!=0) uy=D(1-uy);
        uint w,h; Input2.GetDimensions(w,h);
        double sx=D(ux*double(w)),sy=D(uy*double(h));
        outside=sx<0 || sy<0 || sx>=double(w) || sy>=double(h);
        x=float(int(sx)); y=float(int(sy));
    }
    float4 pixel=Load2(float2(x+.5,y+.5));
    uint mask = outside ? uint(Outside) : uint(round((Live!=0 ? pixel.a : pixel.r)*255));
    uint4 source = (uint4)round(Load0(scenePosition.xy) * 255);
    if(Live!=0)
    {
        float scale=Mul(Div(mask,255),Opacity);
        if(scale>=1) return source/255.0;
        float4 backdrop=Interpolate!=0 ? round(Load1(scenePosition.xy)*255) : 0;
        float4 result;
        [unroll] for(int c=0;c<4;c++) result[c]=Add(Mul(source[c],scale),Mul(backdrop[c],Add(1,-scale)));
        return round(result)/255;
    }
    if (Interpolate != 0)
    {
        uint4 backdrop = (uint4)round(Load1(scenePosition.xy) * 255);
        return ((source * mask + backdrop * (255 - mask) + 127) / 255) / 255.0;
    }
    return (source * mask / 255) / 255.0;
}
