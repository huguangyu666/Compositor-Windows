using Compositor.Core;
using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// Cross-project layer copy (ImportLayersFrom), ported from the Mac's copyLayers
/// (ProjectWorkspace.swift:213-243): names kept, folders carried whole, one layer landing with its own
/// center at the drop point and several with the center of their union, clip bases left behind baked or
/// dropped, one undoable step on the receiving document, and a refusal that touches nothing.
/// </summary>
public class LayerProjectCopyTests
{
    private static (EditorDocument doc, ProjectLayerRecord layer) DocWithPixel(int width = 8, int height = 8,
        byte red = 200, byte green = 10, byte blue = 10, byte alpha = 255)
    {
        var doc = EditorDocument.Create(64, 64);
        var image = new Surface(width, height);
        for (int i = 0; i < image.Pixels.Length; i += 4)
        {
            image.Pixels[i] = red; image.Pixels[i + 1] = green; image.Pixels[i + 2] = blue; image.Pixels[i + 3] = alpha;
        }
        return (doc, doc.ImportImage(image, "Payload"));
    }

    [Fact]
    public void ASingleCopiedLayerLandsWithItsCenterOnTheDropPoint()
    {
        var (source, layer) = DocWithPixel();
        layer.Transform.Origin = new PointD(10, 10);           // center at (14, 14)
        var (target, existing) = DocWithPixel(red: 5, green: 5, blue: 5);
        var existingPixels = target.Snapshot.Images[existing.Id];
        var startCount = target.LayerCount;
        var mapping = target.ImportLayersFrom(source, [layer.Id], new PointD(40, 20))!;
        var copy = target.Snapshot.Manifest.Layers.Single(candidate => candidate.Id == mapping[layer.Id]);

        Assert.Equal("Payload", copy.Name);                    // kept: this is a move between projects
        Assert.NotEqual(layer.Id, copy.Id);
        Assert.Equal(layer.Transform.Size, copy.Transform.Size);
        Assert.Equal(new PointD(40 - 4, 20 - 4), copy.Transform.Origin);   // center exactly on the point
        Assert.Equal(copy.Id, target.Snapshot.Manifest.ActiveLayerId);     // the Mac: active = mapping[id]
        Assert.Same(source.Snapshot.Images[layer.Id], target.Snapshot.Images[copy.Id]);
        Assert.NotEqual(layer.ImageFile, copy.ImageFile);                  // assets get the copy's own name
        Assert.Equal(startCount + 1, target.LayerCount);
        Assert.True(target.Undo());                                        // one step
        Assert.Equal(startCount, target.LayerCount);
        Assert.Same(existingPixels, target.Snapshot.Images[existing.Id]);  // its pixels, instance for instance
    }

    [Fact]
    public void SeveralLayersLandTogetherKeepingTheirOffsetsAroundTheUnionCenter()
    {
        var (source, first) = DocWithPixel();
        first.Transform.Origin = new PointD(0, 0);              // rect 0…8
        var second = source.ImportImage(new Surface(8, 8), "Second");
        second.Transform.Origin = new PointD(32, 0);            // rect 32…40; union center (20, 4)
        var target = EditorDocument.Create(64, 64);
        var mapping = target.ImportLayersFrom(source, [first.Id, second.Id], new PointD(50, 30))!;

        var copyOfFirst = target.Snapshot.Manifest.Layers.Single(l => l.Id == mapping[first.Id]);
        var copyOfSecond = target.Snapshot.Manifest.Layers.Single(l => l.Id == mapping[second.Id]);
        Assert.Equal(new PointD(30, 26), copyOfFirst.Transform.Origin);    // (0,0) + (50-20, 30-4)
        Assert.Equal(new PointD(62, 26), copyOfSecond.Transform.Origin);   // the 32 px gap kept
        Assert.Equal(mapping[first.Id], target.Snapshot.Manifest.ActiveLayerId);
    }

    [Fact]
    public void AFolderAndItsChildrenArriveAsOneTree()
    {
        var source = EditorDocument.Create(64, 64);
        var child = source.ImportImage(new Surface(8, 8), "Child");
        child.Transform.Origin = new PointD(2, 2);
        var folder = Assert.IsType<ProjectLayerRecord>(source.GroupLayers(child.Id));   // the real group shape
        var target = EditorDocument.Create(64, 64);
        var before = target.LayerCount;

        var mapping = target.ImportLayersFrom(source, [folder.Id], new PointD(30, 30))!;
        Assert.Equal(2, mapping.Count);                                     // the subtree rides along
        var copyOfFolder = target.Snapshot.Manifest.Layers.Single(l => l.Id == mapping[folder.Id]);
        var copyOfChild = target.Snapshot.Manifest.Layers.Single(l => l.Id == mapping[child.Id]);
        Assert.True(copyOfFolder.IsGroupOrFalse);
        Assert.Equal(copyOfFolder.Id, copyOfChild.ParentId);
        Assert.True(target.LayerCount == before + 2);
        ProjectValidator.Validate(target.Snapshot.Manifest);
    }

    [Fact]
    public void AttachedMasksRideTheCopyAndSeparatelyPlacedOnesShiftWithTheLayer()
    {
        var (source, layer) = DocWithPixel();
        source.ReplaceLayerMask(layer.Id, new MaskSurface(8, 8, [.. Enumerable.Repeat<byte>(128, 64)]));
        layer.MaskPlacement = new LayerTransform { Origin = new PointD(1, 2), Size = new SizeD(8, 8) };
        var target = EditorDocument.Create(64, 64);
        // The layer is at (0,0) 8×8, so its center (4,4) is the anchor and the drop at (20,20) shifts everything by (16,16).
        var mapping = target.ImportLayersFrom(source, [layer.Id], new PointD(20, 20))!;
        var copy = target.Snapshot.Manifest.Layers.Single(l => l.Id == mapping[layer.Id]);

        Assert.NotNull(copy.MaskFile);
        Assert.NotEqual(layer.MaskFile, copy.MaskFile);
        Assert.Equal(new PointD(16, 16), copy.Transform.Origin);                    // (0,0) + the shift
        Assert.Equal(new PointD(17, 18), copy.MaskPlacement!.Origin);               // (1,2) + the SAME shift (:229)
        Assert.Equal(layer.MaskPlacement!.Size, copy.MaskPlacement!.Size);
        Assert.Same(source.Snapshot.Masks[layer.Id], target.Snapshot.Masks[copy.Id]);
    }

    [Fact]
    public void AClippedLayerWhoseBaseStayedBehindArrivesWithTheClipBakedIn()
    {
        var source = EditorDocument.Create(64, 64);
        var baseImage = new Surface(16, 8);
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 16; x++)
            {
                int at = y * baseImage.Stride + x * 4;
                baseImage.Pixels[at + 3] = (byte)(x < 8 ? 0 : 255);     // left half transparent
            }
        var clipBase = source.ImportImage(baseImage, "Base");
        clipBase.Transform.Origin = new PointD(0, 0);
        var clipped = source.ImportImage(new Surface(16, 8, [.. Enumerable.Repeat<byte>(255, 16 * 8 * 4)], 64), "Clipped");
        clipped.Transform.Origin = new PointD(0, 0);
        clipped.MaskSourceId = clipBase.Id;                              // live clip inside the source
        var target = EditorDocument.Create(64, 64);

        var mapping = target.ImportLayersFrom(source, [clipped.Id], new PointD(32, 32))!;   // base NOT requested
        var copy = target.Snapshot.Manifest.Layers.Single(l => l.Id == mapping[clipped.Id]);
        Assert.Null(copy.MaskSourceId);                                  // unlinked, as the Mac: :242
        var pixels = target.Snapshot.Images[copy.Id];
        Assert.Equal(0, pixels.Pixels[(4 * pixels.Stride + 2 * 4 + 3)]);          // the left half came in invisible
        Assert.Equal(255, pixels.Pixels[(4 * pixels.Stride + 12 * 4 + 3)]);       // the right half stayed as it was seen
    }

    [Fact]
    public void AClippedLayerCopiedWithItsBaseKeepsTheClipOnTheCopies()
    {
        var source = EditorDocument.Create(64, 64);
        var clipBase = source.ImportImage(new Surface(8, 8), "Base");
        var clipped = source.ImportImage(new Surface(8, 8), "Clipped");
        clipped.MaskSourceId = clipBase.Id;
        var target = EditorDocument.Create(64, 64);

        var mapping = target.ImportLayersFrom(source, [clipped.Id, clipBase.Id])!;
        var copy = target.Snapshot.Manifest.Layers.Single(l => l.Id == mapping[clipped.Id]);
        var copyBase = target.Snapshot.Manifest.Layers.Single(l => l.Id == mapping[clipBase.Id]);
        Assert.Equal(copyBase.Id, copy.MaskSourceId);                    // the chain remaps onto the copies
    }

    [Fact]
    public void RefusalsChangeTheReceivingDocumentNothingAtAll()
    {
        var (source, layer) = DocWithPixel();
        var target = EditorDocument.Create(64, 64);                        // pristine: no fixture history
        var own = target.Snapshot.Manifest.ActiveLayerId ?? target.Snapshot.Manifest.Layers[0].Id;
        var before = ManifestJson.Serialize(target.Snapshot.Manifest);
        var snapshot = target.Snapshot;

        Assert.Null(target.ImportLayersFrom(target, [own]));               // the Mac's destination == source
        Assert.Null(target.ImportLayersFrom(source, [Guid.NewGuid()]));  // head id the source does not hold
        Assert.Null(target.ImportLayersFrom(source, []));
        // A virtual counter in the RECEIVING document makes the copy over budget without allocating a
        // budget-sized buffer (the used side of the Mac's guard, :226).
        var assets = (Dictionary<Guid, Surface>)target.Snapshot.Images;
        var ghost = Guid.NewGuid();
        assets[ghost] = new Surface((int)Limits.DocumentPixelBudget, 1, [], 0);
        var counted = target.ImportLayersFrom(source, [layer.Id]);
        assets.Remove(ghost);
        Assert.Null(counted);

        Assert.Same(snapshot, target.Snapshot);
        Assert.Equal(before, ManifestJson.Serialize(target.Snapshot.Manifest));
        Assert.False(target.CanUndo); Assert.False(target.IsDirty);
    }
}
