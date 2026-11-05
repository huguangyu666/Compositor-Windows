#if DEBUG
using Compositor.Core;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task VerifyOutlineMetricsOwnershipAsync(Dictionary<string, object> report)
    {
        var scene = EditorDocument.Create(128, 128); OpenDocument(scene); await WaitForFramesAsync();
        var mask = new MaskSurface(64, 64); var random = new Random(419);
        for (int i = 0; i < mask.Pixels.Length; i++) mask.Pixels[i] = (byte)(random.Next(2) * 255);
        var noisy = new Selection(mask, new(0, 0, 64, 64));
        var rectangle = Selection.Rectangle(128, 128, 8, 8, 20, 20);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        afterOutlineExtractionProbe = selection =>
        {
            if (!ReferenceEquals(selection, noisy)) return Task.CompletedTask;
            entered.TrySetResult(); return release.Task;
        };
        outlineBuildCompletedProbe = selection => { if (ReferenceEquals(selection, noisy)) finished.TrySetResult(); };
        try
        {
            ResetOutlineBuildProbe(); scene.SetSelection(noisy);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            scene.SetSelection(rectangle); await WaitForSelectionReadyAsync();
            var root = selectionOutlineGeometry;
            RequireOutlineRuns(root, SelectionContours.Build(rectangle));
            var before = OutlineBuildProbe;
            release.TrySetResult(); await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var after = OutlineBuildProbe;
            int actualRuns = SelectionOutlineFigures(SelectionOutline.Data).Count();
            report["outlineMetricsAfterSupersededExtraction"] = new { before, after, actualRuns,
                retainedRoot = ReferenceEquals(root, SelectionOutline.Data) };
            Require(actualRuns == 4 && ReferenceEquals(root, SelectionOutline.Data), "A superseded extraction replaced the visible rectangle.");
            Require(before.Runs == 4 && after == before,
                $"Superseded extraction overwrote published outline statistics: before={before}, after={after}, actualRuns={actualRuns}.");
        }
        finally
        {
            afterOutlineExtractionProbe = null; outlineBuildCompletedProbe = null; release.TrySetResult();
        }
        report["outlineMetricsRemainOwnedByPublishedBuild"] = "passed";
    }
}
#endif
