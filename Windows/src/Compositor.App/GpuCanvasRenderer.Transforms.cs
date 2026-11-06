using System.Numerics;
using Compositor.Core;
using Microsoft.Graphics.Canvas;
using static Compositor.App.NativeGpu;

namespace Compositor.App;

// Win2D (MIT) hosts the inverse sampler. CPU doubles are passed as integer words so
// transforming a photo does not downcast its geometry before pixel lookup.
internal sealed partial class GpuCanvasRenderer
{
    private GpuTexture? cubicWeights;

    private GpuEffect PrepareRaster(Raster raster, LayerTransform transform)
    {
        raster.Effect.Source1 = raster.SampleImage;
        bool axis = Math.Abs(Math.Sin(transform.Radians)) < 1e-9;
        if (!axis && raster.RotatedEffect is null)
        {
            if (cubicWeights is null)
            {
                Reserve(512 * 16, raster.Id);
                var table = new float[512 * 4];
                for (int i = 0; i < 512; i++) table[i * 4] = Resampler.CubicWeight(i / 256f);
                cubicWeights = FloatTexture(table, 512, 1);
            }
            Reserve(10 * 16, raster.Id);
            GpuTexture? geometry = null; GpuEffect? rotated = null;
            try
            {
                geometry = FloatTexture(new float[40], 10, 1); rotated = NewEffect("LayerRotate");
                rotated.Source1 = raster.SampleImage; rotated.Source2 = geometry; rotated.Source3 = cubicWeights;
                raster.Geometry = geometry; raster.RotatedEffect = rotated;
                raster.Bytes += 160;
            }
            catch { rotated?.Dispose(); geometry?.Dispose(); retainedBytes -= 10 * 16; throw; }
        }
        if (raster.Placement?.Equals(transform) != true)
        {
            if (axis) SetSampling(raster, transform);
            else
            {
                var center = transform.Center;
                double sw = raster.ContentWidth, sh = raster.ContentHeight;
                double[] parameters = [center.X, center.Y, Math.Cos(transform.Radians), Math.Sin(transform.Radians),
                    1 / transform.Size.Width, 1 / transform.Size.Height, transform.FlipX ? -sw : sw,
                    transform.FlipY ? -sh : sh, transform.FlipX ? sw : 0, transform.FlipY ? sh : 0];
                var words = new float[parameters.Length * 4];
                for (int i = 0; i < parameters.Length; i++) WriteDouble(words, i * 4, parameters[i]);
                raster.Geometry!.SetPixelBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(words.AsSpan()));
                var bounds = Resampler.BoundingRect(transform, width, height);
                raster.RotatedEffect!.Properties["Sampling"] = (int)transform.Sampling;
                raster.RotatedEffect.Properties["SourceSize"] = new Vector2(raster.ContentWidth, raster.ContentHeight);
                raster.RotatedEffect.Properties["Bounds"] = new Vector4(bounds.X0, bounds.Y0, bounds.X1, bounds.Y1);
            }
            raster.Placement = TransformEdit.Copy(transform);
        }
        if (!axis) raster.RotatedEffect!.Source1 = raster.SampleImage;
        return axis ? raster.Effect : raster.RotatedEffect!;
    }

    private static void WriteDouble(float[] words, int offset, double value)
    {
        ulong bits = BitConverter.DoubleToUInt64Bits(value);
        for (int i = 0; i < 4; i++) words[offset + i] = (bits >> (i * 16)) & 0xffff;
    }
}
