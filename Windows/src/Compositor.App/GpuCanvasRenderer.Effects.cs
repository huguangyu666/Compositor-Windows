using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Compositor.Core;
using Vortice.DXGI;
using static Compositor.App.NativeGpu;

namespace Compositor.App;

// Vortice.Windows (Amer Koleci and contributors, MIT) hosts the GPU passes.
// https://github.com/amerkoleci/Vortice.Windows
// Order, coverage and byte rounding follow LayerEffectsRenderer and EffectGaussian.
internal sealed partial class GpuCanvasRenderer
{
    private readonly Dictionary<Guid, EffectsPlate> effectsPlates = new();
    private sealed class EffectsPlate : CacheEntry
    {
        internal required Surface Source;
        internal int Revision, MaskRevision, Inset;
        internal MaskSurface? Mask;
        internal required string Raw;
        internal JsonElement Element;
        internal LayerTransform? LayerPlacement, MaskPlacement;
        internal required GpuTexture Image;
        public override void Dispose() => Image.Dispose();
    }

    private (Raster Raster, LayerTransform Placement) RasterForLayer(GpuPreviewScene scene, ProjectLayerRecord layer)
    {
        var raster = RasterFor(layer.Id, scene.Snapshot.Images[layer.Id]);
        var image = raster.Image; var placement = layer.Transform;
        if (layer.Effects is { } element)
        {
            effectsPlates.TryGetValue(layer.Id, out var cached);
            string raw = cached is not null && cached.Element.Equals(element) ? cached.Raw : element.GetRawText();
            var mask = layer.MaskFile is not null && layer.MaskEnabled != false ? scene.Snapshot.Masks.GetValueOrDefault(layer.Id) : null;
            bool independent = mask is not null && layer.MaskPlacement is { } p && !SameGeometry(p, layer.Transform);
            bool valid = cached is not null && cached.Raw == raw && ReferenceEquals(cached.Source, raster.Source)
                && cached.Revision == raster.Source.Revision && ReferenceEquals(cached.Mask, mask) && cached.MaskRevision == (mask?.Revision ?? 0)
                && (independent ? cached.LayerPlacement?.Equals(layer.Transform) == true && cached.MaskPlacement?.Equals(layer.MaskPlacement) == true : cached.LayerPlacement is null);
            if (!valid)
            {
                if (cached is not null) Retire(cached);
                cached = null;
                if (LayerEffectsRenderer.Visible(element) is { } effects)
                {
                    int inset = LayerEffectsRenderer.Margin(effects);
                    long w = raster.Source.Width + (long)inset * 2, h = raster.Source.Height + (long)inset * 2;
                    if (w <= Limits.MaxSide && h <= Limits.MaxSide && w * h <= Limits.MaxSurfacePixels)
                    {
                        var rendered = BuildEffects(raster, layer, mask, effects, inset, independent);
                        cached = new() { Id = layer.Id, Bytes = w * h * 4, LastUse = ++cacheClock,
                            Source = raster.Source, Revision = raster.Source.Revision, Mask = mask, MaskRevision = mask?.Revision ?? 0,
                            Raw = raw, Element = element, Inset = inset, Image = rendered,
                            LayerPlacement = independent ? TransformEdit.Copy(layer.Transform) : null,
                            MaskPlacement = independent ? TransformEdit.Copy(layer.MaskPlacement!) : null };
                        effectsPlates[layer.Id] = cached;
                    }
                }
            }
            if (cached is not null)
            {
                cached.LastUse = ++cacheClock; image = cached.Image;
                var center = placement.Center;
                double w = placement.Size.Width * image.Width / raster.Source.Width, h = placement.Size.Height * image.Height / raster.Source.Height;
                placement = new() { Origin = new(center.X - w / 2, center.Y - h / 2), Size = new(w, h), Rotation = placement.Rotation,
                    FlipX = placement.FlipX, FlipY = placement.FlipY, Sampling = placement.Sampling };
            }
        }
        if (!ReferenceEquals(raster.SampleImage, image)) { raster.SampleImage = image; raster.Placement = null; }
        return (raster, placement);
    }

    private static bool SameGeometry(LayerTransform a, LayerTransform b) => a.Origin == b.Origin && a.Size == b.Size
        && a.Rotation == b.Rotation && a.FlipX == b.FlipX && a.FlipY == b.FlipY;

    private GpuTexture BuildEffects(Raster raster, ProjectLayerRecord layer, MaskSurface? mask, LayerEffects effects, int inset, bool independent)
    {
        int w = raster.Source.Width + inset * 2, h = raster.Source.Height + inset * 2;
        long plane = (long)w * h * 4;
        // Only the finished image survives. Work planes and small double kernels are retired together.
        bool filters = effects.Shadow is { Opacity: > 0 } || effects.InnerShadow is { Opacity: > 0 }
            || effects.Stroke is { Opacity: > 0, Size: > 0 } || effects.OuterGlow is { Opacity: > 0, Size: > 0 } || effects.InnerGlow is { Opacity: > 0, Size: > 0 };
        bool floats = effects.Shadow is { Opacity: > 0 } sh && EffectGaussian.UsesFloatWorkspace(sh.Blur, w, h)
            || effects.InnerShadow is { Opacity: > 0 } ish && EffectGaussian.UsesFloatWorkspace(ish.Blur, w, h)
            || effects.OuterGlow is { Opacity: > 0 } og && EffectGaussian.UsesFloatWorkspace(og.Size, w, h)
            || effects.InnerGlow is { Opacity: > 0 } ig && EffectGaussian.UsesFloatWorkspace(ig.Size, w, h);
        long bytes = plane * (3 + (filters ? 2 : 0) + (floats ? 2 : 0)) + (mask?.Pixels.LongLength ?? 0) + (128 << 10);
        Reserve(bytes, layer.Id);
        var resources = new List<IDisposable>(); GpuTexture? result = null;
        try
        {
            GpuTexture Target(Format format = Format.B8G8R8A8_UNorm)
            { var t = gpu.Target(w, h, format); resources.Add(t); return t; }
            var shape = Target(); GpuTexture[] output = [Target(), Target()];
            GpuTexture[] coveragePlanes = filters ? [Target(), Target()] : [];
            GpuTexture[] floatPlanes = floats ? [Target(Format.R32_Float), Target(Format.R32_Float)] : [];
            var maskImage = mask is null ? white : gpu.Texture(mask.Pixels, mask.Width, mask.Height, Format.R8_UNorm);
            if (mask is not null) resources.Add(maskImage);
            var constants = new float[64];
            FillGeometry(constants, 0, layer.Transform); FillGeometry(constants, 32, layer.MaskPlacement ?? layer.Transform);
            var geometry = FloatTexture(constants, 16, 1); resources.Add(geometry);
            using var shapeEffect = NewEffect("EffectShape");
            shapeEffect.Source1 = raster.Image; shapeEffect.Source2 = maskImage; shapeEffect.Source3 = geometry;
            shapeEffect.Properties["Inset"] = inset; shapeEffect.Properties["MaskKind"] = mask is null ? 0 : independent ? 2 : 1;
            shapeEffect.Properties["Outside"] = mask is null ? 0 : (int)MaskDisplay.Background(mask);
            DrawPass(shape, shapeEffect);
            using var filter = NewEffect("EffectFilter");
            using var compose = NewEffect("EffectCompose");
            compose.Source3 = shape; compose.Source4 = raster.Image; compose.Properties["Inset"] = inset;
            gpu.Clear(output[0]); int current = 0;
            if (effects.Shadow is { Opacity: > 0 } shadow)
            { var c = Shift(shadow.Offset); c = Blur(c, shadow.Blur); Fill(c, LayerEffectKind.Shadow, 0); }
            if (effects.OuterGlow is { Opacity: > 0, Size: > 0 } glow)
            { var c = Blur(shape, glow.Size); Fill(c, LayerEffectKind.OuterGlow, 2); }
            if (effects.Stroke is { Inside: false, Opacity: > 0, Size: > 0 } stroke)
            { var c = Extreme(stroke.Size, false); Fill(c, LayerEffectKind.Stroke, 3); }
            compose.Source1 = output[current]; compose.Source2 = shape; compose.Properties["Mode"] = 6;
            DrawPass(output[1 - current], compose); current = 1 - current;
            if (effects.ColorOverlay is { Opacity: > 0 }) Fill(shape, LayerEffectKind.ColorOverlay, 1);
            if (effects.InnerGlow is { Opacity: > 0, Size: > 0 } innerGlow)
            { var c = Blur(shape, innerGlow.Size); Fill(c, LayerEffectKind.InnerGlow, 4); }
            if (effects.InnerShadow is { Opacity: > 0 } inner)
            { var c = Shift(inner.Offset); c = Blur(c, inner.Blur); Fill(c, LayerEffectKind.InnerShadow, 4); }
            if (effects.Stroke is { Inside: true, Opacity: > 0, Size: > 0 } inside)
            { var c = Extreme(inside.Size, true); Fill(c, LayerEffectKind.Stroke, 5); }
            return result = output[current];

            void Fill(GpuTexture coverage, LayerEffectKind kind, int mode)
            {
                var color = effects.Color(kind);
                compose.Source1 = output[current]; compose.Source2 = coverage;
                compose.Properties["Mode"] = mode; compose.Properties["Color"] = new Vector4(color.R, color.G, color.B, 0);
                compose.Properties["Opacity"] = effects.Opacity(kind);
                DrawPass(output[1 - current], compose); current = 1 - current;
            }
            GpuTexture Shift((double X, double Y) offset)
            {
                filter.Source1 = shape; filter.Properties["Mode"] = 0;
                filter.Properties["Shift"] = new Vector2((int)Math.Round(offset.X), (int)Math.Round(offset.Y));
                DrawPass(coveragePlanes[0], filter); return coveragePlanes[0];
            }
            GpuTexture Extreme(double size, bool smallest)
            {
                filter.Properties["Mode"] = 1; filter.Properties["Radius"] = Math.Max(1, (int)Math.Round(size, MidpointRounding.AwayFromZero));
                filter.Properties["Smallest"] = smallest ? 1 : 0;
                filter.Source1 = shape; filter.Properties["Axis"] = 0; DrawPass(coveragePlanes[0], filter);
                filter.Source1 = coveragePlanes[0]; filter.Properties["Axis"] = 1; DrawPass(coveragePlanes[1], filter);
                return coveragePlanes[1];
            }
            GpuTexture Blur(GpuTexture input, double blur)
            {
                if (blur <= 0) return input;
                double sigma = Math.Max(.1, blur / 2);
                if (!EffectGaussian.UsesFloatWorkspace(blur, w, h))
                {
                    int radius = Math.Max(1, (int)Math.Ceiling(sigma * 3)), count = radius * 2 + 1;
                    var weights = new double[count]; double sum = 0;
                    for (int i = 0; i < count; i++) { double k = i - radius; weights[i] = Math.Exp(-k * k / (2 * sigma * sigma)); sum += weights[i]; }
                    var words = new float[(count + 1) * 2 * 4]; double prefix = 0;
                    for (int i = 0; i < count; i++)
                    { double coefficient = weights[i] / sum; WriteDouble(words, i * 4, coefficient); WriteDouble(words, (count + 1 + i) * 4, prefix); prefix += coefficient; }
                    WriteDouble(words, ((count + 1) * 2 - 1) * 4, prefix);
                    using var kernel = FloatTexture(words, count + 1, 2);
                    filter.Source2 = kernel; filter.Properties["Mode"] = 2; filter.Properties["Radius"] = radius;
                    filter.Properties["Wide"] = sigma > 2 ? 1 : 0;
                    var horizontal = ReferenceEquals(input, coveragePlanes[0]) ? coveragePlanes[1] : coveragePlanes[0];
                    var vertical = ReferenceEquals(horizontal, coveragePlanes[0]) ? coveragePlanes[1] : coveragePlanes[0];
                    filter.Source1 = input; filter.Properties["Axis"] = 0; DrawPass(horizontal, filter);
                    filter.Source1 = horizontal; filter.Properties["Axis"] = 1; DrawPass(vertical, filter); return vertical;
                }
                int lower = (int)Math.Floor(Math.Sqrt(4 * sigma * sigma + 1)); if (lower % 2 == 0) lower--;
                int lowerCount = Math.Clamp((int)Math.Round((12 * sigma * sigma - 3 * lower * lower - 12 * lower - 9) / (-4.0 * lower - 4), MidpointRounding.AwayFromZero), 0, 3);
                GpuTexture value = input;
                for (int pass = 0; pass < 3; pass++)
                {
                    int radius = (lower - 1) / 2 + (pass < lowerCount ? 0 : 1);
                    var words = new float[4]; WriteDouble(words, 0, 1.0 / (radius * 2 + 1));
                    using var factor = FloatTexture(words, 1, 1); filter.Source2 = factor;
                    filter.Properties["Mode"] = 3; filter.Properties["Radius"] = radius;
                    filter.Source1 = value; filter.Properties["Axis"] = 0; filter.Properties["InputFloat"] = pass == 0 ? 0 : 1;
                    filter.Properties["Quantize"] = 0; DrawPass(floatPlanes[0], filter);
                    filter.Source1 = floatPlanes[0]; filter.Properties["Axis"] = 1; filter.Properties["InputFloat"] = 1;
                    filter.Properties["Quantize"] = pass == 2 ? 1 : 0;
                    value = pass == 2 ? coveragePlanes[0] : floatPlanes[1]; DrawPass(value, filter);
                }
                return value;
            }
        }
        finally
        {
            foreach (var resource in resources) if (!ReferenceEquals(resource, result)) resource.Dispose();
            retainedBytes -= bytes - (result is null ? 0 : plane);
        }

        static void FillGeometry(float[] words, int offset, LayerTransform t)
        {
            var c = t.Center;
            double[] values = [c.X, c.Y, Math.Cos(t.Radians), Math.Sin(t.Radians), t.Size.Width, t.Size.Height, t.FlipX ? 1 : 0, t.FlipY ? 1 : 0];
            for (int i = 0; i < values.Length; i++) WriteDouble(words, offset + i * 4, values[i]);
        }
    }
}
