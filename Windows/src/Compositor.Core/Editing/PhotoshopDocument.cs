using Compositor.Core.Psd;

namespace Compositor.Core;

public sealed partial class EditorDocument
{
    /// <summary>Opens a Photoshop source at its own canvas size, without a synthetic background or import undo step.</summary>
    public static EditorDocument FromPhotoshop(PsdDocument photoshop)
    {
        ArgumentNullException.ThrowIfNull(photoshop);
        var snapshot = new ProjectSnapshot
        {
            Manifest = new() { DocumentId = Guid.NewGuid(), Width = photoshop.Width, Height = photoshop.Height, Resolution = 72 },
            Images = new Dictionary<Guid, Surface>(), Masks = new Dictionary<Guid, MaskSurface>(),
        };
        ProjectValidator.Validate(snapshot.Manifest);
        var builder = new EditorDocument(snapshot);
        builder.ImportPsd(photoshop);
        ProjectValidator.Validate(builder.Snapshot.Manifest);
        return new(builder.Snapshot) { IsDirty = true };
    }
}
