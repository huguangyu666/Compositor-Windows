// LayerEffectsRenderer source-grid mask and shape, hosted through Vortice.Windows (MIT).
#include "ShaderHost.hlsli"
#include "DoubleMath.hlsli"
int Inset, MaskKind, Outside;
double C(int i) { return Unpack(Load2(float2(i+.5,.5))); }
float4 main(float4 scenePosition:SV_Position):SV_Target
{
    int2 p=int2(scenePosition.xy)-Inset;
    uint w,h; Input0.GetDimensions(w,h);
    if(p.x<0 || p.y<0 || p.x>=int(w) || p.y>=int(h)) return 0;
    uint mask=255;
    if(MaskKind!=0)
    {
        uint mw,mh; Input1.GetDimensions(mw,mh);
        double mx,my;
        if(MaskKind==1)
        { mx=D(D((double(p.x)+.5)*double(mw))/double(w)); my=D(D((double(p.y)+.5)*double(mh))/double(h)); }
        else
        {
            double u=D((double(p.x)+.5)/double(w)),v=D((double(p.y)+.5)/double(h));
            if(C(6)!=0) u=D(1-u); if(C(7)!=0) v=D(1-v);
            double x=D(D(u-.5)*C(4)),y=D(D(v-.5)*C(5));
            double dx=D(D(D(C(0)+D(x*C(2)))-D(y*C(3)))-C(8));
            double dy=D(D(D(C(1)+D(x*C(3)))+D(y*C(2)))-C(9));
            double ux=D(D(D(D(dx*C(10))+D(dy*C(11)))/C(12))+.5);
            double uy=D(D(D(D(-dx*C(11))+D(dy*C(10)))/C(13))+.5);
            if(C(14)!=0) ux=D(1-ux); if(C(15)!=0) uy=D(1-uy);
            mx=D(ux*double(mw)); my=D(uy*double(mh));
        }
        mask=mx<0 || my<0 || mx>=double(mw) || my>=double(mh) ? uint(Outside) : uint(round(Load1(float2(int(mx)+.5,int(my)+.5)).r*255));
    }
    uint alpha=uint(round(Load0(float2(p)+.5).a*255));
    return float4(alpha*mask/255,mask,0,0)/255;
}
