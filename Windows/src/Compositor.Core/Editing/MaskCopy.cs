namespace Compositor.Core;

public sealed partial class EditorDocument
{
    public bool CopyMask(Guid source, Guid target)
    {
        var from = Find(source); var to = Find(target);
        if (source == target || from?.MaskFile is null || to is null || to.IsGroupOrFalse || !Snapshot.Masks.TryGetValue(source, out var mask)) return false;
        var placed = from.MaskPlacement ?? from.Transform;
        BeginChange();
        ((Dictionary<Guid, MaskSurface>)Snapshot.Masks)[target] = mask;
        to.MaskFile = ProjectValidator.Uuid(target) + ".mask.png";
        to.MaskEnabled = from.MaskEnabled; to.MaskLinked = from.MaskLinked;
        to.MaskPlacement = new() { Origin = placed.Origin, Size = placed.Size, Rotation = placed.Rotation, FlipX = placed.FlipX, FlipY = placed.FlipY, Sampling = placed.Sampling };
        Snapshot.Manifest.ActiveLayerId = target;
        EndChange(); return true;
    }
}
