using System.Buffers.Binary;

namespace Compositor.Core.Psd;

/// <summary>Big-endian bounded PSD input and PackBits channel decompression.</summary>
public sealed class PsdBinaryReader
{
    private readonly PsdDataView data;
    private readonly long start, end;
    private long offset;
    internal CancellationToken Cancellation { get; }

    public PsdBinaryReader(byte[] data) : this(new PsdDataView(new PsdByteInput(data)), default) { }
    internal PsdBinaryReader(PsdDataView data, CancellationToken cancellation)
        : this(data, 0, data.Length, cancellation) { }
    private PsdBinaryReader(PsdDataView data, long start, long end, CancellationToken cancellation)
    { this.data = data; this.start = start; this.end = end; offset = start; Cancellation = cancellation; }

    public long Offset => offset;
    public long Remaining => end - offset;
    public void Seek(long position)
    {
        Cancellation.ThrowIfCancellationRequested();
        if (position < start || position > end) throw new EndOfStreamException("PSD data truncated.");
        offset = position;
    }
    private void Require(long count)
    {
        Cancellation.ThrowIfCancellationRequested();
        if (count < 0 || count > Remaining) throw new EndOfStreamException("PSD data truncated.");
    }
    internal PsdBinaryReader ReadSection(long length)
    {
        Require(length);
        var section = new PsdBinaryReader(data, offset, offset + length, Cancellation);
        offset += length; return section;
    }
    internal long ReadLength(bool largeDocument = false)
    {
        uint high = largeDocument ? ReadU32() : 0, low = ReadU32();
        if (high > int.MaxValue) throw new EndOfStreamException("PSD data truncated.");
        return ((long)high << 32) | low;
    }
    public ushort PeekU16() => Remaining >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(data.GetSpan(offset, 2)) : (ushort)0;
    public byte ReadU8() { Require(1); return data.GetSpan(offset++, 1)[0]; }
    public ushort ReadU16() { Require(2); var value = BinaryPrimitives.ReadUInt16BigEndian(data.GetSpan(offset, 2)); offset += 2; return value; }
    public short ReadI16() => unchecked((short)ReadU16());
    public uint ReadU32() { Require(4); var value = BinaryPrimitives.ReadUInt32BigEndian(data.GetSpan(offset, 4)); offset += 4; return value; }
    public int ReadI32() => unchecked((int)ReadU32());
    public string ReadString4()
    { Require(4); var text = System.Text.Encoding.ASCII.GetString(data.GetSpan(offset, 4)); offset += 4; return text; }
    public byte[] ReadBytes(int count)
    {
        Require(count); var bytes = new byte[count];
        for (int position = 0; position < count;)
        {
            Cancellation.ThrowIfCancellationRequested();
            int take = Math.Min(1024 * 1024, count - position);
            data.GetSpan(offset + position, take).CopyTo(bytes.AsSpan(position, take)); position += take;
        }
        offset += count; return bytes;
    }
    public void Skip(long count) { Require(count); offset += count; }
    internal PsdDataView ReadView(long count) { Require(count); var view = data.Slice(offset, count); offset += count; return view; }

    /// <summary>PackBits: literals, repeated runs, and −128 no-ops; large planes decode independent rows in parallel.</summary>
    public static byte[] UnpackRle(byte[] data, int offset, int width, int height, bool largeDocument)
    {
        if ((uint)offset > (uint)data.Length) throw new EndOfStreamException("PSD data truncated.");
        return UnpackRle(new PsdDataView(new PsdByteInput(data)).Slice(offset, data.Length - offset), width, height, largeDocument, default);
    }
    internal static byte[] UnpackRle(PsdDataView data, int width, int height, bool largeDocument, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (width < 0 || height < 0 || (long)width * height > int.MaxValue
            || (long)height * (largeDocument ? 4 : 2) > data.Length) throw new EndOfStreamException("PSD data truncated.");
        int countSize = largeDocument ? 4 : 2;
        var counts = new long[height];
        for (int row = 0; row < height; row++)
        {
            cancellation.ThrowIfCancellationRequested();
            var entry = data.GetSpan((long)row * countSize, countSize);
            counts[row] = largeDocument ? BinaryPrimitives.ReadUInt32BigEndian(entry) : BinaryPrimitives.ReadUInt16BigEndian(entry);
        }
        return UnpackRleRows(data, (long)height * countSize, width, height, counts, cancellation);
    }
    public byte[] ReadRlePlane(int[] counts, int width, int height) => ReadRlePlane(Array.ConvertAll(counts, value => (long)value), width, height);
    internal byte[] ReadRlePlane(long[] counts, int width, int height)
    {
        Require(0);
        if (width < 0 || height < 0 || counts.Length != height || (long)width * height > int.MaxValue)
            throw new EndOfStreamException("PSD data truncated.");
        long length = 0;
        foreach (long count in counts)
        {
            if (count < 0 || count > Remaining - length) throw new EndOfStreamException("PSD data truncated.");
            length += count;
        }
        var plane = UnpackRleRows(data.Slice(offset, length), 0, width, height, counts, Cancellation);
        Skip(length); return plane;
    }
    private static byte[] UnpackRleRows(PsdDataView data, long cursor, int width, int height, long[] counts, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        long encoded = 0;
        foreach (long count in counts)
        {
            if (count < 0 || width > 0 && (count == 0 || count < ((long)width + 127) / 128)
                || count > data.Length - cursor - encoded) throw new EndOfStreamException("PSD data truncated.");
            encoded += count;
        }
        var starts = new long[height]; long rowStart = cursor;
        for (int row = 0; row < height; row++) { starts[row] = rowStart; rowStart += counts[row]; }
        cancellation.ThrowIfCancellationRequested();
        var plane = new byte[checked(width * height)];
        void DecodeRow(int row)
        {
            cancellation.ThrowIfCancellationRequested();
            long cursor = starts[row], end = cursor + counts[row]; int written = 0, tokens = 0;
            while (written < width)
            {
                if ((tokens++ & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                if (cursor >= end) throw new EndOfStreamException("PSD data truncated.");
                int n = unchecked((sbyte)data.GetSpan(cursor++, 1)[0]);
                if (n >= 0)
                {
                    int count = n + 1;
                    if (count > width - written || count > end - cursor) throw new EndOfStreamException("PSD data truncated.");
                    data.GetSpan(cursor, count).CopyTo(plane.AsSpan(row * width + written, count));
                    cursor += count; written += count;
                }
                else if (n != -128)
                {
                    int count = 1 - n;
                    if (count > width - written || cursor >= end) throw new EndOfStreamException("PSD data truncated.");
                    Array.Fill(plane, data.GetSpan(cursor++, 1)[0], row * width + written, count); written += count;
                }
            }
        }
        if ((long)width * height >= 4_000_000 && height >= 4)
        {
            try { Parallel.For(0, height, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancellation }, DecodeRow); }
            catch (AggregateException ex)
            {
                cancellation.ThrowIfCancellationRequested();
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
            }
        }
        else for (int row = 0; row < height; row++) DecodeRow(row);
        cancellation.ThrowIfCancellationRequested(); return plane;
    }
}
