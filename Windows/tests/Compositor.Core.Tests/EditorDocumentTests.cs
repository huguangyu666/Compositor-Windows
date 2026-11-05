using Xunit;

namespace Compositor.Core.Tests;

public class EditorDocumentTests
{
    private static Surface Solid(int width, int height, byte r, byte g, byte b)
    {
        var surface = new Surface(width, height);
        for (int row = 0; row < height; row++)
        {
            for (int column = 0; column < width; column++)
            {
                int index = row * surface.Stride + column * 4;
                surface.Pixels[index] = r;
                surface.Pixels[index + 1] = g;
                surface.Pixels[index + 2] = b;
                surface.Pixels[index + 3] = 255;
            }
        }
        return surface;
    }

    [Fact]
    public void CreateMakesAWhiteBackgroundLayer()
    {
        var document = EditorDocument.Create(64, 32);

        Assert.Equal(1, document.LayerCount);
        var layer = document.Snapshot.Manifest.Layers[0];
        Assert.Equal("Background", layer.Name);
        Assert.Equal(64, layer.Transform.Size.Width);
        Assert.True(document.Snapshot.Images[layer.Id].Pixels[0] > 250);
    }

    [Fact]
    public void ImportImageAddsALayerAndUndoRemovesIt()
    {
        var document = EditorDocument.Create(16, 16);
        document.ImportImage(Solid(8, 8, 255, 0, 0), "Red");

        Assert.Equal(2, document.LayerCount);
        Assert.Equal("Red", document.Snapshot.Manifest.Layers[^1].Name);
        Assert.True(document.IsDirty);

        Assert.True(document.Undo());
        Assert.Equal(1, document.LayerCount);

        Assert.True(document.Redo());
        Assert.Equal(2, document.LayerCount);
        Assert.Equal("Red", document.Snapshot.Manifest.Layers[^1].Name);
    }

    [Fact]
    public void ImportedImageGetsAUniqueName()
    {
        var document = EditorDocument.Create(16, 16);
        document.ImportImage(Solid(4, 4, 0, 0, 0), "Photo");
        document.ImportImage(Solid(4, 4, 0, 0, 0), "Photo");

        Assert.Equal("Photo", document.Snapshot.Manifest.Layers[1].Name);
        Assert.Equal("Photo 2", document.Snapshot.Manifest.Layers[2].Name);
    }

    [Fact]
    public void UndoRestoresReplacedPixels()
    {
        var document = EditorDocument.Create(4, 4);
        var id = document.Snapshot.Manifest.Layers[0].Id;
        document.ReplaceLayerImage(id, Solid(4, 4, 0, 255, 0));

        var replaced = document.Snapshot.Images[id];
        Assert.Equal(0, replaced.Pixels[0]);
        Assert.Equal(255, replaced.Pixels[1]);

        document.Undo();
        Assert.Equal(255, document.Snapshot.Images[id].Pixels[0]);
        Assert.Equal(255, document.Snapshot.Images[id].Pixels[1]);
    }

    [Fact]
    public void CoalescedPixelReplacementsOnlyRefreshPixels()
    {
        var document = EditorDocument.Create(4, 4);
        var id = document.Snapshot.Manifest.Layers[0].Id;
        int changed = 0, pixelsChanged = 0;
        document.Changed += () => changed++;
        document.PixelsChanged += () => pixelsChanged++;

        var sessionKey = new object();
        document.ReplaceLayerImage(id, Solid(4, 4, 255, 0, 0), sessionKey);
        document.ReplaceLayerImage(id, Solid(4, 4, 0, 255, 0), sessionKey);
        document.ReplaceLayerImage(id, Solid(4, 4, 0, 0, 255), sessionKey);

        // The first keyed call opens the undo step; the joined ones only move pixels.
        Assert.Equal(1, changed);
        Assert.Equal(2, pixelsChanged);
    }

    [Fact]
    public void CameraRawPreviewAndApplyRevertAsOneStep()
    {
        var document = EditorDocument.Create(4, 4);
        var id = document.Snapshot.Manifest.Layers[0].Id;
        var sessionKey = new object();

        // Live preview ticks and the committed grade share the session's undo step.
        document.ReplaceLayerImage(id, Solid(4, 4, 255, 0, 0), sessionKey);
        document.ReplaceLayerImage(id, Solid(4, 4, 255, 0, 0), sessionKey);
        document.EndTransform(sessionKey);

        Assert.True(document.Undo());
        Assert.Equal(255, document.Snapshot.Images[id].Pixels[0]);
        Assert.Equal(255, document.Snapshot.Images[id].Pixels[1]); // white background again

        Assert.True(document.Redo());
        Assert.Equal(255, document.Snapshot.Images[id].Pixels[0]);
        Assert.Equal(0, document.Snapshot.Images[id].Pixels[1]); // graded pixels back
    }

    [Fact]
    public void OpacityAndVisibilityRoundTripThroughUndo()
    {
        var document = EditorDocument.Create(4, 4);
        var id = document.Snapshot.Manifest.Layers[0].Id;

        document.SetOpacity(id, 0.25);
        Assert.Equal(0.25, document.Snapshot.Manifest.Layers[0].Opacity);

        document.SetVisible(id, false);
        Assert.False(document.Snapshot.Manifest.Layers[0].IsVisible);

        document.Undo(); // visibility back
        Assert.True(document.Snapshot.Manifest.Layers[0].IsVisible);
        Assert.Equal(0.25, document.Snapshot.Manifest.Layers[0].Opacity);

        document.Undo(); // opacity back
        Assert.Equal(1, document.Snapshot.Manifest.Layers[0].Opacity ?? 1);
    }

    [Fact]
    public void MoveByAndScaleKeepTheCenter()
    {
        var document = EditorDocument.Create(16, 16);
        var id = document.Snapshot.Manifest.Layers[0].Id;

        document.MoveLayerBy(id, 2, 3);
        Assert.Equal(2, document.Snapshot.Manifest.Layers[0].Transform.Origin.X);
        Assert.Equal(3, document.Snapshot.Manifest.Layers[0].Transform.Origin.Y);

        document.ScaleLayer(id, 50);
        var transform = document.Snapshot.Manifest.Layers[0].Transform;
        Assert.Equal(8, transform.Size.Width);
        Assert.Equal(8, transform.Size.Height);
        Assert.Equal(10, transform.Center.X); // the moved box's center, kept by the scale
        Assert.Equal(11, transform.Center.Y);
        Assert.Equal(6, transform.Origin.X); // center 10 − half of 8
        Assert.Equal(7, transform.Origin.Y);
    }

    [Fact]
    public void DeletingAFolderDeletesItsChildren()
    {
        var document = EditorDocument.Create(8, 8);
        document.AddFolder();
        var folder = document.Snapshot.Manifest.Layers[^1];
        var child = document.ImportImage(Solid(4, 4, 0, 0, 255), "Child");
        child.ParentId = folder.Id;

        Assert.True(document.DeleteLayer(folder.Id));
        Assert.Equal(1, document.LayerCount);
    }

    [Fact]
    public void MovingWithinSiblingsSwapsOnlySiblings()
    {
        var document = EditorDocument.Create(8, 8);
        document.AddFolder();
        var folder = document.Snapshot.Manifest.Layers[^1];
        var first = document.ImportImage(Solid(2, 2, 1, 1, 1), "First");
        first.ParentId = folder.Id;
        var second = document.ImportImage(Solid(2, 2, 2, 2, 2), "Second");
        second.ParentId = folder.Id;

        // Children order bottom-to-top: First, Second (indices 2 and 3, after Background and folder).
        Assert.True(document.MoveLayerWithinSiblings(first.Id, towardTop: true));
        Assert.Equal(second.Id, document.Snapshot.Manifest.Layers[2].Id);
        Assert.Equal(first.Id, document.Snapshot.Manifest.Layers[3].Id);

        // The folder cannot swap with a child.
        Assert.False(document.MoveLayerWithinSiblings(folder.Id, towardTop: true));
    }

    [Fact]
    public void SavedClearsDirtyButKeepsHistory()
    {
        var document = EditorDocument.Create(8, 8);
        document.SetOpacity(document.Snapshot.Manifest.Layers[0].Id, 0.5);
        Assert.True(document.IsDirty);

        document.MarkSaved();
        Assert.False(document.IsDirty);
        Assert.True(document.CanUndo);
    }

    [Fact]
    public void RenameLayerRenamesAndUndoRestores()
    {
        var document = EditorDocument.Create(8, 8);
        var layer = document.Snapshot.Manifest.Layers[0];

        Assert.True(document.RenameLayer(layer.Id, "Renamed"));
        Assert.Equal("Renamed", document.Snapshot.Manifest.Layers[0].Name);
        Assert.True(document.IsDirty);

        // Empty or whitespace-only names are refused and change nothing.
        Assert.False(document.RenameLayer(layer.Id, "   "));
        Assert.Equal("Renamed", document.Snapshot.Manifest.Layers[0].Name);

        document.Undo();
        Assert.NotEqual("Renamed", document.Snapshot.Manifest.Layers[0].Name);
    }

    // --- Mask variants: hide-all and from selection ---

    [Fact]
    public void HideAllMaskStartsTheLayerFullyHidden()
    {
        var document = EditorDocument.Create(6, 6);
        var layer = document.Snapshot.Manifest.Layers[0];

        Assert.True(document.AddHideAllMask(layer.Id));
        var mask = document.Snapshot.Masks[layer.Id];
        Assert.Equal(6 * 6, mask.Pixels.Length);
        Assert.All(mask.Pixels, pixel => Assert.Equal(0, pixel));
        Assert.True(layer.MaskEnabled);

        Assert.True(document.Undo());
        Assert.False(document.Snapshot.Masks.ContainsKey(layer.Id));
        Assert.Null(document.Snapshot.Manifest.Layers[0].MaskFile); // manifest is restored by value
    }

    [Fact]
    public void RevealSelectionMaskIsWhiteInsideAndBlackOutside()
    {
        var document = EditorDocument.Create(6, 6);
        var layer = document.Snapshot.Manifest.Layers[0];
        document.SetSelection(Selection.Rectangle(6, 6, 2, 2, 4, 4));

        Assert.True(document.AddSelectionMask(layer.Id, revealing: true));
        var mask = document.Snapshot.Masks[layer.Id];
        Assert.Equal(255, mask.Pixels[2 * 6 + 2]); // first selected pixel
        Assert.Equal(255, mask.Pixels[5 * 6 + 5]); // last selected pixel
        Assert.Equal(0, mask.Pixels[0]); // top-left corner stays black
        Assert.Equal(0, mask.Pixels[1 * 6 + 1]); // just outside the selection
        Assert.Equal(0, mask.Pixels[5 * 6 + 1]);

        Assert.True(document.Undo());
        Assert.False(document.Snapshot.Masks.ContainsKey(layer.Id));
    }

    [Fact]
    public void HideSelectionMaskIsTheInverse()
    {
        var document = EditorDocument.Create(6, 6);
        var layer = document.Snapshot.Manifest.Layers[0];
        document.SetSelection(Selection.Rectangle(6, 6, 2, 2, 4, 4));

        Assert.True(document.AddSelectionMask(layer.Id, revealing: false));
        var mask = document.Snapshot.Masks[layer.Id];
        Assert.Equal(0, mask.Pixels[2 * 6 + 2]); // selection hides
        Assert.Equal(255, mask.Pixels[0]); // the rest reveals
        Assert.Equal(255, mask.Pixels[5 * 6 + 0]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SelectionMaskConsumesSelectionAndRestoresItWithTheMaskUndo(bool revealing)
    {
        var document = EditorDocument.Create(6, 6); var id = document.Snapshot.Manifest.Layers[0].Id;
        var selection = Selection.Rectangle(6, 6, 1, 1, 3, 3); document.SetSelection(selection);
        Assert.True(document.AddSelectionMask(id, revealing));
        Assert.Null(document.ActiveSelection);
        Assert.True(document.Undo()); Assert.False(document.CanUndo);
        Assert.Same(selection, document.ActiveSelection); Assert.False(document.Snapshot.Masks.ContainsKey(id));
        Assert.True(document.Redo()); Assert.Null(document.ActiveSelection); Assert.True(document.Snapshot.Masks.ContainsKey(id));
    }

    [Fact]
    public void SelectionMaskIsRefusedWithoutASelectionOrWhenMasked()
    {
        var document = EditorDocument.Create(6, 6);
        var layer = document.Snapshot.Manifest.Layers[0];

        Assert.False(document.AddSelectionMask(layer.Id, revealing: true)); // nothing selected

        document.SetSelection(Selection.Rectangle(6, 6, 2, 2, 4, 4));
        Assert.True(document.AddHideAllMask(layer.Id));
        Assert.False(document.AddSelectionMask(layer.Id, revealing: true)); // already has a mask
        Assert.False(document.AddHideAllMask(layer.Id));
    }

    [Fact]
    public void SetMaskLinkedTogglesAndUndoRestores()
    {
        var document = EditorDocument.Create(6, 6);
        var layer = document.Snapshot.Manifest.Layers[0];
        document.AddHideAllMask(layer.Id);
        Assert.True(layer.MaskLinked ?? true); // masks start linked

        Assert.True(document.SetMaskLinked(layer.Id, linked: false));
        Assert.False(document.Snapshot.Manifest.Layers[0].MaskLinked);

        Assert.True(document.Undo());
        Assert.True(document.Snapshot.Manifest.Layers[0].MaskLinked ?? true);

        Assert.True(document.SetMaskLinked(layer.Id, linked: true));
        Assert.True(document.Snapshot.Manifest.Layers[0].MaskLinked);
    }

    [Fact]
    public void SetMaskLinkedIsRefusedWithoutAMask()
    {
        var document = EditorDocument.Create(6, 6);
        var layer = document.Snapshot.Manifest.Layers[0];

        Assert.False(document.SetMaskLinked(layer.Id, linked: false));
        Assert.Null(layer.MaskLinked);
    }

    [Fact]
    public void SetMaskEnabledTogglesAndUndoRestores()
    {
        var document = EditorDocument.Create(6, 6);
        var layer = document.Snapshot.Manifest.Layers[0];
        document.AddHideAllMask(layer.Id);
        Assert.True(document.Snapshot.Manifest.Layers[0].MaskEnabled ?? true); // masks start enabled

        Assert.True(document.SetMaskEnabled(layer.Id, enabled: false));
        Assert.False(document.Snapshot.Manifest.Layers[0].MaskEnabled);

        Assert.True(document.Undo());
        Assert.True(document.Snapshot.Manifest.Layers[0].MaskEnabled ?? true);

        Assert.True(document.SetMaskEnabled(layer.Id, enabled: true));
        Assert.True(document.Snapshot.Manifest.Layers[0].MaskEnabled);
    }

    [Fact]
    public void SetMaskEnabledIsRefusedWithoutAMask()
    {
        var document = EditorDocument.Create(6, 6);
        var layer = document.Snapshot.Manifest.Layers[0];

        Assert.False(document.SetMaskEnabled(layer.Id, enabled: false));
        Assert.Null(layer.MaskEnabled);
    }
}
