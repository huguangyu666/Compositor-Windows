#if DEBUG
using Compositor.Core;
using System.Linq;

namespace Compositor.App;

public sealed partial class MainWindow
{
    /// <summary>
    /// C02a: a folder's mask has to be paintable through the real pointer path, and the refusal for
    /// the plane that cannot take paint has to explain itself. Before this slice the brush answered
    /// "painting needs a pixel layer" for folders and adjustment layers on both planes, so a folder
    /// mask rendered on canvas but could never be edited.
    /// </summary>
    private async Task VerifyMaskPaintTargetsAsync(Dictionary<string, object> report)
    {
        var scene = EditorDocument.Create(64, 48);
        var child = scene.ImportImage(new Surface(16, 16), "Child");
        child.Transform = new LayerTransform { Origin = new PointD(4, 4), Size = new SizeD(16, 16) };
        var folder = scene.GroupLayers([child.Id])!;
        var white = new MaskSurface(16, 16);
        Array.Fill(white.Pixels, (byte)255);
        Require(scene.AddRasterMask(folder.Id, white), "the folder fixture did not attach a mask");
        OpenDocument(scene); await WaitForFramesAsync();
        LayerList.SelectedItem = layerRows[folder.Id];
        ToolCombo.SelectedIndex = (int)CanvasTool.Brush;
        await WaitForTransformStateAsync(() => tool == CanvasTool.Brush && toolChangeApplied == toolChangeRevision);
        await WaitForFramesAsync();

        HandleThumbnailClick(folder.Id, true, Windows.System.VirtualKeyModifiers.None); await WaitForFramesAsync();
        Require(paintOnMask, "The folder's mask thumbnail did not become the paint target.");
        var center = folder.Transform.Center;
        BeginPointerStroke(new PointD(center.X, center.Y));
        AppendPointerStroke(new PointD(center.X + 3, center.Y + 2));
        FinishPointerStroke(new PointD(center.X + 4, center.Y + 3));
        await WaitForFramesAsync();
        var painted = ((Dictionary<Guid, MaskSurface>)scene.Snapshot.Masks)[folder.Id];
        Require(painted.Pixels.Any(level => level < 255), "painting the folder mask left it untouched");
        Require(scene.Undo() && ((Dictionary<Guid, MaskSurface>)scene.Snapshot.Masks)[folder.Id].Pixels.All(level => level == 255),
            "the folder stroke did not revert as one step");
        report["folderMaskTakesBrushPaint"] = "passed";

        // C02c: over a layer that already has a mask the add-mask commands are not offered, and the model
        // still refuses if a caller reaches around the UI — the mask painted above has to survive both.
        Require(!AddMaskMenuItem.IsEnabled && !AddHideAllMaskMenuItem.IsEnabled
                && !RevealSelectionMaskMenuItem.IsEnabled && !HideSelectionMaskMenuItem.IsEnabled
                && !MaskWorkspaceButton.IsEnabled,
            "an add-mask affordance stayed enabled over a layer that already has a mask.");
        var maskBytesBefore = ((Dictionary<Guid, MaskSurface>)scene.Snapshot.Masks)[folder.Id].Pixels.ToArray();
        OnAddMask(this, new Microsoft.UI.Xaml.RoutedEventArgs()); await WaitForFramesAsync();
        Require(((Dictionary<Guid, MaskSurface>)scene.Snapshot.Masks)[folder.Id].Pixels.SequenceEqual(maskBytesBefore),
            "invoking Add Mask replaced the mask that was already there.");
        Require(StatusText.Text == Loc.F("S_HasMask", folder.Name),
            $"Add Mask over a masked layer reported \"{StatusText.Text}\" instead of the refusal.");
        report["maskAddRefusedAndDisabled"] = "passed";

        // The image plane of the same folder must refuse, and say which plane to use instead.
        HandleThumbnailClick(folder.Id, false, Windows.System.VirtualKeyModifiers.None); await WaitForFramesAsync();
        Require(!paintOnMask, "Returning to the folder's image target failed.");
        var untouched = ((Dictionary<Guid, MaskSurface>)scene.Snapshot.Masks)[folder.Id];
        BeginPointerStroke(new PointD(center.X - 5, center.Y - 5));
        FinishPointerStroke(new PointD(center.X - 4, center.Y - 4));
        await WaitForFramesAsync();
        Require(StatusText.Text == Loc.F("S_PaintFolderHasNoPixels", folder.Name),
            $"the folder refusal did not name the mask as the target: \"{StatusText.Text}\"");
        Require(ReferenceEquals(untouched, ((Dictionary<Guid, MaskSurface>)scene.Snapshot.Masks)[folder.Id])
            && untouched.Pixels.All(level => level == 255), "the refused folder stroke still wrote the mask");
        report["folderImagePaintRefusalExplainsMaskTarget"] = "passed";
    }
}
#endif
