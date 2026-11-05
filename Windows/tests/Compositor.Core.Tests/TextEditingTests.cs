using Xunit;

namespace Compositor.Core.Tests;

public class TextEditingTests
{
    private static LayerTextStyle Style(string content, double size = 24) => new()
    {
        Content = content,
        FontName = "Not-A-Real-Font",
        FontSize = size,
        Red = 1,
    };

    [Fact]
    public void HitTestingUsesTheTopVisibleTextIncludingFolderVisibility()
    {
        var document = EditorDocument.Create(64, 64);
        var lower = document.AddTextLayer(Style("Lower"), PointD.Zero)!;
        var upper = document.AddTextLayer(Style("Upper"), PointD.Zero)!;
        var point = new PointD(20, 20);
        Assert.Equal(upper.Id, document.TextLayerAt(point)!.Id);
        upper.IsVisible = false;
        Assert.Equal(lower.Id, document.TextLayerAt(point)!.Id);
        upper.IsVisible = true;
        var folder = new ProjectLayerRecord { Id = Guid.NewGuid(), IsGroup = true, IsVisible = false };
        document.Snapshot.Manifest.Layers.Add(folder);
        upper.ParentId = folder.Id;
        Assert.Equal(lower.Id, document.TextLayerAt(point)!.Id);
        Assert.Null(document.TextLayerAt(new PointD(-100, -100)));
        Assert.Null(document.TextLayerAt(new PointD(double.NaN, 0)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PixelEditsRetireTextMetadataAndUndoRestoresEditability(bool cut)
    {
        var document = EditorDocument.Create(64, 64);
        var layer = document.AddTextLayer(Style("Paint me"), PointD.Zero)!;
        var original = document.Snapshot.Images[layer.Id];
        if (cut)
        {
            document.SetSelection(Selection.Rectangle(64, 64, 0, 0, 32, 32));
            Assert.NotNull(document.CutSelection(layer.Id));
        }
        else Assert.True(document.ReplaceLayerImage(layer.Id, original.Clone()));
        Assert.Null(layer.Text);
        Assert.Null(document.TextLayerAt(new PointD(20, 20)));
        Assert.False(document.UpdateTextLayer(layer.Id, Style("Do not overwrite paint")));

        Assert.True(document.Undo());
        Assert.Equal("Paint me", document.Snapshot.Manifest.Layers.Single(record => record.Id == layer.Id).Text!.Content);
        Assert.Same(original, document.Snapshot.Images[layer.Id]);
        Assert.Equal(layer.Id, document.TextLayerAt(new PointD(20, 20))!.Id);
    }

    [Fact]
    public void UndoAndRedoRestorePixelsStyleAndTransformAsOneStep()
    {
        var document = EditorDocument.Create(64, 64);
        var originalStyle = Style("Original");
        var layer = document.AddTextLayer(originalStyle, new PointD(7, 11))!;
        var original = document.Snapshot.Images[layer.Id];
        var oldTransform = layer.Transform;
        var updatedStyle = Style("Edited", 40);
        string? observedContent = null;
        int changes = 0;
        document.Changed += () => { changes++; observedContent = layer.Text!.Content; };
        Assert.True(document.UpdateTextLayer(layer.Id, updatedStyle));
        Assert.Equal(1, changes);
        Assert.Equal("Edited", observedContent);
        var updated = document.Snapshot.Images[layer.Id];
        var updatedTransform = layer.Transform;

        Assert.True(document.Undo());
        var restored = document.Snapshot.Manifest.Layers.Single(record => record.Id == layer.Id);
        Assert.Equal(originalStyle, restored.Text);
        Assert.Equal(oldTransform, restored.Transform);
        Assert.Same(original, document.Snapshot.Images[layer.Id]);
        Assert.True(document.Redo());
        restored = document.Snapshot.Manifest.Layers.Single(record => record.Id == layer.Id);
        Assert.Equal(updatedStyle, restored.Text);
        Assert.Equal(updatedTransform, restored.Transform);
        Assert.Same(updated, document.Snapshot.Images[layer.Id]);
    }

    [Fact]
    public void UnchangedOrInvalidStylesKeepTheDocumentCleanAndDoNotConsumeRedo()
    {
        var document = EditorDocument.Create(64, 64);
        var layer = document.AddTextLayer(Style("Original"), PointD.Zero)!;
        document.MarkSaved();
        Assert.True(document.UpdateTextLayer(layer.Id, Style("Original")));
        Assert.False(document.IsDirty);
        Assert.True(document.UpdateTextLayer(layer.Id, Style("Edited")));
        Assert.True(document.Undo());
        Assert.False(document.IsDirty);
        Assert.False(document.UpdateTextLayer(layer.Id, Style("Invalid", 0)));
        Assert.False(document.UpdateTextLayer(Guid.NewGuid(), Style("Missing")));
        Assert.False(document.UpdateTextLayer(document.Snapshot.Manifest.Layers[0].Id, Style("Raster")));
        Assert.True(document.CanRedo);
        Assert.True(document.Redo());
        Assert.Equal("Edited", document.Snapshot.Manifest.Layers.Single(record => record.Id == layer.Id).Text!.Content);
        Assert.True(document.Undo());
        Assert.True(document.Undo()); // no extra unchanged/refused step before the original creation
        Assert.Single(document.Snapshot.Manifest.Layers);
        Assert.False(document.CanUndo);
    }

    [Fact]
    public void OversizedPlacementIsRefusedBeforeChangingHistoryOrPixels()
    {
        var document = EditorDocument.Create(64, 64);
        var layer = document.AddTextLayer(Style("I"), PointD.Zero)!;
        layer.Transform.Size = new SizeD(300_000, layer.Transform.Size.Height);
        var original = document.Snapshot.Images[layer.Id];
        document.MarkSaved();
        Assert.False(document.UpdateTextLayer(layer.Id, Style("WWWWWW")));
        Assert.Same(original, document.Snapshot.Images[layer.Id]);
        Assert.Equal("I", layer.Text!.Content);
        Assert.False(document.IsDirty);
        Assert.True(document.Undo());
        Assert.Single(document.Snapshot.Manifest.Layers);
    }

    [Fact]
    public void FixedBoxesKeepTheirPixelScaleAndUnplacedMasksKeepTheirOldPlacement()
    {
        var document = EditorDocument.Create(64, 64);
        var oldStyle = Style("Old");
        oldStyle.BoxSize = new TextBoxSize(120, 100);
        var layer = document.AddTextLayer(oldStyle, new PointD(7, 11))!;
        layer.Transform.Size = new SizeD(180, 200);
        var oldTransform = layer.Transform;
        layer.MaskFile = ProjectValidator.Uuid(layer.Id) + ".mask.png";
        layer.MaskLinked = false;
        var style = Style("New");
        style.BoxSize = new TextBoxSize(200, 140);
        Assert.True(document.UpdateTextLayer(layer.Id, style));
        Assert.Equal(new SizeD(300, 280), layer.Transform.Size);
        Assert.Equal(oldTransform, layer.MaskPlacement);
        Assert.True(document.Undo());
        Assert.Null(document.Snapshot.Manifest.Layers.Single(record => record.Id == layer.Id).MaskPlacement);
    }

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(37, true, false)]
    [InlineData(-123, false, true)]
    public void EditingPreservesTheTransformedCornerAndPixelScale(double rotation, bool flipX, bool flipY)
    {
        var document = EditorDocument.Create(64, 64);
        var layer = document.AddTextLayer(Style("Old"), new PointD(7, 11))!;
        var original = document.Snapshot.Images[layer.Id];
        layer.Transform.Size = new SizeD(original.Width * 1.5, original.Height * 2);
        layer.Transform.Rotation = rotation;
        layer.Transform.FlipX = flipX;
        layer.Transform.FlipY = flipY;
        var anchor = layer.Transform.Point(PointD.Zero);

        Assert.True(document.UpdateTextLayer(layer.Id, Style("New words", 48)));

        var image = document.Snapshot.Images[layer.Id];
        Assert.Equal(anchor.X, layer.Transform.Point(PointD.Zero).X, 8);
        Assert.Equal(anchor.Y, layer.Transform.Point(PointD.Zero).Y, 8);
        Assert.Equal(image.Width * 1.5, layer.Transform.Size.Width, 8);
        Assert.Equal(image.Height * 2, layer.Transform.Size.Height, 8);
        Assert.Equal(rotation, layer.Transform.Rotation);
        Assert.Equal(flipX, layer.Transform.FlipX);
        Assert.Equal(flipY, layer.Transform.FlipY);
        Assert.Equal("Old", layer.Name);
        Assert.Equal("New words", layer.Text!.Content);
        Assert.NotSame(original, image);
    }
    [Fact]
    public void AFixedBoxKeepsItsPlacementWhileTheLinesRelayout()
    {
        // The Mac re-grows only point text (TypeTool.swift:288-297). A box the user dragged stays put and the
        // words re-wrap inside it; the port grew every text layer, which slid a rotated box's corner on each edit.
        var document = EditorDocument.Create(200, 160);
        LayerTextStyle Boxed(string content) => new()
        {
            Content = content, FontName = "Not-A-Real-Font", FontSize = 24, Red = 1, BoxSize = new TextBoxSize(120, 80),
        };
        var layer = document.AddTextLayer(Boxed("In"), new PointD(5, 7))!;
        layer.Transform.Size = new SizeD(240, 160);
        layer.Transform.Rotation = 30;
        var placement = layer.Transform;
        var original = document.Snapshot.Images[layer.Id];

        // The inline editor hands over the placement it is showing; the dialog path (no placement) still grows
        // the box to keep its pixel scale, which FixedBoxesKeepTheirPixelScaleAndUnplacedMasks... pins.
        Assert.True(document.UpdateTextLayer(layer.Id, Boxed("Many more words than fit on one line at this size"), placement));

        var updated = document.Snapshot.Images[layer.Id];
        Assert.Equal(placement, layer.Transform);
        Assert.NotSame(original, updated);
        Assert.True(document.Undo());
        var restored = document.Snapshot.Manifest.Layers.Single(record => record.Id == layer.Id);
        Assert.Equal(placement, restored.Transform);
        Assert.Same(original, document.Snapshot.Images[layer.Id]);
    }

    [Fact]
    public void EditingABoxDoesNotAliasTheTransformTheHistoryHolds()
    {
        // The commit copies the placement instead of reusing the live instance: a drag after an edit must not
        // rewrite the transform the earlier history step says the layer had.
        var document = EditorDocument.Create(200, 160);
        LayerTextStyle Boxed(string content) => new()
        {
            Content = content, FontName = "Not-A-Real-Font", FontSize = 24, Red = 1, BoxSize = new TextBoxSize(120, 80),
        };
        var layer = document.AddTextLayer(Boxed("In"), new PointD(5, 7))!;
        Assert.True(document.UpdateTextLayer(layer.Id, Boxed("Edited"), layer.Transform));
        layer.Transform.Origin = new PointD(90, 90);

        Assert.True(document.Undo());
        var restored = document.Snapshot.Manifest.Layers.Single(record => record.Id == layer.Id);
        Assert.NotEqual(new PointD(90, 90), restored.Transform.Origin);
        Assert.Equal(5, restored.Transform.Origin.X, 8);
        Assert.Equal(7, restored.Transform.Origin.Y, 8);
    }
}
