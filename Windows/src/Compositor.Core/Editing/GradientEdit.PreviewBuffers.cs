namespace Compositor.Core;

public sealed partial class GradientEdit
{
    internal sealed class PreviewBuffers(GradientEdit edit)
    {
        // The serial renderer protects the UI-published frame, which can be older than
        // the last calculated frame when pointer input supersedes a request in flight.
        // Public Preview still returns independently owned pixels.
        private const int maximumPlaneBytes = 64 * 1024 * 1024;
        private readonly Surface?[] images = new Surface?[2];
        private readonly MaskSurface?[] masks = new MaskSurface?[2];

        internal GradientPreview? Preview(PointD start, PointD end, GradientSettings settings, GradientPreview? retained)
            => edit.RenderPreview(start, end, settings, this, retained);

        internal Surface Copy(Surface source, Surface? retained)
        {
            if (source.Pixels.Length > maximumPlaneBytes) return source.Clone();
            int slot = retained is not null && ReferenceEquals(images[0], retained) ? 1 : 0;
            if (images[slot] is not { } output) return images[slot] = source.Clone();
            source.Pixels.AsSpan().CopyTo(output.Pixels);
            return output;
        }

        internal MaskSurface Copy(MaskSurface source, MaskSurface? retained)
        {
            if (source.Pixels.Length > maximumPlaneBytes) return source.Clone();
            int slot = retained is not null && ReferenceEquals(masks[0], retained) ? 1 : 0;
            if (masks[slot] is not { } output) return masks[slot] = source.Clone();
            source.Pixels.AsSpan().CopyTo(output.Pixels);
            return output;
        }
    }
}
