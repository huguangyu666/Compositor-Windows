using Compositor.Core;
using Compositor.Renderer;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

// Read-only replay of a saved project's layer moves. Includes CPU composition and BGRA
// staging; pointer dispatch and GPU upload are measured separately by the App probe.
if (args.Length < 3) throw new ArgumentException("Pass a .comp project directory, report path and moving layer UUID.");
var source = await ProjectStore.LoadAsync(args[0]);
var moving = source.Manifest.Layers.Single(layer => layer.Id == Guid.Parse(args[2]));
if (moving.ImageFile is null) throw new ArgumentException("Select the image layer being dragged.");
source.Manifest.ActiveLayerId = moving.Id;
var adjustments = source.Manifest.Layers.Where(layer => layer.Adjustment is not null && layer.IsVisible).ToArray();
var rows = new List<object>();
foreach (bool interactive in new[] { false, true })
for (int count = 0; count <= adjustments.Length; count++)
{
    var scene = Copy();
    for (int i = count; i < adjustments.Length; i++) scene.Manifest.Layers.Single(layer => layer.Id == adjustments[i].Id).IsVisible = false;
    var layer = scene.Manifest.Layers.Single(layer => layer.Id == moving.Id);
    var origin = layer.Transform.Origin;
    var compositor = new CanvasCompositor(scene);
    compositor.Render();
    byte[] staging = new byte[source.Manifest.Width * source.Manifest.Height * 4];
    int tick = 0;
    var frame = Measure(() =>
    {
        layer.Transform.Origin = new(origin.X + (++tick % 11) * 3.25, origin.Y + (tick % 7) * 2.5);
        compositor.SetSnapshot(new() { Manifest = ManifestJson.Deserialize(ManifestJson.Serialize(scene.Manifest)), Images = scene.Images, Masks = scene.Masks });
        var canvas = compositor.Render(0, 0, source.Manifest.Width, source.Manifest.Height, interactive);
        SurfacePresentation.FillBgra(canvas, staging, canvas.Width, 0, 0, canvas.Width, canvas.Height);
    });
    rows.Add(new { interactive, adjustments = count, addedKind = count == 0 ? "none" : adjustments[count - 1].Adjustment!.Kind.ToString(), frame,
        interactiveHash = Convert.ToHexString(SHA256.HashData(staging)),
        settledHash = Convert.ToHexString(SHA256.HashData(compositor.Render().Pixels)) });
}
var standalone = new List<object>();
foreach (var adjustment in adjustments)
{
    var input = new CanvasCompositor(source, cacheLayers: false).Render().Clone();
    var pixels = input.Pixels.ToArray();
    var times = Measure(() =>
    {
        pixels.CopyTo(input.Pixels, 0);
        AdjustmentEngine.Apply(adjustment.Adjustment!, input, 0, 0, input.Width, input.Height);
    });
    standalone.Add(new { kind = adjustment.Adjustment!.Kind.ToString(), times,
        hash = Convert.ToHexString(SHA256.HashData(input.Pixels)) });
}
string json = JsonSerializer.Serialize(new { project = Path.GetFileName(args[0]), movingLayer = moving.Id,
    sourceSize = new { source.Images[moving.Id].Width, source.Images[moving.Id].Height }, source.Manifest.Width, source.Manifest.Height,
    processorCount = Environment.ProcessorCount, rows, standalone }, new JsonSerializerOptions { WriteIndented = true });
if (args.Length > 1) File.WriteAllText(args[1], json);
Console.WriteLine(json);

ProjectSnapshot Copy() => new() { Manifest = ManifestJson.Deserialize(ManifestJson.Serialize(source.Manifest)), Images = source.Images, Masks = source.Masks };

static object Measure(Action operation)
{
    for (int i = 0; i < 3; i++) operation();
    var times = new double[9]; var bytes = new long[times.Length];
    for (int i = 0; i < times.Length; i++)
    {
        long before = GC.GetTotalAllocatedBytes(precise: true);
        long start = Stopwatch.GetTimestamp(); operation();
        times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        bytes[i] = GC.GetTotalAllocatedBytes(precise: true) - before;
    }
    Array.Sort(times); Array.Sort(bytes);
    return new { medianMs = times[4], p95Ms = times[^1], medianAllocatedBytes = bytes[4], samples = times.Length };
}
