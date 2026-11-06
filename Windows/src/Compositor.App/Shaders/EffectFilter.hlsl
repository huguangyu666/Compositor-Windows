// EffectGaussian exact kernels/float boxes and LayerEffectsRenderer square morphology.
#include "ShaderHost.hlsli"
#include "DoubleMath.hlsli"
int Mode, Radius, Axis, Smallest, Wide, InputFloat, Quantize;
float2 Shift;
double C(int index,int row) { return Unpack(Load1(float2(index+.5,row+.5))); }
float SampleValue(int2 p,int2 size)
{
    p=clamp(p,0,size-1);
    float v=Load0(float2(p)+.5).r;
    return Mode==3 && InputFloat!=0 ? v : round(v*255);
}
float4 main(float4 scenePosition:SV_Position):SV_Target
{
    int2 p=int2(scenePosition.xy); uint w,h; Input0.GetDimensions(w,h); int2 size=int2(w,h);
    if(Mode==0)
    { int2 q=p-int2(Shift); return q.x<0 || q.y<0 || q.x>=size.x || q.y>=size.y ? 0 : float4(Load0(float2(q)+.5).r,0,0,0); }
    int center=Axis==0 ? p.x : p.y, count=Axis==0 ? size.x : size.y;
    if(Mode==1)
    {
        if(Smallest!=0 && (center<Radius || center+Radius>=count)) return 0;
        float extreme=Smallest!=0 ? 255 : 0;
        [loop] for(int k=max(0,center-Radius);k<=min(count-1,center+Radius);k++)
        {
            int2 q=Axis==0 ? int2(k,p.y) : int2(p.x,k);
            float v=SampleValue(q,size); extreme=Smallest!=0 ? min(extreme,v) : max(extreme,v);
        }
        return float4(extreme/255,0,0,0);
    }
    double sum=0;
    int first=Mode==2 && Wide==0 ? -Radius : max(-Radius,-center);
    int last=Mode==2 && Wide==0 ? Radius : min(Radius,count-1-center);
    [loop] for(int i=first;i<=last;i++)
    {
        int2 q=p+(Axis==0 ? int2(i,0) : int2(0,i));
        double v=double(SampleValue(q,size));
        sum=D(sum+(Mode==2 ? D(v*C(i+Radius,0)) : v));
    }
    if(Mode==2 && Wide!=0)
    {
        if(center<Radius)
        { int2 q=Axis==0 ? int2(0,p.y) : int2(p.x,0); sum=D(sum+D(double(SampleValue(q,size))*C(Radius-center,1))); }
        if(center+Radius>=count)
        { int2 q=Axis==0 ? int2(count-1,p.y) : int2(p.x,count-1); sum=D(sum+D(double(SampleValue(q,size))*D(C(Radius*2+1,1)-C(Radius+count-center,1)))); }
    }
    else if(Mode==3)
    {
        if(center<Radius)
        { int2 q=Axis==0 ? int2(0,p.y) : int2(p.x,0); sum=D(sum+D(double(Radius-center)*double(SampleValue(q,size)))); }
        if(center+Radius>=count)
        { int2 q=Axis==0 ? int2(count-1,p.y) : int2(p.x,count-1); sum=D(sum+D(double(center+Radius-count+1)*double(SampleValue(q,size)))); }
        float value=float(D(sum*C(0,0)));
        return float4(Quantize!=0 ? ByteAway(double(value))/255 : value,0,0,0);
    }
    return float4(ByteAway(sum)/255,0,0,0);
}
