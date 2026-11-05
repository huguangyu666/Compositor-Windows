namespace Compositor.Core;

/// <summary>One serial effect update's reusable window. Retain at most 2MiB and include
/// it in the compositor's existing 64MiB effect-entry budget; full renders stay temporary.</summary>
internal sealed class EffectScratch
{
    private int width, height;
    private Surface? result;
    private byte[]? shape, coverage, mask, horizontal;
    private float[]? currentFloat, horizontalFloat;
    private double[]? columns;
    private int[]? queue;

    internal static long MaximumBytes(int width, int height) => (long)width * height * 16
        + (long)Math.Max(width, height) * 4 + (long)width * 8 + 512;

    internal void Resize(int newWidth, int newHeight)
    {
        if (width == newWidth && height == newHeight) return;
        width = newWidth; height = newHeight;
        result = null; shape = coverage = mask = horizontal = null;
        currentFloat = horizontalFloat = null; columns = null; queue = null;
    }

    internal Surface Result => result ??= new(width, height);
    internal byte[] Shape => shape ??= new byte[width * height];
    internal byte[] Coverage => coverage ??= new byte[width * height];
    internal byte[] Mask => mask ??= new byte[width * height];
    internal byte[] Horizontal => horizontal ??= new byte[width * height];
    internal float[] CurrentFloat => currentFloat ??= new float[width * height];
    internal float[] HorizontalFloatRows(int rows)
    {
        int length = width * rows;
        if (horizontalFloat is null || horizontalFloat.Length < length) horizontalFloat = new float[length];
        return horizontalFloat;
    }
    internal double[] Columns => columns ??= new double[width];
    internal int[] Queue => queue ??= new int[Math.Max(width, height)];
}
