using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// The draft a canvas text editor mutates before anything becomes pixels. These are the behaviours the macOS
/// original spells out in TypeTool.swift — one history step per commit, nothing created for blank new text,
/// a failed commit hands the words back, and history closed for as long as a draft is open.
/// </summary>
public class TextDraftTests
{
    private static LayerTextStyle Style(string content, double size = 24) => new()
    {
        Content = content,
        FontName = "Not-A-Real-Font",
        FontSize = size,
        Red = 1,
    };

    private static (EditorDocument document, ProjectLayerRecord layer) WithTextLayer()
    {
        var document = EditorDocument.Create(120, 90);
        var layer = document.AddTextLayer(Style("Hi"), new PointD(6, 8))!;
        return (document, layer);
    }

    [Fact]
    public void ANewDraftBecomesOneLayerAndExactlyOneHistoryStep()
    {
        var document = EditorDocument.Create(120, 90);
        Assert.Null(document.ActiveTextDraft);
        Assert.True(document.BeginTextDraft(null, new PointD(10, 12), null, Style("Typed words")));
        Assert.NotNull(document.ActiveTextDraft);
        Assert.Equal(1, document.LayerCount); // typing into a draft has not touched the document

        Assert.True(document.ApplyTextDraft());
        Assert.Null(document.ActiveTextDraft);
        Assert.Equal(2, document.LayerCount);
        var created = document.Snapshot.Manifest.Layers[^1];
        Assert.Equal("Typed words", created.Text!.Content);
        Assert.Equal(10, created.Transform.Origin.X, 8);
        Assert.Equal(12, created.Transform.Origin.Y, 8);
        Assert.True(document.Undo());
        Assert.Equal(1, document.LayerCount);
    }

    [Fact]
    public void BlankNewTextCommitsNothingAndLeavesNoStepBehind()
    {
        var document = EditorDocument.Create(120, 90);
        Assert.True(document.BeginTextDraft(null, new PointD(4, 4), null, Style("   \n ")));
        Assert.True(document.FinishTextDraft());
        Assert.Null(document.ActiveTextDraft);
        Assert.Equal(1, document.LayerCount);
        // The layer that came with the document is the only thing there is, and there is nothing to undo.
        Assert.False(document.CanUndo);
    }

    [Fact]
    public void EditingAnExistingLayerIsOneStepThatRestoresPixelsAndStyle()
    {
        var (document, layer) = WithTextLayer();
        var original = document.Snapshot.Images[layer.Id];
        Assert.True(document.BeginTextDraft(layer.Id, layer.Transform.Origin, layer.Transform, Style("Hi")));
        Assert.True(document.UpdateTextDraft(Style("Replaced text", 40), selection: (2, 5)));
        Assert.True(document.FinishTextDraft());

        var updated = document.Snapshot.Images[layer.Id];
        Assert.NotSame(original, updated);
        Assert.Equal("Replaced text", layer.Text!.Content);
        Assert.True(document.Undo());
        var restored = document.Snapshot.Manifest.Layers.Single(record => record.Id == layer.Id);
        Assert.Equal("Hi", restored.Text!.Content);
        Assert.Same(original, document.Snapshot.Images[layer.Id]);
    }

    [Fact]
    public void CommittingAnUnchangedStyleTouchesNeitherPixelsNorHistory()
    {
        var (document, layer) = WithTextLayer();
        var before = document.Snapshot.Images[layer.Id];
        int changes = 0;
        document.Changed += () => changes++;

        Assert.True(document.BeginTextDraft(layer.Id, layer.Transform.Origin, layer.Transform, Style("Hi")));
        Assert.True(document.FinishTextDraft());

        Assert.Equal(0, changes);
        Assert.Same(before, document.Snapshot.Images[layer.Id]);
    }

    [Fact]
    public void ACommitThatCannotHappenHandsTheWordsBack()
    {
        var (document, layer) = WithTextLayer();
        Assert.True(document.BeginTextDraft(layer.Id, layer.Transform.Origin, layer.Transform, Style("Hi")));
        Assert.True(document.UpdateTextDraft(Style("Words not yet committed", 30)));
        Assert.True(document.DeleteLayer(layer.Id)); // the layer it was drafting is gone from under it

        Assert.False(document.ApplyTextDraft());
        var kept = Assert.IsType<TextDraft>(document.ActiveTextDraft);
        Assert.Equal("Words not yet committed", kept.Style.Content);
        Assert.Equal(layer.Id, kept.LayerId);
    }

    [Fact]
    public void HistoryStaysClosedUntilTheDraftIsSettled()
    {
        var (document, layer) = WithTextLayer();
        Assert.True(document.CanUndo); // creating the text layer is one step
        Assert.True(document.BeginTextDraft(layer.Id, layer.Transform.Origin, layer.Transform, Style("Hi")));
        Assert.False(document.CanUndo);
        Assert.False(document.Undo());
        Assert.False(document.BeginTextDraft(null, new PointD(1, 1), null, Style("second draft")));

        document.CancelTextDraft();
        Assert.Null(document.ActiveTextDraft);
        Assert.True(document.CanUndo);
        Assert.True(document.Undo());
    }

    [Fact]
    public void OnlyRealTextLayersCanBeReEdited()
    {
        var (document, layer) = WithTextLayer();
        var image = document.ImportImage(new Surface(8, 8), "Plate");
        Assert.False(document.BeginTextDraft(image.Id, PointD.Zero, null, Style("x")));
        Assert.False(document.BeginTextDraft(Guid.NewGuid(), PointD.Zero, null, Style("x")));
        Assert.False(document.BeginTextDraft(layer.Id, PointD.Zero, null, new LayerTextStyle { Content = "", FontName = "", FontSize = 24, Red = 4 }));
        Assert.Null(document.ActiveTextDraft);
    }

    [Fact]
    public void TheDraftCarriesThePlacementTheEditorIsShowing()
    {
        // What a fixed box keeps its size and point text does not is decided by the draft's placement, so the
        // draft has to be able to hand one over that differs from what the layer currently holds.
        var (document, layer) = WithTextLayer();
        var moved = new LayerTransform { Origin = new PointD(40, 40), Size = layer.Transform.Size, Rotation = 25 };
        Assert.True(document.BeginTextDraft(layer.Id, layer.Transform.Origin, moved, Style("Hi")));
        Assert.True(document.UpdateTextDraft(Style("Longer words here")));
        var corner = moved.Point(PointD.Zero);
        Assert.True(document.FinishTextDraft());

        // Point text grows as it is typed, but the corner the draft was showing stays exactly where it was.
        Assert.Equal(corner.X, layer.Transform.Point(PointD.Zero).X, 8);
        Assert.Equal(corner.Y, layer.Transform.Point(PointD.Zero).Y, 8);
        Assert.Equal(25, layer.Transform.Rotation, 8);
    }

    [Fact]
    public void ABoxDraftKeepsTheBoxTheEditorIsShowing()
    {
        var document = EditorDocument.Create(200, 160);
        LayerTextStyle Boxed(string content) => new()
        {
            Content = content, FontName = "Not-A-Real-Font", FontSize = 24, Red = 1, BoxSize = new TextBoxSize(120, 80),
        };
        var layer = document.AddTextLayer(Boxed("In"), new PointD(5, 7))!;
        var placement = new LayerTransform { Origin = new PointD(30, 22), Size = new SizeD(240, 160), Rotation = 15 };

        Assert.True(document.BeginTextDraft(layer.Id, placement.Origin, placement, Boxed("In")));
        Assert.True(document.UpdateTextDraft(Boxed("Many more words than fit on one line at this size")));
        Assert.True(document.FinishTextDraft());

        Assert.Equal(placement, layer.Transform);
    }

    [Fact]
    public void TheDraftPreviewsPixelsAndEscapeLeavesNoTraceOfThem()
    {
        // The canvas has to show what is being typed (macOS draws the draft's own style, EditorCanvas.swift:31),
        // and Escape must take those pixels back as if they had never existed — not as an undo step.
        var (document, layer) = WithTextLayer();
        var original = document.Snapshot.Images[layer.Id];
        Assert.True(document.BeginTextDraft(layer.Id, layer.Transform.Origin, layer.Transform, Style("Hi")));
        Assert.True(document.UpdateTextDraft(Style("Temporary", 40)));

        var previewed = document.Snapshot.Images[layer.Id];
        Assert.NotSame(original, previewed);
        Assert.Equal("Temporary", layer.Text!.Content);
        document.CancelTextDraft();

        Assert.Same(original, document.Snapshot.Images[layer.Id]);
        // Look the layer up again: restoring rebuilds the manifest, so the record object held from before the
        // draft belongs to the snapshot that was replaced and still says "Temporary". Reading through it is how
        // this test first claimed the product had not undone anything.
        var afterEscape = document.Snapshot.Manifest.Layers.Single(record => record.Id == layer.Id);
        Assert.Equal("Hi", afterEscape.Text!.Content);
        Assert.True(document.Undo()); // the only step left is the layer's own creation
        Assert.Equal(1, document.LayerCount);
    }


    [Fact]
    public void TypingAndTypingBackBooksNoStep()
    {
        var (document, layer) = WithTextLayer();
        var original = document.Snapshot.Images[layer.Id];
        Assert.True(document.BeginTextDraft(layer.Id, layer.Transform.Origin, layer.Transform, Style("Hi")));
        Assert.True(document.UpdateTextDraft(Style("Back and forth", 30)));
        Assert.True(document.UpdateTextDraft(Style("Hi")));

        Assert.True(document.FinishTextDraft());
        Assert.Same(original, document.Snapshot.Images[layer.Id]);
        Assert.True(document.Undo());
        Assert.Equal(1, document.LayerCount); // one step existed: the creation, not a phantom edit
    }

    // The handle drag, checked against what the draft looked like before it — which corner stayed, how far the
    // edge the handle owns moved — rather than against the arithmetic that produced it.
    [Fact]
    public void AHandleDragTurnsPointTextIntoABoxAndMovesOnlyTheEdgeItOwns()
    {
        var (document, layer) = WithTextLayer();
        var measured = TextRasterizer.TextBoxSize(layer.Text!);
        var placement = layer.Transform;
        Assert.Null(layer.Text!.BoxSize);
        Assert.True(document.BeginTextDraft(layer.Id, placement.Origin, placement, layer.Text!));

        var start = new PointD(placement.Origin.X + measured.Width, placement.Origin.Y + measured.Height / 2);
        Assert.True(document.BeginTextResize(3, start));
        Assert.True(document.ResizeTextDraft(new PointD(start.X + 20, start.Y)));
        document.EndTextResize();

        var draft = document.ActiveTextDraft!;
        Assert.NotNull(draft.Style.BoxSize); // point text became a fixed box, at the size it has right now
        Assert.Equal(measured.Width + 20, draft.Style.BoxSize!.Width, 1);
        Assert.Equal(measured.Height, draft.Style.BoxSize.Height, 1);
        Assert.Equal(measured.Width + 20, draft.Transform!.Size.Width, 1);
        // A freshly created text layer is shown at one-to-one scale, so the document numbers are its own.
        Assert.Equal(placement.Origin.X, draft.Transform.Origin.X, 6);
        Assert.Equal(placement.Origin.Y, draft.Transform.Origin.Y, 6);
        Assert.Equal(placement.Point(new PointD(1, 0)).X + 20, draft.Transform.Point(new PointD(1, 0)).X, 6);
        // The pixels have to be laid out in the box that was dragged, not in the one before it: a preview that
        // writes the new box over the old placement re-rasterizes at the old width and still looks fine here.
        Assert.Equal((int)Math.Round(draft.Style.BoxSize!.Width), document.Snapshot.Images[layer.Id].Width);
        Assert.True(document.FinishTextDraft());
        var edited = document.Snapshot.Manifest.Layers.Single(record => record.Id == layer.Id);
        Assert.NotNull(edited.Text!.BoxSize);
        // The layer that is committed has to carry the box that was dragged. Previewing the new box against the
        // placement from before the drag leaves the draft right and the layer one frame behind it, which nothing
        // above can see.
        Assert.Equal(edited.Text.BoxSize!.Width, edited.Transform.Size.Width, 1);
        Assert.Equal(edited.Text.BoxSize.Height, edited.Transform.Size.Height, 1);
        Assert.Equal(placement.Point(PointD.Zero).X, edited.Transform.Point(PointD.Zero).X, 6);
        Assert.Equal(placement.Point(PointD.Zero).Y, edited.Transform.Point(PointD.Zero).Y, 6);
        Assert.Equal((int)Math.Round(edited.Text.BoxSize.Width), document.Snapshot.Images[edited.Id].Width);
    }

    [Fact]
    public void AQuarterTurnedDraftKeepsItsTopLeftAndFollowsThePointerAlongItsOwnAxis()
    {
        var (document, layer) = WithTextLayer();
        layer.Transform.Rotation = 90;
        var measured = TextRasterizer.TextBoxSize(layer.Text!);
        var placement = layer.Transform;
        Assert.True(document.BeginTextDraft(layer.Id, placement.Origin, placement, layer.Text!));

        var corner = placement.Point(new PointD(1, 0));
        Assert.True(document.BeginTextResize(3, corner));
        Assert.True(document.ResizeTextDraft(new PointD(corner.X, corner.Y + 20)));
        var draft = document.ActiveTextDraft!;
        // At a quarter turn the box's own +x is the document's +y, so a downward drag widens it — and the corner
        // this handle does not touch, the top left, has to stay exactly where it was.
        Assert.Equal(placement.Point(PointD.Zero).X, draft.Transform!.Point(PointD.Zero).X, 6);
        Assert.Equal(placement.Point(PointD.Zero).Y, draft.Transform.Point(PointD.Zero).Y, 6);
        Assert.Equal(corner.X, draft.Transform.Point(new PointD(1, 0)).X, 6);
        Assert.Equal(corner.Y + 20, draft.Transform.Point(new PointD(1, 0)).Y, 6);
        Assert.Equal(measured.Width + 20, draft.Style.BoxSize!.Width, 1);
    }

    [Fact]
    public void AHandleDraggedInwardStopsAtTheSixteenPixelFloor()
    {
        var (document, layer) = WithTextLayer();
        var measured = TextRasterizer.TextBoxSize(layer.Text!);
        Assert.True(document.BeginTextDraft(layer.Id, layer.Transform.Origin, layer.Transform, layer.Text!));
        var start = new PointD(layer.Transform.Origin.X + measured.Width, layer.Transform.Origin.Y + measured.Height / 2);
        Assert.True(document.BeginTextResize(3, start));
        Assert.True(document.ResizeTextDraft(new PointD(start.X - 5_000, start.Y)));
        Assert.Equal(16, document.ActiveTextDraft!.Style.BoxSize!.Width, 1);
    }

    [Fact]
    public void ARefusedDragLeavesTheDraftExactlyAsItWas()
    {
        var (document, layer) = WithTextLayer();
        var measured = TextRasterizer.TextBoxSize(layer.Text!);
        Assert.True(document.BeginTextDraft(layer.Id, layer.Transform.Origin, layer.Transform, layer.Text!));
        var start = new PointD(layer.Transform.Origin.X + measured.Width, layer.Transform.Origin.Y + measured.Height / 2);

        // Taking the handle converts point text to a box of its current size — that is the press's own work, and
        // it is the frame that is refused here which must change nothing.
        Assert.True(document.BeginTextResize(3, start));
        var pixels = document.Snapshot.Images[layer.Id];
        var boxed = document.ActiveTextDraft!.Style.BoxSize;
        Assert.NotNull(boxed);
        Assert.Equal(measured.Width, boxed!.Width, 1);
        Assert.Equal(measured.Height, boxed.Height, 1);
        var placed = document.ActiveTextDraft.Transform;

        Assert.False(document.ResizeTextDraft(new PointD(start.X + 2_000_000, start.Y)));
        Assert.Equal(boxed.Width, document.ActiveTextDraft!.Style.BoxSize!.Width, 1);
        Assert.Equal(placed!.Size.Width, document.ActiveTextDraft.Transform!.Size.Width, 6);
        Assert.Equal(placed.Origin.X, document.ActiveTextDraft.Transform.Origin.X, 6);
        Assert.Same(pixels, document.Snapshot.Images[layer.Id]);
        Assert.True(document.ResizeTextDraft(new PointD(start.X + 5, start.Y)));
        Assert.NotSame(pixels, document.Snapshot.Images[layer.Id]);
        Assert.Equal(measured.Width + 5, document.ActiveTextDraft.Style.BoxSize!.Width, 1);
    }

    [Fact]
    public void SeveralDragsCommitAsOneStepAndANewTextDraftCanBeBoxedBeforeItExists()
    {
        var (document, layer) = WithTextLayer();
        var measured = TextRasterizer.TextBoxSize(layer.Text!);
        var pixels = document.Snapshot.Images[layer.Id];
        Assert.True(document.BeginTextDraft(layer.Id, layer.Transform.Origin, layer.Transform, layer.Text!));
        var start = new PointD(layer.Transform.Origin.X + measured.Width, layer.Transform.Origin.Y + measured.Height / 2);
        Assert.True(document.BeginTextResize(3, start));
        for (int step = 1; step <= 4; step++) Assert.True(document.ResizeTextDraft(new PointD(start.X + 10 * step, start.Y)));
        // The frames are measured from where the press landed, not from the frame before them: the same pointer
        // position twice in a row must not grow the box a second time.
        Assert.Equal(40, document.ActiveTextDraft!.Style.BoxSize!.Width - measured.Width, 1);
        // The same pointer position twice in a row must not grow the box a second time, and going back to an
        // earlier position must land exactly where it was: the frames are measured from the press, not from
        // the frame before them.
        Assert.True(document.ResizeTextDraft(new PointD(start.X + 40, start.Y)));
        Assert.Equal(40, document.ActiveTextDraft!.Style.BoxSize!.Width - measured.Width, 1);
        Assert.True(document.ResizeTextDraft(new PointD(start.X + 10, start.Y)));
        Assert.Equal(10, document.ActiveTextDraft!.Style.BoxSize!.Width - measured.Width, 1);
        document.EndTextResize();
        Assert.True(document.FinishTextDraft());
        Assert.True(document.Undo());
        Assert.Same(pixels, document.Snapshot.Images[layer.Id]);
        var restored = document.Snapshot.Manifest.Layers.Single(record => record.Id == layer.Id);
        Assert.Null(restored.Text!.BoxSize);
        Assert.True(document.Undo()); // the step below is the layer's own creation, so four drags booked one
        Assert.False(document.CanUndo);

        Assert.True(document.BeginTextDraft(null, new PointD(10, 12), null, Style("Fresh box")));
        Assert.True(document.BeginTextResize(4, new PointD(10 + measured.Width, 12 + measured.Height)));
        Assert.True(document.ResizeTextDraft(new PointD(10 + measured.Width + 30, 12 + measured.Height + 40)));
        Assert.NotNull(document.ActiveTextDraft!.Style.BoxSize);
        Assert.True(document.FinishTextDraft());
        var created = document.Snapshot.Manifest.Layers[^1];
        Assert.NotNull(created.Text!.BoxSize);
        Assert.Equal(created.Text!.BoxSize!.Width, created.Transform.Size.Width, 1);
        // A brand-new draft has no layer to borrow a placement from; the box it was clicked in is the placement.
        Assert.Equal(10, created.Transform.Origin.X, 6);
        Assert.Equal(12, created.Transform.Origin.Y, 6);
        Assert.Equal(created.Text!.BoxSize!.Height, created.Transform.Size.Height, 1);
    }

    [Fact]
    public void ANewDraftInATurnedPlacementKeepsThatPlacementThroughTheCommit()
    {
        // BeginTextDraft takes a placement, so a new box can be opened turned; the commit has to leave that
        // placement behind. AddTextLayer(style, origin) alone would re-upright the layer and lose the drag, and
        // for an upright new draft both answers are identical — this is the case that tells them apart.
        var document = EditorDocument.Create(320, 240);
        var style = Style("Turned box");
        var measured = TextRasterizer.TextBoxSize(style);
        var turned = new LayerTransform { Origin = new PointD(30, 40), Size = measured, Rotation = 90 };
        Assert.True(document.BeginTextDraft(null, new PointD(30, 40), turned, style));
        var corner = turned.Point(new PointD(1, 0));
        Assert.True(document.BeginTextResize(3, corner));
        Assert.True(document.ResizeTextDraft(new PointD(corner.X, corner.Y + 20)));
        Assert.True(document.FinishTextDraft());

        var created = document.Snapshot.Manifest.Layers[^1];
        Assert.NotNull(created.Text!.BoxSize);
        Assert.Equal(90, created.Transform.Rotation, 6);
        Assert.Equal(turned.Point(PointD.Zero).X, created.Transform.Point(PointD.Zero).X, 6);
        Assert.Equal(turned.Point(PointD.Zero).Y, created.Transform.Point(PointD.Zero).Y, 6);
        Assert.Equal(measured.Width + 20, created.Text.BoxSize!.Width, 1);
    }

    [Fact]
    public void ResizingWithoutADraftResizesNothing()
    {
        var (document, _) = WithTextLayer();
        Assert.False(document.BeginTextResize(3, new PointD(10, 10)));
        Assert.False(document.ResizeTextDraft(new PointD(30, 10)));
        var (document2, layer2) = WithTextLayer();
        Assert.True(document2.BeginTextDraft(layer2.Id, layer2.Transform.Origin, layer2.Transform, layer2.Text!));
        // A frame with no press behind it resizes nothing.
        Assert.False(document2.ResizeTextDraft(new PointD(999, 999)));
        Assert.Null(document2.ActiveTextDraft!.Style.BoxSize);
    }
}
