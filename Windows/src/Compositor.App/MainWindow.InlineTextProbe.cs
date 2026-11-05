#if DEBUG
using Compositor.Core;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.Foundation;

namespace Compositor.App;

public sealed partial class MainWindow
{
    /// <summary>
    /// The on-canvas text editor: does it open on the right layer, sit where that layer's placement says,
    /// preview pixels without booking history, and settle as one step or vanish on Escape. Typing is driven by
    /// setting the control's text — the same signal a keystroke reaches, via the Text property callback —
    /// because no probe in this repository fabricates key events; the caret, IME and the keys themselves stay
    /// an interactive check.
    /// </summary>
    /// <remarks>
    /// Step counts are read by undoing, not by a depth property: the document keeps two stacks and exposes
    /// only whether they are open, so a group proves "exactly one step" by taking that step back, then the
    /// layer's own creation, and then showing the history is empty. Each scene therefore starts with one step
    /// already booked — the text layer's creation — and a group that needs a clean history builds its own.
    /// </remarks>
    private async Task VerifyInlineTextAsync(Dictionary<string, object> report)
    {
        var scene = EditorDocument.Create(320, 240);
        var style = new LayerTextStyle { Content = "Hi", FontName = "Segoe UI", FontSize = 40, Red = 1 };
        var placed = scene.AddTextLayer(style, new PointD(40, 60))!;
        placed.Transform.Rotation = 25;
        OpenDocument(scene); await WaitForFramesAsync();

        var original = scene.Snapshot.Images[placed.Id];
        // The group before this one leaves a floating transform and its frames behind; opening an editor inside
        // that state would fail the same gate a user would hit, so wait for the canvas to be quiet first — and
        // say which gate is still shut if it never opens, instead of blaming the editor for it.
        Require(await WaitUntilAsync(() => CanEditLayerCommands, 6000),
            $"the canvas never came back to an editable state: core={CanEditSelectionCore} pending={renderInputs.HasPending} "
            + $"stroke={pointerStroke is not null} busy={StatusProgress.Visibility != Microsoft.UI.Xaml.Visibility.Visible} "
            + $"renaming={renamingLayer}");
        var target = scene.Snapshot.Manifest.Layers.Single(record => record.Id == placed.Id);
        OpenInlineText(target);
        await WaitForFramesAsync();
        Require(scene.ActiveTextDraft?.LayerId == placed.Id,
            $"the inline editor did not open a draft on the layer under the click: host={ReferenceEquals(document, scene)} "
            + $"editable={CanEditLayerCommands} text={target.Text is not null} styleValid={target.Text?.IsValid} "
            + $"group={target.IsGroupOrFalse} trace={inlineDraftTrace}");
        Require(scene.Snapshot.Manifest.ActiveLayerId == placed.Id, "editing text did not make its layer active");
        Require(inlineEditor is not null && InlineTextLayer.Children.Count == 1 && inlineBox is not null
            && inlineBox.Children.Count == 10 && ReferenceEquals(VisualTreeHelper.GetParent(inlineEditor), inlineBox),
            $"the inline editor was not placed inside the canvas overlay: overlay children={InlineTextLayer.Children.Count} "
            + $"box children={inlineBox?.Children.Count ?? -1}");
        Require(!scene.CanUndo && !scene.Undo(), "history stayed open while a text draft is in flight");
        report["inlineTextOpensOnItsLayerWithHistoryClosed"] = "passed";

        // Where the box is: the control's own corners, mapped back to document coordinates, are the corners
        // LayerTransform.Point reports for the same placement — the same maths the rasterized pixels follow.
        var editor = inlineEditor!;
        var transform = editor.TransformToVisual(InlineTextLayer);
        var record = scene.Snapshot.Manifest.Layers.Single(layer => layer.Id == placed.Id);
        var before = record.Transform.Size;
        var corners = new[] { new Point(0, 0), new Point((float)record.Transform.Size.Width, 0),
            new Point((float)record.Transform.Size.Width, (float)record.Transform.Size.Height), new Point(0, (float)record.Transform.Size.Height) };
        var units = new[] { new PointD(0, 0), new PointD(1, 0), new PointD(1, 1), new PointD(0, 1) };
        for (int index = 0; index < corners.Length; index++)
        {
            var shown = transform.TransformPoint(corners[index]);
            var expected = record.Transform.Point(units[index]);
            Require(Math.Abs(shown.X - expected.X) < .5 && Math.Abs(shown.Y - expected.Y) < .5,
                $"the editor's corner {index} sits at ({shown.X:0.#}, {shown.Y:0.#}) while the placement says ({expected.X:0.#}, {expected.Y:0.#})");
        }
        report["inlineTextSitsOnThePlacementItIsEditing"] = "passed";

        editor.Text = "Words long enough to re-wrap and grow the box";
        await WaitForFramesAsync();
        var previewed = scene.Snapshot.Images[placed.Id];
        Require(!ReferenceEquals(previewed, original) && editor.Text.Length > 10,
            $"typing into the editor did not re-rasterize the layer: stored="
            + $"{scene.Snapshot.Manifest.Layers.Single(layer => layer.Id == placed.Id).Text?.Content} trace={inlineDraftTrace}");
        Require(scene.ActiveTextDraft?.Style.Content == editor.Text && !scene.CanUndo,
            "the preview either reached the history or never reached the draft");
        var previewRecord = scene.Snapshot.Manifest.Layers.Single(layer => layer.Id == placed.Id);
        Require(previewRecord.Text?.Content == editor.Text, "the canvas preview is not the text the draft holds");
        var grown = previewRecord.Transform.Size;
        Require(grown.Width > before.Width && grown.Height == before.Height,
            $"one longer line of point text should widen the box, not tallen it: {before.Width:0.#}×{before.Height:0.#} became "
            + $"{grown.Width:0.#}×{grown.Height:0.#}");
        report["inlineTypingPreviewsPixelsWithNoStepYet"] = "passed";

        CommitInlineText();
        await WaitForFramesAsync();
        Require(scene.ActiveTextDraft is null && inlineEditor is null && scene.CanUndo,
            "committing the editor did not settle into the document");
        Require(scene.Undo(), "the committed draft left no step to undo");
        var undone = scene.Snapshot.Manifest.Layers.Single(layer => layer.Id == placed.Id);
        Require(ReferenceEquals(scene.Snapshot.Images[placed.Id], original) && undone.Text?.Content == "Hi"
                && undone.Transform.Size == before,
            $"one undo did not take back the words, the pixels and the grown box together: content={undone.Text?.Content} "
            + $"size={undone.Transform.Size.Width:0.#}×{undone.Transform.Size.Height:0.#}");
        // One step above the layer's own creation, and that creation is the step still left: taking it back
        // leaves a document with nothing but its background, which is what a two-step history here means.
        Require(scene.Undo() && scene.Snapshot.Manifest.Layers.Count(layer => layer.Id == placed.Id) == 0,
            $"the step below the draft was not the layer's creation, so the draft booked more than one: "
            + $"layers={scene.Snapshot.Manifest.Layers.Count} canUndo={scene.CanUndo}");
        Require(!scene.CanUndo, "the commit left more behind than the draft's step and the layer's creation");
        report["inlineCommitIsExactlyOneStep"] = "passed";

        var second = EditorDocument.Create(320, 240);
        var again = second.AddTextLayer(style, new PointD(40, 60))!;
        OpenDocument(second); await WaitForFramesAsync();
        var untouched = second.Snapshot.Images[again.Id];
        Require(await WaitUntilAsync(() => CanEditLayerCommands, 6000), "the canvas stayed shut for the second draft");
        OpenInlineText(second.Snapshot.Manifest.Layers.Single(layer => layer.Id == again.Id));
        await WaitForFramesAsync();
        Require(inlineEditor is not null, "the editor would not open a second time");
        inlineEditor!.Text = "Never committed";
        await WaitForFramesAsync();
        Require(!ReferenceEquals(second.Snapshot.Images[again.Id], untouched), "the second draft did not preview either");
        CloseInlineText(cancelling: true);
        await WaitForFramesAsync();
        Require(second.ActiveTextDraft is null && inlineEditor is null
                && ReferenceEquals(second.Snapshot.Images[again.Id], untouched)
                && second.Snapshot.Manifest.Layers.Single(layer => layer.Id == again.Id).Text?.Content == "Hi",
            "escaping the editor left its previewed pixels or its draft behind");
        // Escape must have booked nothing: the only step left is the layer's creation, so the one undo this
        // document has takes the layer away rather than putting the words back.
        Require(second.Undo() && second.Snapshot.Manifest.Layers.Count(layer => layer.Id == again.Id) == 0
                && !second.CanUndo,
            $"a cancelled draft still took a history step: layers={second.Snapshot.Manifest.Layers.Count} "
            + $"canUndo={second.CanUndo}");
        report["inlineEscapeLeavesNoPreviewAndNoStep"] = "passed";

        // The product settles a draft when the tool changes (OnToolChanged → CommitInlineText, as the original
        // does), and until now nothing answered for that wiring. The switch is made from a tool the draft was
        // really opened in, so it is a change and not a re-selection of the tool already in place.
        var third = EditorDocument.Create(320, 240);
        var typed = third.AddTextLayer(style, new PointD(24, 36))!;
        OpenDocument(third); await WaitForFramesAsync();
        var kept = third.Snapshot.Images[typed.Id];
        ToolCombo.SelectedIndex = (int)CanvasTool.Text;
        Require(await WaitUntilAsync(() => tool == CanvasTool.Text && CanEditLayerCommands, 6000),
            $"the type tool never became the live tool: tool={tool} editable={CanEditLayerCommands}");
        OpenInlineText(third.Snapshot.Manifest.Layers.Single(layer => layer.Id == typed.Id));
        await WaitForFramesAsync();
        Require(third.ActiveTextDraft is not null && inlineEditor is not null, "the editor would not open in the type tool");
        inlineEditor!.Text = "Settled by the tool switch";
        await WaitForFramesAsync();
        Require(!ReferenceEquals(third.Snapshot.Images[typed.Id], kept), "the third draft did not preview either");
        ToolCombo.SelectedIndex = (int)CanvasTool.Move;
        Require(await WaitUntilAsync(() => tool == CanvasTool.Move && third.ActiveTextDraft is null, 6000),
            $"switching to the Move tool neither moved the tool nor settled the draft: tool={tool} "
            + $"draft={third.ActiveTextDraft?.Style.Content ?? "<none>"} trace={inlineDraftTrace}");
        await WaitForFramesAsync();
        Require(inlineEditor is null && InlineTextLayer.Children.Count == 0,
            "the editor stayed on the canvas after the tool switch committed it");
        var switched = third.Snapshot.Manifest.Layers.Single(layer => layer.Id == typed.Id);
        Require(switched.Text?.Content == "Settled by the tool switch"
                && !ReferenceEquals(third.Snapshot.Images[typed.Id], kept),
            $"the tool switch threw the words away instead of committing them: content={switched.Text?.Content}");
        Require(third.Undo() && third.Snapshot.Manifest.Layers.Single(layer => layer.Id == typed.Id).Text?.Content == "Hi"
                && ReferenceEquals(third.Snapshot.Images[typed.Id], kept)
                && third.Undo() && third.Snapshot.Manifest.Layers.Count(layer => layer.Id == typed.Id) == 0
                && !third.CanUndo,
            $"the tool switch did not commit exactly one step: content="
            + $"{third.Snapshot.Manifest.Layers.FirstOrDefault(layer => layer.Id == typed.Id)?.Text?.Content} "
            + $"layers={third.Snapshot.Manifest.Layers.Count} canUndo={third.CanUndo}");
        report["inlineToolSwitchCommitsTheDraftAsOneStep"] = "passed";

        // The handles: eight squares on the placement the draft is showing, a grab that belongs to the edge band
        // rather than to the square, and a drag measured from where the press landed. The pointer itself is not
        // fabricated here — the probe calls the same three methods the press/move/release handlers call.
        var turned = EditorDocument.Create(320, 240);
        var box = turned.AddTextLayer(style, new PointD(60, 70))!;
        // Turned far enough that a grab which forgot to undo the rotation answers with a different edge — at a
        // small angle the two agree by accident.
        box.Transform.Rotation = 65;
        OpenDocument(turned); await WaitForFramesAsync();
        Require(await WaitUntilAsync(() => CanEditLayerCommands, 6000), "the canvas stayed shut before the handle drag");
        OpenInlineText(turned.Snapshot.Manifest.Layers.Single(record => record.Id == box.Id));
        await WaitForFramesAsync();
        var frame = turned.Snapshot.Manifest.Layers.Single(record => record.Id == box.Id).Transform;
        var measured = TextRasterizer.TextBoxSize(style);
        Require(inlineHandles.All(square => square is not null), "the editor did not build its eight handles");
        for (int index = 0; index < inlineHandles.Length; index++)
        {
            var centre = inlineHandles[index].TransformToVisual(InlineTextLayer)
                .TransformPoint(new Point((float)(inlineHandles[index].Width / 2), (float)(inlineHandles[index].Height / 2)));
            var expected = frame.Point(LayerTransform.Handles[index]);
            Require(Math.Abs(centre.X - expected.X) < .5 && Math.Abs(centre.Y - expected.Y) < .5,
                $"handle {index} is drawn at ({centre.X:0.#}, {centre.Y:0.#}) while the placement puts it at "
                + $"({expected.X:0.#}, {expected.Y:0.#})");
        }

        // A point half a reach inside the right edge, in document coordinates: a turned layer has to be grabbed
        // by the band that follows its own box, not by the axis-aligned side of its bounding rectangle.
        double reach = LayerTransform.EdgeReach(6, measured);
        var grab = frame.Point(new PointD(1 - reach / 2 / measured.Width, .5));
        Require(BeginInlineResize(grab), $"a press half a reach inside the right edge took no handle: trace={inlineDraftTrace}");
        Require(inlineResize?.Handle == 3, $"the right band answered with handle {inlineResize?.Handle} instead of 3");
        // Taking the handle is the press, and the press is what converts: point text becomes a fixed box of the
        // size it already has, and only the frames after it change that size.
        Require(turned.ActiveTextDraft!.Style.BoxSize is { } boxed && Math.Abs(boxed.Width - measured.Width) < 1
                && Math.Abs(boxed.Height - measured.Height) < 1,
            $"taking the handle did not convert the point text into its own size: {turned.ActiveTextDraft.Style.BoxSize?.Width ?? 0:0.#}×"
            + $"{turned.ActiveTextDraft.Style.BoxSize?.Height ?? 0:0.#} against {measured.Width:0.#}×{measured.Height:0.#}");

        var radians = frame.Radians;
        var to = new PointD(grab.X + 20 * Math.Cos(radians), grab.Y + 20 * Math.Sin(radians));
        MoveInlineResize(to);
        EndInlineResize();
        await WaitForFramesAsync();
        var dragged = turned.ActiveTextDraft!;
        Require(dragged.Style.BoxSize is { } sized && sized.Width - measured.Width > 19 && sized.Width - measured.Width < 21
                && Math.Abs(sized.Height - measured.Height) < 1,
            $"a 20-pixel drag along the box's own axis made a box of {dragged.Style.BoxSize?.Width ?? 0:0.#}×"
            + $"{dragged.Style.BoxSize?.Height ?? 0:0.#} out of {measured.Width:0.#}×{measured.Height:0.#}");
        Require(Math.Abs(dragged.Transform!.Point(PointD.Zero).X - frame.Point(PointD.Zero).X) < .001
                && Math.Abs(dragged.Transform.Point(PointD.Zero).Y - frame.Point(PointD.Zero).Y) < .001,
            "the corner the right handle does not own moved: the drag grew about the middle instead of the left edge");
        Require(!turned.CanUndo && turned.Undo() is false, "the handle drag opened the history it is supposed to preview in");

        CommitInlineText();
        await WaitForFramesAsync();
        Require(turned.Undo()
                && turned.Snapshot.Manifest.Layers.Single(record => record.Id == box.Id).Text!.BoxSize is null
                && turned.Undo() && turned.Snapshot.Manifest.Layers.Count(record => record.Id == box.Id) == 0
                && !turned.CanUndo,
            "the drag and the commit were not the single step they are worth");
        report["inlineHandlesDragTheParagraphBox"] = "passed";

        // A flipped layer: the original mirrors the text surface only and keeps the frame and the handles in
        // logical order (applyMirror, InlineTextEditor.swift:282-299), so the square on the frame's right edge
        // is still the one that grows the box's right edge, and the caret still sits over mirrored glyphs.
        var mirrored = EditorDocument.Create(320, 240);
        var flipped = mirrored.AddTextLayer(style, new PointD(50, 55))!;
        flipped.Transform.Rotation = 40;
        flipped.Transform.FlipX = true;
        OpenDocument(mirrored); await WaitForFramesAsync();
        Require(await WaitUntilAsync(() => CanEditLayerCommands, 6000), "the canvas stayed shut for the flipped draft");
        OpenInlineText(mirrored.Snapshot.Manifest.Layers.Single(record => record.Id == flipped.Id));
        await WaitForFramesAsync();
        var face = mirrored.Snapshot.Manifest.Layers.Single(record => record.Id == flipped.Id).Transform;
        var flippedSize = TextRasterizer.TextBoxSize(style);
        Require(inlineEditor?.RenderTransform is TransformGroup caretTurn
                && caretTurn.Children[0] is ScaleTransform caretMirror && caretMirror.ScaleX == -1,
            "the caret is not mirrored with the pixels it is typing into");
        for (int index = 0; index < inlineHandles.Length; index++)
        {
            var square = inlineHandles[index].TransformToVisual(InlineTextLayer).TransformPoint(
                new Point((float)(inlineHandles[index].Width / 2), (float)(inlineHandles[index].Height / 2)));
            var corner = face.Point(LayerTransform.Handles[index]);
            Require(Math.Abs(square.X - corner.X) < .5 && Math.Abs(square.Y - corner.Y) < .5,
                $"handle {index} of the flipped layer is at ({square.X:0.#}, {square.Y:0.#}) instead of the "
                + $"placement's own ({corner.X:0.#}, {corner.Y:0.#})");
        }

        var flippedReach = LayerTransform.EdgeReach(6, flippedSize);
        var atRight = face.Point(new PointD(1 - flippedReach / 2 / flippedSize.Width, .5));
        Require(BeginInlineResize(atRight), $"the flipped layer's right band took no handle: trace={inlineDraftTrace}");
        Require(inlineResize?.Handle == 3,
            $"the flipped layer's right band answered with handle {inlineResize?.Handle} instead of 3");
        var flippedTo = new PointD(atRight.X + 20 * Math.Cos(face.Radians), atRight.Y + 20 * Math.Sin(face.Radians));
        MoveInlineResize(flippedTo);
        EndInlineResize();
        await WaitForFramesAsync();
        var widened = mirrored.ActiveTextDraft!;
        Require(widened.Style.BoxSize is { } grown2 && grown2.Width - flippedSize.Width > 19
                && grown2.Width - flippedSize.Width < 21 && Math.Abs(grown2.Height - flippedSize.Height) < 1,
            $"the flipped layer grew the wrong way: {widened.Style.BoxSize?.Width ?? 0:0.#}×"
            + $"{widened.Style.BoxSize?.Height ?? 0:0.#} against {flippedSize.Width:0.#}×{flippedSize.Height:0.#}");
        Require(Math.Abs(widened.Transform!.Point(PointD.Zero).X - face.Point(PointD.Zero).X) < .001
                && Math.Abs(widened.Transform.Point(PointD.Zero).Y - face.Point(PointD.Zero).Y) < .001,
            "a mirror made the drag hold the wrong corner: the frame's own left edge has to stay put");
        report["inlineHandlesStayInLogicalOrderWhenFlipped"] = "passed";

        // New text on the canvas rather than in a sheet: the click makes point text in the remembered face and
        // the current foreground, and a layer appears only when there are words to keep (beginText,
        // TypeTool.swift:235-262; the blank commit that creates nothing is Core's, proved there and re-proved
        // here through the host's own entry).
        var foregroundBefore = foregroundColor;
        var fresh = EditorDocument.Create(320, 240);
        OpenDocument(fresh); await WaitForFramesAsync();
        Require(await WaitUntilAsync(() => CanEditLayerCommands, 6000), "the canvas stayed shut for new text");
        var layersBefore = fresh.LayerCount;
        foregroundColor = Windows.UI.Color.FromArgb(255, 0x10, 0x20, 0x30);
        Require(BeginNewTextDraft(new PointD(120, 130), null), $"the click started no draft: trace={inlineDraftTrace}");
        Require(fresh.ActiveTextDraft is { } typed2 && typed2.LayerId is null && typed2.Style.Content == ""
                && typed2.Style.BoxSize is null && Math.Abs(typed2.Style.Red - 0x10 / 255.0) < 1e-9
                && Math.Abs(typed2.Style.Green - 0x20 / 255.0) < 1e-9 && Math.Abs(typed2.Style.Blue - 0x30 / 255.0) < 1e-9,
            $"the new draft is not empty point text in the foreground colour: red={fresh.ActiveTextDraft?.Style.Red ?? 0:0.####} "
            + $"box={fresh.ActiveTextDraft?.Style.BoxSize?.Width ?? 0:0.#}");
        Require(fresh.LayerCount == layersBefore && inlineEditor is not null && InlineTextLayer.Children.Count == 1,
            $"new text reached the document before any words: layers={fresh.LayerCount} overlay={InlineTextLayer.Children.Count}");
        // Changed after the click, so the commit has to overwrite it: comparing the remembered face with a default
        // the draft was built from would prove nothing on its own.
        textDefaults = new LayerTextStyle { FontName = "Arial Black", FontSize = 9 };
        inlineEditor!.Text = "Typed straight in";
        await WaitForFramesAsync();
        CommitInlineText();
        await WaitForFramesAsync();
        Require(fresh.LayerCount == layersBefore + 1 && fresh.ActiveTextDraft is null && inlineEditor is null,
            $"committing new text made {fresh.LayerCount - layersBefore} layers instead of one");
        var made = fresh.Snapshot.Manifest.Layers[^1];
        Require(made.Text!.Content == "Typed straight in"
                && Math.Abs(made.Transform.Origin.X - (120 - LayerTextStyle.Padding)) < .001
                && 130 - made.Transform.Origin.Y > 0 && 130 - made.Transform.Origin.Y < made.Transform.Size.Height,
            $"the click did not land on the first baseline: origin=({made.Transform.Origin.X:0.##}, "
            + $"{made.Transform.Origin.Y:0.##}) height={made.Transform.Size.Height:0.#}");
        Require(textDefaults.FontName == made.Text.FontName && textDefaults.ColorRuns is null
                && textDefaults.FontRuns is null && textDefaults.BoxSize is null,
            $"what was typed is not what the next click will type with: {textDefaults.FontName} box={textDefaults.BoxSize?.Width ?? 0:0.#}");
        report["newTextClickTypesInlineAndRemembersItsFace"] = "passed";

        var blank = EditorDocument.Create(320, 240);
        OpenDocument(blank); await WaitForFramesAsync();
        Require(await WaitUntilAsync(() => CanEditLayerCommands, 6000), "the canvas stayed shut for the blank draft");
        var emptyCount = blank.LayerCount;
        Require(BeginNewTextDraft(new PointD(30, 40), null), $"the blank case started no draft: trace={inlineDraftTrace}");
        inlineEditor!.Text = "   \n ";
        await WaitForFramesAsync();
        CommitInlineText();
        await WaitForFramesAsync();
        Require(blank.LayerCount == emptyCount && blank.ActiveTextDraft is null && inlineEditor is null
                && !blank.CanUndo,
            $"whitespace-only new text left {blank.LayerCount - emptyCount} layer(s) and canUndo={blank.CanUndo}");
        report["blankNewTextCreatesNoLayerAndBooksNoStep"] = "passed";

        var band = EditorDocument.Create(320, 240);
        OpenDocument(band); await WaitForFramesAsync();
        Require(await WaitUntilAsync(() => CanEditLayerCommands, 6000), "the canvas stayed shut for the band drag");
        BeginTextBand(new PointD(40, 50));
        MoveTextBand(new PointD(140, 120));
        Require(textDragBox.Width > 99 && textDragBox.Height > 69 && textBand is not null,
            $"the rubber band did not follow the pointer: {textDragBox.Width:0.#}×{textDragBox.Height:0.#}");
        FinishTextGesture();
        Require(band.ActiveTextDraft?.Style.BoxSize is { } boxed2
                && Math.Abs(boxed2.Width - 100) < 1 && Math.Abs(boxed2.Height - 70) < 1,
            $"the dragged band is not a fixed box of what was drawn: {band.ActiveTextDraft?.Style.BoxSize?.Width ?? 0:0.#}×"
            + $"{band.ActiveTextDraft?.Style.BoxSize?.Height ?? 0:0.#}");
        Require(band.ActiveTextDraft!.Style.Content == "" && band.ActiveTextDraft.LayerId is null
                && band.ActiveTextDraft.Transform is { } drafted2
                && Math.Abs(drafted2.Origin.X - 40) < .001 && Math.Abs(drafted2.Origin.Y - 50) < .001,
            "a dragged box is placed where it was drawn, not shifted by a baseline");
        Require(band.LayerCount == 1 && textBand is null && inlineEditor is not null,
            $"the release both drew a layer and left the band up: layers={band.LayerCount} band={textBand is not null}");
        inlineEditor!.Text = "Wrapped inside the box";
        await WaitForFramesAsync();
        CommitInlineText();
        await WaitForFramesAsync();
        var boxedLayer = band.Snapshot.Manifest.Layers[^1];
        Require(band.LayerCount == 2 && boxedLayer.Text!.BoxSize is { } committedBox
                && Math.Abs(boxedLayer.Transform.Size.Width - committedBox.Width) < 1
                && Math.Abs(boxedLayer.Transform.Origin.X - 40) < .001,
            $"the committed box did not survive: size={boxedLayer.Transform.Size.Width:0.#} against {boxedLayer.Text!.BoxSize!.Width:0.#} "
            + $"at x={boxedLayer.Transform.Origin.X:0.#}");
        // What the box commit remembered must not leak into the next click: a click is point text.
        Require(BeginNewTextDraft(new PointD(200, 210), null) && band.ActiveTextDraft!.Style.BoxSize is null,
            $"the next click inherited the dragged box: {band.ActiveTextDraft?.Style.BoxSize?.Width ?? 0:0.#}×"
            + $"{band.ActiveTextDraft?.Style.BoxSize?.Height ?? 0:0.#}");
        CloseInlineText(cancelling: true);
        foregroundColor = foregroundBefore;
        report["draggedTextBandBecomesAFixedBoxOnCommit"] = "passed";

        // A paragraph box too small for what is typed gets the plus the original draws inside its bottom-right
        // handle, and loses it when the words fit again — asked through the same preview an edit takes, so the
        // mark is tied to the layout that is actually painted rather than to a second opinion about size.
        var tight = EditorDocument.Create(320, 240);
        var oneLine = new LayerTextStyle { Content = "one", FontName = "Segoe UI", FontSize = 20, Red = 1,
            BoxSize = new TextBoxSize(200, Math.Ceiling(new LayerTextStyle { FontName = "Segoe UI", FontSize = 20 }.LineHeight
                + 2 * LayerTextStyle.Padding)) };
        var crowded = tight.AddTextLayer(oneLine, new PointD(20, 20))!;
        OpenDocument(tight); await WaitForFramesAsync();
        Require(await WaitUntilAsync(() => CanEditLayerCommands, 6000), "the canvas stayed shut for the overflowing box");
        OpenInlineText(tight.Snapshot.Manifest.Layers.Single(record => record.Id == crowded.Id));
        await WaitForFramesAsync();
        Require(inlineBox is not null && inlineOverflow is (null, null) && inlineBox.Children.Count == 10,
            $"a box that holds its one line was marked as if it overflowed: children={inlineBox?.Children.Count ?? -1}");
        inlineEditor!.Text = "one\ntwo\nthree";
        await WaitForFramesAsync();
        Require(inlineOverflow.Horizontal is not null && inlineOverflow.Vertical is not null
                && inlineBox!.Children.Count == 12,
            $"three lines in a one-line box drew no plus: children={inlineBox?.Children.Count ?? -1}");
        var cross = inlineOverflow.Horizontal!;
        var stem = inlineOverflow.Vertical!;
        Require(cross.X1 < cross.X2 && Math.Abs(cross.Y1 - cross.Y2) < .001 && stem.Y1 < stem.Y2 && Math.Abs(stem.X1 - stem.X2) < .001,
            "the plus was drawn, but not as a cross");
        inlineEditor!.Text = "one";
        await WaitForFramesAsync();
        Require(inlineOverflow is (null, null) && inlineBox!.Children.Count == 10,
            "the plus stayed after the words fit the box again");
        report["inlineOverflowMarksABoxThatCannotHoldItsWords"] = "passed";

        // A face chosen while letters are selected belongs to those letters, not to the layer (the Mac's
        // previewFont → LayerTextStyle.setFont(_:in:)). The selection is written through the same property the
        // caret moves, which the probe does not fabricate; the caret's own behaviour stays an interactive check.
        var mixed = EditorDocument.Create(320, 240);
        var runLayer = mixed.AddTextLayer(new LayerTextStyle { Content = "abcd", FontName = "Segoe UI", FontSize = 40, Red = 1 },
            new PointD(30, 30))!;
        OpenDocument(mixed); await WaitForFramesAsync();
        Require(await WaitUntilAsync(() => CanEditLayerCommands, 6000), "the canvas stayed shut for the run test");
        OpenInlineText(mixed.Snapshot.Manifest.Layers.Single(record => record.Id == runLayer.Id));
        await WaitForFramesAsync();
        Require(mixed.ActiveTextDraft is not null, "the run test opened no draft");
        inlineEditor!.Select(1, 2);
        Require(inlineEditor.SelectionStart == 1 && inlineEditor.SelectionLength == 2,
            $"the editor did not take the selection: {inlineEditor.SelectionStart}+{inlineEditor.SelectionLength}");
        Require(ApplyDraftFont(mixed, "Arial Black"), "applying a face to a selection was refused");
        var runs = mixed.ActiveTextDraft!.Style.FontRuns;
        Require(runs is not null && runs.Count == 1 && runs[0].FontName == "Arial Black"
                && runs[0].Location == 1 && runs[0].Length == 2 && mixed.ActiveTextDraft.Style.FontName == "Segoe UI",
            $"the selected letters did not become one run: runs={runs?.Count ?? -1} base={mixed.ActiveTextDraft.Style.FontName}");
        CommitInlineText();
        await WaitForFramesAsync();
        var carried = mixed.Snapshot.Manifest.Layers.Single(record => record.Id == runLayer.Id).Text!;
        Require(carried.FontRuns is [{ Length: 2, FontName: "Arial Black" }] && carried.FontName == "Segoe UI"
                && carried.Content == "abcd",
            $"the run did not survive the commit: runs={carried.FontRuns?.Count ?? -1} base={carried.FontName}");

        OpenInlineText(mixed.Snapshot.Manifest.Layers.Single(record => record.Id == runLayer.Id));
        await WaitForFramesAsync();
        inlineEditor!.Select(0, 0);
        Require(ApplyDraftFont(mixed, "Times New Roman"), "applying a face with nothing selected was refused");
        var wholesale = mixed.ActiveTextDraft!.Style;
        Require(wholesale.FontName == "Times New Roman" && wholesale.FontRuns is null,
            $"a whole-text face change left runs behind: {wholesale.FontName} runs={wholesale.FontRuns?.Count ?? -1}");
        CloseInlineText(cancelling: true);
        report["inlineSelectionTakesTheChosenFaceAsARun"] = "passed";

        // The palette paints text the way the original's does while a draft is open: onto the selected letters,
        // unless the paint target is the mask, in which case the letters keep their color.
        var painted = EditorDocument.Create(320, 240);
        var letterLayer = painted.AddTextLayer(new LayerTextStyle { Content = "abcd", FontName = "Segoe UI", FontSize = 40, Red = 1 },
            new PointD(30, 30))!;
        OpenDocument(painted); await WaitForFramesAsync();
        Require(await WaitUntilAsync(() => CanEditLayerCommands, 6000), "the canvas stayed shut for the palette test");
        OpenInlineText(painted.Snapshot.Manifest.Layers.Single(record => record.Id == letterLayer.Id));
        await WaitForFramesAsync();
        var firstImage = painted.Snapshot.Images[letterLayer.Id];
        inlineEditor!.Select(1, 2);
        paintOnMask = true;
        ApplyPaletteColor(Windows.UI.Color.FromArgb(255, 0x00, 0x80, 0x00));
        Require(painted.ActiveTextDraft!.Style.ColorRuns is null
                && ReferenceEquals(painted.Snapshot.Images[letterLayer.Id], firstImage)
                && foregroundColor.G == 0x80,
            "painting into a mask still recolored the letters");
        paintOnMask = false;
        ApplyPaletteColor(Windows.UI.Color.FromArgb(255, 0x00, 0x80, 0x00));
        await WaitForFramesAsync();
        var colored = painted.ActiveTextDraft!.Style;
        var colorRun = colored.ColorRuns is { Count: 1 } only ? only[0] : null;
        Require(colorRun is not null && colorRun.Location == 1 && colorRun.Length == 2
                && Math.Abs(colorRun.Green - 0x80 / 255.0) < 1e-6 && colored.Red == 1 && colored.Green == 0,
            $"two letters did not become one color run: runs={colored.ColorRuns?.Count ?? -1}");
        Require(!ReferenceEquals(painted.Snapshot.Images[letterLayer.Id], firstImage),
            "the recolored preview never reached the layer's pixels");
        CommitInlineText();
        await WaitForFramesAsync();
        var carriedColor = painted.Snapshot.Manifest.Layers.Single(record => record.Id == letterLayer.Id).Text!;
        Require(carriedColor.ColorRuns is [{ Location: 1, Length: 2 }] && carriedColor.Red == 1
                && carriedColor.Green == 0 && carriedColor.Blue == 0,
            $"the run did not survive the commit: runs={carriedColor.ColorRuns?.Count ?? -1}");

        OpenInlineText(painted.Snapshot.Manifest.Layers.Single(record => record.Id == letterLayer.Id));
        await WaitForFramesAsync();
        inlineEditor!.Select(0, 0);
        ApplyPaletteColor(Windows.UI.Color.FromArgb(255, 0x00, 0x00, 0x80));
        await WaitForFramesAsync();
        Require(painted.ActiveTextDraft is not null, "the picker's report closed the draft it was painting");
        var whole = painted.ActiveTextDraft!.Style;
        Require(whole.ColorRuns is null && Math.Abs(whole.Blue - 0x80 / 255.0) < 1e-6 && whole.Red == 0 && whole.Green == 0,
            $"an empty selection did not set the text's own color: runs={whole.ColorRuns?.Count ?? -1} "
            + $"rgb=({whole.Red:0.##}, {whole.Green:0.##}, {whole.Blue:0.##})");
        CloseInlineText(cancelling: true);
        await WaitForFramesAsync();
        report["inlinePalettePaintsTheSelectedLetters"] = "passed";

        // Option with the arrows sets spacing (InlineTextEditor.swift:44-58). The chord itself cannot be pushed
        // into a window from here, so what is tested is the seam the key handler calls — the same method the
        // press runs, including the fallback the original has for when nothing is being typed.
        // The chord table the key reader consults. Everything here except the two modifier reads is the mapping
        // a rebind or a wrong arrow would break, so it is asked directly rather than through a keystroke.
        var arrows = new (VirtualKey Key, bool Leading, bool Increase)[]
        {
            (VirtualKey.Left, false, false), (VirtualKey.Right, false, true),
            (VirtualKey.Up, true, false), (VirtualKey.Down, true, true),
        };
        foreach (var (key, expectLeading, expectIncrease) in arrows)
        {
            Require(TrySpacingChord(key, option: true, shift: false, out bool leading, out bool increase, out double step)
                  && leading == expectLeading && increase == expectIncrease && Math.Abs(step - 1) < 1e-9,
                $"Option+{key} is not the spacing chord the original binds: leading={leading} increase={increase} step={step:0.#}");
            Require(TrySpacingChord(key, option: true, shift: true, out _, out _, out double ten) && Math.Abs(ten - 10) < 1e-9,
                $"Shift on Option+{key} is supposed to make the step ten, it said {ten:0.#}");
            Require(!TrySpacingChord(key, option: false, shift: false, out _, out _, out _),
                $"the bare {key} arrow is the caret's, not a spacing chord");
        }
        Require(!TrySpacingChord(VirtualKey.A, option: true, shift: false, out _, out _, out _),
            "Option plus a letter is not one of the four spacing chords");

        var spacing = EditorDocument.Create(320, 240);
        var spacingLayer = spacing.AddTextLayer(new LayerTextStyle { Content = "Hn", FontName = "Segoe UI", FontSize = 40, Red = 1 },
            new PointD(30, 40))!;
        OpenDocument(spacing); await WaitForFramesAsync();
        Require(await WaitUntilAsync(() => CanEditLayerCommands, 6000), "the canvas stayed shut for the spacing test");
        OpenInlineText(spacing.Snapshot.Manifest.Layers.Single(record => record.Id == spacingLayer.Id));
        await WaitForFramesAsync();
        Require(spacing.ActiveTextDraft?.Style is { Leading: 0, Tracking: 0 },
            $"the spacing fixture did not start at auto spacing: leading={spacing.ActiveTextDraft?.Style.Leading ?? -1} "
            + $"tracking={spacing.ActiveTextDraft?.Style.Tracking ?? -1}");
        var atAuto = spacing.ActiveTextDraft!.Style;
        Require(Math.Abs(atAuto.LineHeight - 48) < 1e-9,
            $"a 40-point face should auto-space at 48, it said {atAuto.LineHeight:0.##}");
        Require(NudgeTextSpacing(leading: false, increase: true, step: 1)
              && NudgeTextSpacing(leading: true, increase: true, step: 1),
            "the first spacing step was refused");
        var nudged = spacing.ActiveTextDraft!.Style;
        Require(Math.Abs(nudged.Tracking - 1) < 1e-9 && Math.Abs(nudged.Leading - 49) < 1e-9,
            $"the keys moved the wrong amounts: tracking {nudged.Tracking:0.##}, leading {nudged.Leading:0.##}");
        var previewedSpacing = spacing.Snapshot.Manifest.Layers.Single(record => record.Id == spacingLayer.Id).Text!;
        Require(Math.Abs(previewedSpacing.Leading - 49) < 1e-9 && Math.Abs(previewedSpacing.Tracking - 1) < 1e-9,
            $"the canvas preview did not follow what was set: leading {previewedSpacing.Leading:0.##}, "
            + $"tracking {previewedSpacing.Tracking:0.##}");
        Require(NudgeTextSpacing(leading: true, increase: true, step: 10)
              && Math.Abs(spacing.ActiveTextDraft!.Style.Leading - 59) < 1e-9,
            $"Shift is supposed to make the step ten: leading {spacing.ActiveTextDraft?.Style.Leading ?? -1:0.##}");
        Require(NudgeTextSpacing(leading: false, increase: false, step: 1000)
              && Math.Abs(spacing.ActiveTextDraft!.Style.Tracking - (-100)) < 1e-9,
            $"tracking is supposed to stop at -100, it said {spacing.ActiveTextDraft?.Style.Tracking ?? 0:0.##}");
        Require(NudgeTextSpacing(leading: true, increase: false, step: 1000)
              && Math.Abs(spacing.ActiveTextDraft!.Style.Leading - 1) < 1e-9,
            $"leading is supposed to stop at one point, it said {spacing.ActiveTextDraft?.Style.Leading ?? 0:0.##}");
        Require(NudgeTextSpacing(leading: true, increase: false, step: 5)
              && Math.Abs(spacing.ActiveTextDraft!.Style.Leading - 1) < 1e-9 && spacing.CanUndo is false,
            "tightening below the floor either moved leading or booked a step");
        CloseInlineText(cancelling: true);
        await WaitForFramesAsync();
        // Nothing being typed means the same keys change what the next click types with (TypeTool.swift:389-392).
        textDefaults = new LayerTextStyle { FontName = "Segoe UI", FontSize = 24, Red = 1 };
        Require(NudgeTextSpacing(leading: false, increase: true, step: 10) && Math.Abs(textDefaults.Tracking - 10) < 1e-9,
            $"with no draft the keys are supposed to move the defaults, they left tracking at {textDefaults.Tracking:0.##}");
        Require(BeginNewTextDraft(new PointD(60, 60), null), "the defaults test could not start new text");
        await WaitForFramesAsync();
        Require(document!.ActiveTextDraft is not null, "the next click did not open a draft");
        var freshTracking = document!.ActiveTextDraft!.Style.Tracking;
        Require(Math.Abs(freshTracking - 10) < 1e-9,
            $"the next click did not type with the remembered tracking: {freshTracking:0.##}");
        CloseInlineText(cancelling: true);
        await WaitForFramesAsync();
        report["optionArrowsSetTrackingAndLeading"] = "passed";

        // The editor is invisible on purpose, so the only thing a screen reader has to say about it is the name.
        // Asked through the peer a reader actually queries, not the attached property, so a name that never
        // reaches automation reddens; and the strings are required to resolve, because Loc falls back to the key
        // and an equality against the same fallback would pass with nothing translated.
        Require(Loc.T("A_InlineText") != "A_InlineText" && Loc.T("A_InlineTextNew") != "A_InlineTextNew",
            "the inline editor's accessibility strings are missing from the resources");
        var spoken = EditorDocument.Create(320, 240);
        var titleLayer = spoken.AddTextLayer(new LayerTextStyle { Content = "abc", FontName = "Segoe UI", FontSize = 30, Red = 1 },
            new PointD(20, 20))!;
        Require(spoken.RenameLayer(titleLayer.Id, "Title"), "the layer at the start of the reader test was not renamed");
        OpenDocument(spoken); await WaitForFramesAsync();
        Require(await WaitUntilAsync(() => CanEditLayerCommands, 6000), "the canvas stayed shut for the reader test");
        OpenInlineText(spoken.Snapshot.Manifest.Layers.Single(record => record.Id == titleLayer.Id));
        await WaitForFramesAsync();
        var spokenName = FrameworkElementAutomationPeer.CreatePeerForElement(inlineEditor!)!.GetName();
        Require(spokenName == Loc.F("A_InlineText", "Title"),
            $"what a reader hears is not the layer being edited: \"{spokenName}\"");
        CloseInlineText(cancelling: true);
        await WaitForFramesAsync();
        // New text has no layer yet, so the same control says what it is instead of naming a layer that does not exist.
        Require(BeginNewTextDraft(new PointD(60, 60), null), "the reader test could not start new text on the canvas");
        await WaitForFramesAsync();
        spokenName = FrameworkElementAutomationPeer.CreatePeerForElement(inlineEditor!)!.GetName();
        Require(spokenName == Loc.T("A_InlineTextNew"),
            $"new text was named after a layer it does not have: \"{spokenName}\"");
        CloseInlineText(cancelling: true);
        await WaitForFramesAsync();
        report["inlineEditorIsNamedForAReader"] = "passed";

        // The box answers ⌘Z itself (InlineTextEditor.swift:18-25): a typed step lands back inside the editor,
        // the document's history is not what the keystroke reaches, the box opens clean so the first undo cannot
        // wipe the words it was seeded with (:215), and a run of keystrokes is one step rather than three.
        var undoScene = EditorDocument.Create(320, 240);
        var undoLayer = undoScene.AddTextLayer(
            new LayerTextStyle { Content = "Alpha", FontName = "Segoe UI", FontSize = 28, Red = 1 }, new PointD(20, 20))!;
        OpenDocument(undoScene); await WaitForFramesAsync();
        Require(await WaitUntilAsync(() => CanEditLayerCommands, 6000), "the undo fixture found the canvas shut");
        OpenInlineText(undoScene.Snapshot.Manifest.Layers.Single(record => record.Id == undoLayer.Id));
        await WaitForFramesAsync();
        Require(inlineEditor is not null && undoScene.ActiveTextDraft is not null, "the undo fixture opened no editor");
        // Not asserted here: a draft in flight hides the document's history (the first group of this method pins
        // that rule), so a CanUndo reading inside the box says nothing about what the box booked. The control
        // scene at the end is what measures that.
        OnUndo(this, new RoutedEventArgs()); await WaitForFramesAsync();
        Require(inlineEditor!.Text == "Alpha" && undoScene.ActiveTextDraft!.Style.Content == "Alpha",
            $"an empty local stack reached past the box: text={inlineEditor.Text} "
            + $"draft={undoScene.ActiveTextDraft!.Style.Content}");
        // Re-editing a layer's words puts the caret after them (InlineTextEditor.swift:220-223), so what is typed
        // next joins the end of the word rather than fronting it.
        Require(inlineEditor.SelectionStart == 5 && inlineEditor.SelectionLength == 0,
            $"the box opened with the caret at {inlineEditor.SelectionStart}+{inlineEditor.SelectionLength}, "
            + "not after the five letters it is editing");
        inlineEditor.Text = "AlphaBeta"; await WaitForFramesAsync();
        Require(undoScene.ActiveTextDraft!.Style.Content == "AlphaBeta", "the typed word never reached the draft");
        OnUndo(this, new RoutedEventArgs()); await WaitForFramesAsync();
        Require(inlineEditor.Text == "Alpha" && undoScene.ActiveTextDraft!.Style.Content == "Alpha",
            $"Ctrl+Z did not take the word back inside the box: text={inlineEditor.Text} "
            + $"draft={undoScene.ActiveTextDraft!.Style.Content}");
        OnRedo(this, new RoutedEventArgs()); await WaitForFramesAsync();
        Require(inlineEditor.Text == "AlphaBeta", $"Ctrl+Y did not put the word back: text={inlineEditor.Text}");
        inlineEditor.Select(inlineEditor.Text.Length, 0);
        foreach (var letter in "xyz")
        {
            inlineEditor.Text += letter;
            inlineEditor.Select(inlineEditor.Text.Length, 0);
            await WaitForFramesAsync();
        }
        Require(inlineEditor.Text == "AlphaBetaxyz", $"the typed run did not land: text={inlineEditor.Text}");
        // A face applied to a selection is NOT in this stack: the Mac routes a face, a colour and the spacing
        // chords through the draft style (TypeTool.swift:365-372 and :383-394), never through the text view as an
        // edit, so Ctrl+Z inside the box answers the words only. Applying it before the undo also proves the undo
        // took back the typed run rather than the attribute.
        inlineEditor.Select(0, 5);
        Require(ApplyDraftFont(undoScene, "Arial Black"), "the fixture could not put a face on the first word");
        Require(undoScene.ActiveTextDraft!.Style.FontRuns is { Count: > 0 }, "the face never became a run");
        report["inlineTextUndoStackTrace"] = string.Join("|", inlineTextUndo.Select(s => s.NewText))
            + $" shown={inlineTextShown} typing={inlineTextTyping} runEnd={inlineTextRunEnd}"
            + $" redo={inlineTextRedo.Count} box={inlineEditor.Text} draft={undoScene.ActiveTextDraft!.Style.Content}";
        OnUndo(this, new RoutedEventArgs()); await WaitForFramesAsync();
        Require(inlineEditor.Text == "AlphaBeta" && undoScene.ActiveTextDraft!.Style.FontRuns is { Count: > 0 },
            $"Ctrl+Z inside the box took back something other than the typed run: text={inlineEditor.Text} "
            + $"runs={undoScene.ActiveTextDraft!.Style.FontRuns?.Count ?? -1}");
        // Redo the run, then put the face on the letters that run typed, and take the run back again: a run that
        // describes characters which are no longer there is what skipping the splice leaves behind.
        OnRedo(this, new RoutedEventArgs()); await WaitForFramesAsync();
        inlineEditor.Select(9, 3);
        Require(ApplyDraftFont(undoScene, "Arial Black"), "the fixture could not put a face on the typed letters");
        OnUndo(this, new RoutedEventArgs()); await WaitForFramesAsync();
        var staleRuns = (undoScene.ActiveTextDraft!.Style.FontRuns ?? new List<LayerTextFontRun>())
            .Where(run => run.Location + run.Length > inlineEditor.Text.Length).ToList();
        Require(inlineEditor.Text == "AlphaBeta" && staleRuns.Count == 0,
            $"the undo left run books describing letters that are gone: text={inlineEditor.Text} "
            + $"stale=[{string.Join(", ", staleRuns.Select(run => $"{run.Location}+{run.Length}"))}]");
        // Counting steps here would prove nothing: a draft in flight hides the document's history, which is what
        // the first group of this method pins. The claim that carries weight is that a session which used the
        // box's own undo books exactly what the same edits book when nobody presses Ctrl+Z — so a second scene
        // makes the same edits without any undo and its steps are counted as the control.
        CommitInlineText(); await WaitForFramesAsync();
        Require(inlineEditor is null && undoScene.CanUndo,
            $"the commit did not open the document history again: open={inlineEditor is not null} "
            + $"canUndo={undoScene.CanUndo}");
        string Step(EditorDocument scene, Guid layer)
        {
            var record = scene.Snapshot.Manifest.Layers.FirstOrDefault(row => row.Id == layer);
            return $"{(record is null ? "no-layer" : record.Text!.Content)}/layers={scene.Snapshot.Manifest.Layers.Count}";
        }
        var walk = new List<string>();
        for (int extra = 0; extra < 5 && undoScene.CanUndo; extra++)
        {
            OnUndo(this, new RoutedEventArgs()); await WaitForFramesAsync();
            walk.Add(Step(undoScene, undoLayer.Id));
        }
        var control = EditorDocument.Create(320, 240);
        var controlLayer = control.AddTextLayer(
            new LayerTextStyle { Content = "Alpha", FontName = "Segoe UI", FontSize = 28, Red = 1 }, new PointD(20, 20))!;
        OpenDocument(control); await WaitForFramesAsync();
        Require(await WaitUntilAsync(() => CanEditLayerCommands, 6000), "the control fixture found the canvas shut");
        OpenInlineText(control.Snapshot.Manifest.Layers.Single(record => record.Id == controlLayer.Id));
        await WaitForFramesAsync();
        void controlEditorText(string text)
        {
            inlineEditor!.Text = text;
            inlineEditor.Select(inlineEditor.Text.Length, 0);
        }
        controlEditorText("AlphaBeta");
        await WaitForFramesAsync();
        foreach (var letter in "xyz") { controlEditorText(inlineEditor!.Text + letter); await WaitForFramesAsync(); }
        inlineEditor!.Select(0, 5);
        Require(ApplyDraftFont(control, "Arial Black"), "the control could not put a face on the first word");
        CommitInlineText(); await WaitForFramesAsync();
        var controlWalk = new List<string>();
        while (control.CanUndo && controlWalk.Count < 5) { control.Undo(); controlWalk.Add(Step(control, controlLayer.Id)); }
        report["inlineTextUndoHistoryWalk"] = $"undone={string.Join(" ", walk)} control={string.Join(" ", controlWalk)}";
        Require(walk.Count == controlWalk.Count && walk.Count >= 2 && walk[0] == controlWalk[0],
            $"the box's own undo left a history of a different shape than the same edits without it: "
            + $"{walk.Count} steps vs {controlWalk.Count} ({string.Join(" ", walk)} / {string.Join(" ", controlWalk)})");
        report["inlineTextUndoStepsInsideTheBox"] = "passed";

        // C13's accessibility half is first a count, not a rule: ask every control the window has actually
        // realized, and that a screen reader can operate, what it reports as its name. Written as a measurement
        // because a ceiling set before the numbers are read is a ceiling on an assumption.
        var sweep = AccessibleControls(RootGrid);
        report["accessibilityInventory"] = FormatInventory(sweep.Named, sweep.Unnamed);

        // C13c: every control the strips and panels build now has to answer for itself. Asked through the peer,
        // for the exact string the adjacent label or the resource gives it, so a name that is present but wrong
        // — or a LabeledBy that never resolved — reddens instead of quietly passing.
        var resourceKeys = new[] { "A_NavZoom", "A_EyeTarget", "A_HealMode", "A_GradientShape", "A_GradientStyle",
            "A_LassoKind", "A_SelectionMode", "A_CropRatio", "A_ShapeKind", "A_BlurMode",
            "A_ZoomIn", "A_ZoomOut", "A_SwapColors", "A_ResetColors" };
        // Loc falls back to the key itself, so comparing a control with Loc.T(key) alone would stay true even
        // with nothing in the resources at all; the keys are required to resolve first.
        var untranslated = resourceKeys.Where(key => Loc.T(key) == key).ToList();
        Require(untranslated.Count == 0,
            $"the automation names are missing from the resources: {string.Join(", ", untranslated)}");
        var namedControls = new (FrameworkElement Element, string Id, string Expected)[]
        {
            (BrushSizeSlider, nameof(BrushSizeSlider), BrushSizeLabel.Text),
            (BrushHardnessSlider, nameof(BrushHardnessSlider), BrushHardnessLabel.Text),
            (BrushOpacitySlider, nameof(BrushOpacitySlider), BrushOpacityLabel.Text),
            (GradientOpacitySlider, nameof(GradientOpacitySlider), GradientOpacityLabel.Text),
            (ShapeCornerSlider, nameof(ShapeCornerSlider), ShapeCornerLabel.Text),
            (ShapeLineWidthSlider, nameof(ShapeLineWidthSlider), ShapeLineWidthLabel.Text),
            (BlurRadiusBox, nameof(BlurRadiusBox), BlurRadiusLabel.Text),
            (WandToleranceBox, nameof(WandToleranceBox), WandToleranceLabel.Text),
            (WandSampleCombo, nameof(WandSampleCombo), WandSampleLabel.Text),
            (BlendCombo, nameof(BlendCombo), BlendLabel.Text),
            (OpacitySlider, nameof(OpacitySlider), LayerOpacityLabel.Text),
            (OpacityBox, nameof(OpacityBox), LayerOpacityLabel.Text),
            (NavigationZoomBox, nameof(NavigationZoomBox), Loc.T("A_NavZoom")),
            (EyedropperTargetCombo, nameof(EyedropperTargetCombo), Loc.T("A_EyeTarget")),
            (HealingModeCombo, nameof(HealingModeCombo), Loc.T("A_HealMode")),
            (GradientShapeCombo, nameof(GradientShapeCombo), Loc.T("A_GradientShape")),
            (GradientStyleCombo, nameof(GradientStyleCombo), Loc.T("A_GradientStyle")),
            (LassoKindCombo, nameof(LassoKindCombo), Loc.T("A_LassoKind")),
            (SelectionReplaceButton, nameof(SelectionReplaceButton), SelectionModeName(LassoSelectionMode.Replace)),
            (SelectionAddButton, nameof(SelectionAddButton), SelectionModeName(LassoSelectionMode.Add)),
            (SelectionSubtractButton, nameof(SelectionSubtractButton), SelectionModeName(LassoSelectionMode.Subtract)),
            (CropRatioCombo, nameof(CropRatioCombo), Loc.T("A_CropRatio")),
            (ShapeKindCombo, nameof(ShapeKindCombo), Loc.T("A_ShapeKind")),
            (BlurModeCombo, nameof(BlurModeCombo), Loc.T("A_BlurMode")),
            (ZoomInButton, nameof(ZoomInButton), Loc.T("A_ZoomIn")),
            (ZoomOutButton, nameof(ZoomOutButton), Loc.T("A_ZoomOut")),
            (SwapColorsChip, nameof(SwapColorsChip), Loc.T("A_SwapColors")),
            (ResetColorsChip, nameof(ResetColorsChip), Loc.T("A_ResetColors")),
        };
        var offender = namedControls.FirstOrDefault(entry =>
            string.IsNullOrWhiteSpace(entry.Expected)
            || FrameworkElementAutomationPeer.CreatePeerForElement(entry.Element)?.GetName() != entry.Expected);
        var reported = offender.Element is null ? "" : FrameworkElementAutomationPeer.CreatePeerForElement(offender.Element)?.GetName();
        Require(offender.Element is null,
            $"a control a reader asks about does not report what it is for: {offender.Id} says \"{reported}\", "
            + $"expected \"{offender.Expected}\"");
        // Panel rows are built in a loop, so they are held to the same rule by the sweep above rather than one by one.
        var bare = sweep.Unnamed.Count(entry => entry.EndsWith("@AdjustmentPanel", StringComparison.Ordinal));
        Require(bare == 0, $"the adjustment panel still builds {bare} sliders that report no name");
        report["stripsAndPanelsNameTheirControls"] = "passed";
    }
}
#endif
