using System.IO.MemoryMappedFiles;

namespace Compositor.Core.Psd;

// Views share input storage; only decoded planes escape the lifetime of ReadFile.
internal abstract class PsdInput : IDisposable
{
    internal abstract long Length { get; }
    internal abstract ReadOnlySpan<byte> GetSpan(long offset, int count);
    public abstract void Dispose();
}

internal sealed class PsdByteInput(byte[] bytes) : PsdInput
{
    internal override long Length => bytes.LongLength;
    internal override ReadOnlySpan<byte> GetSpan(long offset, int count) => bytes.AsSpan(checked((int)offset), count);
    public override void Dispose() { }
}

internal sealed unsafe class PsdMappedInput : PsdInput
{
    private readonly FileStream file;
    private MemoryMappedFile? mapping;
    private MemoryMappedViewAccessor? view;
    private byte* pointer;
    private bool acquired, disposed;
    internal override long Length { get; }

    internal PsdMappedInput(string path)
    {
        // Deny writes/truncation while worker threads hold pointers into the mapping.
        file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess);
        Length = file.Length;
        try
        {
            if (Length == 0) return;
            mapping = MemoryMappedFile.CreateFromFile(file, null, 0, MemoryMappedFileAccess.Read,
                HandleInheritability.None, leaveOpen: true);
            view = mapping.CreateViewAccessor(0, Length, MemoryMappedFileAccess.Read);
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            acquired = true;
            pointer += view.PointerOffset;
        }
        catch { Dispose(); throw; }
    }

    internal override ReadOnlySpan<byte> GetSpan(long offset, int count)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (offset < 0 || count < 0 || offset > Length || count > Length - offset)
            throw new EndOfStreamException("PSD data truncated.");
        return new ReadOnlySpan<byte>(pointer + offset, count);
    }

    public override void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (acquired) { view!.SafeMemoryMappedViewHandle.ReleasePointer(); acquired = false; }
        pointer = null;
        view?.Dispose(); mapping?.Dispose(); file.Dispose();
    }
}

internal readonly struct PsdDataView
{
    private readonly PsdInput input;
    private readonly long offset;
    internal long Length { get; }
    internal PsdDataView(PsdInput input) : this(input, 0, input.Length) { }
    private PsdDataView(PsdInput input, long offset, long length)
    { this.input = input; this.offset = offset; Length = length; }

    private void Require(long position, long count)
    {
        if (position < 0 || count < 0 || position > Length || count > Length - position)
            throw new EndOfStreamException("PSD data truncated.");
    }
    internal PsdDataView Slice(long position, long count)
    { Require(position, count); return new(input, offset + position, count); }
    internal ReadOnlySpan<byte> GetSpan(long position, int count)
    { Require(position, count); return input.GetSpan(offset + position, count); }
}
