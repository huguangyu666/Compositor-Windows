// Keep the CPU double multiply/add boundaries through the native D3D11 host.
int ArithmeticBarrier;
double D(double value) { uint lo,hi; asuint(value,lo,hi); return asdouble(lo ^ uint(ArithmeticBarrier),hi); }
double Unpack(float4 packed)
{
    uint4 w=uint4(packed); return asdouble(w.x | w.y<<16,w.z | w.w<<16);
}
float ByteAway(double value)
{
    value=clamp(value,0.0,255.0);
    uint whole=uint(value); return float(whole+(D(value-double(whole))>=.5 ? 1 : 0));
}
