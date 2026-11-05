#if DEBUG
using Compositor.Core;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task<object> MeasureMixedAlphaFramesAsync()
    {
        const int side = 2048;
        var scene = EditorDocument.Create(side, side); var image = new Surface(side, side);
        for (int y = 0; y < side; y++)
        for (int x = 0; x < side; x++)
        {
            int i = (y * side + x) * 4; byte alpha = (byte)((x * 13 + y * 7) & 255);
            image.Pixels[i] = (byte)(alpha / 2); image.Pixels[i + 1] = (byte)(alpha / 3);
            image.Pixels[i + 2] = (byte)(alpha / 4); image.Pixels[i + 3] = alpha;
        }
        scene.ImportImage(image, "Mixed alpha"); OpenDocument(scene); await WaitForFramesAsync();
        var original = scene.Snapshot; bool previous = PremultipliedOver.DebugMixedVectors;
        var oldComposition = new List<double>(); var newComposition = new List<double>();
        var oldTicks = new List<double>(); var newTicks = new List<double>();
        try
        {
            for (int sample = -2; sample < 7; sample++)
            {
                byte[]? reference = null, actual = null;
                async Task Render(bool vectors)
                {
                    // Change the probe-only mode between completed readers, never during a draw.
                    await WaitForFramesAsync(); Volatile.Write(ref PremultipliedOver.DebugMixedVectors, vectors);
                    int before = presentedFrameCount; RenderCanvas(); await WaitForFramesAsync();
                    Require(presentedFrameCount > before, "Mixed-alpha measurement did not publish a fresh frame.");
                    var bytes = fullTarget!.GetPixelBytes(0, 0, side, side);
                    if (vectors) actual = bytes; else reference = bytes;
                    if (sample < 0) return;
                    (vectors ? newComposition : oldComposition).Add(lastFrameProbe.CompositionMs);
                    (vectors ? newTicks : oldTicks).Add(lastFrameProbe.InputMs + lastFrameProbe.CpuMs + lastFrameProbe.UploadMs);
                }
                if (sample % 2 == 0) { await Render(false); await Render(true); }
                else { await Render(true); await Render(false); }
                Require(reference!.AsSpan().SequenceEqual(actual), "Integer mixed-alpha drawing changed the uploaded BGRA pixels.");
            }
            Require(ReferenceEquals(original, scene.Snapshot), "Mixed-alpha measurement changed document content.");
            AssertUploadedFrame(scene);
            return new { side, samples = oldComposition.Count, originalCompositionMs = Median(oldComposition),
                vectorCompositionMs = Median(newComposition), originalTickMs = Median(oldTicks), vectorTickMs = Median(newTicks),
                gpuBytesIdentical = true };
        }
        finally { Volatile.Write(ref PremultipliedOver.DebugMixedVectors, previous); }

        static double Median(List<double> values) { values.Sort(); return values[values.Count / 2]; }
    }
}
#endif
