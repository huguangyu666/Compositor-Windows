using Xunit;

namespace Compositor.Core.Tests;

public class TransformPressTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndependentMaskRetainsActiveAbovePixelsUnderItsEditedGeometry(bool rotated)
    {
        var (source, bottom, active) = Scene();
        var maskPlacement = new LayerTransform { Origin = new(55, 35), Size = new(30, 20), Rotation = rotated ? 37 : 0 };
        var result = TransformPress.Pick(source, maskPlacement.Center, active, maskPlacement, false, false, true, maskTarget: true);
        Assert.Equal(new TransformPressResult(active, false), result);
        Assert.NotEqual(bottom, result!.LayerId);
    }

    [Fact]
    public void AForegroundAboveTheIndependentMaskWinsAutoSelect()
    {
        var (source, bottom, active) = Scene();
        source.Manifest.Layers.Reverse();
        var maskPlacement = new LayerTransform { Origin = new(55, 35), Size = new(30, 20) };
        Assert.Equal(new TransformPressResult(bottom, true), TransformPress.Pick(source, maskPlacement.Center, active, maskPlacement, false, false, true, maskTarget: true));
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public void ControlWithOrWithoutShiftExplicitlyPicksBehindTheMask(bool autoSelect, bool control, bool shift)
    {
        var (source, bottom, active) = Scene(); var mask = new LayerTransform { Origin = new(55, 35), Size = new(30, 20) };
        Assert.Equal(new TransformPressResult(bottom, true), TransformPress.Pick(source, mask.Center, active, mask, false, false, autoSelect, control, shift));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    public void AGroupKeepsItsBoxUnlessAnExplicitPickOverridesIt(bool autoSelect, bool control, bool editing)
    {
        var (source, _, active) = Scene(); var folder = Guid.NewGuid();
        source.Manifest.Layers.Add(new() { Id = folder, IsGroup = true, Name = "Folder", IsVisible = true, Transform = new() { Size = new(100, 80) } });
        var box = new LayerTransform { Origin = new(40, 30), Size = new(50, 40) };
        Assert.Equal(new TransformPressResult(folder, false), TransformPress.Pick(source, box.Center, folder, box, true, editing, autoSelect, control));
        Assert.NotEqual(active, folder);
    }

    [Fact]
    public void EmptyCanvasCanDragAnActiveFolderOutsideItsBox()
    {
        var (source, _, _) = Scene(); var folder = Guid.NewGuid();
        source.Manifest.Layers.Add(new() { Id = folder, IsGroup = true, Name = "Folder", IsVisible = true, Transform = new() { Size = new(100, 80) } });
        Assert.Equal(new TransformPressResult(folder, false), TransformPress.Pick(source, new(110, 95), folder, new() { Size = new(20, 20) }, true, false, true));
    }

    [Fact]
    public void AHiddenActivePixelIsNotDraggedOnEmptyCanvas()
    {
        var (source, _, active) = Scene(); source.Manifest.Layers[1].IsVisible = false;
        Assert.Null(TransformPress.Pick(source, new(110, 95), active, new() { Size = new(120, 100) }, false, false, false));
    }

    [Fact]
    public void ExistingDraftCannotAutoSelectAVisibleForeground()
    {
        var (source, bottom, active) = Scene();
        Assert.Equal(new TransformPressResult(active, false), TransformPress.Pick(source, new(60, 40), active, new() { Size = new(10, 10) }, false, true, true, true, true));
        Assert.NotEqual(active, bottom);
    }

    [Fact]
    public void LeavingMaskGeometryCanPickALowerLayer()
    {
        var (source, bottom, active) = Scene();
        Assert.Equal(new TransformPressResult(bottom, true), TransformPress.Pick(source, new(80, 65), active, new() { Origin = new(55, 35), Size = new(30, 20) }, false, false, true));
    }

    private static (ProjectSnapshot Source, Guid Bottom, Guid Active) Scene()
    {
        var doc = EditorDocument.Create(120, 100); var bottom = doc.Snapshot.Manifest.Layers[0]; bottom.Transform.Size = new(100, 80);
        var active = Guid.NewGuid(); doc.Snapshot.Manifest.Layers.Add(new() { Id = active, Name = "Active", IsVisible = true,
            ImageFile = ProjectValidator.Uuid(active) + ".png", Transform = new() { Size = new(20, 20) } });
        ((Dictionary<Guid, Surface>)doc.Snapshot.Images)[active] = new(20, 20);
        return (doc.Snapshot, bottom.Id, active);
    }
}
