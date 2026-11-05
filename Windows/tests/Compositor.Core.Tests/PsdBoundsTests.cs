using System.Buffers.Binary;
using Compositor.Core.Psd;
using Xunit;

namespace Compositor.Core.Tests;

public sealed class PsdBoundsTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(9)]
    [InlineData(int.MaxValue)]
    public void InvalidSeekPreservesTheCursor(int position)
    {
        var reader = new PsdBinaryReader(new byte[8]);
        reader.Skip(1);
        Assert.Throws<EndOfStreamException>(() => reader.Seek(position));
        Assert.Equal(1, reader.Offset);
        Assert.Equal(7, reader.Remaining);
    }

    [Fact]
    public void SkipCannotWrapTheOffset()
    {
        var reader = new PsdBinaryReader([10, 20]);
        reader.Skip(1);
        Assert.Throws<EndOfStreamException>(() => reader.Skip(int.MaxValue));
        Assert.Equal(1, reader.Offset);
        Assert.Equal(20, reader.ReadU8());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void RleRejectsInvalidOffsetsBeforeIndexing(int offset)
    {
        Assert.Throws<EndOfStreamException>(() => PsdBinaryReader.UnpackRle([0, 2, 255, 7], offset, 1, 1, false));
    }

    [Fact]
    public void TruncatedRleRowTableDoesNotAllocateAccordingToDeclaredHeight()
    {
        Assert.Throws<EndOfStreamException>(() => PsdBinaryReader.UnpackRle([], 0, 1, 1, false));
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<EndOfStreamException>(() => PsdBinaryReader.UnpackRle([], 0, 1, 1_000_000, false));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 65536, $"An empty RLE payload allocated {allocated} bytes from a declared row count.");
    }

    [Fact]
    public void ReadRlePlaneRequiresOneCountPerRow()
    {
        var reader = new PsdBinaryReader([]);
        Assert.Throws<EndOfStreamException>(() => reader.ReadRlePlane([0], 1, 2));
        Assert.Equal(0, reader.Offset);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValidLayeredPsdAndPsbKeepPixels(bool psb)
    {
        var fixture = Build(psb);
        var document = PsdReader.Read(fixture.Bytes);
        var layer = Assert.Single(document.Layers);
        Assert.Equal("U", layer.Name);
        Assert.Equal([116, 45, 42, 128], layer.Pixels!.Pixels);
        Assert.False(document.IsCompositeFallback);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LayerRecordsCannotReadPastDeclaredInfo(bool psb)
    {
        var fixture = Build(psb);
        PutLength(fixture.Bytes, fixture.InfoLength, 2, psb);
        Assert.Throws<EndOfStreamException>(() => PsdReader.Read(fixture.Bytes));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LayerInfoCannotReadPastDeclaredOuterSection(bool psb)
    {
        var fixture = Build(psb);
        PutLength(fixture.Bytes, fixture.SectionLength, (uint)(psb ? 8 : 4), psb);
        Assert.Throws<EndOfStreamException>(() => PsdReader.Read(fixture.Bytes));
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(0x80000000u)]
    public void PsbSectionLengthCannotWrapIntoTheBuffer(uint high)
    {
        var fixture = Build(true);
        Put(fixture.Bytes, fixture.SectionLength, high);
        Assert.Throws<EndOfStreamException>(() => PsdReader.Read(fixture.Bytes));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExtraMetadataCannotBorrowChannelBytes(bool psb)
    {
        var fixture = Build(psb);
        Put(fixture.Bytes, fixture.ExtraLength, 8);
        Assert.Throws<EndOfStreamException>(() => PsdReader.Read(fixture.Bytes));
    }

    [Theory]
    [InlineData(0x7fffffffu)]
    [InlineData(0xffffffffu)]
    public void MaskMetadataLengthCannotWrap(uint length)
    {
        var fixture = Build(false);
        Put(fixture.Bytes, fixture.MaskLength, length);
        Assert.Throws<EndOfStreamException>(() => PsdReader.Read(fixture.Bytes));
    }

    [Fact]
    public void NegativeAdditionalInfoLengthIsRejected()
    {
        var fixture = Build(false);
        Put(fixture.Bytes, fixture.AdditionalLength, uint.MaxValue);
        Assert.Throws<EndOfStreamException>(() => PsdReader.Read(fixture.Bytes));
    }

    [Theory]
    [InlineData(0x7fffffffu)]
    [InlineData(0xffffffffu)]
    public void UnicodeCountIsBoundedByItsOwnPayload(uint count)
    {
        var fixture = Build(false);
        Put(fixture.Bytes, fixture.UnicodeCount, count);
        Assert.Throws<EndOfStreamException>(() => PsdReader.Read(fixture.Bytes));
    }

    [Theory]
    [InlineData(false, ".psd")]
    [InlineData(true, ".psb")]
    [InlineData(true, ".PSB")]
    public void TheFileEntryReadsBothContainersUnderTheirOwnExtension(bool psb, string extension)
    {
        // The picker, the drop filter and the import router hand over a path, so both containers
        // have to answer from disk and under the upper-case spelling Windows shows in a picker.
        string directory = Path.Combine(Path.GetTempPath(), "compositor-psd-entry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "document" + extension);
        try
        {
            File.WriteAllBytes(path, Build(psb).Bytes);
            Assert.True(PsdReader.IsPhotoshopFile(path));
            var layer = Assert.Single(PsdReader.ReadFile(path).Layers);
            Assert.Equal("U", layer.Name);
            Assert.Equal([116, 45, 42, 128], layer.Pixels!.Pixels);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("report.png")]
    [InlineData("notes.txt")]
    [InlineData("layer.psd.mask.png")]
    public void OrdinaryFilesAreNotRoutedToThePhotoshopReader(string name)
        => Assert.False(PsdReader.IsPhotoshopFile(Path.Combine(Path.GetTempPath(), name)));

    private sealed record Fixture(byte[] Bytes, int SectionLength, int InfoLength, int ExtraLength,
        int MaskLength, int AdditionalLength, int UnicodeCount);

    // A single 1x1 raw RGBA layer with fixed bytes, plus a length-delimited Unicode name.
    // Declared lengths are later changed without removing bytes, exposing cross-section reads.
    private static Fixture Build(bool psb)
    {
        using var record = new MemoryStream();
        I32(record, 0); I32(record, 0); I32(record, 1); I32(record, 1); U16(record, 4);
        foreach (int channel in new[] { 0, 1, 2, -1 }) { U16(record, channel); Length(record, 3, psb); }
        record.Write("8BIMnorm"u8); record.Write([255, 0, 0, 0]);
        int extraField = (int)record.Position; I32(record, 30);
        int maskField = (int)record.Position; I32(record, 0); I32(record, 0);
        record.Write([1, (byte)'L', 0, 0]);
        record.Write("8BIMluni"u8);
        int additionalField = (int)record.Position; I32(record, 6);
        int unicodeField = (int)record.Position; I32(record, 1); record.Write([0, (byte)'U']);
        using var info = new MemoryStream(); U16(info, 1);
        int recordOffset = (int)info.Position; info.Write(record.ToArray());
        foreach (byte channel in new byte[] { 232, 90, 84, 128 }) { U16(info, 0); info.WriteByte(channel); }
        using var file = new MemoryStream();
        file.Write("8BPS"u8); U16(file, psb ? 2 : 1); file.Write(new byte[6]); U16(file, 4);
        I32(file, 2); I32(file, 2); U16(file, 8); U16(file, 3); I32(file, 0); I32(file, 0);
        int sectionField = (int)file.Position; Length(file, (uint)info.Length + (uint)(psb ? 8 : 4), psb);
        int infoField = (int)file.Position; Length(file, (uint)info.Length, psb);
        int start = (int)file.Position + recordOffset; file.Write(info.ToArray());
        return new(file.ToArray(), sectionField, infoField, start + extraField, start + maskField,
            start + additionalField, start + unicodeField);
    }

    private static void Put(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset, 4), value);
    private static void PutLength(byte[] data, int offset, uint value, bool psb) { if (psb) Put(data, offset, 0); Put(data, offset + (psb ? 4 : 0), value); }
    private static void U16(Stream stream, int value) => stream.Write([(byte)(value >> 8), (byte)value]);
    private static void I32(Stream stream, uint value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, value); stream.Write(bytes); }
    private static void Length(Stream stream, uint value, bool psb) { if (psb) I32(stream, 0); I32(stream, value); }
}
