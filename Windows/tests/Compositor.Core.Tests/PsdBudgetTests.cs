using System.Buffers.Binary;
using Compositor.Core.Psd;
using Xunit;

namespace Compositor.Core.Tests;

public sealed class PsdBudgetTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void OverBudgetLayersDecodeOnlyTheCanvasWindow(bool psb, bool rle)
    {
        var parsed = Read(Build(2, 2, psb, rle, new Spec(-1, -1, 4, 3)), 4);
        var layer = Assert.Single(parsed.Layers);
        Assert.Equal(new PointD(0, 0), layer.Offset);
        Assert.Equal(2, layer.Pixels!.Width); Assert.Equal(2, layer.Pixels.Height);
        Assert.Equal(new byte[] { 5, 40, 10, 128, 5, 40, 10, 128, 8, 40, 10, 128, 9, 40, 10, 128 }, layer.Pixels.Pixels);
    }

    [Fact]
    public void LayersWithinBudgetRetainTheirOffCanvasPixels()
    {
        var parsed = Read(Build(2, 2, false, false, new Spec(-1, -1, 3, 2)), 12);
        var layer = Assert.Single(parsed.Layers);
        Assert.Equal(new PointD(-1, -1), layer.Offset);
        Assert.Equal(4, layer.Pixels!.Width); Assert.Equal(3, layer.Pixels.Height);
        Assert.Equal(1, layer.Pixels.Pixels[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OriginalSideLimitUsesCanvasCroppingInsteadOfRejectingTheLayer(bool psb)
    {
        var parsed = PsdReader.Read(Build(2, 1, psb, true, new Spec(-1, 0, 30_000, 1)));
        var layer = Assert.Single(parsed.Layers);
        Assert.Equal(new PointD(0, 0), layer.Offset);
        Assert.Equal(2, layer.Pixels!.Width); Assert.Equal(new byte[] { 1, 40, 10, 128, 2, 40, 10, 128 }, layer.Pixels.Pixels);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AggregateLayerBudgetIsCheckedAfterCropping(bool parallel)
    {
        var bytes = Build(2, 2, false, true, new Spec(-1, -1, 4, 3), new Spec(-1, -1, 4, 3));
        Assert.Equal(2, Read(bytes, 8, parallel).Layers.Count);
        var error = Assert.Throws<PsdNotSupportedException>(() => Read(bytes, 7, parallel));
        Assert.Contains("budget", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AdmissionHappensBeforeUnsupportedChannelDecode()
    {
        var bytes = Build(2, 2, false, false, new Spec(0, 0, 2, 2) { Compression = 7 });
        var error = Assert.Throws<PsdNotSupportedException>(() => Read(bytes, 3));
        Assert.Contains("budget", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(0L)]
    public void ExhaustedDocumentBudgetRejectsBeforePixelAllocation(long budget)
    {
        var error = Assert.Throws<PsdNotSupportedException>(() => Read(Build(1, 1, false, false, new Spec(0, 0, 1, 1)), budget));
        Assert.Contains("budget", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void MergedCompositeAlsoRequiresRemainingDocumentBudget(bool psb, bool rle)
    {
        var bytes = Merged(2, 2, psb, rle);
        Assert.Equal(16, Assert.Single(Read(bytes, 4).Layers).Pixels!.Pixels.Length);
        var error = Assert.Throws<PsdNotSupportedException>(() => Read(bytes, 3));
        Assert.Contains("budget", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExtremeSignedBoundsAreCroppedWithoutWrappingTheirDifference()
    {
        var bytes = Build(2, 2, false, false, new Spec(int.MinValue, int.MinValue, int.MaxValue, int.MaxValue) { Pixels = false });
        var layer = Assert.Single(Read(bytes, 4).Layers);
        Assert.Equal(new PointD(0, 0), layer.Offset); Assert.Equal(2, layer.Pixels!.Width);
        Assert.Equal(new byte[] { 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255 }, layer.Pixels.Pixels);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MaskDefaultAndPatchAreMappedOntoTheLayerGrid(bool linked)
    {
        var bytes = Build(5, 4, false, false, new Spec(1, 1, 4, 3)
            { Mask = new(2, 1, 4, 3), MaskDefault = 30, MaskLinked = linked });
        var parsed = Read(bytes, 12); var source = Assert.Single(parsed.Layers);
        Assert.Equal(3, source.Mask!.Width); Assert.Equal(2, source.Mask.Height);
        Assert.Equal(new byte[] { 30, 1, 2, 30, 8, 9 }, source.Mask.Pixels);
        var owner = EditorDocument.Create(10, 10);
        using (var batch = owner.BeginFileImport(new(6, 7))) batch.AddPhotoshop(parsed, "File");
        var layer = owner.Snapshot.Manifest.Layers.Single(l => l.MaskFile is not null);
        Assert.Equal(new PointD(4.5, 6), layer.Transform.Origin);
        Assert.Equal(layer.Transform.Origin, layer.MaskPlacement!.Origin);
        Assert.Equal(new SizeD(3, 2), layer.MaskPlacement.Size); Assert.Equal(linked, layer.MaskLinked);
        Assert.True(owner.Undo()); Assert.True(owner.Redo());
        Assert.Equal(source.Mask.Pixels, owner.Snapshot.Masks[layer.Id].Pixels);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CroppedMaskUsesItsOriginalSourceWindowAndCountsTheRetainedGrid(bool rle)
    {
        var bytes = Build(2, 2, false, rle, new Spec(-1, -1, 4, 3) { Mask = new(-1, -1, 4, 3), MaskDefault = 30 });
        var layer = Assert.Single(Read(bytes, 8).Layers);
        Assert.Equal(new byte[] { 9, 10, 16, 17 }, layer.Mask!.Pixels);
        var error = Assert.Throws<PsdNotSupportedException>(() => Read(bytes, 7));
        Assert.Contains("budget", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CroppedRleDoesNotAllocateTheOriginalPlaneOrCopyItsPayload()
    {
        var bytes = Build(2, 2, false, true, new Spec(0, 0, 1000, 1000) { Flat = true });
        Read(Build(1, 1, false, true, new Spec(0, 0, 1, 1)), 1, false);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var parsed = Read(bytes, 4, false);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 512 * 1024, $"A 2x2 crop allocated {allocated} bytes from a 1000x1000 RLE source.");
        Assert.Equal(16, Assert.Single(parsed.Layers).Pixels!.Pixels.Length);
    }

    private static PsdDocument Read(byte[] bytes, long budget, bool parallel = true)
        => PsdReader.Read(bytes, parallel, budget);

    [Fact]
    public void EmptyMaskWithAnExtremeOtherSideDoesNotOverflow()
    {
        var bytes = Build(1, 1, false, false, new Spec(0, 0, 1, 1)
            { Mask = new(int.MinValue, 0, int.MaxValue, 0) });
        var layer = Assert.Single(Read(bytes, 1).Layers);
        Assert.Null(layer.Mask); Assert.Equal(4, layer.Pixels!.Pixels.Length);
    }

    [Fact]
    public void ImportAdmissionAndPublicationShareTheCombinedImageMaskBudget()
    {
        var owner = FullBudgetScene(); var snapshot = owner.Snapshot;
        using var batch = owner.BeginFileImport();
        var error = Assert.Throws<ProjectFormatException>(() => batch.AddImage(new Surface(1, 1), "Incoming"));
        Assert.Equal(ProjectErrorKind.TooLarge, error.Kind);
        Assert.Equal(0, batch.RemainingPixels);
        Assert.Throws<PsdNotSupportedException>(() => Read(Build(1, 1, false, false, new Spec(0, 0, 1, 1)), batch.RemainingPixels));
        Assert.Same(snapshot, owner.Snapshot); Assert.False(owner.CanUndo); Assert.False(owner.IsDirty);
    }

    [Fact]
    public void DirectPsdImportAlsoRejectsOverBudgetBeforeOpeningHistory()
    {
        var owner = FullBudgetScene(); var snapshot = owner.Snapshot;
        string original = ManifestJson.Serialize(snapshot.Manifest);
        var parsed = Read(Build(1, 1, false, false, new Spec(0, 0, 1, 1)), 1);
        var error = Assert.Throws<ProjectFormatException>(() => owner.ImportPsd(parsed));
        Assert.Equal(ProjectErrorKind.TooLarge, error.Kind);
        Assert.Same(snapshot, owner.Snapshot); Assert.Equal(original, ManifestJson.Serialize(snapshot.Manifest));
        Assert.False(owner.CanUndo); Assert.False(owner.IsDirty);
    }

    private static EditorDocument FullBudgetScene()
    {
        var manifest = new ProjectManifest { DocumentId = Guid.NewGuid(), Width = 1, Height = 1 };
        var images = new Dictionary<Guid, Surface>(); var masks = new Dictionary<Guid, MaskSurface>();
        var shared = new Surface(1000, 1000);
        long remaining = Limits.DocumentPixelBudget - 1;
        while (remaining > 0)
        {
            var image = remaining >= shared.PixelCount ? shared : new Surface((int)Math.Min(30_000, remaining), 1);
            var id = Guid.NewGuid();
            manifest.Layers.Add(new() { Id = id, Name = "Existing", IsVisible = true,
                ImageFile = ProjectValidator.Uuid(id) + ".png", Transform = new() { Size = new(image.Width, image.Height) } });
            images.Add(id, image); remaining -= image.PixelCount;
        }
        var first = manifest.Layers[0]; first.MaskFile = ProjectValidator.Uuid(first.Id) + ".mask.png";
        masks.Add(first.Id, MaskSurface.Solid(true)); manifest.ActiveLayerId = first.Id;
        var snapshot = new ProjectSnapshot { Manifest = manifest, Images = images, Masks = masks };
        return new EditorDocument(snapshot);
    }

    [Fact]
    public async Task LayerGridMasksKeepPixelsAndPlacementAcrossProjectSaveReload()
    {
        var parsed = Read(Build(5, 4, false, true, new Spec(1, 1, 4, 3)
            { Mask = new(2, 1, 4, 3), MaskDefault = 30, MaskLinked = false }), 12);
        var owner = new EditorDocument(new ProjectSnapshot { Manifest = new() { DocumentId = Guid.NewGuid(), Width = 5, Height = 4 },
            Images = new Dictionary<Guid, Surface>(), Masks = new Dictionary<Guid, MaskSurface>() });
        using (var batch = owner.BeginFileImport()) batch.AddPhotoshop(parsed, "File");
        var layer = owner.Snapshot.Manifest.Layers.Single(l => l.MaskFile is not null);
        var before = new CanvasCompositor(owner.Snapshot).Render().Pixels.ToArray();
        Assert.Equal(15, before[(1 * 5 + 1) * 4 + 3]);
        string directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "compositor-psd-budget-" + Guid.NewGuid().ToString("N")));
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), directory);
        try
        {
            await ProjectStore.SaveAsync(owner.Snapshot, directory);
            var loaded = await ProjectStore.LoadAsync(directory);
            Assert.Equal(before, new CanvasCompositor(loaded).Render().Pixels);
            Assert.Equal(new PointD(1, 1), loaded.Manifest.Layers.Single(l => l.Id == layer.Id).MaskPlacement!.Origin);
            Assert.Equal(new byte[] { 30, 1, 2, 30, 8, 9 }, loaded.Masks[layer.Id].Pixels);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed record Rect(int Left, int Top, int Right, int Bottom);
    private sealed record Spec(int Left, int Top, int Right, int Bottom)
    {
        public bool Pixels { get; init; } = true;
        public bool Flat { get; init; }
        public Rect? Mask { get; init; }
        public byte MaskDefault { get; init; } = 255;
        public bool MaskLinked { get; init; } = true;
        public int? Compression { get; init; }
    }

    private static byte[] Build(int width, int height, bool psb, bool rle, params Spec[] specs)
    {
        using var records = new MemoryStream(); using var channels = new MemoryStream();
        foreach (var spec in specs)
        {
            I32(records, spec.Top); I32(records, spec.Left); I32(records, spec.Bottom); I32(records, spec.Right);
            var planes = new List<(int Id, byte[] Data)>();
            if (spec.Pixels)
                foreach (int id in new[] { 0, 1, 2, -1 }) planes.Add((id, Payload(spec.Right - spec.Left, spec.Bottom - spec.Top, id, psb, rle, spec.Flat)));
            if (spec.Mask is { } mask) planes.Add((-2, Payload(mask.Right - mask.Left, mask.Bottom - mask.Top, 0, psb, rle, false)));
            U16(records, planes.Count);
            foreach (var plane in planes)
            { U16(records, plane.Id); Length(records, plane.Data.Length + 2, psb); U16(channels, spec.Compression ?? (rle ? 1 : 0)); channels.Write(plane.Data); }
            records.Write("8BIMnorm"u8); records.Write([255, 0, 0, 0]);
            I32(records, spec.Mask is null ? 12 : 32); I32(records, spec.Mask is null ? 0 : 20);
            if (spec.Mask is { } patch)
            { I32(records, patch.Top); I32(records, patch.Left); I32(records, patch.Bottom); I32(records, patch.Right); records.Write([spec.MaskDefault, (byte)(spec.MaskLinked ? 0 : 1), 0, 0]); }
            I32(records, 0); records.Write([1, (byte)'L', 0, 0]);
        }
        using var file = Header(width, height, psb); int infoLength = checked(2 + (int)records.Length + (int)channels.Length);
        Length(file, infoLength + (psb ? 8 : 4), psb); Length(file, infoLength, psb); U16(file, specs.Length);
        records.Position = 0; records.CopyTo(file); channels.Position = 0; channels.CopyTo(file); return file.ToArray();
    }

    private static byte[] Merged(int width, int height, bool psb, bool rle)
    {
        using var file = Header(width, height, psb); Length(file, 0, psb); U16(file, rle ? 1 : 0);
        var payloads = new[] { 0, 1, 2 }.Select(id => Payload(width, height, id, psb, rle, false)).ToArray();
        int table = height * (psb ? 4 : 2);
        if (rle) foreach (var payload in payloads) file.Write(payload.AsSpan(0, table));
        foreach (var payload in payloads) file.Write(payload.AsSpan(rle ? table : 0)); return file.ToArray();
    }

    private static MemoryStream Header(int width, int height, bool psb)
    {
        var file = new MemoryStream(); file.Write("8BPS"u8); U16(file, psb ? 2 : 1); file.Write(new byte[6]);
        U16(file, 3); I32(file, height); I32(file, width); U16(file, 8); U16(file, 3); I32(file, 0); I32(file, 0); return file;
    }

    private static byte[] Payload(int width, int height, int channel, bool psb, bool rle, bool flat)
    {
        using var table = new MemoryStream(); using var payload = new MemoryStream();
        byte Value(int x, int y) => channel switch { 1 => 80, 2 => 20, -1 => 128, _ => flat ? (byte)1 : (byte)(1 + (x + y * 7) % 251) };
        for (int y = 0; y < height; y++)
        {
            long start = payload.Position;
            for (int x = 0; x < width;)
            {
                int count = Math.Min(128, width - x);
                if (rle && (flat || channel != 0)) { payload.WriteByte(unchecked((byte)(1 - count))); payload.WriteByte(Value(x, y)); }
                else { if (rle) payload.WriteByte((byte)(count - 1)); for (int i = 0; i < count; i++) payload.WriteByte(Value(x + i, y)); }
                x += count;
            }
            if (rle) { if (psb) I32(table, (int)(payload.Position - start)); else U16(table, (int)(payload.Position - start)); }
        }
        payload.Position = 0; payload.CopyTo(table); return table.ToArray();
    }

    private static void U16(Stream stream, int value) => stream.Write([(byte)(value >> 8), (byte)value]);
    private static void I32(Stream stream, int value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(bytes, value); stream.Write(bytes); }
    private static void Length(Stream stream, int value, bool psb) { if (psb) I32(stream, 0); I32(stream, value); }
}
