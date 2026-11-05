namespace Compositor.Renderer;

public readonly record struct CompositeColor(byte Red, byte Green, byte Blue)
{
    /// <summary>ColorPalette.swift samples premultiplied sRGB, ignores transparent pixels,
    /// and rounds unpremultiplied channels to an eight-bit palette color.</summary>
    public static CompositeColor? FromBgra(ReadOnlySpan<byte> bytes)
    {
        int alpha = bytes[3];
        if (alpha == 0) return null;
        byte Channel(byte value) => (byte)Math.Round(Math.Min(alpha, (int)value) * 255.0 / alpha, MidpointRounding.AwayFromZero);
        return new(Channel(bytes[2]), Channel(bytes[1]), Channel(bytes[0]));
    }
}
