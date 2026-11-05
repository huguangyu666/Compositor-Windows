namespace Compositor.Core;

/// <summary>
/// One in-progress text edit, as the macOS `TextDraft` (Document/TypeTool.swift:223-232) holds it: the layer
/// being edited, or null for text that has not become a layer yet, plus what the editor is currently showing.
/// Nothing here touches the document — the draft is what the on-canvas editor mutates on every keystroke, and
/// <see cref="EditorDocument.ApplyTextDraft"/> is the single moment it becomes pixels and one history step.
/// </summary>
public sealed class TextDraft
{
    /// <summary>Regenerated for every draft; a commit that fails puts the same draft back.</summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>The layer being re-edited, or null while this is text that has not been placed yet.</summary>
    public Guid? LayerId { get; init; }

    public PointD Origin { get; set; }

    /// <summary>
    /// The placement the editor is showing. For a layer being re-edited this starts as the layer's own transform
    /// and the macOS `applyText` only re-grows it when the text is point text (TypeTool.swift:288-297), so a
    /// fixed box the user dragged keeps its size while the lines re-layout inside it.
    /// </summary>
    public LayerTransform? Transform { get; set; }

    public required LayerTextStyle Style { get; set; }

    /// <summary>Caret or selection, in characters: (start, length).</summary>
    public (int Location, int Length) Selection { get; set; }

    /// <summary>The macOS `finishText` rule: new text that is only whitespace commits nothing at all.</summary>
    public bool IsBlankNewText => LayerId is null
        && string.IsNullOrWhiteSpace(Style.Content);

    public bool SameContentAs(LayerTextStyle other) => Style.Equals(other);
}
