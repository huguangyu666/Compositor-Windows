using Compositor.Core;
using Compositor.Segmentation;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

if (args.Length < 3) throw new ArgumentException("Usage: SegmentationProbe <model-directory> <image> <output-directory> [cpu]");
string root = Path.GetFullPath(args[2]); Directory.CreateDirectory(root);
if (args[0] == "refine")
{
    var photo = await WicImaging.DecodeSurfaceAsync(await File.ReadAllBytesAsync(args[1]));
    var coarse = await WicImaging.DecodeMaskAsync(await File.ReadAllBytesAsync(args[3]));
    var timings = new List<double>(); var stages = new Dictionary<string, double>(); MaskSurface refined = coarse;
    for (int i = 0; i < 5; i++)
    {
        var watch = Stopwatch.StartNew(); refined = SamEdgeRefinement.Refine(photo, coarse, true, null, default, (stage, ms) => stages[stage] = ms);
        timings.Add(watch.Elapsed.TotalMilliseconds);
    }
    await File.WriteAllBytesAsync(Path.Combine(root, "refined-mask.png"), await WicImaging.EncodePngAsync(refined));
    var preview = photo.Clone();
    for (int y = 0; y < photo.Height; y++) for (int x = 0; x < photo.Width; x++)
        for (int c = 0; c < 4; c++) preview.Pixels[y * preview.Stride + x * 4 + c] = (byte)((photo.Pixels[y * photo.Stride + x * 4 + c] * refined.Pixels[y * photo.Width + x] + 127) / 255);
    await File.WriteAllBytesAsync(Path.Combine(root, "refined-cutout.png"), await WicImaging.EncodePngAsync(preview));
    string evidence = JsonSerializer.Serialize(new { width = photo.Width, height = photo.Height, milliseconds = timings,
        stages, changedPixels = coarse.Pixels.Zip(refined.Pixels).Count(pair => pair.First != pair.Second) });
    await File.WriteAllTextAsync(Path.Combine(root, "refinement-report.json"), evidence); Console.WriteLine(evidence);
    return;
}
var store = new ModelStore(args[0], File.Exists(Path.Combine(args[0], ModelStore.Sam2Files[0].Name)) ? ModelStore.Sam2Files : ModelStore.MobileSamFiles);
await store.EnsureAsync(false, null, default);
var image = await WicImaging.DecodeSurfaceAsync(await File.ReadAllBytesAsync(args[1]));
using var model = new SamSegmenter(store, args.ElementAtOrDefault(3) != "cpu");
var timer = Stopwatch.StartNew();
var result = await model.SelectAsync(image, [new(new(image.Width * .5, image.Height * .5))], null, true, default);
double first = timer.Elapsed.TotalMilliseconds;
await File.WriteAllBytesAsync(Path.Combine(root, "point-mask.png"), await WicImaging.EncodePngAsync(result));
var box = new SelectionBox(image.Width * .1, image.Height * .15, image.Width * .8, image.Height * .7);
timer.Restart(); result = await model.SelectAsync(image, [], box, true, default);
double warm = timer.Elapsed.TotalMilliseconds;
var warmStages = new List<object>();
if (Environment.GetEnvironmentVariable("COMPOSITOR_SELECTION_PROBE_TIMING") == "1")
    for (int i = 0; i < 8; i++)
    {
        timer.Restart(); result = await model.SelectAsync(image, [], box, true, default);
        warmStages.Add(new { totalMs = timer.Elapsed.TotalMilliseconds, model.LastInferenceMilliseconds, model.LastRestoreMilliseconds, model.LastRefineMilliseconds });
    }
await File.WriteAllBytesAsync(Path.Combine(root, "box-mask.png"), await WicImaging.EncodePngAsync(result));
var cutout = image.Clone();
for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++)
    for (int c = 0; c < 4; c++) cutout.Pixels[y * cutout.Stride + x * 4 + c] = (byte)((image.Pixels[y * image.Stride + x * 4 + c] * result.Pixels[y * image.Width + x] + 127) / 255);
await File.WriteAllBytesAsync(Path.Combine(root, "cutout.png"), await WicImaging.EncodePngAsync(cutout));
if (Environment.GetEnvironmentVariable("COMPOSITOR_SELECTION_PROBE_PROMPTS") is { Length: > 0 } promptJson)
{
    var request = ObjectSelectionRequest.Parse(JsonNode.Parse(promptJson)!.AsObject(), image.Width, image.Height);
    result = await model.SelectAsync(image, request.Points, request.Box, true, default);
    await File.WriteAllBytesAsync(Path.Combine(root, "corrected-mask.png"), await WicImaging.EncodePngAsync(result));
    foreach (var point in request.Points)
        if ((result.Pixels[(int)point.Position.Y * image.Width + (int)point.Position.X] >= 128) != point.Include)
            throw new Exception($"Correction ignored the {(point.Include ? "include" : "exclude")} point at {point.Position}.");
}
using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
try { await model.SelectAsync(image, [], box, true, cancelled.Token); throw new Exception("Cancellation failed."); }
catch (OperationCanceledException) { }
if (model.EncoderRuns != 1 || !result.Pixels.Any(value => value > 200) || !result.Pixels.Any(value => value < 20)) throw new Exception("No useful mask or encoder cache not reused.");
var report = new { status = "passed", model.Backend, model.DecoderBackend, image.Width, image.Height, firstMs = first, warmMs = warm, warmStages, model.EncoderRuns, cancellation = "passed" };
string json = JsonSerializer.Serialize(report); await File.WriteAllTextAsync(Path.Combine(root, "report.json"), json); Console.WriteLine(json);
