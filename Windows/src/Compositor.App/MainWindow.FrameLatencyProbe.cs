#if DEBUG
using Compositor.Core;
using Microsoft.UI.Xaml.Media;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private Action<string>? frameTimelineProbe;

    private async Task VerifyOutlineRetirementAsync(Dictionary<string, object> report)
    {
        var mask = new MaskSurface(128, 128); var random = new Random(739);
        for (int i = 0; i < mask.Pixels.Length; i++) mask.Pixels[i] = (byte)(random.Next(2) * 255);
        var original = new Selection(mask, new(0, 0, 128, 128));
        var originalPixels = mask.Pixels.ToArray(); var originalEdges = SelectionContours.Build(original);
        var scene = EditorDocument.Create(256, 256); scene.SetSelection(original);
        OpenDocument(scene); await WaitForFramesAsync(); await WaitForSelectionReadyAsync();
        // Opening the fixture also detaches the preceding document's outline.
        // Drain that cleanup before taking this root's exact figure-count baseline.
        Require(await WaitUntilAsync(() => !retiringOutlines, OutlineDrainBudgetMs), "Earlier outline retirement did not drain.");
        var source = selectionOutlineGeometry!; var warm = compositor; int frames = presentedFrameCount;
        int retiredBefore = retiredOutlineFigures;
        RetireOutline(source);
        Require(!retiredOutlineIdentities.Contains(source), "Retirement accepted the active root.");
        scene.SetSelection(original.Translated(3, 5));
        Require(ReferenceEquals(source, SelectionOutline.Data), "Translation lost the retained contour.");
        RequireOutlineRuns(source, originalEdges);
        var rectangle = Selection.Rectangle(256, 256, 8, 8, 20, 20); scene.SetSelection(rectangle);
        await WaitForSelectionReadyAsync();
        var active = selectionOutlineGeometry;
        RequireOutlineRuns(active, SelectionContours.Build(rectangle));
        Require(await WaitUntilAsync(() => !retiringOutlines, OutlineDrainBudgetMs), "Replaced root retirement did not drain.");
        Require(!SelectionOutlineFigures(source).Any() && retiredOutlineFigures - retiredBefore == originalEdges.Length,
            $"Replaced outline did not release each native figure: empty={!SelectionOutlineFigures(source).Any()}, "
            + $"retired={retiredOutlineFigures - retiredBefore}, expected={originalEdges.Length}.");
        Require(ReferenceEquals(active, SelectionOutline.Data) && ReferenceEquals(warm, compositor) && presentedFrameCount == frames
            && mask.Pixels.AsSpan().SequenceEqual(originalPixels), "Retirement changed active geometry, coverage or composited pixels.");
        report["selectionOutlineRetirementProtectsActiveAndDrainsReplacedRoot"] = "passed";

        retiredBefore = retiredOutlineFigures;
        bool replaced = false;
        beforeOutlineBatch = batch => { if (batch == 8 && !replaced) { replaced = true; scene.SetSelection(rectangle); } };
        try
        {
            scene.SetSelection(original); await WaitForSelectionReadyAsync();
            Require(replaced, "Partial-retirement fixture did not reach its cancellation point.");
            Require(await WaitUntilAsync(() => !retiringOutlines, OutlineDrainBudgetMs), "Partial outline retirement did not drain.");
            Require(retiredOutlineFigures - retiredBefore >= 8 * SelectionOutlineRunsPerBatch,
                "Canceled native outline left its constructed figures outside retirement.");
            RequireOutlineRuns(selectionOutlineGeometry, SelectionContours.Build(rectangle));
        }
        finally { beforeOutlineBatch = null; }
        bool ownerRetired = false; var replacement = EditorDocument.Create(32, 32);
        beforeOutlineBatch = batch => { if (batch == 8 && !ownerRetired) { ownerRetired = true; OpenDocument(replacement); } };
        try
        {
            scene.SetSelection(original); await WaitForSelectionReadyAsync();
            Require(await WaitUntilAsync(() => ownerRetired && !retiringOutlines, OutlineDrainBudgetMs), "Retired-owner outlines did not drain.");
            Require(ReferenceEquals(document, replacement) && SelectionOutline.Data is null && !replacement.CanUndo
                && retiredOutlineRoots.Count == 0 && retiredOutlineIdentities.Count == 0,
                "Old outline retirement changed replacement state or retained its roots.");
        }
        finally { beforeOutlineBatch = null; }
        report["selectionOutlineRetirementCoversPartialBuildAndRetiredOwner"] = "passed";
    }
}
#endif
