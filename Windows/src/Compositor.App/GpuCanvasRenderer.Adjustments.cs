using System.Numerics;
using Compositor.Core;
using static Compositor.App.NativeGpu;

namespace Compositor.App;

// Vortice.Windows (Amer Koleci and contributors, MIT) hosts Compositor's reference filters.
// https://github.com/amerkoleci/Vortice.Windows
internal sealed partial class GpuCanvasRenderer
{
    private GpuTexture? blurTarget;
    private readonly Dictionary<uint, NoisePlate> noisePlates = new();
    private sealed class NoisePlate : CacheEntry
    {
        internal uint Seed;
        internal required GpuTexture Image;
        public override void Dispose() => Image.Dispose();
    }

    private GpuTexture NoiseFor(Grade grade)
    {
        uint seed = grade.Source.NoiseSeed ?? 0;
        if (!noisePlates.TryGetValue(seed, out var plate))
        {
            long bytes = (long)width * height * 16;
            Reserve(bytes, grade.Id);
            try
            {
                if (preparedNoise is null || !preparedNoise.TryGetValue(seed, out var data))
                    throw new NotSupportedException("The noise table was evicted during this frame; use CPU rendering.");
                plate = new() { Id = Guid.NewGuid(), Seed = seed, Bytes = bytes, Image = FloatTexture(data, width, height) };
                noisePlates[seed] = plate;
#if DEBUG
                AssetUploads++;
#endif
            }
            catch { retainedBytes -= bytes; throw; }
        }
        plate.LastUse = ++cacheClock;
        return plate.Image;
    }

    private GpuEffect PrepareGrade(Grade grade, GpuTexture backdrop, ProjectLayerRecord layer)
    {
        if (grade.Source.Kind == AdjustmentKind.AddNoise && grade.Source.NoiseGaussian == true) grade.Effect.Source5 = NoiseFor(grade);
        if (grade.Source.Kind is AdjustmentKind.GaussianBlur or AdjustmentKind.MotionBlur)
        {
            var region = GradeBounds(layer);
            grade.Effect.Properties["Bounds"] = region;
            if (grade.FirstPass is not null) grade.FirstPass.Properties["Bounds"] = region;
        }
        if (grade.FirstPass is not null)
        {
            if (blurTarget is null) { Reserve((long)width * height * 4, grade.Id); blurTarget = CreateTarget(); }
            grade.FirstPass.Source1 = backdrop;
            DrawPass(blurTarget, grade.FirstPass);
            grade.Effect.Source1 = blurTarget;
        }
        else grade.Effect.Source1 = backdrop;
        if (grade.Source.Kind is AdjustmentKind.GaussianBlur or AdjustmentKind.MotionBlur) grade.Effect.Source4 = backdrop;
        return grade.Effect;
    }

    private Vector4 GradeBounds(ProjectLayerRecord layer)
    {
        var (x0, y0, x1, y1) = Resampler.BoundingRect(layer.Transform, width, height);
        if (x0 >= x1 || y0 >= y1) return Vector4.Zero;
        int margin = (int)Math.Ceiling(AdjustmentEngine.SamplingMargin(layer.Adjustment!));
        return new(Math.Max(0, x0 - margin), Math.Max(0, y0 - margin), Math.Min(width, x1 + margin), Math.Min(height, y1 + margin));
    }

    private Grade AdvancedGrade(Guid id, LayerAdjustment adjustment)
    {
        float[] data;
        int w = 256, h = 1;
        string shader = "AdvancedAdjustment";
        switch (adjustment.Kind)
        {
            case AdjustmentKind.HueSaturation:
                data = AdjustmentEngine.BuildCube(adjustment.ResolvedHsv); w = 33; h = 33 * 33; break;
            case AdjustmentKind.GradientMap:
                data = new float[w * 4];
                var gradient = adjustment.GradientMapSettings ?? new();
                var dark = gradient.Reversed ? gradient.Highlights : gradient.Shadows;
                var light = gradient.Reversed ? gradient.Shadows : gradient.Highlights;
                for (int i = 0; i < 256; i++)
                {
                    double t = i / 255.0;
                    data[i * 4] = Channel(dark.Red, light.Red, t);
                    data[i * 4 + 1] = Channel(dark.Green, light.Green, t);
                    data[i * 4 + 2] = Channel(dark.Blue, light.Blue, t);
                }
                break;
            case AdjustmentKind.BlackWhite:
                data = new float[w * 4];
                var bw = adjustment.BlackWhiteSettings ?? new();
                double[] weights = [bw.Reds, bw.Yellows, bw.Greens, bw.Cyans, bw.Blues, bw.Magentas];
                for (int i = 0; i < weights.Length; i++) data[i * 4] = (float)(weights[i] / 100);
                // Tint depends only on the rounded float gray. Keep the exact double constants.
                WriteDouble(data, 6 * 4, bw.TintSaturation / 100.0);
                double hp = bw.TintHue % 360 / 60;
                WriteDouble(data, 7 * 4, 1 - Math.Abs(hp % 2 - 1));
                data[8 * 4] = (float)Math.Floor(hp); data[9 * 4] = bw.Tint && bw.TintSaturation > 0 ? 1 : 0;
                break;
            case AdjustmentKind.GaussianBlur:
                double sigma = Math.Max(.1, adjustment.GaussianRadius);
                int radius = Math.Max(1, (int)Math.Ceiling(sigma * 3));
                w = radius * 2 + 1; data = new float[w * 4]; shader = "SpatialAdjustment";
                double sum = 0;
                for (int i = 0; i < w; i++) { double x = i - radius; data[i * 4] = (float)Math.Exp(-x * x / (2 * sigma * sigma)); sum += data[i * 4]; }
                for (int i = 0; i < w; i++) data[i * 4] /= (float)sum;
                break;
            case AdjustmentKind.MotionBlur:
                double half = adjustment.ResolvedMotionDistance / Math.Sqrt(12);
                w = Math.Max(2, Math.Min(128, (int)Math.Ceiling(half * 2))); h = 3;
                data = new float[w * h * 4]; shader = "SpatialAdjustment";
                double dx = Math.Cos(adjustment.ResolvedMotionAngle * Math.PI / 180);
                double dy = -Math.Sin(adjustment.ResolvedMotionAngle * Math.PI / 180);
                double total = 0;
                for (int i = 0; i < w; i++)
                {
                    double offset = (i / (double)(w - 1) - .5) * half * 2;
                    data[i * 4] = (float)Math.Exp(-offset * offset / (2 * half * half)); total += data[i * 4];
                    WriteDouble(data, (w + i) * 4, dx * offset);
                    WriteDouble(data, (w * 2 + i) * 4, dy * offset);
                }
                for (int i = 0; i < w; i++) data[i * 4] /= (float)total;
                break;
            default: data = new float[w * 4]; break;
        }
        long bytes = (long)data.Length * 4; Reserve(bytes);
        GpuTexture? table = null; GpuEffect? effect = null, first = null;
        try
        {
            table = FloatTexture(data, w, h); effect = NewEffect(shader); effect.Source2 = table;
            effect.Properties["Kind"] = (int)adjustment.Kind;
            if (shader == "SpatialAdjustment")
            {
                effect.Properties["TapCount"] = w;
                effect.Properties["Axis"] = 1;
                if (adjustment.Kind == AdjustmentKind.GaussianBlur)
                {
                    first = NewEffect(shader); first.Source2 = table; first.Source3 = white;
                    first.Properties["Kind"] = (int)adjustment.Kind; first.Properties["TapCount"] = w;
                }
            }
            else if (adjustment.Kind == AdjustmentKind.HueSaturation)
                effect.Properties["Identity"] = !adjustment.ResolvedHsv.Colorize && adjustment.ResolvedHsv.Adjustments.Values.All(v => v.Hue == 0 && v.Saturation == 0 && v.Lightness == 0) ? 1 : 0;
            else if (adjustment.Kind == AdjustmentKind.AddNoise)
            {
                effect.Properties["Seed"] = unchecked((int)(adjustment.NoiseSeed ?? 0));
                effect.Properties["Monochromatic"] = adjustment.NoiseMonochromatic == true ? 1 : 0;
                effect.Properties["Gaussian"] = adjustment.NoiseGaussian == true ? 1 : 0;
                effect.Properties["Strength"] = (float)adjustment.ResolvedNoiseAmount / 100f * 127.5f;
            }
            else if (adjustment.Kind == AdjustmentKind.Grain)
            {
                var grain = adjustment.GrainSettings ?? new();
                WriteDouble(data, 0, grain.Size > 0 ? grain.Size : 1);
                WriteDouble(data, 4, Math.Max(.5, (grain.Size > 0 ? grain.Size : 1) * .35));
                table.SetPixelBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(data.AsSpan()));
                effect.Properties["Seed"] = unchecked((int)grain.Seed);
                effect.Properties["Strength"] = (float)(grain.Amount > 100 ? 1 : grain.Amount / 100) * .35f * 255;
                effect.Properties["Roughness"] = (float)Math.Clamp(grain.Roughness / 100, 0, 1);
            }
#if DEBUG
            AssetUploads++;
#endif
            return grades[id] = new() { Id = id, Bytes = bytes, LastUse = ++cacheClock, Source = adjustment, Revision = adjustment.Revision, Table = table, Effect = effect, FirstPass = first, TableBytes = bytes };
        }
        catch { first?.Dispose(); effect?.Dispose(); table?.Dispose(); retainedBytes -= bytes; throw; }

        static float Channel(double from, double to, double t) => (float)Math.Clamp(Math.Round((from + (to - from) * t) * 255, MidpointRounding.AwayFromZero), 0, 255);
    }
}
