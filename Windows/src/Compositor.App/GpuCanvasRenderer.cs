using System.Runtime.InteropServices;
using Compositor.Core;
using Microsoft.Graphics.Canvas;
using Vortice.DXGI;
using static Compositor.App.NativeGpu;

namespace Compositor.App;

// D3D11 bindings: Vortice.Windows (Amer Koleci and contributors, MIT).
// https://github.com/amerkoleci/Vortice.Windows. Win2D presents the shared texture.
// All pixel formulas and CPU-generated sampling tables are Compositor's own reference math.
internal sealed partial class GpuCanvasRenderer : IDisposable
{
    private readonly CanvasDevice device;
    private readonly NativeGpu gpu;
    private readonly GpuTexture[] targets = [];
    private readonly Dictionary<Guid, Raster> rasters = new();
    private readonly Dictionary<Guid, Grade> grades = new();
    private readonly GpuTexture white = null!;
    private long retainedBytes, cacheClock;
    private readonly long byteBudget;
    private readonly int width, height;
    internal bool IsDisposed { get; private set; }
    internal uint[] NoiseSeeds => noisePlates.Keys.ToArray();
    internal long TextureBudget => byteBudget;
    private IReadOnlyDictionary<uint, float[]>? preparedNoise;
    internal bool MatchesSize(int w, int h) => width == w && height == h;
#if DEBUG
    internal long RetainedTextureBytes => retainedBytes;
    internal int AssetUploads { get; private set; }
    internal long UploadedTextureBytes => gpu.UploadedBytes;
#endif

    private abstract class CacheEntry : IDisposable
    {
        internal required Guid Id;
        internal long Bytes, LastUse;
        public abstract void Dispose();
    }
    private sealed class Raster : CacheEntry
    {
        internal required Surface Source;
        internal int Revision;
        internal required GpuTexture Image, Columns, Rows;
        internal GpuTexture SampleImage = null!;
        internal int ContentWidth => SampleImage.Width;
        internal int ContentHeight => SampleImage.Height;
        internal required GpuEffect Effect;
        internal GpuEffect? RotatedEffect;
        internal GpuTexture? Geometry;
        internal LayerTransform? Placement;
        internal readonly int[] Indices = new int[4];
        internal readonly float[] Weights = new float[4];
        internal byte[] X = [], Y = [];
        public override void Dispose() { RotatedEffect?.Dispose(); Geometry?.Dispose(); Effect.Dispose(); Image.Dispose(); Columns.Dispose(); Rows.Dispose(); }
    }
    private sealed class Grade : CacheEntry
    {
        internal required LayerAdjustment Source;
        internal int Revision;
        internal required GpuTexture Table;
        internal required GpuEffect Effect;
        internal MaskSurface? Mask;
        internal int MaskRevision;
        internal GpuTexture? MaskImage;
        internal long TableBytes;
        internal GpuEffect? FirstPass;
        public override void Dispose() { FirstPass?.Dispose(); Effect.Dispose(); Table.Dispose(); MaskImage?.Dispose(); }
    }

    internal GpuCanvasRenderer(CanvasDevice device, int width, int height, long byteBudget = 0)
    {
        if (width > device.MaximumBitmapSizeInPixels || height > device.MaximumBitmapSizeInPixels
            || byteBudget > 0 && (long)width * height * 8 + 4 > byteBudget) throw new NotSupportedException("GPU canvas exceeds its texture budget.");
        this.device = device; this.width = width; this.height = height;
        gpu = new(device);
        this.byteBudget = byteBudget > 0 ? byteBudget : gpu.TextureBudget;
        GpuTexture? first = null;
        try
        {
            if ((long)width * height * 8 + 4 > this.byteBudget) throw new NotSupportedException("GPU canvas exceeds its texture budget.");
            first = CreateTarget(); targets = [first, CreateTarget()]; first = null;
            retainedBytes = (long)width * height * 8 + 4;
            white = gpu.Texture([255, 255, 255, 255], 1, 1, Format.R8G8B8A8_UNorm);
        }
        catch { first?.Dispose(); Dispose(); throw; }
    }

    internal CanvasBitmap Render(GpuPreviewScene scene, IReadOnlyDictionary<uint, float[]>? noise = null)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        preparedNoise = noise ?? scene.PrepareGaussianNoise(noisePlates.Keys.ToHashSet(), byteBudget);
        try { return RenderScene(scene); }
        finally { preparedNoise = null; }
    }

    private CanvasBitmap RenderScene(GpuPreviewScene scene)
    {
        using var session = gpu.Begin();
        foreach (var id in rasters.Keys.Where(id => !scene.Snapshot.Images.ContainsKey(id)).ToArray()) Retire(rasters[id]);
        foreach (var id in grades.Keys.Where(id => !scene.Layers.Any(layer => layer.Id == id && layer.Adjustment is not null)).ToArray())
        {
            Retire(grades[id]);
        }
        foreach (var id in maskPlates.Keys.Where(id => !scene.Snapshot.Masks.ContainsKey(id)).ToArray()) Retire(maskPlates[id]);
        foreach (var id in effectsPlates.Keys.Where(id => !scene.Snapshot.Images.ContainsKey(id)).ToArray()) Retire(effectsPlates[id]);
        foreach (var id in liveGeometry.Keys.Where(id => !scene.Snapshot.Manifest.Layers.Any(layer => layer.Id == id)).ToArray())
        { liveGeometry[id].Geometry.Dispose(); liveGeometry.Remove(id); retainedBytes -= 160; }
        gpu.Clear(targets[0]);
        var byId = scene.Snapshot.Manifest.Layers.ToDictionary(layer => layer.Id);
        int current = 0;
        foreach (var layer in scene.Layers)
        {
            double opacity = LayerOpacity(layer, byId);
            var enabledMask = layer.MaskFile is not null && layer.MaskEnabled != false ? scene.Snapshot.Masks.GetValueOrDefault(layer.Id) : null;
            if (NeedsGeneralComposition(layer, byId) || enabledMask is { } smaller && (smaller.Width != width || smaller.Height != height))
            {
                RenderGeneral(scene, layer, byId, targets[current], targets[1 - current], opacity);
                current = 1 - current;
                continue;
            }
            var destination = targets[1 - current];
            GpuEffect effect;
            if (layer.Adjustment is { } adjustment)
            {
                var grade = GradeFor(layer.Id, adjustment); effect = PrepareGrade(grade, targets[current], layer);
                var mask = enabledMask;
                if (!ReferenceEquals(grade.Mask, mask) || grade.MaskRevision != (mask?.Revision ?? 0))
                {
                    if (mask is null && grade.MaskImage is not null)
                    { grade.MaskImage.Dispose(); grade.MaskImage = null; retainedBytes -= (long)width * height; grade.Bytes -= (long)width * height; }
                    else if (mask is not null)
                    {
                        if (grade.MaskImage is not null)
                        {
                            var dirty = ReferenceEquals(grade.Mask, mask) && mask.TryGetChangesSince(grade.MaskRevision, out var known)
                                ? known : new RectI(0, 0, width, height);
                            grade.MaskImage.SetPixels(mask.Pixels, width, dirty);
                        }
                        else
                        {
                            long bytes = (long)width * height;
                            Reserve(bytes, grade.Id);
                            try { grade.MaskImage = gpu.Texture(mask.Pixels, width, height, Format.R8_UNorm); grade.Bytes += bytes; }
                            catch { retainedBytes -= bytes; throw; }
                        }
#if DEBUG
                        AssetUploads++;
#endif
                    }
                    grade.Mask = mask; grade.MaskRevision = mask?.Revision ?? 0;
                }
                effect.Source3 = grade.MaskImage ?? white;
                effect.Properties["Masked"] = mask is null ? 0 : 1;
            }
            else
            {
                var (raster, placement) = RasterForLayer(scene, layer); effect = PrepareRaster(raster, placement);
                effect.Source4 = targets[current];
            }
            gpu.Draw(destination, effect);
            current = 1 - current;
        }
        return targets[current].Bitmap;
    }

    private Raster RasterFor(Guid id, Surface source)
    {
        if (rasters.TryGetValue(id, out var cached) && ReferenceEquals(cached.Source, source))
        {
            cached.LastUse = ++cacheClock;
            if (cached.Revision != source.Revision)
            {
                var dirty = source.TryGetChangesSince(cached.Revision, out var known) ? known : new RectI(0, 0, source.Width, source.Height);
                cached.Image.SetPixels(source.Pixels, source.Stride, dirty); cached.Revision = source.Revision;
#if DEBUG
                AssetUploads++;
#endif
            }
            return cached;
        }
        if (cached is not null) Retire(cached);
        long bytes = (long)source.Width * source.Height * 4 + (long)(width + height) * 32;
        Reserve(bytes);
        GpuTexture? image = null, columns = null, rows = null; GpuEffect? effect = null;
        try
        {
        image = gpu.Texture(source.Pixels, source.Width, source.Height, Format.R8G8B8A8_UNorm, source.Stride);
#if DEBUG
        AssetUploads++;
#endif
        columns = FloatTexture(new float[width * 8], width, 2); rows = FloatTexture(new float[height * 8], height, 2);
        effect = NewEffect("LayerSample");
        effect.Source1 = image; effect.Source2 = columns; effect.Source3 = rows;
        return rasters[id] = new() { Id = id, Bytes = bytes, LastUse = ++cacheClock, Source = source, Revision = source.Revision, Image = image, SampleImage = image, Columns = columns, Rows = rows,
            Effect = effect, X = new byte[width * 32], Y = new byte[height * 32] };
        }
        catch { effect?.Dispose(); rows?.Dispose(); columns?.Dispose(); image?.Dispose(); retainedBytes -= bytes; throw; }
    }

    private void SetSampling(Raster raster, LayerTransform transform)
    {
        var center = transform.Center;
        double cos = Math.Cos(transform.Radians);
        double stepX = (transform.FlipX ? -1 : 1) * cos * raster.ContentWidth / transform.Size.Width;
        double startX = transform.FlipX ? raster.ContentWidth / 2d + center.X * cos * raster.ContentWidth / transform.Size.Width
            : raster.ContentWidth / 2d - center.X * cos * raster.ContentWidth / transform.Size.Width;
        double stepY = (transform.FlipY ? -1 : 1) * raster.ContentHeight / transform.Size.Height;
        double startY = transform.FlipY ? raster.ContentHeight / 2d + center.Y * raster.ContentHeight / transform.Size.Height
            : raster.ContentHeight / 2d - center.Y * raster.ContentHeight / transform.Size.Height;
        bool copy = Math.Abs(stepX - 1) < 1e-9 && Math.Abs(stepY - 1) < 1e-9 && !transform.FlipX && !transform.FlipY
            && Math.Abs(startX - Math.Round(startX)) < 1e-9 && Math.Abs(startY - Math.Round(startY)) < 1e-9;
        int taps = copy || transform.Sampling == LayerSampling.Nearest ? 1 : transform.Sampling == LayerSampling.Smooth ? 2 : 4;
        var bounds = Resampler.BoundingRect(transform, width, height);
        Fill(MemoryMarshal.Cast<byte, float>(raster.X.AsSpan()), width, raster.ContentWidth, startX, stepX, bounds.X0, bounds.X1);
        Fill(MemoryMarshal.Cast<byte, float>(raster.Y.AsSpan()), height, raster.ContentHeight, startY, stepY, bounds.Y0, bounds.Y1);
        raster.Columns.SetPixelBytes(raster.X);
        raster.Rows.SetPixelBytes(raster.Y);
        raster.Effect.Properties["TapCount"] = taps;

        void Fill(Span<float> data, int count, int limit, double start, double step, int first, int last)
        {
            for (int i = 0; i < count; i++)
            {
                double position = start + step * (i + .5);
                bool valid = i >= first && i < last && (taps == 1 ? position >= 0 && position < limit : position >= -.5 && position <= limit - .5);
                if (!valid) { data[i * 4] = -1; continue; }
                if (taps == 1)
                {
                    data[i * 4] = copy ? i + (int)Math.Round(start) : Math.Clamp((int)Math.Floor(position), 0, limit - 1);
                    data[(count + i) * 4] = 1;
                }
                else
                {
                    Resampler.WriteTaps(raster.Indices, raster.Weights, 0, position - .5, limit, taps);
                    for (int k = 0; k < taps; k++) { data[i * 4 + k] = raster.Indices[k]; data[(count + i) * 4 + k] = raster.Weights[k]; }
                }
            }
        }
    }

    private Grade GradeFor(Guid id, LayerAdjustment adjustment)
    {
        if (grades.TryGetValue(id, out var cached) && ReferenceEquals(cached.Source, adjustment) && cached.Revision == adjustment.Revision)
        { cached.LastUse = ++cacheClock; return cached; }
        if (cached is not null) Retire(cached);
        if (adjustment.Kind is not (AdjustmentKind.Levels or AdjustmentKind.Curves or AdjustmentKind.Exposure
            or AdjustmentKind.ColorBalance or AdjustmentKind.Invert)) return AdvancedGrade(id, adjustment);
        long tableBytes = adjustment.Kind == AdjustmentKind.ColorBalance ? 256 * 256 * 16 : 256 * 3 * 16;
        Reserve(tableBytes);
        var data = new float[tableBytes / 4];
        if (adjustment.Kind == AdjustmentKind.ColorBalance)
        {
            var settings = adjustment.ColorBalanceSettings ?? new();
            float[] shadow = [(float)(settings.ShadowCyanRed / 100), (float)(settings.ShadowMagentaGreen / 100), (float)(settings.ShadowYellowBlue / 100)];
            float[] midtone = [(float)(settings.MidCyanRed / 100), (float)(settings.MidMagentaGreen / 100), (float)(settings.MidYellowBlue / 100)];
            float[] highlight = [(float)(settings.HighlightCyanRed / 100), (float)(settings.HighlightMagentaGreen / 100), (float)(settings.HighlightYellowBlue / 100)];
            for (int alpha = 1; alpha <= 255; alpha++)
            for (int i = 0; i < 256; i++)
            {
                // CPU divisions and tonal arithmetic are evaluated once per byte/alpha pair,
                // including partial alpha. The GPU keeps these exact floats until rounding.
                int offset = (alpha * 256 + i) * 4;
                float unit = alpha == 255 ? i / 255f : Math.Min(255f, i * 255f / alpha) / 255f;
                data[offset + 3] = unit;
                for (int c = 0; c < 3; c++) data[offset + c] = AdjustmentEngine.ColorBalanceShift(unit, shadow[c], midtone[c], highlight[c]);
            }
        }
        else if (adjustment.Kind != AdjustmentKind.Invert)
        {
            float[][] tables;
            if (adjustment.Kind == AdjustmentKind.Levels) tables = AdjustmentEngine.LevelsTables(adjustment.Levels);
            else if (adjustment.Kind == AdjustmentKind.Curves) tables = AdjustmentEngine.CurvesTables(adjustment.Curves);
            else { var exposure = AdjustmentEngine.ExposureTable(adjustment.ExposureSettings ?? new()); tables = [exposure, exposure, exposure]; }
            for (int i = 0; i < 256; i++) for (int c = 0; c < 3; c++) data[i * 4 + c] = tables[c][i];
        }
        GpuTexture? table = null; GpuEffect? effect = null;
        try
        {
            table = FloatTexture(data, 256, adjustment.Kind == AdjustmentKind.ColorBalance ? 256 : 3);
#if DEBUG
            AssetUploads++;
#endif
            try { effect = NewEffect("PointAdjustmentFast"); }
            catch (SharpGen.Runtime.SharpGenException) { effect = NewEffect("PointAdjustment"); }
            effect.Source2 = table;
            effect.Properties["Kind"] = adjustment.Kind == AdjustmentKind.ColorBalance ? 1 : adjustment.Kind == AdjustmentKind.Invert ? 2 : 0;
            effect.Properties["PreserveLuminosity"] = adjustment.ColorBalanceSettings?.PreserveLuminosity != false ? 1 : 0;
            return grades[id] = new() { Id = id, Bytes = tableBytes, LastUse = ++cacheClock, Source = adjustment, Revision = adjustment.Revision, Table = table, Effect = effect, TableBytes = tableBytes };
        }
        catch { effect?.Dispose(); table?.Dispose(); retainedBytes -= tableBytes; throw; }
    }

    private GpuEffect NewEffect(string name) => gpu.Effect(name);
    private GpuTexture FloatTexture(float[] values, int w, int h) => gpu.Texture(
        MemoryMarshal.AsBytes(values.AsSpan()), w, h, Format.R32G32B32A32_Float);
    private GpuTexture CreateTarget() => gpu.Target(width, height);
    private void Reserve(long bytes, Guid? pinned = null)
    {
        while (retainedBytes + bytes > byteBudget)
        {
            CacheEntry? oldest = null;
            foreach (var item in rasters.Values) Consider(item);
            foreach (var item in grades.Values) Consider(item);
            foreach (var item in maskPlates.Values) Consider(item);
            foreach (var item in effectsPlates.Values) Consider(item);
            foreach (var item in noisePlates.Values) Consider(item);
            if (oldest is null) throw new NotSupportedException("GPU canvas texture budget reached.");
            Retire(oldest);
            void Consider(CacheEntry item) { if (item.Id != pinned && (oldest is null || item.LastUse < oldest.LastUse)) oldest = item; }
        }
        retainedBytes += bytes;
    }
    private void Retire(CacheEntry item)
    {
        switch (item)
        {
            case Raster: rasters.Remove(item.Id); break;
            case Grade: grades.Remove(item.Id); break;
            case MaskPlate: maskPlates.Remove(item.Id); break;
            case EffectsPlate: effectsPlates.Remove(item.Id); break;
            case NoisePlate noise: noisePlates.Remove(noise.Seed); break;
        }
        retainedBytes -= item.Bytes; item.Dispose();
    }
    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        DisposeGeneral();
        foreach (var raster in rasters.Values) raster.Dispose();
        foreach (var grade in grades.Values) grade.Dispose();
        foreach (var plate in effectsPlates.Values) plate.Dispose();
        foreach (var plate in noisePlates.Values) plate.Dispose();
        foreach (var target in targets) target.Dispose();
        white?.Dispose();
        gpu.Dispose();
        rasters.Clear(); grades.Clear(); maskPlates.Clear(); effectsPlates.Clear(); noisePlates.Clear(); liveGeometry.Clear();
        retainedBytes = 0;
    }
}
