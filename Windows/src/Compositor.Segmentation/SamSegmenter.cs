// MobileSAM: Chaoning Zhang et al., Apache-2.0 (https://github.com/ChaoningZhang/MobileSAM).
// SAM prompt labels and ONNX interface: Meta, Apache-2.0 (https://github.com/facebookresearch/segment-anything).
// SAM 2.1: Meta, Apache-2.0 (https://github.com/facebookresearch/sam2).
// SAM 2 ONNX interface: https://huggingface.co/onnx-community/sam2.1-hiera-tiny-ONNX.
// Pinned ONNX exports: Acly (https://huggingface.co/Acly/MobileSAM). This adapter adds bounded
// image-feature reuse, cancellation and DirectML/CPU execution through Microsoft's MIT ONNX Runtime.
using Compositor.Core;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Diagnostics;

namespace Compositor.Segmentation;

public readonly record struct SelectionBox(double X, double Y, double Width, double Height);

public sealed class SamSegmenter(ModelStore model, bool allowGpu = true) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1);
    private InferenceSession? encoder, decoder;
    private Surface? cachedImage;
    private int cachedRevision;
    private Dictionary<string, DenseTensor<float>>? embeddings;
    private volatile bool disposed;
    private bool gpuFailed;
    public string Backend { get; private set; } = "unloaded";
    public string DecoderBackend { get; private set; } = "unloaded";
    public int EncoderRuns { get; private set; }
    public double LastEncodeMilliseconds { get; private set; }
    public double LastDecodeMilliseconds { get; private set; }
    public double LastInferenceMilliseconds { get; private set; }
    public double LastRestoreMilliseconds { get; private set; }
    public double LastRefineMilliseconds { get; private set; }

    public async Task PrepareImageAsync(Surface image, CancellationToken cancellation)
    {
        var geometry = new SamGeometry(image.Width, image.Height, stretch: model.IsSam2);
        await gate.WaitAsync(cancellation);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            await Task.Run(() =>
            {
                try { Prepare(); }
                catch (OnnxRuntimeException) when (Backend == "DirectML" && !cancellation.IsCancellationRequested)
                { Release(); gpuFailed = true; Prepare(); }

                void Prepare()
                {
                    cancellation.ThrowIfCancellationRequested(); Load();
                    using var options = new RunOptions();
                    using var registration = cancellation.Register(() => options.Terminate = true);
                    try { EncodeImage(image, geometry, options, cancellation); }
                    catch (OnnxRuntimeException) when (cancellation.IsCancellationRequested)
                    { throw new OperationCanceledException(cancellation); }
                }
            }, cancellation);
        }
        finally { if (disposed) Release(); gate.Release(); }
    }

    public async Task<MaskSurface> SelectAsync(Surface image, IReadOnlyList<SelectionPoint> points, SelectionBox? box,
        bool softEdges, CancellationToken cancellation, bool refineEdges = true)
    {
        var geometry = new SamGeometry(image.Width, image.Height, stretch: model.IsSam2);
        if (points.Count > 64 || points.Count == 0 && box is null || !points.Any(point => point.Include) && box is null)
            throw new ArgumentException("Use at least one foreground point or a box, with at most 64 points.");
        foreach (var point in points) geometry.ToModel(point.Position);
        if (box is { } area && (!double.IsFinite(area.Width) || !double.IsFinite(area.Height) || area.Width <= 0 || area.Height <= 0
            || !double.IsFinite(area.X) || !double.IsFinite(area.Y) || area.X < 0 || area.Y < 0 || area.X + area.Width > image.Width || area.Y + area.Height > image.Height))
            throw new ArgumentException("Use a nonempty box inside the canvas.");
        await gate.WaitAsync(cancellation);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return await Task.Run(() =>
            {
                try { Load(); return Predict(); }
                catch (OnnxRuntimeException) when (Backend == "DirectML" && !cancellation.IsCancellationRequested)
                { Release(); gpuFailed = true; Load(); return Predict(); }
            }, cancellation);
        }
        finally { if (disposed) Release(); gate.Release(); }

        MaskSurface Predict()
        {
            cancellation.ThrowIfCancellationRequested();
            using var options = new RunOptions();
            using var registration = cancellation.Register(() => options.Terminate = true);
            try
            {
                EncodeImage(image, geometry, options, cancellation);
                var inputs = embeddings!.Select(pair => NamedOnnxValue.CreateFromTensor(pair.Key, pair.Value)).ToList();
                if (model.IsSam2)
                {
                    int pointCount = Math.Max(1, points.Count);
                    var pointCoords = new float[pointCount * 2]; var pointLabels = new long[pointCount];
                    for (int i = 0; i < points.Count; i++)
                    {
                        var point = geometry.ToModel(points[i].Position);
                        pointCoords[2 * i] = (float)point.X; pointCoords[2 * i + 1] = (float)point.Y;
                        pointLabels[i] = points[i].Include ? 1 : 0;
                    }
                    if (points.Count == 0) pointLabels[0] = -1;
                    float[] boxes = box is { } bounds
                        ? [(float)(bounds.X * geometry.Width / image.Width), (float)(bounds.Y * geometry.Height / image.Height),
                           (float)((bounds.X + bounds.Width) * geometry.Width / image.Width), (float)((bounds.Y + bounds.Height) * geometry.Height / image.Height)] : [];
                    inputs.AddRange([
                        NamedOnnxValue.CreateFromTensor("input_points", new DenseTensor<float>(pointCoords, [1, 1, pointCount, 2])),
                        NamedOnnxValue.CreateFromTensor("input_labels", new DenseTensor<long>(pointLabels, [1, 1, pointCount])),
                        NamedOnnxValue.CreateFromTensor("input_boxes", new DenseTensor<float>(boxes, [1, box is null ? 0 : 1, 4])),
                    ]);
                }
                else
                {
                    int count = points.Count + (box is null ? 1 : 2);
                    var coords = new float[count * 2]; var labels = new float[count];
                    for (int i = 0; i < points.Count; i++)
                    {
                        var point = geometry.ToModel(points[i].Position);
                        coords[2 * i] = (float)point.X; coords[2 * i + 1] = (float)point.Y; labels[i] = points[i].Include ? 1 : 0;
                    }
                    if (box is { } bounds)
                    {
                        int i = points.Count;
                        coords[2 * i] = (float)(bounds.X * geometry.Width / image.Width);
                        coords[2 * i + 1] = (float)(bounds.Y * geometry.Height / image.Height); labels[i] = 2;
                        coords[2 * i + 2] = (float)((bounds.X + bounds.Width) * geometry.Width / image.Width);
                        coords[2 * i + 3] = (float)((bounds.Y + bounds.Height) * geometry.Height / image.Height); labels[i + 1] = 3;
                    }
                    else labels[^1] = -1;
                    inputs.AddRange([
                        NamedOnnxValue.CreateFromTensor("point_coords", new DenseTensor<float>(coords, [1, count, 2])),
                        NamedOnnxValue.CreateFromTensor("point_labels", new DenseTensor<float>(labels, [1, count])),
                        NamedOnnxValue.CreateFromTensor("mask_input", new DenseTensor<float>(new float[256 * 256], [1, 1, 256, 256])),
                        NamedOnnxValue.CreateFromTensor("has_mask_input", new DenseTensor<float>(new float[1], [1])),
                        NamedOnnxValue.CreateFromTensor("orig_im_size", new DenseTensor<float>(new float[] { SamGeometry.Side, SamGeometry.Side }, [2])),
                    ]);
                }
                var timer = Stopwatch.StartNew();
                string maskName = model.IsSam2 ? "pred_masks" : "low_res_masks", scoreName = model.IsSam2 ? "iou_scores" : "iou_predictions";
                using var masks = decoder!.Run(inputs, [maskName, scoreName], options);
                LastInferenceMilliseconds = timer.Elapsed.TotalMilliseconds;
                var scores = masks.Single(value => value.Name == scoreName).AsTensor<float>().ToArray();
                var low = masks.Single(value => value.Name == maskName).AsTensor<float>();
                // SAM 2 exports three candidates; SAM 1 also exposes its single-mask token.
                // SAM 1 token 0 is trained for an unambiguous box/multiple-click request.
                // Only a single click uses the three alternative object extents. Comparing
                // all four scores can undo an explicit exclude prompt by picking a broad mask.
                bool alternatives = scores.Length > 1 && (model.IsSam2 || points.Count == 1 && box is null);
                int best = alternatives && !model.IsSam2 ? 1 : 0;
                if (alternatives)
                    for (int i = best + 1; i < scores.Length; i++) if (scores[i] > scores[best]) best = i;
                int height = low.Dimensions[^2], width = low.Dimensions[^1], size = width * height;
                var coverage = geometry.Restore(low.ToArray().AsSpan(best * size, size), width, height, softEdges, cancellation, points);
                LastRestoreMilliseconds = timer.Elapsed.TotalMilliseconds - LastInferenceMilliseconds;
                long refinementStart = Stopwatch.GetTimestamp();
                if (refineEdges) coverage = SamEdgeRefinement.Refine(image, coverage, softEdges, points, cancellation);
                LastRefineMilliseconds = Stopwatch.GetElapsedTime(refinementStart).TotalMilliseconds;
                LastDecodeMilliseconds = timer.Elapsed.TotalMilliseconds;
                return coverage;
            }
            catch (OnnxRuntimeException) when (cancellation.IsCancellationRequested) { throw new OperationCanceledException(cancellation); }
        }
    }

    private void EncodeImage(Surface image, SamGeometry geometry, RunOptions options, CancellationToken cancellation)
    {
        LastEncodeMilliseconds = 0;
        if (ReferenceEquals(image, cachedImage) && cachedRevision == image.Revision && embeddings is not null) return;
        var watch = Stopwatch.StartNew();
        var rgb = SamGeometry.SampleRgb(image, geometry.Width, geometry.Height, cancellation);
        var input = encoder!.InputMetadata.First(); DenseTensor<float> tensor;
        if (input.Value.Dimensions.Length == 3) tensor = new(rgb, [geometry.Height, geometry.Width, 3]);
        else
        {
            var data = new float[3 * SamGeometry.Side * SamGeometry.Side];
            float[] mean = [123.675f, 116.28f, 103.53f], std = [58.395f, 57.12f, 57.375f];
            for (int y = 0; y < geometry.Height; y++)
            {
                cancellation.ThrowIfCancellationRequested();
                for (int x = 0; x < geometry.Width; x++)
                    for (int channel = 0; channel < 3; channel++)
                        data[channel * SamGeometry.Side * SamGeometry.Side + y * SamGeometry.Side + x] = (rgb[(y * geometry.Width + x) * 3 + channel] - mean[channel]) / std[channel];
            }
            tensor = new(data, [1, 3, SamGeometry.Side, SamGeometry.Side]);
        }
        string[] featureNames = model.IsSam2 ? ["image_embeddings.0", "image_embeddings.1", "image_embeddings.2"] : ["image_embeddings"];
        using var result = encoder.Run([NamedOnnxValue.CreateFromTensor(input.Key, tensor)], featureNames, options);
        embeddings = result.ToDictionary(value => value.Name, value =>
        {
            var feature = value.AsTensor<float>();
            return new DenseTensor<float>(feature.ToArray(), feature.Dimensions.ToArray());
        });
        cachedImage = image; cachedRevision = image.Revision; EncoderRuns++;
        LastEncodeMilliseconds = watch.Elapsed.TotalMilliseconds;
    }

    private void Load()
    {
        if (encoder is not null) return;
        bool gpu = allowGpu && !gpuFailed;
        try
        {
            using var options = new SessionOptions { EnableMemoryPattern = false, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4), LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR };
            if (gpu) options.AppendExecutionProvider_DML(0);
            var files = model.IsSam2 ? ModelStore.Sam2Files : ModelStore.MobileSamFiles;
            encoder = new(Path.Combine(model.DirectoryPath, files[0].Name), options);
            // Features already live in host memory for reuse. The small decoder avoids a
            // round trip through DirectML; the large encoder retains GPU/CPU fallback.
            using var decoderOptions = new SessionOptions { IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
                LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR };
            decoder = new(Path.Combine(model.DirectoryPath, files[model.IsSam2 ? 2 : 1].Name), decoderOptions);
            DecoderBackend = "CPU";
            Backend = gpu ? "DirectML" : "CPU";
        }
        catch (Exception ex) when (gpu && ex is OnnxRuntimeException or DllNotFoundException or EntryPointNotFoundException)
        { Release(); gpuFailed = true; Load(); }
    }

    private void Release()
    {
        encoder?.Dispose(); decoder?.Dispose(); encoder = decoder = null;
        cachedImage = null; embeddings = null;
    }

    public void Dispose()
    {
        disposed = true;
        if (gate.Wait(0)) { try { Release(); } finally { gate.Release(); } }
    }
}
