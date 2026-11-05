using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using System.Runtime.CompilerServices;
using Windows.System;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private TextBox? inlineEditor;
    private Border? inlineFrame;
    private Canvas? inlineBox;
    // The Mac's session.textDefaults (EditorSession.swift:235): the face, size and leading the NEXT click types
    // with, remembered when a draft commits. The port has no character panel, so the text dialog is what edits it.
    private LayerTextStyle textDefaults = new() { FontName = "Segoe UI" };
    private PointD? textDragFrom; // the Text tool's rubber band (beginTextGesture, InlineTextEditor.swift:516-531)
    private Rect textDragBox;
    private Rectangle? textBand;
    // The plus drawn inside the bottom-right handle while the box cannot hold its words.
    private (Line? Horizontal, Line? Vertical) inlineOverflow;
    // The eight squares the original draws (InlineTextEditor.swift:411-419). They mark the box; the grab itself
    // is a band along each edge, which is what BeginInlineResize tests.
    private readonly Rectangle[] inlineHandles = new Rectangle[8];
    private (int Handle, PointD Start)? inlineResize;
    // WinUI raises TextChanged from the text view, which only exists once the template has been applied; a
    // control that is still waiting for layout stays silent and the canvas keeps showing the old words.
    // Watching the Text property instead answers a keystroke and a programmatic set the same way.
    private long? inlineTextToken;
    // The box's own undo stack, as the Mac gives its text view (InlineTextEditor.swift:18-25: a private
    // UndoManager, and undo(_:) answers ⌘Z there so the window's history is never reached while typing; :215
    // clears it when the box opens). WinUI's TextBox exposes neither an Undo() to call nor a way to switch its
    // built-in stack off, so the steps are kept here: one entry per typed run, each holding the style and the
    // caret as they were before that run.
    private readonly Stack<InlineTextStep> inlineTextUndo = new();
    private readonly Stack<InlineTextStep> inlineTextRedo = new();
    private string inlineTextShown = "";
    private (int Location, int Length) inlineTextCaret;
    private bool inlineTextTyping; // a run of single-character insertions counts as one step
    private int inlineTextRunEnd = -1; // where the last character of that run landed, so the next must follow it
    private bool inlineTextRestoring;

    /// <summary>One edit inside the box, kept the way NSTextView keeps them: the range it replaced and what it
    /// put there, not a copy of the whole style. Undoing a typed word then leaves a face applied afterwards
    /// alone, which is what the original does - attributes go onto the draft style (TypeTool.swift:365-372,
    /// :383-394) and never into this stack.</summary>
    private readonly record struct InlineTextStep(string OldText, string NewText, int Location, int Length,
        int WithLength, (int Location, int Length) CaretBefore, (int Location, int Length) CaretAfter);
#if DEBUG
    /// <summary>Which call opened, refused or settled the draft — the same idiom as pointerStrokeClosedBy. A
    /// probe that finds no draft can then say who took it away instead of blaming the editor for a gesture
    /// some other part of the window made.</summary>
    internal string? inlineDraftTrace;
#endif

    /// <summary>
    /// Opens the on-canvas editor for an existing text layer. The words are drawn by the canvas, not by the
    /// control: every keystroke goes through <see cref="EditorDocument.UpdateTextDraftContent(string)"/>, which
    /// re-rasterizes the layer, so what the user reads is the pixels the commit will leave — with its mask,
    /// its effects and its placement already applied. The control is therefore transparent; it owns the caret,
    /// the selection and the keyboard. The macOS original does the same with a clear text colour
    /// (InlineTextEditor.swift:190).
    /// </summary>
    /// <summary>
    /// Starts new text at a clicked point, as the original's beginText(at:) does (TypeTool.swift:235-262): the
    /// remembered face and leading, no words yet, the colour taken from the foreground unless the mask is what is
    /// being painted, and the first baseline on the pointer. A dragged band (beginText(in:), :332-340) instead
    /// fixes the box at the size that was drawn and puts its top-left where it was drawn — no baseline shift,
    /// because the box is what the user sized.
    /// </summary>
    private bool BeginNewTextDraft(PointD point, SizeD? box, [CallerMemberName] string site = "")
    {
        if (document is not { } owner || owner.ActiveTextDraft is not null || !CanEditLayerCommands) return false;
        var style = ManifestJson.Clone(textDefaults);
        style.Content = "";
        style.ColorRuns = null;
        style.FontRuns = null;
        if (!paintOnMask)
        {
            style.Red = foregroundColor.R / 255.0;
            style.Green = foregroundColor.G / 255.0;
            style.Blue = foregroundColor.B / 255.0;
        }
        PointD origin;
        LayerTransform? placement = null;
        if (box is { } drawn)
        {
            var size = new TextBoxSize(Math.Max(16, Math.Round(drawn.Width, MidpointRounding.AwayFromZero)),
                Math.Max(16, Math.Round(drawn.Height, MidpointRounding.AwayFromZero)));
            if (!size.IsValid)
            {
                Report(Loc.T("S_TextRefused"), null);
                return false;
            }
            style.BoxSize = size;
            origin = point;
            placement = new LayerTransform { Origin = point, Size = new SizeD(size.Width, size.Height) };
        }
        else
        {
            // A click makes point text: no box of its own, so what is typed decides the size — including when
            // the remembered defaults came from a box that was dragged earlier (beginText, TypeTool.swift:250-253).
            style.BoxSize = null;
            var line = SafeFirstBaseline(style);
            origin = new PointD(point.X - LayerTextStyle.Padding, line is { } baseline ? point.Y - baseline : point.Y);
        }
        if (!owner.BeginTextDraft(null, origin, placement, style))
        {
#if DEBUG
            inlineDraftTrace = $"{site}: the document refused a new-text draft at ({point.X:0.#}, {point.Y:0.#})";
#endif
            return false;
        }
        OpenInlineEditor(style);
        return true;
    }

    /// <summary>The distance a click's baseline sits below the layer's top edge. An invalid style measures as
    /// nothing: the pointer handler must not throw, and the draft is refused by the document a line later.</summary>
    private static double? SafeFirstBaseline(LayerTextStyle style)
    {
        try
        {
            return TextRasterizer.FirstBaseline(style);
        }
        catch (ProjectFormatException)
        {
            return null;
        }
    }

    private void OpenInlineText(ProjectLayerRecord layer, [CallerMemberName] string site = "")
    {
        if (!CanEditLayerCommands || document is not { } owner || layer.Text is null || owner.ActiveTextDraft is not null)
        {
#if DEBUG
            inlineDraftTrace = $"{site}: refused (editable={CanEditLayerCommands} hasText={layer.Text is not null} "
                + $"other draft={document?.ActiveTextDraft?.LayerId})";
#endif
            return;
        }
        owner.SetActiveLayer(layer.Id);
        if (!owner.BeginTextDraft(layer.Id, layer.Transform.Origin, layer.Transform, layer.Text))
        {
#if DEBUG
            inlineDraftTrace = $"{site}: the document refused a draft on {layer.Id}";
#endif
            return;
        }
#if DEBUG
        inlineDraftTrace = $"{site}: draft opened on {layer.Id}";
#endif
        DetachInlineEditor();
        OpenInlineEditor(layer.Text);
    }

    /// <summary>Builds the transparent editor and its eight handles for the draft that is open. The words are
    /// drawn by the canvas rather than here, so new text — no layer, no pixels of its own yet — gets the very
    /// same control as editing an existing layer does.</summary>
    private void OpenInlineEditor(LayerTextStyle style)
    {
        DetachInlineEditor();
        inlineFrame = new Border
        {
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x4C, 0xC2, 0xFF)),
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(a: 0, r: 0, g: 0, b: 0)),
        };
        inlineEditor = new TextBox
        {
            Text = style.Content,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            FontFamily = new FontFamily(style.FontName),
            FontSize = style.FontSize,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(a: 0, r: 0, g: 0, b: 0)),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(a: 0, r: 0, g: 0, b: 0)),
        };
        inlineTextToken = inlineEditor.RegisterPropertyChangedCallback(TextBox.TextProperty, OnInlineTextChanged);
        inlineEditor.SelectionChanged += (_, _) => RefreshQuickTextControls();
        // The box opens clean, as the Mac's removeAllActions (InlineTextEditor.swift:215): what it was seeded with
        // is the floor of this stack, so one ⌘Z after opening on existing text takes back the typing, not the words.
        inlineTextUndo.Clear();
        inlineTextRedo.Clear();
        inlineTextShown = style.Content;
        inlineTextCaret = (inlineEditor.SelectionStart, inlineEditor.SelectionLength);
        inlineTextTyping = false;
        inlineTextRunEnd = -1;
        // Re-editing a layer's words puts the caret after them, ready to add to what is there, unless a click
        // already placed it (InlineTextEditor.swift:220-223). A programmatic Text set leaves WinUI's selection at
        // zero, so without this the first keystroke lands in front of the existing words and the run rule below
        // groups it with the wrong position.
        if (document?.ActiveTextDraft?.LayerId is not null && style.Content.Length > 0
            && inlineEditor.SelectionStart == 0 && inlineEditor.SelectionLength == 0)
        {
            inlineEditor.Select(style.Content.Length, 0);
            inlineTextCaret = (inlineEditor.SelectionStart, 0);
        }
        inlineEditor.KeyDown += OnInlineKeyDown;
        // The control is invisible on purpose, so it has nothing for a screen reader to read: the Mac's text
        // view speaks its own words, and this one would report an empty name. Which layer is being edited is
        // the thing a reader needs, and new text has no layer yet, so it gets its own string.
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(inlineEditor,
            document?.ActiveTextDraft?.LayerId is { } draftId
            && document.Snapshot.Manifest.Layers.FirstOrDefault(record => record.Id == draftId) is { } draftLayer
                ? Loc.F("A_InlineText", draftLayer.Name) : Loc.T("A_InlineTextNew"));
        // One container carries the placement — mirror and turn — for everything in it, so the frame, the caret
        // and the handles move as the box the draft is showing does.
        inlineBox = new Canvas { IsHitTestVisible = true };
        inlineBox.PointerPressed += OnInlineBoxPointerPressed;
        inlineBox.PointerMoved += OnInlineBoxPointerMoved;
        inlineBox.PointerReleased += OnInlineBoxPointerReleased;
        inlineBox.Children.Add(inlineFrame);
        inlineBox.Children.Add(inlineEditor);
        for (int index = 0; index < inlineHandles.Length; index++)
        {
            inlineHandles[index] = new Rectangle
            {
                Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xFF, 0xFF, 0xFF)),
                Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x4C, 0xC2, 0xFF)),
                StrokeThickness = 1,
            };
            inlineBox.Children.Add(inlineHandles[index]);
        }
        InlineTextLayer.Children.Add(inlineBox);
        InlineTextLayer.Visibility = Visibility.Visible;
        PlaceInlineText();
    }

    /// <summary>Puts the editor, its frame and its handles on the placement the draft is showing: the box's own
    /// top-left in document coordinates, mirrored and turned about its centre exactly as
    /// <see cref="LayerTransform.Point"/> maps it, which is where the rasterized pixels already are. The squares
    /// are six of the box's own units across, so a layer scaled away from one-to-one still shows handles the size
    /// the original draws them.</summary>
    private void PlaceInlineText()
    {
        if (document is not { } owner || owner.ActiveTextDraft is not { } draft) return;
        if (InlinePlacement(draft) is not { } placed) return;
        var size = placed.Size;
        var logical = InlineLogicalSize(draft) ?? size;
        if (inlineBox is { } box)
        {
            Canvas.SetLeft(box, placed.Origin.X);
            Canvas.SetTop(box, placed.Origin.Y);
            box.Width = size.Width;
            box.Height = size.Height;
            // The container turns with the placement but is NOT mirrored: the original mirrors only the text
            // surface (applyMirror, InlineTextEditor.swift:282-299) and keeps the frame and the handles in
            // logical order, so the square on the box's own right edge is the one that grows it.
            var group = new TransformGroup();
            group.Children.Add(new RotateTransform { CenterX = size.Width / 2, CenterY = size.Height / 2,
                Angle = placed.Rotation });
            box.RenderTransform = group;
        }
        foreach (FrameworkElement? element in new FrameworkElement?[] { inlineEditor, inlineFrame })
        {
            if (element is null) continue;
            Canvas.SetLeft(element, 0);
            Canvas.SetTop(element, 0);
            element.Width = size.Width;
            element.Height = size.Height;
        }
        if (inlineEditor is { } caret)
        {
            var surface = new TransformGroup();
            surface.Children.Add(new ScaleTransform { CenterX = size.Width / 2, CenterY = size.Height / 2,
                ScaleX = placed.FlipX ? -1 : 1, ScaleY = placed.FlipY ? -1 : 1 });
            caret.RenderTransform = surface;
        }
        var side = logical.Width > 0 ? size.Width / logical.Width * 6 : 6.0;
        for (int index = 0; index < inlineHandles.Length; index++)
        {
            if (inlineHandles[index] is not { } square) continue;
            var unit = LayerTransform.Handles[index];
            Canvas.SetLeft(square, unit.X * size.Width - side / 2);
            Canvas.SetTop(square, unit.Y * size.Height - side / 2);
            square.Width = side;
            square.Height = logical.Height > 0 ? size.Height / logical.Height * 6 : side;
        }
        if (inlineEditor is { } editor)
        {
            editor.FontFamily = new FontFamily(draft.Style.FontName);
            editor.FontSize = draft.Style.FontSize;
        }
        ShowInlineOverflow(draft, size, logical);
    }

    /// <summary>
    /// Marks a paragraph box that cannot hold what is typed into it, the way the original does with a plus drawn
    /// in the bottom-right handle (<c>InlineTextEditor.swift:420-433</c>). The mark is the layout's own verdict —
    /// <see cref="TextRasterizer.TextOverflows(LayerTextStyle)"/> asks the same line count the painter trims by —
    /// so typing past the bottom shows it and typing back hides it.
    /// </summary>
    private void ShowInlineOverflow(TextDraft draft, SizeD size, SizeD logical)
    {
        if (inlineBox is not { } box) return;
        bool overflowing = InlineOverflows(draft);
        if (inlineOverflow is (null, null) && !overflowing) return;
        if (!overflowing)
        {
            if (inlineOverflow.Horizontal is { } away1) box.Children.Remove(away1);
            if (inlineOverflow.Vertical is { } away2) box.Children.Remove(away2);
            inlineOverflow = default;
            return;
        }
        double scale = logical.Width > 0 ? size.Width / logical.Width : 1;
        double arm = 6 * scale * 0.42, stroke = Math.Max(0.5, scale);
        if (inlineOverflow is (null, null))
        {
            var pen = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 0, 0));
            inlineOverflow = (new Line { Stroke = pen, StrokeThickness = stroke }, new Line { Stroke = pen, StrokeThickness = stroke });
            box.Children.Add(inlineOverflow.Horizontal!);
            box.Children.Add(inlineOverflow.Vertical!);
        }
        var corner = LayerTransform.Handles[4];
        double x = corner.X * size.Width, y = corner.Y * size.Height;
        inlineOverflow.Horizontal!.X1 = x - arm;
        inlineOverflow.Horizontal!.Y1 = y;
        inlineOverflow.Horizontal!.X2 = x + arm;
        inlineOverflow.Horizontal!.Y2 = y;
        inlineOverflow.Vertical!.X1 = x;
        inlineOverflow.Vertical!.Y1 = y - arm;
        inlineOverflow.Vertical!.X2 = x;
        inlineOverflow.Vertical!.Y2 = y + arm;
    }

    /// <summary>Point text has no box to overflow — it is as tall as its lines — and a style the document would
    /// refuse gets no mark rather than an exception on the way to a keystroke.</summary>
    private static bool InlineOverflows(TextDraft draft)
    {
        if (draft.Style.BoxSize is null) return false;
        try
        {
            return TextRasterizer.TextOverflows(draft.Style);
        }
        catch (ProjectFormatException)
        {
            return false;
        }
    }

    /// <summary>The box the draft is showing in its own units: the paragraph box if it has one, the text as it
    /// measures otherwise — the same <c>logicalSize</c> the macOS editor derives in synchronize().</summary>
    private static SizeD? InlineLogicalSize(TextDraft draft)
    {
        try
        {
            return TextRasterizer.TextBoxSize(draft.Style);
        }
        catch (ProjectFormatException)
        {
            return null;
        }
    }

    private LayerTransform? InlinePlacement(TextDraft draft) => draft.Transform
        ?? (draft.LayerId is { } id && document?.Snapshot.Manifest.Layers.FirstOrDefault(record => record.Id == id) is { } record
            ? record.Transform : null);

    /// <summary>Where a document point sits inside the box being edited, in the box's own units. A flipped layer
    /// is not un-mirrored here: the frame and its handles stay in logical order while only the text surface is
    /// mirrored (<c>applyMirror</c>, InlineTextEditor.swift:282-299), so the band on the frame's right edge is
    /// the box's own right edge and grows it.</summary>
    private PointD? InlineLocalPoint(TextDraft draft, PointD point)
    {
        if (InlinePlacement(draft) is not { } placed || InlineLogicalSize(draft) is not { } logical
            || logical.Width <= 0 || logical.Height <= 0) return null;
        var unit = placed.InversePoint(point);
        return new PointD(unit.X * logical.Width, unit.Y * logical.Height);
    }

    /// <summary>Takes the handle whose edge band a document point falls in, the way the original's mouseDown
    /// does (<c>handle(at:)</c>, InlineTextEditor.swift:391-408, then :443-458): outside a band the press belongs
    /// to the caret and nothing is taken, and taking a band on point text turns it into a fixed box of the size
    /// it has right now.</summary>
    private bool BeginInlineResize(PointD point, [CallerMemberName] string site = "")
    {
        if (document is not { } owner || owner.ActiveTextDraft is not { } draft) return false;
        if (InlineLocalPoint(draft, point) is not { } local || InlineLogicalSize(draft) is not { } logical) return false;
        if (LayerTransform.HandleAt(local, logical, LayerTransform.EdgeReach(6, logical)) is not { } handle) return false;
        if (!owner.BeginTextResize(handle, point)) return false;
        inlineResize = (handle, point);
#if DEBUG
        inlineDraftTrace = string.Format("{0}: handle {1} taken at ({2:0.#}, {3:0.#})", site, handle, point.X, point.Y);
#endif
        return true;
    }

    /// <summary>One frame of the taken drag. A frame the document refuses — a box over the ceiling, say — moves
    /// nothing, which is the original's guard rather than an error the user would hear about.</summary>
    private void MoveInlineResize(PointD point)
    {
        if (inlineResize is null || document is not { } owner) return;
        if (!owner.ResizeTextDraft(point)) return;
        PlaceInlineText();
    }

    private void EndInlineResize()
    {
        if (inlineResize is null) return;
        document?.EndTextResize();
        inlineResize = null;
    }

    /// <summary>Where the Text tool's press landed. A press on existing text edits that text instead, and the
    /// band only becomes a draft when the pointer lifts (beginTextGesture, InlineTextEditor.swift:516-531).</summary>
    private void BeginTextBand(PointD point)
    {
        textDragFrom = point;
        textDragBox = default;
    }

    /// <summary>The band growing under the pointer. Preview only: the document is touched once, at the release.</summary>
    private void MoveTextBand(PointD point)
    {
        if (textDragFrom is not { } anchor) return;
        textDragBox = ShapeCommit(anchor, point, false).Box;
        ShowTextBand();
    }

    /// <summary>Shows the Text tool's rubber band while a new box is being dragged out
    /// (drawTextBoxDraft, InlineTextEditor.swift:547-556).</summary>
    private void ShowTextBand()
    {
        if (textDragFrom is null) return;
        if (textBand is null)
        {
            textBand = new Rectangle
            {
                Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x4C, 0xC2, 0xFF)),
                StrokeThickness = 1,
                Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(a: 0, r: 0, g: 0, b: 0)),
                IsHitTestVisible = false,
            };
            InlineTextLayer.Children.Add(textBand);
        }
        InlineTextLayer.Visibility = Visibility.Visible;
        Canvas.SetLeft(textBand, textDragBox.X);
        Canvas.SetTop(textBand, textDragBox.Y);
        textBand.Width = textDragBox.Width;
        textBand.Height = textDragBox.Height;
    }

    private void HideTextBand()
    {
        if (textBand is null) return;
        InlineTextLayer.Children.Remove(textBand);
        textBand = null;
        if (inlineBox is null) InlineTextLayer.Visibility = Visibility.Collapsed;
    }

    /// <summary>Where the band went up and where it came down: under four pixels either way is a click and makes
    /// point text, anything wider is a fixed box of the drawn size (finishTextGesture, :539-546).</summary>
    private void FinishTextGesture()
    {
        if (textDragFrom is null) return;
        var box = textDragBox;
        textDragFrom = null;
        textDragBox = default;
        HideTextBand();
        BeginNewTextDraft(new PointD(box.X, box.Y), box.Width < 4 && box.Height < 4 ? null : new SizeD(box.Width, box.Height));
    }

    private void OnInlineBoxPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(InlineTextLayer).Position;
        if (!BeginInlineResize(new PointD(point.X, point.Y))) return;
        // XAML bubbles a press from the text box before it reaches here, so the caret has already jumped by the
        // time the band claims the drag; the original's hitTest keeps the view from ever seeing that press.
        ((UIElement)sender).CapturePointer(args.Pointer);
        args.Handled = true;
    }

    private void OnInlineBoxPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (inlineResize is null) return;
        var point = args.GetCurrentPoint(InlineTextLayer).Position;
        MoveInlineResize(new PointD(point.X, point.Y));
        args.Handled = true;
    }

    private void OnInlineBoxPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (inlineResize is null) return;
        EndInlineResize();
        ((UIElement)sender).ReleasePointerCapture(args.Pointer);
        args.Handled = true;
    }

    private void OnInlineTextChanged(DependencyObject sender, DependencyProperty args)
    {
#if DEBUG
        inlineDraftTrace = $"editor text changed to {inlineEditor?.Text.Length ?? 0} characters: host={document is not null} "
            + $"draft={document?.ActiveTextDraft is not null}";
#endif
        if (document is not { } owner || owner.ActiveTextDraft is null) return;
        // A restore writes the control's text too; recording that would push the step the undo just took back.
        if (inlineTextRestoring) return;
        PushInlineTextStep(inlineEditor?.Text ?? "");
        var updated = owner.UpdateTextDraftContent(inlineEditor?.Text ?? "");
#if DEBUG
        inlineDraftTrace += $" | preview updated={updated}";
#endif
        if (updated) PlaceInlineText(); // point text is as wide as what it now says, so the box follows the words
    }

    /// <summary>Records the edit that just happened, joining a run of single-character insertions into one
    /// step the way NSTextView groups typing; a paste, a delete or an insert somewhere else opens the next
    /// one. WinUI gives the box no selection event to watch, so the join is judged by where each character
    /// arrived (one to the right of the last) rather than by the caret itself.</summary>
    private void PushInlineTextStep(string after)
    {
        if (inlineEditor is not { } editor) return;
        var caretAfter = (editor.SelectionStart, editor.SelectionLength);
        var edit = LayerTextStyle.MinimalEdit(inlineTextShown, after);
        bool typing = edit.Length == 0 && edit.WithLength == 1;
        if (typing && inlineTextTyping && edit.Location == inlineTextRunEnd + 1 && inlineTextUndo.TryPeek(out var top))
        {
            var merged = LayerTextStyle.MinimalEdit(top.OldText, after);
            inlineTextUndo.Pop();
            inlineTextUndo.Push(new InlineTextStep(top.OldText, after, merged.Location, merged.Length,
                merged.WithLength, top.CaretBefore, caretAfter));
            inlineTextRunEnd = merged.Location + merged.WithLength - 1;
        }
        else
        {
            inlineTextUndo.Push(new InlineTextStep(inlineTextShown, after, edit.Location, edit.Length,
                edit.WithLength, inlineTextCaret, caretAfter));
            inlineTextRunEnd = edit.Location + edit.WithLength - 1;
        }
        inlineTextTyping = typing;
        inlineTextRedo.Clear();
        inlineTextShown = after;
        inlineTextCaret = caretAfter;
    }

    /// <summary>Takes back one step inside the box. True whenever an edit is open, because the Mac's text
    /// view claims Ctrl+Z whether or not its own stack has anything left (InlineTextEditor.swift:22-25 does
    /// nothing when it cannot undo), so the document's history is never what the keystroke reaches.</summary>
    internal bool UndoInlineText()
    {
        if (inlineEditor is null || document?.ActiveTextDraft is null) return false;
        if (inlineTextUndo.TryPop(out var step))
        {
            inlineTextRedo.Push(step);
            ApplyInlineTextStep(step, forward: false);
        }
        return true;
    }

    internal bool RedoInlineText()
    {
        if (inlineEditor is null || document?.ActiveTextDraft is null) return false;
        if (inlineTextRedo.TryPop(out var step))
        {
            inlineTextUndo.Push(step);
            ApplyInlineTextStep(step, forward: true);
        }
        return true;
    }

    private void ApplyInlineTextStep(InlineTextStep step, bool forward)
    {
        if (document is not { } owner || owner.ActiveTextDraft is not { } draft || inlineEditor is not { } editor)
            return;
        var text = forward ? step.NewText : step.OldText;
        var caret = forward ? step.CaretAfter : step.CaretBefore;
        caret = (Math.Min(caret.Location, text.Length), caret.Length);
        // The run splice runs against the style as it stands now, not against the one recorded with the step:
        // that is what leaves a face applied after the typing in place when the typing is taken back.
        var style = ManifestJson.Clone(draft.Style);
        style.ReplaceCharacters(step.Location, forward ? step.Length : step.WithLength,
            forward ? step.WithLength : step.Length);
        style.Content = text; // ReplaceCharacters moves the run books only; the words are this step's other half
        inlineTextRestoring = true;
        try
        {
            editor.Text = text;
            editor.Select(caret.Location, caret.Length);
            owner.UpdateTextDraft(style, selection: caret);
            inlineTextShown = text;
            inlineTextCaret = caret;
            inlineTextTyping = false;
            inlineTextRunEnd = -1;
        }
        finally
        {
            inlineTextRestoring = false;
        }
        PlaceInlineText();
    }

    /// <summary>Option with the arrows sets spacing, as Photoshop does and the original does
    /// (InlineTextEditor.swift:44-58): left and right take the tracking, up and down take the leading counted
    /// from whatever Auto works out to, and Shift makes each step ten. Nothing being typed means the keys change
    /// what the next click types with, which is the same fallback the original's <c>changeTextStyle</c> has
    /// (TypeTool.swift:389-392). Leading is stored as an absolute line height, so tightening from Auto starts
    /// from the 120 % the rasterizer would have used.</summary>
    internal bool NudgeTextSpacing(bool leading, bool increase, double step)
    {
        var typing = document?.ActiveTextDraft;
        var target = ManifestJson.Clone(typing?.Style ?? textDefaults);
        if (leading) target.Leading = Math.Clamp((increase ? 1 : -1) * step + target.LineHeight, 1, 5000);
        else target.Tracking = Math.Clamp(target.Tracking + (increase ? step : -step), -100, 1000);
        if (!target.IsValid) return false;
        if (typing is null) { textDefaults = target; return true; }
        if (document is not { } owner || !owner.UpdateTextDraft(target)) return false;
        PlaceInlineText();
        return true;
    }

    private static bool InlineKeyHeld(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
            .HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down);

    /// <summary>What an Option+arrow press means, taken out of the key reader so the table can be tested:
    /// left and right take the tracking, up and down take the leading (InlineTextEditor.swift:44-58), Shift makes
    /// the step ten, and without Option the arrows belong to the caret. The chord goes through the user's own
    /// bindings first, exactly as the original's <c>textEvent</c> does (KeyboardShortcuts.swift:185-202), so a
    /// rebind moves the command with it and the chord it left stops meaning spacing. Reading the physical
    /// modifier keys is the one part a probe here cannot reach.</summary>
    internal bool TrySpacingChord(VirtualKey key, bool option, bool shift,
        out bool leading, out bool increase, out double step)
    {
        (leading, increase, step) = (false, false, 1.0);
        var pressed = new ShortcutChord(key, (option ? VirtualKeyModifiers.Menu : (VirtualKeyModifiers)0)
            | (shift ? VirtualKeyModifiers.Shift : (VirtualKeyModifiers)0));
        var (chord, swallowed) = CanvasTranslate(pressed);
        if (swallowed) return false;
        var row = CanvasSpacingKeys.FirstOrDefault(candidate =>
            new ShortcutChord(candidate.Key, candidate.Default) == chord);
        if (row.Id is null) return false;
        leading = row.Id.Contains("Leading", StringComparison.Ordinal);
        increase = row.Id.Contains("Increase", StringComparison.Ordinal);
        step = row.Pixels;
        return true;
    }

    private void OnInlineKeyDown(object sender, KeyRoutedEventArgs args)
    {
        var control = InlineKeyHeld(VirtualKey.Control);
        if (args.Key == VirtualKey.Escape) { CloseInlineText(cancelling: true); args.Handled = true; }
        else if (control && args.Key == VirtualKey.Enter)
        {
            CommitInlineText(); // the original's Cmd+Return: plain Return is a newline
            args.Handled = true;
        }
        else if (control && args.Key == VirtualKey.Z)
        {
            // Ctrl+Z is answered here as well as in the menu's own accelerator, because which of the two routes
            // WinUI takes for a key a TextBox is holding is not something the probe can press and check; both
            // run the same step, and marking it handled keeps the control's private stack out of it.
            if (InlineKeyHeld(VirtualKey.Shift)) RedoInlineText(); else UndoInlineText();
            args.Handled = true;
        }
        else if (control && args.Key == VirtualKey.Y)
        {
            RedoInlineText();
            args.Handled = true;
        }
        else if (TrySpacingChord(args.Key, InlineKeyHeld(VirtualKey.Menu), InlineKeyHeld(VirtualKey.Shift),
                     out bool leading, out bool increase, out double step))
        {
            // Consumed whether or not the style moved: the original swallows the chord even at a clamp, and an
            // unhandled Alt+arrow here would let the caret jump instead.
            NudgeTextSpacing(leading, increase, step);
            args.Handled = true;
        }
    }

    /// <summary>Settles the draft into the document as the one history step it is worth. Switching tools or
    /// layers, opening another document and starting another text gesture all go through here, as they do in
    /// the original; it is also safe to call when nothing is being edited.</summary>
    private void CommitInlineText([CallerMemberName] string site = "")
    {
        if (document is not { } owner || owner.ActiveTextDraft is null) return;
#if DEBUG
        inlineDraftTrace = $"{site}: draft committed";
#endif
        var settled = ManifestJson.Clone(owner.ActiveTextDraft.Style);
        if (!owner.FinishTextDraft()) return;
        // What was just typed becomes what the next click types with, runs and words aside
        // (TypeTool.swift:312-314).
        settled.ColorRuns = null;
        settled.FontRuns = null;
        textDefaults = settled;
        DetachInlineEditor();
    }

    private void CloseInlineText(bool cancelling = false, [CallerMemberName] string site = "")
    {
        if (document is { } owner && owner.ActiveTextDraft is not null)
        {
#if DEBUG
            inlineDraftTrace = $"{site}: draft {(cancelling ? "cancelled" : "committed")} on close";
#endif
            if (cancelling) owner.CancelTextDraft();
            else owner.FinishTextDraft();
        }
        DetachInlineEditor();
    }

    /// <summary>Tears the control down without touching the draft. Opening an editor has to clear the previous
    /// host's controls, and settling the draft it just began would leave nothing to edit.</summary>
    private void DetachInlineEditor()
    {
        if (inlineEditor is { } editor)
        {
            if (inlineTextToken is { } token) editor.UnregisterPropertyChangedCallback(TextBox.TextProperty, token);
            editor.KeyDown -= OnInlineKeyDown;
        }
        inlineTextToken = null;
        inlineTextUndo.Clear();
        inlineTextRedo.Clear();
        inlineTextShown = "";
        inlineTextTyping = false;
        InlineTextLayer.Children.Clear();
        inlineEditor = null;
        inlineFrame = null;
        inlineBox = null;
        inlineOverflow = default;
        Array.Clear(inlineHandles);
        inlineResize = null;
        InlineTextLayer.Visibility = Visibility.Collapsed;
    }
}
