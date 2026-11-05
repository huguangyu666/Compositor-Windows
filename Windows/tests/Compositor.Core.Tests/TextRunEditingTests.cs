using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// Letters carrying their own face and colour inside one text layer — the per-run model the macOS original
/// keeps as arrays indexed by UTF-16 unit and collapses back into runs when it writes them
/// (TypeTool.swift:71-139). Expected values are worked out from what the letters should be, not from the
/// collapse code.
/// </summary>
public class TextRunEditingTests
{
    private static LayerTextStyle Text(string content, string face = "BaseFace") => new()
    {
        Content = content,
        FontName = face,
        FontSize = 20,
        Red = 1,
    };

    [Fact]
    public void AFaceSetOverARangeBecomesOneRunAndTheOtherLettersKeepTheirOwn()
    {
        var style = Text("abcd");
        style.SetFont("Other", 1, 2);

        Assert.Equal("BaseFace", style.FontName);
        var run = Assert.Single(style.FontRuns!);
        Assert.Equal(1, run.Location);
        Assert.Equal(2, run.Length);
        Assert.Equal("Other", run.FontName);
        Assert.Equal("BaseFace", style.FontNameAt(0));
        Assert.Equal("Other", style.FontNameAt(1));
        Assert.Equal("Other", style.FontNameAt(2));
        Assert.Equal("BaseFace", style.FontNameAt(3));
        Assert.True(style.IsValid);
        Assert.Null(style.UniformFontName(0, 4));
        Assert.Equal("Other", style.UniformFontName(1, 2));
    }

    [Fact]
    public void PaintingTheWholeContentSetsTheTextsOwnFaceAndLeavesNoRuns()
    {
        var style = Text("abcd");
        style.SetFont("Other", 1, 2);
        style.SetFont("Everything", 0, 4);

        Assert.Equal("Everything", style.FontName);
        Assert.Null(style.FontRuns);
    }

    [Fact]
    public void OneFaceReachingEveryLetterCollapsesIntoTheTextsOwnFace()
    {
        var style = Text("abcd");
        style.SetFont("Other", 0, 2);
        Assert.NotNull(style.FontRuns);
        style.SetFont("Other", 2, 2);

        Assert.Equal("Other", style.FontName);
        Assert.Null(style.FontRuns);
    }

    [Fact]
    public void ARangePaintedBackToTheTextsOwnFaceLeavesNoRunBehind()
    {
        var style = Text("abcd");
        style.SetFont("Other", 1, 2);
        style.SetFont("BaseFace", 1, 2);

        Assert.Null(style.FontRuns);
        Assert.Equal("BaseFace", style.FontName);
    }

    [Fact]
    public void AFaceTheDocumentWouldRefuseChangesNothing()
    {
        var style = Text("abcd");
        style.SetFont("", 1, 2);
        style.SetFont("Two\nLines", 1, 2);
        style.SetFont(new string('x', 201), 1, 2);

        Assert.Null(style.FontRuns);
        Assert.Equal("BaseFace", style.FontName);
    }

    [Fact]
    public void AnEmptyRangeIsTheCaretAndSetsTheTextsOwnFaceLikeTheWholeContent()
    {
        var style = Text("abcd");
        style.SetFont("Other", 1, 2);
        Assert.NotNull(style.FontRuns);

        // Nothing selected means the caret, and the caret's face is the text's own: the base changes and no
        // letter needs an override (TypeTool.swift:111-115).
        style.SetFont("Third", 2, 0);
        Assert.Equal("Third", style.FontName);
        Assert.Null(style.FontRuns);
    }

    [Fact]
    public void AColorSetOverARangeBecomesOneRunAndTheWholeContentShortcutClearsThem()
    {
        var style = Text("abcd");
        style.SetColor(0, 1, 0, 1, 2);

        var run = Assert.Single(style.ColorRuns!);
        Assert.Equal(1, run.Location);
        Assert.Equal(2, run.Length);
        Assert.Equal((1, 0, 0), style.ColorAt(0));
        Assert.Equal((0, 1, 0), style.ColorAt(2));
        Assert.True(style.IsValid);

        style.SetColor(0, 0, 1, 0, 4);
        Assert.Equal((0, 0, 1), (style.Red, style.Green, style.Blue));
        Assert.Null(style.ColorRuns);
    }

    [Fact]
    public void InsertingALetterTakesTheFaceOfTheOneBeforeItAndKeepsTheRestWithTheirLetters()
    {
        var style = Text("abcd");
        style.SetFont("Other", 1, 2);

        // "abcd" to "aXbcd" differs by one unit at index 1, so the new letter inherits the face of index 0 —
        // the letter before the caret, as Photoshop does — and the two that follow keep theirs, one later.
        var edit = LayerTextStyle.MinimalEdit("abcd", "aXbcd");
        Assert.Equal((1, 0, 1), edit);
        style.ReplaceCharacters(edit.Location, edit.Length, edit.WithLength);
        style.Content = "aXbcd";

        Assert.Equal("BaseFace", style.FontNameAt(0));
        Assert.Equal("BaseFace", style.FontNameAt(1));
        Assert.Equal("Other", style.FontNameAt(2));
        Assert.Equal("Other", style.FontNameAt(3));
        var run = Assert.Single(style.FontRuns!);
        Assert.Equal(2, run.Location);
        Assert.Equal(2, run.Length);
    }

    [Fact]
    public void TypingOverARangeTakesThoseLettersColorsWithIt()
    {
        var style = Text("abcd");
        style.SetFont("Other", 1, 2);
        style.SetColor(0, 1, 0, 1, 2);

        var edit = LayerTextStyle.MinimalEdit("abcd", "ad");
        Assert.Equal((1, 2, 0), edit);
        style.ReplaceCharacters(edit.Location, edit.Length, edit.WithLength);
        style.Content = "ad";

        Assert.Null(style.FontRuns);
        Assert.Null(style.ColorRuns);
        Assert.Equal("BaseFace", style.FontNameAt(0));
        Assert.Equal((1, 0, 0), style.ColorAt(1));
    }

    [Fact]
    public void MinimalEditLeavesWhatTheTwoStringsShareAlone()
    {
        Assert.Equal((4, 0, 0), LayerTextStyle.MinimalEdit("abcd", "abcd"));
        Assert.Equal((0, 2, 2), LayerTextStyle.MinimalEdit("ab", "ba"));
        Assert.Equal((0, 4, 3), LayerTextStyle.MinimalEdit("abcd", "xyz"));
        Assert.Equal((1, 0, 1), LayerTextStyle.MinimalEdit("ac", "abc"));
    }

    [Fact]
    public void TypingIntoADraftMovesRunsInsteadOfDroppingThem()
    {
        var document = EditorDocument.Create(120, 90);
        var style = Text("abcd");
        style.SetFont("Other", 1, 2);
        var layer = document.AddTextLayer(style, new PointD(4, 6))!;
        Assert.NotNull(layer.Text!.FontRuns);

        Assert.True(document.BeginTextDraft(layer.Id, layer.Transform.Origin, layer.Transform, layer.Text));
        Assert.True(document.UpdateTextDraftContent("aXbcd"));

        var typed = document.ActiveTextDraft!;
        Assert.Equal("aXbcd", typed.Style.Content);
        var run = Assert.Single(typed.Style.FontRuns!);
        Assert.Equal(2, run.Location);
        Assert.Equal("Other", run.FontName);
        Assert.True(typed.Style.IsValid);
    }

    [Fact]
    public void AStyleWithRunsSurvivesTheManifestRoundTrip()
    {
        var style = Text("abcd");
        style.SetFont("Other", 1, 2);
        style.SetColor(0, 1, 0, 2, 1);

        var restored = ManifestJson.Clone(style);

        Assert.Equal(style.FontRuns, restored.FontRuns);
        Assert.Equal(style.ColorRuns, restored.ColorRuns);
        Assert.True(restored.IsValid);
    }
}
