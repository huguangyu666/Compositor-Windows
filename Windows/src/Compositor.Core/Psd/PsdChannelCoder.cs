using System.Buffers.Binary;

namespace Compositor.Core.Psd;

internal readonly record struct PsdCrop(long X, long Y, int Width, int Height);

/// <summary>PSDChannelCoder's raw/PackBits windows, without a full source plane or row buffer.</summary>
internal static class PsdChannelCoder
{
    internal static byte[] Decode(PsdDataView data, int compression, long width, long height,
        bool largeDocument, PsdCrop? crop, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var window = crop ?? new PsdCrop(0, 0, checked((int)width), checked((int)height));
        if (width < 0 || height < 0 || window.X < 0 || window.Y < 0 || window.Width < 0 || window.Height < 0
            || window.X > width - window.Width || window.Y > height - window.Height)
            throw new EndOfStreamException("PSD data truncated.");
        if (compression == 0)
        {
            // Bound the source extent without multiplying untrusted source sides.
            if (height == 0 || width > data.Length / height) throw new EndOfStreamException("PSD data truncated.");
            var plane = new byte[checked(window.Width * window.Height)];
            for (int row = 0; row < window.Height; row++)
            {
                cancellation.ThrowIfCancellationRequested();
                long source = (window.Y + row) * width + window.X;
                data.GetSpan(source, window.Width).CopyTo(plane.AsSpan(row * window.Width, window.Width));
            }
            cancellation.ThrowIfCancellationRequested(); return plane;
        }
        if (compression != 1) throw new PsdNotSupportedException("Unsupported channel compression.");
        if (crop is null) return PsdBinaryReader.UnpackRle(data, (int)width, (int)height, largeDocument, cancellation);
        return CropRle(data, width, height, largeDocument, window, cancellation);
    }

    private static byte[] CropRle(PsdDataView data, long width, long height, bool largeDocument, PsdCrop crop, CancellationToken cancellation)
    {
        int countSize = largeDocument ? 4 : 2;
        if (height > data.Length / countSize) throw new EndOfStreamException("PSD data truncated.");
        long tableSize = height * countSize;
        long Count(long row)
        {
            var entry = data.GetSpan(row * countSize, countSize);
            return largeDocument ? BinaryPrimitives.ReadUInt32BigEndian(entry) : BinaryPrimitives.ReadUInt16BigEndian(entry);
        }
        long extent = tableSize;
        for (long row = 0; row < height; row++)
        {
            if ((row & 1023) == 0) cancellation.ThrowIfCancellationRequested();
            long count = Count(row);
            if (count > data.Length - extent) throw new EndOfStreamException("PSD data truncated.");
            extent += count;
        }
        cancellation.ThrowIfCancellationRequested();
        var plane = new byte[checked(crop.Width * crop.Height)];
        long cursor = tableSize;
        for (long row = 0; row < height; row++)
        {
            if ((row & 1023) == 0) cancellation.ThrowIfCancellationRequested();
            long end = cursor + Count(row);
            if (row < crop.Y || row >= crop.Y + crop.Height) { cursor = end; continue; }
            cancellation.ThrowIfCancellationRequested();
            long written = 0; int tokens = 0;
            while (written < width)
            {
                if ((tokens++ & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                if (cursor >= end) throw new EndOfStreamException("PSD data truncated.");
                int n = unchecked((sbyte)data.GetSpan(cursor++, 1)[0]);
                if (n == -128) continue;
                int count = n >= 0 ? n + 1 : 1 - n;
                if (count > width - written || (n >= 0 ? count > end - cursor : cursor >= end))
                    throw new EndOfStreamException("PSD data truncated.");
                long left = Math.Max(written, crop.X), right = Math.Min(written + count, crop.X + crop.Width);
                if (right > left)
                {
                    int target = checked((int)((row - crop.Y) * crop.Width + left - crop.X)); int take = (int)(right - left);
                    if (n >= 0) data.GetSpan(cursor + left - written, take).CopyTo(plane.AsSpan(target, take));
                    else Array.Fill(plane, data.GetSpan(cursor, 1)[0], target, take);
                }
                cursor += n >= 0 ? count : 1; written += count;
            }
            cursor = end;
        }
        cancellation.ThrowIfCancellationRequested(); return plane;
    }
}
