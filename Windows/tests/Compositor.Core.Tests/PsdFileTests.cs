using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Compositor.Core.Psd;
using Xunit;

namespace Compositor.Core.Tests;

public sealed class PsdFileTests : IDisposable
{
    private readonly string directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "compositor-psd-file-" + Guid.NewGuid().ToString("N")));
    private static readonly byte[] Expected = [11, 55, 99, 255, 22, 66, 111, 255, 33, 77, 122, 255, 44, 88, 133, 255];

    public PsdFileTests() { Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), directory); Directory.CreateDirectory(directory); }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FileAndByteReadersKeepIndependentRawRlePixels(bool psb, bool rle)
    {
        byte[] bytes = Tiny(psb, rle); string path = Path.Combine(directory, "Tiny.psd"); File.WriteAllBytes(path, bytes);
        Assert.Equal(Expected, Assert.Single(ReadFile(path).Layers).Pixels!.Pixels);
        Assert.Equal(Expected, Assert.Single((await ReadFileAsync(path)).Layers).Pixels!.Pixels);
        Assert.Equal(Expected, Assert.Single(PsdReader.Read(bytes).Layers).Pixels!.Pixels);
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void TrailingFileBytesAreNotCopiedIntoManagedMemory()
    {
        string warm = Path.Combine(directory, "Warm.psd"); File.WriteAllBytes(warm, Tiny(false, false)); ReadFile(warm);
        string path = Path.Combine(directory, "Large.psd");
        using (var file = Sparse(path)) { file.Write(Tiny(false, false)); file.SetLength(64 * 1024 * 1024); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        var parsed = ReadFile(path); long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 1024 * 1024, $"Reading a tiny image with a 64MiB tail allocated {allocated} bytes.");
        Assert.Equal(Expected, Assert.Single(parsed.Layers).Pixels!.Pixels);
    }

    [Fact]
    public void ResourceSkipAndMergedPixelsWorkBeyondInt32Offsets()
    {
        string path = Path.Combine(directory, "LongOffset.psd"); uint resources = (uint)int.MaxValue + 8;
        using (var file = Sparse(path))
        {
            Header(file, false); U32(file, resources); file.Seek(resources, SeekOrigin.Current);
            U32(file, 0); U16(file, 0); file.Write([11, 22, 33, 44, 55, 66, 77, 88, 99, 111, 122, 133]);
        }
        var parsed = ReadFile(path);
        Assert.Equal(Expected, Assert.Single(parsed.Layers).Pixels!.Pixels);
    }

    [Fact]
    public void PsbRawChannelLargerThanInt32StillDecodesItsSmallCanvasWindow()
    {
        string path = Path.Combine(directory, "LongChannel.psb"); long sourceWidth = (long)int.MaxValue + 1;
        using (var file = Sparse(path))
        {
            using var record = new MemoryStream();
            U32(record, 0); U32(record, uint.MaxValue); U32(record, 1); U32(record, int.MaxValue); U16(record, 1);
            U16(record, 0); Length(record, sourceWidth + 2, true); record.Write("8BIMnorm"u8); record.Write([255, 0, 0, 0]);
            U32(record, 12); record.Write(new byte[8]); record.Write([1, (byte)'L', 0, 0]);
            file.Write("8BPS"u8); U16(file, 2); file.Write(new byte[6]); U16(file, 3); U32(file, 1); U32(file, 2); U16(file, 8); U16(file, 3); U32(file, 0); U32(file, 0);
            long info = 2 + record.Length + sourceWidth + 2;
            Length(file, info + 8, true); Length(file, info, true); U16(file, 1); file.Write(record.ToArray()); U16(file, 0);
            long payload = file.Position; file.SetLength(payload + sourceWidth); file.Position = payload + 1; file.Write([11, 22]);
        }
        var layer = Assert.Single(ReadFile(path, budget: 2).Layers);
        Assert.Equal(new PointD(0, 0), layer.Offset); Assert.Equal(2, layer.Pixels!.Width);
        Assert.Equal(new byte[] { 11, 0, 0, 255, 22, 0, 0, 255 }, layer.Pixels.Pixels);
    }

    [Fact]
    public void UnknownAdditionalDataIsSkippedWithoutAllocatingItsPayload()
    {
        string path = Path.Combine(directory, "Metadata.psd");
        using (var file = Sparse(path))
        {
            Header(file, false, 1, 1); U32(file, 0);
            int blob = 16 * 1024 * 1024, info = 2 + 18 + 6 + 12 + 4 + 12 + 12 + blob + 3;
            U32(file, (uint)info + 4); U32(file, (uint)info); U16(file, 1);
            file.Write(new byte[8]); U32(file, 1); U32(file, 1); U16(file, 1); U16(file, 0); U32(file, 3);
            file.Write("8BIMnorm"u8); file.Write([255, 0, 0, 0]); U32(file, (uint)(12 + 12 + blob));
            file.Write(new byte[8]); file.Write([1, (byte)'L', 0, 0]); file.Write("8BIMzzzz"u8); U32(file, (uint)blob);
            file.Seek(blob, SeekOrigin.Current); U16(file, 0); file.WriteByte(11);
        }
        long before = GC.GetAllocatedBytesForCurrentThread(); var parsed = ReadFile(path);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 1024 * 1024, $"An ignored 16MiB block allocated {allocated} bytes.");
        Assert.Equal(new byte[] { 11, 0, 0, 255 }, Assert.Single(parsed.Layers).Pixels!.Pixels);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationBeforeByteParseStopsBeforePublication(bool parallel)
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ReadBytes(Tiny(false, true), parallel, cancellation.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBeforeFileOpenStopsBeforeIo(bool async)
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        string missing = Path.Combine(directory, "Missing.psd");
        if (async) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadFileAsync(missing, cancellation.Token));
        else Assert.ThrowsAny<OperationCanceledException>(() => ReadFile(missing, token: cancellation.Token));
    }

    [Fact]
    public void ParseFailureReleasesEveryFileHandle()
    {
        string path = Path.Combine(directory, "Broken.psd"); File.WriteAllBytes(path, Tiny(false, false)[..^1]);
        Assert.ThrowsAny<Exception>(() => ReadFile(path));
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void MappedViewsDenyMutationAndCannotOutliveTheirOwner()
    {
        string path = Path.Combine(directory, "Owned.psd"); File.WriteAllBytes(path, Tiny(false, false));
        var input = new PsdMappedInput(path); var view = new PsdDataView(input);
        try
        {
            Assert.Throws<IOException>(() => { using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); });
            Assert.Throws<EndOfStreamException>(() => view.GetSpan(view.Length, 1));
            Assert.Throws<EndOfStreamException>(() => view.Slice(long.MaxValue, 1));
            Assert.Equal("8BPS"u8.ToArray(), view.GetSpan(0, 4).ToArray());
        }
        finally { input.Dispose(); }
        input.Dispose();
        Assert.Throws<ObjectDisposedException>(() => view.GetSpan(0, 1));
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationDuringRawAndRleReadsStopsBeforeReturningPixels(bool rle)
    {
        using var cancellation = new CancellationTokenSource();
        var input = new CancellingInput(Tiny(false, rle), cancellation, 2);
        Assert.ThrowsAny<OperationCanceledException>(() => PsdReader.Read(new PsdDataView(input), false, null, cancellation.Token));
        Assert.True(cancellation.IsCancellationRequested);
        Assert.InRange(input.ReadsAfterCancellation, 0, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationAlsoStopsPackBitsNoOpStreams(bool cropped)
    {
        using var cancellation = new CancellationTokenSource();
        byte[] bytes = new byte[8200]; bytes[0] = 0; bytes[1] = 0; bytes[2] = 32; bytes[3] = 4;
        bytes.AsSpan(4, 8192).Fill(128); bytes[8196] = 0; bytes[8197] = 11;
        var input = new CancellingInput(bytes, cancellation, 100);
        Assert.ThrowsAny<OperationCanceledException>(() => PsdChannelCoder.Decode(new PsdDataView(input), 1, 1, 1,
            true, cropped ? new PsdCrop(0, 0, 1, 1) : null, cancellation.Token));
        Assert.InRange(input.ReadsAfterCancellation, 0, 4096);
    }

    [Fact]
    public void CroppedPsbSkipsACompressedRowLongerThanInt32()
    {
        string path = Path.Combine(directory, "LongRle.psb"); long firstRow = (long)int.MaxValue + 1;
        using (var file = Sparse(path))
        {
            using var record = new MemoryStream();
            U32(record, uint.MaxValue); U32(record, 0); U32(record, 1); U32(record, 1); U16(record, 1);
            U16(record, 0); Length(record, 2 + 8 + firstRow + 2, true); record.Write("8BIMnorm"u8); record.Write([255, 0, 0, 0]);
            U32(record, 12); record.Write(new byte[8]); record.Write([1, (byte)'L', 0, 0]);
            Header(file, true, 1, 1); U32(file, 0);
            long info = 2 + record.Length + 2 + 8 + firstRow + 2;
            Length(file, info + 8, true); Length(file, info, true); U16(file, 1); file.Write(record.ToArray());
            U16(file, 1); U32(file, (uint)firstRow); U32(file, 2); file.Seek(firstRow, SeekOrigin.Current); file.Write([0, 11]);
        }
        var layer = Assert.Single(ReadFile(path, budget: 1).Layers);
        Assert.Equal(new byte[] { 11, 0, 0, 255 }, layer.Pixels!.Pixels);
        Assert.Equal(new PointD(0, 0), layer.Offset); Assert.True(layer.CroppedToCanvas);
    }

    [Theory]
    [InlineData(false, "zzzz")]
    [InlineData(true, "LMsk")]
    [InlineData(true, "Lr16")]
    [InlineData(true, "Lr32")]
    [InlineData(true, "Layr")]
    [InlineData(true, "Mt16")]
    [InlineData(true, "Mt32")]
    [InlineData(true, "Mtrn")]
    [InlineData(true, "Alph")]
    [InlineData(true, "FMsk")]
    [InlineData(true, "lnk2")]
    [InlineData(true, "FEid")]
    [InlineData(true, "FXid")]
    [InlineData(true, "PxSD")]
    public void LargeAdditionalBlockLengthsKeepTheFollowingChannelAligned(bool psb, string key)
    {
        using var extra = new MemoryStream(); extra.Write(new byte[8]); extra.Write([1, (byte)'L', 0, 0]);
        extra.Write(psb ? "8BIM"u8 : "8B64"u8); extra.Write(System.Text.Encoding.ASCII.GetBytes(key));
        Length(extra, 3, true); extra.Write([10, 20, 30, 0]);
        var layer = Assert.Single(PsdReader.Read(LayerFile(psb, extra.ToArray())).Layers);
        Assert.Equal(new byte[] { 11, 0, 0, 255 }, layer.Pixels!.Pixels);
    }

    [Fact]
    public void OversizedUnicodeNameFailsBeforeAllocatingTheName()
    {
        using var extra = new MemoryStream(); extra.Write(new byte[8]); extra.Write([1, (byte)'L', 0, 0]);
        extra.Write("8BIMluni"u8); U32(extra, 4 + 200_000); U32(extra, 100_000);
        for (int i = 0; i < 100_000; i++) extra.Write([0, (byte)'N']);
        byte[] bytes = LayerFile(false, extra.ToArray()); long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<PsdNotSupportedException>(() => PsdReader.Read(bytes));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 65536);
    }

    private sealed class CancellingInput(byte[] bytes, CancellationTokenSource cancellation, int after) : PsdInput
    {
        private int reads;
        public int ReadsAfterCancellation;
        internal override long Length => bytes.Length;
        internal override ReadOnlySpan<byte> GetSpan(long offset, int count)
        {
            // Wait for channel/table bytes, so this checks cancellation after header parsing.
            if (offset >= 40 && ++reads == after) cancellation.Cancel();
            if (cancellation.IsCancellationRequested) ReadsAfterCancellation++;
            return bytes.AsSpan((int)offset, count);
        }
        public override void Dispose() { }
    }

    private static byte[] LayerFile(bool psb, byte[] extra)
    {
        using var record = new MemoryStream(); record.Write(new byte[8]); U32(record, 1); U32(record, 1); U16(record, 1);
        U16(record, 0); Length(record, 3, psb); record.Write("8BIMnorm"u8); record.Write([255, 0, 0, 0]);
        U32(record, (uint)extra.Length); record.Write(extra);
        using var file = new MemoryStream(); Header(file, psb, 1, 1); U32(file, 0);
        long info = 2 + record.Length + 3; Length(file, info + (psb ? 8 : 4), psb); Length(file, info, psb);
        U16(file, 1); file.Write(record.ToArray()); U16(file, 0); file.WriteByte(11); return file.ToArray();
    }

    private static PsdDocument ReadFile(string path, bool parallel = true, long? budget = null, CancellationToken token = default)
        => PsdReader.ReadFile(path, parallel, budget, token);
    private static Task<PsdDocument> ReadFileAsync(string path, CancellationToken token = default)
        => PsdReader.ReadFileAsync(path, cancellationToken: token);
    private static PsdDocument ReadBytes(byte[] bytes, bool parallel, CancellationToken token)
        => PsdReader.Read(bytes, parallel, cancellationToken: token);

    private static FileStream Sparse(string path)
    {
        var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        if (!DeviceIoControl(file.SafeFileHandle, 0x900C4, 0, 0, 0, 0, out _, 0))
        { file.Dispose(); throw new IOException("The PSD large-file fixture requires a sparse-file volume."); }
        return file;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, nint input, uint inputSize, nint output, uint outputSize, out uint returned, nint overlapped);

    private static byte[] Tiny(bool psb, bool rle)
    {
        using var file = new MemoryStream(); Header(file, psb); U32(file, 0); Length(file, 0, psb); U16(file, rle ? 1 : 0);
        if (rle) for (int row = 0; row < 6; row++) { if (psb) U32(file, 3); else U16(file, 3); }
        foreach (var plane in new byte[][] { [11, 22, 33, 44], [55, 66, 77, 88], [99, 111, 122, 133] })
            for (int row = 0; row < 2; row++) { if (rle) file.WriteByte(1); file.Write(plane.AsSpan(row * 2, 2)); }
        return file.ToArray();
    }
    private static void Header(Stream file, bool psb, int width = 2, int height = 2)
    { file.Write("8BPS"u8); U16(file, psb ? 2 : 1); file.Write(new byte[6]); U16(file, 3); U32(file, (uint)height); U32(file, (uint)width); U16(file, 8); U16(file, 3); U32(file, 0); }
    private static void U16(Stream stream, int value) => stream.Write([(byte)(value >> 8), (byte)value]);
    private static void U32(Stream stream, uint value) => stream.Write([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
    private static void Length(Stream stream, long value, bool psb) { if (psb) U32(stream, (uint)(value >> 32)); U32(stream, (uint)value); }
}
