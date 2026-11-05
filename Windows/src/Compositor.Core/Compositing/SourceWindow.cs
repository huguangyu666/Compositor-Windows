namespace Compositor.Core;

/// <summary>A memory window of a larger source grid. Sampling and edge clamping use the
/// full grid; only the byte address changes, preserving the full render's filter weights.</summary>
internal readonly record struct SourceWindow(Surface Image, int Width, int Height, int X = 0, int Y = 0)
{
    internal SourceWindow(Surface image) : this(image, image.Width, image.Height) { }
    internal byte[] Pixels => Image.Pixels;
    internal int Stride => Image.Stride;
    internal int Offset => -Y * Stride - X * 4;
    internal bool IsWindow => Width != Image.Width || Height != Image.Height;
}
