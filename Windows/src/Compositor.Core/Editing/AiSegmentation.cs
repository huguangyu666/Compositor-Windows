namespace Compositor.Core;

/// <summary>
/// AI segmentation seam. The macOS app uses Apple Vision for subject masks, object
/// instance masks and guided-matte refinement; the Windows port reserves this seam and
/// ships no model. An implementer supplies masks over the whole image — white over the
/// subject, the same contract Vision's output has after GuidedMatte refinement.
/// </summary>
public interface IAiSegmenter
{
    /// <summary>White-over-subject mask for the composite, or null when no subject is found.</summary>
    MaskSurface? SegmentSubject(Surface composite);

    /// <summary>Instance masks for the objects under a clicked point, most confident first.
    /// Empty when nothing is found.</summary>
    IReadOnlyList<MaskSurface> SegmentObjects(Surface composite, PointD point);
}

/// <summary>The registered AI segmenter for the session, or null — commands that need
/// segmentation check this and report it as not wired rather than failing.</summary>
public static class AiSegmentation
{
    public static IAiSegmenter? Registered { get; set; }
}
