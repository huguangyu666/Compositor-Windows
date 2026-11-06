// Compositor's exact tap order, hosted by Microsoft's MIT-licensed Win2D.
// API reference: https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Effects_PixelShaderEffect.htm
#define D2D_INPUT_COUNT 4
#define D2D_INPUT0_COMPLEX
#define D2D_INPUT1_COMPLEX
#define D2D_INPUT2_COMPLEX
#define D2D_INPUT3_SIMPLE
#define D2D_REQUIRES_SCENE_POSITION
#include "ShaderHost.hlsli"

int TapCount;

float4 main(float4 scenePosition : SV_Position) : SV_Target
{
    float2 position = scenePosition.xy;
    float4 ix = Load1(float2(position.x, .5));
    float4 iy = Load2(float2(position.y, .5));
    precise float4 backdrop = round(Load3(scenePosition.xy) * 255.0);
    if (ix.x < 0 || iy.x < 0) return backdrop / 255.0;
    float4 wx = Load1(float2(position.x, 1.5));
    float4 wy = Load2(float2(position.y, 1.5));
    precise float4 vertical = 0;
    [unroll] for (int j = 0; j < 4; j++)
    {
        if (j >= TapCount) break;
        precise float4 horizontal = 0;
        [unroll] for (int k = 0; k < 4; k++)
        {
            if (k >= TapCount) break;
            float4 pixel = round(Load0(float2(ix[k] + .5, iy[j] + .5)) * 255.0);
            horizontal = horizontal + wx[k] * pixel;
        }
        vertical = vertical + wy[j] * horizontal;
    }
    precise float4 source = clamp(round(vertical), 0.0, 255.0);
    if (source.a == 0) return backdrop / 255.0;
    precise float keep = 1.0 - source.a / 255.0;
    precise float4 result = source + backdrop * keep;
    return clamp(round(result), 0.0, 255.0) / 255.0;
}
