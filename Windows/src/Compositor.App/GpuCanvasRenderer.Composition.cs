using Compositor.Core;
using Microsoft.Graphics.Canvas;
using Vortice.DXGI;
using static Compositor.App.NativeGpu;

namespace Compositor.App;

// Additional composition passes keep each CPU byte-quantization boundary. Win2D is MIT:
// https://github.com/microsoft/Win2D. The formulas are shared with the CPU reference.
internal sealed partial class GpuCanvasRenderer
{
    private GpuTexture[] temporary = [];
    private GpuTexture? units, transparent;
    private GpuEffect? blending, coverage;
    private readonly Dictionary<Guid, MaskPlate> maskPlates = new();
    private readonly Dictionary<Guid, (LayerTransform Placement, GpuTexture Geometry)> liveGeometry = new();

    private sealed class MaskPlate : CacheEntry
    {
        internal required MaskSurface Source;
        internal int Revision;
        internal required GpuTexture Image, Columns, Rows;
        internal GpuTexture? Geometry;
        internal LayerTransform? Placement;
        internal byte[] X = [], Y = [];
        public override void Dispose() { Geometry?.Dispose(); Image.Dispose(); Columns.Dispose(); Rows.Dispose(); }
    }

    private static double LayerOpacity(ProjectLayerRecord layer, Dictionary<Guid, ProjectLayerRecord> byId)
    {
        double value = layer.OpacityOrOne;
        Guid? parent = layer.ParentId;
        for (int depth = 0; depth < 64 && parent is { } id && byId.TryGetValue(id, out var folder); depth++)
        { value *= folder.OpacityOrOne; parent = folder.ParentId; }
        return value;
    }

    private bool NeedsGeneralComposition(ProjectLayerRecord layer, Dictionary<Guid, ProjectLayerRecord> byId)
    {
        if ((layer.BlendMode ?? LayerBlendMode.Normal) != LayerBlendMode.Normal || LayerOpacity(layer, byId) != 1
            || layer.MaskSourceId is not null
            || layer.Adjustment is not null && (!Aligned(layer.Transform)
                || layer.MaskFile is not null && layer.MaskEnabled != false && !Aligned(layer.MaskPlacement ?? layer.Transform))
            || layer.Adjustment is null && layer.MaskFile is not null && layer.MaskEnabled != false) return true;
        Guid? parent = layer.ParentId;
        for (int depth = 0; depth < 64 && parent is { } id && byId.TryGetValue(id, out var folder); depth++)
        {
            if (folder.MaskFile is not null && folder.MaskEnabled != false) return true;
            parent = folder.ParentId;
        }
        return false;
    }

    private bool Aligned(LayerTransform t) => t.Origin == new PointD(0, 0) && t.Size == new SizeD(width, height)
        && t.Rotation == 0 && !t.FlipX && !t.FlipY;

    private void EnsureGeneral()
    {
        if (blending is not null) return;
        long bytes = (long)width * height * 8 + 4 + 256 * 256 * 16;
        Reserve(bytes);
        var created = new List<IDisposable>();
        try
        {
            var first = CreateTarget(); created.Add(first);
            var second = CreateTarget(); created.Add(second);
            transparent = gpu.Texture([0, 0, 0, 0], 1, 1, Format.R8G8B8A8_UNorm);
            created.Add(transparent);
            var values = new float[256 * 256 * 4];
            for (int alpha = 0; alpha < 256; alpha++)
            for (int value = 0; value < 256; value++)
            {
                int p = (alpha * 256 + value) * 4;
                float a = alpha / 255f;
                values[p] = value / 255f * (a > 0 ? 1f / a : 0f);
                values[p + 1] = value / 255f * (alpha > 0 ? 255f / alpha : 0f);
                values[p + 2] = value / 255f;
                values[p + 3] = a;
            }
            units = FloatTexture(values, 256, 256); created.Add(units);
            coverage = NewEffect("LayerCoverage"); created.Add(coverage);
            blending = NewEffect("LayerBlend"); created.Add(blending);
            blending.Source3 = units;
            temporary = [first, second];
        }
        catch
        {
            foreach (var item in created) item.Dispose();
            temporary = []; units = transparent = null; blending = coverage = null;
            retainedBytes -= bytes;
            throw;
        }
    }

    private void DrawPass(GpuTexture destination, GpuEffect image) => gpu.Draw(destination, image);

    private void RenderGeneral(GpuPreviewScene scene, ProjectLayerRecord layer, Dictionary<Guid, ProjectLayerRecord> byId,
        GpuTexture backdrop, GpuTexture destination, double opacity)
    {
        EnsureGeneral();
        GpuEffect effect;
        bool maskInside = false;
        if (layer.Adjustment is { } adjustment)
        {
            effect = PrepareGrade(GradeFor(layer.Id, adjustment), backdrop, layer);
            effect.Source3 = white;
            effect.Properties["Masked"] = 0;
        }
        else
        {
            var (raster, placement) = RasterForLayer(scene, layer); effect = PrepareRaster(raster, placement);
            maskInside = !ReferenceEquals(raster.SampleImage, raster.Image);
            effect.Source4 = transparent;
        }
        DrawPass(temporary[0], effect);
        int current = 0;
        bool interpolate = layer.Adjustment is not null && (layer.BlendMode ?? LayerBlendMode.Normal) == LayerBlendMode.Normal;
        if (!maskInside) ApplyMask(layer);
        Guid? parent = layer.ParentId;
        for (int depth = 0; depth < 64 && parent is { } id && byId.TryGetValue(id, out var folder); depth++)
        { ApplyMask(folder); parent = folder.ParentId; }
        Guid? sourceId = layer.MaskSourceId;
        for (int depth = 0; depth < 256 && sourceId is { } id && byId.TryGetValue(id, out var source); depth++)
        {
            double strength = LayerOpacity(source, byId);
            bool blank = strength <= 0 || source.ImageFile is null || !scene.Snapshot.Images.TryGetValue(source.Id, out _);
            var clip = blank ? default : RasterForLayer(scene, source);
            coverage!.Source1 = temporary[current]; coverage.Source2 = backdrop;
            coverage.Source3 = blank ? transparent : clip.Raster.SampleImage;
            coverage.Source6 = LiveGeometry(source.Id, blank ? source.Transform : clip.Placement);
            coverage.Source4 = white; coverage.Source5 = white;
            coverage.Properties["Live"] = 1; coverage.Properties["Rotated"] = 1;
            coverage.Properties["Outside"] = 0;
            coverage.Properties["Interpolate"] = interpolate ? 1 : 0;
            coverage.Properties["Opacity"] = blank ? 0f : (float)strength;
            DrawPass(temporary[1 - current], coverage); current = 1 - current;
            if (blank) break;
            if (ReferenceEquals(clip.Raster.SampleImage, clip.Raster.Image)) ApplyMask(source);
            sourceId = source.MaskSourceId;
        }
        blending!.Source1 = temporary[current]; blending.Source2 = backdrop;
        blending.Properties["Mode"] = (int)(layer.BlendMode ?? LayerBlendMode.Normal);
        blending.Properties["Replace"] = interpolate ? 1 : 0;
        blending.Properties["Opacity"] = (float)opacity;
        blending.Properties["Keep"] = (float)(1 - opacity);
        blending.Properties["Region"] = layer.Adjustment is not null ? GradeBounds(layer) : new System.Numerics.Vector4(0, 0, width, height);
        DrawPass(destination, blending);

        void ApplyMask(ProjectLayerRecord record)
        {
            if (record.MaskFile is null || record.MaskEnabled == false || !scene.Snapshot.Masks.TryGetValue(record.Id, out var mask)) return;
            var placement = record.MaskPlacement ?? record.Transform;
            var plate = MaskFor(record.Id, mask, placement);
            coverage!.Source1 = temporary[current]; coverage.Source2 = backdrop; coverage.Source3 = plate.Image;
            coverage.Source4 = plate.Columns; coverage.Source5 = plate.Rows;
            coverage.Source6 = plate.Geometry;
            coverage.Properties["Live"] = 0;
            coverage.Properties["Rotated"] = placement.Rotation == 0 ? 0 : 1;
            coverage.Properties["Interpolate"] = interpolate ? 1 : 0;
            bool same = placement.Origin == record.Transform.Origin && placement.Size == record.Transform.Size
                && placement.Rotation == record.Transform.Rotation && placement.FlipX == record.Transform.FlipX && placement.FlipY == record.Transform.FlipY;
            coverage.Properties["Outside"] = same ? 0 : (int)MaskDisplay.Background(mask);
            DrawPass(temporary[1 - current], coverage); current = 1 - current;
        }
    }

    private MaskPlate MaskFor(Guid id, MaskSurface source, LayerTransform transform)
    {
        if (!maskPlates.TryGetValue(id, out var plate) || !ReferenceEquals(plate.Source, source))
        {
            if (plate is not null) Retire(plate);
            long bytes = (long)source.Width * source.Height + (long)(width + height) * 16 + 160;
            Reserve(bytes);
            GpuTexture? image = null, columns = null, rows = null, geometry = null;
            try
            {
                image = gpu.Texture(source.Pixels, source.Width, source.Height, Format.R8_UNorm);
                columns = FloatTexture(new float[width * 4], width, 1); rows = FloatTexture(new float[height * 4], height, 1);
                geometry = FloatTexture(new float[40], 10, 1);
                plate = new() { Id = id, Bytes = bytes, LastUse = ++cacheClock, Source = source, Revision = source.Revision, Image = image, Columns = columns, Rows = rows, Geometry = geometry,
                    X = new byte[width * 16], Y = new byte[height * 16] };
                maskPlates[id] = plate;
#if DEBUG
                AssetUploads++;
#endif
            }
            catch { geometry?.Dispose(); rows?.Dispose(); columns?.Dispose(); image?.Dispose(); retainedBytes -= bytes; throw; }
        }
        else if (plate.Revision != source.Revision)
        {
            var dirty = source.TryGetChangesSince(plate.Revision, out var known) ? known : new RectI(0, 0, source.Width, source.Height);
            plate.Image.SetPixels(source.Pixels, source.Width, dirty); plate.Revision = source.Revision;
#if DEBUG
            AssetUploads++;
#endif
        }
        plate.LastUse = ++cacheClock;
        if (plate.Placement?.Equals(transform) != true)
        {
            Fill(plate.X, width, source.Width, transform.Origin.X, transform.Size.Width, transform.FlipX);
            Fill(plate.Y, height, source.Height, transform.Origin.Y, transform.Size.Height, transform.FlipY);
            plate.Columns.SetPixelBytes(plate.X); plate.Rows.SetPixelBytes(plate.Y);
            WriteCoverageTransform(plate.Geometry!, transform);
            plate.Placement = TransformEdit.Copy(transform);
        }
        return plate;

        static void Fill(byte[] bytes, int count, int limit, double origin, double size, bool flip)
        {
            var values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes.AsSpan());
            double center = origin + size / 2;
            for (int i = 0; i < count; i++)
            {
                double unit = (i + .5 - center) / size + .5;
                double coordinate = (flip ? 1 - unit : unit) * limit;
                values[i * 4] = coordinate < 0 || coordinate >= limit ? -1 : (float)Math.Floor(coordinate);
            }
        }
    }

    private GpuTexture LiveGeometry(Guid id, LayerTransform transform)
    {
        if (!liveGeometry.TryGetValue(id, out var cached))
        {
            Reserve(160, id);
            cached = (TransformEdit.Copy(transform), FloatTexture(new float[40], 10, 1));
            WriteCoverageTransform(cached.Geometry, transform);
            liveGeometry[id] = cached;
        }
        else if (!cached.Placement.Equals(transform))
        {
            WriteCoverageTransform(cached.Geometry, transform);
            liveGeometry[id] = (TransformEdit.Copy(transform), cached.Geometry);
        }
        return cached.Geometry;
    }

    private static void WriteCoverageTransform(GpuTexture geometry, LayerTransform transform)
    {
        var center = transform.Center;
        double[] values = [center.X, center.Y, Math.Cos(transform.Radians), Math.Sin(transform.Radians),
            transform.Size.Width, transform.Size.Height, transform.FlipX ? 1 : 0, transform.FlipY ? 1 : 0];
        var words = new float[40];
        for (int i = 0; i < values.Length; i++) WriteDouble(words, i * 4, values[i]);
        geometry.SetPixelBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(words.AsSpan()));
    }

    private void DisposeGeneral()
    {
        blending?.Dispose(); coverage?.Dispose(); units?.Dispose(); transparent?.Dispose();
        foreach (var target in temporary) target.Dispose();
        foreach (var plate in maskPlates.Values) plate.Dispose();
        foreach (var entry in liveGeometry.Values) entry.Geometry.Dispose();
        cubicWeights?.Dispose();
        blurTarget?.Dispose();
    }
}
