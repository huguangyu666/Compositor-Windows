// D3D11 hosting through Vortice.Windows (Amer Koleci and contributors, MIT).
// The shaders retain the existing pixel coordinates and quantization boundaries.
#define INPUT(N) \
Texture2D<float4> Input##N : register(t##N); \
float4 Load##N(float2 p) { return Input##N.Load(int3(int2(floor(p)), 0)); }
INPUT(0) INPUT(1) INPUT(2) INPUT(3) INPUT(4) INPUT(5) INPUT(6) INPUT(7)
